"""Real PostgreSQL lease/concurrency gate for the durable manual-scoring queue."""
from __future__ import annotations

import os
import threading
from datetime import datetime, timedelta, timezone
from uuid import UUID, uuid4

import pytest

psycopg = pytest.importorskip("psycopg")


@pytest.fixture(autouse=True)
def durable_manual_queue_schema() -> str:
    database_url = os.environ.get("TEST_DATABASE_URL")
    if not database_url:
        pytest.skip("requires TEST_DATABASE_URL for a dedicated PostgreSQL database")
    with psycopg.connect(database_url, autocommit=True) as connection:
        if "test" not in connection.info.dbname.lower():
            pytest.fail("TEST_DATABASE_URL must name a dedicated database containing 'test'")
        with connection.cursor() as cursor:
            cursor.execute("DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;")
            cursor.execute('''
CREATE TABLE listings ("Id" uuid PRIMARY KEY);
CREATE TABLE manual_scoring_jobs (
 "Id" uuid PRIMARY KEY, "ListingId" uuid NOT NULL UNIQUE REFERENCES listings("Id") ON DELETE RESTRICT,
 "SourceExternalId" text NOT NULL, "SourceCanonicalUrl" character varying(2048) NOT NULL,
 "RequestedAt" timestamptz NOT NULL, "NextAttemptAt" timestamptz NULL, "LastAttemptAt" timestamptz NULL,
 "AttemptCount" integer NOT NULL DEFAULT 0 CHECK ("AttemptCount" >= 0),
 "LeaseFence" bigint NOT NULL DEFAULT 0 CHECK ("LeaseFence" >= 0), "LeaseExpiresAt" timestamptz NULL,
 "CompletedAt" timestamptz NULL, "TerminalFailureAt" timestamptz NULL,
 "LastErrorCode" character varying(100) NULL, "LastErrorMessage" character varying(1000) NULL,
 CHECK ("CompletedAt" IS NULL OR "TerminalFailureAt" IS NULL)
);
CREATE INDEX "IX_manual_scoring_jobs_Claim" ON manual_scoring_jobs ("NextAttemptAt", "RequestedAt", "Id")
WHERE "CompletedAt" IS NULL AND "TerminalFailureAt" IS NULL;
''')
    return database_url


def database_url() -> str:
    return os.environ["TEST_DATABASE_URL"]


def store(*, lease_duration: timedelta = timedelta(minutes=5)):
    from house_consensus_manual_scoring.postgres_store import PostgresManualScoringStore
    return PostgresManualScoringStore(database_url(), lease_duration=lease_duration)


def add_listing() -> UUID:
    listing_id = uuid4()
    with psycopg.connect(database_url()) as connection, connection.cursor() as cursor:
        cursor.execute('INSERT INTO listings ("Id") VALUES (%s)', (listing_id,))
    return listing_id


def enqueue(listing_id: UUID, external_id: str, requested_at: datetime) -> None:
    with psycopg.connect(database_url()) as connection, connection.cursor() as cursor:
        cursor.execute('''INSERT INTO manual_scoring_jobs ("Id", "ListingId", "SourceExternalId", "SourceCanonicalUrl", "RequestedAt", "NextAttemptAt")
VALUES (%s, %s, %s, %s, %s, CURRENT_TIMESTAMP)''',
            (uuid4(), listing_id, external_id, f"https://example.test/{external_id}", requested_at))


def test_two_workers_claiming_one_row_receive_exactly_one_lease() -> None:
    listing_id = add_listing()
    enqueue(listing_id, "manual:only", datetime.now(timezone.utc))
    barrier = threading.Barrier(3)
    claims: list[object] = []
    def claim() -> None:
        barrier.wait()
        claims.append(store().claim_next_pending(datetime.now(timezone.utc)))
    workers = [threading.Thread(target=claim), threading.Thread(target=claim)]
    for worker in workers: worker.start()
    barrier.wait()
    for worker in workers:
        worker.join(timeout=10)
        assert not worker.is_alive()
    leases = [claim for claim in claims if claim is not None]
    assert len(leases) == 1
    assert leases[0].listing_id == str(listing_id)


def test_skip_locked_claims_next_row_while_oldest_row_is_locked() -> None:
    now = datetime.now(timezone.utc)
    first_id, second_id = add_listing(), add_listing()
    enqueue(first_id, "manual:first", now - timedelta(minutes=2))
    enqueue(second_id, "manual:second", now - timedelta(minutes=1))
    with psycopg.connect(database_url()) as blocker, blocker.cursor() as cursor:
        cursor.execute('SELECT "Id" FROM manual_scoring_jobs WHERE "ListingId" = %s FOR UPDATE', (first_id,))
        claim = store().claim_next_pending(now)
    assert claim is not None
    assert claim.listing_id == str(second_id)


