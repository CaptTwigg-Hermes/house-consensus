"""Read-only parity checks between a completed legacy SQLite scope and native projections."""
from __future__ import annotations

import argparse
from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass
import json
from pathlib import Path
import sqlite3
from typing import Any, Protocol


class _Cursor(Protocol):
    def __enter__(self) -> _Cursor: ...
    def __exit__(self, *args: object) -> None: ...
    def execute(self, statement: str, parameters: tuple[object, ...] | None = None) -> None: ...
    def fetchall(self) -> list[tuple[object, ...]]: ...


class _Connection(Protocol):
    def __enter__(self) -> _Connection: ...
    def __exit__(self, *args: object) -> None: ...
    def cursor(self) -> _Cursor: ...


class ShadowParityError(RuntimeError):
    """The completed legacy source scope and native projection differ."""


@dataclass(frozen=True)
class ProjectionRecord:
    source_id: str
    address: str | None
    city: str | None
    price: object
    source_url: str | None


@dataclass(frozen=True)
class ShadowParityResult:
    source_count: int
    native_count: int
    source_ids: tuple[str, ...]
    native_ids: tuple[str, ...]


def run_shadow_parity(*, sqlite_path: str | Path, source_system: str, source_scope: str, connection_factory: Callable[[], _Connection]) -> ShadowParityResult:
    source_records = _load_completed_legacy_scope(Path(sqlite_path), source_scope)
    native_records = _load_native_projection(connection_factory, source_system, source_scope)
    _assert_parity(source_records, native_records)
    return ShadowParityResult(len(source_records), len(native_records), tuple(sorted(source_records)), tuple(sorted(native_records)))


def _load_completed_legacy_scope(path: Path, source_scope: str) -> dict[str, ProjectionRecord]:
    with sqlite3.connect(path) as connection:
        completed = connection.execute(
            """SELECT run_id, case_count FROM pipeline_runs
            WHERE status = ? AND source_scope = ?
            ORDER BY completed_at DESC, run_id DESC LIMIT 1""",
            ("complete", source_scope),
        ).fetchone()
        if completed is None:
            raise ShadowParityError(f"no completed legacy source snapshot is available for scope {source_scope!r}")
        run_id, expected_count = completed
        rows = connection.execute(
            "SELECT id, case_payload, match_payload FROM pipeline_snapshot_items WHERE run_id = ? ORDER BY id",
            (run_id,),
        ).fetchall()
    if len(rows) != expected_count:
        raise ShadowParityError(f"completed legacy snapshot {run_id!r} contains {len(rows)} of {expected_count} declared cases")
    records: dict[str, ProjectionRecord] = {}
    for fallback_id, case_payload, match_payload in rows:
        raw = json.loads(case_payload)
        raw.setdefault("caseID", fallback_id)
        match = json.loads(match_payload) if match_payload else {}
        merged = {**raw, **match}
        record = ProjectionRecord(
            source_id=_required_id(merged),
            address=_address(merged.get("address")),
            city=_city(merged),
            price=_first(merged, "price_dkk", "cashPrice", "price"),
            source_url=_first(merged, "maegler_url", "caseUrl", "link", "url"),
        )
        if record.source_id in records:
            raise ShadowParityError(f"completed legacy snapshot {run_id!r} contains duplicate source ID {record.source_id!r}")
        records[record.source_id] = record
    return records


def _load_native_projection(connection_factory: Callable[[], _Connection], source_system: str, source_scope: str) -> dict[str, ProjectionRecord]:
    with connection_factory() as connection:
        with connection.cursor() as cursor:
            cursor.execute("SET TRANSACTION READ ONLY")
            cursor.execute(
                """SELECT p.source_record_id, l."ExternalId", l."Address", l."City", l."Price", l."SourceUrl"
                FROM listing_ingestion_projections p
                JOIN listings l ON l."Id" = p.listing_id
                WHERE p.source_system = %s AND p.source_scope = %s
                ORDER BY p.source_record_id""",
                (source_system, source_scope),
            )
            rows = cursor.fetchall()
    records: dict[str, ProjectionRecord] = {}
    for source_id, external_id, address, city, price, source_url in rows:
        source_id = str(source_id)
        if source_id != str(external_id):
            raise ShadowParityError(f"native projection source ID {source_id!r} has mismatched listing ExternalId {external_id!r}")
        if source_id in records:
            raise ShadowParityError(f"native projection contains duplicate source ID {source_id!r}")
        records[source_id] = ProjectionRecord(source_id, _text(address), _text(city), price, _text(source_url))
    return records


def _assert_parity(source: Mapping[str, ProjectionRecord], native: Mapping[str, ProjectionRecord]) -> None:
    source_ids, native_ids = set(source), set(native)
    if source_ids != native_ids:
        raise ShadowParityError(f"source identity mismatch: missing_native={sorted(source_ids - native_ids)!r}, unexpected_native={sorted(native_ids - source_ids)!r}")
    differences = [source_id for source_id in sorted(source_ids) if source[source_id] != native[source_id]]
    if differences:
        raise ShadowParityError(f"projection field mismatch for source IDs: {differences!r}")



def _required_id(record: Mapping[str, Any]) -> str:
    value = _first(record, "caseID", "id", "case_id")
    if value is None or not str(value).strip():
        raise ShadowParityError("legacy source record has no source ID")
    return str(value).strip()


def _address(value: object) -> str | None:
    if isinstance(value, str):
        return value.strip() or None
    if not isinstance(value, Mapping):
        return None
    return " ".join(str(value[key]).strip() for key in ("roadName", "houseNumber", "floor", "door") if value.get(key) not in (None, "")).strip() or None


def _city(record: Mapping[str, Any]) -> str | None:
    address = record.get("address")
    nested_city = address.get("cityName") if isinstance(address, Mapping) else None
    return _text(_first(record, "city", "cityName", default=nested_city))


def _first(record: Mapping[str, Any], *keys: str, default: object = None) -> object:
    return next((record[key] for key in keys if record.get(key) not in (None, "")), default)


def _text(value: object) -> str | None:
    return value.strip() or None if isinstance(value, str) else None


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Read-only legacy SQLite-to-native projection parity check")
    parser.add_argument("--sqlite", required=True)
    parser.add_argument("--database-url", required=True)
    parser.add_argument("--source-system", required=True)
    parser.add_argument("--source-scope", required=True)
    arguments = parser.parse_args(argv)
    import psycopg
    result = run_shadow_parity(sqlite_path=arguments.sqlite, source_system=arguments.source_system, source_scope=arguments.source_scope, connection_factory=lambda: psycopg.connect(arguments.database_url))
    print(json.dumps({"native_count": result.native_count, "source_count": result.source_count, "source_scope": arguments.source_scope, "status": "parity"}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
