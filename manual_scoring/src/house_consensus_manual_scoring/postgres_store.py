"""Synchronous PostgreSQL adapter for lease-fenced manual-scoring jobs.

The database is authoritative for time: claim and finalization predicates use
``CURRENT_TIMESTAMP`` so a worker with a skewed clock cannot steal or finalize
another worker's lease.
"""

from __future__ import annotations

from datetime import datetime, timedelta
from typing import Any, Callable, Protocol
from uuid import UUID

from .worker import ManualScoringRequest, ScoringFailure, ScoringOutput, SourceIdentity


class _Cursor(Protocol):
    rowcount: int

    def __enter__(self) -> "_Cursor": ...
    def __exit__(self, *args: object) -> bool: ...
    def execute(self, query: str, parameters: dict[str, Any]) -> None: ...
    def fetchone(self) -> tuple[Any, ...] | None: ...


class _Connection(Protocol):
    def __enter__(self) -> "_Connection": ...
    def __exit__(self, *args: object) -> bool: ...
    def cursor(self) -> _Cursor: ...


Connect = Callable[[str], _Connection]


class PostgresManualScoringStore:
    """Claims and finalizes rows in ``manual_scoring_jobs`` synchronously.

    This intentionally uses one short-lived database transaction per operation.
    It is safe to call from a cron/CLI worker; do not call it from an async event
    loop without offloading it to a thread.
    """

    def __init__(self, database_url: str, *, lease_duration: timedelta = timedelta(minutes=5), connect: Connect | None = None):
        if not database_url.strip():
            raise ValueError("a PostgreSQL database URL is required")
        if lease_duration <= timedelta():
            raise ValueError("lease_duration must be positive")
        self._database_url = database_url
        self._lease_duration = lease_duration
        self._connect = connect or _psycopg_connect

    def claim_next_pending(self, now: datetime) -> ManualScoringRequest | None:
        del now  # Database time, rather than a worker clock, decides eligibility.
        with self._connect(self._database_url) as connection, connection.cursor() as cursor:
            cursor.execute(
                """
WITH candidate AS (
    SELECT "Id"
    FROM manual_scoring_jobs
    WHERE "CompletedAt" IS NULL
      AND "TerminalFailureAt" IS NULL
      AND "NextAttemptAt" <= CURRENT_TIMESTAMP
      AND ("LeaseExpiresAt" IS NULL OR "LeaseExpiresAt" <= CURRENT_TIMESTAMP)
    ORDER BY "RequestedAt", "Id"
    FOR UPDATE SKIP LOCKED
    LIMIT 1
)
UPDATE manual_scoring_jobs AS job
SET "LeaseFence" = job."LeaseFence" + 1,
    "LeaseExpiresAt" = CURRENT_TIMESTAMP + %(lease_duration)s,
    "LastAttemptAt" = CURRENT_TIMESTAMP,
    "AttemptCount" = job."AttemptCount" + 1
FROM candidate
WHERE job."Id" = candidate."Id"
RETURNING job."Id", job."ListingId", job."SourceExternalId", job."SourceCanonicalUrl",
          job."RequestedAt", job."LeaseFence", job."LeaseExpiresAt";
""",
                {"lease_duration": self._lease_duration},
            )
            row = cursor.fetchone()
        if row is None:
            return None
        job_id, listing_id, external_id, canonical_url, requested_at, lease_fence, lease_expires_at = row
        return ManualScoringRequest(
            listing_id=str(listing_id),
            source_identity=SourceIdentity(external_id=external_id, canonical_url=canonical_url),
            requested_at=requested_at,
            job_id=job_id,
            lease_fence=lease_fence,
            lease_expires_at=lease_expires_at,
        )

    def record_completion(
        self, request: ManualScoringRequest, output: ScoringOutput | None, completed_at: datetime
    ) -> bool:
        del output  # Score persistence belongs to the scoring pipeline/projection boundary.
        return self._finalize(request, completed_at, error_code=None, error_message=None, retry_at=None, terminal=False)

    def record_failure(self, request: ManualScoringRequest, failure: ScoringFailure, attempted_at: datetime) -> bool:
        if failure.terminal == (failure.retry_at is not None):
            raise ValueError("terminal failures require no retry time; retryable failures require one")
        return self._finalize(
            request,
            attempted_at,
            error_code=failure.code,
            error_message=failure.message,
            retry_at=failure.retry_at,
            terminal=failure.terminal,
        )

    def _finalize(
        self,
        request: ManualScoringRequest,
        at: datetime,
        *,
        error_code: str | None,
        error_message: str | None,
        retry_at: datetime | None,
        terminal: bool,
    ) -> bool:
        if request.job_id is None or request.lease_fence is None:
            raise ValueError("a durable lease token is required to finalize a manual-scoring job")
        with self._connect(self._database_url) as connection, connection.cursor() as cursor:
            cursor.execute(
                """
UPDATE manual_scoring_jobs
SET "CompletedAt" = CASE WHEN %(is_completion)s THEN %(at)s ELSE "CompletedAt" END,
    "TerminalFailureAt" = CASE WHEN %(terminal)s THEN %(at)s ELSE "TerminalFailureAt" END,
    "NextAttemptAt" = CASE WHEN %(is_completion)s OR %(terminal)s THEN NULL ELSE %(retry_at)s END,
    "LeaseExpiresAt" = NULL,
    "LastErrorCode" = %(error_code)s,
    "LastErrorMessage" = %(error_message)s
WHERE "Id" = %(id)s
  AND "LeaseFence" = %(lease_fence)s
  AND "LeaseExpiresAt" > CURRENT_TIMESTAMP
  AND "CompletedAt" IS NULL
  AND "TerminalFailureAt" IS NULL;
""",
                {
                    "is_completion": error_code is None,
                    "terminal": terminal,
                    "at": at,
                    "retry_at": retry_at,
                    "error_code": error_code,
                    "error_message": error_message,
                    "id": request.job_id,
                    "lease_fence": request.lease_fence,
                },
            )
            return cursor.rowcount == 1


def _psycopg_connect(database_url: str) -> _Connection:
    try:
        import psycopg
    except ImportError as error:  # pragma: no cover - exercised only in misconfigured deployments
        raise RuntimeError("install house-consensus-manual-scoring[postgres] to use PostgreSQL") from error
    return psycopg.connect(database_url)
