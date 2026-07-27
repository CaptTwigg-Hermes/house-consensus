from __future__ import annotations

import argparse
import os
from datetime import datetime, timedelta, timezone

from .media import MediaCache
from .postgres import PostgresExporter, tombstone_listing
from .source import load_sqlite_snapshot


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Export every fetched houseshopping case"
    )
    parser.add_argument(
        "--sqlite",
        default=os.getenv(
            "HOUSESHOPPING_DB", "/workspace/houseshopping/state/house.db"
        ),
    )
    parser.add_argument("--database-url", default=os.getenv("CONSENSUS_DATABASE_URL"))
    parser.add_argument(
        "--media-dir", default=os.getenv("CONSENSUS_MEDIA_DIR", "./var/media")
    )
    parser.add_argument(
        "--scope", default=os.getenv("CONSENSUS_SOURCE_SCOPE", "tofamiliehus")
    )
    parser.add_argument("--run-id", default=None)
    parser.add_argument(
        "--ensure-schema",
        action="store_true",
        help="create exporter-owned tables before importing",
    )
    parser.add_argument(
        "--dry-run", action="store_true", help="calculate changes and roll back all DML"
    )
    parser.add_argument(
        "--skip-media",
        action="store_true",
        help="skip optional media downloads",
    )
    parser.add_argument(
        "--tombstone-external-id",
        help="record a verified-delisted external ID and archive its listing",
    )
    parser.add_argument("--tombstone-source-url")
    parser.add_argument("--verification-method", default="http_404")
    args = parser.parse_args()
    if not args.database_url:
        parser.error("--database-url or CONSENSUS_DATABASE_URL is required")
    if args.tombstone_external_id:
        tombstone_listing(
            args.database_url,
            external_id=args.tombstone_external_id,
            source_url=args.tombstone_source_url,
            verification_method=args.verification_method,
        )
        print(f"tombstoned={args.tombstone_external_id}")
        return 0
    cases, snapshot_run_id, snapshot_completed_at = load_sqlite_snapshot(
        args.sqlite, source_scope=args.scope
    )
    now = datetime.now(timezone.utc)
    if snapshot_completed_at > now + timedelta(minutes=5):
        raise RuntimeError("completed snapshot timestamp is unexpectedly in the future")
    if now - snapshot_completed_at > timedelta(hours=36):
        raise RuntimeError(
            "latest completed snapshot is stale; refusing reconciliation"
        )
    if args.run_id and args.run_id != snapshot_run_id:
        raise RuntimeError("--run-id must equal the immutable source snapshot ID")
    exporter = PostgresExporter(
        args.database_url,
        source_scope=args.scope,
        media_cache=None
        if args.skip_media or args.dry_run
        else MediaCache(args.media_dir),
        ensure_schema_on_export=args.ensure_schema,
    )
    export_kwargs = {
        "run_id": snapshot_run_id,
        "fetched_at": snapshot_completed_at,
    }
    if args.dry_run:
        export_kwargs["dry_run"] = True
    result = exporter.export(cases, **export_kwargs)
    print(
        f"dry_run={args.dry_run} exported={result.exported} inserted={getattr(result, 'inserted', 0)} "
        f"updated={getattr(result, 'updated', 0)} reactivated={getattr(result, 'reactivated', 0)} "
        f"archived={result.archived} archival_blocked={getattr(result, 'archival_blocked', 0)} "
        f"active_total={getattr(result, 'active_total', 0)} geometry_covered={getattr(result, 'geometry_covered', 0)} "
        f"media_cached={result.media_cached} media_errors={result.media_errors}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
