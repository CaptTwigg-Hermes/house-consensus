from datetime import datetime, timezone

from house_consensus_manual_scoring.worker import (
    ManualScoringRequest,
    ManualScoringWorker,
    ScoringOutput,
    SourceIdentity,
)


class Store:
    def __init__(self, request):
        self.request = request
        self.completed = []
        self.failures = []

    def claim_next_pending(self, now):
        request, self.request = self.request, None
        return request

    def record_completion(self, request, output, completed_at):
        self.completed.append((request, output, completed_at))

    def record_failure(self, request, failure, attempted_at):
        self.failures.append((request, failure, attempted_at))


class Source:
    def resolve(self, identity):
        return {"external_id": identity.external_id}


class Pipeline:
    def score(self, listing):
        return ScoringOutput(
            family_fit_score=88.5,
            commute_evidence={"minutes": 24},
            ai_evidence={"summary": "separate entrance"},
        )


def test_worker_completes_oldest_claimed_pending_request_with_required_outputs():
    requested_at = datetime(2026, 8, 7, tzinfo=timezone.utc)
    request = ManualScoringRequest(
        listing_id="listing-1",
        source_identity=SourceIdentity(external_id="manual:1", canonical_url="https://example.test/1"),
        requested_at=requested_at,
    )
    store = Store(request)

    result = ManualScoringWorker(store, Source(), Pipeline()).run_once(requested_at)

    assert result.status == "completed"
    assert store.failures == []
    assert store.completed == [(request, Pipeline().score({"external_id": "manual:1"}), requested_at)]


def test_select_next_pending_prefers_oldest_retryable_uncompleted_request():
    from house_consensus_manual_scoring.worker import select_next_pending

    now = datetime(2026, 8, 7, 12, tzinfo=timezone.utc)
    identity = SourceIdentity(external_id="manual:1", canonical_url="https://example.test/1")
    completed = ManualScoringRequest("done", identity, now.replace(hour=8), completed_at=now)
    delayed_retry = ManualScoringRequest("later", identity, now.replace(hour=7), retry_at=now.replace(hour=13))
    oldest_eligible = ManualScoringRequest("first", identity, now.replace(hour=9), retry_at=now)
    newer_eligible = ManualScoringRequest("second", identity, now.replace(hour=10))

    assert select_next_pending([newer_eligible, completed, delayed_retry, oldest_eligible], now) == oldest_eligible


def test_worker_records_retryable_error_when_source_identity_is_ambiguous():
    from house_consensus_manual_scoring.worker import AmbiguousSourceIdentity

    now = datetime(2026, 8, 7, 12, tzinfo=timezone.utc)
    request = ManualScoringRequest("listing-1", SourceIdentity("manual:1", "https://example.test/1"), now)
    store = Store(request)

    class AmbiguousSource:
        def resolve(self, identity):
            raise AmbiguousSourceIdentity("two sources matched")

    result = ManualScoringWorker(store, AmbiguousSource(), Pipeline()).run_once(now)

    assert result.status == "failed"
    assert store.completed == []
    assert store.failures[0][0] == request
    assert store.failures[0][1].code == "source_identity_ambiguous"
    assert store.failures[0][1].retry_at == now
    assert store.failures[0][2] == now


def test_worker_does_not_complete_when_required_score_or_evidence_is_missing():
    now = datetime(2026, 8, 7, 12, tzinfo=timezone.utc)
    request = ManualScoringRequest("listing-1", SourceIdentity("manual:1", "https://example.test/1"), now)
    store = Store(request)

    class IncompletePipeline:
        def score(self, listing):
            return ScoringOutput(family_fit_score=None, commute_evidence={"minutes": 24}, ai_evidence=None)

    result = ManualScoringWorker(store, Source(), IncompletePipeline()).run_once(now)

    assert result.status == "failed"
    assert store.completed == []
    assert store.failures[0][1].code == "incomplete_scoring_output"
    assert "family_fit_score" in store.failures[0][1].message
    assert "ai_evidence" in store.failures[0][1].message
    assert store.failures[0][1].retry_at == now


def test_worker_records_retryable_pipeline_error_without_completing_request():
    now = datetime(2026, 8, 7, 12, tzinfo=timezone.utc)
    request = ManualScoringRequest("listing-1", SourceIdentity("manual:1", "https://example.test/1"), now)
    store = Store(request)

    class BrokenPipeline:
        def score(self, listing):
            raise RuntimeError("executor unavailable")

    result = ManualScoringWorker(store, Source(), BrokenPipeline()).run_once(now)

    assert result.status == "failed"
    assert store.completed == []
    assert store.failures[0][1].code == "pipeline_error"
    assert store.failures[0][1].message == "executor unavailable"
    assert store.failures[0][1].retry_at == now
