from __future__ import annotations

from datetime import UTC, datetime

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
        result=(snapshot.source_system, snapshot.source_scope, snapshot.manifest_sha256)
    )

    PostgresRunWriter(connection_factory=lambda: connection).write_started_run(
        snapshot=snapshot,
        requested_at=datetime(2026, 8, 7, tzinfo=UTC),
    )

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
    assert "RETURNING source_system, source_scope, manifest_sha256" in statements
    assert "FOR KEY SHARE" in statements
