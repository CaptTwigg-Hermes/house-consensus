from __future__ import annotations

from collections.abc import Callable, Mapping
from datetime import datetime
from hashlib import sha256
import json
from typing import Any, Protocol
from uuid import UUID

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

    def write_started_run(self, *, snapshot: RunSnapshot, requested_at: datetime) -> None:
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
                    (snapshot.run_id, snapshot.source_system, snapshot.source_scope, requested_at, requested_at, "running", snapshot.manifest_sha256),
                )
                provenance = cursor.fetchone()
                if provenance is None:
                    cursor.execute(
                        """SELECT source_system, source_scope, manifest_sha256
                        FROM ingestion_runs WHERE run_id = %s FOR KEY SHARE""", (snapshot.run_id,),
                    )
                    provenance = cursor.fetchone()
                if provenance != (snapshot.source_system, snapshot.source_scope, snapshot.manifest_sha256):
                    raise IngestionRunConflictError(f"run ID {snapshot.run_id} conflicts with immutable provenance")
            connection.commit()

    def write_source_snapshot(self, *, snapshot: RunSnapshot, source_name: str, payload: Mapping[str, Any], captured_at: datetime) -> str:
        canonical_payload = json.dumps(payload, ensure_ascii=False, separators=(",", ":"), sort_keys=True)
        snapshot_sha256 = sha256(canonical_payload.encode()).hexdigest()
        snapshot_id = str(UUID(bytes=sha256(snapshot_sha256.encode()).digest()[:16], version=5))
        with self._connection_factory() as connection:
            with connection.cursor() as cursor:
                cursor.execute(
                    """INSERT INTO ingestion_source_snapshots
                    (snapshot_id, run_id, source_name, snapshot_sha256, payload, captured_at)
                    VALUES (%s, %s, %s, %s, %s::jsonb, %s)
                    ON CONFLICT (run_id, source_name, snapshot_sha256) DO NOTHING""",
                    (snapshot_id, snapshot.run_id, source_name, snapshot_sha256, canonical_payload, captured_at),
                )
            connection.commit()
        return snapshot_id

    def write_stage_outcome(self, *, snapshot: RunSnapshot, stage_name: str, stage_status: str, outcome: Mapping[str, Any], started_at: datetime, completed_at: datetime) -> None:
        with self._connection_factory() as connection:
            with connection.cursor() as cursor:
                cursor.execute(
                    """INSERT INTO ingestion_stage_outcomes
                    (run_id, stage_name, attempt, stage_status, outcome, started_at, completed_at)
                    VALUES (%s, %s, 1, %s, %s::jsonb, %s, %s)
                    ON CONFLICT (run_id, stage_name, attempt) DO NOTHING""",
                    (snapshot.run_id, stage_name, stage_status, json.dumps(outcome, ensure_ascii=False, separators=(",", ":"), sort_keys=True), started_at, completed_at),
                )
            connection.commit()

    def complete_run(self, *, snapshot: RunSnapshot, run_status: str, completed_at: datetime) -> None:
        if run_status not in {"succeeded", "failed", "cancelled"}:
            raise ValueError("run status must be terminal")
        with self._connection_factory() as connection:
            with connection.cursor() as cursor:
                cursor.execute(
                    """UPDATE ingestion_runs SET run_status = %s, completed_at = %s
                    WHERE run_id = %s AND run_status = 'running'""",
                    (run_status, completed_at, snapshot.run_id),
                )
            connection.commit()
