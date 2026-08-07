import os
from datetime import UTC, datetime
from pathlib import Path

import psycopg
import pytest

from house_consensus_ingestion.identity import build_snapshot
from house_consensus_ingestion.postgres import PostgresRunWriter


ROOT = Path(__file__).resolve().parents[2]
SCHEMA = ROOT / "exporter/src/consensus_exporter/schema.sql"


@pytest.fixture()
def database_url():
    url = os.environ.get("TEST_DATABASE_URL")
    if not url:
        pytest.skip("TEST_DATABASE_URL is not configured")
    with psycopg.connect(url, autocommit=True) as conn:
        conn.execute("drop schema public cascade")
        conn.execute("create schema public")
        conn.execute(SCHEMA.read_text())
    return url


def test_exact_provenance_retry_does_not_mutate_immutable_ingestion_run(database_url):
    """A real trigger-protected run accepts an exact retry without an UPDATE no-op."""
    snapshot = build_snapshot(
        source_system="house-consensus-ingestion",
        source_scope="boliga.dk",
        records=[{"external_id": "1", "address": "One Street 1"}],
    )
    requested_at = datetime(2026, 8, 7, tzinfo=UTC)

    writer = PostgresRunWriter(lambda: psycopg.connect(database_url))
    writer.write_started_run(snapshot=snapshot, requested_at=requested_at)
    writer.write_started_run(snapshot=snapshot, requested_at=requested_at)

    with psycopg.connect(database_url) as conn:
        assert conn.execute(
            "SELECT source_system, source_scope, manifest_sha256 FROM ingestion_runs WHERE run_id=%s",
            (snapshot.run_id,),
        ).fetchone() == (
            snapshot.source_system,
            snapshot.source_scope,
            snapshot.manifest_sha256,
        )



@pytest.mark.parametrize("terminal_status", ["succeeded", "failed", "cancelled"])
def test_terminal_exact_retry_returns_the_existing_terminal_status_without_reopening_it(database_url, terminal_status):
    snapshot = build_snapshot(
        source_system="house-consensus-ingestion",
        source_scope="boliga.dk",
        records=[{"external_id": "1", "address": "One Street 1"}],
    )
    requested_at = datetime(2026, 8, 7, tzinfo=UTC)
    writer = PostgresRunWriter(lambda: psycopg.connect(database_url))
    writer.write_started_run(snapshot=snapshot, requested_at=requested_at)
    writer.complete_run(snapshot=snapshot, run_status=terminal_status, completed_at=requested_at)

    assert writer.write_started_run(snapshot=snapshot, requested_at=requested_at) == terminal_status
    with psycopg.connect(database_url) as conn:
        assert conn.execute(
            "SELECT run_status, completed_at FROM ingestion_runs WHERE run_id=%s", (snapshot.run_id,)
        ).fetchone() == (terminal_status, requested_at)


def test_native_lifecycle_snapshot_and_projection_round_trip_on_postgres(database_url):
    from house_consensus_ingestion.projection import PostgresListingProjectionWriter

    raw_records = [{
        "caseID": "case-42",
        "address": {"roadName": "Example Road", "houseNumber": "42", "zipCode": "2100", "cityName": "Copenhagen"},
        "priceCash": 2_500_000,
    }]
    snapshot = build_snapshot(source_system="house-consensus-ingestion", source_scope="boligsiden.dk/open-cases", records=raw_records)
    now = datetime(2026, 8, 7, tzinfo=UTC)
    writer = PostgresRunWriter(lambda: psycopg.connect(database_url))
    writer.write_started_run(snapshot=snapshot, requested_at=now)
    source_snapshot_id = writer.write_source_snapshot(
        snapshot=snapshot,
        source_name="boligsiden-search-cases",
        payload={
            "records": raw_records,
            "projection_records": [{"external_id": "case-42", "address": "Example Road 42, 2100 Copenhagen", "city": "Copenhagen", "price": 2_500_000}],
        },
        captured_at=now,
    )
    writer.write_stage_outcome(snapshot=snapshot, stage_name="fetch", stage_status="succeeded", outcome={"record_count": 1}, started_at=now, completed_at=now)

    assert PostgresListingProjectionWriter(lambda: psycopg.connect(database_url)).project_completed_snapshot(
        source_snapshot_id=source_snapshot_id, projected_at=now,
    ) == 1
    writer.complete_run(snapshot=snapshot, run_status="succeeded", completed_at=now)
    with psycopg.connect(database_url) as conn:
        assert conn.execute("SELECT run_status FROM ingestion_runs WHERE run_id=%s", (snapshot.run_id,)).fetchone() == ("succeeded",)
        assert conn.execute('SELECT "ExternalId", "Address", "Price" FROM listings').fetchone() == ("case-42", "Example Road 42, 2100 Copenhagen", 2_500_000)
        assert conn.execute("SELECT source_record_id FROM listing_ingestion_projections").fetchone() == ("case-42",)


