from __future__ import annotations

from datetime import UTC, datetime

import pytest


class Fetcher:
    def __init__(self, result: object) -> None:
        self.result = result
        self.calls = 0

    def fetch(self):
        self.calls += 1
        if isinstance(self.result, BaseException):
            raise self.result
        return self.result


class Writer:
    def __init__(
        self, *, existing_run_status: str = "running", existing_source_snapshot_id: str | None = None,
        existing_projection_status: str | None = None,
    ) -> None:
        self.calls: list[tuple[str, object]] = []
        self.existing_run_status = existing_run_status
        self.existing_source_snapshot_id = existing_source_snapshot_id
        self.existing_projection_status = existing_projection_status

    def write_started_run(self, *, snapshot, requested_at) -> str:
        self.calls.append(("started", snapshot, requested_at))
        return self.existing_run_status

    def write_source_snapshot(self, *, snapshot, source_name, payload, captured_at):
        self.calls.append(("snapshot", snapshot, source_name, payload, captured_at))
        return "00000000-0000-0000-0000-000000000011"

    def source_snapshot_id(self, *, snapshot, source_name):
        self.calls.append(("source_snapshot", snapshot, source_name))
        return self.existing_source_snapshot_id

    def run_projection_once(self, *, snapshot, source_snapshot_id, projected_at, project):
        self.calls.append(("projection_once", snapshot, source_snapshot_id))
        if self.existing_projection_status == "succeeded":
            return 0
        try:
            projected_count = project()
        except BaseException as error:
            self.write_projection_outcome(
                snapshot=snapshot, source_snapshot_id=source_snapshot_id, projection_status="failed",
                outcome={"error": str(error)}, started_at=projected_at, completed_at=projected_at,
            )
            raise
        self.write_projection_outcome(
            snapshot=snapshot, source_snapshot_id=source_snapshot_id, projection_status="succeeded",
            outcome={"projected_count": projected_count}, started_at=projected_at, completed_at=projected_at,
        )
        return projected_count


    def write_stage_outcome(self, *, snapshot, stage_name, stage_status, outcome, started_at, completed_at) -> None:
        self.calls.append(("stage", snapshot, stage_name, stage_status, outcome, started_at, completed_at))

    def complete_run(self, *, snapshot, run_status, completed_at) -> None:
        self.calls.append(("terminal", snapshot, run_status, completed_at))

    def write_projection_outcome(
        self, *, snapshot, source_snapshot_id, projection_status, outcome, started_at, completed_at
    ) -> None:
        self.calls.append(
            ("projection", snapshot, source_snapshot_id, projection_status, outcome, started_at, completed_at)
        )


class Projector:
    def __init__(self) -> None:
        self.calls: list[tuple[str, datetime]] = []

    def project_completed_snapshot(self, *, source_snapshot_id: str, projected_at: datetime) -> int:
        self.calls.append((source_snapshot_id, projected_at))
        return 1


class Pipeline:
    def process(self, cases):
        from house_consensus_ingestion.pipeline import PipelineResult

        records = tuple(dict(case) for case in cases)
        return PipelineResult(
            records=records,
            matched_count=len(records),
            stage_outcomes={"classification": {"records": len(records), "matched": len(records)}},
        )


def raw_fetch():
    from house_consensus_ingestion.boligsiden import RawFetchSnapshot
    from house_consensus_ingestion.identity import build_snapshot

    records = ({
        "caseID": "case-42",
        "address": {"roadName": "Example Road", "houseNumber": "42", "zipCode": "2100", "cityName": "Copenhagen"},
        "priceCash": 2_500_000,
    },)
    return RawFetchSnapshot(
        records=records,
        run_snapshot=build_snapshot(
            source_system="house-consensus-ingestion",
            source_scope="boligsiden.dk/open-cases",
            records=records,
        ),
        source_config_sha256="a" * 64,
    )


