# Production setup

The pilot runs on one Linux host with Docker Compose: six long-running containers (`postgres` with pgBackRest inside,
`rag-api`, `rag-admin`, `rag-worker`, `rag-parser` and, unless `--clamav off`, `rag-clamav`: *Malware scan*, below), with
`--render` a seventh (`rag-renderer`, below), and the one-shot `rag-migrate`, all in the Compose project `ume-rag`. `deploy/compose.prod.yaml` holds the data plane and
control zone, `deploy/compose.ingestion.yaml` the ingestion zone; `init-deployment.sh` lists both (and the overlays your options need) in `COMPOSE_FILE` in `deploy/.env`,
so every command below is `docker compose --env-file deploy/.env ...` without `-f`. Run the commands from the directory
that holds `deploy/`.

Day-to-day operation (backups, restore, key escrow, the firewall, troubleshooting) is in the [runbook](runbook.md);
upgrades in the [upgrade guide](upgrade-guide.md); the test host's choices and checks in [test-host.md](test-host.md). Ports, firewall and proxy rules per setup are in [network-and-ports.md](network-and-ports.md). The design behind this page is
[`plans/platform/repo-and-deployment.md`](../plans/platform/repo-and-deployment.md) §6 and §7.

## Host prerequisites

- **Linux with systemd**, Docker 28+ and Compose v2.24+ (the 24 GB overlay uses `!override`), `openssl` (for
  `init-deployment.sh`), and root for the host units.
- **cgroup v2 and Docker's systemd cgroup driver.** The ingestion containers run below `rag-ingestion.slice`
  (`cgroup_parent`), which caps the whole zone; with the `cgroupfs` driver the slice caps nothing. `docker info` must
  show `Cgroup Driver: systemd` and `Cgroup Version: 2`.
- **`172.30.0.0/16` stays out of Docker's `default-address-pools`.** The Compose networks use fixed subnets there
  (`edge` 172.30.1.0/24 to `mirror` 172.30.7.0/24, `backup` 172.30.16.0/24, `openwebui` 172.30.17.0/24, `render`
  172.30.18.0/24), so the host
  firewall can give each its own egress rule. If Docker hands out a pool from that range to another network first,
  `up` fails. `deploy/host/docker-user-firewall.sh --print-subnets` lists them.
- **Docker's iptables integration** (the default), so the `DOCKER-USER` chain exists; not the nftables firewall
  backend. The firewall is IPv4 only (the networks have no IPv6).
- **Unprivileged user namespaces** for the renderer's Chromium sandbox (`rag-renderer`, with `--render`, below). On
  Ubuntu 23.10 and later AppArmor may restrict them (`sysctl kernel.apparmor_restrict_unprivileged_userns`): check that
  the renderer becomes healthy after the first `up` (*Renderer*, below).
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
| `rag-renderer` | ingestion | 2.0 | 256m | 2g | 2g |
| `rag-ingestion.slice` (zone cap) | ingestion | `CPUQuota=500%` | – | `MemoryMax=7G` | `MemoryMax=11G` |
| `open-webui` (optional) | – | 1.0 | 512m | not on a 16 GB host | 1.5g |

