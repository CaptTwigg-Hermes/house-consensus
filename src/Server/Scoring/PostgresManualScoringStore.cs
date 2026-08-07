using Npgsql;
using NpgsqlTypes;

namespace HouseConsensus.Server.Scoring;

public sealed record ManualScoringLease(
    Guid JobId,
    Guid ListingId,
    string SourceExternalId,
    string SourceCanonicalUrl,
    DateTimeOffset RequestedAt,
    long LeaseFence,
    DateTimeOffset LeaseExpiresAt);

/// <summary>PostgreSQL-backed, lease-fenced persistence boundary for manual scoring workers.</summary>
public sealed class PostgresManualScoringStore(string connectionString)
{
    private readonly string _connectionString = string.IsNullOrWhiteSpace(connectionString)
        ? throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString))
        : connectionString;

    public async Task EnqueueAsync(Guid listingId, string sourceExternalId, string sourceCanonicalUrl, DateTimeOffset requestedAt, CancellationToken ct = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await EnqueueAsync(connection, null, listingId, sourceExternalId, sourceCanonicalUrl, requestedAt, ct);
    }

    public async Task EnqueueAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, Guid listingId, string sourceExternalId, string sourceCanonicalUrl, DateTimeOffset requestedAt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceExternalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCanonicalUrl);
        await using var command = new NpgsqlCommand("""
INSERT INTO manual_scoring_jobs ("Id", "ListingId", "SourceExternalId", "SourceCanonicalUrl", "RequestedAt", "NextAttemptAt")
VALUES (@id, @listingId, @externalId, @canonicalUrl, @requestedAt, CURRENT_TIMESTAMP)
ON CONFLICT ("ListingId") DO UPDATE
SET "SourceExternalId" = EXCLUDED."SourceExternalId",
    "SourceCanonicalUrl" = EXCLUDED."SourceCanonicalUrl",
    "NextAttemptAt" = CURRENT_TIMESTAMP,
    "LeaseFence" = CASE WHEN manual_scoring_jobs."LeaseExpiresAt" > CURRENT_TIMESTAMP THEN manual_scoring_jobs."LeaseFence" + 1 ELSE manual_scoring_jobs."LeaseFence" END,
    "LeaseExpiresAt" = NULL,
    "LastErrorCode" = NULL,
    "LastErrorMessage" = NULL
WHERE manual_scoring_jobs."CompletedAt" IS NULL AND manual_scoring_jobs."TerminalFailureAt" IS NULL;
""", connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid()); command.Parameters.AddWithValue("listingId", listingId);
        command.Parameters.AddWithValue("externalId", sourceExternalId); command.Parameters.AddWithValue("canonicalUrl", sourceCanonicalUrl);
        command.Parameters.AddWithValue("requestedAt", requestedAt);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<ManualScoringLease?> ClaimNextAsync(DateTimeOffset now, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        if (leaseDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
WITH candidate AS (
    SELECT "Id"
    FROM manual_scoring_jobs
    WHERE "CompletedAt" IS NULL
      AND "TerminalFailureAt" IS NULL
      AND "NextAttemptAt" <= CURRENT_TIMESTAMP
      AND ("LeaseExpiresAt" IS NULL OR "LeaseExpiresAt" <= CURRENT_TIMESTAMP)
    ORDER BY "RequestedAt", "Id"
    FOR UPDATE SKIP LOCKED
    LIMIT 1
)
UPDATE manual_scoring_jobs AS job
SET "LeaseFence" = job."LeaseFence" + 1,
    "LeaseExpiresAt" = CURRENT_TIMESTAMP + @leaseDuration,
    "LastAttemptAt" = CURRENT_TIMESTAMP,
    "AttemptCount" = job."AttemptCount" + 1
FROM candidate
WHERE job."Id" = candidate."Id"
RETURNING job."Id", job."ListingId", job."SourceExternalId", job."SourceCanonicalUrl", job."RequestedAt", job."LeaseFence", job."LeaseExpiresAt";
""", connection);
        command.Parameters.Add(new NpgsqlParameter("leaseDuration", NpgsqlDbType.Interval) { Value = leaseDuration });
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new ManualScoringLease(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetInt64(5), reader.GetFieldValue<DateTimeOffset>(6));
    }

    public Task<bool> RecordCompletionAsync(ManualScoringLease lease, DateTimeOffset completedAt, CancellationToken ct = default) =>
        FinalizeAsync(lease, completedAt, null, null, null, false, ct);

    public Task<bool> RecordFailureAsync(ManualScoringLease lease, string errorCode, string errorMessage, DateTimeOffset attemptedAt, DateTimeOffset? retryAt, bool terminal, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode); ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        if (terminal != !retryAt.HasValue) throw new ArgumentException("Terminal failures must not have a retry time, and retryable failures must have one.", nameof(retryAt));
        return FinalizeAsync(lease, attemptedAt, errorCode, errorMessage, retryAt, terminal, ct);
    }

    private async Task<bool> FinalizeAsync(ManualScoringLease lease, DateTimeOffset at, string? errorCode, string? errorMessage, DateTimeOffset? retryAt, bool terminal, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
UPDATE manual_scoring_jobs
SET "CompletedAt" = CASE WHEN @isCompletion THEN @at ELSE "CompletedAt" END,
    "TerminalFailureAt" = CASE WHEN @terminal THEN @at ELSE "TerminalFailureAt" END,
    "NextAttemptAt" = CASE WHEN @isCompletion OR @terminal THEN NULL ELSE @retryAt END,
    "LeaseExpiresAt" = NULL,
    "LastErrorCode" = @errorCode,
    "LastErrorMessage" = @errorMessage
WHERE "Id" = @id
  AND "LeaseFence" = @leaseFence
  AND "LeaseExpiresAt" > CURRENT_TIMESTAMP
  AND "CompletedAt" IS NULL
  AND "TerminalFailureAt" IS NULL;
""", connection);
        command.Parameters.AddWithValue("isCompletion", errorCode is null); command.Parameters.AddWithValue("terminal", terminal);
        command.Parameters.AddWithValue("at", at);
        command.Parameters.Add(new NpgsqlParameter("retryAt", NpgsqlDbType.TimestampTz) { Value = (object?)retryAt ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("errorCode", NpgsqlDbType.Varchar) { Value = (object?)errorCode ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("errorMessage", NpgsqlDbType.Varchar) { Value = (object?)errorMessage ?? DBNull.Value });
        command.Parameters.AddWithValue("id", lease.JobId); command.Parameters.AddWithValue("leaseFence", lease.LeaseFence);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }
}
