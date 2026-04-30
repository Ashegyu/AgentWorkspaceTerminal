using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AgentWorkspace.App.Wpf;

/// <summary>
/// ADR-008 #1 — bridge between the renderer's buffered echo samples (ms doubles)
/// and <c>awt-perfprobe echo-latency</c>. Host receives a JSON array of round-trip
/// samples from the renderer, writes them to a temp file (one number per line),
/// invokes the probe with <c>--input</c>, and returns the parsed JSON result so
/// the UI can show a one-line summary.
/// </summary>
internal static class EchoLatencyDump
{
    /// <summary>
    /// Runs the probe against the supplied samples. Returns a one-line
    /// summary suitable for the status bar (e.g. "p95=42.3ms PASS, n=87").
    /// Throws if no probe binary is found or the probe exits with usage error.
    /// </summary>
    public static async Task<string> RunProbeAsync(double[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length == 0) return "echo-latency: no samples buffered yet — type some keys in a pane first.";

        var probePath = LocateProbe()
            ?? throw new FileNotFoundException(
                "awt-perfprobe.exe not found beside App.Wpf or on PATH. Build src/AgentWorkspace.PerfProbe (Release) first.");

        var tempFile = Path.Combine(Path.GetTempPath(),
            $"awt-echo-samples-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}.txt");
        try
        {
            // One sample per line — matches the probe's stdin/--input format.
            await File.WriteAllLinesAsync(tempFile,
                samples.Select(s => s.ToString("0.###", CultureInfo.InvariantCulture)))
                .ConfigureAwait(false);

            var psi = new ProcessStartInfo
            {
                FileName               = probePath,
                Arguments              = $"echo-latency --input \"{tempFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start probe at {probePath}.");

            string stdout = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            string stderr = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);

            if (proc.ExitCode == 64) throw new InvalidOperationException($"echo-latency usage error: {stderr.Trim()}");

            return SummariseProbeOutput(stdout, proc.ExitCode, samples.Length);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best-effort cleanup */ }
        }
    }

    private static string LocateProbe()
    {
        const string exeName = "awt-perfprobe.exe";

        var beside = Path.Combine(
            Path.GetDirectoryName(typeof(EchoLatencyDump).Assembly.Location)
                ?? AppContext.BaseDirectory,
            exeName);
        if (File.Exists(beside)) return beside;

        // Repo-relative fallback for `dotnet run` style local dev.
        var repoCandidate = ResolveRepoRelative(
            "src/AgentWorkspace.PerfProbe/bin/Release/net10.0-windows/" + exeName);
        if (repoCandidate is not null && File.Exists(repoCandidate)) return repoCandidate;

        // PATH lookup — last resort.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate)) return candidate;
        }

        return null!;
    }

    private static string? ResolveRepoRelative(string relativeUnixStyle)
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "src")))
            {
                return Path.Combine(dir, relativeUnixStyle.Replace('/', Path.DirectorySeparatorChar));
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    internal static string SummariseProbeOutput(string stdout, int exitCode, int sampleCount)
    {
        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Reverse()
            .FirstOrDefault(l => l.TrimStart().StartsWith('{'));
        if (line is null)
            return $"echo-latency: probe exit={exitCode} but no JSON output (n={sampleCount}). Stdout: {stdout.Trim()}";

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            // EchoLatencyCommand emits keys without unit suffix: p50, p95, max.
            var p95   = root.TryGetProperty("p95", out var p) ? p.GetDouble() : double.NaN;
            var p50   = root.TryGetProperty("p50", out var q) ? q.GetDouble() : double.NaN;
            var maxMs = root.TryGetProperty("max", out var m) ? m.GetDouble() : double.NaN;
            var pass  = root.TryGetProperty("pass",        out var ps) && ps.GetBoolean();
            var verdict = pass ? "PASS" : "FAIL";

            var sb = new StringBuilder();
            sb.Append("echo-latency: ").Append(verdict)
              .Append("  p95=").Append(p95.ToString("0.0",  CultureInfo.InvariantCulture)).Append("ms")
              .Append(", p50=").Append(p50.ToString("0.0",  CultureInfo.InvariantCulture)).Append("ms")
              .Append(", max=").Append(maxMs.ToString("0.0", CultureInfo.InvariantCulture)).Append("ms")
              .Append(", n=").Append(sampleCount);
            return sb.ToString();
        }
        catch (JsonException)
        {
            return $"echo-latency: probe exit={exitCode}, raw={line.Trim()}";
        }
    }
}
