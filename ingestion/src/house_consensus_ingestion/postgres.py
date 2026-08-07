from __future__ import annotations

from collections.abc import Callable
from datetime import datetime
from typing import Protocol

from .identity import RunSnapshot


class _Cursor(Protocol):
    def __enter__(self) -> _Cursor: ...
    def __exit__(self, *args: object) -> None: ...
    def execute(self, statement: str, parameters: tuple[object, ...]) -> None: ...


class _Connection(Protocol):
    def __enter__(self) -> _Connection: ...
    def __exit__(self, *args: object) -> None: ...
    def cursor(self) -> _Cursor: ...
    def commit(self) -> None: ...


class PostgresRunWriter:
    def __init__(self, connection_factory: Callable[[], _Connection]) -> None:
        self._connection_factory = connection_factory

    def write_started_run(
        self,
        *,
        snapshot: RunSnapshot,
        fetched_at: datetime,
        source_config_sha256: str | None,
    ) -> None:
        with self._connection_factory() as connection:
            with connection.cursor() as cursor:
                cursor.execute(
                    """
                    INSERT INTO export_runs
                        (run_id, source_scope, fetched_at, snapshot_count, manifest_sha256, source_config_sha256)
                    VALUES (%s, %s, %s, %s, %s, %s)
                    ON CONFLICT (run_id) DO NOTHING
                    """,
                    (
                        snapshot.run_id,
                        snapshot.source_scope,
                        fetched_at,
                        snapshot.snapshot_count,
                        snapshot.manifest_sha256,
                        source_config_sha256,
                    ),
                )
            connection.commit()
