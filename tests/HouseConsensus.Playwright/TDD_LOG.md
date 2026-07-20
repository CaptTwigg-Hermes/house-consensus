# Strict TDD log

This log records commands and observed outcomes; it does not claim an application pass
when the application/browser infrastructure was unavailable.

## RED — 2026-07-20T20:34:54Z

Defined the acceptance boundaries and test-id contract, then checked for the local
Playwright runner:

```text
$ test -x node_modules/.bin/playwright
RED: Playwright dependency not installed; static/test runner cannot start.
(exit 1)
```

The initial state therefore could not execute any acceptance test. Next gate: install
locked dependencies, type-check, and enumerate tests before attempting browser E2E.

## GREEN — 2026-07-20T20:36:40Z

Installed the lockfile and ran all static gates:

```text
$ npm ci
added 6 packages; found 0 vulnerabilities

$ npm run typecheck
> tsc --noEmit
(exit 0)

$ npx playwright test --list
7 tests in 7 files (Chromium)
(exit 0)

$ sh -n scripts/run-e2e.sh
shell and JSON syntax OK
(exit 0)

$ npm audit
found 0 vulnerabilities
(exit 0)
```

The first dependency pin (Playwright 1.54.1) produced two high-severity audit findings
for unverified browser-download certificates. In the refactor step it was upgraded to
1.61.1, the matching Microsoft Playwright image was updated, the lockfile regenerated,
and the audit became clean.

## REFACTOR / browser execution — 2026-07-20T20:36:40Z

Ran one real Chromium test to verify runner/browser startup and artifact capture:

```text
$ npx playwright test specs/auth-invite.spec.ts --workers=1 --reporter=line
Running 1 test using 1 worker
apiRequestContext.delete: connect ECONNREFUSED 127.0.0.1:8025
1 failed (exit 1)
```

This is an infrastructure RED, not an asserted product failure: both
`http://127.0.0.1:8080` and `http://127.0.0.1:8025/api/v1/info` returned
`ECONNREFUSED`. Chromium did launch and Playwright produced screenshot, video, trace,
and error context. Compose could not start the supplied harness because this host has
no Docker daemon (`Cannot connect to the Docker daemon at unix:///var/run/docker.sock`)
and no Docker Compose plugin. Full product GREEN therefore remains a CI/Compose gate.
