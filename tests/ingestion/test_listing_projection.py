from __future__ import annotations

from datetime import UTC, datetime

import pytest


class Cursor:
    def __init__(self, results: list[tuple[object, ...] | None]) -> None:
        self.executed: list[tuple[str, tuple[object, ...]]] = []
        self._results = iter(results)

    def __enter__(self) -> Cursor:
        return self

    def __exit__(self, *_: object) -> None:
        return None

    def execute(self, statement: str, parameters: tuple[object, ...]) -> None:
        self.executed.append((statement, parameters))

    def fetchone(self) -> tuple[object, ...] | None:
        return next(self._results)


class Connection:
    def __init__(self, results: list[tuple[object, ...] | None]) -> None:
        self.cursor_instance = Cursor(results)
        self.committed = False

    def __enter__(self) -> Connection:
        return self

    def __exit__(self, *_: object) -> None:
        return None

    def cursor(self) -> Cursor:
        return self.cursor_instance

    def commit(self) -> None:
        self.committed = True


def test_projects_a_completed_native_source_record_without_legacy_exporter_tables() -> None:
    from house_consensus_ingestion.projection import PostgresListingProjectionWriter

    source_snapshot_id = "00000000-0000-0000-0000-000000000010"
    connection = Connection(
        results=[
            (
                "house-consensus-ingestion",
                "boliga.dk",
                [{"external_id": "42", "address": "Example Street 42", "price": 2_500_000}],
            ),
            None,
            ("00000000-0000-0000-0000-000000000042",),
        ]
    )

    count = PostgresListingProjectionWriter(lambda: connection).project_completed_snapshot(
        source_snapshot_id=source_snapshot_id,
        projected_at=datetime(2026, 8, 7, tzinfo=UTC),
    )

    statements = "\n".join(statement for statement, _ in connection.cursor_instance.executed)
    assert count == 1
    assert "ingestion_source_snapshots" in statements
    assert "run_status IN ('running', 'succeeded', 'failed')" in statements
    assert "AND EXISTS" in statements
    assert "stage_name = 'fetch'" in statements
    assert "INSERT INTO listings" in statements
    assert "INSERT INTO listing_ingestion_projections" in statements
    assert "INSERT INTO asbestos_roof_assessments" in statements
    assert "ON CONFLICT (listing_id, rule_version, source_fingerprint) DO NOTHING" in statements
    assert "export_runs" not in statements
    assert "consensus_exporter" not in statements
    assert connection.committed is True


def test_failed_native_assessment_falls_back_to_unknown(monkeypatch) -> None:
    import house_consensus_ingestion.projection as projection

    monkeypatch.setattr(projection, "assess_asbestos_roof", lambda _: (_ for _ in ()).throw(RuntimeError("broken")))
    result = projection._assess_safely({"roof_materials": ["Asbest"]})

    assert result.status == "unknown"
    assert result.rule_version == "asbestos-roof-v1"
    assert result.source_fingerprint


def test_rejects_an_uncompleted_or_missing_native_source_snapshot() -> None:
    from house_consensus_ingestion.projection import (
        CompletedSourceSnapshotRequiredError,
        PostgresListingProjectionWriter,
    )

    connection = Connection(results=[None])

    with pytest.raises(CompletedSourceSnapshotRequiredError, match="successful fetch"):
        PostgresListingProjectionWriter(lambda: connection).project_completed_snapshot(
            source_snapshot_id="00000000-0000-0000-0000-000000000010",
            projected_at=datetime(2026, 8, 7, tzinfo=UTC),
        )


