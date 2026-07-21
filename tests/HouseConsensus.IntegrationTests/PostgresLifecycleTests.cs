using Xunit;
using HouseConsensus.Server.Auth;
using HouseConsensus.Server.Data;
using HouseConsensus.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task Magic_link_is_invite_only_expires_and_is_single_use()
    {
        var ct = TestContext.Current.CancellationToken;
        var mail = new CaptureEmail(); var now = new ManualTimeProvider(DateTimeOffset.UtcNow); var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { { "PublicOrigin", "https://example.test" } }).Build(); Guid inviter;
        await using (var db = Db()) { var owner = new Member { Email = "owner@example.test", Role = MemberRole.Owner }; db.Members.Add(owner); await db.SaveChangesAsync(ct); inviter = owner.Id; db.Invites.Add(new Invite { Email = "new@example.test", InvitedById = inviter, ExpiresAt = now.GetUtcNow().AddDays(1) }); await db.SaveChangesAsync(ct); var service = new MagicLinkService(db, mail, cfg, now); await service.RequestAsync("NEW@example.test", ct); }
        var token = new Uri(mail.Link).Query.Split("token=")[1];
        await using (var db = Db()) { var service = new MagicLinkService(db, mail, cfg, now); var member = await service.ConsumeAsync(Uri.UnescapeDataString(token), ct); Assert.NotNull(member); Assert.Equal("new@example.test", member.Email); Assert.Null(await service.ConsumeAsync(Uri.UnescapeDataString(token), ct)); }
    }
    private sealed class CaptureEmail : IEmailSender { public string Link { get; private set; } = ""; public Task SendMagicLinkAsync(string email, string link, CancellationToken ct) { Link = link; return Task.CompletedTask; } }
    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}

