using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;

[DbContext(typeof(AppDbContext)), Migration("202608230001_AddIngestionProjectionOutcomes")]
public sealed class AddIngestionProjectionOutcomes : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
CREATE TABLE IF NOT EXISTS ingestion_projection_outcomes (
    outcome_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    run_id uuid NOT NULL REFERENCES ingestion_runs(run_id) ON DELETE RESTRICT,
    source_snapshot_id uuid NOT NULL REFERENCES ingestion_source_snapshots(snapshot_id) ON DELETE RESTRICT,
    attempt integer NOT NULL CHECK (attempt > 0),
    projection_status text NOT NULL CHECK (projection_status IN ('succeeded','failed')),
    outcome jsonb NOT NULL DEFAULT '{}'::jsonb,
    started_at timestamptz NOT NULL,
    completed_at timestamptz NOT NULL,
    UNIQUE (run_id, source_snapshot_id, attempt),
    CHECK (completed_at >= started_at)
);
CREATE INDEX IF NOT EXISTS ix_ingestion_projection_outcomes_snapshot
    ON ingestion_projection_outcomes(source_snapshot_id, attempt DESC);

CREATE OR REPLACE FUNCTION enforce_ingestion_projection_outcome_source()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    PERFORM 1
    FROM ingestion_runs r
    JOIN ingestion_source_snapshots s ON s.run_id = r.run_id
    WHERE r.run_id = NEW.run_id
      AND s.snapshot_id = NEW.source_snapshot_id
      AND r.run_status = 'succeeded'
    FOR UPDATE OF r;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'projection outcomes require a completed succeeded source run and snapshot';
    END IF;
    RETURN NEW;
END;
$$;
DROP TRIGGER IF EXISTS ingestion_projection_outcome_source_guard ON ingestion_projection_outcomes;
CREATE TRIGGER ingestion_projection_outcome_source_guard
BEFORE INSERT ON ingestion_projection_outcomes
FOR EACH ROW EXECUTE FUNCTION enforce_ingestion_projection_outcome_source();

DROP TRIGGER IF EXISTS ingestion_projection_outcomes_immutable ON ingestion_projection_outcomes;
CREATE TRIGGER ingestion_projection_outcomes_immutable
BEFORE UPDATE OR DELETE ON ingestion_projection_outcomes
FOR EACH ROW EXECUTE FUNCTION reject_ingestion_audit_fact_mutation();
DROP TRIGGER IF EXISTS ingestion_projection_outcomes_truncate_immutable ON ingestion_projection_outcomes;
CREATE TRIGGER ingestion_projection_outcomes_truncate_immutable
BEFORE TRUNCATE ON ingestion_projection_outcomes
FOR EACH STATEMENT EXECUTE FUNCTION reject_ingestion_audit_fact_truncate();

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'house_consensus') THEN
        GRANT SELECT, INSERT ON ingestion_projection_outcomes TO house_consensus;
    END IF;
END
$$;
""");

    protected override void Down(MigrationBuilder m) =>
        throw new NotSupportedException("Projection outcomes are immutable audit facts and cannot be rolled back safely.");
}
