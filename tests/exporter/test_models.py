from consensus_exporter.models import ExportCase


def test_every_fetched_case_is_normalized_and_missing_ai_does_not_reject_pass():
    case = ExportCase.from_records(
        {"caseID": "case-1"}, {"id": "case-1", "family_score": 82.5}
    )
    assert case.source_id == "case-1"
    assert case.non_ai_passed is True
    assert case.ai_status == "not_assessed"
    assert case.pipeline_decision == "passed"


def test_high_confidence_ai_rejection_is_reviewable_with_evidence():
    case = ExportCase.from_records(
        {"caseID": "case-2"},
        {
            "id": "case-2",
            "ai_decision": "reject",
            "ai_confidence": "high",
            "ai_model_version": "gemma:4b-v2",
            "ai_rule_version": "multigen-v3",
            "ai_evidence": {"summary": "No independent living areas"},
        },
    )
    assert case.pipeline_decision == "ai_rejected"
    assert case.ai_status == "rejected"
    assert case.ai_evidence["model_version"] == "gemma:4b-v2"


def test_failed_ai_never_rejects_non_ai_pass():
    case = ExportCase.from_records(
        {"caseID": "case-3"},
        {
            "id": "case-3",
            "ai_status": "failed",
            "ai_decision": "reject",
            "ai_confidence": "high",
        },
    )
    assert case.ai_status == "not_assessed"
    assert case.pipeline_decision == "passed"


def test_raw_case_not_in_matches_is_still_exported_as_non_ai_rejected():
    case = ExportCase.from_records({"caseID": "case-4"}, None)
    assert case.pipeline_decision == "filter_rejected"


def test_real_vision_fields_and_string_booleans_are_interpreted_semantically():
    rejected = ExportCase.from_records(
        {"caseID": "real-1"},
        {"id": "real-1", "non_ai_passed": "true", "vision_multigen_layout": "unlikely", "vision_confidence": "HIGH"},
    )
    assert rejected.non_ai_passed is True
    assert rejected.ai_status == "rejected"
    assert rejected.pipeline_decision == "ai_rejected"

    filtered = ExportCase.from_records(
        {"caseID": "real-2"},
        {"id": "real-2", "non_ai_passed": "false", "vision_multigen_layout": "strong", "vision_confidence": "high"},
    )
    assert filtered.non_ai_passed is False
    assert filtered.ai_status == "assessed"
    assert filtered.pipeline_decision == "filter_rejected"


def test_match_coordinates_are_normalized_from_pipeline_private_field():
    case = ExportCase.from_records(
        {"caseID": "coords"},
        {"id": "coords", "_coordinates": {"lat": 55.7, "lon": 12.4}},
    )
    assert (case.latitude, case.longitude) == (55.7, 12.4)


def test_realtor_url_is_preferred_over_boligsiden_aggregator_url():
    case = ExportCase.from_records(
        {"caseID": "original-link"},
        {
            "id": "original-link",
            "link": "https://www.boligsiden.dk/adresse/example",
            "maegler_url": "https://estate.example/bolig/original-link",
        },
    )

    assert case.source_url == "https://estate.example/bolig/original-link"
