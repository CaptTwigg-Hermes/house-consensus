from __future__ import annotations

import argparse
from datetime import UTC, datetime
import json
import os
from collections.abc import Mapping, Sequence
from typing import Any

from .boligsiden import BoligsidenFetcher, BoligsidenSourceConfig
from .identity import build_snapshot
from .orchestration import NativeIngestionOrchestrator
from .postgres import PostgresRunWriter
from .projection import PostgresListingProjectionWriter


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--execute", action="store_true")
    parser.add_argument("--boligsiden", action="store_true")
    parser.add_argument("--source-system", default="house-consensus-ingestion")
    parser.add_argument("--source-scope", default="boligsiden.dk/open-cases")
    parser.add_argument("--records-json")
    parser.add_argument("--municipality", action="append", default=[])
    parser.add_argument("--address-type", action="append", default=[])
    parser.add_argument("--price-min", type=int)
    parser.add_argument("--price-max", type=int)
    arguments = parser.parse_args(argv)
    if arguments.dry_run == arguments.execute:
        parser.error("choose exactly one of --dry-run or --execute")

    if not arguments.boligsiden:
        if arguments.records_json is None:
            parser.error("--records-json is required unless --boligsiden is selected")
        return _records_dry_run(parser, arguments)
    if arguments.records_json is not None:
        parser.error("--records-json cannot be used with --boligsiden")
    if not arguments.municipality or not arguments.address_type or arguments.price_min is None or arguments.price_max is None:
        parser.error("--boligsiden requires --municipality, --address-type, --price-min, and --price-max")

    config = BoligsidenSourceConfig(
        municipalities=tuple(arguments.municipality), address_types=tuple(arguments.address_type),
        price_min=arguments.price_min, price_max=arguments.price_max,
        source_system=arguments.source_system, source_scope=arguments.source_scope,
    )
    if arguments.execute:
        database_url = os.environ.get("DATABASE_URL")
        if not database_url:
            parser.error("DATABASE_URL is required for --execute")
        import psycopg
        factory = lambda: psycopg.connect(database_url)
        run_writer = PostgresRunWriter(factory)
        projector = PostgresListingProjectionWriter(factory)
    else:
        run_writer = _DryRunWriter()
        projector = _DryRunProjector()
    result = NativeIngestionOrchestrator(
        fetcher=BoligsidenFetcher(config), run_writer=run_writer, projector=projector,
    ).run(dry_run=arguments.dry_run, requested_at=datetime.now(UTC))
    print(json.dumps({
        "dry_run": result.dry_run, "source_system": config.source_system, "source_scope": config.source_scope,
        "snapshot_count": result.snapshot_count, "manifest_sha256": result.manifest_sha256,
        "run_id": result.run_id, "projected_count": result.projected_count,
    }, sort_keys=True))
    return 0


def _records_dry_run(parser: argparse.ArgumentParser, arguments: argparse.Namespace) -> int:
    if not arguments.dry_run:
        parser.error("--records-json supports only --dry-run; use --boligsiden --execute for native persistence")
    records = json.loads(arguments.records_json)
    if not isinstance(records, list) or not all(isinstance(record, Mapping) for record in records):
        parser.error("--records-json must be a JSON array of objects")
    snapshot = build_snapshot(source_system=arguments.source_system, source_scope=arguments.source_scope, records=records)
    print(json.dumps({"dry_run": True, "source_scope": snapshot.source_scope, "source_system": snapshot.source_system,
        "snapshot_count": snapshot.snapshot_count, "manifest_sha256": snapshot.manifest_sha256, "run_id": snapshot.run_id}, sort_keys=True))
    return 0


class _DryRunWriter:
    def __getattr__(self, name: str) -> Any:
        raise AssertionError(f"dry run must not write native ingestion data: {name}")


class _DryRunProjector:
    def project_completed_snapshot(self, **_: Any) -> int:
        raise AssertionError("dry run must not project listings")


if __name__ == "__main__":
    raise SystemExit(main())
