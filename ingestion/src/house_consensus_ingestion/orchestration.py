"""Native Boligsiden fetch, audit, lifecycle, and listing-projection orchestration."""
from __future__ import annotations

import math
from collections.abc import Callable, Iterable, Mapping
from dataclasses import dataclass
from datetime import datetime
from typing import Any, Protocol

from .boligsiden import RawFetchSnapshot
from .identity import RunSnapshot
from .pipeline import PipelineResult


class BoligsidenProjectionRecordError(ValueError):
    """A live Boligsiden case cannot safely become a listing projection."""


class Fetcher(Protocol):
    def fetch(self) -> RawFetchSnapshot: ...


class CasePipeline(Protocol):
    def process(self, cases: Iterable[Mapping[str, Any]]) -> PipelineResult: ...


class RunWriter(Protocol):
    def write_started_run(self, *, snapshot: RunSnapshot, requested_at: datetime) -> str: ...
    def write_source_snapshot(self, *, snapshot: RunSnapshot, source_name: str, payload: Mapping[str, Any], captured_at: datetime) -> str: ...
    def source_snapshot_id(self, *, snapshot: RunSnapshot, source_name: str) -> str | None: ...
    def run_projection_once(
        self, *, snapshot: RunSnapshot, source_snapshot_id: str, projected_at: datetime,
        project: Callable[[], int],
    ) -> int: ...
    def write_stage_outcome(self, *, snapshot: RunSnapshot, stage_name: str, stage_status: str, outcome: Mapping[str, Any], started_at: datetime, completed_at: datetime) -> None: ...
    def complete_run(self, *, snapshot: RunSnapshot, run_status: str, completed_at: datetime) -> None: ...
    def write_projection_outcome(
        self, *, snapshot: RunSnapshot, source_snapshot_id: str, projection_status: str,
        outcome: Mapping[str, Any], started_at: datetime, completed_at: datetime,
    ) -> None: ...


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
    matched_count: int | None


def boligsiden_projection_record(case: Mapping[str, Any]) -> dict[str, Any]:
    if isinstance(case.get("address"), str):
        external_id = _text(case.get("external_id") or case.get("id") or case.get("caseID"), "external_id")
        address = _text(case.get("address"), "address")
        price = case.get("price") if "price" in case else case.get("price_dkk")
        if isinstance(price, bool) or not isinstance(price, (int, float)) or not math.isfinite(price) or price < 0:
            raise BoligsidenProjectionRecordError("normalized price must be a non-negative number")
        result = dict(case)
        result.update(external_id=external_id, address=address, price=price)
        return result
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
    return {"external_id": case_id, "address": full_address, "city": city, "price": price}


class NativeIngestionOrchestrator:
    """Runs a complete immutable native ingestion slice; dry runs never write."""

    def __init__(self, *, fetcher: Fetcher, pipeline: CasePipeline, run_writer: RunWriter, projector: Projector) -> None:
        self._fetcher = fetcher
        self._pipeline = pipeline
        self._run_writer = run_writer
        self._projector = projector

    def run(self, *, dry_run: bool, requested_at: datetime) -> IngestionResult:
        fetched = self._fetcher.fetch()
        snapshot = fetched.run_snapshot
        if dry_run:
            processed = self._pipeline.process(fetched.records)
            for case in processed.records:
                boligsiden_projection_record(case)
            return IngestionResult(
                True, snapshot.run_id, snapshot.manifest_sha256, snapshot.snapshot_count, 0, "dry_run",
                processed.matched_count,
            )

        run_status = self._run_writer.write_started_run(snapshot=snapshot, requested_at=requested_at)
        if run_status != "running":
            if run_status == "succeeded":
                source_snapshot_id = self._run_writer.source_snapshot_id(
                    snapshot=snapshot, source_name="boligsiden-search-cases",
                )
                if source_snapshot_id is not None:
                    projected_count = self._project_completed_source(
                        snapshot=snapshot, source_snapshot_id=source_snapshot_id, requested_at=requested_at,
                    )
                    return IngestionResult(
                        False, snapshot.run_id, snapshot.manifest_sha256, snapshot.snapshot_count,
                        projected_count, run_status, None,
                    )
            return IngestionResult(
                False, snapshot.run_id, snapshot.manifest_sha256, snapshot.snapshot_count, 0, run_status, None,
            )

        failed_stage = "pipeline"
        try:
            processed = self._pipeline.process(fetched.records)
            projection_records = [boligsiden_projection_record(case) for case in processed.records]
            payload = {
                "raw_records": [dict(case) for case in fetched.records],
                "records": [dict(case) for case in processed.records],
                "projection_records": projection_records,
                "source_system": snapshot.source_system,
                "source_scope": snapshot.source_scope,
                "manifest_sha256": snapshot.manifest_sha256,
                "source_config_sha256": fetched.source_config_sha256,
                "snapshot_count": snapshot.snapshot_count,
            }
            failed_stage = "fetch"
            source_snapshot_id = self._run_writer.write_source_snapshot(
                snapshot=snapshot, source_name="boligsiden-search-cases", payload=payload, captured_at=requested_at,
            )
            self._run_writer.write_stage_outcome(
                snapshot=snapshot, stage_name="fetch", stage_status="succeeded",
                outcome={"record_count": snapshot.snapshot_count, "source_snapshot_id": source_snapshot_id},
                started_at=requested_at, completed_at=requested_at,
            )
            for stage_name, outcome in processed.stage_outcomes.items():
                failed_stage = stage_name
                self._run_writer.write_stage_outcome(
                    snapshot=snapshot, stage_name=stage_name, stage_status="succeeded", outcome=outcome,
                    started_at=requested_at, completed_at=requested_at,
                )
        except BaseException as error:
            terminal_status = "cancelled" if isinstance(error, KeyboardInterrupt) else "failed"
            try:
                self._run_writer.write_stage_outcome(
                    snapshot=snapshot, stage_name=failed_stage, stage_status="failed", outcome={"error": str(error)},
                    started_at=requested_at, completed_at=requested_at,
                )
            finally:
                self._run_writer.complete_run(snapshot=snapshot, run_status=terminal_status, completed_at=requested_at)
            raise

        self._run_writer.complete_run(snapshot=snapshot, run_status="succeeded", completed_at=requested_at)
        projected_count = self._project_completed_source(
            snapshot=snapshot, source_snapshot_id=source_snapshot_id, requested_at=requested_at,
        )
        return IngestionResult(
            False, snapshot.run_id, snapshot.manifest_sha256, snapshot.snapshot_count, projected_count,
            "succeeded", processed.matched_count,
        )


    def _project_completed_source(
        self, *, snapshot: RunSnapshot, source_snapshot_id: str, requested_at: datetime,
    ) -> int:
        return self._run_writer.run_projection_once(
            snapshot=snapshot,
            source_snapshot_id=source_snapshot_id,
            projected_at=requested_at,
            project=lambda: self._projector.project_completed_snapshot(
                source_snapshot_id=source_snapshot_id, projected_at=requested_at,
            ),
        )


def _text(value: object, field: str) -> str:
    if isinstance(value, bool) or not isinstance(value, (str, int)) or not str(value).strip():
        raise BoligsidenProjectionRecordError(f"Boligsiden {field} must be non-blank")
    return str(value).strip()


def _optional_text(value: object) -> str | None:
    if value is None:
        return None
    return _text(value, "address component")
