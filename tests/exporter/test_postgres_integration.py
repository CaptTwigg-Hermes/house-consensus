import os
import time
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import uuid

import consensus_exporter.postgres as postgres_module
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
        {"caseID": source_id, "address": f"A {source_id}"},
        {"id": source_id, "family_score": 50, **match},
    )


def test_canonical_root_url_matches_application_behavior():
    assert postgres_module._canonical_listing_url("https://Example.dk/?utm_source=x#photos") == "https://example.dk/"
    assert postgres_module._canonical_listing_url("https://Example.dk:8443/home/") == "https://example.dk:8443/home"
    assert postgres_module._canonical_listing_url("https://user:secret@example.dk/home") is None
    assert postgres_module._canonical_listing_url("https://[2001:db8::1]:8443/home/") == "https://[2001:db8::1]:8443/home"
    assert postgres_module._canonical_listing_url("https://example.dk:70000/home") is None
    assert postgres_module._canonical_listing_url("https://münchen.example/home") == "https://xn--mnchen-3ya.example/home"
    assert postgres_module._canonical_listing_url("https://127.0.0.1:8443/home") == "https://127.0.0.1:8443/home"
    assert postgres_module._canonical_listing_url("https://example.dk/a/../home/") == "https://example.dk/home"
    assert postgres_module._canonical_listing_url("https://example.dk/%2e%2e/home/") == "https://example.dk/home"
    assert postgres_module._canonical_listing_url("https://example.dk/%7Euser/") == "https://example.dk/~user"
    assert postgres_module._canonical_listing_url("https://example.dk/a//home/") == "https://example.dk/a//home"
    assert postgres_module._canonical_listing_url("https://example.dk/home?q=a%20b") == "https://example.dk/home?q=a+b"
    assert postgres_module._canonical_listing_url("https://example.dk/home?q=%7E%2F%C3%B8") == "https://example.dk/home?q=~%2f%C3%B8"
    assert postgres_module._canonical_listing_url("https://127.000.000.001/home") == "https://127.0.0.1/home"
    assert postgres_module._canonical_listing_url("https://127.1/home") == "https://127.0.0.1/home"
    assert postgres_module._canonical_listing_url("https://2130706433/home") == "https://127.0.0.1/home"
    assert postgres_module._canonical_listing_url("https://0177.0.0.1/home") == "https://127.0.0.1/home"
    assert postgres_module._canonical_listing_url("https://0x7f.0.0.1/home") == "https://127.0.0.1/home"
    assert postgres_module._canonical_listing_url("https://example.dk/%zz/home") == "https://example.dk/%25zz/home"
    assert postgres_module._canonical_listing_url("https://example.dk/home?A=1&a=2") == "https://example.dk/home?A=1&A=2"
    assert postgres_module._canonical_listing_url("https://4294967296/home") == "https://4294967296/home"
    assert postgres_module._canonical_listing_url("https://09.0.0.1/home") == "https://09.0.0.1/home"
    assert postgres_module._canonical_listing_url("https://0x100000000/home") == "https://0x100000000/home"
    assert postgres_module._canonical_listing_url("https://256.1.1.1/home") == "https://256.1.1.1/home"
    assert postgres_module._normalize_listing_address("a" * 501) is None
    assert postgres_module._canonical_listing_url("https://example.dk/" + "a" * 2049) is None


def test_oversized_imported_address_does_not_abort_export(database_url):
    PostgresExporter(database_url).export([_case("oversized-address", address="a" * 501)], run_id="oversized-address")
    with psycopg.connect(database_url) as conn:
        ensure_schema(conn)
        row = conn.execute('select "NormalizedAddress" from listings where "ExternalId"=%s', ("oversized-address",)).fetchone()
    assert row == (None,)


def test_oversized_imported_url_does_not_abort_export(database_url):
    PostgresExporter(database_url).export([_case("oversized-url", url="https://example.dk/" + "a" * 2049)], run_id="oversized-url")
    with psycopg.connect(database_url) as conn:
        row = conn.execute('select "CanonicalUrl" from listings where "ExternalId"=%s', ("oversized-url",)).fetchone()
    assert row == (None,)


