using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using HouseConsensus.Shared;

namespace HouseConsensus.Server.Listings;

public sealed partial class BoligsidenListingLookup
{
    private static readonly Uri DefaultDawaEndpoint = new("https://api.dataforsyningen.dk/");
    private static readonly Uri DefaultBoligsidenEndpoint = new("https://api.boligsiden.dk/");
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private const int MaximumIdentifierLength = 128;
    private static readonly SemaphoreSlim OutboundConcurrency = new(4, 4);
    private readonly HttpClient _http;
    private readonly Uri _dawaEndpoint;
    private readonly Uri _boligsidenEndpoint;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public BoligsidenListingLookup(HttpClient http) : this(http, DefaultDawaEndpoint, DefaultBoligsidenEndpoint) { }

    public BoligsidenListingLookup(HttpClient http, Uri dawaEndpoint, Uri boligsidenEndpoint)
    {
        _http = http;
        _dawaEndpoint = EnsureBase(dawaEndpoint);
        _boligsidenEndpoint = EnsureBase(boligsidenEndpoint);
    }

    public async Task<ManualListingPreview?> ResolveAsync(string? sourceUrl, CancellationToken ct)
    {
        var slug = AddressSlug(sourceUrl);
        if (slug is null) return null;
        await OutboundConcurrency.WaitAsync(ct);
        try
        {
        var addressUrl = new Uri(_dawaEndpoint, $"adresser?q={Uri.EscapeDataString(slug.Replace('-', ' '))}&struktur=mini&per_side=5");
        using var dawa = await GetJsonAsync(addressUrl, ct);
        if (dawa is null || dawa.RootElement.ValueKind != JsonValueKind.Array) return null;

        foreach (var candidate in dawa.RootElement.EnumerateArray())
        {
            if (!TryIdentifier(candidate, "id", out var addressId)) continue;
            using var address = await GetJsonAsync(new Uri(_boligsidenEndpoint, $"addresses/{Uri.EscapeDataString(addressId)}"), ct);
            if (address is null || !TryString(address.RootElement, "slugAddress", out var actualSlug) ||
                !string.Equals(actualSlug, slug, StringComparison.OrdinalIgnoreCase)) continue;

            var caseId = OpenCaseId(address.RootElement);
            if (caseId is null) continue;
            using var detail = await GetJsonAsync(new Uri(_boligsidenEndpoint, $"cases/{Uri.EscapeDataString(caseId)}"), ct);
            if (detail is null || !string.Equals(String(detail.RootElement, "status"), "open", StringComparison.OrdinalIgnoreCase)) continue;
            return Map(detail.RootElement, caseId);
        }
        return null;
        }
        finally { OutboundConcurrency.Release(); }
    }

