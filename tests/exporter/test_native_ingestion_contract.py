import os
from pathlib import Path

import psycopg
import pytest

from consensus_exporter.postgres import ensure_schema


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


def test_native_ingestion_contract_copies_define_the_same_immutability_guards():
    """The EF migration and exporter bootstrap must protect the same audit facts."""
    migration = MIGRATION.read_text()
    schema = SCHEMA.read_text()
    required_guards = (
        "CREATE OR REPLACE FUNCTION reject_ingestion_audit_fact_mutation()",
        "CREATE TRIGGER ingestion_source_snapshots_immutable",
        "CREATE TRIGGER ingestion_stage_outcomes_immutable",
        "CREATE OR REPLACE FUNCTION enforce_ingestion_run_lifecycle()",
        "CREATE TRIGGER ingestion_runs_lifecycle_guard",
    )

    for guard in required_guards:
        assert guard in migration
        assert guard in schema
    for contract in (migration, schema):
        assert "AS " + "$" * 2 in contract
        assert "$" * 2 + ";" in contract

    def normalized_guard_sql(contract: str) -> str:
        start = contract.index(required_guards[0])
        end = contract.rindex(
            "FOR EACH STATEMENT EXECUTE FUNCTION reject_ingestion_audit_fact_truncate();",
            start,
        ) + len("FOR EACH STATEMENT EXECUTE FUNCTION reject_ingestion_audit_fact_truncate();")
        return " ".join(contract[start:end].split())

    assert normalized_guard_sql(migration) == normalized_guard_sql(schema)


def test_native_ingestion_contract_copies_reject_child_facts_after_run_completion():
    """Snapshots and stage outcomes are only appendable while their parent run is running."""
    migration = MIGRATION.read_text()
    schema = SCHEMA.read_text()
    required_guards = (
        "CREATE OR REPLACE FUNCTION enforce_ingestion_child_fact_parent_running()",
        "CREATE TRIGGER ingestion_source_snapshots_parent_running",
        "BEFORE INSERT ON ingestion_source_snapshots",
        "CREATE TRIGGER ingestion_stage_outcomes_parent_running",
        "BEFORE INSERT ON ingestion_stage_outcomes",
    )

    for guard in required_guards:
        assert guard in migration
        assert guard in schema

    def normalized_guard_sql(contract: str) -> str:
        start = contract.index(required_guards[0])
        end = contract.index(
            "CREATE OR REPLACE FUNCTION reject_ingestion_audit_fact_truncate()",
            start,
        )
        return " ".join(contract[start:end].split())

    assert normalized_guard_sql(migration) == normalized_guard_sql(schema)


def test_native_ingestion_contract_copies_reject_truncating_every_audit_table():
    """Both schema entry points make each native audit table append-only."""
    migration = MIGRATION.read_text()
    schema = SCHEMA.read_text()
    truncate_guards = (
        "CREATE OR REPLACE FUNCTION reject_ingestion_audit_fact_truncate()",
        "CREATE TRIGGER ingestion_runs_truncate_immutable\nBEFORE TRUNCATE ON ingestion_runs\nFOR EACH STATEMENT EXECUTE FUNCTION reject_ingestion_audit_fact_truncate();",
        "CREATE TRIGGER ingestion_source_snapshots_truncate_immutable\nBEFORE TRUNCATE ON ingestion_source_snapshots\nFOR EACH STATEMENT EXECUTE FUNCTION reject_ingestion_audit_fact_truncate();",
        "CREATE TRIGGER ingestion_stage_outcomes_truncate_immutable\nBEFORE TRUNCATE ON ingestion_stage_outcomes\nFOR EACH STATEMENT EXECUTE FUNCTION reject_ingestion_audit_fact_truncate();",
    )

    for guard in truncate_guards:
        assert guard in migration
        assert guard in schema


@pytest.fixture()
def database_url():
    url = os.environ.get("TEST_DATABASE_URL")
    if not url:
        pytest.skip("TEST_DATABASE_URL is not configured")
    with psycopg.connect(url, autocommit=True) as conn:
        conn.execute("drop schema public cascade")
        conn.execute("create schema public")
        ensure_schema(conn)
    return url