def test_export_does_not_match_unrelated_null_canonical_urls(database_url):
    imported_at = datetime(2026, 7, 29, 11, tzinfo=timezone.utc)
    with psycopg.connect(database_url) as conn:
        ensure_schema(conn)
        conn.execute(
            '''INSERT INTO listings ("Id","ExternalId","Address","FamilyFitScore","State","ImportedAt","AiAssessed","SourceUrl","CanonicalUrl","NormalizedAddress")
               VALUES (%s,'legacy-invalid','Legacyvej 9',80,'active',%s,false,'not a url',NULL,'legacyvej 9')''',
            (uuid.uuid4(), imported_at),
        )
        conn.commit()
    PostgresExporter(database_url).export([_case("incoming-invalid", address="Nyvej 10", url="also not a url")], run_id="null-canonical-no-match")
    with psycopg.connect(database_url) as conn:
        rows = conn.execute('select "ExternalId" from listings order by "ExternalId"').fetchall()
    assert rows == [("incoming-invalid",), ("legacy-invalid",)]


def test_export_canonicalizes_legacy_source_url_before_duplicate_matching(database_url):
    archived_at = datetime(2026, 7, 29, 11, tzinfo=timezone.utc)
    with psycopg.connect(database_url) as conn:
        ensure_schema(conn)
        conn.execute(
            '''INSERT INTO listings ("Id","ExternalId","Address","FamilyFitScore","State","ImportedAt","ArchivedAt","AiAssessed","SourceUrl","CanonicalUrl","NormalizedAddress")
               VALUES (%s,'legacy-canonical','Legacyvej 1',80,'archived',%s,%s,false,'https://Example.dk/home/?utm_source=old#photo',NULL,'legacyvej 1')''',
            (uuid.uuid4(), archived_at, archived_at),
        )
        conn.commit()
    PostgresExporter(database_url).export([_case("incoming-new-id", address="Different address", url="https://example.dk/home")], run_id="legacy-canonical-match")
    with psycopg.connect(database_url) as conn:
        rows = conn.execute('select "ExternalId","CanonicalUrl" from listings').fetchall()
    assert rows == [("legacy-canonical", "https://example.dk/home")]


def test_export_rejects_split_url_and_address_identity_without_mutation(database_url):
    exporter = PostgresExporter(database_url)
    exporter.export([_case("split-url", address="Urlvej 1", url="https://example.dk/split-url")], run_id="split-url")
    exporter.export([_case("split-address", address="Adressevej 2", url="https://example.dk/split-address")], run_id="split-address")
    with pytest.raises(ValueError, match="different existing listings"):
        exporter.export([_case("split-conflict", address="Adressevej 2", url="https://example.dk/split-url")], run_id="split-conflict")
    with psycopg.connect(database_url) as conn:
        rows = conn.execute('select "ExternalId","CanonicalUrl","NormalizedAddress" from listings order by "ExternalId"').fetchall()
    assert len(rows) == 2
    assert {row[0] for row in rows} == {"split-url", "split-address"}


def test_importer_preserves_manually_archived_listing_state_and_timestamp(database_url):
    archived_at = datetime(2026, 7, 1, 12, tzinfo=timezone.utc)
    manual_id = "11111111-1111-1111-1111-111111111111"
    with psycopg.connect(database_url) as conn:
        ensure_schema(conn)
        conn.execute(
            '''INSERT INTO listings
               ("Id","ExternalId","Address","FamilyFitScore","State","AiAssessed","ImportedAt","ArchivedAt","SourceUrl","CanonicalUrl","NormalizedAddress","IsManuallyAdded","ManualLifecycleProtected")
               VALUES (%s,'manual:archived','Manualvej 1',NULL,'archived',false,%s,%s,'https://example.dk/manual-archived','https://example.dk/manual-archived','manualvej 1',true,true)''',
            (manual_id, archived_at, archived_at),
        )
        conn.commit()
    PostgresExporter(database_url).export(
        [_case("imported-copy", address="Manualvej 1", url="https://example.dk/manual-archived")],
        run_id="manual-archived-reappearance",
    )
    with psycopg.connect(database_url) as conn:
        row = conn.execute('select "ExternalId","State"::text,"ArchivedAt","ManualLifecycleProtected" from listings where "Id"=%s', (manual_id,)).fetchone()
        count = conn.execute('select count(*) from listings').fetchone()[0]
    assert row == ("manual:archived", "archived", archived_at, True)
    assert count == 1


