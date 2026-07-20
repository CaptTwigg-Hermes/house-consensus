"""Read the complete current fetch scope from houseshopping's SQLite store."""

from __future__ import annotations
import json
import sqlite3
from pathlib import Path
from .models import ExportCase


_SCOPE_COLUMNS = ("fetch_id", "run_id", "batch_id", "scope_id", "fetch_scope", "search_id")
_TIME_COLUMNS = ("fetched_at", "fetchedAt", "fetch_time", "last_seen_at", "updated_at", "created_at")


def _columns(conn: sqlite3.Connection, table: str) -> set[str]:
    return {row[1] for row in conn.execute(f"pragma table_info({table})")}


def _current_scope(conn: sqlite3.Connection) -> tuple[str | None, object | None]:
    """Find the newest complete fetch marker, while supporting the legacy schema."""
    columns = _columns(conn, "cases")
    scope = next((name for name in _SCOPE_COLUMNS if name in columns), None)
    if scope:
        time = next((name for name in _TIME_COLUMNS if name in columns), None)
        order = f'"{time}" desc, rowid desc' if time else "rowid desc"
        row = conn.execute(
            f'select "{scope}" from cases where "{scope}" is not null order by {order} limit 1'
        ).fetchone()
        return scope, row[0] if row else None
    time = next((name for name in _TIME_COLUMNS if name in columns), None)
    if time:
        row = conn.execute(f'select max("{time}") from cases').fetchone()
        return time, row[0] if row else None
    return None, None


def load_sqlite_cases(path: str | Path) -> list[ExportCase]:
    """Return every case in the latest fetch scope; matches only enrich that set."""
    with sqlite3.connect(str(path)) as conn:
        scope_column, scope_value = _current_scope(conn)
        where, parameters = ("", ()) if scope_column is None else (f' where "{scope_column}" = ?', (scope_value,))
        match_columns = _columns(conn, "matches")
        match_where = where if scope_column in match_columns else ""
        match_parameters = parameters if match_where else ()
        matches = {
            row[0]: json.loads(row[1])
            for row in conn.execute(f"select id, payload from matches{match_where}", match_parameters)
        }
        rows = conn.execute(f"select id, payload from cases{where} order by id", parameters).fetchall()
    result = []
    for fallback_id, payload in rows:
        raw = json.loads(payload)
        raw.setdefault("caseID", fallback_id)
        result.append(ExportCase.from_records(raw, matches.get(fallback_id)))
    return result
