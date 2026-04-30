using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace AgentWorkspace.PerfProbe;

/// <summary>
/// ADR-008 #1 — keystroke → screen echo p95 ≤ 50 ms.
/// Real round-trip data must come from a WebView2 hook (xterm.js posts
/// <c>performance.now()</c> diffs back to the host); that hook is not in scope today.
/// This command accepts pre-collected millisecond samples on stdin and emits a single
/// JSON line with percentile stats and a pass/fail verdict against the threshold.
///
/// Input formats (auto-detected):
///   • newline-separated decimals    e.g.  12.4\n11.0\n13.3
///   • single JSON array             e.g.  [12.4, 11.0, 13.3]
/// </summary>
internal static class EchoLatencyCommand
{
    public static int Run(string[] args)
    {
        var thresholdMs = 50.0; // ADR-008 #1 ceiling
        string? inputPath = null;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--threshold-ms" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out thresholdMs))
                    {
                        Console.Error.WriteLine($"echo-latency: invalid --threshold-ms '{args[i]}'");
                        return 64;
                    }
                    break;
                case "--input" when i + 1 < args.Length:
                    inputPath = args[++i];
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine($"echo-latency: unknown arg '{args[i]}'");
                    PrintUsage();
                    return 64;
            }
        }

        var raw = inputPath is null
            ? Console.In.ReadToEnd()
            : File.ReadAllText(inputPath);

        var samples = ParseSamples(raw);
        if (samples.Count == 0)
        {
            Console.Error.WriteLine("echo-latency: no samples on stdin/file. Provide newline-separated ms values or a JSON array.");
            return 65; // EX_DATAERR
        }

        var stats = PercentileStats.From(samples);
        var pass  = stats.P95 <= thresholdMs;

        var payload = new Dictionary<string, object?>
        {
            ["metric"]      = "echoLatencyP95Ms",
            ["adr008Item"]  = 1,
            ["count"]       = stats.Count,
            ["min"]         = stats.Min,
            ["p50"]         = stats.P50,
            ["p95"]         = stats.P95,
            ["p99"]         = stats.P99,
            ["max"]         = stats.Max,
            ["thresholdMs"] = thresholdMs,
            ["pass"]        = pass,
        };

        Console.WriteLine(JsonSerializer.Serialize(payload));
        return pass ? 0 : 1;
    }

    /// <summary>
    /// Accepts either a JSON array of numbers or newline-separated decimals. Lines
    /// starting with '#' are treated as comments. Empty lines / whitespace are skipped.
    /// </summary>
    internal static List<double> ParseSamples(string raw)
    {
        var trimmed = raw.AsSpan().Trim();
        var result  = new List<double>();
        if (trimmed.Length == 0) return result;

        if (trimmed[0] == '[')
        {
            using var doc = JsonDocument.Parse(trimmed.ToString());
            foreach (var el in doc.RootElement.EnumerateArray())
                if (el.ValueKind == JsonValueKind.Number)
                    result.Add(el.GetDouble());
            return result;
        }

        foreach (var line in raw.Split('\n'))
        {
            var s = line.Trim();
            if (s.Length == 0 || s[0] == '#') continue;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                result.Add(v);
        }
        return result;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("""
            awt-perfprobe echo-latency — ADR-008 #1 keystroke → screen echo p95.

            Usage:
              awt-perfprobe echo-latency [--threshold-ms 50] [--input <path>]

            Reads ms samples from stdin (or --input file). Accepts either:
              • newline-separated decimal values (lines starting with '#' are comments)
              • a single JSON array of numbers

            Emits a single-line JSON result on stdout:
              {"metric":"echoLatencyP95Ms","count":N,"p50":..,"p95":..,
               "thresholdMs":50,"pass":true|false,...}

            Exit codes:
              0 = p95 within threshold
              1 = p95 exceeds threshold
              64 = bad usage
              65 = no samples
            """);
    }
}