def test_imported_rows_deduplicate_by_normalized_address(database_url):
    exporter = PostgresExporter(database_url)
    exporter.export([_case("address-one", address="  Samevej  1 ")], run_id="address-one")
    exporter.export([_case("address-two", address="samevej 1")], run_id="address-two")
    with psycopg.connect(database_url) as conn:
        assert conn.execute('select count(*) from listings where "NormalizedAddress"=%s', ("samevej 1",)).fetchone()[0] == 1


def test_imported_rows_store_manual_deduplication_keys(database_url):
    case = _case("dedup-keys", url="https://Example.dk/home/?utm_source=mail#photos")
    PostgresExporter(database_url).export([case], run_id="dedup-keys")
    with psycopg.connect(database_url) as conn:
        keys = conn.execute('select "CanonicalUrl","NormalizedAddress" from listings where "ExternalId"=%s', ("dedup-keys",)).fetchone()
    assert keys == ("https://example.dk/home", "a dedup-keys")

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
    exporter.export(
        [case], run_id="run-1", fetched_at=datetime(2026, 7, 20, tzinfo=timezone.utc)
    )
    changed = _case(
        ai_decision="pass",
        ai_confidence="high",
        ai_model_version="model-1",
        ai_rule_version="rule-1",
    )
    with pytest.raises(RuntimeError, match="different immutable snapshot"):
        exporter.export(
            [changed],
            run_id="run-1",
            fetched_at=datetime(2026, 7, 20, tzinfo=timezone.utc),
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
    initial = [_case(str(i)) for i in range(1, 7)]
    exporter.export(
        initial, run_id="r1", fetched_at=datetime(2026, 7, 24, 8, tzinfo=timezone.utc)
    )
    with psycopg.connect(database_url) as conn:
        listing_id = conn.execute(
            'select "Id" from listings where "ExternalId"=%s', ("6",)
        ).fetchone()[0]
        conn.execute('create table votes ("ListingId" uuid not null)')
        conn.execute('insert into votes ("ListingId") values (%s)', (listing_id,))
    exporter.export(
        initial[:-1],
        run_id="r2",
        fetched_at=datetime(2026, 7, 27, 8, tzinfo=timezone.utc),
    )
    with psycopg.connect(database_url) as conn:
        assert not conn.execute(
            'select "ArchivedAt" is not null from listings where "ExternalId"=\'6\''
        ).fetchone()[0]
    exporter.export(
        initial[:-1],
        run_id="r2-retry",
        fetched_at=datetime(2026, 7, 27, 9, tzinfo=timezone.utc),
    )
    with psycopg.connect(database_url) as conn:
        assert not conn.execute(
            'select "ArchivedAt" is not null from listings where "ExternalId"=\'6\''
        ).fetchone()[0]
    exporter.export(
        initial[:-1],
        run_id="r3",
        fetched_at=datetime(2026, 7, 28, 8, tzinfo=timezone.utc),
    )
    with psycopg.connect(database_url) as conn:
        assert conn.execute(
            'select "ArchivedAt" is not null from listings where "ExternalId"=\'6\''
        ).fetchone()[0]
    exporter.export(
        [_case("6")],
        run_id="r4",
        fetched_at=datetime(2026, 7, 29, 8, tzinfo=timezone.utc),
    )
    with psycopg.connect(database_url) as conn:
        assert conn.execute(
            'select "State"::text,"ArchivedAt" is null from listings where "ExternalId"=\'6\''
        ).fetchone() == ("active", True)
        assert (
            conn.execute(
                'select count(*) from votes where "ListingId"=%s', (listing_id,)
            ).fetchone()[0]
            == 1
        )
    sold = ExportCase.from_records({"caseID": "6", "caseStatus": "sold"}, {"id": "6"})
    exporter.export([sold], run_id="r5")
    with psycopg.connect(database_url) as conn:
        assert (
            conn.execute("""select l."ArchivedAt" is not null,s.archive_reason from listings l
            join listing_export_state s on s.listing_id=l."Id" where l."ExternalId"='6'""").fetchone()
            == (True, "sold")
        )


def test_dry_run_reports_changes_and_rolls_them_back(database_url):
    result = PostgresExporter(database_url).export(
        [_case("dry")], run_id="dry-run", dry_run=True
    )
    with psycopg.connect(database_url) as conn:
        assert (
            conn.execute(
                "select count(*) from listings where \"ExternalId\"='dry'"
            ).fetchone()[0]
            == 0
        )
        assert (
            conn.execute(
                "select count(*) from export_runs where run_id='dry-run'"
            ).fetchone()[0]
            == 0
        )
    assert result.inserted == 1
    assert result.exported == 1


def test_mass_removal_guard_blocks_archival_over_twenty_percent(database_url):
    exporter = PostgresExporter(database_url)
    exporter.export(
        [_case(str(i)) for i in range(5)],
        run_id="guard-1",
        fetched_at=datetime(2026, 7, 24, 8, tzinfo=timezone.utc),
    )
    exporter.export(
        [_case("0")],
        run_id="guard-2",
        fetched_at=datetime(2026, 7, 27, 8, tzinfo=timezone.utc),
    )
    result = exporter.export(
        [_case("0")],
        run_id="guard-3",
        fetched_at=datetime(2026, 7, 28, 8, tzinfo=timezone.utc),
    )
    with psycopg.connect(database_url) as conn:
        archived = conn.execute(
            'select count(*) from listings where "ArchivedAt" is not null'
        ).fetchone()[0]
    assert archived == 0
    assert result.archival_blocked == 4


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
        road_noise_db=62.5,
        rail_noise_db=57.5,
        air_noise_db=67.5,
        buildable_headroom_m2=220,
        vision_ground_floor_bedroom=True,
        vision_separate_entrance=True,
        vision_second_kitchen=True,
        vision_privacy_score=5,
        _coordinates={"lat": 55.7, "lon": 12.4},
        monthly_expense=5244,
        days_on_market=18,
        commute={
            "status": "ok",
            "destinations": {
                "work": {
                    "label": "Capt Twigg",
                    "car": {"min": 31, "km": 26.4},
                    "bike": {"min": 74, "km": 23.1},
                    "public": {"min": 52, "transfers": 1},
                },
                "station": {"label": "Girlfriend", "car": {"min": 22, "km": 18.0}},
            },
        },
        buildable_status="extra_house",
        vision_condition="good",
        vision_garden_orientation="southwest",
        vision_multigen_layout="likely",
        zip="4000",
        preferred=True,
        new=True,
        family_units="two_family",
        _source_first_seen_at="2026-07-25T08:00:00+00:00",
    )

    PostgresExporter(database_url).export([case], run_id="card-facts")

    with psycopg.connect(database_url) as conn:
        actual = conn.execute(
            """SELECT "PreviewImageUrl","LivingArea","LotArea","Rooms","YearBuilt",
                      "Bathrooms","Bedrooms","Floors","EnergyLabel","Quiet",
                      "BuildableHeadroom","GroundFloorBedroom","SeparateEntrance",
                      "SecondKitchen","PrivacyScore","Latitude","Longitude",
                      "MonthlyExpense","DaysOnMarket","CommuteMinutes","CommuteJson","BuildableStatus",
                      "Condition","GardenOrientation","MultigenFit","PostalCode","Preferred","IsNew","FirstSeenAt","FamilyUnits",
                      "RoadNoiseDb","RailNoiseDb","AirNoiseDb"
               FROM listings WHERE "ExternalId"='card' """
        ).fetchone()
    assert actual[:20] == (
        "https://images.example.test/house.webp",
        249,
        1563,
        8,
        1948,
        2,
        3,
        1,
        "A2020",
        True,
        220,
        True,
        True,
        True,
        5,
        55.7,
        12.4,
        5244,
        18,
        22,
    )
    commute = __import__("json").loads(actual[20])
    assert commute["destinations"]["work"]["public"] == {"min": 52, "transfers": 1}
    assert actual[21:30] == (
        "extra_house",
        "good",
        "southwest",
        "likely",
        "4000",
        True,
        True,
        datetime(2026, 7, 25, 8, tzinfo=timezone.utc),
        "two_family",
    )
    assert actual[30:] == (62.5, 57.5, 67.5)


