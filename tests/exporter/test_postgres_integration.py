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


def test_explicit_schema_bootstrap_supports_a_fresh_application_database(database_url):
    with psycopg.connect(database_url, autocommit=True) as conn:
        conn.execute("drop schema public cascade")
        conn.execute("create schema public")

    result = PostgresExporter(database_url, ensure_schema_on_export=True).export(
        [_case("bootstrap")], run_id="bootstrap-run"
    )

    assert result.exported == 1
    with psycopg.connect(database_url) as conn:
        assert conn.execute("select count(*) from export_runs").fetchone() == (1,)
        assert conn.execute("select count(*) from listings").fetchone() == (1,)


def test_export_populates_house_card_facts(database_url):
    case = _case(
        "card",
        preview_image="https://images.example.test/house.webp",
        housing_area_m2=249,
        garden_size_m2=1563,
        rooms=8,
        year_built=1948,
        numberOfBathrooms=2,
        vision_bedroom_count=3,
        number_of_floors=1,
        energy_label="a2020",
        noise_status="quiet",
        buildable_headroom_m2=220,
        vision_ground_floor_bedroom=True,
        vision_separate_entrance=True,
        vision_second_kitchen=True,
        vision_privacy_score=5,
    )

    PostgresExporter(database_url).export([case], run_id="card-facts")

    with psycopg.connect(database_url) as conn:
        actual = conn.execute(
            '''SELECT "PreviewImageUrl","LivingArea","LotArea","Rooms","YearBuilt",
                      "Bathrooms","Bedrooms","Floors","EnergyLabel","Quiet",
                      "BuildableHeadroom","GroundFloorBedroom","SeparateEntrance",
                      "SecondKitchen","PrivacyScore"
               FROM listings WHERE "ExternalId"='card' '''
        ).fetchone()
    assert actual == (
        "https://images.example.test/house.webp", 249, 1563, 8, 1948,
        2, 3, 1, "A2020", True, 220, True, True, True, 5,
    )


def test_exports_family_score_breakdown(database_url):
    exporter = PostgresExporter(database_url)
    case = _case(
        family_score=81,
        vision_privacy_score=5,
        family_score_breakdown={
            "privacy": 90,
            "kids_space": 80,
            "garden": 70,
            "shared_living": 80,
            "practical": 80,
        },
    )

    exporter.export([case], run_id="score-breakdown")

    with psycopg.connect(database_url) as conn:
        scores = conn.execute(
            "select \"FamilyPrivacyScore\",\"KidsSpaceScore\",\"GardenScore\","
            "\"SharedLivingScore\",\"PracticalScore\",\"FamilyPrivacyWeight\","
            "\"KidsSpaceWeight\",\"GardenWeight\",\"SharedLivingWeight\",\"PracticalWeight\" "
            "from listings where \"ExternalId\"='one'"
        ).fetchone()
    assert scores == (90, 80, 70, 80, 80, 30, 20, 20, 15, 15)
    assert sum(score * weight / 100 for score, weight in zip(scores[:5], scores[5:])) == 81

    inconsistent = _case(
        "bad",
        family_score=81,
        vision_privacy_score=5,
        family_score_breakdown={
            "privacy": 0, "kids_space": 0, "garden": 0,
            "shared_living": 0, "practical": 0,
        },
    )
    exporter.export([inconsistent], run_id="bad-score-breakdown")
    with psycopg.connect(database_url) as conn:
        rejected_breakdown = conn.execute(
            'select "FamilyPrivacyScore","FamilyPrivacyWeight" '
            "from listings where \"ExternalId\"='bad'"
        ).fetchone()
    assert rejected_breakdown == (None, None)


    renormalized = _case(
        "renormalized",
        family_score=33.9,
        vision_privacy_score=1,
        family_score_breakdown={
            "privacy": 0, "kids_space": 33, "garden": 46,
            "shared_living": 6, "practical": 47,
            "weights": {
                "privacy": 0, "kids_space": 20 / 0.7, "garden": 20 / 0.7,
                "shared_living": 15 / 0.7, "practical": 15 / 0.7,
            },
        },
    )
    exporter.export([renormalized], run_id="renormalized-score-breakdown")
    with psycopg.connect(database_url) as conn:
        weights = conn.execute(
            "select \"FamilyPrivacyWeight\",\"KidsSpaceWeight\" "
            "from listings where \"ExternalId\"='renormalized'"
        ).fetchone()
    assert weights == pytest.approx((0, 20 / 0.7))
