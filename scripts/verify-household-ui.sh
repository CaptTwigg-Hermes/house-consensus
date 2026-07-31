#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
DOTNET="${DOTNET:-/opt/data/.dotnet/dotnet}"
BASE_URL="${E2E_BASE_URL:-http://127.0.0.1:8094}"
DATABASE_URL="${HOUSE_CONSENSUS_TEST_DATABASE_URL:-${TEST_DATABASE_URL:-}}"
ADMIN_URL="${HOUSE_CONSENSUS_TEST_ADMIN_URL:-}"
PLAYWRIGHT_TARGET="${PLAYWRIGHT_TARGET:-specs/household-votes.spec.ts}"
E2E_REPEAT="${E2E_REPEAT:-2}"
LOG="${TMPDIR:-/tmp}/house-consensus-household-ui-$$.log"
DB_TOOL="$ROOT/tools/HouseConsensus.TestDatabase/bin/Release/net10.0/HouseConsensus.TestDatabase.dll"
TEST_DATABASE_NAME=""
DATABASE_CREATED=false
SERVER_PID=""

if [[ -n "$ADMIN_URL" ]]; then
  TEST_DATABASE_NAME="house_consensus_test_e2e_$$"
  DATABASE_URL="$(ADMIN_URL="$ADMIN_URL" TEST_DATABASE_NAME="$TEST_DATABASE_NAME" python3 - <<'MAKE_URL'
import os
from urllib.parse import urlparse, urlunparse
raw = os.environ['ADMIN_URL']
name = os.environ['TEST_DATABASE_NAME']
if '://' in raw:
    parsed = urlparse(raw)
    print(urlunparse(parsed._replace(path='/' + name)))
else:
    parts = [part for part in raw.split(';') if part]
    replaced = False
    for index, part in enumerate(parts):
        if '=' in part and part.split('=', 1)[0].strip().lower() in ('database', 'initial catalog'):
            parts[index] = 'Database=' + name
            replaced = True
    if not replaced:
        parts.append('Database=' + name)
    print(';'.join(parts))
MAKE_URL
)"
elif [[ -z "$DATABASE_URL" ]]; then
  printf '%s\n' 'Provide a disposable test database URL or HOUSE_CONSENSUS_TEST_ADMIN_URL.' >&2
  exit 2
fi

DATABASE_URL="$DATABASE_URL" python3 - <<'CHECK'
import os
from urllib.parse import urlparse
raw = os.environ['DATABASE_URL']
if '://' in raw:
    name = urlparse(raw).path.lstrip('/')
else:
    parts = dict(part.split('=', 1) for part in raw.split(';') if '=' in part)
    name = parts.get('Database', parts.get('database', ''))
if 'test' not in name.lower() and 'e2e' not in name.lower():
    raise SystemExit(f"Refusing non-test database: {name or '<missing>'}")
CHECK

cleanup() {
  code=$?
  if [[ -n "$SERVER_PID" ]] && kill -0 "$SERVER_PID" 2>/dev/null; then
    kill "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
  if [[ "$DATABASE_CREATED" == true ]]; then
    TEST_DATABASE_ADMIN_URL="$ADMIN_URL" TEST_DATABASE_NAME="$TEST_DATABASE_NAME" \
      "$DOTNET" "$DB_TOOL" drop >/dev/null 2>&1 || true
  fi
  if [[ $code -eq 0 ]]; then rm -f "$LOG"; else printf 'Server log: %s\n' "$LOG" >&2; fi
}
trap cleanup EXIT

dotnet() { "$DOTNET" "$@"; }
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

if [[ -n "$TEST_DATABASE_NAME" ]]; then
  dotnet build tools/HouseConsensus.TestDatabase/HouseConsensus.TestDatabase.csproj -c Release --verbosity minimal
  TEST_DATABASE_ADMIN_URL="$ADMIN_URL" TEST_DATABASE_NAME="$TEST_DATABASE_NAME" \
    dotnet "$DB_TOOL" create
  DATABASE_CREATED=true
fi

dotnet test tests/HouseConsensus.UnitTests/HouseConsensus.UnitTests.csproj -c Release --no-restore --verbosity minimal
HOUSE_CONSENSUS_TEST_DATABASE_URL="$DATABASE_URL" dotnet test tests/HouseConsensus.IntegrationTests/HouseConsensus.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~E2E_test_auth|FullyQualifiedName~E2E_seed_creates|FullyQualifiedName~E2E_household_reset|FullyQualifiedName~E2E_ai_generator' --verbosity minimal
dotnet build HouseConsensus.slnx -c Release --no-restore --verbosity minimal
npm --prefix tests/HouseConsensus.Playwright run typecheck

if curl -fsS "$BASE_URL/health" >/dev/null 2>&1; then
  printf 'Refusing to reuse an existing server at %s\n' "$BASE_URL" >&2
  exit 3
fi

(
  cd src/Server
  ASPNETCORE_ENVIRONMENT=Development \
  ASPNETCORE_URLS="$BASE_URL" \
  ConnectionStrings__Database="$DATABASE_URL" \
  Database__AutoMigrate=true \
  E2E__SeedData=true \
  E2E__TestAuth=true \
  CloudflareAccess__Enabled=true \
  CloudflareAccess__TeamDomain=e2e.cloudflareaccess.com \
  CloudflareAccess__Audience=e2e-house-consensus \
  Debug__AutoLogin=true \
  INITIAL_OWNER_EMAIL=owner@example.test \
  exec "$DOTNET" bin/Release/net10.0/HouseConsensus.Server.dll
) >"$LOG" 2>&1 &
SERVER_PID=$!

ready=0
for _ in $(seq 1 60); do
  if ! kill -0 "$SERVER_PID" 2>/dev/null; then
    printf '%s\n' 'E2E server exited before readiness.' >&2
    exit 4
  fi
  if curl -fsS "$BASE_URL/health" >/dev/null 2>&1; then ready=1; break; fi
  sleep 0.5
done
if [[ $ready -ne 1 ]]; then
  printf '%s\n' 'E2E server did not become healthy.' >&2
  exit 5
fi

for _ in $(seq 1 "$E2E_REPEAT"); do
  (
    cd tests/HouseConsensus.Playwright
    E2E_BASE_URL="$BASE_URL" E2E_TEST_AUTH=1 npx playwright test "$PLAYWRIGHT_TARGET" --project=chromium --reporter=line
  )
done

git diff --check
printf 'Household UI verification passed (%s run(s)).\n' "$E2E_REPEAT"
