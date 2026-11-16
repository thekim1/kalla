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

- **Rolling update: yes** for Portainer and Kubernetes. No migration, no new variable, nothing to do.
- **Compose:** `init-deployment.sh` asks for no sign-in value: sign-in is set in the setup wizard
  (`https://<admin host>/setup`, the setup code in rag-admin's log), as on Portainer. Only with `--hardened` does an
  interactive run (no `--yes`) ask for the sign-in provider's address when neither `--oidc-authority` nor `deploy/.env`
  gives one: leave it empty to keep the provider in the setup wizard, or type it so the host firewall lets rag-admin
  reach it. New and optional: `--gateway-secrets-dir`, which reads the three credential keys from a gateway on the same
  host ([quick start](compose-quick-start.md)).
- **Kubernetes:** `create-secrets.sh` now generates the certificates and secrets itself, with the bootstrap of the
  Compose and Portainer stacks, and no longer reads what `init-deployment.sh` wrote. Run it on your existing
  `deploy/certs` and `deploy/secrets` with your host names (`--domain`, or `--api-host` and `--admin-host`): it keeps
  the CA, the passwords, the keys, the pepper, the Data Protection certificate and the credential keys, and issues
  the five service certificates again from the same CA (have `deploy/certs/ca.key` in place), so restart the
  deployments afterwards. The options that `init-deployment.sh` took for Kubernetes are now its own
  ([Kubernetes](kubernetes.md), *Install*); with your own database, pass `--postgres-host` instead of editing the
  connection strings.
- **The prerequisite was stated too low:** the Compose files need Docker Compose 2.33.1 or later (`gw_priority`), as
  they have since they pinned each service's default route; the guides said 2.24.
- **Open WebUI is off unless asked for.** `stack.env.example` no longer has `openwebui` in `COMPOSE_PROFILES`, and
  without the profile `rag-bootstrap` generates nothing for Open WebUI and removes rag-api's `OpenWebUi__JwtSecret`. **A
  stack that has the profile keeps it: nothing to do.** A stack that ran without the profile changes in one way:
  rag-api no longer has a key to check Open WebUI's user tokens with, which nothing used. Turning the profile on later
  generates what is missing; turning it off keeps Open WebUI's store and keys for the next time.
- **Portainer: nothing changes.** The stack file is generated from `deploy/compose.rag.yaml` now, with the same
  services, volumes, networks and variables; update the stack as for any release.
- **Compose: no in-place upgrade; install again.** Compose runs `deploy/compose.rag.yaml` with the Portainer stack's
  names (`rag-postgres`, the volumes `rag-postgres-data`, `rag-certs`, `rag-secrets`, `rag-config`, the `RAG_*_IMAGE`
  variables), and `rag-bootstrap` keeps the certificates and secrets in volumes instead of `deploy/certs` and
  `deploy/secrets`. Its published ports bind `0.0.0.0` by default, as the Portainer stack's (before: `127.0.0.1`).
  No Compose installation exists outside tests, so there is no migration. To carry an installation's data over, keep
  before the new install: the database volume `ume-rag_postgres-data` with the role passwords that
  open it (`deploy/secrets/postgres/*_password`), `deploy/secrets/.pepper`, `deploy/secrets/keys/rag-blob-internal`
  and `deploy/certs/data-protection.pfx` (without these three the database is unusable), and the `ume-rag_rag-blobs`
  volume. Then run the new `init-deployment.sh` (`--help`) and, before the first `up`, copy the old database volume's
  contents into `ume-rag_rag-postgres-data` and the kept files to the same paths in the `rag-secrets` volume
  (`.pepper`, `postgres/`, `keys/`) and the `rag-certs` volume (`data-protection.pfx`), owned as `rag-bootstrap` leaves
  them; `up` then runs `rag-bootstrap`, which keeps them and copies them to each service. **This carry-over
  is written from how `rag-bootstrap` treats existing files and has not been run**: try it on a copy first.

## 0.7.0

- **Rolling update: yes** from 0.6.0. Migrations `IdentityOidcSettings`, `SetupMode` and `OpenWebUiSignInSettings` only
  add; Open WebUI is recreated once. The Portainer stack moves the gateway to `v0.11.0`, also rolling.
- **Before the update:** nothing must be added or changed. A deployment that sets its authority and
  `UME_RAG_OIDC_CLIENT_SECRET` never sees a setup wizard. Only a Portainer stack **without**
  `UME_INTEGRATION_MANIFEST` (keys issued by hand) decides first whether the gateway may create three new credential
  keys from the stack file's default, or sets the variable empty (below, and `docs/portainer.md`, *Upgrade from 0.6.0*).
- **After the update:** read the end of `rag-bootstrap`'s log (a retired variable the stack still sets is named
  there), and check that you sign in as before.
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
    2. Re-run `init-deployment.sh` with your options. The `--oidc-*` options are optional and, in 0.7.0, never prompted; a re-run
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
    (Portainer: `UME_RAG_OIDC_CLIENT_SECRET`, and Open WebUI's `UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET`, which is then
    entered in the wizard's Open WebUI step; Compose: delete
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
  still sets a variable this version no longer reads (the ones the sections below say to remove, back to 0.5.0's; the
  per-level `UME_RAG_*GATEWAY_KEY_<level>` variables of 0.4.0 and before are not named) works
  as before; `rag-bootstrap`'s checklist now names each with what replaced it, and Home in the admin UI shows platform
  admins a notice with the names. **Apply:** nothing is required; to clear the notice, remove the named variables from
  the stack's variables and update the stack. **Check:** `rag-bootstrap`'s log has no line *is set and no longer read*.
- **One typed value in the Portainer stack, `UME_DOMAIN`, and the gateway's own setup wizard (M-20 S8).** The stack pins
  the gateway's `v0.11.0`: **rolling update: yes**, its migrations `SignInSettings` and `SetupMode` add tables,
  columns and grants (the gateway's upgrade guide, *Portainer*). No variable has to be added and none is retired (the
  gateway's new `UME_SETUP_REOPEN` is optional).
  - **Host names:** the gateway's two default under `UME_DOMAIN` as Ume.Rag's three do (`llm.<domain>`,
    `llm-admin.<domain>`), and its public URL to `https://<gateway host>`. rag-admin links to the gateway's admin
    (among them the setup wizard's link to the gateway's wizard) at `UME_ADMIN_PUBLIC_URL`, else
    `https://<gateway admin host>`. Each `UME_*_HOST` and public URL that is set still wins.
  - **Sign-in:** `stack.env.example` no longer sets `UME_OIDC_AUTHORITY`. Unset, the gateway's admin opens its own
    setup wizard (the code is in `adminapi`'s log; `UME_SETUP_REOPEN` opens it again) and rag-admin has no declared
    authority: its wizard suggests the provider saved in the gateway's. So a new stack sets up the gateway's wizard
    first, then Ume.Rag's. Set, `UME_OIDC_AUTHORITY` declares the provider for both, as before, and the gateway's
    wizard stays closed; `UME_OIDC_CLIENT_SECRET` is used only together with it.
  - **The integration manifest:** the stack file gives the gateway's `UME_INTEGRATION_MANIFEST` a default,
    `deploy/portainer/integration-manifest.json` (the integration `Ume.Rag` and its three credential keys, every key
    limited to `OnPrem` and `Eu`, no owner). The gateway places an integration without an owner in a team and
    department of its own, code `ume-rag`, which a gateway admin moves to another team with its keys. Unset, the
    default applies; set, the stack's own manifest wins (0.6.0's, with an owner: nothing changes); set and empty,
    nothing is provisioned.
  - **Portainer apply:** step by step in [portainer.md](portainer.md#upgrade-from-060). A stack that sets
    `UME_OIDC_AUTHORITY` and `UME_INTEGRATION_MANIFEST` (every 0.6.0 stack made from its `stack.env.example`) changes
    nothing: paste the new file and update the stack. **A stack without `UME_INTEGRATION_MANIFEST`** (keys issued by
    hand, kept from 0.5.x) gets the default from now on: the gateway creates the keys `rag-api`, `rag-admin` and
    `rag-worker` in the integration `Ume.Rag` where they don't exist (and the integration, in a team `ume-rag`, if none
    has that name), writes their secrets to `<stack>_integration-secrets`, and changes no existing key. The services
    keep the keys their `UME_RAG_*_GATEWAY_KEY` variables set, which win, so the new keys stay unused. To create none,
    add `UME_INTEGRATION_MANIFEST` with an empty value before the update; or revoke the three afterwards (a revoked key
    is not created again). Optional afterwards: set `UME_DOMAIN` and remove the host and public URL lines whose value
    is its default.
  - **Compose and Kubernetes:** nothing. This change touches only the Portainer stack; a gateway deployed with its own
    Compose bundle or on Kubernetes follows the gateway's upgrade guide for `v0.11.0`, which keeps a deployment
    that sets the authority as it is.
  - **Check:** the gateway's `bootstrap` log has `bootstrap: sign-in to the admin console is declared by the
    stack's variables (UME_OIDC_AUTHORITY), …` (an existing stack) or `… is set in the setup wizard: …` (a new one);
    `adminapi`'s log has no `Setup code` line for a declared stack; `migrations`' log says
    `… 'Ume.Rag' applied, 0 of 3 credential keys created` for a stack whose keys exist.

## 0.6.0

- **Rolling update: no** from 0.5.x. Migration `RemoveWorkerConcurrency` drops a column the old rag-admin still reads:
  stop the old rag-admin before `rag-migrate` runs, then start the new one (rag-api and rag-worker are not affected).
- **Before the update:** give rag-api's and rag-admin's credential keys the residencies `OnPrem` and `Eu` in the
  gateway (C9, below). **Portainer:** paste the `v0.6.0` release's `compose.portainer.yaml` (gateway v0.10.3, Ume.Rag 0.6.0) and add
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
  profile under *Models → Embedding profiles → Create profile* ([portainer.md](portainer.md) step 9).
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
- **The Portainer stack pins the gateway's `v0.10.3`.** Against `v0.10.2` (next entry): rolling update, no migration,
  no new variable; the only change is the gateway's Ume.Rag preset (*Integrations → Add from manifest*), whose `rag-api` and `rag-admin` keys now
  allow only `OnPrem` and `Eu`, as the stack's `UME_INTEGRATION_MANIFEST` already does. Keys made from the `v0.10.2`
  preset keep their residencies: set them to `OnPrem` and `Eu` in the gateway's admin (0.6.0, *Before the update*).
- **The gateway creates Ume.Rag's credential keys (M-17 C7; the gateway's `v0.10.2`, in the stack's `v0.10.3`).**
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

## Before 0.5.0

A deployment older than 0.5.0 first moves to 0.7.0 with that release's copy of this guide, which has the sections for
0.2.0 to 0.5.0: [`docs/upgrade-guide.md` at `v0.7.0`](https://github.com/thekim1/ume-rag-platform/blob/v0.7.0/docs/upgrade-guide.md).

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
4. **New files and images.** Replace `deploy/` with the release's (keep `deploy/.env`; the certificates and secrets
   are in the Docker volumes), and remove or update any image pins you set in `deploy/.env` (below). Re-run
   `sudo deploy/host/install.sh` when `deploy/host/` changed.
5. **Superuser steps**, only if the release lists them (below), all before step 6. Role caps and per-role settings
   run against the running database. With a new `RAG_POSTGRES_IMAGE`, first replace rag-postgres alone with
   `dc up -d --wait rag-postgres` (a short outage of every service), then update the extension.
6. **Migrate and replace.** For a release marked *Rolling update: no*, first stop the services its section names
   (`dc stop rag-api rag-admin`, or `dc stop rag-admin` for 0.6.0): `up` runs the migration while the old containers
   are still running, and replaces them only afterwards.

   ```bash
   dc up -d --wait
   ```

   Compose recreates every container whose image or settings changed. `rag-bootstrap` runs first, then `rag-migrate`;
   api, admin and the worker start only after it has finished successfully, so no service runs against an older schema. The worker is
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
files' `${RAG_X_IMAGE:-…}` defaults by `deploy/build-release-bundle.sh`, which refuses an unpinned image; in a clone the
defaults of `deploy/compose.rag.yaml` are the last release's pins. To run images from a registry or built on the host,
or to override one, set the variable in `deploy/.env` (`init-deployment.sh --set RAG_API_IMAGE=<image>@sha256:…`;
`--set RAG_API_IMAGE=` removes it again):

| Variable | Image |
|---|---|
| `RAG_POSTGRES_IMAGE` | `ume-rag/postgres` (pinned `pgvector/pgvector` plus pgBackRest) |
| `RAG_MIGRATIONS_IMAGE`, `RAG_API_IMAGE`, `RAG_ADMIN_IMAGE`, `RAG_WORKER_IMAGE` | the .NET services |
| `RAG_PARSER_IMAGE` | the parser |
| `RAG_CLAMAV_IMAGE` | `ume-rag/clamav` |
| `RAG_RENDERER_IMAGE` | `ume-rag/renderer` |
| `OPENWEBUI_IMAGE` | Open WebUI, the profile `openwebui` (pinned in `deploy/compose.rag.yaml`) |
| `RAG_PROXY_IMAGE` | nginx of the TLS proxy, the profile `proxy` (pinned in `deploy/compose.rag.yaml`) |

Every pin, image or package, was released at least 7 days before it was pinned.

## Superuser steps

The migrator (`ume_rag_migrator`) owns ragdb but is not a superuser and cannot create or alter roles, so two kinds of
change need the bootstrap superuser. They run inside the rag-postgres container over its socket, as the
`postgres` user (peer authentication):

```bash
dc exec -T rag-postgres psql -X -v ON_ERROR_STOP=1 -U postgres -d ragdb -c '<statement>'
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

**Extensions.** A new `RAG_POSTGRES_IMAGE` with a newer pgvector needs `ALTER EXTENSION vector UPDATE;` once the new
image runs (`dc up -d --wait rag-postgres`) and before the migrations. Recreating rag-postgres restarts the database: a
short outage of every service.

## Change types

| Change | Procedure |
|---|---|
| pgvector minor | new `RAG_POSTGRES_IMAGE`; extension step above |
| Postgres major (17 → 18) | not rolling: `pg_upgrade` where possible (else `pg_dump`/`pg_restore` into a new volume), then a new pgBackRest stanza and a full backup |
| Word list or decompounder | changes the stored search text: the release ships a worker command that recomputes it in batches; the evaluation smoke set is compared before and after |
| Parser image (Docling, models) | the parse cache key includes the parser build, so old outputs stay valid; re-parsing is an opt-in publish |
| Embedding profile | never a migration: a background re-embed with the evaluation report |

None of these has happened yet; the first release that brings one adds its commands to its section above.

## Certificate and secret rotation

- **Internal certificates** (825 days): `rag-bootstrap` re-issues a service certificate at the first `dc up -d` within
  30 days of its expiry (it needs `ca.key` in the `rag-certs` volume), and the end of its log names what to restart:
  `dc restart rag-postgres rag-parser`. A new internal CA: delete `ca.pem` and `ca.key` in `rag-certs`,
  `dc up -d --wait`, then `dc restart` (production-setup.md, *Internal TLS*).
- **PKI certificates** of the public listeners: put the new `api.crt` and `api.key`, `admin.crt` and `admin.key` in
  the directory `UME_RAG_PKI_DIR` names and run `dc up -d`: `rag-bootstrap` copies them at every start. rag-api and
  rag-admin serve theirs through Kestrel, which watches the files; if a client still sees the old certificate, restart
  them.
- **Gateway credential keys and the OIDC client secret:** run `init-deployment.sh` with only the new
  `--gateway-credential-key-file SERVICE=FILE` or `--set-file UME_RAG_OIDC_CLIENT_SECRET=FILE` (it keeps the rest of
  `deploy/.env`), then `dc up -d`: `rag-bootstrap` rewrites those files. Restart the services that read them. A credential key is rotated in the gateway with its 24-hour grace period first
  (production-setup.md, *Gateway credential keys*).
- **Never regenerate** the pepper, the Data Protection certificate, the blob master key or the pgBackRest passphrase:
  existing data or backups would become unreadable. There is no rotation procedure for them yet; after any future
  rotation, escrow again (runbook, *Key escrow*).
- A re-run of `init-deployment.sh` changes only what its options name and keeps every other variable in
  `deploy/.env` (image pins and `UME_OTEL_ENDPOINT` included); only comments and empty values are dropped.
