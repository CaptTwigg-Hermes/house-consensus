from datetime import datetime, timedelta, timezone
from uuid import UUID

import pytest


def test_claim_next_pending_returns_a_lease_fenced_request_from_durable_postgres_row():
    from house_consensus_manual_scoring.postgres_store import PostgresManualScoringStore

    now = datetime(2026, 8, 7, 12, tzinfo=timezone.utc)
    job_id = UUID("00000000-0000-0000-0000-000000000001")
    listing_id = UUID("00000000-0000-0000-0000-000000000002")
    connection = FakeConnection(
        rows=[(job_id, listing_id, "manual:1", "https://example.test/1", now, 7, now + timedelta(minutes=5))]
    )

    request = PostgresManualScoringStore(
        "postgresql://example.test/db", lease_duration=timedelta(minutes=5), connect=lambda _: connection
    ).claim_next_pending(now)

    assert request is not None
    assert request.job_id == job_id
    assert request.listing_id == str(listing_id)
    assert request.source_identity.external_id == "manual:1"
    assert request.lease_fence == 7
    assert 'FOR UPDATE SKIP LOCKED' in connection.executed[0][0]
    assert 'CURRENT_TIMESTAMP' in connection.executed[0][0]
    assert connection.executed[0][1]["lease_duration"] == timedelta(minutes=5)


def test_record_completion_uses_job_id_fence_and_unexpired_database_lease():
    from house_consensus_manual_scoring.postgres_store import PostgresManualScoringStore
    from house_consensus_manual_scoring.worker import ManualScoringRequest, ScoringOutput, SourceIdentity

    now = datetime(2026, 8, 7, 12, tzinfo=timezone.utc)
    request = ManualScoringRequest(
        listing_id="00000000-0000-0000-0000-000000000002",
        source_identity=SourceIdentity("manual:1", "https://example.test/1"),
        requested_at=now,
        job_id=UUID("00000000-0000-0000-0000-000000000001"),
        lease_fence=7,
    )
    connection = FakeConnection(rowcount=1)

    accepted = PostgresManualScoringStore("postgresql://example.test/db", connect=lambda _: connection).record_completion(
        request, ScoringOutput(87.5, {"minutes": 31}, {"model": "scorer-v1"}), now
    )

    assert accepted is True
    sql, parameters = connection.executed[0]
    assert 'WITH finalized_job AS' in sql
    assert 'UPDATE listings AS listing' in sql
    assert '"LeaseFence" = %(lease_fence)s' in sql
    assert '"LeaseExpiresAt" > CURRENT_TIMESTAMP' in sql
    assert parameters["id"] == request.job_id
    assert parameters["lease_fence"] == 7
    assert parameters["family_fit_score"] == 87.5
    assert parameters["commute_json"] == '{"minutes":31}'
    assert parameters["ai_evidence_json"] == '{"model":"scorer-v1"}'


def test_record_failure_rejects_request_without_durable_lease_token():
    from house_consensus_manual_scoring.postgres_store import PostgresManualScoringStore
    from house_consensus_manual_scoring.worker import ManualScoringRequest, ScoringFailure, SourceIdentity

    now = datetime(2026, 8, 7, 12, tzinfo=timezone.utc)
    request = ManualScoringRequest("listing-1", SourceIdentity("manual:1", "https://example.test/1"), now)

    with pytest.raises(ValueError, match="durable lease"):
        PostgresManualScoringStore("postgresql://example.test/db", connect=lambda _: FakeConnection()).record_failure(
            request, ScoringFailure("temporary", "retry", now + timedelta(minutes=5)), now
        )


class FakeCursor:
    def __init__(self, connection):
        self.connection = connection
        self.rowcount = connection.rowcount

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return False

    def execute(self, sql, parameters=None):
        self.connection.executed.append((sql, parameters or {}))

    def fetchone(self):
        return self.connection.rows.pop(0) if self.connection.rows else None


class FakeConnection:
    def __init__(self, rows=None, rowcount=0):
        self.rows = list(rows or [])
        self.rowcount = rowcount
        self.executed = []

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return False

    def cursor(self):
        return FakeCursor(self)
