# Host units

Systemd units and scripts for the pilot host (`plans/platform/repo-and-deployment.md` §6.1, §6.2 and §7.4). Install
them as root after `deploy/init-deployment.sh` and, with `compose.hardened.yaml` (`--hardened`), **before** the first
`docker compose up`:

```sh
sudo deploy/host/install.sh            # reads KALLA_HOST_MEMORY from deploy/.env
systemctl list-timers 'rag-*'
```

The install script copies the units to `/etc/systemd/system` and writes `/etc/kalla/host.env` (the state directory,
`KALLA_STATE_DIR`): the deployment's `deploy/` directory (`KALLA_DEPLOY_DIR`) and its Compose project
(`COMPOSE_PROJECT_NAME`: `--project`, else `deploy/.env`'s, else the only Källa project on the host, else `kalla`). Every
unit reads that file, so the units act on the project by name, whatever its containers are called, and an upgrade's
`install.sh` from the new bundle moves them to its directory. It then reloads systemd, enables what `COMPOSE_FILE` in `deploy/.env` uses (`rag-firewall.service` and the
restart-unhealthy timer with `compose.hardened.yaml`, the backup timers with `compose.backup.yaml`, the certificate check
always), and warns when Docker does not use the systemd cgroup driver. Re-run it after an upgrade or after changing `--host-memory`. `sudo deploy/host/install.sh --check` changes nothing and
exits 1 unless the installed units are this bundle's and `host.env` names this `deploy/` directory and a project with a
kalla-postgres container; `deploy/doctor.sh` reports the same under *Host units*. `--unit-dir DIR --no-enable` only
writes the rendered units (CI runs `systemd-analyze verify` on them).

| Unit | What it does | When |
|---|---|---|
| `rag-ingestion.slice` | Caps the ingestion zone (kalla-worker, kalla-parser, kalla-clamav, kalla-renderer): `CPUQuota=500%`, `CPUWeight=25`, `MemoryMax=7G`; a drop-in sets `MemoryMax=11G` on a 24 GB host | Loaded when the containers start (`cgroup_parent` in `compose.hardened.yaml`) |
| `rag-firewall.service` + `.timer` | `docker-user-firewall.sh`: egress per Compose network in `DOCKER-USER`, no new connections from the networks to the host | After Docker starts or restarts, then every 15 minutes (host names resolved again) |
| `rag-backup.service` + `.timer` | `backup-postgres.sh --type auto`: pgBackRest backup inside the kalla-postgres container, full on Sundays (or when the newest full is 7 days old), differential otherwise; expiry follows | Daily at 01:30 (up to 15 minutes later), caught up after downtime |
| `rag-backup-status.service` + `.timer` | `backup-postgres.sh --status-only`: backup age and WAL archive age from `pgbackrest info` and `pg_stat_archiver` | Every 2 minutes |
| `rag-upload-backup.service` + `.timer` | `backup-uploads.sh`: new files below `uploads/` to the upload copy, never overwriting | Every 10 minutes (not enabled with an S3 backup target) |
| `rag-restart-unhealthy.service` + `.timer` | `restart-unhealthy.sh`: `docker restart` of containers labelled `kalla.restart-unhealthy=true` that are unhealthy (kalla-clamav and kalla-renderer, with `compose.hardened.yaml`) | Every minute |
| `rag-cert-expiry.service` + `.timer` | `cert-expiry.sh`: the internal CA and the service certificates in the `kalla-certs` volume; the service fails, naming the certificate and how to renew it, while one expires within 60 days | Daily at 07:00 (up to 15 minutes later), caught up after downtime |

## Requirements

With `compose.hardened.yaml`:

- **cgroup v2 with Docker's systemd cgroup driver** (`docker info` shows `Cgroup Driver: systemd`, `Cgroup Version: 2`;
  to-verify #90). Without it the slice caps nothing. Where the slice is not installed (a developer machine), set
  `KALLA_INGESTION_SLICE=` (empty) in `deploy/.env`: `init-deployment.sh --set KALLA_INGESTION_SLICE=`, which a
  re-run keeps.
- **Docker's iptables integration** (the default), so `DOCKER-USER` exists. The firewall uses the `iptables` that
  Docker uses (`iptables-nft` on current distributions). IPv4 only: the Compose networks have no IPv6.
- `172.30.0.0/16` stays out of Docker's `default-address-pools` (the Compose networks use fixed subnets there).

## Firewall

Check the rules before applying them, and after a change of `deploy/.env`:

```sh
deploy/host/docker-user-firewall.sh --dry-run
sudo systemctl start rag-firewall.service
sudo iptables -S UME-RAG-EGRESS
```

A container sends all its egress through the network that holds its default route. Compose pins it with
`gw_priority` (kalla-api and kalla-admin: `llm`; kalla-worker: `kalla-crawl`), so `llm` allows the gateway, the OIDC authority and the OTLP endpoint, `kalla-edge` Open WebUI's OIDC provider, and `kalla-crawl` the
gateway, the OTLP endpoint, `KALLA_CRAWL_ALLOW` and the public Internet. The allow-lists name hosts, resolved when
the script runs: a CDN-hosted name (`login.microsoftonline.com`, `database.clamav.net`) can move to addresses the rules
do not have yet, until the next run. Where IT provides an egress proxy, send freshclam through it
(`init-deployment.sh --freshclam-proxy`). `--allow NETWORK=HOST[:PORT]` adds a destination, such as
`llm=<gateway admin host>:9443` for kalla-admin's cost and budget read-back (the gateway's Integration API, at the address
its context names). `--remove` takes the rules
out.

## Metrics and alerts (§7.4 rule 5)

The backup scripts and `cert-expiry.sh` write Prometheus text files to `/var/lib/kalla/` (`backup.prom`,
`upload-backup.prom`, `certs.prom`); point node_exporter's textfile collector (or the collector that reads them,
to-verify §14) at that directory. Proposed alerts:

- `rag_backup_age_seconds > 93600` (no backup for 26 hours);
- `rag-backup.service` failed, or `rag_backup_last_run_success == 0`;
- `rag_backup_wal_archive_age_seconds > 300` for 2 minutes (the newest archived WAL is older than the 5-minute RPO);
- `rag_backup_repo_status != 0`, or `rag_backup_status_timestamp_seconds` older than 10 minutes;
- `rag_upload_backup_last_success_timestamp_seconds` older than 30 minutes.
- `rag_cert_expiry_timestamp_seconds - time() < 60 * 86400` (an internal certificate expires within 60 days), or
  `rag-cert-expiry.service` failed.

## Restore and escrow

`deploy/restore-pitr.sh --target-time TIME --confirm` (point-in-time restore) and `deploy/escrow-keys.sh DEST` /
`--verify DEST` (key escrow) are run by hand; the runbook describes the drill.
