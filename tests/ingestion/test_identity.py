from __future__ import annotations

from uuid import UUID


def test_build_snapshot_is_stable_across_record_and_key_order() -> None:
    from house_consensus_ingestion.identity import build_snapshot

    first = build_snapshot(
        source_system="house-consensus-ingestion",
        source_scope="boliga.dk",
        records=[
            {"external_id": "2", "address": "Two Street 2", "price": 2_000_000},
            {"external_id": "1", "address": "One Street 1", "price": 1_000_000},
        ],
    )
    second = build_snapshot(
        source_system="house-consensus-ingestion",
        source_scope="boliga.dk",
        records=[
            {"price": 1_000_000, "address": "One Street 1", "external_id": "1"},
            {"price": 2_000_000, "external_id": "2", "address": "Two Street 2"},
        ],
    )

    assert first == second
    assert first.snapshot_count == 2
    assert first.manifest_sha256 == "90f1e5c0c7cce21d9af45d84db7bc0067418dcb6a3ad1c10b4c8b4cb0de13b1c"
    assert first.run_id == second.run_id
    assert UUID(first.run_id).version == 5


def test_build_snapshot_binds_run_identity_to_source_system_and_scope() -> None:
    from house_consensus_ingestion.identity import build_snapshot

    records = [{"external_id": "1", "address": "One Street 1"}]
    boliga_public = build_snapshot(
        source_system="house-consensus-ingestion",
        source_scope="boliga.dk/public",
        records=records,
    )
    boliga_private = build_snapshot(
        source_system="house-consensus-ingestion",
        source_scope="boliga.dk/private",
        records=records,
    )
    alternate_system = build_snapshot(
        source_system="alternate-ingestion-worker",
        source_scope="boliga.dk/public",
        records=records,
    )
    retry = build_snapshot(
        source_system="house-consensus-ingestion",
        source_scope="boliga.dk/public",
        records=list(reversed(records)),
    )

    assert boliga_public == retry
    assert boliga_public.manifest_sha256 != boliga_private.manifest_sha256
    assert boliga_public.manifest_sha256 != alternate_system.manifest_sha256
    assert boliga_public.run_id != boliga_private.run_id
    assert boliga_public.run_id != alternate_system.run_id
    assert UUID(boliga_public.run_id).version == 5
