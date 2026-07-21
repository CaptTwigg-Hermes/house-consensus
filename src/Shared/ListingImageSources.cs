namespace HouseConsensus.Shared;

public static class ListingImageSources
{
    public static bool IsAllowedSource(string? source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "images.boligsiden.dk", StringComparison.OrdinalIgnoreCase);
}
