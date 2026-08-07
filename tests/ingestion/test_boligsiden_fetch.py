from __future__ import annotations

import json
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
    return {"totalCount": total, "cases": cases}


def config():
    from house_consensus_ingestion.boligsiden import BoligsidenSourceConfig

    return BoligsidenSourceConfig(
        municipalities=("101",),
        address_types=("villa",),
        price_min=1_000_000,
        price_max=2_000_000,
        page_size=2,
    )


def test_config_exposes_canonical_boligsiden_source_namespace_and_query() -> None:
    configured = config()

    assert configured.source_system == "house-consensus-ingestion"
    assert configured.source_scope == "boligsiden.dk/open-cases"
    assert configured.endpoint == "https://api.boligsiden.dk/search/cases"
    assert configured.query(page=2) == {
        "municipality": "101", "addressType": "villa", "priceMin": "1000000",
        "priceMax": "2000000", "page": "2", "pageSize": "2",
    }


def test_fetch_retries_transient_http_failure_with_configured_timeout() -> None:
    from house_consensus_ingestion.boligsiden import BoligsidenFetcher

    transport = Transport([TimeoutError("temporarily unavailable"), Response(page(total=1, cases=[{"id": "case-1", "priceCash": 1_500_000}]))])
    snapshot = BoligsidenFetcher(config(), transport=transport, retry_attempts=2, timeout_seconds=7.5, sleep=lambda _: None).fetch()

    assert snapshot.records == ({"id": "case-1", "priceCash": 1_500_000},)
    assert snapshot.run_snapshot.snapshot_count == 1
    assert len(transport.calls) == 2
    assert {timeout for _, timeout in transport.calls} == {7.5}


def test_fetch_returns_deterministic_raw_snapshot_after_complete_pagination() -> None:
    from house_consensus_ingestion.boligsiden import BoligsidenFetcher

    transport = Transport([Response(page(total=3, cases=[{"id": "3"}, {"id": "1"}])), Response(page(total=3, cases=[{"id": "2"}]))])
    snapshot = BoligsidenFetcher(config(), transport=transport, sleep=lambda _: None).fetch()

    assert snapshot.records == ({"id": "1"}, {"id": "2"}, {"id": "3"})
    assert snapshot.run_snapshot.source_scope == "boligsiden.dk/open-cases"
    assert snapshot.run_snapshot.snapshot_count == 3
    assert len(transport.calls) == 2


@pytest.mark.parametrize(
    "responses",
    [
        [Response(page(total=3, cases=[{"id": "1"}, {"id": "2"}])), Response(page(total=2, cases=[{"id": "3"}]))],
        [Response(page(total=3, cases=[{"id": "1"}, {"id": "2"}])), Response(page(total=3, cases=[{"id": "2"}]))],
        [Response(page(total=1, cases=[{"id": " "}]))],
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
