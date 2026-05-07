using System.Text.Json;
using AgentWorkspace.App.Wpf;

namespace AgentWorkspace.Tests.App;

public sealed class RendererResizeDecoderTests
{
    [Fact]
    public void TryDecodeResize_ReturnsFalse_WhenColsPropertyMissing()
    {
        var root = JsonDocument.Parse("""{"rows":30}""").RootElement;

        bool decoded = RendererResizeDecoder.TryDecodeResize(root, out var cols, out var rows);

        Assert.False(decoded);
        Assert.Equal(0, cols);
        Assert.Equal(0, rows);
    }

    [Fact]
    public void TryDecodeResize_ReturnsFalse_WhenRowsPropertyMissing()
    {
        var root = JsonDocument.Parse("""{"cols":120}""").RootElement;

        bool decoded = RendererResizeDecoder.TryDecodeResize(root, out var cols, out var rows);

        Assert.False(decoded);
        Assert.Equal(0, cols);
        Assert.Equal(0, rows);
    }

    [Theory]
    [InlineData("""{"cols":"auto","rows":30}""")]
    [InlineData("""{"cols":120,"rows":"auto"}""")]
    [InlineData("""{"cols":120.5,"rows":30}""")]
    [InlineData("""{"cols":120,"rows":30.5}""")]
    public void TryDecodeResize_ReturnsFalse_WhenValueIsNonInteger(string json)
    {
        var root = JsonDocument.Parse(json).RootElement;

        bool decoded = RendererResizeDecoder.TryDecodeResize(root, out var cols, out var rows);

        Assert.False(decoded);
        Assert.Equal(0, cols);
        Assert.Equal(0, rows);
    }

    [Theory]
    [InlineData("""{"cols":0,"rows":30}""")]
    [InlineData("""{"cols":-1,"rows":30}""")]
    [InlineData("""{"cols":120,"rows":0}""")]
    [InlineData("""{"cols":120,"rows":-5}""")]
    public void TryDecodeResize_ReturnsFalse_WhenValueIsZeroOrNegative(string json)
    {
        var root = JsonDocument.Parse(json).RootElement;

        bool decoded = RendererResizeDecoder.TryDecodeResize(root, out var cols, out var rows);

        Assert.False(decoded);
        Assert.Equal(0, cols);
        Assert.Equal(0, rows);
    }

    [Fact]
    public void TryDecodeResize_DecodesValidDimensions()
    {
        var root = JsonDocument.Parse("""{"cols":120,"rows":30}""").RootElement;

        bool decoded = RendererResizeDecoder.TryDecodeResize(root, out var cols, out var rows);

        Assert.True(decoded);
        Assert.Equal(120, cols);
        Assert.Equal(30, rows);
    }

    [Fact]
    public void TryDecodeResize_ClampsOversizedCols_ToShortMaxValue()
    {
        var root = JsonDocument.Parse("""{"cols":100000,"rows":30}""").RootElement;

        bool decoded = RendererResizeDecoder.TryDecodeResize(root, out var cols, out var rows);

        Assert.True(decoded);
        Assert.Equal(short.MaxValue, cols);
        Assert.Equal(30, rows);
    }
}
