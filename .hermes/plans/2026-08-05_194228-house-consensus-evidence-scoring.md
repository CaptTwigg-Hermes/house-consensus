# House Consensus Evidence-Backed Scoring Plan

> **For Hermes:** Execute with subagent-driven development and two-stage review. House Shopping is deprecated and MUST NOT receive new product functionality.

**Goal:** Make House Consensus own broker-first enrichment, complete-media analysis, evidence-backed family scoring, provenance, review states, and rescoring so incomplete imagery cannot produce a confidently wrong score.

**Architecture:** Add native source adapters, a secure media fetcher, immutable media/evidence manifests, a dedicated House Consensus scoring-worker process backed by a durable PostgreSQL queue, an Ollama structured extractor, and a deterministic versioned scorer. Keep outbound broker/media/model access out of the web process. Facts are tri-state with provenance. Publish a score only when its evidence contract passes; otherwise show `Incomplete` or `Needs review`.

**Stack:** .NET 10, ASP.NET Core, EF Core/Npgsql/PostgreSQL, Blazor WebAssembly, Ollama, xUnit, Playwright, Cloudflare Access.

## Verified baseline findings

- Current ingestion is a synchronous compatibility exporter reading deprecated House Shopping SQLite; House Consensus has no native broker-gallery worker yet.
- Current exporter media discovery is thumbnail/floor-plan oriented, not a complete canonical broker gallery.
- House Consensus already has exporter-side score coverage/rule/privacy columns, but EF/domain/API/UI do not project them consistently.
- Actual current exporter code preserves missing `family_score` as SQL `NULL`; the delegated claim that it still used `case.family_score or 0` was stale and must not be reintroduced.
- Invocation success, parse validity, evidence sufficiency, contradiction status, and score publication must be separate states. A failed attempt never overwrites the last complete run.
- A fixed five-image sample is rejected: Ladagervej proved that sparse sampling can miss decisive structural evidence. Process the complete manifest within explicit count/byte/pixel/time budgets; exceeding a budget yields `Incomplete`, never silent sampling.
- Score publication uses atomic score epochs so Browse/Queue never mix old and new scoring versions during backfill.
- The repository baseline is real Git commit `82b26f9` on `main`; reports of a repository with no `HEAD` came from an incorrect alternate checkout and are not authoritative.

---

## Boundaries

- Implement only in `/workspace/house-consensus`.
- Add no new product functionality to `/workspace/houseshopping`.
- House Consensus PostgreSQL becomes authoritative for listing, queue, media, evidence, score, and audit state.
- Existing House Shopping import is temporary compatibility input only.
- Never turn “not shown” into `false`.
- Never publish numeric privacy without usable direct evidence.
- Preserve votes, notes, archives, overrides, and manual-listing identity.
- Production remains behind Cloudflare Access; auto-login stays development/E2E only.

## Product states

`Pending`, `Complete`, `Incomplete`, `NeedsReview`, `Failed`.

Facts use `True`, `False`, or `Unknown`. `False` requires positive disproof from readable evidence. Photo absence is `Unknown`.

## Complete-score evidence contract

A score is `Complete` only when:

- canonical broker listing or explicit supported fallback is recorded;
- complete broker media manifest finished;
- every still decoded or has a bounded recorded failure;
- every dedicated floor plan decoded and analysed;
- extraction succeeded at accepted quality;
- core privacy facts have direct evidence;
- contradiction checks passed;
- deterministic arithmetic validated;
- manifest hash, model, prompt/schema, and rule versions were committed atomically with the score.

Broker “two-family” text conflicting with extracted single-unit facts means `NeedsReview`, not a low score.

---

## Phase 1 — Canonical contracts and scorer

### Task 1: Shared states and DTOs

**Modify:** `src/Shared/Domain.cs`, `src/Shared/Contracts.cs`
**Test:** `tests/HouseConsensus.UnitTests/DomainTests.cs`

Add scoring state, tri-state fact, evidence quality, media type, coverage/provenance, and owner rescore DTOs. Unknown/malformed enums fail closed. Numeric score plus incomplete provenance is invalid.

### Task 2: One native deterministic scorer

