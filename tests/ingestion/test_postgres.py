from __future__ import annotations

from datetime import UTC, datetime
from pathlib import Path

import pytest


class Cursor:
    def __init__(self, result: tuple[str] | None = ("persisted",)) -> None:
        self.executed: list[tuple[str, tuple[object, ...]]] = []
        self.result = result

    def __enter__(self) -> Cursor:
        return self

    def __exit__(self, *_: object) -> None:
        return None

    def execute(self, statement: str, parameters: tuple[object, ...]) -> None:
        self.executed.append((statement, parameters))

    def fetchone(self) -> tuple[str] | None:
        return self.result


class Connection:
    def __init__(self, result: tuple[str] | None = ("persisted",)) -> None:
        self.cursor_instance = Cursor(result)
        self.committed = False

    def __enter__(self) -> Connection:
        return self

    def __exit__(self, *_: object) -> None:
        return None

    def cursor(self) -> Cursor:
        return self.cursor_instance

    def commit(self) -> None:
        self.committed = True


def test_write_started_run_uses_injected_native_postgres_connection() -> None:
    from house_consensus_ingestion.identity import build_snapshot
    from house_consensus_ingestion.postgres import PostgresRunWriter

    snapshot = build_snapshot(
        source_system="house-consensus-ingestion",
        source_scope="boliga.dk",
        records=[{"external_id": "1", "address": "One Street 1"}],
    )
    connection = Connection(
        result=(snapshot.source_system, snapshot.source_scope, snapshot.manifest_sha256, "running")
    )

    assert PostgresRunWriter(connection_factory=lambda: connection).write_started_run(
        snapshot=snapshot,
        requested_at=datetime(2026, 8, 7, tzinfo=UTC),
    ) == "running"

    statement, parameters = connection.cursor_instance.executed[0]
    assert "INSERT INTO ingestion_runs" in statement
    assert "source_system" in statement
    assert "source_scope" in statement
    assert "requested_at" in statement
    assert "started_at" in statement
    assert "run_status" in statement
    assert "manifest_sha256" in statement
    assert "export_runs" not in statement
    assert parameters == (
        snapshot.run_id,
        snapshot.source_system,
        "boliga.dk",
        datetime(2026, 8, 7, tzinfo=UTC),
        datetime(2026, 8, 7, tzinfo=UTC),
        "running",
        snapshot.manifest_sha256,
    )
    assert connection.committed is True


def test_write_started_run_rejects_a_conflicting_native_run_identity() -> None:
    from house_consensus_ingestion.identity import build_snapshot
    from house_consensus_ingestion.postgres import IngestionRunConflictError, PostgresRunWriter

    connection = Connection(result=None)
    snapshot = build_snapshot(
        source_system="house-consensus-ingestion",
        source_scope="boliga.dk",
        records=[{"external_id": "1", "address": "One Street 1"}],
    )

    with pytest.raises(IngestionRunConflictError, match="immutable provenance"):
        PostgresRunWriter(connection_factory=lambda: connection).write_started_run(
            snapshot=snapshot,
            requested_at=datetime(2026, 8, 7, tzinfo=UTC),
        )

    statements = "\n".join(statement for statement, _ in connection.cursor_instance.executed)
    assert "ON CONFLICT (run_id) DO NOTHING" in statements
    assert "DO UPDATE" not in statements
    assert "RETURNING source_system, source_scope, manifest_sha256, run_status" in statements
    assert "FOR KEY SHARE" in statements



