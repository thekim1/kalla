# Production setup

The pilot runs on one Linux host with Docker Compose, as the Compose project `ume-rag` from `deploy/compose.rag.yaml`:
six long-running containers (`rag-postgres`, with pgBackRest inside when backups are on, `rag-api`, `rag-admin`,
`rag-worker`, `rag-parser` and, with the profile `clamav` (the default), `rag-clamav`: *Malware scan*, below), with the
profile `render` a seventh (`rag-renderer`, below), and the one-shots `rag-bootstrap` and `rag-migrate`.
`rag-bootstrap` creates the certificates and secrets in the volumes `rag-certs` and `rag-secrets` and copies the
static configuration into `rag-config`. The same file is the Ume.Rag part of the Portainer stack. `init-deployment.sh`
writes `deploy/.env`, whose `COMPOSE_FILE` lists `compose.rag.yaml` and the overlays your options need, so every
command below is `docker compose --env-file deploy/.env ...` without `-f`. Run the commands from the directory that
holds `deploy/`.

**A first install next to a gateway on the same host:** the [quick start](compose-quick-start.md) is the short path, and
this page the reference behind it.

Day-to-day operation (backups, restore, key escrow, the firewall, troubleshooting) is in the [runbook](runbook.md);
upgrades in the [upgrade guide](upgrade-guide.md); the test host's choices and checks in [test-host.md](test-host.md). Ports, firewall and proxy rules per setup are in [network-and-ports.md](network-and-ports.md). The design behind this page is
[`plans/platform/repo-and-deployment.md`](../plans/platform/repo-and-deployment.md) §6 and §7.

## Host prerequisites

- **Linux with systemd**, Docker 28+ and Compose v2.33.1+ (the Compose files use `gw_priority`, the overlays
  `!override`), and root for the host units.
- **With `--hardened`** (`compose.hardened.yaml` and the host units of `deploy/host/`), three more:
  - **cgroup v2 and Docker's systemd cgroup driver.** The ingestion containers run below `rag-ingestion.slice`
    (`cgroup_parent`), which caps the whole zone; with the `cgroupfs` driver the slice caps nothing. `docker info` must
    show `Cgroup Driver: systemd` and `Cgroup Version: 2`.
  - **`172.30.0.0/16` stays out of Docker's `default-address-pools`.** The Compose networks then use fixed subnets
    there (`rag-edge` 172.30.1.0/24 to `rag-mirror` 172.30.7.0/24, `backup` 172.30.16.0/24, `rag-render`
    172.30.18.0/24), so the host firewall can give each its own egress rule. If Docker hands out a pool from that range
    to another network first, `up` fails. `deploy/host/docker-user-firewall.sh --print-subnets` lists them.
  - **Docker's iptables integration** (the default), so the `DOCKER-USER` chain exists; not the nftables firewall
    backend. The firewall is IPv4 only (the networks have no IPv6).

  Without `--hardened` there is no slice, no fixed subnet and no host firewall: the networks take subnets Docker
  chooses.
- **Unprivileged user namespaces** for the renderer's Chromium sandbox (`rag-renderer`, with the profile `render`,
  below). On Ubuntu 23.10 and later AppArmor may restrict them (`sysctl kernel.apparmor_restrict_unprivileged_userns`):
  check that the renderer becomes healthy after the first `up` (*Renderer*, below).
- **An encrypted disk** for the Docker volumes (`rag-postgres-data`, `rag-blobs`, `rag-clamav-db`, `rag-certs`,
  `rag-secrets`, `rag-config`) and `deploy/.env`, which holds the keys and secrets you give `init-deployment.sh`.
- **The backup target**, agreed with IT: a share mounted on the host, or an S3-compatible store (below).
- **Escrow media** for the long-lived keys (runbook, *Key escrow*).

`/etc/docker/daemon.json` for the two Docker settings `--hardened` needs, then `sudo systemctl restart docker`:

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
| `rag-postgres` | data | 3.0 | 2g | 3g (`shared_buffers` 768MB) | 5g (`shared_buffers` 1536MB) |
| `rag-api` | data | 2.0 | 384m | 1g | 1.5g |
| `rag-admin` | control | 1.0 | 256m | 512m | 768m |
| `rag-migrate` (one-shot) | – | 1.0 | – | 512m | 512m |
| `rag-worker` | ingestion | 1.5 | 512m | 1.5g | 2g |
| `rag-parser` | ingestion | 4.0 | 2g | 3.5g (1 slot × 4 threads) | 6g (2 slots × 2 threads) |
| `rag-clamav` | ingestion | 1.0 | 1.2g | 1.5g | 3g |
| `rag-renderer` | ingestion | 2.0 | 256m | 2g | 2g |
| `rag-ingestion.slice` (zone cap) | ingestion | `CPUQuota=500%` | – | `MemoryMax=7G` | `MemoryMax=11G` |
| `rag-openwebui` (optional, profile `openwebui`) | – | 1.0 | 512m | not on a 16 GB host | 1.5g |

During development the base Compose file caps every container at 2 CPUs or less (decisions table, "Low CPU during
development"): `rag-postgres` at 2.0 and `rag-parser` at 2.0 with 1 slot × 2 threads. Only `--host-memory 24`
(`compose.host-24g.yaml`) restores the CPUs of the table; the pilot's CPU sizing is set again before go-live with the
R1 release test.

The limits add up to about 12 GB on a 16 GB host and 18.75 GB on a 24 GB host (20.25 GB with Open WebUI); the OS and
Docker keep about 2 GB, and the limits are not all reached at once. With `--hardened`, under CPU contention the data
plane (weight 1024) gets about four times the CPU of each ingestion container, and the slice holds the ingestion zone
at five cores. On a 24 GB host ClamAV keeps its old engine during a signature reload (`ConcurrentDatabaseReload yes`)
and test-loads updates (`TestDatabases yes`); on a 16 GB host it has no room for that. Without ClamAV (without the
profile `clamav`, *Malware scan*, below) the limits add up to 1.5 GB less on a 16 GB host and 3 GB less on a 24 GB
host, with 1.2 GB less reserved and one CPU fewer in the ingestion zone; the slice's cap stays as it is. The R1 release test (P1) confirms or corrects these
numbers. On-prem model capacity is the gateway's, measured with `scripts/capacity/`
([its README](../scripts/capacity/README.md)).

## Install

### 1. Files and images

**From a release (`v0.1.0` on):** download `ume-rag-X.Y.Z.tar.gz` and `SHA256SUMS` from the GitHub release, check and
unpack it. Its Compose files already pin every image by digest (`ghcr.io/<owner>/ume-rag/<service>`), so `deploy/.env`
needs no image variables:

```bash
sha256sum -c --ignore-missing SHA256SUMS
tar -xzf ume-rag-X.Y.Z.tar.gz && cd ume-rag-X.Y.Z
```

The release also carries an SPDX SBOM per image. The bundle holds `deploy/` (with the bootstrap and the gateway
manifest in `deploy/portainer/`), the operator docs (the quick start, this guide, the runbook, the upgrade guide, the
Portainer, Kubernetes, Keycloak, network, test-host and security-event pages), and the files the overlays name
relative to `deploy/`: the renderer's seccomp profile `images/renderer/seccomp.json` and the ClamAV confs
`images/clamav/clamd.conf` and `freshclam.conf`.

**From a clone (between releases, or to build the images yourself):** check out the commit and build the images on the
host. The admin UI is built first and published into the admin image with `-p:PublishAdminUi=true`, as `release.yml`
does:

```bash
docker build -f images/postgres/Dockerfile -t ume-rag/postgres:latest .
docker build -t ume-rag/clamav:latest images/clamav
docker build -t ume-rag/renderer:latest images/renderer
docker build -t ume-rag/parser:latest src/parser
(cd src/admin-ui && npm ci && npm run build)
for p in Api Worker MigrationService; do dotnet publish src/Ume.Rag.$p -c Release -t:PublishContainer; done
dotnet publish src/Ume.Rag.AdminApi -c Release -t:PublishContainer -p:PublishAdminUi=true
```

The image defaults in `deploy/compose.rag.yaml` are the released pins, so images built on the host or elsewhere are set
in `deploy/.env` with `init-deployment.sh --set`: `--set RAG_API_IMAGE=ume-rag/api:latest` for a local build, or by
digest for one pushed to a registry, `--set RAG_API_IMAGE=ghcr.io/<owner>/ume-rag/api@sha256:...`. The variables are
`RAG_POSTGRES_IMAGE`, `RAG_MIGRATIONS_IMAGE`, `RAG_API_IMAGE`, `RAG_ADMIN_IMAGE`, `RAG_WORKER_IMAGE`,
`RAG_PARSER_IMAGE`, `RAG_CLAMAV_IMAGE`, `RAG_RENDERER_IMAGE` and, for the profiles `openwebui` and `proxy`,
`OPENWEBUI_IMAGE` and `RAG_PROXY_IMAGE`.

### 2. `init-deployment.sh`

```bash
./deploy/init-deployment.sh --api-host rag-api.intern.umea.se --admin-host rag-admin.intern.umea.se \
  --gateway-url https://llm.intern.umea.se --set UME_RAG_TRUST_DIR=/etc/ume-rag/trust \
  --gateway-credential-key-file api=rag-api.key --gateway-credential-key-file admin=rag-admin.key \
  --gateway-credential-key-file worker=rag-worker.key \
  --set UME_RAG_CRAWL_ALLOW=10.20.0.0/16 \
  --backup-target /mnt/backup/ume-rag --host-memory 24 --freshclam-proxy proxy.intern.umea.se:8080 \
  --hardened --oidc-authority https://<keycloak host>/realms/ume --proxy-address <the proxy's addresses> --yes
```

Without `--yes` it asks for what is missing: the domain (or `--api-host`), the gateway's address and, with
`--hardened`, the sign-in provider's address. It writes only `deploy/.env` (mode 600): the variables of
`compose.rag.yaml`, which are the Portainer stack's ([`deploy/portainer/stack.env.example`](../deploy/portainer/stack.env.example)
lists them), and `COMPOSE_FILE` with `compose.rag.yaml` and the overlays. A key or secret you give it (a credential
key, the S3 credentials, a file passed with `--set-file`) is kept there as a variable, as the Portainer stack keeps it
in its variables. It generates no certificate and no secret, and asks for no sign-in value unless `--hardened`.

