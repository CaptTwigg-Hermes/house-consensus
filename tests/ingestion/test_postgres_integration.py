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
