using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AgentWorkspace.PerfProbe;

/// <summary>
/// ADR-008 #6 — GC Gen2 collections per minute during idle ≤ 1.
/// Measures the probe's own Gen2 frequency over <c>--seconds</c>. Panes are
/// separate processes with their own heaps, so booting them does not affect
/// <see cref="GC.CollectionCount"/> here — default is no panes for the cleanest
/// signal. The full workspace-idle GC budget needs in-App.Wpf sampling; this
/// probe is the daemon-side floor (noted in baseline.json).
/// </summary>
internal static class GcIdleCommand
{
    public static int Run(string[] args) => RunAsync(args).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(string[] args)
    {
        var seconds = 60;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seconds" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out seconds) || seconds < 5 || seconds > 600)
                    {
                        Console.Error.WriteLine($"gc-idle: invalid --seconds '{args[i]}' (5..600)");
                        return 64;
                    }
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine($"gc-idle: unknown arg '{args[i]}'");
                    PrintUsage();
                    return 64;
            }
        }

        // Drain pending finalizers so the start counter isn't already mid-collection.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var startGen2 = GC.CollectionCount(2);
        var startGen1 = GC.CollectionCount(1);
        var startGen0 = GC.CollectionCount(0);

        await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);

        var endGen2 = GC.CollectionCount(2);
        var endGen1 = GC.CollectionCount(1);
        var endGen0 = GC.CollectionCount(0);

        var gen2Delta = endGen2 - startGen2;
        var perMinute = gen2Delta * 60.0 / seconds;
        var pass      = perMinute <= 1.0; // ADR-008 #6 ceiling

        var payload = new Dictionary<string, object?>
        {
            ["metric"]               = "gcGen2PerMinuteIdle",
            ["adr008Item"]           = 6,
            ["seconds"]              = seconds,
            ["gen2Delta"]            = gen2Delta,
            ["gen1Delta"]            = endGen1 - startGen1,
            ["gen0Delta"]            = endGen0 - startGen0,
            ["gen2PerMinute"]        = perMinute,
            ["thresholdPerMinute"]   = 1.0,
            ["pass"]                 = pass,
        };
        Console.WriteLine(JsonSerializer.Serialize(payload));
        return pass ? 0 : 1;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            awt-perfprobe gc-idle — ADR-008 #6 GC Gen2/min during idle.

            Usage:
              awt-perfprobe gc-idle [--seconds 60]

            Sleeps for the requested window, reports the probe's own Gen2
            collection delta normalised to per-minute. Panes don't share the
            probe's GC heap so this measures daemon-side floor only.

            Output (single-line JSON):
              {"metric":"gcGen2PerMinuteIdle","seconds":60,
               "gen2Delta":N, "gen2PerMinute":..,
               "thresholdPerMinute":1.0, "pass":true|false}

            Exit 0 = within threshold, 1 = exceeded.
            """);
    }
}
