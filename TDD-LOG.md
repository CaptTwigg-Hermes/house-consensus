# RED / GREEN log

The implementation was developed test-first at the source level. RED cases were captured before their corresponding implementations. A repository-local .NET 10 SDK became available for verification; Docker/PostgreSQL verification remains blocked because this environment has no reachable daemon.

| Cycle | RED specification | GREEN implementation | Execution |
|---|---|---|---|
| 1 | Unanimity needs every active member's latest explicit Like | `ConsensusRules` latest-event calculation and membership recalculation | GREEN: unit suite |
| 2 | Owner overrides survive imports; archive preserves history | append-only `ListingOverride` and guarded import decision | GREEN: unit suite |
| 3 | Comment edits/deletes retain audit and enforce author/owner | `CommentRevision` append-only mutations | GREEN: unit suite |
| 4 | PostgreSQL persists vote history, overrides and archive lifecycle | EF mappings + initial migration | Migration script GREEN; runtime blocked |
| 5 | Invite-only magic links are hashed, 15-minute, one-use | transactional `MagicLinkService` + cookie auth | Blocked |
| 6 | Browse query must URL-encode city and include only supplied server filters; UI culture must accept only English/Danish | `BrowseQuery` and `UiCulture` client helpers | RED captured: missing Client project/types; GREEN: client unit suite |
| 7 | All locked routes need authenticated, localized, mobile-first rendering and owner gating | hosted Blazor WASM screens, shared components, culture/auth state and polished responsive CSS | GREEN: Release solution build |
| 8 | Browse map, live updates and installability require browser integration | clustered Leaflet/OSM geocoded map, reconnecting SignalR client and versioned PWA service worker | GREEN: Release publish/static web assets |

Run the full gate on a Docker-enabled host with: `docker build -f Dockerfile.test -t house-consensus-tests . && docker run --rm -v /var/run/docker.sock:/var/run/docker.sock house-consensus-tests`.


## Latest verification

- Release solution build: **GREEN**, 0 compiler/analyzer errors (the invariant-mode host SDK emitted locale-resource warnings).
- Unit tests: **GREEN**, 21/21 (including client culture/filter helpers).
- Hosted WASM Release publish: **GREEN**; static framework, manifest, service worker, and SPA fallback verified over HTTP.
- PostgreSQL integration tests: **RED/BLOCKED**, Testcontainers reported Docker unavailable at `/var/run/docker.sock`; both tests reached environment setup and could not start PostgreSQL.
