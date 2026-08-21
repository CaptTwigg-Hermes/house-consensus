"""Concern-separated native source processing before immutable snapshot creation."""
from __future__ import annotations

from collections.abc import Iterable, Mapping, Sequence
from dataclasses import dataclass
from typing import Any, Protocol

from .classification import ClassificationConfig, classify_cases
from .scoring import family_score


class Enricher(Protocol):
    name: str
    def enrich(self, records: list[dict[str, Any]]) -> Mapping[str, Any]: ...


class RequiredEnrichmentError(RuntimeError):
    """A required stage failed, so no partial completed snapshot may be emitted."""


@dataclass(frozen=True)
class PipelineResult:
    records: tuple[dict[str, Any], ...]
    matched_count: int
    stage_outcomes: Mapping[str, Mapping[str, Any]]


class NativeCasePipeline:
    def __init__(self, *, classification: ClassificationConfig, enrichers: Sequence[Enricher] = ()) -> None:
        self._classification = classification
        self._enrichers = tuple(enrichers)

    @property
    def enrichers(self) -> tuple[Enricher, ...]:
        return self._enrichers

    def process(self, cases: Iterable[Mapping[str, Any]]) -> PipelineResult:
        records = classify_cases(cases, self._classification)
        matches = [record for record in records if record["non_ai_passed"]]
        outcomes: dict[str, Mapping[str, Any]] = {
            "classification": {"records": len(records), "matched": len(matches)}
        }
        for enricher in self._enrichers:
            try:
                outcome = dict(enricher.enrich(matches))
                if outcome.get("incomplete"):
                    raise RequiredEnrichmentError(
                        f"required enrichment {enricher.name} reported "
                        f"{outcome['incomplete']} incomplete record(s)"
                    )
                outcomes[enricher.name] = outcome
            except Exception as error:
                if isinstance(error, RequiredEnrichmentError):
                    raise
                raise RequiredEnrichmentError(f"required enrichment {enricher.name} failed: {error}") from error
        for record in matches:
            score = family_score(record)
            record["family_score"] = score["total"]
            record["family_score_breakdown"] = score
        outcomes["scoring"] = {"scored": len(matches)}
        return PipelineResult(
            records=tuple(sorted(records, key=lambda item: str(item["id"]))),
            matched_count=len(matches),
            stage_outcomes=outcomes,
        )
