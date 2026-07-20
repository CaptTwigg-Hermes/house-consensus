using System.Net;
using HouseConsensus.Client.Services;
using Xunit;

namespace HouseConsensus.UnitTests;

public sealed class ClientUiTests
{
    [Fact]
    public void Browse_query_encodes_all_existing_filters_and_omits_empty_values()
    {
        var uri = BrowseQuery.Build("Aarhus C & V", 2_500_000m, 5_000_000m);

        Assert.Equal("api/listings/browse?city=Aarhus%20C%20%26%20V&minPrice=2500000&maxPrice=5000000", uri);
        Assert.Equal("api/listings/browse", BrowseQuery.Build(" ", null, null));
    }

    [Theory]
    [InlineData("da-DK", "da")]
    [InlineData("DA", "da")]
    [InlineData("en-US", "en")]
    [InlineData("de-DE", "en")]
    [InlineData(null, "en")]
    public void Culture_is_restricted_to_supported_languages(string? requested, string expected)
        => Assert.Equal(expected, UiCulture.Normalize(requested));

    [Fact]
    public async Task Comment_edits_use_the_audited_comment_endpoint()
    {
        var handler = new CaptureHandler();
        var api = new ApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") });

        using var response = await api.EditComment(Guid.Parse("11111111-1111-1111-1111-111111111111"), "updated", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.Request!.Method);
        Assert.Equal("https://example.test/api/comments/11111111-1111-1111-1111-111111111111", handler.Request.RequestUri!.ToString());
        Assert.Contains("updated", handler.Body, StringComparison.Ordinal);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
