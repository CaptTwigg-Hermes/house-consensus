from __future__ import annotations

from datetime import UTC, datetime


class Cursor:
    def __init__(self) -> None:
        self.executed: list[tuple[str, tuple[object, ...]]] = []

    def __enter__(self) -> Cursor:
        return self

    def __exit__(self, *_: object) -> None:
        return None

    def execute(self, statement: str, parameters: tuple[object, ...]) -> None:
        self.executed.append((statement, parameters))


class Connection:
    def __init__(self) -> None:
        self.cursor_instance = Cursor()
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

    connection = Connection()
    snapshot = build_snapshot(
        source_scope="boliga.dk",
        records=[{"external_id": "1", "address": "One Street 1"}],
    )

    PostgresRunWriter(connection_factory=lambda: connection).write_started_run(
        snapshot=snapshot,
        fetched_at=datetime(2026, 8, 7, tzinfo=UTC),
        source_config_sha256=None,
    )

    statement, parameters = connection.cursor_instance.executed[0]
    assert "INSERT INTO export_runs" in statement
    assert parameters == (
        snapshot.run_id,
        "boliga.dk",
        datetime(2026, 8, 7, tzinfo=UTC),
        1,
        snapshot.manifest_sha256,
        None,
    )
    assert connection.committed is True
