# Operational runbook

For the pilot host set up as in [production-setup.md](production-setup.md). Commands run from the directory that holds
`deploy/`, as root or a member of the `docker` group; `dc` below stands for `docker compose --env-file deploy/.env`.
The design behind this page is [`plans/platform/repo-and-deployment.md`](../plans/platform/repo-and-deployment.md)
§6 and §7.4. Upgrades are in the [upgrade guide](upgrade-guide.md), security events in
[security-events.md](security-events.md), the test host's deployment and drill in [test-host.md](test-host.md). Every port and outbound destination, per setup: [network-and-ports.md](network-and-ports.md).

## Ownership and targets

Name these before real data arrives (placeholders until then):

| Role | Who |
|---|---|
| Service owner | `<owner>` |
| On-call function | `<owner>` |
| Operations owner (reviews this runbook) | `<owner>` |
| Data protection officer (dataskyddsombud) | `<owner>` |
| Records owner (registrar) | `<owner>` |
| Gateway team (the gateway and the shared Keycloak) | `<owner>` |

Pilot targets: **RPO ≤ 5 minutes, RTO ≤ 4 hours** for the database; uploads copied off the host within 15 minutes.
Retention of the backups is 35 days (proposed).

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

## Daily checks

Until the metrics reach the municipal monitoring (how is still to be settled, *Metrics and alerts*), check by hand
each working day:

```bash
systemctl list-timers 'rag-*'                   # every timer has a next run; the backup ran last night
systemctl list-units --failed 'rag-*'           # no failed unit
cat /var/lib/ume-rag/backup.prom /var/lib/ume-rag/upload-backup.prom
dc ps                                           # every service healthy (rag-migrate exited 0)
sudo curl --cacert deploy/certs/ca.pem https://localhost:8543/health/ready
sudo curl --cacert deploy/certs/ca.pem https://localhost:9543/health/ready
dc exec -T postgres pgbackrest --stanza=ragdb info
```

