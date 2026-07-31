using HouseConsensus.Shared;

namespace HouseConsensus.Client.Services;

public static class QueueFilter
{
    public static IReadOnlyList<ListingDto> Apply(IEnumerable<ListingDto> listings, bool hideDisliked) =>
        listings.Where(x => !hideDisliked || x.Votes.All(v => v.Choice != VoteChoice.Dislike)).ToList();
}