**`rag-bootstrap`** (`deploy/portainer/bootstrap.sh`, the same script the Portainer stack runs and Kubernetes'
`create-secrets.sh` runs locally) does that at every `up`, before the other services start. It creates what is missing
in `rag-certs` and `rag-secrets`: an internal CA and certificates for rag-postgres, rag-worker, rag-parser and the two
public listeners; the Data Protection certificate; the database passwords and connection strings; the key pepper
(`.pepper`); the api's download-grant, cache-key and conversation-id keys; the admin's setup secret
(`admin/Setup__Secret`, the key of the setup wizard's code; it needs no backup, a new one only changes future codes);
the Open WebUI JWT secret, Open WebUI's session key, its key for rag-api and the feedback service account's password;
the worker's crawl key pepper (`worker/Security__CrawlKeyPepper`, the HMAC key of the host hash in security event 5101);
the renderer's secret WebSocket path (`.render-ws-path`, written for the renderer as `renderer/ws-path` and, with the
profile `render`, for the worker as `worker/Render__Endpoint`); the blob master key `keys/rag-blob-internal` (a file:
the only key provider until postponed decision K1); and, with a backup target, the pgBackRest passphrase
`pgbackrest/cipher-pass` and `pgbackrest/repo.conf`. It copies the static configuration (Postgres's `postgresql.conf`
and `pg_hba.conf`, the database roles, the ClamAV confs, the TLS proxy's `nginx.conf`) into `rag-config`, and gives
each service's files to its user (uid 999, 1654, 10001, 10002 and 1000), so nothing needs a `chown`. Secrets and
certificates reach every service as read-only files from those volumes, never as environment variables; Open WebUI and
the renderer take theirs only from their environment, so their entrypoints read the files and pass them on. The end of
its log (`docker compose --env-file deploy/.env logs rag-bootstrap`) lists what is still missing and points at the
setup wizard.

Both are safe to re-run. `rag-bootstrap` never overwrites an existing secret, key or Data Protection certificate (a new
one would break existing data or backups); the connection strings and the files that follow a variable (the credential
keys, a declared client secret) are written again at every start. A re-run of `init-deployment.sh` changes what its
options name and keeps every other variable in `deploy/.env` (an empty one, and comments, are dropped), so
`UME_RAG_SETUP_REOPEN` and anything you added stays.

| Option | Default | Effect |
|---|---|---|
| `--domain DOMAIN` | – | `UME_DOMAIN`: rag-api, rag-admin and Open WebUI are `rag.DOMAIN`, `rag-admin.DOMAIN` and `chat.DOMAIN` |
| `--api-host HOST`, `--admin-host HOST` | under `--domain`; without it the admin shares rag-api's name | `UME_RAG_API_HOST`, `UME_RAG_ADMIN_HOST`: a name of its own instead; in the listener certificates and rag-api's public URL `https://<api host>` |
| `--gateway-url URL` | asked for (required) | the LLM gateway (HTTPS): `UME_RAG_GATEWAY_URL` |
| `--gateway-ca-dir DIR` | – | `UME_RAG_GATEWAY_CA_DIR`: a directory with the gateway's `ca.pem`, which the services then trust (a gateway on this host: its certificate directory, `<gateway>/deploy/certs`); not needed for a gateway with a public certificate |
| `--gateway-secrets-dir DIR` | – | `UME_RAG_GATEWAY_SECRETS_DIR`: the three keys from a gateway on this host, the directory its migration writes a manifest's keys to (`<gateway>/deploy/integration-secrets`, holding `Ume.Rag/rag-api`, `rag-admin` and `rag-worker`; *Gateway credential keys*, below). `rag-bootstrap` copies them at every start, and its checklist names a key that is missing |
| `--gateway-credential-key-file SERVICE=FILE` | – | `UME_RAG_<SERVICE>_GATEWAY_KEY`: `SERVICE`'s credential key of the gateway integration *Ume.Rag* (`SERVICE` is `api`, `admin` or `worker`; repeat, one per service), issued by a gateway admin (*Gateway credential keys*, below). It wins over the key from `--gateway-secrets-dir`. A service without a key has its model calls refused |
| `--profiles LIST` | `clamav` | `COMPOSE_PROFILES`, comma-separated, replacing the current list: `clamav` (*Malware scan*), `render` (*Renderer*), `openwebui` (*Open WebUI*) and `proxy` (*Reverse proxy and PKI*); `--profiles ''` runs none |
| `--backup-target DIR\|s3://ENDPOINT/BUCKET[/PATH]\|local` | – | `UME_RAG_BACKUP_TARGET`, with `compose.backup.yaml`: pgBackRest `repo1` and the upload copy (*Backup target*, below) |
| `--backup-s3-credentials-file FILE` | – | with an `s3://` target: access key and secret key on two lines |
| `--backup-s3-region R` | `us-east-1` | with an `s3://` target |
| `--host-memory 16\|24` | 16 | `UME_RAG_HOST_MEMORY`, the host profile; 24 adds `compose.host-24g.yaml` and `compose.clamav-conf.yaml` |
| `--freshclam-proxy HOST:PORT` | – | `UME_RAG_FRESHCLAM_PROXY`, with `compose.clamav-conf.yaml`: freshclam reaches `database.clamav.net` through this HTTP proxy |
| `--hardened` | – | `compose.hardened.yaml`: the host firewall's fixed subnets, the ingestion slice, the CPU weights, the restart-unhealthy labels and the renderer's seccomp profile, with the host units of `deploy/host/install.sh` (*4. Host units*) |
| `--oidc-authority URL` | asked for with `--hardened` (empty: none for now) | `UME_RAG_OIDC_AUTHORITY`: the sign-in provider (HTTPS) as a deployment value, locked in the setup wizard. With the host firewall rag-admin reaches only this provider, so the wizard's test sign-in needs it. Keycloak: `https://<host>/realms/<realm>`; Entra ID: `https://login.microsoftonline.com/<tenant>/v2.0` |
| `--proxy-address ADDR[,ADDR]` | – | `UME_RAG_PROXY_SOURCES`: the reverse proxy's IPv4 addresses or CIDRs; the host firewall (`--hardened`) accepts new connections to the published ports only from these |
| `--rolling` | – | `compose.rolling.yaml`: a second `rag-api` and `rag-admin` (ports 8544 and 9544) for updates without downtime; see [Updates without downtime](zero-downtime-updates.md) |
| `--overlays LIST` | the options' | the overlays exactly, comma-separated: `backup`, `hardened`, `host-24g`, `clamav-conf`, `rolling` (empty: none) |
| `--set NAME=VALUE` | – | any other variable (below, and `stack.env.example`); an empty value removes it |
| `--set-file NAME=FILE` | – | the same with `FILE`'s first line as the value, for a secret (`UME_RAG_OIDC_CLIENT_SECRET`) |
| `--yes`, `-y` | – | no questions: a missing required value stops the run |

`--help` prints the same list. Variables you set with `--set` (or `--set-file`), besides those of *Sign-in* and
*Open WebUI* below:

| Variable | Default | Effect |
|---|---|---|
| `UME_RAG_API_PORT`, `UME_RAG_ADMIN_PORT` | 8543, 9543 | host ports of the two listeners |
| `UME_RAG_API_BIND`, `UME_RAG_ADMIN_BIND` | `0.0.0.0` | address the listeners are published on (*Reverse proxy and PKI*, below) |
| `UME_RAG_API_PUBLIC_URL` | `https://<api host>` | rag-api's public address, when it has a port or a path (citation links and the admin's *How to connect* use it); the script names it when no proxy is in front |
| `UME_RAG_TRUST_DIR` | – | a host directory of `*.pem` CA certificates the services trust besides the internal CA: your PKI chain, a sign-in provider's or the gateway's CA from your own PKI, a database's |
| `UME_RAG_PKI_DIR` | – | a host directory with certificates from your PKI for the public listeners, `api.crt` and `api.key`, `admin.crt` and `admin.key`, used instead of the internal CA's (*Reverse proxy and PKI*, below) |
| `UME_RAG_CRAWL_ALLOW` | – | private ranges the crawler may fetch (municipal hosts), comma-separated CIDRs: used by the worker and the firewall |
| `UME_OTEL_ENDPOINT` | – | the OpenTelemetry collector (*Telemetry*, below) |
| `RAG_*_IMAGE`, `OPENWEBUI_IMAGE` | the release's pins | the images (*1. Files and images*) |