In `backup.prom`: `rag_backup_age_seconds` under 93600 (26 h), `rag_backup_repo_status 0`,
`rag_backup_wal_archive_age_seconds` under 300, `rag_backup_last_run_success 1`, and
`rag_backup_status_timestamp_seconds` within the last 10 minutes. In `upload-backup.prom`:
`rag_upload_backup_last_success_timestamp_seconds` within the last 30 minutes (not with an S3 target). These are the
thresholds of the proposed alerts below; anything else: *Where to look first*.

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
escrowed (*Key escrow*). Open WebUI's data directory (`UME_RAG_OPENWEBUI_DATA_DIR`) is **not backed up by any script
in P0** (*Public-records requests*).

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
sudo systemctl start rag-backup.service                  # the scheduled run (--type auto), now
```

`--env-file FILE` (default `deploy/.env`) picks the deployment, and `--state-dir DIR` (default `$STATE_DIRECTORY`, else
`/var/lib/ume-rag`) where `backup.prom` and `backup-last-run` go. A diff without a full becomes a full. A failed run is
recorded (`rag_backup_last_run_success 0`) and the metrics are refreshed if the database still answers.

### Metrics and alerts

The scripts write Prometheus text files to `/var/lib/ume-rag/` (`backup.prom`, `upload-backup.prom`), atomically and
world-readable. Point node_exporter's textfile collector, or the collector that reads them, at that directory (how they
reach the municipal monitoring is still to be settled). Every value is read from the repository and `pg_stat_archiver`
at run time, so a backup taken by any means is reported.

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
   with the ingestion pipeline (P1; not built in P0). A missing **upload** is restored from the upload copy
   (*Upload copy*).

**Timings.** The local drill (PR #40; a 30 MB database, local repository) took **23.0 s** end to end: archive and stop
2.1 s, restore 0.9 s, recovery to the target 2.9 s, promote 1.6 s, check 0.3 s, services healthy 15.2 s. A second
restore on the new timeline took 22.7 s. Restore and recovery grow with the database size and the WAL since the chosen
backup, and a share or S3 target is slower than a local volume: the test-host drill measures realistic numbers.

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

## Restore drill

§7.4 rule 3: a drill after the first deployment, then **every quarter**. The P0 drill was run locally (PR #40, timings
above). The **first drill on the test host is scheduled before go-live (P1b)**, together with the test-host deployment
(decisions table, "M2 and test hosting"); it is still open, and its specifics are in [test-host.md](test-host.md).
Record the date, the achieved RPO and RTO, and anything that went wrong.

1. **Isolated host** with its own **copy** of the pgBackRest repository (never production's) and of the blob volume
   (or the upload copy), set up as in *Restoring onto another host*, from the **escrowed** keys: this is the check
   that the escrow can be read back. Once the keys are in place, run `escrow-keys.sh --verify` against the escrow set:
   it must print `PASS`.
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

## Key escrow

`deploy/escrow-keys.sh` exports the keys no backup can replace to escrow media and reads them back. Escrowed in P0
and P1:

- `secrets/keys/rag-blob-*`: the blob master keys of the file key provider (`rag-blob-internal`). Without them the
  `internal` blobs cannot be decrypted.
- `secrets/pgbackrest/cipher-pass`: the repository passphrase. Without it no backup can be read.

Chat master keys (`rag-chat-<level>`) join only once stored chat content is allowed (register A1); there are none in
P0 and P1. The pepper and `data-protection.pfx` go to the secret store, not to this escrow.

```bash
sudo deploy/escrow-keys.sh /media/escrow-a              # writes /media/escrow-a/ume-rag-keys-<project>-<UTC time>/
sudo deploy/escrow-keys.sh --verify /media/escrow-a     # the newest set there, or name one set
```

The export writes the keys with `SHA256SUMS` and a `MANIFEST` (what each key is and where it goes back), read-only for
the owner. `--verify` checks the checksums and the key formats, and that every key equals this deployment's current
one (and the passphrase equals `repo1-cipher-pass` in `repo.conf`); it prints `PASS` and exits 0, or names each
`FAIL`. `--env-file FILE` or `--secrets-dir DIR` points it at another deployment.

- **Run it** after the first deployment (production-setup.md) and **after every rotation**, then verify: `--verify`
  fails with "differs from the deployment's key" until the new key is escrowed. The upgrade guide verifies the escrow
  before each upgrade.
- **Seal the media in two places** (or use the key service's own backup). The files are in the clear: whoever holds
  the media can read every `internal` blob and every backup. Where the escrow lives is agreed with IT (to-verify #145).
- **Keep each set as long as the data and backups it protects.**
- **A restore reads them back**: copy the keys to the places the `MANIFEST` names (*Restoring onto another host*),
  then `--verify` against the set; every drill does this.

## Upload copy

`rag-upload-backup.timer` runs `deploy/backup-uploads.sh` every 10 minutes, so an upload is off the host within 15. It
runs the profiled Compose service `rag-upload-backup` (the postgres image's shell as uid 1654, the blob volume
read-only, no network) with `UME_RAG_UPLOAD_BACKUP_DIR` mounted as the target. Each new file is written under a
temporary name and renamed; a file already in the copy is skipped. Deletions are not propagated: prune the copy with
the backup retention (35 days). With an S3 target there is no upload copy yet (not built in P0).

```bash
sudo deploy/backup-uploads.sh          # by hand; --env-file FILE, --state-dir DIR
sudo systemctl start rag-upload-backup.service
journalctl -u rag-upload-backup.service --since today
```

### Restoring a missing upload

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

## Updates and restarts

Upgrades follow the [upgrade guide](upgrade-guide.md): announce the window, take a full backup and verify the escrow,
replace `deploy/`, run the superuser steps if the release lists them, then `dc up -d --wait`, which runs `rag-migrate`
first. With one slot each (the default) `rag-api` and `rag-admin` are replaced together in one announced window. With
`init-deployment.sh --rolling` there are two of each behind the proxy, and `deploy/rolling-update.sh` replaces them one
at a time for releases marked *Rolling update: yes* ([Updates without downtime](zero-downtime-updates.md)).

**The worker is stop-then-start, never overlapping.** Two `rag-worker` processes at once would exceed its role's
connection limit; its job leases make a short pause harmless. Never scale it (`--scale rag-worker=2`) or start a
second worker against the same database. To restart it by hand:

```bash
dc stop rag-worker              # it gets its stop grace period (120 s) before it is killed
dc up -d --wait rag-worker
```

`dc restart rag-worker` also stops it before it starts. The parser, ClamAV and the renderer can be restarted on their
own: the worker reports them in its own health and retries (a render that loses the renderer fails as
`render.unavailable` and keeps the page's last good content).

**After any change to the gateway's models** (a new deployment behind an alias, a provider switch, a gateway upgrade),
a platform admin runs **Check embedding canary** on Platform → Gateway models. The platform makes no model calls of its
own to notice a changed embedding model: the check embeds each active profile's canary text once and stores the
similarity on the profile. Below 0.999 the worker refuses the profile until a later check passes (*Where to look
first*).

### Restarting unhealthy containers

Docker restarts a container only when its main process exits. ClamAV's does not, so a hung clamd would stay
unhealthy. `rag-restart-unhealthy.timer` runs `deploy/host/restart-unhealthy.sh` every minute: it restarts every
container labelled `ume-rag.restart-unhealthy=true` (today `rag-clamav` and `rag-renderer`) that Docker reports
unhealthy. A container
in its start period counts as starting, not unhealthy, so a slow first download does not loop.

```bash
deploy/host/restart-unhealthy.sh --dry-run      # what it would restart now
journalctl -u rag-restart-unhealthy.service --since today
```

## Portainer and other hosts without shell access

`deploy/portainer/compose.portainer.yaml` runs the gateway and Ume Rag as **one** Portainer stack, configured only
through environment variables, for hosts where `init-deployment.sh` cannot run. It is the gateway's
`deploy/compose.portainer.yaml`, unchanged, plus RAG's services (all named `rag-…`): rag-postgres, rag-migrate,
rag-api (8543), rag-admin (9543), rag-worker, rag-parser, rag-clamav and, with the `render` profile, rag-renderer. Open
WebUI is not part of it.

**Deploy.** In the gateway's existing stack, *Editor*: replace the file with this one, keep the stack name (the
gateway's volumes `<stack>_ume-certs`, `<stack>_ume-secrets` and its database are found by it), add the variables
below, and *Update the stack*. The RAG images on GHCR are private: add `ghcr.io` under *Registries* with a GitHub token
that can read packages.

| Variable | |
|---|---|
| `UME_RAG_API_HOST`, `UME_RAG_API_PUBLIC_URL` | required: rag-api's public host name and URL (`https://rag.example.se:8543`) |
| `UME_RAG_GATEWAY_KEY_Eu` | a gateway API key for RAG (gateway admin UI, residency Eu); without it every model call fails, so deploy once, create the key, add it and update the stack |
| `COMPOSE_PROFILES` | include `data` (the gateway's Data API, which RAG's admin reads for the model catalogue and org sync); `data,render` adds the renderer |
| `UME_RAG_ORG_SYNC_KEY` | a Data API key with `catalog.read`; without it model settings cannot be saved |
| `UME_RAG_EMBEDDING_MODEL` | the gateway's model id of the embedding model (`UME_RAG_EMBEDDING_DIMENSIONS`, default 1024; `UME_RAG_EMBEDDING_REQUEST_DIMENSIONS`, default `true`, sends `dimensions`: set `false` for a model without that parameter, such as `bge-m3`). Seeded once: changing these later does not change the profile (`docs/gateway-host-setup.md` §13). Every RAG gateway key must allow this model, and its deployment must be marked `Eu` |
| `UME_RAG_OIDC_CLIENT_SECRET`, `UME_RAG_OIDC_GROUPS_PLATFORM_ADMIN` | RAG admin's own OIDC client `ume-rag-admin` at the gateway's `UME_OIDC_AUTHORITY`, redirect URIs `https://<admin host>:9543/signin-oidc` and `/signout-callback-oidc`; the group that grants platform admin (the other roles: `_DEPARTMENT_ADMIN`, `_PIPELINE_EDITOR`, `_REVIEWER`, `_VIEWER`) |

