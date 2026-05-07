using System.Text.Json;
using AgentWorkspace.App.Wpf;

namespace AgentWorkspace.Tests.App;

public sealed class EchoSamplesDecoderTests
{
    [Fact]
    public void TryDecodeSamples_ReturnsFalse_WhenSamplesPropertyMissing()
    {
        var root = JsonDocument.Parse("""{}""").RootElement;
        Assert.False(EchoSamplesDecoder.TryDecodeSamples(root, out _));
    }

    [Fact]
    public void TryDecodeSamples_ReturnsFalse_WhenSamplesIsNotArray()
    {
        var root = JsonDocument.Parse("""{"samples":42}""").RootElement;
        Assert.False(EchoSamplesDecoder.TryDecodeSamples(root, out _));
    }

    [Fact]
    public void TryDecodeSamples_ReturnsTrue_WithEmptyArray()
    {
        var root = JsonDocument.Parse("""{"samples":[]}""").RootElement;
        Assert.True(EchoSamplesDecoder.TryDecodeSamples(root, out var samples));
        Assert.Empty(samples);
    }

    [Theory]
    [InlineData("""{"samples":["bad",10.0]}""")]
    [InlineData("""{"samples":[null,10.0]}""")]
    [InlineData("""{"samples":[true,10.0]}""")]
    [InlineData("""{"samples":[5.0,"bad"]}""")]
    public void TryDecodeSamples_ReturnsFalse_WhenAnyElementIsNonNumber(string json)
    {
        var root = JsonDocument.Parse(json).RootElement;
        Assert.False(EchoSamplesDecoder.TryDecodeSamples(root, out _));
    }

    [Fact]
    public void TryDecodeSamples_DecodesValidDoublesArray()
    {
        var root = JsonDocument.Parse("""{"samples":[1.5,2.0,3.75]}""").RootElement;
        Assert.True(EchoSamplesDecoder.TryDecodeSamples(root, out var samples));
        Assert.Equal([1.5, 2.0, 3.75], samples);
    }

    [Fact]
    public void TryDecodeSamples_DecodesIntegerElementsAsDouble()
    {
        var root = JsonDocument.Parse("""{"samples":[10,20,30]}""").RootElement;
        Assert.True(EchoSamplesDecoder.TryDecodeSamples(root, out var samples));
        Assert.Equal([10.0, 20.0, 30.0], samples);
    }
}
