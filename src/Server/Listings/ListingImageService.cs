using HouseConsensus.Shared;
using Microsoft.Extensions.Caching.Memory;
using System.Net.Http.Headers;

namespace HouseConsensus.Server.Listings;

public sealed record ListingImage(byte[] Bytes, string ContentType);

public sealed class ListingImageService(HttpClient http, IMemoryCache cache, ILogger<ListingImageService> logger)
{
    private const int MaximumImageBytes = 8 * 1024 * 1024;

    public async Task<ListingImage?> GetAsync(Guid listingId, string? source, CancellationToken ct)
    {
        if (!ListingImageSources.IsAllowedSource(source)) return null;
        if (cache.TryGetValue<ListingImage>($"listing-image:{listingId}", out var cached)) return cached;

        try
        {
            return await FetchAsync(listingId, source!, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(
                new EventId(DiagnosticEventIds.ListingImageRejected, nameof(DiagnosticEventIds.ListingImageRejected)),
                "Listing image transport failed for {ListingId} with {FailureType}",
                listingId,
                ex.GetType().Name);
            return null;
        }
    }

    private async Task<ListingImage?> FetchAsync(Guid listingId, string source, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/avif"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/webp"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not { } contentType || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                new EventId(DiagnosticEventIds.ListingImageRejected, nameof(DiagnosticEventIds.ListingImageRejected)),
                "Rejected listing image response for {ListingId}: status {StatusCode}, content type {ContentType}",
                listingId,
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.MediaType ?? "missing");
            return null;
        }
        if (response.Content.Headers.ContentLength > MaximumImageBytes)
        {
            logger.LogWarning(
                new EventId(DiagnosticEventIds.ListingImageRejected, nameof(DiagnosticEventIds.ListingImageRejected)),
                "Rejected oversized listing image response for {ListingId}: declared {ContentLength} bytes",
                listingId,
                response.Content.Headers.ContentLength);
            return null;
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0 || bytes.Length > MaximumImageBytes)
        {
            logger.LogWarning(
                new EventId(DiagnosticEventIds.ListingImageRejected, nameof(DiagnosticEventIds.ListingImageRejected)),
                "Rejected listing image body for {ListingId}: received {ContentLength} bytes",
                listingId,
                bytes.Length);
            return null;
        }
        var image = new ListingImage(bytes, contentType);
        cache.Set($"listing-image:{listingId}", image, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(6), Size = bytes.Length });
        return image;
    }
}
