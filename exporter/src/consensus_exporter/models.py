"""Types and normalization at the houseshopping/export boundary."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any

_AI_FAILURES = {
    "failed",
    "error",
    "missing",
    "not_assessed",
    "not assessed",
    "unavailable",
}
_ARCHIVE_STATES = {"sold", "removed", "inactive", "off_market", "off market", "deleted"}


def _bool(value: Any) -> bool | None:
    """Parse booleans without Python's surprising ``bool('false')`` behaviour."""
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)) and value in (0, 1):
        return bool(value)
    if isinstance(value, str):
        normalized = value.strip().lower()
        if normalized in {"true", "1", "yes", "y"}:
            return True
        if normalized in {"false", "0", "no", "n"}:
            return False
    return None


def _first(data: dict[str, Any], *keys: str, default=None):
    for key in keys:
        value = data.get(key)
        if value is not None and value != "":
            return value
    return default


def _address(value: Any) -> str | None:
    if isinstance(value, str):
        return value
    if not isinstance(value, dict):
        return None
    road = value.get("roadName") or value.get("street") or ""
    number = value.get("houseNumber") or ""
    floor = value.get("floor") or ""
    door = value.get("door") or ""
    return " ".join(str(x) for x in (road, number, floor, door) if x).strip() or None


@dataclass(frozen=True, slots=True)
class ExportCase:
    source_id: str
    address: str | None
    municipality: str | None
    postal_code: str | None
    price_dkk: int | None
    latitude: float | None
    longitude: float | None
    source_url: str | None
    family_score: float | None
    non_ai_passed: bool
    ai_status: str
    ai_confidence: str | None
    pipeline_decision: str
    archive_reason: str | None
    raw: dict[str, Any]
    ai_evidence: dict[str, Any] | None

    @classmethod
    def from_records(
        cls, raw: dict[str, Any], match: dict[str, Any] | None
    ) -> "ExportCase":
        merged = dict(raw)
        if match:
            merged.update(match)
        source_id = str(_first(merged, "caseID", "id", "case_id", default="")).strip()
        if not source_id:
            raise ValueError("case is missing caseID/id")
        non_ai_passed = match is not None
        # An explicit non-AI decision takes precedence when an upstream stage provides it.
        explicit_non_ai = _first(merged, "non_ai_passed", "passes_non_ai_filters")
        if explicit_non_ai is not None:
            parsed_non_ai = _bool(explicit_non_ai)
            if parsed_non_ai is not None:
                non_ai_passed = parsed_non_ai

        ai_raw_status = str(
            _first(merged, "ai_status", "vision_status", default="")
        ).lower()
        ai_decision = str(
            _first(merged, "ai_decision", "vision_decision", default="")
        ).lower()
        confidence = _first(merged, "ai_confidence", "vision_confidence")
        confidence = str(confidence).lower() if confidence is not None else None
        # houseshopping's real vision result says whether the layout supports
        # multiple generations. False is therefore the rejection decision.
        multigen_layout = _bool(merged.get("vision_multigen_layout"))
        if not ai_decision and multigen_layout is not None:
            ai_decision = "pass" if multigen_layout else "reject"
        failed = ai_raw_status in _AI_FAILURES
        assessed = not failed and bool(
            ai_decision
            or ai_raw_status in {"passed", "rejected", "assessed", "complete", "ok"}
        )
        rejected = (
            assessed
            and ai_decision in {"reject", "rejected", "fail", "failed"}
            and confidence == "high"
        )
        ai_status = (
            "rejected" if rejected else ("assessed" if assessed else "not_assessed")
        )

        status = str(
            _first(merged, "caseStatus", "status", "sale_status", default="")
        ).lower()
        archive_reason = status.replace(" ", "_") if status in _ARCHIVE_STATES else None
        if not non_ai_passed:
            decision = "filter_rejected"
        elif rejected:
            decision = "ai_rejected"
        else:
            decision = "passed"

        evidence_value = merged.get("ai_evidence")
        if evidence_value is None and assessed:
            evidence_value = {
                k: merged.get(k)
                for k in (
                    "vision_summary",
                    "vision_fit_reason",
                    "vision_dwelling_evidence",
                    "vision_two_family_fit",
                    "vision_multigen_layout",
                    "two_family_reasons",
                )
                if merged.get(k) is not None
            }
        evidence = None
        if assessed or evidence_value:
            evidence = {
                "decision": ai_decision or ai_status,
                "confidence": confidence,
                "model_version": _first(
                    merged, "ai_model_version", "vision_model", default="unknown"
                ),
                "rule_version": _first(
                    merged, "ai_rule_version", "rule_version", default="unknown"
                ),
                "evidence": evidence_value or {},
            }

        coordinates = (
            merged.get("coordinates")
            if isinstance(merged.get("coordinates"), dict)
            else {}
        )
        return cls(
            source_id=source_id,
            address=_first(merged, "address")
            if isinstance(merged.get("address"), str)
            else _address(merged.get("address")),
            municipality=_first(merged, "municipality", "municipalityName"),
            postal_code=str(_first(merged, "zip", "zipCode", "postalCode", default=""))
            or None,
            price_dkk=_first(merged, "price_dkk", "cashPrice", "price"),
            latitude=_first(merged, "latitude", "lat", default=coordinates.get("lat")),
            longitude=_first(
                merged,
                "longitude",
                "lon",
                "lng",
                default=coordinates.get("lon") or coordinates.get("lng"),
            ),
            source_url=_first(merged, "link", "caseUrl", "url"),
            family_score=_first(merged, "family_score"),
            non_ai_passed=non_ai_passed,
            ai_status=ai_status,
            ai_confidence=confidence,
            pipeline_decision=decision,
            archive_reason=archive_reason,
            raw=merged,
            ai_evidence=evidence,
        )