def test_projection_failure_leaves_a_terminal_failed_run_and_a_reconcilable_snapshot(database_url):
    from house_consensus_ingestion.boligsiden import RawFetchSnapshot
    from house_consensus_ingestion.projection import PostgresListingProjectionWriter
    from house_consensus_ingestion.orchestration import NativeIngestionOrchestrator

    raw_records = ({
        "caseID": "case-42",
        "address": {"roadName": "Example Road", "houseNumber": "42", "zipCode": "2100", "cityName": "Copenhagen"},
        "priceCash": 2_500_000,
    },)
    snapshot = build_snapshot(
        source_system="house-consensus-ingestion", source_scope="boligsiden.dk/open-cases", records=raw_records
    )
    now = datetime(2026, 8, 7, tzinfo=UTC)

    class Fetcher:
        def fetch(self):
            return RawFetchSnapshot(records=raw_records, run_snapshot=snapshot)

    class FailingProjector:
        def project_completed_snapshot(self, *, source_snapshot_id: str, projected_at: datetime) -> int:
            raise RuntimeError("listing lock timeout")

    with pytest.raises(RuntimeError, match="listing lock timeout"):
        NativeIngestionOrchestrator(
            fetcher=Fetcher(),
            run_writer=PostgresRunWriter(lambda: psycopg.connect(database_url)),
            projector=FailingProjector(),
        ).run(dry_run=False, requested_at=now)

    with psycopg.connect(database_url) as conn:
        assert conn.execute("SELECT run_status FROM ingestion_runs WHERE run_id=%s", (snapshot.run_id,)).fetchone() == ("failed",)
        assert conn.execute("SELECT count(*) FROM ingestion_source_snapshots WHERE run_id=%s", (snapshot.run_id,)).fetchone() == (1,)
        assert conn.execute(
            "SELECT stage_name, stage_status FROM ingestion_stage_outcomes WHERE run_id=%s ORDER BY outcome_id",
            (snapshot.run_id,),
        ).fetchall() == [("fetch", "succeeded"), ("projection", "failed")]
        source_snapshot_id = conn.execute(
            "SELECT snapshot_id FROM ingestion_source_snapshots WHERE run_id=%s", (snapshot.run_id,)
        ).fetchone()[0]
        assert conn.execute("SELECT count(*) FROM listing_ingestion_projections").fetchone() == (0,)

    assert PostgresListingProjectionWriter(lambda: psycopg.connect(database_url)).project_completed_snapshot(
        source_snapshot_id=str(source_snapshot_id), projected_at=now
    ) == 1
    with psycopg.connect(database_url) as conn:
        assert conn.execute("SELECT run_status FROM ingestion_runs WHERE run_id=%s", (snapshot.run_id,)).fetchone() == ("failed",)
        assert conn.execute("SELECT count(*) FROM listing_ingestion_projections").fetchone() == (1,)


