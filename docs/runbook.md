# Operational runbook

For the pilot host set up as in [production-setup.md](production-setup.md). Commands run from the directory that holds
`deploy/`, as root or a member of the `docker` group; `dc` below stands for `docker compose --env-file deploy/.env`.
The design behind this page is [`plans/platform/repo-and-deployment.md`](../plans/platform/repo-and-deployment.md)
§6 and §7.4.

## Ownership and targets

Name a service owner, an on-call function, the data protection officer and the records owner (registrar) before real
data arrives. Pilot targets: **RPO ≤ 5 minutes, RTO ≤ 4 hours** for the database; uploads copied off the host within
15 minutes. Retention of the backups is 35 days (proposed).

## Everyday commands

```bash
dc ps                                    # state and health of every container
dc logs --since 1h rag-worker            # one service's log
dc restart rag-api                       # restart one service
dc stop                                  # stop everything, keep the volumes
dc up -d --wait                          # start (or apply a changed .env or image)
```

Never run `dc down --volumes` on a deployment: it deletes `postgres-data`, `rag-blobs`, the signatures and, with a
`local` backup target, the backup repository. A plain `dc down` keeps the volumes. Every .NET service answers
`/health/live`, `/health/ready` and `/version`; the published ones are on `https://localhost:8543` (api) and `:9543`
(admin), with the internal CA in `deploy/certs/ca.pem`.

## Backups

### What runs

| Unit | Runs | When |
|---|---|---|
| `rag-backup.timer` | `backup-postgres.sh --type auto`: a pgBackRest backup inside the postgres container; full on Sundays or when the newest full is 7 days old (or there is none), else differential; expiry follows each backup | daily at 01:30 (up to 15 minutes later), caught up after downtime |
| `rag-backup-status.timer` | `backup-postgres.sh --status-only`: the metrics below, from `pgbackrest info` and `pg_stat_archiver` | every 2 minutes |
| `rag-upload-backup.timer` | `backup-uploads.sh`: new files below `uploads/` of the blob store to the upload copy, never overwriting | every 10 minutes (not with an S3 target) |
| `archive_command` in postgres | `pgbackrest archive-push`: every WAL segment, synchronously | at least every 60 s (`archive_timeout`) when anything was written |

Derived blobs (fetched pages, parse outputs) are not backed up: a restore fetches and parses them again. The pepper
(`deploy/secrets/.pepper`), `deploy/certs/data-protection.pfx` and the rest of `deploy/secrets/` are backed up
separately, through the municipality's secret store; the blob master key and the pgBackRest passphrase are also
escrowed (*Key escrow*).

```bash
systemctl list-timers 'rag-*'
journalctl -u rag-backup.service --since yesterday
dc exec -T postgres pgbackrest --stanza=ragdb info      # backup sets and the archived WAL range
```

### `backup-postgres.sh`

```bash
sudo deploy/backup-postgres.sh --type full               # by hand: full, diff or auto
sudo deploy/backup-postgres.sh --type full --start-fast  # an immediate checkpoint instead of a spread one (drills)
sudo deploy/backup-postgres.sh --status-only             # refresh the metrics only
```

`--env-file FILE` (default `deploy/.env`) picks the deployment, and `--state-dir DIR` (default `$STATE_DIRECTORY`, else
`/var/lib/ume-rag`) where `backup.prom` and `backup-last-run` go. A diff without a full becomes a full. A failed run is
recorded (`rag_backup_last_run_success 0`) and the metrics are refreshed if the database still answers.

### Metrics and alerts

The scripts write Prometheus text files to `/var/lib/ume-rag/` (`backup.prom`, `upload-backup.prom`), atomically.
Point node_exporter's textfile collector, or the collector that reads them, at that directory (how they reach the
municipal monitoring is still to be settled). Every value is read from the repository and `pg_stat_archiver` at run
time, so a backup taken by any means is reported.

