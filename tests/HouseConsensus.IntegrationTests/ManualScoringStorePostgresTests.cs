using HouseConsensus.Server.Data;
using HouseConsensus.Server.Scoring;
using HouseConsensus.Shared;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

#pragma warning disable xUnit1051
namespace HouseConsensus.IntegrationTests;

public sealed class ManualScoringStorePostgresTests : IAsyncLifetime
{
    private string _connectionString = "";
    public static bool HasTestDatabaseUrl => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TEST_DATABASE_URL"));

    [Fact(Skip = "requires TEST_DATABASE_URL", SkipUnless = nameof(HasTestDatabaseUrl))]
    public async Task Claim_is_atomic_skips_locked_rows_and_returns_persisted_source_identity()
    {
        var now = DateTimeOffset.UtcNow;
        var first = await AddManualListingAsync("first", now.AddMinutes(-2)); var second = await AddManualListingAsync("second", now.AddMinutes(-1));
        var store = new PostgresManualScoringStore(_connectionString);
        await store.EnqueueAsync(first.Id, first.ExternalId, first.CanonicalUrl!, now.AddMinutes(-2)); await store.EnqueueAsync(second.Id, second.ExternalId, second.CanonicalUrl!, now.AddMinutes(-1));
        var claim1 = await store.ClaimNextAsync(now, TimeSpan.FromMinutes(1)); var claim2 = await store.ClaimNextAsync(now, TimeSpan.FromMinutes(1));
        Assert.NotNull(claim1); Assert.NotNull(claim2); Assert.Equal(first.Id, claim1!.ListingId); Assert.Equal(first.ExternalId, claim1.SourceExternalId); Assert.Equal(first.CanonicalUrl, claim1.SourceCanonicalUrl); Assert.Equal(second.Id, claim2!.ListingId); Assert.NotEqual(claim1.JobId, claim2.JobId);
    }

    [Fact(Skip = "requires TEST_DATABASE_URL", SkipUnless = nameof(HasTestDatabaseUrl))]
    public async Task Concurrent_claims_return_distinct_jobs()
    {
        var now = DateTimeOffset.UtcNow;
        var first = await AddManualListingAsync("concurrent-first", now.AddMinutes(-2)); var second = await AddManualListingAsync("concurrent-second", now.AddMinutes(-1));
        var firstStore = new PostgresManualScoringStore(_connectionString); var secondStore = new PostgresManualScoringStore(_connectionString);
        await firstStore.EnqueueAsync(first.Id, first.ExternalId, first.CanonicalUrl!, now.AddMinutes(-2)); await firstStore.EnqueueAsync(second.Id, second.ExternalId, second.CanonicalUrl!, now.AddMinutes(-1));

        var claims = await Task.WhenAll(firstStore.ClaimNextAsync(now, TimeSpan.FromMinutes(1)), secondStore.ClaimNextAsync(now, TimeSpan.FromMinutes(1)));

        Assert.All(claims, Assert.NotNull); Assert.NotEqual(claims[0]!.JobId, claims[1]!.JobId); Assert.Contains(first.Id, claims.Select(x => x!.ListingId)); Assert.Contains(second.Id, claims.Select(x => x!.ListingId));
    }

    [Fact(Skip = "requires TEST_DATABASE_URL", SkipUnless = nameof(HasTestDatabaseUrl))]
    public async Task Enqueue_fences_active_lease_and_reschedules_new_source_identity()
    {
        var now = DateTimeOffset.UtcNow; var listing = await AddManualListingAsync("identity-fence", now); var store = new PostgresManualScoringStore(_connectionString);
        await store.EnqueueAsync(listing.Id, "manual:old", "https://example.test/old", now); var oldLease = await store.ClaimNextAsync(now, TimeSpan.FromMinutes(1)); Assert.NotNull(oldLease);

        await store.EnqueueAsync(listing.Id, "manual:new", "https://example.test/new", now.AddMinutes(1));

        Assert.False(await store.RecordCompletionAsync(oldLease!, now.AddMinutes(1)));
        var replacement = await store.ClaimNextAsync(now.AddMinutes(1), TimeSpan.FromMinutes(1));
        Assert.NotNull(replacement); Assert.True(replacement!.LeaseFence > oldLease.LeaseFence); Assert.Equal("manual:new", replacement.SourceExternalId); Assert.Equal("https://example.test/new", replacement.SourceCanonicalUrl);
    }

    [Fact(Skip = "requires TEST_DATABASE_URL", SkipUnless = nameof(HasTestDatabaseUrl))]
    public async Task Lease_validity_uses_database_current_timestamp_not_worker_clock()
    {
        var now = DateTimeOffset.UtcNow; var listing = await AddManualListingAsync("database-clock", now); var store = new PostgresManualScoringStore(_connectionString);
        await store.EnqueueAsync(listing.Id, listing.ExternalId, listing.CanonicalUrl!, now); Assert.NotNull(await store.ClaimNextAsync(now, TimeSpan.FromMinutes(1)));

        var stolenWithFutureWorkerClock = await store.ClaimNextAsync(now.AddYears(1), TimeSpan.FromMinutes(1));

        Assert.Null(stolenWithFutureWorkerClock);
    }

