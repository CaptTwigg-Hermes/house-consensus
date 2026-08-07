# House Consensus ingestion worker foundation

This package is the narrow foundation for a direct-PostgreSQL ingestion worker. It does not read the HouseShopping SQLite database, invoke the existing exporter, assume credentials, or contact a source endpoint. Callers inject source records and, for persistence, a PostgreSQL connection factory.

## Dry run

Run: uv run --project ingestion house-consensus-ingest --dry-run --source-scope boliga.dk --records-json JSON_ARRAY

The records-json argument must be a JSON array of objects. The command emits one JSON object with source_scope, snapshot_count, manifest_sha256, and run_id. It performs no database work. The manifest is SHA-256 over newline-separated canonical JSON source records: object-key order and input-record order do not change the identity; duplicate records remain part of the snapshot. The run id is ingest-source_scope-first_16_manifest_hex.

## Native PostgreSQL seam

PostgresRunWriter receives a connection factory rather than a connection string. Its write_started_run method directly executes an idempotent INSERT into the PostgreSQL export_runs table, with ON CONFLICT on run_id doing nothing. This is deliberately independent of the Python exporter. A later orchestration slice can supply its own psycopg connection factory and source adapter without changing identity creation.

## Required application-migration contract

Run the House Consensus application migrations before using the writer. The present interface requires these PostgreSQL export_runs columns:

| Column | Required behavior |
| --- | --- |
| run_id text primary key | deterministic worker run identity; conflict target |
| source_scope text not null | caller-supplied source namespace |
| fetched_at timestamptz not null | injected fetch timestamp |
| snapshot_count integer not null | number of accepted source records |
| manifest_sha256 text not null | canonical snapshot SHA-256 |
| source_config_sha256 text nullable | optional canonical source-config SHA-256 |

The app schema also owns completed_at, completion_ordinal, reconciliation columns, and constraints. This foundation neither completes nor reconciles a run nor migrates schema; those belong to a later contract slice.
