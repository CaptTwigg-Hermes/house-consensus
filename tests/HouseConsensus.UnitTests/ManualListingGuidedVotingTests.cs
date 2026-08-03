using HouseConsensus.Shared;
using Xunit;

namespace HouseConsensus.UnitTests;

public sealed class GuidedVoteTests
{
    private static VoteRating[] Ratings(params (VoteCategory Category, CategoryRating Rating)[] changes)
    {
        var values = VoteCategories.All.Select(x => new VoteRating { Category = x, Rating = CategoryRating.Neutral }).ToArray();
        foreach (var change in changes) values.Single(x => x.Category == change.Category).Rating = change.Rating;
        return values;
    }
    [Fact] public void Locked_categories_have_exactly_the_confirmed_order() => Assert.Equal(new[] { VoteCategory.Layout, VoteCategory.Privacy, VoteCategory.Garden, VoteCategory.Condition, VoteCategory.Location, VoteCategory.Noise, VoteCategory.Price, VoteCategory.MultiGenerationFit, VoteCategory.Commute, VoteCategory.Buildability }, VoteCategories.All);
    [Theory] [InlineData(CategoryRating.Like, VoteChoice.Like, 1)] [InlineData(CategoryRating.Dislike, VoteChoice.Dislike, -1)]
    public void Final_choice_is_derived_from_equal_weighted_categories(CategoryRating rating, VoteChoice expected, int total) { var vote = new Vote(Guid.NewGuid(), Guid.NewGuid(), Ratings((VoteCategory.Layout, rating)), 3, " note ", DateTimeOffset.UtcNow); Assert.Equal(expected, vote.Choice); Assert.Equal(total, vote.Total); Assert.Equal("note", vote.Note); }
    [Fact] public void Tie_is_explicitly_derived_as_dislike() { var vote = new Vote(Guid.NewGuid(), Guid.NewGuid(), Ratings((VoteCategory.Layout, CategoryRating.Like), (VoteCategory.Privacy, CategoryRating.Dislike)), 3, null, DateTimeOffset.UtcNow); Assert.Equal(0, vote.Total); Assert.Equal(VoteChoice.Dislike, vote.Choice); }
    [Theory] [InlineData(1)] [InlineData(5)]
    public void Overall_score_accepts_the_one_to_five_boundary(int score) { var vote = new Vote(Guid.NewGuid(), Guid.NewGuid(), Ratings((VoteCategory.Layout, CategoryRating.Like)), score, null, DateTimeOffset.UtcNow); Assert.Equal(score, vote.OverallScore); }
    [Theory] [InlineData(0)] [InlineData(6)]
    public void Overall_score_rejects_values_outside_one_to_five(int score) { Assert.Throws<DomainException>(() => new Vote(Guid.NewGuid(), Guid.NewGuid(), Ratings((VoteCategory.Layout, CategoryRating.Like)), score, null, DateTimeOffset.UtcNow)); }
    [Fact] public void Cast_vote_carries_the_required_overall_score() { var request = new CastVote([], 4); Assert.Equal(4, request.OverallScore); }
    [Fact] public void Evaluation_requires_all_ten_unique_categories_and_one_non_neutral() { Assert.Throws<DomainException>(() => new Vote(Guid.NewGuid(), Guid.NewGuid(), Ratings(), 3, null, DateTimeOffset.UtcNow)); Assert.Throws<DomainException>(() => new Vote(Guid.NewGuid(), Guid.NewGuid(), Ratings((VoteCategory.Layout, CategoryRating.Like)).Take(9), 3, null, DateTimeOffset.UtcNow)); var duplicate = Ratings((VoteCategory.Layout, CategoryRating.Like)); duplicate[1].Category = VoteCategory.Layout; Assert.Throws<DomainException>(() => new Vote(Guid.NewGuid(), Guid.NewGuid(), duplicate, 3, null, DateTimeOffset.UtcNow)); }
    [Fact] public void Evaluation_rejects_undefined_numeric_rating() { var ratings = Ratings((VoteCategory.Layout, CategoryRating.Like)); ratings[1].Rating = (CategoryRating)99; Assert.Throws<DomainException>(() => new Vote(Guid.NewGuid(), Guid.NewGuid(), ratings, 3, null, DateTimeOffset.UtcNow)); }
}

