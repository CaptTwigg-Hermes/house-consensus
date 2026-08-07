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
