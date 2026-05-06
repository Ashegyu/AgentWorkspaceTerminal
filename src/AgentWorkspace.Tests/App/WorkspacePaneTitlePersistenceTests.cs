using System.Runtime.CompilerServices;
using AgentWorkspace.Abstractions.Channels;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Abstractions.Pty;
using AgentWorkspace.Abstractions.Sessions;
using AgentWorkspace.App.Wpf;

namespace AgentWorkspace.Tests.App;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WorkspacePaneTitlePersistenceTests
{
    [Fact]
    public async Task PersistPaneTitleAsync_ForStoredWorkspace_UpdatesOnlyPaneTitle()
    {
        var pane = PaneId.New();
        var session = SessionId.New();
        var store = new FakeStore();
        await using var channel = new FakeChannel();
        await using var workspace = new Workspace(
            id => new PaneSession(id, _ => ValueTask.CompletedTask, channel, channel),
            defaultOptionsFactory: () => new PaneStartOptions(
                "pwsh.exe",
                Array.Empty<string>(),
                null,
                null,
                80,
                25),
            initial: pane,
            store: store,
            sessionId: session);

        await workspace.PersistPaneTitleAsync(pane, "api server", CancellationToken.None);

        Assert.Equal(session, store.UpdatedSessionId);
        Assert.Equal(pane, store.UpdatedPaneId);
        Assert.Equal("api server", store.UpdatedTitle);
    }

    private sealed class FakeStore : ISessionStore
    {
        public SessionId? UpdatedSessionId { get; private set; }
        public PaneId? UpdatedPaneId { get; private set; }
        public string? UpdatedTitle { get; private set; }

        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<SessionId> CreateAsync(string name, string? workspaceRoot, CancellationToken cancellationToken) =>
            ValueTask.FromResult(SessionId.New());
        public ValueTask<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<SessionInfo>>(Array.Empty<SessionInfo>());
        public ValueTask<SessionSnapshot?> AttachAsync(SessionId id, CancellationToken cancellationToken) =>
            ValueTask.FromResult<SessionSnapshot?>(null);
        public ValueTask UpsertPaneAsync(SessionId id, PaneSpec pane, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask DeletePaneAsync(SessionId id, PaneId pane, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask SaveLayoutAsync(SessionId id, LayoutSnapshot layout, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask DeleteAsync(SessionId id, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask UpdatePaneTitleAsync(
            SessionId id,
            PaneId pane,
            string? title,
            CancellationToken cancellationToken)
        {
            UpdatedSessionId = id;
            UpdatedPaneId = pane;
            UpdatedTitle = title;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeChannel : IControlChannel, IDataChannel
    {
#pragma warning disable CS0067
        public event EventHandler<PaneExitedEventArgs>? PaneExited;
#pragma warning restore CS0067

        public ValueTask<PaneState> StartPaneAsync(
            PaneId id,
            PaneStartOptions options,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(PaneState.Running);

        public ValueTask<int> ClosePaneAsync(PaneId id, KillMode mode, CancellationToken cancellationToken) =>
            ValueTask.FromResult(0);

        public ValueTask WriteInputAsync(PaneId id, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask ResizePaneAsync(PaneId id, short columns, short rows, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask SignalPaneAsync(PaneId id, PtySignal signal, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

#pragma warning disable CS1998
        public async IAsyncEnumerable<PaneFrame> SubscribeAsync(
            PaneId pane,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield break;
        }
#pragma warning restore CS1998

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
