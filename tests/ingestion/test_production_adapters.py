import pytest


def test_exact_boligsiden_source_identity_is_enforced():
    from house_consensus_ingestion.adapters import (
        BoligsidenCaseClient,
        SourceIdentityError,
    )

    calls = []
    client = BoligsidenCaseClient(get_json=lambda url, timeout: calls.append((url, timeout)) or {"caseID": "case-1"})
    assert client.fetch_case("case-1")["caseID"] == "case-1"
    assert calls[0][0].endswith("/case-1")
    with pytest.raises(SourceIdentityError, match="does not match"):
        BoligsidenCaseClient(get_json=lambda _url, _timeout: {"caseID": "other"}).fetch_case("case-1")


def test_floorplan_recovery_accepts_only_explicit_labels_and_safe_urls():
    from house_consensus_ingestion.adapters import FloorplanRecoverer

    html = b'<img alt="Facade" src="https://img.example/front.jpg"><img alt="Plantegning" srcset="/small.jpg 400w, /plan.jpg 1200w">'
    recover = FloorplanRecoverer(get_bytes=lambda _url, _timeout: ("text/html", html))
    assert recover({"caseUrl": "https://broker.example/listing"}) == ["https://broker.example/plan.jpg"]


def test_commute_router_accepts_brouter_numeric_strings() -> None:
    from house_consensus_ingestion.adapters import CommuteRouter, RoutingConfig

    def get_json(url: str, _timeout: float):
        if "osrm" in url:
            return {"code": "Ok", "routes": [{"duration": 1200, "distance": 10000}]}
        if "brouter" in url:
            return {
                "features": [
                    {"properties": {"total-time": "3061", "track-length": "17448"}}
                ]
            }
        return {
            "itineraries": [
                {"duration": 1800, "transfers": 1, "legs": [{"mode": "TRAIN"}]}
            ]
        }

    result = CommuteRouter(
        RoutingConfig(
            osrm_endpoint="https://osrm.example/route",
            brouter_endpoint="https://brouter.example/route",
            transit_endpoint="https://transit.example/plan",
        ),
        get_json=get_json,
    ).route((55.6, 12.4), (55.7, 12.5))

    assert result["bike"] == {"min": 51, "km": 17.4}


def test_postgres_factory_bounds_lock_and_statement_waits(monkeypatch) -> None:
    import psycopg
    from house_consensus_ingestion.adapters import postgres_connection_factory

    observed = {}
    marker = object()

    def connect(database_url, **options):
        observed.update(database_url=database_url, **options)
        return marker

    monkeypatch.setattr(psycopg, "connect", connect)

    result = postgres_connection_factory(
        "postgresql://db/house",
        statement_timeout_seconds=120,
        lock_timeout_seconds=10,
    )()

    assert result is marker
    assert observed == {
        "database_url": "postgresql://db/house",
        "connect_timeout": 10,
        "options": "-c statement_timeout=120000 -c lock_timeout=10000",
    }


def test_production_configuration_requires_fail_closed_inputs():
    from house_consensus_ingestion.adapters import ProductionAdapterConfig

    env = {
        "DATABASE_URL": "postgresql://app@db/house",
        "CONSENSUS_NOISE_DATABASE_URL": "postgresql://noise@db/noise",
        "CONSENSUS_OLLAMA_MODEL": "gemma3:27b",
        "CONSENSUS_OLLAMA_HOST": "http://ollama.internal:11434",
        "CONSENSUS_COMMUTE_DESTINATIONS": '{"work":{"label":"Work","latitude":55.7,"longitude":12.5}}',
    }
    assert ProductionAdapterConfig.from_env(env).destinations["work"] == ("Work", 55.7, 12.5)
    with pytest.raises(ValueError, match="DATABASE_URL"):
        ProductionAdapterConfig.from_env({})
