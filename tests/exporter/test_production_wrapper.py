import importlib.util
from pathlib import Path
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[2] / "exporter" / "run_production_export.py"
spec = importlib.util.spec_from_file_location("production_wrapper", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def test_wrapper_uses_exporter_directory_and_fixed_database(monkeypatch, tmp_path):
    captured = {}
    monkeypatch.setattr(module, "load_env", lambda _: {"POSTGRES_PASSWORD": "p@ss/word"})
    monkeypatch.setattr(module.subprocess, "run", lambda command, **kwargs: captured.update(command=command, **kwargs) or type("R", (), {"returncode": 0})())
    assert module.main(["--dry-run"]) == 0
    assert captured["cwd"] == SCRIPT.parent
    assert captured["command"] == ["uv", "run", "python", "-m", "consensus_exporter.cli", "--skip-media", "--dry-run"]
    assert captured["env"]["CONSENSUS_DATABASE_URL"] == "postgresql://house_consensus:p%40ss%2Fword@192.168.50.2:5433/house_consensus"


def test_wrapper_rejects_database_override():
    import pytest
    with pytest.raises(SystemExit) as exc:
        module.main(["--database-url", "postgresql://attacker.invalid/other"])
    assert exc.value.code == 2
