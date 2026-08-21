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
    from house_consensus_ingestion import cli
    from house_consensus_ingestion.boligsiden import RawFetchSnapshot
    from house_consensus_ingestion.identity import build_snapshot

    records = ({"caseID": "42", "address": {"roadName": "Example Road", "houseNumber": "42", "cityName": "Copenhagen"}, "priceCash": 2_500_000},)
    fetched = RawFetchSnapshot(
        records=records,
        run_snapshot=build_snapshot(source_scope="boligsiden.dk/open-cases", records=records),
        source_config_sha256="a" * 64,
    )

    class Fetcher:
        def __init__(self, config):
            assert config.municipalities == ("101",)
            assert config.price_min == 0
            assert config.price_max == 5_000_000
        def fetch(self):
            return fetched

    monkeypatch.setattr(cli, "BoligsidenFetcher", Fetcher)
    assert cli.main(["--boligsiden", "--dry-run", "--municipality", "101", "--address-type", "villa", "--price-min", "1000000", "--price-max", "3000000"]) == 0
    output = json.loads(capsys.readouterr().out)
    assert output["dry_run"] is True
    assert output["snapshot_count"] == 1
    assert output["projected_count"] == 0


def test_boligsiden_execute_builds_explicit_production_pipeline(monkeypatch, capsys) -> None:
    from house_consensus_ingestion import cli

    observed = {}
    production_pipeline = object()

    class Config:
        database_url = "postgresql://example.test/app"
        database_statement_timeout_seconds = 120
        database_lock_timeout_seconds = 10
        price_min = 1_000_000
        price_max = 3_000_000

        @classmethod
        def from_env(cls):
            observed["config"] = True
            return cls()

    class Result:
        dry_run = False
        snapshot_count = 1
        manifest_sha256 = "digest"
        run_id = "run"
        projected_count = 1
        matched_count = 1
        run_status = "succeeded"

    class Orchestrator:
        def __init__(self, *, fetcher, pipeline, run_writer, projector):
            observed["pipeline"] = pipeline
            observed["projector"] = type(projector).__name__

        def run(self, **kwargs):
            return Result()

    monkeypatch.setenv("DATABASE_URL", "postgresql://example.test/app")
    monkeypatch.setattr(cli, "ProductionAdapterConfig", Config)
    def build_pipeline(config, classification):
        observed["classification_prices"] = (
            classification.price_min,
            classification.price_max,
        )
        return production_pipeline

    monkeypatch.setattr(cli, "build_production_pipeline", build_pipeline)
    monkeypatch.setattr(cli, "PostgresRunWriter", lambda factory: object())
    monkeypatch.setattr(cli, "NativeIngestionOrchestrator", Orchestrator)

    assert cli.main([
        "--boligsiden", "--execute", "--municipality", "101", "--address-type", "villa",
        "--price-min", "1000000", "--price-max", "3000000",
    ]) == 0
    assert observed == {
        "config": True,
        "classification_prices": (1_000_000, 3_000_000),
        "pipeline": production_pipeline,
        "projector": "ExporterBackedPostgresListingProjector",
    }
