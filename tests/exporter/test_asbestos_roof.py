from pathlib import Path

from consensus_exporter.asbestos import RULE_VERSION, assess_asbestos_roof


def test_explicit_structured_asbestos_is_likely():
    result = assess_asbestos_roof({"roofMaterial": "Asbestholdige bølgeplader"})
    assert (result.status, result.primary_source) == ("likely", "structured")


def test_explicit_listing_text_is_likely():
    result = assess_asbestos_roof({"description": "Taget indeholder asbest"})
    assert (result.status, result.primary_source) == ("likely", "text")


def test_ambiguous_bbr_fibercement_category_is_only_possible():
    result = assess_asbestos_roof({"bbrRoofMaterial": "Fibercement, herunder asbest"})
    assert result.status == "possible"


def test_generic_eternit_is_only_possible():
    assert assess_asbestos_roof({"roof": "Eternittag"}).status == "possible"


def test_image_only_evidence_cannot_be_likely():
    result = assess_asbestos_roof({"images": [{"alt": "Asbestholdigt bølgepladetag"}]})
    assert (result.status, result.primary_source) == ("possible", "image")


def test_checked_non_risk_material_is_no_indication_not_asbestos_free():
    result = assess_asbestos_roof({"roofMaterial": "Tegl"})
    assert result.status == "no_indication"
    assert "asbestos-free" not in str(result.evidence).lower()


def test_missing_roof_evidence_is_unknown():
    assert assess_asbestos_roof({"address": "Tagvej 1"}).status == "unknown"


def test_image_locator_metadata_without_evaluated_content_is_unknown():
    result = assess_asbestos_roof({
        "images": [{
            "url": "https://example.test/asbestos-roof.jpg",
            "filename": "asbestos-roof.jpg",
            "contentType": "image/jpeg",
        }]
    })

    assert result.status == "unknown"
    assert result.primary_source is None


def test_image_locator_change_reassesses_even_without_evaluated_content():
    first = assess_asbestos_roof({"images": [{"url": "https://example.test/roof-1.jpg"}]})
    second = assess_asbestos_roof({"images": [{"url": "https://example.test/roof-2.jpg"}]})

    assert first.status == second.status == "unknown"
    assert first.source_fingerprint != second.source_fingerprint


def test_negated_asbestos_statement_is_not_positive_evidence():
    assert assess_asbestos_roof({"roofMaterial": "Fibercement uden asbest"}).status == "no_indication"


def test_conflicting_positive_and_negative_evidence_is_unknown():
    result = assess_asbestos_roof({"roofMaterial": "Asbest", "description": "Dokumenteret asbestfrit tag"})
    assert result.status == "unknown"
    assert any(item["source"] == "conflict" for item in result.evidence)


def test_version_and_source_fingerprint_are_deterministic():
    first = assess_asbestos_roof({"roofMaterial": "Tegl"})
    same = assess_asbestos_roof({"roofMaterial": "Tegl"})
    changed = assess_asbestos_roof({"roofMaterial": "Skifer"})
    assert first.rule_version == RULE_VERSION == "asbestos-roof-v1"
    assert first.source_fingerprint == same.source_fingerprint
    assert first.source_fingerprint != changed.source_fingerprint


def test_schema_keeps_versioned_automated_assessments_and_one_untracked_correction():
    root = Path(__file__).resolve().parents[2]
    schema = (root / "exporter/src/consensus_exporter/schema.sql").read_text()
    assert "CREATE TABLE IF NOT EXISTS asbestos_roof_assessments" in schema
    assert "UNIQUE (listing_id, rule_version, source_fingerprint)" in schema
    assert 'ADD COLUMN IF NOT EXISTS "AsbestosRoofCorrection"' in schema
    assert "'epoch'::timestamptz" in schema
    assert "now()" not in schema.lower()
    assert "CorrectedBy" not in schema
