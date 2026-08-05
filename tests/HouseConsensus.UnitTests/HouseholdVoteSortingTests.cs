using Xunit;
using HouseConsensus.Client.Pages;
using HouseConsensus.Shared;

namespace HouseConsensus.UnitTests;

public sealed class HouseholdVoteSortingTests
{
    private static readonly Guid Viewer = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    [Fact]
    public void Sorts_household_vote_cards_by_each_supported_order_with_missing_values_last()
    {
        var older = Listing("Older", 8_000_000m, 91, VoteChoice.Like, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var newest = Listing("Newest", null, null, VoteChoice.Dislike, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var middle = Listing("Middle", 6_000_000m, 78, VoteChoice.Like, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), extraLikes: 1);
        var source = new[] { older, newest, middle };

        Assert.Equal(["Newest", "Middle", "Older"], Addresses(HouseholdVoteSorting.Sort(source, Viewer, HouseholdVoteSort.LatestActivity)));
        Assert.Equal(["Middle", "Older", "Newest"], Addresses(HouseholdVoteSorting.Sort(source, Viewer, HouseholdVoteSort.MostPositive)));
        Assert.Equal(["Older", "Middle", "Newest"], Addresses(HouseholdVoteSorting.Sort(source, Viewer, HouseholdVoteSort.FamilyFit)));
        Assert.Equal(["Middle", "Older", "Newest"], Addresses(HouseholdVoteSorting.Sort(source, Viewer, HouseholdVoteSort.PriceLow)));
        Assert.Equal(["Older", "Middle", "Newest"], Addresses(HouseholdVoteSorting.Sort(source, Viewer, HouseholdVoteSort.PriceHigh)));
    }

    [Fact]
    public void Family_fit_sort_places_untrusted_numeric_scores_last()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var trusted = Listing("Trusted", 1, 70, VoteChoice.Like, timestamp);
        var untrusted = Listing("Untrusted", 1, 99, VoteChoice.Like, timestamp) with { ScoreRuleVersion = null };

        Assert.Equal(["Trusted", "Untrusted"],
            Addresses(HouseholdVoteSorting.Sort([untrusted, trusted], Viewer, HouseholdVoteSort.FamilyFit)));
    }

    [Fact]
    public void Uses_ordinal_address_and_listing_id_as_stable_tie_breakers()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var higherId = Listing("Same", 1, 1, VoteChoice.Like, timestamp) with { Id = Guid.Parse("00000000-0000-0000-0000-000000000002") };
        var lowerId = Listing("Same", 1, 1, VoteChoice.Like, timestamp) with { Id = Guid.Parse("00000000-0000-0000-0000-000000000001") };

        var result = HouseholdVoteSorting.Sort([higherId, lowerId], Viewer, HouseholdVoteSort.PriceHigh);

        Assert.Equal([lowerId.Id, higherId.Id], result.Select(listing => listing.Id));
    }

    [Fact]
    public void Household_votes_page_exposes_localized_sorting_controls()
    {
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var page = File.ReadAllText(Path.Combine(root, "src/Client/Pages/HouseholdVotes.razor"));

        Assert.Contains("data-testid=\"household-sort\"", page, StringComparison.Ordinal);
        Assert.Contains("@L[\"Sort\"]", page, StringComparison.Ordinal);
        Assert.Contains("HouseholdVoteSort.LatestActivity", page, StringComparison.Ordinal);
        Assert.Contains("HouseholdVoteSort.MostPositive", page, StringComparison.Ordinal);
        Assert.Contains("HouseholdVoteSort.FamilyFit", page, StringComparison.Ordinal);
        Assert.Contains("HouseholdVoteSort.PriceLow", page, StringComparison.Ordinal);
        Assert.Contains("HouseholdVoteSort.PriceHigh", page, StringComparison.Ordinal);
    }

    [Fact]
    public void Excludes_cards_with_only_the_viewers_vote()
    {
        var listing = Listing("Private", 1, 1, VoteChoice.Like, DateTimeOffset.UtcNow) with
        {
            Votes = [new VoteDto(1, Guid.NewGuid(), Viewer, VoteChoice.Like, [], DateTimeOffset.UtcNow)]
        };

        Assert.Empty(HouseholdVoteSorting.Sort([listing], Viewer, HouseholdVoteSort.LatestActivity));
    }

    private static string[] Addresses(IEnumerable<ListingDto> listings) => listings.Select(x => x.Address).ToArray();

    private static ListingDto Listing(string address, decimal? price, double? fit, VoteChoice choice, DateTimeOffset created, int extraLikes = 0)
    {
        var id = Guid.NewGuid();
        var votes = new List<VoteDto> { new(1, id, Other, choice, [], created) };
        for (var index = 0; index < extraLikes; index++)
            votes.Add(new VoteDto(index + 2, id, Guid.NewGuid(), VoteChoice.Like, [], created.AddMinutes(index + 1)));
        return new ListingDto(id, id.ToString(), address, "City", price, fit, ListingState.Active, true, 1, null, null, null, null, false, votes,
            FamilyPrivacyScore: fit.HasValue ? fit : null, KidsSpaceScore: fit.HasValue ? fit : null,
            GardenScore: fit.HasValue ? fit : null, SharedLivingScore: fit.HasValue ? fit : null,
            PracticalScore: fit.HasValue ? fit : null, FamilyPrivacyWeight: fit.HasValue ? 30 : null,
            KidsSpaceWeight: fit.HasValue ? 20 : null, GardenWeight: fit.HasValue ? 20 : null,
            SharedLivingWeight: fit.HasValue ? 15 : null, PracticalWeight: fit.HasValue ? 15 : null,
            ScoreCoveragePct: fit.HasValue ? 100 : null, FamilyPrivacyAvailable: fit.HasValue ? true : null,
            ScoreRuleVersion: fit.HasValue ? "family-v1" : null);
    }
}
