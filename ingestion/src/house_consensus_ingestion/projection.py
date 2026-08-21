from __future__ import annotations

import re
from collections.abc import Callable, Mapping, Sequence
from datetime import datetime
from typing import Any, Protocol
from uuid import NAMESPACE_URL, UUID, uuid5

from consensus_exporter.models import ExportCase


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


class _ExportResult(Protocol):
    exported: int


class _Exporter(Protocol):
    def export(self, cases: Sequence[ExportCase], **kwargs: object) -> _ExportResult: ...


class CompletedSourceSnapshotRequiredError(RuntimeError):
    """The requested source snapshot is absent or belongs to a non-completed run."""


class ListingIdentityConflictError(RuntimeError):
    """A native source identity would overwrite a manual or unprovenanced listing."""


class SourceRecordError(ValueError):
    """An immutable source snapshot contains a record without the minimum listing contract."""


class ExporterBackedPostgresListingProjector:
    """Projects a latest completed native snapshot through the canonical exporter."""

    def __init__(
        self,
        *,
        connection_factory: Callable[[], _Connection],
        exporter_factory: Callable[[str], _Exporter],
    ) -> None:
        self._connection_factory = connection_factory
        self._exporter_factory = exporter_factory

    def project_completed_snapshot(self, *, source_snapshot_id: str, projected_at: datetime) -> int:
        with self._connection_factory() as connection:
            with connection.cursor() as cursor:
                cursor.execute(
                    """
                    SELECT r.run_id::text, r.source_system, r.source_scope,
                           r.completed_at, r.manifest_sha256, s.payload
                    FROM ingestion_source_snapshots s
                    JOIN ingestion_runs r ON r.run_id = s.run_id
                    WHERE s.snapshot_id = %s
                      AND r.run_status = 'succeeded'
                      AND r.completed_at IS NOT NULL
                      AND EXISTS (
                          SELECT 1
                          FROM ingestion_stage_outcomes o
                          WHERE o.run_id = r.run_id
                            AND o.stage_name = 'fetch'
                            AND o.stage_status = 'succeeded'
                      )
                      AND NOT EXISTS (
                          SELECT 1
                          FROM ingestion_runs newer
                          WHERE newer.source_system = r.source_system
                            AND newer.source_scope = r.source_scope
                            AND newer.run_status = 'succeeded'
                            AND newer.completed_at IS NOT NULL
                            AND (newer.completed_at, newer.run_id) > (r.completed_at, r.run_id)
                      )
                    FOR KEY SHARE OF s, r
                    """,
                    (source_snapshot_id,),
                )
                source = cursor.fetchone()
        if source is None:
            raise CompletedSourceSnapshotRequiredError(
                "the latest completed native source snapshot with a successful fetch outcome is required"
            )

        run_id, source_system, source_scope, completed_at, manifest_sha256, payload = source
        records = self._validated_full_records(
            payload=payload,
            source_system=str(source_system),
            source_scope=str(source_scope),
            manifest_sha256=str(manifest_sha256),
        )
        source_config_sha256 = payload.get("source_config_sha256") if isinstance(payload, Mapping) else None
        if not isinstance(source_config_sha256, str) or re.fullmatch(r"[0-9a-f]{64}", source_config_sha256) is None:
            raise SourceRecordError("completed source snapshot requires a canonical source configuration SHA-256")
        cases = [ExportCase.from_records(dict(record), dict(record)) for record in records]
        source_ids = [case.source_id for case in cases]
        if len(source_ids) != len(set(source_ids)):
            raise ListingIdentityConflictError(
                "completed source snapshot contains duplicate canonical listing identities"
            )

        def record_projection(export_connection: object, case: ExportCase, listing_id: UUID) -> None:
            self._record_projection(
                connection=export_connection,
                listing_id=listing_id,
                source_system=str(source_system),
                source_scope=str(source_scope),
                source_record_id=case.source_id,
                source_snapshot_id=source_snapshot_id,
                projected_at=projected_at,
            )

        result = self._exporter_factory(str(source_scope)).export(
            cases,
            run_id=str(run_id),
            fetched_at=completed_at,
            source_config_sha256=source_config_sha256,
            projection_recorder=record_projection,
        )
        return result.exported

    @staticmethod
    def _validated_full_records(
        *,
        payload: object,
        source_system: str,
        source_scope: str,
        manifest_sha256: str,
    ) -> Sequence[Mapping[str, Any]]:
        if not isinstance(payload, Mapping):
            raise SourceRecordError("completed source snapshot payload must be an object")
        expected_metadata = {
            "source_system": source_system,
            "source_scope": source_scope,
            "manifest_sha256": manifest_sha256,
        }
        if any(payload.get(key) != value for key, value in expected_metadata.items()):
            raise SourceRecordError("completed source snapshot metadata does not match its ingestion run")
        records = payload.get("records")
        if not isinstance(records, Sequence) or isinstance(records, (str, bytes)):
            raise SourceRecordError("completed source snapshot payload must contain the full records array")
        if not all(isinstance(record, Mapping) for record in records):
            raise SourceRecordError("completed source snapshot records must be objects")
        declared_count = payload.get("snapshot_count")
        if isinstance(declared_count, bool) or not isinstance(declared_count, int):
            raise SourceRecordError("completed source snapshot requires an integer snapshot count")
        if declared_count != len(records):
            raise SourceRecordError(
                f"completed source snapshot count {declared_count} does not match {len(records)} full records"
            )
        if not records:
            raise SourceRecordError("completed source snapshot cannot be empty")
        return records

    @staticmethod
    def _record_projection(
        *,
        connection: object,
        listing_id: UUID,
        source_system: str,
        source_scope: str,
        source_record_id: str,
        source_snapshot_id: str,
        projected_at: datetime,
    ) -> None:
        existing = connection.execute(
            """
            SELECT listing_id, source_system, source_scope, source_record_id
            FROM listing_ingestion_projections
            WHERE listing_id = %s
               OR (source_system = %s AND source_scope = %s AND source_record_id = %s)
            FOR UPDATE
            """,
            (listing_id, source_system, source_scope, source_record_id),
        ).fetchall()
        expected = (listing_id, source_system, source_scope, source_record_id)
        if any(tuple(row) != expected for row in existing):
            raise ListingIdentityConflictError(
                f"native projection identity {source_system}/{source_scope}/{source_record_id} conflicts with an existing mapping"
            )
        persisted = connection.execute(
            """
            INSERT INTO listing_ingestion_projections AS current
                (listing_id, source_system, source_scope, source_record_id,
                 source_snapshot_id, projected_at)
            VALUES (%s, %s, %s, %s, %s, %s)
            ON CONFLICT (source_system, source_scope, source_record_id) DO UPDATE SET
                source_snapshot_id = EXCLUDED.source_snapshot_id,
                projected_at = EXCLUDED.projected_at
            WHERE current.listing_id = EXCLUDED.listing_id
            RETURNING listing_id
            """,
            (
                listing_id,
                source_system,
                source_scope,
                source_record_id,
                source_snapshot_id,
                projected_at,
            ),
        ).fetchone()
        if persisted is None:
            raise ListingIdentityConflictError(
                f"native projection identity {source_system}/{source_scope}/{source_record_id} changed listing identity"
            )


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
                      AND r.run_status IN ('running', 'succeeded', 'failed')
                      AND EXISTS (
                          SELECT 1 FROM ingestion_stage_outcomes o
                          WHERE o.run_id = r.run_id
                            AND o.stage_name = 'fetch'
                            AND o.stage_status = 'succeeded'
                      )
                    FOR KEY SHARE OF s, r
                    """,
                    (source_snapshot_id,),
                )
                source = cursor.fetchone()
                if source is None:
                    raise CompletedSourceSnapshotRequiredError(
                        "an eligible native source snapshot with a successful fetch outcome is required for projection"
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
