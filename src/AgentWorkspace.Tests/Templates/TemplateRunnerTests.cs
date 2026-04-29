using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Channels;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Abstractions.Pty;
using AgentWorkspace.Abstractions.Templates;
using AgentWorkspace.Core.Templates;

namespace AgentWorkspace.Tests.Templates;

public sealed class TemplateRunnerTests
{
    // ── FakeControlChannel ───────────────────────────────────────────────────

    private sealed class FakeControlChannel : IControlChannel
    {
        public List<PaneId> Started { get; } = [];
        public List<(PaneId Id, KillMode Mode)> Closed { get; } = [];

        /// <summary>
        /// When non-null, StartPaneAsync throws this exception on the n-th call (1-based).
        /// </summary>
        public int? FailOnStartIndex { get; set; }
        private int _startCount;

#pragma warning disable CS0067
        public event EventHandler<PaneExitedEventArgs>? PaneExited;
#pragma warning restore CS0067

        public ValueTask<PaneState> StartPaneAsync(
            PaneId id, PaneStartOptions options, CancellationToken cancellationToken)
        {
            _startCount++;
            if (FailOnStartIndex.HasValue && _startCount == FailOnStartIndex.Value)
                throw new InvalidOperationException($"Fake failure on start #{_startCount}");
            Started.Add(id);
            return ValueTask.FromResult(PaneState.Running);
        }

        public ValueTask<int> ClosePaneAsync(
            PaneId id, KillMode mode, CancellationToken cancellationToken)
        {
            Closed.Add((id, mode));
            return ValueTask.FromResult(0);
        }

        public ValueTask WriteInputAsync(
            PaneId id, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask ResizePaneAsync(
            PaneId id, short columns, short rows, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask SignalPaneAsync(
            PaneId id, PtySignal signal, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static WorkspaceTemplate SinglePaneTemplate(string slotId = "shell") =>
        new(
            Name: "Test",
            Description: null,
            Panes: [new PaneTemplate(slotId, "cmd", [], null, null)],
            Layout: new PaneRefTemplate(slotId),
            Focus: null);

    private static WorkspaceTemplate TwoPaneTemplate() =>
        new(
            Name: "Two",
            Description: null,
            Panes: [
                new PaneTemplate("left", "cmd", [], null, null),
                new PaneTemplate("right", "pwsh", [], null, null)
            ],
            Layout: new SplitNodeTemplate(
                SplitDirection.Horizontal, 0.6,
                new PaneRefTemplate("left"),
                new PaneRefTemplate("right")),
            Focus: "right");

    // ── happy path ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SinglePane_StartsOnePane()
    {
        var ch = new FakeControlChannel();
        var runner = new TemplateRunner(ch);

        var result = await runner.RunAsync(SinglePaneTemplate());

        Assert.Single(ch.Started);
        Assert.Single(result.SlotToPaneId);
        Assert.True(result.SlotToPaneId.ContainsKey("shell"));
    }

    [Fact]
    public async Task RunAsync_SinglePane_LayoutIsLeafNode()
    {
        var ch = new FakeControlChannel();
        var runner = new TemplateRunner(ch);

        var result = await runner.RunAsync(SinglePaneTemplate());

        var leaf = Assert.IsType<PaneNode>(result.Layout.Root);
        Assert.Equal(result.SlotToPaneId["shell"], leaf.Pane);
    }

    [Fact]
    public async Task RunAsync_NoExplicitFocus_DefaultsToFirstPane()
    {
        var ch = new FakeControlChannel();
        var runner = new TemplateRunner(ch);

        var result = await runner.RunAsync(SinglePaneTemplate("shell"));

        Assert.Equal(result.SlotToPaneId["shell"], result.Layout.Focused);
    }

    [Fact]
    public async Task RunAsync_ExplicitFocus_ResolvedCorrectly()
    {
        var ch = new FakeControlChannel();
        var runner = new TemplateRunner(ch);

        var result = await runner.RunAsync(TwoPaneTemplate());

        Assert.Equal(result.SlotToPaneId["right"], result.Layout.Focused);
    }

    [Fact]
    public async Task RunAsync_TwoPane_StartsBothPanes()
    {
        var ch = new FakeControlChannel();
        var runner = new TemplateRunner(ch);

        var result = await runner.RunAsync(TwoPaneTemplate());

        Assert.Equal(2, ch.Started.Count);
        Assert.Contains(result.SlotToPaneId["left"], ch.Started);
        Assert.Contains(result.SlotToPaneId["right"], ch.Started);
    }

    [Fact]
    public async Task RunAsync_TwoPane_SplitNodeHasCorrectDirectionAndRatio()
    {
        var ch = new FakeControlChannel();
        var runner = new TemplateRunner(ch);

        var result = await runner.RunAsync(TwoPaneTemplate());

        var split = Assert.IsType<SplitNode>(result.Layout.Root);
        Assert.Equal(SplitDirection.Horizontal, split.Direction);
        Assert.Equal(0.6, split.Ratio, precision: 10);
    }

    [Fact]
    public async Task RunAsync_TwoPane_LeafNodesHaveCorrectPaneIds()
    {
        var ch = new FakeControlChannel();
        var runner = new TemplateRunner(ch);

        var result = await runner.RunAsync(TwoPaneTemplate());

        var split = Assert.IsType<SplitNode>(result.Layout.Root);
        var leftLeaf = Assert.IsType<PaneNode>(split.A);
        var rightLeaf = Assert.IsType<PaneNode>(split.B);

        Assert.Equal(result.SlotToPaneId["left"], leftLeaf.Pane);
        Assert.Equal(result.SlotToPaneId["right"], rightLeaf.Pane);
    }

    [Fact]
    public async Task RunAsync_EachCallProducesFreshPaneIds()
    {
        var ch = new FakeControlChannel();
        var runner = new TemplateRunner(ch);

        var r1 = await runner.RunAsync(SinglePaneTemplate());
        var r2 = await runner.RunAsync(SinglePaneTemplate());

        Assert.NotEqual(r1.SlotToPaneId["shell"], r2.SlotToPaneId["shell"]);
    }

    // ── rollback on failure ──────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SecondPaneFails_FirstPaneIsClosed()
    {
        var ch = new FakeControlChannel { FailOnStartIndex = 2 };
        var runner = new TemplateRunner(ch);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(TwoPaneTemplate()).AsTask());

        Assert.Single(ch.Started);
        Assert.Single(ch.Closed);
        Assert.Equal(ch.Started[0], ch.Closed[0].Id);
        Assert.Equal(KillMode.Force, ch.Closed[0].Mode);
    }

    [Fact]
    public async Task RunAsync_FirstPaneFails_NoPanesAreClosed()
    {
        var ch = new FakeControlChannel { FailOnStartIndex = 1 };
        var runner = new TemplateRunner(ch);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync(TwoPaneTemplate()).AsTask());

        Assert.Empty(ch.Started);
        Assert.Empty(ch.Closed);
    }

    // ── default dimensions forwarded ────────────────────────────────────────

    [Fact]
    public async Task RunAsync_DefaultDimensions_PassedThroughToOptions()
    {
        var ch = new FakeControlChannel();
        var runner = new TemplateRunner(ch, defaultCols: 100, defaultRows: 30);

        var result = await runner.RunAsync(SinglePaneTemplate());

        Assert.Single(ch.Started);
    }
}