When it is done the script prints the next steps, in the order below: with `--hardened` the host units come
**before** the first `up`, because the ingestion containers run below the slice they install.

### 3. Sign-in (OIDC)

**The pilot signs in with the Keycloak shared with the gateway** (`plans/decisions.md` "P1 pilot sign-in";
identity design §11.2): the gateway repo runs it and its realm `ume`; RAG's realm additions (`deploy/keycloak/`) add the
clients `ume-rag-admin`, `open-webui` and `ume-rag-api`, the `rag-*` roles and the `departmentCodes` attribute. The
provider address is `https://<keycloak host>/realms/ume`; Open WebUI's provider URL follows from it.

Give the admin's client the two redirect URIs the setup wizard shows (code + PKCE):
`https://<admin host>[:<admin port>]/signin-oidc` and `/signout-callback-oidc`, and Open WebUI's client
`https://<owui host>/oauth/oidc/login/callback`.

**The setup wizard** (identity design §19) sets sign-in after the first start (*5. Start*); a Compose install asks for
no sign-in value before it:

1. Read the setup code: `docker compose --env-file deploy/.env logs rag-admin | grep 'Setup code'`. It is valid for
   one hour; while setup was never finished, `docker compose --env-file deploy/.env restart rag-admin` prints a new one. Treat it like a password: whoever
   holds it chooses the sign-in provider while setup is open.
2. Open `https://<admin host>[:<admin port>]/setup` at the address users will use (the redirect URIs are built from
   it), and enter the code.
3. The wizard shows the redirect URIs, takes the client ID (default `ume-rag-admin`), the client secret and its
   optional expiry date, with the claim names under *Advanced* (presets for Keycloak and Entra ID), and the provider
   address, which is locked when the deployment sets it (`--oidc-authority`). **Test sign-in** opens the provider in a new window; sign in as yourself.
   *Finish* is offered only when the test passed for a platform admin (the role `rag-platform-admin`, or a group in
   `UME_RAG_OIDC_GROUPS_PLATFORM_ADMIN`).

**With `--hardened`, give `--oidc-authority`:** the host firewall (*4. Host units*) lets rag-admin reach only the
provider in `UME_RAG_OIDC_AUTHORITY`, so without it the wizard's test can't reach the provider (the script asks for it,
and ends with a reminder while it is empty). Without `--hardened` the wizard takes the address. Open WebUI's provider
follows from the authority, and the wizard's Open WebUI step takes its client secret (or declare it,
`--set-file UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET=FILE`).

**Declaring sign-in instead:** an authority (`--oidc-authority`) and the client secret
(`--set-file UME_RAG_OIDC_CLIENT_SECRET=FILE`) declare the sign-in, and the wizard never opens. Each `UME_RAG_OIDC_*`
variable you set (`--set`: `UME_RAG_OIDC_CLIENT_ID`, `_ROLE_CLAIM`, `_DEPARTMENT_CLAIM`, `_TEAM_CLAIM`, `_GROUP_CLAIM`,
`_EXTRA_SCOPE`, `_TENANT_ID`, `_REFUSE_GUESTS`) is a deployment value, locked in the wizard; an empty one is unset.
`rag-bootstrap` writes the client secret and refusing guests as rag-admin's secret files at every start, and removes
them when the variable is removed. Secrets stay out of the repository.

