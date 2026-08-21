"""Deterministic Boligsiden normalization and family-home filtering.

Rules are ported from the former source pipeline and intentionally contain no
network, filesystem, or process dependencies.
"""
from __future__ import annotations

import math
import re
from collections import Counter
from collections.abc import Iterable, Mapping
from dataclasses import dataclass
from typing import Any

CONFIDENCE = {"none": 0, "possible": 1, "likely": 2, "confirmed": 3}
STRONG_TWO_FAMILY = ("tofamilie", "flerfamilie", "to-familie", "to familie", "generationsbolig")
WEAK_TWO_FAMILY = (
    "to lejligheder", "to selvstændige", "to boliger", "udlejningsdel", "udlejes",
    "separat lejlighed", "bolig nr. 2", "bolig 2", "to køkkener", "dobbelt",
    "bofællesskab", "to indgange", "egen indgang", "selvstændig lejlighed",
    "annekslejlighed", "flergenerations", "svigermorslejlighed",
    "bedsteforældrebolig", "kælder med køkken", "separat fløj", "eget badeværelse",
)
FAMILY_TIERS = (
    ("three_family", 3, ("trefamilie", "trefamilieshus", "tre-familie", "tre familie", "3-familie", "3 familie"),
     ("tre lejligheder", "tre boliger", "tre selvstændige", "tre indgange", "tre køkkener")),
    ("four_family", 4, ("firefamilie", "firefamilieshus", "fire-familie", "fire familie", "4-familie", "4 familie"),
     ("fire lejligheder", "fire boliger", "fire selvstændige")),
    ("multi_family", None, ("flerfamilie", "fler-familie", "etagebolig", "etageejendom", "udlejningsejendom"),
     ("flere lejligheder", "flere selvstændige boliger", "flere boligenheder")),
)
SINGLE_FAMILY = ("enfamilie", "enfamilieshus", "en-familie", "parcelhus")
GARDEN_TERMS = ("have", "haven", "grund", "anlagt have", "syd-vendt", "sydvendt", "terrasse", "gårdhave")
ONE_PLAN_TERMS = ("i ét plan", "i et plan", "i ét-plan", "ét-plans", "et-plans", "etplans", "ét plans")
APARTMENT_TYPES = {"condo", "villa apartment", "ejerlejlighed", "lejlighed", "andelslejlighed", "lejelejlighed"}
_UNIT_SUFFIX = re.compile(r",\s*\d*\.?\s*(tv|th|st\.?|mf)\b", re.IGNORECASE)
_ROAD = re.compile(r"^(.*?)\s+\d+")


@dataclass(frozen=True)
class ClassificationConfig:
    price_min: int = 0
    price_max: int = 12_000_000
    min_confidence: str = "possible"
    family_units: str | None = None
    max_noise: float | None = None
    max_per_road: int = 5

    def __post_init__(self) -> None:
        if self.price_min < 0 or self.price_max < self.price_min:
            raise ValueError("price range must be non-negative and ordered")
        if self.min_confidence not in CONFIDENCE:
            raise ValueError("unknown minimum confidence")
        if self.max_per_road < 1:
            raise ValueError("max_per_road must be positive")


def _address(case: Mapping[str, Any]) -> Mapping[str, Any]:
    value = case.get("address")
    return value if isinstance(value, Mapping) else {}


def _buildings(case: Mapping[str, Any]) -> list[Mapping[str, Any]]:
    value = _address(case).get("buildings")
    return [item for item in value if isinstance(item, Mapping)] if isinstance(value, list) else []


def _text_blob(case: Mapping[str, Any]) -> str:
    values = [case.get("descriptionTitle", ""), case.get("descriptionBody", "")]
    values.extend(building.get("buildingName", "") for building in _buildings(case))
    return " ".join(str(value) for value in values).casefold()


def _labels(case: Mapping[str, Any]) -> list[str]:
    return [str(building.get("buildingName", "")).casefold() for building in _buildings(case)]


def classify_two_family(case: Mapping[str, Any]) -> tuple[str, list[str]]:
    blob, labels = _text_blob(case), _labels(case)
    label_hit = next((term for label in labels for term in STRONG_TWO_FAMILY if term in label), None)
    if label_hit:
        return "confirmed", [f"BBR building label contains '{label_hit}'"]
    text_hit = next((term for term in STRONG_TWO_FAMILY if term in blob), None)
    if text_hit:
        return "likely", [f"listing text contains strong term '{text_hit}'"]
    weak = [term for term in WEAK_TWO_FAMILY if term in blob]
    if weak:
        return ("likely" if len(weak) >= 2 else "possible"), [f"two-dwelling hints: {weak}"]
    return "none", []


