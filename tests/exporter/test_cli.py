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
