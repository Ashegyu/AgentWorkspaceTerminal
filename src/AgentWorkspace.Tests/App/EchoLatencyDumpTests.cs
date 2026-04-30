using AgentWorkspace.App.Wpf;

namespace AgentWorkspace.Tests.App;

/// <summary>
/// ADR-008 #1 — verifies the host-side probe-output parser produces the right
/// status-bar summary string for the JSON shape that
/// <see cref="AgentWorkspace.PerfProbe.EchoLatencyCommand"/> emits.
/// </summary>
public sealed class EchoLatencyDumpTests
{
    [Fact]
    public void SummariseProbeOutput_PassPayload_FormatsAllPercentiles()
    {
        // Real probe emits one JSON object per line — keep the test fixture single-line.
        const string json = "{\"metric\":\"echoLatencyP95Ms\",\"adr008Item\":1,\"count\":5,\"min\":11.0,\"p50\":12.4,\"p95\":13.2,\"p99\":13.3,\"max\":45.2,\"thresholdMs\":50,\"pass\":true}";
        var summary = EchoLatencyDump.SummariseProbeOutput(json, exitCode: 0, sampleCount: 5);

        Assert.Contains("PASS", summary);
        Assert.Contains("p95=13.2ms", summary);
        Assert.Contains("p50=12.4ms", summary);
        Assert.Contains("max=45.2ms", summary);
        Assert.Contains("n=5", summary);
    }

    [Fact]
    public void SummariseProbeOutput_FailPayload_FormatsFailVerdict()
    {
        const string json = "{\"count\":12,\"p50\":40.1,\"p95\":62.7,\"p99\":71.5,\"max\":80.0,\"pass\":false}";
        var summary = EchoLatencyDump.SummariseProbeOutput(json, exitCode: 1, sampleCount: 12);

        Assert.Contains("FAIL", summary);
        Assert.Contains("p95=62.7ms", summary);
        Assert.Contains("n=12", summary);
    }

    [Fact]
    public void SummariseProbeOutput_NonJsonStdout_FallsBackGracefully()
    {
        var summary = EchoLatencyDump.SummariseProbeOutput("usage error somewhere\n",
            exitCode: 64, sampleCount: 0);

        Assert.Contains("exit=64", summary);
        Assert.Contains("no JSON output", summary);
    }

    [Fact]
    public void SummariseProbeOutput_LeadingNoise_ParsesLastJsonLine()
    {
        // The probe writes a single line, but the host reads stdout as a stream — make sure
        // an extra noise line before the JSON line is tolerated.
        const string stdout = "ignore this leading line\n{\"count\":3,\"p50\":10.0,\"p95\":11.0,\"p99\":11.0,\"max\":11.0,\"pass\":true}\n";
        var summary = EchoLatencyDump.SummariseProbeOutput(stdout, exitCode: 0, sampleCount: 3);

        Assert.Contains("PASS", summary);
        Assert.Contains("p95=11.0ms", summary);
    }
}
