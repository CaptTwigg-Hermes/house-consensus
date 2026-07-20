# House Consensus contributor rules

- `SPEC.md` is authoritative.
- Strict TDD: failing test first, expected failure, minimal implementation, pass, refactor.
- .NET 10 LTS. Build/test in containers; do not install SDK on host.
- Same-origin HttpOnly cookie auth. Never store tokens in browser storage.
- One household only. Do not add multi-tenancy abstractions.
- Owner overrides and audit history are immutable business facts.
- Export failure must never break houseshopping.
- No secrets, runtime state, cached media, or database dumps in git.
- Mobile-first; reuse houseshopping visual language; no default Blazor UI.
- All UI text has English and Danish resources.
- Verify with unit, PostgreSQL integration, and Playwright tests.
