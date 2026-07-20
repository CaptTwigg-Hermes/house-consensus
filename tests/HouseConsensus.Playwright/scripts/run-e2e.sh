#!/usr/bin/env sh
set -eu

BASE_URL="${E2E_BASE_URL:-http://app:8080}"
MAILPIT_URL="${MAILPIT_API_URL:-http://mailpit:8025}"
TIMEOUT="${E2E_WAIT_SECONDS:-120}"

wait_for() {
  name="$1"; url="$2"; elapsed=0
  until node -e "fetch(process.argv[1]).then(r=>{if(!r.ok)process.exit(1)}).catch(()=>process.exit(1))" "$url"; do
    elapsed=$((elapsed + 2))
    if [ "$elapsed" -ge "$TIMEOUT" ]; then
      echo "Timed out waiting for $name at $url" >&2
      exit 1
    fi
    sleep 2
  done
  echo "$name ready: $url"
}

wait_for application "$BASE_URL"
wait_for Mailpit "$MAILPIT_URL/api/v1/info"
exec npx playwright test "$@"
