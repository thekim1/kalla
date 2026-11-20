# Quick start: the gateway and Ume.Rag on one host with Docker Compose

The shortest path from an empty Linux host to a first knowledge base: two downloads, two setup scripts, two setup
wizards. It covers the common case, the LLM gateway and Ume.Rag as two Compose projects on the same machine. Everything
it leaves out (sizing, a reverse proxy, your own PKI, Open WebUI, the renderer, backups to a share or S3) is in
[production-setup.md](production-setup.md); a host without shell access uses [portainer.md](portainer.md) instead, and
ports and firewall rules per setup are in [network-and-ports.md](network-and-ports.md).

What you type, in all: the host names, the gateway's address, the sign-in provider's address (once, in the gateway's
setup wizard; Ume.Rag's wizard suggests it) and the two client secrets. Every certificate, password and
key is generated, and the gateway hands Ume.Rag its three credential keys through a directory on the host.

## 1. Before you start

**The host:** Linux with systemd, Docker 28 or later, Compose 2.33.1 or later (the Compose files use
`gw_priority`), `openssl`, root. For both products on one
host count 8 cores and 20 GB of memory or more: Ume.Rag's smaller profile caps its containers at about 12 GB
([sizing](production-setup.md#sizing)), and the gateway takes about 2 cores and 4 GB.

```bash
docker version --format '{{.Server.Version}}'                  # 28 or later
docker compose version --short                                 # 2.33.1 or later
ss -ltn | grep -E ':(8443|9443|8543|9543)\b' || echo "the four ports are free"
```

- **Ports:** the gateway listens on 8443 (API) and 9443 (admin), Ume.Rag on 8543 (kalla-api) and 9543 (kalla-admin).
  Another port: `--gateway-port` and `--admin-port` (the gateway's script), `--set KALLA_API_PORT=N` and
  `--set KALLA_ADMIN_PORT=N` (Ume.Rag's).

**The sign-in provider** (Keycloak, Entra ID or AD FS) with two clients, one per admin console: `ume-admin` for the
gateway and `kalla-admin` for Ume.Rag (a confidential client, code + PKCE). Each wizard shows the redirect URIs to
register on its client. A person who tests a sign-in must come out as `gateway-admin` in the gateway and as
`rag-platform-admin` in Ume.Rag. For Keycloak, the gateway's `docs/keycloak.md` and Ume.Rag's
[`deploy/keycloak/README.md`](../deploy/keycloak/README.md) hold the clients and roles as importable files.

**The addresses.** The gateway's script publishes its listeners on `127.0.0.1` unless told otherwise; Ume.Rag
publishes kalla-api and kalla-admin on `0.0.0.0`. Neither a browser on another machine nor Ume.Rag's containers reach the
host's loopback. Choose one:

- **A reverse proxy in front** (production): `--proxy` on the gateway's script, each of the four names its own host
  name, and for Ume.Rag the proxy's routes and its address (`--proxy-address`, which the host firewall then alone
  admits to the published ports; [Reverse proxy and PKI](production-setup.md#reverse-proxy-and-pki)). The gateway's
  address is then `https://llm.<domain>`. One wildcard DNS record and one wildcard certificate for `*.<domain>`
  cover the four names.
- **No proxy** (a test host): `--bind 0.0.0.0` on the gateway's script. One host name serves everything: the
  addresses carry the ports (`https://<host>:8443`), the certificates come from each product's internal CA (the
  browser warns), and Ume.Rag trusts the gateway's CA through `--gateway-ca-file`. Restrict the four ports in a
  network firewall.

The commands below are the second case. With a proxy, replace the gateway's `--bind 0.0.0.0` with `--proxy`, use the
host names, and add `--proxy-address` to Ume.Rag's script.

## 2. The gateway

```bash
curl -fsSLO https://github.com/thekim1/fors/releases/download/vX.Y.Z/fors-X.Y.Z.tar.gz
tar -xzf fors-X.Y.Z.tar.gz && cd fors-X.Y.Z
sudo ./deploy/init-deployment.sh --gateway-host <host> --admin-host <host> --bind 0.0.0.0 --yes
sudo docker compose -f deploy/compose.prod.yaml --env-file deploy/.env up -d --wait
```

`<host>` is the host's DNS name, not an IP address: the certificates are issued for the name. With `--yes` the script
asks nothing and leaves sign-in to the setup wizard. Without it, it asks for the provider's address (an address typed
there declares the sign-in, and the wizard below never opens) and whether a reverse proxy is in front.

**The gateway's setup wizard:**

1. Read the setup code (valid for an hour; once the hour has passed, a restart of `adminapi` opens a new hour with a
   new code while setup was never finished):
   `sudo docker compose -f deploy/compose.prod.yaml --env-file deploy/.env logs adminapi | grep 'Setup code'`
2. Open `https://<host>:9443/setup` and enter the code.
3. Register the two redirect URIs the wizard shows on the client `ume-admin`, type the provider's address and the
   client, press **Test sign-in** and sign in as yourself. **Save and close setup** is offered once a gateway admin's
   test passed.

Then sign in to the gateway's admin and **add a provider and its models**: at least one embedding model and one chat
model, hosted on-premises or in the EU (the gateway's `docs/providers-and-models.md`). Ume.Rag's keys allow only those
two residencies.

## 3. Ume.Rag's keys, from the gateway

Ume.Rag calls the gateway with three credential keys, one each for kalla-api, kalla-admin and kalla-worker. The gateway
creates them from a manifest that ships with Ume.Rag, and writes their secrets to a directory that Ume.Rag's bootstrap
reads. Download and unpack Ume.Rag's release first, next to the gateway's directory. Its images are private: log in as root,
which is who pulls them (`sudo docker login ghcr.io`, a GitHub user and a token holding `read:packages`).

```bash
cd ..
curl -fsSLO https://github.com/thekim1/kalla/releases/download/vX.Y.Z/kalla-X.Y.Z.tar.gz
curl -fsSLO https://github.com/thekim1/kalla/releases/download/vX.Y.Z/SHA256SUMS
sha256sum -c --ignore-missing SHA256SUMS
tar -xzf kalla-X.Y.Z.tar.gz
sudo install -m 644 kalla-X.Y.Z/deploy/portainer/integration-manifest.json \
  fors-X.Y.Z/deploy/integration-manifests/kalla.json
(cd fors-X.Y.Z && sudo docker compose -f deploy/compose.prod.yaml --env-file deploy/.env up -d --wait)
sudo ls fors-X.Y.Z/deploy/integration-secrets/Kalla     # kalla-admin  kalla-api  kalla-worker
```

The second `up` runs the gateway's migration service again, which applies the manifest: the integration *Ume.Rag* in a
team and department of its own (`kalla`), and the three keys with the settings Ume.Rag needs (residencies `OnPrem`
and `Eu`, the ceiling header required, PII policy `Off`, kalla-admin's scopes). What exists is never changed, so the
manifest can stay where it is.

**A gateway on another host, or keys issued by hand:** in the gateway's admin, *Access → Integrations → Add from
manifest*, pick the preset **Ume.Rag** and create it. The three secrets are shown once: put each in a file of its own
and pass them with `--gateway-credential-key-file` ([Gateway credential keys](production-setup.md#gateway-credential-keys)).

## 4. Ume.Rag

```bash
cd kalla-X.Y.Z
sudo ./deploy/init-deployment.sh --api-host <host> --admin-host <host> \
  --gateway-url https://<host>:8443 --gateway-ca-file ../fors-X.Y.Z/deploy/certs/ca.pem \
  --gateway-secrets-dir ../fors-X.Y.Z/deploy/integration-secrets \
  --set KALLA_API_PUBLIC_URL=https://<host>:8543 \
  --backup-target local
sudo docker compose --env-file deploy/.env up -d --wait
sudo docker compose --env-file deploy/.env logs kalla-bootstrap
sudo docker compose --env-file deploy/.env exec -T kalla-postgres pgbackrest --stanza=ragdb stanza-create
sudo deploy/host/install.sh
sudo systemctl start rag-backup.service
```

- **The script writes only `deploy/.env`** (mode 600). The certificates and secrets are created at the first `up` by
  the one-shot `kalla-bootstrap`, in Docker volumes; the end of its log lists what is still missing (a credential key,
  for example) and the setup wizard's address.
- **`--gateway-ca-file` is the gateway's CA certificate**, which the script copies to `deploy/gateway-ca` (only the
  certificate: the gateway's directory also holds its private keys): Ume.Rag's services then
  trust the gateway's internal CA.
- **No sign-in value is asked for:** sign-in is set in the setup wizard below, which suggests the provider saved in
  the gateway's wizard.
- **`KALLA_API_PUBLIC_URL`** is kalla-api's address with its port, for the citation links; behind a proxy it is
  `https://<api host>`, the default, and the line goes.
- **`--gateway-url` is an address the containers reach:** the host's name, never `localhost`.
- **`--backup-target local`** keeps the database backups in a Docker volume on the same disk, which is no backup of
  the host: fine for a test, and for production name the share or the bucket
  ([Backup target](production-setup.md#backup-target)).
- **`deploy/host/install.sh`** installs the backup and certificate timers. `up --wait` returns when every service is
  healthy; the first start of `kalla-clamav` downloads its signatures and can take ten minutes.
- **kalla-api and kalla-admin are published on `0.0.0.0`**, ports 8543 and 9543. Restrict them in a network firewall, or
  harden the host (step 7), whose firewall admits only your proxy.

The script ends with the next steps, the setup wizard's address among them.

**Ume.Rag's setup wizard:**

1. Read the setup code: `sudo docker compose --env-file deploy/.env logs kalla-admin | grep 'Setup code'`
2. Open `https://<host>:9543/setup`, at the address users will use, and enter the code.
3. Register the two redirect URIs the wizard shows on the client `kalla-admin`, type the client secret, press
   **Test sign-in** and sign in as yourself. **Finish** is offered once a platform admin's test passed.

## 5. First content

1. **Sign in** to Ume.Rag's admin at `https://<host>:9543`. *Models* shows the gateway's version, a recent
   organisation sync and the gateway's models. If it shows none, see *When something fails* below.
2. **Create the embedding profile** (*Home* asks for it until one exists): *Models → Embedding profiles → Create
   profile*, pick the gateway's embedding model. For a model that shortens its vectors, such as
   `text-embedding-3-large`, tick *Request dimensions* and give the size (1024 in the pilot).
3. **In the gateway's admin**, move the integration to your own team (*Access → Integrations → Ume.Rag*, *Edit*), and
   create the **assigned keys** that knowledge bases and assistants run on: one or more per department, in the
   integration, with role *Assigned*.
4. **Follow *Get started*** on Ume.Rag's *Home*: create a knowledge base, add a source, run the first fetch, write test
   questions, evaluate, publish, create an assistant and publish it.

## 6. Before real use

- **Escrow the keys:** `sudo deploy/escrow-keys.sh /media/<escrow>` writes one set with the pepper, the blob key, the
  backup passphrase, the database passwords, the conversation id key, the Data Protection certificate and the internal
  CA, and `--verify` reads it back (the runbook's
  *Key escrow*). A database restored without them is unusable. The gateway
  has two of its own, in its directory (`deploy/secrets/.pepper` and `deploy/certs/data-protection.pfx` of the
  gateway's release).
- **A real backup target, a reverse proxy with certificates from your PKI, and the proxy's addresses** under
  *Settings → Network* in Ume.Rag's admin: production-setup.md.
- **The cost and budget read-back is off** after this guide: *Models* shows *Cost and budgets from the AI gateway:
  Off*, since the gateway names no Integration API address (its `FORS_INTEGRATION_API_URL`). The gateway's
  `deploy/.env` needs it and kalla-admin a route to the gateway's Admin API:
  [production-setup.md](production-setup.md#cost-and-budget-read-back).
- **Open WebUI** for staff chat: [production-setup.md](production-setup.md#open-webui-optional).
- **Upgrades:** the [upgrade guide](upgrade-guide.md); day-to-day operation: the [runbook](runbook.md).

## 7. Harden the host (production)

A first install runs as the Portainer stack does: Docker's own network isolation, the services' own egress guard, no
host firewall. A production host adds the hardening once the installation works. It gives each of Ume.Rag's networks
a fixed subnet and an egress rule of its own (the crawler reaches the Internet, the other services only what they
call), caps the ingestion containers as a group, and gives the renderer its seccomp profile.

```bash
docker info --format '{{.CgroupDriver}} {{.CgroupVersion}}'    # systemd 2
docker network ls -q | xargs docker network inspect --format '{{.Name}} {{range .IPAM.Config}}{{.Subnet}} {{end}}'
sudo ./deploy/init-deployment.sh --hardened --oidc-authority https://<provider>/realms/ume [--proxy-address <proxy>]
sudo docker compose --env-file deploy/.env down
sudo deploy/host/install.sh
sudo docker compose --env-file deploy/.env up -d --wait
```

- **The cgroup driver must be `systemd` on cgroup v2**, and **no Docker network may use `172.30.0.0/16`**: with
  `--hardened` Ume.Rag's networks have fixed subnets there, so its host firewall can give each its own rule,
  and its ingestion containers run in a slice. If either check differs, set both in `/etc/docker/daemon.json` as
  [production-setup.md](production-setup.md#host-prerequisites) shows: a restart of
  Docker stops every container on the host, the gateway's too, and a network that already holds `172.30.x.x` has to be recreated.
- **`--oidc-authority` is the provider's address** (Keycloak: `https://<host>/realms/<realm>`; Entra ID:
  `https://login.microsoftonline.com/<tenant>/v2.0`): the firewall lets kalla-admin reach only this provider, which a
  later re-open of the setup wizard needs. A run without `--yes` asks for it when the option is missing.
- **`down` before `up`:** the networks get their fixed subnets only when they are created again. The volumes, and so
  the database and every secret, stay. `install.sh` installs the slice and the firewall before the services start.
- **`--proxy-address`** names your reverse proxy's addresses, which the firewall then alone admits to ports 8543 and
  9543. The firewall lets Ume.Rag's networks through to a gateway that this host serves.

## When something fails

| Symptom | Likely cause | What to do |
|---|---|---|
| `up` fails with "Pool overlaps with other one on this address space" | on a hardened host, a Docker network already uses `172.30.x.x` | step 7; recreate that network after setting `default-address-pools` |
| `up --wait` times out on `kalla-worker`, `kalla-parser` or `kalla-clamav` | on a hardened host, the cgroup driver is not `systemd`, or the slice is not installed | `systemctl status kalla-ingestion.slice`, `docker info`; `sudo deploy/host/install.sh` |
| `kalla-clamav` unhealthy for more than 15 minutes | no signature download | `docker compose --env-file deploy/.env logs kalla-clamav`; `--freshclam-proxy` behind an egress proxy |
| The end of `kalla-bootstrap`'s log: *KALLA_API_GATEWAY_KEY: no key for kalla-api* (or `kalla-admin`, `kalla-worker`) | the gateway has not applied the manifest | step 3: the manifest in `deploy/integration-manifests`, mode 644, then the gateway's `up` again (its `migrations` log names a manifest it refused), then Ume.Rag's `up -d` |
| The wizard: *kalla-admin could not reach the provider address* | on a hardened host, the firewall does not know the provider | re-run `init-deployment.sh --oidc-authority <provider>`, then `sudo docker compose --env-file deploy/.env up -d` and `sudo systemctl start rag-firewall.service` |
| *Models* is empty, or the organisation sync is *degraded* | Ume.Rag's containers do not reach the gateway, or do not trust its certificate | the check below; `--gateway-ca-file` with the gateway's `deploy/certs/ca.pem`; `docker compose --env-file deploy/.env logs kalla-admin` |
| 403 `model_not_allowed` or `max_residency_not_met` in a log | the model is not hosted at `OnPrem` or `Eu`, or a key lists other models | the provider's residency and the three keys, in the gateway's admin |

**Do Ume.Rag's containers reach the gateway?** A throwaway container on the `llm` network takes the same way out as
kalla-api and kalla-admin (and, on a hardened host, gets the same firewall rules):

```bash
sudo docker run --rm --network kalla_llm curlimages/curl:8.11.1 -k -sS -o /dev/null -w 'HTTP %{http_code}\n' \
  https://<host>:8443/v1/models
```

`HTTP 401` is the right answer (no key was sent). A time-out on a hardened host means the firewall blocks it: compare
`sudo iptables -S UME-RAG-INPUT` with the address the gateway's name resolves to, and run
`sudo systemctl start rag-firewall.service` again. When the gateway's name resolves to this host, the firewall's
`UME-RAG-INPUT` and `--ctorigdst` rules are what let Ume.Rag's networks through
([network-and-ports.md](network-and-ports.md)).
