#!/bin/sh
set -eu

fail() {
    code=$1
    shift
    printf 'native ingestion: %s\n' "$*" >&2
    exit "$code"
}

script_root=$(CDPATH='' cd -- "$(dirname -- "$0")/.." && pwd -P)
root=${HOUSE_CONSENSUS_ROOT:-$script_root}
case "$root" in
    /*) ;;
    *) fail 64 "HOUSE_CONSENSUS_ROOT must be an absolute path" ;;
esac
root=$(CDPATH='' cd -- "$root" 2>/dev/null && pwd -P) || fail 72 "HOUSE_CONSENSUS_ROOT is not an accessible directory"
[ -d "$root/ingestion" ] || fail 72 "ingestion project is missing under HOUSE_CONSENSUS_ROOT"

explicit_env=${HOUSE_CONSENSUS_ENV_FILE+x}
env_file=${HOUSE_CONSENSUS_ENV_FILE:-$root/.env}
case "$env_file" in
    /*) ;;
    *) fail 64 "HOUSE_CONSENSUS_ENV_FILE must be an absolute path" ;;
esac
if [ -n "$explicit_env" ] && [ ! -r "$env_file" ]; then
    fail 66 "HOUSE_CONSENSUS_ENV_FILE is not readable"
fi
if [ -r "$env_file" ]; then
    set -a
    # Deployment chooses the absolute environment file.
    # shellcheck disable=SC1090
    . "$env_file"
    set +a
fi

command -v uv >/dev/null 2>&1 || fail 69 "uv is not available on PATH"
command -v flock >/dev/null 2>&1 || fail 69 "flock is not available on PATH"

lock_dir=${HOUSE_CONSENSUS_LOCK_DIR:-${XDG_RUNTIME_DIR:-/tmp}/house-consensus}
case "$lock_dir" in
    /*) ;;
    *) fail 64 "HOUSE_CONSENSUS_LOCK_DIR must be an absolute path" ;;
esac
umask 077
mkdir -p -- "$lock_dir" || fail 73 "cannot create lock directory"
lock_file=$lock_dir/native-ingestion.lock
touch "$lock_file" || fail 73 "cannot open ingestion lock"
exec 9>"$lock_file"
flock -n 9 || fail 75 "native ingestion is already running"

cd -- "$root"
printf 'native ingestion: start root=%s\n' "$root" >&2
set +e
uv run --project ingestion house-consensus-ingest "$@"
status=$?
set -e
printf 'native ingestion: finish exit=%s\n' "$status" >&2
exit "$status"
