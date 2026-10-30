# Production setup

The pilot runs on one Linux host with Docker Compose: six long-running containers (`postgres` with pgBackRest inside,
`rag-api`, `rag-admin`, `rag-worker`, `rag-parser`, `rag-clamav`) and the one-shot `rag-migrate`, all in the Compose
project `ume-rag`. `deploy/compose.prod.yaml` holds the data plane and control zone, `deploy/compose.ingestion.yaml` the
ingestion zone; `init-deployment.sh` lists both (and the overlays your options need) in `COMPOSE_FILE` in `deploy/.env`,
so every command below is `docker compose --env-file deploy/.env ...` without `-f`. Run the commands from the directory
that holds `deploy/`.

Day-to-day operation (backups, restore, key escrow, the firewall, troubleshooting) is in the [runbook](runbook.md);
upgrades in the [upgrade guide](upgrade-guide.md). The design behind this page is
[`plans/platform/repo-and-deployment.md`](../plans/platform/repo-and-deployment.md) §6 and §7.

## Host prerequisites

- **Linux with systemd**, Docker 28+ and Compose v2.24+ (the 24 GB overlay uses `!override`), `openssl` (for
  `init-deployment.sh`), and root for the host units.
- **cgroup v2 and Docker's systemd cgroup driver.** The ingestion containers run below `rag-ingestion.slice`
  (`cgroup_parent`), which caps the whole zone; with the `cgroupfs` driver the slice caps nothing. `docker info` must
  show `Cgroup Driver: systemd` and `Cgroup Version: 2`.
- **`172.30.0.0/16` stays out of Docker's `default-address-pools`.** The Compose networks use fixed subnets there
  (`edge` 172.30.1.0/24 to `mirror` 172.30.7.0/24, `backup` 172.30.16.0/24, `openwebui` 172.30.17.0/24), so the host
  firewall can give each its own egress rule. If Docker hands out a pool from that range to another network first,
  `up` fails. `deploy/host/docker-user-firewall.sh --print-subnets` lists them.
- **Docker's iptables integration** (the default), so the `DOCKER-USER` chain exists; not the nftables firewall
  backend. The firewall is IPv4 only (the networks have no IPv6).
- **An encrypted disk** for the Docker volumes (`postgres-data`, `rag-blobs`, `clamav-db`) and `deploy/secrets`.
- **The backup target**, agreed with IT: a share mounted on the host, or an S3-compatible store (below).
- **Escrow media** for the long-lived keys (runbook, *Key escrow*).

`/etc/docker/daemon.json` for the two Docker settings, then `sudo systemctl restart docker`:

```json
{
  "exec-opts": ["native.cgroupdriver=systemd"],
  "default-address-pools": [{ "base": "10.210.0.0/16", "size": 24 }]
}
```

The pool is an example: pick a range IT confirms is unused on your network, and never one inside `172.30.0.0/16`.

## Sizing

The pilot host has 8 vCPU and 16 GB or 24 GB of memory (repo-and-deployment §6.2). `init-deployment.sh --host-memory`
picks the profile; 16 is the default.

| Container | Zone | CPUs | Memory reserved | Limit, 16 GB host | Limit, 24 GB host |
|---|---|---|---|---|---|
| `postgres` | data | 3.0 | 2g | 3g (`shared_buffers` 768MB) | 5g (`shared_buffers` 1536MB) |
| `rag-api` | data | 2.0 | 384m | 1g | 1.5g |
| `rag-admin` | control | 1.0 | 256m | 512m | 768m |
| `rag-migrate` (one-shot) | – | 1.0 | – | 512m | 512m |
| `rag-worker` | ingestion | 1.5 | 512m | 1.5g | 2g |
| `rag-parser` | ingestion | 4.0 | 2g | 3.5g (1 slot × 4 threads) | 6g (2 slots × 2 threads) |
| `rag-clamav` | ingestion | 1.0 | 1.2g | 1.5g | 3g |
| `rag-ingestion.slice` (zone cap) | ingestion | `CPUQuota=500%` | – | `MemoryMax=7G` | `MemoryMax=11G` |
| `open-webui` (optional) | – | 1.0 | 512m | not on a 16 GB host | 1.5g |