def test_rejects_ambiguous_or_manual_listing_identity_instead_of_overwriting_it() -> None:
    from house_consensus_ingestion.projection import ListingIdentityConflictError, PostgresListingProjectionWriter

    connection = Connection(
        results=[
            (
                "house-consensus-ingestion",
                "boliga.dk",
                [{"external_id": "42", "address": "Example Street 42"}],
            ),
            ("00000000-0000-0000-0000-000000000099",),
        ]
    )

    with pytest.raises(ListingIdentityConflictError, match="identity"):
        PostgresListingProjectionWriter(lambda: connection).project_completed_snapshot(
            source_snapshot_id="00000000-0000-0000-0000-000000000010",
            projected_at=datetime(2026, 8, 7, tzinfo=UTC),
        )

    statements = "\n".join(statement for statement, _ in connection.cursor_instance.executed)
    assert '\"IsManuallyAdded\" = true' in statements
    assert "listing_overrides" in statements
    assert "FOR KEY SHARE" in statements


def test_application_migration_owns_native_listing_projection_identity() -> None:
    from pathlib import Path

    migration = Path(__file__).resolve().parents[2] / "src/Server/Data/Migrations/202608070003_AddNativeListingProjection.cs"
    text = migration.read_text()

    assert 'Migration("202608070003_AddNativeListingProjection")' in text
    assert "CREATE TABLE IF NOT EXISTS listing_ingestion_projections" in text
    assert "UNIQUE (source_system, source_scope, source_record_id)" in text
    assert 'FOREIGN KEY (listing_id) REFERENCES listings("Id") ON DELETE RESTRICT' in text
    assert 'FOREIGN KEY (source_snapshot_id) REFERENCES ingestion_source_snapshots(snapshot_id) ON DELETE RESTRICT' in text
    assert "export_runs" not in text
    assert "consensus_exporter" not in text



def test_rejects_duplicate_native_source_identities_in_one_completed_snapshot() -> None:
    from house_consensus_ingestion.projection import ListingIdentityConflictError, PostgresListingProjectionWriter

    connection = Connection(
        results=[
            (
                "house-consensus-ingestion",
                "boliga.dk",
                [
                    {"external_id": "42", "address": "Example Street 42"},
                    {"external_id": "42", "address": "Conflicting Street 42"},
                ],
            ),
            None,
            ("00000000-0000-0000-0000-000000000042",),
            None,
            ("00000000-0000-0000-0000-000000000042",),
        ]
    )

    with pytest.raises(ListingIdentityConflictError, match="duplicate"):
        PostgresListingProjectionWriter(lambda: connection).project_completed_snapshot(
            source_snapshot_id="00000000-0000-0000-0000-000000000010",
            projected_at=datetime(2026, 8, 7, tzinfo=UTC),
        )

    assert len(connection.cursor_instance.executed) == 1



def test_projects_native_boligsiden_snapshot_using_validated_projection_records() -> None:
    from house_consensus_ingestion.projection import PostgresListingProjectionWriter

    connection = Connection(results=[
        ("house-consensus-ingestion", "boligsiden.dk/open-cases", {
            "records": [{"caseID": "case-42", "address": {"roadName": "Example Road"}}],
            "projection_records": [{"external_id": "case-42", "address": "Example Road 42, 2100 Copenhagen", "city": "Copenhagen", "price": 2_500_000}],
        }),
        None,
        ("00000000-0000-0000-0000-000000000042",),
    ])

    assert PostgresListingProjectionWriter(lambda: connection).project_completed_snapshot(
        source_snapshot_id="00000000-0000-0000-0000-000000000010", projected_at=datetime(2026, 8, 7, tzinfo=UTC),
    ) == 1
    listing_insert = connection.cursor_instance.executed[2]
    assert listing_insert[1][1:5] == ("case-42", "Example Road 42, 2100 Copenhagen", "Copenhagen", 2_500_000)



def test_isolated_postgres_bootstrap_mirrors_native_listing_projection_contract() -> None:
    from pathlib import Path
    schema = (Path(__file__).resolve().parents[2] / "exporter/src/consensus_exporter/schema.sql").read_text()
    assert "CREATE TABLE IF NOT EXISTS listing_ingestion_projections" in schema
    assert "UNIQUE (source_system, source_scope, source_record_id)" in schema
