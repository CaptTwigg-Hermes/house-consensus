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
}