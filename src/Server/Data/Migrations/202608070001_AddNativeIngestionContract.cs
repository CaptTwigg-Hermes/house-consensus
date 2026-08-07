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
""");

    protected override void Down(MigrationBuilder m) =>
        throw new NotSupportedException("Native ingestion records are immutable audit facts and cannot be rolled back safely.");
}
