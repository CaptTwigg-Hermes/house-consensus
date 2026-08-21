from __future__ import annotations

import fcntl
import os
import subprocess
import tomllib
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = {
    "ingestion": ROOT / "scripts" / "run-native-ingestion.sh",
    "manual-scoring": ROOT / "scripts" / "run-native-manual-scoring.sh",
}


@pytest.fixture()
def runtime(tmp_path: Path) -> tuple[Path, dict[str, str]]:
    root = tmp_path / "checkout"
    (root / "ingestion").mkdir(parents=True)
    (root / "manual_scoring").mkdir()
    bin_dir = tmp_path / "bin"
    bin_dir.mkdir()
    uv = bin_dir / "uv"
    uv.write_text(
        "#!/bin/sh\n"
        "printf '%s\\n' \"$@\" > \"$UV_ARGS_FILE\"\n"
        "pwd > \"$UV_CWD_FILE\"\n"
        "[ \"${DATABASE_URL:-}\" = 'postgresql://secret-sentinel' ] && printf 'loaded\\n' > \"$UV_ENV_FILE\"\n"
        "printf 'worker stdout\\n'\n"
        "printf 'worker stderr\\n' >&2\n"
        "exit \"${UV_EXIT:-0}\"\n"
    )
    uv.chmod(0o755)
    env = {
        **os.environ,
        "PATH": f"{bin_dir}:{os.environ['PATH']}",
        "HOUSE_CONSENSUS_ROOT": str(root),
        "HOUSE_CONSENSUS_LOCK_DIR": str(tmp_path / "locks"),
        "UV_ARGS_FILE": str(tmp_path / "uv-args"),
        "UV_CWD_FILE": str(tmp_path / "uv-cwd"),
        "UV_ENV_FILE": str(tmp_path / "uv-env"),
    }
    return root, env


def run_script(kind: str, env: dict[str, str], *arguments: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [str(SCRIPTS[kind]), *arguments], env=env, text=True, capture_output=True, check=False
    )


@pytest.mark.parametrize(
    ("kind", "project", "command"),
    [
        ("ingestion", "ingestion", "house-consensus-ingest"),
        ("manual-scoring", "manual_scoring", "house-consensus-manual-scorer"),
    ],
)
def test_wrapper_loads_explicit_env_and_runs_exact_cli_from_absolute_root(
    runtime: tuple[Path, dict[str, str]], tmp_path: Path, kind: str, project: str, command: str
) -> None:
    root, env = runtime
    project_metadata = tomllib.loads((ROOT / project / "pyproject.toml").read_text())
    assert command in project_metadata["project"]["scripts"]
    env_file = tmp_path / "native.env"
    env_file.write_text("DATABASE_URL=postgresql://secret-sentinel\n")
    env["HOUSE_CONSENSUS_ENV_FILE"] = str(env_file)

    result = run_script(kind, env, "--example", "value")

    assert result.returncode == 0
    assert result.stdout == "worker stdout\n"
    assert "worker stderr\n" in result.stderr
    assert "exit=0" in result.stderr
    assert "secret-sentinel" not in result.stdout + result.stderr
    assert Path(env["UV_ARGS_FILE"]).read_text().splitlines() == [
        "run",
        "--project",
        project,
        command,
        "--example",
        "value",
    ]
    assert Path(env["UV_CWD_FILE"]).read_text().strip() == str(root)
    assert Path(env["UV_ENV_FILE"]).read_text() == "loaded\n"


def test_wrapper_preserves_worker_exit_status(runtime: tuple[Path, dict[str, str]]) -> None:
    _, env = runtime
    env["UV_EXIT"] = "23"
    result = run_script("ingestion", env, "--dry-run")
    assert result.returncode == 23
    assert result.stdout == "worker stdout\n"
    assert "worker stderr\n" in result.stderr
    assert "exit=23" in result.stderr


def test_wrapper_rejects_relative_root_before_running_uv(runtime: tuple[Path, dict[str, str]]) -> None:
    _, env = runtime
    env["HOUSE_CONSENSUS_ROOT"] = "relative/path"
    result = run_script("ingestion", env)
    assert result.returncode == 64
    assert result.stdout == ""
    assert "HOUSE_CONSENSUS_ROOT must be an absolute path" in result.stderr
    assert not Path(env["UV_ARGS_FILE"]).exists()


def test_explicit_missing_env_file_fails_closed(runtime: tuple[Path, dict[str, str]], tmp_path: Path) -> None:
    _, env = runtime
    env["HOUSE_CONSENSUS_ENV_FILE"] = str(tmp_path / "missing.env")
    result = run_script("manual-scoring", env)
    assert result.returncode == 66
    assert "HOUSE_CONSENSUS_ENV_FILE is not readable" in result.stderr
    assert not Path(env["UV_ARGS_FILE"]).exists()


def test_single_flight_lock_rejects_overlap(runtime: tuple[Path, dict[str, str]]) -> None:
    _, env = runtime
    lock_dir = Path(env["HOUSE_CONSENSUS_LOCK_DIR"])
    lock_dir.mkdir()
    lock_path = lock_dir / "native-ingestion.lock"
    with lock_path.open("w") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        result = run_script("ingestion", env)
    assert result.returncode == 75
    assert result.stdout == ""
    assert "native ingestion is already running" in result.stderr
    assert not Path(env["UV_ARGS_FILE"]).exists()


@pytest.mark.parametrize(
    "argument",
    ["--database-url=postgresql://secret-sentinel", "--database-u=postgresql://secret-sentinel"],
)
def test_manual_wrapper_rejects_database_url_argument(
    runtime: tuple[Path, dict[str, str]], argument: str
) -> None:
    _, env = runtime
    result = run_script("manual-scoring", env, argument)
    assert result.returncode == 64
    assert "database credentials must come from the environment" in result.stderr
    assert "secret-sentinel" not in result.stderr
    assert not Path(env["UV_ARGS_FILE"]).exists()
