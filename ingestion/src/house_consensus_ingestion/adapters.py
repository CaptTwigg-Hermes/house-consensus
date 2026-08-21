"""Production HTTP, Ollama, routing, and native-pipeline adapters.

All source/network dependencies are explicit constructor arguments or validated
configuration.  This module owns its behavior; it never imports the retired
HouseShopping runtime or reads its files.
"""
from __future__ import annotations

import base64
import ipaddress
import json
import math
import os
import re
from collections.abc import Callable, Mapping, Sequence
from dataclasses import dataclass, field
from datetime import datetime, timedelta
from html.parser import HTMLParser
from typing import Any
from urllib.parse import quote, urlencode, urljoin, urlparse
from urllib.request import Request, urlopen
from zoneinfo import ZoneInfo

from .classification import ClassificationConfig
from .enrichments import (
    CommuteEnricher,
    JsonCache,
    PostGISNoiseEnricher,
    PostgresJsonCache,
    SoldHistoryEnricher,
    VisionEnricher,
)
from .pipeline import NativeCasePipeline, RequiredEnrichmentError
from .scoring import family_score

JsonGet = Callable[[str, float], Mapping[str, Any]]
BytesGet = Callable[[str, float], tuple[str, bytes]]
JsonPost = Callable[[str, dict[str, Any], float], Mapping[str, Any]]

_MAX_JSON_BYTES = 2 * 1024 * 1024
_MAX_HTML_BYTES = 4 * 1024 * 1024
_MAX_IMAGE_BYTES = 12 * 1024 * 1024
_USER_AGENT = "HouseConsensus/1.0"


class SourceIdentityError(RuntimeError):
    """The exact requested source identity was absent or changed."""


class UpstreamResponseError(RuntimeError):
    """An upstream response is unavailable or violates its contract."""


def _read_bounded(response: Any, maximum: int) -> bytes:
    declared = response.headers.get("Content-Length")
    if declared:
        try:
            if int(declared) > maximum:
                raise UpstreamResponseError("upstream response exceeds size limit")
        except ValueError as error:
            raise UpstreamResponseError("upstream returned invalid Content-Length") from error
    value = response.read(maximum + 1)
    if len(value) > maximum:
        raise UpstreamResponseError("upstream response exceeds size limit")
    return value


def http_get_json(url: str, timeout: float) -> Mapping[str, Any]:
    request = Request(url, headers={"Accept": "application/json", "User-Agent": _USER_AGENT})
    with urlopen(request, timeout=timeout) as response:
        payload = json.loads(_read_bounded(response, _MAX_JSON_BYTES))
    if not isinstance(payload, Mapping):
        raise UpstreamResponseError("upstream JSON response must be an object")
    return payload


def http_get_bytes(url: str, timeout: float) -> tuple[str, bytes]:
    request = Request(url, headers={"Accept": "text/html,image/*", "User-Agent": _USER_AGENT})
    with urlopen(request, timeout=timeout) as response:
        content_type = (response.headers.get("Content-Type") or "").split(";", 1)[0].strip().lower()
        maximum = _MAX_HTML_BYTES if content_type in {"text/html", "application/xhtml+xml"} else _MAX_IMAGE_BYTES
        return content_type, _read_bounded(response, maximum)


def http_post_json(url: str, payload: dict[str, Any], timeout: float) -> Mapping[str, Any]:
    body = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode()
    request = Request(
        url,
        data=body,
        method="POST",
        headers={"Accept": "application/json", "Content-Type": "application/json", "User-Agent": _USER_AGENT},
    )
    with urlopen(request, timeout=timeout) as response:
        result = json.loads(_read_bounded(response, _MAX_JSON_BYTES))
    if not isinstance(result, Mapping):
        raise UpstreamResponseError("upstream JSON response must be an object")
    return result


