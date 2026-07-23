using System.Text.Json;

namespace HouseConsensus.Client.Services;

public static class AiEvidenceText
{
    public static string SafeFallback(string raw, string unavailable)
    {
        var trimmed = raw.Trim();
        return LooksLikeJson(trimmed) ? unavailable : trimmed;
    }

    private static bool LooksLikeJson(string value)
    {
        if (value.StartsWith('{') || value.StartsWith('[') || value.StartsWith('"'))
            return true;
        try
        {
            using var _ = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
