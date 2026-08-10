"""Conservative, deterministic asbestos-roof evidence classification."""

from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
import json
import re
from typing import Any

RULE_VERSION = "asbestos-roof-v1"

_STRUCTURED_KEYS = ("roof", "tag", "bbr", "material")
_TEXT_KEYS = ("description", "beskrivelse", "remark", "note", "text", "tekst")
_DOCUMENT_KEYS = ("document", "dokument", "report", "rapport", "tilstand", "pdf")
_IMAGE_KEYS = ("vision", "image", "photo", "billede", "roof_evidence")
_IMAGE_EVIDENCE_KEYS = ("alt", "caption", "description", "analysis", "vision", "evidence", "label", "text", "title")
_EXPLICIT = re.compile(r"\b(?:asbest(?:holdig(?:e|t)?)?|asbestos)\b", re.IGNORECASE)
_AMBIGUOUS = re.compile(r"\b(?:eternit|fiber\s*cement|fibercement)", re.IGNORECASE)
_NEGATED = re.compile(
    r"\b(?:asbestfri(?:t|e)?|uden\s+asbest|ikke\s+asbest|asbestos[- ]free|without\s+asbestos|no\s+asbestos)\b",
    re.IGNORECASE,
)


@dataclass(frozen=True, slots=True)
class AsbestosRoofAssessment:
    status: str
    confidence: float | None
    primary_source: str | None
    evidence: tuple[dict[str, str], ...]
    rule_version: str
    source_fingerprint: str


def assess_asbestos_roof(record: dict[str, Any]) -> AsbestosRoofAssessment:
    candidates = _candidates(record)
    fingerprint = sha256(
        json.dumps(record, ensure_ascii=False, sort_keys=True, separators=(",", ":"), default=str).encode()
    ).hexdigest()

    findings: list[dict[str, str]] = []
    negated_findings: list[dict[str, str]] = []
    status = "unknown"
    confidence: float | None = None
    primary_source: str | None = None

    for source in ("structured", "text", "document", "image"):
        for candidate_source, path, value in candidates:
            if candidate_source != source:
                continue
            if _NEGATED.search(value):
                negated_findings.append({"source": source, "path": path, "excerpt": _excerpt(value)})
                continue
            clean = value
            ambiguous_bbr = source == "structured" and "herunder asbest" in value.casefold()
            if _EXPLICIT.search(clean) and not ambiguous_bbr:
                candidate_status = "possible" if source == "image" else "likely"
            elif _AMBIGUOUS.search(clean) or ambiguous_bbr:
                candidate_status = "possible"
            else:
                continue
            findings.append({"source": source, "path": path, "excerpt": _excerpt(value)})
            if _rank(candidate_status) > _rank(status):
                status = candidate_status
                primary_source = source
                confidence = 0.95 if status == "likely" else (0.55 if source == "image" else 0.7)

    if findings and negated_findings:
        findings.extend(negated_findings)
        findings.append({"source": "conflict", "path": "", "excerpt": "Positive and negative asbestos evidence conflict."})
        status = "unknown"
        confidence = None
        primary_source = None
    elif not findings and candidates:
        status = "no_indication"
        primary_source = candidates[0][0]
        confidence = 0.8
        findings.append({"source": primary_source, "path": candidates[0][1], "excerpt": _excerpt(candidates[0][2])})
    elif not findings:
        findings.append({"source": "none", "path": "", "excerpt": "No roof-relevant evidence was available."})

    return AsbestosRoofAssessment(
        status=status,
        confidence=confidence,
        primary_source=primary_source,
        evidence=tuple(findings),
        rule_version=RULE_VERSION,
        source_fingerprint=fingerprint,
    )


def _candidates(record: dict[str, Any]) -> list[tuple[str, str, str]]:
    values: list[tuple[str, str, str]] = []

    def visit(value: Any, path: tuple[str, ...]) -> None:
        if isinstance(value, dict):
            for key, child in value.items():
                visit(child, (*path, str(key)))
            return
        if isinstance(value, (list, tuple)):
            for index, child in enumerate(value):
                visit(child, (*path, str(index)))
            return
        if not isinstance(value, str) or not value.strip():
            return
        joined = ".".join(path).casefold()
        source = _source(joined)
        if source == "image" and not any(key in path[-1].casefold() for key in _IMAGE_EVIDENCE_KEYS):
            return
        if source is not None:
            values.append((source, ".".join(path), " ".join(value.split())))

    visit(record, ())
    return sorted(values, key=lambda item: (("structured", "text", "document", "image").index(item[0]), item[1]))


def _source(path: str) -> str | None:
    if any(key in path for key in _DOCUMENT_KEYS):
        return "document"
    if any(key in path for key in _IMAGE_KEYS):
        return "image"
    if any(key in path for key in _STRUCTURED_KEYS):
        return "structured"
    if any(key in path for key in _TEXT_KEYS):
        return "text"
    return None


def _rank(status: str) -> int:
    return {"unknown": 0, "no_indication": 1, "possible": 2, "likely": 3}[status]


def _excerpt(value: str) -> str:
    return value if len(value) <= 240 else f"{value[:237]}..."
