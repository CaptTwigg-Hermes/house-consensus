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
