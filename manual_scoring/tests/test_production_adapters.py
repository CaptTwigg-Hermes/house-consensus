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
