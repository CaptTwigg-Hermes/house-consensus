using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;

[DbContext(typeof(AppDbContext)), Migration("202608040002_AddManualScoringQueue")]
public sealed class AddManualScoringQueue : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "ManualScoringRequestedAt" timestamptz;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "ManualScoringAttemptedAt" timestamptz;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "ManualScoringCompletedAt" timestamptz;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "ManualScoringError" character varying(1000);
UPDATE listings
SET "ManualScoringRequestedAt"=COALESCE("ManuallyAddedAt","ImportedAt",now())
WHERE "IsManuallyAdded"=true
  AND ("FamilyFitScore" IS NULL OR "CommuteJson" IS NULL)
  AND "ManualScoringCompletedAt" IS NULL
  AND "ManualScoringRequestedAt" IS NULL;
CREATE INDEX IF NOT EXISTS "IX_listings_ManualScoringPending"
ON listings ("ManualScoringRequestedAt")
WHERE "ManualScoringRequestedAt" IS NOT NULL AND "ManualScoringCompletedAt" IS NULL;
""");

    protected override void Down(MigrationBuilder m) =>
        throw new NotSupportedException("Manual scoring request history cannot be rolled back safely.");
}
