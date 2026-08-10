"""Native Boligsiden fetch, audit, lifecycle, and listing-projection orchestration."""
from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass
import math
from datetime import datetime
from typing import Any, Protocol

from .boligsiden import RawFetchSnapshot
from .identity import RunSnapshot


class BoligsidenProjectionRecordError(ValueError):
    """A live Boligsiden case cannot safely become a listing projection."""


class Fetcher(Protocol):
    def fetch(self) -> RawFetchSnapshot: ...


class RunWriter(Protocol):
    def write_started_run(self, *, snapshot: RunSnapshot, requested_at: datetime) -> str: ...
    def write_source_snapshot(self, *, snapshot: RunSnapshot, source_name: str, payload: Mapping[str, Any], captured_at: datetime) -> str: ...
    def write_stage_outcome(self, *, snapshot: RunSnapshot, stage_name: str, stage_status: str, outcome: Mapping[str, Any], started_at: datetime, completed_at: datetime) -> None: ...
    def complete_run(self, *, snapshot: RunSnapshot, run_status: str, completed_at: datetime) -> None: ...


class Projector(Protocol):
    def project_completed_snapshot(self, *, source_snapshot_id: str, projected_at: datetime) -> int: ...


@dataclass(frozen=True)
class IngestionResult:
    dry_run: bool
    run_id: str
    manifest_sha256: str
    snapshot_count: int
    projected_count: int
    run_status: str


def boligsiden_projection_record(case: Mapping[str, Any]) -> dict[str, Any]:
    case_id = _text(case.get("caseID"), "caseID")
    address = case.get("address")
    if not isinstance(address, Mapping):
        raise BoligsidenProjectionRecordError("Boligsiden address must be an object")
    road = _text(address.get("roadName"), "address.roadName")
    house_number = _text(address.get("houseNumber"), "address.houseNumber")
    city = _optional_text(address.get("cityName"))
    zip_code = _optional_text(address.get("zipCode"))
    locality = " ".join(value for value in (zip_code, city) if value)
    full_address = f"{road} {house_number}" + (f", {locality}" if locality else "")
    price = case.get("priceCash")
    if isinstance(price, bool) or not isinstance(price, (int, float)) or not math.isfinite(price) or price < 0:
        raise BoligsidenProjectionRecordError("Boligsiden priceCash must be a non-negative number")
    asbestos_source = _asbestos_source(case, address)
    record = {"external_id": case_id, "address": full_address, "city": city, "price": price}
    if asbestos_source["roof_materials"] or asbestos_source["description_title"] or asbestos_source["description_body"] or asbestos_source["images"]:
        record["asbestos_source"] = asbestos_source
    return record


def _asbestos_source(case: Mapping[str, Any], address: Mapping[str, Any]) -> dict[str, Any]:
    buildings = address.get("buildings")
    roof_materials: list[str] = []
    if isinstance(buildings, (list, tuple)):
        for building in buildings:
            if isinstance(building, Mapping) and isinstance(building.get("roofingMaterial"), str):
                roof_materials.append(str(building["roofingMaterial"]).strip())
    images = case.get("images")
    return {
        "roof_materials": roof_materials,
        "description_title": case.get("descriptionTitle"),
        "description_body": case.get("descriptionBody"),
        "images": list(images) if isinstance(images, (list, tuple)) else [],
    }


class NativeIngestionOrchestrator:
    """Runs a complete immutable native ingestion slice; dry runs never write."""

    def __init__(self, *, fetcher: Fetcher, run_writer: RunWriter, projector: Projector) -> None:
        self._fetcher = fetcher
        self._run_writer = run_writer
        self._projector = projector

    def run(self, *, dry_run: bool, requested_at: datetime) -> IngestionResult:
        fetched = self._fetcher.fetch()
        projection_records = [boligsiden_projection_record(case) for case in fetched.records]
        snapshot = fetched.run_snapshot
        if dry_run:
            return IngestionResult(True, snapshot.run_id, snapshot.manifest_sha256, snapshot.snapshot_count, 0, "dry_run")

        payload = {
            "records": [dict(case) for case in fetched.records],
            "projection_records": projection_records,
            "source_system": snapshot.source_system,
            "source_scope": snapshot.source_scope,
            "manifest_sha256": snapshot.manifest_sha256,
            "snapshot_count": snapshot.snapshot_count,
        }
        run_status = self._run_writer.write_started_run(snapshot=snapshot, requested_at=requested_at)
        if run_status != "running":
            return IngestionResult(False, snapshot.run_id, snapshot.manifest_sha256, snapshot.snapshot_count, 0, run_status)

        failed_stage = "fetch"
        try:
            source_snapshot_id = self._run_writer.write_source_snapshot(
                snapshot=snapshot, source_name="boligsiden-search-cases", payload=payload, captured_at=requested_at,
            )
            self._run_writer.write_stage_outcome(
                snapshot=snapshot, stage_name="fetch", stage_status="succeeded",
                outcome={"record_count": snapshot.snapshot_count, "source_snapshot_id": source_snapshot_id},
                started_at=requested_at, completed_at=requested_at,
            )
            failed_stage = "projection"
            projected_count = self._projector.project_completed_snapshot(
                source_snapshot_id=source_snapshot_id, projected_at=requested_at,
            )
            self._run_writer.write_stage_outcome(
                snapshot=snapshot, stage_name="projection", stage_status="succeeded",
                outcome={"projected_count": projected_count, "source_snapshot_id": source_snapshot_id},
                started_at=requested_at, completed_at=requested_at,
            )
            self._run_writer.complete_run(snapshot=snapshot, run_status="succeeded", completed_at=requested_at)
        except Exception as error:
            try:
                self._run_writer.write_stage_outcome(
                    snapshot=snapshot, stage_name=failed_stage, stage_status="failed", outcome={"error": str(error)},
                    started_at=requested_at, completed_at=requested_at,
                )
            finally:
                self._run_writer.complete_run(snapshot=snapshot, run_status="failed", completed_at=requested_at)
            raise
        return IngestionResult(False, snapshot.run_id, snapshot.manifest_sha256, snapshot.snapshot_count, projected_count, "succeeded")


def _text(value: object, field: str) -> str:
    if isinstance(value, bool) or not isinstance(value, (str, int)) or not str(value).strip():
        raise BoligsidenProjectionRecordError(f"Boligsiden {field} must be non-blank")
    return str(value).strip()


def _optional_text(value: object) -> str | None:
    if value is None:
        return None
    return _text(value, "address component")
