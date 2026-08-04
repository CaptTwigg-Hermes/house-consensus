"""Read immutable completed snapshots from houseshopping SQLite."""

from __future__ import annotations

import json
import os
import re
import sqlite3
from datetime import datetime, timezone
from pathlib import Path

from .models import ExportCase

_REQUIRED_TABLES = {"pipeline_runs", "pipeline_snapshot_items"}


def _tables(conn: sqlite3.Connection) -> set[str]:
    return {
        row[0]
        for row in conn.execute("select name from sqlite_master where type='table'")
    }


def _columns(conn: sqlite3.Connection, table: str) -> set[str]:
    return {row[1] for row in conn.execute(f"pragma table_info({table})")}


def load_sqlite_snapshot(
    path: str | Path,
    *,
    snapshot_run_id: str | None = None,
    source_scope: str = "tofamiliehus",
) -> tuple[list[ExportCase], str, datetime, str]:
    with sqlite3.connect(str(path)) as conn:
        if not _REQUIRED_TABLES.issubset(_tables(conn)):
            raise RuntimeError("explicit completed snapshot tables are required")
        run_columns = _columns(conn, "pipeline_runs")
        required_run_columns = {"source_scope", "case_count", "source_config_sha256"}
        if not required_run_columns.issubset(run_columns):
            raise RuntimeError("completed snapshot scope, counts, and source configuration identity are required")
        latest = conn.execute(
            """select run_id,completed_at,case_count,source_config_sha256 from pipeline_runs
            where status='complete' and source_scope=?
            order by completed_at desc,run_id desc limit 1""",
            (source_scope,),
        ).fetchone()
        if latest is None:
            raise RuntimeError(
                f"no completed source snapshot is available for scope {source_scope!r}"
            )
        requested = snapshot_run_id or os.getenv("HOUSESHOPPING_SNAPSHOT_RUN_ID")
        run_id = requested or latest[0]
        if run_id != latest[0]:
            raise RuntimeError(
                f"snapshot {run_id!r} is not the latest completed snapshot {latest[0]!r}"
            )
        if not latest[1]:
            raise RuntimeError(f"completed snapshot {run_id!r} has no completion time")
        if not isinstance(latest[3], str) or re.fullmatch(r"[0-9a-f]{64}", latest[3]) is None:
            raise RuntimeError(
                f"completed snapshot {run_id!r} has no valid source configuration identity"
            )
        completed_at = datetime.fromisoformat(str(latest[1]).replace("Z", "+00:00"))
        if completed_at.tzinfo is None:
            completed_at = completed_at.replace(tzinfo=timezone.utc)
        snapshot_columns = _columns(conn, "pipeline_snapshot_items")
        first_seen = "first_seen_at" if "first_seen_at" in snapshot_columns else "null"
        rows = conn.execute(
            f"""select id,case_payload,match_payload,{first_seen}
            from pipeline_snapshot_items where run_id=? order by id""",
            (run_id,),
        ).fetchall()
        if not rows:
            raise RuntimeError(f"completed snapshot {run_id!r} is unexpectedly empty")
        if len(rows) != latest[2]:
            raise RuntimeError(
                f"completed snapshot {run_id!r} contains {len(rows)} of {latest[2]} declared cases"
            )
        snapshot_ids = [str(row[0]) for row in rows]
        if len(set(snapshot_ids)) != latest[2]:
            raise RuntimeError(
                f"completed snapshot {run_id!r} does not contain {latest[2]} unique IDs"
            )
    result = []
    for fallback_id, case_payload, match_payload, first_seen_at in rows:
        raw = json.loads(case_payload)
        raw.setdefault("caseID", fallback_id)
        if first_seen_at:
            raw["_source_first_seen_at"] = first_seen_at
        result.append(
            ExportCase.from_records(
                raw, json.loads(match_payload) if match_payload else None
            )
        )
    source_ids = [case.source_id for case in result]
    if len(set(source_ids)) != len(result):
        raise RuntimeError(
            f"completed snapshot {run_id!r} normalizes to duplicate source IDs"
        )
    return result, run_id, completed_at.astimezone(timezone.utc), latest[3]


def load_sqlite_cases(
    path: str | Path,
    *,
    snapshot_run_id: str | None = None,
    source_scope: str = "tofamiliehus",
) -> list[ExportCase]:
    return load_sqlite_snapshot(
        path, snapshot_run_id=snapshot_run_id, source_scope=source_scope
    )[0]