| Metric | Meaning |
|---|---|
| `rag_backup_age_seconds` | since the end of the newest backup of any type |
| `rag_backup_last_timestamp_seconds{type}` | end of the newest `full`, `diff` and `incr` backup (0: none) |
| `rag_backup_repo_status` | pgBackRest's stanza status code: 0 ok, 2 no valid backup, 99 the repository could not be read |
| `rag_backup_wal_archive_age_seconds` | since the newest archived WAL while unarchived WAL exists; 0 when everything written is archived |
| `rag_backup_wal_last_archived_timestamp_seconds`, `rag_backup_wal_archive_failed_total`, `rag_backup_wal_last_failed_timestamp_seconds` | from `pg_stat_archiver` |
| `rag_backup_last_run_success`, `rag_backup_last_run_timestamp_seconds` | the outcome of the last scheduled backup |
| `rag_backup_status_timestamp_seconds` | when these values were read |
| `rag_upload_backup_last_success_timestamp_seconds`, `rag_upload_backup_copied`, `rag_upload_backup_files` | the upload copy: last success, files copied by the last run, files in the copy |

Proposed alerts (§7.4 rule 5):

- `rag_backup_age_seconds > 93600`: no backup for 26 hours;
- `rag-backup.service` failed, or `rag_backup_last_run_success == 0`;
- `rag_backup_wal_archive_age_seconds > 300` for 2 minutes: the newest archived WAL is older than the 5-minute RPO.
  With `archive_timeout` 60 s a busy server reads at most about 60 s; the 2 minutes absorb the first write after an
  idle spell;
- `rag_backup_repo_status != 0`, or `rag_backup_status_timestamp_seconds` older than 10 minutes (the status timer
  stopped);
- `rag_upload_backup_last_success_timestamp_seconds` older than 30 minutes.

## Restore (point in time)

`deploy/restore-pitr.sh` restores ragdb **in place** to a moment you choose. It replaces the current database:
everything archived before it starts stays restorable, nothing written after its first step is.

1. **Choose the time.** Find the last good moment (an incident report, the audit log). Check that it lies inside the
   archived range with `dc exec -T postgres pgbackrest --stanza=ragdb info`. Write it with a UTC offset; without one the
   host's time zone applies. Transactions committed up to and including that time are kept.
2. **Preview.** Without `--confirm` the script prints what it would do and stops:

   ```bash
   sudo deploy/restore-pitr.sh --target-time '2026-10-30 21:15:00+01:00'
   ```

   It names the Compose project, the target time, the backup set (`--set LABEL` picks one instead of the newest that
   ended before the target) and the services it will stop.
3. **Restore**, in an announced maintenance window:

   ```bash
   sudo deploy/restore-pitr.sh --target-time '2026-10-30 21:15:00+01:00' --confirm
   ```

   It then:
   1. closes the current WAL segment and waits up to 60 s until it is archived, so the restore can reach the newest
      commits;
   2. stops `rag-api`, `rag-admin` and `rag-worker` (those that run), then postgres;
   3. runs `pgbackrest restore --type=time --delta` in a one-off container of the postgres service (same image, user,
      volumes and repository settings) on the `postgres-data` volume;
   4. starts postgres, which recovers to the target and pauses there, and prints the last transaction it replayed;
   5. promotes it to a new timeline (the old one stays in the repository) and runs `pgbackrest check`;
   6. starts the services it stopped, waits until they are healthy, and prints the time of each step and the total
      (the achieved RTO).

   `--timeout SECONDS` (default 10800) bounds the recovery. If the repository has no WAL up to the target, it stops
   with "the repository has no WAL up to …": pick an earlier time. `--env-file FILE` picks another deployment.
4. **Check the data** (the drill's checks below), then **take a full backup** at once, so the new timeline has its own
   base:

   ```bash
   sudo deploy/backup-postgres.sh --type full
   ```

5. **Blobs.** Blobs are write-once and purged only after the backup retention, so the restored database finds every
   blob it references. The worker's orphan audit (§7.4 rule 2) then reports blobs without a referencing row (left to
   the purge) and documents whose derived blob is missing (fetched or parsed again on the next sync). The audit comes
   with the ingestion pipeline (P1). A missing **upload** is restored from the upload copy (below).

**Timings.** The local drill (a 30 MB database, local repository) took **23.0 s** end to end: archive and stop 2.1 s,
restore 0.9 s, recovery to the target 2.9 s, promote 1.6 s, check 0.3 s, services healthy 15.2 s. A second restore on
the new timeline took 22.7 s. Restore and recovery grow with the database size and the WAL since the chosen backup, and
a share or S3 target is slower than a local volume: the test-host drill measures realistic numbers.

