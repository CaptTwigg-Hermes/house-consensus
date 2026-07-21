using HouseConsensus.Shared;
using Microsoft.Extensions.Caching.Memory;
using System.Net.Http.Headers;

namespace HouseConsensus.Server.Listings;

public sealed record ListingImage(byte[] Bytes, string ContentType);

public sealed class ListingImageService(HttpClient http, IMemoryCache cache)
{
    private const int MaximumImageBytes = 8 * 1024 * 1024;

    public async Task<ListingImage?> GetAsync(Guid listingId, string? source, CancellationToken ct)
    {
        if (!ListingImageSources.IsAllowedSource(source)) return null;
        if (cache.TryGetValue<ListingImage>($"listing-image:{listingId}", out var cached)) return cached;

        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/avif"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/webp"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not { } contentType || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
        if (response.Content.Headers.ContentLength > MaximumImageBytes) return null;

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0 || bytes.Length > MaximumImageBytes) return null;
        var image = new ListingImage(bytes, contentType);
        cache.Set($"listing-image:{listingId}", image, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(6), Size = bytes.Length });
        return image;
    }
}
