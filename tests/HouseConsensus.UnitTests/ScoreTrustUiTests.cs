using Xunit;

namespace HouseConsensus.UnitTests;

public sealed class ScoreTrustUiTests
{
    private static readonly string Root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);

    [Fact]
    public void Score_provenance_is_projected_into_listing_dtos()
    {
        var program = File.ReadAllText(Path.Combine(Root, "src/Server/Program.cs"));
        Assert.Contains("x.ScoreCoveragePct", program, StringComparison.Ordinal);
        Assert.Contains("x.FamilyPrivacyAvailable", program, StringComparison.Ordinal);
        Assert.Contains("x.ScoreRuleVersion", program, StringComparison.Ordinal);
        Assert.Contains("x.ScoreNotesJson", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Listing_card_distinguishes_incomplete_review_and_not_scored()
    {
        var card = File.ReadAllText(Path.Combine(Root, "src/Client/Components/ListingCard.razor"));
        var detail = File.ReadAllText(Path.Combine(Root, "src/Client/Pages/Detail.razor"));
        var browse = File.ReadAllText(Path.Combine(Root, "src/Client/Pages/Browse.razor"));
        var household = File.ReadAllText(Path.Combine(Root, "src/Client/Pages/HouseholdVotes.razor"));
        var myVotes = File.ReadAllText(Path.Combine(Root, "src/Client/Pages/MyVotes.razor"));
        var review = File.ReadAllText(Path.Combine(Root, "src/Client/Pages/Owner/Review.razor"));
        var filters = File.ReadAllText(Path.Combine(Root, "src/Client/Services/ListingFilterState.cs"));
        var program = File.ReadAllText(Path.Combine(Root, "src/Server/Program.cs"));
        var i18n = File.ReadAllText(Path.Combine(Root, "src/Client/Services/I18n.cs"));

        Assert.Contains("Listing.ScoreStatus", card, StringComparison.Ordinal);
        Assert.Contains("ScoreStatus.Incomplete", card, StringComparison.Ordinal);
        Assert.Contains("ScoreStatus.NeedsReview", card, StringComparison.Ordinal);
        Assert.Contains("ScoreStatus.NotScored", card, StringComparison.Ordinal);
        Assert.Contains("listing.ScoreStatus", detail, StringComparison.Ordinal);
        Assert.Contains("ScoreStatus.Incomplete", detail, StringComparison.Ordinal);
        Assert.Contains("ScoreStatus.NeedsReview", detail, StringComparison.Ordinal);
        Assert.Contains("x.TrustedFamilyFitScore", browse, StringComparison.Ordinal);
        Assert.Contains("listing.TrustedFamilyFitScore", household, StringComparison.Ordinal);
        Assert.Contains("row.Listing.TrustedFamilyFitScore", myVotes, StringComparison.Ordinal);
        Assert.Contains("item.TrustedFamilyFitScore", review, StringComparison.Ordinal);
        Assert.Contains("x.TrustedFamilyFitScore", filters, StringComparison.Ordinal);
        Assert.DoesNotContain("ThenByDescending(x => x.FamilyFitScore)", program, StringComparison.Ordinal);
        Assert.Contains("[\"ScoreIncomplete\"]", i18n, StringComparison.Ordinal);
        Assert.Contains("[\"ScoreNeedsReview\"]", i18n, StringComparison.Ordinal);
        Assert.Contains("[\"ScoreNotScored\"]", i18n, StringComparison.Ordinal);
    }

    [Fact]
    public void Ef_migration_owns_existing_score_provenance_columns()
    {
        var migration = Path.Combine(Root, "src/Server/Data/Migrations/202608050001_AddScoreTrustProjection.cs");
        Assert.True(File.Exists(migration));
        var source = File.ReadAllText(migration);
        foreach (var column in new[] { "ScoreCoveragePct", "FamilyPrivacyAvailable", "ScoreRuleVersion", "ScoreNotesJson" })
            Assert.Contains(column, source, StringComparison.Ordinal);
        Assert.Contains("conrelid='listings'::regclass", source, StringComparison.Ordinal);
    }
}
