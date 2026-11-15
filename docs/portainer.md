# Deploying and upgrading with Portainer (0.6.x)

How to run Ume.Rag with the LLM gateway `v0.10.3` as one Portainer stack: a first deploy on a new VM, the upgrade of
a `0.5.x` stack to `0.6.0`, and the upgrade from Ume.Rag `0.2.0`–`0.4.0` (gateway `v0.9.x`). The stack is configured only through
environment variables, for hosts where `init-deployment.sh` can't run. Background and the full variable list: the
header of [`deploy/portainer/compose.portainer.yaml`](../deploy/portainer/compose.portainer.yaml), the runbook's
[*Portainer and other hosts without shell access*](runbook.md#portainer-and-other-hosts-without-shell-access), and
[production-setup.md](production-setup.md#portainer-gateway-and-rag-in-one-stack).

> **Open WebUI in this stack is new in `0.5.0`'s stack file (#657) and was merged without a boot test.** If
> `rag-openwebui` doesn't come up, check its log first for the start-up wrapper (the two secrets it reads from the
> `rag-secrets` volume), the CA bundle (`openwebui/ca.pem`) and the owner of its data volume, and report it.

## Which file

Use the **`compose.portainer.yaml` asset of the `v0.6.0` release** (or later), or the same file in the repository at
`deploy/portainer/compose.portainer.yaml` once the release's pin PR has merged. It pins the gateway's images
(`v0.10.2` in the `v0.6.0` asset, `v0.10.3` in the repository since) and Ume.Rag's by digest, so no image variable is needed, and has Open WebUI as the profile `openwebui`. The `v0.5.0` asset predates both: it has
no Open WebUI and leaves `GATEWAY_IMAGE`, `ADMINAPI_IMAGE` and `MIGRATIONS_IMAGE` to be set by hand.

## What you need

- A VM with Docker (Compose v2) and Portainer. Sizing: 4 vCPU, 16 GB RAM and 100 GB SSD for a pilot (floor 4 vCPU,
  8 GB, 50 GB; R1 sets the final numbers); ClamAV alone takes about 1 GB of RAM.
- **Registry access:** Ume.Rag's images on `ghcr.io/thekim1/ume-rag/*` are private. In Portainer, *Registries → Add
  registry → Custom*: `ghcr.io`, a GitHub user and a token with `read:packages`. The gateway's images are public.
- An **OIDC provider** (the shared Keycloak realm, AD FS or Entra ID) with clients for the gateway's admin
  (`ume-admin`), Ume.Rag's admin (`ume-rag-admin`) and, for the chat, Open WebUI (`open-webui`). The clients must exist
  before you set up sign-in. Their redirect URIs: `https://rag-admin.<domain>/signin-oidc` and
  `/signout-callback-oidc` for `ume-rag-admin`, `https://chat.<domain>/oauth/oidc/login/callback` for `open-webui` (the
  setup wizard shows them, ready to copy). Ume.Rag's client secrets are entered in the setup wizard, not in the stack.
- **Host names** and **real certificates** for the gateway API, the gateway admin, rag-api, Ume.Rag's admin and Open
  WebUI. The services serve certificates from their own internal CAs (Open WebUI plain HTTP), so something in front
  terminates TLS: your own reverse proxy (routes and body sizes:
  [production-setup.md](production-setup.md#reverse-proxy-and-pki)), or the stack's optional **proxy profile** (*TLS
  proxy* below).
- The **embedding and chat models** on the gateway, EU-hosted for now.

## TLS proxy

With `proxy` in `COMPOSE_PROFILES` the stack runs `rag-proxy`: nginx on `UME_RAG_PROXY_BIND:UME_RAG_PROXY_PORT`
(`0.0.0.0:443`) in front of all five host names, with the routes and body sizes of production-setup.md. It re-encrypts
to the services and verifies them against their internal CAs, so nothing else needs to be published.

1. On the host, put one certificate for all five names (SAN or wildcard) in a directory, as `tls.crt` (with its chain)
   and `tls.key`, readable by uid 101 (`chown 101 tls.key && chmod 0400 tls.key`), and set
   `UME_RAG_PROXY_CERTS_DIR` to it (default `/etc/ume-rag/proxy-certs`). Without it `rag-proxy` doesn't start.
2. Give each service a host name of its own: `UME_GATEWAY_HOST` and `UME_ADMIN_HOST` for the gateway, and for
   Ume.Rag `UME_DOMAIN`, which names rag-api `rag.<domain>`, its admin `rag-admin.<domain>` and Open WebUI
   `chat.<domain>` (`UME_RAG_API_HOST`, `UME_RAG_ADMIN_HOST`, `UME_RAG_OPENWEBUI_HOST` override one each). A name shared
   with another service gets no route, and
   `rag-bootstrap`'s checklist says which. The public URLs are then plain `https://<host>` (`UME_GATEWAY_PUBLIC_URL`,
   the OIDC redirect URIs without a port).
3. Bind the other published ports to loopback (`UME_GATEWAY_BIND`, `UME_ADMIN_BIND`, `UME_RAG_API_BIND`,
   `UME_RAG_ADMIN_BIND` = `127.0.0.1`), so only the proxy is reachable.
4. After the first deploy, list the proxy as a trusted proxy in both admins (the gateway's *Settings → Trusted proxies*,
   Ume.Rag's *Settings → Network*): its address on the stack's `edge` and `rag-edge` networks (Portainer: *Networks*),
   or the clients' addresses are the proxy's.

**Renewing the certificate:** replace both files and restart `rag-proxy` (*Containers*, select, *Restart*).

## Internal certificates

`rag-bootstrap` keeps RAG's internal CA (10 years) and the service certificates (825 days) in `rag-certs`. On every
deploy it re-issues a service certificate that expires within 30 days, and the checklist at the end of its log:

- says **restart `rag-postgres` `rag-parser`** after re-issuing theirs (*Containers*, select, *Restart*). They read
  their certificates only at start, and a stack update recreates neither when its configuration is unchanged. rag-api,
  rag-admin and rag-worker serve theirs through Kestrel, which watches the files; if a client still sees the old
  certificate, restart those too. Without the restart Postgres keeps serving the old certificate, and once it
  expires every service's database connection fails.
- names **every certificate that expires within 60 days**. For a service certificate, update the stack once fewer than
  30 days remain (nothing needs to change): `rag-bootstrap` then re-issues it.

The checklist is written only when the stack is deployed. Update the stack at least once a year, or, with a shell on
the host, run `deploy/host/cert-expiry.sh --certs-dir /var/lib/docker/volumes/<stack>_rag-certs/_data` as root daily
(cron or a systemd timer): it names each certificate that expires within 60 days and then exits non-zero.

**Replacing the CA** (the checklist names it 60 days before it expires): delete `ca.pem` and `ca.key` in `rag-certs`
(`docker run --rm -v <stack>_rag-certs:/c alpine rm /c/ca.pem /c/ca.key`) and update the stack. `rag-bootstrap`
creates a new CA and re-issues every service certificate; then restart every Ume.Rag service and `rag-proxy`, which
read the CA only at start.

## First deploy

1. **Create the stack.** *Stacks → Add stack*, a name you will keep (the volumes are found by it), *Web editor*: paste
   the file. Pick the `ghcr.io` registry.
2. **Variables:** *Environment variables → Load variables from .env file* with
   [`deploy/portainer/stack.env.example`](../deploy/portainer/stack.env.example), then replace the five values its
   comments mark CHANGE. None of them is a secret of Ume.Rag or Open WebUI:

   ```text
   # The gateway
   UME_OIDC_AUTHORITY=https://keycloak.example.se/realms/ume
   UME_GATEWAY_HOST=llm.example.se
   UME_GATEWAY_PUBLIC_URL=https://llm.example.se
   UME_ADMIN_HOST=llm-admin.example.se
   # Ume.Rag
   UME_DOMAIN=example.se                                  # rag.example.se, rag-admin.example.se, chat.example.se
   ```

   Optional, for the gateway: `UME_ADMIN_PUBLIC_URL` (links from Ume.Rag's admin to the gateway's) and
   `UME_OIDC_CLIENT_SECRET` (only if `ume-admin` is a confidential client). `COMPOSE_PROFILES=clamav,openwebui` is
   already in the file.

   `UME_DOMAIN` gives Ume.Rag's three host names: rag-api `rag.<domain>` (its public URL `https://rag.<domain>`;
   `UME_RAG_API_PUBLIC_URL` for a port), its admin `rag-admin.<domain>` and Open WebUI `chat.<domain>`. To use another
   name for one, set `UME_RAG_API_HOST`, `UME_RAG_ADMIN_HOST` or `UME_RAG_OPENWEBUI_HOST`. A stack that sets the names
   and no `UME_DOMAIN` works as before (the admin then shares rag-api's name unless `UME_RAG_ADMIN_HOST` is set).

   Keep `UME_INTEGRATION_MANIFEST` and `UME_INTEGRATION_API_URL` as the file has them: the gateway creates the
   integration `Ume.Rag` and its three credential keys from the manifest, and hands rag-admin the Integration API's
   address (M-17 C7, C11). If the team `ume-rag` doesn't exist in the gateway yet, it is created in the department whose
   cost centre code is the manifest's `departmentCode`: change that to yours (and `departmentName`), or create the team
   first. The other optional variables (ports, binds, groups, the gateway's settings) are in the file's header.

   Sign-in is not a variable: you set it up in step 4. To set it in the stack instead, see *Declaring sign-in instead*
   below.
3. **Deploy the stack.** The one-shot `bootstrap` (the gateway's) and `rag-bootstrap` create the internal CAs,
   certificates and secrets in the volumes; both databases are created and migrated; the gateway's `migrations`
   creates the integration `Ume.Rag` and its keys `rag-api`, `rag-admin` and `rag-worker` from the manifest and writes
   their secrets to the volume `<stack>_integration-secrets`, from which `rag-bootstrap` gives each service its key; the
   services start. **The end of `rag-bootstrap`'s log lists what is still missing** (example values left in, Open
   WebUI's settings); it is printed again on every deploy, until it says every setting it checks is in place. Its last
   lines point at the setup wizard on every deploy, since `rag-bootstrap` can't see whether setup is done: skip that
   when you can already sign in.
4. **Set up sign-in.** rag-admin opens its setup wizard on the first start and prints a setup code.
   - **Find the code:** *Containers → rag-admin → Logs*, the line
     `rag-admin: setup mode is open until 2026-11-20 14:05 UTC. Setup code: XXXXX-XXXXX-XXXXX-XXXXX-XXXXXX`. The code
     is valid for one hour. Treat it like a password: whoever holds it chooses the sign-in provider while setup is open.
     If the hour ends before you finish, restart rag-admin (*Containers*, select, *Restart*): a setup that was never
     finished opens again with a new code.
   - **Open** `https://rag-admin.<domain>/setup`, at the public address users will use: the wizard builds the redirect
     URIs from the address you open it at.
   - **The wizard's steps:**
     1. *Setup code*: paste the code.
     2. *Addresses*: the redirect URI and the post-logout redirect URI. Register both on the `ume-rag-admin` client at
        the provider now. The page also shows what the stack sets (the gateway's address, rag-api's public address, the
        platform admin groups).
     3. *Sign-in*: the provider address is locked (*Set by the deployment*: the stack's `UME_OIDC_AUTHORITY`); the
        client ID defaults to `ume-rag-admin`; type the client secret and, if you like, the date it expires. *Advanced*
        holds the claim names, with a preset for Keycloak and for Entra ID (the tenant ID). **Test sign-in** opens the
        provider in a new window: sign in there as yourself. The result shows who signed in, the roles, groups and
        departments the token carried, and whether that person is a platform admin.
     4. *Summary and finish*: the values to save and where each comes from. **Finish**, then **Sign in**.
   - **Who may finish:** only a test sign-in by a platform admin enables *Finish*: the role `rag-platform-admin` in the
     token's `roles` claim (Keycloak: that realm role, directly or through a group,
     [`deploy/keycloak/README.md`](../deploy/keycloak/README.md)), or a group named in
     `UME_RAG_OIDC_GROUPS_PLATFORM_ADMIN` (only for an identity provider whose tokens carry groups instead). The wizard
     says which claim it read.

   After *Finish* setup is closed: the setup API answers 404 and the code is void. The sign-in provider, client and
   secret change only after a re-open (*Opening the setup wizard again*), never from an admin session.
5. **Open WebUI's sign-in** (profile `openwebui`): Open WebUI asks rag-api for its sign-in settings when it starts, and
   restarts by itself within a minute when they change. Its provider defaults to rag-admin's authority's
   `/.well-known/openid-configuration` and its client to `open-webui`; register its redirect URI
   `https://chat.<domain>/oauth/oidc/login/callback` on that client (the wizard shows it with a copy button). The
   wizard's **Open WebUI** step, after the sign-in test, takes the client secret (leave the provider address empty to
   follow the admin's provider) and **Check** asks the provider whether it accepts the client: *accepted*, *refused*
   (a wrong client id or secret), or *could not be verified* (then a wrong secret shows when the first user signs in;
   saving is still allowed). **Save**, or **Skip for now** and open setup again later. Without a secret Open WebUI
   signs in as a public client (PKCE). Its log says what it started with:
   `rag-openwebui: starting with OIDC sign-in from rag-api's sign-in settings`. To declare the values instead, set
   `UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET` (and the other `UME_RAG_OPENWEBUI_OIDC_*` lines) in the stack.
6. **In the gateway's admin**, as a gateway admin ([production-setup.md](production-setup.md#gateway-credential-keys)
   has every field): under *Access → Integrations → Ume.Rag*, check the three credential keys (residencies `OnPrem`
   and `Eu`: Ume.Rag's ceiling, register M2), then create the **assigned keys** the assistants and knowledge bases
   will run on, one or more per department, in the integration with role *Assigned*. The manifest never changes a key
   that exists: change one here. **Rotate** a credential key here (with a grace period), then delete its file
   `Ume.Rag/rag-<service>` in `<stack>_integration-secrets` and update the stack; the gateway writes the new secret and
   the service picks it up within seconds.
7. **Keys issued by hand instead** (no manifest, or the gateway runs elsewhere): create the integration `Ume.Rag` and
   the three credential keys as production-setup.md says, set `UME_RAG_API_GATEWAY_KEY`, `UME_RAG_ADMIN_GATEWAY_KEY`
   and `UME_RAG_WORKER_GATEWAY_KEY` (set, a variable wins over the manifest's key) and update the stack. The services
   pick them up within seconds. **Address list** of a credential key: the services reach the gateway over the stack's
   internal network `llm`, whose subnet Docker chose (`docker network inspect <stack>_llm`); allow that subnet, or
   leave the list empty.
8. **Check** in Ume.Rag's admin (`https://rag-admin.example.se`): *Models* (side menu) shows the gateway's
   version and features with `act-as-key`, a recent organisation sync without a *degraded* reason, and the models.
   Then **create the embedding profile** (*Home* asks for it until one exists): *Models → Embedding profiles → Create
   profile*, pick the gateway's embedding model. The ceiling and the input limit come from the model; the test
   embedding measures its dimensions (or tick *Request dimensions* and give the size, for a model such as
   `text-embedding-3-large` that shortens its vectors) and is stored as the profile's canary. Every RAG credential key
   must allow the model. The first profile is the default, which new knowledge bases use.
9. **Open WebUI** connects by itself: `rag-bootstrap` generates its key for rag-api and `rag-migrate` registers it as
   the deployment's own client, shown in Ume.Rag's admin under *Apps and keys* as *Managed by the deployment*. Open it
   there to choose the assistants it offers (an assistant's *Who can use it* works too).

   Users sign in through OIDC and get the role `user`. The **feedback import** (thumbs and comments into Ume.Rag) needs
   nothing either: the one-shot `rag-openwebui-setup` mints the feedback service account's key once Open WebUI runs
   (password sign-in stays off) and keeps it while it works, and rag-api finds Open WebUI in the stack by itself. Its log
   says *minted* or *kept*. A new key: delete `rag-secrets/openwebui-feedback/OpenWebUi__AdminApiKey` and update the
   stack.
10. **Back up** with the databases: `rag-secrets/.pepper`, `rag-secrets/keys/rag-blob-internal` and
    `rag-certs/data-protection.pfx` (for example `docker run --rm -v <stack>_rag-secrets:/s alpine cat /s/.pepper`). A
    database restored without them is unusable, and the client secrets the wizard stored can only be read with the same
    `data-protection.pfx`. The stack has no WAL archiving: use `pg_dump` or volume snapshots.

Then the knowledge bases and assistants: each gets one of its department's assigned keys (V39); a published assistant
must have one once the gateway acts as keys.

### Declaring sign-in instead

A value set in the stack is the deployment's: it wins, and the wizard shows it locked (*Set by the deployment*). An
empty variable counts as unset. With an authority and a client secret in the stack, rag-admin counts setup as done at
its first start and never opens the wizard; a re-open shows those values locked.

- **rag-admin:** `UME_OIDC_AUTHORITY` (or `UME_RAG_OIDC_AUTHORITY` for another provider than the gateway's) and
  `UME_RAG_OIDC_CLIENT_SECRET`; optionally `UME_RAG_OIDC_CLIENT_ID` (default `ume-rag-admin`) and the token's shape:
  `UME_RAG_OIDC_ROLE_CLAIM`, `_DEPARTMENT_CLAIM`, `_TEAM_CLAIM`, `_GROUP_CLAIM`, `_EXTRA_SCOPE`, `_TENANT_ID` (Entra ID)
  and `_REFUSE_GUESTS` (`true` or `false`). The secret and refusing guests reach rag-admin as files that
  `rag-bootstrap` writes on every deploy (and removes when the variable is emptied).
- **Open WebUI:** `UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET`, and only to change the defaults
  `UME_RAG_OPENWEBUI_OIDC_CLIENT_ID`, `UME_RAG_OPENWEBUI_OIDC_PROVIDER_URL` and `UME_RAG_OPENWEBUI_OIDC_PROVIDER_NAME`.

The stack's variables are readable by everyone who may edit the stack; the wizard keeps the secrets encrypted in the
database instead.

## Upgrade to the release with the setup wizard

A stack that has its authority and `UME_RAG_OIDC_CLIENT_SECRET` in its variables keeps working and never sees the
wizard: paste the new file and update the stack. Open WebUI's variables keep working too; they now feed rag-api and
rag-admin. If you changed a token-shape variable (`UME_RAG_OIDC_ROLE_CLAIM` and the others), keep it: the stack file no
longer sets their defaults, so only the variables you set are passed. To move to the wizard, and the secrets out of the
stack's variables: the [upgrade guide](upgrade-guide.md#unreleased) has the steps.

## Upgrade from 0.5.x to 0.6.0

**Not a rolling update:** migration `RemoveWorkerConcurrency` drops a column the old rag-admin still reads, so stop
rag-admin first. Plan a short window (the services restart once). The gateway moves to `v0.10.2` (rolling, no
migration of its own). Every change and why: [upgrade-guide.md](upgrade-guide.md#060).

**Before the update**

1. **Back up** both databases and the three Ume.Rag files of *First deploy* step 10.
2. **In the gateway's admin**, give `rag-api`'s and `rag-admin`'s credential keys the residencies **`OnPrem` and `Eu`
   only** (the worker's already are). From 0.6.0 each service's ceiling is its key's residencies; a key that allows
   every residency would let a public assistant answer at `External`.
3. **If Open WebUI is on and its client was made by hand** (the 0.5.0 stack): in Ume.Rag's admin, *Apps and keys*,
   open that client and note its assistants, information class, history and feedback days. After the update Open WebUI
   uses a client the deployment makes, and the hand-made one is no longer trusted.
4. If you set any of `UME_RAG_OIDC_GROUPS_DEPARTMENT_ADMIN`, `_PIPELINE_EDITOR`, `_REVIEWER` or `_VIEWER`, note their
   groups (with the shared Keycloak the roles come in the token and there is nothing to note).
5. Optional: set the organisation's name in the gateway's admin console (branding); Ume.Rag's header and sign-in page
   show it from now on.

**The update**

6. **Stop** `rag-admin` (*Containers*, select, *Stop*).
7. **Edit the stack:** paste the new file over the old one (keep the stack name), and change the variables:
   - **add** `UME_INTEGRATION_API_URL=https://adminapi:8443/integration/v1` (the cost and budget read-back);
   - **keep** `UME_RAG_API_GATEWAY_KEY`, `UME_RAG_ADMIN_GATEWAY_KEY` and `UME_RAG_WORKER_GATEWAY_KEY`. Don't add
     `UME_INTEGRATION_MANIFEST` from `stack.env.example`: it would create three new keys beside yours (the 0.5.0 keys
     are named `Ume.Rag api` etc., not `rag-api`). The upgrade guide says when to move to it;
   - **remove** those that are no longer read (a leftover is ignored, so this is tidying): `UME_RAG_OPENWEBUI_API_KEY`,
     `UME_RAG_OPENWEBUI_CLIENT_ID`, `UME_RAG_OPENWEBUI_ADMIN_API_KEY`, `UME_RAG_OPENWEBUI_PASSWORD_AUTH`,
     `UME_RAG_OPENWEBUI_BASE_URL`, `UME_RAG_CLAMAV_MODE`, `UME_RAG_EMBEDDING_MODEL` and the other
     `UME_RAG_EMBEDDING_*`, `UME_RAG_GATEWAY_MAX_RESIDENCY`, `UME_RAG_GATEWAY_INTEGRATION_API_URL`,
     `UME_ORGANISATION_NAME` and the four group variables of step 4 (`UME_RAG_OIDC_GROUPS_PLATFORM_ADMIN` stays).
8. **Update the stack** (*Re-pull image and redeploy* on). In order: the gateway's `migrations`; `rag-bootstrap`;
   `rag-migrate` (migrations `ClientManagedByDeployment`, `RemoveWorkerConcurrency`, `IdentityRoleGroups`,
   `ChunkSetsEmbeddingProfileIndex`, `ClientsManagedPerName`; it creates the managed clients *Open WebUI* (while its
   profile is on) and
   `admin-console`); then the services. With Open WebUI on, `rag-openwebui-setup` mints the feedback key a minute after
   Open WebUI starts.

**After the update**

9. **Read the end of `rag-bootstrap`'s log:** it lists what is still missing, and should name no gateway key.
10. **Open WebUI's client** (step 3): in *Apps and keys*, open *Open WebUI* (*Managed by the deployment*), set the class,
    history and feedback days to the old client's and tick the same assistants; save. Then disable the hand-made client
    and revoke its key.
11. If you noted groups in step 4, enter them under *Settings → Role groups*.
12. **Check:**
    - each of `rag-api`, `rag-admin` and `rag-worker` logs *the credential key's ceiling: Eu* at start;
    - *Models* shows the gateway's version with `act-as-key`, and the embedding profile is still there;
    - a test user signs in to Open WebUI, sees the assistants and gets an answer;
    - `rag-openwebui-setup`'s log says *minted*;
    - *Apps and keys* shows `admin-console` and *Open WebUI* as managed, and the assistant test panel answers;
    - an assistant's or knowledge base's *Cost* card shows the gateway's figures (the read-back) within a few minutes.

## Upgrade from 0.2.0–0.4.0

**Not a rolling update:** migration `DailyTotalsWithoutCost` removes columns the old rag-api still writes, so rag-api
and rag-admin stop before the migration. Plan a short window. The gateway's own upgrade (`v0.9.x` → `v0.10.1`) is
rolling and its migrations run by themselves. All notes: [upgrade-guide.md](upgrade-guide.md#050).

1. **Back up** both databases (`pg_dump` from `postgres` and `rag-postgres`) and the three Ume.Rag files of *First
   deploy* step 10.
2. **Stop** `rag-api` and `rag-admin` (*Containers*, select, *Stop*).
3. **Edit the stack:** paste the new file over the old one (keep the stack name), and change the variables:
   - remove the per-level keys `UME_RAG_*GATEWAY_KEY_<level>` and the Data API's `UME_RAG_ORG_SYNC_KEY` and
     `UME_RAG_ORG_SYNC_URL`; drop `data` from `COMPOSE_PROFILES` unless the gateway's Data API serves something else;
   - add `clamav` to `COMPOSE_PROFILES` for the malware scan; without it files are stored without a scan, marked so
     (`rag-bootstrap` sets the worker's mode from the profile; the stack no longer reads `UME_RAG_CLAMAV_MODE` or
     `UME_RAG_OPENWEBUI_BASE_URL`, so remove them if you set them);
   - add `openwebui` and its variables if you want the chat in this stack;
   - optionally `UME_ADMIN_PUBLIC_URL` for the gateway.
4. **Update the stack** (*Re-pull image and redeploy* on). The gateway moves to `v0.10.1` and migrates, `rag-migrate`
   applies every migration since your version (from `0.4.0`: `UploadScannedAt`, `GatewayKeys`,
   `GatewayModelLimitsSource`, `DailyUsage`, `ObjectGatewayKeys`, `DailyTotalsWithoutCost`, `ContentRetentionStep`) and
   the grants, then `rag-api` and `rag-admin` start on the new
   schema. `rag-bootstrap` deletes the old per-level key files and the Data API key file and logs it.
5. **Issue the credential keys** in the gateway's admin and set them as in *First deploy* steps 6–7; update the stack
   (`0.5.0`'s images: then restart `rag-api`, `rag-admin` and `rag-worker`). Until then their model calls are refused.
6. **Revoke** the old per-level keys and the Data API key in the gateway's admin.
7. **Check** as in *First deploy* step 8, then give each existing assistant and knowledge base one of its department's
   assigned keys (they have none after the upgrade, and a published assistant can't be republished without one).
8. Optional, once: files uploaded before `UploadScannedAt` have no scan date though they were scanned; set it with
   `UPDATE content."Uploads" SET "ScannedAt" = "UploadedAt" WHERE "ScannedAt" IS NULL AND "State" IN ('clean',
   'indexed');` in `rag-postgres` (*Containers → rag-postgres → Console*, then `psql -d ragdb`).

Tokens and cost per assistant and day from before the upgrade are gone (`DailyTotalsWithoutCost`); from the upgrade
on they are recorded per model call.

## Opening the setup wizard again

After setup, the sign-in provider, the client and its secret change only in the wizard, and only after a re-open:
never from an admin session. Re-open it for a client secret that is about to expire, a new provider or client, or a
sign-in that broke.

1. Set `UME_RAG_SETUP_REOPEN` to a new value (for example today's date) and update the stack.
2. rag-admin opens the wizard once, for one hour, and prints a new setup code in its log (*Containers → rag-admin →
   Logs*). The current sign-in keeps working until you finish.
3. Open `https://rag-admin.<domain>/setup`, enter the code, change what you need (a stored secret is never shown: type
   the new one, and its expiry date), test the sign-in and finish.

The value is used once: a restart doesn't open the wizard again, and an abandoned re-open ends after the hour. The next
re-open needs another new value. Values the stack sets stay locked; to change one, change the variable.

**A client secret that expires:** enter its expiry date in the wizard; Home reminds platform admins from 30 days
before it. Create the new secret at the provider before
the old one expires, then re-open and enter it.

**Before restoring a database,** remove `UME_RAG_SETUP_REOPEN` (or keep its value unchanged afterwards): a restored
database may have consumed an older value, and the wizard would open again for an hour. The client secrets the wizard
stored need the same `rag-certs/data-protection.pfx` as the database; with another certificate they can't be read
and sign-in fails until you re-open and type them again.

The code is derived from `rag-secrets/admin/Setup__Secret`, which `rag-bootstrap` generates once; it needs no backup (a
new one only changes future codes).

## When something fails

| Symptom | Look at |
|---|---|
| The stack won't deploy: `pull access denied` for `ghcr.io/thekim1/ume-rag/...` | the `ghcr.io` registry in Portainer and its token's `read:packages`; pick it on the stack |
| `rag-bootstrap` exits non-zero | its log; `the gateway's CA is missing` means the gateway's `bootstrap` didn't run first (keep the stack name) |
| Ume.Rag's services log `gateway_key_missing` or the gateway answers `401` | the three `UME_RAG_*_GATEWAY_KEY` variables and `rag-bootstrap`'s checklist; update the stack (`0.5.0`'s images: restart the service) |
| The gateway answers `403` to Ume.Rag | the credential key's address list (the `llm` subnet) and its integration role *Credential* |
| *Models* shows `degraded` or `scope_missing` | `organisation.read` (and `usage.read` for the read-back) on the admin's credential key |
| Uploads wait with `clamav.unavailable` | `clamav` in `COMPOSE_PROFILES`, `rag-clamav` healthy (its first signature download takes minutes) |
| `rag-openwebui` restarts | its log: the wrapper's two files in `rag-secrets/openwebui/`, the CA bundle, write access to its volume |
| Open WebUI lists no models | *Apps and keys*: the Open WebUI app managed by the deployment offers assistants, and they are published; `rag-migrate`'s log names the client |
| No setup code in rag-admin's log | Setup is done already (sign in; to change sign-in, re-open). Or the stack declares an authority and `UME_RAG_OIDC_CLIENT_SECRET` (the log says *The deployment declares the sign-in*), or a placeholder such as `CHANGE` is left in that variable (`rag-bootstrap`'s checklist names it). Or the log says *the setup secret (Setup__Secret …) is missing*: update the stack, so `rag-bootstrap` writes it, then restart rag-admin. *The settings row is missing*: `rag-migrate` didn't complete (its log) |
| The wizard says *Too many attempts* | More than 20 codes a minute reached rag-admin. Wait the seconds it says and paste the code again, exactly. If it wasn't you, someone is guessing: security event 6009 has the address ([security-events.md](security-events.md)) |
| *The setup window has ended* | The hour is over. Setup never finished: restart rag-admin for a new code. After a finished setup: a new `UME_RAG_SETUP_REOPEN` value |
| The test sign-in fails: the provider can't be reached, or its certificate isn't trusted | rag-admin itself fetches the provider's discovery document: the address (the issuer, `https`), DNS and firewalls from the VM, and that rag-admin trusts the provider's certificate |
| The provider's own page says the redirect URI is invalid | Register the wizard's redirect URI (step *Addresses*) on the client exactly, with the host name you opened the wizard at |
| The test sign-in fails at the token (*The provider refused rag-admin’s sign-in request*) | A wrong client secret or client ID, or a public client tested with a secret (leave the field empty) |
| The test passed, but *Finish* stays off: *not a platform admin* | Give the person the role `rag-platform-admin` at the provider, or name one of their groups in `UME_RAG_OIDC_GROUPS_PLATFORM_ADMIN` and update the stack; then test again |
| After setup the sign-in page says sign-in is not set up | No usable authority, or a stored secret that can't be read (a different `data-protection.pfx`): re-open and enter the values again, or declare them in the stack (*Declaring sign-in instead*) and update it |
| Open WebUI shows no sign-in button | `rag-openwebui`'s log: *rag-api did not answer within 60 s* (rag-api down or unreachable from the wrapper: it asks again every minute and restarts Open WebUI once it answers), or *no sign-in settings yet* (no provider: rag-admin has no authority yet) |