### Restoring uploads from the upload copy

The upload copy holds every file below `uploads/` of the blob store, under the same key (`<backup target>/uploads/`,
or `deploy/upload-backup/uploads/` with a `local` target). Keys are write-once, so copying back only what is missing is
always safe. With the stack running:

```bash
dir="$(sed -n 's/^UME_RAG_UPLOAD_BACKUP_DIR=//p' deploy/.env)"
docker run --rm --network none --user 1654:1654 --read-only \
  -v ume-rag_rag-blobs:/data/blobs -v "$dir/uploads:/backup/uploads:ro" \
  --entrypoint sh ume-rag/postgres:latest -c '
    cd /backup/uploads && find . -type f ! -name "*.partial" | while IFS= read -r f; do
      [ -e "/data/blobs/rag/uploads/$f" ] && continue
      mkdir -p "/data/blobs/rag/uploads/$(dirname "$f")" && cp -p "$f" "/data/blobs/rag/uploads/$f"
    done'
```

Use the deployment's `POSTGRES_IMAGE` if it is pinned (any image with a POSIX shell works). This step has not been
drilled yet: uploads arrive with the admin UI (P1).

### Restoring onto another host

Not drilled yet; the test-host drill confirms it. The order matters, because `init-deployment.sh` never overwrites
existing files:

1. Before running `init-deployment.sh` on the new host, put back from the escrow and the secret store:
   `secrets/pgbackrest/cipher-pass` (owner 999, mode 0600), `secrets/keys/rag-blob-internal` (owner 1654, mode 0400),
   `secrets/.pepper` and `certs/data-protection.pfx`. The escrow set's `MANIFEST` says where each key goes.
2. Run `init-deployment.sh` with the same `--backup-target`, then the install steps of production-setup.md up to
   `up --wait` (no `stanza-create`: the stanza exists).
3. Run `restore-pitr.sh --target-time … --confirm`, then take a full backup.

For a drill (not a real loss of the production host), give the drill host a **copy** of the repository, not the
production repository itself: after the restore it archives WAL and its backup timer takes backups, which must not
land in production's repository.

## Key escrow

`deploy/escrow-keys.sh` exports the keys no backup can replace to escrow media and reads them back:

- `secrets/keys/rag-blob-*`: the blob master keys of the file key provider (`rag-blob-internal`). Without them the
  `internal` blobs cannot be decrypted. Chat master keys (`rag-chat-<level>`) join once stored chat content is
  allowed (A1); there are none in P0 and P1.
- `secrets/pgbackrest/cipher-pass`: the repository passphrase. Without it no backup can be read.

```bash
sudo deploy/escrow-keys.sh /media/escrow-a              # writes /media/escrow-a/ume-rag-keys-<project>-<UTC time>/
sudo deploy/escrow-keys.sh --verify /media/escrow-a     # the newest set there, or name one set
```

The export writes the keys with `SHA256SUMS` and a `MANIFEST` (what each key is and where it goes back), read-only for
the owner. `--verify` checks the checksums and the key formats, and that every key equals this deployment's current
one (and the passphrase equals `repo1-cipher-pass` in `repo.conf`); it prints `PASS` and exits 0, or names each
`FAIL`. `--env-file FILE` or `--secrets-dir DIR` points it at another deployment.