**Roles from groups** (#468; Decisions "Role groups as a setting"). A role in the token's `roles` claim counts as it is.
For an identity provider whose tokens carry groups instead, a platform admin maps groups to roles in RAG's admin
(*Settings → Role groups*: one list per role, each group a `rag-` name or an Entra object id, #413); it applies at each
user's next sign-in. The only deployment value is the break-glass for the first platform admin,
`UME_RAG_OIDC_GROUPS_PLATFORM_ADMIN` in `deploy/.env` (`;`-separated), merged with the setting and never replaced. The claim names default to `roles`, `groups`, `departmentCodes` and `teamCodes`
(`UME_RAG_OIDC_ROLE_CLAIM`, `_GROUP_CLAIM`, `_DEPARTMENT_CLAIM`, `_TEAM_CLAIM`), the gateway's defaults too. Users
keep one account and one set of groups in the shared Keycloak for both products. Whether gateway groups may also grant
RAG roles is open in #468.

**Entra ID from P2:** RAG's own registrations (`ume-rag-admin`, `ume-rag-open-webui`), their app roles and groups are
Bicep in `infra/entra/`, applied by a GitHub workflow after IT's approval: see
[`infra/entra/README.md`](../infra/entra/README.md). Switching is configuration (`Oidc:*`), not code.

**Opening the setup wizard again** (a client secret about to expire, a new provider, a broken sign-in): set
`UME_RAG_SETUP_REOPEN` to a new value (`./deploy/init-deployment.sh --set UME_RAG_SETUP_REOPEN=$(date +%F)`) and run
`docker compose --env-file deploy/.env up -d`. rag-admin opens the wizard once and prints a new setup code, valid for
one hour, in `docker compose --env-file deploy/.env logs rag-admin`; the current sign-in keeps working until you finish.
`init-deployment.sh` keeps the value on a re-run. Before restoring a database, remove the variable first
(`--set UME_RAG_SETUP_REOPEN=`), or the restored database opens the wizard again. The runbook has the lock-out and restore cases (*Sign-in and the setup wizard*).

### 4. Host units

```bash
sudo deploy/host/install.sh            # reads UME_RAG_HOST_MEMORY from deploy/.env
systemctl list-timers 'rag-*'
```

It installs the units of `deploy/host/` ([`deploy/host/README.md`](../deploy/host/README.md)) with this deployment's
`deploy/` path filled in, and enables what `COMPOSE_FILE` uses: `rag-ingestion.slice`, `rag-firewall.service` with its
timer and the restart-unhealthy timer with `compose.hardened.yaml` (`--hardened`: run it before the first `up` then),
the backup, backup-status and upload-copy timers with `compose.backup.yaml` (`--backup-target`), and the
certificate-expiry timer always. It warns when Docker does not use the systemd cgroup driver. The upload-copy timer is
not enabled with an S3 backup target. Options: `--host-memory 16|24` (else from `deploy/.env`),
`--deploy-dir DIR`, `--unit-dir DIR` (only writes the units there) and `--no-enable`. Re-run it after an upgrade or a
change of `--host-memory`.

With `--hardened`, check the firewall rules it applies (no root needed for the dry run):

```bash
deploy/host/docker-user-firewall.sh --dry-run
sudo iptables -S UME-RAG-EGRESS
```

### 5. Start

```bash
docker compose --env-file deploy/.env up -d --wait
docker compose --env-file deploy/.env logs rag-bootstrap                       # its last lines: what is still missing
docker compose --env-file deploy/.env exec -T rag-postgres pgbackrest --stanza=ragdb stanza-create
```

`up --wait` returns when every service is healthy and `rag-migrate` has finished. The end of `rag-bootstrap`'s log
lists what is still missing (a gateway key, an example value) and the setup wizard's address. Then set up sign-in in
the setup wizard (*3. Sign-in*), unless the deployment declares it. `stanza-create` is needed once per
new database or repository and is safe to repeat: WAL archiving fails until the stanza exists (the runbook's
*Where to look first*), and the backup-status timer reports a non-zero `rag_backup_repo_status` until then. Then
take the first backup and escrow the keys:

```bash
sudo systemctl start rag-backup.service      # a full backup, as there is none yet
sudo deploy/escrow-keys.sh /media/<escrow>   # then --verify it, and seal the media (runbook)
```

Back up `rag-secrets/.pepper` and `rag-certs/data-protection.pfx` separately from the database backups (a restored
database is unusable without them). They are read out of the volumes with `rag-bootstrap`'s image, as the escrow
script does:

```bash
image="$(docker compose --env-file deploy/.env config --images rag-bootstrap)"
(umask 077
 docker run --rm --network none -v ume-rag_rag-secrets:/s:ro --entrypoint cat "$image" /s/.pepper > pepper
 docker run --rm --network none -v ume-rag_rag-certs:/c:ro --entrypoint cat "$image" /c/data-protection.pfx > data-protection.pfx)
```

Move both files to the secret store and delete them here. `rag-certs/ca.key`, the internal CA's key, stays in the
volume: anyone who can read it can issue certificates the services trust, and `rag-bootstrap` needs it to re-issue a
service certificate (*Internal TLS*, below). Without it `rag-bootstrap` stops with an error when a certificate is due,
and `up` fails until it is copied back.

Check the listeners with the internal CA, read out of the volume the same way:

```bash
docker compose --env-file deploy/.env ps
docker run --rm --network none -v ume-rag_rag-certs:/c:ro --entrypoint cat "$image" /c/ca.pem > rag-ca.pem
curl --cacert rag-ca.pem https://localhost:8543/health/ready
curl --cacert rag-ca.pem https://localhost:9543/health/ready
```

Every .NET service also answers `/health/live` and `/version`.

### 6. The admin test panel's client

Nothing to do: rag-migrate creates the **`admin-console` client** on its first run, in every deployment (identity design
§4.1): type `internal`, highest information class `Sensitive`, *Stores chat history* off, shown under *Apps and keys* as
managed by the deployment. The assistant test panel writes the cost of each test turn under it, so test use shows apart
from real use on the *Costs* pages. A platform admin may edit its settings but not its name; it has no keys, and none can
be made on it.
An `admin-console` client of type `internal` made by hand before becomes the managed one, and rag-migrate revokes any key
it had.

## Gateway credential keys

Each RAG service calls the LLM gateway with its own key: rag-api, rag-admin and rag-worker each hold one **credential
key** of the gateway integration *Ume.Rag* (identity design §4.7, gateway-changes §5.1; the gateway's
`docs/integrations.md`). A service sends every call with its key and an explicit ceiling (`x-ume-max-residency`), and
never holds another service's key. The three keys come about in one of three ways:

- **A gateway on this host creates them from a manifest**, and `rag-bootstrap` reads them: copy
  `deploy/portainer/integration-manifest.json` to the gateway's `deploy/integration-manifests/ume-rag.json` (mode 644),
  run the gateway's `up -d --wait` again (its migration service applies the manifest and writes each key's secret to
  `deploy/integration-secrets/Ume.Rag/`), then pass `--gateway-secrets-dir <gateway>/deploy/integration-secrets`
  ([quick start](compose-quick-start.md), step 3; the gateway's `docs/integrations.md`, *Provisioning from a manifest*).
  The integration gets a team and department of its own, `ume-rag`: move it to your team in the gateway's admin
  afterwards. To rotate, rotate the key in the gateway's admin, delete its file under `integration-secrets/Ume.Rag/`,
  run the gateway's `up` again, then `docker compose --env-file deploy/.env up -d` (`rag-bootstrap` copies the new
  key) and restart the service.
- **A gateway admin creates them from the preset:** *Access → Integrations → Add from manifest*, the preset *Ume.Rag*.
  The three secrets are shown once; hand them over as step 3 below says.
- **By hand**, field by field, in the gateway's admin UI. The manifest and the preset carry these settings, with an
  empty address list:

1. **The integration.** Under *Access → Integrations*, create `Ume.Rag` with its owner team. Calls made as a credential
   key are charged to that team: the organisation and model syncs, the embedding canary, model checks, and every call
   of an assistant or knowledge base that has no key of its own.
2. **One credential key per service**, in the owner team, with the key form's *Integration* section set to `Ume.Rag`,
   role *Credential*. Name them so the usage report tells them apart, for example `Ume.Rag api`, `Ume.Rag admin` and
   `Ume.Rag worker`. Each key has:
   - **`RequireMaxResidency` on**: the gateway refuses a request without the ceiling header (`400 invalid_request`).
   - **An address list** (`AllowedIpRanges`): the addresses that service's requests reach the gateway from (this host's,
     or the NAT or proxy address the gateway sees; on Kubernetes the cluster's egress addresses).
   - **Allowed residencies capped per service**, at the highest level that service serves. The gateway takes the
     credential key's residencies as an upper bound on every key the service acts as (gateway-changes §5.3), so the cap
     also holds for the keys that assistants and knowledge bases run on. The worker's key: `OnPrem` and `Eu` (it never
     sends `External`). rag-api's and rag-admin's: up to the highest level assistants may use, which is `OnPrem` and
     `Eu` under register M2, and all three once External routes exist. **These residencies are Ume.Rag's ceiling**: each
     service reads its key's residencies from the gateway's context and no call carries a higher ceiling (identity design
     §4.7, M-17 C9), so changing them in the gateway's admin is the one place to change it.
   - **PII policy `Off` or `Allow`**, never `Redact` or `Block`: every service embeds (the worker documents, rag-api
     queries, rag-admin the canary check), and a redacting key would silently change text with personal identity
     numbers in it.
   - **Models**: none listed (all) or every model the platform calls, the embedding model among them.
   - **Scopes** on rag-admin's key only: `organisation.read` (the whole organisation in `GET /v1/ume/context`, which the
     admin's organisation sync reads) and `usage.read` (the cost and budget read-back, below). rag-api's and the
     worker's keys hold no scope, so a key that serves public chat reads nothing else.
   - An expiry as your key policy sets it; rotate before it (below).