The optional ones (ports, a separate admin host, per-service gateway keys, `UME_RAG_OIDC_TENANT_ID` for Entra ID, image
overrides `RAG_*_IMAGE`) are listed at the top of the file. rag-api, rag-admin and rag-worker join the gateway's `llm`
network and reach it at `https://gateway:8443`, trusting the gateway's internal CA. The gateway's Data API is not on
`llm`, so the stack adds one network to the gateway's part: `dataapi` joins `rag-dataapi` (internal), which only
rag-admin shares, and rag-admin reaches it at `https://dataapi:8443`. That join is the only change to the gateway's
file; a standalone gateway keeps its own.

**Bootstrap.** The one-shot `rag-bootstrap` runs after the gateway's `bootstrap` and creates RAG's internal CA, service
certificates, the Data Protection certificate, database passwords, the key pepper, the api's keys and the blob master
key in the `rag-certs` and `rag-secrets` volumes. It is idempotent: secrets are never regenerated, service
certificates are re-issued when they expire within 30 days or the host names change. The secrets that follow a
variable (gateway keys, the Data API key, the OIDC client secret) are written again on every deploy, and removed when
the variable is emptied; they live in the stack's environment, so restrict who can view it.

**Differences from `compose.prod.yaml`.** No fixed subnets and no host firewall (*Host firewall* does not apply;
firewall 8543 and 9543 to your proxy yourself), no ingestion slice, and no pgBackRest: WAL archiving is off, so back
up `rag-postgres` with `pg_dump` or volume snapshots. The renderer is off by default: Portainer cannot load its
seccomp profile from a file, so with `render` on it runs without Docker's seccomp filter (Chromium's own sandbox stays
on); without it the crawler indexes pages as served. PKI-issued certificates are not supported by this variant.

