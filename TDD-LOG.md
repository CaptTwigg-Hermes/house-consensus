# RED / GREEN log

The implementation was developed test-first at the source level. RED cases were captured before their corresponding implementations. A repository-local .NET 10 SDK and dedicated PostgreSQL 18.4 test instance are available. Browser flows run directly against the published app and Mailpit when Docker is unavailable.

| Cycle | RED specification | GREEN implementation | Execution |
|---|---|---|---|
| 1 | Unanimity needs every active member's latest explicit Like | `ConsensusRules` latest-event calculation and membership recalculation | GREEN: unit suite |
| 2 | Owner overrides survive imports; archive preserves history | append-only `ListingOverride` and guarded import decision | GREEN: unit suite |
| 3 | Comment edits/deletes retain audit and enforce author/owner | `CommentRevision` append-only mutations | GREEN: unit suite |
| 4 | PostgreSQL persists vote history, overrides and archive lifecycle | EF mappings + initial migration | GREEN: PostgreSQL integration suite |
| 5 | Invite-only magic links are hashed, 15-minute, one-use | transactional `MagicLinkService` + cookie auth | GREEN: PostgreSQL integration suite |
| 6 | Browse query must URL-encode city and include only supplied server filters; UI culture must accept only English/Danish | `BrowseQuery` and `UiCulture` client helpers | RED captured: missing Client project/types; GREEN: client unit suite |
| 7 | All locked routes need authenticated, localized, mobile-first rendering and owner gating | hosted Blazor WASM screens, shared components, culture/auth state and polished responsive CSS | GREEN: Release solution build |
| 8 | Browse map, live updates and installability require browser integration | clustered Leaflet/OSM geocoded map, reconnecting SignalR client and versioned PWA service worker | GREEN: Release publish/static web assets |
| 9 | Comment owners need an audited edit path from the UI | `ApiClient.EditComment` and inline localized edit controls; owner detail actions expose restore/reject | RED: missing client method; GREEN: focused and aggregate unit suites |
| 10 | E2E Compose must build the app and use the server’s real configuration keys | corrected build context, database key, owner bootstrap, public origin, and SMTP keys | RED: deployment config regression test; GREEN: exporter test suite |
| 11 | Exporter PostgreSQL upsert must execute against a real server | repaired the unterminated `RETURNING "Id"` identifier | RED: 2/2 exporter PostgreSQL tests failed; GREEN: 2/2 focused and 12/12 aggregate exporter tests |
| 12 | First-start migrations must make newly-created PostgreSQL enums usable immediately | reload Npgsql type metadata after migration; allow a guarded external test database | RED: both .NET PostgreSQL tests failed on unknown enum types; GREEN: integration tests and live health/bootstrap checks |
| 13 | Browser startup must load configured cultures and expose stable automation contracts | ship Blazor globalization data and add semantic test hooks | RED: WebAssembly culture-data startup exception and missing locators; GREEN: browser auth flow |
| 14 | E2E must have deterministic listings and respect the single-household model | guarded E2E seeder, fixed bootstrap owner, serial workers and active-member cleanup | RED: empty Browse and non-unanimous historical members; GREEN: Browse, restore and voting flows |
| 15 | Repeated browser auth must remain testable without weakening production limits | configurable request/consume permit limits with E2E-only overrides | RED: HTTP 429 during the suite; GREEN: seven sequential browser flows |
| 16 | Feedback submit must react while the user types | bind textarea on `oninput` | RED: enabled-button timeout after `fill`; GREEN: feedback submit and CSV/JSON export flow |

Run the full gate on a Docker-enabled host with: `docker build -f Dockerfile.test -t house-consensus-tests . && docker run --rm -v /var/run/docker.sock:/var/run/docker.sock house-consensus-tests`.


## Latest verification

- Release solution build: **GREEN**, 0 compiler/analyzer errors (the invariant-mode host SDK emitted locale-resource warnings).
- Unit tests: **GREEN**, 26/26 (including client culture/filter, audited comments, and browser-contract regressions).
- Hosted WASM Release publish: **GREEN**; static framework, manifest, service worker, and server assembly verified in the publish artifact.
- Exporter tests: **GREEN**, 14/14 against PostgreSQL 18.4 on the dedicated test instance; Ruff is GREEN.
- Houseshopping export-failure isolation: **GREEN**, 3/3 focused tests.
- TypeScript/Playwright: **GREEN**, typecheck plus 7/7 Chromium flows against the published app, PostgreSQL and Mailpit.
- PostgreSQL integration tests: **GREEN**, 7/7 using the guarded external test database.
- Live server health/bootstrap: **GREEN**; migration applied, owner bootstrapped, SPA served, and `/health` returned HTTP 200 `Healthy`.
