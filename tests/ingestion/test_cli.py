from __future__ import annotations

import json


def test_dry_run_prints_deterministic_snapshot_identity(capsys) -> None:
    from house_consensus_ingestion.cli import main

    exit_code = main([
        "--dry-run",
        "--source-system",
        "house-consensus-ingestion",
        "--source-scope",
        "boliga.dk",
        "--records-json",
        '[{"external_id": "1", "address": "One Street 1", "price": 1000000}]',
    ])

    assert exit_code == 0
    assert json.loads(capsys.readouterr().out) == {
        "dry_run": True,
        "manifest_sha256": "c2ac808b19c468877e52f130a9b7d279bcab845bd694c09dd13cc498808d5150",
        "run_id": "47eaa5b9-e45c-500a-a0a6-910e84037a8e",
        "snapshot_count": 1,
        "source_scope": "boliga.dk",
        "source_system": "house-consensus-ingestion",
    }



def test_boligsiden_dry_run_uses_native_orchestrator_without_database(monkeypatch, capsys) -> None:
    from house_consensus_ingestion.boligsiden import RawFetchSnapshot
    from house_consensus_ingestion.identity import build_snapshot
    import house_consensus_ingestion.cli as cli

    records = ({"caseID": "42", "address": {"roadName": "Example Road", "houseNumber": "42", "cityName": "Copenhagen"}, "priceCash": 2_500_000},)
    fetched = RawFetchSnapshot(records=records, run_snapshot=build_snapshot(source_scope="boligsiden.dk/open-cases", records=records))

    class Fetcher:
        def __init__(self, config):
            assert config.municipalities == ("101",)
        def fetch(self):
            return fetched

    monkeypatch.setattr(cli, "BoligsidenFetcher", Fetcher)
    assert cli.main(["--boligsiden", "--dry-run", "--municipality", "101", "--address-type", "villa", "--price-min", "1000000", "--price-max", "3000000"]) == 0
    output = json.loads(capsys.readouterr().out)
    assert output["dry_run"] is True
    assert output["snapshot_count"] == 1
    assert output["projected_count"] == 0