During development the base Compose files cap every container at 2 CPUs or less (decisions table, "Low CPU during
development"): `postgres` at 2.0 and `rag-parser` at 2.0 with 1 slot × 2 threads. Only `--host-memory 24`
(`compose.host-24g.yaml`) restores the CPUs of the table; the pilot's CPU sizing is set again before go-live with the
R1 release test.

The limits add up to about 12 GB on a 16 GB host and 18.75 GB on a 24 GB host (20.25 GB with Open WebUI); the OS and
Docker keep about 2 GB, and the limits are not all reached at once. Under CPU contention the data plane (weight 1024)
gets about four times the CPU of each ingestion container, and the slice holds the ingestion zone at five cores. On a
24 GB host ClamAV keeps its old engine during a signature reload (`ConcurrentDatabaseReload yes`) and test-loads
updates (`TestDatabases yes`); on a 16 GB host it has no room for that. Without ClamAV (`--clamav off`, *Malware scan*,
below) the limits add up to 1.5 GB less on a 16 GB host and 3 GB less on a 24 GB host, with 1.2 GB less reserved and one
CPU fewer in the ingestion zone; the slice's cap stays as it is. The R1 release test (P1) confirms or corrects these
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

The release also carries an SPDX SBOM per image. The bundle holds `deploy/`, the operator docs (this guide, the
runbook, the upgrade guide) and `images/renderer/seccomp.json`, the renderer's seccomp profile, at the path the Compose
file names.

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

Images built elsewhere and pushed to a registry are set in `deploy/.env` by digest, for example
`API_IMAGE=ghcr.io/<owner>/ume-rag/api@sha256:...`. The variables are `POSTGRES_IMAGE`, `MIGRATIONS_IMAGE`,
`API_IMAGE`, `ADMIN_IMAGE`, `WORKER_IMAGE`, `PARSER_IMAGE`, `CLAMAV_IMAGE`, `RENDERER_IMAGE` and, for the optional stack,
`OPENWEBUI_IMAGE`.

### 2. `init-deployment.sh`

```bash
./deploy/init-deployment.sh --oidc-authority https://<keycloak host>/realms/ume \
  --oidc-client-secret-file admin-client.secret \
  --api-host rag-api.intern.umea.se --admin-host rag-admin.intern.umea.se \
  --gateway-url https://llm.intern.umea.se --ca-file municipal-ca.pem \
  --gateway-credential-key-file api=rag-api.key --gateway-credential-key-file admin=rag-admin.key \
  --gateway-credential-key-file worker=rag-worker.key \
  --crawl-allow 10.20.0.0/16 \
  --backup-target /mnt/backup/ume-rag --host-memory 24 --freshclam-proxy proxy.intern.umea.se:8080 \
  --allow-file-keys --yes
```

Without `--yes` it prompts for what is missing. It generates, under `deploy/certs` and `deploy/secrets`: an internal CA
and certificates for postgres, the worker, the parser and the two public listeners; the Data Protection certificate;
the database passwords and connection strings; the key pepper; the api's download-grant, cache-key and
conversation-id keys; the Open WebUI JWT secret; the worker's crawl key pepper (`Security__CrawlKeyPepper`, the HMAC key
of the host hash in security event 5101); the renderer's secret WebSocket path (`.render-ws-path`, written for the
renderer as `renderer.env` and, with `--render`, for the worker as `worker/Render__Endpoint`); the blob master key
`keys/rag-blob-internal`; the pgBackRest passphrase and `pgbackrest/repo.conf`; and `deploy/.env` (non-secret settings
only). Secrets and certificates reach the RAG services as read-only files, never as environment variables (Open WebUI
and the renderer are the exceptions: they read their secrets only from their environment, so they come from
`secrets/openwebui.env` and `secrets/renderer.env`, which `docker compose` reads).

It is safe to re-run: existing secrets, keys and the Data Protection certificate are never overwritten (a new one would
break existing data or backups). Connection strings and other derived files are written again, and a re-run without
`--renew-certs` rewrites `deploy/.env` and keeps the previous one as `deploy/.env.bak`, so carry over any lines you
added by hand. After the first run the files belong to the containers' users, so re-run it as root.

