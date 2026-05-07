using System.Text.Json;

namespace AgentWorkspace.App.Wpf;

internal static class RendererResizeDecoder
{
    public static bool TryDecodeResize(JsonElement root, out short cols, out short rows)
    {
        cols = 0;
        rows = 0;

        if (!root.TryGetProperty("cols", out var c) || !root.TryGetProperty("rows", out var r))
            return false;

        if (c.ValueKind != JsonValueKind.Number || r.ValueKind != JsonValueKind.Number)
            return false;

        if (!c.TryGetInt32(out int rawCols) || !r.TryGetInt32(out int rawRows))
            return false;

        if (rawCols <= 0 || rawRows <= 0)
            return false;

        cols = (short)Math.Clamp(rawCols, 1, short.MaxValue);
        rows = (short)Math.Clamp(rawRows, 1, short.MaxValue);
        return true;
    }
}
