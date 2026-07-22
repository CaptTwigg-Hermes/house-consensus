import json
import sqlite3
from pathlib import Path
from consensus_exporter.source import load_sqlite_cases


def test_sqlite_source_returns_passes_plus_non_persisted_hard_reject_tombstones(tmp_path: Path):
    db = tmp_path / "house.db"
    conn = sqlite3.connect(db)
    conn.execute("create table cases (id text primary key, payload text)")
    conn.execute("create table matches (id text primary key, payload text)")
    conn.execute(
        "insert into cases values (?, ?)",
        ("one", json.dumps({"caseID": "one", "x": 1})),
    )
    conn.execute(
        "insert into cases values (?, ?)",
        ("two", json.dumps({"caseID": "two", "x": 2})),
    )
    conn.execute(
        "insert into matches values (?, ?)",
        ("one", json.dumps({"id": "one", "family_score": 99})),
    )
    conn.commit()
    conn.close()
    cases = load_sqlite_cases(db)
    assert [c.source_id for c in cases] == ["one", "two"]
    assert cases[0].family_score == 99
    assert cases[1].pipeline_decision == "filter_rejected"


def test_sqlite_source_only_returns_latest_fetch_scope(tmp_path: Path):
    db = tmp_path / "scoped.db"
    with sqlite3.connect(db) as conn:
        conn.execute("create table cases (id text, payload text, fetch_id text, fetched_at text)")
        conn.execute("create table matches (id text, payload text, fetch_id text)")
        conn.execute("insert into cases values (?,?,?,?)", ("stale", json.dumps({"caseID": "stale"}), "old", "2026-07-19"))
        conn.execute("insert into cases values (?,?,?,?)", ("current", json.dumps({"caseID": "current"}), "new", "2026-07-20"))
        conn.execute("insert into matches values (?,?,?)", ("stale", json.dumps({"family_score": 1}), "old"))
        conn.execute("insert into matches values (?,?,?)", ("current", json.dumps({"family_score": 99}), "new"))
    cases = load_sqlite_cases(db)
    assert [(case.source_id, case.family_score) for case in cases] == [("current", 99)]