def test_run_writer_persists_native_snapshot_stage_outcome_and_terminal_status() -> None:
    from house_consensus_ingestion.identity import build_snapshot
    from house_consensus_ingestion.postgres import PostgresRunWriter

    snapshot = build_snapshot(source_scope="boligsiden.dk/open-cases", records=[{"caseID": "42"}])
    connection = Connection(result=(snapshot.source_system, snapshot.source_scope, snapshot.manifest_sha256))
    writer = PostgresRunWriter(lambda: connection)
    now = datetime(2026, 8, 7, tzinfo=UTC)

    snapshot_id = writer.write_source_snapshot(
        snapshot=snapshot, source_name="boligsiden-search-cases", payload={"records": [{"caseID": "42"}]}, captured_at=now,
    )
    writer.write_stage_outcome(
        snapshot=snapshot, stage_name="fetch", stage_status="succeeded", outcome={"record_count": 1}, started_at=now, completed_at=now,
    )
    writer.complete_run(snapshot=snapshot, run_status="succeeded", completed_at=now)

    statements = "\n".join(statement for statement, _ in connection.cursor_instance.executed)
    assert snapshot_id
    assert "INSERT INTO ingestion_source_snapshots" in statements
    assert "INSERT INTO ingestion_stage_outcomes" in statements
    assert "UPDATE ingestion_runs" in statements
    assert "run_status = %s" in statements
    assert "run_status = 'running'" in statements


def test_run_writer_persists_projection_outcome_after_completed_source_run() -> None:
    from house_consensus_ingestion.identity import build_snapshot
    from house_consensus_ingestion.postgres import PostgresRunWriter

    snapshot = build_snapshot(source_scope="boligsiden.dk/open-cases", records=[{"caseID": "42"}])
    connection = Connection()
    writer = PostgresRunWriter(lambda: connection)
    now = datetime(2026, 8, 7, tzinfo=UTC)

    writer.write_projection_outcome(
        snapshot=snapshot, source_snapshot_id="00000000-0000-0000-0000-000000000011",
        projection_status="failed", outcome={"error": "listing lock timeout"},
        started_at=now, completed_at=now,
    )

    statements = connection.cursor_instance.executed
    assert "FOR NO KEY UPDATE OF r" in statements[0][0]
    statement, parameters = statements[1]
    assert "INSERT INTO ingestion_projection_outcomes" in statement
    assert "MAX(attempt)" in statement
    assert "ingestion_stage_outcomes" not in statement
    assert parameters[0] == snapshot.run_id
    assert parameters[1] == "00000000-0000-0000-0000-000000000011"
    assert parameters[2] == "failed"
    assert connection.committed is True


def test_projection_outcome_migration_keeps_completed_source_audit_separate_from_child_facts() -> None:
    migration = (Path(__file__).parents[2] / "src/Server/Data/Migrations/202608230001_AddIngestionProjectionOutcomes.cs").read_text()

    assert "CREATE TABLE IF NOT EXISTS ingestion_projection_outcomes" in migration
    assert "source_snapshot_id uuid NOT NULL" in migration
    assert "projection_status IN ('succeeded','failed')" in migration
    assert "enforce_ingestion_child_fact_parent_running" not in migration
    assert "GRANT SELECT, INSERT ON ingestion_projection_outcomes TO house_consensus" in migration
    bootstrap_schema = (Path(__file__).parents[2] / "exporter/src/consensus_exporter/schema.sql").read_text()
    assert "CREATE TABLE IF NOT EXISTS ingestion_projection_outcomes" in bootstrap_schema
    assert "enforce_ingestion_projection_outcome_source" in bootstrap_schema


def test_run_writer_rejects_projection_outcomes_for_a_non_succeeded_source_run() -> None:
    from house_consensus_ingestion.identity import build_snapshot
    from house_consensus_ingestion.postgres import IngestionProjectionLifecycleError, PostgresRunWriter

    snapshot = build_snapshot(source_scope="boligsiden.dk/open-cases", records=[{"caseID": "42"}])
    writer = PostgresRunWriter(lambda: Connection(result=None))

    with pytest.raises(IngestionProjectionLifecycleError, match="completed succeeded source"):
        writer.write_projection_outcome(
            snapshot=snapshot, source_snapshot_id="00000000-0000-0000-0000-000000000011",
            projection_status="failed", outcome={"error": "listing lock timeout"},
            started_at=datetime(2026, 8, 7, tzinfo=UTC), completed_at=datetime(2026, 8, 7, tzinfo=UTC),
        )


def test_projection_gate_uses_a_non_conflicting_parent_lock_for_the_separate_projector_connection() -> None:
    source = (Path(__file__).parents[2] / "ingestion/src/house_consensus_ingestion/postgres.py").read_text()
    assert "FOR NO KEY UPDATE OF r" in source
    assert "FOR UPDATE OF r" not in source
