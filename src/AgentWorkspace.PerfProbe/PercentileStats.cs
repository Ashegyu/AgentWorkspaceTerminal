using System;
using System.Collections.Generic;

namespace AgentWorkspace.PerfProbe;

/// <summary>
/// Percentile statistics for a stream of double samples (ADR-008 latency budgets).
/// Linear interpolation between adjacent ranks — same convention BenchmarkDotNet uses for
/// its p95 column, so probe output stays comparable with `Mvp8/*Bench.cs` numbers.
/// </summary>
public readonly record struct PercentileStats(
    int    Count,
    double Min,
    double Max,
    double P50,
    double P95,
    double P99)
{
    public static PercentileStats From(IReadOnlyList<double> samples)
    {
        if (samples is null || samples.Count == 0)
            return new PercentileStats(0, 0, 0, 0, 0, 0);

        var sorted = new double[samples.Count];
        for (var i = 0; i < samples.Count; i++) sorted[i] = samples[i];
        Array.Sort(sorted);

        return new PercentileStats(
            Count: sorted.Length,
            Min:   sorted[0],
            Max:   sorted[^1],
            P50:   Percentile(sorted, 0.50),
            P95:   Percentile(sorted, 0.95),
            P99:   Percentile(sorted, 0.99));
    }

    /// <summary>Linear-interpolation percentile (R-7, identical to NumPy's default).</summary>
    private static double Percentile(double[] sortedAsc, double rank)
    {
        if (sortedAsc.Length == 1) return sortedAsc[0];

        var pos    = rank * (sortedAsc.Length - 1);
        var lower  = (int)Math.Floor(pos);
        var upper  = (int)Math.Ceiling(pos);
        if (lower == upper) return sortedAsc[lower];

        var frac   = pos - lower;
        return sortedAsc[lower] + (sortedAsc[upper] - sortedAsc[lower]) * frac;
    }
}
