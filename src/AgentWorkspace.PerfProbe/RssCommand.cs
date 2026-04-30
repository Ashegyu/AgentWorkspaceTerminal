using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Pty;
using AgentWorkspace.ConPTY;

namespace AgentWorkspace.PerfProbe;

/// <summary>
/// ADR-008 #3 (4-pane idle RSS ≤ 500 MB) and #4 (1-pane delta ≤ 30 MB).
/// Boots N <see cref="PseudoConsoleProcess"/> instances running an idle child
/// (<c>cmd /k</c> with no stdin), waits warmup, then samples
/// <see cref="Process.WorkingSet64"/> every second for the requested window.
///
/// IMPORTANT: probe measures *host process RSS*. The full ADR-008 #3 budget
/// includes the WPF app + WebView2 + xterm.js renderers, which this probe does
/// not boot. Use the numbers here as a daemon-side floor; the renderer
/// contribution is captured separately (manual run from App.Wpf).
/// </summary>
internal static class RssCommand
{
    public static int Run(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string[] args)
    {
        var panes        = 1;
        var warmupSec    = 3;
        var sampleSec    = 5;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--panes" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out panes) || panes < 0 || panes > 32)
                        return UsageError($"invalid --panes '{args[i]}'");
                    break;
                case "--warmup-sec" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out warmupSec) || warmupSec < 0 || warmupSec > 60)
                        return UsageError($"invalid --warmup-sec '{args[i]}'");
                    break;
                case "--sample-sec" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out sampleSec) || sampleSec < 1 || sampleSec > 120)
                        return UsageError($"invalid --sample-sec '{args[i]}'");
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return 0;
                default:
                    return UsageError($"unknown arg '{args[i]}'");
            }
        }

        // Capture baseline before any pane starts.
        Settle();
        var baseline = new ProcessSnapshot();

        var processes = new List<PseudoConsoleProcess>(panes);
        try
        {
            for (var i = 0; i < panes; i++)
            {
                var p = new PseudoConsoleProcess(PaneId.New());
                await p.StartAsync(IdleChildOptions(), CancellationToken.None).ConfigureAwait(false);
                processes.Add(p);
            }

            await Task.Delay(TimeSpan.FromSeconds(warmupSec)).ConfigureAwait(false);

            var samples = new List<long>(sampleSec);
            for (var i = 0; i < sampleSec; i++)
            {
                Settle();
                samples.Add(Process.GetCurrentProcess().WorkingSet64);
                await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }

            var stats = ComputeStats(samples);
            var deltaBytes = stats.Peak - baseline.WorkingSet;

            var payload = new Dictionary<string, object?>
            {
                ["metric"]        = panes == 1 ? "onePaneRssDeltaMb" :
                                    panes == 4 ? "fourPaneIdleRssMb" : "rssMb",
                ["adr008Item"]    = panes == 1 ? 4 : (panes == 4 ? 3 : (int?)null),
                ["panes"]         = panes,
                ["warmupSec"]     = warmupSec,
                ["sampleSec"]     = sampleSec,
                ["baselineMb"]    = ToMb(baseline.WorkingSet),
                ["peakMb"]        = ToMb(stats.Peak),
                ["minMb"]         = ToMb(stats.Min),
                ["p50Mb"]         = ToMb(stats.P50),
                ["p95Mb"]         = ToMb(stats.P95),
                ["deltaMb"]       = ToMb(deltaBytes),
                ["sampleCount"]   = samples.Count,
            };
            Console.WriteLine(JsonSerializer.Serialize(payload));
            return 0;
        }
        finally
        {
            foreach (var p in processes)
            {
                try { await p.KillAsync(KillMode.Force, CancellationToken.None).ConfigureAwait(false); }
                catch { /* best-effort teardown */ }
                try { await p.DisposeAsync().ConfigureAwait(false); }
                catch { /* best-effort */ }
            }
        }
    }

    /// <summary>cmd /k with no stdin → child sits idle waiting for input it never gets.</summary>
    private static PaneStartOptions IdleChildOptions() => new(
        Command:          "cmd.exe",
        Arguments:        new[] { "/k" },
        WorkingDirectory: null,
        Environment:      null,
        InitialColumns:   80,
        InitialRows:      24);

    private static void Settle()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private readonly record struct SampleStats(long Min, long Peak, long P50, long P95);

    private static SampleStats ComputeStats(List<long> samples)
    {
        var sorted = samples.ToArray();
        Array.Sort(sorted);
        return new SampleStats(
            Min:  sorted[0],
            Peak: sorted[^1],
            P50:  Percentile(sorted, 0.50),
            P95:  Percentile(sorted, 0.95));
    }

    private static long Percentile(long[] sortedAsc, double rank)
    {
        if (sortedAsc.Length == 1) return sortedAsc[0];
        var pos    = rank * (sortedAsc.Length - 1);
        var lower  = (int)Math.Floor(pos);
        var upper  = (int)Math.Ceiling(pos);
        if (lower == upper) return sortedAsc[lower];
        var frac   = pos - lower;
        return (long)(sortedAsc[lower] + (sortedAsc[upper] - sortedAsc[lower]) * frac);
    }

    private static double ToMb(long bytes) =>
        Math.Round(bytes / 1024.0 / 1024.0, 2);

    private readonly record struct ProcessSnapshot(long WorkingSet)
    {
        public ProcessSnapshot() : this(Process.GetCurrentProcess().WorkingSet64) { }
    }

    private static int UsageError(string msg)
    {
        Console.Error.WriteLine($"rss: {msg}");
        PrintUsage();
        return 64;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            awt-perfprobe rss — ADR-008 #3 (4-pane RSS) / #4 (1-pane delta)

            Usage:
              awt-perfprobe rss [--panes 1] [--warmup-sec 3] [--sample-sec 5]

            Boots N idle ConPTY child processes ('cmd /k', stdin never written),
            waits warmup, then samples Process.WorkingSet64 every second.

            Output (single-line JSON):
              {"metric":"onePaneRssDeltaMb"|"fourPaneIdleRssMb",
               "panes":N, "baselineMb":..., "peakMb":..., "p50Mb":..., "p95Mb":...,
               "deltaMb":..., "sampleCount":N}

            NOTE: probe measures host process RSS only. The full ADR-008 #3 budget
            includes the WPF app + WebView2; that contribution is captured manually
            from App.Wpf and noted separately in baseline.json.
            """);
    }
}
