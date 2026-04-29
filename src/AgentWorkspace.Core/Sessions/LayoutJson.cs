using System;
using System.IO;
using System.Text;
using System.Text.Json;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;

namespace AgentWorkspace.Core.Sessions;

/// <summary>
/// JSON ⇆ <see cref="LayoutNode"/> tree (de)serialisation. Lives next to the SQLite store
/// because the serialised shape is part of the persisted schema; the renderer's wire format
/// in <c>Envelope.Layout</c> is intentionally identical so that on-disk JSON can be replayed
/// to the renderer verbatim if we ever want to.
/// </summary>
public static class LayoutJson
{
    private static readonly JsonWriterOptions WriterOpts = new()
    {
        Indented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(LayoutNode root)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOpts))
        {
            WriteNode(w, root);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static LayoutNode Deserialize(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ReadNode(doc.RootElement);
    }

    private static void WriteNode(Utf8JsonWriter w, LayoutNode node)
    {
        switch (node)
        {
            case PaneNode p:
                w.WriteStartObject();
                w.WriteString("kind", "pane");
                w.WriteString("id", p.Id.ToString());
                w.WriteString("paneId", p.Pane.ToString());
                w.WriteEndObject();
                break;
            case SplitNode s:
                w.WriteStartObject();
                w.WriteString("kind", "split");
                w.WriteString("id", s.Id.ToString());
                w.WriteString("direction", s.Direction == SplitDirection.Horizontal ? "horizontal" : "vertical");
                w.WriteNumber("ratio", s.Ratio);
                w.WritePropertyName("a");
                WriteNode(w, s.A);
                w.WritePropertyName("b");
                WriteNode(w, s.B);
                w.WriteEndObject();
                break;
            default:
                throw new InvalidOperationException("Unrecognised layout node type.");
        }
    }

    private static LayoutNode ReadNode(JsonElement el)
    {
        string kind = el.GetProperty("kind").GetString()
            ?? throw new InvalidDataException("layout node missing 'kind'.");

        var id = LayoutId.Parse(el.GetProperty("id").GetString()
            ?? throw new InvalidDataException("layout node missing 'id'."));

        switch (kind)
        {
            case "pane":
                {
                    var paneId = PaneId.Parse(el.GetProperty("paneId").GetString()
                        ?? throw new InvalidDataException("pane node missing 'paneId'."));
                    return new PaneNode(id, paneId);
                }
            case "split":
                {
                    string dirStr = el.GetProperty("direction").GetString()
                        ?? throw new InvalidDataException("split node missing 'direction'.");
                    var direction = dirStr == "horizontal"
                        ? SplitDirection.Horizontal
                        : SplitDirection.Vertical;
                    double ratio = el.GetProperty("ratio").GetDouble();
                    var a = ReadNode(el.GetProperty("a"));
                    var b = ReadNode(el.GetProperty("b"));
                    return new SplitNode(id, direction, ratio, a, b);
                }
            default:
                throw new InvalidDataException($"Unknown layout node kind '{kind}'.");
        }
    }
}

