using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Abstractions.Sessions;
using Microsoft.Data.Sqlite;

namespace AgentWorkspace.Core.Sessions;

/// <summary>
/// SQLite-backed implementation of <see cref="ISessionStore"/>. Three tables form the schema:
/// <c>sessions</c> (meta), <c>session_panes</c> (pane specs), <c>session_layouts</c> (one
/// JSON-serialised layout per session). The pane spec is stored as JSON because we never
/// query inside it — we either round-trip the whole record or delete it.
/// </summary>
/// <remarks>
/// Concurrency strategy: the connection is single-writer, opened with
/// <c>journal_mode = WAL</c> and <c>foreign_keys = ON</c>. All public methods take the same
/// instance lock; SQLite calls are short, so the lock contention is acceptable for an
/// interactive workspace. If the store ever needs to scale to dozens of concurrent writers,
/// switch to a connection-per-call pattern with a connection pool.
/// </remarks>
public sealed class SqliteSessionStore : ISessionStore, IAsyncDisposable
{
    private const int CurrentSchemaVersion = 1;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private SqliteConnection? _connection;
    private bool _initialized;

    public SqliteSessionStore(string databasePath)
    {
        // Allow ":memory:" through verbatim for tests; otherwise treat the input as a file path.
        _connectionString = databasePath == ":memory:"
            ? "Data Source=:memory:;Cache=Shared"
            : new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                ForeignKeys = true,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString();
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            _connection = new SqliteConnection(_connectionString);
            await _connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            using (var pragma = _connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;";
                await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        if (_connection is null) throw new InvalidOperationException("connection not opened.");

        using var checkVersion = _connection.CreateCommand();
        checkVersion.CommandText = "PRAGMA user_version;";
        long current = (long)(await checkVersion.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);

        if (current >= CurrentSchemaVersion) return;

        using var tx = _connection.BeginTransaction();
        using (var create = _connection.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS sessions (
                    session_id              TEXT PRIMARY KEY,
                    name                    TEXT NOT NULL,
                    workspace_root          TEXT,
                    created_at_utc          TEXT NOT NULL,
                    last_attached_at_utc    TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS session_panes (
                    session_id              TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
                    pane_id                 TEXT NOT NULL,
                    command                 TEXT NOT NULL,
                    args_json               TEXT NOT NULL,
                    working_directory       TEXT,
                    env_json                TEXT,
                    PRIMARY KEY (session_id, pane_id)
                );

                CREATE TABLE IF NOT EXISTS session_layouts (
                    session_id              TEXT PRIMARY KEY REFERENCES sessions(session_id) ON DELETE CASCADE,
                    layout_json             TEXT NOT NULL,
                    focused_pane_id         TEXT NOT NULL,
                    updated_at_utc          TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_sessions_last_attached
                    ON sessions(last_attached_at_utc DESC);
                """;
            await create.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        using (var bumpVersion = _connection.CreateCommand())
        {
            bumpVersion.Transaction = tx;
            bumpVersion.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion};";
            await bumpVersion.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async ValueTask<SessionId> CreateAsync(string name, string? workspaceRoot, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var id = SessionId.New();
            var nowIso = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sessions (session_id, name, workspace_root, created_at_utc, last_attached_at_utc)
                VALUES ($id, $name, $root, $now, $now);
                """;
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$root", (object?)workspaceRoot ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", nowIso);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return id;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                SELECT session_id, name, workspace_root, created_at_utc, last_attached_at_utc
                FROM sessions
                ORDER BY last_attached_at_utc DESC;
                """;
            var list = new List<SessionInfo>();
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                list.Add(ReadInfo(reader));
            }
            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<SessionSnapshot?> AttachAsync(SessionId id, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionInfo? info = null;
            using (var cmd = _connection!.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT session_id, name, workspace_root, created_at_utc, last_attached_at_utc
                    FROM sessions WHERE session_id = $id;
                    """;
                cmd.Parameters.AddWithValue("$id", id.ToString());
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    info = ReadInfo(reader);
                }
            }
            if (info is null) return null;

            // Pane specs.
            var panes = new List<PaneSpec>();
            using (var cmd = _connection!.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT pane_id, command, args_json, working_directory, env_json
                    FROM session_panes WHERE session_id = $id;
                    """;
                cmd.Parameters.AddWithValue("$id", id.ToString());
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var paneId = PaneId.Parse(reader.GetString(0));
                    string command = reader.GetString(1);
                    var args = JsonSerializer.Deserialize<List<string>>(reader.GetString(2)) ?? new();
                    string? cwd = reader.IsDBNull(3) ? null : reader.GetString(3);
                    Dictionary<string, string>? env = reader.IsDBNull(4)
                        ? null
                        : JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4));
                    panes.Add(new PaneSpec(paneId, command, args, cwd, env));
                }
            }

            // Layout (optional — a brand-new session may not have one yet).
            LayoutSnapshot? layout = null;
            using (var cmd = _connection!.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT layout_json, focused_pane_id FROM session_layouts WHERE session_id = $id;
                    """;
                cmd.Parameters.AddWithValue("$id", id.ToString());
                using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var root = LayoutJson.Deserialize(reader.GetString(0));
                    var focused = PaneId.Parse(reader.GetString(1));
                    layout = new LayoutSnapshot(root, focused);
                }
            }

            // Touch last_attached_at_utc.
            using (var cmd = _connection!.CreateCommand())
            {
                cmd.CommandText = """
                    UPDATE sessions SET last_attached_at_utc = $now WHERE session_id = $id;
                    """;
                cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("$id", id.ToString());
                await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // If the session has panes but no layout (a freshly created session), synthesise a
            // single-pane snapshot so callers can rely on Layout being non-null.
            if (layout is null && panes.Count > 0)
            {
                layout = new LayoutSnapshot(
                    new PaneNode(LayoutId.New(), panes[0].Pane),
                    panes[0].Pane);
            }

            return layout is null ? null : new SessionSnapshot(info, layout, panes);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask UpsertPaneAsync(SessionId id, PaneSpec pane, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO session_panes (session_id, pane_id, command, args_json, working_directory, env_json)
                VALUES ($sid, $pid, $cmd, $args, $cwd, $env)
                ON CONFLICT(session_id, pane_id) DO UPDATE SET
                    command = excluded.command,
                    args_json = excluded.args_json,
                    working_directory = excluded.working_directory,
                    env_json = excluded.env_json;
                """;
            cmd.Parameters.AddWithValue("$sid", id.ToString());
            cmd.Parameters.AddWithValue("$pid", pane.Pane.ToString());
            cmd.Parameters.AddWithValue("$cmd", pane.Command);
            cmd.Parameters.AddWithValue("$args", JsonSerializer.Serialize(pane.Arguments));
            cmd.Parameters.AddWithValue("$cwd", (object?)pane.WorkingDirectory ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$env",
                pane.Environment is null ? DBNull.Value : (object)JsonSerializer.Serialize(pane.Environment));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DeletePaneAsync(SessionId id, PaneId pane, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM session_panes WHERE session_id = $sid AND pane_id = $pid;";
            cmd.Parameters.AddWithValue("$sid", id.ToString());
            cmd.Parameters.AddWithValue("$pid", pane.ToString());
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask SaveLayoutAsync(SessionId id, LayoutSnapshot layout, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = """
                INSERT INTO session_layouts (session_id, layout_json, focused_pane_id, updated_at_utc)
                VALUES ($sid, $json, $focused, $now)
                ON CONFLICT(session_id) DO UPDATE SET
                    layout_json = excluded.layout_json,
                    focused_pane_id = excluded.focused_pane_id,
                    updated_at_utc = excluded.updated_at_utc;
                """;
            cmd.Parameters.AddWithValue("$sid", id.ToString());
            cmd.Parameters.AddWithValue("$json", LayoutJson.Serialize(layout.Root));
            cmd.Parameters.AddWithValue("$focused", layout.Focused.ToString());
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DeleteAsync(SessionId id, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM sessions WHERE session_id = $id;";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                await _connection.CloseAsync().ConfigureAwait(false);
                _connection.Dispose();
                _connection = null;
            }
        }
        finally
        {
            _gate.Release();
        }
        _gate.Dispose();
    }

    private static SessionInfo ReadInfo(SqliteDataReader r)
    {
        var id = SessionId.Parse(r.GetString(0));
        string name = r.GetString(1);
        string? root = r.IsDBNull(2) ? null : r.GetString(2);
        var created = DateTimeOffset.Parse(r.GetString(3), CultureInfo.InvariantCulture);
        var attached = DateTimeOffset.Parse(r.GetString(4), CultureInfo.InvariantCulture);
        return new SessionInfo(id, name, root, created, attached);
    }

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }
}
