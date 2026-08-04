using Xunit;
using HouseConsensus.Server.Auth;
using HouseConsensus.Server.Data;
using HouseConsensus.Server.Learning;
using HouseConsensus.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Npgsql;
using Testcontainers.PostgreSql;
namespace HouseConsensus.IntegrationTests;

public sealed class PostgresLifecycleTests : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private string _connectionString = "";
    private AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString, n => n.MapEnum<MemberRole>("member_role").MapEnum<VoteChoice>("vote_choice").MapEnum<ListingState>("listing_state").MapEnum<ReasonTag>("reason_tag").MapEnum<OverrideAction>("override_action").MapEnum<CategoryRating>("category_rating").MapEnum<VoteCategory>("vote_category")).Options);
    public async ValueTask InitializeAsync()
    {
        _connectionString = Environment.GetEnvironmentVariable("HOUSE_CONSENSUS_TEST_DATABASE_URL") ?? "";
        var externalDatabase = !string.IsNullOrWhiteSpace(_connectionString);
        if (!externalDatabase)
        {
            _postgres = new PostgreSqlBuilder().WithImage("postgres:17-alpine").WithDatabase("hc").WithUsername("hc").WithPassword("hc-test-password").Build();
            await _postgres.StartAsync();
            _connectionString = _postgres.GetConnectionString();
        }
        if (externalDatabase)
        {
            var database = new NpgsqlConnectionStringBuilder(_connectionString).Database;
            if (string.IsNullOrWhiteSpace(database) || !database.Contains("test", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("External integration database name must contain 'test'.");
            await using var reset = new NpgsqlConnection(_connectionString);
            await reset.OpenAsync();
            await using var command = new NpgsqlCommand("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;", reset);
            await command.ExecuteNonQueryAsync();
        }
        await using var db = Db();
        await db.Database.MigrateAsync();
        await db.Database.OpenConnectionAsync();
        await ((NpgsqlConnection)db.Database.GetDbConnection()).ReloadTypesAsync();
    }
    public async ValueTask DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }
    [Fact]
    public async Task Migration_constraints_vote_history_override_and_archive_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var db = Db())
        { var owner = new Member { Email = "owner@example.test", Role = MemberRole.Owner }; var member = new Member { Email = "member@example.test" }; var listing = new Listing { ExternalId = "case-1", Address = "Example 1", FamilyFitScore = 0.9 }; db.AddRange(owner, member, listing); await db.SaveChangesAsync(ct); db.Votes.AddRange(new Vote { ListingId = listing.Id, MemberId = member.Id, Choice = VoteChoice.Dislike }, new Vote { ListingId = listing.Id, MemberId = member.Id, Choice = VoteChoice.Like, Tags = [ReasonTag.PrivacyFromNeighbors], CreatedAt = DateTimeOffset.UtcNow.AddSeconds(1) }); listing.ApplyOverride(OverrideAction.Restore, owner.Id, "reviewed", DateTimeOffset.UtcNow); var comment = new Comment(listing.Id, member.Id, "original", DateTimeOffset.UtcNow); comment.Edit(member.Id, false, "revised", DateTimeOffset.UtcNow.AddSeconds(1)); comment.Delete(owner.Id, true, DateTimeOffset.UtcNow.AddSeconds(2)); db.Comments.Add(comment); db.Feedback.Add(new Feedback { MemberId = member.Id, ListingId = listing.Id, Body = "wrong score", ReviewedAt = DateTimeOffset.UtcNow }); member.Deactivate(); listing.Archive(DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); }
        await using (var db = Db()) { var listing = await db.Listings.Include(x => x.Overrides).SingleAsync(ct); var votes = await db.Votes.OrderBy(x => x.CreatedAt).ToListAsync(ct); Assert.Equal(ListingState.Archived, listing.State); Assert.Single(listing.Overrides); Assert.Equal(2, votes.Count); Assert.Contains(ReasonTag.PrivacyFromNeighbors, votes.Last().Tags); Assert.Equal(VoteChoice.Like, ConsensusRules.LatestVotes(votes).Single().Value.Choice); var comment = await db.Comments.Include(x => x.Revisions).SingleAsync(ct); Assert.True(comment.IsDeleted); Assert.Equal(2, comment.Revisions.Count); Assert.False((await db.Members.SingleAsync(x => x.Role == MemberRole.Member, ct)).IsActive); Assert.NotNull((await db.Feedback.SingleAsync(ct)).ReviewedAt); }
    }
    [Fact]
    public async Task Debug_auto_login_authenticates_the_active_configured_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = Db();
        var owner = new Member { Email = "debug-owner@example.test", Role = MemberRole.Owner };
        db.Members.Add(owner);
        await db.SaveChangesAsync(ct);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["INITIAL_OWNER_EMAIL"] = owner.Email,
        }).Build();
        var context = new DefaultHttpContext { RequestAborted = ct };
        var nextCalled = false;
        var middleware = new DebugAutoLoginMiddleware(_ => { nextCalled = true; return Task.CompletedTask; }, config);

        await middleware.InvokeAsync(context, db);

        Assert.True(nextCalled);
        Assert.True(context.User.Identity?.IsAuthenticated);
        Assert.Equal(owner.Id.ToString(), context.User.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.True(context.User.IsInRole(MemberRole.Owner.ToString()));
    }

    [Fact]
    public async Task Debug_auto_login_rejects_non_owner_inactive_owner_and_missing_member()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = Db();
        var member = new Member { Email = "debug-member@example.test", Role = MemberRole.Member };
        var inactiveOwner = new Member { Email = "debug-inactive@example.test", Role = MemberRole.Owner };
        inactiveOwner.Deactivate();
        db.Members.AddRange(member, inactiveOwner);
        await db.SaveChangesAsync(ct);

        foreach (var email in new[] { member.Email, inactiveOwner.Email, "debug-missing@example.test" })
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["INITIAL_OWNER_EMAIL"] = email,
            }).Build();
            var context = new DefaultHttpContext { RequestAborted = ct };
            var middleware = new DebugAutoLoginMiddleware(_ => Task.CompletedTask, config);

            await middleware.InvokeAsync(context, db);

            Assert.False(context.User.Identity?.IsAuthenticated);
        }
    }

    [Fact]
    public void Debug_auto_login_cannot_be_enabled_outside_development()
    {
        Assert.Throws<InvalidOperationException>(() => DebugAutoLoginMiddleware.EnsureSafe(true, "Production"));
        DebugAutoLoginMiddleware.EnsureSafe(true, "Development");
    }

    [Fact]
    public async Task E2E_test_auth_provisions_an_uninvited_cloudflare_identity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = Db();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["E2E:TestAuth"] = "true"
        }).Build();
        var context = new DefaultHttpContext { RequestAborted = ct };
        context.Request.Headers[DebugAutoLoginMiddleware.E2EEmailHeader] = "allowed@example.test";
        context.RequestServices = new ServiceCollection()
            .AddSingleton<ICloudflareMemberService>(new CloudflareMemberService(db))
            .BuildServiceProvider();
        var middleware = new DebugAutoLoginMiddleware(_ => Task.CompletedTask, config);

        await middleware.InvokeAsync(context, db);

        Assert.True(context.User.Identity?.IsAuthenticated);
        var member = await db.Members.SingleAsync(x => x.Email == "allowed@example.test", ct);
        Assert.True(member.IsActive);
        Assert.Equal(MemberRole.Member, member.Role);
    }

    [Fact]
    public async Task Cloudflare_login_reactivates_a_returning_allowed_identity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = Db();
        var member = new Member { Email = "returning@example.test", Role = MemberRole.Member };
        member.Deactivate();
        db.Members.Add(member);
        await db.SaveChangesAsync(ct);

        var resolved = await new CloudflareMemberService(db).ResolveAsync(member.Email, ct);

        Assert.NotNull(resolved);
        Assert.True(resolved.IsActive);
        Assert.True((await db.Members.SingleAsync(x => x.Id == member.Id, ct)).IsActive);
    }

    [Fact]
    public async Task E2E_test_auth_header_authenticates_an_active_member()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = Db();
        var member = new Member { Email = "e2e-member@example.test", DisplayName = "E2E Member" };
        db.Members.Add(member);
        await db.SaveChangesAsync(ct);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["E2E:TestAuth"] = "true",
        }).Build();
        var context = new DefaultHttpContext { RequestAborted = ct };
        context.Request.Headers["X-House-Consensus-E2E-Email"] = member.Email;
        var middleware = new DebugAutoLoginMiddleware(_ => Task.CompletedTask, config);

        await middleware.InvokeAsync(context, db);

        Assert.True(context.User.Identity?.IsAuthenticated);
        Assert.Equal(member.Id.ToString(), context.User.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.True(context.User.IsInRole(MemberRole.Member.ToString()));
    }

    [Fact]
    public void E2E_test_auth_requires_development_and_debug_auto_login()
    {
        Assert.Throws<InvalidOperationException>(() => DebugAutoLoginMiddleware.EnsureE2ETestAuthSafe(true, true, true, "Production"));
        Assert.Throws<InvalidOperationException>(() => DebugAutoLoginMiddleware.EnsureE2ETestAuthSafe(true, false, true, "Development"));
        Assert.Throws<InvalidOperationException>(() => DebugAutoLoginMiddleware.EnsureE2ETestAuthSafe(true, true, false, "Development"));
        DebugAutoLoginMiddleware.EnsureE2ETestAuthSafe(true, true, true, "Development");
        DebugAutoLoginMiddleware.EnsureE2ETestAuthSafe(false, false, false, "Production");
    }

    [Fact]
    public async Task E2E_household_reset_removes_listing_votes_and_restores_member_defaults()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = Db();
        await E2EDataSeeder.SeedAsync(db, ct);
        var member = await db.Members.SingleAsync(x => x.Email == E2EDataSeeder.MemberEmail, ct);
        var transient = new Member { Email = "playwright-worker@example.test" };
        db.Members.Add(transient);
        var listing = await db.Listings.SingleAsync(x => x.ExternalId == "e2e-active", ct);
        member.SetLanguage("da");
        member.Deactivate();
        db.Votes.Add(new Vote(listing.Id, member.Id, VoteChoice.Like, [], "residue", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(ct);

        await E2EDataSeeder.ResetHouseholdVotesAsync(db, ct);

        await db.Entry(member).ReloadAsync(ct);
        Assert.True(member.IsActive);
        Assert.Equal("en", member.Language);
        Assert.Equal("E2E Member", member.DisplayName);
        Assert.False(await db.Members.AnyAsync(x => x.Id == transient.Id, ct));
        Assert.False(await db.Votes.AnyAsync(x => x.ListingId == listing.Id, ct));
    }

    [Fact]
    public async Task E2E_ai_generator_is_deterministic_and_network_free()
    {
        var generated = await new E2EAiRuleGenerator().GenerateAsync([], CancellationToken.None);
        Assert.Equal("E2E deterministic proposal", generated.Summary);
        Assert.Equal("all", System.Text.Json.JsonDocument.Parse(generated.RuleJson).RootElement.GetProperty("combinator").GetString());
    }

    [Fact]
    public async Task E2E_seed_creates_one_active_test_member_idempotently()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = Db();

        await E2EDataSeeder.SeedAsync(db, ct);
        await E2EDataSeeder.SeedAsync(db, ct);

        var members = await db.Members.Where(x => x.Email == E2EDataSeeder.MemberEmail).ToListAsync(ct);
        Assert.Single(members);
        Assert.True(members[0].IsActive);
        Assert.Equal("E2E Member", members[0].DisplayName);
        var owners = await db.Members.Where(x => x.Email == E2EDataSeeder.OwnerEmail).ToListAsync(ct);
        Assert.Single(owners);
        Assert.True(owners[0].IsActive);
        Assert.Equal(MemberRole.Owner, owners[0].Role);
    }

    [Fact]
    public async Task Magic_link_is_invite_only_expires_and_is_single_use()
    {
        var ct = TestContext.Current.CancellationToken;
        var mail = new CaptureEmail(); var now = new ManualTimeProvider(DateTimeOffset.UtcNow); var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { { "PublicOrigin", "https://example.test" } }).Build(); Guid inviter;
        await using (var db = Db()) { var owner = new Member { Email = "owner@example.test", Role = MemberRole.Owner }; db.Members.Add(owner); await db.SaveChangesAsync(ct); inviter = owner.Id; db.Invites.Add(new Invite { Email = "new@example.test", InvitedById = inviter, ExpiresAt = now.GetUtcNow().AddDays(1) }); await db.SaveChangesAsync(ct); var service = new MagicLinkService(db, mail, cfg, now); await service.RequestAsync("NEW@example.test", ct); }
        var token = new Uri(mail.Link).Query.Split("token=")[1];
        await using (var db = Db()) { var service = new MagicLinkService(db, mail, cfg, now); var member = await service.ConsumeAsync(Uri.UnescapeDataString(token), ct); Assert.NotNull(member); Assert.Equal("new@example.test", member.Email); Assert.Null(await service.ConsumeAsync(Uri.UnescapeDataString(token), ct)); }
    }
    [Fact]
    public async Task Unknown_and_expired_invites_do_not_create_magic_links()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var mail = new CaptureEmail();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { { "PublicOrigin", "https://example.test" } }).Build();
        await using var db = Db();
        var owner = new Member { Email = "owner@example.test", Role = MemberRole.Owner };
        db.Members.Add(owner);
        await db.SaveChangesAsync(ct);
        db.Invites.Add(new Invite { Email = "expired@example.test", InvitedById = owner.Id, ExpiresAt = now.GetUtcNow() });
        await db.SaveChangesAsync(ct);
        var service = new MagicLinkService(db, mail, cfg, now);

        await service.RequestAsync("unknown@example.test", ct);
        await service.RequestAsync("expired@example.test", ct);

        Assert.Equal(0, await db.MagicLinks.CountAsync(ct));
        Assert.Equal(0, mail.SendCount);
    }

    [Fact]
    public async Task Expired_magic_link_cannot_accept_invite()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var mail = new CaptureEmail();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { { "PublicOrigin", "https://example.test" } }).Build();
        await using var db = Db();
        var owner = new Member { Email = "owner@example.test", Role = MemberRole.Owner };
        db.Members.Add(owner);
        await db.SaveChangesAsync(ct);
        var invite = new Invite { Email = "invitee@example.test", InvitedById = owner.Id, ExpiresAt = now.GetUtcNow().AddDays(1) };
        db.Invites.Add(invite);
        await db.SaveChangesAsync(ct);
        var service = new MagicLinkService(db, mail, cfg, now);
        await service.RequestAsync(invite.Email, ct);
        var token = Uri.UnescapeDataString(new Uri(mail.Link).Query.Split("token=")[1]);
        now.Advance(TimeSpan.FromMinutes(15));

        Assert.Null(await service.ConsumeAsync(token, ct));
        Assert.Null((await db.Invites.SingleAsync(ct)).AcceptedAt);
        Assert.False(await db.Members.AnyAsync(x => x.Email == invite.Email, ct));
    }

    [Fact]
    public async Task Vote_tags_and_latest_choice_round_trip_through_PostgreSQL()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = Db();
        var member = new Member { Email = "member@example.test" };
        var listing = new Listing { ExternalId = "vote-case", Address = "Vote Street 1" };
        db.AddRange(member, listing);
        await db.SaveChangesAsync(ct);
        var at = DateTimeOffset.UtcNow;
        var firstVote = new Vote(listing.Id, member.Id, VoteChoice.Dislike, [ReasonTag.Noise, ReasonTag.Price], "too noisy", at);
        firstVote.EditNote(member.Id, "road noise", at.AddMilliseconds(500));
        db.Votes.AddRange(
            firstVote,
            new Vote(listing.Id, member.Id, VoteChoice.Like, [ReasonTag.Garden], null, at.AddSeconds(1)));
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        var history = await db.Votes.Include(x => x.NoteRevisions).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        Assert.Equal([ReasonTag.Noise, ReasonTag.Price], history[0].Tags);
        Assert.Equal("road noise", history[0].Note);
        Assert.Equal("too noisy", Assert.Single(history[0].NoteRevisions).PreviousNote);
        Assert.Equal(VoteChoice.Like, ConsensusRules.LatestVotes(history)[member.Id].Choice);
        Assert.True(ConsensusRules.HasConsensus([member.Id], history));
    }

    [Fact]
    public async Task Comment_revision_audit_round_trips_actor_body_and_deletion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = Db();
        var author = new Member { Email = "author@example.test" };
        var owner = new Member { Email = "owner@example.test", Role = MemberRole.Owner };
        var listing = new Listing { ExternalId = "comment-case", Address = "Comment Street 1" };
        db.AddRange(author, owner, listing);
        await db.SaveChangesAsync(ct);
        var at = DateTimeOffset.UtcNow;
        var comment = new Comment(listing.Id, author.Id, "first", at);
        comment.Edit(author.Id, false, "second", at.AddSeconds(1));
        comment.Delete(owner.Id, true, at.AddSeconds(2));
        db.Comments.Add(comment);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        var saved = await db.Comments.Include(x => x.Revisions).SingleAsync(ct);
        var revisions = saved.Revisions.OrderBy(x => x.ChangedAt).ToArray();
        Assert.True(saved.IsDeleted);
        Assert.Equal("", saved.Body);
        Assert.Equal("first", revisions[0].PreviousBody);
        Assert.Equal(author.Id, revisions[0].ActorId);
        Assert.Equal("second", revisions[1].PreviousBody);
        Assert.Equal(owner.Id, revisions[1].ActorId);
        Assert.True(revisions[1].WasDeletion);
    }

    [Fact]
    public async Task Listing_filter_and_map_fields_round_trip_through_PostgreSQL()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = Db();
        var listing = new Listing
        {
            ExternalId = "filter-map-case", Address = "Map Street 1",
            Latitude = 55.7, Longitude = 12.4, MonthlyExpense = 5_244,
            DaysOnMarket = 18, CommuteMinutes = 22,
            BuildableStatus = "extra_house", Condition = "good",
            GardenOrientation = "southwest", MultigenFit = "likely",
            PostalCode = "4000", Preferred = true, IsNew = true, FamilyUnits = "two_family"
        };
        db.Listings.Add(listing);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        var saved = await db.Listings.SingleAsync(x => x.ExternalId == "filter-map-case", ct);
        Assert.Equal(55.7, saved.Latitude);
        Assert.Equal(12.4, saved.Longitude);
        Assert.Equal(5_244, saved.MonthlyExpense);
        Assert.Equal(18, saved.DaysOnMarket);
        Assert.Equal(22, saved.CommuteMinutes);
        Assert.Equal("extra_house", saved.BuildableStatus);
        Assert.Equal("good", saved.Condition);
        Assert.Equal("southwest", saved.GardenOrientation);
        Assert.Equal("likely", saved.MultigenFit);
        Assert.Equal("4000", saved.PostalCode);
        Assert.True(saved.Preferred);
        Assert.True(saved.IsNew);
        Assert.Equal("two_family", saved.FamilyUnits);
    }

    [Fact]
    public async Task Ai_rule_proposal_and_learning_rejection_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = Db();
        var owner = new Member { Email = "learning-owner@example.test", Role = MemberRole.Owner };
        var listing = new Listing { ExternalId = "learning-case", Address = "Learning 1", Condition = "poor", AiConfidence = 1.0 };
        db.AddRange(owner, listing);
        await db.SaveChangesAsync(ct);
        var proposal = new AiRuleProposal(owner.Id, 1, "Avoid poor condition", """{"combinator":"all","conditions":[{"field":"condition","operator":"eq","value":"poor"}]}""", "[]", """{"eligible":1,"wouldReject":1}""", DateTimeOffset.UtcNow);
        proposal.Approve(owner.Id, DateTimeOffset.UtcNow);
        Assert.True(AiLearningRules.Apply(listing, false, proposal.VersionLabel, proposal.RuleJson));
        db.AiRuleProposals.Add(proposal);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        var saved = await db.AiRuleProposals.SingleAsync(ct);
        Assert.True(saved.IsActive);
        Assert.Equal("feedback-v1", (await db.Listings.SingleAsync(x => x.Id == listing.Id, ct)).LearningRuleVersion);
    }

    [Fact]
    public async Task Owner_triggered_proposal_previews_and_applies_only_eligible_matches()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = Db();
        var owner = new Member { Email = "proposal-owner@example.test", Role = MemberRole.Owner };
        var source = new Listing { ExternalId = "note-source", Address = "Source", Condition = "poor", AiConfidence = 1.0 };
        var target = new Listing { ExternalId = "rule-target", Address = "Target", Condition = "poor", AiConfidence = 1.0 };
        var safe = new Listing { ExternalId = "rule-safe", Address = "Safe", Condition = "good", AiConfidence = 1.0 };
        var baselineRejected = new Listing { ExternalId = "rule-reconsider", Address = "Reconsider", Condition = "good", AiConfidence = 1.0 };
        baselineRejected.ApplyImportDecision(true);
        db.AddRange(owner, source, target, safe, baselineRejected); await db.SaveChangesAsync(ct);
        db.Votes.Add(new Vote(source.Id, owner.Id, VoteChoice.Dislike, [ReasonTag.Condition], "Too much renovation", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(ct);
        var service = new AiLearningService(db, new FakeRuleGenerator(), TimeProvider.System);

        var proposal = await service.CreateProposalAsync(owner.Id, ct);
        Assert.Contains("wouldReject", proposal.ImpactPreviewJson, StringComparison.Ordinal);
        await service.ApproveAsync(proposal.Id, owner.Id, ct);

        Assert.Equal(ListingState.AiRejected, (await db.Listings.FindAsync([target.Id], ct))!.State);
        Assert.Equal(ListingState.Active, (await db.Listings.FindAsync([safe.Id], ct))!.State);
        Assert.Equal(ListingState.Active, (await db.Listings.FindAsync([source.Id], ct))!.State);
        Assert.Equal(ListingState.Active, (await db.Listings.FindAsync([baselineRejected.Id], ct))!.State);
        Assert.Equal(proposal.VersionLabel, (await db.Listings.FindAsync([baselineRejected.Id], ct))!.LearningRuleVersion);

        var approvedAt = proposal.ReviewedAt;
        target.ApplyOverride(OverrideAction.Restore, owner.Id, "Owner decision", DateTimeOffset.UtcNow);
        await db.SaveChangesAsync(ct);
        await service.DeactivateAsync(proposal.Id, owner.Id, ct);
        Assert.Equal(ListingState.AiRejected, (await db.Listings.FindAsync([baselineRejected.Id], ct))!.State);
        Assert.Null((await db.Listings.FindAsync([baselineRejected.Id], ct))!.LearningRuleVersion);
        Assert.Equal(ListingState.Restored, (await db.Listings.FindAsync([target.Id], ct))!.State);
        Assert.Null((await db.Listings.FindAsync([target.Id], ct))!.LearningRuleVersion);
        Assert.Equal(approvedAt, proposal.ReviewedAt);
        Assert.Equal(["approved", "deactivated"], await db.AiRuleProposalActions.Where(x => x.ProposalId == proposal.Id).OrderBy(x => x.CreatedAt).Select(x => x.Action).ToArrayAsync(ct));
    }

    [Fact]
    public async Task Deactivating_a_replacement_reactivates_the_previous_version()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = Db();
        var owner = new Member { Email = "versions-owner@example.test", Role = MemberRole.Owner };
        var source = new Listing { ExternalId = "versions-source", Address = "Source", Condition = "poor", AiConfidence = 1.0 };
        var target = new Listing { ExternalId = "versions-target", Address = "Target", Condition = "poor", AiConfidence = 1.0 };
        db.AddRange(owner, source, target); await db.SaveChangesAsync(ct);
        db.Votes.Add(new Vote(source.Id, owner.Id, VoteChoice.Dislike, [], "Avoid poor condition", DateTimeOffset.UtcNow)); await db.SaveChangesAsync(ct);
        var service = new AiLearningService(db, new FakeRuleGenerator(), TimeProvider.System);

        var first = await service.CreateProposalAsync(owner.Id, ct); await service.ApproveAsync(first.Id, owner.Id, ct);
        var second = await service.CreateProposalAsync(owner.Id, ct); await service.ApproveAsync(second.Id, owner.Id, ct);
        Assert.False(first.IsActive); Assert.True(second.IsActive);
        Assert.Equal(second.VersionLabel, (await db.Listings.FindAsync([target.Id], ct))!.LearningRuleVersion);

        await service.DeactivateAsync(second.Id, owner.Id, ct);

        Assert.True(first.IsActive); Assert.False(second.IsActive);
        Assert.Equal(first.VersionLabel, (await db.Listings.FindAsync([target.Id], ct))!.LearningRuleVersion);
        Assert.Equal(ListingState.AiRejected, (await db.Listings.FindAsync([target.Id], ct))!.State);
        Assert.Single(await db.AiRuleProposals.Where(x => x.IsActive).ToListAsync(ct));
    }

    [Fact]
    public async Task Legacy_cleared_vote_still_protects_listing_from_learning()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = Db();
        var owner = new Member { Email = "cleared-owner@example.test", Role = MemberRole.Owner };
        var source = new Listing { ExternalId = "cleared-source", Address = "Source", Condition = "poor", AiConfidence = 1.0 };
        var target = new Listing { ExternalId = "cleared-target", Address = "Target", Condition = "poor", AiConfidence = 1.0 };
        db.AddRange(owner, source, target); await db.SaveChangesAsync(ct);
        db.Votes.AddRange(
            new Vote(source.Id, owner.Id, VoteChoice.Dislike, [], "Avoid poor condition", DateTimeOffset.UtcNow),
            new Vote(target.Id, owner.Id, VoteChoice.Like, [], null, DateTimeOffset.UtcNow.AddMinutes(1)),
            new Vote(target.Id, owner.Id, VoteChoice.NotVoted, [], null, DateTimeOffset.UtcNow.AddMinutes(2)));
        await db.SaveChangesAsync(ct);
        var service = new AiLearningService(db, new FakeRuleGenerator(), TimeProvider.System);

        var proposal = await service.CreateProposalAsync(owner.Id, ct);
        await service.ApproveAsync(proposal.Id, owner.Id, ct);

        var saved = await db.Listings.FindAsync([target.Id], ct);
        Assert.Equal(ListingState.Active, saved!.State);
        Assert.Null(saved.LearningRuleVersion);
    }

    [Fact]
    public async Task Ai_rule_approval_rejects_a_stale_impact_preview()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = Db();
        var owner = new Member { Email = "stale-owner@example.test", Role = MemberRole.Owner };
        var source = new Listing { ExternalId = "stale-source", Address = "Source", Condition = "poor", AiConfidence = 1.0 };
        var target = new Listing { ExternalId = "stale-target", Address = "Target", Condition = "poor", AiConfidence = 1.0 };
        db.AddRange(owner, source, target); await db.SaveChangesAsync(ct);
        db.Votes.Add(new Vote(source.Id, owner.Id, VoteChoice.Dislike, [], "Avoid poor condition", DateTimeOffset.UtcNow)); await db.SaveChangesAsync(ct);
        var service = new AiLearningService(db, new FakeRuleGenerator(), TimeProvider.System);
        var proposal = await service.CreateProposalAsync(owner.Id, ct);
        db.Votes.Add(new Vote(target.Id, owner.Id, VoteChoice.Like, [], null, DateTimeOffset.UtcNow)); await db.SaveChangesAsync(ct);

        var error = await Assert.ThrowsAsync<DomainException>(() => service.ApproveAsync(proposal.Id, owner.Id, ct));
        Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Ai_rule_approval_waits_for_concurrent_vote_and_rechecks_eligibility()
    {
        var ct = TestContext.Current.CancellationToken;
        Guid targetId;
        Guid ownerId;
        Guid proposalId;
        await using (var setup = Db())
        {
            var owner = new Member { Email = "race-owner@example.test", Role = MemberRole.Owner };
            var source = new Listing { ExternalId = "race-source", Address = "Source", Condition = "poor", AiConfidence = 1.0 };
            var target = new Listing { ExternalId = "race-target", Address = "Target", Condition = "poor", AiConfidence = 1.0 };
            setup.AddRange(owner, source, target);
            await setup.SaveChangesAsync(ct);
            setup.Votes.Add(new Vote(source.Id, owner.Id, VoteChoice.Dislike, [], "Avoid poor condition", DateTimeOffset.UtcNow));
            await setup.SaveChangesAsync(ct);
            var proposal = await new AiLearningService(setup, new FakeRuleGenerator(), TimeProvider.System).CreateProposalAsync(owner.Id, ct);
            targetId = target.Id; ownerId = owner.Id; proposalId = proposal.Id;
        }

        await using var blocker = new NpgsqlConnection(_connectionString);
        await blocker.OpenAsync(ct);
        await using var blockerTransaction = await blocker.BeginTransactionAsync(ct);
        await using (var lockCommand = new NpgsqlCommand("SELECT 1 FROM listings WHERE \"Id\"=@id FOR KEY SHARE", blocker, blockerTransaction))
        {
            lockCommand.Parameters.AddWithValue("id", targetId);
            await lockCommand.ExecuteScalarAsync(ct);
        }

        await using var approvalDb = Db();
        var approval = new AiLearningService(approvalDb, new FakeRuleGenerator(), TimeProvider.System).ApproveAsync(proposalId, ownerId, ct);
        await Task.Delay(150, ct);
        Assert.False(approval.IsCompleted);

        await using (var voteCommand = new NpgsqlCommand("""INSERT INTO votes ("ListingId","MemberId","Choice","Tags","CreatedAt") VALUES (@listing,@member,'like',ARRAY[]::reason_tag[],now())""", blocker, blockerTransaction))
        {
            voteCommand.Parameters.AddWithValue("listing", targetId);
            voteCommand.Parameters.AddWithValue("member", ownerId);
            await voteCommand.ExecuteNonQueryAsync(ct);
        }
        await blockerTransaction.CommitAsync(ct);

        var error = await Assert.ThrowsAsync<DomainException>(() => approval);
        Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
        await using var verify = Db();
        Assert.Equal(ListingState.Active, (await verify.Listings.FindAsync([targetId], ct))!.State);
    }

    [Fact]
    public async Task Ai_rule_deactivation_waits_for_concurrent_override_and_rechecks_protection()
    {
        var ct = TestContext.Current.CancellationToken;
        Guid targetId;
        Guid ownerId;
        Guid proposalId;
        await using (var setup = Db())
        {
            var owner = new Member { Email = "deactivate-race-owner@example.test", Role = MemberRole.Owner };
            var source = new Listing { ExternalId = "deactivate-race-source", Address = "Source", Condition = "poor", AiConfidence = 1.0 };
            var target = new Listing { ExternalId = "deactivate-race-target", Address = "Target", Condition = "poor", AiConfidence = 1.0 };
            setup.AddRange(owner, source, target);
            await setup.SaveChangesAsync(ct);
            setup.Votes.Add(new Vote(source.Id, owner.Id, VoteChoice.Dislike, [], "Avoid poor condition", DateTimeOffset.UtcNow));
            await setup.SaveChangesAsync(ct);
            var learning = new AiLearningService(setup, new FakeRuleGenerator(), TimeProvider.System);
            var proposal = await learning.CreateProposalAsync(owner.Id, ct);
            await learning.ApproveAsync(proposal.Id, owner.Id, ct);
            targetId = target.Id; ownerId = owner.Id; proposalId = proposal.Id;
        }

        await using var blocker = new NpgsqlConnection(_connectionString);
        await blocker.OpenAsync(ct);
        await using var blockerTransaction = await blocker.BeginTransactionAsync(ct);
        await using (var lockCommand = new NpgsqlCommand("SELECT 1 FROM listings WHERE \"Id\"=@id FOR KEY SHARE", blocker, blockerTransaction))
        {
            lockCommand.Parameters.AddWithValue("id", targetId);
            await lockCommand.ExecuteScalarAsync(ct);
        }

        await using var deactivationDb = Db();
        var deactivation = new AiLearningService(deactivationDb, new FakeRuleGenerator(), TimeProvider.System).DeactivateAsync(proposalId, ownerId, ct);
        await Task.Delay(150, ct);
        Assert.False(deactivation.IsCompleted);

        await using (var overrideCommand = new NpgsqlCommand("""INSERT INTO listing_overrides ("ListingId","OwnerId","Action","CreatedAt") VALUES (@listing,@owner,'restore',now()); UPDATE listings SET "State"='restored' WHERE "Id"=@listing;""", blocker, blockerTransaction))
        {
            overrideCommand.Parameters.AddWithValue("listing", targetId);
            overrideCommand.Parameters.AddWithValue("owner", ownerId);
            await overrideCommand.ExecuteNonQueryAsync(ct);
        }
        await blockerTransaction.CommitAsync(ct);
        await deactivation;

        await using var verify = Db();
        var saved = await verify.Listings.FindAsync([targetId], ct);
        Assert.Equal(ListingState.Restored, saved!.State);
    }

    [Fact]
    public async Task Ai_application_audit_survives_listing_purge_with_external_identity()
    {
        var ct = TestContext.Current.CancellationToken;
        Guid listingId;
        Guid proposalId;
        await using (var setup = Db())
        {
            var owner = new Member { Email = "audit-owner@example.test", Role = MemberRole.Owner };
            var listing = new Listing { ExternalId = "audited-purge", Address = "Audit" };
            setup.AddRange(owner, listing);
            await setup.SaveChangesAsync(ct);
            var proposal = new AiRuleProposal(owner.Id, 91, "Audit rule", """{"combinator":"all","conditions":[{"field":"condition","operator":"eq","value":"poor"}]}""", "[]", "{}", DateTimeOffset.UtcNow);
            setup.AiRuleProposals.Add(proposal);
            await setup.SaveChangesAsync(ct);
            listingId = listing.Id; proposalId = proposal.Id;
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using (var insert = new NpgsqlCommand("""INSERT INTO ai_rule_applications ("ProposalId","ListingId","ListingExternalId","PreviousState","AppliedState","AppliedAt") VALUES (@proposal,@listing,'audited-purge','active','ai_rejected',now())""", connection))
        {
            insert.Parameters.AddWithValue("proposal", proposalId);
            insert.Parameters.AddWithValue("listing", listingId);
            await insert.ExecuteNonQueryAsync(ct);
        }
        await using (var delete = new NpgsqlCommand("""DELETE FROM listings WHERE "Id"=@listing""", connection))
        {
            delete.Parameters.AddWithValue("listing", listingId);
            Assert.Equal(1, await delete.ExecuteNonQueryAsync(ct));
        }
        await using var verify = new NpgsqlCommand("""SELECT "ListingId","ListingExternalId" FROM ai_rule_applications WHERE "ProposalId"=@proposal""", connection);
        verify.Parameters.AddWithValue("proposal", proposalId);
        await using var reader = await verify.ExecuteReaderAsync(ct);
        Assert.True(await reader.ReadAsync(ct));
        Assert.Equal(listingId, reader.GetGuid(0));
        Assert.Equal("audited-purge", reader.GetString(1));
    }

    [Theory]
    [InlineData("http://public.example.test")]
    [InlineData("http://localhost:11434")]
    public async Task Ai_learning_rejects_non_allowlisted_plain_http_hosts(string baseUrl)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AiLearning:BaseUrl"] = baseUrl,
            ["AiLearning:AllowInsecureHttp"] = "true",
            ["AiLearning:InsecureHttpAllowedHosts"] = "192.168.50.227",
        }).Build();
        var generator = new OllamaAiRuleGenerator(new HttpClient(), config);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => generator.GenerateAsync([], TestContext.Current.CancellationToken));
        Assert.Contains("allowlisted", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task E2E_seed_is_idempotent_and_covers_active_and_rejected_review_flows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = Db();

        await E2EDataSeeder.SeedAsync(db, ct);
        await E2EDataSeeder.SeedAsync(db, ct);

        var listings = await db.Listings.OrderBy(x => x.ExternalId).ToListAsync(ct);
        Assert.Equal(2, listings.Count);
        Assert.Contains(listings, x => x.State == ListingState.Active && x.Price == 4_500_000m);
        Assert.Contains(listings, x => x.State == ListingState.AiRejected && x.AiAssessed);
    }

    [Fact]
    public async Task Cloudflare_member_resolution_preserves_owner_role_and_provisions_unknown_identity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = Db();
        var owner = new Member { Email = "cf-active@example.test", Role = MemberRole.Owner };
        db.Members.Add(owner);
        await db.SaveChangesAsync(ct);
        var service = new CloudflareMemberService(db);

        Assert.Equal(owner.Id, (await service.ResolveAsync("CF-ACTIVE@example.test", ct))?.Id);
        var added = await service.ResolveAsync("cf-unknown@example.test", ct);
        Assert.NotNull(added);
        Assert.Equal(MemberRole.Member, added.Role);
        Assert.True(added.IsActive);
    }

    [Fact]
    public async Task Cloudflare_member_resolution_atomically_provisions_an_allowed_identity_under_concurrency()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 4).Select(async _ =>
        {
            await start.Task;
            await using var db = Db();
            return await new CloudflareMemberService(db).ResolveAsync("CF-ALLOWED@example.test", ct);
        }).ToArray();
        start.SetResult();
        var members = await Task.WhenAll(attempts);

        Assert.All(members, member => Assert.Equal(MemberRole.Member, member?.Role));
        Assert.Single(members.Select(x => x!.Id).Distinct());
        await using var verify = Db();
        Assert.Equal(1, await verify.Members.CountAsync(x => x.Email == "cf-allowed@example.test", ct));
    }

    [Fact]
    public async Task Member_profile_migration_preserves_existing_defaults_and_enforces_palette()
    {
        var ct = TestContext.Current.CancellationToken;
        var member = new Member { Email = "profile-migration@example.test" };
        await using (var db = Db())
        {
            db.Members.Add(member);
            await db.SaveChangesAsync(ct);
            Assert.Equal("", member.AvatarColor);
            await db.Database.MigrateAsync(ct);
        }

        await using var invalid = Db();
        var error = await Assert.ThrowsAsync<PostgresException>(() => invalid.Database.ExecuteSqlRawAsync("UPDATE members SET \"AvatarColor\" = '#ffffff' WHERE \"Id\" = {0}", [member.Id], ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        await using var verify = Db();
        Assert.Equal("", (await verify.Members.FindAsync([member.Id], ct))?.AvatarColor);
    }

    [Fact]
    public async Task Profile_endpoint_updates_only_the_authenticated_member_and_rejects_invalid_payloads()
    {
        var ct = TestContext.Current.CancellationToken;
        var current = new Member { Email = "profile-current@example.test", DisplayName = "Before" };
        var other = new Member { Email = "profile-other@example.test", DisplayName = "Other" };
        await using (var setup = Db())
        {
            setup.Members.AddRange(current, other);
            await setup.SaveChangesAsync(ct);
        }

        await using var factory = new WebApplicationFactory<CloudflareAccessOptions>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Debug:AutoLogin", "true");
            builder.UseSetting("E2E:TestAuth", "true");
            builder.UseSetting("E2E:SeedData", "true");
            builder.UseSetting("ConnectionStrings:Database", _connectionString);
            builder.UseSetting("Database:AutoMigrate", "false");
            builder.UseSetting("INITIAL_OWNER_EMAIL", "");
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-House-Consensus-CSRF", "1");
        client.DefaultRequestHeaders.Add(DebugAutoLoginMiddleware.E2EEmailHeader, current.Email);

        var saved = await client.PutAsJsonAsync("/api/auth/profile", new UpdateProfile("  Captain  ", "#6D28D9"), ct);
        var dto = await saved.Content.ReadFromJsonAsync<MemberDto>(ct);
        var missing = await client.PutAsync("/api/auth/profile", new StringContent("{\"displayName\":null,\"avatarColor\":null}", System.Text.Encoding.UTF8, "application/json"), ct);
        var longName = await client.PutAsJsonAsync("/api/auth/profile", new UpdateProfile(new string('x', 41), "#6d28d9"), ct);
        var unknownColor = await client.PutAsJsonAsync("/api/auth/profile", new UpdateProfile("Captain", "#ffffff"), ct);

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("Captain", dto?.DisplayName);
        Assert.Equal("#6d28d9", dto?.AvatarColor);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, longName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownColor.StatusCode);
        await using var verify = Db();
        Assert.Equal("Captain", (await verify.Members.FindAsync([current.Id], ct))?.DisplayName);
        Assert.Equal("#6d28d9", (await verify.Members.FindAsync([current.Id], ct))?.AvatarColor);
        Assert.Equal("Other", (await verify.Members.FindAsync([other.Id], ct))?.DisplayName);
        Assert.Equal("", (await verify.Members.FindAsync([other.Id], ct))?.AvatarColor);
    }

    [Fact]
    public async Task Manual_listing_and_guided_vote_endpoints_are_durable_deduplicated_and_audited()
    {
        var ct = TestContext.Current.CancellationToken;
        var current = new Member { Email = "manual-listing@example.test", DisplayName = "Manual member" };
        var legacyImported = new Listing { ExternalId = "legacy-imported", Address = "Legacyvej 9", SourceUrl = "https://Example.dk/legacy/?utm_source=old#photo" };
        await using (var setup = Db()) { setup.Members.Add(current); setup.Listings.Add(legacyImported); await setup.SaveChangesAsync(ct); }
        await using var factory = new WebApplicationFactory<CloudflareAccessOptions>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development"); builder.UseSetting("Debug:AutoLogin", "true"); builder.UseSetting("E2E:TestAuth", "true"); builder.UseSetting("E2E:SeedData", "true");
            builder.UseSetting("ConnectionStrings:Database", _connectionString); builder.UseSetting("Database:AutoMigrate", "false"); builder.UseSetting("INITIAL_OWNER_EMAIL", "");
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-House-Consensus-CSRF", "1");
        client.DefaultRequestHeaders.Add(DebugAutoLoginMiddleware.E2EEmailHeader, current.Email);

        var legacyDuplicateResponse = await client.PostAsJsonAsync("/api/listings", new CreateManualListing("https://example.dk/legacy", "Different address"), ct);
        var legacyDuplicate = await legacyDuplicateResponse.Content.ReadFromJsonAsync<ManualListingResult>(ct);
        Assert.Equal(HttpStatusCode.OK, legacyDuplicateResponse.StatusCode); Assert.True(legacyDuplicate?.Existing); Assert.Equal(legacyImported.Id, legacyDuplicate?.ListingId);

        var oversizedAddress = await client.PostAsJsonAsync("/api/listings", new CreateManualListing("https://example.dk/oversized-address", new string('a', 501)), ct);
        var whitespaceExpandedAddress = await client.PostAsJsonAsync("/api/listings", new CreateManualListing("https://example.dk/whitespace-address", "a" + new string(' ', 600) + "b"), ct);
        var oversizedUrl = await client.PostAsJsonAsync("/api/listings", new CreateManualListing("https://example.dk/" + new string('a', 2049), "Boundaryvej 1"), ct);
        var oversizedPrice = await client.PostAsJsonAsync("/api/listings", new CreateManualListing("https://example.dk/oversized-price", "Boundaryvej 2", AskingPrice: 1_000_000_000_000m), ct);
        Assert.Equal(HttpStatusCode.BadRequest, oversizedAddress.StatusCode); Assert.Equal(HttpStatusCode.BadRequest, whitespaceExpandedAddress.StatusCode); Assert.Equal(HttpStatusCode.BadRequest, oversizedUrl.StatusCode); Assert.Equal(HttpStatusCode.BadRequest, oversizedPrice.StatusCode);
        await using (var boundaryVerify = Db()) Assert.False(await boundaryVerify.Listings.AnyAsync(x => x.IsManuallyAdded, ct));

        var createdResponse = await client.PostAsJsonAsync("/api/listings", new CreateManualListing("https://Example.dk/home/?utm_source=test#photos", "  Testvej  1 ", "Roskilde", 7_500_000m), ct);
        var created = await createdResponse.Content.ReadFromJsonAsync<ManualListingResult>(ct);
        await using (var clearRequest = Db())
        {
            var pending = await clearRequest.Listings.SingleAsync(x => x.Id == created!.ListingId, ct);
            pending.ManualScoringRequestedAt = null;
            await clearRequest.SaveChangesAsync(ct);
        }
        var duplicateResponse = await client.PostAsJsonAsync("/api/listings", new CreateManualListing("https://example.dk/home", "testvej 1"), ct);
        var duplicate = await duplicateResponse.Content.ReadFromJsonAsync<ManualListingResult>(ct);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode); Assert.NotNull(created); Assert.False(created.Existing);
        Assert.Equal(HttpStatusCode.OK, duplicateResponse.StatusCode); Assert.Equal(created.ListingId, duplicate?.ListingId); Assert.True(duplicate?.Existing);
        var beforeActivity = await client.GetFromJsonAsync<ListingDto>($"/api/listings/{created.ListingId}", ct);
        Assert.True(beforeActivity?.CanWithdraw); Assert.False(beforeActivity?.CanArchive);

        var ratings = VoteCategories.All.Select(category => new VoteRatingInput(category, category == VoteCategory.Layout ? CategoryRating.Like : CategoryRating.Neutral)).ToArray();
        var invalidRatings = ratings.ToArray(); invalidRatings[1] = new VoteRatingInput(VoteCategory.Privacy, (CategoryRating)99);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/listings/{created.ListingId}/votes", new CastVote(invalidRatings, 4, "invalid"), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/listings/{created.ListingId}/votes", new CastVote(ratings, 0), ct)).StatusCode);
        var firstVote = await client.PostAsJsonAsync($"/api/listings/{created.ListingId}/votes", new CastVote(ratings, 4, "first"), ct);
        var afterActivity = await client.GetFromJsonAsync<ListingDto>($"/api/listings/{created.ListingId}", ct);
        Assert.False(afterActivity?.CanWithdraw); Assert.False(afterActivity?.CanArchive);
        ratings[0] = new VoteRatingInput(VoteCategory.Layout, CategoryRating.Dislike);
        var secondVote = await client.PostAsJsonAsync($"/api/listings/{created.ListingId}/votes", new CastVote(ratings, 2, "second"), ct);
        Assert.Equal(HttpStatusCode.OK, firstVote.StatusCode); Assert.Equal(HttpStatusCode.OK, secondVote.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/listings/{created.ListingId}", ct)).StatusCode);

        await using var verify = Db();
        var listing = await verify.Listings.SingleAsync(x => x.Id == created.ListingId, ct);
        Assert.True(listing.IsManuallyAdded); Assert.True(listing.ManualLifecycleProtected); Assert.Equal("Roskilde", listing.City); Assert.Equal(7_500_000m, listing.Price); Assert.Null(listing.FamilyFitScore); Assert.NotNull(listing.ManualScoringRequestedAt); Assert.Null(listing.ManualScoringCompletedAt);
        var votes = await verify.Votes.Include(x => x.Ratings).Where(x => x.ListingId == created.ListingId).OrderBy(x => x.Id).ToArrayAsync(ct);
        Assert.Equal(2, votes.Length); Assert.Equal(VoteChoice.Like, votes[0].Choice); Assert.Equal(VoteChoice.Dislike, votes[1].Choice); Assert.All(votes, x => Assert.Equal(10, x.Ratings.Count)); Assert.Equal(4, votes[0].OverallScore); Assert.Equal(2, votes[1].OverallScore);
    }

    [Fact]
    public async Task Manual_create_rejects_split_url_and_address_identity_without_mutation()
    {
        var ct = TestContext.Current.CancellationToken;
        var member = new Member { Email = "split-identity@example.test" };
        var urlMatch = new Listing { ExternalId = "split-url", Address = "Urlvej 1", SourceUrl = "https://example.dk/split-url", CanonicalUrl = "https://example.dk/split-url", NormalizedAddress = "urlvej 1" };
        var addressMatch = new Listing { ExternalId = "split-address", Address = "Adressevej 2", SourceUrl = "https://example.dk/split-address", CanonicalUrl = "https://example.dk/split-address", NormalizedAddress = "adressevej 2" };
        await using (var setup = Db()) { setup.AddRange(member, urlMatch, addressMatch); await setup.SaveChangesAsync(ct); }
        await using var factory = new WebApplicationFactory<CloudflareAccessOptions>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development"); builder.UseSetting("Debug:AutoLogin", "true"); builder.UseSetting("E2E:TestAuth", "true"); builder.UseSetting("E2E:SeedData", "true");
            builder.UseSetting("ConnectionStrings:Database", _connectionString); builder.UseSetting("Database:AutoMigrate", "false"); builder.UseSetting("INITIAL_OWNER_EMAIL", "");
        });
        using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-House-Consensus-CSRF", "1"); client.DefaultRequestHeaders.Add(DebugAutoLoginMiddleware.E2EEmailHeader, member.Email);
        var response = await client.PostAsJsonAsync("/api/listings", new CreateManualListing("https://example.dk/split-url", "Adressevej 2"), ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var verify = Db(); Assert.Equal(2, await verify.Listings.CountAsync(x => x.ExternalId.StartsWith("split-"), ct));
    }

    [Fact]
    public async Task Manual_create_serializes_against_concurrent_import()
    {
        var ct = TestContext.Current.CancellationToken;
        var member = new Member { Email = "manual-import-race@example.test" };
        await using (var setup = Db()) { setup.Members.Add(member); await setup.SaveChangesAsync(ct); }
        await using var factory = new WebApplicationFactory<CloudflareAccessOptions>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development"); builder.UseSetting("Debug:AutoLogin", "true"); builder.UseSetting("E2E:TestAuth", "true"); builder.UseSetting("E2E:SeedData", "true");
            builder.UseSetting("ConnectionStrings:Database", _connectionString); builder.UseSetting("Database:AutoMigrate", "false"); builder.UseSetting("INITIAL_OWNER_EMAIL", "");
        });
        using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-House-Consensus-CSRF", "1"); client.DefaultRequestHeaders.Add(DebugAutoLoginMiddleware.E2EEmailHeader, member.Email);
        var imported = new Listing { ExternalId = "concurrent-import", Address = "Importvej 1", SourceUrl = "https://example.dk/import-race", CanonicalUrl = "https://example.dk/import-race", NormalizedAddress = "importvej 1" };
        await using var import = Db(); await using var tx = await import.Database.BeginTransactionAsync(ct); import.Listings.Add(imported); await import.SaveChangesAsync(ct);
        var creating = client.PostAsJsonAsync("/api/listings", new CreateManualListing("https://example.dk/import-race", "Different address"), ct);
        await Task.Delay(150, ct); Assert.False(creating.IsCompleted);
        await tx.CommitAsync(ct);
        var response = await creating; var result = await response.Content.ReadFromJsonAsync<ManualListingResult>(ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(result?.Existing); Assert.Equal(imported.Id, result?.ListingId);
    }

    [Fact]
    public async Task Manual_withdraw_serializes_against_new_household_activity()
    {
        var ct = TestContext.Current.CancellationToken;
        var member = new Member { Email = "withdraw-race@example.test" };
        var listing = Listing.CreateManual("https://example.dk/race", "Racevej 1", member.Id, DateTimeOffset.UtcNow);
        await using (var setup = Db()) { setup.Members.Add(member); setup.Listings.Add(listing); await setup.SaveChangesAsync(ct); }
        await using var factory = new WebApplicationFactory<CloudflareAccessOptions>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development"); builder.UseSetting("Debug:AutoLogin", "true"); builder.UseSetting("E2E:TestAuth", "true"); builder.UseSetting("E2E:SeedData", "true");
            builder.UseSetting("ConnectionStrings:Database", _connectionString); builder.UseSetting("Database:AutoMigrate", "false"); builder.UseSetting("INITIAL_OWNER_EMAIL", "");
        });
        using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-House-Consensus-CSRF", "1"); client.DefaultRequestHeaders.Add(DebugAutoLoginMiddleware.E2EEmailHeader, member.Email);
        await using var activity = Db(); await using var tx = await activity.Database.BeginTransactionAsync(ct);
        activity.Comments.Add(new Comment(listing.Id, member.Id, "Concurrent activity", DateTimeOffset.UtcNow)); await activity.SaveChangesAsync(ct);
        var withdrawing = client.DeleteAsync($"/api/listings/{listing.Id}", ct);
        await Task.Delay(150, ct); Assert.False(withdrawing.IsCompleted);
        await tx.CommitAsync(ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await withdrawing).StatusCode);
    }

    [Fact]
    public async Task Manual_withdraw_waits_for_concurrent_owner_override_without_deadlock()
    {
        var ct = TestContext.Current.CancellationToken;
        var submitter = new Member { Email = "withdraw-override-member@example.test" };
        var owner = new Member { Email = "withdraw-override-owner@example.test", Role = MemberRole.Owner };
        var listing = Listing.CreateManual("https://example.dk/override-race", "Overridevej 1", submitter.Id, DateTimeOffset.UtcNow);
        await using (var setup = Db()) { setup.Members.AddRange(submitter, owner); setup.Listings.Add(listing); await setup.SaveChangesAsync(ct); }
        await using var factory = new WebApplicationFactory<CloudflareAccessOptions>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development"); builder.UseSetting("Debug:AutoLogin", "true"); builder.UseSetting("E2E:TestAuth", "true"); builder.UseSetting("E2E:SeedData", "true");
            builder.UseSetting("ConnectionStrings:Database", _connectionString); builder.UseSetting("Database:AutoMigrate", "false"); builder.UseSetting("INITIAL_OWNER_EMAIL", "");
        });
        using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add("X-House-Consensus-CSRF", "1"); client.DefaultRequestHeaders.Add(DebugAutoLoginMiddleware.E2EEmailHeader, submitter.Email);
        await using var activity = Db(); await using var tx = await activity.Database.BeginTransactionAsync(ct);
        var changing = await activity.Listings.Include(x => x.Overrides).SingleAsync(x => x.Id == listing.Id, ct);
        changing.ApplyOverride(OverrideAction.Restore, owner.Id, "Concurrent owner decision", DateTimeOffset.UtcNow); await activity.SaveChangesAsync(ct);
        var withdrawing = client.DeleteAsync($"/api/listings/{listing.Id}", ct);
        await Task.Delay(150, ct); Assert.False(withdrawing.IsCompleted);
        await tx.CommitAsync(ct);
        Assert.Equal(HttpStatusCode.Forbidden, (await withdrawing).StatusCode);
    }

    [Fact]
    public async Task Existing_activity_mutations_share_archive_gate_and_recheck_archived_state()
    {
        var ct = TestContext.Current.CancellationToken;
        var member = new Member { Email = "mutation-member@example.dk" };
        var owner = new Member { Email = "mutation-owner@example.dk", Role = MemberRole.Owner };
        var listing = Listing.CreateManual("https://example.dk/mutation-race", "Mutationvej 1", member.Id, DateTimeOffset.UtcNow);
        var vote = new Vote(listing.Id, member.Id, VoteChoice.Like, [], "before", DateTimeOffset.UtcNow);
        var comment = new Comment(listing.Id, member.Id, "before", DateTimeOffset.UtcNow);
        await using (var arrange = Db()) { arrange.Members.AddRange(member, owner); arrange.Listings.Add(listing); arrange.Votes.Add(vote); arrange.Comments.Add(comment); await arrange.SaveChangesAsync(ct); }

        await using var factory = new WebApplicationFactory<CloudflareAccessOptions>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development"); builder.UseSetting("Debug:AutoLogin", "true"); builder.UseSetting("E2E:TestAuth", "true"); builder.UseSetting("E2E:SeedData", "true");
            builder.UseSetting("ConnectionStrings:Database", _connectionString); builder.UseSetting("Database:AutoMigrate", "false"); builder.UseSetting("INITIAL_OWNER_EMAIL", "");
        });
        using var memberClient = factory.CreateClient(); memberClient.DefaultRequestHeaders.Add("X-House-Consensus-CSRF", "1"); memberClient.DefaultRequestHeaders.Add(DebugAutoLoginMiddleware.E2EEmailHeader, member.Email);
        using var ownerClient = factory.CreateClient(); ownerClient.DefaultRequestHeaders.Add("X-House-Consensus-CSRF", "1"); ownerClient.DefaultRequestHeaders.Add(DebugAutoLoginMiddleware.E2EEmailHeader, owner.Email);

        await using var gate = Db(); await using var gateTransaction = await gate.Database.BeginTransactionAsync(ct);
        await gate.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({listing.Id.ToString()}, 0))", ct);
        var editing = memberClient.PutAsJsonAsync($"/api/listings/{listing.Id}/votes/note", new EditVoteNote("mutation first"), ct);
        await Task.Delay(150, ct); Assert.False(editing.IsCompleted);
        var archiving = ownerClient.DeleteAsync($"/api/listings/{listing.Id}", ct);
        await Task.Delay(150, ct); Assert.False(archiving.IsCompleted);
        await gateTransaction.CommitAsync(ct);

        Assert.Equal(HttpStatusCode.OK, (await editing).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await archiving).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await memberClient.PutAsJsonAsync($"/api/listings/{listing.Id}/votes/note", new EditVoteNote("too late"), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await memberClient.PutAsJsonAsync($"/api/comments/{comment.Id}", new EditComment("too late"), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await memberClient.DeleteAsync($"/api/comments/{comment.Id}", ct)).StatusCode);
    }

    [Fact]
    public async Task Manual_archive_waiting_for_listing_gate_does_not_block_child_fk_insert()
    {
        var ct = TestContext.Current.CancellationToken;
        var submitter = new Member { Email = "gate-submitter@example.dk", DisplayName = "Gate Submitter" };
        var owner = new Member { Email = "gate-owner@example.dk", DisplayName = "Gate Owner", Role = MemberRole.Owner };
        var listing = Listing.CreateManual("https://example.dk/gate-race", "Gatevej 1", submitter.Id, DateTimeOffset.UtcNow);
        await using (var arrange = Db()) { arrange.Members.AddRange(submitter, owner); arrange.Listings.Add(listing); await arrange.SaveChangesAsync(ct); }

        await using var factory = new WebApplicationFactory<CloudflareAccessOptions>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development"); builder.UseSetting("Debug:AutoLogin", "true"); builder.UseSetting("E2E:TestAuth", "true"); builder.UseSetting("E2E:SeedData", "true");
            builder.UseSetting("ConnectionStrings:Database", _connectionString); builder.UseSetting("Database:AutoMigrate", "false"); builder.UseSetting("INITIAL_OWNER_EMAIL", "");
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-House-Consensus-CSRF", "1"); client.DefaultRequestHeaders.Add(DebugAutoLoginMiddleware.E2EEmailHeader, submitter.Email);

        await using var gate = Db();
        await using var gateTransaction = await gate.Database.BeginTransactionAsync(ct);
        await gate.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({listing.Id.ToString()}, 0))", ct);
        var deleteTask = client.DeleteAsync($"/api/listings/{listing.Id}", ct);
        await Task.Delay(200, ct); Assert.False(deleteTask.IsCompleted);

        await using (var activity = Db())
        {
            activity.Comments.Add(new Comment(listing.Id, owner.Id, "Concurrent activity", DateTimeOffset.UtcNow));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await activity.SaveChangesAsync(timeout.Token);
        }

        await gateTransaction.CommitAsync(ct);
        var response = await deleteTask.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Actual_production_application_disables_magic_link_routes_and_does_not_redirect_tunnel_HTTP()
    {
        await using var factory = new WebApplicationFactory<CloudflareAccessOptions>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("CloudflareAccess:Enabled", "true");
            builder.UseSetting("CloudflareAccess:TeamDomain", "team.cloudflareaccess.com");
            builder.UseSetting("CloudflareAccess:Audience", "exact-production-audience");
            builder.UseSetting("ConnectionStrings:Database", _connectionString);
            builder.UseSetting("Database:AutoMigrate", "false");
            builder.UseSetting("INITIAL_OWNER_EMAIL", "");
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("X-House-Consensus-CSRF", "1");

        var request = await client.PostAsync("/api/auth/request", new StringContent("{\"email\":\"member@example.test\"}", System.Text.Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        var consume = await client.GetAsync("/api/auth/consume?token=unused", TestContext.Current.CancellationToken);
        var root = await client.GetAsync("/", TestContext.Current.CancellationToken);
        var mode = await client.GetFromJsonAsync<AuthModeDto>("/api/auth/mode", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, request.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, consume.StatusCode);
        Assert.DoesNotContain(root.StatusCode, new[] { HttpStatusCode.MovedPermanently, HttpStatusCode.Redirect, HttpStatusCode.TemporaryRedirect, HttpStatusCode.PermanentRedirect });
        Assert.True(mode?.CloudflareAccess);
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(x => x.Key, x => (string?)x.Value)).Build();

    private sealed class FakeRuleGenerator : IAiRuleGenerator
    {
        public Task<GeneratedAiRule> GenerateAsync(IReadOnlyList<VoteNoteInput> notes, CancellationToken ct) => Task.FromResult(new GeneratedAiRule("Avoid renovation-heavy homes", """{"combinator":"all","conditions":[{"field":"condition","operator":"eq","value":"poor"}]}"""));
    }

    private sealed class CaptureEmail : IEmailSender
    {
        public string Link { get; private set; } = "";
        public int SendCount { get; private set; }
        public Task SendMagicLinkAsync(string email, string link, CancellationToken ct) { Link = link; SendCount++; return Task.CompletedTask; }
    }
    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan amount) => now += amount;
    }
}

