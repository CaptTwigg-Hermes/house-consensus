namespace HouseConsensus.Shared;

public static class AvatarInitials
{
    public static string From(string? displayName, string email)
    {
        var value = string.IsNullOrWhiteSpace(displayName) ? email : displayName;
        return string.Concat(value.Split([' ', '@'], StringSplitOptions.RemoveEmptyEntries)
            .Take(2).Select(part => char.ToUpperInvariant(part[0])));
    }
}
