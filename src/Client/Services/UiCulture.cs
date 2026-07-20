using System.Globalization;

namespace HouseConsensus.Client.Services;

public static class UiCulture
{
    public static string Normalize(string? requested) => requested?.StartsWith("da", StringComparison.OrdinalIgnoreCase) == true ? "da" : "en";
    public static void Apply(string language)
    {
        var culture = new CultureInfo(Normalize(language));
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}

public static class BrowseQuery
{
    public static string Build(string? city, decimal? minPrice, decimal? maxPrice)
    {
        var values = new List<string>();
        if (!string.IsNullOrWhiteSpace(city)) values.Add($"city={Uri.EscapeDataString(city.Trim())}");
        if (minPrice.HasValue) values.Add($"minPrice={minPrice.Value.ToString(CultureInfo.InvariantCulture)}");
        if (maxPrice.HasValue) values.Add($"maxPrice={maxPrice.Value.ToString(CultureInfo.InvariantCulture)}");
        return "api/listings/browse" + (values.Count == 0 ? "" : "?" + string.Join('&', values));
    }
}