3. **Hand them over** as one file per key, holding the key alone on one line, and pass them to `init-deployment.sh`:
   `--gateway-credential-key-file api=rag-api.key --gateway-credential-key-file admin=rag-admin.key
   --gateway-credential-key-file worker=rag-worker.key`. It keeps them in `deploy/.env` (mode 600) as
   `UME_RAG_API_GATEWAY_KEY`, `UME_RAG_ADMIN_GATEWAY_KEY` and `UME_RAG_WORKER_GATEWAY_KEY`, and `rag-bootstrap` writes
   each as its service's secret file `Gateway__Key` in `rag-secrets` at every start. Delete the handed-over files
   afterwards. On Kubernetes, `create-secrets.sh` takes the same option and
   puts each into its service's secret ([Kubernetes](kubernetes.md)); on Portainer the keys are the variables `UME_RAG_API_GATEWAY_KEY`,
   `UME_RAG_ADMIN_GATEWAY_KEY` and `UME_RAG_WORKER_GATEWAY_KEY` (runbook, *Portainer and other hosts without shell
   access*).

Settings, scopes included, are changed by gateway admins only; the key's managers may rotate and revoke it.

**Rotate one key.** In the gateway's admin UI, rotate that service's key with the 24-hour grace period (the new key
keeps the integration, role, scopes, residencies and address list; the old one works until the grace period ends). Put
the new key in a file and re-run `./deploy/init-deployment.sh --gateway-credential-key-file SERVICE=FILE` for that
service only: it changes that variable and keeps the rest of `deploy/.env`. Then run
`docker compose --env-file deploy/.env up -d` (`rag-bootstrap` rewrites the key file) and restart the service:
`docker compose --env-file deploy/.env restart rag-api` (or `rag-admin`, `rag-worker`). Check its log for gateway
`401` answers, then let the grace period end or revoke the old key. Kubernetes: `create-secrets.sh` again with that
option, then
`kubectl rollout restart deployment/rag-<service>`. Portainer: change the variable, update the stack (`rag-bootstrap`
rewrites the file), then restart that service's container. A key that
leaked is rotated without the grace period (*revoke immediately*): the service fails its model calls until it is
restarted with the new key.

### Cost and budget read-back

From gateway v0.10 (its integration phase 3) the admin UI shows the gateway's own figures for an assistant's or
knowledge base's gateway key beside Ume.Rag's: its requests, tokens and cost per day or month and step on the *Cost*
cards, and its budgets with how much is spent (an *Unavailable* badge once one is used up). rag-admin reads them from
the gateway's **Integration API**, `/integration/v1/usage` and `/integration/v1/budgets` on the gateway's **Admin API**
(not the gateway address of `--gateway-url`), with its own credential key. It is optional; without it the admin UI
shows Ume.Rag's figures only.

1. **The scope.** A gateway admin adds the scope `usage.read` to rag-admin's credential key (`Ume.Rag admin` in the
   naming above), next to `organisation.read`. Only rag-admin's key gets it. The key's status, expiry and address list
   apply on the Admin API as on the gateway, so the list must hold the address the Admin API sees rag-admin's requests
   from.
2. **The address.** The gateway names it: rag-admin reads the Integration API's address from its gateway context
   (`integration_api_url`, gateway v0.10.2), which is the gateway's `UME_INTEGRATION_API_URL` (`Gateway:IntegrationApiUrl`)
   ending in `/integration/v1`, as rag-admin reaches it: for example `https://llm-admin.intern.umea.se/integration/v1`
   (the gateway's Compose stack publishes the Admin API on 9443; behind its proxy, usually its own host name on 443).
   The gateway admin sets it; Ume.Rag has no setting of its own, and while the gateway names none there is no read-back.
3. **The firewall.** Add the address's host and port to the `llm` network's allow-list:
   `docker-user-firewall.sh --allow llm=<gateway admin host>:<port>`, in a drop-in for `rag-firewall.service`
   (`sudo systemctl edit rag-firewall.service`: clear `ExecStart=` and repeat it with the option; *Host firewall* in the
   runbook), with `--hardened`. Outside the host, the network firewall must let the RAG host reach
   `<gateway admin host>:<port>` over HTTPS ([Network and ports](network-and-ports.md)). rag-admin must trust the Admin
   API's certificate: its CA as a `*.pem` in `UME_RAG_TRUST_DIR` if it is not the gateway's own.
4. **Apply:** restart the firewall with the new allow-list (`sudo systemctl start rag-firewall.service`). rag-admin picks
   the address up at its next context read (within 5 minutes), without a restart.

**Check:** the *Gateway* page's status, `GET /api/gateway/status`, has `integration.configured: true` and no
`integration.lastError` after an assistant's or knowledge base's *Cost* card has been opened; a reason there is in the
runbook (*Gateway keys, budgets and the read-back*). Kubernetes: the Admin API's port in your egress rules. Portainer:
the gateway's `UME_INTEGRATION_API_URL` names an address rag-admin reaches (the gateway's `adminapi` is not on the `llm`
network).

## Backup target

