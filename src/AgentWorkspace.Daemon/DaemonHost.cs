using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Sessions;
using AgentWorkspace.Core.Sessions;
using AgentWorkspace.Daemon.Auth;
using AgentWorkspace.Daemon.Channels;

namespace AgentWorkspace.Daemon;

/// <summary>
/// Daemon process host. Day 15 generated the bearer token and started a NamedPipe listener; Day
/// 17 adds ownership of the pane channel + session store so accepted clients can drive the
/// workspace via <see cref="RpcDispatcher"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DaemonHost : IAsyncDisposable
{
    private readonly DaemonHostOptions _options;
    private SessionToken? _token;
    private ControlChannelServer? _server;
    private PtyControlChannel? _panes;
    private SqliteSessionStore? _store;
    private bool _disposed;

    public DaemonHost(DaemonHostOptions? options = null)
    {
        _options = options ?? new DaemonHostOptions();
    }

    public string TokenPath => _options.TokenPath;
    public string? ResolvedPipeName => _server?.ResolvedPipeName;

    /// <summary>Pane channel owned by the daemon. Exposed for in-process tests.</summary>
    public PtyControlChannel? Panes => _panes;

    /// <summary>Session store owned by the daemon. Exposed for in-process tests.</summary>
    public ISessionStore? Store => _store;

    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _token = SessionToken.Generate();
        SessionTokenStore.Save(_token, _options.TokenPath);

        _panes = new PtyControlChannel();

        _store = new SqliteSessionStore(_options.DatabasePath);
        await _store.InitializeAsync(ct).ConfigureAwait(false);

        _server = new ControlChannelServer(_token, _options.Channel, _panes, _store);
        _server.ClientAuthenticated += (_, args) =>
            _options.OnClientAuthenticated?.Invoke(args);
        _server.ClientRejected += (_, args) =>
            _options.OnClientRejected?.Invoke(args);

        await _server.Start().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _server = null;
        }

        if (_panes is not null)
        {
            try { await _panes.DisposeAsync().ConfigureAwait(false); }
            catch { /* swallow */ }
            _panes = null;
        }

        if (_store is not null)
        {
            try { await _store.DisposeAsync().ConfigureAwait(false); }
            catch { /* swallow */ }
            _store = null;
        }

        if (_options.DeleteTokenOnShutdown)
        {
            try
            {
                if (File.Exists(_options.TokenPath))
                {
                    File.Delete(_options.TokenPath);
                }
            }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class DaemonHostOptions
{
    public string TokenPath { get; init; } = SessionTokenStore.DefaultPath;
    public ControlChannelOptions Channel { get; init; } = ControlChannelOptions.Default;

    /// <summary>
    /// Path to the SQLite session database. Default places it under the user profile so the
    /// daemon process and the legacy single-process App.Wpf agree on location during transition.
    /// </summary>
    public string DatabasePath { get; init; } = ResolveDefaultDatabasePath();

    public bool DeleteTokenOnShutdown { get; init; } = true;
    public Action<ControlClientAuthenticatedEventArgs>? OnClientAuthenticated { get; init; }
    public Action<ControlClientRejectedEventArgs>? OnClientRejected { get; init; }

    private static string ResolveDefaultDatabasePath()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentworkspace");
        Directory.CreateDirectory(root);
        return Path.Combine(root, "sessions.db");
    }
}
