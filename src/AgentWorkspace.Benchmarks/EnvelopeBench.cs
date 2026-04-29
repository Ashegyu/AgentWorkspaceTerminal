using System;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.App.Wpf;
using BenchmarkDotNet.Attributes;

namespace AgentWorkspace.Benchmarks;

/// <summary>
/// Output-envelope construction sits on the absolute hottest path in the app: it runs once per
/// PTY chunk, multiplied by every active pane. Allocation here directly determines GC pressure
/// during heavy output (build logs, file dumps, etc.).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class EnvelopeBench
{
    private readonly PaneId _id = PaneId.New();
    private byte[] _small = new byte[64];     // typical chunk after frame coalescing — small
    private byte[] _medium = new byte[8 * 1024];   // 8 KB: PipeReader segment-ish size
    private byte[] _large = new byte[64 * 1024];   // 64 KB: large burst tile

    [GlobalSetup]
    public void Setup()
    {
        new Random(1).NextBytes(_small);
        new Random(2).NextBytes(_medium);
        new Random(3).NextBytes(_large);
    }

    [Benchmark(Description = "Envelope.Output  64 B")]
    public string Output_Small() => Envelope.Output(_id, _small);

    [Benchmark(Description = "Envelope.Output  8 KB")]
    public string Output_Medium() => Envelope.Output(_id, _medium);

    [Benchmark(Description = "Envelope.Output 64 KB")]
    public string Output_Large() => Envelope.Output(_id, _large);

    [Benchmark(Description = "Envelope.Init")]
    public string Init() => Envelope.Init(_id);

    [Benchmark(Description = "Envelope.Layout (4-pane H+V tree)")]
    public string Layout4Pane()
    {
        var p1 = PaneId.New();
        var p2 = PaneId.New();
        var p3 = PaneId.New();
        var p4 = PaneId.New();
        var snap = new Abstractions.Layout.LayoutSnapshot(
            Root: new Abstractions.Layout.SplitNode(
                Abstractions.Ids.LayoutId.New(),
                Abstractions.Layout.SplitDirection.Horizontal,
                0.5,
                new Abstractions.Layout.SplitNode(
                    Abstractions.Ids.LayoutId.New(),
                    Abstractions.Layout.SplitDirection.Vertical,
                    0.5,
                    new Abstractions.Layout.PaneNode(Abstractions.Ids.LayoutId.New(), p1),
                    new Abstractions.Layout.PaneNode(Abstractions.Ids.LayoutId.New(), p2)),
                new Abstractions.Layout.SplitNode(
                    Abstractions.Ids.LayoutId.New(),
                    Abstractions.Layout.SplitDirection.Vertical,
                    0.5,
                    new Abstractions.Layout.PaneNode(Abstractions.Ids.LayoutId.New(), p3),
                    new Abstractions.Layout.PaneNode(Abstractions.Ids.LayoutId.New(), p4))),
            Focused: p1);

        return Envelope.Layout(snap);
    }
}
