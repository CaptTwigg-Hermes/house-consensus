"""Native enrichment stages and PostgreSQL-backed durable caches."""
from __future__ import annotations

import hashlib
import json
import math
from collections.abc import Callable, Mapping, Sequence
from datetime import UTC, datetime
from typing import Any, Protocol


class JsonCache(Protocol):
    def get(self, namespace: str, key: str) -> dict[str, Any] | None: ...
    def put(self, namespace: str, key: str, payload: dict[str, Any]) -> None: ...


def _digest(value: object) -> str:
    encoded = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode()
    return hashlib.sha256(encoded).hexdigest()


class PostgresJsonCache:
    """Durable cache stored with source state, never in local files."""
    def __init__(self, connection_factory: Callable[[], Any]) -> None:
        self._connection_factory = connection_factory

    def ensure_schema(self) -> None:
        with self._connection_factory() as connection, connection.cursor() as cursor:
            cursor.execute("""
                CREATE TABLE IF NOT EXISTS ingestion_enrichment_cache (
                    namespace text NOT NULL,
                    cache_key text NOT NULL,
                    payload jsonb NOT NULL,
                    cached_at timestamptz NOT NULL DEFAULT now(),
                    PRIMARY KEY (namespace, cache_key)
                )
            """)

    def get(self, namespace: str, key: str) -> dict[str, Any] | None:
        with self._connection_factory() as connection, connection.cursor() as cursor:
            cursor.execute(
                "SELECT payload FROM ingestion_enrichment_cache WHERE namespace = %s AND cache_key = %s",
                (namespace, key),
            )
            row = cursor.fetchone()
        if row is None:
            return None
        value = row[0]
        return json.loads(value) if isinstance(value, str) else dict(value)

    def put(self, namespace: str, key: str, payload: dict[str, Any]) -> None:
        with self._connection_factory() as connection, connection.cursor() as cursor:
            cursor.execute("""
                INSERT INTO ingestion_enrichment_cache (namespace, cache_key, payload, cached_at)
                VALUES (%s, %s, %s::jsonb, %s)
                ON CONFLICT (namespace, cache_key) DO UPDATE
                SET payload = EXCLUDED.payload, cached_at = EXCLUDED.cached_at
            """, (namespace, key, json.dumps(payload, sort_keys=True), datetime.now(UTC)))


def _image_urls(record: Mapping[str, Any]) -> list[str]:
    result: list[tuple[int, str]] = []
    images = [*(record.get("floorPlanImages") or [])]
    default_image = record.get("defaultImage")
    if isinstance(default_image, Mapping):
        images.append(default_image)
    images.extend(record.get("images") or [])
    for image in images:
        if not isinstance(image, Mapping):
            continue
        for source in image.get("imageSources") or []:
            if not isinstance(source, Mapping) or not source.get("url"):
                continue
            size = source.get("size")
            width = size.get("width", 0) if isinstance(size, Mapping) else 0
            result.append((int(width) if isinstance(width, (int, float)) else 0, str(source["url"])))
    direct = record.get("caseUrlFloorPlan")
    urls = [str(direct)] if isinstance(direct, str) and direct.startswith("https://") else []
    urls.extend(url for _, url in sorted(result, reverse=True)[:10])
    return list(dict.fromkeys(urls))


class VisionEnricher:
    name = "vision"
    def __init__(self, *, cache: JsonCache, recover_floorplans: Callable[[Mapping[str, Any]], list[str]],
                 analyze: Callable[[list[str]], dict[str, Any]], model: str) -> None:
        self._cache, self._recover, self._analyze, self._model = cache, recover_floorplans, analyze, model

    def enrich(self, records: list[dict[str, Any]]) -> dict[str, int]:
        hits = fresh = 0
        for record in records:
            recovered = self._recover(record)
            urls = list(dict.fromkeys([*recovered, *_image_urls(record)]))
            if recovered:
                record["floor_plan_recovered"] = recovered[0]
            key = _digest({"model": self._model, "urls": urls})
            result = self._cache.get("vision-v2", key)
            if result is None:
                result = self._analyze(urls)
                if result.get("vision_run_status") not in {"ok", "no_image"}:
                    raise RuntimeError(f"vision analysis did not complete: {result.get('vision_run_status')}")
                self._cache.put("vision-v2", key, result); fresh += 1
            else:
                hits += 1
            record.update(result)
        return {"enriched": len(records), "fresh": fresh, "cache_hits": hits}


