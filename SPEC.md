# House Consensus

## Locked v1

Private, invite-only household app for evaluating houses from the existing
houseshopping pipeline.

### Product

- One fixed household; one owner manages invited members.
- Email magic-link sign-in. Local Mailpit; production provider selected later.
- 15-minute single-use links; 30-day secure HttpOnly same-origin session.
- Vote states: Like, Dislike, Not voted.
- Votes are mutable; retain history; latest wins.
- Everyone always sees everyone’s votes.
- Consensus requires every active member to explicitly Like.
- Membership changes recalculate consensus.
- Queue: current user’s unvoted active passed/restored houses, family-fit score descending.
- Compact cards, buttons only; details page for full evidence.
- Optional Like/Dislike reason tags plus comments.
- Tags: layout, privacy, garden, condition, location, noise, price, other.
- Feedback is reviewed, never fed automatically into live AI.
- SignalR live votes/comments/consensus.
- English and Danish; browser language initially, saved user preference.
- Installable PWA, online voting only.

### Pipeline

- Keep `/workspace/houseshopping` Python ingestion and AI.
- New non-fatal final export stage after publish.
- Export every fetched case in current scope to dedicated PostgreSQL database.
- Existing non-AI filters unchanged.
- High-confidence AI rejection may remove from main feed; all rejected houses remain in owner-only Review with evidence, confidence, and model/rule versions.
- Missing/failed AI does not reject a non-AI pass; mark `AI not assessed`.
- Owner may restore or manually reject. Overrides survive later imports.
- Removed/sold houses archive; votes/history remain.
- Keep weekday 08:00 refresh.
- Cache thumbnails and floor plans locally.

### Screens

1. Queue
2. House detail
3. Browse with existing filters and Leaflet/OpenStreetMap clustered map
4. Everyone Likes
5. My Votes
6. Owner Review
7. Owner Feedback dashboard + CSV/JSON export
8. Owner Members

Only owner sees Review. Members edit/delete own comments; owner moderates any; retain revision/deletion audit history.

### Architecture

- .NET 10 LTS hosted Blazor WebAssembly.
- One ASP.NET Core container serves API, SignalR, and compiled WASM.
- Dedicated PostgreSQL database.
- Monorepo: Client, Server, Shared, Python exporter, tests, Compose/deploy.
- Local Compose: app, PostgreSQL, Mailpit.
- Production: TrueNAS/Dockge + existing PostgreSQL + Cloudflare Tunnel.
- `INITIAL_OWNER_EMAIL` one-time owner bootstrap.
- Reuse houseshopping visual style, cards, colors, badges.
- Private target repository: `CaptTwigg-Hermes/house-consensus`.
- Daily encrypted production DB backup, retain 30 days.

### Quality gate

- Unit tests for consensus, filters, overrides, vote history.
- PostgreSQL integration tests for exporter, auth, votes, comments, membership, review, and archive lifecycle.
- Playwright tests for invite/Mailpit sign-in, vote/live unanimity, restore, localization, Browse/map, and feedback export.
- Existing houseshopping run remains successful when export fails.

### Deferred

Multi-household, automatic retraining, offline vote sync, swipe gestures, buying workflow, owner-triggered refresh, and external notifications.
