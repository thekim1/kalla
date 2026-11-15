# Setup wizard: sign-in and the last deploy values set in the browser

**Status:** **proposed** (main session, session 40, 2026-11-15). The user answered the ten questions W1–W10 (§3) the
same day; the design that follows from them (§4–§8) waits for the user's agreement. Nothing here binds until
Decisions "Deploy configuration" is changed (§9), and no code is written before that.

**Goal** (the user's idea, 2026-11-15): the platform is easy to set up. What a deployer still types into Portainer's
stack variables, `deploy/.env` or the Kubernetes values moves into a first-run wizard in the admin UI wherever that is
sound.

**Builds on** [`deploy-configuration.md`](deploy-configuration.md) (M-17), which took the Portainer stack from 48
variables and three paste round trips to one pass. This proposal is about the values its §2.1 kept.

## 1. What is left to type today

`deploy/portainer/stack.env.example` at v0.6.0 marks **11 lines CHANGE**, and one more value hides inside a line:

| Whose | Values |
|---|---|
| The gateway (4) | `UME_OIDC_AUTHORITY` (shared with Ume.Rag and Open WebUI), `UME_GATEWAY_HOST`, `UME_GATEWAY_PUBLIC_URL`, `UME_ADMIN_HOST` |
| Ume.Rag (3) | `UME_RAG_API_HOST`, `UME_RAG_ADMIN_HOST`, `UME_RAG_OIDC_CLIENT_SECRET` |
| Open WebUI (2 + the client id line) | `UME_RAG_OPENWEBUI_HOST`, `UME_RAG_OPENWEBUI_OIDC_CLIENT_SECRET`, `UME_RAG_OPENWEBUI_OIDC_CLIENT_ID` |
| Inside `UME_INTEGRATION_MANIFEST` | the owner department's code (`departmentCode`), edited inside one line of JSON |

So: five host names, one URL that repeats a host name, one sign-in provider address, two client secrets (three with a
confidential `ume-admin`), one department code. Two of them are secrets typed into Portainer's variable list, where
they stay readable.

**Checked in the code and the stack** (2026-11-15, `main` at `de0c3a41`):

- Every service reads its secrets directory as key-per-file with reload on change
  (`InfrastructureExtensions.cs:43`), and the settings row notifies every service of a change.
- rag-admin copies `Oidc:Authority`, `ClientId` and `ClientSecret` into the OpenID Connect handler's options once
  (`AdminAuthentication.cs:206`), so a change needs the cached options dropped, or a restart.
- The TLS proxy verifies its upstreams by their internal names (`proxy_ssl_name rag-api`,
  `deploy/proxy/nginx.conf.example`), so the public host names in the internal certificates' SANs
  (`bootstrap.sh:51-52`) serve only a client that reaches a service past the proxy.
- Open WebUI gets `OAUTH_CLIENT_ID`, `OAUTH_CLIENT_SECRET`, `OPENID_PROVIDER_URL`, `WEBUI_URL` and
  `OPENID_REDIRECT_URI` from its environment, with `ENABLE_PERSISTENT_CONFIG=false`; it already starts through a wrapper
  that exports three secrets from files (template line 471).
- `rag-bootstrap` runs with `network_mode: none`: it can't read the database, so it can't know whether setup is done.

## 2. How other products do it

A survey of 14 self-hosted products, from their source and official documentation (Grafana, GitLab, Nextcloud,
Portainer, Keycloak, Home Assistant, Open WebUI, Dify, Onyx, RAGFlow, Authentik, Gitea, Mattermost, Harbor). The
points that shaped the answers; three were re-checked by the main session in the fetched files (Portainer's setup
token, Keycloak's localhost rule, Onyx's last-provider guard):

- **First-run access comes in three forms.** An open race, where the first visitor wins (Nextcloud, Gitea, Home
  Assistant, Open WebUI, Onyx, Authentik, Mattermost). A deploy-time secret (Grafana's `admin`/`admin`, Harbor,
  Authentik's bootstrap variables). Proof of host access: GitLab's password file, RAGFlow's password file, Keycloak's
  localhost-only page, and Portainer, whose `develop` branch adds a 256-bit setup token printed in the start-up log
  (`X-Setup-Token`) on top of its 5-minute window.
- **The newest code tightens the open race** (Portainer, RAGFlow, Keycloak's temporary bootstrap admin).
- **Re-opening always needs host access**: a CLI in the container, a helper container, a file. Never the network.
- **Every product with sign-in settings in the UI keeps a way back in.** Mostly a local admin password. Onyx alone
  enforces it server-side: it refuses to remove the last working sign-in method.
- **A test before saving is rare**: Harbor's *Test OIDC Server*, Mattermost's *Test Live URL*, Onyx's discovery fetch
  on save. None requires a real sign-in round trip.
- **Variables against stored values, three camps:** the variable wins and the field is locked (Mattermost, Harbor,
  Gitea); the variable seeds the first start and is ignored afterwards (Open WebUI, whose documentation has a
  troubleshooting section for it); the stored value overrides and the variable is the fallback (Onyx, Grafana's SSO).
- **The public URL is deploy-time almost everywhere**; Mattermost is the exception.
- **Signing and encryption keys are never editable in a UI.**

## 3. Decided by the user (2026-11-15)

Each was put as four alternatives with a recommendation; the user took the recommendation in all but W2.

| # | Question | Answer |
|---|---|---|
| W1 | Which products | **The whole stack, Ume.Rag first:** one design for the gateway, Ume.Rag and Open WebUI; Ume.Rag's and Open WebUI's part is built first, the gateway's part is issues in its repository |
| W2 | Which deploy forms | **All three:** Portainer, Compose and Kubernetes (the recommendation was Portainer only) |
| W3 | Who may use the wizard before sign-in works | **A setup code from the log:** reading it proves host access; the wizard is open only while no working sign-in is stored and locks itself when done |
| W4 | Lock-out | **A test sign-in before saving, and the setup code re-opens:** no passwords; after setup the sign-in provider changes only through the wizard with a new code, never from an ordinary admin session |
| W5 | A value set both by the deployment and in the wizard | **The deployment wins and the field is locked**, shown as *set by the deployment* |
| W6 | Where wizard values live | **The database:** plain values as settings, secrets encrypted with the Data Protection certificate |
| W7 | Host names | **One base domain variable** (`UME_DOMAIN`); the five names default under it, each still overridable. They stay deploy-time |
| W8 | Open WebUI | **Its wrapper fetches its sign-in settings from rag-api** at start and exits when they change |
| W9 | The crawler's private ranges | **Stay deploy-time** (Decisions "Deploy configuration", D4 unchanged); the wizard shows the value read-only |
| W10 | How a saved value takes effect | **Reload in place**; only Open WebUI restarts itself |

## 4. The design

### 4.1 What the wizard sets

| Value | Today | After |
|---|---|---|
| Sign-in provider address | `UME_OIDC_AUTHORITY` / `UME_RAG_OIDC_AUTHORITY` | `Settings.Identity.Oidc.Authority`; in a shared stack the default comes from the gateway (§6, WG2) |
| rag-admin's client id | `UME_RAG_OIDC_CLIENT_ID` (default `ume-rag-admin`) | `Settings.Identity.Oidc.ClientId`, same default |
| rag-admin's client secret | `UME_RAG_OIDC_CLIENT_SECRET` → `admin/Oidc__ClientSecret` | an encrypted setting (§4.5) |
| Token shape: role, department, team and group claims, extra scope, tenant id, refuse guests | seven `UME_RAG_OIDC_*` variables, defaults fit Keycloak | `Settings.Identity.Oidc.*` under *Advanced*, with a preset per provider (Keycloak, Entra ID) |
| The break-glass platform-admin groups | `UME_RAG_OIDC_GROUPS_PLATFORM_ADMIN` | the same variable (it stays a deployment value, Decisions "Role groups as a setting"); the wizard shows it and the test sign-in says whether it applied |
| Open WebUI's provider address, client id, client secret, provider name | four `UME_RAG_OPENWEBUI_OIDC_*` variables | `Settings.OpenWebUi.Oidc.*`, the secret encrypted; the provider address defaults to rag-admin's |

**Not in the wizard:** host names (W7), ports and binds, profiles, image pins, telemetry, the gateway's address, the
crawler's private ranges (W9), every generated secret and key. The wizard's last page lists them read-only with their
source, so the deployer sees the whole configuration in one place.

### 4.2 Setup mode and the setup code (W3)

- **Setup is open** when rag-admin has no effective sign-in configuration: neither the deployment nor the database
  gives an authority, a client id and a client secret. It is also open after a re-open request (§4.4).
- **The code.** While setup is open, rag-admin makes a random code (128 bits, shown in groups for typing), stores its
  hash and an expiry (24 hours) in the database, and writes the code once to its log, with the wizard's address. A
  second replica finds the hash and logs that another instance holds the code. A restart after the expiry makes a new
  one. The deployer reads it in Portainer's container log, `docker logs` or `kubectl logs`: the same step on all three
  deploy forms, with nothing typed into the stack (`rag-bootstrap` can't be the source: it can't see the database, and
  would print a secret on every deploy).
- **The wizard** is the admin UI's `/setup` route, served before sign-in. Entering the code gives a setup session (a
  cookie, 30 minutes, the same antiforgery as the BFF). Attempts are rate-limited per address and in total, and a run
  of failures voids the code until a restart. While setup is closed, `/setup` and its API answer 404.
- **Locking** is derived from state, as most surveyed products do: once a tested sign-in is saved, setup is closed and
  the code's hash is deleted. No flag to forget.
- **Audit:** opening, each failed code, each test and the save are security events, with the address.

### 4.3 The test sign-in (W4)

Saving the sign-in values requires a passed test, made with the values as typed (not yet saved):

1. rag-admin fetches the provider's discovery document and says what is wrong in plain words (not reachable, not a
   discovery document, the certificate isn't trusted).
2. **Test sign-in** opens the provider in a new window with the candidate values and the redirect URI rag-admin
   already uses, so nothing extra is registered at the provider. The deployer signs in as themselves.
3. rag-admin exchanges the code with the candidate client secret and runs the same checks as a real sign-in (tenant,
   guests, claims). The wizard shows the result: who signed in, the roles and groups the token carried, the departments,
   and **whether this person would be a platform admin**.
4. **Save** is offered only when the test passed and the tested person is a platform admin. That removes today's
   separate step "the first platform admin needs the role `rag-platform-admin`": the wizard says so before anything is
   locked, and names the claim it looked in.

The values are then in force (§4.6), and the wizard hands over to the normal sign-in and the *Get started* checklist
(the embedding profile is next).

### 4.4 Re-opening: a sign-in that broke, or a planned change (W4)

- After setup, no page and no API of an ordinary admin session changes the sign-in provider, the client or its secret.
  A stolen platform-admin session can't move sign-in to another provider.
- **To re-open**, the deployer sets `Setup:Reopen` (`UME_RAG_SETUP_REOPEN`) to any new value, for example today's
  date, and updates the stack. rag-admin compares it with the last value it consumed (in the database); a new value
  opens setup with a new code in the log, and closing setup consumes it. Changing a stack variable is the same proof of
  host access as reading the log, needs no shell in the container, and works on all three deploy forms.
- While re-opened, the existing sign-in keeps working; the wizard shows the stored values (never a secret) and the
  same test applies before a save.
- **A client secret that is about to expire** is the common case: Home shows a notice to platform admins when the
  provider reports an expiry (Entra ID does; Keycloak's secrets don't expire), with these steps.
- **The other way out** is W5's: set the value in the deployment and restart. It always wins.

### 4.5 Storage (W6)

- **Plain values** are `Settings.Identity.Oidc.*` and `Settings.OpenWebUi.Oidc.*`, in the settings row with its
  version, `pg_notify('rag_config')` and audit, as Decisions "Settings rule" says.
- **Secrets** (the two client secrets) are stored encrypted with ASP.NET Data Protection under a purpose of their own,
  with the Data Protection certificate each deployment already has as a file (`data-protection.pfx`, in rag-api,
  rag-admin and rag-migrate). They are write-only in the API: a read answers "set" and when, never the value.
- **Backup** needs nothing new: the database and `data-protection.pfx` are already on the list
  (`docs/portainer.md`, *First deploy* step 8).
- **This changes a rule:** repo-and-deployment §6.3's "secrets and certificates are files, never environment
  variables" becomes "files, or settings encrypted with the Data Protection certificate; never environment
  variables". Only values a person types in the wizard are stored this way; every generated secret stays a file.
- The same storage on Portainer, Compose and Kubernetes, so no service needs write access to a secrets volume or to
  the Kubernetes API.

### 4.6 Precedence and taking effect (W5, W10)

- **The deployment wins.** A value the deployment sets (`Oidc:*` from a variable or a secret file) is used, and the
  wizard shows the field locked with *set by the deployment*. A deployment that sets authority, client id and secret
  never opens setup: Compose with `init-deployment.sh`'s options and Kubernetes with declared values keep working as
  today, unchanged.
- Each field reports its source to the UI: `deployment`, `wizard` or `default`.
- **Reload in place:** rag-admin's sign-in options are built from the effective values, and the cached
  `OpenIdConnectOptions` are dropped when the settings version changes, so the next sign-in uses the new values without
  a restart. Sessions that exist stay valid until they end.
- The test sign-in (§4.3) runs on the same code path with candidate values, so "the test passed" and "sign-in works"
  can't differ.

### 4.7 Open WebUI (W8)

- rag-api serves Open WebUI's sign-in settings on an internal route, to the deployment's own Open WebUI client only
  (the key in `openwebui/rag-api-key`, kind `open-webui`, Decisions "Deploy configuration"). The client secret leaves
  rag-api only there, inside the stack's network.
- Open WebUI's start wrapper fetches them before `start.sh` and exports them with the three secrets it already
  exports. Its own URL and redirect URI come from the host name (W7), not from the wizard.
- The wrapper asks again every minute and exits when the answer changed; `restart: unless-stopped` (a Deployment on
  Kubernetes) starts Open WebUI with the new values. No Docker socket.
- Until the values exist, the wrapper waits and says so in its log, instead of starting an Open WebUI nobody can sign
  in to.
- The wizard gets an *Open WebUI* step when the deployment runs it, with the same test (discovery, then a round trip
  with Open WebUI's client and redirect URI). The step can be skipped and done later from the checklist: it isn't a
  security boundary of the admin UI, so a platform admin may also change it later on the Settings page.

### 4.8 Host names (W7)

- **`UME_DOMAIN`** (for example `example.se`) gives the defaults `rag.<domain>`, `rag-admin.<domain>` and
  `chat.<domain>`; the gateway's `llm.<domain>` and `llm-admin.<domain>` follow when its stack file takes the same
  variable (§6, WG1). Each `UME_*_HOST` still overrides its own name. Compose: `init-deployment.sh --domain`.
  Kubernetes: one value for the Ingress hosts.
- The names stay deploy-time: they must be in DNS, in the operator's certificate and in the provider's redirect URIs
  before a wizard can be reached, and the proxy routes by them.
- The internal certificates keep their SANs as they are (no change needed for this proposal).
- The wizard's first page shows the five names and tests that each answers through the proxy (*Test live URL*, as
  Mattermost), and shows the redirect URIs to register at the provider, ready to copy.

### 4.9 The three deploy forms (W2)

| | Portainer | Compose | Kubernetes |
|---|---|---|---|
| Setup code | rag-admin's log in Portainer | `docker compose logs rag-admin`; `init-deployment.sh` prints the command | `kubectl logs deploy/rag-admin`; `deploy.sh` prints the command |
| Declared instead | leave the variables set | `--oidc-*` options stay, now optional (no prompt); set, they lock the fields | values and `create-secrets.sh` stay; set, they lock the fields |
| Re-open | `UME_RAG_SETUP_REOPEN` in the stack | the same in `deploy/.env` | the same on the Deployment |
| Open WebUI | the wrapper in the template | the same wrapper in `compose.openwebui.yaml` (it replaces `secrets/openwebui.env`'s two OAuth lines) | the same wrapper in its Deployment |

The code is one path: setup mode, storage, precedence and reload are in rag-admin and rag-api, and no deploy form has
logic of its own beyond where a variable is written.

## 5. What it changes for the person deploying

**Today** (Portainer, v0.6.0): fill 11 values, two of them secrets, into the stack's variables; deploy; read
`rag-bootstrap`'s checklist; make sure the first admin has the role; sign in; create the embedding profile.

**After stage 1** (Ume.Rag and Open WebUI): fill **6** values, none a secret for Ume.Rag or Open WebUI:
`UME_OIDC_AUTHORITY`, `UME_GATEWAY_HOST`, `UME_GATEWAY_PUBLIC_URL`, `UME_ADMIN_HOST` (the gateway's four),
`UME_DOMAIN`, and the department code in the manifest. Deploy, copy the setup code from rag-admin's log, open
`https://rag-admin.<domain>/setup`. The wizard shows the redirect URIs, takes the two client secrets, tests each
sign-in and tells the deployer whether they are a platform admin. Then the checklist, as now.

**After stage 2** (the gateway's part, §6): fill **1** value, `UME_DOMAIN`, and deploy. The gateway's wizard takes the
provider address and its client; Ume.Rag's wizard gets the address from the gateway and asks for its two client
secrets.

What gets better beyond the count:

- **No secret in Portainer's variable list**, where every stack editor can read it.
- **A wrong value is found before it locks anyone out.** Today a wrong authority or secret shows as a failed sign-in
  with the reason in a container log.
- **"Am I a platform admin?" is answered by the wizard**, not by a failed first sign-in.
- **The redirect URIs are shown ready to copy**, instead of built by hand from the docs.

What stays as it is: host names (as one value), ports, profiles, the crawler's ranges, and everything a declared
deployment sets on purpose.

## 6. Gateway changes (the user's repository), for stage 2

Proposed as issues in thekim1/llmgateway once this proposal is agreed; none blocks stage 1.

| # | Change | For |
|---|---|---|
| WG1 | **`UME_DOMAIN` in the gateway's stack file:** `UME_GATEWAY_HOST` defaults to `llm.<domain>`, `UME_ADMIN_HOST` to `llm-admin.<domain>`, `UME_GATEWAY_PUBLIC_URL` to `https://<gateway host>` | 5 host values → 1 |
| WG2 | **The sign-in provider's address in `/v1/ume/context`** (`gateway.oidc_authority`, an addition as `organisation_name` was): Ume.Rag's wizard uses it as the default, so the address is typed once for the stack | the shared authority |
| WG3 | **The same setup mode in the gateway's admin:** a setup code in adminapi's log, a test sign-in before saving, the deployment's value wins, re-open by a variable. This proposal's §4.2–§4.6 is the contract; the gateway's role groups are its own to decide | `UME_OIDC_AUTHORITY`, `UME_OIDC_CLIENT_ID`, `UME_OIDC_CLIENT_SECRET` |
| WG4 | **The manifest without an edit:** the owner department chosen in the gateway's admin after the first sign-in (the integration created without an owner, or in a default department), so `UME_INTEGRATION_MANIFEST` can ship as a default in the stack file | the department code inside the JSON line |

With WG3 the gateway's wizard runs first (the gateway is the base product, Ume.Rag its add-on) and Ume.Rag's wizard
links to it when the context has no provider address yet.

## 7. Risks and how the design answers them

| Risk | Answer |
|---|---|
| A stranger reaches `/setup` first | They need the code from the container log; attempts are limited; the code expires; setup is closed whenever a working sign-in exists |
| A wrong value locks every admin out | A save needs a passed round trip by a platform admin; a value that breaks later is fixed by re-opening or by a deployment value |
| A compromised platform admin moves sign-in to their own provider | Sign-in settings change only in setup mode, which needs host access |
| A compromised admin reads a client secret | Secrets are write-only in the API and encrypted in the database |
| The database is restored without the certificate | Already the case for chat and blob keys; the wizard's secrets are then typed again after a re-open |
| The setup code in a log collector | It is single-purpose, expires in 24 hours and is void once setup closes; the docs say so |
| Two sources for one value confuse | One rule (the deployment wins), shown per field |
| The test sign-in shares the real callback | Built first as a spike (step S2); if one callback can't serve both, the test gets a callback path of its own and the wizard shows two redirect URIs |

## 8. Steps

One PR per step or smaller; streams by `.github/stream-ownership.txt` (`src/**` except the platform pages is stream
b; `deploy/**`, `docs/**` and `src/admin-ui/src/platform/**` stream a). The designs (identity §5.2 and §11,
repo-and-deployment §6.3 and §7.1, admin §5.4) and Decisions are updated by main before S1.

| # | What | Paths | Done when | Stream | Waits on |
|---|---|---|---|---|---|
| S0 | `UME_DOMAIN`: the three names default under it in the template, `init-deployment.sh --domain`, the Kubernetes example | `deploy/**`, `docs/portainer.md` | A stack with only `UME_DOMAIN` for Ume.Rag's names boots (the boot-only run); each `*_HOST` still overrides | a | – |
| S1 | Encrypted settings: the store (Data Protection purpose, write-only API shape), `Settings.Identity.Oidc.*`, effective values with their source (deployment, wizard, default) | `src/Ume.Rag.Infrastructure/`, `src/Ume.Rag.AdminApi/` | Tests: a deployment value wins; a secret never appears in a response or the audit row | b | the designs |
| S2 | Spike, then build: rag-admin's sign-in options from the effective values, reloaded on the settings version; the test sign-in with candidate values | `src/Ume.Rag.AdminApi/` | A changed authority applies to the next sign-in without a restart; a test with a wrong secret fails with the reason and saves nothing | b | S1 |
| S3 | Setup mode: open and closed from state, the code (hash, expiry, log line, rate limit), the setup session, `Setup:Reopen`, the security events | `src/Ume.Rag.AdminApi/` | `/setup` answers 404 with a working sign-in; a re-open value opens it once; five wrong codes void the code | b | S2 |
| S4 | The wizard's pages: code, host names with *Test live URL* and the redirect URIs, sign-in with the test and its result, the read-only summary; texts in `labels.ts` and `messages.ts` | `src/admin-ui/` | A fresh FakeGateway stack with no `Oidc:*` set is taken from the log's code to a signed-in platform admin in the browser | a (texts b, or a `main-session/` branch) | S3 |
| S5 | Open WebUI: rag-api's internal route, the wrapper (fetch, wait, exit on change) in the template and `compose.openwebui.yaml`, the wizard's step and the Settings section | `src/Ume.Rag.Api/` (b), `deploy/**` (a), `src/admin-ui/` | The boot test signs a user in to Open WebUI with no `UME_RAG_OPENWEBUI_OIDC_*` set; a changed secret restarts Open WebUI by itself | b, then a | S1, S4 |
| S6 | The deploy files lose what moved: `stack.env.example`, `init-deployment.sh` (options optional, no prompt), Kubernetes values; `docs/portainer.md`, `production-setup.md`, the upgrade guide; `rag-bootstrap`'s checklist points at the wizard | `deploy/**`, `docs/**` | `stack.env.example` has no secret and 6 CHANGE lines; the release boot test passes through the wizard's API | a | S4, S5 |
| S7 | The browser test of the wizard in `e2e.yml` (code from the log, test sign-in against the test Keycloak, lock, re-open) | `tests/e2e/` | Green on a manual `e2e.yml` run | a | S6 |
| S8 | The gateway issues WG1–WG4, and Ume.Rag's side of each when released (the context's `oidc_authority` as the default; `UME_DOMAIN` for all five names) | issues; `src/Ume.Rag.Infrastructure/Gateway/`, `deploy/**` | `stack.env.example` has 1 CHANGE line | main, then b and a | the gateway |

S0 needs nothing and is useful by itself. S1–S3 are the security-relevant part and get the main session's review of
every line (handbook, *How we work*).

## 9. What changes in the binding documents (on agreement)

- **Decisions "Deploy configuration":** "the OIDC authority, client ids and client secrets" leave the list of what a
  deployment must set. New text: they are set in the setup wizard (a setup code from rag-admin's log, a test sign-in
  before saving, changed afterwards only in setup mode) unless the deployment sets them, which wins; host names
  default under `UME_DOMAIN`; `Crawl:AllowedPrivateRanges` stays as it is.
- **A new entry "Setup wizard"** with W1–W10 in the present tense.
- **Decisions "Settings rule"** gains: a typed secret may be a setting, encrypted with the Data Protection certificate.
- **repo-and-deployment §6.3** (the secrets rule, §4.5 above) and §7.1 (`--domain`, optional OIDC options);
  **identity** §5.2 and §11 (setup mode, the test sign-in, the security events); **admin** §5.4 (the wizard before
  step 1, the summary page).
- A dated line under *Later decisions* in `plans/archive/decisions-log.md`.
