# Host units

Systemd units and scripts for the pilot host (`plans/platform/repo-and-deployment.md` §6.1, §6.2 and §7.4). Install
them as root after `deploy/init-deployment.sh` and **before** the first `docker compose up`:

```sh
sudo deploy/host/install.sh            # reads UME_RAG_HOST_MEMORY from deploy/.env
systemctl list-timers 'rag-*'
```

The install script copies the units to `/etc/systemd/system`, with the deployment's `deploy/` directory in place of
`@DEPLOY_DIR@`, reloads systemd, enables `rag-firewall.service` and the timers, and warns when Docker does not use the
systemd cgroup driver. Re-run it after an upgrade or after changing `--host-memory`. `--unit-dir DIR --no-enable` only
writes the rendered units (CI runs `systemd-analyze verify` on them).

| Unit | What it does | When |
|---|---|---|
| `rag-ingestion.slice` | Caps the ingestion zone (rag-worker, rag-parser, rag-clamav, rag-renderer): `CPUQuota=500%`, `CPUWeight=25`, `MemoryMax=7G`; a drop-in sets `MemoryMax=11G` on a 24 GB host | Loaded when the containers start (`cgroup_parent` in `compose.ingestion.yaml`) |
| `rag-firewall.service` + `.timer` | `docker-user-firewall.sh`: egress per Compose network in `DOCKER-USER`, no new connections from the networks to the host | After Docker starts or restarts, then every 15 minutes (host names resolved again) |
| `rag-backup.service` + `.timer` | `backup-postgres.sh --type auto`: pgBackRest backup inside the postgres container, full on Sundays (or when the newest full is 7 days old), differential otherwise; expiry follows | Daily at 01:30 (up to 15 minutes later), caught up after downtime |
| `rag-backup-status.service` + `.timer` | `backup-postgres.sh --status-only`: backup age and WAL archive age from `pgbackrest info` and `pg_stat_archiver` | Every 2 minutes |
| `rag-upload-backup.service` + `.timer` | `backup-uploads.sh`: new files below `uploads/` to the upload copy, never overwriting | Every 10 minutes (not enabled with an S3 backup target) |
| `rag-restart-unhealthy.service` + `.timer` | `restart-unhealthy.sh`: `docker restart` of containers labelled `ume-rag.restart-unhealthy=true` that are unhealthy (rag-clamav) | Every minute |

## Requirements

- **cgroup v2 with Docker's systemd cgroup driver** (`docker info` shows `Cgroup Driver: systemd`, `Cgroup Version: 2`;
  to-verify #90). Without it the slice caps nothing. Where the slice is not installed (a developer machine), set
  `UME_RAG_INGESTION_SLICE=` (empty) in `deploy/.env`.
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
`gw_priority` (rag-api and rag-admin: `llm`; rag-worker: `crawl`), so `llm` allows the gateway, the OIDC authority, the
Open WebUI host and the OTLP endpoint, and `crawl` the gateway, the OTLP endpoint, `UME_RAG_CRAWL_ALLOW` and the public
Internet. The allow-lists name hosts, resolved when the script runs: a CDN-hosted name (`login.microsoftonline.com`,
`database.clamav.net`) can move to addresses the rules do not have yet, until the next run. Where IT provides an egress
proxy, send freshclam through it (`init-deployment.sh --freshclam-proxy`). `--allow NETWORK=HOST[:PORT]` adds a
destination. `--remove` takes the rules out.

## Metrics and alerts (§7.4 rule 5)

The backup scripts write Prometheus text files to `/var/lib/ume-rag/` (`backup.prom`, `upload-backup.prom`); point
node_exporter's textfile collector (or the collector that reads them, to-verify §14) at that directory. Proposed alerts:

- `rag_backup_age_seconds > 93600` (no backup for 26 hours);
- `rag-backup.service` failed, or `rag_backup_last_run_success == 0`;
- `rag_backup_wal_archive_age_seconds > 300` for 2 minutes (the newest archived WAL is older than the 5-minute RPO);
- `rag_backup_repo_status != 0`, or `rag_backup_status_timestamp_seconds` older than 10 minutes;
- `rag_upload_backup_last_success_timestamp_seconds` older than 30 minutes.

## Restore and escrow

`deploy/restore-pitr.sh --target-time TIME --confirm` (point-in-time restore) and `deploy/escrow-keys.sh DEST` /
`--verify DEST` (key escrow) are run by hand; the runbook describes the drill.
