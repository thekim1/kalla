# Upgrade guide and release checklist

Each release says whether it supports a **rolling update**: the previous version keeps running against the new schema
while its containers are replaced. The pilot runs **one slot** (one `rag-api`, one `rag-admin`), so every pilot upgrade
is a short, announced maintenance window whatever the answer. With two slots (`init-deployment.sh --rolling`) a "yes"
release is updated without downtime by `deploy/rolling-update.sh` ([Updates without downtime](zero-downtime-updates.md)).
Until the first production deployment releases may break compatibility (Decisions, "No backward compatibility before
production"), so check the answer every time; from then on migrations are expand/contract only and "no" is the
exception, with its own steps.

`dc` below stands for `docker compose --env-file deploy/.env`, run from the directory that holds `deploy/`.

## Unreleased

- **The Portainer stack pins the gateway's `v0.10.3`.** Rolling update, no migration, no new variable: the only
  change is the gateway's Ume.Rag preset (*Integrations → Add from manifest*), whose `rag-api` and `rag-admin` keys now
  allow only `OnPrem` and `Eu`, as the stack's `UME_INTEGRATION_MANIFEST` already does. Keys made from the `v0.10.2`
  preset keep their residencies: set them to `OnPrem` and `Eu` in the gateway's admin (0.6.0, *Before the update*).
- **`UME_DOMAIN` names Ume.Rag's hosts (setup wizard S0).** Rolling update, no migration. Portainer: `UME_DOMAIN`
  gives rag-api `rag.<domain>`, its admin `rag-admin.<domain>` and Open WebUI `chat.<domain>`; each `UME_RAG_*_HOST`
  still overrides its own name. Compose: `init-deployment.sh --domain DOMAIN` does the same for `--api-host`,
  `--admin-host` and, when another `--openwebui-*` option asks for Open WebUI, `--openwebui-host`. **Apply:** nothing; a stack that sets `UME_RAG_API_HOST` (and the others)
  without `UME_DOMAIN` keeps its names. To switch, set `UME_DOMAIN` and remove the host variables whose value is the
  default. **Check:** `rag-bootstrap`'s certificates and the proxy's names are unchanged (no `issuing certificate` for
  rag-api or rag-admin in its log unless a name changed).
- **Sign-in is set in the setup wizard (M-20 S1–S6; identity design §19).** rag-admin starts without an authority and,
  on a first run, opens its setup wizard at `https://<admin host>/setup` with a setup code on its output: the wizard
  shows the redirect URIs, takes the client secrets (stored encrypted with Data Protection) and saves only after a test
  sign-in by a platform admin. A value the deployment sets wins and is locked in the wizard; an empty one is unset.
  Open WebUI's start wrapper (`deploy/openwebui/start-with-sign-in.py`) asks rag-api for its sign-in settings and
  restarts Open WebUI within a minute when they change. The stack files no longer pass `Oidc__*` defaults, and
  `stack.env.example` has no secret left. Migrations `IdentityOidcSettings`, `SetupMode` and `OpenWebUiSignInSettings`
  add columns and a table and widen a check: **rolling update: yes** for the schema (Open WebUI is recreated).
  - **An existing deployment keeps working and never sees the wizard** when it has its authority and client secret in
    its variables (Portainer: `UME_OIDC_AUTHORITY` or `UME_RAG_OIDC_AUTHORITY`, and `UME_RAG_OIDC_CLIENT_SECRET`;
    Compose: `--oidc-authority` and `--oidc-client-secret-file`; Kubernetes: `Oidc__Authority` and the secret file):
    rag-admin counts setup as done at its first start.
  - **A token-shape variable you changed** (`UME_RAG_OIDC_ROLE_CLAIM`, `_DEPARTMENT_CLAIM`, `_TEAM_CLAIM`,
    `_GROUP_CLAIM`, `_EXTRA_SCOPE`, `_TENANT_ID`, `_REFUSE_GUESTS`, `_CLIENT_ID`): keep the line. The stack files pass
    each only when it is set, so the code's defaults (the old defaults; `email` is always requested) apply otherwise, or
    the wizard's values. `UME_RAG_OIDC_REFUSE_GUESTS` now reaches rag-admin as a file: Portainer's `rag-bootstrap` writes
    it; Compose passes `--oidc-refuse-guests true|false` to `init-deployment.sh` instead of the variable in
    `deploy/.env`; Kubernetes keeps its `Oidc__RefuseGuests` literal, which the base no longer sets.
  - **Open WebUI's variables** (`UME_RAG_OPENWEBUI_OIDC_*`) now feed rag-api and rag-admin, which serve them to Open
    WebUI's start; they keep working as deployment values. `UME_RAG_OPENWEBUI_OIDC_CLIENT_ID=open-webui` may go (it is
    the default; set, it locks the wizard's field). Without a client secret Open WebUI signs in as a public client
    (PKCE). A confidential client's secret stays in the stack, or is entered in the wizard's Open WebUI step after a re-open.
  - **Portainer apply:** step by step in [portainer.md](portainer.md#upgrade-from-060). In short: paste the new `compose.portainer.yaml` and update the stack (`rag-bootstrap` writes
    `admin/Setup__Secret`, and Open WebUI's secret to rag-api's and rag-admin's secret files). A leftover
    `UME_RAG_OIDC_CLIENT_SECRET=CHANGE` declares a wrong secret and keeps the wizard closed: `rag-bootstrap`'s checklist
    names it; remove it.
  - **Compose apply** (an existing 0.6.0 deployment; steps 1–3 and 7–8 of *Upgrade procedure* below stay as they are):
    1. Replace `deploy/` with the release's, keeping `deploy/.env`, `deploy/certs` and `deploy/secrets`, and set the
       image pins.
    2. Re-run `init-deployment.sh` with your options. The `--oidc-*` options are optional and never prompted; a re-run
       without `--oidc-authority` or `--oidc-client-id` keeps the value in `deploy/.env`, and the client secret file
       in `secrets/admin` stays, so the sign-in stays declared and the wizard stays closed. It writes
       `secrets/admin/Setup__Secret`. With the host firewall, keep `--oidc-authority`: rag-admin reaches only that
       provider.
    3. With Open WebUI: pass **`--openwebui-oidc-client-secret-file` again**. The secret moved to `secrets/api` and
       `secrets/admin/OpenWebUi__Oidc__ClientSecret`; the old `OAUTH_CLIENT_SECRET` line in `secrets/openwebui.env` is
       no longer read and may be deleted. Pass `--openwebui-oidc-client-id` and `--openwebui-oidc-provider-url` only
       if they differ from the defaults. A provider on another host than the authority needs `--allow openwebui=HOST`
       for the host firewall.
    4. If you used `UME_RAG_OIDC_REFUSE_GUESTS` in `deploy/.env`: pass `--oidc-refuse-guests true|false` instead.
    5. `dc up -d --wait`, then `docker compose -f deploy/compose.openwebui.yaml --env-file deploy/.env up -d`.
  - **Kubernetes apply:** `create-secrets.sh` after the re-run of `init-deployment.sh` (it carries
    `admin/Setup__Secret`), then `deploy.sh`. The base no longer sets `Oidc__ClientId`, the claims, `Oidc__ExtraScopes__0`
    or `Oidc__RefuseGuests`: add to your overlay any of them you had changed.
  - **To move a declared deployment to the wizard** (and the secrets out of the variables): remove the client secret
    (Portainer: `UME_RAG_OIDC_CLIENT_SECRET`, and Open WebUI's once the wizard has its step; Compose: delete
    `secrets/admin/Oidc__ClientSecret`; Kubernetes: delete the key from `ume-rag-admin-secrets`), set
    `UME_RAG_SETUP_REOPEN` (Kubernetes: `Setup__Reopen`) to a new value, update the stack, then enter the values in the
    wizard with the code from rag-admin's log. Until you finish, sign-in uses the stored values, which are now
    incomplete: do it in one go. Keep the authority variable if the gateway shares it (it stays locked).
  - **Check:** a declared deployment's rag-admin logs *The deployment declares the sign-in* once and signs in as
    before; a new one prints `rag-admin: setup mode is open until … Setup code: …`. rag-openwebui's (or open-webui's)
    log has `rag-openwebui: starting with OIDC sign-in from rag-api's sign-in settings`, and Open WebUI's sign-in page
    shows the button. `UME_RAG_SETUP_REOPEN` (Kubernetes: `Setup__Reopen`) stays empty until you want to open the
    wizard again; remove it before restoring a database.
- **A leftover variable is named (M-21).** Rolling update, no migration, no new variable to set. A Portainer stack that
  still sets a variable this version no longer reads (the ones the sections below say to remove, back to 0.3.0) works
  as before; `rag-bootstrap`'s checklist now names each with what replaced it, and Home in the admin UI shows platform
  admins a notice with the names. **Apply:** nothing is required; to clear the notice, remove the named variables from
  the stack's variables and update the stack. **Check:** `rag-bootstrap`'s log has no line *is set and no longer read*.

## 0.6.0

- **Rolling update: no** from 0.5.x. Migration `RemoveWorkerConcurrency` drops a column the old rag-admin still reads:
  stop the old rag-admin before `rag-migrate` runs, then start the new one (rag-api and rag-worker are not affected).
- **Before the update:** give rag-api's and rag-admin's credential keys the residencies `OnPrem` and `Eu` in the
  gateway (C9, below). **Portainer:** paste the new `compose.portainer.yaml` (gateway v0.10.2, Ume.Rag 0.6.0) and add
  `UME_INTEGRATION_API_URL` (C11); keep your `UME_RAG_*_GATEWAY_KEY` variables unless you move to the manifest (C7).
- **After the update:** create the embedding profile if none exists (C5) and check *Models* shows `act-as-key`.

- **Fewer Portainer variables (M-17 C1–C3, #682, #683).** The stack sets Open WebUI's feedback address while the
  `openwebui` profile is on and the malware-scan mode from the `clamav` profile, so `UME_RAG_OPENWEBUI_BASE_URL` and
  `UME_RAG_CLAMAV_MODE` are no longer read; `UME_RAG_API_PUBLIC_URL` defaults to `https://<UME_RAG_API_HOST>` and is
  needed only for a public URL with a port. **Apply:** nothing is required (a leftover value is ignored); remove the two
  variables when you paste the new stack file. **Check:** `rag-bootstrap`'s checklist names nothing about them.
- **Open WebUI's client is set up by the deployment (M-17 C4, #684).** rag-migrate creates the client *Open WebUI*
  (*Managed by the deployment*) from a key the deployment generates, and rag-api trusts Open WebUI's users **only**
  for that client. An Open WebUI client made by hand stays in the registry but is no longer trusted, so until the
  assistants' audiences move, Open WebUI users see no models. **Apply:**
  1. Before the update, note which assistants the hand-made client offers (*Apps and keys* → its drawer, or each
     assistant's *Who can use it*), and its information class, history and feedback days.
  2. Portainer: paste the new stack file and remove `UME_RAG_OPENWEBUI_API_KEY` and `UME_RAG_OPENWEBUI_CLIENT_ID`
     from the variables; Compose: re-run `init-deployment.sh` with your options, without `--openwebui-api-key-file`
     and `--openwebui-rag-client-id` (both are unknown options now). Update the stack.
  3. In *Apps and keys*, open *Open WebUI* (managed): set the class, history and feedback days to the old client's
     (it starts as `internal`, keeps history, 90 days), and tick the same assistants. Save.
  4. Disable the hand-made client and revoke its key.
  **Check:** a test user signs in to Open WebUI and sees the assistants; chat answers no `401 user_jwt_invalid`
  (what the hand-made client's requests get now). A new deployment key later: delete `.openwebui-rag-api-key` in the secrets volume
  (Compose: `secrets/.openwebui-rag-api-key`) and update the stack; rag-migrate revokes the old one.
- **The embedding profile is created in the admin UI (M-17 C5).** rag-migrate no longer seeds it, and
  `UME_RAG_EMBEDDING_MODEL`, `_DIMENSIONS`, `_REQUEST_DIMENSIONS`, `_MAX_INPUT_TOKENS` and `_MAX_RESIDENCY` (Compose,
  Portainer) and `EmbeddingProfile__*` with the `ume-rag-migrations-config` map (Kubernetes) are gone. An existing
  profile stays as it is. **Apply:** remove those variables from the stack or `deploy/.env`; Kubernetes: take the new
  base and drop the `ume-rag-migrations-config` entry from your overlay. A new deployment: a platform admin creates the
  profile under *Models → Embedding profiles → Create profile* ([portainer.md](portainer.md) step 8).
- **The Open WebUI feedback key is minted by the stack (M-17 C6).** A one-shot job (Portainer: `rag-openwebui-setup`;
  Compose: `open-webui-setup` in `compose.openwebui.yaml`) mints the feedback service account's key after Open WebUI
  starts, with password sign-in off, into `openwebui-feedback/OpenWebUi__AdminApiKey`, which rag-api now reads
  (`Security__ExtraSecretsDirectory`). `UME_RAG_OPENWEBUI_ADMIN_API_KEY`, `UME_RAG_OPENWEBUI_PASSWORD_AUTH` and
  `deploy/openwebui-feedback-key.sh` are gone; `rag-bootstrap` and `init-deployment.sh` delete the old
  `api/OpenWebUi__AdminApiKey`. **Apply:** Portainer: paste the new stack file, remove the two variables, update the
  stack. Compose: re-run `init-deployment.sh` with your options (it creates `secrets/openwebui-feedback`), update the
  main stack, then `docker compose -f deploy/compose.openwebui.yaml --env-file deploy/.env up -d`. Until the job has
  minted the new key (a minute after Open WebUI starts), the import has none and skips its run. **Check:** the job's log says *minted*, and rag-api's next feedback import
  runs (within 15 minutes).
- **Optional TLS proxy in the Portainer stack (M-17 C8).** `proxy` in `COMPOSE_PROFILES` starts `rag-proxy`, nginx
  with your certificate (`UME_RAG_PROXY_CERTS_DIR`) in front of the five host names ([portainer.md](portainer.md#tls-proxy)).
  Nothing changes without the profile.
- **The residency ceiling comes from the gateway (M-17 C9).** Each service's ceiling is its credential key's allowed
  residencies in the gateway's context; `UME_RAG_GATEWAY_MAX_RESIDENCY` (`Gateway__MaxResidency`, Kubernetes
  `ume-rag-config`) is gone. **Apply before the update:** in the gateway's admin, give rag-api's and rag-admin's credential
  keys the residencies `OnPrem` and `Eu` only while register M2 holds (the worker's already are); a key that allows every
  residency would otherwise let a public assistant answer at `External`. Then remove the variable. **Check:** each
  service logs *the credential key's ceiling: Eu* at start.
- **Role groups are a setting (M-17 C10).** Migration `IdentityRoleGroups` adds `config."Settings"."IdentityRoleGroups"`;
  *Settings → Role groups* in RAG's admin maps sign-in groups to roles, read at each sign-in.
  `UME_RAG_OIDC_GROUPS_DEPARTMENT_ADMIN`, `_PIPELINE_EDITOR`, `_REVIEWER` and `_VIEWER` are gone;
  `UME_RAG_OIDC_GROUPS_PLATFORM_ADMIN` stays as the break-glass and is merged with the setting. **Apply:** if you set
  any of the four, enter the same groups on that page right after the update and remove the variables; until then, a
  user who had a role only through one of those groups doesn't have it. With the shared Keycloak the roles come in the token and nothing changes.
- **The organisation's name comes from the gateway (M-17 C12).** RAG's admin UI names the organisation from its gateway
  context's `organisation_name` (the gateway admin console's branding; gateway v0.10.2, feature `organisation-name`).
  `UME_RAG_ORGANISATION_NAME` (Compose), `UME_ORGANISATION_NAME` (Portainer, for rag-admin) and `Admin__OrganisationName`
  (Kubernetes, `ume-rag-admin-config`) are no longer read. **Apply:** set the organisation's name in the gateway's admin
  console (branding) if it isn't, and remove the variable. With an older gateway, or an empty name, the texts name no
  organisation. **Check:** RAG's sign-in page names the organisation.
- **The Integration API's address comes from the gateway (M-17 C11).** rag-admin reads the address of the gateway's
  Integration API (the cost and budget read-back) from its gateway context's `integration_api_url` (gateway v0.10.2,
  feature `integration-api-url`). `UME_RAG_GATEWAY_INTEGRATION_API_URL` (Compose, Portainer) and
  `Gateway__IntegrationApiUrl` (Kubernetes, `ume-rag-admin-config`) are no longer read. **Apply:** if you used the
  read-back, the gateway admin sets the gateway's `UME_INTEGRATION_API_URL` to the Admin API's address as rag-admin
  reaches it, **ending in `/integration/v1`** (your old value plus `/integration/v1`); then remove the variable. Compose
  with the host firewall: the variable no longer adds the host to `llm`, so run `docker-user-firewall.sh` with
  `--allow llm=<gateway admin host>:<port>` (a drop-in for `rag-firewall.service` that adds it to `ExecStart`). Without the gateway's address there is no
  read-back. **Check:** `GET /api/gateway/status` has `integration.configured: true` within 5 minutes of the update.
- **Migration `RemoveWorkerConcurrency` (M-18, #686)** drops `config."Settings"."WorkerConcurrency"`, which nothing
  read; per-instance concurrency stays `Worker__Concurrency__<class>`. The old rag-admin still selects the column
  (its settings row through EF), so its settings pages fail between the migration and its own update; stop it first.
  rag-api and rag-worker name their columns and are not affected.
- **The `admin-console` client is set up by the deployment (#712).** rag-migrate creates the test panel's client
  `admin-console` (type `internal`, *Managed by the deployment*) in every deployment; one made by hand becomes the managed
  one, its settings kept and any key of it revoked. Its keys can no longer be made or changed in the admin. Migration
  `ClientsManagedPerName` makes the managed clients unique per type and name instead of per type, and rag-migrate finds
  them by both, so the name of a client managed by the deployment (*Open WebUI*, `admin-console`) can no longer be
  changed in the admin. **Apply:**
  nothing. **Check:** *Apps and keys* shows `admin-console` as managed; rag-migrate's log says "The admin test panel's
  client admin-console: Created" (or `Adopted`, then `Unchanged`).
- **Migration `ChunkSetsEmbeddingProfileIndex` (#712)** adds an index on `content."ChunkSets"."EmbeddingProfileId"`
  for the admin's embedding-profile *in use* checks. Nothing else changes; the old services run against it.
- **The Portainer stack pins the gateway's `v0.10.2`, and the gateway creates Ume.Rag's credential keys (M-17 C7).**
  The gateway's own upgrade notes for 0.10.2 apply (rolling update, no migration; a new volume
  `<stack>_integration-secrets`; a read grant on `Branding.OrganisationName`). The gateway's `migrations` now applies
  `UME_INTEGRATION_MANIFEST` (in `stack.env.example`): it creates the integration `Ume.Rag`, its owner team and the keys
  `rag-api`, `rag-admin` and `rag-worker` where they don't exist, by name, and never changes one that does;
  `rag-bootstrap` copies their secrets to the services. `UME_RAG_*_GATEWAY_KEY`, set, still wins. `rag-bootstrap` now
  waits for the gateway's `migrations`, and rag-admin joins the gateway's `edge` network to reach `adminapi`.
  **Apply** to a stack whose keys were made by hand: paste the new stack file and keep your three `UME_RAG_*_GATEWAY_KEY`
  variables; add `UME_INTEGRATION_API_URL=https://adminapi:8443/integration/v1` (the cost read-back's address, handed
  to rag-admin in the gateway's context). Add `UME_INTEGRATION_MANIFEST` only if your integration is named `Ume.Rag` and
  its keys `rag-api`, `rag-admin` and `rag-worker` (then nothing is created, and the existing keys' secrets are written
  to the volume, audited as a reveal), or once you want the gateway to issue new keys under those names; set its
  `teamCode` to the integration's owner team. A new stack: keep both from `stack.env.example`, change `departmentCode`.
  **Check:** the gateway's `migrations` log says *'Ume.Rag' applied*; `rag-bootstrap`'s checklist names no gateway key;
  *Models* in RAG's admin shows `act-as-key`.

- **Kubernetes NetworkPolicies are on by default (#706).** `base` now carries default-deny and every flow the stack
  needs; `components/networkpolicies` is gone and the Ingress controller's rule is `components/ingress-controller`
  (namespace `ingress-nginx`). The worker's and freshclam's 80/443 egress reaches public addresses only. **Apply:** in
  your overlay replace `../components/networkpolicies` with `../components/ingress-controller` (change its namespace
  with the example's patch if yours differs); add the example overlay's egress rules for a gateway at a private address
  or on 8443, each `Crawl__AllowedPrivateRanges` range, and pgBackRest's S3 store. **Check:** with an enforcing CNI,
  sign in to the admin UI, ask a question, crawl a page and upload a file ([kubernetes.md](kubernetes.md), *Network
  policies*).
- **Certificate expiry is watched (#705).** **Apply (Compose):** run `deploy/host/install.sh` again to install and
  enable `rag-cert-expiry.timer` (daily; it fails below 60 days and writes `certs.prom`). Portainer: nothing; the
  checklist names expiring certificates on every deploy. **Check:** `systemctl status rag-cert-expiry.timer`.

## 0.5.1

- **Rolling update: yes** from 0.5.0: no migration, no configuration change for Compose or Kubernetes.
- **Portainer:** paste the new `deploy/portainer/compose.portainer.yaml` over the stack (it pins the gateway's and
  Ume.Rag's images and adds the optional `openwebui` profile) and update it. From this release a changed gateway key or
  Open WebUI feedback key applies without restarts; the end of `rag-bootstrap`'s log lists what is still missing
  ([portainer.md](portainer.md)).

## 0.5.0

- **One gateway credential key per service (#580, B-37, A-30).** rag-api, rag-admin and rag-worker each read only
  their credential key of the gateway integration *Ume.Rag*, the secret file `Gateway__Key`; the per-level keys
  (`Gateway__Keys__<level>`) are no longer read. **Apply:** a gateway admin issues the three keys
  (production-setup.md, *Gateway credential keys*); re-run `init-deployment.sh` with your options, replacing every
  `--gateway-key-file` with `--gateway-credential-key-file api=FILE`, `admin=FILE` and `worker=FILE`. It writes the new
  files, deletes the per-level ones and names them: ask the gateway admin to revoke those keys. Then restart api, admin
  and worker. Kubernetes: `create-secrets.sh` again (the old keys leave the secrets), then `kubectl rollout restart`.
  Portainer: set `UME_RAG_API_GATEWAY_KEY`, `UME_RAG_ADMIN_GATEWAY_KEY` and `UME_RAG_WORKER_GATEWAY_KEY`, remove the
  `UME_RAG_*GATEWAY_KEY_<level>` variables, update the stack with the new stack file (`rag-bootstrap` deletes the old
  files), then restart rag-api, rag-admin and rag-worker. **Check:** no service's log shows `gateway_key_missing` or a
  gateway `401`.
- **The gateway's Data API is no longer used (#580, B-35, A-30).** rag-admin syncs the organisation, its gateway keys
  and the models from the gateway's `GET /v1/ume/context` and `GET /v1/models` with its credential key, which needs the
  scope `organisation.read` (production-setup.md, *Gateway credential keys*). The Data API key
  (`secrets/admin/OrgSync__DataApiKey`), `UME_RAG_ORG_SYNC_URL` and rag-admin's `OrgSync__BaseUrl` are gone, and
  `init-deployment.sh` refuses `--org-sync-url` and `--org-sync-key-file`. **Apply:** have the gateway admin give the
  admin's credential key the scope `organisation.read` if it lacks it. Re-run `init-deployment.sh` with your options
  minus those two: it deletes the Data API key file and names it; ask the gateway admin to revoke that Data API key.
  Then `dc up -d --wait` (rag-admin is recreated without `OrgSync__BaseUrl`); the host firewall drops the Data API from
  `llm` on its next run (`sudo systemctl start rag-firewall.service` for now). Kubernetes: remove `OrgSync__BaseUrl`
  from your overlay's `ume-rag-admin-config`, re-run `init-deployment.sh` as above, then `create-secrets.sh` (the key
  leaves `ume-rag-admin-secrets`) and `kubectl rollout restart deployment/rag-admin`; the Data API's port can leave
  your egress rules. Portainer: remove `UME_RAG_ORG_SYNC_KEY` (and `UME_RAG_ORG_SYNC_URL` if set), drop `data` from
  `COMPOSE_PROFILES` unless the gateway's Data API serves something else, and update the stack with the new stack file
  (`rag-bootstrap` deletes the key file and logs it; the network `rag-dataapi` is no longer in the file); then restart
  rag-admin and ask the gateway admin to revoke the Data API key. **Check:** `secrets/admin/OrgSync__DataApiKey` is
  gone, and the admin UI's Gateway page (`GET /api/gateway/status`) shows a recent successful organisation sync with
  no `degraded` reason.
- **Migration `UploadScannedAt` (#535, B-47).** It adds `content."Uploads"."ScannedAt"` and lets the upload state
  be `unscanned`. Additive: the previous release runs against it. Files uploaded before the migration have no
  `ScannedAt`, although they were scanned; once the admin UI shows *Not scanned for malware* (later in this
  release), set it once after the migration: `UPDATE content."Uploads" SET "ScannedAt" = "UploadedAt" WHERE
  "ScannedAt" IS NULL AND "State" IN ('clean', 'indexed');`
- **Malware scan optional (#535, A-36, A-37).** The worker's `ClamAv:Mode` is `on` (default) or `off`, and
  `rag-clamav` now runs only with the Compose profile `clamav`, which `init-deployment.sh` writes into
  `COMPOSE_PROFILES` unless `--clamav off` (production-setup.md, *Malware scan*). An existing `deploy/.env` has no
  `clamav` profile yet: **without a re-run, `rag-clamav` does not start after the update, and since the worker is still
  `on` and fails closed, every upload and fetched file waits with `clamav.unavailable`.** **Apply (Compose):** re-run
  `init-deployment.sh` with your usual options (it adds the profile and `UME_RAG_CLAMAV_MODE=on`; add `--clamav off`
  only to store files without a malware scan, then also `dc rm -sf rag-clamav`), then `dc up -d --wait`. Later re-runs
  keep the mode in `deploy/.env`. Kubernetes: `rag-clamav` moved from `components/ingestion` to `components/clamav`;
  add `- ../components/clamav` to your overlay's components (as in `example/`), or, to run without the scan, set
  `ClamAv__Mode=off` in its `ume-rag-worker-config` and delete the old resources (kubernetes.md, *Malware scan*).
  Portainer: add `clamav` to `COMPOSE_PROFILES`, or set `UME_RAG_CLAMAV_MODE=off`, and
  update the stack with the new stack file. **Check:** `dc ps rag-clamav` is healthy (or absent with `--clamav off`,
  where the worker logs that the scan is off at its start).
- **The gateway's cost and budget read-back (#580, B-45, A-35), optional:** a gateway admin adds the scope `usage.read`
  to rag-admin's credential key, and the new `UME_RAG_GATEWAY_INTEGRATION_API_URL` in `deploy/.env` (rag-admin's
  `Gateway__IntegrationApiUrl`; Kubernetes: that key in `ume-rag-admin-config`; Portainer: the variable) names the
  gateway's Admin API; unset, there is no read-back (production-setup.md, *Cost and budget read-back*).
- **Rolling update: no**, because of `DailyTotalsWithoutCost` below. Five more migrations since 0.4.0, besides
  `UploadScannedAt` above; `rag-migrate` applies them and the grants in `app-roles.sql`, with no superuser steps:
  - `GatewayKeys` adds `config."GatewayKeys"`, the mirror of the integration's assigned keys that rag-admin syncs from
    the gateway's context (#580, B-35, B-36). Additive.
  - `GatewayModelLimitsSource` adds `config."GatewayModels"."LimitsSource"` (`catalog` | `manual`, default `manual`):
    the limits of a `catalog` row come from the gateway and can't be edited (B-35). Additive.
  - `DailyUsage` adds `chat."AssistantDailyUsage"` and `content."CollectionDailyUsage"`, the tokens and cost of every
    model call per day and step (B-38, A-31), and adds the first table to the chat retention step (3 years). Additive.
  - `ObjectGatewayKeys` adds `config."Assistants"."GatewayKeyId"` and `config."Collections"."GatewayKeyId"` (B-36), the
    key each one is charged to. Additive; an existing assistant or knowledge base has none until someone picks one.
  - `DailyTotalsWithoutCost` drops `CostUsd`, `InputTokens` and `OutputTokens` from `chat."AssistantDailyTotals"`
    (B-38): tokens and cost are only in `AssistantDailyUsage` now, and the totals of days before the upgrade lose them.
    The previous release's rag-api still writes those columns, so **stop rag-api and rag-admin before `rag-migrate`**
    and start the new release after it.

## 0.4.0

- **Rolling update: yes** from 0.3.0. Three migrations, all of which 0.3.0 runs against:
  - `ProfileIndexReset` adds `content.rag_reset_profile` and `content.rag_lock_profile_if_unchanged` (editable
    embedding profiles, #554).
  - `AnswerLengthFromModel` adds `config."GatewayModels"."MaxOutputTokens"` and removes `answer.maxOutputTokens` and
    `answer.contextTokens` from every assistant version's `QuerySteps`, published ones included. 0.3.0 then uses its
    defaults (16,384 and 1,024 tokens).
  - `KeywordStatistics` adds `content."KeywordStatistics"`.

  The new grants are in `app-roles.sql`, which `rag-migrate` applies. There are no superuser steps and no role changes.
- **Answer length now follows the model (B-31).** After the upgrade every assistant answers up to its model's output
  limit, in a context of the model's window. A model with no limit on the mirror gets no `max_tokens`, so the
  provider's default applies. **Apply:** enter *Context window* and *Longest output* for each chat model on Platform →
  Gateway models. Optionally, set a ceiling for every assistant under Settings → *Longest answer, all assistants*.
  Re-enter an override on an assistant's Draft tab only where one is wanted.
- **PostgreSQL settings (A-22):** `postgresql.conf` sets `jit = off`, `random_page_cost = 1.1` and
  `effective_io_concurrency = 200`. The file is bind-mounted, so postgres reads the new one only after a restart:
  `dc restart postgres` in the window (a short outage of every service), unless `dc up` recreates postgres for a new
  `POSTGRES_IMAGE`. On a two-slot installation it can wait for the next window: nothing depends on it.
  **Check:** `dc exec -T postgres psql -U postgres -d ragdb -Atc 'SHOW jit'` says `off`.
- **Keyword statistics (A-29):** the worker writes `content."KeywordStatistics"` after each finished run and in the
  nightly maintenance, and rag-api reads them (no `ts_stat` in rag-api any more, #499). The first check of a large
  collection reads its search vectors once, on the maintenance session. The worker checks every collection once when
  it starts, so after the upgrade each collection has its stop list within minutes (until then K runs with every
  term). Nothing to configure. **Check:** `SELECT count(DISTINCT "CollectionId") FROM content."KeywordStatistics"`
  is above 0 a few minutes after `dc up` on an installation with collections of at least 100 chunks.
- **New knobs**, all with defaults: `UME_RAG_ORGANISATION_NAME` (`Admin:OrganisationName`, default `Umeå kommun`),
  `Crawl:UserAgent` (default `UmeaKommunBot/1.0`), and `Settings.Chat.prompts` (prompt texts, built-in when absent).
- **Apply (Compose):** as for 0.3.0. Replace `deploy/` with the bundle's (keep `.env`, `certs/`, `secrets/`), set the
  image pins, re-run `init-deployment.sh` with your options (plus `--render` with `browser` sources, below), then
  `dc up -d --wait`, or `./deploy/rolling-update.sh --pull` with two slots. Then follow the notes below.
- **Check:** `dc ps` all healthy; `/version` of rag-api and rag-admin says 0.4.0; `dc logs rag-migrate` lists the three
  migrations; a question in an assistant's test panel shows the answer length it sent.

- **Telemetry and health checks:** traces are now sampled at 10 % of the requests that start at a service
  (`UME_RAG_OTEL_TRACES_SAMPLER_ARG`, `always_on` for all); logs and metrics are unchanged. The .NET containers' health
  probe runs every 30 s (every 5 s while starting) instead of every 15 s, and a container is unhealthy after 3 failed
  probes (90 s) instead of 5 (75 s). Nothing to configure.

- **Renderer behind a profile (#505):** `rag-renderer` (Chromium) now runs only with the Compose profile `render`, and
  the worker gets its `Render__Endpoint` only then; without them sources with fetch mode `browser` wait with
  `render.not_configured`. **Apply:** an installation with `browser` sources re-runs `init-deployment.sh` with its
  options plus `--render` (it keeps every secret, adds `COMPOSE_PROFILES=render` to `deploy/.env` and keeps
  `secrets/worker/Render__Endpoint`), then `dc up -d --wait`. Without `browser` sources, re-run it without `--render`
  (it removes the endpoint), then `dc up -d --wait`, `dc restart rag-worker` (it reads its secret files at start) and
  `dc rm -sf rag-renderer` (`up` leaves a running container of a profile that is now off alone). Kubernetes: `create-secrets.sh --renderer` now needs `init-deployment.sh --render`
  first. **Check:** `dc ps rag-renderer` is healthy with the profile, absent without it.

- **Idle job lanes (#505):** an idle worker no longer probes the queue every 2 s per job class: a lane waits for a
  `rag_jobs` notification, its class's next due job, or at most `Worker:IdleWaitSeconds` (30 s). `Worker:PollSeconds`
  is gone (a `Worker__PollSeconds` you set is ignored). A requeued job (a transient failure, a release, a reclaim) now
  notifies `rag_jobs` too. Nothing to configure.

- **Reverse proxy:** the api host (and Open WebUI's) now allows 2 MB request bodies, matching rag-api's chat route;
  with 1 MB, long conversations were refused by the proxy (SA-15). A generated `deploy/proxy/nginx.conf` doesn't
  change by itself: set `client_max_body_size 2m;` in its api (and Open WebUI) server block, or `proxy-body-size: 2m`
  on the `rag-api` ingress, then reload the proxy.

## 0.3.0

- **Rolling update: yes** from 0.2.0. No schema migration and no new grants or superuser steps; `rag-migrate` runs
  and finds nothing to apply.
- **Parser image:** legacy Word, Excel, PowerPoint and RTF uploads are converted with LibreOffice inside `rag-parser`
  (#526), so the image is larger. Nothing to configure.
- **New deployment options**, all opt-in; an existing single-slot Compose installation changes nothing:
  - Updates without downtime: `init-deployment.sh --rolling` and `deploy/rolling-update.sh`
    ([zero-downtime-updates.md](zero-downtime-updates.md)). The first rolling update stops slots that had no drain
    configured, so a few requests can fail at that moment.
  - Kubernetes: `deploy/kubernetes` ([kubernetes.md](kubernetes.md)).
  - Portainer, gateway and RAG in one stack: the release asset `compose.portainer.yaml` (runbook, *Portainer and other
    hosts without shell access*).
  - SIEM: `deploy/otel/collector-siem.example.yaml`; Keycloak setups: [keycloak.md](keycloak.md).
- **Apply (Compose):** replace `deploy/` with the bundle's (keep `.env`, `certs/`, `secrets/`), set the image pins, then
  `dc up -d --wait`, or `./deploy/rolling-update.sh --pull` with two slots.
- **Check:** `dc ps` all healthy; `/version` of rag-api and rag-admin says 0.3.0; an upload of a `.doc` file is
  indexed.

## 0.2.0

### Renderer (`rag-renderer`)

- **Rolling update: yes.** No migration; a new container in the ingestion zone and a new network.
- `compose.ingestion.yaml` adds `rag-renderer` (`images/renderer`, Chromium behind Playwright's `launch-server`) on
  the new internal network `render` (172.30.18.0/24), which `rag-worker` joins. `init-deployment.sh` generates the
  renderer's secret WebSocket path once (`secrets/.render-ws-path`) and writes `secrets/worker/Render__Endpoint` and
  `secrets/renderer.env`. The release bundle carries the renderer's seccomp profile as `images/renderer/seccomp.json`
  next to `deploy/`. The host firewall rejects the new subnet like the other internal ones.
- **Apply:** keep `172.30.18.0/24` free (production-setup.md, *Host prerequisites*); re-run `init-deployment.sh` with
  your options (it keeps every existing secret); a new `RENDERER_IMAGE` (or a built `ume-rag/renderer:latest`); then
  `dc up -d --wait` and `sudo systemctl start rag-firewall.service`.
- **Check:** `dc ps rag-renderer` is healthy (a restart loop with "No usable sandbox" in `dc logs rag-renderer`: the
  host restricts user namespaces, production-setup.md, *Renderer*), and the firewall's dry run
  (`deploy/host/docker-user-firewall.sh --dry-run`) rejects `172.30.18.0/24` in `UME-RAG-INPUT`.

### ClamAV 1.4.6

- **Rolling update: yes.** No migration and no configuration change.
- `images/clamav/Dockerfile` moves from `clamav/clamav-debian:1.4.5_base` to `1.4.6_base`, which fixes
  CVE-2026-20339, CVE-2026-20345 and CVE-2026-20346. Its only image was pushed on 2026-10-26; it is pinned for
  `v0.1.0` (2026-10-31, #27; the 7-day rule does not apply during development). With 1.4.5 freshclam logged "Your ClamAV
  installation is OUTDATED".
- **Apply:** a new `CLAMAV_IMAGE` (or a rebuilt `ume-rag/clamav:latest`), then `dc up -d --wait rag-clamav`. The
  signatures in the `clamav-db` volume are reused. Nothing is scanned while clamd restarts.
- **Check:** `dc ps rag-clamav` is healthy, and `dc logs rag-clamav` no longer warns that the installation is outdated.

### Open WebUI: the feedback service account

- **Rolling update: yes.** Only the optional Open WebUI stack changes; the main stack is untouched.
- `compose.openwebui.yaml` turns password sign-in off (`ENABLE_PASSWORD_AUTH`, on only while
  `deploy/openwebui-feedback-key.sh` runs) and gives every OIDC sign-in the role `user` (`DEFAULT_USER_ROLE`; it was
  `pending`). `init-deployment.sh --openwebui-host` adds the feedback service account to `secrets/openwebui.env`.
- **Apply** (only where Open WebUI runs): re-run `init-deployment.sh` with your options, then
  `docker compose -f deploy/compose.openwebui.yaml --env-file deploy/.env up -d --wait`, then
  `sudo deploy/openwebui-feedback-key.sh` and `dc restart rag-api`. On a store that already had users the account is
  not created: production-setup.md, *Open WebUI*, says how to add it.
- **Check:** the script ends with `ok: password sign-in refused (403)`.

## Upgrade procedure (single slot)

1. **Read** this guide's section for every release between yours and the target: *Rolling update*, migrations, new
   grants, superuser steps, settings. Review the release's image digests, SBOM and advisories.
2. **Announce** the window. `rag-api` and `rag-admin` are replaced together: the admin's test panel and answer judge run
   the same answering code as the api.
3. **Back up and check the escrow:**

   ```bash
   sudo deploy/backup-postgres.sh --type full --start-fast
   sudo deploy/escrow-keys.sh --verify /media/<escrow>
   ```

   Note the time: with the WAL archive, any later moment is restorable too (runbook, *Restore*).
4. **New files and images.** Replace `deploy/` with the release's (keep `deploy/.env`, `deploy/certs` and
   `deploy/secrets`), and set the image pins in `deploy/.env` (below). Re-run `sudo deploy/host/install.sh` when
   `deploy/host/` changed.
5. **Superuser steps**, only if the release lists them (below), all before step 6. Role caps and per-role settings
   run against the running database. With a new `POSTGRES_IMAGE`, first replace postgres alone with
   `dc up -d --wait postgres` (a short outage of every service), then update the extension.
6. **Migrate and replace:**

   ```bash
   dc up -d --wait
   ```

   Compose recreates every container whose image or settings changed. `rag-migrate` runs first; api, admin and the
   worker start only after it has finished successfully, so no service runs against an older schema. The worker is
   stopped before its replacement starts (its leases make the pause harmless; an overlap would exceed its role's
   connection limit). The parser and ClamAV are recreated only if their image changed.
7. **Verify:** `dc ps` all healthy, `/health/ready` and `/version` of api and admin, `dc logs rag-migrate` shows the
   expected migration, a test question in the admin's test panel (once it exists), and the backup-status metrics are
   back to normal within a few minutes.
8. **Record** the release, the image digests, the schema migration and the test evidence in change management.

**Rolling back.** An image rollback is safe only when the previous version supports the new schema (a release with
*Rolling update: yes*). Never run a destructive down-migration: restore the backup of step 3 in place
(`restore-pitr.sh`, runbook) and start the previous images.

## Image pins

Production runs images pinned by digest. A release bundle (from `v0.1.0`) has each digest written into the Compose
files' `${X_IMAGE:-…}` defaults by `deploy/build-release-bundle.sh`, which refuses an unpinned image; in a clone the
defaults are the local `ume-rag/<service>:latest` images built on the host. To run images from a registry, or to override one, set the variable in `deploy/.env` to `<image>@sha256:…`:

| Variable | Image |
|---|---|
| `POSTGRES_IMAGE` | `ume-rag/postgres` (pinned `pgvector/pgvector` plus pgBackRest) |
| `MIGRATIONS_IMAGE`, `API_IMAGE`, `ADMIN_IMAGE`, `WORKER_IMAGE` | the .NET services |
| `PARSER_IMAGE` | the parser |
| `CLAMAV_IMAGE` | `ume-rag/clamav` |
| `OPENWEBUI_IMAGE` | Open WebUI, in the separate `compose.openwebui.yaml` project |

Every pin, image or package, was released at least 7 days before it was pinned.

## Superuser steps

The migrator (`ume_rag_migrator`) owns ragdb but is not a superuser and cannot create or alter roles, so two kinds of
change need the bootstrap superuser. They run inside the postgres container over its socket, as the
`postgres` user (peer authentication):

```bash
dc exec -T postgres psql -X -v ON_ERROR_STOP=1 -U postgres -d ragdb -c '<statement>'
```

**Role caps and per-role settings.** Connection limits, timeouts and the `hnsw.*` defaults of the service roles are
set only by `deploy/postgres/init.sql`, which runs once, when the database is created. `app-roles.sql`, which the
migration service applies on every run, re-grants table privileges but cannot change a role. A release that changes a
cap or a per-role setting lists the statements here; run them on existing databases before `dc up`, for example:

```sql
ALTER ROLE ume_rag_api CONNECTION LIMIT 26;
ALTER ROLE ume_rag_api IN DATABASE ragdb SET statement_timeout = '5s';
```

New sessions pick up a per-role setting; restart the service to make all its connections use it. Check the current
values with `SELECT rolname, rolconnlimit FROM pg_roles WHERE rolname LIKE 'ume_rag_%'` and `\drds`.

**Extensions.** A new `POSTGRES_IMAGE` with a newer pgvector needs `ALTER EXTENSION vector UPDATE;` once the new
image runs (`dc up -d --wait postgres`) and before the migrations. Recreating postgres restarts the database: a short
outage of every service.

## Change types

| Change | Procedure |
|---|---|
| pgvector minor | new `POSTGRES_IMAGE`; extension step above |
| Postgres major (17 → 18) | not rolling: `pg_upgrade` where possible (else `pg_dump`/`pg_restore` into a new volume), then a new pgBackRest stanza and a full backup |
| Word list or decompounder | changes the stored search text: the release ships a worker command that recomputes it in batches; the evaluation smoke set is compared before and after |
| Parser image (Docling, models) | the parse cache key includes the parser build, so old outputs stay valid; re-parsing is an opt-in publish |
| Embedding profile | never a migration: a background re-embed with the evaluation report |

None of these has happened yet; the first release that brings one adds its commands to its section above.

## Certificate and secret rotation

- **Internal certificates** (825 days): `sudo ./deploy/init-deployment.sh --renew-certs` with `ca.key` back on the
  host, then `dc up -d --force-recreate`, and move `ca.key` off again. `--renew-ca` replaces the CA as well.
- **PKI certificates** of the public listeners: run `init-deployment.sh` again, with all your original options and the
  new `--api-cert`, `--api-key`, `--admin-cert` and `--admin-key`, then recreate api and admin.
- **Gateway credential keys and the OIDC client secret:** run `init-deployment.sh` again, with all your original options
  and the new `--gateway-credential-key-file SERVICE=FILE` or `--oidc-client-secret-file`; it rewrites those files.
  Restart the services that read them. A credential key is rotated in the gateway with its 24-hour grace period first
  (production-setup.md, *Gateway credential keys*).
- **Never regenerate** the pepper, the Data Protection certificate, the blob master key or the pgBackRest passphrase:
  existing data or backups would become unreadable. There is no rotation procedure for them yet; after any future
  rotation, escrow again (runbook, *Key escrow*).
- A re-run without `--renew-certs` rewrites `deploy/.env` and keeps the old one as `deploy/.env.bak`: carry over the
  lines you added by hand (image pins, `UME_RAG_OTEL_ENDPOINT`).