def test_tombstone_operation_archives_listing_and_records_identity(database_url):
    exporter = PostgresExporter(database_url, ensure_schema_on_export=False)
    exporter.export([_case("gone-via-operation")], run_id="before-tombstone")

    postgres_module.tombstone_listing(
        database_url,
        external_id="  gone-via-operation  ",
        source_url="https://www.boligsiden.dk/cases/gone-via-operation",
        verification_method="http_404",
    )

    with psycopg.connect(database_url) as conn:
        tombstone = conn.execute(
            "select source_url, verification_method from delisted_listings where external_id=%s",
            ("gone-via-operation",),
        ).fetchone()
        state = conn.execute(
            'select "State"::text from listings where "ExternalId"=%s',
            ("gone-via-operation",),
        ).fetchone()
    assert tombstone == (
        "https://www.boligsiden.dk/cases/gone-via-operation",
        "http_404",
    )
    assert state == ("archived",)


def test_concurrent_uncommitted_tombstone_blocks_reimport(database_url):
    exporter = PostgresExporter(database_url, ensure_schema_on_export=False)
    external_id = "gone-concurrently"

    with psycopg.connect(database_url) as tombstone_conn:
        tombstone_conn.execute(
            "select pg_advisory_xact_lock(hashtextextended(%s, 0))",
            (external_id,),
        )
        tombstone_conn.execute(
            "insert into delisted_listings(external_id,verified_at) values (%s,now())",
            (external_id,),
        )
        with ThreadPoolExecutor(max_workers=1) as pool:
            future = pool.submit(
                exporter.export,
                [_case(external_id)],
                run_id="concurrent-tombstone",
            )
            time.sleep(0.15)
            assert not future.done(), "export did not serialize with tombstoning"
            tombstone_conn.commit()
            result = future.result(timeout=5)

    with psycopg.connect(database_url) as conn:
        count = conn.execute(
            'select count(*) from listings where "ExternalId"=%s',
            (external_id,),
        ).fetchone()[0]
    assert result.exported == 0
    assert count == 0