def _positive_number(value: Any, name: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value <= 0:
        raise UpstreamResponseError(f"{name} must be a positive finite number")
    return float(value)


class BoligsidenCaseClient:
    """Resolve one exact Boligsiden case ID through ``/cases/{id}``."""

    def __init__(
        self,
        *,
        endpoint: str = "https://api.boligsiden.dk/cases",
        get_json: JsonGet = http_get_json,
        timeout_seconds: float = 20.0,
    ) -> None:
        self._endpoint = _https_endpoint(endpoint, "Boligsiden cases endpoint")
        self._get_json = get_json
        self._timeout = _positive_config(timeout_seconds, "case timeout")

    def fetch_case(self, case_id: str) -> dict[str, Any]:
        identifier = str(case_id).strip()
        if not identifier or len(identifier) > 128:
            raise SourceIdentityError("a valid Boligsiden case ID is required")
        payload = self._get_json(f"{self._endpoint}/{quote(identifier, safe='')}", self._timeout)
        actual = payload.get("caseID")
        if isinstance(actual, bool) or str(actual).strip() != identifier:
            raise SourceIdentityError("Boligsiden response does not match requested case ID")
        return dict(payload)


class BoligsidenSoldHistoryClient:
    """Fetch the immutable registration envelope for one exact address ID."""

    def __init__(
        self,
        *,
        endpoint: str = "https://api.boligsiden.dk/addresses",
        get_json: JsonGet = http_get_json,
        timeout_seconds: float = 20.0,
    ) -> None:
        self._endpoint = _https_endpoint(endpoint, "Boligsiden addresses endpoint")
        self._get_json = get_json
        self._timeout = _positive_config(timeout_seconds, "sold-history timeout")

    def __call__(self, address_id: str) -> Sequence[Mapping[str, Any]]:
        identifier = str(address_id).strip()
        if not identifier or len(identifier) > 128:
            raise UpstreamResponseError("a valid Boligsiden address ID is required")
        payload = self._get_json(f"{self._endpoint}/{quote(identifier, safe='')}", self._timeout)
        registrations = payload.get("registrations")
        if not isinstance(registrations, list) or not all(isinstance(item, Mapping) for item in registrations):
            raise UpstreamResponseError("Boligsiden address response has no registrations array")
        return registrations


@dataclass(frozen=True)
class RoutingConfig:
    osrm_endpoint: str = "https://router.project-osrm.org/route/v1/driving"
    brouter_endpoint: str = "https://brouter.de/brouter"
    transit_endpoint: str = "https://api.transitous.org/api/v1/plan"
    arrival_time: str | None = None
    timeout_seconds: float = 30.0
    bike_profile: str = "trekking"

    def __post_init__(self) -> None:
        for endpoint in (self.osrm_endpoint, self.brouter_endpoint, self.transit_endpoint):
            _http_endpoint(endpoint, "routing endpoint")
        _positive_config(self.timeout_seconds, "routing timeout")
        if not self.bike_profile.strip():
            raise ValueError("bike profile must not be blank")


class CommuteRouter:
    """Route car, bike, and public transport using real public APIs."""

    def __init__(self, config: RoutingConfig, *, get_json: JsonGet = http_get_json) -> None:
        self._config = config
        self._get_json = get_json

    def route(self, origin: tuple[float, float], destination: tuple[float, float]) -> dict[str, Any]:
        origin_lat, origin_lon = _coordinates(origin)
        dest_lat, dest_lon = _coordinates(destination)
        car_url = (
            f"{self._config.osrm_endpoint.rstrip('/')}/{origin_lon},{origin_lat};{dest_lon},{dest_lat}"
            "?overview=false&alternatives=false&steps=false"
        )
        car_payload = self._get_json(car_url, self._config.timeout_seconds)
        routes = car_payload.get("routes")
        if car_payload.get("code") != "Ok" or not isinstance(routes, list) or not routes or not isinstance(routes[0], Mapping):
            raise UpstreamResponseError("OSRM returned no route")
        car = _duration_distance(routes[0], duration="duration", distance="distance")

        bike_url = f"{self._config.brouter_endpoint}?{urlencode({'lonlats': f'{origin_lon},{origin_lat}|{dest_lon},{dest_lat}', 'profile': self._config.bike_profile, 'alternativeidx': '0', 'format': 'geojson'})}"
        bike_payload = self._get_json(bike_url, self._config.timeout_seconds)
        features = bike_payload.get("features")
        properties = features[0].get("properties") if isinstance(features, list) and features and isinstance(features[0], Mapping) else None
        if not isinstance(properties, Mapping):
            raise UpstreamResponseError("BRouter returned no route")
        bike = _duration_distance(properties, duration="total-time", distance="track-length")

        public_url = f"{self._config.transit_endpoint}?{urlencode({'fromPlace': f'{origin_lat},{origin_lon}', 'toPlace': f'{dest_lat},{dest_lon}', 'arriveBy': 'true', 'time': self._config.arrival_time or _next_weekday_arrival()})}"
        public_payload = self._get_json(public_url, self._config.timeout_seconds)
        itineraries = public_payload.get("itineraries")
        valid = [item for item in itineraries if isinstance(item, Mapping)] if isinstance(itineraries, list) else []
        if not valid:
            raise UpstreamResponseError("Transitous returned no itinerary")
        best = min(valid, key=lambda item: _sortable_duration(item.get("duration")))
        duration = _positive_number(best.get("duration"), "Transitous duration")
        modes: list[str] = []
        for leg in best.get("legs") or []:
            mode = leg.get("mode") if isinstance(leg, Mapping) else None
            if isinstance(mode, str) and mode not in {"WALK", "BIKE", "CAR", "BIKE_RENTAL"} and mode not in modes:
                modes.append(mode)
        transfers = best.get("transfers")
        if isinstance(transfers, bool) or not isinstance(transfers, int) or transfers < 0:
            transfers = None
        return {
            "status": "ok",
            "car": car,
            "bike": bike,
            "public": {"min": round(duration / 60), "transfers": transfers, "modes": modes},
        }


class _PlanParser(HTMLParser):
    def __init__(self, base_url: str) -> None:
        super().__init__(convert_charrefs=True)
        self.base_url = base_url
        self.urls: list[str] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        if tag.casefold() != "img":
            return
        values = {name.casefold(): value or "" for name, value in attrs}
        if not re.search(r"(?:plantegn|floor\s*plan)", values.get("alt", ""), re.IGNORECASE):
            return
        source = _largest_srcset(values.get("srcset") or values.get("data-srcset"))
        source = source or values.get("data-src") or values.get("data-original") or values.get("data-lazy") or values.get("src")
        if source:
            url = urljoin(self.base_url, source)
            if _safe_media_url(url):
                self.urls.append(url)


class FloorplanRecoverer:
    """Recover only realtor images explicitly labelled as floor plans."""

    def __init__(self, *, get_bytes: BytesGet = http_get_bytes, timeout_seconds: float = 20.0) -> None:
        self._get_bytes = get_bytes
        self._timeout = _positive_config(timeout_seconds, "floorplan timeout")

    def __call__(self, record: Mapping[str, Any]) -> list[str]:
        case_url = record.get("caseUrl")
        if not isinstance(case_url, str) or not _safe_media_url(case_url):
            return []
        content_type, raw = self._get_bytes(case_url, self._timeout)
        if content_type not in {"text/html", "application/xhtml+xml"}:
            raise UpstreamResponseError("realtor floorplan response is not HTML")
        parser = _PlanParser(case_url)
        parser.feed(raw.decode("utf-8", "replace"))
        return list(dict.fromkeys(parser.urls))


_BOOL_VISION_FIELDS = {
    "vision_separate_entrance", "vision_second_kitchen", "vision_internal_connection",
    "vision_garage_on_plan", "vision_basement_on_plan", "vision_ground_floor_bedroom",
    "vision_utility_room", "vision_wc_ground_floor", "vision_open_plan_shared",
    "vision_has_terrace", "vision_has_solar", "vision_garage_visible",
    "vision_garden_private_zones", "vision_annex_visible",
}
_INT_VISION_FIELDS = {
    "vision_staircase_count", "vision_en_suite_count", "vision_bathroom_count",
    "vision_bedroom_count", "vision_storage_count", "vision_parking_count",
}
_TEXT_VISION_FIELDS = {
    "vision_split_type", "vision_condition", "vision_style", "vision_roof_type",
    "vision_exterior_material", "vision_garden_orientation", "vision_garden_structure",
    "vision_dining_capacity", "vision_kitchen_size", "vision_summary",
}


@dataclass(frozen=True)
class OllamaVisionConfig:
    host: str
    model: str
    timeout_seconds: float = 180.0
    image_timeout_seconds: float = 30.0
    max_images: int = 6

    def __post_init__(self) -> None:
        _http_endpoint(self.host, "Ollama host")
        if not self.model.strip():
            raise ValueError("Ollama model must not be blank")
        _positive_config(self.timeout_seconds, "Ollama timeout")
        _positive_config(self.image_timeout_seconds, "image timeout")
        if self.max_images < 1 or self.max_images > 10:
            raise ValueError("Ollama max_images must be from 1 through 10")


class OllamaVisionAnalyzer:
    """Fetch bounded public images and request deterministic structured analysis."""

    def __init__(
        self,
        config: OllamaVisionConfig,
        *,
        get_bytes: BytesGet = http_get_bytes,
        post_json: JsonPost = http_post_json,
    ) -> None:
        self._config = config
        self._get_bytes = get_bytes
        self._post_json = post_json

    def __call__(self, urls: list[str]) -> dict[str, Any]:
        images: list[str] = []
        used_urls: list[str] = []
        for url in urls:
            if len(images) >= self._config.max_images or not _safe_media_url(url):
                continue
            content_type, raw = self._get_bytes(url, self._config.image_timeout_seconds)
            if not content_type.startswith("image/") or not raw or len(raw) > _MAX_IMAGE_BYTES:
                raise UpstreamResponseError("vision image response is invalid")
            images.append(base64.b64encode(raw).decode("ascii"))
            used_urls.append(url)
        if not images:
            return {
                "vision_run_status": "no_image",
                "vision_confidence": "none",
                "vision_model_used": self._config.model,
                "vision_image_count": 0,
                "vision_image_urls": [],
            }
        response = self._post_json(
            f"{self._config.host.rstrip('/')}/api/generate",
            {
                "model": self._config.model,
                "prompt": _VISION_PROMPT,
                "images": images,
                "stream": False,
                "think": False,
                "format": "json",
                "options": {"temperature": 0, "num_predict": 1600},
            },
            self._config.timeout_seconds,
        )
        text = response.get("response")
        if not isinstance(text, str):
            raise UpstreamResponseError("Ollama response has no text")
        parsed = _parse_json_object(text)
        if parsed is None:
            raise UpstreamResponseError("Ollama did not return structured JSON")
        result = _validated_vision(parsed)
        result.update(
            vision_run_status="ok",
            vision_model_used=self._config.model,
            vision_image_count=len(images),
            vision_image_urls=used_urls,
        )
        return result


_VISION_PROMPT = """Analyze these images of one Danish home. Return one JSON object only.
Never infer details that are not visible; use null when uncertain. Fields:
vision_separate_entrance, vision_second_kitchen, vision_internal_connection,
vision_split_type (horizontal|vertical|side_by_side|none|null),
vision_staircase_count, vision_en_suite_count, vision_bathroom_count,
vision_bedroom_count, vision_ground_floor_bedroom, vision_utility_room,
vision_wc_ground_floor, vision_open_plan_shared, vision_storage_count,
vision_condition (excellent|good|fair|poor|null), vision_has_terrace,
vision_has_solar, vision_garage_on_plan, vision_garage_visible,
vision_parking_count, vision_garden_orientation, vision_garden_structure,
vision_garden_private_zones, vision_annex_visible, vision_dining_capacity,
vision_kitchen_size, vision_confidence (high|medium|low|none), vision_summary.
"""


@dataclass(frozen=True)
class ProductionAdapterConfig:
    database_url: str
    noise_database_url: str
    destinations: Mapping[str, tuple[str, float, float]]
    ollama: OllamaVisionConfig
    routing: RoutingConfig = field(default_factory=RoutingConfig)
    boligsiden_cases_endpoint: str = "https://api.boligsiden.dk/cases"
    boligsiden_addresses_endpoint: str = "https://api.boligsiden.dk/addresses"

    @classmethod
    def from_env(cls, environment: Mapping[str, str] | None = None) -> ProductionAdapterConfig:
        env = os.environ if environment is None else environment
        database_url = _required_env(env, "DATABASE_URL")
        noise_database_url = _required_env(env, "CONSENSUS_NOISE_DATABASE_URL")
        model = _required_env(env, "CONSENSUS_OLLAMA_MODEL")
        host = _required_env(env, "CONSENSUS_OLLAMA_HOST")
        raw_destinations = _required_env(env, "CONSENSUS_COMMUTE_DESTINATIONS")
        try:
            values = json.loads(raw_destinations)
        except json.JSONDecodeError as error:
            raise ValueError("CONSENSUS_COMMUTE_DESTINATIONS must be valid JSON") from error
        destinations = _destinations(values)
        return cls(
            database_url=database_url,
            noise_database_url=noise_database_url,
            destinations=destinations,
            ollama=OllamaVisionConfig(
                host=host,
                model=model,
                timeout_seconds=_env_float(env, "CONSENSUS_OLLAMA_TIMEOUT_SECONDS", 180),
                image_timeout_seconds=_env_float(env, "CONSENSUS_IMAGE_TIMEOUT_SECONDS", 30),
                max_images=_env_int(env, "CONSENSUS_OLLAMA_MAX_IMAGES", 6),
            ),
            routing=RoutingConfig(
                osrm_endpoint=env.get("CONSENSUS_OSRM_ENDPOINT", RoutingConfig.osrm_endpoint),
                brouter_endpoint=env.get("CONSENSUS_BROUTER_ENDPOINT", RoutingConfig.brouter_endpoint),
                transit_endpoint=env.get("CONSENSUS_TRANSIT_ENDPOINT", RoutingConfig.transit_endpoint),
                arrival_time=env.get("CONSENSUS_TRANSIT_ARRIVAL_TIME") or None,
                timeout_seconds=_env_float(env, "CONSENSUS_ROUTING_TIMEOUT_SECONDS", 30),
                bike_profile=env.get("CONSENSUS_BIKE_PROFILE", "trekking"),
            ),
            boligsiden_cases_endpoint=env.get("CONSENSUS_BOLIGSIDEN_CASES_ENDPOINT", "https://api.boligsiden.dk/cases"),
            boligsiden_addresses_endpoint=env.get("CONSENSUS_BOLIGSIDEN_ADDRESSES_ENDPOINT", "https://api.boligsiden.dk/addresses"),
        )


class NativeListingScorer:
    """Run required enrichment stages and deterministic scoring for one raw case."""

    def __init__(self, enrichers: Sequence[Any]) -> None:
        self._enrichers = tuple(enrichers)

    def score(self, listing: dict[str, Any]) -> dict[str, Any]:
        for enricher in self._enrichers:
            try:
                outcome = enricher.enrich([listing])
                if outcome.get("incomplete"):
                    raise RequiredEnrichmentError(
                        f"required enrichment {enricher.name} reported incomplete evidence"
                    )
            except Exception as error:
                if isinstance(error, RequiredEnrichmentError):
                    raise
                raise RequiredEnrichmentError(f"required enrichment {enricher.name} failed: {error}") from error
        return family_score(listing)


def build_production_pipeline(
    config: ProductionAdapterConfig,
    *,
    cache: JsonCache | None = None,
    connection_factory: Callable[[], Any] | None = None,
    noise_connection_factory: Callable[[], Any] | None = None,
    get_json: JsonGet = http_get_json,
    get_bytes: BytesGet = http_get_bytes,
    post_json: JsonPost = http_post_json,
    classification: ClassificationConfig | None = None,
) -> NativeCasePipeline:
    enrichers = _build_enrichers(
        config,
        cache=cache,
        connection_factory=connection_factory,
        noise_connection_factory=noise_connection_factory,
        get_json=get_json,
        get_bytes=get_bytes,
        post_json=post_json,
    )
    return NativeCasePipeline(classification=classification or ClassificationConfig(), enrichers=enrichers)


def build_production_scorer(
    config: ProductionAdapterConfig,
    **dependencies: Any,
) -> NativeListingScorer:
    return NativeListingScorer(_build_enrichers(config, **dependencies))


def _build_enrichers(
    config: ProductionAdapterConfig,
    *,
    cache: JsonCache | None = None,
    connection_factory: Callable[[], Any] | None = None,
    noise_connection_factory: Callable[[], Any] | None = None,
    get_json: JsonGet = http_get_json,
    get_bytes: BytesGet = http_get_bytes,
    post_json: JsonPost = http_post_json,
) -> tuple[Any, ...]:
    if cache is None:
        connection_factory = connection_factory or _psycopg_factory(config.database_url)
        postgres_cache = PostgresJsonCache(connection_factory)
        postgres_cache.ensure_schema()
        cache = postgres_cache
    noise_connection_factory = noise_connection_factory or _psycopg_factory(config.noise_database_url)
    router = CommuteRouter(config.routing, get_json=get_json)
    return (
        PostGISNoiseEnricher(noise_connection_factory),
        SoldHistoryEnricher(
            cache=cache,
            fetch=BoligsidenSoldHistoryClient(
                endpoint=config.boligsiden_addresses_endpoint,
                get_json=get_json,
            ),
        ),
        CommuteEnricher(cache=cache, destinations=config.destinations, route=router.route),
        VisionEnricher(
            cache=cache,
            recover_floorplans=FloorplanRecoverer(get_bytes=get_bytes),
            analyze=OllamaVisionAnalyzer(config.ollama, get_bytes=get_bytes, post_json=post_json),
            model=config.ollama.model,
        ),
    )


def _psycopg_factory(database_url: str) -> Callable[[], Any]:
    def connect() -> Any:
        import psycopg

        return psycopg.connect(database_url)

    return connect


def _duration_distance(payload: Mapping[str, Any], *, duration: str, distance: str) -> dict[str, Any]:
    seconds = _positive_number(payload.get(duration), f"{duration}")
    metres = _positive_number(payload.get(distance), f"{distance}")
    return {"min": round(seconds / 60), "km": round(metres / 1000, 1)}


def _coordinates(value: tuple[float, float]) -> tuple[float, float]:
    lat, lon = value
    if not all(isinstance(item, (int, float)) and not isinstance(item, bool) and math.isfinite(item) for item in value):
        raise ValueError("route coordinates must be finite numbers")
    if not -90 <= lat <= 90 or not -180 <= lon <= 180:
        raise ValueError("route coordinates are out of range")
    return float(lat), float(lon)


def _sortable_duration(value: Any) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value <= 0:
        return math.inf
    return float(value)


def _next_weekday_arrival() -> str:
    now = datetime.now(ZoneInfo("Europe/Copenhagen"))
    day = now.date() + timedelta(days=1)
    while day.weekday() >= 5:
        day += timedelta(days=1)
    return datetime(day.year, day.month, day.day, 8, tzinfo=now.tzinfo).isoformat()


def _largest_srcset(value: str | None) -> str | None:
    best: tuple[int, str] | None = None
    for item in (value or "").split(","):
        parts = item.strip().split()
        if not parts:
            continue
        width = int(parts[1][:-1]) if len(parts) > 1 and parts[1][:-1].isdigit() and parts[1].endswith("w") else 0
        if best is None or width >= best[0]:
            best = width, parts[0]
    return best[1] if best else None


def _safe_media_url(url: str) -> bool:
    try:
        parsed = urlparse(url)
        if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password:
            return False
        host = parsed.hostname.rstrip(".").casefold()
        if host == "localhost" or host.endswith(".localhost"):
            return False
        try:
            return not ipaddress.ip_address(host).is_private
        except ValueError:
            return True
    except ValueError:
        return False


def _parse_json_object(text: str) -> Mapping[str, Any] | None:
    cleaned = text.strip()
    if cleaned.startswith("```"):
        cleaned = re.sub(r"^```(?:json)?\s*", "", cleaned, flags=re.IGNORECASE)
        cleaned = re.sub(r"\s*```$", "", cleaned)
    start, end = cleaned.find("{"), cleaned.rfind("}")
    if start < 0 or end <= start:
        return None
    try:
        result = json.loads(cleaned[start : end + 1])
    except json.JSONDecodeError:
        return None
    return result if isinstance(result, Mapping) else None


def _validated_vision(payload: Mapping[str, Any]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for name in _BOOL_VISION_FIELDS:
        value = payload.get(name)
        result[name] = value if isinstance(value, bool) else None
    for name in _INT_VISION_FIELDS:
        value = payload.get(name)
        result[name] = value if isinstance(value, int) and not isinstance(value, bool) and 0 <= value <= 100 else None
    for name in _TEXT_VISION_FIELDS:
        value = payload.get(name)
        result[name] = value.strip()[:1000] if isinstance(value, str) and value.strip() else None
    confidence = payload.get("vision_confidence")
    result["vision_confidence"] = confidence if confidence in {"high", "medium", "low", "none"} else "none"
    return result


def _destinations(value: Any) -> dict[str, tuple[str, float, float]]:
    if not isinstance(value, Mapping) or not value:
        raise ValueError("CONSENSUS_COMMUTE_DESTINATIONS must be a non-empty object")
    result: dict[str, tuple[str, float, float]] = {}
    for key, raw in value.items():
        if not isinstance(key, str) or not key.strip() or not isinstance(raw, Mapping):
            raise ValueError("each commute destination requires a non-blank key and object value")
        label, lat, lon = raw.get("label"), raw.get("latitude"), raw.get("longitude")
        if not isinstance(label, str) or not label.strip():
            raise ValueError(f"commute destination {key!r} requires a label")
        coordinates = _coordinates((lat, lon))
        result[key] = (label.strip(), *coordinates)
    return result


def _required_env(environment: Mapping[str, str], name: str) -> str:
    value = environment.get(name)
    if not value or not value.strip():
        raise ValueError(f"{name} is required")
    return value.strip()


def _env_float(environment: Mapping[str, str], name: str, default: float) -> float:
    raw = environment.get(name)
    try:
        return default if raw is None else float(raw)
    except ValueError as error:
        raise ValueError(f"{name} must be numeric") from error


def _env_int(environment: Mapping[str, str], name: str, default: int) -> int:
    raw = environment.get(name)
    try:
        return default if raw is None else int(raw)
    except ValueError as error:
        raise ValueError(f"{name} must be an integer") from error


def _positive_config(value: float, name: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value <= 0:
        raise ValueError(f"{name} must be a positive finite number")
    return float(value)


def _http_endpoint(value: str, name: str) -> str:
    parsed = urlparse(value)
    if parsed.scheme not in {"http", "https"} or not parsed.hostname or parsed.username or parsed.password:
        raise ValueError(f"{name} must be an absolute HTTP URL without credentials")
    return value.rstrip("/")


def _https_endpoint(value: str, name: str) -> str:
    endpoint = _http_endpoint(value, name)
    if urlparse(endpoint).scheme != "https":
        raise ValueError(f"{name} must use HTTPS")
    return endpoint
