using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;

[DbContext(typeof(AppDbContext)), Migration("202608030002_AddSourceConfigIdentity")]
public sealed class AddSourceConfigIdentity : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
DO $$ BEGIN
  IF to_regclass('export_runs') IS NOT NULL THEN
    ALTER TABLE export_runs ADD COLUMN IF NOT EXISTS source_config_sha256 text;
    IF NOT EXISTS (
      SELECT 1 FROM pg_constraint
      WHERE conname='ck_export_runs_source_config_sha256'
        AND conrelid = 'export_runs'::regclass
    ) THEN
      ALTER TABLE export_runs
        ADD CONSTRAINT ck_export_runs_source_config_sha256
        CHECK (
          source_config_sha256 IS NULL
          OR source_config_sha256 ~ '^[0-9a-f]{64}$'
        );
    END IF;
  END IF;
END $$;
""");

    protected override void Down(MigrationBuilder m) => m.Sql("""
DO $$ BEGIN
  IF to_regclass('export_runs') IS NOT NULL THEN
    ALTER TABLE export_runs
      DROP CONSTRAINT IF EXISTS ck_export_runs_source_config_sha256;
    ALTER TABLE export_runs DROP COLUMN IF EXISTS source_config_sha256;
  END IF;
END $$;
""");
}
