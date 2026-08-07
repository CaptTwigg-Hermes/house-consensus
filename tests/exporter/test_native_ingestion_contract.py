from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
MIGRATION = ROOT / "src/Server/Data/Migrations/202608070001_AddNativeIngestionContract.cs"
SCHEMA = ROOT / "exporter/src/consensus_exporter/schema.sql"


def test_native_ingestion_contract_persists_runs_snapshots_and_stage_outcomes_in_postgres():
    """The application migration and isolated PostgreSQL bootstrap expose one durable contract."""
    assert MIGRATION.exists()
    migration = MIGRATION.read_text()
    schema = SCHEMA.read_text()

    for contract in (migration, schema):
        assert "CREATE TABLE IF NOT EXISTS ingestion_runs" in contract
        assert "CREATE TABLE IF NOT EXISTS ingestion_source_snapshots" in contract
        assert "CREATE TABLE IF NOT EXISTS ingestion_stage_outcomes" in contract
        assert "payload jsonb NOT NULL" in contract
        assert "snapshot_sha256 ~ '^[0-9a-f]{64}$'" in contract
        assert "FOREIGN KEY (run_id) REFERENCES ingestion_runs(run_id) ON DELETE RESTRICT" in contract
        assert "UNIQUE (run_id, source_name, snapshot_sha256)" in contract
        assert "UNIQUE (run_id, stage_name, attempt)" in contract
        assert "stage_status IN ('succeeded','failed','skipped')" in contract
        assert "run_status IN ('running','succeeded','failed','cancelled')" in contract
