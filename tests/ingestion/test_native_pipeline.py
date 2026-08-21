import pytest


def case(case_id="case-1", **updates):
    value = {
        "caseID": case_id,
        "addressType": "villa",
        "priceCash": 8_000_000,
        "lotArea": 900,
        "housingArea": 220,
        "numberOfRooms": 7,
        "numberOfFloors": 1,
        "descriptionBody": "Tofamiliehus med to køkkener og to indgange",
        "address": {
            "roadName": "Examplevej",
            "houseNumber": "1",
            "zipCode": 2500,
            "cityName": "Valby",
            "addressID": "address-1",
            "buildings": [{"buildingName": "Fritliggende tofamiliehus", "numberOfBathrooms": 2}],
        },
        "coordinates": {"lat": 55.6, "lon": 12.5},
    }
    value.update(updates)
    return value


def test_classification_projects_family_home_and_buildability():
    from house_consensus_ingestion.classification import (
        ClassificationConfig,
        classify_case,
    )

    result = classify_case(case(), ClassificationConfig())
    assert result["external_id"] == "case-1"
    assert result["non_ai_passed"] is True
    assert result["two_family_confidence"] == "confirmed"
    assert result["family_units"] == "two_family"
    assert result["buildable_status"] in {"expand", "extra_house"}


def test_filter_and_cluster_rules_are_deterministic():
    from house_consensus_ingestion.classification import (
        ClassificationConfig,
        classify_cases,
    )

    records = classify_cases([case(str(i), lotArea=300) for i in range(2)], ClassificationConfig())
    assert {record["filter_reason"] for record in records} == {"lot_area_below_500"}

    clustered = classify_cases([case(str(i)) for i in range(3)], ClassificationConfig(max_per_road=2))
    assert all(record["filter_reason"] == "developer_cluster" for record in clustered)


def test_pipeline_runs_enrichment_before_scoring_and_fails_closed():
    from house_consensus_ingestion.classification import ClassificationConfig
    from house_consensus_ingestion.pipeline import NativeCasePipeline

    events = []

    class Enricher:
        name = "vision"
        required = True

        def enrich(self, records):
            events.append("vision")
            for record in records:
                record["vision_run_status"] = "ok"
                record["vision_confidence"] = "high"
                record["vision_separate_entrance"] = True
            return {"enriched": len(records)}

    result = NativeCasePipeline(classification=ClassificationConfig(), enrichers=[Enricher()]).process([case()])
    assert events == ["vision"]
    assert result.records[0]["family_score"] > 0

    class Broken(Enricher):
        def enrich(self, records):
            raise RuntimeError("offline")

    with pytest.raises(RuntimeError, match="offline"):
        NativeCasePipeline(classification=ClassificationConfig(), enrichers=[Broken()]).process([case()])


def test_pipeline_fails_closed_when_required_enricher_reports_incomplete_records():
    from house_consensus_ingestion.classification import ClassificationConfig
    from house_consensus_ingestion.pipeline import (
        NativeCasePipeline,
        RequiredEnrichmentError,
    )

    class Incomplete:
        name = "commute"

        def enrich(self, records):
            return {"enriched": len(records), "incomplete": 1}

    with pytest.raises(RequiredEnrichmentError, match="commute.*incomplete"):
        NativeCasePipeline(
            classification=ClassificationConfig(), enrichers=[Incomplete()]
        ).process([case()])
