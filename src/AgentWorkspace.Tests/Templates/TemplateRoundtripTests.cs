using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Channels;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Abstractions.Pty;
using AgentWorkspace.Abstractions.Templates;
using AgentWorkspace.Core.Templates;

namespace AgentWorkspace.Tests.Templates;

public sealed class TemplateRoundtripTests
{
    // ── FakeControlChannel ───────────────────────────────────────────────────

    private sealed class FakeControlChannel : IControlChannel
    {
        public List<PaneId> Started { get; } = [];

#pragma warning disable CS0067
        public event EventHandler<PaneExitedEventArgs>? PaneExited;
#pragma warning restore CS0067

        public ValueTask<PaneState> StartPaneAsync(
            PaneId id, PaneStartOptions options, CancellationToken cancellationToken)
        {
            Started.Add(id);
            return ValueTask.FromResult(PaneState.Running);
        }

        public ValueTask<int> ClosePaneAsync(
            PaneId id, KillMode mode, CancellationToken cancellationToken) =>
            ValueTask.FromResult(0);

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

    private static string TestData(string fileName) =>
        Path.Combine("TestData", fileName);

    private static async Task<(WorkspaceTemplate Template, TemplateRunResult Result)> LoadAndRunAsync(
        string yamlFileName)
    {
        var loader = new YamlTemplateLoader();
        var template = await loader.LoadAsync(TestData(yamlFileName));
        var runner = new TemplateRunner(new FakeControlChannel());
        var result = await runner.RunAsync(template);
        return (template, result);
    }

    private static async Task<(WorkspaceTemplate Template, TemplateRunResult Result, FakeControlChannel Channel)>
        LoadAndRunWithChannelAsync(string yamlFileName)
    {
        var ch = new FakeControlChannel();
        var loader = new YamlTemplateLoader();
        var template = await loader.LoadAsync(TestData(yamlFileName));
        var runner = new TemplateRunner(ch);
        var result = await runner.RunAsync(template);
        return (template, result, ch);
    }

    // ── basic.yaml ───────────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAndRun_BasicYaml_StartsTwoPanes()
    {
        var (_, result, ch) = await LoadAndRunWithChannelAsync("basic.yaml");

        Assert.Equal(2, ch.Started.Count);
        Assert.Equal(2, result.SlotToPaneId.Count);
    }

    [Fact]
    public async Task LoadAndRun_BasicYaml_SplitNodeRatioIsCorrect()
    {
        var (_, result) = await LoadAndRunAsync("basic.yaml");

        var split = Assert.IsType<SplitNode>(result.Layout.Root);
        Assert.Equal(SplitDirection.Horizontal, split.Direction);
        Assert.Equal(0.65, split.Ratio, precision: 10);
    }

    [Fact]
    public async Task LoadAndRun_BasicYaml_FocusIsEditor()
    {
        var (_, result) = await LoadAndRunAsync("basic.yaml");

        Assert.Equal(result.SlotToPaneId["editor"], result.Layout.Focused);
    }

    [Fact]
    public async Task LoadAndRun_BasicYaml_SlotMapContainsEditorAndShell()
    {
        var (_, result) = await LoadAndRunAsync("basic.yaml");

        Assert.True(result.SlotToPaneId.ContainsKey("editor"));
        Assert.True(result.SlotToPaneId.ContainsKey("shell"));
    }

    // ── three-pane.yaml ──────────────────────────────────────────────────────

    [Fact]
    public async Task LoadAndRun_ThreePaneYaml_StartsThreePanes()
    {
        var (_, result, ch) = await LoadAndRunWithChannelAsync("three-pane.yaml");

        Assert.Equal(3, ch.Started.Count);
        Assert.Equal(3, result.SlotToPaneId.Count);
    }

    [Fact]
    public async Task LoadAndRun_ThreePaneYaml_RootIsVerticalSplit()
    {
        var (_, result) = await LoadAndRunAsync("three-pane.yaml");

        var split = Assert.IsType<SplitNode>(result.Layout.Root);
        Assert.Equal(SplitDirection.Vertical, split.Direction);
        Assert.Equal(0.65, split.Ratio, precision: 10);
    }

    [Fact]
    public async Task LoadAndRun_ThreePaneYaml_InnerSplitIsHorizontal()
    {
        var (_, result) = await LoadAndRunAsync("three-pane.yaml");

        var outer = Assert.IsType<SplitNode>(result.Layout.Root);
        var inner = Assert.IsType<SplitNode>(outer.A);
        Assert.Equal(SplitDirection.Horizontal, inner.Direction);
    }

    [Fact]
    public async Task LoadAndRun_ThreePaneYaml_FocusIsEditor()
    {
        var (_, result) = await LoadAndRunAsync("three-pane.yaml");

        Assert.Equal(result.SlotToPaneId["editor"], result.Layout.Focused);
    }
}