def classify_family_units(case: Mapping[str, Any], two_family: tuple[str, list[str]]) -> tuple[str, int | None, str, list[str]]:
    blob, labels = _text_blob(case), _labels(case)
    for unit, count, strong, weak in FAMILY_TIERS:
        label_hit = next((term for label in labels for term in strong if term in label), None)
        if label_hit:
            return unit, count, "confirmed", [f"BBR building label contains '{label_hit}'"]
        text_hit = next((term for term in strong if term in blob), None)
        if text_hit:
            return unit, count, "likely", [f"listing text contains strong term '{text_hit}'"]
        weak_hits = [term for term in weak if term in blob]
        if weak_hits:
            return unit, count, "likely" if len(weak_hits) >= 2 else "possible", [f"{unit} hints: {weak_hits}"]
    if two_family[0] != "none":
        return "two_family", 2, two_family[0], list(two_family[1])
    single = next((term for label in labels for term in SINGLE_FAMILY if term in label), None)
    if single:
        return "single_family", 1, "confirmed", [f"BBR building label contains '{single}'"]
    single = next((term for term in SINGLE_FAMILY if term in blob), None)
    if single:
        return "single_family", 1, "likely", [f"listing text contains '{single}'"]
    return "unknown", None, "none", []


def _number(value: object) -> float | None:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return None
    number = float(value)
    return number if math.isfinite(number) else None


def _address_string(case: Mapping[str, Any]) -> str:
    address = _address(case)
    line = " ".join(str(address.get(key) or "").strip() for key in ("roadName", "houseNumber") if address.get(key))
    unit = " ".join(str(address.get(key) or "").strip() for key in ("floor", "door") if address.get(key))
    locality = " ".join(str(address.get(key) or "").strip() for key in ("zipCode", "cityName") if address.get(key))
    return ", ".join(value for value in (line, unit, locality) if value)


def _municipality(case: Mapping[str, Any]) -> str | None:
    value = _address(case).get("municipality")
    if isinstance(value, Mapping):
        value = value.get("name")
    return str(value).strip() if value else (_address(case).get("municipalityName") or None)


def _source_url(case: Mapping[str, Any]) -> str | None:
    self_link = case.get("_links")
    if isinstance(self_link, Mapping):
        self_link = self_link.get("self")
        href = self_link.get("href") if isinstance(self_link, Mapping) else None
        if isinstance(href, str) and href:
            return "https://www.boligsiden.dk" + href if href.startswith("/") else href
    slug = case.get("slugAddress")
    if slug:
        return f"https://www.boligsiden.dk/adresse/{slug}"
    value = case.get("caseUrl")
    return value.strip() if isinstance(value, str) and value.strip() else None


def _preview(case: Mapping[str, Any]) -> str | None:
    image = case.get("defaultImage")
    sources = image.get("imageSources") if isinstance(image, Mapping) else None
    valid = [item for item in sources if isinstance(item, Mapping) and item.get("url")] if isinstance(sources, list) else []
    if not valid:
        return None
    def width(item: Mapping[str, Any]) -> int:
        size = item.get("size")
        value = size.get("width") if isinstance(size, Mapping) else 0
        return int(value) if isinstance(value, (int, float)) and not isinstance(value, bool) else 0
    enough = sorted((item for item in valid if width(item) >= 600), key=width)
    return str((enough[0] if enough else max(valid, key=width))["url"])


def _buildability(lot: float | None, area: float | None, confidence: str) -> dict[str, Any]:
    result = {"buildable_status": "unknown", "buildable_pct_assumed": None, "buildable_max_m2": None,
              "buildable_headroom_m2": None, "buildable_current_pct": None,
              "buildable_note": "no land/building area data"}
    if not lot or lot <= 0 or not area or area <= 0:
        return result
    pct = 40 if confidence in {"confirmed", "likely"} else 30
    maximum, headroom = lot * pct / 100, lot * pct / 100 - area
    status = "extra_house" if headroom >= 80 else "expand" if headroom >= 40 else "none"
    return {"buildable_status": status, "buildable_pct_assumed": pct, "buildable_max_m2": round(maximum),
            "buildable_headroom_m2": round(headroom), "buildable_current_pct": round(area / lot * 100, 1),
            "buildable_note": f"~{round(headroom)} m² spare at {pct}% (estimate)"}