def test_failed_snapshot_without_a_successful_fetch_is_not_reconcilable(database_url):
    from house_consensus_ingestion.projection import (
        CompletedSourceSnapshotRequiredError,
        PostgresListingProjectionWriter,
    )

    raw_records = [{
        "caseID": "case-42",
        "address": {"roadName": "Example Road", "houseNumber": "42", "zipCode": "2100", "cityName": "Copenhagen"},
        "priceCash": 2_500_000,
    }]
    snapshot = build_snapshot(
        source_system="house-consensus-ingestion", source_scope="boligsiden.dk/open-cases", records=raw_records
    )
    now = datetime(2026, 8, 7, tzinfo=UTC)
    writer = PostgresRunWriter(lambda: psycopg.connect(database_url))
    writer.write_started_run(snapshot=snapshot, requested_at=now)
    source_snapshot_id = writer.write_source_snapshot(
        snapshot=snapshot,
        source_name="boligsiden-search-cases",
        payload={"projection_records": [{"external_id": "case-42", "address": "Example Road 42"}]},
        captured_at=now,
    )
    writer.complete_run(snapshot=snapshot, run_status="failed", completed_at=now)

    with pytest.raises(CompletedSourceSnapshotRequiredError, match="successful fetch"):
        PostgresListingProjectionWriter(lambda: psycopg.connect(database_url)).project_completed_snapshot(
            source_snapshot_id=source_snapshot_id, projected_at=now
        )


@pytest.mark.parametrize("run_status", ["running", "succeeded", "failed"])
def test_projection_requires_a_successful_fetch_for_every_eligible_run_status(database_url, run_status):
    from house_consensus_ingestion.projection import (
        CompletedSourceSnapshotRequiredError,
        PostgresListingProjectionWriter,
    )

    snapshot = build_snapshot(
        source_system="house-consensus-ingestion",
        source_scope=f"boligsiden.dk/open-cases/{run_status}",
        records=[{"external_id": "case-42", "address": "Example Road 42"}],
    )
    now = datetime(2026, 8, 7, tzinfo=UTC)
    writer = PostgresRunWriter(lambda: psycopg.connect(database_url))
    writer.write_started_run(snapshot=snapshot, requested_at=now)
    source_snapshot_id = writer.write_source_snapshot(
        snapshot=snapshot,
        source_name="boligsiden-search-cases",
        payload={"projection_records": [{"external_id": "case-42", "address": "Example Road 42"}]},
        captured_at=now,
    )
    if run_status != "running":
        writer.complete_run(snapshot=snapshot, run_status=run_status, completed_at=now)

    with pytest.raises(CompletedSourceSnapshotRequiredError, match="successful fetch"):
        PostgresListingProjectionWriter(lambda: psycopg.connect(database_url)).project_completed_snapshot(
            source_snapshot_id=source_snapshot_id, projected_at=now
        )


@pytest.mark.parametrize("run_status", ["running", "succeeded", "failed"])
def test_projection_accepts_a_successful_fetch_for_every_eligible_run_status(database_url, run_status):
    from house_consensus_ingestion.projection import PostgresListingProjectionWriter

    snapshot = build_snapshot(
        source_system="house-consensus-ingestion",
        source_scope=f"boligsiden.dk/open-cases/fetch-succeeded/{run_status}",
        records=[{"external_id": "case-42", "address": "Example Road 42"}],
    )
    now = datetime(2026, 8, 7, tzinfo=UTC)
    writer = PostgresRunWriter(lambda: psycopg.connect(database_url))
    writer.write_started_run(snapshot=snapshot, requested_at=now)
    source_snapshot_id = writer.write_source_snapshot(
        snapshot=snapshot,
        source_name="boligsiden-search-cases",
        payload={"projection_records": [{"external_id": "case-42", "address": "Example Road 42"}]},
        captured_at=now,
    )
    writer.write_stage_outcome(
        snapshot=snapshot,
        stage_name="fetch",
        stage_status="succeeded",
        outcome={"record_count": 1},
        started_at=now,
        completed_at=now,
    )
    if run_status != "running":
        writer.complete_run(snapshot=snapshot, run_status=run_status, completed_at=now)

    assert PostgresListingProjectionWriter(lambda: psycopg.connect(database_url)).project_completed_snapshot(
        source_snapshot_id=source_snapshot_id, projected_at=now
    ) == 1
