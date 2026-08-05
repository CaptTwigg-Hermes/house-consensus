using HouseConsensus.Client.Services;
using HouseConsensus.Shared;
using Xunit;

namespace HouseConsensus.UnitTests;
public sealed class ListingFilterStateTests
{
    private static ListingDto Listing(string id, double score = 80, decimal? price = 8_000_000, int? area = 200, int? garden = 800, string? condition = "good", bool trusted = true) =>
        new(Guid.Parse(id), id, "House " + id[^1], "Roskilde", price, score, ListingState.Active, true, .9, null, null, null, null, false, [], LivingArea: area, LotArea: garden, Rooms: 6, YearBuilt: 1980, PrivacyScore: 4,
            FamilyPrivacyScore: trusted ? 80 : null, KidsSpaceScore: trusted ? 80 : null, GardenScore: trusted ? 80 : null,
            SharedLivingScore: trusted ? 80 : null, PracticalScore: trusted ? 80 : null,
            FamilyPrivacyWeight: trusted ? 30 : null, KidsSpaceWeight: trusted ? 20 : null, GardenWeight: trusted ? 20 : null,
            SharedLivingWeight: trusted ? 15 : null, PracticalWeight: trusted ? 15 : null,
            MonthlyExpense: 5000, DaysOnMarket: 20, CommuteMinutes: 30, BuildableStatus: "extra_house", Condition: condition, GardenOrientation: "southwest", MultigenFit: "likely", PostalCode: "4000", Preferred: true, IsNew: true, FamilyUnits: "two_family",
            ScoreCoveragePct: trusted ? 100 : null, FamilyPrivacyAvailable: trusted ? true : null, ScoreRuleVersion: trusted ? "family-v1" : null);

    [Fact]
    public void Active_filters_exclude_unknown_values_and_match_all_selected_ranges()
    {
        var filter = new ListingFilterState { MinArea = 180, MinGarden = 700, MaxCommute = 35, MinFamilyScore = 75, Conditions = ["good"] };
        Assert.True(filter.Matches(Listing("11111111-1111-1111-1111-111111111111")));
        Assert.False(filter.Matches(Listing("22222222-2222-2222-2222-222222222222", area: null)));
        Assert.False(filter.Matches(Listing("33333333-3333-3333-3333-333333333333", condition: null)));
    }

    [Fact]
    public void Search_sort_and_category_filters_are_deterministic()
    {
        var filter = new ListingFilterState { Search = "roskilde", Sort = ListingSort.PriceLow, MultigenFits = ["likely"] };
        var expensive = Listing("11111111-1111-1111-1111-111111111111", price: 9_000_000);
        var cheap = Listing("22222222-2222-2222-2222-222222222222", price: 7_000_000);
        Assert.Equal([cheap.Id, expensive.Id], filter.Apply([expensive, cheap]).Select(x => x.Id));
    }

    [Fact]
    public void Score_filter_and_sort_ignore_untrusted_numeric_scores()
    {
        var trusted = Listing("11111111-1111-1111-1111-111111111111", score: 70);
        var untrusted = Listing("22222222-2222-2222-2222-222222222222", score: 99, trusted: false);
        var filter = new ListingFilterState { Sort = ListingSort.FamilyScore };

        Assert.Equal([trusted.Id, untrusted.Id], filter.Apply([untrusted, trusted]).Select(x => x.Id));

        filter.MinFamilyScore = 80;
        Assert.Empty(filter.Apply([trusted, untrusted]));
    }

    [Fact]
    public void Municipality_and_upstream_sort_options_match_houseshopping_parity()
    {
        var first = Listing("55555555-5555-5555-5555-555555555555") with { LotArea = 500, YearBuilt = 1970, CommuteMinutes = 40, IsNew = false };
        var second = Listing("66666666-6666-6666-6666-666666666666") with { LotArea = 900, YearBuilt = 2000, CommuteMinutes = 20, IsNew = true };
        var filter = new ListingFilterState { Municipalities = ["Roskilde"] };
        foreach (var sort in new[] { ListingSort.GardenHigh, ListingSort.YearNewest, ListingSort.CommuteFastest, ListingSort.NewFirst })
        {
            filter.Sort = sort;
            Assert.Equal(second.Id, filter.Apply([first, second])[0].Id);
        }
        filter.Municipalities = ["Copenhagen"];
        Assert.Empty(filter.Apply([first, second]));
    }

    [Fact]
    public void Quick_filters_and_postal_search_match_houseshopping_parity()
    {
        var listing = Listing("44444444-4444-4444-4444-444444444444");
        var filter = new ListingFilterState { Search = "4000", OnlyPreferred = true, OnlyNew = true, OnlyQuiet = false, OnlyAiAssessed = true, FamilyUnits = ["two_family"] };
        Assert.True(filter.Matches(listing));
        Assert.False(filter.Matches(listing with { Preferred = null }));
    }
}