| Option | Default | Effect |
|---|---|---|
| `--oidc-authority URL` | prompted | the admin BFF's identity provider (HTTPS): `UME_RAG_OIDC_AUTHORITY`. P1: the shared Keycloak's realm, `https://<keycloak host>/realms/ume`; P2: `https://login.microsoftonline.com/<tenant>/v2.0` |
| `--oidc-client-id ID` | `ume-rag-admin` | the admin's own OIDC client (Keycloak in the pilot, Entra from P2), never the gateway's |
| `--oidc-client-secret-file FILE` | – | the admin registration's secret: `secrets/admin/Oidc__ClientSecret` |
| `--api-host HOST`, `--admin-host HOST` | prompted (the host name; the admin defaults to the api's) | public host names, in the listener certificates, `UME_RAG_API_PUBLIC_URL` and `UME_RAG_ADMIN_PUBLIC_HOST` |
| `--api-port N`, `--admin-port N` | 8543, 9543 | host ports of the two listeners |
| `--bind ADDR` | `127.0.0.1` (`0.0.0.0` with `--proxy`) | address the listeners are published on |
| `--gateway-url URL` | prompted | the LLM gateway (HTTPS): `UME_RAG_GATEWAY_URL` |
| `--ca-file FILE` | – | appended to the trust store of api, admin and worker: the gateway's CA and your PKI chain |
| `--gateway-credential-key-file SERVICE=FILE` | – | `SERVICE`'s credential key of the gateway integration *Ume.Rag* (`SERVICE` is `api`, `admin` or `worker`; repeat, one per service): `secrets/SERVICE/Gateway__Key`. A gateway admin issues the three keys (*Gateway credential keys*, below). A service without one has its model calls refused |
| `--key-provider file` | `file` | the only key provider until postponed decision K1 |
| `--allow-file-keys` | – | required: accepts that the blob master key lives in a file on this host (pilot and test hosts) |
| `--backup-target DIR\|s3://ENDPOINT/BUCKET[/PATH]\|local` | prompted | pgBackRest `repo1` and the upload copy (below) |
| `--backup-s3-credentials-file FILE` | – | with an `s3://` target: access key and secret key on two lines |
| `--backup-s3-region R` | `us-east-1` | with an `s3://` target |
| `--crawl-allow CIDR[,CIDR]` | – | private ranges the crawler may fetch (municipal hosts): `UME_RAG_CRAWL_ALLOW`, used by the worker and the firewall |
| `--host-memory 16\|24` | 16 | the host profile; 24 adds `compose.host-24g.yaml` and, with the malware scan on, the ClamAV confs |
| `--freshclam-proxy HOST:PORT` | – | freshclam reaches `database.clamav.net` through this HTTP proxy |
| `--clamav on\|off` | `on`; a re-run without it keeps the value in `deploy/.env` | the malware scan: `UME_RAG_CLAMAV_MODE` (the worker's `ClamAv__Mode`) and, with `on`, the profile `clamav` in `COMPOSE_PROFILES`, so `rag-clamav` runs. `off` runs no `rag-clamav`, writes no ClamAV confs (even with `--host-memory 24` or `--freshclam-proxy`) and says so at the end: files are stored without a malware scan (*Malware scan*, below). `--renew-certs` keeps what `deploy/.env` says |
| `--render` | – | runs `rag-renderer` for sources with fetch mode `browser`: `COMPOSE_PROFILES=render` in `deploy/.env` and the worker's `secrets/worker/Render__Endpoint`. Without it neither, and those sources wait with `render.not_configured` (*Renderer*, below). `--renew-certs` keeps what `deploy/.env` says |
| `--openwebui-host HOST` | – | the optional Open WebUI stack (below); the options below need it |
| `--openwebui-oidc-client-id ID` | `open-webui` | Open WebUI's own OIDC client (P1: the shared Keycloak's `open-webui`; P2: Entra's `ume-rag-open-webui`) |
| `--openwebui-oidc-client-secret-file FILE` | – | that client's secret: `OAUTH_CLIENT_SECRET` in `secrets/openwebui.env` |
| `--openwebui-oidc-provider-url URL` | the OIDC authority + `/.well-known/openid-configuration` | `UME_RAG_OPENWEBUI_OIDC_PROVIDER_URL` |
| `--openwebui-oidc-provider-name NAME` | `Umeå kommun` | the label of Open WebUI's sign-in button |
| `--openwebui-data-dir DIR` | `deploy/openwebui-data` | Open WebUI's own store |
| `--role all` | `all` | multi-host roles come with P2 |
| `--proxy` | – | a reverse proxy terminates public TLS: two distinct host names, listeners on `0.0.0.0` |
| `--upstream-host HOST` | the host name | the address the proxy uses to reach this host |
| `--proxy-address ADDR[,ADDR]` | – | the proxy's IPv4 source addresses or CIDRs: the host firewall accepts new connections to the published ports only from these (`UME_RAG_PROXY_SOURCES`). Without it, restrict the ports in a network firewall |
| `--api-cert`, `--api-key`, `--admin-cert`, `--admin-key FILE` | – | certificates from your PKI for the public listeners (all four) instead of the internal CA |
| `--certs-dir DIR`, `--secrets-dir DIR` | `deploy/certs`, `deploy/secrets` | where the files go |
| `--no-chown` | – | leave the files owned by you |
| `--renew-certs` | – | re-issue the internal certificates only |
| `--renew-ca` | – | a new internal CA, then re-issue its certificates |
| `--yes`, `-y` | – | no prompts: every value from an option or its default |
| `--rolling` | – | a second `rag-api` and `rag-admin` (`compose.rolling.yaml`, ports 8544 and 9544) for updates without downtime; see [Updates without downtime](zero-downtime-updates.md) |

`--help` prints the same list. Not built yet: roles other than `all`, and the `openbao` and `azure` key providers.

When it is done the script prints the next steps. If it did not run as root, step 0 lists the `chown` commands to run
(the containers run as uid 999, 1654, 10001 and 1000 and must read their own files; the renderer, uid 10002, has no
files). The order below is the
script's: the host units come **before** the first `up`, because the ingestion containers run below the slice they
install.

### 3. Sign-in (OIDC)

**The pilot signs in with the Keycloak shared with the gateway** (`plans/decisions.md` "P1 pilot sign-in";
identity design §11.2): the gateway repo runs it and its realm `ume`; RAG's realm additions (`deploy/keycloak/`) add the
clients `ume-rag-admin`, `open-webui` and `ume-rag-api`, the `rag-*` roles and the `departmentCodes` attribute. Point
`--oidc-authority` at `https://<keycloak host>/realms/ume`; Open WebUI's provider URL follows from it.

Give the admin's client the two redirect URIs the script prints (code + PKCE):
`https://<admin host>[:<admin port>]/signin-oidc` and `/signout-callback-oidc`, and Open WebUI's client
`https://<owui host>/oauth/oidc/login/callback`. The client secrets go to `--oidc-client-secret-file` and
`--openwebui-oidc-client-secret-file`, never into the repository.

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
never holds another service's key. A gateway admin issues the three keys in the gateway's admin UI:

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
   --gateway-credential-key-file worker=rag-worker.key`. It writes `secrets/api/Gateway__Key`,
   `secrets/admin/Gateway__Key` and `secrets/worker/Gateway__Key` (mode 0600, owned by the services' user like every
   secret file). Delete the handed-over files afterwards. On Kubernetes, `create-secrets.sh` then puts each into its
   service's secret ([Kubernetes](kubernetes.md)); on Portainer the keys are the variables `UME_RAG_API_GATEWAY_KEY`,
   `UME_RAG_ADMIN_GATEWAY_KEY` and `UME_RAG_WORKER_GATEWAY_KEY` (runbook, *Portainer and other hosts without shell
   access*).

Settings, scopes included, are changed by gateway admins only; the key's managers may rotate and revoke it.

**Rotate one key.** In the gateway's admin UI, rotate that service's key with the 24-hour grace period (the new key
keeps the integration, role, scopes, residencies and address list; the old one works until the grace period ends). Put
the new key in a file and re-run `sudo ./deploy/init-deployment.sh` with your options and
`--gateway-credential-key-file SERVICE=FILE` for that service only: it rewrites that key file and keeps the others and
every generated secret. Then restart the service, which reads its secret files at start:
`docker compose --env-file deploy/.env restart rag-api` (or `rag-admin`, `rag-worker`). Check its log for gateway
`401` answers, then let the grace period end or revoke the old key. Kubernetes: `create-secrets.sh` again, then
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
2. **The address.** Set `UME_RAG_GATEWAY_INTEGRATION_API_URL` in `deploy/.env` to the Admin API's base URL, without
   `/integration/v1`, for example `UME_RAG_GATEWAY_INTEGRATION_API_URL=https://llm-admin.intern.umea.se` (the gateway's
   Compose stack publishes the Admin API on 9443; behind its proxy, usually its own host name on 443). It is not a
   secret. rag-admin gets it as `Gateway__IntegrationApiUrl`; while it is unset or empty there is no read-back.
   `init-deployment.sh` has no option for it and writes the line commented out, so carry it over after a re-run.
3. **The firewall.** The host firewall adds the URL's host and port to the `llm` network's allow-list
   (`deploy/host/docker-user-firewall.sh`, *Host firewall* in the runbook). Outside the host, the network firewall
   must let the RAG host reach `<gateway admin host>:<port>` over HTTPS ([Network and ports](network-and-ports.md)).
   rag-admin must trust the Admin API's certificate: its CA in `--ca-file` if it is not the gateway's own.
4. **Apply:** `sudo systemctl start rag-firewall.service`, then `docker compose --env-file deploy/.env up -d --wait`
   (rag-admin is recreated with the setting).

**Check:** the *Gateway* page's status, `GET /api/gateway/status`, has `integration.configured: true` and no
`integration.lastError` after an assistant's or knowledge base's *Cost* card has been opened; a reason there is in the
runbook (*Gateway keys, budgets and the read-back*). Kubernetes: `Gateway__IntegrationApiUrl` in your overlay's
`ume-rag-admin-config` (commented in `deploy/kubernetes/example`), and the Admin API's port in your egress rules.
Portainer: the variable `UME_RAG_GATEWAY_INTEGRATION_API_URL`, for example `https://<UME_ADMIN_HOST>:9443` (the
gateway's `adminapi` is not on the `llm` network, so rag-admin reaches it through its published port).

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

## Malware scan

The malware scan is the deployment's choice (Decisions "Malware scan optional"): `init-deployment.sh --clamav on|off`,
`on` by default, sets the worker's `ClamAv:Mode` through `UME_RAG_CLAMAV_MODE` in `deploy/.env`.

- **`on`** (the default): `rag-clamav` runs (the Compose profile `clamav` in `COMPOSE_PROFILES`), and the worker scans
  every upload and every fetched file other than HTML, text and CSV before storing it. The scan fails closed: while
  clamd is down or still loading its signatures, those files wait with `clamav.unavailable` and are retried.
- **`off`**: no `rag-clamav` container runs and the worker contacts no clamd. Files are stored and parsed **without a
  malware scan**: nothing is quarantined. The worker says so at every start with a warning and security event 5106
  `rag.malware.scan_off` ([security events](security-events.md)); its `clamav` health check stays Healthy and says that
  the scan is off; upload slots are `unscanned` (never `clean`) with no scan time, a run's report has
  `malwareScan: "off"`, and the admin UI shows *Not scanned for malware* on those files and runs. Sizing: the
  `rag-clamav` row of *Sizing* (above) drops out, and the `mirror` network and `database.clamav.net` are not used.

Choose `off` only where the files come from sources you trust, or where they are scanned before they reach RAG. **Files
stored while the scan is off are not scanned when you turn it on later**: they stay as they are and keep their
*Not scanned for malware* mark; only new and changed files are scanned from then on.

To change it, re-run `init-deployment.sh` with your options plus `--clamav off` or `--clamav on` (a re-run without the
option keeps the current mode), then `docker compose --env-file deploy/.env up -d --wait`, which recreates the worker
with the new mode. When turning it off, also `docker compose --env-file deploy/.env rm -sf rag-clamav` (`up` leaves a
container of a profile that is off alone). When turning it on, freshclam downloads the signatures first (several
minutes); files wait with `clamav.unavailable` until clamd is ready. On Kubernetes the same choice is the
`components/clamav` component ([Kubernetes](kubernetes.md), *Malware scan*); in the Portainer stack, `clamav` in
`COMPOSE_PROFILES` and `UME_RAG_CLAMAV_MODE`.

## ClamAV signatures

With the malware scan on, freshclam updates the signatures straight from `database.clamav.net` (no internal mirror), four times a day, through
the `mirror` network, which reaches nothing else. That host name is served by a CDN: the firewall resolves it when it
runs (at Docker start and every 15 minutes), so an address change can block updates until the next run. **Where IT
provides an egress proxy, use it**: `--freshclam-proxy HOST:PORT` writes `HTTPProxyServer`/`HTTPProxyPort` into a copy
of `freshclam.conf`, and the firewall then lets the `mirror` network reach only the proxy.

The ClamAV image's own confs are used unless a setting differs. `--host-memory 24` or `--freshclam-proxy` makes
`init-deployment.sh` copy them to `secrets/clamav/`, change them, and add `compose.clamav-conf.yaml` (which mounts the
copies) to `COMPOSE_FILE`. In a clone the source is `images/clamav/`; in a release bundle, `deploy/clamav/`.

## Renderer

`rag-renderer` renders the pages of sources with fetch mode `browser`: Chromium behind Playwright's `launch-server`
(`images/renderer/`, its README holds the contract), driven by the worker over WebSocket on port 3000. It sits only on
the internal `render` network, so the page scripts it runs reach nothing but the worker, which fetches every request
itself with the crawler's guard (connectors design §8.3). The worker finds it through `Render__Endpoint`
(`ws://rag-renderer:3000/<secret path>`, the secret file `secrets/worker/Render__Endpoint`); without that file `browser`
sources wait with `render.not_configured`. Rotate the path by deleting `secrets/.render-ws-path`, re-running
`init-deployment.sh` and restarting `rag-renderer` and `rag-worker`.

It is **off unless you ask for it**: the service has the Compose profile `render`, and `init-deployment.sh --render`
puts `COMPOSE_PROFILES=render` in `deploy/.env` and writes the worker's endpoint file; a run without `--render` removes
both. Keep the two together: the profile without the endpoint leaves the browser idle, the endpoint without the
profile makes every render fail as `render.unavailable` instead of waiting with `render.not_configured`. To turn it on
or off later, re-run `init-deployment.sh` with your options, with or without `--render`, then `docker compose --env-file
deploy/.env up -d --wait` and `docker compose --env-file deploy/.env restart rag-worker`: the worker reads its secret
files only at start. When turning it off, `docker compose --env-file deploy/.env rm -sf rag-renderer` stops the
renderer (`up` leaves a container of a profile that is off alone).

It runs hardened like the others, plus a seccomp profile of its own (`images/renderer/seccomp.json`: Docker's default
with `clone`, `unshare` and `chroot` allowed), so Chromium's own sandbox can create its user namespaces. Without them
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

Every connection inside the host is TLS with the internal CA: the services to postgres (`SSL Mode=VerifyFull`), the
worker to the parser (`https://rag-parser:8000`, certificate `certs/parser/` with the name `rag-parser`, set through
`PARSER_TLS_CERT_FILE` and `PARSER_TLS_KEY_FILE`), and the health probes. Only clamd (TCP 3310) has no TLS, so it stays
on the internal `ingestion` network, and the renderer's WebSocket (port 3000) is plain, with a secret path, on the
internal `render` network. Internal certificates last 825 days: re-issue them with
`./deploy/init-deployment.sh --renew-certs`, which needs `ca.key` back on the host.

## Kubernetes

Instead of Compose, `deploy/kubernetes` has Kustomize manifests for the same services, settings, certificates and
secrets, with rolling updates built in: see [Kubernetes](kubernetes.md). Sign-in through the gateway's Keycloak, a
Keycloak of RAG's own, or Entra ID directly: see [Keycloak](keycloak.md).

## Portainer (gateway and RAG in one stack)

No shell access on the host? `deploy/portainer/compose.portainer.yaml` is the gateway's own Portainer stack with Ume
Rag added, configured only through environment variables: paste it over the gateway's stack, keep the stack name, and
set the RAG variables listed at the top of the file. See the runbook, *Portainer and other hosts without shell access*,
and [portainer.md](portainer.md) for a first deploy and the upgrade to 0.5.0 step by step.

**On a new VM** (Docker and Portainer installed, nothing else):

1. **Take the release's file:** the `compose.portainer.yaml` asset of the GitHub release (or the repository's copy at
   the release tag). It pins the gateway's images (the gateway release it was built from, `v0.10.1` for Ume.Rag
   `0.5.0`) and Ume.Rag's by digest, so no image variable is needed.
