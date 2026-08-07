"""Executable wiring for a lease-fenced manual scoring run."""

from __future__ import annotations

import argparse
import importlib
import json
import os
from datetime import datetime, timedelta, timezone
from typing import Any, Callable, Sequence

from .postgres_store import PostgresManualScoringStore
from .worker import ManualScoringWorker


StoreFactory = Callable[[str, timedelta], Any]


def load_component(specification: str) -> Any:
    """Load a zero-argument component factory using ``module:attribute`` syntax."""
    module_name, separator, attribute_name = specification.partition(":")
    if not separator or not module_name or not attribute_name:
        raise ValueError("component must use module:attribute syntax")
    component = getattr(importlib.import_module(module_name), attribute_name)
    return component() if callable(component) else component


def validate_components(source_resolver: Any, scoring_pipeline: Any) -> None:
    if not callable(getattr(source_resolver, "resolve", None)):
        raise TypeError("source resolver must provide resolve(identity)")
    if not callable(getattr(scoring_pipeline, "score", None)):
        raise TypeError("scoring pipeline must provide score(listing)")


def main(argv: Sequence[str] | None = None, *, store_factory: StoreFactory | None = None) -> int:
    parser = argparse.ArgumentParser(description="Claim and score one durable manual-scoring job.")
    parser.add_argument("--database-url", default=os.environ.get("CONSENSUS_DATABASE_URL"))
    parser.add_argument("--source-resolver", required=True, metavar="MODULE:FACTORY")
    parser.add_argument("--scoring-pipeline", required=True, metavar="MODULE:FACTORY")
    parser.add_argument("--lease-seconds", type=int, default=300)
    arguments = parser.parse_args(argv)
    if not arguments.database_url:
        parser.error("--database-url or CONSENSUS_DATABASE_URL is required")
    if arguments.lease_seconds <= 0:
        parser.error("--lease-seconds must be positive")

    lease_duration = timedelta(seconds=arguments.lease_seconds)
    factory = store_factory or (lambda url, duration: PostgresManualScoringStore(url, lease_duration=duration))
    source_resolver = load_component(arguments.source_resolver)
    scoring_pipeline = load_component(arguments.scoring_pipeline)
    validate_components(source_resolver, scoring_pipeline)
    store = factory(arguments.database_url, lease_duration)
    worker = ManualScoringWorker(store, source_resolver, scoring_pipeline)
    result = worker.run_once(datetime.now(timezone.utc))
    print(json.dumps({"status": result.status}, sort_keys=True))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