- **Back up** `rag-secrets/.pepper`, `rag-secrets/keys/rag-blob-internal` and `rag-certs/data-protection.pfx` with the
  database (for example `docker run --rm -v <stack>_rag-secrets:/s alpine cat /s/.pepper`). A database restored without
  them is unusable.
- **Upgrade.** Read the [upgrade guide](upgrade-guide.md), regenerate the file for the new release (or set the
  `RAG_*_IMAGE` variables to its images, all on the same version: the migrations image must match the services), and
  *Update the stack* with *Re-pull image and redeploy*. `rag-migrate` runs before the services.
- **Generated file.** Edit `deploy/portainer/compose.rag.template.yaml` or `deploy/portainer/bootstrap.sh`, then run
  `deploy/portainer/build-stack.sh`: it takes the gateway's current `compose.portainer.yaml` (from GitHub, or a path
  argument), adds RAG's sections and inlines the postgres config files. Run it again after a gateway release too.

## Host firewall

`deploy/host/docker-user-firewall.sh` gives each Compose network its egress rules in the `DOCKER-USER` chain
(`UME-RAG-EGRESS`) and rejects new connections from the networks to the host itself (`UME-RAG-INPUT`).
`rag-firewall.service` applies it when Docker starts or restarts; its timer applies it again every 15 minutes, because
the allow-lists are host names, resolved when it runs.

| Network | May reach |
|---|---|
| `edge`, `data`, `ingestion`, `parser`, `render` | nothing (`edge` carries the published ports only); `render` holds the renderer's Chromium, and the host is rejected for it too (`UME-RAG-INPUT`) |
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

## Crawl politeness per host

By default the crawler is polite: 1 request a second and 1 connection per external host (10 and 4 on the municipal
hosts in `Settings.Crawl.MunicipalHosts`), robots.txt and its `Crawl-delay` respected, slower when the site is slow,
back-off on 429/503, a 30-minute pause when half of the last 100 answers fail, and scheduled crawls only in the source's
night window (connectors design §5.1, §6.3–§6.6). A platform admin can change that **per host** in the admin UI:
*Platform objects → Settings → Website fetching → Host politeness*. Each row is one host name (no scheme or port) with a
preset (`polite`, `municipal`, `fast`, `custom`) and the settings that override it; it is stored in
`Settings.Crawl.HostOverrides`, checked on save (V37, `crawl_politeness_invalid`) and audited as `settings.update`.

```json
"hostOverrides": {
  "www.umea.se": { "preset": "fast", "requestsPerSecond": 20, "circuitBreaker": "off",
                   "robots": { "mode": "ignore", "reason": "own site, web team consent 2026-11-10" },
                   "userAgent": "Mozilla/5.0 (X11; Linux x86_64)" }
}
```

- `fast` = 10 requests a second, 4 connections, `Crawl-delay` and latency ignored, any time of day.
- robots.txt can only be ignored per host and with a reason; its `Sitemap:` lines are still read. The User-Agent is
  free per host; robots.txt rules are still matched against the crawler's own product token.
- The crawler's own User-Agent is `Crawl:UserAgent` on `rag-worker` (default `UmeaKommunBot/1.0`), e.g.
  `Crawl__UserAgent="ExampleBot/1.0 (+https://www.example.org/bot)"`. Its product token, the part before the first `/` or
  space (letters, `_` and `-` only), is the name robots.txt groups, meta robots and `X-Robots-Tag` are matched against.
  The worker refuses to start with an invalid value.
