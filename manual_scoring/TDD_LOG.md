# RED / GREEN log

All commands were run from the repository root. Each RED command was observed before the immediately following GREEN implementation.

| Cycle | RED command and observed failure | GREEN command and observed result |
|---|---|---|
| Completion | `uv run --project manual_scoring --extra test pytest -q manual_scoring/tests/test_worker.py::test_worker_completes_oldest_claimed_pending_request_with_required_outputs` — `ModuleNotFoundError: house_consensus_manual_scoring` | Same command — `1 passed` |
| Pending selection | `uv run --project manual_scoring --extra test pytest -q manual_scoring/tests/test_worker.py::test_select_next_pending_prefers_oldest_retryable_uncompleted_request` — cannot import `select_next_pending` | Same command — `1 passed` |
| Identity ambiguity | `uv run --project manual_scoring --extra test pytest -q manual_scoring/tests/test_worker.py::test_worker_records_retryable_error_when_source_identity_is_ambiguous` — cannot import `AmbiguousSourceIdentity` | Same command — `1 passed` |
| Required outputs | `uv run --project manual_scoring --extra test pytest -q manual_scoring/tests/test_worker.py::test_worker_does_not_complete_when_required_score_or_evidence_is_missing` — expected `failed`, got `completed` | Same command — `1 passed` |
| Pipeline error | `uv run --project manual_scoring --extra test pytest -q manual_scoring/tests/test_worker.py::test_worker_records_retryable_pipeline_error_without_completing_request` — uncaught `RuntimeError: executor unavailable` | Same command — `1 passed` |

After a formatting/type-only refactor, the focused suite was run: `uv run --project manual_scoring --extra test pytest -q manual_scoring/tests` — **5 passed**.
