using System;
using System.IO;
using System.Text;
using System.Text.Json;
using AgentWorkspace.Abstractions.Ids;

namespace AgentWorkspace.App.Wpf;

/// <summary>
/// JSON envelope construction for messages posted to the WebView2 renderer.
/// </summary>
/// <remarks>
/// All envelopes share a top-level <c>type</c> discriminator and a <c>paneId</c> string, except
/// for chrome-only events (status text). The output envelope encodes raw PTY bytes as base64
/// because <c>WebView2.PostWebMessageAsString</c> only accepts strings.
/// </remarks>
internal static class Envelope
{
    private static readonly JsonWriterOptions WriterOpts = new()
    {
        Indented = false,
        // The renderer parses the string with JSON.parse — escaping of non-ASCII is unnecessary
        // and only inflates the payload, so we let UnsafeRelaxedJsonEscaping pass through.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Init(PaneId id) => Simple("init", id);

    public static string Status(string text)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOpts))
        {
            w.WriteStartObject();
            w.WriteString("type", "status");
            w.WriteString("text", text);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string Clear(PaneId id) => Simple("clear", id);

    public static string FocusTerm()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOpts))
        {
            w.WriteStartObject();
            w.WriteString("type", "focusTerm");
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string FontSizeDelta(int delta)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOpts))
        {
            w.WriteStartObject();
            w.WriteString("type", "fontSize");
            w.WriteNumber("delta", delta);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string Output(PaneId id, ReadOnlySpan<byte> data)
    {
        using var ms = new MemoryStream(data.Length + 64);
        using (var w = new Utf8JsonWriter(ms, WriterOpts))
        {
            w.WriteStartObject();
            w.WriteString("type", "output");
            w.WriteString("paneId", id.ToString());
            w.WriteString("b64", Convert.ToBase64String(data));
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string Exit(PaneId id, int code)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOpts))
        {
            w.WriteStartObject();
            w.WriteString("type", "exit");
            w.WriteString("paneId", id.ToString());
            w.WriteNumber("code", code);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string Simple(string type, PaneId id)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOpts))
        {
            w.WriteStartObject();
            w.WriteString("type", type);
            w.WriteString("paneId", id.ToString());
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
