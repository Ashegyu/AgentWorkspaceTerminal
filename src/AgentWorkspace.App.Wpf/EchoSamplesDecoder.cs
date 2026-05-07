using System.Text.Json;

namespace AgentWorkspace.App.Wpf;

internal static class EchoSamplesDecoder
{
    public static bool TryDecodeSamples(JsonElement root, out double[] samples)
    {
        samples = [];

        if (!root.TryGetProperty("samples", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return false;

        var result = new double[arr.GetArrayLength()];
        for (int i = 0; i < result.Length; i++)
        {
            var el = arr[i];
            if (el.ValueKind != JsonValueKind.Number)
                return false;

            result[i] = el.GetDouble();
        }

        samples = result;
        return true;
    }
}
