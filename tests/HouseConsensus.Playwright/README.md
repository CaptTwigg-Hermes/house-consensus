# HouseConsensus Playwright acceptance suite

Black-box TypeScript tests for the user journeys that cross the UI, API, database,
live update transport, and SMTP delivery. No login bypasses or secrets are used: each
test creates unique `example.test` identities and follows Mailpit links.

## Run locally

Prerequisites: the app and Mailpit are running, Node 22+, and Chromium is installed.

```sh
npm ci
npx playwright install chromium
E2E_BASE_URL=http://localhost:8080 \
MAILPIT_API_URL=http://localhost:8025 \
npm test
```

Run against a Compose/CI image (the image must expose HTTP port 8080 and accept the
standard ASP.NET/database/SMTP environment variables in `compose.e2e.yml`):

```sh
HOUSECONSENSUS_IMAGE=registry.example/houseconsensus:sha docker compose \
  -f compose.e2e.yml up --build --abort-on-container-exit --exit-code-from playwright
```

If the repository's app service uses different environment names, keep the `db`,
`mailpit`, and `playwright` services and override only `app`. The runner waits up to
`E2E_WAIT_SECONDS` for both public endpoints. Set `E2E_ALL_BROWSERS=1` for the full
Chromium/Firefox/WebKit matrix. Artifacts, traces, video, JUnit, and HTML reports are
written under `test-results/` and `playwright-report/`.

## Stable UI contract

Tests prefer accessible roles/names and use `data-testid` only where visible text is
localized, repeated, or stateful. The UI should expose these stable hooks (never put
user data or translated copy in the id):

- shell/auth: `app-shell`, `auth-email`, `auth-link-sent`, `profile-name`,
  `invite-email`, `current-user-email`, `language-select`
- listings: `listing-card` plus `data-listing-id`, `data-price`, `data-area`;
  `vote-interested`, `vote-reject`, `restore-listing`, `unanimity-status`, `match-banner`;
  asbestos assessment hooks: `asbestos-status` plus `data-status`,
  `asbestos-assessment-open`, `asbestos-confirm-dialog`, `asbestos-assessment-option`,
  `asbestos-confirm`, `asbestos-cancel`
- browse/map: `filter-price-max`, `filter-area-min`, `filter-apply`, `filter-clear`,
  `browse-map`, `map-marker` plus `data-highlighted=true` on the synchronized marker
- feedback: `feedback-message`, `feedback-category`, `feedback-success`,
  `feedback-export-csv`, `feedback-export-json`
- members: `member-row`, `member-invite-email`, `member-status`, `member-role`,
  `member-notice`, `household-access-revoked`

Native controls must retain associated labels and buttons/links accessible names. The
suite intentionally does not fall back to CSS implementation details.

## Isolation assumptions

E2E mode must start with deterministic listing seed data and make the first signed-in
user owner of a fresh household. Parallel tests use unique plus-addressed emails. If
an environment shares one global household, run with `--workers=1`; isolated test
runs are strongly preferred. Mailpit is cleared at each test start, so its instance
must be dedicated to this suite.

## Coverage

1. Invite and Mailpit magic-link sign-in/acceptance.
2. Independent two-browser voting and owner-page live unanimity update.
3. Owner restoration of rejected homes.
4. English/Danish preference persistence across navigation, reload, and tabs.
5. Combined Browse filters and list/map synchronization.
6. Feedback submission plus CSV and JSON export validity/redaction checks.
7. Owner invite/resend/revoke/remove flows and owner-protection rule.
