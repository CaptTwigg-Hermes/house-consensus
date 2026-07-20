# House Consensus PostgreSQL exporter

Exports **every row in houseshopping's latest fetch scope** from the `cases` table. The smaller `matches` table only enriches those cases, so filtered/rejected cases remain available to owner Review. Legacy databases without a fetch marker retain the all-row behavior.

## Guarantees

- Transactional idempotent listing upserts keyed by the application listing's external case ID; archive comparison is isolated by source scope.
- Immutable per-run import provenance and versioned AI evidence.
- Import SQL never updates or deletes `listing_overrides`; the latest owner restore/reject decision survives refreshes.
- Missing/failed AI is `not_assessed` and cannot reject a non-AI pass. Only an explicit high-confidence AI rejection produces `ai_rejected`.
- Cases absent from a completed current-scope export, and sold/removed cases, are archived rather than deleted. Reappearing cases reactivate. No vote/history table is modified.
- Thumbnail and all discovered floor-plan assets are cached locally by content hash. Individual media failures do not abort listing export.

## Setup and run

Python 3.11+ and PostgreSQL 14+ are supported. From the repository root:

```sh
uv sync --project exporter --extra test
cp exporter/config.example.env exporter/.env  # edit values; never commit .env
set -a; . exporter/.env; set +a
uv run --project exporter house-consensus-export \
  --sqlite "$HOUSESHOPPING_DB"
```

Run the application EF migrations and `src/consensus_exporter/schema.sql` as a deployment step. Export runs do not execute DDL. Grant the runtime exporter SELECT/INSERT/UPDATE on these tables and sequences; `ensure_schema(conn)` is available to deployment tooling and isolated tests.

`CONSENSUS_SOURCE_SCOPE` identifies one complete fetch scope. Archive comparison is restricted to that scope. Use a unique `--run-id` for each refresh; retrying the same ID is safe.

## Tests

```sh
uv run --project exporter --extra test pytest -q tests/exporter
# Integration tests require a disposable PostgreSQL database:
TEST_DATABASE_URL=postgresql://postgres:test@127.0.0.1:5432/consensus_test \
  uv run --project exporter --extra test pytest -q tests/exporter/test_postgres_integration.py
```

The integration fixture drops and recreates the database's `public` schema. Never point it at a shared or production database. Runtime media (`var/media`) and `.env` files must be ignored by Git.

## Application semantics

The application can compute effective visibility without mutating pipeline facts:

- The latest `listing_overrides."Action" = 'restore'` restores a reappearing AI-rejected listing.
- The latest `listing_overrides."Action" = 'reject'` keeps a reappearing active listing manually rejected.
- `listings."ArchivedAt" IS NOT NULL` and `State = 'archived'` denote archive state while preserving listing IDs and related votes/history.