def test_database_time_expiration_reclaims_lease_and_fences_stale_finalizers() -> None:
    from house_consensus_manual_scoring.worker import ScoringFailure
    listing_id = add_listing()
    enqueue(listing_id, "manual:fenced", datetime.now(timezone.utc))
    first = store().claim_next_pending(datetime.now(timezone.utc))
    assert first is not None
    assert store().claim_next_pending(datetime.now(timezone.utc) + timedelta(days=365)) is None
    with psycopg.connect(database_url()) as connection, connection.cursor() as cursor:
        cursor.execute('UPDATE manual_scoring_jobs SET "LeaseExpiresAt" = CURRENT_TIMESTAMP - interval \'1 second\' WHERE "Id" = %s', (first.job_id,))
    second = store().claim_next_pending(datetime.now(timezone.utc))
    assert second is not None
    assert second.lease_fence > first.lease_fence
    assert store().record_completion(first, None, datetime.now(timezone.utc)) is False
    assert store().record_failure(first, ScoringFailure("retry", "stale", datetime.now(timezone.utc) + timedelta(minutes=1)), datetime.now(timezone.utc)) is False
    assert store().record_completion(second, None, datetime.now(timezone.utc)) is True


def test_reenqueue_replaces_identity_and_fences_active_holder() -> None:
    listing_id = add_listing()
    enqueue(listing_id, "manual:old", datetime.now(timezone.utc))
    old = store().claim_next_pending(datetime.now(timezone.utc))
    assert old is not None
    with psycopg.connect(database_url()) as connection, connection.cursor() as cursor:
        cursor.execute('''UPDATE manual_scoring_jobs SET "SourceExternalId" = %s, "SourceCanonicalUrl" = %s,
"NextAttemptAt" = CURRENT_TIMESTAMP, "LeaseFence" = CASE WHEN "LeaseExpiresAt" > CURRENT_TIMESTAMP THEN "LeaseFence" + 1 ELSE "LeaseFence" END,
"LeaseExpiresAt" = NULL WHERE "ListingId" = %s''', ("manual:new", "https://example.test/manual:new", listing_id))
    assert store().record_completion(old, None, datetime.now(timezone.utc)) is False
    replacement = store().claim_next_pending(datetime.now(timezone.utc))
    assert replacement is not None
    assert replacement.lease_fence > old.lease_fence
    assert replacement.source_identity.external_id == "manual:new"
    assert replacement.source_identity.canonical_url == "https://example.test/manual:new"


def test_retry_and_terminal_failure_leave_later_work_claimable() -> None:
    from house_consensus_manual_scoring.worker import ScoringFailure
    now = datetime.now(timezone.utc)
    terminal_id, later_id = add_listing(), add_listing()
    enqueue(terminal_id, "manual:terminal", now - timedelta(minutes=2))
    enqueue(later_id, "manual:later", now - timedelta(minutes=1))
    terminal = store().claim_next_pending(now)
    assert terminal is not None and terminal.listing_id == str(terminal_id)
    assert store().record_failure(terminal, ScoringFailure("ambiguous", "two sources", None, terminal=True), now) is True
    later = store().claim_next_pending(now)
    assert later is not None and later.listing_id == str(later_id)
    assert store().record_failure(later, ScoringFailure("temporary", "retry", now + timedelta(minutes=5)), now) is True
    assert store().claim_next_pending(now + timedelta(minutes=1)) is None
    with psycopg.connect(database_url()) as connection, connection.cursor() as cursor:
        cursor.execute('UPDATE manual_scoring_jobs SET "NextAttemptAt" = CURRENT_TIMESTAMP WHERE "Id" = %s', (later.job_id,))
    retry = store().claim_next_pending(now)
    assert retry is not None and retry.job_id == later.job_id
    with psycopg.connect(database_url()) as connection, connection.cursor() as cursor:
        cursor.execute('SELECT "AttemptCount", "TerminalFailureAt", "NextAttemptAt", "LastErrorCode" FROM manual_scoring_jobs WHERE "Id" = %s', (terminal.job_id,))
        attempts, terminal_at, next_attempt_at, error_code = cursor.fetchone()
    assert attempts == 1 and terminal_at is not None and next_attempt_at is None and error_code == "ambiguous"