def test_verified_delisted_tombstone_prevents_reimport(database_url):
    with psycopg.connect(database_url) as conn:
        conn.execute(
            "insert into delisted_listings(external_id,source_url,verified_at) values (%s,%s,now())",
            ("gone", "https://www.boligsiden.dk/cases/gone"),
        )
        conn.commit()

    result = PostgresExporter(database_url).export([_case("gone")], run_id="delisted")

    assert result.exported == 0
    with psycopg.connect(database_url) as conn:
        assert conn.execute(
            'select count(*) from listings where "ExternalId"=%s', ("gone",)
        ).fetchone() == (0,)


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
            'select "FamilyPrivacyScore","KidsSpaceScore","GardenScore",'
            '"SharedLivingScore","PracticalScore","FamilyPrivacyWeight",'
            '"KidsSpaceWeight","GardenWeight","SharedLivingWeight","PracticalWeight" '
            "from listings where \"ExternalId\"='one'"
        ).fetchone()
    assert scores == (90, 80, 70, 80, 80, 30, 20, 20, 15, 15)
    assert (
        sum(score * weight / 100 for score, weight in zip(scores[:5], scores[5:])) == 81
    )

    inconsistent = _case(
        "bad",
        family_score=81,
        vision_privacy_score=5,
        family_score_breakdown={
            "privacy": 0,
            "kids_space": 0,
            "garden": 0,
            "shared_living": 0,
            "practical": 0,
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
            "privacy": 0,
            "kids_space": 33,
            "garden": 46,
            "shared_living": 6,
            "practical": 47,
            "weights": {
                "privacy": 0,
                "kids_space": 20 / 0.7,
                "garden": 20 / 0.7,
                "shared_living": 15 / 0.7,
                "practical": 15 / 0.7,
            },
        },
    )
    exporter.export([renormalized], run_id="renormalized-score-breakdown")
    with psycopg.connect(database_url) as conn:
        weights = conn.execute(
            'select "FamilyPrivacyWeight","KidsSpaceWeight" '
            "from listings where \"ExternalId\"='renormalized'"
        ).fetchone()
    assert weights == pytest.approx((0, 20 / 0.7))


def test_filter_rejected_cases_are_never_stored(database_url):
    exporter = PostgresExporter(database_url)
    filtered = ExportCase.from_records(
        {"caseID": "hard-filtered", "address": "Filtered 1"}, None
    )

    result = exporter.export([filtered], run_id="omit-hard-filter")

    with psycopg.connect(database_url) as conn:
        count = conn.execute(
            "select count(*) from listings where \"ExternalId\"='hard-filtered'"
        ).fetchone()[0]
    assert count == 0
    assert result.exported == 0


