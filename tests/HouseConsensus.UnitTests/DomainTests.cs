using Xunit;
using HouseConsensus.Shared;
namespace HouseConsensus.UnitTests;

public sealed class ConsensusTests
{
    [Theory]
    [InlineData("Mads Frederiksen", "mads@example.test", "MF")]
    [InlineData("", "capt.twigg@example.test", "CE")]
    public void Avatar_initials_prefer_display_name_and_fall_back_to_email(string displayName, string email, string expected)
        => Assert.Equal(expected, AvatarInitials.From(displayName, email));

    [Fact]
    public void Avatar_color_is_stable_and_varies_by_member()
    {
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("00000000-0000-0000-0000-000000000002");

        Assert.Equal(AvatarColor.Css(first), AvatarColor.Css(first));
        Assert.NotEqual(AvatarColor.Css(first), AvatarColor.Css(second));
        Assert.StartsWith("#", AvatarColor.Css(first), StringComparison.Ordinal);
    }

    [Fact]
    public void Requires_explicit_latest_like_from_every_active_member()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var house = Guid.NewGuid(); var t = DateTimeOffset.UtcNow;
        var votes = new[] { new Vote { Id = 1, ListingId = house, MemberId = a, Choice = VoteChoice.Like, CreatedAt = t }, new Vote { Id = 2, ListingId = house, MemberId = b, Choice = VoteChoice.Like, CreatedAt = t }, new Vote { Id = 3, ListingId = house, MemberId = b, Choice = VoteChoice.Dislike, CreatedAt = t.AddSeconds(1) } };
        Assert.False(ConsensusRules.HasConsensus([a, b], votes)); Assert.True(ConsensusRules.HasConsensus([a], votes));
    }
    [Fact] public void Empty_household_never_has_consensus() => Assert.False(ConsensusRules.HasConsensus([], []));
    [Fact]
    public void Vote_note_is_optional_editable_and_audited()
    {
        var member = Guid.NewGuid(); var at = DateTimeOffset.UtcNow;
        var vote = new Vote(Guid.NewGuid(), member, VoteChoice.Like, [], " first reason ", at);
        Assert.Equal("first reason", vote.Note);
        vote.EditNote(member, "better reason", at.AddSeconds(1));
        Assert.Equal("better reason", vote.Note);
        Assert.Single(vote.NoteRevisions);
        Assert.Equal("first reason", vote.NoteRevisions[0].PreviousNote);
        Assert.Throws<DomainException>(() => vote.EditNote(Guid.NewGuid(), "tamper", at.AddSeconds(2)));
    }
    [Fact]
    public void Vote_note_may_be_skipped_but_cannot_exceed_limit()
    {
        var member = Guid.NewGuid();
        Assert.Null(new Vote(Guid.NewGuid(), member, VoteChoice.Dislike, [], null, DateTimeOffset.UtcNow).Note);
        Assert.Throws<DomainException>(() => new Vote(Guid.NewGuid(), member, VoteChoice.Like, [], new string('x', 2001), DateTimeOffset.UtcNow));
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
public sealed class AiLearningRuleTests
{
    private const string Rule = """{"combinator":"all","conditions":[{"field":"condition","operator":"eq","value":"poor"}]}""";
    [Theory]
    [InlineData("{\"combinator\":\"xor\",\"conditions\":[{\"field\":\"condition\",\"operator\":\"eq\",\"value\":\"poor\"}]}")]
    [InlineData("{\"combinator\":\"all\",\"conditions\":[{\"field\":\"privacy_score\",\"operator\":\"lt\",\"value\":\"low\"}]}")]
    [InlineData("{\"combinator\":\"all\",\"conditions\":[{\"field\":\"separate_entrance\",\"operator\":\"contains\",\"value\":true}]}")]
    public void Invalid_rule_shape_or_types_are_rejected(string rule) => Assert.Throws<DomainException>(() => AiLearningRules.Validate(rule));

    [Fact]
    public void Approved_rule_rejects_only_high_confidence_unvoted_unoverridden_match()
    {
        var match = new Listing { ExternalId = "match", Address = "A", Condition = "poor", AiConfidence = 1.0 };
        Assert.True(AiLearningRules.Apply(match, false, "feedback-v1", Rule));
        Assert.Equal(ListingState.AiRejected, match.State);
        Assert.Equal("feedback-v1", match.LearningRuleVersion);

        var voted = new Listing { ExternalId = "voted", Address = "B", Condition = "poor", AiConfidence = 1.0 };
        Assert.False(AiLearningRules.Apply(voted, true, "feedback-v1", Rule));
        var medium = new Listing { ExternalId = "medium", Address = "C", Condition = "poor", AiConfidence = .66 };
        Assert.False(AiLearningRules.Apply(medium, false, "feedback-v1", Rule));
        var mismatch = new Listing { ExternalId = "mismatch", Address = "D", Condition = "good", AiConfidence = 1.0 };
        Assert.False(AiLearningRules.Apply(mismatch, false, "feedback-v1", Rule));
    }
}

public sealed class CommentTests
{
    [Fact]
    public void Edit_and_delete_retain_immutable_revisions()
    { var author = Guid.NewGuid(); var c = new Comment(Guid.NewGuid(), author, "before", DateTimeOffset.UtcNow); c.Edit(author, false, "after", DateTimeOffset.UtcNow.AddSeconds(1)); c.Delete(Guid.NewGuid(), true, DateTimeOffset.UtcNow.AddSeconds(2)); Assert.True(c.IsDeleted); Assert.Equal(2, c.Revisions.Count); Assert.Equal("before", c.Revisions[0].PreviousBody); Assert.Equal("after", c.Revisions[1].PreviousBody); Assert.True(c.Revisions[1].WasDeletion); }
    [Fact] public void Other_member_cannot_moderate() => Assert.Throws<DomainException>(() => new Comment(Guid.NewGuid(), Guid.NewGuid(), "text", DateTimeOffset.UtcNow).Delete(Guid.NewGuid(), false, DateTimeOffset.UtcNow));
}
public sealed class MemberTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("da")]
    public void Supported_language_is_saved(string language)
    {
        var member = new Member { Email = "a@b.test" };
        member.SetLanguage(language);
        Assert.Equal(language, member.Language);
    }

    [Fact]
    public void Unsupported_language_is_rejected()
        => Assert.Throws<DomainException>(() => new Member { Email = "a@b.test" }.SetLanguage("de"));

    [Fact]
    public void Profile_trims_nickname_and_saves_palette_color()
    {
        var member = new Member { Email = "a@b.test" };

        member.SetProfile("  Captain  ", AvatarColor.Options[3]);

        Assert.Equal("Captain", member.DisplayName);
        Assert.Equal(AvatarColor.Options[3], member.AvatarColor);
    }

    [Fact]
    public void Profile_rejects_missing_nickname_and_color()
    {
        var member = new Member { Email = "a@b.test" };
        Assert.Throws<DomainException>(() => member.SetProfile(null!, AvatarColor.Options[0]));
        Assert.Throws<DomainException>(() => member.SetProfile("Captain", null!));
    }

    [Fact]
    public void Profile_rejects_nickname_over_40_characters()
        => Assert.Throws<DomainException>(() => new Member { Email = "a@b.test" }.SetProfile(new string('x', 41), AvatarColor.Options[0]));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Profile_rejects_blank_nickname(string nickname)
        => Assert.Throws<DomainException>(() => new Member { Email = "a@b.test" }.SetProfile(nickname, AvatarColor.Options[0]));

    [Fact]
    public void Profile_rejects_unknown_color()
        => Assert.Throws<DomainException>(() => new Member { Email = "a@b.test" }.SetProfile("Captain", "#ffffff"));
}

