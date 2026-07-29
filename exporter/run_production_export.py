#!/usr/bin/env python3
"""Run the guarded exporter against the dedicated production database."""

from __future__ import annotations

import argparse
import os
import subprocess
from pathlib import Path
from urllib.parse import quote


def load_env(path: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    for raw in path.read_text().splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        values[key.strip()] = value.strip().strip('"').strip("'")
    return values


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Export the latest immutable snapshot to House Consensus production"
    )
    parser.add_argument(
        "--dry-run", action="store_true", help="calculate changes and roll back all DML"
    )
    args = parser.parse_args(argv)

    root = Path(__file__).resolve().parents[1]
    exporter_dir = Path(__file__).resolve().parent
    values = load_env(root / ".env")
    password = values.get("POSTGRES_PASSWORD")
    if not password:
        raise SystemExit("POSTGRES_PASSWORD is missing from House Consensus .env")

    env = os.environ.copy()
    env["CONSENSUS_DATABASE_URL"] = (
        "postgresql://house_consensus:"
        f"{quote(password, safe='')}@192.168.50.2:5433/house_consensus"
    )
    command = ["uv", "run", "python", "-m", "consensus_exporter.cli", "--skip-media"]
    if args.dry_run:
        command.append("--dry-run")
    return subprocess.run(command, cwd=exporter_dir, env=env, check=False).returncode


if __name__ == "__main__":
    raise SystemExit(main())
