from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest


class Response:
    def __init__(self, payload: object, *, status: int = 200) -> None:
        self.status = status
        self.body = json.dumps(payload).encode()


class Transport:
    def __init__(self, responses: list[object]) -> None:
        self.responses = iter(responses)
        self.calls: list[tuple[str, float]] = []

    def __call__(self, url: str, timeout: float) -> Response:
        self.calls.append((url, timeout))
        response = next(self.responses)
        if isinstance(response, BaseException):
            raise response
        assert isinstance(response, Response)
        return response


def page(*, total: int, cases: list[dict[str, Any]]) -> dict[str, object]:
    return {"totalHits": total, "cases": cases}


def config():
    from house_consensus_ingestion.boligsiden import BoligsidenSourceConfig

    return BoligsidenSourceConfig(
        municipalities=("101",),
        address_types=("villa",),
        price_min=1_000_000,
        price_max=2_000_000,
    )


FIXTURE = Path(__file__).with_name("fixtures") / "boligsiden-search-page-1.sanitized.json"


def real_page(*, total: int, case_ids: list[str]) -> dict[str, object]:
    return {"totalHits": total, "cases": [{"caseID": case_id} for case_id in case_ids]}


def test_parser_accepts_sanitized_real_api_page_with_total_hits_and_case_id() -> None:
    from house_consensus_ingestion.boligsiden import _page

    total, cases = _page(json.loads(FIXTURE.read_text()))

    assert total > 50
    assert len(cases) == 50
    assert cases[0]["caseID"] == "fixture-case-001"


def test_fetch_paginates_by_real_total_hits_despite_ignored_requested_page_size() -> None:
    from house_consensus_ingestion.boligsiden import BoligsidenFetcher

    transport = Transport([
        Response(real_page(total=51, case_ids=[f"case-{number:03d}" for number in range(1, 51)])),
        Response(real_page(total=51, case_ids=["case-051"])),
    ])

    snapshot = BoligsidenFetcher(config(), transport=transport, sleep=lambda _: None).fetch()

    assert snapshot.records[0]["caseID"] == "case-001"
    assert snapshot.records[-1]["caseID"] == "case-051"
    assert snapshot.run_snapshot.snapshot_count == 51
    assert len(transport.calls) == 2
    assert "pageSize" not in transport.calls[0][0]


def test_config_exposes_canonical_boligsiden_source_namespace_and_query() -> None:
    configured = config()

    assert configured.source_system == "house-consensus-ingestion"
    assert configured.source_scope == "boligsiden.dk/open-cases"
    assert configured.endpoint == "https://api.boligsiden.dk/search/cases"
    assert configured.query(page=2) == {
        "municipality": "101", "addressType": "villa", "priceMin": "1000000",
        "priceMax": "2000000", "page": "2",
    }


def test_fetch_retries_transient_http_failure_with_configured_timeout() -> None:
    from house_consensus_ingestion.boligsiden import BoligsidenFetcher

    transport = Transport([TimeoutError("temporarily unavailable"), Response(page(total=1, cases=[{"caseID": "case-1", "priceCash": 1_500_000}]))])
    snapshot = BoligsidenFetcher(config(), transport=transport, retry_attempts=2, timeout_seconds=7.5, sleep=lambda _: None).fetch()

    assert snapshot.records == ({"caseID": "case-1", "priceCash": 1_500_000},)
    assert snapshot.run_snapshot.snapshot_count == 1
    assert len(transport.calls) == 2
    assert {timeout for _, timeout in transport.calls} == {7.5}


def test_fetch_returns_deterministic_raw_snapshot_after_complete_pagination() -> None:
    from house_consensus_ingestion.boligsiden import BoligsidenFetcher

    transport = Transport([Response(page(total=3, cases=[{"caseID": "3"}, {"caseID": "1"}])), Response(page(total=3, cases=[{"caseID": "2"}]))])
    snapshot = BoligsidenFetcher(config(), transport=transport, sleep=lambda _: None).fetch()

    assert snapshot.records == ({"caseID": "1"}, {"caseID": "2"}, {"caseID": "3"})
    assert snapshot.run_snapshot.source_scope == "boligsiden.dk/open-cases"
    assert snapshot.run_snapshot.snapshot_count == 3
    assert len(transport.calls) == 2


@pytest.mark.parametrize(
    "responses",
    [
        [Response(page(total=3, cases=[{"caseID": "1"}, {"caseID": "2"}])), Response(page(total=2, cases=[{"caseID": "3"}]))],
        [Response(page(total=3, cases=[{"caseID": "1"}, {"caseID": "2"}])), Response(page(total=3, cases=[{"caseID": "2"}]))],
        [Response(page(total=1, cases=[{"caseID": " "}]))],
    ],
    ids=["total-changes", "duplicate-id", "blank-id"],
)
def test_fetch_retries_one_full_sweep_when_pagination_contract_is_invalid(responses: list[object]) -> None:
    from house_consensus_ingestion.boligsiden import BoligsidenFetchError, BoligsidenFetcher

    repeated_responses = responses + responses
    transport = Transport(repeated_responses)

    with pytest.raises(BoligsidenFetchError, match="full sweep"):
        BoligsidenFetcher(config(), transport=transport, retry_attempts=1, sleep=lambda _: None).fetch()

    assert len(transport.calls) == len(repeated_responses)


def test_fetch_rejects_an_empty_default_search_like_the_legacy_fetch_stage() -> None:
    from house_consensus_ingestion.boligsiden import BoligsidenFetchError, BoligsidenFetcher

    transport = Transport([Response(page(total=0, cases=[])), Response(page(total=0, cases=[]))])

    with pytest.raises(BoligsidenFetchError, match="full sweep"):
        BoligsidenFetcher(config(), transport=transport, sleep=lambda _: None).fetch()

    assert len(transport.calls) == 2


def test_fetch_does_not_retry_permanent_http_errors() -> None:
    from urllib.error import HTTPError

    from house_consensus_ingestion.boligsiden import BoligsidenFetchError, BoligsidenFetcher

    error = lambda: HTTPError("https://api.boligsiden.dk/search/cases", 404, "not found", {}, None)
    transport = Transport([error(), error()])

    with pytest.raises(BoligsidenFetchError, match="full sweep"):
        BoligsidenFetcher(config(), transport=transport, retry_attempts=3, sleep=lambda _: None).fetch()

    assert len(transport.calls) == 2
