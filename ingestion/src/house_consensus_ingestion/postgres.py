from __future__ import annotations

from collections.abc import Callable
from datetime import datetime
from typing import Protocol

from .identity import RunSnapshot


class _Cursor(Protocol):
    def __enter__(self) -> _Cursor: ...
    def __exit__(self, *args: object) -> None: ...
    def execute(self, statement: str, parameters: tuple[object, ...]) -> None: ...
    def fetchone(self) -> tuple[object, ...] | None: ...


class _Connection(Protocol):
    def __enter__(self) -> _Connection: ...
    def __exit__(self, *args: object) -> None: ...
    def cursor(self) -> _Cursor: ...
    def commit(self) -> None: ...


class IngestionRunConflictError(RuntimeError):
    """A deterministic run ID is already bound to different immutable provenance."""


class PostgresRunWriter:
    def __init__(self, connection_factory: Callable[[], _Connection]) -> None:
        self._connection_factory = connection_factory

    def write_started_run(
        self,
        *,
        snapshot: RunSnapshot,
        requested_at: datetime,
    ) -> None:
        with self._connection_factory() as connection:
            with connection.cursor() as cursor:
                cursor.execute(
                    """
                    INSERT INTO ingestion_runs
                        (run_id, source_system, source_scope, requested_at, started_at, run_status, manifest_sha256)
                    VALUES (%s, %s, %s, %s, %s, %s, %s)
                    ON CONFLICT (run_id) DO NOTHING
                    RETURNING source_system, source_scope, manifest_sha256
                    """,
                    (
                        snapshot.run_id,
                        snapshot.source_system,
                        snapshot.source_scope,
                        requested_at,
                        requested_at,
                        "running",
                        snapshot.manifest_sha256,
                    ),
                )
                provenance = cursor.fetchone()
                if provenance is None:
                    cursor.execute(
                        """
                        SELECT source_system, source_scope, manifest_sha256
                        FROM ingestion_runs
                        WHERE run_id = %s
                        FOR KEY SHARE
                        """,
                        (snapshot.run_id,),
                    )
                    provenance = cursor.fetchone()
                if provenance != (
                    snapshot.source_system,
                    snapshot.source_scope,
                    snapshot.manifest_sha256,
                ):
                    raise IngestionRunConflictError(
                        f"run ID {snapshot.run_id} conflicts with immutable provenance"
                    )
            connection.commit()