`--backup-target` decides where pgBackRest's repository `repo1` and the upload copy go, and adds `compose.backup.yaml`
(WAL archiving, the repository settings and the upload copy's service `rag-upload-backup`) to `COMPOSE_FILE`. The
repository is always encrypted client-side (`aes-256-cbc`, passphrase in `rag-secrets/pgbackrest/cipher-pass`, which
`rag-bootstrap` generates once and writes into `pgbackrest/repo.conf`; escrowed).

| Target | pgBackRest repository | Upload copy | `deploy/.env` |
|---|---|---|---|
| `DIR` (a share mounted on the host) | `DIR/pgbackrest`, mounted into rag-postgres at `/var/lib/pgbackrest` | `DIR/uploads` (`rag-upload-backup.timer`) | `UME_RAG_BACKUP_DIR`, `UME_RAG_UPLOAD_BACKUP_DIR` |
| `s3://ENDPOINT/BUCKET[/PATH]` | the bucket, path `/PATH` (default `/ume-rag`), path-style URIs, over the `backup` network | not built yet: replicate the bucket's `uploads/` prefix once the blob store moves to S3 | `UME_RAG_BACKUP_ENDPOINT` (the firewall allows only this) |
| `local` (test hosts only) | the `rag-pgbackrest-repo` Docker volume | the `rag-upload-copy` Docker volume | – |

`local` is not a backup off the host. With a share, mount it before Docker starts (for example with an `fstab` entry
or a systemd mount unit that `docker.service` requires). Docker creates the two directories at the first `up` when they
are missing, and `rag-bootstrap` gives them to uid 999 (pgBackRest) and 1654 (the upload copy); a share that refuses
root's `chown` is named on its checklist, and then you `chown` them on the share. Retention is 35 days (`repo1-retention-full=35`): a full backup expires
once a newer one is at least 35 days old.

## Malware scan

The malware scan is the deployment's choice (Decisions "Malware scan optional"): the Compose profile `clamav`, in
`COMPOSE_PROFILES` by default (`init-deployment.sh --profiles`). The worker's `ClamAv:Mode` follows it: `rag-bootstrap`
writes it as the worker's secret file, `on` with the profile and `off` without.

- **`on`** (the profile `clamav`, the default): `rag-clamav` runs, and the worker scans
  every upload and every fetched file other than HTML, text and CSV before storing it. The scan fails closed: while
  clamd is down or still loading its signatures, those files wait with `clamav.unavailable` and are retried.
- **`off`**: no `rag-clamav` container runs and the worker contacts no clamd. Files are stored and parsed **without a
  malware scan**: nothing is quarantined. The worker says so at every start with a warning and security event 5106
  `rag.malware.scan_off` ([security events](security-events.md)); its `clamav` health check stays Healthy and says that
  the scan is off; upload slots are `unscanned` (never `clean`) with no scan time, a run's report has
  `malwareScan: "off"`, and the admin UI shows *Not scanned for malware* on those files and runs. Sizing: the
  `rag-clamav` row of *Sizing* (above) drops out, and the `rag-mirror` network and `database.clamav.net` are not used.

Choose `off` only where the files come from sources you trust, or where they are scanned before they reach RAG. **Files
stored while the scan is off are not scanned when you turn it on later**: they stay as they are and keep their
*Not scanned for malware* mark; only new and changed files are scanned from then on.

To change it, re-run `init-deployment.sh --profiles` with the new list, with or without `clamav` (for example
`--profiles ''` or `--profiles clamav`; the list replaces the current one), then
`docker compose --env-file deploy/.env up -d --wait`, which recreates the worker with the new mode. When turning it off, also `docker compose --env-file deploy/.env rm -sf rag-clamav` (`up` leaves a
container of a profile that is off alone). When turning it on, freshclam downloads the signatures first (several
minutes); files wait with `clamav.unavailable` until clamd is ready. On Kubernetes the same choice is the
`components/clamav` component ([Kubernetes](kubernetes.md), *Malware scan*); in the Portainer stack, `clamav` in
`COMPOSE_PROFILES`, as here.

## ClamAV signatures

With the malware scan on, freshclam updates the signatures straight from `database.clamav.net` (no internal mirror), four times a day, through
the `rag-mirror` network, which reaches nothing else. With `--hardened`, that host name is served by a CDN: the
firewall resolves it when it runs (at Docker start and every 15 minutes), so an address change can block updates until
the next run. **Where IT provides an egress proxy, use it**: `--freshclam-proxy HOST:PORT` has `rag-bootstrap` write
`HTTPProxyServer`/`HTTPProxyPort` into a copy of `freshclam.conf`, and the firewall then lets the `rag-mirror` network
reach only the proxy.

The ClamAV image's own confs are used unless a setting differs. `--host-memory 24` or `--freshclam-proxy` adds
`compose.clamav-conf.yaml` to `COMPOSE_FILE`: `rag-bootstrap` then copies the image's confs (`images/clamav/`, at the
same path in a release bundle) to `rag-config/clamav`, changes them, and the overlay mounts the copies into
`rag-clamav`.

## Renderer

`rag-renderer` renders the pages of sources with fetch mode `browser`: Chromium behind Playwright's `launch-server`
(`images/renderer/`, its README holds the contract), driven by the worker over WebSocket on port 3000. It sits only on
the internal `rag-render` network, so the page scripts it runs reach nothing but the worker, which fetches every
request itself with the crawler's guard (connectors design §8.3). The worker finds it through `Render__Endpoint`
(`ws://rag-renderer:3000/<secret path>`, the secret file `rag-secrets/worker/Render__Endpoint`); without that file
`browser` sources wait with `render.not_configured`. Rotate the path by deleting `.render-ws-path` in `rag-secrets`
(`docker run --rm -v ume-rag_rag-secrets:/s alpine rm /s/.render-ws-path`), then
`docker compose --env-file deploy/.env up -d` and `docker compose --env-file deploy/.env restart rag-renderer rag-worker`.

It is **off unless you ask for it**: the service has the Compose profile `render`, and `rag-bootstrap` writes the
worker's endpoint file only while `render` is in `COMPOSE_PROFILES` (and removes it without), so the profile and the
endpoint always go together. To turn it on or off, re-run `init-deployment.sh --profiles` with the new list, with or
without `render` (for example `--profiles clamav,render`), then `docker compose --env-file deploy/.env up -d --wait`,
which recreates the worker with the new endpoint. When turning it off,
`docker compose --env-file deploy/.env rm -sf rag-renderer` stops the renderer (`up` leaves a container of a profile
that is off alone).

It runs hardened like the others. With `--hardened` (`compose.hardened.yaml`) it gets a seccomp profile of its own
(`images/renderer/seccomp.json`: Docker's default with `clone`, `unshare` and `chroot` allowed), so Chromium's own
sandbox can create its user namespaces; without that overlay it runs without Docker's seccomp filter, as in the
Portainer stack, and Chromium's own sandbox stays on. Without them
Chromium refuses to start ("No usable sandbox") and the renderer restarts in a loop: `docker compose --env-file
deploy/.env logs rag-renderer` shows it. On Ubuntu 23.10 and later, if AppArmor restricts unprivileged user namespaces,
that is what happens; do not turn the sandbox off, agree a fix with IT (an AppArmor profile for the renderer, or the
sysctl). One browser serves every render; launch-server exits when Chromium does and Docker restarts it.

## Embedding request size

The worker sends at most 64 chunks per embedding request, with no cap on the request's total tokens. From v0.9.0 the
gateway checks each input of an embedding request against the model's context window (its longest input, estimated at
about 4 bytes per token), so the embedding deployment may have a `ContextWindow`. A chunk holds at most about 920
tokens (template `chunkTokens` ≤ 800, plus the tail merge), far below the embedding profile's `MaxInputTokens`; a
refused input fails the document (`embed.input_rejected`) and is a bug to report.

## Rerank (optional)

Rerank is optional (Decisions "Gateway release 1 and shared Keycloak as built"): while the platform has no model for an
assistant's reranker, chat, the test panel and `/v1/retrieve` make no rerank call and degrade nothing. Two rerankers
exist, each with its own platform model (run-time settings, the admin's `PUT /api/settings/retrieval`) and its own time
limit (deployment knobs, text read included; past the limit the fused order stands, `rerank_unavailable`):

| Reranker | Model (`Settings.Retrieval.*`) | Time limit (`Retrieval:*`, environment `Retrieval__*`) |
|---|---|---|
| Chat-model reranker (interim, a chat call that scores the passages) | `rerankModel`: a chat model of the mirror | `RerankTimeout`, default 3 s |
| Gateway reranker (`POST /v1/rerank`, Cohere-compatible) | `gatewayRerankModel`: a model of kind `rerank` in the mirror (`Cohere-rerank-v5-fast` on Azure AI Foundry at `Eu` for the POC; on-prem later) | `GatewayRerankTimeout`, default 1.5 s |

A gateway rerank model is billed per input token: its mirror row has `InputPerMillionUsd` and no `OutputPerMillionUsd`.
The evaluation's `hybrid-rerank` and `hybrid-rerank-gateway` variants allow either call 30 s and skip a variant whose
model is unset (`no_rerank_model`); see [`scripts/evaluation/README.md`](../scripts/evaluation/README.md).

## Internal TLS

Every connection inside the host is TLS with the internal CA: the services to rag-postgres (`SSL Mode=VerifyFull`), the
worker to the parser (`https://rag-parser:8000`, certificate `rag-certs/parser/` with the name `rag-parser`, set
through `PARSER_TLS_CERT_FILE` and `PARSER_TLS_KEY_FILE`), and the health probes. Only clamd (TCP 3310) has no TLS, so
it stays on the internal `rag-ingestion` network, and the renderer's WebSocket (port 3000) is plain, with a secret
path, on the internal `rag-render` network.

Internal certificates last 825 days, the internal CA 10 years. `rag-bootstrap` re-issues a service certificate at the
first start within 30 days of its expiry (or when its host names change), which needs `rag-certs/ca.key`; run
`docker compose --env-file deploy/.env up -d` then, and restart what its checklist names
(`docker compose --env-file deploy/.env restart rag-postgres rag-parser`: they read their certificates only at start).
The certificate-expiry timer (`deploy/host/cert-expiry.sh`, *4. Host units*) warns 60 days ahead. **A new internal
CA:** delete `ca.pem` and `ca.key` in `rag-certs`
(`docker run --rm -v ume-rag_rag-certs:/c alpine rm /c/ca.pem /c/ca.key`) and run `docker compose --env-file deploy/.env up -d --wait`: `rag-bootstrap` creates a new CA and re-issues
every service certificate. Then restart every Ume.Rag service (and `rag-proxy`, `rag-openwebui` where their profiles
are on), which read the CA only at start: `docker compose --env-file deploy/.env restart`.

## Kubernetes

Instead of Compose, `deploy/kubernetes` has Kustomize manifests for the same services, settings, certificates and
secrets, with rolling updates built in: see [Kubernetes](kubernetes.md). Sign-in through the gateway's Keycloak, a
Keycloak of RAG's own, or Entra ID directly: see [Keycloak](keycloak.md).

## Portainer (gateway and RAG in one stack)

No shell access on the host? `deploy/portainer/compose.portainer.yaml` is the gateway's own Portainer stack with Ume
Rag added, configured only through environment variables: paste it over the gateway's stack, keep the stack name, and
set the RAG variables listed at the top of the file. See the runbook, *Portainer and other hosts without shell access*,
and [portainer.md](portainer.md) for a first deploy and each upgrade step by step.

**On a new VM** (Docker and Portainer installed, nothing else):

1. **Take the release's file:** the `compose.portainer.yaml` asset of the GitHub release (or the repository's copy at
   the release tag). It pins the gateway's images (the gateway release it was built from, `v0.11.0` for Ume.Rag
   `0.7.0`) and Ume.Rag's by digest, so no image variable is needed.