- The deployment caps every host at `Crawl:MaxHostRate` (50 requests a second) and `Crawl:MaxHostConnections` (16).
  Set them alike on `rag-admin` (which refuses more) and `rag-worker` (which clamps to them), e.g.
  `Crawl__MaxHostRate=20` in both services' environment. `renderPerHost` is capped at `Render:MaxConcurrent` (4) and, in effect, at the host's `maxConnections`.
- A crawl run's page lists the hosts it fetched with their own politeness and the robots.txt reason.

## Public-records requests (allmän handling)

**Status: open; there is no admin procedure yet.** In the pilot our database holds no chat content (history mode
`none`), so a request for a conversation is answered from Open WebUI's own copy, the primary copy (weakness review §4,
plan §8). With the pilot settings in `deploy/compose.openwebui.yaml` (`ENABLE_ADMIN_CHAT_ACCESS=false`,
`ENABLE_ADMIN_EXPORT=false`) **no Open WebUI admin can find, open or export another user's conversation**. The only
route that works today is the user's own export. Which route the registrar uses is decided in the DPIA (register G1,
with W2; to-verify #173) before go-live; direct SQL against Open WebUI's database is ruled out. This section gets the
procedure once G1 has decided it.

What the pinned Open WebUI (v0.12.5) does, read from its source at tag `v0.12.5` (paths below `backend/open_webui/`
unless they start with `src/`):

| Setting or route | Effect at v0.12.5 | Source |
|---|---|---|
| `ENABLE_ADMIN_CHAT_ACCESS` | read once from the environment at start (default `True`; we set `false`), not a persistent setting: changing it means recreating the container | `env.py:371` |
| An admin lists a user's chats: `GET /api/v1/chats/list/user/{user_id}`, the *Chats* button in *Admin > Users* | refused (401) with the flag off; the button is hidden (and never shown for admin-role users) | `routers/chats.py:751-764`; `src/lib/components/admin/Users/UserList.svelte:438` |
| An admin opens a chat by id: `GET /api/v1/chats/{id}`, and the admin fallback of `GET /api/v1/chats/share/{id}` | only the owner can read it with the flag off | `routers/chats.py:1380-1390`, `:1276-1278`; `models/chats.py:2001-2006` |
| `ENABLE_ADMIN_EXPORT` | read once from the environment (default `True`; we set `false`) | `config.py:2150` |
| All chats of all users `GET /api/v1/chats/all/db`, the raw database `GET /api/v1/utils/db/download`, the export section of *Admin > Settings > Database* | refused (401) with the flag off; the section is hidden | `routers/chats.py:1081-1085`; `routers/utils.py:74-78`; `src/lib/components/admin/Settings/Database.svelte:127` |
| The user's own export: `GET /api/v1/chats/all` (only the caller's chats), the chat export under *Data Controls* in the user's settings, and *Download > Export chat (.json)* in a chat's menu | works; the menu entry needs `permissions.chat.export`, default `True` (`USER_PERMISSIONS_CHAT_EXPORT`, which we do not set) | `routers/chats.py:1040-1045`; `src/lib/components/chat/Settings/DataControls.svelte:93-97`, `:185-196`; `src/lib/components/layout/Navbar/Menu.svelte:427-446`; `config.py:1933`, `:2045` |
| Feedback snapshots: an admin reads any feedback with its `snapshot.chat` (`GET /api/v1/evaluations/feedback/{id}`, `GET /api/v1/evaluations/feedbacks/all/export`) | works, but only for rated messages, and only the chat as it was when rated | `routers/evaluations.py:449-459`, `:354-364`; `models/feedbacks.py:92-93` |
| The feedback service account's API key | reaches only the allow-listed paths (prefix match), so it cannot read chats | `utils/auth.py:538-546` |
| The feedback service account itself | created as the store's first user, so as admin, from `WEBUI_ADMIN_EMAIL` and `WEBUI_ADMIN_PASSWORD` when Open WebUI starts with no users; with `ENABLE_PASSWORD_AUTH=false` (our default) it cannot sign in, and `ENABLE_LOGIN_FORM=false` only hides the form | `main.py:399-403`; `utils/auth.py:631-662`; `routers/auths.py:663-674`; `config.py:1704-1708` |
| A chat the user deletes | is removed from the database (rows deleted, not flagged) | `models/chats.py:2824-2832` |

