from datetime import datetime, timezone
from uuid import UUID

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


def test_worker_records_terminal_failure_when_source_identity_is_ambiguous():
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
    assert store.failures[0][1].terminal is True
    assert store.failures[0][1].retry_at is None
    assert store.failures[0][2] == now


def test_select_next_pending_skips_terminal_ambiguous_failure_after_persistence():
    from house_consensus_manual_scoring.worker import select_next_pending

    now = datetime(2026, 8, 7, 12, tzinfo=timezone.utc)
    ambiguous_request = ManualScoringRequest(
        "listing-1",
        SourceIdentity("manual:1", "https://example.test/1"),
        now,
        terminal_failure=True,
    )
    later_request = ManualScoringRequest(
        "listing-2",
        SourceIdentity("manual:2", "https://example.test/2"),
        now.replace(minute=1),
    )

    assert select_next_pending([ambiguous_request, later_request], now) == later_request


def test_worker_records_retryable_source_resolution_failure_for_resolver_outage():
    now = datetime(2026, 8, 7, 12, tzinfo=timezone.utc)
    request = ManualScoringRequest("listing-1", SourceIdentity("manual:1", "https://example.test/1"), now)
    store = Store(request)

    class FixedRetryPolicy:
        def retry_at(self, attempted_at, failure_code):
            assert failure_code == "source_resolution_error"
            return attempted_at.replace(minute=5)

    class OutageSource:
        def resolve(self, identity):
            raise ConnectionError("source service unavailable")

    result = ManualScoringWorker(store, OutageSource(), Pipeline(), FixedRetryPolicy()).run_once(now)

    assert result.status == "failed"
    assert store.completed == []
    assert store.failures[0][1].code == "source_resolution_error"
    assert store.failures[0][1].message == "source service unavailable"
    assert store.failures[0][1].retry_at == now.replace(minute=5)


def test_worker_rejects_non_finite_score_output():
    now = datetime(2026, 8, 7, 12, tzinfo=timezone.utc)
    request = ManualScoringRequest("listing-1", SourceIdentity("manual:1", "https://example.test/1"), now)
    store = Store(request)

    class NonFinitePipeline:
        def score(self, listing):
            return ScoringOutput(float("nan"), {"minutes": 24}, {"summary": "separate entrance"})

    result = ManualScoringWorker(store, Source(), NonFinitePipeline()).run_once(now)

    assert result.status == "failed"
    assert store.completed == []
    assert store.failures[0][1].code == "incomplete_scoring_output"
    assert "family_fit_score" in store.failures[0][1].message


def test_worker_rejects_out_of_range_score_output():
    now = datetime(2026, 8, 7, 12, tzinfo=timezone.utc)
    request = ManualScoringRequest("listing-1", SourceIdentity("manual:1", "https://example.test/1"), now)
    store = Store(request)

    class OutOfRangePipeline:
        def score(self, listing):
            return ScoringOutput(101, {"minutes": 24}, {"summary": "separate entrance"})

    result = ManualScoringWorker(store, Source(), OutOfRangePipeline()).run_once(now)

    assert result.status == "failed"
    assert store.completed == []
    assert "family_fit_score" in store.failures[0][1].message


def test_worker_rejects_blank_evidence_output():
    now = datetime(2026, 8, 7, 12, tzinfo=timezone.utc)
    request = ManualScoringRequest("listing-1", SourceIdentity("manual:1", "https://example.test/1"), now)
    store = Store(request)

    class BlankEvidencePipeline:
        def score(self, listing):
            return ScoringOutput(88.5, {}, {"summary": "separate entrance"})

    result = ManualScoringWorker(store, Source(), BlankEvidencePipeline()).run_once(now)

    assert result.status == "failed"
    assert store.completed == []
    assert "commute_evidence" in store.failures[0][1].message


def test_worker_rejects_non_dict_evidence_output():
    now = datetime(2026, 8, 7, 12, tzinfo=timezone.utc)
    request = ManualScoringRequest("listing-1", SourceIdentity("manual:1", "https://example.test/1"), now)
    store = Store(request)

    class NonDictEvidencePipeline:
        def score(self, listing):
            return ScoringOutput(88.5, "24 minutes", {"summary": "separate entrance"})

    result = ManualScoringWorker(store, Source(), NonDictEvidencePipeline()).run_once(now)

    assert result.status == "failed"
    assert store.completed == []
    assert "commute_evidence" in store.failures[0][1].message


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
    assert store.failures[0][1].retry_at > now


def test_worker_defers_pipeline_failure_so_later_queue_item_remains_selectable():
    from house_consensus_manual_scoring.worker import select_next_pending

    now = datetime(2026, 8, 7, 12, tzinfo=timezone.utc)
    request = ManualScoringRequest("listing-1", SourceIdentity("manual:1", "https://example.test/1"), now)
    later_request = ManualScoringRequest(
        "listing-2", SourceIdentity("manual:2", "https://example.test/2"), now.replace(minute=1)
    )
    store = Store(request)

    class FixedRetryPolicy:
        def retry_at(self, attempted_at, failure_code):
            assert failure_code == "pipeline_error"
            return attempted_at.replace(minute=5)

    class BrokenPipeline:
        def score(self, listing):
            raise RuntimeError("executor unavailable")

    result = ManualScoringWorker(store, Source(), BrokenPipeline(), FixedRetryPolicy()).run_once(now)

    assert result.status == "failed"
    assert store.completed == []
    assert store.failures[0][1].code == "pipeline_error"
    assert store.failures[0][1].message == "executor unavailable"
    assert store.failures[0][1].retry_at == now.replace(minute=5)
    failed_request = ManualScoringRequest(
        request.listing_id, request.source_identity, request.requested_at, retry_at=store.failures[0][1].retry_at
    )
    assert select_next_pending([failed_request, later_request], now) == later_request


def test_worker_reports_lost_lease_when_fenced_completion_is_rejected():
    now = datetime(2026, 8, 7, 12, tzinfo=timezone.utc)
    request = ManualScoringRequest(
        "listing-1",
        SourceIdentity("manual:1", "https://example.test/1"),
        now,
        job_id=UUID("00000000-0000-0000-0000-000000000001"),
        lease_fence=1,
    )

    class FencedStore(Store):
        def record_completion(self, request, output, completed_at):
            super().record_completion(request, output, completed_at)
            return False

    store = FencedStore(request)

    result = ManualScoringWorker(store, Source(), Pipeline()).run_once(now)

    assert result.status == "lost_lease"
