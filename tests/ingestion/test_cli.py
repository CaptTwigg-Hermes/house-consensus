from __future__ import annotations

import json


def test_dry_run_prints_deterministic_snapshot_identity(capsys) -> None:
    from house_consensus_ingestion.cli import main

    exit_code = main([
        "--dry-run",
        "--source-scope",
        "boliga.dk",
        "--records-json",
        '[{"external_id": "1", "address": "One Street 1", "price": 1000000}]',
    ])

    assert exit_code == 0
    assert json.loads(capsys.readouterr().out) == {
        "dry_run": True,
        "manifest_sha256": "08ad28ea97226e274874ee2c0a8332209f0a88e40d63d0e5e91de9a3ea48f345",
        "run_id": "ingest-boliga.dk-08ad28ea97226e27",
        "snapshot_count": 1,
        "source_scope": "boliga.dk",
    }
