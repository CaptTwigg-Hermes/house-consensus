# House Consensus durable manual scorer

`house-consensus-manual-scorer` claims exactly one row from PostgreSQL
`manual_scoring_jobs`, resolves its source, runs the scorer, then lease-fenced
finalizes the row. It is an executable worker, not a scheduler or deployment
configuration.

## Run one job

```sh
uv run --project manual_scoring --extra postgres \
  house-consensus-manual-scorer \
  --database-url "$CONSENSUS_DATABASE_URL" \
  --source-resolver your_package.manual_source:build_resolver \
  --scoring-pipeline your_package.manual_pipeline:build_pipeline
```

`CONSENSUS_DATABASE_URL` can replace `--database-url`. `--lease-seconds`
defaults to 300. The command prints one JSON status (`idle`, `completed`,
`failed`, or `lost_lease`) and intentionally processes only one job; external
orchestration decides when to invoke it again.

## Component contracts

Both arguments use strict `module:attribute` import syntax. The attribute can
be an object or a zero-argument factory that returns one. This keeps deployment
configuration explicit and avoids shelling out from the worker.

### Source resolver

The resolver must implement:

```python
class Resolver:
    def resolve(self, identity: SourceIdentity) -> dict[str, Any]: ...
```

`identity.external_id` and `identity.canonical_url` come from the claimed
PostgreSQL row. Resolve the *same* source identity; never select an arbitrary
match. Raise `AmbiguousSourceIdentity` when more than one source could match
(the worker records a terminal `source_identity_ambiguous` failure). Transient
lookup failures are recorded as retryable `source_resolution_error` failures.

### Scoring pipeline

The pipeline must implement:

```python
class Pipeline:
    def score(self, listing: dict[str, Any]) -> ScoringOutput: ...
```

It must persist any score/projection side effects idempotently before returning
`ScoringOutput`: a finite `family_fit_score` in `[0, 100]`, and nonempty dicts
for `commute_evidence` and `ai_evidence`. The durable store only owns job
completion/failure state; it does not overwrite scoring projection data.

## Lease and fencing guarantees

- Claim uses PostgreSQL `CURRENT_TIMESTAMP`, `FOR UPDATE SKIP LOCKED`, the
  oldest eligible job, and increments `LeaseFence` atomically.
- Completion and failure update only when the row has the same job ID and
  `LeaseFence`, and its database lease is still unexpired.
- A stale/re-enqueued/reclaimed worker gets `lost_lease` instead of reporting a
  false completion or failure.
- The worker never retries in process. Retry timing and terminal markers are
  durable fields in `manual_scoring_jobs`.

## Test

```sh
uv run --project manual_scoring --extra test pytest -q manual_scoring/tests
```

### Real PostgreSQL lease gate

`tests/test_postgres_store_integration.py` resets only the `public` schema of
`TEST_DATABASE_URL`, verifies that its database name contains `test`, and uses
independent real psycopg connections. It covers simultaneous claims, an actual
`FOR UPDATE` lock skipped by another claim, database-clock lease expiry,
reclaim fencing, active-lease re-enqueue identity replacement, and terminal /
retry queue selection.

For the dedicated native database described by `/opt/data/.env`, construct the
URL without printing its password and run the gate:

```sh
set -a; . /opt/data/.env; set +a
TEST_DATABASE_URL="$(python3 - <<'BUILD_DSN'
import os
from urllib.parse import quote
print(f"postgresql://{quote(os.environ['POSTGRES_USERNAME'], safe='')}:{quote(os.environ['POSTGRES_PASSWORD'], safe='')}@{os.environ['POSTGRES_IP']}:{os.environ['POSTGRES_PORT']}/house_consensus_native_test")
BUILD_DSN
)" uv run --project manual_scoring --extra 'postgres,test' pytest -q \
  manual_scoring/tests/test_postgres_store_integration.py
```

The C# companion is `ManualScoringStorePostgresTests`. The environment used for
this gate has no `dotnet` executable, so it could not execute the C# project or
apply EF migrations; the Python gate creates the exact durable queue table
shape from `AddDurableManualScoringStore` and exercises the Python adapter
against PostgreSQL. The remaining verification gap is executing that C# suite
with `TEST_DATABASE_URL` on a host with the .NET SDK installed.
