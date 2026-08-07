from __future__ import annotations

from dataclasses import dataclass
from hashlib import sha256
import json
from uuid import UUID
from collections.abc import Mapping, Sequence
from typing import Any


@dataclass(frozen=True)
class RunSnapshot:
    source_system: str
    source_scope: str
    snapshot_count: int
    manifest_sha256: str
    run_id: str


def build_snapshot(
    *,
    source_system: str = "house-consensus-ingestion",
    source_scope: str,
    records: Sequence[Mapping[str, Any]],
) -> RunSnapshot:
    canonical_records = sorted(
        json.dumps(record, ensure_ascii=False, separators=(",", ":"), sort_keys=True)
        for record in records
    )
    canonical_manifest = [
        json.dumps(
            {"source_scope": source_scope, "source_system": source_system},
            ensure_ascii=False,
            separators=(",", ":"),
            sort_keys=True,
        ),
        *canonical_records,
    ]
    manifest_sha256 = sha256("\n".join(canonical_manifest).encode()).hexdigest()
    return RunSnapshot(
        source_system=source_system,
        source_scope=source_scope,
        snapshot_count=len(canonical_records),
        manifest_sha256=manifest_sha256,
        run_id=str(UUID(bytes=sha256(manifest_sha256.encode()).digest()[:16], version=5)),
    )
