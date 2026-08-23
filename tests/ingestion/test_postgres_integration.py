import os
from concurrent.futures import ThreadPoolExecutor
from datetime import UTC, datetime
from pathlib import Path
from threading import Barrier, Lock

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


def test_projection_failure_keeps_source_succeeded_and_retries_the_completed_snapshot(database_url):
    from house_consensus_ingestion.boligsiden import RawFetchSnapshot
    from house_consensus_ingestion.classification import ClassificationConfig
    from house_consensus_ingestion.orchestration import NativeIngestionOrchestrator
    from house_consensus_ingestion.pipeline import NativeCasePipeline
    from house_consensus_ingestion.projection import PostgresListingProjectionWriter

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
            return RawFetchSnapshot(
                records=raw_records,
                run_snapshot=snapshot,
                source_config_sha256="a" * 64,
            )

    class FailingProjector:
        def project_completed_snapshot(self, *, source_snapshot_id: str, projected_at: datetime) -> int:
            raise RuntimeError("listing lock timeout")

    pipeline = NativeCasePipeline(classification=ClassificationConfig(price_max=3_000_000))
    writer_factory = lambda: PostgresRunWriter(lambda: psycopg.connect(database_url))
    with pytest.raises(RuntimeError, match="listing lock timeout"):
        NativeIngestionOrchestrator(
            fetcher=Fetcher(), pipeline=pipeline, run_writer=writer_factory(), projector=FailingProjector(),
        ).run(dry_run=False, requested_at=now)

    with psycopg.connect(database_url) as conn:
        assert conn.execute("SELECT run_status FROM ingestion_runs WHERE run_id=%s", (snapshot.run_id,)).fetchone() == ("succeeded",)
        assert conn.execute("SELECT count(*) FROM ingestion_source_snapshots WHERE run_id=%s", (snapshot.run_id,)).fetchone() == (1,)
        assert conn.execute(
            "SELECT stage_name, stage_status FROM ingestion_stage_outcomes WHERE run_id=%s ORDER BY outcome_id",
            (snapshot.run_id,),
        ).fetchall() == [
            ("fetch", "succeeded"),
            ("classification", "succeeded"),
            ("scoring", "succeeded"),
        ]
        assert conn.execute(
            "SELECT attempt, projection_status FROM ingestion_projection_outcomes WHERE run_id=%s ORDER BY attempt",
            (snapshot.run_id,),
        ).fetchall() == [(1, "failed")]
        assert conn.execute("SELECT count(*) FROM listing_ingestion_projections").fetchone() == (0,)

    retried = NativeIngestionOrchestrator(
        fetcher=Fetcher(), pipeline=pipeline, run_writer=writer_factory(),
        projector=PostgresListingProjectionWriter(lambda: psycopg.connect(database_url)),
    ).run(dry_run=False, requested_at=now)
    assert retried.run_status == "succeeded"
    assert retried.projected_count == 1

    with psycopg.connect(database_url) as conn:
        assert conn.execute("SELECT run_status FROM ingestion_runs WHERE run_id=%s", (snapshot.run_id,)).fetchone() == ("succeeded",)
        assert conn.execute(
            "SELECT attempt, projection_status FROM ingestion_projection_outcomes WHERE run_id=%s ORDER BY attempt",
            (snapshot.run_id,),
        ).fetchall() == [(1, "failed"), (2, "succeeded")]
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



def test_projection_gate_allows_production_projector_read_and_runs_concurrent_retry_once(database_url):
    """The writer advisory gate must not block the real projector key-share read."""
    from house_consensus_ingestion.projection import ExporterBackedPostgresListingProjector

    raw_records = [{"caseID": "case-gate", "address": {"roadName": "Gate Road", "houseNumber": "1", "zipCode": "2100", "cityName": "Copenhagen"}}]
    snapshot = build_snapshot(source_system="house-consensus-ingestion", source_scope="boligsiden.dk/gate", records=raw_records)
    now = datetime(2026, 8, 7, tzinfo=UTC)
    writer = PostgresRunWriter(lambda: psycopg.connect(database_url))
    writer.write_started_run(snapshot=snapshot, requested_at=now)
    source_snapshot_id = writer.write_source_snapshot(
        snapshot=snapshot, source_name="boligsiden-search-cases",
        payload={
            "source_system": snapshot.source_system, "source_scope": snapshot.source_scope,
            "manifest_sha256": snapshot.manifest_sha256, "source_config_sha256": "a" * 64,
            "snapshot_count": 1, "records": raw_records,
        }, captured_at=now,
    )
    writer.write_stage_outcome(snapshot=snapshot, stage_name="fetch", stage_status="succeeded", outcome={}, started_at=now, completed_at=now)
    writer.complete_run(snapshot=snapshot, run_status="succeeded", completed_at=now)

    exports: list[str] = []
    export_lock = Lock()

    class Exporter:
        def export(self, cases, **kwargs):
            with export_lock:
                exports.append(kwargs["run_id"])
            return type("Result", (), {"exported": len(cases)})()

    projector = ExporterBackedPostgresListingProjector(
        connection_factory=lambda: psycopg.connect(database_url), exporter_factory=lambda _: Exporter(),
    )
    barrier = Barrier(2)

    def retry() -> int:
        barrier.wait(timeout=5)
        return writer.run_projection_once(
            snapshot=snapshot, source_snapshot_id=source_snapshot_id, projected_at=now,
            project=lambda: projector.project_completed_snapshot(source_snapshot_id=source_snapshot_id, projected_at=now),
        )

    with ThreadPoolExecutor(max_workers=2) as pool:
        results = [future.result(timeout=10) for future in (pool.submit(retry), pool.submit(retry))]

    assert sorted(results) == [0, 1]
    assert exports == [snapshot.run_id]
    with psycopg.connect(database_url) as conn:
        assert conn.execute(
            "SELECT attempt, projection_status FROM ingestion_projection_outcomes WHERE run_id=%s", (snapshot.run_id,)
        ).fetchall() == [(1, "succeeded")]



