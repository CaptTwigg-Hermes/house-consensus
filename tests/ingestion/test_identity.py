from __future__ import annotations

from uuid import UUID


def test_build_snapshot_is_stable_across_record_and_key_order() -> None:
    from house_consensus_ingestion.identity import build_snapshot

    first = build_snapshot(
        source_scope="boliga.dk",
        records=[
            {"external_id": "2", "address": "Two Street 2", "price": 2_000_000},
            {"external_id": "1", "address": "One Street 1", "price": 1_000_000},
        ],
    )
    second = build_snapshot(
        source_scope="boliga.dk",
        records=[
            {"price": 1_000_000, "address": "One Street 1", "external_id": "1"},
            {"price": 2_000_000, "external_id": "2", "address": "Two Street 2"},
        ],
    )

    assert first == second
    assert first.snapshot_count == 2
    assert first.manifest_sha256 == "807980f5562cfa681e160fdeaacfc7e19bfa12b08d3397b95876703856bf45b3"
    assert first.run_id == second.run_id
    assert UUID(first.run_id).version == 5
