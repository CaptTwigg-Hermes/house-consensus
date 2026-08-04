using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;

[DbContext(typeof(AppDbContext)), Migration("202608040001_AddReconciliationOutcome")]
public sealed class AddReconciliationOutcome : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
DO $$ DECLARE max_ordinal bigint; BEGIN
  IF to_regclass('export_runs') IS NOT NULL THEN
    ALTER TABLE export_runs ADD COLUMN IF NOT EXISTS reconciliation_status text NOT NULL DEFAULT 'running';
    ALTER TABLE export_runs ADD COLUMN IF NOT EXISTS archival_candidate_count integer NOT NULL DEFAULT 0;
    ALTER TABLE export_runs ADD COLUMN IF NOT EXISTS archival_blocked_count integer NOT NULL DEFAULT 0;
    ALTER TABLE export_runs ADD COLUMN IF NOT EXISTS archived_count integer NOT NULL DEFAULT 0;
    CREATE SEQUENCE IF NOT EXISTS export_run_completion_ordinal_seq AS bigint;
    ALTER TABLE export_runs ADD COLUMN IF NOT EXISTS completion_ordinal bigint;
    WITH base AS (
      SELECT COALESCE(MAX(completion_ordinal), 0) AS ordinal FROM export_runs
    ), ranked AS (
      SELECT run_id, ROW_NUMBER() OVER (
        ORDER BY completed_at, fetched_at, run_id
      ) AS ordinal
      FROM export_runs
      WHERE completed_at IS NOT NULL AND completion_ordinal IS NULL
    )
    UPDATE export_runs e
      SET completion_ordinal = base.ordinal + ranked.ordinal
      FROM base, ranked
      WHERE e.run_id = ranked.run_id;
    SELECT MAX(completion_ordinal) INTO max_ordinal FROM export_runs;
      IF max_ordinal IS NULL THEN
        PERFORM setval('export_run_completion_ordinal_seq', 1, false);
      ELSE
        PERFORM setval(
          'export_run_completion_ordinal_seq',
          GREATEST(
            max_ordinal,
            (SELECT last_value FROM export_run_completion_ordinal_seq)
          ),
          true
        );
      END IF;
    UPDATE export_runs SET reconciliation_status='outcome_unknown'
      WHERE completed_at IS NOT NULL AND reconciliation_status='running';
    CREATE UNIQUE INDEX IF NOT EXISTS ux_export_runs_completion_ordinal
      ON export_runs(completion_ordinal) WHERE completion_ordinal IS NOT NULL;
    IF NOT EXISTS (
      SELECT 1 FROM pg_constraint
      WHERE conname='ck_export_runs_completion_pair'
      AND conrelid='export_runs'::regclass
    ) THEN
      ALTER TABLE export_runs ADD CONSTRAINT ck_export_runs_completion_pair
        CHECK ((completed_at IS NULL) = (completion_ordinal IS NULL));
    END IF;
  END IF;
END $$;
""");

    protected override void Down(MigrationBuilder m) =>
        throw new NotSupportedException("Reconciliation outcome history cannot be rolled back safely.");
}
