from __future__ import annotations

import argparse
import os
import uuid

from .media import MediaCache
from .postgres import PostgresExporter, tombstone_listing
from .source import load_sqlite_cases


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
        "--scope", default=os.getenv("CONSENSUS_SOURCE_SCOPE", "default")
    )
    parser.add_argument("--run-id", default=None)
    parser.add_argument(
        "--ensure-schema",
        action="store_true",
        help="create exporter-owned tables before importing",
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
    cases = load_sqlite_cases(args.sqlite)
    result = PostgresExporter(
        args.database_url,
        source_scope=args.scope,
        media_cache=None if args.skip_media else MediaCache(args.media_dir),
        ensure_schema_on_export=args.ensure_schema,
    ).export(cases, run_id=args.run_id or str(uuid.uuid4()))
    print(
        f"exported={result.exported} archived={result.archived} media_cached={result.media_cached} media_errors={result.media_errors}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