**Create:**
- `src/Server/Scoring/FamilyScoreContract.cs`
- `src/Server/Scoring/FamilyScorer.cs`
- `tests/HouseConsensus.UnitTests/FamilyScorerTests.cs`

Keep weights privacy 30, kids 20, garden 20, shared 15, practical 15. Score direct facts once. Keep unavailable dimensions nullable with explicit coverage. Practical noise uses the loudest valid road/rail/air source. Return dimensions, weights, total, notes, versions, availability, and coverage atomically.

Probe complete, unknown, failed, unreadable-plan, explicit-negative, broker-contradiction, rail-louder-than-road, and malformed cases. Ladagervej-style incomplete evidence must never become privacy zero.

---

## Phase 2 — PostgreSQL queue, media, evidence, and history

### Task 3: Entities and migration

**Modify:** `src/Server/Data/AppDbContext.cs`, `src/Shared/Domain.cs`
**Create:** `src/Server/Data/Migrations/20260805xxxx_AddEvidenceBackedScoring.cs`
**Test:** `tests/HouseConsensus.IntegrationTests/PostgresLifecycleTests.cs`

Add:

- `listing_enrichment_jobs`: durable priority, lease, retry, attempts, timestamps.
- `listing_media_manifests`: adapter, source, counts, ordered manifest hash.
- `listing_media_items`: ordinal, URL, type, caption, content hash, dimensions, fetch/decode state.
- `listing_evidence_facts`: fact, tri-state value, quality, evidence media IDs/text.
- `listing_score_runs`: immutable input hash, versions, state, dimensions, weights, total, coverage.
- `score_epochs`: immutable rule/model cohort plus one atomically promoted active epoch.
- `listing_score_contradictions`: code and evidence references.

Constraints prevent duplicate jobs, changed payloads under reused run IDs, stale-worker finalization, and partial projection updates. Completed runs are immutable. Human history survives rescoring. New cohort results stay hidden until the epoch is validated and promoted atomically, preventing mixed-version ranking.

### Task 4: Listing score projection

**Modify:** `src/Shared/Domain.cs`, `src/Shared/Contracts.cs`, DTO mapping in `src/Server/Program.cs`.

Project current state, immutable run ID, coverage, media/floor-plan counts, contradictions, timestamps, and safe failure code. Do not create competing evidence authorities.

---

## Phase 3 — Native broker-first adapters

### Task 5: Adapter contract

**Create:**
- `src/ScoringWorker/Brokers/IBrokerListingAdapter.cs`
- `src/ScoringWorker/Brokers/BrokerListingSnapshot.cs`
- `src/ScoringWorker/Brokers/BrokerAdapterResolver.cs`
- `tests/HouseConsensus.UnitTests/BrokerAdapterResolverTests.cs`

Resolve canonical URL, description, declared property type, complete gallery, dedicated floor plans, and video separately. Preserve broker IDs, captions, ordinals, original URLs, and explicit unsupported/incomplete states.

### Task 6: Danbolig first

**Create:** `src/ScoringWorker/Brokers/DanboligAdapter.cs`, `tests/HouseConsensus.IntegrationTests/DanboligAdapterTests.cs`, sanitized fixtures under `tests/fixtures/danbolig/`.

Resolve property/broker IDs, consume gallery media, distinguish photos/composite plan/dedicated plans/video, and deduplicate without dropping plan variants. Mandatory Ladagervej fixture: 43 photos, one composite plan, three dedicated plans, one video.

### Task 7: Aggregator fallback

**Modify:** `src/Server/Listings/BoligsidenListingLookup.cs`
**Create:** `src/ScoringWorker/Brokers/BoligsidenFallbackAdapter.cs`

Boligsiden may seed previews. Supported broker media wins for scoring. Unsupported sources become explicit `Incomplete`.

---

## Phase 4 — Safe media manifests

### Task 8: Secure media fetcher

**Create:**
- `src/ScoringWorker/Media/SafeMediaFetcher.cs`
- `src/ScoringWorker/Media/MediaFetchOptions.cs`
- `tests/HouseConsensus.UnitTests/SafeMediaFetcherTests.cs`

