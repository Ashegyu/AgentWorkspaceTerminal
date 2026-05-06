using System.Runtime.CompilerServices;
using AgentWorkspace.Abstractions.Channels;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Abstractions.Pty;
using AgentWorkspace.App.Wpf;

namespace AgentWorkspace.Tests.App;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class WorkspaceFocusGuardTests
{
    [Fact]
    public async Task TryFocusPane_ReturnsFalse_WhenPaneIsInLayoutButSessionIsMissing()
    {
        var registered = PaneId.New();
        var missingSession = PaneId.New();
        var layout = TwoPaneLayout(registered, missingSession, registered);
        await using var channel = new FakeChannel();
        await using var workspace = NewWorkspace(channel, layout);
        workspace.Register(registered);

        var focused = workspace.TryFocusPane(missingSession, out var snapshot);

        Assert.False(focused);
        Assert.Null(snapshot);
        Assert.Equal(registered, workspace.Layout.Current.Focused);
    }

    [Fact]
    public async Task TryFocusPane_ReturnsFalse_ForUnknownPane()
    {
        var registered = PaneId.New();
        var other = PaneId.New();
        var layout = TwoPaneLayout(registered, other, registered);
        await using var channel = new FakeChannel();
        await using var workspace = NewWorkspace(channel, layout);
        workspace.Register(registered);
        workspace.Register(other);

        var focused = workspace.TryFocusPane(PaneId.New(), out var snapshot);

        Assert.False(focused);
        Assert.Null(snapshot);
        Assert.Equal(registered, workspace.Layout.Current.Focused);
    }

    [Fact]
    public async Task TryFocusPane_FocusesRegisteredPane()
    {
        var first = PaneId.New();
        var second = PaneId.New();
        var layout = TwoPaneLayout(first, second, first);
        await using var channel = new FakeChannel();
        await using var workspace = NewWorkspace(channel, layout);
        workspace.Register(first);
        workspace.Register(second);

        var focused = workspace.TryFocusPane(second, out var snapshot);

        Assert.True(focused);
        Assert.NotNull(snapshot);
        Assert.Equal(second, snapshot!.Focused);
        Assert.Equal(second, workspace.Layout.Current.Focused);
    }

    private static Workspace NewWorkspace(FakeChannel channel, LayoutSnapshot layout) => new(
        id => new PaneSession(id, _ => ValueTask.CompletedTask, channel, channel),
        defaultOptionsFactory: () => new PaneStartOptions(
            "pwsh.exe",
            Array.Empty<string>(),
            null,
            null,
            80,
            25),
        initialLayout: layout);

    private static LayoutSnapshot TwoPaneLayout(PaneId first, PaneId second, PaneId focused) =>
        new(
            new SplitNode(
                LayoutId.New(),
                SplitDirection.Horizontal,
                0.5,
                new PaneNode(LayoutId.New(), first),
                new PaneNode(LayoutId.New(), second)),
            focused);

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
