using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;

[DbContext(typeof(AppDbContext)), Migration("202608050001_AddScoreTrustProjection")]
public sealed class AddScoreTrustProjection : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "ScoreRuleVersion" character varying(100) NULL;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "ScoreCoveragePct" double precision NULL;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "FamilyPrivacyAvailable" boolean NULL;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "ScoreNotesJson" text NULL;
ALTER TABLE listings ALTER COLUMN "FamilyFitScore" DROP NOT NULL;
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='CK_listings_ScoreCoveragePct' AND conrelid='listings'::regclass) THEN
    ALTER TABLE listings ADD CONSTRAINT "CK_listings_ScoreCoveragePct"
      CHECK ("ScoreCoveragePct" IS NULL OR ("ScoreCoveragePct" >= 0 AND "ScoreCoveragePct" <= 100));
  END IF;
END $$;
""");

    protected override void Down(MigrationBuilder m) =>
        throw new NotSupportedException("Score provenance columns may contain audit evidence and cannot be rolled back safely.");
}