def _insert_legacy_hard_reject(conn, external_id="legacy-hard"):
    listing_id = conn.execute(
        '''insert into listings
        ("Id","ExternalId","Address","FamilyFitScore","State","AiAssessed","ImportedAt")
        values (gen_random_uuid(),%s,'Legacy hard reject',0,'filter_rejected',false,now())
        returning "Id"'''.strip(),
        (external_id,),
    ).fetchone()[0]
    conn.execute(
        """insert into listing_export_state
        (listing_id,source_scope,first_seen_at,last_seen_at,last_seen_run_id,non_ai_passed,pipeline_decision,raw_payload)
        values (%s,'default',now(),now(),'legacy',false,'filter_rejected','{}')""",
        (listing_id,),
    )
    return listing_id


def test_export_deletes_machine_only_hard_reject_without_history(database_url):
    with psycopg.connect(database_url) as conn:
        conn.execute("""insert into listings
            ("Id","ExternalId","Address","FamilyFitScore","State","AiAssessed","ImportedAt")
            values (gen_random_uuid(),'machine-only','Machine only',0,'filter_rejected',false,now())""")

    PostgresExporter(database_url).export([], run_id="purge-machine-only")

    with psycopg.connect(database_url) as conn:
        assert (
            conn.execute(
                'select count(*) from listings where "ExternalId"=%s', ("machine-only",)
            ).fetchone()[0]
            == 0
        )


def test_export_preserves_hard_reject_with_user_history(database_url):
    with psycopg.connect(database_url) as conn:
        listing_id = _insert_legacy_hard_reject(conn)
        conn.execute('create table votes ("ListingId" uuid not null)')
        conn.execute('insert into votes ("ListingId") values (%s)', (listing_id,))

    PostgresExporter(database_url).export([], run_id="preserve-hard-reject-history")

    with psycopg.connect(database_url) as conn:
        assert conn.execute(
            'select "State"::text from listings where "ExternalId"=%s', ("legacy-hard",)
        ).fetchone() == ("filter_rejected",)


def test_active_owner_approved_learning_rule_applies_to_future_unvoted_imports(
    database_url,
):
    with psycopg.connect(database_url) as conn:
        conn.execute(
            'CREATE TABLE ai_rule_proposals ("Id" uuid, "Version" integer, "RuleJson" text, "IsActive" boolean)'
        )
        conn.execute(
            'INSERT INTO ai_rule_proposals ("Id","Version","RuleJson","IsActive") VALUES (\'00000000-0000-0000-0000-000000000003\',3,%s,true)',
            (
                '{"combinator":"all","conditions":[{"field":"condition","operator":"eq","value":"poor"}]}',
            ),
        )
        conn.execute("""CREATE TABLE ai_rule_applications (
            "ProposalId" uuid, "ListingId" uuid, "ListingExternalId" text,
            "PreviousState" listing_state, "PreviousLearningRuleVersion" text,
            "AppliedState" listing_state, "AppliedAt" timestamptz,
            UNIQUE ("ProposalId","ListingId"))""")
    exporter = PostgresExporter(database_url)
    exporter.export(
        [
            _case(
                "learned",
                vision_status="ok",
                vision_confidence="high",
                vision_condition="poor",
            ),
            _case(
                "safe",
                vision_status="ok",
                vision_confidence="high",
                vision_condition="good",
            ),
            _case(
                "reconsidered",
                vision_status="ok",
                vision_confidence="high",
                vision_multigen_layout="unlikely",
                vision_condition="good",
            ),
        ],
        run_id="learning-rule",
    )
    with psycopg.connect(database_url) as conn:
        rows = conn.execute(
            'SELECT "ExternalId","State"::text,"LearningRuleVersion" FROM listings ORDER BY "ExternalId"'
        ).fetchall()
        applications = conn.execute(
            'SELECT "PreviousState"::text,"AppliedState"::text FROM ai_rule_applications ORDER BY "ListingId"'
        ).fetchall()
    assert rows == [
        ("learned", "ai_rejected", "feedback-v3"),
        ("reconsidered", "active", "feedback-v3"),
        ("safe", "active", "feedback-v3"),
    ]
    assert sorted(applications) == [
        ("active", "active"),
        ("active", "ai_rejected"),
        ("ai_rejected", "active"),
    ]


