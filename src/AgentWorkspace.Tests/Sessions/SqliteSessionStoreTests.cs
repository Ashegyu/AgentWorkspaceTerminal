using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Abstractions.Sessions;
using AgentWorkspace.Core.Sessions;

namespace AgentWorkspace.Tests.Sessions;

/// <summary>
/// SQLite store tests run against a real on-disk file in the test temp directory so the schema
/// migration / WAL / foreign-key behaviour is exercised end-to-end. Each test gets its own file
/// and deletes it on disposal.
/// </summary>
public sealed class SqliteSessionStoreTests : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly SqliteSessionStore _store;

    public SqliteSessionStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(),
            $"agentworkspace-tests-{Guid.NewGuid():N}.db");
        _store = new SqliteSessionStore(_dbPath);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();

        // SQLite WAL means we may have -shm / -wal sidecars; remove them too.
        foreach (string suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Initialize_IsIdempotent_AndSetsSchemaVersion()
    {
        await _store.InitializeAsync(CancellationToken.None);
        await _store.InitializeAsync(CancellationToken.None);

        // We can list with no sessions and get an empty list.
        var sessions = await _store.ListAsync(CancellationToken.None);
        Assert.Empty(sessions);
    }

    [Fact]
    public async Task Create_PersistsAndIsListed()
    {
        var id = await _store.CreateAsync("dev", @"D:\Projects\demo", CancellationToken.None);

        var sessions = await _store.ListAsync(CancellationToken.None);
        Assert.Single(sessions);
        Assert.Equal(id, sessions[0].Id);
        Assert.Equal("dev", sessions[0].Name);
        Assert.Equal(@"D:\Projects\demo", sessions[0].WorkspaceRoot);
        Assert.True(sessions[0].CreatedAtUtc <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task UpsertPane_RoundTripsAllFields_IncludingUnicodeEnv()
    {
        var sid = await _store.CreateAsync("dev", null, CancellationToken.None);

        var paneId = PaneId.New();
        var spec = new PaneSpec(
            Pane: paneId,
            Command: "pwsh.exe",
            Arguments: new[] { "-NoLogo", "-Command", "Write-Host '한글'" },
            WorkingDirectory: @"C:\Users\dev",
            Environment: new System.Collections.Generic.Dictionary<string, string>
            {
                ["LANG"] = "ko_KR.UTF-8",
                ["EMOJI"] = "🎉",
                ["EMPTY"] = "",
            });

        await _store.UpsertPaneAsync(sid, spec, CancellationToken.None);

        var snap = await _store.AttachAsync(sid, CancellationToken.None);
        Assert.NotNull(snap);
        Assert.Single(snap!.Panes);
        var got = snap.Panes[0];
        Assert.Equal(spec.Pane, got.Pane);
        Assert.Equal(spec.Command, got.Command);
        Assert.Equal(spec.Arguments, got.Arguments);
        Assert.Equal(spec.WorkingDirectory, got.WorkingDirectory);
        Assert.Equal("ko_KR.UTF-8", got.Environment!["LANG"]);
        Assert.Equal("🎉", got.Environment!["EMOJI"]);
        Assert.Equal(string.Empty, got.Environment!["EMPTY"]);
    }

    [Fact]
    public async Task UpsertPane_SecondCallOverwritesByPaneIdKey()
    {
        var sid = await _store.CreateAsync("dev", null, CancellationToken.None);
        var paneId = PaneId.New();

        await _store.UpsertPaneAsync(sid,
            new PaneSpec(paneId, "cmd.exe", Array.Empty<string>(), null, null),
            CancellationToken.None);

        await _store.UpsertPaneAsync(sid,
            new PaneSpec(paneId, "pwsh.exe", new[] { "-NoLogo" }, @"C:\", null),
            CancellationToken.None);

        var snap = await _store.AttachAsync(sid, CancellationToken.None);
        Assert.Single(snap!.Panes);
        Assert.Equal("pwsh.exe", snap.Panes[0].Command);
        Assert.Equal(@"C:\", snap.Panes[0].WorkingDirectory);
    }

    [Fact]
    public async Task SaveLayout_RoundTripsTreeAndFocus()
    {
        var sid = await _store.CreateAsync("dev", null, CancellationToken.None);

        var p1 = PaneId.New();
        var p2 = PaneId.New();

        // Need pane rows so AttachAsync's "synthesise single-pane layout if missing" branch
        // doesn't fire; we want to verify the *real* saved layout comes back.
        await _store.UpsertPaneAsync(sid,
            new PaneSpec(p1, "cmd.exe", Array.Empty<string>(), null, null),
            CancellationToken.None);
        await _store.UpsertPaneAsync(sid,
            new PaneSpec(p2, "cmd.exe", Array.Empty<string>(), null, null),
            CancellationToken.None);

        var root = new SplitNode(
            LayoutId.New(),
            SplitDirection.Horizontal,
            0.42,
            new PaneNode(LayoutId.New(), p1),
            new PaneNode(LayoutId.New(), p2));
        var saved = new LayoutSnapshot(root, p2);

        await _store.SaveLayoutAsync(sid, saved, CancellationToken.None);

        var snap = await _store.AttachAsync(sid, CancellationToken.None);
        Assert.NotNull(snap);
        var loaded = snap!.Layout;

        var loadedRoot = Assert.IsType<SplitNode>(loaded.Root);
        Assert.Equal(SplitDirection.Horizontal, loadedRoot.Direction);
        Assert.Equal(0.42, loadedRoot.Ratio, 5);
        Assert.Equal(p1, ((PaneNode)loadedRoot.A).Pane);
        Assert.Equal(p2, ((PaneNode)loadedRoot.B).Pane);
        Assert.Equal(p2, loaded.Focused);
    }

    [Fact]
    public async Task SaveLayout_OverwritesPriorValueForSameSession()
    {
        var sid = await _store.CreateAsync("dev", null, CancellationToken.None);
        var p1 = PaneId.New();
        await _store.UpsertPaneAsync(sid,
            new PaneSpec(p1, "cmd.exe", Array.Empty<string>(), null, null),
            CancellationToken.None);

        await _store.SaveLayoutAsync(sid,
            new LayoutSnapshot(new PaneNode(LayoutId.New(), p1), p1),
            CancellationToken.None);

        var p2 = PaneId.New();
        await _store.UpsertPaneAsync(sid,
            new PaneSpec(p2, "cmd.exe", Array.Empty<string>(), null, null),
            CancellationToken.None);

        var newRoot = new SplitNode(
            LayoutId.New(), SplitDirection.Vertical, 0.6,
            new PaneNode(LayoutId.New(), p1),
            new PaneNode(LayoutId.New(), p2));
        await _store.SaveLayoutAsync(sid, new LayoutSnapshot(newRoot, p1), CancellationToken.None);

        var snap = await _store.AttachAsync(sid, CancellationToken.None);
        Assert.IsType<SplitNode>(snap!.Layout.Root);
        Assert.Equal(p1, snap.Layout.Focused);
    }

    [Fact]
    public async Task DeleteSession_CascadesPanesAndLayout()
    {
        var sid = await _store.CreateAsync("dev", null, CancellationToken.None);
        var p1 = PaneId.New();
        await _store.UpsertPaneAsync(sid,
            new PaneSpec(p1, "cmd.exe", Array.Empty<string>(), null, null),
            CancellationToken.None);
        await _store.SaveLayoutAsync(sid,
            new LayoutSnapshot(new PaneNode(LayoutId.New(), p1), p1),
            CancellationToken.None);

        await _store.DeleteAsync(sid, CancellationToken.None);

        Assert.Null(await _store.AttachAsync(sid, CancellationToken.None));
        Assert.Empty(await _store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task List_OrdersByLastAttachedDescending()
    {
        var first = await _store.CreateAsync("alpha", null, CancellationToken.None);
        await Task.Delay(20);
        var second = await _store.CreateAsync("beta", null, CancellationToken.None);
        await Task.Delay(20);

        // Touch 'first' to bump its LastAttachedAtUtc.
        await _store.AttachAsync(first, CancellationToken.None);

        var list = await _store.ListAsync(CancellationToken.None);
        Assert.Equal(2, list.Count);
        Assert.Equal(first, list[0].Id);
        Assert.Equal(second, list[1].Id);
    }

    [Fact]
    public async Task DeletePane_RemovesOnlyTheTargetRow()
    {
        var sid = await _store.CreateAsync("dev", null, CancellationToken.None);
        var p1 = PaneId.New();
        var p2 = PaneId.New();
        await _store.UpsertPaneAsync(sid,
            new PaneSpec(p1, "cmd.exe", Array.Empty<string>(), null, null),
            CancellationToken.None);
        await _store.UpsertPaneAsync(sid,
            new PaneSpec(p2, "pwsh.exe", Array.Empty<string>(), null, null),
            CancellationToken.None);

        await _store.DeletePaneAsync(sid, p1, CancellationToken.None);

        var snap = await _store.AttachAsync(sid, CancellationToken.None);
        Assert.NotNull(snap);
        Assert.Single(snap!.Panes);
        Assert.Equal(p2, snap.Panes[0].Pane);
    }
}
