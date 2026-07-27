using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;
[DbContext(typeof(AppDbContext)), Migration("202607270010_AddPostGisSnapshotLifecycle")]
public sealed class AddPostGisSnapshotLifecycle : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
ALTER TABLE listings ADD COLUMN IF NOT EXISTS "FirstSeenAt" timestamptz;
DO $$ BEGIN
  IF to_regclass('listing_export_state') IS NOT NULL THEN
    ALTER TABLE listing_export_state ADD COLUMN IF NOT EXISTS missing_complete_snapshots integer NOT NULL DEFAULT 0;
    ALTER TABLE listing_export_state ADD COLUMN IF NOT EXISTS last_missing_snapshot_date date;
  END IF;
  IF to_regclass('export_runs') IS NOT NULL THEN
    ALTER TABLE export_runs ADD COLUMN IF NOT EXISTS snapshot_count integer;
    ALTER TABLE export_runs ADD COLUMN IF NOT EXISTS manifest_sha256 text;
  END IF;
END $$;
DO $$ BEGIN
  IF EXISTS (SELECT 1 FROM pg_available_extensions WHERE name='postgis') THEN
    CREATE EXTENSION IF NOT EXISTS postgis;
    EXECUTE 'ALTER TABLE listings ADD COLUMN IF NOT EXISTS "Location" geometry(Point,4326)';
    EXECUTE 'CREATE INDEX IF NOT EXISTS "IX_listings_Location" ON listings USING gist ("Location")';
    EXECUTE 'UPDATE listings SET "Location"=ST_SetSRID(ST_MakePoint("Longitude","Latitude"),4326) WHERE "Latitude" BETWEEN -90 AND 90 AND "Longitude" BETWEEN -180 AND 180';
  END IF;
END $$;
UPDATE listings SET "FirstSeenAt"=COALESCE("FirstSeenAt","ImportedAt" - interval '120 hours');
""");

    protected override void Down(MigrationBuilder m) =>
        throw new NotSupportedException("PostGIS and listing lifecycle history cannot be rolled back safely.");
}
