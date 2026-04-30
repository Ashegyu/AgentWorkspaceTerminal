using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgentWorkspace.PerfProbe;

/// <summary>
/// ADR-008 #3 — full-stack 4-pane idle RSS (≤ 500 MB).
/// Where <c>rss</c> measures only the daemon-side floor (probe + ConPTY children),
/// <c>rss-full</c> walks the descendant tree of an already-running App.Wpf (or any
/// supplied PID) and sums <c>WorkingSet64</c> across every transitive child —
/// notably the <c>msedgewebview2.exe</c> renderer / GPU / utility processes that
/// host xterm.js. baseline.json carries this number as
/// <c>fourPaneIdleRssFullMb</c>; the existing <c>fourPaneIdleRssMb</c> stays as
/// the daemon-floor tracer.
///
/// Probe must be launched with the App.Wpf already running and the user-chosen
/// pane configuration loaded — the probe itself does not start UI processes.
/// </summary>
internal static class RssFullCommand
{
    public static int Run(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string[] args)
    {
        int?    pid          = null;
        string? processName  = null;
        var     warmupSec    = 3;
        var     sampleSec    = 5;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pid" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var parsedPid) || parsedPid <= 0)
                        return UsageError($"invalid --pid '{args[i]}'");
                    pid = parsedPid;
                    break;
                case "--process-name" when i + 1 < args.Length:
                    processName = args[++i];
                    if (string.IsNullOrWhiteSpace(processName))
                        return UsageError("--process-name must not be empty");
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

        if (pid is null && processName is null)
        {
            return UsageError("must supply --pid or --process-name");
        }

        if (pid is null)
        {
            var matches = Process.GetProcessesByName(processName!);
            try
            {
                if (matches.Length == 0)
                    return UsageError($"no running process named '{processName}'");
                if (matches.Length > 1)
                    return UsageError($"multiple processes named '{processName}' (PIDs: {string.Join(",", matches.Select(p => p.Id))}); use --pid");
                pid = matches[0].Id;
            }
            finally
            {
                foreach (var p in matches) p.Dispose();
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(warmupSec)).ConfigureAwait(false);

        // Sample N times (1 Hz). Each sample re-walks the tree, since WebView2
        // children come and go (e.g. when a pane is closed/reopened).
        var samples = new List<long>(sampleSec);
        IReadOnlyList<ProcessTreeWalker.ProcessNode>? lastTree = null;
        for (var i = 0; i < sampleSec; i++)
        {
            var tree   = ProcessTreeWalker.Walk(pid!.Value);
            if (tree.Count == 0)
            {
                Console.Error.WriteLine($"rss-full: target pid {pid} disappeared mid-walk");
                return 1;
            }
            var totalWs = 0L;
            foreach (var n in tree) totalWs += n.WorkingSetBytes;
            samples.Add(totalWs);
            lastTree = tree;
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        var sorted = samples.ToArray();
        Array.Sort(sorted);
        var min  = sorted[0];
        var peak = sorted[^1];
        var p50  = Percentile(sorted, 0.50);
        var p95  = Percentile(sorted, 0.95);

        // Breakdown is taken from the most recent walk — children appearing/
        // disappearing across samples is rare for a settled UI but possible.
        var breakdown = (lastTree ?? Array.Empty<ProcessTreeWalker.ProcessNode>())
            .GroupBy(n => n.Name)
            .Select(g => new
            {
                name        = g.Key,
                count       = g.Count(),
                totalMb     = ToMb(g.Sum(n => n.WorkingSetBytes)),
                pids        = g.Select(n => n.Pid).OrderBy(x => x).ToArray(),
            })
            .OrderByDescending(x => x.totalMb)
            .ToArray();

        var thresholdMb = 500.0; // ADR-008 #3
        var pass        = ToMb(peak) <= thresholdMb;

        var payload = new Dictionary<string, object?>
        {
            ["metric"]            = "fourPaneIdleRssFullMb",
            ["adr008Item"]        = 3,
            ["scope"]             = "full-stack (App.Wpf + descendants — WebView2 host/GPU/utility/renderer + ConPTY childs)",
            ["rootPid"]           = pid,
            ["warmupSec"]         = warmupSec,
            ["sampleSec"]         = sampleSec,
            ["sampleCount"]       = samples.Count,
            ["minMb"]             = ToMb(min),
            ["peakMb"]            = ToMb(peak),
            ["p50Mb"]             = ToMb(p50),
            ["p95Mb"]             = ToMb(p95),
            ["thresholdMb"]       = thresholdMb,
            ["pass"]              = pass,
            ["processBreakdown"]  = breakdown,
        };
        Console.WriteLine(JsonSerializer.Serialize(payload));
        return pass ? 0 : 1;
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

    private static int UsageError(string msg)
    {
        Console.Error.WriteLine($"rss-full: {msg}");
        PrintUsage();
        return 64;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            awt-perfprobe rss-full — ADR-008 #3 full-stack 4-pane idle RSS

            Usage:
              awt-perfprobe rss-full (--pid N | --process-name NAME) [--warmup-sec 3] [--sample-sec 5]

            Walks the descendant tree of the supplied process, sums Process.WorkingSet64
            across every transitive child, and records the peak / p95 / breakdown.
            Required to capture the WPF + WebView2 contribution that 'rss' (daemon-floor)
            does not see.

            Pre-conditions:
              - Target process is already running.
              - User has loaded the desired pane configuration (e.g. 4 panes for ADR-008 #3).

            Output (single-line JSON):
              {"metric":"fourPaneIdleRssFullMb","rootPid":N,"peakMb":..,"p95Mb":..,
               "thresholdMb":500,"pass":true|false,"processBreakdown":[{"name":..,
               "count":N,"totalMb":..,"pids":[..]}, ...]}

            Exit 0 = within ADR-008 #3 ceiling, 1 = exceeded.
            """);
    }
}