def test_maps_live_boligsiden_case_id_address_object_and_price_cash_to_projection_record() -> None:
    from house_consensus_ingestion.orchestration import boligsiden_projection_record

    assert boligsiden_projection_record(raw_fetch().records[0]) == {
        "external_id": "case-42",
        "address": "Example Road 42, 2100 Copenhagen",
        "city": "Copenhagen",
        "price": 2_500_000,
    }


def test_rejects_malformed_live_boligsiden_address_or_price_before_any_native_write() -> None:
    from house_consensus_ingestion.orchestration import (
        BoligsidenProjectionRecordError,
        boligsiden_projection_record,
    )

    with pytest.raises(BoligsidenProjectionRecordError, match="address"):
        boligsiden_projection_record({"caseID": "case-42", "address": {}, "priceCash": 2_500_000})
    with pytest.raises(BoligsidenProjectionRecordError, match="priceCash"):
        boligsiden_projection_record({"caseID": "case-42", "address": {"roadName": "Road", "houseNumber": "1"}, "priceCash": True})
    with pytest.raises(BoligsidenProjectionRecordError, match="priceCash"):
        boligsiden_projection_record({"caseID": "case-42", "address": {"roadName": "Road", "houseNumber": "1"}, "priceCash": float("nan")})


def test_dry_run_fetches_validates_and_reports_without_native_database_or_projection_writes() -> None:
    from house_consensus_ingestion.orchestration import NativeIngestionOrchestrator

    fetcher = Fetcher(raw_fetch())
    writer = Writer()
    projector = Projector()
    result = NativeIngestionOrchestrator(fetcher=fetcher, pipeline=Pipeline(), run_writer=writer, projector=projector).run(
        dry_run=True,
        requested_at=datetime(2026, 8, 7, tzinfo=UTC),
    )

    assert result.dry_run is True
    assert result.snapshot_count == 1
    assert result.projected_count == 0
    assert fetcher.calls == 1
    assert writer.calls == []
    assert projector.calls == []


def test_native_lifecycle_terminalizes_source_before_projecting() -> None:
    from house_consensus_ingestion.orchestration import NativeIngestionOrchestrator

    writer = Writer()
    projector = Projector()
    result = NativeIngestionOrchestrator(fetcher=Fetcher(raw_fetch()), pipeline=Pipeline(), run_writer=writer, projector=projector).run(
        dry_run=False,
        requested_at=datetime(2026, 8, 7, tzinfo=UTC),
    )

    assert result.dry_run is False
    assert result.projected_count == 1
    assert [call[0] for call in writer.calls] == ["started", "snapshot", "stage", "stage", "terminal", "projection_once", "projection"]
    snapshot_payload = writer.calls[1][3]
    assert snapshot_payload["source_config_sha256"] == "a" * 64
    assert snapshot_payload["records"] == [dict(raw_fetch().records[0])]
    assert snapshot_payload["projection_records"] == [{
        "external_id": "case-42", "address": "Example Road 42, 2100 Copenhagen", "city": "Copenhagen", "price": 2_500_000,
    }]
    assert writer.calls[2][3] == "succeeded"
    assert writer.calls[3][3] == "succeeded"
    assert writer.calls[4][2] == "succeeded"
    assert writer.calls[6][3:5] == ("succeeded", {"projected_count": 1})
    assert projector.calls == [("00000000-0000-0000-0000-000000000011", datetime(2026, 8, 7, tzinfo=UTC))]


def test_terminal_failure_is_persisted_when_snapshot_write_fails() -> None:
    from house_consensus_ingestion.orchestration import NativeIngestionOrchestrator

    class FailingWriter(Writer):
        def write_source_snapshot(self, **kwargs):
            raise RuntimeError("disk full")

    writer = FailingWriter()
    with pytest.raises(RuntimeError, match="disk full"):
        NativeIngestionOrchestrator(fetcher=Fetcher(raw_fetch()), pipeline=Pipeline(), run_writer=writer, projector=Projector()).run(
            dry_run=False,
            requested_at=datetime(2026, 8, 7, tzinfo=UTC),
        )

    assert [call[0] for call in writer.calls] == ["started", "stage", "terminal"]
    assert writer.calls[-1][2] == "failed"