Require HTTPS and per-adapter hostname allowlists. Reject loopback/private/link-local/metadata destinations on every redirect. Enforce redirect, timeout, byte, pixel, MIME, magic-byte, decompression, decoder, and concurrency limits. Never forward credentials cross-origin.

### Task 9: Immutable manifest service

**Create:** `src/ScoringWorker/Media/MediaManifestService.cs`, `tests/HouseConsensus.IntegrationTests/MediaManifestTests.cs`.

Stream/hash content, record every result, compute deterministic ordered manifest hash, cache by content hash, and never call partial fetches complete. Configure explicit per-item and per-listing byte/pixel/count/time budgets. If a complete broker manifest exceeds a budget, preserve the full inventory and mark the run `Incomplete`; never silently truncate or replace it with a five-image sample.

---

## Phase 5 — Structured extraction and scoring worker

### Task 10: Strict Ollama client

**Create:**
- `src/ScoringWorker/Vision/OllamaVisionClient.cs`
- `src/ScoringWorker/Vision/VisionExtractionSchema.cs`
- `tests/HouseConsensus.UnitTests/OllamaVisionClientTests.cs`

Use strict structured output. Preserve image ordinal/caption. Send no household/member/vote/note identifiers. Failure returns typed unavailable, never default facts. Store model, prompt, and schema versions.

### Task 11: Deterministic complete coverage

**Create:**
- `src/ScoringWorker/Vision/MediaBatchPlanner.cs`
- `src/ScoringWorker/Vision/EvidenceExtractor.cs`
- corresponding unit tests.

Analyse every dedicated floor plan individually, composite plans as support, every kitchen/bathroom/entrance/bedroom-or-wing image, every caption class, and all remaining stills in bounded labelled batches. Video is recorded but not evidence in v1. Cache by media hash + model + prompt/schema.

### Task 12: Evidence merger

**Create:** `src/Server/Scoring/EvidenceMerger.cs`, `tests/HouseConsensus.UnitTests/EvidenceMergerTests.cs`.

Visible positive evidence may set true. False requires readable disproof. Missing stays unknown. Floor plans outrank photo absence. Conflicts become review. Every fact references media IDs.

### Task 13: Contradiction detector

**Create:** `src/Server/Scoring/ContradictionDetector.cs`, `tests/HouseConsensus.UnitTests/ContradictionDetectorTests.cs`.

Detect broker two-family vs denied/unknown second unit, kitchen captions vs one-kitchen output, discovered plans not analysed, manifest/processed count mismatch, large score delta, and quiet status conflicting with louder road/rail/air evidence.

### Task 14: Dedicated durable scoring worker

**Create:**
- `src/ScoringWorker/HouseConsensus.ScoringWorker.csproj`
- `src/ScoringWorker/Program.cs`
- `src/ScoringWorker/ListingEnrichmentWorker.cs`
- `src/ScoringWorker/ListingEnrichmentService.cs`
- `tests/HouseConsensus.IntegrationTests/ListingEnrichmentWorkerTests.cs`

**Modify:** `HouseConsensus.sln`, `docker-compose.production.yml`. The web API only validates and enqueues work in PostgreSQL; it never fetches broker media or calls Ollama.

Use PostgreSQL skip-locked claims and leases, bounded concurrency, jittered retries, cancellation-safe transactional finalize, and manifest/input-hash stale-write prevention. Manual listings get highest priority. Persist invocation, parse, sufficiency, contradiction, and publication states separately. Failed attempts retain the last complete projection; update the visible projection only after validation and score-epoch promotion.

---

## Phase 6 — API and mobile-first UX

### Task 15: Owner status/rescore API

**Modify:** `src/Server/Program.cs`, `src/Shared/Contracts.cs`, `src/Client/Services/ApiClient.cs`.

Add owner enqueue/retry/rescore, status, immutable run summary, paged media/evidence, and review-resolution endpoints. Require owner auth, rate limits, bounded IDs, and optimistic concurrency.

### Task 16: Honest card state

**Modify:**
- `src/Client/Components/ListingCard.razor`
- `src/Client/Pages/Browse.razor`
- `src/Client/Pages/Queue.razor`
- `tests/HouseConsensus.UnitTests/ClientUiTests.cs`
- `tests/HouseConsensus.Playwright/specs/evidence-scoring.spec.ts`

