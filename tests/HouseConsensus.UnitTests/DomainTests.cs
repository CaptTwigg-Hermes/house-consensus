using Xunit;
using HouseConsensus.Shared;
namespace HouseConsensus.UnitTests;

public sealed class ConsensusTests
{
    [Fact]
    public void Requires_explicit_latest_like_from_every_active_member()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var house = Guid.NewGuid(); var t = DateTimeOffset.UtcNow;
        var votes = new[] { new Vote { Id = 1, ListingId = house, MemberId = a, Choice = VoteChoice.Like, CreatedAt = t }, new Vote { Id = 2, ListingId = house, MemberId = b, Choice = VoteChoice.Like, CreatedAt = t }, new Vote { Id = 3, ListingId = house, MemberId = b, Choice = VoteChoice.Dislike, CreatedAt = t.AddSeconds(1) } };
        Assert.False(ConsensusRules.HasConsensus([a, b], votes)); Assert.True(ConsensusRules.HasConsensus([a], votes));
    }
    [Fact] public void Empty_household_never_has_consensus() => Assert.False(ConsensusRules.HasConsensus([], []));
    [Fact]
    public void Clearing_a_vote_is_an_immutable_not_voted_history_event()
    {
        var member = Guid.NewGuid(); var house = Guid.NewGuid(); var at = DateTimeOffset.UtcNow;
        var history = new[] { new Vote { Id = 1, ListingId = house, MemberId = member, Choice = VoteChoice.Like, CreatedAt = at }, new Vote { Id = 2, ListingId = house, MemberId = member, Choice = VoteChoice.NotVoted, CreatedAt = at.AddSeconds(1) } };
        Assert.Equal(2, history.Length); Assert.Equal(VoteChoice.NotVoted, ConsensusRules.LatestVotes(history)[member].Choice); Assert.False(ConsensusRules.HasConsensus([member], history));
    }
    [Fact]
    public void Deactivation_and_reactivation_recalculate_from_history()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var votes = new[] { new Vote { MemberId = a, Choice = VoteChoice.Like }, new Vote { MemberId = b, Choice = VoteChoice.Dislike } };
        Assert.False(ConsensusRules.HasConsensus([a, b], votes)); Assert.True(ConsensusRules.HasConsensus([a], votes)); Assert.False(ConsensusRules.HasConsensus([a, b], votes));
    }
}
public sealed class ListingTests
{
    [Fact]
    public void Latest_owner_override_survives_import_decisions()
    { var l = new Listing { ExternalId = "x", Address = "a" }; var owner = Guid.NewGuid(); l.ApplyOverride(OverrideAction.Restore, owner, "good evidence", DateTimeOffset.UtcNow); l.ApplyImportDecision(true); Assert.Equal(ListingState.Restored, l.State); Assert.Single(l.Overrides); }
    [Fact]
    public void Archive_preserves_override_audit_and_can_be_restored()
    { var l = new Listing { ExternalId = "x", Address = "a" }; var owner = Guid.NewGuid(); l.ApplyOverride(OverrideAction.Reject, owner, null, DateTimeOffset.UtcNow); l.Archive(DateTimeOffset.UtcNow); l.ApplyOverride(OverrideAction.Restore, owner, null, DateTimeOffset.UtcNow); Assert.Equal(2, l.Overrides.Count); Assert.True(l.IsQueueEligible); }
    [Theory]
    [InlineData(ListingState.Active, true)]
    [InlineData(ListingState.Restored, true)]
    [InlineData(ListingState.AiRejected, false)]
    [InlineData(ListingState.ManuallyRejected, false)]
    [InlineData(ListingState.Archived, false)]
    public void Queue_eligibility_follows_state(ListingState state, bool expected)
    { var l = new Listing { ExternalId = "x", Address = "a" }; if (state == ListingState.AiRejected) l.ApplyImportDecision(true); else if (state == ListingState.ManuallyRejected) l.ApplyOverride(OverrideAction.Reject, Guid.NewGuid(), null, DateTimeOffset.UtcNow); else if (state == ListingState.Restored) l.ApplyOverride(OverrideAction.Restore, Guid.NewGuid(), null, DateTimeOffset.UtcNow); else if (state == ListingState.Archived) l.Archive(DateTimeOffset.UtcNow); Assert.Equal(expected, l.IsQueueEligible); }
}
public sealed class CommentTests
{
    [Fact]
    public void Edit_and_delete_retain_immutable_revisions()
    { var author = Guid.NewGuid(); var c = new Comment(Guid.NewGuid(), author, "before", DateTimeOffset.UtcNow); c.Edit(author, false, "after", DateTimeOffset.UtcNow.AddSeconds(1)); c.Delete(Guid.NewGuid(), true, DateTimeOffset.UtcNow.AddSeconds(2)); Assert.True(c.IsDeleted); Assert.Equal(2, c.Revisions.Count); Assert.Equal("before", c.Revisions[0].PreviousBody); Assert.Equal("after", c.Revisions[1].PreviousBody); Assert.True(c.Revisions[1].WasDeletion); }
    [Fact] public void Other_member_cannot_moderate() => Assert.Throws<DomainException>(() => new Comment(Guid.NewGuid(), Guid.NewGuid(), "text", DateTimeOffset.UtcNow).Delete(Guid.NewGuid(), false, DateTimeOffset.UtcNow));
}
public sealed class MemberTests
{ [Theory][InlineData("en")][InlineData("da")] public void Supported_language_is_saved(string language) { var m = new Member { Email = "a@b.test" }; m.SetLanguage(language); Assert.Equal(language, m.Language); } [Fact] public void Unsupported_language_is_rejected() => Assert.Throws<DomainException>(() => new Member { Email = "a@b.test" }.SetLanguage("de")); }

