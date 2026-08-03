using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;

[DbContext(typeof(AppDbContext)), Migration("202608030001_AddNoiseEvidenceStates")]
public sealed class AddNoiseEvidenceStates : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "RoadNoiseStatus" character varying(20) NULL;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "RoadNoiseLnightDb" double precision NULL;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "RoadNoiseLnightStatus" character varying(20) NULL;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "RailNoiseStatus" character varying(20) NULL;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "RailNoiseLnightDb" double precision NULL;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "RailNoiseLnightStatus" character varying(20) NULL;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "AirNoiseStatus" character varying(20) NULL;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "AirNoiseLnightDb" double precision NULL;
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "AirNoiseLnightStatus" character varying(20) NULL;
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='CK_listings_noise_statuses') THEN
    ALTER TABLE listings ADD CONSTRAINT "CK_listings_noise_statuses" CHECK (
      ("RoadNoiseStatus" IS NULL OR "RoadNoiseStatus" IN ('covered','no_contour','unavailable','stale','error')) AND
      ("RoadNoiseLnightStatus" IS NULL OR "RoadNoiseLnightStatus" IN ('covered','no_contour','unavailable','stale','error')) AND
      ("RailNoiseStatus" IS NULL OR "RailNoiseStatus" IN ('covered','no_contour','unavailable','stale','error')) AND
      ("RailNoiseLnightStatus" IS NULL OR "RailNoiseLnightStatus" IN ('covered','no_contour','unavailable','stale','error')) AND
      ("AirNoiseStatus" IS NULL OR "AirNoiseStatus" IN ('covered','no_contour','unavailable','stale','error')) AND
      ("AirNoiseLnightStatus" IS NULL OR "AirNoiseLnightStatus" IN ('covered','no_contour','unavailable','stale','error'))
    );
  END IF;
END $$;
""");

    protected override void Down(MigrationBuilder m) => m.Sql("""
ALTER TABLE listings DROP CONSTRAINT IF EXISTS "CK_listings_noise_statuses";
ALTER TABLE listings DROP COLUMN IF EXISTS "AirNoiseLnightStatus";
ALTER TABLE listings DROP COLUMN IF EXISTS "AirNoiseLnightDb";
ALTER TABLE listings DROP COLUMN IF EXISTS "AirNoiseStatus";
ALTER TABLE listings DROP COLUMN IF EXISTS "RailNoiseLnightStatus";
ALTER TABLE listings DROP COLUMN IF EXISTS "RailNoiseLnightDb";
ALTER TABLE listings DROP COLUMN IF EXISTS "RailNoiseStatus";
ALTER TABLE listings DROP COLUMN IF EXISTS "RoadNoiseLnightStatus";
ALTER TABLE listings DROP COLUMN IF EXISTS "RoadNoiseLnightDb";
ALTER TABLE listings DROP COLUMN IF EXISTS "RoadNoiseStatus";
""");
}
