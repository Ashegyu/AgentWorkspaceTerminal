using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.Core.Layout;
using BenchmarkDotNet.Attributes;

namespace AgentWorkspace.Benchmarks;

/// <summary>
/// Layout ops are user-driven (one click → one Split/Close), so they don't sit on a hot loop —
/// but they need to be predictable. The benchmark protects against accidental quadratic
/// regressions and tells us how much memory each operation churns.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class LayoutBench
{
    private BinaryLayoutManager _mgr = null!;
    private PaneId _last;

    [Params(1, 4, 16)]
    public int InitialPaneCount;

    [IterationSetup]
    public void IterationSetup()
    {
        var first = PaneId.New();
        _mgr = new BinaryLayoutManager(first);
        _last = first;
        for (int i = 1; i < InitialPaneCount; i++)
        {
            _last = _mgr.Split(_last, SplitDirection.Horizontal).NewPane;
        }
    }

    [Benchmark(Description = "Split focused pane horizontally")]
    public PaneId SplitOnce()
    {
        return _mgr.Split(_mgr.Current.Focused, SplitDirection.Horizontal).NewPane;
    }

    [Benchmark(Description = "FocusNext")]
    public PaneId FocusNext() => _mgr.FocusNext().Focused;

    [Benchmark(Description = "Close focused pane (refused if last)")]
    public bool CloseFocused()
    {
        if (_mgr.Panes.Count <= 1) return false;
        _mgr.Close(_mgr.Current.Focused);
        return true;
    }
}