class CommuteEnricher:
    name = "commute"
    def __init__(self, *, cache: JsonCache, destinations: Mapping[str, tuple[str, float, float]],
                 route: Callable[[tuple[float, float], tuple[float, float]], dict[str, Any]]) -> None:
        self._cache, self._destinations, self._route = cache, dict(destinations), route

    def enrich(self, records: list[dict[str, Any]]) -> dict[str, int]:
        hits = fresh = incomplete = 0
        for record in records:
            lat, lon = record.get("latitude"), record.get("longitude")
            if not _finite(lat) or not _finite(lon):
                record["commute"] = {"status": "unavailable", "reason": "missing_coordinates", "destinations": {}}
                incomplete += 1
                continue
            payload = {"status": "ok", "destinations": {}}
            for key, (label, dest_lat, dest_lon) in sorted(self._destinations.items()):
                cache_key = _digest({"origin": [lat, lon], "destination": [dest_lat, dest_lon]})
                value = self._cache.get("commute-v1", cache_key)
                if value is None:
                    value = self._route((float(lat), float(lon)), (dest_lat, dest_lon))
                    if value.get("status") not in (None, "ok"):
                        raise RuntimeError(f"routing failed for {key}: {value.get('status')}")
                    self._cache.put("commute-v1", cache_key, value); fresh += 1
                else:
                    hits += 1
                payload["destinations"][key] = {"label": label, **value}
            record["commute"] = payload
        return {"enriched": len(records), "fresh": fresh, "cache_hits": hits, "incomplete": incomplete}


class SoldHistoryEnricher:
    name = "sold_history"
    def __init__(self, *, cache: JsonCache, fetch: Callable[[str], Sequence[Mapping[str, Any]]]) -> None:
        self._cache, self._fetch = cache, fetch

    def enrich(self, records: list[dict[str, Any]]) -> dict[str, int]:
        hits = fresh = incomplete = 0
        for record in records:
            address_id = str(record.get("_addressID") or "")
            if not address_id:
                record.update(sold_history=[], sold_history_status="unavailable")
                incomplete += 1
                continue
            value = self._cache.get("sold-history-v1", address_id)
            if value is None:
                sales = [_normal_sale(item) for item in self._fetch(address_id)]
                sales = [sale for sale in sales if sale is not None]
                sales.sort(key=lambda sale: str(sale.get("date") or ""), reverse=True)
                sales = sales[:3]
                value = {"sales": sales}
                self._cache.put("sold-history-v1", address_id, value); fresh += 1
            else:
                sales = value.get("sales") or []; hits += 1
            record["sold_history"] = [dict(sale) for sale in sales if isinstance(sale, Mapping)]
            record["sold_history_status"] = "ok" if record["sold_history"] else "empty"
        return {"enriched": len(records), "fresh": fresh, "cache_hits": hits, "incomplete": incomplete}


def _normal_sale(sale: Mapping[str, Any]) -> dict[str, Any] | None:
    amount = sale.get("amount_dkk", sale.get("amount"))
    if not _finite(amount) or float(amount) <= 0:
        return None
    per_m2 = sale.get("per_m2", sale.get("perAreaPrice"))
    if not _finite(per_m2) or float(per_m2) <= 0:
        area = sale.get("area")
        per_m2 = float(amount) / float(area) if _finite(area) and float(area) > 0 else None
    return {
        "date": sale.get("date"),
        "amount_dkk": round(float(amount)),
        "per_m2": round(float(per_m2)) if _finite(per_m2) and float(per_m2) > 0 else None,
        "type": sale.get("type") or "normal",
    }


def _finite(value: Any) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(float(value))


