using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using AgentWorkspace.Abstractions.Agents;

namespace AgentWorkspace.Agents.Claude.Wire;

/// <summary>Parses a single stream-json line into an <see cref="AgentEvent"/>.</summary>
internal static class StreamJsonParser
{
    internal static AgentEvent? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

            return type switch
            {
                "assistant" => ParseAssistant(root),
                "result"    => ParseResult(root),
                _           => null,
            };
        }
        catch
        {
            return null;
        }
    }

    private static AgentEvent? ParseAssistant(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var msg)) return null;
        if (!msg.TryGetProperty("content", out var content)) return null;

        var text = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            var ct = item.TryGetProperty("type", out var ctp) ? ctp.GetString() : null;
            if (ct == "text" && item.TryGetProperty("text", out var tp))
            {
                text.Append(tp.GetString());
            }
            else if (ct == "tool_use")
            {
                var id   = item.TryGetProperty("id",   out var ip) ? ip.GetString()   ?? "" : "";
                var name = item.TryGetProperty("name", out var np) ? np.GetString()   ?? "" : "";
                return new ActionRequestEvent(id, name, name);
            }
        }

        var str = text.ToString();
        return str.Length > 0 ? new AgentMessageEvent("assistant", str) : null;
    }

    private static AgentEvent ParseResult(JsonElement root)
    {
        var isError = root.TryGetProperty("is_error", out var ie) && ie.GetBoolean();
        if (isError)
        {
            var err = root.TryGetProperty("error", out var ep) ? ep.GetString() ?? "Unknown error" : "Unknown error";
            return new AgentErrorEvent(err);
        }

        var summary = root.TryGetProperty("result", out var rp) ? rp.GetString() : null;
        return new AgentDoneEvent(0, summary);
    }
}
