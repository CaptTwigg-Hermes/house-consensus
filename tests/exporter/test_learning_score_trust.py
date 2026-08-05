from consensus_exporter.models import ExportCase
from consensus_exporter.postgres import _learning_field


def _case(score, *, complete):
    enriched = {"id": "case", "family_score": score}
    if complete:
        enriched["family_score_breakdown"] = {
            "privacy": score,
            "kids_space": score,
            "garden": score,
            "shared_living": score,
            "practical": score,
            "weights": {
                "privacy": 30,
                "kids_space": 20,
                "garden": 20,
                "shared_living": 15,
                "practical": 15,
            },
            "score_version": "family-v1",
            "privacy_available": True,
            "score_coverage_pct": 100,
            "notes": {
                "privacy": [],
                "kids_space": [],
                "garden": [],
                "shared_living": [],
                "practical": [],
            },
        }
    return ExportCase.from_records({"caseID": "case"}, enriched)


def test_learning_rules_ignore_untrusted_numeric_family_scores():
    assert _learning_field(_case(99, complete=False), "family_score") is None
    assert _learning_field(_case(99, complete=True), "family_score") == 99


def test_learning_rules_preserve_trusted_zero_family_score():
    assert _learning_field(_case(0, complete=True), "family_score") == 0
