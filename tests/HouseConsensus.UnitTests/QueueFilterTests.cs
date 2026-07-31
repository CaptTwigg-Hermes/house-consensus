using HouseConsensus.Client.Services;
using HouseConsensus.Shared;
using Xunit;

namespace HouseConsensus.UnitTests;

public sealed class QueueFilterTests
{
    private static ListingDto Listing(string id, params VoteChoice[] choices)
    {
        var listingId = Guid.Parse(id);
        var votes = choices.Select((choice, index) =>
            new VoteDto(index + 1, listingId, Guid.NewGuid(), choice, [], DateTimeOffset.UtcNow)).ToArray();
        return new ListingDto(listingId, id, "House", "Roskilde", 8_000_000, 80,
            ListingState.Active, true, .9, null, null, null, null, false, votes);
    }

    [Fact]
    public void Hide_disliked_excludes_any_listing_with_a_dislike_vote()
    {
        var liked = Listing("11111111-1111-1111-1111-111111111111", VoteChoice.Like);
        var disliked = Listing("22222222-2222-2222-2222-222222222222", VoteChoice.Dislike);
        var mixed = Listing("33333333-3333-3333-3333-333333333333", VoteChoice.Like, VoteChoice.Dislike);
        var unvoted = Listing("44444444-4444-4444-4444-444444444444");

        Assert.Equal([liked.Id, unvoted.Id], QueueFilter.Apply([liked, disliked, mixed, unvoted], hideDisliked: true).Select(x => x.Id));
        Assert.Equal(4, QueueFilter.Apply([liked, disliked, mixed, unvoted], hideDisliked: false).Count);
    }
}
