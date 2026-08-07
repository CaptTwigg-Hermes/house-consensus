"""Validated, deterministic raw fetches from Boligsiden's public case search."""
from __future__ import annotations

from collections.abc import Callable, Mapping
from dataclasses import dataclass
import json
import time
from typing import Any, Protocol
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import Request, urlopen

from .identity import RunSnapshot, build_snapshot


class BoligsidenFetchError(RuntimeError):
    """A source response cannot be used as an auditable complete snapshot."""


class Response(Protocol):
    status: int
    body: bytes


Transport = Callable[[str, float], Response]


@dataclass(frozen=True)
class BoligsidenSourceConfig:
    """Canonical public-search source namespace and intentionally fixed filters."""

    municipalities: tuple[str, ...]
    address_types: tuple[str, ...]
    price_min: int
    price_max: int
    allow_empty: bool = False
    source_system: str = "house-consensus-ingestion"
    source_scope: str = "boligsiden.dk/open-cases"
    endpoint: str = "https://api.boligsiden.dk/search/cases"

    def __post_init__(self) -> None:
        if not self.municipalities or not self.address_types:
            raise ValueError("municipalities and address_types must not be empty")
        if any(not value.strip() for value in (*self.municipalities, *self.address_types)):
            raise ValueError("source filters must be non-blank")
        if self.price_min < 0 or self.price_max < self.price_min:
            raise ValueError("price range must be non-negative and ordered")
        if not self.endpoint.startswith("https://api.boligsiden.dk/"):
            raise ValueError("endpoint must be the Boligsiden public API")

    def query(self, *, page: int, municipality: str | None = None, address_type: str | None = None) -> dict[str, str]:
        if page < 1:
            raise ValueError("page must be positive")
        municipality = municipality or _only(self.municipalities, "municipality")
        address_type = address_type or _only(self.address_types, "address type")
        return {
            "municipality": municipality,
            "addressType": address_type,
            "priceMin": str(self.price_min),
            "priceMax": str(self.price_max),
            "page": str(page),
        }


@dataclass(frozen=True)
class RawFetchSnapshot:
    """Immutable raw records and their run/audit identity."""

    records: tuple[Mapping[str, Any], ...]
    run_snapshot: RunSnapshot


