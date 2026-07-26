using System.Net;
using HouseConsensus.Client.Services;
using Xunit;
using Microsoft.JSInterop;
using HouseConsensus.Client.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HouseConsensus.UnitTests;

public sealed class ClientUiTests
{
    [Fact]
    public void Cloudflare_access_users_never_receive_the_magic_link_form()
    {
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var gate = File.ReadAllText(Path.Combine(root, "src/Client/Components/AuthGate.razor"));
        var auth = File.ReadAllText(Path.Combine(root, "src/Client/Services/AuthState.cs"));
        var program = File.ReadAllText(Path.Combine(root, "src/Server/Program.cs"));

        Assert.Contains("Auth.CloudflareAccess", gate, StringComparison.Ordinal);
        Assert.Contains("CloudflareAccessDenied", gate, StringComparison.Ordinal);
        Assert.Contains("api/auth/mode", auth, StringComparison.Ordinal);
        Assert.Contains("auth.MapGet(\"/mode\"", program, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rendered_Cloudflare_access_gate_contains_no_magic_link_form()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new HttpClient(new CloudflareUnauthenticatedHandler()) { BaseAddress = new Uri("https://example.test/") });
        services.AddSingleton<ApiClient>();
        services.AddSingleton<IJSRuntime>(new CaptureJsRuntime());
        services.AddSingleton<AuthState>();
        services.AddSingleton<I18n>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<AuthGate>()).ToHtmlString());

        Assert.Contains("data-testid=\"cloudflare-access-denied\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-testid=\"auth-email\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<form", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ai_evidence_component_uses_safe_fallback_for_unparseable_and_nested_json()
    {
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var evidence = File.ReadAllText(Path.Combine(root, "src/Client/Components/AiEvidencePanel.razor"));
        Assert.Contains("AiEvidenceText.SafeFallback", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("summaries.Add(Evidence.Trim())", evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Ai_evidence_keeps_shared_supporting_signals_out_of_assessment()
    {
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var component = File.ReadAllText(Path.Combine(root, "src/Client/Components/AiEvidencePanel.razor"));
        var summaryKeysStart = component.IndexOf("SummaryKeys", StringComparison.Ordinal);
        var summaryKeysEnd = component.IndexOf("};", summaryKeysStart, StringComparison.Ordinal);
        var summaryKeys = component[summaryKeysStart..summaryKeysEnd];

        Assert.Contains("\"vision_summary\"", summaryKeys, StringComparison.Ordinal);
        Assert.DoesNotContain("\"two_family_reasons\"", summaryKeys, StringComparison.Ordinal);
        Assert.Contains("else facts.Add", component, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{broken", "Evidence unavailable")]
    [InlineData("[1,2]", "Evidence unavailable")]
    [InlineData("\"unterminated", "Evidence unavailable")]
    [InlineData("{\"nested\":true}", "Evidence unavailable")]
    [InlineData("true", "Evidence unavailable")]
    [InlineData("123", "Evidence unavailable")]
    [InlineData("null", "Evidence unavailable")]
    [InlineData("Clear prose reason", "Clear prose reason")]
    public void Ai_evidence_fallback_never_exposes_json(string raw, string expected)
        => Assert.Equal(expected, AiEvidenceText.SafeFallback(raw, "Evidence unavailable"));

    [Fact]
    public void Browse_query_encodes_all_existing_filters_and_omits_empty_values()
    {
        var uri = BrowseQuery.Build("Aarhus C & V", 2_500_000m, 5_000_000m);

        Assert.Equal("api/listings/browse?city=Aarhus%20C%20%26%20V&minPrice=2500000&maxPrice=5000000", uri);
        Assert.Equal("api/listings/browse", BrowseQuery.Build(" ", null, null));
    }

    [Theory]
    [InlineData("da-DK", "da")]
    [InlineData("DA", "da")]
    [InlineData("en-US", "en")]
    [InlineData("de-DE", "en")]
    [InlineData(null, "en")]
    public void Culture_is_restricted_to_supported_languages(string? requested, string expected)
        => Assert.Equal(expected, UiCulture.Normalize(requested));

    [Fact]
    public async Task Comment_edits_use_the_audited_comment_endpoint()
    {
        var handler = new CaptureHandler();
        var api = new ApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") });

        using var response = await api.EditComment(Guid.Parse("11111111-1111-1111-1111-111111111111"), "updated", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Put, handler.Request!.Method);
        Assert.Equal("https://example.test/api/comments/11111111-1111-1111-1111-111111111111", handler.Request.RequestUri!.ToString());
        Assert.Contains("updated", handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cloudflare_logout_navigates_the_top_level_browser_to_the_Access_logout_endpoint()
    {
        var handler = new LogoutHandler("/cdn-cgi/access/logout");
        var js = new CaptureJsRuntime();
        var state = new AuthState(new ApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") }), js);

        await state.LogoutAsync();

        Assert.Equal("hc.navigate", js.Identifier);
        Assert.Equal("/cdn-cgi/access/logout", js.Argument);
    }

    [Fact]
    public async Task Cookie_logout_does_not_redirect_to_Cloudflare()
    {
        var js = new CaptureJsRuntime();
        var state = new AuthState(new ApiClient(new HttpClient(new LogoutHandler(null)) { BaseAddress = new Uri("https://example.test/") }), js);

        await state.LogoutAsync();

        Assert.Null(js.Identifier);
    }

    [Fact]
    public void Client_publish_loads_globalization_data_for_runtime_culture_selection()
    {
        var project = File.ReadAllText(Path.GetFullPath("../../../../../src/Client/HouseConsensus.Client.csproj", AppContext.BaseDirectory));
        Assert.Contains("<BlazorWebAssemblyLoadAllGlobalizationData>true</BlazorWebAssemblyLoadAllGlobalizationData>", project, StringComparison.Ordinal);
    }

    [Fact]
    public void Authentication_shell_exposes_stable_browser_contract()
    {
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var auth = File.ReadAllText(Path.Combine(root, "src/Client/Components/AuthGate.razor"));
        var layout = File.ReadAllText(Path.Combine(root, "src/Client/Layout/MainLayout.razor"));
        var members = File.ReadAllText(Path.Combine(root, "src/Client/Pages/Owner/Members.razor"));
        Assert.Contains("data-testid=\"auth-email\"", auth, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"auth-link-sent\"", auth, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"app-shell\"", layout, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"current-user-email\"", layout, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"language-select\"", layout, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"member-invite-email\"", members, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"member-notice\"", members, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"member-row\"", members, StringComparison.Ordinal);
    }

    [Fact]
    public void Listing_and_feedback_flows_expose_stable_browser_contract()
    {
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var sources = string.Join("\n", new[]
        {
            "src/Client/Components/ListingCard.razor", "src/Client/Components/VoteButtons.razor",
            "src/Client/Components/ListingFilterDrawer.razor",
            "src/Client/Pages/Browse.razor", "src/Client/Pages/Detail.razor",
            "src/Client/Pages/Owner/Review.razor", "src/Client/Components/FeedbackButton.razor",
            "src/Client/Pages/Owner/Feedback.razor"
        }.Select(path => File.ReadAllText(Path.Combine(root, path))));
        foreach (var testId in new[] { "listing-card", "vote-interested", "filter-price-max", "filter-apply", "browse-map", "unanimity-status", "match-banner", "restore-listing", "feedback-message", "feedback-success", "feedback-export-csv", "feedback-export-json" })
            Assert.Contains($"data-testid=\"{testId}\"", sources, StringComparison.Ordinal);
    }

    [Fact]
    public void Voting_uses_optional_note_sheet_and_has_no_clear_action()
    {
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var vote = File.ReadAllText(Path.Combine(root, "src/Client/Components/VoteButtons.razor"));
        var api = File.ReadAllText(Path.Combine(root, "src/Client/Services/ApiClient.cs"));
        var program = File.ReadAllText(Path.Combine(root, "src/Server/Program.cs"));
        Assert.Contains("vote-note-sheet", vote, StringComparison.Ordinal);
        Assert.Contains("vote-note", vote, StringComparison.Ordinal);
        Assert.Contains("vote-skip-comment", vote, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearVote", vote, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearVote", api, StringComparison.Ordinal);
        Assert.DoesNotContain("listings.MapDelete", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Vote_api_rejects_legacy_clear_choice()
    {
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var program = File.ReadAllText(Path.Combine(root, "src/Server/Program.cs"));
        Assert.Contains("request.Choice == VoteChoice.NotVoted", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Owner_review_queries_only_unresolved_ai_rejections()
    {
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var program = File.ReadAllText(Path.Combine(root, "src/Server/Program.cs"));
        var reviewQuery = program[(program.IndexOf("review.MapGet", StringComparison.Ordinal))..program.IndexOf("review.MapPost", StringComparison.Ordinal)];
        Assert.Contains("x.State == ListingState.AiRejected", reviewQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("FilterRejected", reviewQuery, StringComparison.Ordinal);
        Assert.DoesNotContain("ManuallyRejected", reviewQuery, StringComparison.Ordinal);
    }

    [Fact]
    public void Browse_and_my_votes_share_full_persisted_filter_contract()
    {
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var drawer = File.ReadAllText(Path.Combine(root, "src/Client/Components/ListingFilterDrawer.razor"));
        var browse = File.ReadAllText(Path.Combine(root, "src/Client/Pages/Browse.razor"));
        var votes = File.ReadAllText(Path.Combine(root, "src/Client/Pages/MyVotes.razor"));
        var js = File.ReadAllText(Path.Combine(root, "src/Client/wwwroot/js/app.js"));
        foreach (var id in new[] { "filter-area-min", "filter-garden-min", "filter-rooms-min", "filter-year-min", "filter-commute-max", "filter-expense-max", "filter-days-max", "filter-score-min", "filter-privacy-min", "filter-preferred", "filter-quiet", "filter-new", "filter-ai-plan" })
            Assert.Contains(id, drawer, StringComparison.Ordinal);
        foreach (var category in new[] { "Municipalities", "MultigenFits", "BuildableStatuses", "Conditions", "EnergyLabels", "GardenOrientations", "FamilyUnits" })
            Assert.Contains(category, drawer, StringComparison.Ordinal);
        foreach (var sort in new[] { "GardenHigh", "YearNewest", "CommuteFastest", "NewFirst" }) Assert.Contains(sort, File.ReadAllText(Path.Combine(root, "src/Client/Services/ListingFilterState.cs")), StringComparison.Ordinal);
        Assert.Contains("hc.filters.browse", browse, StringComparison.Ordinal);
        Assert.Contains("hc.filters.myvotes", votes, StringComparison.Ordinal);
        Assert.Contains("saveState", js, StringComparison.Ordinal);
        Assert.Contains("loadState", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_uses_imported_coordinates_and_rich_popups_without_browser_geocoding()
    {
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var browse = File.ReadAllText(Path.Combine(root, "src/Client/Pages/Browse.razor"));
        var js = File.ReadAllText(Path.Combine(root, "src/Client/wwwroot/js/app.js"));
        Assert.Contains("x.Latitude.HasValue && x.Longitude.HasValue", browse, StringComparison.Ordinal);
        Assert.Contains("listing.latitude", js, StringComparison.Ordinal);
        Assert.Contains("listing.image", js, StringComparison.Ordinal);
        Assert.Contains("scrollWheelZoom: true", js, StringComparison.Ordinal);
        Assert.Contains("listing.score", js, StringComparison.Ordinal);
        Assert.DoesNotContain("nominatim", js, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("setTimeout(resolve, 1100)", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Owner_feedback_exposes_versioned_ai_rule_proposals_and_impact_actions()
    {
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var feedback = File.ReadAllText(Path.Combine(root, "src/Client/Pages/Owner/Feedback.razor"));
        var program = File.ReadAllText(Path.Combine(root, "src/Server/Program.cs"));
        foreach (var token in new[] { "learning-generate", "learning-impact", "learning-approve", "learning-reject", "learning-deactivate" }) Assert.Contains(token, feedback, StringComparison.Ordinal);
        foreach (var endpoint in new[] { "/learning/proposals", "/learning/{id:guid}/approve", "/learning/{id:guid}/reject", "/learning/{id:guid}/deactivate" }) Assert.Contains(endpoint, program, StringComparison.Ordinal);
    }

    [Fact]
    public void House_cards_surface_photos_property_facts_and_fit_signals()
    {
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var card = File.ReadAllText(Path.Combine(root, "src/Client/Components/ListingCard.razor"))
            + File.ReadAllText(Path.Combine(root, "src/Client/Components/PropertyFacts.razor"));
        var detail = File.ReadAllText(Path.Combine(root, "src/Client/Pages/Detail.razor"));
        var css = File.ReadAllText(Path.Combine(root, "src/Client/wwwroot/css/app.css"));

        Assert.Contains("card-image", card, StringComparison.Ordinal);
        Assert.Contains("property-facts", card, StringComparison.Ordinal);
        Assert.Contains("fit-signals", card, StringComparison.Ordinal);
        Assert.Contains("detail-cover", detail, StringComparison.Ordinal);
        Assert.Contains("aspect-ratio: 3/2", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Dislike_vote_mark_has_no_extra_bottom_leading()
    {
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var card = File.ReadAllText(Path.Combine(root, "src/Client/Components/ListingCard.razor"));
        var css = File.ReadAllText(Path.Combine(root, "src/Client/wwwroot/css/app.css"));

        Assert.Contains("vote-symbol", card, StringComparison.Ordinal);
        Assert.Contains(".vote-dots .vote-symbol", css, StringComparison.Ordinal);
        Assert.Contains("line-height: 1", css, StringComparison.Ordinal);
        Assert.Contains(".vote-dots .dislike .vote-symbol", css, StringComparison.Ordinal);
        Assert.Contains("translateY(1.5px)", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Score_chip_is_compact_and_explains_the_weighted_breakdown()
    {
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var card = File.ReadAllText(Path.Combine(root, "src/Client/Components/ListingCard.razor"));
        var css = File.ReadAllText(Path.Combine(root, "src/Client/wwwroot/css/app.css"));
        var seed = File.ReadAllText(Path.Combine(root, "src/Server/Data/E2EDataSeeder.cs"));

        Assert.Contains("<button type=\"button\" class=\"score-chip\"", card, StringComparison.Ordinal);
        Assert.Contains("aria-describedby=\"@TooltipId\"", card, StringComparison.Ordinal);
        Assert.Contains("id=\"@TooltipId\"", card, StringComparison.Ordinal);
        Assert.Contains("score-tooltip", card, StringComparison.Ordinal);
        Assert.Contains("Listing.FamilyPrivacyScore", card, StringComparison.Ordinal);
        Assert.Contains("Listing.KidsSpaceScore", card, StringComparison.Ordinal);
        Assert.Contains("Listing.GardenScore", card, StringComparison.Ordinal);
        Assert.Contains("Listing.SharedLivingScore", card, StringComparison.Ordinal);
        Assert.Contains("Listing.PracticalScore", card, StringComparison.Ordinal);
        Assert.Contains("Listing.FamilyPrivacyWeight", card, StringComparison.Ordinal);
        Assert.Contains("Listing.PracticalWeight", card, StringComparison.Ordinal);
        Assert.Contains("FamilyPrivacyScore =", seed, StringComparison.Ordinal);
        Assert.Contains("PracticalScore =", seed, StringComparison.Ordinal);
        Assert.Contains("FamilyPrivacyWeight =", seed, StringComparison.Ordinal);
        Assert.Contains("PracticalWeight =", seed, StringComparison.Ordinal);
        Assert.Matches(@"\.score-chip\s*\{[^}]*height:\s*30px", css);
        Assert.Contains(".score-chip:focus-within .score-tooltip", css, StringComparison.Ordinal);
    }


    [Fact]
    public void Detail_and_vote_cards_surface_commute_readable_evidence_and_unclipped_notes()
    {
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var facts = File.ReadAllText(Path.Combine(root, "src/Client/Components/PropertyFacts.razor"));
        var detail = File.ReadAllText(Path.Combine(root, "src/Client/Pages/Detail.razor"));
        var votes = File.ReadAllText(Path.Combine(root, "src/Client/Pages/MyVotes.razor"));
        var listingLink = File.ReadAllText(Path.Combine(root, "src/Client/Components/ExternalListingLink.razor"));
        var css = File.ReadAllText(Path.Combine(root, "src/Client/wwwroot/css/app.css"));

        Assert.Contains("AiEvidencePanel", detail, StringComparison.Ordinal);
        Assert.Contains("uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps", listingLink, StringComparison.Ordinal);
        Assert.Contains("liveSubscription?.Dispose()", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("comment-form", detail, StringComparison.Ordinal);
        Assert.Contains("my-vote-current", votes, StringComparison.Ordinal);
        Assert.Matches(@"\.household-votes \.vote-note-excerpt\s*\{[^}]*grid-column:\s*2/-1", css);
        Assert.Matches(@"\.my-vote-copy\s*\{[^}]*min-width:\s*0", css);
    }

    [Fact]
    public void Cards_and_review_show_full_commute_safe_listing_links_and_readable_ai_reasons()
    {
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var card = File.ReadAllText(Path.Combine(root, "src/Client/Components/ListingCard.razor"));
        var commute = File.ReadAllText(Path.Combine(root, "src/Client/Components/CommuteTable.razor"));
        var link = File.ReadAllText(Path.Combine(root, "src/Client/Components/ExternalListingLink.razor"));
        var review = File.ReadAllText(Path.Combine(root, "src/Client/Pages/Owner/Review.razor"));
        var detail = File.ReadAllText(Path.Combine(root, "src/Client/Pages/Detail.razor"));
        var votes = File.ReadAllText(Path.Combine(root, "src/Client/Pages/MyVotes.razor"));
        var contracts = File.ReadAllText(Path.Combine(root, "src/Shared/Contracts.cs"));
        var facts = File.ReadAllText(Path.Combine(root, "src/Client/Components/PropertyFacts.razor"));
        var css = File.ReadAllText(Path.Combine(root, "src/Client/wwwroot/css/app.css"));

        Assert.Contains("CommuteJson", contracts, StringComparison.Ordinal);
        Assert.Contains("CommuteTable", card, StringComparison.Ordinal);
        Assert.Contains("CommuteTable", detail, StringComparison.Ordinal);
        Assert.Contains("CommuteTable", review, StringComparison.Ordinal);
        Assert.Contains("CommuteTable", votes, StringComparison.Ordinal);
        Assert.Contains("ExternalListingLink", card, StringComparison.Ordinal);
        Assert.Contains("ExternalListingLink", review, StringComparison.Ordinal);
        Assert.Contains("AiEvidencePanel", review, StringComparison.Ordinal);
        Assert.Contains("PropertyFacts", review, StringComparison.Ordinal);
        Assert.Contains("public", commute, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bike", commute, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("car", commute, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Uri.UriSchemeHttp", link, StringComparison.Ordinal);
        Assert.Contains("Uri.UriSchemeHttps", link, StringComparison.Ordinal);
        Assert.Contains("target=\"_blank\"", link, StringComparison.Ordinal);
        Assert.DoesNotContain("Listing.CommuteMinutes", facts, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\.commute-table\s*\{[^}]*overflow-x:\s*auto", css);
        Assert.DoesNotMatch(@"\.commute-grid\s*\{[^}]*min-width:\s*(350|390)px", css);
    }

    [Fact]
    public void E2e_routes_and_seed_data_are_disabled_in_production()
    {
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var program = File.ReadAllText(Path.Combine(root, "src/Server/Program.cs"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(program, "!app.Environment.IsProduction\\(\\) && app.Configuration.GetValue\\(\"E2E:SeedData\", false\\)").Count);
    }

    [Fact]
    public void Browse_field_migration_has_valid_rollback_sql()
    {
        var root = Path.GetFullPath("../../../../../", AppContext.BaseDirectory);
        var migration = File.ReadAllText(Path.Combine(root, "src/Server/Data/Migrations/202607220002_AddBrowseFields.cs"));
        Assert.Contains("ALTER TABLE listings DROP COLUMN \"MultigenFit\"", migration, StringComparison.Ordinal);
    }

    private sealed class CloudflareUnauthenticatedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/api/auth/mode", StringComparison.Ordinal) == true)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"cloudflareAccess\":true}", System.Text.Encoding.UTF8, "application/json") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }

    private sealed class LogoutHandler(string? location) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.NoContent);
            if (location is not null) response.Headers.Add("X-House-Consensus-Logout", location);
            return Task.FromResult(response);
        }
    }

    private sealed class CaptureJsRuntime : IJSRuntime
    {
        public string? Identifier { get; private set; }
        public string? Argument { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Identifier = identifier;
            Argument = args?.FirstOrDefault()?.ToString();
            return ValueTask.FromResult(default(TValue)!);
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
