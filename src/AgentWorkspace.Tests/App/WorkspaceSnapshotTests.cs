using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Channels;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Abstractions.Pty;
using AgentWorkspace.Abstractions.Templates;
using AgentWorkspace.App.Wpf;
using AgentWorkspace.Core.Templates;

namespace AgentWorkspace.Tests.App;

[SupportedOSPlatform("windows")]
public sealed class WorkspaceSnapshotTests
{
    // ── FakeChannel ───────────────────────────────────────────────────────────

    private sealed class FakeChannel : IControlChannel, IDataChannel
    {
#pragma warning disable CS0067
        public event EventHandler<PaneExitedEventArgs>? PaneExited;
#pragma warning restore CS0067

        public ValueTask<PaneState> StartPaneAsync(
            PaneId id, PaneStartOptions options, CancellationToken cancellationToken) =>
            ValueTask.FromResult(PaneState.Running);

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

    // ── helpers ───────────────────────────────────────────────────────────────

    private static readonly PaneStartOptions DefaultOpts = new(
        Command: "cmd.exe",
        Arguments: Array.Empty<string>(),
        WorkingDirectory: null,
        Environment: null,
        InitialColumns: 80,
        InitialRows: 25);

    private static ValueTask NullPostToWeb(string _) => ValueTask.CompletedTask;

