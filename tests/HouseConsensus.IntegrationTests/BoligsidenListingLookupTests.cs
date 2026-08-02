using HouseConsensus.Server.Listings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text;
using Xunit;

namespace HouseConsensus.IntegrationTests;

public sealed class BoligsidenListingLookupTests
{
    [Fact]
    public async Task Boligsiden_address_url_fetches_listing_details_over_real_http()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/adresser", () => Results.Json(new[] { new { id = "address-1", betegnelse = "Åvendingen 48, 2700 Brønshøj" } }));
        app.MapGet("/addresses/address-1", () => Results.Json(new { slugAddress = "aavendingen-48-2700-broenshoej", cases = new[] { new { caseID = "case-1", status = "open" } } }));
        app.MapGet("/cases/case-1", () => Results.Json(new
        {
            caseID = "case-1", status = "open", priceCash = 7_495_000, housingArea = 199, lotArea = 720,
            numberOfRooms = 7, numberOfFloors = 2, numberOfBathrooms = 2, yearBuilt = 1952,
            energyLabel = "d", monthlyExpense = 5_432, daysOnMarket = 17,
            slugAddress = "aavendingen-48-2700-broenshoej", coordinates = new { lat = 55.70537, lon = 12.464963 },
            address = new { roadName = "Åvendingen", houseNumber = "48", cityName = "Brønshøj", zipCode = 2700 },
            defaultImage = new { imageSources = new object?[] { null, "malformed", new { url = "https://images.boligsiden.dk/images/case/case-1/1440x960/photo.webp", size = new { width = 1440, height = 960 } } } }
        }));
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var endpoint = new Uri(app.Urls.Single());
            var lookup = new BoligsidenListingLookup(new HttpClient(), endpoint, endpoint);
            var result = await lookup.ResolveAsync("https://www.boligsiden.dk/adresse/aavendingen-48-2700-broenshoej-01018704__48_______", TestContext.Current.CancellationToken);
            Assert.NotNull(result);
            Assert.Equal("Åvendingen 48", result.Address);
            Assert.Equal("Brønshøj", result.City);
            Assert.Equal("2700", result.PostalCode);
            Assert.Equal(7_495_000m, result.AskingPrice);
            Assert.Equal(199, result.LivingArea);
            Assert.Equal(720, result.LotArea);
            Assert.Equal(7, result.Rooms);
            Assert.Equal(2, result.Floors);
            Assert.Equal(2, result.Bathrooms);
            Assert.Equal(1952, result.YearBuilt);
            Assert.Equal("D", result.EnergyLabel);
            Assert.Equal("case-1", result.ExternalId);
            Assert.Equal("https://images.boligsiden.dk/images/case/case-1/1440x960/photo.webp", result.PreviewImageUrl);
        }
        finally { await app.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public void Typed_http_client_resolves_the_production_constructor()
    {
        var services = new ServiceCollection();
        services.AddHttpClient<BoligsidenListingLookup>();
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<BoligsidenListingLookup>());
    }

    [Fact]
    public async Task Malformed_upstream_values_are_ignored_without_throwing()
    {
        var handler = new SequenceHandler(
            "[{\"id\":\"address-1\"}]",
            "{\"slugAddress\":\"aavendingen-48-2700-broenshoej\",\"cases\":[{\"caseID\":\"case-1\",\"status\":\"open\"}]}",
            "{\"status\":\"open\",\"priceCash\":-1,\"housingArea\":\"bad\",\"lotArea\":-5,\"numberOfRooms\":0,\"numberOfFloors\":{},\"numberOfBathrooms\":-1,\"yearBuilt\":9999,\"energyLabel\":\"invalid\",\"monthlyExpense\":-1,\"daysOnMarket\":-1,\"coordinates\":{\"lat\":100,\"lon\":12},\"address\":{\"roadName\":\"Åvendingen\",\"houseNumber\":\"48\"},\"defaultImage\":null}");
        var lookup = new BoligsidenListingLookup(new HttpClient(handler), new Uri("https://dawa.test/"), new Uri("https://boligsiden.test/"));
        var result = await lookup.ResolveAsync("https://www.boligsiden.dk/adresse/aavendingen-48-2700-broenshoej", TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Null(result.AskingPrice);
        Assert.Null(result.LivingArea);
        Assert.Null(result.LotArea);
        Assert.Null(result.Rooms);
        Assert.Null(result.Floors);
        Assert.Null(result.Bathrooms);
        Assert.Null(result.YearBuilt);
        Assert.Null(result.EnergyLabel);
        Assert.Null(result.MonthlyExpense);
        Assert.Null(result.DaysOnMarket);
        Assert.Null(result.Latitude);
        Assert.Null(result.Longitude);
        Assert.Null(result.PreviewImageUrl);
    }

    [Fact]
    public async Task Oversized_address_identifier_is_rejected_before_uri_construction()
    {
        var handler = new SequenceHandler($"[{{\"id\":\"{new string('a', 129)}\"}}]");
        var lookup = new BoligsidenListingLookup(new HttpClient(handler), new Uri("https://dawa.test/"), new Uri("https://boligsiden.test/"));
        Assert.Null(await lookup.ResolveAsync("https://www.boligsiden.dk/adresse/aavendingen-48-2700-broenshoej", TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Oversized_case_identifier_is_rejected_before_uri_construction()
    {
        var handler = new SequenceHandler(
            "[{\"id\":\"address-1\"}]",
            $"{{\"slugAddress\":\"aavendingen-48-2700-broenshoej\",\"cases\":[{{\"caseID\":\"{new string('c', 129)}\",\"status\":\"open\"}}]}}");
        var lookup = new BoligsidenListingLookup(new HttpClient(handler), new Uri("https://dawa.test/"), new Uri("https://boligsiden.test/"));
        Assert.Null(await lookup.ResolveAsync("https://www.boligsiden.dk/adresse/aavendingen-48-2700-broenshoej", TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Scalar_upstream_object_returns_no_preview_instead_of_throwing()
    {
        var handler = new SequenceHandler("[{\"id\":\"address-1\"}]", "null");
        var lookup = new BoligsidenListingLookup(new HttpClient(handler), new Uri("https://dawa.test/"), new Uri("https://boligsiden.test/"));
        Assert.Null(await lookup.ResolveAsync("https://www.boligsiden.dk/adresse/aavendingen-48-2700-broenshoej", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("https://example.com/adresse/aavendingen-48-2700-broenshoej")]
    [InlineData("http://www.boligsiden.dk/adresse/aavendingen-48-2700-broenshoej")]
    [InlineData("https://www.boligsiden.dk/bolig/other")]
    public async Task Lookup_rejects_non_Boligsiden_address_urls(string url)
    {
        var lookup = new BoligsidenListingLookup(new HttpClient(), new Uri("http://127.0.0.1:1"), new Uri("http://127.0.0.1:1"));
        Assert.Null(await lookup.ResolveAsync(url, TestContext.Current.CancellationToken));
    }

    private sealed class SequenceHandler(params string[] bodies) : HttpMessageHandler
    {
        private readonly Queue<string> _bodies = new(bodies);
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_bodies.Dequeue(), Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
            return Task.FromResult(response);
        }
    }
}