def test_projection_outcome_trigger_rejects_non_succeeded_or_cross_run_snapshot(database_url):
    now = datetime(2026, 8, 7, tzinfo=UTC)

    def make_run(scope: str, status: str) -> tuple[object, str]:
        snapshot = build_snapshot(
            source_system="house-consensus-ingestion", source_scope=scope,
            records=[{"external_id": scope, "address": f"{scope} Street 1"}],
        )
        writer = PostgresRunWriter(lambda: psycopg.connect(database_url))
        writer.write_started_run(snapshot=snapshot, requested_at=now)
        source_snapshot_id = writer.write_source_snapshot(
            snapshot=snapshot, source_name="boligsiden-search-cases", payload={"records": []}, captured_at=now,
        )
        writer.complete_run(snapshot=snapshot, run_status=status, completed_at=now)
        return snapshot, source_snapshot_id

    succeeded, succeeded_snapshot_id = make_run("trigger/succeeded", "succeeded")
    failed, failed_snapshot_id = make_run("trigger/failed", "failed")
    cancelled, cancelled_snapshot_id = make_run("trigger/cancelled", "cancelled")

    with psycopg.connect(database_url) as conn:
        conn.execute(
            """INSERT INTO ingestion_projection_outcomes
            (run_id, source_snapshot_id, attempt, projection_status, outcome, started_at, completed_at)
            VALUES (%s, %s, 1, 'succeeded', '{}'::jsonb, %s, %s)""",
            (succeeded.run_id, succeeded_snapshot_id, now, now),
        )
        conn.commit()
        for run_id, source_snapshot_id in (
            (failed.run_id, failed_snapshot_id),
            (cancelled.run_id, cancelled_snapshot_id),
            (succeeded.run_id, failed_snapshot_id),
        ):
            with pytest.raises(psycopg.DatabaseError):
                conn.execute(
                    """INSERT INTO ingestion_projection_outcomes
                    (run_id, source_snapshot_id, attempt, projection_status, outcome, started_at, completed_at)
                    VALUES (%s, %s, 1, 'failed', '{}'::jsonb, %s, %s)""",
                    (run_id, source_snapshot_id, now, now),
                )
            conn.rollback()

        assert conn.execute("SELECT count(*) FROM ingestion_projection_outcomes").fetchone() == (1,)



def test_same_payload_from_distinct_sources_has_distinct_immutable_snapshot_ids(database_url):
    snapshot = build_snapshot(
        source_system="house-consensus-ingestion", source_scope="identity/source-name",
        records=[{"external_id": "case-1", "address": "Identity Road 1"}],
    )
    now = datetime(2026, 8, 7, tzinfo=UTC)
    writer = PostgresRunWriter(lambda: psycopg.connect(database_url))
    writer.write_started_run(snapshot=snapshot, requested_at=now)

    first = writer.write_source_snapshot(
        snapshot=snapshot, source_name="boligsiden-search-cases",
        payload={"records": [{"caseID": "case-1"}]}, captured_at=now,
    )
    second = writer.write_source_snapshot(
        snapshot=snapshot, source_name="boligsiden-listing-details",
        payload={"records": [{"caseID": "case-1"}]}, captured_at=now,
    )

    assert first != second
    with psycopg.connect(database_url) as conn:
        assert conn.execute(
            "SELECT source_name, snapshot_id::text FROM ingestion_source_snapshots WHERE run_id=%s ORDER BY source_name",
            (snapshot.run_id,),
        ).fetchall() == [
            ("boligsiden-listing-details", second),
            ("boligsiden-search-cases", first),
        ]
