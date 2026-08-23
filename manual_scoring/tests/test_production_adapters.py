def test_manual_source_resolver_uses_only_exact_case_identity():
    from house_consensus_manual_scoring.adapters import BoligsidenSourceResolver
    from house_consensus_manual_scoring.worker import SourceIdentity

    calls = []

    class Client:
        def fetch_case(self, identity):
            calls.append(identity)
            return {"caseID": identity, "address": {"roadName": "Exact"}}

    resolver = BoligsidenSourceResolver(Client())
    identity = SourceIdentity("case-9", "https://www.boligsiden.dk/adresse/example")
    assert resolver.resolve(identity)["caseID"] == "case-9"
    assert calls == ["case-9"]



def test_manual_source_resolver_terminalizes_synthetic_manual_identity_without_network_lookup():
    import pytest

    from house_consensus_manual_scoring.adapters import BoligsidenSourceResolver
    from house_consensus_manual_scoring.worker import AmbiguousSourceIdentity, SourceIdentity

    class Client:
        def fetch_case(self, identity):
            raise AssertionError(f"synthetic identity reached source API: {identity}")

    resolver = BoligsidenSourceResolver(Client())
    with pytest.raises(AmbiguousSourceIdentity, match="real case ID"):
        resolver.resolve(SourceIdentity("manual:abc", "https://www.boligsiden.dk/adresse/example"))



def test_default_source_resolver_factory_resolves_a_normal_case_once(monkeypatch):
    from house_consensus_manual_scoring import adapters
    from house_consensus_manual_scoring.worker import SourceIdentity

    calls: list[str] = []

    class Client:
        def __init__(self, *, endpoint, timeout_seconds):
            assert endpoint == "https://api.boligsiden.dk/cases"
            assert timeout_seconds == 20

        def fetch_case(self, case_id):
            calls.append(case_id)
            return {"caseID": case_id, "address": {"roadName": "Exact"}}

    monkeypatch.setattr(adapters, "BoligsidenCaseClient", Client)
    listing = adapters.build_source_resolver().resolve(
        SourceIdentity("case-42", "https://www.boligsiden.dk/adresse/example")
    )

    assert calls == ["case-42"]
    assert listing["external_id"] == "case-42"
