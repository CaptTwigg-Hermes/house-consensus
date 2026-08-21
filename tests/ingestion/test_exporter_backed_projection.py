from datetime import UTC, datetime
from types import SimpleNamespace
from uuid import UUID

import pytest


class Result:
    def __init__(self, *, one=None, rows=()):
        self.one = one
        self.rows = list(rows)

    def fetchone(self):
        return self.one

    def fetchall(self):
        return self.rows


class SourceCursor:
    def __init__(self, source):
        self.source = source
        self.statements = []

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return None

    def execute(self, statement, parameters):
        self.statements.append((statement, parameters))

    def fetchone(self):
        return self.source


class SourceConnection:
    def __init__(self, source):
        self.cursor_instance = SourceCursor(source)

    def __enter__(self):
        return self

    def __exit__(self, *_):
        return None

    def cursor(self):
        return self.cursor_instance


class ProjectionConnection:
    def __init__(self):
        self.calls = []

    def execute(self, statement, parameters):
        self.calls.append((statement, parameters))
        if statement.lstrip().startswith("SELECT"):
            return Result(rows=[])
        return Result(one=(parameters[0],))


def payload(config_sha="a" * 64):
    return {
        "source_system": "house-consensus-ingestion",
        "source_scope": "tofamiliehus",
        "manifest_sha256": "b" * 64,
        "source_config_sha256": config_sha,
        "snapshot_count": 1,
        "records": [{
            "id": "case-1",
            "external_id": "case-1",
            "address": "Examplevej 1, 2500 Valby",
            "city": "Valby",
            "price_dkk": 8_000_000,
            "source_url": "https://www.boligsiden.dk/adresse/example",
            "non_ai_passed": True,
            "family_score": {"total": 82.5},
            "commute": {"status": "ok"},
            "vision_run_status": "ok",
        }],
    }


def test_exporter_backed_projector_preserves_full_case_and_records_provenance():
    from house_consensus_ingestion.projection import (
        ExporterBackedPostgresListingProjector,
    )

    completed = datetime(2026, 8, 21, tzinfo=UTC)
    source = ("run-1", "house-consensus-ingestion", "tofamiliehus", completed, "b" * 64, payload())
    export_connection = ProjectionConnection()
    observed = {}

    class Exporter:
        def export(self, cases, **kwargs):
            observed["cases"] = cases
            observed["kwargs"] = kwargs
            kwargs["projection_recorder"](export_connection, cases[0], UUID("00000000-0000-0000-0000-000000000111"))
            return SimpleNamespace(exported=1)

    projector = ExporterBackedPostgresListingProjector(
        connection_factory=lambda: SourceConnection(source),
        exporter_factory=lambda scope: observed.setdefault("scope", scope) and Exporter(),
    )
    assert projector.project_completed_snapshot(source_snapshot_id="snap-1", projected_at=completed) == 1
    case = observed["cases"][0]
    assert case.source_id == "case-1"
    assert case.source_url == "https://www.boligsiden.dk/adresse/example"
    assert observed["kwargs"]["source_config_sha256"] == "a" * 64
    assert any("listing_ingestion_projections" in statement for statement, _ in export_connection.calls)


def test_exporter_backed_projector_rejects_missing_or_invalid_completed_snapshot():
    from house_consensus_ingestion.projection import (
        CompletedSourceSnapshotRequiredError,
        ExporterBackedPostgresListingProjector,
        SourceRecordError,
    )

    projector = ExporterBackedPostgresListingProjector(
        connection_factory=lambda: SourceConnection(None), exporter_factory=lambda _scope: None
    )
    with pytest.raises(CompletedSourceSnapshotRequiredError):
        projector.project_completed_snapshot(source_snapshot_id="missing", projected_at=datetime.now(UTC))

    source = ("run-1", "house-consensus-ingestion", "tofamiliehus", datetime.now(UTC), "b" * 64, payload("bad"))
    projector = ExporterBackedPostgresListingProjector(
        connection_factory=lambda: SourceConnection(source), exporter_factory=lambda _scope: None
    )
    with pytest.raises(SourceRecordError, match="configuration SHA-256"):
        projector.project_completed_snapshot(source_snapshot_id="snap-1", projected_at=datetime.now(UTC))
