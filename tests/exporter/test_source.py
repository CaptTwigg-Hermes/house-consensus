import json
import sqlite3
from pathlib import Path

from consensus_exporter.source import load_sqlite_cases


def test_sqlite_source_rejects_legacy_accumulated_tables(tmp_path: Path):
    db = tmp_path / "legacy.db"
    with sqlite3.connect(db) as conn:
        conn.execute("create table cases (id text primary key, payload text)")
        conn.execute("create table matches (id text primary key, payload text)")
    with __import__("pytest").raises(RuntimeError, match="explicit completed snapshot"):
        load_sqlite_cases(db)


def test_sqlite_source_reads_only_an_explicit_completed_snapshot(tmp_path: Path):
    db = tmp_path / "snapshots.db"
    with sqlite3.connect(db) as conn:
        conn.execute(
            "create table pipeline_runs (run_id text primary key, status text, completed_at text, source_scope text, case_count integer)"
        )
        conn.execute(
            "create table pipeline_snapshot_items (run_id text, id text, case_payload text, match_payload text)"
        )
        conn.execute(
            "insert into pipeline_runs values ('good','complete','2026-07-27T08:00:00Z','tofamiliehus',1)"
        )
        conn.execute(
            "insert into pipeline_runs values ('bad','failed',null,'tofamiliehus',0)"
        )
        conn.execute(
            "insert into pipeline_snapshot_items values (?,?,?,?)",
            (
                "good",
                "one",
                json.dumps({"caseID": "one"}),
                json.dumps({"id": "one", "family_score": 88}),
            ),
        )
        conn.execute(
            "insert into pipeline_snapshot_items values (?,?,?,?)",
            (
                "bad",
                "two",
                json.dumps({"caseID": "two"}),
                json.dumps({"id": "two", "family_score": 99}),
            ),
        )

    cases = load_sqlite_cases(db, snapshot_run_id="good")
    assert [(case.source_id, case.family_score) for case in cases] == [("one", 88)]
    with __import__("pytest").raises(
        RuntimeError, match="not the latest completed snapshot"
    ):
        load_sqlite_cases(db, snapshot_run_id="bad")


def test_sqlite_source_binds_scope_and_declared_case_count(tmp_path: Path):
    db = tmp_path / "scoped.db"
    with sqlite3.connect(db) as conn:
        conn.execute(
            "create table pipeline_runs (run_id text primary key, status text, completed_at text, source_scope text, case_count integer)"
        )
        conn.execute(
            "create table pipeline_snapshot_items (run_id text, id text, case_payload text, match_payload text)"
        )
        conn.execute(
            "insert into pipeline_runs values ('other','complete','2026-07-27T09:00:00Z','other-scope',1)"
        )
        conn.execute(
            "insert into pipeline_runs values ('target','complete','2026-07-27T08:00:00Z','tofamiliehus',2)"
        )
        conn.execute(
            "insert into pipeline_snapshot_items values ('target','one',?,null)",
            (json.dumps({"caseID": "one"}),),
        )
    with __import__("pytest").raises(
        RuntimeError, match="contains 1 of 2 declared cases"
    ):
        load_sqlite_cases(db, source_scope="tofamiliehus")
    with __import__("pytest").raises(
        RuntimeError, match="no completed source snapshot"
    ):
        load_sqlite_cases(db, source_scope="missing-scope")


def test_sqlite_source_rejects_duplicate_snapshot_and_normalized_ids(tmp_path: Path):
    import pytest

    def make_db(name, rows):
        db = tmp_path / name
        with sqlite3.connect(db) as conn:
            conn.execute(
                "create table pipeline_runs (run_id text, status text, completed_at text, source_scope text, case_count integer)"
            )
            conn.execute(
                "create table pipeline_snapshot_items (run_id text, id text, case_payload text, match_payload text)"
            )
            conn.execute(
                "insert into pipeline_runs values ('run','complete','2026-07-27T08:00:00Z','tofamiliehus',2)"
            )
            conn.executemany(
                "insert into pipeline_snapshot_items values ('run',?,?,null)", rows
            )
        return db

    duplicate_rows = make_db(
        "duplicate-rows.db",
        [
            ("same", json.dumps({"caseID": "same"})),
            ("same", json.dumps({"caseID": "same"})),
        ],
    )
    with pytest.raises(RuntimeError, match="unique IDs"):
        load_sqlite_cases(duplicate_rows)

    duplicate_normalized = make_db(
        "duplicate-normalized.db",
        [
            ("row-1", json.dumps({"caseID": "same"})),
            ("row-2", json.dumps({"caseID": "same"})),
        ],
    )
    with pytest.raises(RuntimeError, match="duplicate source IDs"):
        load_sqlite_cases(duplicate_normalized)