@pytest.mark.parametrize("terminal_status", ["succeeded", "failed", "cancelled"])
def test_exact_retry_of_a_terminal_run_is_a_no_op_after_provenance_is_verified(terminal_status: str) -> None:
    from house_consensus_ingestion.orchestration import NativeIngestionOrchestrator

    writer = Writer(existing_run_status=terminal_status)
    projector = Projector()

    result = NativeIngestionOrchestrator(fetcher=Fetcher(raw_fetch()), pipeline=Pipeline(), run_writer=writer, projector=projector).run(
        dry_run=False,
        requested_at=datetime(2026, 8, 7, tzinfo=UTC),
    )

    assert result.run_status == terminal_status
    assert result.projected_count == 0
    expected_calls = ["started", "source_snapshot"] if terminal_status == "succeeded" else ["started"]
    assert [call[0] for call in writer.calls] == expected_calls
    assert projector.calls == []


def test_projection_failure_preserves_completed_source_and_records_separate_failure() -> None:
    from house_consensus_ingestion.orchestration import NativeIngestionOrchestrator

    class FailingProjector(Projector):
        def project_completed_snapshot(self, **kwargs) -> int:
            raise RuntimeError("listing lock timeout")

    writer = Writer()
    with pytest.raises(RuntimeError, match="listing lock timeout"):
        NativeIngestionOrchestrator(
            fetcher=Fetcher(raw_fetch()), pipeline=Pipeline(), run_writer=writer, projector=FailingProjector()
        ).run(dry_run=False, requested_at=datetime(2026, 8, 7, tzinfo=UTC))

    assert [call[0] for call in writer.calls] == ["started", "snapshot", "stage", "stage", "terminal", "projection_once", "projection"]
    assert writer.calls[4][2] == "succeeded"
    assert writer.calls[-1][3:5] == ("failed", {"error": "listing lock timeout"})


def test_completed_source_retry_retries_an_unprojected_snapshot_without_reopening_it() -> None:
    from house_consensus_ingestion.orchestration import NativeIngestionOrchestrator

    writer = Writer(
        existing_run_status="succeeded",
        existing_source_snapshot_id="00000000-0000-0000-0000-000000000011",
    )
    projector = Projector()
    result = NativeIngestionOrchestrator(
        fetcher=Fetcher(raw_fetch()), pipeline=Pipeline(), run_writer=writer, projector=projector
    ).run(dry_run=False, requested_at=datetime(2026, 8, 7, tzinfo=UTC))

    assert result.run_status == "succeeded"
    assert result.projected_count == 1
    assert [call[0] for call in writer.calls] == ["started", "source_snapshot", "projection_once", "projection"]
    assert projector.calls == [("00000000-0000-0000-0000-000000000011", datetime(2026, 8, 7, tzinfo=UTC))]


def test_completed_source_retry_is_a_no_op_after_a_durable_successful_projection() -> None:
    from house_consensus_ingestion.orchestration import NativeIngestionOrchestrator

    writer = Writer(
        existing_run_status="succeeded",
        existing_source_snapshot_id="00000000-0000-0000-0000-000000000011",
        existing_projection_status="succeeded",
    )
    projector = Projector()
    result = NativeIngestionOrchestrator(
        fetcher=Fetcher(raw_fetch()), pipeline=Pipeline(), run_writer=writer, projector=projector
    ).run(dry_run=False, requested_at=datetime(2026, 8, 7, tzinfo=UTC))

    assert result.run_status == "succeeded"
    assert result.projected_count == 0
    assert [call[0] for call in writer.calls] == ["started", "source_snapshot", "projection_once"]
    assert projector.calls == []
