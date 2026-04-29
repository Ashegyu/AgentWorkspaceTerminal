using System;
using System.IO;
using System.Text;
using System.Text.Json;
using AgentWorkspace.Abstractions.Ids;
using AgentWorkspace.Abstractions.Layout;

namespace AgentWorkspace.Client.Sessions;

/// <summary>
/// Layout (de)serialiser used on the client side of <see cref="RemoteSessionStore"/>. Matches the
/// format of <c>AgentWorkspace.Core.Sessions.LayoutJson</c> so the daemon's SQLite payload can be
/// round-tripped through the wire as a string. Day 18 will replace both copies with proto.
/// </summary>
internal static class LayoutJsonClient
{
    private static readonly JsonWriterOptions WriterOpts = new()
    {
        Indented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(LayoutNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOpts))
        {
            WriteNode(w, root);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static LayoutNode Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
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

        return kind switch
        {
            "pane" => new PaneNode(
                id,
                PaneId.Parse(el.GetProperty("paneId").GetString()
                    ?? throw new InvalidDataException("pane node missing 'paneId'."))),
            "split" => new SplitNode(
                id,
                el.GetProperty("direction").GetString() == "horizontal"
                    ? SplitDirection.Horizontal
                    : SplitDirection.Vertical,
                el.GetProperty("ratio").GetDouble(),
                ReadNode(el.GetProperty("a")),
                ReadNode(el.GetProperty("b"))),
            _ => throw new InvalidDataException($"Unknown layout node kind '{kind}'."),
        };
    }
}
