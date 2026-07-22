import math

import pytest

from consensus_exporter.postgres import _score_breakdown


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
        assert _score_breakdown(data, 81) == (None,) * 10

    assert _score_breakdown(VALID, bad_value) == (None,) * 10


@pytest.mark.parametrize("score", [-1, 101])
def test_score_breakdown_rejects_out_of_range_scores(score):
    data = {"family_score_breakdown": {**VALID["family_score_breakdown"], "privacy": score}}
    assert _score_breakdown(data, 81) == (None,) * 10


def test_score_breakdown_rejects_negative_or_non_normalized_weights():
    for privacy_weight in (-1, 29):
        weights = {**VALID["family_score_breakdown"]["weights"], "privacy": privacy_weight}
        data = {"family_score_breakdown": {**VALID["family_score_breakdown"], "weights": weights}}
        assert _score_breakdown(data, 81) == (None,) * 10


def test_score_breakdown_rejects_malformed_persisted_weights():
    for weights in ({"privacy": 30}, "30/20/20/15/15", {**VALID["family_score_breakdown"]["weights"], "privacy": "bad"}):
        data = {"vision_privacy_score": 5, "family_score_breakdown": {
            **VALID["family_score_breakdown"], "weights": weights,
        }}
        assert _score_breakdown(data, 81) == (None,) * 10


def test_score_breakdown_rejects_explicit_null_weights_but_infers_absent_legacy_weights():
    null_weights = {"vision_privacy_score": 5, "family_score_breakdown": {
        **VALID["family_score_breakdown"], "weights": None,
    }}
    assert _score_breakdown(null_weights, 81) == (None,) * 10

    legacy = {"vision_privacy_score": 5, "family_score_breakdown": {
        key: value for key, value in VALID["family_score_breakdown"].items() if key != "weights"
    }}
    assert _score_breakdown(legacy, 81) == (90, 80, 70, 80, 80, 30, 20, 20, 15, 15)


def test_score_breakdown_rejects_boolean_numeric_values():
    score_data = {"family_score_breakdown": {
        **VALID["family_score_breakdown"], "privacy": True,
    }}
    assert _score_breakdown(score_data, 54.3) == (None,) * 10

    weights = {**VALID["family_score_breakdown"]["weights"], "privacy": True, "kids_space": 49}
    weight_data = {"family_score_breakdown": {
        **VALID["family_score_breakdown"], "weights": weights,
    }}
    assert _score_breakdown(weight_data, 63.9) == (None,) * 10
    assert _score_breakdown(VALID, True) == (None,) * 10


def test_score_breakdown_rejects_numeric_strings():
    score_data = {"family_score_breakdown": {
        **VALID["family_score_breakdown"], "privacy": "90",
    }}
    assert _score_breakdown(score_data, 81) == (None,) * 10

    weights = {**VALID["family_score_breakdown"]["weights"], "privacy": "30"}
    weight_data = {"family_score_breakdown": {
        **VALID["family_score_breakdown"], "weights": weights,
    }}
    assert _score_breakdown(weight_data, 81) == (None,) * 10
    assert _score_breakdown(VALID, "81") == (None,) * 10


def test_score_breakdown_rejects_huge_integers_without_raising():
    huge = 10**1000
    score_data = {"family_score_breakdown": {
        **VALID["family_score_breakdown"], "privacy": huge,
    }}
    assert _score_breakdown(score_data, 81) == (None,) * 10

    weights = {**VALID["family_score_breakdown"]["weights"], "privacy": huge}
    weight_data = {"family_score_breakdown": {
        **VALID["family_score_breakdown"], "weights": weights,
    }}
    assert _score_breakdown(weight_data, 81) == (None,) * 10
    assert _score_breakdown(VALID, huge) == (None,) * 10
