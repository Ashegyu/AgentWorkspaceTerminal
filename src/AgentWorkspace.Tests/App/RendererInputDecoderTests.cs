using System.Text;
using AgentWorkspace.App.Wpf;

namespace AgentWorkspace.Tests.App;

public sealed class RendererInputDecoderTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryDecodeBase64_RejectsEmptyInput(string? payload)
    {
        bool decoded = RendererInputDecoder.TryDecodeBase64(payload, out var bytes);

        Assert.False(decoded);
        Assert.Empty(bytes);
    }

    [Fact]
    public void TryDecodeBase64_RejectsMalformedInputWithoutThrowing()
    {
        bool decoded = RendererInputDecoder.TryDecodeBase64("not-base64", out var bytes);

        Assert.False(decoded);
        Assert.Empty(bytes);
    }

    [Fact]
    public void TryDecodeBase64_DecodesValidPayload()
    {
        byte[] expected = Encoding.UTF8.GetBytes("hello terminal");
        string payload = Convert.ToBase64String(expected);

        bool decoded = RendererInputDecoder.TryDecodeBase64(payload, out var bytes);

        Assert.True(decoded);
        Assert.Equal(expected, bytes);
    }
}
