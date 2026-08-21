"""Deterministic family-fit scoring ported from the former pipeline."""
from __future__ import annotations

import math
from typing import Any

WEIGHTS = {"privacy": .30, "kids_space": .20, "garden": .20, "shared_living": .15, "practical": .15}
SCORE_VERSION = "family-score-v2"


def _number(value: Any, default: float = 0.0) -> float:
    if isinstance(value, bool):
        return default
    try:
        parsed = float(value)
    except (TypeError, ValueError, OverflowError):
        return default
    return parsed if math.isfinite(parsed) else default


def _integer(value: Any, default: int = 0) -> int:
    return int(_number(value, float(default)))


def _boolean(value: Any) -> bool | None:
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)) and not isinstance(value, bool) and value in (0, 1):
        return bool(value)
    if isinstance(value, str):
        value = value.strip().casefold()
        if value in {"true", "yes", "ja", "1"}:
            return True
        if value in {"false", "no", "nej", "0"}:
            return False
    return None


def _text(value: Any) -> str:
    return value.strip().casefold() if isinstance(value, str) else ""


def _clamp(value: float) -> float:
    return max(0.0, min(100.0, value))


def _privacy(record: dict[str, Any]) -> tuple[float, list[str]]:
    points, notes = 0.0, []
    separate, kitchen = _boolean(record.get("vision_separate_entrance")), _boolean(record.get("vision_second_kitchen"))
    connection = _boolean(record.get("vision_internal_connection"))
    split = _text(record.get("vision_split_type"))
    if separate is True:
        points += 35; notes.append("separate entrance (+35)")
    if kitchen is True:
        points += 25; notes.append("second kitchen (+25)")
    independent = separate is True or kitchen is True or split in {"horizontal", "vertical", "side_by_side"}
    if connection is True:
        points -= 15; notes.append("internal connection (-15)")
    elif connection is False and independent:
        points += 8; notes.append("no internal connection (+8)")
    if split in {"horizontal", "vertical", "side_by_side"}:
        points += 15; notes.append(f"split={split} (+15)")
    ensuite = record.get("vision_en_suite_count")
    if ensuite is not None and _integer(ensuite) >= 2:
        points += 12; notes.append("two en-suites (+12)")
    elif ensuite is not None and _integer(ensuite) == 1:
        points += 5; notes.append("one en-suite (+5)")
    if _integer(record.get("vision_staircase_count")) >= 2:
        points += 8; notes.append("multiple staircases (+8)")
    return _clamp(points), notes


def _kids(record: dict[str, Any]) -> tuple[float, list[str]]:
    points, notes = 0.0, []
    rooms, area = _integer(record.get("rooms") or record.get("numberOfRooms")), _number(record.get("housing_area_m2") or record.get("housingArea"))
    room_points = 40 if rooms >= 8 else 28 if rooms >= 6 else 18 if rooms >= 5 else 10 if rooms >= 4 else 0
    area_points = 30 if area >= 220 else 22 if area >= 180 else 15 if area >= 150 else 8 if area >= 120 else 0
    points += room_points + area_points; notes.extend([f"rooms={rooms} (+{room_points})", f"area={area:g} (+{area_points})"])
    if _boolean(record.get("vision_basement_on_plan")) is True:
        points += 12; notes.append("basement (+12)")
    if _integer(record.get("number_of_floors") or record.get("numberOfFloors") or 1) >= 2:
        points += 8; notes.append("multiple floors (+8)")
    if _boolean(record.get("vision_has_terrace") or record.get("hasTerrace")) is True:
        points += 5; notes.append("terrace (+5)")
    if _integer(record.get("vision_storage_count")) > 0:
        points += 10; notes.append("storage (+10)")
    if _boolean(record.get("vision_utility_room")) is True:
        points += 5; notes.append("utility (+5)")
    return _clamp(points), notes


def _garden(record: dict[str, Any]) -> tuple[float, list[str]]:
    lot = _number(record.get("garden_size_m2") or record.get("lotArea"))
    points = 70 if lot >= 1200 else 58 if lot >= 800 else 46 if lot >= 600 else 35 if lot >= 400 else 20 if lot >= 200 else 8 if lot > 0 else 0
    notes = [f"lot={lot:g} (+{points})"]
    if record.get("garden_structure") or record.get("vision_garden_structure"):
        points += 10; notes.append("garden structure (+10)")
    if _boolean(record.get("garden_private_zones") if record.get("garden_private_zones") is not None else record.get("vision_garden_private_zones")) is True:
        points += 12; notes.append("private garden zones (+12)")
    if _boolean(record.get("vision_has_terrace") or record.get("hasTerrace")) is True:
        points += 8; notes.append("terrace (+8)")
    if lot == 0 and _boolean(record.get("has_garden")) is True:
        points += 15; notes.append("garden present (+15)")
    return _clamp(points), notes


