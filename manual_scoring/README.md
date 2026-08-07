# House Consensus manual-scoring domain

This is a deliberately small, native House Consensus domain/orchestration slice for manually added listings. It has no Houseshopping imports, subprocesses, SQLite access, or database adapter.

## Contract

- `select_next_pending` selects the oldest uncompleted request whose retry time is due.
- A `ManualScoringStore` adapter atomically claims that selected request and persists either a completion or a `ScoringFailure`. A future PostgreSQL adapter owns locking, attempt timestamps, error text/code, and retry scheduling.
- A `ListingSource` adapter resolves both `external_id` and `canonical_url`. It must raise `AmbiguousSourceIdentity` rather than choose among multiple source listings.
- A `ScoringPipeline` adapter returns all required outputs: `family_fit_score`, `commute_evidence`, and `ai_evidence`. Completion is withheld when any is absent.
- Failures are explicit: pipeline errors and incomplete outputs are retryable (`retry_at`), while source ambiguity is terminal. This worker does not spin/retry in-process; the store determines later scheduling.

`ManualScoringWorker.run_once(now)` accepts its dependencies and clock as arguments, so an application runner can wire PostgreSQL and the actual scoring pipeline later without coupling this domain to the legacy exporter or Houseshopping database.

## Test

```sh
uv run --project manual_scoring --extra test pytest -q manual_scoring/tests
```