def classify_case(case: Mapping[str, Any], config: ClassificationConfig) -> dict[str, Any]:
    case_id = str(case.get("caseID") or "").strip()
    if not case_id:
        raise ValueError("caseID is required")
    address = _address_string(case)
    if not address:
        raise ValueError(f"case {case_id} has no usable address")
    two = classify_two_family(case)
    units, units_count, units_confidence, units_reasons = classify_family_units(case, two)
    price, lot, area = _number(case.get("priceCash")), _number(case.get("lotArea")), _number(case.get("housingArea"))
    text = _text_blob(case)
    garden_term = next((term for term in GARDEN_TERMS if term in text), None)
    has_garden = bool(lot and lot > 0) or garden_term is not None
    floors = _number(case.get("numberOfFloors"))
    if floors is None:
        building_floors = [_number(item.get("numberOfFloors")) for item in _buildings(case)]
        floors = max((value for value in building_floors if value), default=None)
    preferred = floors == 1 or (floors is None and any(term in text for term in ONE_PLAN_TERMS))
    address_type = str(case.get("addressType") or case.get("_addressTypeQueried") or "")
    in_price_band = price is not None and config.price_min <= price <= config.price_max
    reason = None
    if not has_garden:
        reason = "garden_required"
    elif address_type.casefold() in APARTMENT_TYPES or _UNIT_SUFFIX.search(address):
        reason = "apartment"
    elif area is None or area < 150:
        reason = "living_area_below_150"
    elif lot is None or lot < 500:
        reason = "lot_area_below_500"
    elif config.family_units and units != config.family_units:
        reason = "family_units"
    elif not config.family_units and CONFIDENCE[two[0]] < CONFIDENCE[config.min_confidence]:
        reason = "family_confidence"
    elif not in_price_band:
        reason = "price"
    coordinates = case.get("coordinates") if isinstance(case.get("coordinates"), Mapping) else {}
    current_market = case.get("timeOnMarket")
    current_market = current_market.get("current") if isinstance(current_market, Mapping) else {}
    first_building = _buildings(case)[0] if _buildings(case) else {}
    record = {
        "id": case_id, "caseID": case_id, "external_id": case_id, "address": address,
        "municipality": _municipality(case), "city": _address(case).get("cityName"),
        "zip": _address(case).get("zipCode") or _address(case).get("zip"), "price_dkk": price,
        "price": price, "source_url": _source_url(case), "link": _source_url(case), "maegler_url": case.get("caseUrl"),
        "energy_label": str(case.get("energyLabel") or "").strip().lower() or None,
        "monthly_expense": case.get("monthlyExpense"), "garden_size_m2": int(lot) if lot is not None else None,
        "has_garden": has_garden, "garden_reason": f"lotArea={int(lot)} m²" if lot else f"text mentions '{garden_term}'",
        "number_of_floors": int(floors) if floors is not None else None, "rooms": case.get("numberOfRooms"),
        "housing_area_m2": int(area) if area is not None else None, "year_built": case.get("yearBuilt"),
        "numberOfBathrooms": case.get("numberOfBathrooms") or first_building.get("numberOfBathrooms"),
        "numberOfToilets": case.get("numberOfToilets") or first_building.get("numberOfToilets") or first_building.get("numberOfBathrooms"),
        "address_type": address_type, "days_on_market": current_market.get("days") if isinstance(current_market, Mapping) else None,
        "two_family_confidence": two[0], "two_family_reasons": two[1], "family_units": units,
        "family_units_count": units_count, "family_units_confidence": units_confidence,
        "family_units_reasons": units_reasons, "preferred": preferred, "in_price_band": in_price_band,
        "preview_image": _preview(case), "latitude": coordinates.get("lat"), "longitude": coordinates.get("lon") or coordinates.get("lng"),
        "_coordinates": dict(coordinates), "_addressID": _address(case).get("addressID"),
        "non_ai_passed": reason is None, "filter_reason": reason,
        "noise_status": "unknown", "noise_sources": {}, "commute": {"status": None, "destinations": {}},
        "sold_history": [], "sold_history_status": None,
        **_buildability(lot, area, two[0]),
    }
    # Preserve source fields used by scoring and the final raw audit projection.
    for key, value in case.items():
        record.setdefault(key, value)
    return record


def classify_cases(cases: Iterable[Mapping[str, Any]], config: ClassificationConfig) -> list[dict[str, Any]]:
    records = [classify_case(case, config) for case in cases]
    passed = [record for record in records if record["non_ai_passed"]]
    def road_key(record: Mapping[str, Any]) -> tuple[str, str]:
        match = _ROAD.match(str(record.get("address") or ""))
        road = match.group(1) if match else str(record.get("address") or "")
        return road.casefold(), str(record.get("zip") or "")
    counts = Counter(road_key(record) for record in passed)
    for record in passed:
        if counts[road_key(record)] > config.max_per_road:
            record["non_ai_passed"] = False
            record["filter_reason"] = "developer_cluster"
    return records
