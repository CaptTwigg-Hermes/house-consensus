# House Consensus Implementation Plan

> **For Hermes:** Implement task-by-task with strict RED-GREEN-REFACTOR.

**Goal:** Build and locally verify the locked `house-consensus` v1.

**Architecture:** Hosted .NET 10 Blazor WebAssembly with an ASP.NET Core API, SignalR, PostgreSQL, cookie identity, a Python exporter from houseshopping, and Docker Compose.

**Tech:** .NET 10, EF Core/Npgsql, ASP.NET Identity, SignalR, Blazor WASM, Leaflet/OSM, PostgreSQL, Python, Mailpit, xUnit, Testcontainers, Playwright.

1. Create solution/project files, Docker SDK workflow, package versions, analyzers, `.gitignore`, and local Compose.
2. RED→GREEN domain entities/rules for membership, listings, filter decisions, vote events/latest vote, comments/revisions, overrides, archives, and unanimity.
3. RED→GREEN EF mapping/migrations and PostgreSQL integration fixtures.
4. RED→GREEN magic links, owner bootstrap, cookies, invites, deactivation, Mailpit.
5. RED→GREEN listing/queue/Browse/Review/vote/comment/consensus/feedback APIs.
6. RED→GREEN SignalR events.
7. RED→GREEN Python exporter with idempotent upserts, provenance, media cache, archive lifecycle, and override preservation.
8. Add non-fatal export stage to `/workspace/houseshopping` after publish with failure-isolation test.
9. Build localized Queue, Detail, Browse/map, Consensus, My Votes, Review, Feedback, Members screens.
10. Add PWA behavior.
11. Add Playwright flows using PostgreSQL + Mailpit.
12. Run formatting, analyzers, tests, production build, container health, secret scan, spec review.
13. Update graphify for houseshopping and document local run/deploy/backup.

No deployment or GitHub push until Capt Twigg reviews the local build.
