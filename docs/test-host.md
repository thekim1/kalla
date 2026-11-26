# Test host

The test host runs a release the way the pilot will, before go-live: behind the municipal reverse proxy, with the
pilot's CPU caps, the real backup target and escrowed keys. It is the cheapest host that can do that (decisions table,
"M2 and test hosting"), and production-style tests run on it shortly before go-live. **Status: not deployed.**
It waits for the test host, the backup target and the key-escrow location.

Install with [production-setup.md](production-setup.md) and operate with the [runbook](runbook.md). This page lists only
the test host's own choices and the checks to record. `dc` stands for `docker compose --env-file /etc/kalla/.env`, run
from the directory that holds `deploy/`.

## What it checks

- The current release runs behind the municipal proxy, with health green through the proxy.
- cgroup v2 and Docker's systemd driver on the host image; the ingestion slice caps the zone.
- A restore drill with a realistic database size: RPO ≤ 5 minutes, RTO ≤ 4 hours, keys read back from the escrow.
- The performance test under the pilot caps (`perf.yml`, repo-and-deployment §8.3), once a runner for it is approved;
  the caps are set again from its result in `compose.host-24g.yaml`.

## Host

- **8 vCPU and 24 GB**, the pilot's 24 GB profile (repo-and-deployment §6.2). The `cpus` values are caps, not
  reservations, so a smaller host starts the stack, but its performance numbers do not stand for the pilot.
