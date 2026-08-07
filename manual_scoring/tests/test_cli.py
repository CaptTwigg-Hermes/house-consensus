import json
import sys
from datetime import datetime, timezone
from types import ModuleType
from uuid import UUID


def test_cli_wires_component_contracts_to_one_durable_worker_run(capsys):
    from house_consensus_manual_scoring.cli import main
    from house_consensus_manual_scoring.worker import ManualScoringRequest, ScoringOutput, SourceIdentity

    now = datetime.now(timezone.utc)
    request = ManualScoringRequest(
        "listing-1", SourceIdentity("manual:1", "https://example.test/1"), now,
        job_id=UUID("00000000-0000-0000-0000-000000000001"), lease_fence=1,
    )
    module = ModuleType("manual_scoring_test_components")
    observed = {}

    class Source:
        def resolve(self, identity):
            observed["identity"] = identity
            return {"external_id": identity.external_id, "canonical_url": identity.canonical_url}

    class Pipeline:
        def score(self, listing):
            observed["listing"] = listing
            return ScoringOutput(88.5, {"minutes": 24}, {"summary": "evidence"})

    module.source = lambda: Source()
    module.pipeline = lambda: Pipeline()
    sys.modules[module.__name__] = module

    class Store:
        def claim_next_pending(self, now):
            return request

        def record_completion(self, request, output, completed_at):
            observed["completed"] = output
            return True

        def record_failure(self, request, failure, attempted_at):
            raise AssertionError(failure)

    try:
        exit_code = main(
            [
                "--database-url", "postgresql://example.test/db",
                "--source-resolver", "manual_scoring_test_components:source",
                "--scoring-pipeline", "manual_scoring_test_components:pipeline",
                "--lease-seconds", "42",
            ],
            store_factory=lambda url, lease_duration: observed.setdefault("lease_duration", lease_duration) and Store(),
        )
    finally:
        sys.modules.pop(module.__name__, None)

    assert exit_code == 0
    assert json.loads(capsys.readouterr().out) == {"status": "completed"}
    assert observed["identity"] == request.source_identity
    assert observed["listing"]["canonical_url"] == "https://example.test/1"
    assert observed["lease_duration"].total_seconds() == 42


def test_component_spec_rejects_missing_colon_separator():
    from house_consensus_manual_scoring.cli import load_component

    try:
        load_component("not-a-component-spec")
    except ValueError as error:
        assert "module:attribute" in str(error)
    else:
        raise AssertionError("invalid component specification was accepted")


def test_component_contracts_require_resolve_and_score_methods_before_claiming_work():
    from house_consensus_manual_scoring.cli import validate_components

    class NotAResolver:
        pass

    class NotAPipeline:
        pass

    try:
        validate_components(NotAResolver(), NotAPipeline())
    except TypeError as error:
        assert "resolve" in str(error)
    else:
        raise AssertionError("invalid source resolver was accepted")
