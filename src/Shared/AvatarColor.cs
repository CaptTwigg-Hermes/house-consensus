namespace HouseConsensus.Shared;

public static class AvatarColor
{
    private static readonly string[] Palette =
    [
        "#7c2d12", "#9f1239", "#86198f", "#6d28d9",
        "#3730a3", "#1e40af", "#075985", "#0f766e",
        "#166534", "#3f6212", "#854d0e", "#9a3412"
    ];

    public static IReadOnlyList<string> Options { get; } = Array.AsReadOnly(Palette);

    public static bool IsValid(string? color) => Palette.Contains(color, StringComparer.OrdinalIgnoreCase);

    public static string Resolve(string? color, Guid memberId)
        => !string.IsNullOrWhiteSpace(color) && IsValid(color) ? color.ToLowerInvariant() : Css(memberId);

    public static string Css(Guid memberId)
    {
        var hash = 2166136261u;
        foreach (var value in memberId.ToByteArray())
        {
            hash ^= value;
            hash *= 16777619u;
        }

        return Palette[hash % Palette.Length];
    }
}