- The [host prerequisites](production-setup.md#host-prerequisites): systemd, Docker 28+ with Compose v2.33.1+, cgroup v2
  with the systemd cgroup driver, `default-address-pools` outside `172.30.0.0/16`, an encrypted disk. Record the
  driver check:

  ```bash
  docker info --format 'driver={{.CgroupDriver}} version={{.CgroupVersion}}'   # driver=systemd version=2
  ```

- Its own host names, for example `rag-api-test.intern.umea.se` and `rag-admin-test.intern.umea.se`, its own OIDC
  redirect URIs, and its own gateway credential keys. Nothing is shared with a production deployment: not the secrets,
  not the backup repository, not the escrow set.

## Install with the pilot caps

The release bundle as in production-setup.md, *Files and images*. Then `init-deployment.sh` with `--host-memory 24`
and `--hardened`:

```bash
./deploy/init-deployment.sh --api-host rag-api-test.intern.umea.se --admin-host rag-admin-test.intern.umea.se \
  --gateway-url https://<gateway host> --set KALLA_TRUST_DIR=/etc/kalla/trust \
  --gateway-credential-key-file api=kalla-api.key --gateway-credential-key-file admin=kalla-admin.key \
  --gateway-credential-key-file worker=kalla-worker.key \
  --set KALLA_CRAWL_ALLOW=<municipal ranges> --backup-target /mnt/backup/kalla-test \
  --host-memory 24 --freshclam-proxy <proxy host>:<port> \
  --hardened --oidc-authority https://<keycloak host>/realms/fors --proxy-address <the proxy's addresses> --yes
```

`/etc/kalla/trust` holds the municipal CA as a `*.pem` file, which the services trust besides the internal CA (the
gateway's and the sign-in provider's certificates). Sign-in is set in kalla-admin's setup wizard after the first start
(production-setup.md, *Sign-in*); `--oidc-authority` names the provider that the host firewall lets kalla-admin reach.

`--host-memory 24` lists `compose.host-24g.yaml` and `compose.clamav-conf.yaml` in `COMPOSE_FILE`. The base Compose
file keeps every `cpus` cap at 2 or less during development (decisions table, "Low CPU during development"); the
overlay restores the pilot caps and sets the 24 GB limits:

| Container | `cpus` | `mem_limit` | Other |
|---|---|---|---|
| `kalla-postgres` | 3.0 | 5g | `shared_buffers=1536MB`, `effective_cache_size=3500MB`, `maintenance_work_mem=1GB` (written by `kalla-bootstrap` into `kalla-config`) |
| `kalla-api` | 2.0 | 1.5g | |
| `kalla-admin` | 1.0 | 768m | |
| `kalla-worker` | 1.5 | 2g | |
| `kalla-parser` | 4.0 | 6g | 2 slots × 2 threads, slot watchdog 2 600 MB |
| `kalla-clamav` | 1.0 | 3g | `ConcurrentDatabaseReload`, `TestDatabases` |
| `rag-ingestion.slice` | `CPUQuota=500%` | `MemoryMax=11G` | `deploy/host/install.sh` reads `KALLA_HOST_MEMORY=24` |

Check what the deployment renders, and that the slice got its 24 GB cap after the host units are installed:

```bash
dc config --format json | jq -r '.services | to_entries[] | "\(.key) cpus=\(.value.cpus) mem_limit=\(.value.mem_limit)"'
systemctl show rag-ingestion.slice -p MemoryMax          # MemoryMax=11811160064
```

In the repository, `deploy/tests/pilot-stack.sh` checks the same values offline, with `docker compose config`.

Then the host units (before the first start: `--hardened` needs the slice), the start, `stanza-create` and the first
full backup, as production-setup.md steps 4 and 5.

## Behind the municipal proxy

The two listeners publish on `0.0.0.0` (ports 8543 and 9543), and with `--proxy-address` the host firewall admits
only the proxy's addresses to them. Give the proxy's owners the routes of `deploy/proxy/nginx.conf.example`
(production-setup.md, *Reverse proxy and PKI*), and let the proxy trust the listeners' certificates: the internal CA
(`ca.pem` in the `kalla-certs` volume, read below) or your PKI's certificates, as `api.crt`/`api.key` and
`admin.crt`/`admin.key` in the directory `KALLA_PKI_DIR` names (`--set KALLA_PKI_DIR=DIR`).

Health, first on the host, then through the proxy from a client on the municipal network:

```bash
sudo docker run --rm --network none -v kalla_kalla-certs:/c:ro --entrypoint cat \
  "$(sudo docker compose --env-file /etc/kalla/.env config --images kalla-bootstrap)" /c/ca.pem > rag-ca.pem
curl --cacert rag-ca.pem https://localhost:8543/health/ready
curl --cacert rag-ca.pem https://localhost:9543/health/ready
curl -fsS https://rag-api-test.intern.umea.se/health/ready
curl -fsS https://rag-admin-test.intern.umea.se/health/ready
curl -fsS https://rag-api-test.intern.umea.se/version          # the release's version
```

Each `/health/ready` answers 200. Then sign in to the admin UI through the proxy once, so the redirect URIs and the
`Host` header the proxy forwards are proven too. Record the commands and answers.

## Backup target

The share or S3 store agreed with IT, never `local`: the drill measures a restore from the real
target. Mount a share before Docker starts (production-setup.md, *Backup target*). After the first full backup:

```bash
dc exec -T kalla-postgres pgbackrest --stanza=ragdb info
cat /var/lib/kalla/backup.prom          # rag_backup_repo_status 0, rag_backup_wal_archive_age_seconds under 300
```

The test host's repository is its own (a path of its own on the share, or a bucket of its own), so its backups and
WAL never mix with a production repository.

## Key escrow

As the runbook's *Key escrow*, after the first start and after every rotation:

```bash
sudo deploy/escrow-keys.sh /media/<escrow>
sudo deploy/escrow-keys.sh --verify /media/<escrow>     # PASS
```

Label the media as the test host's. Where the escrow lives is agreed with IT.

## Restore drill

The runbook's *Restore drill*, with these test-host specifics:

1. **A realistic size.** Run the drill once the pilot collection is crawled and parsed, so the
   database, its WAL and the blob volume have the pilot's size. Note the size before the drill:
   `dc exec -T kalla-postgres psql -U postgres -d ragdb -c "SELECT pg_size_pretty(pg_database_size('ragdb'))"`.
2. **An isolated drill host** (a second VM is enough) with a **copy** of the test host's repository and of its upload
   copy, set up as the runbook's *Restoring onto another host*: the keys come from the escrow media, and
   `sudo deploy/escrow-keys.sh --verify /media/<escrow>` on the drill host prints `PASS`.
3. **The target time**, a few minutes before the drill; note the newest change you expect to survive.
4. **Restore** with `sudo deploy/restore-pitr.sh --target-time '<time with offset>' --confirm` and keep its timing
   output.
5. **The checks** of the runbook's step 4 that exist in this release: the newest expected change and nothing after the
   target, every service healthy, a search with citations, an `internal` document's preview.
6. **Record** in the operations log and the results file: database size, target time, newest surviving change, RPO
   (against 5 minutes), RTO from the script's total (against 4 hours), the `PASS` of the escrow check, and anything
   that went wrong. Then remove the drill host and its copy.

## Open WebUI

Optional, as production-setup.md, *Open WebUI*: the 24 GB host may run it beside the stack (repo-and-deployment §6.2).
It is the profile `openwebui` (`--profiles clamav,openwebui`). Once it runs, its one-shot `kalla-openwebui-setup` gives kalla-api the
feedback service account's key and checks that the key reads the feedback list; `dc logs kalla-openwebui-setup` says
*minted* or *kept*. That the key reaches only the feedback routes and password sign-in is refused is checked here too.
