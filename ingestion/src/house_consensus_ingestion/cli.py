from __future__ import annotations

import argparse
import json
from collections.abc import Mapping, Sequence
from typing import Any

from .identity import build_snapshot


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--source-scope", required=True)
    parser.add_argument("--records-json", required=True)
    arguments = parser.parse_args(argv)
    if not arguments.dry_run:
        parser.error("only --dry-run is available in this foundation")

    records = json.loads(arguments.records_json)
    if not isinstance(records, list) or not all(isinstance(record, Mapping) for record in records):
        parser.error("--records-json must be a JSON array of objects")

    snapshot = build_snapshot(source_scope=arguments.source_scope, records=records)
    print(json.dumps({
        "dry_run": True,
        "source_scope": snapshot.source_scope,
        "snapshot_count": snapshot.snapshot_count,
        "manifest_sha256": snapshot.manifest_sha256,
        "run_id": snapshot.run_id,
    }, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