def test_native_ingestion_contract_enforces_audit_immutability_and_run_lifecycle(database_url):
    """PostgreSQL itself rejects audit rewrites while admitting only run completion."""
    run_id = "00000000-0000-0000-0000-000000000001"
    snapshot_id = "00000000-0000-0000-0000-000000000002"
    requested_at = "2026-08-07T12:00:00Z"
    completed_at = "2026-08-07T12:01:00Z"
    digest = "a" * 64

    with psycopg.connect(database_url, autocommit=True) as conn:
        conn.execute(
            """INSERT INTO ingestion_runs
            (run_id, source_system, source_scope, requested_at, run_status, manifest_sha256)
            VALUES (%s, 'native', 'all', %s, 'running', %s)""",
            (run_id, requested_at, digest),
        )
        conn.execute(
            """INSERT INTO ingestion_source_snapshots
            (snapshot_id, run_id, source_name, snapshot_sha256, payload, captured_at)
            VALUES (%s, %s, 'source', %s, '{}', %s)""",
            (snapshot_id, run_id, digest, requested_at),
        )
        conn.execute(
            """INSERT INTO ingestion_stage_outcomes
            (run_id, stage_name, attempt, stage_status, started_at, completed_at)
            VALUES (%s, 'fetch', 1, 'succeeded', %s, %s)""",
            (run_id, requested_at, completed_at),
        )

        for statement in (
            "UPDATE ingestion_source_snapshots SET source_name='rewritten'",
            "DELETE FROM ingestion_source_snapshots",
            "UPDATE ingestion_stage_outcomes SET stage_status='failed'",
            "DELETE FROM ingestion_stage_outcomes",
            "UPDATE ingestion_runs SET source_scope='rewritten'",
            "DELETE FROM ingestion_runs",
        ):
            with pytest.raises(psycopg.errors.RaiseException):
                conn.execute(statement)

        with pytest.raises(psycopg.errors.RaiseException):
            conn.execute(
                """UPDATE ingestion_runs
                SET run_status='succeeded', completed_at=%s, manifest_sha256=%s""",
                (completed_at, "b" * 64),
            )

        conn.execute(
            """UPDATE ingestion_runs
            SET run_status='succeeded', completed_at=%s
            WHERE run_id=%s""",
            (completed_at, run_id),
        )
        assert conn.execute(
            "SELECT run_status, completed_at IS NOT NULL FROM ingestion_runs WHERE run_id=%s",
            (run_id,),
        ).fetchone() == ("succeeded", True)

        with pytest.raises(psycopg.errors.RaiseException):
            conn.execute("UPDATE ingestion_runs SET completed_at=%s", (requested_at,))


def test_native_ingestion_contract_rejects_child_facts_after_parent_run_is_terminal(database_url):
    """PostgreSQL rejects late child audit facts, not just application writers."""
    run_id = "00000000-0000-0000-0000-000000000011"
    requested_at = "2026-08-07T12:00:00Z"
    completed_at = "2026-08-07T12:01:00Z"
    digest = "a" * 64

    with psycopg.connect(database_url, autocommit=True) as conn:
        conn.execute(
            """INSERT INTO ingestion_runs
            (run_id, source_system, source_scope, requested_at, run_status, manifest_sha256)
            VALUES (%s, 'native', 'all', %s, 'running', %s)""",
            (run_id, requested_at, digest),
        )
        conn.execute(
            """UPDATE ingestion_runs
            SET run_status='succeeded', completed_at=%s
            WHERE run_id=%s""",
            (completed_at, run_id),
        )

        with pytest.raises(psycopg.errors.RaiseException, match="running parent run"):
            conn.execute(
                """INSERT INTO ingestion_source_snapshots
                (snapshot_id, run_id, source_name, snapshot_sha256, payload, captured_at)
                VALUES ('00000000-0000-0000-0000-000000000012', %s, 'source', %s, '{}', %s)""",
                (run_id, digest, completed_at),
            )
        with pytest.raises(psycopg.errors.RaiseException, match="running parent run"):
            conn.execute(
                """INSERT INTO ingestion_stage_outcomes
                (run_id, stage_name, attempt, stage_status, started_at, completed_at)
                VALUES (%s, 'fetch', 1, 'succeeded', %s, %s)""",
                (run_id, requested_at, completed_at),
            )


def test_native_ingestion_contract_rejects_truncating_every_audit_table(database_url):
    """PostgreSQL rejects TRUNCATE even though it bypasses row-level triggers."""
    with psycopg.connect(database_url, autocommit=True) as conn:
        for table in (
            "ingestion_runs",
            "ingestion_source_snapshots",
            "ingestion_stage_outcomes",
        ):
            with pytest.raises(psycopg.errors.RaiseException):
                conn.execute(f"TRUNCATE {table}")
