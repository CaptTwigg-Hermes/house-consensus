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


class IngestionProjectionLifecycleError(RuntimeError):
    """Projection outcomes require a completed succeeded source run and snapshot."""


class PostgresRunWriter:
    def __init__(self, connection_factory: Callable[[], _Connection]) -> None:
        self._connection_factory = connection_factory

    def write_started_run(self, *, snapshot: RunSnapshot, requested_at: datetime) -> str:
        with self._connection_factory() as connection:
            with connection.cursor() as cursor:
                cursor.execute(
                    """
                    INSERT INTO ingestion_runs
                        (run_id, source_system, source_scope, requested_at, started_at, run_status, manifest_sha256)
                    VALUES (%s, %s, %s, %s, %s, %s, %s)
                    ON CONFLICT (run_id) DO NOTHING
                    RETURNING source_system, source_scope, manifest_sha256, run_status
                    """,
                    (snapshot.run_id, snapshot.source_system, snapshot.source_scope, requested_at, requested_at, "running", snapshot.manifest_sha256),
                )
                provenance = cursor.fetchone()
                if provenance is None:
                    cursor.execute(
                        """SELECT source_system, source_scope, manifest_sha256, run_status
                        FROM ingestion_runs WHERE run_id = %s FOR KEY SHARE""", (snapshot.run_id,),
                    )
                    provenance = cursor.fetchone()
                if provenance is None or provenance[:3] != (
                    snapshot.source_system, snapshot.source_scope, snapshot.manifest_sha256
                ):
                    raise IngestionRunConflictError(f"run ID {snapshot.run_id} conflicts with immutable provenance")
                run_status = str(provenance[3])
            connection.commit()
        return run_status

    def write_source_snapshot(self, *, snapshot: RunSnapshot, source_name: str, payload: Mapping[str, Any], captured_at: datetime) -> str:
        canonical_payload = json.dumps(payload, ensure_ascii=False, separators=(",", ":"), sort_keys=True)
        snapshot_sha256 = sha256(canonical_payload.encode()).hexdigest()
        snapshot_id = str(UUID(bytes=sha256(f"{snapshot.run_id}:{source_name}:{snapshot_sha256}".encode()).digest()[:16], version=5))
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

    def source_snapshot_id(self, *, snapshot: RunSnapshot, source_name: str) -> str | None:
        with self._connection_factory() as connection:
            with connection.cursor() as cursor:
                cursor.execute(
                    """SELECT snapshot_id::text FROM ingestion_source_snapshots
                    WHERE run_id = %s AND source_name = %s
                    ORDER BY captured_at DESC, snapshot_id DESC
                    LIMIT 1""",
                    (snapshot.run_id, source_name),
                )
                found = cursor.fetchone()
        return str(found[0]) if found is not None else None

    def latest_projection_status(self, *, snapshot: RunSnapshot, source_snapshot_id: str) -> str | None:
        with self._connection_factory() as connection:
            with connection.cursor() as cursor:
                self._lock_completed_projection_source(
                    cursor=cursor, snapshot=snapshot, source_snapshot_id=source_snapshot_id,
                )
                cursor.execute(
                    """SELECT projection_status FROM ingestion_projection_outcomes
                    WHERE run_id = %s AND source_snapshot_id = %s
                    ORDER BY attempt DESC LIMIT 1""",
                    (snapshot.run_id, source_snapshot_id),
                )
                latest = cursor.fetchone()
            connection.commit()
        return str(latest[0]) if latest is not None else None

    def run_projection_once(
        self, *, snapshot: RunSnapshot, source_snapshot_id: str, projected_at: datetime,
        project: Callable[[], int],
    ) -> int:
        lock_key = f"{snapshot.run_id}:{source_snapshot_id}"
        with self._connection_factory() as connection:
            with connection.cursor() as cursor:
                cursor.execute("SELECT pg_advisory_xact_lock(hashtextextended(%s, 0))", (lock_key,))
                self._lock_completed_projection_source(
                    cursor=cursor, snapshot=snapshot, source_snapshot_id=source_snapshot_id,
                )
                cursor.execute(
                    """SELECT projection_status FROM ingestion_projection_outcomes
                    WHERE run_id = %s AND source_snapshot_id = %s
                    ORDER BY attempt DESC LIMIT 1""",
                    (snapshot.run_id, source_snapshot_id),
                )
                latest = cursor.fetchone()
                if latest is not None and str(latest[0]) == "succeeded":
                    connection.commit()
                    return 0
                try:
                    projected_count = project()
                except BaseException as error:
                    self._append_projection_outcome(
                        cursor=cursor, snapshot=snapshot, source_snapshot_id=source_snapshot_id,
                        projection_status="failed", outcome={"error": str(error)},
                        started_at=projected_at, completed_at=projected_at,
                    )
                    connection.commit()
                    raise
                self._append_projection_outcome(
                    cursor=cursor, snapshot=snapshot, source_snapshot_id=source_snapshot_id,
                    projection_status="succeeded", outcome={"projected_count": projected_count},
                    started_at=projected_at, completed_at=projected_at,
                )
            connection.commit()
        return projected_count

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

    def write_projection_outcome(
        self, *, snapshot: RunSnapshot, source_snapshot_id: str, projection_status: str,
        outcome: Mapping[str, Any], started_at: datetime, completed_at: datetime,
    ) -> None:
        if projection_status not in {"succeeded", "failed"}:
            raise ValueError("projection status must be succeeded or failed")
        with self._connection_factory() as connection:
            with connection.cursor() as cursor:
                self._lock_completed_projection_source(
                    cursor=cursor, snapshot=snapshot, source_snapshot_id=source_snapshot_id,
                )
                self._append_projection_outcome(
                    cursor=cursor, snapshot=snapshot, source_snapshot_id=source_snapshot_id,
                    projection_status=projection_status, outcome=outcome,
                    started_at=started_at, completed_at=completed_at,
                )
            connection.commit()


    @staticmethod
    def _append_projection_outcome(
        *, cursor: _Cursor, snapshot: RunSnapshot, source_snapshot_id: str,
        projection_status: str, outcome: Mapping[str, Any], started_at: datetime, completed_at: datetime,
    ) -> None:
        cursor.execute(
            """INSERT INTO ingestion_projection_outcomes
            (run_id, source_snapshot_id, attempt, projection_status, outcome, started_at, completed_at)
            SELECT %s, %s, COALESCE(MAX(attempt), 0) + 1, %s, %s::jsonb, %s, %s
            FROM ingestion_projection_outcomes
            WHERE run_id = %s AND source_snapshot_id = %s""",
            (
                snapshot.run_id, source_snapshot_id, projection_status,
                json.dumps(outcome, ensure_ascii=False, separators=(",", ":"), sort_keys=True),
                started_at, completed_at, snapshot.run_id, source_snapshot_id,
            ),
        )

    @staticmethod
    def _lock_completed_projection_source(*, cursor: _Cursor, snapshot: RunSnapshot, source_snapshot_id: str) -> None:
        cursor.execute(
            """SELECT r.run_id FROM ingestion_runs r
            JOIN ingestion_source_snapshots s ON s.run_id = r.run_id
            WHERE r.run_id = %s
              AND s.snapshot_id = %s
              AND r.run_status = 'succeeded'
            FOR NO KEY UPDATE OF r""",
            (snapshot.run_id, source_snapshot_id),
        )
        if cursor.fetchone() is None:
            raise IngestionProjectionLifecycleError(
                "projection outcomes require a completed succeeded source run and snapshot"
            )
