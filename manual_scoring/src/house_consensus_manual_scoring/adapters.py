"""Repository-owned production factories for the manual-scoring worker."""
from __future__ import annotations

import os
from typing import Any
from urllib.parse import urlparse

from house_consensus_ingestion.adapters import (
    BoligsidenCaseClient,
    ProductionAdapterConfig,
    SourceIdentityError,
    build_production_scorer,
)

from .worker import AmbiguousSourceIdentity, ScoringOutput, SourceIdentity


class BoligsidenSourceResolver:
    """Resolve the lease's exact Boligsiden case ID without fuzzy fallback."""

    def __init__(self, client: BoligsidenCaseClient) -> None:
        self._client = client

    def resolve(self, identity: SourceIdentity) -> dict[str, Any]:
        case_id = identity.external_id.strip()
        canonical_url = identity.canonical_url.strip()
        if not case_id or len(case_id) > 128:
            raise AmbiguousSourceIdentity("claimed source has no valid real case ID")
        parsed = urlparse(canonical_url)
        if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password:
            raise AmbiguousSourceIdentity("claimed source has no valid canonical URL")
        try:
            listing = self._client.fetch_case(case_id)
        except SourceIdentityError as error:
            raise AmbiguousSourceIdentity(f"Boligsiden case identity is invalid: {error}") from error
        if str(listing.get("caseID") or "").strip() != case_id:
            raise AmbiguousSourceIdentity("Boligsiden case identity changed during resolution")
        listing["external_id"] = case_id
        listing["canonical_url"] = canonical_url
        return listing


class NativeManualScoringPipeline:
    """Adapt the shared native enricher/scorer to the durable worker contract."""

    def __init__(self, scorer: Any) -> None:
        self._scorer = scorer

    def score(self, listing: dict[str, Any]) -> ScoringOutput:
        breakdown = self._scorer.score(listing)
        commute = listing.get("commute")
        if not isinstance(commute, dict) or commute.get("status") != "ok":
            raise RuntimeError("native commute enrichment did not produce complete evidence")
        vision = {
            key: value
            for key, value in listing.items()
            if key.startswith("vision_") and key not in {"vision_image_urls"}
        }
        ai_evidence = {
            "source_case_id": str(listing.get("caseID") or listing.get("external_id") or ""),
            "score_breakdown": breakdown,
            "vision": vision,
            "noise": {
                "status": listing.get("noise_status"),
                "sources": listing.get("noise_sources") if isinstance(listing.get("noise_sources"), dict) else {},
            },
            "sold_history": listing.get("sold_history") if isinstance(listing.get("sold_history"), list) else [],
            "sold_history_status": listing.get("sold_history_status"),
        }
        return ScoringOutput(
            family_fit_score=breakdown.get("total"),
            commute_evidence=commute,
            ai_evidence=ai_evidence,
        )


def build_source_resolver() -> BoligsidenSourceResolver:
    """Factory for ``house_consensus_manual_scoring.adapters:build_source_resolver``."""
    return BoligsidenSourceResolver(
        BoligsidenCaseClient(
            endpoint=os.environ.get(
                "CONSENSUS_BOLIGSIDEN_CASES_ENDPOINT",
                "https://api.boligsiden.dk/cases",
            ),
            timeout_seconds=_env_float("CONSENSUS_BOLIGSIDEN_TIMEOUT_SECONDS", 20),
        )
    )


def build_scoring_pipeline() -> NativeManualScoringPipeline:
    """Factory for ``house_consensus_manual_scoring.adapters:build_scoring_pipeline``."""
    config = ProductionAdapterConfig.from_env()
    return NativeManualScoringPipeline(build_production_scorer(config))


def _env_float(name: str, default: float) -> float:
    raw = os.environ.get(name)
    try:
        return default if raw is None else float(raw)
    except ValueError as error:
        raise ValueError(f"{name} must be numeric") from error
