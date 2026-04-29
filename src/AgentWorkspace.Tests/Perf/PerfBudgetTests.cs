using System;
using System.Diagnostics;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;
using AgentWorkspace.App.Wpf;

namespace AgentWorkspace.Tests.Perf;

/// <summary>
/// Cheap latency guards for ops that BenchmarkDotNet measures more precisely. The thresholds
/// are deliberately loose (orders of magnitude above the BenchmarkDotNet-measured numbers) so
/// CI can't get false-positives from noisy machines, while still catching the kind of
/// accidental quadratic that turns a microsecond into a millisecond.
/// </summary>
/// <remarks>
/// We do not run these on every test invocation; the tests warm-up before timing, and timing
/// is over thousands of iterations to dilute single-call jitter.
/// </remarks>
public sealed class PerfBudgetTests
{
    [Fact]
    public void Layout_Split_AverageUnderOneMillisecond_With16PaneTree()
    {
        // Each iteration starts with a fresh 16-pane tree and times a single Split. Without the
        // per-iteration reset the tree would grow with each Split call (the workspace doesn't
        // forget what we did) and the test would actually be measuring O(n²) traversal cost,
        // which is not the regression we care to guard against.
        const int iterations = 1000;
        double totalUs = 0;

        // Warm-up loop without measurement.
        for (int i = 0; i < 50; i++)
        {
            var (m, _) = BuildTree(16);
            m.Split(m.Current.Focused, SplitDirection.Horizontal);
        }

        for (int i = 0; i < iterations; i++)
        {
            var (m, _) = BuildTree(16);
            var sw = Stopwatch.StartNew();
            m.Split(m.Current.Focused, SplitDirection.Horizontal);
            sw.Stop();
            totalUs += sw.Elapsed.TotalMicroseconds;
        }

        double avgUs = totalUs / iterations;
        // BenchmarkDotNet shows ~5μs for a 16-pane Split. 1000μs is a 200× headroom — generous
        // enough to absorb CI noise without missing a real quadratic regression.
        Assert.True(avgUs < 1000,
            $"Split average {avgUs:F1}μs exceeds 1000μs budget on a 16-pane tree.");
    }

    private static (BinaryLayoutManager Mgr, PaneId Last) BuildTree(int paneCount)
    {
        var first = PaneId.New();
        var mgr = new BinaryLayoutManager(first);
        var last = first;
        for (int i = 1; i < paneCount; i++)
        {
            last = mgr.Split(last, SplitDirection.Horizontal).NewPane;
        }
        return (mgr, last);
    }

    [Fact]
    public void Layout_FocusNext_AverageUnderTenMicroseconds_With64PaneTree()
    {
        var first = PaneId.New();
        var mgr = new BinaryLayoutManager(first);
        var last = first;
        for (int i = 1; i < 64; i++)
        {
            last = mgr.Split(last, SplitDirection.Horizontal).NewPane;
        }

        for (int i = 0; i < 1000; i++) mgr.FocusNext();

        const int iterations = 100_000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) mgr.FocusNext();
        sw.Stop();

        double avgUs = sw.Elapsed.TotalMicroseconds / iterations;
        // 10μs / call gives 200× headroom over BenchmarkDotNet-measured single-digit μs.
        Assert.True(avgUs < 10,
            $"FocusNext average {avgUs:F2}μs exceeds 10μs budget on a 64-pane tree.");
    }

    [Fact]
    public void Envelope_Output_64KB_AverageUnderOneMillisecond()
    {
        var id = PaneId.New();
        var payload = new byte[64 * 1024];
        new Random(42).NextBytes(payload);

        for (int i = 0; i < 50; i++) _ = Envelope.Output(id, payload);

        const int iterations = 1000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            _ = Envelope.Output(id, payload);
        }
        sw.Stop();

        double avgUs = sw.Elapsed.TotalMicroseconds / iterations;
        // 1ms gives ample headroom over the ~120μs/call BenchmarkDotNet measures for 64 KB
        // payloads (mostly base64 encoding cost). Catches an accidental O(n^2) regression in
        // string assembly or JSON writing.
        Assert.True(avgUs < 1000,
            $"Envelope.Output (64 KB) average {avgUs:F1}μs exceeds 1000μs budget.");
    }
}