class BoligsidenFetcher:
    """Fetch every configured search partition, retrying one invalid sweep once."""

    def __init__(
        self,
        config: BoligsidenSourceConfig,
        *,
        transport: Transport | None = None,
        timeout_seconds: float = 20.0,
        retry_attempts: int = 3,
        sleep: Callable[[float], None] = time.sleep,
    ) -> None:
        if timeout_seconds <= 0:
            raise ValueError("timeout_seconds must be positive")
        if retry_attempts < 1:
            raise ValueError("retry_attempts must be at least one")
        self._config = config
        self._transport = transport or _http_get
        self._timeout_seconds = timeout_seconds
        self._retry_attempts = retry_attempts
        self._sleep = sleep

    def fetch(self) -> RawFetchSnapshot:
        last_error: BoligsidenFetchError | None = None
        for _ in range(2):  # A changing listing set gets one fresh, complete sweep.
            try:
                records = self._fetch_sweep()
                if not records and not self._config.allow_empty:
                    raise BoligsidenFetchError("Boligsiden search returned zero cases")
                return RawFetchSnapshot(
                    records=records,
                    run_snapshot=build_snapshot(
                        source_system=self._config.source_system,
                        source_scope=self._config.source_scope,
                        records=records,
                    ),
                )
            except BoligsidenFetchError as error:
                last_error = error
        raise BoligsidenFetchError(f"Boligsiden full sweep failed after one retry: {last_error}") from last_error

    def _fetch_sweep(self) -> tuple[Mapping[str, Any], ...]:
        records: list[Mapping[str, Any]] = []
        seen_ids: set[str] = set()
        for municipality in self._config.municipalities:
            for address_type in self._config.address_types:
                partition = self._fetch_partition(municipality, address_type)
                for record in partition:
                    case_id = _case_id(record)
                    if case_id in seen_ids:
                        raise BoligsidenFetchError(f"duplicate Boligsiden case id {case_id!r}")
                    seen_ids.add(case_id)
                    records.append(record)
        return tuple(sorted(records, key=lambda record: _case_id(record)))

    def _fetch_partition(self, municipality: str, address_type: str) -> list[Mapping[str, Any]]:
        expected_total: int | None = None
        records: list[Mapping[str, Any]] = []
        page = 1
        while expected_total is None or len(records) < expected_total:
            payload = self._request_json(self._url(page, municipality, address_type))
            total, cases = _page(payload)
            if expected_total is None:
                expected_total = total
            elif total != expected_total:
                raise BoligsidenFetchError("Boligsiden pagination total changed during sweep")
            if len(records) + len(cases) > expected_total:
                raise BoligsidenFetchError("Boligsiden page exceeds declared cardinality")
            records.extend(cases)
            if not cases and len(records) != expected_total:
                raise BoligsidenFetchError("Boligsiden pagination ended before declared cardinality")
            page += 1
        return records

    def _url(self, page: int, municipality: str, address_type: str) -> str:
        return f"{self._config.endpoint}?{urlencode(self._config.query(page=page, municipality=municipality, address_type=address_type))}"

    def _request_json(self, url: str) -> Mapping[str, Any]:
        last_error: Exception | None = None
        for attempt in range(self._retry_attempts):
            try:
                response = self._transport(url, self._timeout_seconds)
                if response.status == 200:
                    payload = json.loads(response.body)
                    if isinstance(payload, Mapping):
                        return payload
                    raise BoligsidenFetchError("Boligsiden response must be a JSON object")
                if response.status not in {408, 429} and response.status < 500:
                    raise BoligsidenFetchError(f"Boligsiden returned HTTP {response.status}")
                last_error = BoligsidenFetchError(f"Boligsiden returned transient HTTP {response.status}")
            except BoligsidenFetchError:
                raise
            except HTTPError as error:
                if error.code not in {408, 429} and error.code < 500:
                    raise BoligsidenFetchError(f"Boligsiden returned HTTP {error.code}") from error
                last_error = error
            except (URLError, OSError, TimeoutError, json.JSONDecodeError) as error:
                last_error = error
            if attempt + 1 < self._retry_attempts:
                self._sleep(0.25 * (2 ** attempt))
        raise BoligsidenFetchError(f"Boligsiden request failed after {self._retry_attempts} attempts: {last_error}") from last_error


def _page(payload: Mapping[str, Any]) -> tuple[int, list[Mapping[str, Any]]]:
    total = payload.get("totalHits")
    cases = payload.get("cases")
    if isinstance(total, bool) or not isinstance(total, int) or total < 0:
        raise BoligsidenFetchError("Boligsiden response has no valid totalHits")
    if not isinstance(cases, list) or not all(isinstance(case, Mapping) for case in cases):
        raise BoligsidenFetchError("Boligsiden response has no valid cases array")
    copied = [dict(case) for case in cases]
    for case in copied:
        _case_id(case)
    return total, copied


def _case_id(record: Mapping[str, Any]) -> str:
    value = record.get("caseID")
    if isinstance(value, bool) or not isinstance(value, (str, int)):
        raise BoligsidenFetchError("Boligsiden case has no valid caseID")
    identifier = str(value).strip()
    if not identifier or len(identifier) > 128:
        raise BoligsidenFetchError("Boligsiden case has no valid caseID")
    return identifier


def _only(values: tuple[str, ...], name: str) -> str:
    if len(values) != 1:
        raise ValueError(f"{name} must be explicit for a multi-filter query")
    return values[0]


@dataclass(frozen=True)
class _HttpResponse:
    status: int
    body: bytes


def _http_get(url: str, timeout: float) -> _HttpResponse:
    request = Request(url, headers={"Accept": "application/json", "User-Agent": "HouseConsensusIngestion/1.0"})
    with urlopen(request, timeout=timeout) as response:  # noqa: S310 - endpoint is canonical and validated
        return _HttpResponse(status=response.status, body=response.read())