2. **Registry:** Ume.Rag's images on `ghcr.io/thekim1/ume-rag/*` are private: add `ghcr.io` as a registry in Portainer
   with a GitHub user and a token holding `read:packages`, and pick it when you create the stack. The gateway's images
   are public.
3. **Variables:** the gateway's required ones (`UME_OIDC_AUTHORITY`, `UME_GATEWAY_HOST`, `UME_GATEWAY_PUBLIC_URL`) and
   Ume.Rag's (`UME_RAG_API_HOST`, the three `UME_RAG_*_GATEWAY_KEY` and
   `UME_RAG_OIDC_CLIENT_SECRET`), and `COMPOSE_PROFILES=clamav` (add `openwebui` for the chat,
   `render` for JavaScript pages). The credential keys come from the gateway once it runs: start the stack, issue the
   keys in the gateway's admin (*Gateway credential keys* above), set them, and update the stack.
4. **TLS:** the gateway (8443/9443) and Ume.Rag (8543/9543) serve certificates from their own internal CAs; Open WebUI
   (3000) serves plain HTTP on loopback. Put a reverse proxy with real certificates in front of the public names
   (*Reverse proxy and PKI* below for the routes and body sizes), and set `UME_RAG_OPENWEBUI_BIND` and the other `*_BIND`
   variables to match. Or add `proxy` to `COMPOSE_PROFILES`: the stack's own nginx with your certificate in front of
   all five host names ([portainer.md](portainer.md#tls-proxy)).
5. **Open WebUI** (profile `openwebui`): set `UME_RAG_OPENWEBUI_HOST` and its OIDC client
   (`UME_RAG_OPENWEBUI_OIDC_CLIENT_ID`, `UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET`; redirect URI
   `https://<host>/oauth/oidc/login/callback`). Open WebUI's key for rag-api needs nothing: `rag-bootstrap` generates it
   and `rag-migrate` registers it as the deployment's own Open WebUI client (*Apps and keys*, marked *Managed by the
   deployment*; identity design §5.2). The feedback import's key is minted by the one-shot `rag-openwebui-setup` (as
   *The feedback service account* below says for Compose); `rag-bootstrap` points
   rag-api at the stack's own Open WebUI while the `openwebui` profile is on. As with Compose, Open WebUI keeps its own copy of each chat
   (W2, below).
