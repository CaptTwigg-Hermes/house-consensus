using HouseConsensus.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace HouseConsensus.Server.Data.Migrations;

[DbContext(typeof(AppDbContext)), Migration("202608070001_AddNativeIngestionContract")]
public sealed class AddNativeIngestionContract : Migration
{
    protected override void Up(MigrationBuilder m) => m.Sql("""
CREATE TABLE IF NOT EXISTS ingestion_runs (
    run_id uuid PRIMARY KEY,
    source_system text NOT NULL CHECK (length(btrim(source_system)) > 0),
    source_scope text NOT NULL CHECK (length(btrim(source_scope)) > 0),
    requested_at timestamptz NOT NULL,
    started_at timestamptz,
    completed_at timestamptz,
    run_status text NOT NULL CHECK (run_status IN ('running','succeeded','failed','cancelled')),
    manifest_sha256 text NOT NULL CHECK (manifest_sha256 ~ '^[0-9a-f]{64}$'),
    CHECK ((run_status = 'running' AND completed_at IS NULL) OR (run_status <> 'running' AND completed_at IS NOT NULL))
);

CREATE TABLE IF NOT EXISTS ingestion_source_snapshots (
    snapshot_id uuid PRIMARY KEY,
    run_id uuid NOT NULL,
    source_name text NOT NULL CHECK (length(btrim(source_name)) > 0),
    snapshot_sha256 text NOT NULL CHECK (snapshot_sha256 ~ '^[0-9a-f]{64}$'),
    payload jsonb NOT NULL,
    captured_at timestamptz NOT NULL,
    FOREIGN KEY (run_id) REFERENCES ingestion_runs(run_id) ON DELETE RESTRICT,
    UNIQUE (run_id, source_name, snapshot_sha256)
);
CREATE INDEX IF NOT EXISTS ix_ingestion_source_snapshots_run_id
    ON ingestion_source_snapshots(run_id, captured_at);

CREATE TABLE IF NOT EXISTS ingestion_stage_outcomes (
    outcome_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    run_id uuid NOT NULL,
    stage_name text NOT NULL CHECK (length(btrim(stage_name)) > 0),
    attempt integer NOT NULL CHECK (attempt > 0),
    stage_status text NOT NULL CHECK (stage_status IN ('succeeded','failed','skipped')),
    outcome jsonb NOT NULL DEFAULT '{}'::jsonb,
    started_at timestamptz NOT NULL,
    completed_at timestamptz NOT NULL,
    FOREIGN KEY (run_id) REFERENCES ingestion_runs(run_id) ON DELETE RESTRICT,
    UNIQUE (run_id, stage_name, attempt),
    CHECK (completed_at >= started_at)
);
CREATE INDEX IF NOT EXISTS ix_ingestion_stage_outcomes_run_stage
    ON ingestion_stage_outcomes(run_id, stage_name, attempt DESC);

CREATE OR REPLACE FUNCTION reject_ingestion_audit_fact_mutation()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION '% records are immutable', TG_TABLE_NAME;
END;
$$;

CREATE OR REPLACE FUNCTION enforce_ingestion_run_lifecycle()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'ingestion runs cannot be deleted';
    END IF;

    IF NEW.run_id IS DISTINCT FROM OLD.run_id
       OR NEW.source_system IS DISTINCT FROM OLD.source_system
       OR NEW.source_scope IS DISTINCT FROM OLD.source_scope
       OR NEW.requested_at IS DISTINCT FROM OLD.requested_at
       OR NEW.started_at IS DISTINCT FROM OLD.started_at
       OR NEW.manifest_sha256 IS DISTINCT FROM OLD.manifest_sha256 THEN
        RAISE EXCEPTION 'ingestion run identity and provenance are immutable';
    END IF;

    IF OLD.run_status <> 'running'
       OR NEW.run_status NOT IN ('succeeded', 'failed', 'cancelled')
       OR NEW.completed_at IS NULL THEN
        RAISE EXCEPTION 'ingestion runs may only transition from running to a terminal status with a completion timestamp';
    END IF;

    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION reject_ingestion_audit_fact_truncate()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION '% records cannot be truncated', TG_TABLE_NAME;
END;
$$;

DROP TRIGGER IF EXISTS ingestion_runs_truncate_immutable ON ingestion_runs;
CREATE TRIGGER ingestion_runs_truncate_immutable
BEFORE TRUNCATE ON ingestion_runs
FOR EACH STATEMENT EXECUTE FUNCTION reject_ingestion_audit_fact_truncate();

DROP TRIGGER IF EXISTS ingestion_source_snapshots_truncate_immutable ON ingestion_source_snapshots;
CREATE TRIGGER ingestion_source_snapshots_truncate_immutable
BEFORE TRUNCATE ON ingestion_source_snapshots
FOR EACH STATEMENT EXECUTE FUNCTION reject_ingestion_audit_fact_truncate();

DROP TRIGGER IF EXISTS ingestion_stage_outcomes_truncate_immutable ON ingestion_stage_outcomes;
CREATE TRIGGER ingestion_stage_outcomes_truncate_immutable
BEFORE TRUNCATE ON ingestion_stage_outcomes
FOR EACH STATEMENT EXECUTE FUNCTION reject_ingestion_audit_fact_truncate();

DROP TRIGGER IF EXISTS ingestion_source_snapshots_immutable ON ingestion_source_snapshots;
CREATE TRIGGER ingestion_source_snapshots_immutable
BEFORE UPDATE OR DELETE ON ingestion_source_snapshots
FOR EACH ROW EXECUTE FUNCTION reject_ingestion_audit_fact_mutation();

DROP TRIGGER IF EXISTS ingestion_stage_outcomes_immutable ON ingestion_stage_outcomes;
CREATE TRIGGER ingestion_stage_outcomes_immutable
BEFORE UPDATE OR DELETE ON ingestion_stage_outcomes
FOR EACH ROW EXECUTE FUNCTION reject_ingestion_audit_fact_mutation();

DROP TRIGGER IF EXISTS ingestion_runs_lifecycle_guard ON ingestion_runs;
CREATE TRIGGER ingestion_runs_lifecycle_guard
BEFORE UPDATE OR DELETE ON ingestion_runs
FOR EACH ROW EXECUTE FUNCTION enforce_ingestion_run_lifecycle();
""");

    protected override void Down(MigrationBuilder m) =>
        throw new NotSupportedException("Native ingestion records are immutable audit facts and cannot be rolled back safely.");
}
