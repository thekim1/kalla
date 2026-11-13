# Deploying and upgrading with Portainer (0.5.0)

How to run Ume.Rag `0.5.0` with the LLM gateway `v0.10.1` as one Portainer stack: a first deploy on a new VM, and the
upgrade of an existing stack from Ume.Rag `0.2.0`–`0.4.0` (gateway `v0.9.x`). The stack is configured only through
environment variables, for hosts where `init-deployment.sh` can't run. Background and the full variable list: the
header of [`deploy/portainer/compose.portainer.yaml`](../deploy/portainer/compose.portainer.yaml), the runbook's
[*Portainer and other hosts without shell access*](runbook.md#portainer-and-other-hosts-without-shell-access), and
[production-setup.md](production-setup.md#portainer-gateway-and-rag-in-one-stack).

> **Open WebUI in this stack is new in `0.5.0`'s stack file (#657) and was merged without a boot test.** If
> `rag-openwebui` doesn't come up, check its log first for the start-up wrapper (the two secrets it reads from the
> `rag-secrets` volume), the CA bundle (`openwebui/ca.pem`) and the owner of its data volume, and report it.

## Which file

Use **`deploy/portainer/compose.portainer.yaml` from `main`** (or from the next release's assets). It pins the gateway's
images (`v0.10.1`) and Ume.Rag's (`0.5.0`) by digest, so no image variable is needed, and it has Open WebUI as the
profile `openwebui`. The `compose.portainer.yaml` asset of the `v0.5.0` release predates that: it has no Open WebUI and
leaves `GATEWAY_IMAGE`, `ADMINAPI_IMAGE` and `MIGRATIONS_IMAGE` to be set by hand.

## What you need

- A VM with Docker (Compose v2) and Portainer. Sizing: 4 vCPU, 16 GB RAM and 100 GB SSD for a pilot (floor 4 vCPU,
  8 GB, 50 GB; R1 sets the final numbers); ClamAV alone takes about 1 GB of RAM.
- **Registry access:** Ume.Rag's images on `ghcr.io/thekim1/ume-rag/*` are private. In Portainer, *Registries → Add
  registry → Custom*: `ghcr.io`, a GitHub user and a token with `read:packages`. The gateway's images are public.
- An **OIDC provider** (the shared Keycloak realm, AD FS or Entra ID) with clients for the gateway's admin
  (`ume-admin`), Ume.Rag's admin (`ume-rag-admin`) and, for the chat, Open WebUI (`open-webui`).
- **Host names** and a **reverse proxy with real certificates** in front of them: the gateway API, the gateway admin,
  rag-api, Ume.Rag's admin and Open WebUI. The services serve certificates from their own internal CAs (Open WebUI plain
  HTTP), so the proxy terminates TLS; routes and body sizes: [production-setup.md](production-setup.md#reverse-proxy-and-pki).
- The **embedding and chat models** on the gateway, EU-hosted for now.

## First deploy

1. **Create the stack.** *Stacks → Add stack*, a name you will keep (the volumes are found by it), *Web editor*: paste
   the file. Pick the `ghcr.io` registry.
2. **Variables** (*Environment variables*, advanced mode accepts `NAME=value` lines):

   ```text
   # The gateway
   UME_OIDC_AUTHORITY=https://keycloak.example.se/realms/ume
   UME_GATEWAY_HOST=llm.example.se
   UME_GATEWAY_PUBLIC_URL=https://llm.example.se
   UME_ADMIN_PUBLIC_URL=https://llm-admin.example.se      # optional: links from Ume.Rag's admin to the gateway's
   UME_OIDC_CLIENT_SECRET=...                             # if ume-admin is a confidential client
   # Ume.Rag
   UME_RAG_API_HOST=rag.example.se
   UME_RAG_API_PUBLIC_URL=https://rag.example.se
   UME_RAG_ADMIN_HOST=rag-admin.example.se
   UME_RAG_EMBEDDING_MODEL=<the gateway's embedding model id>
   UME_RAG_OIDC_CLIENT_SECRET=...
   UME_RAG_OIDC_GROUPS_PLATFORM_ADMIN=<group that grants rag-platform-admin>
   COMPOSE_PROFILES=clamav,openwebui
   # Open WebUI
   UME_RAG_OPENWEBUI_HOST=chat.example.se
   UME_RAG_OPENWEBUI_OIDC_CLIENT_ID=open-webui
   UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET=...
   ```

   Leave the gateway keys of Ume.Rag empty for now. The other optional variables (ports, binds, groups, the gateway's
   settings) are in the file's header.
3. **Deploy the stack.** The one-shot `bootstrap` (the gateway's) and `rag-bootstrap` create the internal CAs,
   certificates and secrets in the volumes; both databases are created and migrated; the services start. Ume.Rag's
   services log that they have no gateway credential key: expected until step 5.
4. **In the gateway's admin**, as a gateway admin ([production-setup.md](production-setup.md#gateway-credential-keys)
   has every field):
   - *Access → Integrations*: create the integration `Ume.Rag` with its owner team.
   - Three **credential keys** in it, one per service: `Ume.Rag api`, `Ume.Rag admin` (scopes `organisation.read` and
     `usage.read`) and `Ume.Rag worker` (residencies `OnPrem` and `Eu` only). `RequireMaxResidency` on, PII policy
     `Off` or `Allow`. **Address list:** the services reach the gateway over the stack's internal network `llm`, whose
     subnet Docker chose (Portainer: *Networks → <stack>_llm*, or `docker network inspect <stack>_llm`); allow that
     subnet, or leave the list empty.
   - The **assigned keys** the assistants and knowledge bases will run on, one or more per department, in the
     integration with role *Assigned*.
5. **Back in the stack**, set the keys and update it:

   ```text
   UME_RAG_API_GATEWAY_KEY=...
   UME_RAG_ADMIN_GATEWAY_KEY=...
   UME_RAG_WORKER_GATEWAY_KEY=...
   UME_RAG_GATEWAY_INTEGRATION_API_URL=https://llm-admin.example.se:9443   # optional: the gateway's cost and budgets
   ```

   *Update the stack*, then restart `rag-api`, `rag-admin` and `rag-worker` (*Containers*, select, *Restart*): they read
   their keys at start. The read-back URL is the gateway admin's address as `rag-admin` reaches it: `adminapi` isn't on
   the `llm` network, so use its published address (`https://<UME_ADMIN_HOST>:<UME_ADMIN_PORT, 9443>`); without it
   there is no read-back.
6. **Check** in Ume.Rag's admin (`https://rag-admin.example.se`): *Platform → Gateway models* shows the gateway's
   version and features with `act-as-key`, a recent organisation sync without a *degraded* reason, and the models; the
   embedding canary passes under *Embedding profiles* on the same page.
7. **Open WebUI:** in Ume.Rag's admin, *Platform → Clients*, a new client of kind `open-webui`. Set its key and id, update the stack, and
   restart `rag-api` and `rag-openwebui`:

   ```text
   UME_RAG_OPENWEBUI_API_KEY=<the client's key>
   UME_RAG_OPENWEBUI_CLIENT_ID=<the client's id>
   ```

   Users sign in through OIDC and get the role `user`. The **feedback import** (thumbs and comments into Ume.Rag) is
   optional: the file's header has the steps (password sign-in on for a moment, mint the service account's key, set
   `UME_RAG_OPENWEBUI_ADMIN_API_KEY` and `UME_RAG_OPENWEBUI_BASE_URL=http://rag-openwebui:8080`, password sign-in off).
8. **Back up** with the databases: `rag-secrets/.pepper`, `rag-secrets/keys/rag-blob-internal` and
   `rag-certs/data-protection.pfx` (for example `docker run --rm -v <stack>_rag-secrets:/s alpine cat /s/.pepper`). A
   database restored without them is unusable. The stack has no WAL archiving: use `pg_dump` or volume snapshots.

Then the knowledge bases and assistants: each gets one of its department's assigned keys (V39); a published assistant
must have one once the gateway acts as keys.

## Upgrade from 0.2.0–0.4.0

**Not a rolling update:** migration `DailyTotalsWithoutCost` removes columns the old rag-api still writes, so rag-api
and rag-admin stop before the migration. Plan a short window. The gateway's own upgrade (`v0.9.x` → `v0.10.1`) is
rolling and its migrations run by themselves. All notes: [upgrade-guide.md](upgrade-guide.md#050).

1. **Back up** both databases (`pg_dump` from `postgres` and `rag-postgres`) and the three Ume.Rag files of *First
   deploy* step 8.
2. **Stop** `rag-api` and `rag-admin` (*Containers*, select, *Stop*).
3. **Edit the stack:** paste the new file over the old one (keep the stack name), and change the variables:
   - remove the per-level keys `UME_RAG_*GATEWAY_KEY_<level>` and the Data API's `UME_RAG_ORG_SYNC_KEY` and
     `UME_RAG_ORG_SYNC_URL`; drop `data` from `COMPOSE_PROFILES` unless the gateway's Data API serves something else;
   - add `clamav` to `COMPOSE_PROFILES` (without it uploads wait with `clamav.unavailable`), or set
     `UME_RAG_CLAMAV_MODE=off` to store files without a malware scan;
   - add `openwebui` and its variables if you want the chat in this stack;
   - optionally `UME_ADMIN_PUBLIC_URL` for the gateway.
4. **Update the stack** (*Re-pull image and redeploy* on). The gateway moves to `v0.10.1` and migrates, `rag-migrate`
   applies every migration since your version (from `0.4.0`: `UploadScannedAt`, `GatewayKeys`,
   `GatewayModelLimitsSource`, `DailyUsage`, `ObjectGatewayKeys`, `DailyTotalsWithoutCost`, `ContentRetentionStep`) and
   the grants, then `rag-api` and `rag-admin` start on the new
   schema. `rag-bootstrap` deletes the old per-level key files and the Data API key file and logs it.
5. **Issue the credential keys** in the gateway's admin and set them as in *First deploy* steps 4–5; update the stack
   and restart `rag-api`, `rag-admin` and `rag-worker`. Until then their model calls are refused.
6. **Revoke** the old per-level keys and the Data API key in the gateway's admin.
7. **Check** as in *First deploy* step 6, then give each existing assistant and knowledge base one of its department's
   assigned keys (they have none after the upgrade, and a published assistant can't be republished without one).
8. Optional, once: files uploaded before `UploadScannedAt` have no scan date though they were scanned; set it with
   `UPDATE content."Uploads" SET "ScannedAt" = "UploadedAt" WHERE "ScannedAt" IS NULL AND "State" IN ('clean',
   'indexed');` in `rag-postgres` (*Containers → rag-postgres → Console*, then `psql -d ragdb`).

Tokens and cost per assistant and day from before the upgrade are gone (`DailyTotalsWithoutCost`); from the upgrade
on they are recorded per model call.

## When something fails

| Symptom | Look at |
|---|---|
| The stack won't deploy: `pull access denied` for `ghcr.io/thekim1/ume-rag/...` | the `ghcr.io` registry in Portainer and its token's `read:packages`; pick it on the stack |
| `rag-bootstrap` exits non-zero | its log; `the gateway's CA is missing` means the gateway's `bootstrap` didn't run first (keep the stack name) |
| Ume.Rag's services log `gateway_key_missing` or the gateway answers `401` | the three `UME_RAG_*_GATEWAY_KEY` variables, then restart the service |
| The gateway answers `403` to Ume.Rag | the credential key's address list (the `llm` subnet) and its integration role *Credential* |
| *Platform → Gateway models* shows `degraded` or `scope_missing` | `organisation.read` (and `usage.read` for the read-back) on the admin's credential key |
| Uploads wait with `clamav.unavailable` | `clamav` in `COMPOSE_PROFILES`, `rag-clamav` healthy (its first signature download takes minutes) |
| `rag-openwebui` restarts | its log: the wrapper's two files in `rag-secrets/openwebui/`, the CA bundle, write access to its volume |
| Open WebUI lists no models | `UME_RAG_OPENWEBUI_API_KEY`, the client's kind `open-webui`, and that its assistants are published |
