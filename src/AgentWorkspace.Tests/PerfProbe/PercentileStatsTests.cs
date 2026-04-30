using AgentWorkspace.PerfProbe;

namespace AgentWorkspace.Tests.PerfProbe;

/// <summary>
/// Day 54 — verifies <see cref="PercentileStats"/> uses the same R-7 linear-interpolation
/// convention as BenchmarkDotNet, so probe output stays comparable with BDN benches.
/// </summary>
public sealed class PercentileStatsTests
{
    [Fact]
    public void Empty_ReturnsZeroes()
    {
        var s = PercentileStats.From([]);
        Assert.Equal(0, s.Count);
        Assert.Equal(0, s.P95);
    }

    [Fact]
    public void SingleSample_AllFieldsEqual()
    {
        var s = PercentileStats.From([12.5]);
        Assert.Equal(1,    s.Count);
        Assert.Equal(12.5, s.Min);
        Assert.Equal(12.5, s.Max);
        Assert.Equal(12.5, s.P50);
        Assert.Equal(12.5, s.P95);
        Assert.Equal(12.5, s.P99);
    }

    [Fact]
    public void HundredSequentialSamples_R7Percentiles()
    {
        // 1..100 — R-7 percentiles match NumPy: p50=50.5, p95=95.05, p99=99.01
        var samples = new double[100];
        for (var i = 0; i < 100; i++) samples[i] = i + 1;

        var s = PercentileStats.From(samples);
        Assert.Equal(100,   s.Count);
        Assert.Equal(1.0,   s.Min);
        Assert.Equal(100.0, s.Max);
        Assert.Equal(50.5,  s.P50,  3);
        Assert.Equal(95.05, s.P95,  3);
        Assert.Equal(99.01, s.P99,  3);
    }

    [Fact]
    public void UnsortedInput_ProducesSameResultAsSorted()
    {
        var unsorted = new double[] { 9, 1, 5, 3, 7, 4, 8, 2, 6, 10 };
        var sorted   = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        Assert.Equal(PercentileStats.From(sorted), PercentileStats.From(unsorted));
    }
}