Show compact `Pending`, `Incomplete`, `Needs review`, `Failed`, or numeric complete score. Never render unavailable privacy as zero. Sorting/filtering distinguish incomplete from low. Verify accessibility and 390px mobile geometry.

### Task 17: Detail provenance

**Modify:** `src/Client/Pages/Detail.razor`, `src/Client/Components/AiEvidencePanel.razor`
**Create:** `src/Client/Components/ScoreProvenance.razor`

Show breakdown, weights, discovered/decoded/analysed/floor-plan counts, broker source, decisive evidence, versions, contradictions, and owner rescore control. Use a mobile bottom sheet.

### Task 18: Owner review queue

**Modify:** `src/Client/Pages/Owner/Review.razor`, `src/Client/Layout/MainNavigation.razor`.

Prioritize contradictions, large deltas, and repeated failures. Resolution records member, time, reason, and accepted run without altering votes/notes.

---

## Phase 7 — Backfill and cutover

### Task 19: Native bounded backfill

**Create:** `src/ScoringWorker/EnrichmentBackfillService.cs`, `tests/HouseConsensus.IntegrationTests/EnrichmentBackfillTests.cs`. Add owner invocation in `Program.cs`.

Order: manual listings; broker-declared two-family listings; scores without readable-floor-plan provenance; remaining active/restored non-archived listings. Resumable, idempotent, rate-limited, and human-state preserving.

### Task 20: Historical reconciliation

Flag rather than silently replace when score delta is at least 15, privacy availability changes, state changes, or contradictions appear/clear. Ladagervej 1 is mandatory regression coverage.

### Task 21: Remove operational House Shopping dependency

After one verified native production cycle and rollback rehearsal, freeze `exporter/src/consensus_exporter/source.py` as migration-only compatibility code. Update `README.md` and deployment docs. No production schedule may require House Shopping SQLite. Keep rollback code until proven.

---

## Verification gates

```bash
/opt/data/.dotnet/dotnet test tests/HouseConsensus.UnitTests/HouseConsensus.UnitTests.csproj
/opt/data/.dotnet/dotnet build HouseConsensus.sln -c Release
/opt/data/.dotnet/dotnet test tests/HouseConsensus.IntegrationTests/HouseConsensus.IntegrationTests.csproj
cd tests/HouseConsensus.Playwright && npm run typecheck && ./scripts/run-e2e.sh
```

Use PostgreSQL test port 5434 only after explicit reset approval. Never reset production 5433.

Security gates: SSRF redirects/DNS rebinding, response/decode bombs, owner auth/rate limits, prompt identifier leakage, immutable runs/stale leases, canonical URL/duplicate identity.

Release: freeze exact staged index with full SHA-256; independent spec and security reviews; all tests/build/typecheck/Chromium; encrypted backup; immutable main-only image; Dockge deploy; verify `/api/version`, migration, queue health, and a real broker listing; bounded backfill; inspect every large delta.

## Risks and controls

- Broker changes: versioned adapters/fixtures; fail incomplete.
- SSRF/media attacks: strict allowlists and byte/pixel/decode controls.
- Ollama outage: durable retries; no partial publication.
- Cost: content-hash cache and bounded batches without weakening plan coverage.
- Hallucination: observable facts, provenance, deterministic scoring, contradictions.
- Comparability: immutable manifest/model/prompt/rule versions and coverage.
- Human-state loss: update score projection only; preserve all human data.
- Large deltas: owner review before trust.
- `Program.cs` growth: focused services and thin endpoints.

## Definition of done

- House Consensus independently discovers broker media and scores listings.
- Missing/unreadable floor plans produce `Incomplete`, never privacy zero.
- Contradictions produce `Needs review`.
- Every score is reproducible from immutable ordered media/evidence manifests.
- Mobile UI shows coverage and decisive evidence.
- Owners can retry/rescore and review changes.
- Manual listings start native scoring at highest priority.
- Human history remains intact.
- Ongoing production enrichment/scoring no longer requires House Shopping.