6. **The embedding profile** is not a variable: once the keys are set, a platform admin creates it in Ume.Rag's admin
   (*Models → Embedding profiles → Create profile*, [portainer.md](portainer.md) step 6). Until then no knowledge base
   can be created.

## Reverse proxy and PKI

The listeners default to loopback (`127.0.0.1:8543` and `:9543`). Behind the municipal reverse proxy, run
`init-deployment.sh --proxy` with two distinct host names (and `--upstream-host` if the proxy reaches this host by
another name): the listeners bind to `0.0.0.0`, so restrict those ports to the proxy (`--proxy-address`). It writes
`deploy/proxy/nginx.conf` from `deploy/proxy/nginx.conf.example` (as the gateway's `init-deployment.sh` does, #468): rag-api,
the admin host and, with `--openwebui-host`, Open WebUI, ready for nginx once your certificates are added. For another
proxy the script prints the routes:
each host name forwarded over HTTPS to its port, preserving the `Host` header; `client_max_body_size 60m` and
`proxy_request_buffering off` on `PUT /api/upload-batches/{batchId}/files/{n}` of the admin host, 2 MB on the api host
(and on Open WebUI's), because `/v1/chat/completions` takes up to 2 MB of conversation, and 1 MB elsewhere.
Add the proxy's addresses to the trusted proxies (admin UI, Settings, *Network*) and have it set `X-Forwarded-For`:
rag-api and the admin then see the client's address (key IP allow-lists, the refused-credential throttle, security
events); without the entry every request comes from the proxy, and a key allow-list holding a client host refuses it.
With certificates from your PKI, pass `--api-cert`, `--api-key`, `--admin-cert` and `--admin-key` (and `--ca-file`
for the chain).

## Telemetry

The services export OpenTelemetry (logs, metrics, traces) only when an endpoint is set: add
`UME_RAG_OTEL_ENDPOINT=<collector>` to `deploy/.env` (the firewall then allows it from the `llm` and `crawl` networks)
and run `up -d` again. Health-check requests are not traced, and query strings are removed from HTTP spans. Traces
are sampled: a tenth of the requests that start at a service (`UME_RAG_OTEL_TRACES_SAMPLER`, default
`parentbased_traceidratio`, and `UME_RAG_OTEL_TRACES_SAMPLER_ARG`, default `0.1`; `always_on` records every request),
and a request that arrives with a sampled parent is always recorded. Logs and metrics are not sampled. The
backup scripts write Prometheus text files to `/var/lib/ume-rag/` (runbook, *Backups*).

Security events (logger category `Ume.Rag.Security`, [security-events.md](security-events.md)) go to the SIEM through a
collector: [`deploy/otel/collector-siem.example.yaml`](../deploy/otel/collector-siem.example.yaml) keeps only that
category and sends it as syslog (RFC 5424), with Splunk, Elastic and Sentinel alternatives, as the gateway's does. One
collector can serve both products.

## Open WebUI (optional)

`deploy/compose.openwebui.yaml` is a separate Compose project (`ume-rag-openwebui`), never in `COMPOSE_FILE` and off by
default. It is started only once the DPIA (register G1) has decided register item **W2**, how Open WebUI's own copies of
the chats are pruned; until then they stay unpruned. It needs the 24 GB host, or a VM of its own.

```bash
./deploy/init-deployment.sh ... --openwebui-host chat.intern.umea.se \
  --openwebui-oidc-client-secret-file open-webui.secret
docker compose -f deploy/compose.openwebui.yaml --env-file deploy/.env up -d --wait
sudo systemctl start rag-firewall.service   # allow the openwebui network its destinations now
```

Open WebUI's key for rag-api is generated by `init-deployment.sh` (`OPENAI_API_KEYS` in `secrets/openwebui.env`), and
`rag-migrate` registers it as the deployment's own Open WebUI client when the main stack starts (identity design §5.2):
nothing to create in the admin. A new key: delete `secrets/.openwebui-rag-api-key`, re-run `init-deployment.sh` with the
same options and recreate `rag-migrate`, `rag-api` and Open WebUI; the old key is revoked. The run book for a local P1a
run is [p1a-local-run.md](p1a-local-run.md).

**The feedback service account** (chat design §14.2). rag-api's feedback importer reads Open WebUI's feedback list
and deletes feedback past retention with the API key of an Open WebUI admin account that is never a person.
`init-deployment.sh --openwebui-host` writes its credentials once into `secrets/openwebui.env` (`WEBUI_ADMIN_EMAIL`
`feedback-import@localhost`, a generated `WEBUI_ADMIN_PASSWORD`), and Open WebUI creates it as its first user, so as
its only admin, when it starts on an empty store. Everyone signing in through OIDC gets the role `user`
(`DEFAULT_USER_ROLE`), so no person becomes admin. Password sign-in is always off (`ENABLE_PASSWORD_AUTH`), and nobody
signs in as the account: `compose.openwebui.yaml`'s one-shot `open-webui-setup` mints its key when Open WebUI starts
(M-17 C6, `deploy/openwebui/feedback-key-setup.py`). It runs Open WebUI's own image as Open WebUI's user, reads the
account's id from Open WebUI's store, signs a 5-minute session token with Open WebUI's `WEBUI_SECRET_KEY`, mints the key
with it, checks that the key reads the feedback list (only `API_KEYS_ALLOWED_ENDPOINTS` is open to API keys) and writes
it to `secrets/openwebui-feedback/OpenWebUi__AdminApiKey`, which rag-api reads without a restart
(`Security__ExtraSecretsDirectory`). A key that still works is kept on the next `up`; a new key: delete that file and
run `docker compose -f deploy/compose.openwebui.yaml --env-file deploy/.env up -d` again. If the store had users before
the account was created (Open WebUI started before `init-deployment.sh` wrote it), the job stops because the account
is not admin: create it as an existing admin with `POST /api/v1/auths/add` (name, the email and password from
`secrets/openwebui.env`, role `admin`), then run `up` again. The importer's own
settings in `compose.prod.yaml` are added with the importer (P1b item 17, after stream B's L6).

It has one connection only, to `rag-api` (`OPENAI_API_KEYS` is the `ume-rag-sk-` key of its client, issued in the admin
UI; its JWT secret is generated once and shared with rag-api's `OpenWebUi__JwtSecret`), signs in only through its own
OIDC client (`open-webui` on the shared Keycloak in the pilot; Entra's `ume-rag-open-webui` from P2 with
`--openwebui-oidc-client-id` and `--openwebui-oidc-provider-url`), and listens on plain HTTP on port 3000 of the listeners' bind
address (loopback unless `--proxy`), so the reverse proxy terminates TLS. Its settings come from the Compose file on
every start; an admin's edit in Open WebUI lasts until the next restart. Its fixed input warning (`WEBUI_BANNERS`) still
shows a close button: a closed banner returns on the next reload.
