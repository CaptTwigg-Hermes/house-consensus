from __future__ import annotations
import argparse
import os
import uuid
from .media import MediaCache
from .postgres import PostgresExporter
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
    args = parser.parse_args()
    if not args.database_url:
        parser.error("--database-url or CONSENSUS_DATABASE_URL is required")
    cases = load_sqlite_cases(args.sqlite)
    result = PostgresExporter(
        args.database_url,
        source_scope=args.scope,
        media_cache=MediaCache(args.media_dir),
    ).export(cases, run_id=args.run_id or str(uuid.uuid4()))
    print(
        f"exported={result.exported} archived={result.archived} media_cached={result.media_cached} media_errors={result.media_errors}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
