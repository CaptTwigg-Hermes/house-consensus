from __future__ import annotations

from collections.abc import Callable, Mapping, Sequence
from datetime import datetime
from typing import Any, Protocol
from uuid import NAMESPACE_URL, uuid5


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


class CompletedSourceSnapshotRequiredError(RuntimeError):
    """The requested source snapshot is absent or belongs to a non-completed run."""


class ListingIdentityConflictError(RuntimeError):
    """A native source identity would overwrite a manual or unprovenanced listing."""


class SourceRecordError(ValueError):
    """An immutable source snapshot contains a record without the minimum listing contract."""


class PostgresListingProjectionWriter:
    """Projects a running/succeeded source snapshot or a fetch-complete failed snapshot."""

    def __init__(self, connection_factory: Callable[[], _Connection]) -> None:
        self._connection_factory = connection_factory

    def project_completed_snapshot(self, *, source_snapshot_id: str, projected_at: datetime) -> int:
        with self._connection_factory() as connection:
            with connection.cursor() as cursor:
                cursor.execute(
                    """
                    SELECT r.source_system, r.source_scope, s.payload
                    FROM ingestion_source_snapshots s
                    JOIN ingestion_runs r ON r.run_id = s.run_id
                    WHERE s.snapshot_id = %s
                      AND (r.run_status IN ('running', 'succeeded')
                           OR (r.run_status = 'failed' AND EXISTS (
                               SELECT 1 FROM ingestion_stage_outcomes o
                               WHERE o.run_id = r.run_id
                                 AND o.stage_name = 'fetch'
                                 AND o.stage_status = 'succeeded'
                           )))
                    FOR KEY SHARE OF s, r
                    """,
                    (source_snapshot_id,),
                )
                source = cursor.fetchone()
                if source is None:
                    raise CompletedSourceSnapshotRequiredError(
                        "a running, completed, or fetch-complete failed native source snapshot is required for projection"
                    )
                source_system, source_scope, payload = source
                records = self._records(payload)
                record_ids = [self._source_record_id(record) for record in records]
                if len(record_ids) != len(set(record_ids)):
                    raise ListingIdentityConflictError(
                        "completed source snapshot contains duplicate native listing identities"
                    )
                for record in records:
                    self._project_record(
                        cursor=cursor,
                        source_system=str(source_system),
                        source_scope=str(source_scope),
                        record=record,
                        source_snapshot_id=source_snapshot_id,
                        projected_at=projected_at,
                    )
            connection.commit()
        return len(records)

    @staticmethod
    def _records(payload: object) -> Sequence[Mapping[str, Any]]:
        records = (payload.get("projection_records") or payload.get("records")) if isinstance(payload, Mapping) else payload
        if not isinstance(records, Sequence) or isinstance(records, (str, bytes)):
            raise SourceRecordError("completed source snapshot payload must contain a records array")
        if not all(isinstance(record, Mapping) for record in records):
            raise SourceRecordError("completed source snapshot records must be objects")
        return records

    @staticmethod
    def _source_record_id(record: Mapping[str, Any]) -> str:
        external_id = str(record.get("external_id") or record.get("id") or "").strip()
        if not external_id:
            raise SourceRecordError("source records require a non-empty external_id")
        return external_id

    @staticmethod
    def _project_record(
        *,
        cursor: _Cursor,
        source_system: str,
        source_scope: str,
        record: Mapping[str, Any],
        source_snapshot_id: str,
        projected_at: datetime,
    ) -> None:
        external_id = PostgresListingProjectionWriter._source_record_id(record)
        address = record.get("address")
        if not isinstance(address, str) or not address.strip():
            raise SourceRecordError("source records require non-empty external_id and address")
        listing_id = str(uuid5(NAMESPACE_URL, f"{source_system}\n{source_scope}\n{external_id}"))
        cursor.execute(
            """
            SELECT l."Id"
            FROM listings l
            WHERE l."ExternalId" = %s
              AND (l."IsManuallyAdded" = true
                   OR EXISTS (SELECT 1 FROM listing_overrides o WHERE o."ListingId" = l."Id")
                   OR NOT EXISTS (
                       SELECT 1
                       FROM listing_ingestion_projections p
                       WHERE p.listing_id = l."Id"
                         AND p.source_system = %s
                         AND p.source_scope = %s
                         AND p.source_record_id = %s
                   ))
            FOR KEY SHARE
            """,
            (external_id, source_system, source_scope, external_id),
        )
        if cursor.fetchone() is not None:
            raise ListingIdentityConflictError(
                f"listing identity {source_system}/{source_scope}/{external_id} conflicts with a protected listing"
            )
        cursor.execute(
            """
            INSERT INTO listings AS current
                ("Id", "ExternalId", "Address", "City", "Price", "FamilyFitScore", "State", "AiAssessed", "SourceUrl", "ImportedAt")
            VALUES (%s, %s, %s, %s, %s, NULL, 'active'::listing_state, false, %s, %s)
            ON CONFLICT ("Id") DO UPDATE SET
                "Address" = EXCLUDED."Address",
                "City" = EXCLUDED."City",
                "Price" = EXCLUDED."Price",
                "SourceUrl" = EXCLUDED."SourceUrl",
                "ImportedAt" = EXCLUDED."ImportedAt"
            WHERE current."IsManuallyAdded" = false
              AND NOT EXISTS (SELECT 1 FROM listing_overrides o WHERE o."ListingId" = current."Id")
            RETURNING "Id"
            """,
            (
                listing_id,
                external_id,
                address.strip(),
                record.get("city"),
                record.get("price"),
                record.get("source_url") or record.get("url"),
                projected_at,
            ),
        )
        if cursor.fetchone() is None:
            raise ListingIdentityConflictError(
                f"listing identity {source_system}/{source_scope}/{external_id} is protected from projection"
            )
        cursor.execute(
            """
            INSERT INTO listing_ingestion_projections
                (listing_id, source_system, source_scope, source_record_id, source_snapshot_id, projected_at)
            VALUES (%s, %s, %s, %s, %s, %s)
            ON CONFLICT (source_system, source_scope, source_record_id) DO NOTHING
            """,
            (listing_id, source_system, source_scope, external_id, source_snapshot_id, projected_at),
        )
