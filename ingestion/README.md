# House Consensus ingestion worker foundation

This package is the narrow foundation for a direct-PostgreSQL ingestion worker. It does not read the HouseShopping SQLite database, invoke the existing exporter, assume credentials, or contact a source endpoint. Callers inject source records and, for persistence, a PostgreSQL connection factory.

## Dry run

Run: uv run --project ingestion house-consensus-ingest --dry-run --source-scope boliga.dk --records-json JSON_ARRAY

The records-json argument must be a JSON array of objects. The command emits one JSON object with source_scope, snapshot_count, manifest_sha256, and run_id. It performs no database work. The manifest is SHA-256 over newline-separated canonical JSON source records: object-key order and input-record order do not change the identity; duplicate records remain part of the snapshot. The run ID is a deterministic UUID derived from the complete manifest SHA-256 (not a scope-prefixed truncated digest), so it is accepted by the native PostgreSQL UUID column.

## Native PostgreSQL seam

`PostgresRunWriter` receives a connection factory rather than a connection string. Its `write_started_run` method writes a `running` record directly to the application-owned `ingestion_runs` table; it neither reads SQLite nor invokes the Python exporter. Callers provide the request timestamp and can supply their own psycopg connection factory and source adapter.

The insert uses `ON CONFLICT (run_id)` only when the existing row has the same immutable provenance: `source_system`, `source_scope`, and `manifest_sha256`. A conflicting identity returns no row and raises `IngestionRunConflictError`; it is never silently accepted. An exact-provenance retry is accepted without replacing the existing run state.

## Required application-migration contract

Run the House Consensus application migrations before using the writer. The present interface requires these native PostgreSQL `ingestion_runs` columns:

| Column | Required behavior |
| --- | --- |
| `run_id uuid` primary key | deterministic worker identity and conflict target |
| `source_system text` | fixed worker source identity (`house-consensus-ingestion`) |
| `source_scope text` | caller-supplied source namespace |
| `requested_at timestamptz` | injected request timestamp |
| `started_at timestamptz` | initialized from the injected request timestamp |
| `run_status text` | initialized as `running` |
| `manifest_sha256 text` | canonical snapshot SHA-256 and immutable provenance |

The application contract also owns source snapshots and stage outcomes. This foundation does not yet capture those records, complete/reconcile a run, or migrate schema; those belong to later orchestration slices.
