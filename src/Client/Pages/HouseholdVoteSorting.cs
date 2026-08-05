using HouseConsensus.Shared;

namespace HouseConsensus.Client.Pages;

public enum HouseholdVoteSort
{
    LatestActivity,
    MostPositive,
    FamilyFit,
    PriceLow,
    PriceHigh
}

public static class HouseholdVoteSorting
{
    public static ListingDto[] Sort(IEnumerable<ListingDto> listings, Guid? viewerId, HouseholdVoteSort sort)
    {
        var visible = listings.Where(listing => OtherVotes(listing, viewerId).Any());
        IOrderedEnumerable<ListingDto> ordered = sort switch
        {
            HouseholdVoteSort.MostPositive => visible
                .OrderByDescending(listing => OtherVotes(listing, viewerId).Count(vote => vote.Choice == VoteChoice.Like))
                .ThenBy(listing => OtherVotes(listing, viewerId).Count(vote => vote.Choice == VoteChoice.Dislike))
                .ThenByDescending(listing => LatestActivity(listing, viewerId)),
            HouseholdVoteSort.FamilyFit => visible
                .OrderByDescending(listing => listing.TrustedFamilyFitScore.HasValue)
                .ThenByDescending(listing => listing.TrustedFamilyFitScore),
            HouseholdVoteSort.PriceLow => visible
                .OrderByDescending(listing => listing.Price.HasValue)
                .ThenBy(listing => listing.Price),
            HouseholdVoteSort.PriceHigh => visible
                .OrderByDescending(listing => listing.Price.HasValue)
                .ThenByDescending(listing => listing.Price),
            _ => visible.OrderByDescending(listing => LatestActivity(listing, viewerId))
        };

        return ordered
            .ThenBy(listing => listing.Address, StringComparer.OrdinalIgnoreCase)
            .ThenBy(listing => listing.Id)
            .ToArray();
    }

    private static IEnumerable<VoteDto> OtherVotes(ListingDto listing, Guid? viewerId) =>
        listing.Votes.Where(vote => vote.MemberId != viewerId);

    private static DateTimeOffset LatestActivity(ListingDto listing, Guid? viewerId) =>
        OtherVotes(listing, viewerId).Max(vote => vote.CreatedAt);
}
