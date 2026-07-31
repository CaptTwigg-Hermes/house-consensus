import math

import pytest

from consensus_exporter.postgres import _privacy_rating, _score_breakdown


VALID = {
    "family_score_breakdown": {
        "privacy": 90,
        "kids_space": 80,
        "garden": 70,
        "shared_living": 80,
        "practical": 80,
        "weights": {
            "privacy": 30,
            "kids_space": 20,
            "garden": 20,
            "shared_living": 15,
            "practical": 15,
        },
        "score_version": "family-score-v2",
        "privacy_available": True,
        "score_coverage_pct": 100,
        "notes": {
            "privacy": ["separate entrance"],
            "kids_space": [],
            "garden": [],
            "shared_living": [],
            "practical": [],
        },
    }
}


@pytest.mark.parametrize("bad_value", [math.nan, math.inf, -math.inf, "nan", "inf"])
def test_score_breakdown_rejects_non_finite_values(bad_value):
    for field in ("privacy", "weights"):
        data = {"family_score_breakdown": {
            **VALID["family_score_breakdown"],
            "weights": dict(VALID["family_score_breakdown"]["weights"]),
        }}
        if field == "weights":
            data["family_score_breakdown"]["weights"]["privacy"] = bad_value
        else:
            data["family_score_breakdown"][field] = bad_value
        assert _score_breakdown(data, 81) == (None,) * 14

    assert _score_breakdown(VALID, bad_value) == (None,) * 14


@pytest.mark.parametrize("score", [-1, 101])
def test_score_breakdown_rejects_out_of_range_scores(score):
    data = {"family_score_breakdown": {**VALID["family_score_breakdown"], "privacy": score}}
    assert _score_breakdown(data, 81) == (None,) * 14


def test_score_breakdown_rejects_negative_or_non_normalized_weights():
    for privacy_weight in (-1, 29):
        weights = {**VALID["family_score_breakdown"]["weights"], "privacy": privacy_weight}
        data = {"family_score_breakdown": {**VALID["family_score_breakdown"], "weights": weights}}
        assert _score_breakdown(data, 81) == (None,) * 14


def test_score_breakdown_rejects_malformed_persisted_weights():
    for weights in ({"privacy": 30}, "30/20/20/15/15", {**VALID["family_score_breakdown"]["weights"], "privacy": "bad"}):
        data = {"vision_privacy_score": 5, "family_score_breakdown": {
            **VALID["family_score_breakdown"], "weights": weights,
        }}
        assert _score_breakdown(data, 81) == (None,) * 14


def test_score_breakdown_requires_authoritative_producer_weights():
    for weights in (None, "absent"):
        breakdown = dict(VALID["family_score_breakdown"])
        if weights == "absent":
            breakdown.pop("weights")
        else:
            breakdown["weights"] = weights
        assert _score_breakdown({"family_score_breakdown": breakdown}, 81) == (None,) * 14


def test_score_breakdown_rejects_boolean_numeric_values():
    score_data = {"family_score_breakdown": {
        **VALID["family_score_breakdown"], "privacy": True,
    }}
    assert _score_breakdown(score_data, 54.3) == (None,) * 14

    weights = {**VALID["family_score_breakdown"]["weights"], "privacy": True, "kids_space": 49}
    weight_data = {"family_score_breakdown": {
        **VALID["family_score_breakdown"], "weights": weights,
    }}
    assert _score_breakdown(weight_data, 63.9) == (None,) * 14
    assert _score_breakdown(VALID, True) == (None,) * 14


def test_score_breakdown_rejects_numeric_strings():
    score_data = {"family_score_breakdown": {
        **VALID["family_score_breakdown"], "privacy": "90",
    }}
    assert _score_breakdown(score_data, 81) == (None,) * 14

    weights = {**VALID["family_score_breakdown"]["weights"], "privacy": "30"}
    weight_data = {"family_score_breakdown": {
        **VALID["family_score_breakdown"], "weights": weights,
    }}
    assert _score_breakdown(weight_data, 81) == (None,) * 14
    assert _score_breakdown(VALID, "81") == (None,) * 14


def test_score_breakdown_rejects_huge_integers_without_raising():
    huge = 10**1000
    score_data = {"family_score_breakdown": {
        **VALID["family_score_breakdown"], "privacy": huge,
    }}
    assert _score_breakdown(score_data, 81) == (None,) * 14

    weights = {**VALID["family_score_breakdown"]["weights"], "privacy": huge}
    weight_data = {"family_score_breakdown": {
        **VALID["family_score_breakdown"], "weights": weights,
    }}
    assert _score_breakdown(weight_data, 81) == (None,) * 14
    assert _score_breakdown(VALID, huge) == (None,) * 14


@pytest.mark.parametrize("value", [None, -1, 0, 6, 999, True, "5"])
def test_privacy_rating_rejects_values_outside_integer_1_to_5(value):
    assert _privacy_rating({"vision_privacy_score": value}) is None


@pytest.mark.parametrize("value", [1, 2, 3, 4, 5])
def test_privacy_rating_accepts_integer_1_to_5(value):
    assert _privacy_rating({"vision_privacy_score": value}) == value


def test_score_breakdown_preserves_authoritative_metadata():
    result = _score_breakdown(VALID, 81)

    assert result[:10] == (90.0, 80.0, 70.0, 80.0, 80.0, 30.0, 20.0, 20.0, 15.0, 15.0)
    assert result[10:13] == ("family-score-v2", 100.0, True)
    assert result[13] == '{"garden":[],"kids_space":[],"practical":[],"privacy":["separate entrance"],"shared_living":[]}'


def test_score_breakdown_accepts_unavailable_privacy_as_missing_evidence():
    breakdown = {
        **VALID["family_score_breakdown"],
        "privacy": None,
        "privacy_available": False,
        "score_coverage_pct": 70,
    }

    result = _score_breakdown({"family_score_breakdown": breakdown}, 54)

    assert result[0] is None
    assert result[1:10] == (80.0, 70.0, 80.0, 80.0, 30.0, 20.0, 20.0, 15.0, 15.0)
    assert result[10:13] == ("family-score-v2", 70.0, False)


@pytest.mark.parametrize(
    ("field", "value"),
    [
        ("score_version", None),
        ("score_version", ""),
        ("privacy_available", None),
        ("privacy_available", "false"),
        ("score_coverage_pct", 101),
        ("score_coverage_pct", "100"),
        ("notes", []),
        ("notes", {"privacy": "not-a-list"}),
    ],
)
def test_score_breakdown_rejects_invalid_metadata(field, value):
    breakdown = {**VALID["family_score_breakdown"], field: value}
    assert _score_breakdown({"family_score_breakdown": breakdown}, 81) == (None,) * 14


def test_score_breakdown_requires_coverage_to_match_available_dimensions():
    breakdown = {**VALID["family_score_breakdown"], "score_coverage_pct": 70}
    assert _score_breakdown({"family_score_breakdown": breakdown}, 81) == (None,) * 14
