# House Consensus

Private invite-only household house-evaluation app. This monorepo contains a .NET 10 hosted Blazor WebAssembly client, ASP.NET Core API/cookie auth/SignalR server, PostgreSQL persistence, Python houseshopping exporter, and Playwright tests. `SPEC.md` is authoritative.

## Local stack

1. Create an untracked `.env` containing strong `POSTGRES_PASSWORD`, `INITIAL_OWNER_EMAIL`, and `PUBLIC_ORIGIN` values.
2. Start only on loopback:

```sh
docker compose -f docker-compose.yml -f docker-compose.dev.yml up --build --wait
```

App: `http://127.0.0.1:8080`; Mailpit: `http://127.0.0.1:8025`. The first startup creates exactly one owner from `INITIAL_OWNER_EMAIL`. Base Compose exposes no host ports; the development overlay binds loopback only.

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

The application starts with an empty listing database. Import the real houseshopping SQLite data into the local Compose PostgreSQL database with:

```sh
docker compose -f docker-compose.yml -f docker-compose.dev.yml --profile tools run --rm --build importer
```

The importer reads `../houseshopping/state/house.db` by default. Set `HOUSESHOPPING_DB` when the SQLite file is elsewhere. Re-running the importer is safe and refreshes listings idempotently.

`/workspace/houseshopping` runs `export_consensus` after publish in an isolated subprocess when `CONSENSUS_EXPORT=1` is configured. That stage is non-fatal, logs failure to stderr, and leaves existing alert stdout and pipeline status unchanged.

## Production handoff and backup

Do not deploy this development Compose file. For TrueNAS/Dockge use external PostgreSQL, TLS through Cloudflare Tunnel, platform-managed required secrets, and no public service ports. Production cookies are always Secure. Schedule `scripts/backup-postgres.sh` daily; it makes an AES-256 encrypted dump and keeps 30 days. Keep `BACKUP_PASSPHRASE_FILE` as a protected mounted secret and test restores into a disposable database regularly.
