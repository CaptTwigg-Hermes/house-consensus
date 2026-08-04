import math

import pytest

import consensus_exporter.postgres as postgres


def test_nested_noise_sources_preserve_lden_lnight_and_explicit_statuses():
    facts = postgres._noise_facts(
        {
            "noise_status": "quiet",
            "noise_sources": {
                "ROAD": {
                    "Lden": {"status": "no_contour", "db_value": None},
                    "Lnight": {"status": "unavailable", "db_value": None},
                },
                "RAIL": {
                    "Lden": {"status": "stale", "db_value": 67.0},
                    "Lnight": {"status": "covered", "db_value": 57.0},
                },
                "AIR": {
                    "Lden": {"status": "covered", "db_value": 62.0},
                    "Lnight": {"status": "no_contour", "db_value": None},
                },
            },
        }
    )

    assert facts.quiet is None
    assert (facts.road_lden_db, facts.road_lden_status) == (None, "no_contour")
    assert (facts.road_lnight_db, facts.road_lnight_status) == (None, "unavailable")
    assert (facts.rail_lden_db, facts.rail_lden_status) == (67.0, "stale")
    assert (facts.rail_lnight_db, facts.rail_lnight_status) == (57.0, "covered")
    assert (facts.air_lden_db, facts.air_lden_status) == (62.0, "covered")
    assert (facts.air_lnight_db, facts.air_lnight_status) == (None, "no_contour")


def test_nested_noise_rejects_numeric_values_without_covered_or_stale_status():
    facts = postgres._noise_facts(
        {
            "noise_sources": {
                "ROAD": {
                    "Lden": {"status": "no_contour", "db_value": 55},
                    "Lnight": {"status": "mystery", "db_value": math.inf},
                }
            }
        }
    )

    assert (facts.road_lden_db, facts.road_lden_status) == (None, "no_contour")
    assert (facts.road_lnight_db, facts.road_lnight_status) == (None, "unavailable")
    assert facts.quiet is None


def test_legacy_flat_noise_fields_remain_compatible():
    facts = postgres._noise_facts(
        {
            "noise_status": "quiet",
            "road_noise_db": 48.5,
            "rail_noise_db": 57.5,
            "air_noise_db": 67.5,
        }
    )

    assert facts.quiet is True
    assert (facts.road_lden_db, facts.road_lden_status) == (48.5, "covered")
    assert (facts.rail_lden_db, facts.rail_lden_status) == (57.5, "covered")
    assert (facts.air_lden_db, facts.air_lden_status) == (67.5, "covered")
    assert facts.road_lnight_status == "unavailable"



def test_nested_noise_preserves_explicit_error_without_a_value():
    facts = postgres._noise_facts(
        {
            "noise_sources": {
                "ROAD": {
                    "Lden": {"status": "error", "db_value": None, "error": "lookup failed"},
                    "Lnight": {"status": "error", "db_value": 61.0, "error": "invalid payload"},
                }
            }
        }
    )

    assert (facts.road_lden_db, facts.road_lden_status) == (None, "error")
    assert (facts.road_lnight_db, facts.road_lnight_status) == (None, "error")
    assert facts.quiet is None



def test_legacy_quiet_requires_valid_covered_road_evidence():
    for raw in (
        {"noise_status": "quiet"},
        {"noise_status": "quiet", "road_noise_db": "malformed"},
    ):
        facts = postgres._noise_facts(raw)
        assert facts.quiet is None
        assert (facts.road_lden_db, facts.road_lden_status) == (None, "unavailable")


def test_noise_boolean_values_are_malformed_not_measurements():
    for raw in (
        {
            "noise_sources": {
                "road": {
                    "Lden": {"status": "covered", "db_value": True},
                    "Lnight": {"status": "unavailable", "db_value": None},
                }
            }
        },
        {"noise_status": "quiet", "road_noise_db": False},
    ):
        facts = postgres._noise_facts(raw)
        assert facts.road_lden_db is None
        assert facts.road_lden_status == "unavailable"
        assert facts.quiet is None


def test_tofamiliehus_missing_source_config_fails_before_database_connect(monkeypatch):
    monkeypatch.setattr(
        postgres.psycopg,
        "connect",
        lambda *_args, **_kwargs: (_ for _ in ()).throw(
            AssertionError("database connection attempted")
        ),
    )
    exporter = postgres.PostgresExporter(
        "postgresql://unused", source_scope="tofamiliehus"
    )
    with pytest.raises(ValueError, match="require source_config_sha256"):
        exporter.export([], run_id="missing-source-config")
