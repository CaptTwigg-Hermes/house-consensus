using HouseConsensus.Shared;
using Xunit;

namespace HouseConsensus.UnitTests;

public sealed class ListingImageServiceTests
{
    [Theory]
    [InlineData("https://images.boligsiden.dk/example.webp", true)]
    [InlineData("http://images.boligsiden.dk/example.webp", false)]
    [InlineData("https://images.boligsiden.dk.evil.test/example.webp", false)]
    [InlineData("https://127.0.0.1/private", false)]
    [InlineData("not-a-url", false)]
    public void Only_trusted_https_listing_images_can_be_proxied(string source, bool expected)
    {
        Assert.Equal(expected, ListingImageSources.IsAllowedSource(source));
    }
}
