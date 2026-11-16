# Changelog

Notable changes per release. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow
[Semantic Versioning](https://semver.org/). Shared file: append under **Unreleased**, in a PR of its own.

## Unreleased

## 0.7.0 (2026-11-16)

**The setup wizard (M-20):** sign-in is set up in the browser instead of in the deployment's variables. rag-admin
prints a one-time setup code at its first start; `/setup` takes the provider, the client and its secret, runs a test
sign-in and saves only what a platform admin's test passed with; Open WebUI's sign-in is a step of the same wizard. A
new Portainer stack needs one typed value, `UME_DOMAIN`, and no secret (0.6.0: nine values, two secrets), with the
gateway's own setup wizard first (pins the gateway's v0.11.0). Also: a stack that still sets a retired variable is told
so (M-21), and three hot paths measured in #764 are shorter.
**Rolling update: yes** from 0.6.0 (migrations `IdentityOidcSettings`, `SetupMode` and `OpenWebUiSignInSettings` only
add). **An existing deployment keeps its variables and never sees a wizard.** See `docs/upgrade-guide.md`.

### Added

- **Sign-in values as settings** (setup wizard S1, S2; identity design §19): `Settings.Identity.Oidc` holds the
  provider address, the client and the token's shape, with the client secret encrypted with Data Protection and
  write-only; each effective value has a source (the deployment's wins and is shown locked, then the wizard's, then
  the code's default), shown by `GET /api/settings/sign-in`. rag-admin signs in with the effective values, starts
  without an authority, and follows a change without a restart (#761, #762).
- **Setup mode** (setup wizard S3, S4; admin design §5.11): open only before its first completion and after a re-open,
  never on a fault; a deployment that declares an authority and a client secret counts as completed. `/bff/setup/*`
  takes the setup code (derived from the setup secret file, the same on every replica, valid one hour, written to
  rag-admin's standard output only), gives a setup session, runs a test sign-in in a window of its own and completes
  with the tested values; security events 6008–6013. The admin UI's `/setup` has the steps *Setup code*, *Addresses*,
  *Sign-in*, *Open WebUI* and *Summary and finish* (#767, #769, #773).
- **Home reminds a platform admin before a client secret expires** (`GET /api/overview`, `secretExpiry`; the date is
  typed in the wizard, #773).
- **The browser test of the setup wizard** on a stack of its own in `e2e.yml` (#774), and
  `deploy/tests/portainer-upgrade.sh`: the stack file renders with the previous release's variables (#777).

### Changed

- **`UME_DOMAIN` gives the host names their defaults** (setup wizard S0): in the Portainer stack, rag-api, rag-admin
  and Open WebUI default to `rag.<domain>`, `rag-admin.<domain>` and `chat.<domain>`, each `UME_RAG_*_HOST` still
  overrides its own, and a stack with the names and no domain works as before; `init-deployment.sh --domain` does the
  same for Compose. `stack.env.example` has `UME_DOMAIN` instead of the three host lines.
- **rag-admin's setup secret and the re-open variable** (setup wizard S3, deploy side): `rag-bootstrap`,
  `init-deployment.sh` and so `create-secrets.sh` generate `admin/Setup__Secret` once (never overwritten), the key of
  the setup wizard's code; `UME_RAG_SETUP_REOPEN` (Portainer, `deploy/.env`, kept on a re-run) and `Setup__Reopen`
  (Kubernetes) pass rag-admin `Setup:Reopen`, empty by default. The AppHost's `Setup:Try=true` starts rag-admin without
  the sign-in authority and client secret and with a setup secret, to try the wizard locally.
- **Open WebUI's sign-in settings from the setup wizard** (setup wizard S5, identity design §19): `Settings.OpenWebUi.Oidc`
  (provider URL, client id, button label, the client secret encrypted under its own purpose) with each value's source;
  the setup API's `test` and `complete` take `target: open-webui` (a back-channel test of the discovery document and the
  client's credentials; saving needs the session's passed platform-admin test and leaves setup open; event 6013), and
  `GET /bff/setup/state` and `GET /api/settings/sign-in` show Open WebUI's block. rag-api registers the shared key ring
  read-only and serves the effective values on `/internal/openwebui/sign-in` to the deployment's own Open WebUI client
  only; Open WebUI's new start wrapper fetches them, starts without sign-in when there are none, and restarts it when they
  change. `UME_RAG_OPENWEBUI_OIDC_*` are now deployment values of rag-api and rag-admin; Compose's
  `--openwebui-oidc-client-secret-file` writes their secret files instead of `openwebui.env`. The button's label is also
  a setting (`PUT /api/settings/openwebui`). The wizard's Open WebUI step in the admin UI comes in its own change.
- **The deploy files and the operators' docs follow the setup wizard** (setup wizard S6): the Portainer template,
  `compose.prod.yaml` and the Kubernetes base pass no `Oidc__*` default any more (a variable set is the deployment's
  value; refusing guests reaches rag-admin as the file `Oidc__RefuseGuests`, from `rag-bootstrap` or
  `init-deployment.sh --oidc-refuse-guests`). `stack.env.example` has five CHANGE values and no secret; the sign-in
  variables are commented-out "declared instead" entries. `init-deployment.sh`'s `--oidc-*` options are optional and
  never prompted (a re-run keeps the authority and client id from `deploy/.env`), and its closing notes,
  `rag-bootstrap`'s last lines, `create-secrets.sh` and `deploy.sh` point at the wizard and the setup code. The release
  boot test starts rag-admin without a client secret and checks the wizard up to a setup session. `docs/portainer.md`,
  `production-setup.md`, `kubernetes.md`, the runbook and the upgrade guide describe the wizard, declaring sign-in
  instead, re-opening, and the restore and lock-out cases.
- **The setup wizard offers the gateway's provider address** (setup wizard S8, identity design §19): rag-admin reads
  the context's `gateway.oidc_authority` (feature `oidc-authority`; only an absolute https address counts), and
  `GET /bff/setup/state` carries it as `gateway.suggestedAuthority`, with `gateway.adminUrl` for a link to the gateway's
  own setup (`gateway` is null for a gateway without the feature). The sign-in step fills it in when neither the
  deployment nor the wizard sets an authority, says it came from the gateway, and sends it like a typed value; a gateway
  without an address gets a note with that link. The effective sign-in values and their sources don't change. The
  FakeGateway's context has `oidc_authority` (null unless `FakeGateway:Gateway:OidcAuthority` is set) and the feature,
  and `contracts/gateway/integrations.json` is the gateway's unreleased copy with that field.
- **A stack that still sets a retired variable is told so** (M-21; Decisions "Deployment values across releases"):
  the Portainer stack hands `rag-bootstrap` the names of the retired variables that have a value
  (`UME_RAG_RETIRED_SET`, the 20 retired since 0.3.0). `rag-bootstrap` names each on its checklist with what replaced
  it and writes the names to `admin/Deployment__RetiredVariables`; `GET /api/overview` returns them to a platform admin
  as `deploymentNotices` (`{ code: "retired_variables", names }`, an addition), and Home shows a notice that can be
  hidden. Nothing stops and no value is read. Compose and Kubernetes have no such list.
- **rag-admin's sign-in paths are bounded for anonymous callers** (setup wizard, from the review of
  thekim1/llmgateway#103; identity design §19). `/bff/login`, `/bff/user`, `/bff/logout` and the sign-in callback read
  the sign-in values at most once every 2 seconds per instance (requests arriving together share one read) instead of
  once per request; completing setup reads them at once on its own instance, another replica follows within 2 seconds.
  A callback with a setup test's state answers 404 without a read while setup is closed and is limited to 20 a minute
  per client address while it is open (429). Completing setup also compares what the test's result rested on (claim
  names, tenant, guest rule, the deployment's role groups) with what is in force (`values_changed`).
- **One typed value in the Portainer stack, `UME_DOMAIN`** (setup wizard S8, the stack): `build-stack.sh` gives the
  gateway's `UME_INTEGRATION_MANIFEST` a default, `deploy/portainer/integration-manifest.json` (the integration
  `Ume.Rag` and its three credential keys, no owner, so the gateway places it in a team and department `ume-rag` that
  a gateway admin can move), used only while the variable is unset: set, the stack's own manifest wins; set and empty,
  nothing is provisioned. The proxy's gateway host names default under `UME_DOMAIN` (`llm.` and `llm-admin.<domain>`),
  and rag-admin gets the gateway admin's address for its links as `Admin__GatewayAdminUrl` (`UME_ADMIN_PUBLIC_URL`,
  else `https://<gateway admin host>`). `stack.env.example` has one CHANGE value; the gateway's `UME_OIDC_AUTHORITY`,
  host names and public URL are commented overrides, so a new stack sets sign-in in the gateway's setup wizard and then
  in rag-admin's, which suggests the provider saved in the gateway's. A stack that sets `UME_OIDC_AUTHORITY` and its
  own manifest changes nothing; one without a manifest gets the default (new keys beside hand-made ones: upgrade
  guide). The stack is built from the gateway's release with setup mode and manifests without an owner.
  `docs/portainer.md`, the upgrade guide, the runbook, `production-setup.md` and `keycloak.md` follow.

### Fixed

- **Setup mode after an independent security review** (#778, #770): the right setup code is never throttled by wrong
  ones, a made-up callback keeps the test's cookie, completing re-checks the provider the test ran against, and a
  `complete` that lost the race answers 404.
- **A Portainer stack with only the host names renders on every Compose** (#776): no required-variable form inside a
  default, which some Compose versions evaluate even when the outer variable is set.
- **Hot paths measured 2026-11-15** (#764): a search slot is one statement's connection, held only while the statement
  runs (#766); a lane keeps its ready departments for a second instead of reading them before every claim (#768); the
  chunk insert and the embedding cache send vectors as a binary `vector[]`, not as text (#780).

## 0.6.0 (2026-11-15)

A deploy that needs only what a process can't start without (M-17): the gateway creates Ume.Rag's credential keys,
and the organisation's name, the Integration API's address, the residency ceiling, role groups, the embedding profile
and Open WebUI's client and feedback key come from the gateway, the admin UI or the stack itself. Also: apps and keys
connected in one go (M-16), the admin UI review (M-19), a stricter general-knowledge level per chat request (#726),
Kubernetes NetworkPolicies on by default, and the fixes of the 2026-11-14 review (#694). Pins the gateway's v0.10.3
(its Ume.Rag preset limits every key to `OnPrem` and `Eu`, #747).
**Rolling update: no** from 0.5.x (`RemoveWorkerConcurrency`: stop the old rag-admin before `rag-migrate`); some steps
come **before** the update (rag-api's and rag-admin's keys get `OnPrem` and `Eu`). See `docs/upgrade-guide.md`.

### Changed

- **The gateway creates Ume.Rag's credential keys** (M-17 C7): the Portainer stack pins the gateway's v0.10.3, whose
  migrations create the integration `Ume.Rag` and the keys `rag-api`, `rag-admin` and `rag-worker` from
  `UME_INTEGRATION_MANIFEST` (create-only, secrets on their own volume); rag-bootstrap gives each service its key, and a
  set `UME_RAG_*_GATEWAY_KEY` still wins. rag-admin joins the gateway's `edge` network for the Integration API (#727).
- **`admin-console` is seeded in every deployment** (#712): the migration service creates the test panel's client (or
  adopts a hand-made one) as managed by the deployment; managed clients are unique per kind and name, their name and
  keys can't be changed in the admin UI.
- **Kubernetes NetworkPolicies are on by default** (#706): default-deny in `base` with every flow the stack needs; the
  worker's and freshclam's 80/443 egress reaches public addresses only. The Ingress controller's rule is the component
  `components/ingress-controller`, and a gateway at a private address needs an overlay rule (upgrade guide).
- **Admin config writes call the gateway before the config lock** (#696): model lists are fetched once per request,
  outside the lock, with a 5 s timeout, so a slow gateway no longer blocks every other admin save; key reveal skips the
  lock.
- The worker reads each embedding answer in one pass (#703), prices estimated calls from a one-minute cache (#711),
  and the Costs page asks for monthly figures and keeps the chosen granularity for the CSV (#710).
- **Clients and keys have a page of their own** (*Platform → Clients*): the type picked from cards, only that type's
  fields, the first key made with the client (for Open WebUI with the two stack lines to copy), and the client's keys and
  assistants in its drawer; keys get pickers instead of JSON. `POST /api/clients` takes `firstKey` and answers
  `{ client, key }` (#663).
- **Open WebUI's client is set up by the deployment** (M-17 C4): rag-migrate registers it from a key the deployment
  generates, it shows as *Managed by the deployment*, and rag-api trusts Open WebUI's users only for that client. A
  hand-made Open WebUI client is no longer trusted: move its assistants over as the upgrade guide says (#684, #685).
- **The residency ceiling comes from the gateway** (M-17 C9): each service's maximum is its credential key's
  residencies in its own gateway context, `OnPrem` until the first context answer; give rag-api's and rag-admin's keys
  `OnPrem` and `Eu` in the gateway **before** the update (#691).
- **Role groups are a setting** (M-17 C10): *Settings → Role groups* maps sign-in groups to roles, read at each sign-in;
  the deployment's platform-admin group stays as the break-glass and is merged in (#693).
- **The organisation's name comes from the gateway** (M-17 C12): the admin UI's header and sign-in page name the
  organisation the gateway's admin console is branded with (its context's `organisation_name`, gateway v0.10.2); a
  gateway without one, or before the first context answer, names none.
- **The Integration API's address comes from the gateway** (M-17 C11): rag-admin's cost and budget read-back uses the
  address its gateway context names (`integration_api_url`, the gateway's `UME_INTEGRATION_API_URL`, gateway v0.10.2);
  without one there is no read-back, as before without the variable.
- **The Portainer stack needs fewer variables** (M-17 C1–C3): rag-api's public URL defaults to `https://<its host>`, the
  checklist no longer demands a platform-admin group, and Open WebUI's feedback address and the malware-scan mode follow
  the `openwebui` and `clamav` profiles (#682, #683).
- **Settings is one page of sections** (M-16): Chat, Search and Processing's schedules as fields with their defaults
  (the rest under *Advanced*), Personal data's flag rules as a table, site profiles and data protection as sections, and
  a new site profile's information class as a select (#673, #676, #679, #681).

### Added

- **A chat request may ask for a stricter general-knowledge level** (`ume.general_knowledge`, #726): `off`,
  `supplement`, `fallback` or `open`; the turn runs at the stricter of it and the assistant's own, so a request never
  loosens it. The admin test panel has the same level picker.
- **Internal certificate expiry is watched** (#705): rag-bootstrap's checklist names certificates within 60 days of
  expiry and the services to restart after a re-issue; Compose hosts get a daily `rag-cert-expiry` timer with a
  `certs.prom` metric; `--renew-certs` ends with the restart command.
- CI: the Portainer stack's rag-bootstrap is run twice and must keep every secret, owner and mode (#697), Open WebUI's
  settings must match between Compose and Portainer (#707), and `deploy/portainer` and `deploy/kubernetes` get
  shellcheck.
- **Apps and keys: connect an app in one go** (M-16): *Add app* picks its assistants and ends in *How to connect*; the
  app's drawer sets its assistants and lists its keys with their last use (rag-api records a key's last use at most every
  five minutes per key); a new key asks only for its name, the rest under *Restrictions* (#668–#672).
- **Every platform page in the side menu**: Apps and keys, Models, Settings, Organisation and Audit log; the *Platform
  objects* page goes (#675).
- **The embedding profile is created in the admin UI** (M-17 C5): *Models → Embedding profiles → Create profile* from
  one of the gateway's embedding models, its size measured by a test embedding; platform admins see a Home card until it
  exists (#687, #688).
- **The Open WebUI feedback key is minted by the stack** (M-17 C6): a one-shot job after Open WebUI starts, with
  password sign-in kept off; rag-api reads it without a restart (#689).
- **An optional TLS proxy in the Portainer stack** (M-17 C8): the `proxy` profile runs nginx with the operator's
  certificate in front of the five host names, re-encrypting to the services and following them when a stack update
  recreates one (#690, #692).
- The Integration API read-back (a key's budget and usage) on the Gateway models view (A-41, #665).
- The release's smoke test boots the Portainer stack, and `deploy-checks` fails on a stale stack file (M-17 C0, #674).
- **Home shows what is running** (M-19): a *Live* card per published assistant (7 days: questions, not found, ratings,
  cost), *Health* (runs of the last 24 hours per department, the queue, organisation and model sync), and published
  knowledge bases' checklists folded, each with *Hide* (#724).
- **The lists say more** (M-19): knowledge bases show their last fetch and quality, assistants their knowledge bases,
  model, published version and questions in 7 days; every list row is a real link (#724).
- **The audit log is readable** (M-19): object names linked to their pages, events in plain words, pickers for event
  and object type, and *Only this object* (#724).

### Removed

Deployment variables whose values are now set up automatically or in the admin UI (the upgrade guide says what to do
for each): `UME_RAG_OPENWEBUI_API_KEY` and `UME_RAG_OPENWEBUI_CLIENT_ID` (C4); `UME_RAG_EMBEDDING_MODEL`,
`_DIMENSIONS`, `_REQUEST_DIMENSIONS`, `_MAX_INPUT_TOKENS`, `_MAX_RESIDENCY` and Kubernetes' `EmbeddingProfile__*` (C5);
`UME_RAG_OPENWEBUI_ADMIN_API_KEY`, `UME_RAG_OPENWEBUI_PASSWORD_AUTH` and `deploy/openwebui-feedback-key.sh` (C6);
`UME_RAG_GATEWAY_MAX_RESIDENCY` (C9); `UME_RAG_OIDC_GROUPS_DEPARTMENT_ADMIN`, `_PIPELINE_EDITOR`, `_REVIEWER` and
`_VIEWER` (C10); `UME_RAG_ORGANISATION_NAME` (Compose), `UME_ORGANISATION_NAME` (Portainer) and Kubernetes'
`Admin__OrganisationName` (C12); `UME_RAG_GATEWAY_INTEGRATION_API_URL` and Kubernetes' `Gateway__IntegrationApiUrl`
(C11); in the Portainer stack `UME_RAG_CLAMAV_MODE` and `UME_RAG_OPENWEBUI_BASE_URL` (C2, C3). The unread
`Settings.WorkerConcurrency` is dropped (M-18, #686).

### Fixed

- The Portainer stack starts with the `openwebui` profile: `rag-openwebui-setup` reads its script from `rag-config`
  instead of an inline config, which Compose can't write into a read-only container (#749).
- A publish refused for want of an AI gateway key now says so and where to choose one, instead of reading like a cost
  note; the audit log's object links and *Only this object*, and a knowledge base's *Used by* links, have a 24 px target
  (WCAG 2.5.8). The browser tests pass again (#748).
- A client's ceiling or history change re-checks the assistants it offers, and a published public assistant with
  general knowledge no longer blocks its audience and client saves (#700, #738).
- Prompt texts save with the version they loaded, so another admin's concurrent change answers 409 instead of being
  overwritten (#708). The Files list keeps every loaded row and the selection when it refreshes (#709); a slower,
  older answer no longer overwrites a newer one on any page (#710).
- The client drawers read every assistant and knowledge base instead of the first 200, and fetch them once (#711);
  the upload list shows counts and at most 50 rows (#712).
- A key that allows a model the gateway runs only at another residency is now explained as such, naming the knowledge
  base's embedding model (#720).
- `init-deployment.sh --renew-certs` reads `deploy/.env` instead of running it, and a bad proxy host name in the
  Portainer stack leaves that block out instead of stopping rag-bootstrap (#711).
- An embedding profile that comes into use during a save answers `concurrent_change`; `ChunkSets` has an index on its
  profile (#712).
- A non-streamed answer (API `stream:false`, the admin test panel) is bounded by `Chat:MaxAnswerDurationSeconds`
  instead of the 15 s headers timeout, which cut longer answers off with `503 upstream_unavailable` (#695, #713).
- The Integration API client no longer retries refused calls and has a 10 s timeout, so a failed read-back answers
  `null` with its reason instead of `500` (#701, #716).
- Admin UI: a knowledge base opened on its Documents tab by link or reload listed its documents as "No documents yet";
  archived assistants showed as "Draft only"; the knowledge base and assistant pages now keep their tab in `?tab=`
  (#723). The assistant page's wording says what saving a draft does, and the general-knowledge modes have distinct
  descriptions (#724).

### Security

- LibreOffice's output passes the same zip-bomb check as an upload, and LibreOffice runs under 3 GiB of address
  space and a 512 MiB file limit (#702).
- rag-admin sends its credential key to the Integration API only over `https` (C11, #740); shown-once key secrets are
  cleared when their drawer closes (#711); a gateway admin link is made only from an `http(s)` address (#712); the
  deployment's own keys can't be edited (#712).
- Every admin request type with validation attributes is validated: the test panel's question and history lengths,
  `topK`, key scopes (#699, #714).
- Open WebUI's message-id headers are taken only as UUIDs, a reused id no longer breaks the conversation writer, and the
  writer retries only transient errors, so one user can't stall it or drop other users' usage rows (#698, #715).
- A tag release reads no cache that pull-request jobs can write: no build cache, its own NuGet and npm caches (#704,
  #717).
- The parser's `opentelemetry-api` 1.45.2 (CVE-2026-126360) (#678).

## 0.5.1 (2026-11-13)

Easier deploys with Portainer: the gateway and Ume.Rag as one stack with every image pinned, Open WebUI in it, a
variables file, a checklist of what is still missing, and gateway keys that apply without restarts. **Rolling update:
yes** from 0.5.0 (no migration). See `docs/portainer.md`.

### Added

- The Portainer stack runs Open WebUI with the profile `openwebui` (`rag-openwebui`, as `compose.openwebui.yaml`), on
  plain HTTP for a TLS proxy in front, calling rag-api inside the stack (#657).
- `deploy/portainer/stack.env.example` for Portainer's *Load variables from .env file*, and a checklist at the end of
  `rag-bootstrap`'s log of what is still missing on every deploy: the gateway keys, example values, the ClamAV profile,
  Open WebUI's settings (#659).
- `docs/portainer.md`: a first deploy on a new VM and the upgrade to 0.5.x with Portainer, step by step (#658).
- The admin's sync status carries the gateway's cost read-back state; the key picker counts the assistants and
  knowledge bases holding each key, so the shared-key note shows only for a shared key; texts for
  `gateway.key_unavailable` and `gateway.delegation_not_applied` (#653).

### Changed

- The Portainer stack pins the gateway's images (built from the gateway's `v0.10.1` release) and Ume.Rag's by digest,
  so no image variable is needed; `deploy/portainer/build-stack.sh` builds from a gateway release by default (#657).
- **The secret files reload on change:** a gateway credential key or the Open WebUI feedback key written by a stack
  update applies within seconds, without restarting the services; a service started without its key begins its gateway
  syncs once the key arrives (#659). rag-admin's OIDC client secret still needs a restart.

## 0.5.0 (2026-11-13)

Ume.Rag on the gateway's integration (phases 1–3): one credential key per service, a gateway key per assistant and
knowledge base that every model call runs as, the organisation and models from the gateway's context instead of the
Data API, tokens and cost of every model call with the gateway's cost and budgets beside them, a key's refusals in chat
and the admin, cancelled turns, the worker's content retention, and the malware scan optional per deployment.
**Rolling update: no** from 0.4.0 (`DailyTotalsWithoutCost`: stop rag-api and rag-admin before `rag-migrate`);
re-run `init-deployment.sh` for the credential keys, the Data API's removal and the `clamav` profile. See
`docs/upgrade-guide.md`.

### Added

- The FakeGateway answers `GET /v1/ume/context` and gives `/v1/models` the gateway's model facts, both with an `ETag`
  and an empty `304`; the gateway's contract is pinned in `contracts/gateway/integrations.json`, and a contract test
  holds the fake to it (#595, #580).
- Schema contract tests for the services' raw SQL: every statement must prepare against the migrated database, and
  each result's columns must match a checked-in `sql-shapes.json` (#588, #592, #510).
- The malware scan is optional per deployment: `ClamAv:Mode` = `on` (default) | `off`, set with
  `init-deployment.sh --clamav on|off` (`UME_RAG_CLAMAV_MODE`; Kubernetes: the component `components/clamav`). With
  `off` no `rag-clamav` runs and files are stored without a scan and marked so: upload state `unscanned`, no
  `Uploads.ScannedAt`, `malwareScan: "off"` in the run's report, security event 5106 `rag.malware.scan_off` at every
  worker start. Migration `UploadScannedAt`. The admin UI's *Not scanned for malware* note is still to come (#599,
  #602, #601, #535).
- **A gateway key per assistant and knowledge base** (gateway integration phases 1–2). rag-admin mirrors the
  integration's assigned keys from the gateway's context (`config."GatewayKeys"`, migration `GatewayKeys`); an
  assistant or knowledge base names one (`GatewayKeyId`, migration `ObjectGatewayKeys`), picked on its edit drawer
  from the keys of its department, and admin rule V39 checks it: active, of the object's department, reaching its
  ceiling and allowing each of its models (#610, #612). Once the gateway's context shows `act-as-key`, every model
  call runs as that key (`x-ume-on-behalf-of`): chat turns, Knowledge API searches, the test panel, the retrieval
  test (#616, #617) and every worker job (#621). An answer without the key's echo is discarded before use (security
  event 5011, `Gateway:RequireDelegationEcho`); a spent budget or an unusable key makes a worker job wait until the next
  full hour and then fail the document with `gateway.budget_exhausted` or `gateway.key_unavailable`. The FakeGateway
  acts as assigned keys, with `DropOnBehalfOfEcho` and a spent budget per key for tests (#616).
- **Tokens and cost of every model call**, per day and step: `chat."AssistantDailyUsage"` for every chat model call
  (condense, query embedding, rerank, answer, follow-ups), written with the turn, and `content."CollectionDailyUsage"`
  for every worker model call (indexing, preview, evaluations), with `RunItems.CostUsd` and `x-ume-run-id`. The cost
  is the gateway's (`x-ume-cost-usd`, or the stream's cost comment, never forwarded), else an estimate from the model
  mirror's prices, stored apart. Migration `DailyUsage`; metrics `rag.chat.tokens` (with `cached-input`) and the cost
  metrics. The FakeGateway sends a cost and has `fake-no-cost` and `FakeGateway:CachedInputTokens` (#608, #615, #613).
- The Files tab counts an `unscanned` file as *processing*, each file has `scannedAt`, and the admin UI shows *Not
  scanned for malware* on such a file and *Files in this run were not scanned for malware* on its run (#614, #535).
- **Cost in the admin.** The cost report API (`GET /api/costs`, `/api/assistants/{id}/costs`,
  `/api/collections/{id}/costs`: per assistant, knowledge base and department, by day or month, at most 400 days or
  36 months, CSV), scoped by the department each call was charged to (#627). The assistant's and knowledge base's
  *Cost* cards and the *Costs* page show the gateway's cost and the estimate apart, by step and client (#628). The
  test panel's calls run as the assistant's key and count under the client `admin-console`, which a platform admin
  creates in production (#625).
- **The gateway's cost and budgets beside Ume.Rag's** (gateway phase 3). With `Gateway:IntegrationApiUrl` set and
  rag-admin's credential key holding `usage.read`, rag-admin reads a key's cost per step and its budgets from the
  gateway's Integration API (cached 5 minutes; #637), and the Cost cards show the gateway's figures and the budget
  status beside Ume.Rag's (#638). Deployment: `UME_RAG_GATEWAY_INTEGRATION_API_URL`, and the host firewall allows it
  for `llm` when set (#643).
- **A key's refusal in chat and the admin** (gateway phase 2): an assistant whose key is disabled, revoked or expired
  answers `503 assistant_unavailable` before any model call, and a spent budget `402 budget_exceeded` with the
  gateway's message before the first byte (#632); the assistant page shows *Unavailable* with the reason, as does the
  test panel (#634). Save checks V8 and V39 run as the object's key, a published assistant needs a key once the
  gateway acts as keys and its key can't be cleared, and the status check makes an acted call (#618).
- **A cancelled turn is recorded:** a client that leaves or an idle or total timeout stores the user's message and
  the partial answer with `Outcome = cancelled` and counts the day's `Cancelled`; a broken stream is an error turn
  (#636).
- **The worker's content retention:** `content.rag_retention_content_step` removes, nightly and in batches, usage
  rows older than 3 years, runs older than 13 months (keeping the ones the scheduler still reads), parse observations
  older than 180 days, retrieval statistics older than 13 months, embedding cache entries unused for 30 days and old
  superseded site profile versions. Migration `ContentRetentionStep` (#626).
- Worker metrics `rag.embed.tokens` (input tokens of every answered embedding call) and `rag.ingestion.cost` (USD, tags
  `class` and `source` = `gateway` | `estimate`, as `rag.chat.cost`) (#646).
- The platform's *Gateway models* view shows the gateway's version and features; a model whose limits the gateway
  sends shows them read-only (#623).
- Runbook: reading Ume.Rag's cost beside the gateway's (#641), and the gateway keys, budgets and read-back with each
  refusal and its fix (#643).

### Changed

- A chunk set of at most 1,000 chunks is written once, inside the swap transaction: half the dead chunk rows of a
  re-crawl and 8–14 % less WAL (#589, #501).
- Indexing and the stage preview run the same stage runner (#590, #508).
- **One gateway credential key per service.** rag-api, rag-admin and rag-worker each call the gateway with one
  credential key (`Gateway:Key`, secret file `Gateway__Key`) at every ceiling up to the service's maximum: `Eu` for
  the worker, `Gateway:MaxResidency` or else `External` for rag-api and rag-admin. A call above the maximum is refused
  before sending and logged. The per-level keys (`Gateway:Keys:<level>`) are gone; see `docs/upgrade-guide.md` (#593,
  #594, #596, #580).
- Deployment: `init-deployment.sh --gateway-credential-key-file SERVICE=FILE` replaces `--gateway-key-file`, and a
  re-run deletes the per-level key files and names them. The Portainer stack takes `UME_RAG_API_GATEWAY_KEY`,
  `UME_RAG_ADMIN_GATEWAY_KEY` and `UME_RAG_WORKER_GATEWAY_KEY`. `docs/production-setup.md`, *Gateway credential keys*,
  says how a gateway admin issues the three keys (#597).
- Local stack: against a real gateway the AppHost reads `Gateway:Services:<service>:Key` from its user secrets, and it
  refuses to start with a leftover `Gateway:Keys` section (#593, #596).
- `rag-clamav` runs only with the Compose profile `clamav`, which `init-deployment.sh` writes by default: an existing
  deployment re-runs `init-deployment.sh`, or files wait with `clamav.unavailable`; on Kubernetes `rag-clamav` moved
  from `components/ingestion` to `components/clamav`. See `docs/upgrade-guide.md` (#601).
- Outside Development a worker with the scan on and no `ClamAv:Host` refuses to start (#602). The AppHost's switch is
  `ClamAv:Mode` in place of `ClamAv:Enabled` (#599).
- The Portainer stack is regenerated from the gateway's v0.10.0 stack file, which adds `UME_ADMIN_PUBLIC_URL` and
  `UME_INTEGRATION_REQUESTS_PER_MINUTE` to the gateway's services (#601).
- **The gateway's Data API is no longer used.** rag-admin syncs the organisation, member groups, its gateway keys and
  the models from the gateway's `GET /v1/ume/context` and `GET /v1/models` with its credential key, which needs the
  scope `organisation.read`; without it the departments and teams are left as they are and its health reports
  `Degraded`. `OrgSync:BaseUrl`, `OrgSync:DataApiKey`, `--org-sync-url`, `--org-sync-key-file` and the Data API
  firewall entry are gone, and the AppHost refuses the old keys. See `docs/upgrade-guide.md` (#605, #607, #611).
- A model's context window and output limit come from the gateway when its catalogue carries them
  (`GatewayModels.LimitsSource` `catalog`, migration `GatewayModelLimitsSource`); only a `manual` row can be edited
  (#607). The gateway status shows the gateway's version, admin URL and features (#607).
- Member groups are read-only on the platform's Organisation page, with a link to the gateway's page; the route
  `PUT /api/organisation/departments/{id}/member-groups` is gone (#605, #609).
- `chat."AssistantDailyTotals"` no longer holds tokens and cost (migration `DailyTotalsWithoutCost`): they are in
  `AssistantDailyUsage`, and the totals of earlier days lose them. **Rolling update: no** for this release; see
  `docs/upgrade-guide.md` (#615, #620).
- **Gateway v0.10.1:** a refusal of the object's assigned key is `403` (`delegation_not_allowed`, `key_disabled`,
  `key_revoked`, `key_expired`) and a `401` is always the credential key's own; the contract is pinned at `v0.10.1`
  (#629).
- A query embedding refused for one assistant's key no longer opens the circuit for the others, an answer without the
  key's echo isn't retried, and the query-embedding cache is per acting key (#631).
- The gateway links fall back to `Admin:GatewayAdminUrl` when the gateway's context has no admin URL (#633, #640).
- CI: `backend` skips pull requests that change only `docs/` and `deploy/`, except the files its tests read (#647),
  and the admin API's database tests run in four collections with a database each, which takes `backend` from about
  6 to 4.5 minutes (#648).

## 0.4.0 (2026-11-11)

Answer length from the model, editable embedding profiles, prompt texts as settings, keyword statistics kept by the
worker, lower idle and telemetry cost, and Aspire 13.6.5. **Rolling update: yes** from 0.3.0; migrations
`ProfileIndexReset`, `AnswerLengthFromModel` and `KeywordStatistics` add functions, a column and a table, and
`AnswerLengthFromModel` removes every assistant version's stored answer length and context size, so each follows its
model's limits. See `docs/upgrade-guide.md`.

### Added

- `Admin:OrganisationName` (default `Umeå kommun`; empty names no organisation) for the admin UI's header and sign-in
  texts; admin UI wording works for any organisation, and admin forms opt out of browser autofill and password managers
  (#544).
- `Crawl:UserAgent` on rag-worker (default `UmeaKommunBot/1.0`): robots.txt, meta robots and `X-Robots-Tag` are matched
  against its product token (#545).
- Embedding profiles can be edited on Platform → Gateway models until content is indexed with them (then only the
  residency ceiling), with **Test** and a test before every save; worker writes are guarded against a profile changed
  mid-embedding (#554, #559, #558).
- Models' context window and output limit can be entered on the gateway model mirror; V17 is bounded by them (#561).
- `UME_RAG_ORGANISATION_NAME` sets `Admin:OrganisationName` in Compose, Portainer and Kubernetes (#566).
- Answer length from the model: an assistant's *Longest answer* and *Context size* use the model's limits unless
  overridden, and the test panel shows the `max_tokens` sent and when an answer stopped at the length limit (#564, #569,
  #571).
- An optional platform ceiling for every answer, `Settings.Chat.MaxAnswerTokens` (Settings → *Longest answer, all
  assistants*) (#573, #574).
- Chat prompt texts as settings (`Settings.Chat.prompts`), each falling back to the built-in text; the citation and
  data-safety lines stay fixed (V38) (#575).
- The worker keeps the keyword stop list's statistics in `content."KeywordStatistics"`, after each run and nightly
  (#578, #579) and for every collection when it starts, and rag-api reads them instead of running `ts_stat` over
  every search vector (#583, #499).

### Changed

- Deployment templates request `dimensions` from the embedding model by default and set `MaxInputTokens` 8191 (#546).
- Reverse proxy: the api host and Open WebUI allow 2 MB request bodies, as rag-api's chat route does; with 1 MB, long
  conversations were refused by the proxy (#552).
- PostgreSQL: `jit = off`, `random_page_cost = 1.1`, `effective_io_concurrency = 200` for SSD storage and short
  queries (#553).
- Traces are sampled (`UME_RAG_OTEL_TRACES_SAMPLER`, default `parentbased_traceidratio` at `0.1`), and the .NET
  containers' health probe runs every 30 s (every 5 s while starting) (#555).
- Idle job lanes wait for a `rag_jobs` notification or their next due job (at most `Worker:IdleWaitSeconds`, 30 s)
  instead of polling every 2 s; `Worker:PollSeconds` is gone (#563).
- `rag-renderer` runs only with the Compose profile `render` (`init-deployment.sh --render`); without it, `browser`
  sources wait with `render.not_configured` (#563). The release smoke test boots the bundle both ways (#567).
- Release workflow: a build cache per image, one smoke boot for the renderer on and off, and a Docker client config
  per job, so an expired registry login on the runners can't break a build (#572).

### Fixed

- Portainer stack: rag-admin reaches the gateway's Data API; `dataapi` joins the internal network `rag-dataapi`, so the
  organisation and model sync no longer fail with `HttpRequestException` (#542).
- Portainer stack: the grants for the embedding profile functions were missing from the generated file (#566).

### Security

- Aspire 13.6.5 for GHSA-f7vh-h5g3-7jrh (CVE-2026-120006, elevation of privilege in `Aspire.Hosting` < 13.6.5), with the
  .NET packages at 10.0.14 that it requires (#549).

## 0.3.0 (2026-11-10)

Deployment options matching the gateway's, upload knowledge bases, legacy Office formats, and the review fixes of
#499–#513. **Rolling update: yes** from 0.2.0 (no schema migration); see `docs/upgrade-guide.md`.

### Added

- Updates without downtime: a second `rag-api` and `rag-admin` (`compose.rolling.yaml`, `init-deployment.sh --rolling`),
  `deploy/rolling-update.sh`, nginx retries on the other slot, and an end-to-end test that lost no request (#537).
- Kubernetes manifests (`deploy/kubernetes`): base, in-cluster Postgres, ingestion, renderer, NetworkPolicies, an
  example overlay, `create-secrets.sh` and `deploy.sh`; the release bundle pins them by digest (#538).
- Portainer: the gateway's stack plus Ume Rag in one compose file, attached to the release as `compose.portainer.yaml`
  (#536, #538).
- An OpenTelemetry Collector example that sends security events to a SIEM, and a Keycloak guide (#538).
- Upload knowledge bases: Files tab, one-step create, live progress, folder paths, drag-and-drop and folder upload
  (#527, #532, #534).
- Legacy Word, Excel, PowerPoint and RTF uploads, converted with LibreOffice in the parser (#526).
- Cancel a run from the run and source pages (#531).

### Changed

- Statement K: one keyword statement with the search vector as a parameter (#509, #523).
- Open WebUI user JWTs are validated with Microsoft.IdentityModel.JsonWebTokens (#512, #525).
- Search statistics stay within their own server-side limit (#499, #516); less telemetry overhead (#503, #518).
- `/version` has one camelCase shape in every service (#497, #529); the EF command log is at Warning everywhere (#522).

### Fixed

- A `readySelector` or `noiseSelector` that is not CSS is refused on save (#530).
- The worker records an upload's outcome (indexed or refused) on its slot (#533, #534).

## 0.2.0 (2026-11-08)

P1a and P1b code: crawl, upload, parse and embed; hybrid retrieval with citations; the chat API behind Open WebUI;
the admin UI for the pilot journey; evaluation, publishing and rollback; JavaScript rendering. Built on the
FakeGateway and checked live on the local stack against the interim gateway. Not in production yet: the live block
(gold set, test host, DPIA) comes before the pilot goes live.

### Added

- Retrieval core (`Ume.Rag.Retrieval`) and the knowledge API in `rag-api`: access policy and snapshot, keys and
  grants, citations and downloads through the blob envelope, section expansion, security events and the auth-failure
  throttle (#112, #125, #127, #146, #152, #163, #165).
- Blob envelope URB2: blobs encrypted by data-protection level, segment-verified AES-256-GCM (#153).
- Retrieval tuning: age decay and source demote after fusion, the keyword stop list, topics that limit the scope,
  HNSW index health with concurrent rebuild, optional rerank per assistant (a chat model, or the gateway's
  `/v1/rerank`) (#235, #241, #248, #330, #347, #388, #400, #415).
- Chat API, OpenAI-compatible: `/v1/models`, chat completions with streaming, condense, the answer filter, rate limits
  per user and per key, Open WebUI user-JWT trust, long questions condensed into the search query, the `ume` chat
  extension (outcome, related documents) and follow-up suggestions (#198, #203, #204, #205, #430, #431, #433, #434).
- Conversations and feedback: issued conversation ids, conversation list and delete, chat retention,
  `POST /v1/feedback` (ratings only), the Open WebUI feedback importer (#262, #263, #264, #267).
- Admin API for the pilot journey: knowledge bases, sources, runs and the crawl report, uploads, API keys, site
  profiles, assistants with drafts, versions, publish, restore, archive and retention, documents, personal-data
  clearance and deletion decisions, pipelines with preview, publish and rollback, evaluation, the retrieval and
  assistant test panels, the audit log and explain access, organisation (#176, #178, #180, #185, #188, #254, #255,
  #257, #258, #259, #260, #261, #298, #299, #361).
- Admin UI (Vue, English): knowledge bases, Add source with check and preview (Before / After), source and run pages,
  files and quarantine, deletion decisions, assistants (draft, test, publish, versions, audience, storage),
  publishing, test questions and quality, search settings, the audit log, feedback, organisation and the Map, generic
  forms for platform objects, the setup checklist (#279, #281, #285, #288, #301, #303, #304, #305, #306, #309, #310,
  #312, #313, #320, #321, #322, #366).
- General knowledge per assistant, a confirmed warning on a public assistant; a new assistant starts from a default
  (#358, #359, #369, #464).
- Organisation: sharing levels, collection shares, collection links with topics, member groups (#346, #347, #348,
  #349).
- Evaluation: test-question generation with a forecast, candidate comparison and threshold proposal, the
  `evaluation-smoke` CI job, rerank variants with latency and cost per question (#208, #220, #258, #402, #416).
- Browser tests: the pilot journey with mouse and keyboard and the accessibility spec on a fixture website
  (`tests/e2e`, `e2e.yml`) (#362, #368, #377).
- Shared Keycloak realm: realm additions, `deploy/keycloak` for keycloak-config-cli, role groups as `rag-` names or
  Entra object ids, OIDC claim names configurable in Compose, `init-deployment --proxy` writes the nginx config
  (#207, #409, #423, #469, #470, #474).
- AppHost: Open WebUI, fixed host ports, the feedback importer and the renderer container locally (#206, #340, #341).
- Restore a deleted uploaded file within the 14-day grace; publish checks that a live worker runs the template
  version (#486, #489).
- Licence notices; the sv_SE word list as separate, replaceable files (#381, #450).
- Worker contracts (J3) and P1b contracts (L1): source settings, pipeline definition v2, job payloads and results,
  site profiles, with a drift test (#78, #88).
- Schema part 1 (ingestion) and part 2 (serving); platform pipelines seeded from the built-in templates (#76, #89, #93).
- Parser P1 formats: Office/ODF, images and TIFF, `/v1/inspect`, `/v1/ner`, page ranges, limits, RSS watchdog (#84).
- Connectors: canonicaliser, egress guard with pinned connect, robots, sitemaps, URL lists, crawl with one frontier per
  source and the shared host budget, uploads by slot, ClamAV client (fail closed), deletion guard, Postgres stores
  (#82, #91, #97); crawler freshness: completeness by ratio, removal check, `demote`, gated `lastmod` skip (#101).
- Processing stages `detect` → `embed`, personal-data flags with NER, embedding cache (#98).
- Sitevision preset, the hand-checked umea.se profile and a 20-page umea.se regression corpus (#104).
- Engine: stage manifest, segments, fenced commit, URL-list and upload sync into live chunks, runs and finalizer,
  scheduler, index writer with invisible batches and swap, sweeps (#108).
- Security events 5101 and 5102 for the worker (#83).
- Pilot stack on Keycloak with per-service gateway keys and the P1a local run book (#94).
- Scripts: gold-set grading (#99), umea.se sitemap inventory (#111); R1 harness with `perf.yml`, `evaluation.yml`,
  `e2e.yml` (#103, #105).
- Worker commands for every P1 kind: deletion decisions, release hold, PII clearance, `reprocess-document`, site-profile
  edits and rollback (#115).
- Preview class: `stage-preview` with isolation, `preflight`, `publish-plan` (#117, #131).
- Engine: crawl sync through the engine, `reprocess` and `retry-failed`, the nightly drift sweep, parse slicing and
  resume from stored ranges, parse cache keyed by parser build (#121, #122); publish with per-document swap, previous
  sets kept 14 days, rollback swap-back, `publish-report` (#131); one `EnsureCurrent` decision with swap-back in the
  drift sweep (#138); forced `reprocess` and `reprocess-document` bypass the stage caches from `fromStage` (#140).
- Source settings-change effects: a scope or identity change starts a `config-change` sync; narrowed scope and
  site-profile `excludedTypes` remove documents without fetching (`scope-narrowed`); identity changes re-sync
  (`identity-changed`) (#136, #141).
- Admin-made parse rules matched like the built-in ones, also in the preview's parse key (#133, #136).
- Evaluation class: on-demand three-way runs, `publish-report`, `nightly` with the recall@10 alert (#128); generated
  sets with filtering on FakeGateway (#130); set triggers (first syncs, weekly, document count +25 %) and the
  reproducibility test (#137).
- Pilot stack: test-host run book, the Open WebUI feedback service account and `deploy/openwebui-feedback-key.sh`,
  the runbook's public-records routes, `deploy/tests/pilot-stack.sh` (#135).
- JavaScript rendering: `images/renderer` (Playwright 1.64.1 `launch-server`, Chromium with its sandbox, secret
  WebSocket path, WebRTC off) on the internal `render` network; the worker drives it and fetches every browser request
  through its egress guard; `PageFetcher` as the one fetch path; source settings `fetchMode`, `readySelector`,
  `scrollToLoad`; `Documents.FetchMode`; the preflight compares raw and rendered text (#459, #462, #463, #472).
- Politeness profiles per host in `Settings.Crawl.hostOverrides` (presets, rate, connections, `Crawl-delay`, window,
  back-off, breaker, robots.txt ignore with a reason, User-Agent) with the ceilings `Crawl:MaxHostRate` and
  `Crawl:MaxHostConnections`, V37 and a host table in the admin UI (#461).
- Crawler: meta refresh as a redirect hop, `redirect.cookie_required`, sniffing a missing or `octet-stream` type,
  a soft-404 fingerprint per host (`http.soft_404`), challenge pages as `http.challenge`, sitemap retries and text
  sitemaps (#460).
- Settings model fields as pickers that grey out models the platform's key may not use (#473).

### Changed

- Embedding requests no longer have a token cap: `Worker:Embed:MaxTokensPerRequest` is removed and a request carries up to
  64 inputs, since gateway v0.9.0 checks each input against the context window (A-5, #429).

- HTML noise rules v2 (Sitevision `data-cid` and `sv_` ids): **HTML documents are re-processed once after the
  upgrade**, because the change-detection hash is version-tagged (#107).
- CI on the self-hosted runners: `verify.yml` on every non-draft pull request, path-filtered (images, evaluation smoke
  and renderer only when their paths change); `e2e.yml` by hand or from a release when asked, after `verify` and
  `parser`; no schedules (#109, #151, #367, #372, #373, #397, #494).
- Site profiles are keyed by host without a port (#136).
- Files waiting more than 24 h for a ClamAV verdict end `failed` (`clamav.unavailable`) (#143).
- Open WebUI: password sign-in off (`ENABLE_PASSWORD_AUTH=false`, on only while the key script runs) and
  `DEFAULT_USER_ROLE=user` for OIDC sign-ins (#135).
- A URL that failed at any depth in a run is never a `not-seen` deletion candidate in that run, not only a seed
  (#460).
- Site-profile identity rules match with the non-backtracking engine and no timeout: under load a timeout kept a
  URL's path form and gave one page two documents (#475).
- `Retrieval:GatewayRerankTimeout` defaults to 1.5 s (was 500 ms) for the Foundry reranker (#439).
- Crawl and render knobs after live checks: `Worker:Concurrency:fetch` 32 with fetched bodies spooled to disk,
  `Render:MaxConcurrent` 4 with `renderPerHost` capped at the host's connections, `Render:QuietPeriod` 500 ms, the
  ready rule is `readySelector` and quiet (#482, #491, #492, #493).
- Gateway clients retry 502 and 504 with the same ceiling, and once on a connection failure (#406, #422).
- Open WebUI v0.12.5 (OIDC callback `/oauth/oidc/login/callback`), Keycloak 26.8.1, Aspire 13.6.4 (#339, #409, #457,
  #458).
- Parser image 2.88 GB → 2.58 GB; NER loads on demand and an idle convert slot goes cold (#418, #419).

### Security

- The gateway address stays out of logs and telemetry (#233, #238, #247, #272).
- API keys' `AllowedIpRanges` are enforced, with the client address taken behind a trusted proxy only (#432).
- Regex patterns and identity rules refuse constructs the non-backtracking engine lacks (#476, #477).

## 0.1.0 (2026-10-31)

P0 foundation: the skeleton builds, deploys from a digest-pinned bundle, is observable and can be restored (drilled locally). No
retrieval or chat yet (P1a).

### Added

- Stage 0 skeleton: solution `UmeRag.slnx`, every P0 project, health endpoints and `--health-check` probe in every
  service, AppHost with every P0 resource, `TestPostgres` on the pinned pgvector image, `verify.yml` (backend and the
  stream ownership check).
- Postgres image `ume-rag/postgres`: stock pgvector 0.8.7 on PostgreSQL 17 with pgBackRest, image test (#5).
- Schema and database roles, Swedish text search with the .NET decompounder (#7, #8).
- Config-version library: monitor, grace window, readiness, serialised writes (#13, #24).
- Keys and clients in gateway style; FakeGateway with the residency-ceiling client handler (#17, #18).
- Parser sidecar (Docling, pypdfium2 fast path, Tesseract `swe`) with contract v1 and contract tests (#9, #16).
- Worker: Postgres job queue, dispatcher, reaper, `cancel-run`; `rag_jobs` wake-ups on the config monitor (#20, #25, #30).
- ClamAV image (1.4 LTS, unprivileged) and the dev start scripts (#26).
- Write-once blob store on the file system with reusable contract tests (#33).
- Admin: Postgres session store, BFF sign-in with Keycloak, realm `ume`, write path with audit, organisation sync and
  config retention, admin UI skeleton in Swedish (#23, #32, #35, #38, #39, #45).
- Production Compose, `init-deployment.sh`, host units and firewall, backups, point-in-time restore and key escrow
  (#31, #40).
- Entra registrations as Bicep with `entra.yml` (P2) (#36); on-prem capacity scripts (#37).
- Docs: local development, production setup, runbook, upgrade guide (#42).
- CI: `admin-ui` job (#49); parser jobs in their own `parser.yml` (#50).
- Admin: the worker's `command` enqueue path (#46); pooler-safe SQL rules with a scan test over all service SQL (#53);
  `docs/security-events.md` (#54).
- Ownership rules for P1 (#57).
- Runbook: daily checks, restore drill, key escrow, public-records requests (#58).
- Release: `release.yml` (images to `ghcr.io/<owner>/ume-rag/<service>`, SPDX SBOMs, digest-pinned bundle, smoke test,
  GitHub release), `deploy/build-release-bundle.sh`, `deploy/tests/release-smoke.sh` (#60).
- Docs: install from the release bundle (#62).

### Security

- ClamAV 1.4.6 (CVE-2026-20339, CVE-2026-20345, CVE-2026-20346) (#67).
- `admin-ui` audit: production dependencies strict; two dated exceptions for lint-toolchain advisories until
  2026-11-10 (#64, #61).
