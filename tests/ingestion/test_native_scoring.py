def test_scoring_preserves_legacy_evidence_semantics() -> None:
    from house_consensus_ingestion.scoring import family_score

    result = family_score(
        {
            "vision_run_status": "ok",
            "vision_confidence": "high",
            "vision_separate_entrance": False,
            "vision_second_kitchen": False,
            "vision_internal_connection": False,
            "vision_split_type": "none",
            "vision_en_suite_count": None,
            "vision_staircase_count": 1,
            "vision_bathroom_count": 3,
            "vision_dining_capacity": "large",
            "vision_kitchen_size": "island",
            "noise_status": "noisy",
            "numberOfRooms": 6,
            "housingArea": 180,
            "lotArea": 800,
            "numberOfToilets": 0,
        }
    )

    assert result["privacy"] == 8
    assert result["shared_living"] == 0
    assert result["practical"] == 10


def test_scoring_uses_explicit_two_dwelling_evidence_for_connection() -> None:
    from house_consensus_ingestion.scoring import family_score

    result = family_score(
        {
            "vision_run_status": "ok",
            "vision_confidence": "medium",
            "vision_separate_entrance": False,
            "vision_second_kitchen": False,
            "vision_internal_connection": False,
            "vision_split_type": "none",
            "vision_two_dwellings": True,
        }
    )

    assert result["privacy"] == 8