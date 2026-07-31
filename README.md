# House Consensus

Private invite-only household house-evaluation app. This monorepo contains a .NET 10 hosted Blazor WebAssembly client, ASP.NET Core API/Cloudflare Access auth/SignalR server, PostgreSQL persistence, Python houseshopping exporter, and Playwright tests. `SPEC.md` is authoritative.

## Local stack

1. Create an untracked `.env` containing strong `POSTGRES_PASSWORD`, `INITIAL_OWNER_EMAIL`, and `PUBLIC_ORIGIN` values. The stack uses the external PostgreSQL database `house_consensus` at `192.168.50.2:5433` by default; override `POSTGRES_HOST`, `POSTGRES_PORT`, `POSTGRES_DB`, or `POSTGRES_USER` when needed.
2. Start only on loopback:

```sh
docker compose -f docker-compose.yml -f docker-compose.dev.yml up --build --wait
```

App: `http://127.0.0.1:8080`; Mailpit: `http://127.0.0.1:8025`. The first startup creates exactly one owner from `INITIAL_OWNER_EMAIL`. Base Compose exposes no host ports; the development overlay binds loopback only.

Set `DEBUG_AUTO_LOGIN=true` in `.env` to authenticate automatically as `INITIAL_OWNER_EMAIL` when using `docker-compose.dev.yml`. The flag is wired only into the Development overlay; enabling it in any other ASP.NET environment aborts startup.

Owner-triggered AI learning defaults to the trusted LAN Ollama endpoint at `192.168.50.227:11434` with `gemma4:12b`. Override `AI_LEARNING_BASE_URL`, `AI_LEARNING_MODEL`, and optionally `AI_LEARNING_API_KEY` for another deployment. Public endpoints must use HTTPS. Plain HTTP requires both `AI_LEARNING_ALLOW_INSECURE_HTTP=true` and an exact host match in `AI_LEARNING_INSECURE_HTTP_ALLOWED_HOSTS`; the Compose default allowlists only `192.168.50.227`.

## Verification

```sh
docker build -f Dockerfile.test -t house-consensus-test .
docker run --rm -v /var/run/docker.sock:/var/run/docker.sock house-consensus-test
TEST_DATABASE_URL='postgresql://user:password@host:5434/house_consensus_test' \
  uv run --project exporter --with pytest --with pytest-asyncio --with 'psycopg[binary]' --with httpx pytest -q tests/exporter
HOUSE_CONSENSUS_TEST_DATABASE_URL='Host=host;Port=5434;Database=house_consensus_dotnet_test;Username=user;Password=password' \
  dotnet test HouseConsensus.slnx -c Release
docker compose -f tests/HouseConsensus.Playwright/compose.e2e.yml up --build --abort-on-container-exit --exit-code-from playwright
```

External integration databases are reset by the tests and their names must contain `test`. Never point either variable at a production database. Without the variables, .NET integration tests fall back to Testcontainers.

See `tests/HouseConsensus.Playwright/README.md` and `exporter/README.md`.

## Houseshopping integration

The application database is external to Compose. Import the real houseshopping SQLite data into the same PostgreSQL database used by the app with:

On Windows/PowerShell, use the wrapper that stages the SQLite file on local disk before mounting it into Docker Desktop (Docker cannot bind-mount a UNC path):

```powershell
.\scripts\import-houses.ps1
```

On Linux, run:

```sh
docker compose -f docker-compose.yml -f docker-compose.dev.yml --profile tools run --rm --build importer
```

The importer reads `../houseshopping/state/house.db` by default. Pass `-SourceDb` to the PowerShell wrapper or set `HOUSESHOPPING_DB` on Linux when the SQLite file is elsewhere. Re-running the importer is safe and refreshes listings idempotently.

`/workspace/houseshopping` runs `export_consensus` after publish in an isolated subprocess when `CONSENSUS_EXPORT=1` is configured. That stage is non-fatal, logs failure to stderr, and leaves existing alert stdout and pipeline status unchanged.

Record a listing independently verified as delisted before the next export. This transaction writes the durable tombstone and archives any current listing; future imports serialize on the same external ID and skip it:

```sh
uv run --project exporter house-consensus-export \
  --database-url "$CONSENSUS_DATABASE_URL" \
  --tombstone-external-id "SOURCE_EXTERNAL_ID" \
  --tombstone-source-url "https://example.invalid/original-listing" \
  --verification-method http_404
```

## Production image and Dockge Compose

Every push to `main` publishes `ghcr.io/capttwigg-hermes/house-consensus:latest` plus an immutable commit-SHA tag. `docker-compose.production.yml` uses `pull_policy: always`, so a Dockge **Update/Recreate** pulls the newest published image without cloning or building the repository.

1. Copy `docker-compose.production.yml` into a Dockge stack. This stack uses the existing TrueNAS Cloudflare Tunnel; it does not create another connector or require a tunnel token.
2. Set `DATABASE_CONNECTION_STRING`, `INITIAL_OWNER_EMAIL`, and `PUBLIC_ORIGIN` in Dockge. `DATABASE_CONNECTION_STRING` must contain the full PostgreSQL Npgsql connection string; `PUBLIC_ORIGIN` is the HTTPS hostname already published by Cloudflare.
3. In Cloudflare Zero Trust, open the existing Access application. Copy its application audience (`AUD`) into `CLOUDFLARE_ACCESS_AUDIENCE`. Set `CLOUDFLARE_ACCESS_TEAM_DOMAIN` to the team hostname only, such as `team-name.cloudflareaccess.com`.
4. Point the existing tunnel route at this app's host and port. Set `APP_BIND_IP` to the TrueNAS LAN IP only when the connector cannot reach the loopback default. The origin rejects requests without a valid signed Access assertion even on the LAN.
5. Ensure the Access policy allows the household identities. Cloudflare authenticates identity; House Consensus still requires an active member or pending owner-created invitation and retains owner/member authorization.
6. For a private GHCR package, authenticate Docker/Dockge to `ghcr.io` with a GitHub token that has `read:packages`, or make the package public.
7. After a green publish workflow, use Dockge **Update** (or run `docker compose -f docker-compose.production.yml pull && docker compose -f docker-compose.production.yml up -d`).

The `latest` tag updates only after a successful image build from `main`. Use `ghcr.io/capttwigg-hermes/house-consensus:sha-<full-commit-sha>` in the Compose file when a deployment must be pinned.

## Production handoff and backup

Do not deploy the loopback development overlay. Production requires Cloudflare Access and validates `Cf-Access-Jwt-Assertion` using Cloudflare's rotating keys, exact issuer, application audience, and token lifetime. Cloudflare terminates public HTTPS; the app does not redirect the tunnel's internal HTTP hop. Raw identity headers are not trusted. Magic links remain available only for non-production test/development flows. For TrueNAS/Dockge keep the external PostgreSQL connection, use TLS through the existing Cloudflare Tunnel, platform-managed required secrets, and no public service ports. Schedule `scripts/backup-postgres.sh` daily; it makes an AES-256 encrypted dump and keeps 30 days. Keep `BACKUP_PASSPHRASE_FILE` as a protected mounted secret and test restores into a disposable database regularly.
