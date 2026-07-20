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


def _case(source_id="one", **match):
    return ExportCase.from_records(
        {"caseID": source_id, "address": "A 1"},
        {"id": source_id, "family_score": 50, **match},
    )


def test_idempotent_upsert_provenance_ai_and_override_preservation(database_url):
    exporter = PostgresExporter(database_url)
    case = _case(
        ai_decision="reject",
        ai_confidence="high",
        ai_model_version="model-1",
        ai_rule_version="rule-1",
        ai_evidence={"reason": "layout"},
    )
    exporter.export(
        [case], run_id="run-1", fetched_at=datetime(2026, 7, 20, tzinfo=timezone.utc)
    )
    with psycopg.connect(database_url) as conn:
        conn.execute("""insert into listing_overrides("ListingId","OwnerId","Action","CreatedAt")
            select "Id",'11111111-1111-1111-1111-111111111111','restore',now() from listings where "ExternalId"='one'""")
        conn.commit()
    changed = _case(
        ai_decision="pass",
        ai_confidence="high",
        ai_model_version="model-1",
        ai_rule_version="rule-1",
    )
    exporter.export(
        [changed], run_id="run-1", fetched_at=datetime(2026, 7, 20, tzinfo=timezone.utc)
    )
    with psycopg.connect(database_url) as conn:
        listing = conn.execute(
            'select "State"::text from listings where "ExternalId"=\'one\''
        ).fetchone()
        counts = conn.execute(
            "select (select count(*) from listing_imports), (select count(*) from ai_evidence), (select count(*) from listing_overrides)"
        ).fetchone()
    assert listing == ("restored",)
    assert counts == (1, 1, 1)


def test_archive_and_reappearance_lifecycle(database_url):
    exporter = PostgresExporter(database_url)
    exporter.export([_case("one"), _case("two")], run_id="r1")
    exporter.export([_case("one")], run_id="r2")
    with psycopg.connect(database_url) as conn:
        assert conn.execute(
            'select "ArchivedAt" is not null from listings where "ExternalId"=\'two\''
        ).fetchone()[0]
    exporter.export([_case("two")], run_id="r3")
    with psycopg.connect(database_url) as conn:
        assert not conn.execute(
            'select "ArchivedAt" is not null from listings where "ExternalId"=\'two\''
        ).fetchone()[0]
    sold = ExportCase.from_records(
        {"caseID": "two", "caseStatus": "sold"}, {"id": "two"}
    )
    exporter.export([sold], run_id="r4")
    with psycopg.connect(database_url) as conn:
        assert (
            conn.execute("""select l."ArchivedAt" is not null,s.archive_reason from listings l
            join listing_export_state s on s.listing_id=l."Id" where l."ExternalId"='two'""").fetchone()
            == (True, "sold")
        )
