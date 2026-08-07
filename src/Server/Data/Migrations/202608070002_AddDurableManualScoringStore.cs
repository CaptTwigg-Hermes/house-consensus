using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;

[DbContext(typeof(AppDbContext)), Migration("202608070002_AddDurableManualScoringStore")]
public sealed class AddDurableManualScoringStore : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
CREATE TABLE IF NOT EXISTS manual_scoring_jobs (
    "Id" uuid PRIMARY KEY,
    "ListingId" uuid NOT NULL UNIQUE REFERENCES listings("Id") ON DELETE RESTRICT,
    "SourceExternalId" text NOT NULL,
    "SourceCanonicalUrl" character varying(2048) NOT NULL,
    "RequestedAt" timestamptz NOT NULL,
    "NextAttemptAt" timestamptz NULL,
    "LastAttemptAt" timestamptz NULL,
    "AttemptCount" integer NOT NULL DEFAULT 0 CHECK ("AttemptCount" >= 0),
    "LeaseFence" bigint NOT NULL DEFAULT 0 CHECK ("LeaseFence" >= 0),
    "LeaseExpiresAt" timestamptz NULL,
    "CompletedAt" timestamptz NULL,
    "TerminalFailureAt" timestamptz NULL,
    "LastErrorCode" character varying(100) NULL,
    "LastErrorMessage" character varying(1000) NULL,
    CHECK ("CompletedAt" IS NULL OR "TerminalFailureAt" IS NULL)
);
CREATE INDEX IF NOT EXISTS "IX_manual_scoring_jobs_Claim"
ON manual_scoring_jobs ("NextAttemptAt", "RequestedAt", "Id")
WHERE "CompletedAt" IS NULL AND "TerminalFailureAt" IS NULL;
INSERT INTO manual_scoring_jobs ("Id", "ListingId", "SourceExternalId", "SourceCanonicalUrl", "RequestedAt", "NextAttemptAt")
SELECT md5(random()::text || clock_timestamp()::text)::uuid, l."Id", l."ExternalId", COALESCE(l."CanonicalUrl", l."SourceUrl", ''), l."ManualScoringRequestedAt", l."ManualScoringRequestedAt"
FROM listings AS l
WHERE l."IsManuallyAdded" = true
  AND l."ManualScoringRequestedAt" IS NOT NULL
  AND l."ManualScoringCompletedAt" IS NULL
ON CONFLICT ("ListingId") DO NOTHING;
""");

    protected override void Down(MigrationBuilder m) =>
        throw new NotSupportedException("Durable manual scoring history cannot be rolled back safely.");
}
