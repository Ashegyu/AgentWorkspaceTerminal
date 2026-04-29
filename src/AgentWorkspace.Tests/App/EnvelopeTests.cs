using System;
using System.Text;
using System.Text.Json;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.App.Wpf;

namespace AgentWorkspace.Tests.App;

/// <summary>
/// Round-trip tests for the JSON envelopes emitted by <see cref="Envelope"/>.
/// Three properties checked simultaneously: well-formed JSON, base64 byte preservation
/// across CJK / emoji payloads, and a string safe for PostWebMessageAsString.
/// </summary>
public sealed class EnvelopeTests
{
    [Fact]
    public void Init_HasTypeAndPaneId()
    {
        var id = PaneId.New();
        string json = Envelope.Init(id);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("init", root.GetProperty("type").GetString());
        Assert.Equal(id.ToString(), root.GetProperty("paneId").GetString());
    }

    [Fact]
    public void Exit_CarriesNumericExitCode()
    {
        var id = PaneId.New();
        string json = Envelope.Exit(id, 42);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("exit", root.GetProperty("type").GetString());
        Assert.Equal(42, root.GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x01, 0xff, 0x80, 0x7f })]   // arbitrary binary
    [InlineData(new byte[] { 0xe2, 0x9c, 0xa8 })]                // U+2728
    [InlineData(new byte[] { 0xf0, 0x9f, 0x8e, 0x89 })]          // U+1F389 (4-byte UTF-8 emoji)
    public void Output_BytesRoundTripExactlyViaBase64(byte[] payload)
    {
        var id = PaneId.New();
        string json = Envelope.Output(id, payload);

        using var doc = JsonDocument.Parse(json);
        string b64 = doc.RootElement.GetProperty("b64").GetString()!;
        byte[] decoded = Convert.FromBase64String(b64);

        Assert.Equal(payload, decoded);
    }

    [Theory]
    [InlineData("한글 출력")]
    [InlineData("中文输出")]
    [InlineData("emoji 🎉🚀")]
    public void Output_PreservesUtf8TextThroughEnvelope(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var id = PaneId.New();
        string json = Envelope.Output(id, bytes);

        using var doc = JsonDocument.Parse(json);
        byte[] decoded = Convert.FromBase64String(doc.RootElement.GetProperty("b64").GetString()!);

        Assert.Equal(bytes, decoded);
        Assert.Equal(text, Encoding.UTF8.GetString(decoded));
    }

    [Fact]
    public void Output_LargePayloadDoesNotInflateBeyondBase64Floor()
    {
        var rng = new Random(42);
        byte[] payload = new byte[16 * 1024];
        rng.NextBytes(payload);

        var id = PaneId.New();
        string json = Envelope.Output(id, payload);

        // base64 is at most ceil(n/3)*4. Anything wildly larger would mean we accidentally
        // re-encoded the payload as JSON-escaped string content rather than base64.
        int expectedB64 = (int)Math.Ceiling(payload.Length / 3.0) * 4;
        int envelopeFloor = expectedB64;
        int envelopeCeiling = expectedB64 + 256;

        Assert.InRange(json.Length, envelopeFloor, envelopeCeiling);
    }
}