    [Fact(Skip = "requires TEST_DATABASE_URL", SkipUnless = nameof(HasTestDatabaseUrl))]
    public async Task Reclaimed_job_rejects_completion_and_failure_from_stale_lease_holder()
    {
        var now = DateTimeOffset.UtcNow; var listing = await AddManualListingAsync("fenced", now); var store = new PostgresManualScoringStore(_connectionString);
        await store.EnqueueAsync(listing.Id, listing.ExternalId, listing.CanonicalUrl!, now);
        var first = await store.ClaimNextAsync(now, TimeSpan.FromMinutes(1)); Assert.NotNull(first);
        await using (var connection = new NpgsqlConnection(_connectionString))
        {
            await connection.OpenAsync(); await using var expire = new NpgsqlCommand("UPDATE manual_scoring_jobs SET \"LeaseExpiresAt\" = CURRENT_TIMESTAMP - make_interval(secs => 1) WHERE \"Id\" = @id", connection); expire.Parameters.AddWithValue("id", first!.JobId); await expire.ExecuteNonQueryAsync();
        }
        var second = await store.ClaimNextAsync(now.AddMinutes(2), TimeSpan.FromMinutes(1));
        Assert.NotNull(first); Assert.NotNull(second); Assert.True(second!.LeaseFence > first!.LeaseFence); Assert.False(await store.RecordCompletionAsync(first, now.AddMinutes(2))); Assert.False(await store.RecordFailureAsync(first, "retry", "stale", now.AddMinutes(2), now.AddMinutes(3), terminal: false)); Assert.True(await store.RecordCompletionAsync(second, now.AddMinutes(2))); Assert.Null(await store.ClaimNextAsync(now.AddMinutes(3), TimeSpan.FromMinutes(1)));
    }

    [Fact(Skip = "requires TEST_DATABASE_URL", SkipUnless = nameof(HasTestDatabaseUrl))]
    public async Task Failure_records_retry_or_terminal_marker_and_terminal_job_is_never_claimed()
    {
        var now = DateTimeOffset.UtcNow; var listing = await AddManualListingAsync("terminal", now); var store = new PostgresManualScoringStore(_connectionString);
        await store.EnqueueAsync(listing.Id, listing.ExternalId, listing.CanonicalUrl!, now); var initial = await store.ClaimNextAsync(now, TimeSpan.FromMinutes(1)); Assert.NotNull(initial);
        Assert.True(await store.RecordFailureAsync(initial!, "temporary", "try later", now, now.AddMinutes(5), terminal: false)); Assert.Null(await store.ClaimNextAsync(now.AddMinutes(1), TimeSpan.FromMinutes(1)));
        await using (var retryConnection = new NpgsqlConnection(_connectionString))
        {
            await retryConnection.OpenAsync(); await using var makeRetryable = new NpgsqlCommand("UPDATE manual_scoring_jobs SET \"NextAttemptAt\" = CURRENT_TIMESTAMP WHERE \"Id\" = @id", retryConnection); makeRetryable.Parameters.AddWithValue("id", initial.JobId); await makeRetryable.ExecuteNonQueryAsync();
        }
        var retry = await store.ClaimNextAsync(now.AddMinutes(5), TimeSpan.FromMinutes(1)); Assert.NotNull(retry); Assert.True(await store.RecordFailureAsync(retry!, "ambiguous", "two sources", now.AddMinutes(5), null, terminal: true)); Assert.Null(await store.ClaimNextAsync(now.AddDays(1), TimeSpan.FromMinutes(1)));
        await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync(); await using var command = new NpgsqlCommand("SELECT \"AttemptCount\", \"TerminalFailureAt\", \"NextAttemptAt\", \"LastErrorCode\" FROM manual_scoring_jobs WHERE \"ListingId\" = @listingId", connection); command.Parameters.AddWithValue("listingId", listing.Id); await using var reader = await command.ExecuteReaderAsync(); Assert.True(await reader.ReadAsync()); Assert.Equal(2, reader.GetInt32(0)); Assert.False(reader.IsDBNull(1)); Assert.True(reader.IsDBNull(2)); Assert.Equal("ambiguous", reader.GetString(3));
    }

    public async ValueTask InitializeAsync()
    {
        _connectionString = Environment.GetEnvironmentVariable("TEST_DATABASE_URL") ?? ""; if (string.IsNullOrWhiteSpace(_connectionString)) return;
        var database = new NpgsqlConnectionStringBuilder(_connectionString).Database; if (string.IsNullOrWhiteSpace(database) || !database.Contains("test", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("TEST_DATABASE_URL must name a dedicated database containing 'test'.");
        await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync(); await using (var reset = new NpgsqlCommand("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;", connection)) await reset.ExecuteNonQueryAsync(); await using var db = CreateDb(); await db.Database.MigrateAsync(); await db.Database.OpenConnectionAsync(); await ((NpgsqlConnection)db.Database.GetDbConnection()).ReloadTypesAsync();
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    private AppDbContext CreateDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_connectionString, n => n.MapEnum<MemberRole>("member_role").MapEnum<VoteChoice>("vote_choice").MapEnum<ListingState>("listing_state").MapEnum<ReasonTag>("reason_tag").MapEnum<OverrideAction>("override_action").MapEnum<CategoryRating>("category_rating").MapEnum<VoteCategory>("vote_category")).Options);
    private async Task<Listing> AddManualListingAsync(string suffix, DateTimeOffset at) { var member = new Member { Email = $"{suffix}@example.test" }; var listing = Listing.CreateManual($"https://example.test/{suffix}", $"{suffix}vej 1", member.Id, at); await using var db = CreateDb(); db.AddRange(member, listing); await db.SaveChangesAsync(); return listing; }
}
