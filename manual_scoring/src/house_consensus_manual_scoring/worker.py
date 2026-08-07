"""Pure orchestration for native manual listing scoring."""

from __future__ import annotations

from dataclasses import dataclass
import math
from datetime import datetime, timedelta
from typing import Any, Protocol


@dataclass(frozen=True)
class SourceIdentity:
    external_id: str
    canonical_url: str


@dataclass(frozen=True)
class ManualScoringRequest:
    listing_id: str
    source_identity: SourceIdentity
    requested_at: datetime
    completed_at: datetime | None = None
    retry_at: datetime | None = None


def select_next_pending(
    requests: list[ManualScoringRequest], now: datetime
) -> ManualScoringRequest | None:
    eligible = [
        request
        for request in requests
        if request.completed_at is None
        and (request.retry_at is None or request.retry_at <= now)
    ]
    return min(eligible, key=lambda request: request.requested_at, default=None)


@dataclass(frozen=True)
class ScoringOutput:
    family_fit_score: float | None
    commute_evidence: dict[str, Any] | None
    ai_evidence: dict[str, Any] | None


@dataclass(frozen=True)
class ScoringFailure:
    code: str
    message: str
    retry_at: datetime | None


class AmbiguousSourceIdentity(Exception):
    pass


@dataclass(frozen=True)
class WorkerResult:
    status: str


class ManualScoringStore(Protocol):
    def claim_next_pending(self, now: datetime) -> ManualScoringRequest | None: ...
    def record_completion(self, request: ManualScoringRequest, output: ScoringOutput, completed_at: datetime) -> None: ...
    def record_failure(self, request: ManualScoringRequest, failure: ScoringFailure, attempted_at: datetime) -> None: ...


class ListingSource(Protocol):
    def resolve(self, identity: SourceIdentity) -> dict[str, Any]: ...


class ScoringPipeline(Protocol):
    def score(self, listing: dict[str, Any]) -> ScoringOutput: ...


class RetryPolicy(Protocol):
    def retry_at(self, attempted_at: datetime, failure_code: str) -> datetime: ...


@dataclass(frozen=True)
class FixedRetryPolicy:
    delay: timedelta = timedelta(minutes=5)

    def retry_at(self, attempted_at: datetime, failure_code: str) -> datetime:
        return attempted_at + self.delay


class ManualScoringWorker:
    def __init__(
        self,
        store: ManualScoringStore,
        source: ListingSource,
        pipeline: ScoringPipeline,
        retry_policy: RetryPolicy | None = None,
    ):
        self._store = store
        self._source = source
        self._pipeline = pipeline
        self._retry_policy = retry_policy or FixedRetryPolicy()

    def run_once(self, now: datetime) -> WorkerResult:
        request = self._store.claim_next_pending(now)
        if request is None:
            return WorkerResult(status="idle")
        try:
            listing = self._source.resolve(request.source_identity)
        except AmbiguousSourceIdentity as error:
            self._store.record_failure(
                request,
                ScoringFailure("source_identity_ambiguous", str(error), retry_at=None),
                now,
            )
            return WorkerResult(status="failed")
        except Exception as error:
            self._store.record_failure(
                request,
                ScoringFailure(
                    "source_resolution_error",
                    str(error),
                    retry_at=self._retry_policy.retry_at(now, "source_resolution_error"),
                ),
                now,
            )
            return WorkerResult(status="failed")
        try:
            output = self._pipeline.score(listing)
        except Exception as error:
            self._store.record_failure(
                request,
                ScoringFailure("pipeline_error", str(error), retry_at=self._retry_policy.retry_at(now, "pipeline_error")),
                now,
            )
            return WorkerResult(status="failed")
        missing = []
        score = output.family_fit_score
        if not isinstance(score, (int, float)) or isinstance(score, bool) or not math.isfinite(score) or not 0 <= score <= 100:
            missing.append("family_fit_score")
        for name, value in (
            ("commute_evidence", output.commute_evidence),
            ("ai_evidence", output.ai_evidence),
        ):
            if not isinstance(value, dict) or not value:
                missing.append(name)
        if missing:
            self._store.record_failure(
                request,
                ScoringFailure("incomplete_scoring_output", ", ".join(missing), retry_at=self._retry_policy.retry_at(now, "incomplete_scoring_output")),
                now,
            )
            return WorkerResult(status="failed")
        self._store.record_completion(request, output, now)
        return WorkerResult(status="completed")
