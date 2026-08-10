from __future__ import annotations

import os
from datetime import datetime, timezone

import psycopg
import pytest

from consensus_exporter.models import ExportCase
from consensus_exporter.postgres import PostgresExporter, ensure_schema

@pytest.fixture()
def database_url():
    url = os.environ.get("TEST_DATABASE_URL")
    if not url:
        pytest.skip("TEST_DATABASE_URL is not configured")
    with psycopg.connect(url, autocommit=True) as conn:
        conn.execute("drop schema public cascade")
        conn.execute("create schema public")
        ensure_schema(conn)
    return url

def case(roof_material: str) -> ExportCase:
    return ExportCase.from_records(
        {"caseID": "asbestos-versioned", "address": "A asbestos-versioned"},
        {"id": "asbestos-versioned", "family_score": 50, "roofMaterial": roof_material},
    )

def test_asbestos_assessments_are_versioned_and_do_not_replace_member_correction(database_url):
    exporter = PostgresExporter(database_url)
    exporter.export([case("Asbestholdige bølgeplader")], run_id="asbestos-run-1", fetched_at=datetime(2026, 8, 10, 8, tzinfo=timezone.utc))
    with psycopg.connect(database_url) as conn:
        first = conn.execute("select status,primary_source,rule_version from asbestos_roof_assessments").fetchone()
        conn.execute('''update listings set "AsbestosRoofCorrection"='Possible' where "ExternalId"='asbestos-versioned' ''')
        conn.commit()
    exporter.export([case("Tegl")], run_id="asbestos-run-2", fetched_at=datetime(2026, 8, 11, 8, tzinfo=timezone.utc))
    with psycopg.connect(database_url) as conn:
        rows = conn.execute('''select a.status,l."AsbestosRoofCorrection" from asbestos_roof_assessments a join listings l on l."Id"=a.listing_id order by a.assessed_at,a.id''').fetchall()
    assert first == ("likely", "structured", "asbestos-roof-v1")
    assert rows == [("likely", "Possible"), ("no_indication", "Possible")]


def test_failed_asbestos_assessment_stores_unknown_without_blocking_export(database_url, monkeypatch):
    import consensus_exporter.postgres as postgres

    monkeypatch.setattr(postgres, "assess_asbestos_roof", lambda _: (_ for _ in ()).throw(RuntimeError("broken")))
    PostgresExporter(database_url).export(
        [case("Asbest")],
        run_id="asbestos-failed-run",
        fetched_at=datetime(2026, 8, 12, 8, tzinfo=timezone.utc),
    )

    with psycopg.connect(database_url) as conn:
        assessment = conn.execute("select status,confidence,primary_source from asbestos_roof_assessments").fetchone()
        listing_count = conn.execute("select count(*) from listings").fetchone()[0]

    assert assessment == ("unknown", None, None)
    assert listing_count == 1


def test_schema_upgrade_reassesses_retained_nonarchived_raw_payload_at_source_time(database_url):
    assessed_at = datetime(2026, 7, 1, 8, tzinfo=timezone.utc)
    PostgresExporter(database_url).export(
        [case("Asbestholdige bølgeplader")],
        run_id="asbestos-retained-source",
        fetched_at=assessed_at,
    )

    with psycopg.connect(database_url, autocommit=True) as conn:
        conn.execute("drop table asbestos_roof_assessments")
        ensure_schema(conn)
        rows = conn.execute(
            """select status, rule_version, assessed_at
               from asbestos_roof_assessments
               where rule_version = 'asbestos-roof-v1'"""
        ).fetchall()

    real = [row for row in rows if row[1] == "asbestos-roof-v1"]
    assert real == [("likely", "asbestos-roof-v1", assessed_at)]
