using HouseConsensus.Shared;
using Xunit;

namespace HouseConsensus.UnitTests;

public sealed class ScoreStatusRulesTests
{
    [Fact]
    public void Never_assessed_listing_is_not_scored()
        => Assert.Equal(ScoreStatus.NotScored,
            ScoreStatusRules.Resolve(null, false, null, null, false, null));

    [Fact]
    public void Failed_or_incomplete_assessment_is_incomplete_not_zero()
        => Assert.Equal(ScoreStatus.Incomplete,
            ScoreStatusRules.Resolve(null, true, null, null, false, null));

    [Fact]
    public void Legacy_numeric_score_without_provenance_needs_review()
        => Assert.Equal(ScoreStatus.NeedsReview,
            ScoreStatusRules.Resolve(41.6, true, null, null, false, null));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Numeric_score_without_rule_version_needs_review(string? ruleVersion)
        => Assert.Equal(ScoreStatus.NeedsReview,
            ScoreStatusRules.Resolve(82.4, true, 100, true, true, ruleVersion));

    [Theory]
    [InlineData(82.4, 70, true, true)]
    [InlineData(82.4, 100, false, true)]
    [InlineData(82.4, 100, true, false)]
    public void Numeric_score_with_incomplete_evidence_is_incomplete(
        double score, double coverage, bool privacyAvailable, bool hasCompleteBreakdown)
        => Assert.Equal(ScoreStatus.Incomplete,
            ScoreStatusRules.Resolve(score, true, coverage, privacyAvailable, hasCompleteBreakdown, "family-v1"));

    [Theory]
    [InlineData(82.4)]
    [InlineData(0.0)]
    public void Numeric_score_with_complete_evidence_is_complete(double score)
        => Assert.Equal(ScoreStatus.Complete,
            ScoreStatusRules.Resolve(score, true, 100, true, true, "family-v1"));

    [Fact]
    public void Listing_dto_exposes_only_trusted_complete_score()
    {
        var listing = CompleteListing();
        Assert.Equal(ScoreStatus.Complete, listing.ScoreStatus);
        Assert.Equal(82.4, listing.TrustedFamilyFitScore);

        var unversioned = listing with { ScoreRuleVersion = null };
        Assert.Equal(ScoreStatus.NeedsReview, unversioned.ScoreStatus);
        Assert.Null(unversioned.TrustedFamilyFitScore);
    }

    private static ListingDto CompleteListing() => new(
        Guid.NewGuid(), "case", "Address", "City", 1, 82.4,
        ListingState.Active, true, 1, null, null, null, null, false, [],
        FamilyPrivacyScore: 91, KidsSpaceScore: 98, GardenScore: 100,
        SharedLivingScore: 68, PracticalScore: 35,
        FamilyPrivacyWeight: 30, KidsSpaceWeight: 20, GardenWeight: 20,
        SharedLivingWeight: 15, PracticalWeight: 15,
        ScoreCoveragePct: 100, FamilyPrivacyAvailable: true,
        ScoreRuleVersion: "family-v1");
}