The limits add up to about 12 GB on a 16 GB host and 18.75 GB on a 24 GB host (20.25 GB with Open WebUI); the OS and
Docker keep about 2 GB, and the limits are not all reached at once. Under CPU contention the data plane (weight 1024)
gets about four times the CPU of each ingestion container, and the slice holds the ingestion zone at five cores. On a
24 GB host ClamAV keeps its old engine during a signature reload (`ConcurrentDatabaseReload yes`) and test-loads
updates (`TestDatabases yes`); on a 16 GB host it has no room for that. The R1 release test (P1) confirms or corrects
these numbers. On-prem model capacity is the gateway's, measured with `scripts/capacity/`
([its README](../scripts/capacity/README.md)).

## Install

### 1. Files and images

Until the first release (`v0.1.0`) there is no release bundle: use a clone of the repository at the release commit and
build the images on the host:

```bash
docker build -f images/postgres/Dockerfile -t ume-rag/postgres:latest .
docker build -t ume-rag/clamav:latest images/clamav
docker build -t ume-rag/parser:latest src/parser
for p in Api AdminApi Worker MigrationService; do dotnet publish src/Ume.Rag.$p -c Release -t:PublishContainer; done
```

Images built elsewhere and pushed to a registry are set in `deploy/.env` by digest, for example
`API_IMAGE=ghcr.io/<owner>/ume-rag/api@sha256:...`. The variables are `POSTGRES_IMAGE`, `MIGRATIONS_IMAGE`,
`API_IMAGE`, `ADMIN_IMAGE`, `WORKER_IMAGE`, `PARSER_IMAGE`, `CLAMAV_IMAGE` and, for the optional stack,
`OPENWEBUI_IMAGE`.

### 2. `init-deployment.sh`

```bash
./deploy/init-deployment.sh --oidc-authority https://login.microsoftonline.com/<tenant>/v2.0 \
  --oidc-client-secret-file admin-client.secret \
  --api-host rag-api.intern.umea.se --admin-host rag-admin.intern.umea.se \
  --gateway-url https://llm.intern.umea.se --ca-file municipal-ca.pem \
  --gateway-key-file OnPrem=onprem.key --gateway-key-file Eu=eu.key --gateway-key-file External=external.key \
  --org-sync-key-file org-sync.key --crawl-allow 10.20.0.0/16 \
  --backup-target /mnt/backup/ume-rag --host-memory 24 --freshclam-proxy proxy.intern.umea.se:8080 \
  --allow-file-keys --yes
```

Without `--yes` it prompts for what is missing. It generates, under `deploy/certs` and `deploy/secrets`: an internal CA
and certificates for postgres, the worker, the parser and the two public listeners; the Data Protection certificate;
the database passwords and connection strings; the key pepper; the api's download-grant, cache-key and
conversation-id keys; the Open WebUI JWT secret; the blob master key `keys/rag-blob-internal`; the pgBackRest
passphrase and `pgbackrest/repo.conf`; and `deploy/.env` (non-secret settings only). Secrets and certificates reach the
RAG services as read-only files, never as environment variables (Open WebUI is the exception: it reads its secrets only
from its environment, so they come from `secrets/openwebui.env`).

It is safe to re-run: existing secrets, keys and the Data Protection certificate are never overwritten (a new one would
break existing data or backups). Connection strings and other derived files are written again, and a re-run without
`--renew-certs` rewrites `deploy/.env` and keeps the previous one as `deploy/.env.bak`, so carry over any lines you
added by hand. After the first run the files belong to the containers' users, so re-run it as root.

