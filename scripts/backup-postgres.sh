#!/bin/sh
set -eu
: "${DATABASE_URL:?DATABASE_URL is required}"
: "${BACKUP_PASSPHRASE_FILE:?BACKUP_PASSPHRASE_FILE is required}"
[ -r "$BACKUP_PASSPHRASE_FILE" ] || { echo "passphrase file is not readable" >&2; exit 1; }
BACKUP_DIR=${BACKUP_DIR:-/backups}
mkdir -p "$BACKUP_DIR"
umask 077
stamp=$(date -u +%Y%m%dT%H%M%SZ)
tmp="$BACKUP_DIR/.house-consensus-$stamp.dump.enc.tmp"
out="$BACKUP_DIR/house-consensus-$stamp.dump.enc"
cleanup() { rm -f "$tmp"; }
trap cleanup EXIT HUP INT TERM
pg_dump --dbname="$DATABASE_URL" --format=custom --no-owner --no-acl \
  | openssl enc -aes-256-cbc -salt -pbkdf2 -iter 200000 -pass "file:$BACKUP_PASSPHRASE_FILE" -out "$tmp"
test -s "$tmp"
mv "$tmp" "$out"
find "$BACKUP_DIR" -type f -name 'house-consensus-*.dump.enc' -mtime +30 -delete
trap - EXIT HUP INT TERM
printf '%s\n' "$out"