2. **Registry:** Ume.Rag's images on `ghcr.io/thekim1/ume-rag/*` are private: add `ghcr.io` as a registry in Portainer
   with a GitHub user and a token holding `read:packages`, and pick it when you create the stack. The gateway's images
   are public.
3. **Variables:** `UME_DOMAIN`, which names the gateway API and its admin `llm.` and `llm-admin.<domain>`, and
   rag-api, its admin and Open WebUI `rag.`, `rag-admin.` and `chat.<domain>` (or `UME_GATEWAY_HOST`,
   `UME_RAG_API_HOST` and the other host variables instead), and `COMPOSE_PROFILES=clamav` (add `openwebui` for the
   chat, `render` for JavaScript pages). The credential keys come from the stack file's default manifest (leave the
   gateway's `UME_INTEGRATION_MANIFEST` unset), or are issued by hand and set as the three `UME_RAG_*_GATEWAY_KEY`. No
   provider and no client secret: after the first start, sign-in is set in the gateway's setup wizard and then in
   rag-admin's, each with the setup code from its log ([portainer.md](portainer.md), *First deploy* steps 4–5), unless
   the stack declares it (`UME_OIDC_AUTHORITY`).
4. **TLS:** the gateway (8443/9443) and Ume.Rag (8543/9543) serve certificates from their own internal CAs; Open WebUI
   (3000) serves plain HTTP on loopback. Put a reverse proxy with real certificates in front of the public names
   (*Reverse proxy and PKI* below for the routes and body sizes), and set `UME_RAG_OPENWEBUI_BIND` and the other `*_BIND`
   variables to match. Or add `proxy` to `COMPOSE_PROFILES`: the stack's own nginx with your certificate in front of
   all five host names ([portainer.md](portainer.md#tls-proxy)).
5. **Open WebUI** (profile `openwebui`): its host is `chat.<domain>` (`UME_RAG_OPENWEBUI_HOST` for another); its
   OIDC client (redirect URI `https://<host>/oauth/oidc/login/callback`) is `UME_RAG_OPENWEBUI_OIDC_CLIENT_ID` and
   `UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET`, or the values entered in the setup wizard's Open WebUI step
   (*Open WebUI's sign-in settings* below). Open WebUI's key for rag-api needs nothing: `rag-bootstrap` generates it
   and `rag-migrate` registers it as the deployment's own Open WebUI client (*Apps and keys*, marked *Managed by the
   deployment*; identity design §5.2). The feedback import's key is minted by the one-shot `rag-openwebui-setup` (as
   *The feedback service account* below says for Compose); `rag-bootstrap` points
   rag-api at the stack's own Open WebUI while the `openwebui` profile is on. As with Compose, Open WebUI keeps its own copy of each chat
   (W2, below).
6. **The embedding profile** is not a variable: once the keys are set, a platform admin creates it in Ume.Rag's admin
   (*Models → Embedding profiles → Create profile*, [portainer.md](portainer.md) step 9). Until then no knowledge base
   can be created.

## Reverse proxy and PKI

The listeners are published on `0.0.0.0:8543` (rag-api) and `0.0.0.0:9543` (rag-admin) by default, so whoever reaches
the host reaches them; `UME_RAG_API_BIND` and `UME_RAG_ADMIN_BIND` (`--set`) publish them on another address, such as
`127.0.0.1`. Open WebUI's plain-HTTP port is published on `127.0.0.1:3000` (`UME_RAG_OPENWEBUI_BIND`). Two ways to put
TLS with real certificates in front:

- **The stack's own TLS proxy**, the profile `proxy` (`--profiles clamav,proxy`): `rag-proxy`, nginx on
  `UME_RAG_PROXY_BIND:UME_RAG_PROXY_PORT` (`0.0.0.0:443`), in front of rag-api, rag-admin and, with the profile
  `openwebui`, Open WebUI, each on a host name of its own. `rag-bootstrap` writes its `nginx.conf` from
  `deploy/proxy/nginx.portainer.conf.template` with the routes and body sizes below, and it re-encrypts to the services,
  verifying them against the internal CA. Put one certificate for the names in a host directory as `tls.crt` (with its
  chain) and `tls.key`, readable by uid 101, and set `UME_RAG_PROXY_CERTS_DIR` to it (default
  `/etc/ume-rag/proxy-certs`); [portainer.md](portainer.md#tls-proxy) has the steps. It knows one slot of each
  service, so not with `--rolling`.
- **A reverse proxy of your own** (the municipal one): `deploy/proxy/nginx.conf.example` has the server blocks for
  rag-api, the admin host and Open WebUI (the lines marked `#owui`) and both slots with `--rolling` (`#rolling`), ready
  for nginx once its placeholders are filled in and your certificates added. For another proxy, the routes:
  each host name forwarded over HTTPS to its port, preserving the `Host` header; `client_max_body_size 60m` and
  `proxy_request_buffering off` on `PUT /api/upload-batches/{batchId}/files/{n}` of the admin host, 2 MB on the api host
  (and on Open WebUI's), because `/v1/chat/completions` takes up to 2 MB of conversation, and 1 MB elsewhere.

Only the proxy should reach the published ports. With `--hardened`, `--proxy-address` makes the host firewall accept new
connections to them only from the proxy's addresses; otherwise restrict them in a network firewall, or publish them on
an address only the proxy reaches (`UME_RAG_API_BIND`, `UME_RAG_ADMIN_BIND`). Without a proxy, rag-api's public
address carries the port: set `UME_RAG_API_PUBLIC_URL` (`https://<api host>:8543`), as the script says.

Add the proxy's addresses to the trusted proxies (admin UI, Settings, *Network*) and have it set `X-Forwarded-For`:
rag-api and the admin then see the client's address (key IP allow-lists, the refused-credential throttle, security
events); without the entry every request comes from the proxy, and a key allow-list holding a client host refuses it.
With certificates from your PKI for the two listeners, put `api.crt` and `api.key`, `admin.crt` and `admin.key` in a
host directory and set `UME_RAG_PKI_DIR` to it (`--set UME_RAG_PKI_DIR=DIR`): `rag-bootstrap` copies a pair found there
at every start instead of issuing one from the internal CA, so a renewed pair is picked up at the next
`docker compose --env-file deploy/.env up -d`. The chain the services must trust goes into `UME_RAG_TRUST_DIR` as a
`*.pem` file.

## Telemetry

The services export OpenTelemetry (logs, metrics, traces) only when an endpoint is set:
`./deploy/init-deployment.sh --set UME_OTEL_ENDPOINT=<collector>` (with `--hardened`, the firewall then allows it from
the `llm` and `rag-crawl` networks) and run `up -d` again. Health-check requests are not traced, and query strings are
removed from HTTP spans. Traces are sampled: a tenth of the requests that start at a service (`UME_OTEL_TRACES_SAMPLER`,
default `parentbased_traceidratio`, and `UME_OTEL_TRACES_SAMPLER_ARG`, default `0.1`; `always_on` records every
request),
and a request that arrives with a sampled parent is always recorded. Logs and metrics are not sampled. The
backup scripts write Prometheus text files to `/var/lib/ume-rag/` (runbook, *Backups*).

Security events (logger category `Ume.Rag.Security`, [security-events.md](security-events.md)) go to the SIEM through a
collector: [`deploy/otel/collector-siem.example.yaml`](../deploy/otel/collector-siem.example.yaml) keeps only that
category and sends it as syslog (RFC 5424), with Splunk, Elastic and Sentinel alternatives, as the gateway's does. One
collector can serve both products.

## Open WebUI (optional)

Open WebUI is the Compose profile `openwebui` (`rag-openwebui` and its one-shot `rag-openwebui-setup`), off by default.
It is started only once the DPIA (register G1) has decided register item **W2**, how Open WebUI's own copies of the
chats are pruned; until then they stay unpruned. It needs the 24 GB host.

```bash
./deploy/init-deployment.sh --profiles clamav,openwebui --set UME_RAG_OPENWEBUI_HOST=chat.intern.umea.se
docker compose --env-file deploy/.env up -d --wait
sudo systemctl start rag-firewall.service   # with --hardened: allow rag-edge Open WebUI's provider now
```

`UME_RAG_OPENWEBUI_HOST` defaults to `chat.<domain>` with `--domain`. Its sign-in is the setup wizard's Open WebUI step
(*Open WebUI's sign-in settings*, below), or declared with `--set-file UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET=FILE`.

Open WebUI's key for rag-api is generated by `rag-bootstrap` once (`rag-secrets/.openwebui-rag-api-key`, given to Open
WebUI as `openwebui/rag-api-key`, which its start wrapper passes on as `OPENAI_API_KEYS`), and `rag-migrate` registers
it as the deployment's own Open WebUI client while the profile is on (identity design §5.2): nothing to create in the
admin. A new key: delete `.openwebui-rag-api-key` in `rag-secrets`
(`docker run --rm -v ume-rag_rag-secrets:/s alpine rm /s/.openwebui-rag-api-key`), run
`docker compose --env-file deploy/.env up -d --wait` and restart `rag-api` and `rag-openwebui`; `rag-migrate` revokes
the old key. The run book for a local P1a run is [p1a-local-run.md](p1a-local-run.md).

**The feedback service account** (chat design §14.2). rag-api's feedback importer reads Open WebUI's feedback list
and deletes feedback past retention with the API key of an Open WebUI admin account that is never a person.
Its email is `feedback-import@localhost` (`WEBUI_ADMIN_EMAIL`) and its password is generated once by `rag-bootstrap`
(`rag-secrets/openwebui/admin-password`, which the start wrapper passes on as `WEBUI_ADMIN_PASSWORD`), and Open WebUI
creates it as its first user, so as its only admin, when it starts on an empty store. Everyone signing in through OIDC
gets the role `user` (`DEFAULT_USER_ROLE`), so no person becomes admin. Password sign-in is always off
(`ENABLE_PASSWORD_AUTH`), and nobody signs in as the account: the one-shot `rag-openwebui-setup` mints its key when
Open WebUI starts (`deploy/openwebui/feedback-key-setup.py`). It runs Open WebUI's own image as Open WebUI's user, reads
the account's id from Open WebUI's store, signs a 5-minute session token with Open WebUI's `WEBUI_SECRET_KEY`, mints
the key with it, checks that the key reads the feedback list (only `API_KEYS_ALLOWED_ENDPOINTS` is open to API keys)
and writes it to `rag-secrets/openwebui-feedback/OpenWebUi__AdminApiKey`, which rag-api reads without a restart
(`Security__ExtraSecretsDirectory`). A key that still works is kept on the next `up`; a new key: delete that file
(`docker run --rm -v ume-rag_rag-secrets:/s alpine rm /s/openwebui-feedback/OpenWebUi__AdminApiKey`) and run
`docker compose --env-file deploy/.env up -d` again. If the store had users before the account was created (a store
Open WebUI used before), the job stops because the account is not admin: create it as an existing admin with
`POST /api/v1/auths/add` (name, the email above, the password from `rag-secrets/openwebui/admin-password`, role
`admin`), then run `up` again. rag-api's importer finds Open WebUI by itself: `rag-bootstrap` gives it the stack's
`rag-openwebui` while the profile is on.

It has one connection only, to `rag-api` inside the stack (`OPENAI_API_KEYS` is the `ume-rag-sk-` key of its client,
generated by `rag-bootstrap`; its JWT secret is generated once and shared with rag-api's `OpenWebUi__JwtSecret`), signs
in only through its own OIDC client (`open-webui` on the shared Keycloak in the pilot; Entra's `ume-rag-open-webui` from
P2 with `UME_RAG_OPENWEBUI_OIDC_CLIENT_ID` and `UME_RAG_OPENWEBUI_OIDC_PROVIDER_URL`), and listens on plain HTTP on
`127.0.0.1:3000` (`UME_RAG_OPENWEBUI_BIND`, `UME_RAG_OPENWEBUI_PORT`), so a TLS proxy in front terminates TLS (the
profile `proxy`, or yours: *Reverse proxy and PKI*). Its settings come from the Compose file on every start; an admin's
edit in Open WebUI lasts until the next restart. Its fixed input warning (`WEBUI_BANNERS`) still shows a close button:
a closed banner returns on the next reload.

**Open WebUI's sign-in settings** (identity design §19) are not Open WebUI's own: rag-api holds them. Its start wrapper
(`deploy/openwebui/start-with-sign-in.py`, the container's entrypoint) asks rag-api's internal route
`/internal/openwebui/sign-in` with Open WebUI's key (only the deployment's own Open WebUI client may), waits up to a
minute for rag-api at start, starts Open WebUI with the provider URL, client id, client secret and button label it
answers, and asks again every minute: when an answer differs it stops Open WebUI and exits, and `restart:
unless-stopped` starts it with the new values (users then sign in again). Each value comes from the deployment
(the `UME_RAG_OPENWEBUI_OIDC_*` variables in `deploy/.env`; the client secret reaches rag-api and rag-admin as secret
files from `rag-bootstrap`) when set, else from rag-admin's
setup wizard, else the default (client `open-webui`, rag-admin's authority's discovery document, *Umeå kommun*). With
no provider at all, or a stored secret rag-api can't read, it starts Open WebUI without OIDC sign-in, so nobody can sign
in (its log says so). A provider the wizard sets on another host than the authority needs `--allow rag-edge=HOST` for
the host firewall (`deploy/host/docker-user-firewall.sh`). The button's label is also a setting in RAG's admin
(*Settings*, `PUT /api/settings/openwebui`) unless the deployment sets it.
