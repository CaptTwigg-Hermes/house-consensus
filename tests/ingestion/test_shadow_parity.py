from __future__ import annotations

import json
import sqlite3
from pathlib import Path


def _completed_legacy_snapshot(path: Path, *, source_url: str = "https://example.test/case-42") -> None:
    with sqlite3.connect(path) as conn:
        conn.execute("create table pipeline_runs (run_id text primary key, status text, completed_at text, source_scope text, case_count integer)")
        conn.execute("create table pipeline_snapshot_items (run_id text, id text, case_payload text, match_payload text)")
        conn.execute("insert into pipeline_runs values (?, ?, ?, ?, ?)", ("completed", "complete", "2026-08-07T10:00:00Z", "tofamiliehus", 1))
        conn.execute("insert into pipeline_snapshot_items values (?, ?, ?, ?)", ("completed", "case-42", json.dumps({"caseID": "case-42", "address": {"roadName": "North Road", "houseNumber": "42", "cityName": "Copenhagen"}, "cashPrice": 2_500_000, "caseUrl": source_url}), json.dumps({"id": "case-42", "family_score": 88})))


class Cursor:
    def __init__(self) -> None:
        self.statements: list[tuple[str, tuple[object, ...] | None]] = []

    def __enter__(self) -> Cursor:
        return self

    def __exit__(self, *_: object) -> None:
        return None

    def execute(self, statement: str, parameters: tuple[object, ...] | None = None) -> None:
        self.statements.append((statement, parameters))

    def fetchall(self) -> list[tuple[object, ...]]:
        return [("case-42", "case-42", "North Road 42", "Copenhagen", 2_500_000, "https://example.test/case-42")]


class Connection:
    def __init__(self) -> None:
        self.cursor_instance = Cursor()

    def __enter__(self) -> Connection:
        return self

    def __exit__(self, *_: object) -> None:
        return None

    def cursor(self) -> Cursor:
        return self.cursor_instance


def test_shadow_parity_compares_completed_legacy_scope_to_native_projection_read_only(tmp_path: Path) -> None:
    from house_consensus_ingestion.shadow_parity import run_shadow_parity

    legacy = tmp_path / "legacy.db"
    _completed_legacy_snapshot(legacy)
    connection = Connection()

    result = run_shadow_parity(sqlite_path=legacy, source_system="house-consensus-ingestion", source_scope="tofamiliehus", connection_factory=lambda: connection)

    assert result.source_count == 1
    assert result.native_count == 1
    assert result.source_ids == ("case-42",)
    assert result.native_ids == ("case-42",)
    statements = "\n".join(statement for statement, _ in connection.cursor_instance.statements)
    assert "SET TRANSACTION READ ONLY" in statements
    assert "listing_ingestion_projections" in statements
    assert "INSERT" not in statements
    assert "UPDATE" not in statements
    assert "DELETE" not in statements



def test_shadow_parity_treats_outer_source_url_whitespace_as_canonical_equivalent(tmp_path: Path) -> None:
    from house_consensus_ingestion.shadow_parity import run_shadow_parity

    legacy = tmp_path / "legacy.db"
    _completed_legacy_snapshot(legacy, source_url="  https://example.test/case-42  ")

    result = run_shadow_parity(
        sqlite_path=legacy,
        source_system="house-consensus-ingestion",
        source_scope="tofamiliehus",
        connection_factory=Connection,
    )

    assert result.source_count == result.native_count == 1


def test_shadow_parity_reports_projection_field_differences(tmp_path: Path) -> None:
    import pytest
    from house_consensus_ingestion.shadow_parity import ShadowParityError, run_shadow_parity

    legacy = tmp_path / "legacy.db"
    _completed_legacy_snapshot(legacy)
    connection = Connection()
    connection.cursor_instance.fetchall = lambda: [("case-42", "case-42", "Wrong Road 42", "Copenhagen", 2_500_000, "https://example.test/case-42")]

    with pytest.raises(ShadowParityError, match="projection field mismatch"):
        run_shadow_parity(sqlite_path=legacy, source_system="house-consensus-ingestion", source_scope="tofamiliehus", connection_factory=lambda: connection)



def test_shadow_parity_reports_genuine_source_url_differences(tmp_path: Path) -> None:
    import pytest
    from house_consensus_ingestion.shadow_parity import ShadowParityError, run_shadow_parity

    legacy = tmp_path / "legacy.db"
    _completed_legacy_snapshot(legacy)
    connection = Connection()
    connection.cursor_instance.fetchall = lambda: [(
        "case-42",
        "case-42",
        "North Road 42",
        "Copenhagen",
        2_500_000,
        "https://example.test/other-case",
    )]

    with pytest.raises(ShadowParityError, match="projection field mismatch"):
        run_shadow_parity(
            sqlite_path=legacy,
            source_system="house-consensus-ingestion",
            source_scope="tofamiliehus",
            connection_factory=lambda: connection,
        )


def test_shadow_parity_is_checked_in_as_a_cli_command() -> None:
    import tomllib

    project = tomllib.loads((Path(__file__).resolve().parents[2] / "ingestion/pyproject.toml").read_text())
    assert project["project"]["scripts"]["house-consensus-shadow-parity"] == "house_consensus_ingestion.shadow_parity:main"