def test_previously_active_listing_that_now_hard_fails_is_hidden_with_audit(
    database_url,
):
    exporter = PostgresExporter(database_url)
    exporter.export([_case("transition")], run_id="before-hard-fail")
    tombstone = ExportCase.from_records(
        {"caseID": "transition", "address": "A 1"}, None
    )

    result = exporter.export([tombstone], run_id="after-hard-fail")

    with psycopg.connect(database_url) as conn:
        saved = conn.execute(
            'select "State"::text from listings where "ExternalId"=%s', ("transition",)
        ).fetchone()
        imports = conn.execute(
            'select count(*) from listing_imports where listing_id=(select "Id" from listings where "ExternalId"=%s)',
            ("transition",),
        ).fetchone()[0]
    assert saved == ("filter_rejected",)
    assert imports == 1
    assert result.exported == 0


def test_previously_active_hard_fail_preserves_vote_history(database_url):
    exporter = PostgresExporter(database_url)
    exporter.export(
        [_case("protected-transition")], run_id="before-protected-hard-fail"
    )
    with psycopg.connect(database_url) as conn:
        listing_id = conn.execute(
            'select "Id" from listings where "ExternalId"=%s', ("protected-transition",)
        ).fetchone()[0]
        conn.execute('create table votes ("ListingId" uuid not null)')
        conn.execute('insert into votes ("ListingId") values (%s)', (listing_id,))
    tombstone = ExportCase.from_records({"caseID": "protected-transition"}, None)

    exporter.export([tombstone], run_id="preserved-protected-hard-fail")

    with psycopg.connect(database_url) as conn:
        assert conn.execute(
            'select "State"::text from listings where "Id"=%s', (listing_id,)
        ).fetchone() == ("filter_rejected",)
        assert (
            conn.execute(
                'select count(*) from votes where "ListingId"=%s', (listing_id,)
            ).fetchone()[0]
            == 1
        )


def test_active_learning_rule_never_changes_a_listing_with_vote_history(database_url):
    exporter = PostgresExporter(database_url)
    exporter.export(
        [
            _case(
                "voted-policy",
                vision_status="ok",
                vision_confidence="high",
                vision_condition="good",
            )
        ],
        run_id="voted-before-rule",
    )
    with psycopg.connect(database_url) as conn:
        listing_id = conn.execute(
            'select "Id" from listings where "ExternalId"=%s', ("voted-policy",)
        ).fetchone()[0]
        conn.execute('create table votes ("ListingId" uuid not null)')
        conn.execute('insert into votes ("ListingId") values (%s)', (listing_id,))
        conn.execute(
            'create table ai_rule_proposals ("Id" uuid, "Version" integer, "RuleJson" text, "IsActive" boolean)'
        )
        conn.execute(
            "insert into ai_rule_proposals values ('00000000-0000-0000-0000-000000000004',4,%s,true)",
            (
                '{"combinator":"all","conditions":[{"field":"condition","operator":"eq","value":"poor"}]}',
            ),
        )
    exporter.export(
        [
            _case(
                "voted-policy",
                vision_status="ok",
                vision_confidence="high",
                vision_condition="poor",
            )
        ],
        run_id="voted-after-rule",
    )
    with psycopg.connect(database_url) as conn:
        saved = conn.execute(
            'select "State"::text,"LearningRuleVersion","RuleVersion" from listings where "Id"=%s',
            (listing_id,),
        ).fetchone()
    assert saved == ("active", None, "unknown")