- **Seal the media in two places** (or use the key service's own backup). The files are in the clear: whoever holds
  the media can read every `internal` blob and every backup.
- **Keep each set as long as the data and backups it protects.**
- **Re-run after every rotation**, and verify: `--verify` fails with "differs from the deployment's key" until the new
  key is escrowed.
- **The restore drill reads them back** (below).

## Upload copy

`rag-upload-backup.timer` runs `deploy/backup-uploads.sh` every 10 minutes, so an upload is off the host within 15. It
runs the profiled Compose service `rag-upload-backup` (the postgres image's shell as uid 1654, the blob volume
read-only, no network) with `UME_RAG_UPLOAD_BACKUP_DIR` mounted as the target. Each new file is written under a
temporary name and renamed; a file already in the copy is skipped. Deletions are not propagated: prune the copy with
the backup retention (35 days). With an S3 target there is no upload copy yet.

```bash
sudo deploy/backup-uploads.sh          # by hand; --env-file FILE, --state-dir DIR
journalctl -u rag-upload-backup.service --since today
```

## Restarting unhealthy containers

Docker restarts a container only when its main process exits. ClamAV's does not, so a hung clamd would stay
unhealthy. `rag-restart-unhealthy.timer` runs `deploy/host/restart-unhealthy.sh` every minute: it restarts every
container labelled `ume-rag.restart-unhealthy=true` (today `rag-clamav`) that Docker reports unhealthy. A container
in its start period counts as starting, not unhealthy, so a slow first download does not loop.

```bash
deploy/host/restart-unhealthy.sh --dry-run      # what it would restart now
journalctl -u rag-restart-unhealthy.service --since today
```

## Host firewall

`deploy/host/docker-user-firewall.sh` gives each Compose network its egress rules in the `DOCKER-USER` chain
(`UME-RAG-EGRESS`) and rejects new connections from the networks to the host itself (`UME-RAG-INPUT`).
`rag-firewall.service` applies it when Docker starts or restarts; its timer applies it again every 15 minutes, because
the allow-lists are host names, resolved when it runs.

| Network | May reach |
|---|---|
| `edge`, `data`, `ingestion`, `parser` | nothing (`edge` carries the published ports only) |
| `llm` (api, admin) | the gateway, the OIDC authority, the Open WebUI host, the OTLP endpoint, DNS |
| `crawl` (worker) | the gateway, the OTLP endpoint, DNS, `UME_RAG_CRAWL_ALLOW`; then the public Internet, but no private, loopback, link-local or reserved range |
| `mirror` (clamav) | `database.clamav.net:443`, or only freshclam's proxy |
| `backup` (postgres) | the S3 endpoint, DNS; nothing with a mounted share |
| `openwebui` | `rag-api`'s public URL and the OIDC provider, DNS (when `UME_RAG_OPENWEBUI_HOST` is set) |

A container sends all its egress through the network that holds its default route; Compose pins that with
`gw_priority` (api and admin: `llm`, worker: `crawl`).

```bash
deploy/host/docker-user-firewall.sh --dry-run                    # the rules it would apply (no root needed)
deploy/host/docker-user-firewall.sh --print-subnets              # the networks and their subnets
sudo systemctl start rag-firewall.service                        # apply now (after a change of deploy/.env)
sudo iptables -S UME-RAG-EGRESS
sudo deploy/host/docker-user-firewall.sh --remove                # take the chains and jumps out
```

Other options: `--env-file FILE`, `--subnet NAME=CIDR` (when a network's subnet differs from the Compose files),
`--allow NAME=HOST[:PORT]` (one more destination; port 443 when omitted) and `--dns IP` (instead of the resolvers in
`/etc/resolv.conf`). The timer runs the script without them, so an option given by hand lasts only until the next
run. To keep one, override the service's command with `sudo systemctl edit rag-firewall.service`:

```ini
[Service]
ExecStart=
ExecStart=/opt/ume-rag/deploy/host/docker-user-firewall.sh --env-file /opt/ume-rag/deploy/.env --allow crawl=intranet.umea.se
```

(with your deployment's path). `install.sh` leaves that override in place.

## Restore drill

After the first deployment (P0) and then **every quarter** (§7.4 rule 3). Record the date, the achieved RPO and RTO,
and anything that went wrong.

1. **Isolated host** with its own copy of the repository and of the blob volume (or the upload copy), set up as in
   *Restoring onto another host*, from the **escrowed** keys: this is the check that the escrow can be read back.
   Run `escrow-keys.sh --verify` against the escrow set first.
2. **Choose a target time** that tests the RPO: a moment a few minutes before the drill, and note the newest change
   you expect to survive.
3. **Restore** with `restore-pitr.sh --target-time … --confirm`; keep its timing output (the RTO).
4. **Check:**
   - the newest expected change is there and nothing after the target is;
   - `pgbackrest check` passed (the script runs it) and every service is healthy;
   - a search query returns results with citations;
   - a document preview opens (an `internal` document: this reads a blob with the escrowed key);
   - a chat history read: metadata only while the pilot runs history mode `none`.
   The search, preview and history checks apply once those features exist (P1).
5. **Take a full backup** on the restored host if it stays in use; otherwise remove it and its copy.
6. **Record** the RPO (target time minus the newest change that survived, against 5 minutes) and the RTO (against 4
   hours) in the operations log.

## Where to look first

| Symptom | Where to look and what to do |
|---|---|
| Postgres log: archive command failed, pgBackRest error `[103]`; `rag_backup_wal_archive_failed_total` rising | The stanza does not exist yet: `dc exec -T postgres pgbackrest --stanza=ragdb stanza-create` (once per new database or repository) |
| `rag_backup_repo_status` 99 | pgBackRest cannot read the repository: is the share mounted (`UME_RAG_BACKUP_DIR`), are the S3 endpoint and credentials right, does the firewall's `backup` rule have the endpoint? `dc exec -T postgres pgbackrest --stanza=ragdb info` shows the error |
| `rag_backup_wal_archive_age_seconds` above 300 | WAL is not being archived: `dc logs --since 30m postgres`, and `SELECT * FROM pg_stat_archiver` through `dc exec -T postgres psql -U postgres -d ragdb` |
| `rag-backup.service` failed | `journalctl -u rag-backup.service`; a failed run leaves the previous backups intact. Run it again by hand once the cause is fixed |
| Upload copy: "the upload copy's directory … does not exist (is the backup share mounted?)" | Mount the share; the next run catches up |
| `rag-clamav` unhealthy after a signature update | On a 16 GB host a failed reload leaves clamd without an engine (it still answers PING; every scan fails). The restart timer restarts it within a few minutes; meanwhile nothing can be scanned (the worker's scan step, P1, fails closed). `journalctl -u rag-restart-unhealthy.service`, `dc logs rag-clamav`. On a 24 GB host clamd keeps its old engine during a reload |
| Admin API answers 503 `config_busy` | Another config change held the config version lock for more than 5 s. Retry; if it persists, look for a long transaction holding the lock on `config."ConfigVersion"` in `pg_stat_activity` |
| `rag-migrate` exited non-zero; api, admin and worker do not start | `dc logs rag-migrate`. They wait for a successful migration, so the old schema is untouched; fix the cause and `dc up -d --wait` again |
| A service exits at once with a permission error on `/run/app-secrets` or `/run/certs` | The files belong to the wrong user: run the `chown` commands `init-deployment.sh` printed (or re-run it as root) |
| `rag-firewall.service` failed: "cannot resolve HOST" | DNS for an allow-listed name failed; the previous rules stay in place. Check the name in `deploy/.env` |
| `rag-firewall.service` failed: "no DOCKER-USER chain" | Docker is not running, or runs without its iptables integration |
| `install.sh` warns "Docker does not use the systemd cgroup driver" | The ingestion slice caps nothing: set `native.cgroupdriver=systemd` (production-setup.md), restart Docker, then `dc up -d --force-recreate` |
| `escrow-keys.sh --verify`: "differs from the deployment's key" | A key was rotated after that escrow: escrow again and verify |
| The parser stays unhealthy for minutes after a start | It warms its slots and runs a self-test first; the worker reports it in its own health and retries |

## Public-records requests

In the pilot our database holds no chat content (history mode `none`), so a request for a conversation is answered
from Open WebUI's own copy, the primary copy. How one user's conversation is found and exported for the registrar is
decided in the DPIA (register G1, to-verify #173), before go-live: the user's own export, a temporary, logged
admin-access window, or another route. Direct SQL against Open WebUI's database is ruled out. This section gets the
procedure once G1 has decided it.

## Secrets and certificates

- **Internal certificates** last 825 days. Bring `ca.key` back to `deploy/certs/`, run
  `sudo ./deploy/init-deployment.sh --renew-certs`, restart the stack, and move `ca.key` off the host again.
  `--renew-ca` replaces the internal CA and re-issues its certificates.
- **The Data Protection certificate and the pepper** are never regenerated: a new one makes the stored data
  unreadable (protected values, every key hash).
- **The blob master key and the pgBackRest passphrase** are generated once and escrowed. There is no rotation
  procedure yet (key provider decision K1).
- **The api's keys** (`Security__DownloadGrantKeys__<kid>`, `Security__ConversationIdKeys__<kid>`): a rotation adds a
  new key id next to `k1`.