    private async Task<JsonDocument?> GetJsonAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("HouseConsensus/1.0");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumResponseBytes) return null;
            await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, ct);
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    private static ManualListingPreview? Map(JsonElement detail, string caseId)
    {
        if (detail.ValueKind != JsonValueKind.Object || !detail.TryGetProperty("address", out var address) || address.ValueKind != JsonValueKind.Object) return null;
        var road = String(address, "roadName");
        var house = String(address, "houseNumber");
        if (string.IsNullOrWhiteSpace(road) || string.IsNullOrWhiteSpace(house)) return null;
        var latitude = BoundedDouble(detail, "coordinates", "lat", -90, 90);
        var longitude = BoundedDouble(detail, "coordinates", "lon", -180, 180);
        if (latitude is null || longitude is null) { latitude = null; longitude = null; }
        return new ManualListingPreview(
            $"{road} {house}", String(address, "cityName"), BoundedInt(address, "zipCode", 1000, 9999)?.ToString(),
            BoundedDecimal(detail, "priceCash", 1, ManualListing.MaxAskingPrice), BoundedInt(detail, "housingArea", 1, 100_000), BoundedInt(detail, "lotArea", 0, 100_000_000),
            BoundedInt(detail, "numberOfRooms", 1, 1_000), BoundedInt(detail, "numberOfFloors", 1, 100), BoundedInt(detail, "numberOfBathrooms", 0, 1_000),
            BoundedInt(detail, "yearBuilt", 1000, DateTime.UtcNow.Year + 2), EnergyLabel(detail), BoundedInt(detail, "monthlyExpense", 0, int.MaxValue),
            BoundedInt(detail, "daysOnMarket", 0, int.MaxValue), LargestAllowedImage(detail),
            latitude, longitude, caseId.Length <= 100 ? caseId : caseId[..100]);
    }

    private static string? AddressSlug(string? sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            (uri.Host != "boligsiden.dk" && uri.Host != "www.boligsiden.dk")) return null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 || !string.Equals(segments[0], "adresse", StringComparison.OrdinalIgnoreCase)) return null;
        var slug = Uri.UnescapeDataString(segments[1]).Trim().ToLowerInvariant();
        slug = AddressIdentifierSuffix().Replace(slug, "");
        return slug.Length is > 4 and <= 300 ? slug : null;
    }

    private static string? OpenCaseId(JsonElement address)
    {
        if (address.ValueKind != JsonValueKind.Object || !address.TryGetProperty("cases", out var cases) || cases.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in cases.EnumerateArray())
            if (string.Equals(String(item, "status"), "open", StringComparison.OrdinalIgnoreCase) && TryIdentifier(item, "caseID", out var id)) return id;
        return null;
    }

    private static string? LargestAllowedImage(JsonElement detail)
    {
        if (detail.ValueKind != JsonValueKind.Object || !detail.TryGetProperty("defaultImage", out var image) || image.ValueKind != JsonValueKind.Object ||
            !image.TryGetProperty("imageSources", out var sources) || sources.ValueKind != JsonValueKind.Array) return null;
        return sources.EnumerateArray()
            .Select(x => new { Url = String(x, "url"), Width = x.ValueKind == JsonValueKind.Object && x.TryGetProperty("size", out var size) ? Int(size, "width") ?? 0 : 0 })
            .Where(x => ListingImageSources.IsAllowedSource(x.Url))
            .OrderByDescending(x => x.Width).Select(x => x.Url).FirstOrDefault();
    }

    private static Uri EnsureBase(Uri uri) => new(uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + "/");
    private static string? String(JsonElement element, string name) => TryString(element, name, out var value) ? value : null;
    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = "";
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value = property.GetString()!);
    }
    private static bool TryIdentifier(JsonElement element, string name, out string value) =>
        TryString(element, name, out value) && value.Length <= MaximumIdentifierLength;
    private static int? Int(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var value) ? value : null;
    private static decimal? Decimal(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDecimal(out var value) ? value : null;
    private static double? NestedDouble(JsonElement element, string parent, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(parent, out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var value) ? value : null;
    private static int? BoundedInt(JsonElement element, string name, int min, int max) => Int(element, name) is { } value && value >= min && value <= max ? value : null;
    private static decimal? BoundedDecimal(JsonElement element, string name, decimal min, decimal max) => Decimal(element, name) is { } value && value >= min && value <= max ? value : null;
    private static double? BoundedDouble(JsonElement element, string parent, string name, double min, double max) => NestedDouble(element, parent, name) is { } value && double.IsFinite(value) && value >= min && value <= max ? value : null;
    private static string? EnergyLabel(JsonElement detail)
    {
        var value = String(detail, "energyLabel")?.Trim().ToUpperInvariant();
        return value is { Length: > 0 and <= 10 } && EnergyLabelPattern().IsMatch(value) ? value : null;
    }

    [GeneratedRegex(@"-\d{8}.*$")]
    private static partial Regex AddressIdentifierSuffix();
    [GeneratedRegex(@"^[A-G](?:20\d{2})?$")]
    private static partial Regex EnergyLabelPattern();
}