def test_policy_import_waits_for_concurrent_vote_and_rechecks_protection(database_url):
    exporter = PostgresExporter(database_url)
    exporter.export(
        [
            _case(
                "race-policy",
                vision_status="ok",
                vision_confidence="high",
                vision_condition="good",
            )
        ],
        run_id="race-before",
    )
    with psycopg.connect(database_url) as setup:
        listing_id = setup.execute(
            'select "Id" from listings where "ExternalId"=%s', ("race-policy",)
        ).fetchone()[0]
        setup.execute(
            'create table votes ("ListingId" uuid not null references listings("Id"))'
        )
        setup.execute(
            'create table ai_rule_proposals ("Id" uuid, "Version" integer, "RuleJson" text, "IsActive" boolean)'
        )
        setup.execute(
            "insert into ai_rule_proposals values ('00000000-0000-0000-0000-000000000099',99,%s,true)",
            (
                '{"combinator":"all","conditions":[{"field":"condition","operator":"eq","value":"poor"}]}',
            ),
        )

    with (
        psycopg.connect(database_url) as blocker,
        ThreadPoolExecutor(max_workers=1) as pool,
    ):
        blocker.execute(
            'select 1 from listings where "Id"=%s for key share', (listing_id,)
        )
        future = pool.submit(
            exporter.export,
            [
                _case(
                    "race-policy",
                    vision_status="ok",
                    vision_confidence="high",
                    vision_condition="poor",
                )
            ],
            run_id="race-after",
        )
        time.sleep(0.15)
        assert not future.done()
        blocker.execute('insert into votes ("ListingId") values (%s)', (listing_id,))
        blocker.commit()
        future.result(timeout=5)

    with psycopg.connect(database_url) as conn:
        saved = conn.execute(
            'select "State"::text,"LearningRuleVersion" from listings where "Id"=%s',
            (listing_id,),
        ).fetchone()
    assert saved == ("active", None)


def test_hard_reject_purge_preserves_immutable_ai_application_audit(database_url):
    with psycopg.connect(database_url) as conn:
        listing_id = _insert_legacy_hard_reject(conn, "audited-hard")
        conn.execute("""CREATE TABLE ai_rule_applications (
            "ProposalId" uuid, "ListingId" uuid, "ListingExternalId" text,
            "PreviousState" listing_state, "PreviousLearningRuleVersion" text,
            "AppliedState" listing_state, "AppliedAt" timestamptz)""")
        conn.execute(
            """INSERT INTO ai_rule_applications
            ("ProposalId","ListingId","ListingExternalId","PreviousState","AppliedState","AppliedAt")
            VALUES ('00000000-0000-0000-0000-000000000088',%s,'audited-hard','active','ai_rejected',now())""",
            (listing_id,),
        )

    PostgresExporter(database_url).export([], run_id="purge-audited-hard")

    with psycopg.connect(database_url) as conn:
        assert conn.execute(
            'select "State"::text from listings where "Id"=%s', (listing_id,)
        ).fetchone() == ("filter_rejected",)
        assert conn.execute(
            'select "ListingId","ListingExternalId" from ai_rule_applications'
        ).fetchone() == (listing_id, "audited-hard")


def test_authoritative_older_source_first_seen_replaces_migration_fallback(
    database_url,
):
    exporter = PostgresExporter(database_url)
    case = _case("first-seen-correction")
    exporter.export(
        [case],
        run_id="first-seen-initial",
        fetched_at=datetime(2026, 7, 27, 8, tzinfo=timezone.utc),
    )
    case.raw["_source_first_seen_at"] = "2026-01-01T08:00:00+00:00"
    exporter.export(
        [case],
        run_id="first-seen-authoritative",
        fetched_at=datetime(2026, 7, 28, 8, tzinfo=timezone.utc),
    )
    with psycopg.connect(database_url) as conn:
        assert conn.execute(
            'select "FirstSeenAt","IsNew" from listings where "ExternalId"=%s',
            ("first-seen-correction",),
        ).fetchone() == (datetime(2026, 1, 1, 8, tzinfo=timezone.utc), False)


def test_export_rejects_duplicate_source_ids_before_database_writes(database_url):
    exporter = PostgresExporter(database_url)
    first = _case("duplicate")
    second = ExportCase.from_records(
        {"caseID": "duplicate", "address": "Last"},
        {"id": "duplicate", "family_score": 60},
    )
    with pytest.raises(RuntimeError, match="duplicate source IDs"):
        exporter.export(
            [first, second],
            run_id="duplicate-run",
            fetched_at=datetime(2026, 7, 27, tzinfo=timezone.utc),
        )
    with psycopg.connect(database_url) as conn:
        counts = conn.execute(
            "select (select count(*) from export_runs), "
            "(select count(*) from listings), "
            "(select count(*) from listing_imports)"
        ).fetchone()
    assert counts == (0, 0, 0)
