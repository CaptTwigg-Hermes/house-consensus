from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
import json
from collections.abc import Mapping, Sequence
from typing import Any


@dataclass(frozen=True)
class RunSnapshot:
    source_scope: str
    snapshot_count: int
    manifest_sha256: str
    run_id: str


def build_snapshot(*, source_scope: str, records: Sequence[Mapping[str, Any]]) -> RunSnapshot:
    canonical_records = sorted(
        json.dumps(record, ensure_ascii=False, separators=(",", ":"), sort_keys=True)
        for record in records
    )
    manifest_sha256 = sha256("\n".join(canonical_records).encode()).hexdigest()
    return RunSnapshot(
        source_scope=source_scope,
        snapshot_count=len(canonical_records),
        manifest_sha256=manifest_sha256,
        run_id=f"ingest-{source_scope}-{manifest_sha256[:16]}",
    )
