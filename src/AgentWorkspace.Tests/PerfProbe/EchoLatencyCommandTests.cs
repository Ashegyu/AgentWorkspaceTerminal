using AgentWorkspace.PerfProbe;

namespace AgentWorkspace.Tests.PerfProbe;

/// <summary>
/// Day 54 — verifies <see cref="EchoLatencyCommand.ParseSamples"/> handles both supported
/// input formats and graceful fallback for blank / commented / malformed lines.
/// </summary>
public sealed class EchoLatencyCommandTests
{
    [Fact]
    public void ParseSamples_NewlineSeparated_ReturnsAllValues()
    {
        var samples = EchoLatencyCommand.ParseSamples("12.4\n11.0\n13.3");
        Assert.Equal(3, samples.Count);
        Assert.Equal(12.4, samples[0]);
        Assert.Equal(11.0, samples[1]);
        Assert.Equal(13.3, samples[2]);
    }

    [Fact]
    public void ParseSamples_JsonArray_ReturnsAllValues()
    {
        var samples = EchoLatencyCommand.ParseSamples("[1.5, 2.5, 3.5]");
        Assert.Equal([1.5, 2.5, 3.5], samples);
    }

    [Fact]
    public void ParseSamples_SkipsCommentLinesAndBlanks()
    {
        var raw = """
            # collected via xterm.js → host bridge 2026-04-30
            12.4

            11.0
            # outlier ignored manually
            13.3
            """;
        var samples = EchoLatencyCommand.ParseSamples(raw);
        Assert.Equal([12.4, 11.0, 13.3], samples);
    }

    [Fact]
    public void ParseSamples_EmptyInput_ReturnsEmptyList()
        => Assert.Empty(EchoLatencyCommand.ParseSamples(""));

    [Fact]
    public void ParseSamples_NonNumericLines_AreSkipped()
    {
        var samples = EchoLatencyCommand.ParseSamples("12.4\nNaN-junk\n13.3");
        Assert.Equal([12.4, 13.3], samples);
    }
}