def _shared(record: dict[str, Any]) -> tuple[float, list[str]]:
    points, notes = 0.0, []
    open_plan = _boolean(record.get("open_plan_shared") if record.get("open_plan_shared") is not None else record.get("vision_open_plan_shared"))
    dining = record.get("dining_capacity") if record.get("dining_capacity") is not None else record.get("vision_dining_capacity")
    kitchen = _text(record.get("kitchen_size") or record.get("vision_kitchen_size"))
    condition = _text(record.get("vision_condition"))
    area = _number(record.get("housing_area_m2") or record.get("housingArea"))
    if open_plan is True:
        points += 30; notes.append("open plan (+30)")
    elif open_plan is False:
        points += 5; notes.append("closed rooms (+5)")
    if isinstance(dining, (int, float)) and not isinstance(dining, bool):
        if dining >= 8: points += 20
        elif dining >= 6: points += 12
    elif _text(dining) == "large": points += 20
    elif _text(dining) == "medium": points += 12
    if kitchen in {"large", "xl", "stor", "island"}: points += 20
    elif kitchen in {"medium", "medium-large", "mellom"}: points += 12
    points += {"excellent": 10, "good": 6, "fair": 2}.get(condition, 0)
    if open_plan is None and not kitchen:
        points += 10 if area >= 200 else 6 if area >= 160 else 0
    return _clamp(points), notes or ["limited shared-living evidence"]


def _practical(record: dict[str, Any]) -> tuple[float, list[str]]:
    points, notes = 0.0, []
    parking = _integer(record.get("parking_count") or record.get("vision_parking_count"))
    if parking >= 2: points += 30; notes.append("parking >=2 (+30)")
    elif parking == 1: points += 10; notes.append("parking (+10)")
    elif _boolean(record.get("vision_garage_on_plan")) is True or _boolean(record.get("vision_garage_visible")) is True:
        points += 15; notes.append("garage (+15)")
    ground = _boolean(record.get("ground_floor_bedroom") if record.get("ground_floor_bedroom") is not None else record.get("vision_ground_floor_bedroom"))
    floors = _integer(record.get("number_of_floors") or record.get("numberOfFloors") or 1)
    if ground is True: points += 15; notes.append("ground-floor bedroom (+15)")
    elif ground is None and floors == 1: points += 10
    noise = _text(record.get("noise_status"))
    points += {"quiet": 25, "moderate": 10, "loud": -10, "noisy": -10}.get(noise, 0)
    if noise: notes.append(f"noise={noise}")
    toilets = _integer(record.get("numberOfToilets") or record.get("vision_bathroom_count"))
    if not toilets:
        address = record.get("address")
        buildings = address.get("buildings") if isinstance(address, dict) else None
        first = buildings[0] if isinstance(buildings, list) and buildings and isinstance(buildings[0], dict) else {}
        toilets = _integer(first.get("numberOfToilets") or first.get("numberOfBathrooms"))
    if toilets >= 3: points += 15
    elif toilets == 2: points += 8
    energy = _text(record.get("energy_label") or record.get("energyLabel")).replace(" ", "")
    points += {"a2020": 15, "a2015": 12, "a2010": 10, "a": 10, "b": 6, "c": 3}.get(energy, 0)
    return _clamp(points), notes


def family_score(record: dict[str, Any]) -> dict[str, Any]:
    confidence = _text(record.get("vision_confidence"))
    privacy_available = record.get("vision_run_status") == "ok" and confidence in {"medium", "high"} and any(
        record.get(key) is not None for key in ("vision_separate_entrance", "vision_second_kitchen", "vision_internal_connection", "vision_split_type", "vision_en_suite_count", "vision_staircase_count", "vision_bathroom_count")
    )
    privacy, privacy_notes = _privacy(record) if privacy_available else (None, ["privacy not assessed"])
    kids, kids_notes = _kids(record)
    garden, garden_notes = _garden(record)
    shared, shared_notes = _shared(record)
    practical, practical_notes = _practical(record)
    total = round((privacy or 0) * WEIGHTS["privacy"] + kids * WEIGHTS["kids_space"] + garden * WEIGHTS["garden"] + shared * WEIGHTS["shared_living"] + practical * WEIGHTS["practical"], 1)
    return {"total": total, "privacy": round(privacy, 1) if privacy is not None else None,
            "kids_space": round(kids, 1), "garden": round(garden, 1), "shared_living": round(shared, 1),
            "practical": round(practical, 1), "weights": {key: value * 100 for key, value in WEIGHTS.items()},
            "score_version": SCORE_VERSION, "privacy_available": privacy_available,
            "score_coverage_pct": 100.0 if privacy_available else 70.0,
            "notes": {"privacy": privacy_notes, "kids_space": kids_notes, "garden": garden_notes,
                      "shared_living": shared_notes, "practical": practical_notes}}