| Option | Default | Effect |
|---|---|---|
| `--oidc-authority URL` | prompted | the admin BFF's identity provider (HTTPS): `UME_RAG_OIDC_AUTHORITY` |
| `--oidc-client-id ID` | `ume-rag-admin` | the admin's own Entra registration, never the gateway's |
| `--oidc-client-secret-file FILE` | – | the admin registration's secret: `secrets/admin/Oidc__ClientSecret` |
| `--api-host HOST`, `--admin-host HOST` | prompted (the host name; the admin defaults to the api's) | public host names, in the listener certificates, `UME_RAG_API_PUBLIC_URL` and `UME_RAG_ADMIN_PUBLIC_HOST` |
| `--api-port N`, `--admin-port N` | 8543, 9543 | host ports of the two listeners |
| `--bind ADDR` | `127.0.0.1` (`0.0.0.0` with `--proxy`) | address the listeners are published on |
| `--gateway-url URL` | prompted | the LLM gateway (HTTPS): `UME_RAG_GATEWAY_URL` |
| `--ca-file FILE` | – | appended to the trust store of api, admin and worker: the gateway's CA and your PKI chain |
| `--gateway-key-file LEVEL=FILE` | – | a gateway key per level (`OnPrem`, `Eu`, `External`; repeat). api and admin get all, the worker only `OnPrem` and `Eu` |
| `--org-sync-key-file FILE` | – | the gateway Data API key for the admin's organisation sync (`catalog.read` only) |
| `--key-provider file` | `file` | the only key provider until postponed decision K1 |
| `--allow-file-keys` | – | required: accepts that the blob master key lives in a file on this host (pilot and test hosts) |
| `--backup-target DIR\|s3://ENDPOINT/BUCKET[/PATH]\|local` | prompted | pgBackRest `repo1` and the upload copy (below) |
| `--backup-s3-credentials-file FILE` | – | with an `s3://` target: access key and secret key on two lines |
| `--backup-s3-region R` | `us-east-1` | with an `s3://` target |
| `--crawl-allow CIDR[,CIDR]` | – | private ranges the crawler may fetch (municipal hosts): `UME_RAG_CRAWL_ALLOW`, used by the worker and the firewall |
| `--host-memory 16\|24` | 16 | the host profile; 24 adds `compose.host-24g.yaml` and the ClamAV confs |
| `--freshclam-proxy HOST:PORT` | – | freshclam reaches `database.clamav.net` through this HTTP proxy |
| `--openwebui-host HOST`, `--openwebui-oidc-client-id ID`, `--openwebui-oidc-provider-url URL`, `--openwebui-data-dir DIR` | –, –, –, `deploy/openwebui-data` | the optional Open WebUI stack (below) |
| `--role all` | `all` | multi-host roles come with P2 |
| `--proxy` | – | a reverse proxy terminates public TLS: two distinct host names, listeners on `0.0.0.0` |
| `--upstream-host HOST` | the host name | the address the proxy uses to reach this host |
| `--api-cert`, `--api-key`, `--admin-cert`, `--admin-key FILE` | – | certificates from your PKI for the public listeners (all four) instead of the internal CA |
| `--certs-dir DIR`, `--secrets-dir DIR` | `deploy/certs`, `deploy/secrets` | where the files go |
| `--no-chown` | – | leave the files owned by you |
| `--renew-certs` | – | re-issue the internal certificates only |
| `--renew-ca` | – | a new internal CA, then re-issue its certificates |
| `--yes`, `-y` | – | no prompts: every value from an option or its default |

`--help` prints the same list. Not built yet: `--rolling`, roles other than `all`, the `openbao` and `azure` key
providers, and an nginx file for `--proxy` (the script prints the proxy routes instead).

When it is done the script prints the next steps. If it did not run as root, step 0 lists the `chown` commands to run
(the containers run as uid 999, 1654, 10001 and 1000 and must read their own files). The order below is the
script's: the host units come **before** the first `up`, because the ingestion containers run below the slice they
install.

### 3. Entra

Register the two redirect URIs the script prints for the admin's client (code + PKCE):
`https://<admin host>[:<admin port>]/signin-oidc` and `/signout-callback-oidc`. RAG's own registrations
(`ume-rag-admin`, `ume-rag-open-webui`), their app roles and groups are Bicep in `infra/entra/`, applied by a
GitHub workflow after approval: see [`infra/entra/README.md`](../infra/entra/README.md). The client secrets are
created by an operator and handed to `--oidc-client-secret-file` and Open WebUI's `OAUTH_CLIENT_SECRET`, never
committed.

### 4. Host units

```bash
sudo deploy/host/install.sh            # reads UME_RAG_HOST_MEMORY from deploy/.env
systemctl list-timers 'rag-*'
```

It installs and enables `rag-ingestion.slice`, `rag-firewall.service` with its timer, and the backup, backup-status,
upload-copy and restart-unhealthy timers ([`deploy/host/README.md`](../deploy/host/README.md)), with this deployment's
`deploy/` path filled in. It warns when Docker does not use the systemd cgroup driver. The upload-copy timer is not
enabled with an S3 backup target. Options: `--host-memory 16|24` (else from `deploy/.env`), `--deploy-dir DIR`,
`--unit-dir DIR` (only writes the units there) and `--no-enable`. Re-run it after an upgrade or a change of
`--host-memory`.

Check the firewall rules it applies (no root needed for the dry run):

```bash
deploy/host/docker-user-firewall.sh --dry-run
sudo iptables -S UME-RAG-EGRESS
```

### 5. Start

```bash
docker compose --env-file deploy/.env up -d --wait
docker compose --env-file deploy/.env exec -T postgres pgbackrest --stanza=ragdb stanza-create
```

`up --wait` returns when every service is healthy and `rag-migrate` has finished. `stanza-create` is needed once per
new database or repository and is safe to repeat: WAL archiving fails until the stanza exists (the runbook's
*Where to look first*), and the backup-status timer reports a non-zero `rag_backup_repo_status` until then. Then
take the first backup and escrow the keys:

```bash
sudo systemctl start rag-backup.service      # a full backup, as there is none yet
sudo deploy/escrow-keys.sh /media/<escrow>   # then --verify it, and seal the media (runbook)
```

Back up `deploy/secrets/.pepper` and `deploy/certs/data-protection.pfx` separately from the database backups (a
restored database is unusable without them), and move `deploy/certs/ca.key` off the host: anyone who can read it can
issue certificates the services trust. It is needed again only for `--renew-certs`.

Check the listeners with the internal CA (as root, since the files are not world-readable):

```bash
docker compose --env-file deploy/.env ps
sudo curl --cacert deploy/certs/ca.pem https://localhost:8543/health/ready
sudo curl --cacert deploy/certs/ca.pem https://localhost:9543/health/ready
```

Every .NET service also answers `/health/live` and `/version`.

## Backup target

`--backup-target` decides where pgBackRest's repository `repo1` and the upload copy go. The repository is always
encrypted client-side (`aes-256-cbc`, passphrase in `secrets/pgbackrest/cipher-pass`, escrowed).

| Target | pgBackRest repository | Upload copy | `deploy/.env` |
|---|---|---|---|
| `DIR` (a share mounted on the host) | `DIR/pgbackrest`, mounted into postgres at `/var/lib/pgbackrest` | `DIR/uploads` (`rag-upload-backup.timer`) | `UME_RAG_BACKUP_DIR`, `UME_RAG_UPLOAD_BACKUP_DIR` |
| `s3://ENDPOINT/BUCKET[/PATH]` | the bucket, path `/PATH` (default `/ume-rag`), path-style URIs, over the `backup` network | not built yet: replicate the bucket's `uploads/` prefix once the blob store moves to S3 | `UME_RAG_BACKUP_ENDPOINT` (the firewall allows only this) |
| `local` (test hosts only) | the `pgbackrest-repo` Docker volume | `deploy/upload-backup` | `UME_RAG_UPLOAD_BACKUP_DIR` |

`local` is not a backup off the host. With a share, mount it before Docker starts (for example with an `fstab` entry
or a systemd mount unit that `docker.service` requires); the script creates the two directories and gives them to uid
999 (pgBackRest) and 1654 (the upload copy). Retention is 35 days (`repo1-retention-full=35`): a full backup expires
once a newer one is at least 35 days old.

## ClamAV signatures

freshclam updates the signatures straight from `database.clamav.net` (no internal mirror), four times a day, through
the `mirror` network, which reaches nothing else. That host name is served by a CDN: the firewall resolves it when it
runs (at Docker start and every 15 minutes), so an address change can block updates until the next run. **Where IT
provides an egress proxy, use it**: `--freshclam-proxy HOST:PORT` writes `HTTPProxyServer`/`HTTPProxyPort` into a copy
of `freshclam.conf`, and the firewall then lets the `mirror` network reach only the proxy.

The ClamAV image's own confs are used unless a setting differs. `--host-memory 24` or `--freshclam-proxy` makes
`init-deployment.sh` copy them to `secrets/clamav/`, change them, and add `compose.clamav-conf.yaml` (which mounts the
copies) to `COMPOSE_FILE`. In a clone the source is `images/clamav/`; in a release bundle, `deploy/clamav/`.

## Internal TLS

Every connection inside the host is TLS with the internal CA: the services to postgres (`SSL Mode=VerifyFull`), the
worker to the parser (`https://rag-parser:8000`, certificate `certs/parser/` with the name `rag-parser`, set through
`PARSER_TLS_CERT_FILE` and `PARSER_TLS_KEY_FILE`), and the health probes. Only clamd (TCP 3310) has no TLS, so it stays
on the internal `ingestion` network. Internal certificates last 825 days: re-issue them with
`./deploy/init-deployment.sh --renew-certs`, which needs `ca.key` back on the host.

## Reverse proxy and PKI

The listeners default to loopback (`127.0.0.1:8543` and `:9543`). Behind the municipal reverse proxy, run
`init-deployment.sh --proxy` with two distinct host names (and `--upstream-host` if the proxy reaches this host by
another name): the listeners bind to `0.0.0.0`, so restrict those ports to the proxy. The script prints the routes:
each host name forwarded over HTTPS to its port, preserving the `Host` header; `client_max_body_size 60m` and
`proxy_request_buffering off` on `PUT /api/upload-batches/{batchId}/files/{n}` of the admin host, 1 MB elsewhere.
With certificates from your PKI, pass `--api-cert`, `--api-key`, `--admin-cert` and `--admin-key` (and `--ca-file`
for the chain).

## Telemetry

The services export OpenTelemetry (logs, metrics, traces) only when an endpoint is set: add
`UME_RAG_OTEL_ENDPOINT=<collector>` to `deploy/.env` (the firewall then allows it from the `llm` and `crawl` networks)
and run `up -d` again. Health-check requests are not traced, and query strings are removed from HTTP spans. The
backup scripts write Prometheus text files to `/var/lib/ume-rag/` (runbook, *Backups*).

## Open WebUI (optional)

`deploy/compose.openwebui.yaml` is a separate Compose project (`ume-rag-openwebui`), never in `COMPOSE_FILE` and off by
default. It is started only once the DPIA (register G1) has decided register item **W2**, how Open WebUI's own copies of
the chats are pruned; until then they stay unpruned. It needs the 24 GB host, or a VM of its own.

```bash
./deploy/init-deployment.sh ... --openwebui-host chat.intern.umea.se --openwebui-oidc-client-id <id> \
  --openwebui-oidc-provider-url https://login.microsoftonline.com/<tenant>/v2.0/.well-known/openid-configuration
# fill in OPENAI_API_KEYS and OAUTH_CLIENT_SECRET in deploy/secrets/openwebui.env
docker compose -f deploy/compose.openwebui.yaml --env-file deploy/.env up -d --wait
sudo systemctl start rag-firewall.service   # allow the openwebui network its destinations now
```

It has one connection only, to `rag-api` (`OPENAI_API_KEYS` is the `ume-rag-sk-` key of its client, issued in the admin
UI), signs in only through its own Entra registration, and listens on plain HTTP on port 3000 of the listeners' bind
address (loopback unless `--proxy`), so the reverse proxy terminates TLS. Its settings come from the Compose file on
every start; an admin's edit in Open WebUI lasts until the next restart. Its fixed input warning (`WEBUI_BANNERS`) still
shows a close button: a closed banner returns on the next reload.
