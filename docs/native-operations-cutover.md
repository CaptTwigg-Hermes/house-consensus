# Native worker operations and scheduler cutover

Status: **NOT CUT OVER**. This runbook prepares a controlled production change;
committing it does not alter any scheduler or retire any existing service.

## Scope and invariants

- Run ingestion and manual scoring only from a reviewed House Consensus commit.
- Keep the exporter available until all of its consumers have a proven native
  replacement. Exporter removal is a separate approved change.
- Supply database credentials through the process environment or a protected
  House Consensus environment file. Never put credentials in scheduler command
  lines, logs, or source control.
- Keep the application, migrations, and worker checkout at the same release.
- Allow at most one instance of each worker. The two workers have independent
  locks and may run concurrently with each other.

## Repository entrypoints

| Worker | Wrapper | Command executed from the repository root |
| --- | --- | --- |
| Ingestion | `scripts/run-native-ingestion.sh` | `uv run --project ingestion house-consensus-ingest` |
| Manual scoring | `scripts/run-native-manual-scoring.sh` | `uv run --project manual_scoring house-consensus-manual-scorer` |

Both wrappers:

1. resolve their repository root and accept an explicit absolute override in
   `HOUSE_CONSENSUS_ROOT`;
2. optionally load `HOUSE_CONSENSUS_ENV_FILE`, which must be absolute and
   readable (otherwise `$HOUSE_CONSENSUS_ROOT/.env` is used only when present);
3. acquire a non-blocking `flock` under `HOUSE_CONSENSUS_LOCK_DIR`, defaulting
   to `${XDG_RUNTIME_DIR:-/tmp}/house-consensus`;
4. write only lifecycle messages to stderr, leave worker stdout and stderr
   attached to the scheduler, and return the worker's exact exit status.

An overlap exits `75` without starting another worker. Configuration errors use
`64`, an explicitly configured unreadable environment file uses `66`, a missing
runtime command uses `69`, an invalid root uses `72`, and an unusable lock path
uses `73`. The wrappers never print environment values or command arguments.
The manual-scoring wrapper rejects `--database-url` and its abbreviated forms; set
`CONSENSUS_DATABASE_URL` in the environment file instead.

## Deployment preflight

1. Obtain written approval naming the operator, release commit, cutover window,
   rollback owner, and observation period.
2. Deploy the reviewed commit to a stable absolute path, for example
   `/srv/house-consensus`. Do not schedule a mutable development checkout.
3. Install `uv` and `flock` for the scheduler account and verify that account can
   read the checkout and create the selected lock directory.
4. Create an absolute environment file outside the checkout, owned by the
   scheduler account with mode `0600`. Set `DATABASE_URL` for ingestion and
   `CONSENSUS_DATABASE_URL` for manual scoring. Add only other settings required
   by the selected source resolver and scoring pipeline.
5. Apply the matching House Consensus application migrations before enabling a
   writer. Confirm the application is healthy afterward.
6. Confirm the exact commands exist in this release without exposing values:

   ```sh
   cd /srv/house-consensus
   uv run --project ingestion house-consensus-ingest --help >/dev/null
   uv run --project manual_scoring house-consensus-manual-scorer --help >/dev/null
   ```

7. Record the current scheduler definition and recent successful run evidence in
   the change ticket. Preparation alone is not proof that production changed.

## Backup and restore gate

Before enabling either native writer, run the repository backup script using the
same database target, record the returned encrypted artifact path and checksum,
and copy the artifact to the approved retention location:

```sh
HOUSE_CONSENSUS_ENV_FILE=/etc/house-consensus/native.env
set -a
. "$HOUSE_CONSENSUS_ENV_FILE"
set +a
BACKUP_DIR=/var/backups/house-consensus \
BACKUP_PASSPHRASE_FILE=/run/secrets/house-consensus-backup-passphrase \
  /srv/house-consensus/scripts/backup-postgres.sh
```

Do not treat file creation as restore proof. Restore the selected encrypted dump
into a new disposable database, run `pg_restore --list` against the decrypted
stream or restore it fully, then verify application migrations and representative
row counts. Record the artifact checksum, disposable database name, commands,
timestamps, and results. Delete the disposable database only after approval.

## Pre-cutover verification

Run the exact release gates against a dedicated test database whose name contains
`test`:

