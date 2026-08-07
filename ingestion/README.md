# House Consensus ingestion worker foundation

This package is the narrow foundation for a direct-PostgreSQL ingestion worker. It does not read the HouseShopping SQLite database, invoke the existing exporter, assume credentials, or contact a source endpoint. Callers inject source records and, for persistence, a PostgreSQL connection factory.

## Dry run

Run: uv run --project ingestion house-consensus-ingest --dry-run --source-system house-consensus-ingestion --source-scope boliga.dk --records-json JSON_ARRAY

The records-json argument must be a JSON array of objects. The command emits one JSON object with source_system, source_scope, snapshot_count, manifest_sha256, and run_id. It performs no database work. The manifest is SHA-256 over a canonical JSON source namespace header (`source_system` and `source_scope`) followed by newline-separated canonical JSON source records: object-key order and input-record order do not change the identity; duplicate records remain part of the snapshot. Therefore identical records from different source systems or scopes have different manifests and deterministic run IDs. The run ID is a deterministic UUID derived from the complete manifest SHA-256 (not a scope-prefixed truncated digest), so it is accepted by the native PostgreSQL UUID column.

## Native PostgreSQL seam

`PostgresRunWriter` receives a connection factory rather than a connection string. Its `write_started_run` method writes a `running` record directly to the application-owned `ingestion_runs` table; it neither reads SQLite nor invokes the Python exporter. Callers provide the request timestamp and can supply their own psycopg connection factory and source adapter.

The insert uses `ON CONFLICT (run_id)` only when the existing row has the same immutable provenance: `source_system`, `source_scope`, and `manifest_sha256`. A conflicting identity returns no row and raises `IngestionRunConflictError`; it is never silently accepted. An exact-provenance retry is accepted without replacing the existing run state.

## Required application-migration contract

Run the House Consensus application migrations before using the writer. The present interface requires these native PostgreSQL `ingestion_runs` columns:

| Column | Required behavior |
| --- | --- |
| `run_id uuid` primary key | deterministic worker identity and conflict target |
| `source_system text` | caller-supplied source-system namespace (defaults to `house-consensus-ingestion` in the CLI) |
| `source_scope text` | caller-supplied source namespace |
| `requested_at timestamptz` | injected request timestamp |
| `started_at timestamptz` | initialized from the injected request timestamp |
| `run_status text` | initialized as `running` |
| `manifest_sha256 text` | canonical snapshot SHA-256 and immutable provenance |

The native run writer captures immutable source snapshots and fetch-stage outcomes while the run is running, then transitions it exactly once to a terminal status.

## Native listing projection seam

`PostgresListingProjectionWriter.project_completed_snapshot` reads an immutable `ingestion_source_snapshots.payload` only after its parent run is `succeeded`. Payloads may be a records array or an object containing a `records` array; every record must supply `external_id` (or `id`) and a non-empty `address`. It writes only the core listing fields from that source record and records the source identity in the application-owned `listing_ingestion_projections` table added by migration `202608070003_AddNativeListingProjection`.

The source identity is `(source_system, source_scope, source_record_id)` and is unique. If an `ExternalId` is already held by a manually added listing, a listing with an owner override, an unprovenanced legacy row, or a different native source identity, the projection raises `ListingIdentityConflictError` instead of changing it. The native CLI invokes this seam only after its source run has reached succeeded; no scheduler or cron entrypoint is added.


## Native Boligsiden orchestration

The runnable native path is dry-run-first. It maps Boligsiden caseID, address, and priceCash fields into validated projection records before any write.

    uv run --project ingestion house-consensus-ingest --boligsiden --dry-run --municipality 101 --address-type villa --price-min 1000000 --price-max 3000000

Use --execute instead of --dry-run only with an explicit DATABASE_URL. Execution fetches the complete sweep, creates a running native run, stores the raw source envelope plus validated projection_records, appends its fetch outcome, makes the run terminal succeeded, and only then projects listings. A persistence error after the run starts records a failed fetch outcome and terminal failed state; projection is deliberately not scheduled by cron.