    private static Workspace MakeWorkspace(
        FakeChannel ch, PaneId firstPane, PaneStartOptions? opts = null) =>
        new(
            sessionFactory: id => new PaneSession(id, NullPostToWeb, ch, ch),
            defaultOptionsFactory: () => opts ?? DefaultOpts,
            initial: firstPane);

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveSnapshotAsync_SinglePane_WritesYamlFile()
    {
        var ch = new FakeChannel();
        var firstPane = PaneId.New();
        await using var ws = MakeWorkspace(ch, firstPane);

        var session = ws.Register(firstPane);
        await session.StartAsync(DefaultOpts, CancellationToken.None);

        var path = Path.Combine(Path.GetTempPath(), $"awtd-test-{Guid.NewGuid()}.yaml");
        try
        {
            await ws.SaveSnapshotAsync(path, "single-pane-snapshot");

            Assert.True(File.Exists(path));
            var content = await File.ReadAllTextAsync(path);
            Assert.Contains("single-pane-snapshot", content);
            Assert.Contains("pane-1", content);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveSnapshotAsync_SinglePane_RoundtripsToOnePane()
    {
        var ch = new FakeChannel();
        var firstPane = PaneId.New();
        await using var ws = MakeWorkspace(ch, firstPane);

        var session = ws.Register(firstPane);
        await session.StartAsync(DefaultOpts, CancellationToken.None);

        var path = Path.Combine(Path.GetTempPath(), $"awtd-test-{Guid.NewGuid()}.yaml");
        try
        {
            await ws.SaveSnapshotAsync(path, "one-pane");

            var loaded = await new YamlTemplateLoader().LoadAsync(path);

            Assert.Equal("one-pane", loaded.Name);
            Assert.Single(loaded.Panes);
            Assert.Equal("pane-1", loaded.Panes[0].Id);
            Assert.IsType<PaneRefTemplate>(loaded.Layout);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveSnapshotAsync_TwoPanes_RoundtripsHorizontalSplit()
    {
        var ch = new FakeChannel();
        var firstPane = PaneId.New();
        await using var ws = MakeWorkspace(ch, firstPane);

        var firstSession = ws.Register(firstPane);
        await firstSession.StartAsync(DefaultOpts, CancellationToken.None);

        await ws.OpenSplitAsync(firstPane, SplitDirection.Horizontal, CancellationToken.None);
        Assert.Equal(2, ws.Layout.Panes.Count);

        var path = Path.Combine(Path.GetTempPath(), $"awtd-test-{Guid.NewGuid()}.yaml");
        try
        {
            await ws.SaveSnapshotAsync(path, "two-pane-h");

            var loaded = await new YamlTemplateLoader().LoadAsync(path);

            Assert.Equal(2, loaded.Panes.Count);
            var split = Assert.IsType<SplitNodeTemplate>(loaded.Layout);
            Assert.Equal(SplitDirection.Horizontal, split.Direction);
            Assert.Equal(0.5, split.Ratio, precision: 10);

            var slots = loaded.Panes.Select(p => p.Id).ToHashSet();
            Assert.Contains("pane-1", slots);
            Assert.Contains("pane-2", slots);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveSnapshotAsync_TwoPanes_SlotRefsMatchPaneIds()
    {
        var ch = new FakeChannel();
        var firstPane = PaneId.New();
        await using var ws = MakeWorkspace(ch, firstPane);

        var firstSession = ws.Register(firstPane);
        await firstSession.StartAsync(DefaultOpts, CancellationToken.None);
        await ws.OpenSplitAsync(firstPane, SplitDirection.Vertical, CancellationToken.None);

        var path = Path.Combine(Path.GetTempPath(), $"awtd-test-{Guid.NewGuid()}.yaml");
        try
        {
            await ws.SaveSnapshotAsync(path, "slot-ref-test");

            var loaded = await new YamlTemplateLoader().LoadAsync(path);
            var split = Assert.IsType<SplitNodeTemplate>(loaded.Layout);

            var leafA = Assert.IsType<PaneRefTemplate>(split.A);
            var leafB = Assert.IsType<PaneRefTemplate>(split.B);

            var definedSlots = loaded.Panes.Select(p => p.Id).ToHashSet();
            Assert.Contains(leafA.Slot, definedSlots);
            Assert.Contains(leafB.Slot, definedSlots);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveSnapshotAsync_ThreePanes_AllSlotsPresent()
    {
        var ch = new FakeChannel();
        var firstPane = PaneId.New();
        await using var ws = MakeWorkspace(ch, firstPane);

        var firstSession = ws.Register(firstPane);
        await firstSession.StartAsync(DefaultOpts, CancellationToken.None);

        var second = await ws.OpenSplitAsync(firstPane, SplitDirection.Horizontal, CancellationToken.None);
        await ws.OpenSplitAsync(second, SplitDirection.Vertical, CancellationToken.None);
        Assert.Equal(3, ws.Layout.Panes.Count);

        var path = Path.Combine(Path.GetTempPath(), $"awtd-test-{Guid.NewGuid()}.yaml");
        try
        {
            await ws.SaveSnapshotAsync(path, "three-pane");

            var loaded = await new YamlTemplateLoader().LoadAsync(path);

            Assert.Equal(3, loaded.Panes.Count);
            var slots = loaded.Panes.Select(p => p.Id).ToHashSet();
            Assert.Contains("pane-1", slots);
            Assert.Contains("pane-2", slots);
            Assert.Contains("pane-3", slots);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SaveSnapshotAsync_PaneCommand_IsPreservedInYaml()
    {
        var ch = new FakeChannel();
        var firstPane = PaneId.New();
        var opts = new PaneStartOptions(
            Command: "pwsh.exe",
            Arguments: new[] { "-NoProfile" },
            WorkingDirectory: @"C:\Projects",
            Environment: null,
            InitialColumns: 120,
            InitialRows: 30);

        await using var ws = MakeWorkspace(ch, firstPane, opts);

        var session = ws.Register(firstPane);
        await session.StartAsync(opts, CancellationToken.None);

        var path = Path.Combine(Path.GetTempPath(), $"awtd-test-{Guid.NewGuid()}.yaml");
        try
        {
            await ws.SaveSnapshotAsync(path, "pwsh-snapshot");

            var loaded = await new YamlTemplateLoader().LoadAsync(path);

            var pane = Assert.Single(loaded.Panes);
            Assert.Equal("pwsh.exe", pane.Command);
            Assert.Equal(@"C:\Projects", pane.Cwd);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