**Until G1 decides, a request is handled like this** (it depends on the user):

1. The registrar (`<owner>`) records the request and identifies the user and the conversation (date, title).
2. The user opens the conversation in Open WebUI, chooses *Download > Export chat (.json)* (or exports all chats
   under *Data Controls* in the settings), and hands the file to the registrar through the records channel.
3. If the user cannot or will not export it, or has deleted it, there is no admin route with the pilot settings:
   escalate to the records owner and the data protection officer (open points below).

**The three candidate routes, until G1 picks one** (weakness review §4 "Export route", chat design §13.3, register G1
with W2, to-verify #173). None of them is the registrar's procedure until G1 has decided; then this section keeps the
chosen one and drops the others.

1. *The user's own export.* Works with the pilot settings and is the interim handling above. It depends on the user:
   nothing can be exported if the user cannot or will not, or has deleted the conversation.
2. *A temporary, logged admin-access window.* `ENABLE_ADMIN_CHAT_ACCESS=true` on the Open WebUI service, recreate it,
   an admin signs in and uses *Admin > Users > Chats*, then the flag goes back and the container is recreated again.
   While it is on, **every** Open WebUI admin can read **every** chat. In the pilot stack the only admin is the
   feedback service account (production-setup.md, *Open WebUI*): it cannot sign in, and its key reaches only the
   feedback routes. So the window needs a person with the admin role for its duration, which W2 prefers to avoid, and
   how the window is logged is not settled. Not tested.
3. *Another route G1 names.* The chat design lists a read-only query as the third option, but direct SQL against Open
   WebUI's database is ruled out (weakness review §4, which overrides the chat design). With the pilot settings Open
   WebUI's API has no admin route to another user's conversation (table above), so this route exists only once G1
   defines one.

Also for G1 and W2:

- *Feedback snapshots* cover only rated conversations, as of the rating.
- *Backups of Open WebUI's data directory*: none in P0, so a deleted conversation cannot be recovered.
- The Open WebUI stack is off by default and not deployed in P0 (production-setup.md, *Open WebUI*).

## Incidents

- **Security events** go to the SIEM under `Ume.Rag.Security`; what each one means and which alerts to set up is in
  [security-events.md](security-events.md).
- **Who to tell:** the service owner `<owner>` and on-call `<owner>` first; the data protection officer `<owner>` for
  any suspected personal-data breach (the GDPR breach assessment: a notifiable breach is reported within 72 hours of
  discovery); the municipality's IT security `<owner>` per its reporting routine; the gateway team `<owner>` when the
  gateway or sign-in is involved (the pilot signs in through the Keycloak shared with the gateway, on the gateway's
  host); the records owner `<owner>` before anything that may be an allmän handling is deleted.
- **Preserve evidence**: logs, the audit log and the security events. Do not capture conversations to investigate.
- **Safe shutdown**: `dc stop` (keeps the volumes). For a bad data change: a point-in-time restore to just before it
  (*Restore*).

### Where to look first

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
| `rag-renderer` restarts in a loop; its log says "No usable sandbox" | Chromium cannot create the user namespaces of its sandbox: the seccomp profile is not applied (`images/renderer/seccomp.json` missing next to `deploy/`), or the host restricts unprivileged user namespaces (AppArmor on Ubuntu 23.10+). Don't turn the sandbox off: production-setup.md, *Renderer* |
| `rag-renderer` exits at once: "UME_RAG_RENDER_WS_PATH is required" | `secrets/renderer.env` is missing: re-run `init-deployment.sh` (it keeps every other secret) |
| The parser stays unhealthy for minutes after a start | It warms its slots and runs a self-test first; the worker reports it in its own health and retries |
| Worker health unhealthy on `embedding-canary`; embed, preview and evaluation jobs wait with `embed.canary_mismatch` | The last embedding canary check of a profile came out below 0.999: the gateway's model alias now answers with another model, which would mix vector spaces. Ask the gateway team what changed; once the profile's model is restored, run **Check embedding canary** again (admin UI, Platform → Gateway models). The jobs continue when a check passes. A new model is a profile migration, not a fix here |

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
- **The feedback service account's Open WebUI key** (`secrets/api/OpenWebUi__AdminApiKey`): `sudo
  deploy/openwebui-feedback-key.sh` mints a new one (the old one stops working at once), then `dc restart rag-api`.
  Its password stays in `secrets/openwebui.env` and is usable only while the script runs.
