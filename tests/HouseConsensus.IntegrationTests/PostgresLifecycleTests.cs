using Xunit;
using HouseConsensus.Server.Auth;
using HouseConsensus.Server.Data;
using HouseConsensus.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Npgsql;
using Testcontainers.PostgreSql;
namespace HouseConsensus.IntegrationTests;

public sealed class PostgresLifecycleTests : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private string _connectionString = "";
    private AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString, n => n.MapEnum<MemberRole>("member_role").MapEnum<VoteChoice>("vote_choice").MapEnum<ListingState>("listing_state").MapEnum<ReasonTag>("reason_tag").MapEnum<OverrideAction>("override_action")).Options);
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
        { var owner = new Member { Email = "owner@example.test", Role = MemberRole.Owner }; var member = new Member { Email = "member@example.test" }; var listing = new Listing { ExternalId = "case-1", Address = "Example 1", FamilyFitScore = 0.9 }; db.AddRange(owner, member, listing); await db.SaveChangesAsync(ct); db.Votes.AddRange(new Vote { ListingId = listing.Id, MemberId = member.Id, Choice = VoteChoice.Dislike }, new Vote { ListingId = listing.Id, MemberId = member.Id, Choice = VoteChoice.Like, CreatedAt = DateTimeOffset.UtcNow.AddSeconds(1) }); listing.ApplyOverride(OverrideAction.Restore, owner.Id, "reviewed", DateTimeOffset.UtcNow); var comment = new Comment(listing.Id, member.Id, "original", DateTimeOffset.UtcNow); comment.Edit(member.Id, false, "revised", DateTimeOffset.UtcNow.AddSeconds(1)); comment.Delete(owner.Id, true, DateTimeOffset.UtcNow.AddSeconds(2)); db.Comments.Add(comment); db.Feedback.Add(new Feedback { MemberId = member.Id, ListingId = listing.Id, Body = "wrong score", ReviewedAt = DateTimeOffset.UtcNow }); member.Deactivate(); listing.Archive(DateTimeOffset.UtcNow); await db.SaveChangesAsync(ct); }
        await using (var db = Db()) { var listing = await db.Listings.Include(x => x.Overrides).SingleAsync(ct); var votes = await db.Votes.OrderBy(x => x.CreatedAt).ToListAsync(ct); Assert.Equal(ListingState.Archived, listing.State); Assert.Single(listing.Overrides); Assert.Equal(2, votes.Count); Assert.Equal(VoteChoice.Like, ConsensusRules.LatestVotes(votes).Single().Value.Choice); var comment = await db.Comments.Include(x => x.Revisions).SingleAsync(ct); Assert.True(comment.IsDeleted); Assert.Equal(2, comment.Revisions.Count); Assert.False((await db.Members.SingleAsync(x => x.Role == MemberRole.Member, ct)).IsActive); Assert.NotNull((await db.Feedback.SingleAsync(ct)).ReviewedAt); }
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
        db.Votes.AddRange(
            new Vote { ListingId = listing.Id, MemberId = member.Id, Choice = VoteChoice.Dislike, Tags = [ReasonTag.Noise, ReasonTag.Price], CreatedAt = at },
            new Vote { ListingId = listing.Id, MemberId = member.Id, Choice = VoteChoice.Like, Tags = [ReasonTag.Garden], CreatedAt = at.AddSeconds(1) });
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();

        var history = await db.Votes.OrderBy(x => x.CreatedAt).ToListAsync(ct);
        Assert.Equal([ReasonTag.Noise, ReasonTag.Price], history[0].Tags);
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

