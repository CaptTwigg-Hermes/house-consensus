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
    public static ListingDto[] Sort(IEnumerable<ListingDto> listings, Guid? viewerVotingIdentityId, HouseholdVoteSort sort)
    {
        var visible = listings.Where(listing => OtherVotes(listing, viewerVotingIdentityId).Any());
        IOrderedEnumerable<ListingDto> ordered = sort switch
        {
            HouseholdVoteSort.MostPositive => visible
                .OrderByDescending(listing => OtherVotes(listing, viewerVotingIdentityId).Count(vote => vote.Choice == VoteChoice.Like))
                .ThenBy(listing => OtherVotes(listing, viewerVotingIdentityId).Count(vote => vote.Choice == VoteChoice.Dislike))
                .ThenByDescending(listing => LatestActivity(listing, viewerVotingIdentityId)),
            HouseholdVoteSort.FamilyFit => visible
                .OrderByDescending(listing => listing.TrustedFamilyFitScore.HasValue)
                .ThenByDescending(listing => listing.TrustedFamilyFitScore),
            HouseholdVoteSort.PriceLow => visible
                .OrderByDescending(listing => listing.Price.HasValue)
                .ThenBy(listing => listing.Price),
            HouseholdVoteSort.PriceHigh => visible
                .OrderByDescending(listing => listing.Price.HasValue)
                .ThenByDescending(listing => listing.Price),
            _ => visible.OrderByDescending(listing => LatestActivity(listing, viewerVotingIdentityId))
        };

        return ordered
            .ThenBy(listing => listing.Address, StringComparer.OrdinalIgnoreCase)
            .ThenBy(listing => listing.Id)
            .ToArray();
    }

    private static IEnumerable<VoteDto> OtherVotes(ListingDto listing, Guid? viewerVotingIdentityId) =>
        listing.Votes.Where(vote => (vote.EffectiveMemberId ?? vote.MemberId) != viewerVotingIdentityId);

    private static DateTimeOffset LatestActivity(ListingDto listing, Guid? viewerVotingIdentityId) =>
        OtherVotes(listing, viewerVotingIdentityId).Max(vote => vote.CreatedAt);
}
