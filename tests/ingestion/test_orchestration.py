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
    def __init__(self, *, existing_run_status: str = "running") -> None:
        self.calls: list[tuple[str, object]] = []
        self.existing_run_status = existing_run_status

    def write_started_run(self, *, snapshot, requested_at) -> str:
        self.calls.append(("started", snapshot, requested_at))
        return self.existing_run_status

    def write_source_snapshot(self, *, snapshot, source_name, payload, captured_at):
        self.calls.append(("snapshot", snapshot, source_name, payload, captured_at))
        return "00000000-0000-0000-0000-000000000011"

    def write_stage_outcome(self, *, snapshot, stage_name, stage_status, outcome, started_at, completed_at) -> None:
        self.calls.append(("stage", snapshot, stage_name, stage_status, outcome, started_at, completed_at))

    def complete_run(self, *, snapshot, run_status, completed_at) -> None:
        self.calls.append(("terminal", snapshot, run_status, completed_at))


class Projector:
    def __init__(self) -> None:
        self.calls: list[tuple[str, datetime]] = []

    def project_completed_snapshot(self, *, source_snapshot_id: str, projected_at: datetime) -> int:
        self.calls.append((source_snapshot_id, projected_at))
        return 1


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
    from house_consensus_ingestion.orchestration import BoligsidenProjectionRecordError, boligsiden_projection_record

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
    result = NativeIngestionOrchestrator(fetcher=fetcher, run_writer=writer, projector=projector).run(
        dry_run=True,
        requested_at=datetime(2026, 8, 7, tzinfo=UTC),
    )

    assert result.dry_run is True
    assert result.snapshot_count == 1
    assert result.projected_count == 0
    assert fetcher.calls == 1
    assert writer.calls == []
    assert projector.calls == []


def test_native_lifecycle_projects_before_terminal_success() -> None:
    from house_consensus_ingestion.orchestration import NativeIngestionOrchestrator

    writer = Writer()
    projector = Projector()
    result = NativeIngestionOrchestrator(fetcher=Fetcher(raw_fetch()), run_writer=writer, projector=projector).run(
        dry_run=False,
        requested_at=datetime(2026, 8, 7, tzinfo=UTC),
    )

    assert result.dry_run is False
    assert result.projected_count == 1
    assert [call[0] for call in writer.calls] == ["started", "snapshot", "stage", "stage", "terminal"]
    snapshot_payload = writer.calls[1][3]
    assert snapshot_payload["records"] == [dict(raw_fetch().records[0])]
    assert snapshot_payload["projection_records"] == [{
        "external_id": "case-42", "address": "Example Road 42, 2100 Copenhagen", "city": "Copenhagen", "price": 2_500_000,
    }]
    assert writer.calls[2][3] == "succeeded"
    assert writer.calls[3][3] == "succeeded"
    assert writer.calls[4][2] == "succeeded"
    assert projector.calls == [("00000000-0000-0000-0000-000000000011", datetime(2026, 8, 7, tzinfo=UTC))]


def test_terminal_failure_is_persisted_when_snapshot_write_fails() -> None:
    from house_consensus_ingestion.orchestration import NativeIngestionOrchestrator

    class FailingWriter(Writer):
        def write_source_snapshot(self, **kwargs):
            raise RuntimeError("disk full")

    writer = FailingWriter()
    with pytest.raises(RuntimeError, match="disk full"):
        NativeIngestionOrchestrator(fetcher=Fetcher(raw_fetch()), run_writer=writer, projector=Projector()).run(
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

    result = NativeIngestionOrchestrator(fetcher=Fetcher(raw_fetch()), run_writer=writer, projector=projector).run(
        dry_run=False,
        requested_at=datetime(2026, 8, 7, tzinfo=UTC),
    )

    assert result.run_status == terminal_status
    assert result.projected_count == 0
    assert [call[0] for call in writer.calls] == ["started"]
    assert projector.calls == []


def test_projection_failure_marks_the_running_run_failed_before_it_can_succeed() -> None:
    from house_consensus_ingestion.orchestration import NativeIngestionOrchestrator

    class FailingProjector(Projector):
        def project_completed_snapshot(self, **kwargs) -> int:
            raise RuntimeError("listing lock timeout")

    writer = Writer()
    with pytest.raises(RuntimeError, match="listing lock timeout"):
        NativeIngestionOrchestrator(
            fetcher=Fetcher(raw_fetch()), run_writer=writer, projector=FailingProjector()
        ).run(dry_run=False, requested_at=datetime(2026, 8, 7, tzinfo=UTC))

    assert [call[0] for call in writer.calls] == ["started", "snapshot", "stage", "stage", "terminal"]
    assert writer.calls[-2][3:5] == ("failed", {"error": "listing lock timeout"})
    assert writer.calls[-1][2] == "failed"