```sh
TEST_DATABASE_URL="$TEST_DATABASE_URL" uv run --project ingestion --extra test pytest -q tests/ingestion
TEST_DATABASE_URL="$TEST_DATABASE_URL" uv run --project exporter --extra test pytest -q tests/exporter
TEST_DATABASE_URL="$TEST_DATABASE_URL" uv run --project manual_scoring --extra 'postgres,test' pytest -q manual_scoring/tests
sh -n scripts/run-native-ingestion.sh scripts/run-native-manual-scoring.sh
```

Then use the ingestion wrapper for a read-only source dry run with the approved
scope and filters. Check that stdout is one valid JSON result, stderr contains
the wrapper start/finish lines, the count is plausible, and the exit status is
zero. Do not continue on an empty, partial, failed, or surprising result.

## Scheduler cutover

Use separate scheduler entries and an absolute checkout. The following command
shapes are examples; substitute the approved source filters and production
component modules, but never add credentials:

```sh
HOUSE_CONSENSUS_ROOT=/srv/house-consensus \
HOUSE_CONSENSUS_ENV_FILE=/etc/house-consensus/native.env \
HOUSE_CONSENSUS_LOCK_DIR=/run/lock/house-consensus \
  /srv/house-consensus/scripts/run-native-ingestion.sh \
  --boligsiden --execute --municipality 101 --address-type villa \
  --price-min 1000000 --price-max 3000000

HOUSE_CONSENSUS_ROOT=/srv/house-consensus \
HOUSE_CONSENSUS_ENV_FILE=/etc/house-consensus/native.env \
HOUSE_CONSENSUS_LOCK_DIR=/run/lock/house-consensus \
  /srv/house-consensus/scripts/run-native-manual-scoring.sh \
  --source-resolver approved_package.source:build_resolver \
  --scoring-pipeline approved_package.pipeline:build_pipeline
```

At the cutover hold point:

1. Confirm backup and restore evidence, release gates, dry run, and approval.
2. Prevent the previous ingestion schedule from starting new work, but preserve
   its definition and artifacts for rollback. Confirm no previous run remains.
3. Trigger one native ingestion invocation manually. Require exit `0`; retain
   its complete stdout and stderr as evidence.
4. Verify the emitted run ID, source scope, manifest, source count, terminal
   success, projected count, and representative listing rows directly in
   PostgreSQL. Confirm no failed/running run was used for projection.
5. Trigger one manual-scoring invocation. Accept `idle` or a successfully
   persisted `completed` result; investigate `failed` or `lost_lease` before
   enabling its schedule.
6. Enable the native schedules, inspect the scheduler's persisted definitions,
   and observe at least one scheduled run of each entry. A successful manual
   invocation alone does not prove the scheduler is active.
7. Confirm overlap attempts return `75`, scheduler logs contain complete worker
   output, and no secret values appear in commands or logs.

## Post-cutover verification

For every run during the observation period, record scheduler start/end time,
exit status, ingestion run ID and manifest, source/projected counts, manual-score
status, queue depth, and application health. Alert on nonzero exits, repeated
lock contention, stale running ingestion rows, count discontinuities, retry
growth, or missing projections. Compare representative application reads with
the committed source identity, not only aggregate counts.

## Rollback

1. Disable both native scheduler entries and confirm no native process still
   holds either lock.
2. Preserve logs and database evidence for the failed run. Do not rewrite its
   terminal status or delete audit rows.
3. If the database remains valid, re-enable the preserved previous schedule and
   verify one run. Never allow both ingestion schedules to overlap.
4. Restore a database backup only for confirmed data corruption and only with
   the rollback owner's approval: stop all writers, retain a pre-restore copy,
   restore the verified artifact, reapply the matching release if needed, and
   verify application reads before resuming one scheduler.
5. Record the final scheduler state, active release SHA, database decision, and
   follow-up owner. A code rollback alone is not scheduler rollback proof.

## Controlled retirement

The previous schedule and exporter remain **NOT RETIRED** throughout preparation
and initial cutover. After the approved observation period, inventory all active
schedulers and downstream consumers. Retirement requires documented proof that
native ingestion is complete and stable, every required exporter consumer has a
tested replacement, retained artifacts satisfy policy, and rollback is no longer
required. Remove those components only in a separate reviewed and approved
change; this runbook does not authorize deletion.
