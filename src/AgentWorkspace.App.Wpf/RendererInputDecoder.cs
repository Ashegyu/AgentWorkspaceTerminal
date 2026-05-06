namespace AgentWorkspace.App.Wpf;

internal static class RendererInputDecoder
{
    public static bool TryDecodeBase64(string? payload, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(payload))
            return false;

        try
        {
            bytes = Convert.FromBase64String(payload);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
