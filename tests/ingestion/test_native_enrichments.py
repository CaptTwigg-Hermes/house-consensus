from __future__ import annotations

from typing import Any

import pytest


class MemoryCache:
    def __init__(self) -> None:
        self.values: dict[tuple[str, str], dict[str, Any]] = {}
        self.gets: list[tuple[str, str]] = []
        self.puts: list[tuple[str, str]] = []

    def get(self, namespace: str, key: str) -> dict[str, Any] | None:
        self.gets.append((namespace, key))
        value = self.values.get((namespace, key))
        return dict(value) if value is not None else None

    def put(self, namespace: str, key: str, payload: dict[str, Any]) -> None:
        self.puts.append((namespace, key))
        self.values[(namespace, key)] = dict(payload)


def test_vision_recovers_floorplan_and_reuses_input_bound_cache() -> None:
    from house_consensus_ingestion.enrichments import VisionEnricher

    cache = MemoryCache()
    analyzed: list[list[str]] = []
    record = {"id": "1", "images": [{"imageSources": [
        {"url": "https://img/small", "size": {"width": 300}},
        {"url": "https://img/large", "size": {"width": 1200}},
    ]}]}

    def analyze(urls: list[str]) -> dict[str, Any]:
        analyzed.append(urls)
        return {"vision_run_status": "ok", "vision_confidence": "high"}

    enricher = VisionEnricher(
        cache=cache, recover_floorplans=lambda _: ["https://img/plan"], analyze=analyze, model="vision-v1",
    )
    first = enricher.enrich([record])
    second_record = dict(record)
    second = enricher.enrich([second_record])

    assert analyzed == [["https://img/plan", "https://img/large", "https://img/small"]]
    assert first == {"enriched": 1, "fresh": 1, "cache_hits": 0}
    assert second == {"enriched": 1, "fresh": 0, "cache_hits": 1}
    assert second_record["vision_run_status"] == "ok"
    assert len(cache.puts) == 1


def test_vision_failure_is_not_cached() -> None:
    from house_consensus_ingestion.enrichments import VisionEnricher

    cache = MemoryCache()
    enricher = VisionEnricher(
        cache=cache, recover_floorplans=lambda _: [],
        analyze=lambda _: {"vision_run_status": "backend_error"}, model="vision-v1",
    )

    with pytest.raises(RuntimeError, match="did not complete"):
        enricher.enrich([{"id": "1"}])

    assert cache.puts == []


def test_commute_routes_each_destination_once_and_marks_missing_coordinates_unavailable() -> None:
    from house_consensus_ingestion.enrichments import CommuteEnricher

    cache = MemoryCache()
    routes: list[tuple[tuple[float, float], tuple[float, float]]] = []

    def route(origin: tuple[float, float], destination: tuple[float, float]) -> dict[str, Any]:
        routes.append((origin, destination))
        return {"status": "ok", "car_minutes": 20}

    enricher = CommuteEnricher(
        cache=cache, destinations={"work": ("Work", 55.8, 12.6)}, route=route,
    )
    records = [{"id": "1", "latitude": 55.7, "longitude": 12.5}, {"id": "2"}]

    first = enricher.enrich(records)
    second = enricher.enrich([{"id": "3", "latitude": 55.7, "longitude": 12.5}])

    assert routes == [((55.7, 12.5), (55.8, 12.6))]
    assert first == {"enriched": 2, "fresh": 1, "cache_hits": 0}
    assert second == {"enriched": 1, "fresh": 0, "cache_hits": 1}
    assert records[0]["commute"]["destinations"]["work"]["label"] == "Work"
    assert records[1]["commute"] == {"status": "unavailable", "reason": "missing_coordinates", "destinations": {}}


def test_sold_history_normalizes_and_reuses_address_bound_cache() -> None:
    from house_consensus_ingestion.enrichments import SoldHistoryEnricher

    cache = MemoryCache()
    calls: list[str] = []

    def fetch(address_id: str) -> list[dict[str, Any]]:
        calls.append(address_id)
        return [{"date": "2024-01-01", "amount": 4_000_000, "perAreaPrice": 20_000, "type": "normal"}]

    enricher = SoldHistoryEnricher(cache=cache, fetch=fetch)
    first_record = {"id": "1", "_addressID": "address-1"}
    second_record = {"id": "2", "_addressID": "address-1"}

    first = enricher.enrich([first_record])
    second = enricher.enrich([second_record])

    assert calls == ["address-1"]
    assert first == {"enriched": 1, "fresh": 1, "cache_hits": 0}
    assert second == {"enriched": 1, "fresh": 0, "cache_hits": 1}
    assert second_record["sold_history"] == [{
        "date": "2024-01-01", "amount_dkk": 4_000_000, "per_m2": 20_000, "type": "normal",
    }]


class NoiseCursor:
    def __init__(self, rows: list[tuple[Any, ...]]) -> None:
        self.rows = rows
        self.executed: list[tuple[str, tuple[float, float]]] = []

    def __enter__(self):
        return self

    def __exit__(self, *_: object) -> None:
        return None

    def execute(self, statement: str, parameters: tuple[float, float]) -> None:
        self.executed.append((statement, parameters))

    def fetchall(self) -> list[tuple[Any, ...]]:
        return self.rows


class NoiseConnection:
    def __init__(self, rows: list[tuple[Any, ...]]) -> None:
        self.cursor_instance = NoiseCursor(rows)

    def __enter__(self):
        return self

    def __exit__(self, *_: object) -> None:
        return None

    def cursor(self) -> NoiseCursor:
        return self.cursor_instance


@pytest.mark.parametrize(
    ("road_row", "expected"),
    [
        (("ROAD", "Lden", "no_contour", None, None, "road-v1"), "quiet"),
        (("ROAD", "Lden", "covered", 61.0, "60-65", "road-v1"), "noisy"),
    ],
)
def test_postgis_noise_preserves_source_status_and_uses_lon_lat_order(road_row: tuple[Any, ...], expected: str) -> None:
    from house_consensus_ingestion.enrichments import PostGISNoiseEnricher

    connection = NoiseConnection([road_row, ("RAIL", "Lden", "no_contour", None, None, "rail-v1")])
    record = {"id": "1", "latitude": 55.7, "longitude": 12.5}

    outcome = PostGISNoiseEnricher(lambda: connection).enrich([record])

    statement, parameters = connection.cursor_instance.executed[0]
    assert "ST_Intersects" in statement
    assert parameters == (12.5, 55.7)
    assert record["noise_status"] == expected
    assert record["noise_sources"]["ROAD"]["Lden"]["status"] == road_row[2]
    assert outcome["enriched"] == 1