public sealed class ManualListingTests
{
    [Theory] [InlineData("https://Example.dk/home/?utm_source=x#photos", "https://example.dk/home")] [InlineData("https://example.dk:443/home/", "https://example.dk/home")] [InlineData("https://Example.dk:8443/home/", "https://example.dk:8443/home")] [InlineData("https://[2001:db8::1]:8443/home/", "https://[2001:db8::1]:8443/home")] [InlineData("https://Example.dk/?utm_source=x#photos", "https://example.dk/")]
    public void Canonical_url_removes_tracking_fragment_default_port_and_trailing_slash(string value, string expected) => Assert.Equal(expected, ManualListing.NormalizeUrl(value));
    [Theory] [InlineData("http://example.dk/home")] [InlineData("/relative")] [InlineData("not a url")] [InlineData("https://example.dk:70000/home")]
    public void Manual_url_must_be_absolute_https(string value) => Assert.Throws<DomainException>(() => ManualListing.NormalizeUrl(value));
    [Fact] public void Canonical_url_rejects_embedded_credentials() => Assert.Throws<DomainException>(() => ManualListing.NormalizeUrl("https://user:secret@example.dk/home"));
        [Fact] public void Address_normalization_is_case_and_whitespace_insensitive() => Assert.Equal("nørregade 12, 8000 aarhus c", ManualListing.NormalizeAddress("  Nørregade   12,  8000 Aarhus C "));
    [Fact] public void Persistence_boundaries_reject_oversized_normalized_values() { Assert.Throws<DomainException>(() => ManualListing.NormalizeAddress(new string('a', 501))); Assert.Throws<DomainException>(() => ManualListing.NormalizeAddress("a" + new string(' ', 600) + "b")); Assert.Throws<DomainException>(() => ManualListing.NormalizeUrl("https://example.dk/" + new string('a', 2049))); }
    [Fact] public void Manual_protection_blocks_automated_state_changes() { var listing = Listing.CreateManual("https://example.dk/home", "Address 1", Guid.NewGuid(), DateTimeOffset.UtcNow); listing.ApplyImportDecision(true); listing.ApplyLearningDecision("feedback-v1", true); listing.Archive(DateTimeOffset.UtcNow, automated: true); Assert.Equal(ListingState.Active, listing.State); Assert.True(listing.ManualLifecycleProtected); Assert.Null(listing.FamilyFitScore); }
}

public sealed class ConfirmedFeatureSourceTests
{
    private static readonly string Root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
    [Fact] public void Responsive_navigation_exposes_mobile_drawer_and_permanent_desktop_panel() { var layout = File.ReadAllText(Path.Combine(Root, "src/Client/Layout/MainLayout.razor")); var navigation = File.ReadAllText(Path.Combine(Root, "src/Client/Layout/MainNavigation.razor")); Assert.Contains("data-testid=\"menu-trigger\"", layout); Assert.Contains("data-testid=\"desktop-navigation\"", layout); Assert.Contains("role=\"dialog\"", layout); Assert.Contains("@onkeydown=\"DrawerKeyDown\"", layout); Assert.Contains("href=\"listings/add\"", navigation); Assert.DoesNotContain("class=\"side-nav\"", layout); Assert.DoesNotContain("class=\"bottom-nav\"", layout); }
    [Fact] public void Manual_form_and_guided_sheet_expose_accessible_localized_contracts() { var add = File.ReadAllText(Path.Combine(Root, "src/Client/Pages/AddListing.razor")); var vote = File.ReadAllText(Path.Combine(Root, "src/Client/Components/VoteButtons.razor")); var i18n = File.ReadAllText(Path.Combine(Root, "src/Client/Services/I18n.cs")); Assert.Contains("@page \"/listings/add\"", add); Assert.Contains("type=\"url\"", add); Assert.Contains("data-testid=\"manual-address\"", add); Assert.Contains("data-testid=\"guided-vote-sheet\"", vote); Assert.Contains("VoteCategories.All", vote); Assert.Contains("type=\"radio\"", vote); foreach (var key in new[] { "AddListing", "Manual", "Unscored", "Neutral", "MultiGenerationFit", "Buildability", "StartVoting", "DerivedResult" }) Assert.Contains($"[\"{key}\"]", i18n); }

    [Fact]
    public void Manual_listing_accepts_optional_city_and_asking_price()
    {
        var contracts = File.ReadAllText(Path.Combine(Root, "src/Shared/Contracts.cs"));
        var page = File.ReadAllText(Path.Combine(Root, "src/Client/Pages/AddListing.razor"));
        var program = File.ReadAllText(Path.Combine(Root, "src/Server/Program.cs"));
        Assert.Contains("string? City", contracts);
        Assert.Contains("decimal? AskingPrice", contracts);
        Assert.Contains("manual-city", page);
        Assert.Contains("manual-price", page);
        Assert.Contains("request.AskingPrice", program);
        Assert.Contains("length(n.value) <= 500", File.ReadAllText(Path.Combine(Root, "src/Server/Data/Migrations/202607310002_AddManualListingsAndGuidedVoting.cs")));
        Assert.Contains("LatestOwnVote", File.ReadAllText(Path.Combine(Root, "src/Client/Components/ListingCard.razor")));
    }
}