class PostGISNoiseEnricher:
    """Status-aware point lookup across PostgreSQL-authoritative noise layers."""
    name = "noise"
    def __init__(self, connection_factory: Callable[[], Any]) -> None:
        self._connection_factory = connection_factory

    def enrich(self, records: list[dict[str, Any]]) -> dict[str, int]:
        covered = incomplete = 0
        with self._connection_factory() as connection:
            for record in records:
                lat, lon = record.get("latitude"), record.get("longitude")
                if not _finite(lat) or not _finite(lon):
                    record.update(noise_status="unknown", noise_sources={})
                    incomplete += 1
                    continue
                with connection.cursor() as cursor:
                    cursor.execute("""
                        WITH inputs(property_id, lon, lat) AS (
                            VALUES (%s::text, %s::double precision, %s::double precision)
                        ), requested(source, indicator) AS (
                            VALUES ('ROAD', 'Lden'), ('ROAD', 'Lnight'),
                                   ('RAIL', 'Lden'), ('RAIL', 'Lnight'),
                                   ('AIR', 'Lden'), ('AIR', 'Lnight')
                        )
                        SELECT requested.source, requested.indicator,
                               CASE
                                   WHEN requested.source = 'ROAD' AND area.feature_uid IS NOT NULL THEN 'covered'
                                   WHEN requested.source = 'ROAD' AND area.layer_available THEN 'no_contour'
                                   WHEN requested.source = 'ROAD' THEN 'unavailable'
                                   ELSE COALESCE(sample.status,
                                       CASE WHEN sample.db_value IS NULL THEN 'unavailable' ELSE 'covered' END)
                               END AS status,
                               CASE WHEN requested.source = 'ROAD' THEN area.db_value ELSE sample.db_value END,
                               CASE WHEN requested.source = 'ROAD' THEN area.db_band ELSE sample.db_band END,
                               CASE WHEN requested.source = 'ROAD' THEN area.source_key ELSE sample.source_service END,
                               CASE WHEN requested.source = 'ROAD' THEN NULL ELSE sample.error END,
                               CASE WHEN requested.source = 'ROAD' THEN NULL ELSE sample.sampled_at END
                        FROM requested
                        CROSS JOIN inputs
                        LEFT JOIN LATERAL (
                            SELECT hit.feature_uid, hit.db_value, hit.db_band, hit.source_key,
                                   EXISTS (
                                       SELECT 1 FROM noise.noise_areas available
                                       WHERE lower(available.source) = lower(requested.source)
                                         AND lower(available.indicator) = lower(requested.indicator)
                                   ) AS layer_available
                            FROM (SELECT 1) seed
                            LEFT JOIN LATERAL (
                                SELECT candidate.feature_uid, candidate.db_value,
                                       candidate.payload->>'db_band' AS db_band,
                                       catalog.source_key
                                FROM noise.noise_areas candidate
                                LEFT JOIN noise.noise_source catalog ON catalog.id = candidate.source_id
                                WHERE lower(candidate.source) = lower(requested.source)
                                  AND lower(candidate.indicator) = lower(requested.indicator)
                                  AND ST_Covers(candidate.geom, ST_SetSRID(ST_Point(inputs.lon, inputs.lat), 4326))
                                ORDER BY candidate.db_value DESC NULLS LAST
                                LIMIT 1
                            ) hit ON true
                        ) area ON requested.source = 'ROAD'
                        LEFT JOIN LATERAL (
                            SELECT current.db_value, current.db_band, current.source_service,
                                   current.status, current.error, current.sampled_at
                            FROM public.property_noise_samples current
                            WHERE upper(current.source) = requested.source
                              AND current.indicator = requested.indicator
                              AND (
                                  current.property_id = inputs.property_id
                                  OR (current.geom IS NOT NULL AND ST_DWithin(
                                      current.geom::geography,
                                      ST_SetSRID(ST_Point(inputs.lon, inputs.lat), 4326)::geography,
                                      1
                                  ))
                              )
                            ORDER BY (current.property_id = inputs.property_id) DESC, current.sampled_at DESC
                            LIMIT 1
                        ) sample ON requested.source <> 'ROAD'
                        ORDER BY requested.source DESC, requested.indicator
                    """, (str(record.get("_addressID") or ""), float(lon), float(lat)))
                    rows = cursor.fetchall()
                sources: dict[str, dict[str, Any]] = {}
                for row in rows:
                    source, indicator, status, db_value, db_band, source_key = row[:6]
                    error = row[6] if len(row) > 6 else None
                    sampled_at = row[7] if len(row) > 7 else None
                    sources.setdefault(str(source), {})[str(indicator)] = {
                        "status": str(status), "db_value": db_value, "db_band": db_band,
                        "source_key": source_key, "error": error, "sampled_at": sampled_at,
                    }
                    covered += status == "covered"
                road = sources.get("ROAD", {}).get("Lden", {})
                db = road.get("db_value")
                if road.get("status") == "no_contour": aggregate = "quiet"
                elif road.get("status") == "covered" and _finite(db): aggregate = "noisy"
                else: aggregate = "unknown"
                record.update(noise_status=aggregate, noise_sources=sources)
        return {"enriched": len(records), "covered_observations": covered, "incomplete": incomplete}
