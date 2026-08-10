using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;

[DbContext(typeof(AppDbContext)), Migration("202608100002_AddAsbestosRoofAssessments")]
public sealed class AddAsbestosRoofAssessments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE listings ADD COLUMN IF NOT EXISTS "AsbestosRoofCorrection" character varying(20);
        DO $$ BEGIN
          IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'CK_listings_asbestos_roof_correction') THEN
            ALTER TABLE listings ADD CONSTRAINT "CK_listings_asbestos_roof_correction"
              CHECK ("AsbestosRoofCorrection" IS NULL OR "AsbestosRoofCorrection" IN ('Likely','Possible','NoIndication','Unknown'));
          END IF;
        END $$;

        CREATE TABLE IF NOT EXISTS asbestos_roof_assessments (
            id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            listing_id uuid NOT NULL REFERENCES listings("Id") ON DELETE RESTRICT,
            run_id text,
            status character varying(20) NOT NULL CHECK (status IN ('likely','possible','no_indication','unknown')),
            confidence double precision CHECK (confidence IS NULL OR confidence BETWEEN 0 AND 1),
            primary_source character varying(20),
            evidence jsonb NOT NULL,
            rule_version character varying(100) NOT NULL,
            source_fingerprint character(64) NOT NULL CHECK (source_fingerprint ~ '^[0-9a-f]{64}$'),
            assessed_at timestamptz NOT NULL,
            UNIQUE (listing_id, rule_version, source_fingerprint)
        );
        CREATE INDEX IF NOT EXISTS ix_asbestos_roof_assessments_latest
          ON asbestos_roof_assessments(listing_id, assessed_at DESC, id DESC);

        INSERT INTO asbestos_roof_assessments
          (listing_id, status, confidence, primary_source, evidence, rule_version, source_fingerprint, assessed_at)
        SELECT "Id", 'unknown', NULL, 'backfill',
          '[{"source":"backfill","signal":"evidence_unavailable"}]'::jsonb,
          'asbestos-roof-v1-backfill-pending', repeat('0', 64), 'epoch'::timestamptz
        FROM listings
        WHERE "State" <> 'archived'
          AND NOT EXISTS (SELECT 1 FROM asbestos_roof_assessments a WHERE a.listing_id = listings."Id");
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TABLE IF EXISTS asbestos_roof_assessments;
        ALTER TABLE listings DROP CONSTRAINT IF EXISTS "CK_listings_asbestos_roof_correction";
        ALTER TABLE listings DROP COLUMN IF EXISTS "AsbestosRoofCorrection";
        """);
}
