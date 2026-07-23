from types import SimpleNamespace

from consensus_exporter import cli


def test_cli_explicitly_enables_schema_bootstrap(monkeypatch, tmp_path):
    captured = {}

    class FakeExporter:
        def __init__(self, database_url, **kwargs):
            captured["database_url"] = database_url
            captured.update(kwargs)

        def export(self, cases, *, run_id):
            return SimpleNamespace(exported=0, archived=0, media_cached=0, media_errors=0)

    monkeypatch.setattr(cli, "PostgresExporter", FakeExporter)
    monkeypatch.setattr(cli, "load_sqlite_cases", lambda _: [])
    monkeypatch.setattr(
        "sys.argv",
        [
            "house-consensus-export",
            "--database-url",
            "postgresql://example.test/db",
            "--media-dir",
            str(tmp_path),
            "--ensure-schema",
        ],
    )

    assert cli.main() == 0
    assert captured["ensure_schema_on_export"] is True


def test_cli_can_skip_unused_media_downloads(monkeypatch):
    captured = {}

    class FakeExporter:
        def __init__(self, database_url, **kwargs):
            captured.update(kwargs)

        def export(self, cases, *, run_id):
            return SimpleNamespace(exported=0, archived=0, media_cached=0, media_errors=0)

    monkeypatch.setattr(cli, "PostgresExporter", FakeExporter)
    monkeypatch.setattr(cli, "load_sqlite_cases", lambda _: [])
    monkeypatch.setattr(cli, "MediaCache", lambda _: (_ for _ in ()).throw(AssertionError("media cache created")))
    monkeypatch.setattr("sys.argv", ["house-consensus-export", "--database-url", "postgresql://example.test/db", "--skip-media"])

    assert cli.main() == 0
    assert captured["media_cache"] is None


def test_cli_tombstones_without_loading_sqlite(monkeypatch):
    captured = {}
    monkeypatch.setattr(
        cli,
        "tombstone_listing",
        lambda database_url, **kwargs: captured.update(database_url=database_url, **kwargs),
        raising=False,
    )
    monkeypatch.setattr(
        cli,
        "load_sqlite_cases",
        lambda _: (_ for _ in ()).throw(AssertionError("SQLite source loaded")),
    )
    monkeypatch.setattr(
        "sys.argv",
        [
            "house-consensus-export",
            "--database-url",
            "postgresql://example.test/db",
            "--tombstone-external-id",
            "gone-123",
            "--tombstone-source-url",
            "https://www.boligsiden.dk/cases/gone-123",
        ],
    )

    assert cli.main() == 0
    assert captured == {
        "database_url": "postgresql://example.test/db",
        "external_id": "gone-123",
        "source_url": "https://www.boligsiden.dk/cases/gone-123",
        "verification_method": "http_404",
    }
