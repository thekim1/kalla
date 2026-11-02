# Admin API and admin UI: detailed design

Detail step 10 of the Ume.Rag design. The rules of the weakness review and the simplification, performance and
consistency passes are stated here (reasoning: [`../simplification-pass.md`](../simplification-pass.md) §1, §2, §5,
§7–§10, §13, §15). Details [`plan.md`](plan.md) (binding, incl. its answered questions 1–3 and §14 here) and the Decisions table
in [`../../01-general-idea.md`](../../01-general-idea.md) §10. Consumes, without redesigning: the BFF, claim mapping,
`RagPrincipal`, internal callers, keys and security events of [`../identity/design.md`](../identity/design.md) (§1–§4,
§6, §10–§12); tables, grants, the outbox, encryption and retention of
[`../platform/data-model.md`](../platform/data-model.md); templates, parameters, preview, publish, rollback and site
profiles of [`../ingestion-worker/design-engine.md`](../ingestion-worker/design-engine.md); source settings, preflight,
crawl report, uploads, effect tags and the deletion guard of
[`../ingestion-worker/design-connectors.md`](../ingestion-worker/design-connectors.md) (§4–§5, §9, §12.3, §13); PII
flags and evaluation sets of [`../ingestion-worker/design-processing.md`](../ingestion-worker/design-processing.md)
(§9, §13–§14); `RetrievalSettings` and the test-panel scope of [`../knowledge-api/design.md`](../knowledge-api/design.md)
(§4.2, §4.5, §12.1); residency, not-found texts, output filter, retention and feedback of
[`../chat-api/design.md`](../chat-api/design.md) (§8.2, §9.4, §10, §13–§14); skills, tool servers, approvals, grants,
drift, policy, agent limits and `GatewayModels` of [`../tools/design.md`](../tools/design.md) (§3–§6, §9); projects,
build, images, networks, CI and observability of [`../platform/repo-and-deployment.md`](../platform/repo-and-deployment.md).
Where the code exists (`src/Ume.Rag.AdminApi`, `src/admin-ui`), the text states what it does. Changes needed
elsewhere: §13; questions: §14; unverified facts: §15; deferred or removed: §16.

Gateway citations are `path:line` in `~/git/llmgateway` at `e25b491` (v0.8.5); `AdminApi/` = `src/Ume.LlmGateway.AdminApi/`,
`ui/` = `src/admin-ui/src/`. "Copy" = copied with names changed; "adapt" = copied and changed as stated; "new" = no
gateway counterpart.

**Sources read for this step** (all read only, never executed):

| Source | Version | Used for |
|---|---|---|
| Gateway `~/git/llmgateway` | `e25b491` (v0.8.5) | admin API conventions (endpoints, validation, problem details, audit, pagination, CSP), admin UI (stack, shell, router, client, forms, tables, charts), Data API catalogue, E2E and accessibility tests, `docs/admin-ui.md`, `docs/admin-api.md`, `docs/accessibility-statement.md` |
| Open WebUI `~/git/rag-platforms/open-webui` | `f7ac7ee` (v0.12.5) | fixed feedback reason keys (`src/lib/components/chat/Messages/RateComment.svelte:17-35`, `:186-212`) |

**Gateway facts this design relies on** (checked in the source):

- The gateway admin UI's **texts are English**: `index.html:2` `lang="en"`, labels in `ui/utils/labels.ts` and
  components (nav labels in `ui/router/nav.ts:43-150`); only numbers use `sv-SE` (`ui/utils/format.ts:2-6`), dates
  `en-GB` (`:7-10`), time zone `Europe/Stockholm` (`:1`); Swedish names are marked `lang="sv"`
  (`ui/views/OrganisationView.vue:150`). The RAG admin UI follows the gateway: **English** texts and `lang="en"`, every
  string in a labels file (Decisions, "Admin UI in English").
- No general ETag/If-Match handling: concurrency tokens only on keys (`src/Ume.LlmGateway.Infrastructure/Persistence/
  GatewayDbContext.cs:71-72`); any `DbUpdateConcurrencyException`, unique (23505) or FK (23503) violation becomes a 409
  (`AdminApi/Program.cs:20-27`). The client has `GET/POST/PUT/DELETE` only (`ui/api/client.ts:8`).
- The UI shows the server's `detail`/`title` verbatim (`ui/utils/problem.ts:7-8`); field errors are English
  (`docs/admin-api.md:5-6`, `AdminApi/KeyEndpoints.cs:31-55`).
- Pagination `page`/`pageSize` ≤ 200, `PageDto(items, total)` (`AdminApi/ReportEndpoints.cs:268-271`, `:160-169`); the
  Data API uses keyset feeds `after`/`limit` (`src/Ume.LlmGateway.DataApi/DataEndpoints.cs:67-80`).
- The Data API catalogue has departments, teams, keys, providers (with `Residency`), models (name, kind, provider),
  prices, budgets (`DataEndpoints.cs:139-160`) — **no routes/aliases**, no `Features`, no `ContextWindow`.
- Accessibility target WCAG 2.2 AA incl. 24 px targets (`docs/accessibility-statement.md:9`), axe A/AA on every routed
  page and on drawers and dialogs in three themes (`tests/e2e/specs/accessibility.spec.ts:6-9`, `:24-70`), target-size
  check (`tests/e2e/helpers.ts:160-172`); **no screen-reader check, "ej utfört och inte planerat"**
  (`docs/accessibility-statement.md:22`, `:31`).

## 1. Summary of decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Placement | `Ume.Rag.AdminApi` (`rag-admin`; `rag-admin-b` with the a/b slots of P2) is the BFF, the only writer of `config`, the job producer for the worker, the reader of `content`/`chat`/`audit` and the host of the built UI (`src/admin-ui/dist` as `wwwroot`); copied gateway BFF, CSP, rate limit, validation filter and problem details (§2) |
| 2 | Answer code in the admin | Library **`Ume.Rag.Answering`** holds the knowledge-role core and the chat answer core, so the **test panel** runs the exact chat path in process, including collections that are not yet published, without an API route that could include them; `rag-admin` and `rag-api` are deployed from the **same release**, so the panel always runs the code chat runs (§2.1, §4.9) |
| 3 | Language | **English** UI (Decisions, "Admin UI in English", 2026-11-02): `lang="en"`, every string in `ui/utils/labels.ts` and `ui/utils/messages.ts`, so Swedish can be added later as a translation; no i18n library until then. API, logs, OpenAPI and docs in English. Server errors carry a stable **`code`**; the UI renders its texts from a code table (§4.2) |
| 4 | Roles | **`rag-reviewer`** (answered 3): policies `Review`, `FeedbackComments`, `Read`. Feedback **comments**: `rag-reviewer` and `rag-department-admin`. **Full conversations: `rag-reviewer` only.** No administrative role implies conversation content (§3). Roles: **P1** realm roles `rag-*` on the shared Keycloak's client `ume-rag-admin`, audit actor `sub`; **P2** app roles on RAG's own Entra registration `ume-rag-admin`, actor `oid` (fallback `sub`) (identity §1.1, §1.6, 2026-10-31) |
| 5 | Concurrency | Every editable aggregate carries `version` (Postgres `xmin`); a mismatch is 409 `concurrent_change` (§4.3) |
| 6 | Write path | One transaction: **`SELECT … FOR UPDATE` on `config."ConfigVersion"` first** (serialises admin config writes) → cross-rules → entity change → outbox trigger (version bump) → audit row with that `ConfigVersion` → optional job rows; `pg_notify` at commit (§4.4) |
| 7 | Validation | Field rules on request records; **cross-rules** in one `ConfigRules` service: **V1–V35 with V20 and V27 retired (33 rules)** (§4.6), run on save, on dependent changes and again before publish, under the version lock; CHECK violations (23514) answer 400 |
| 8 | Jobs | The admin inserts `content."Jobs"` rows only (preview, sync, command, eval); every result is read from `Jobs.Result`, `Runs`, `EvaluationRuns` (§4.7) |
| 9 | Pilot UI | P1 has **dedicated pages only for the department journey** (collection, source, preview, publish with the evaluation report, assistant, test panel, audience, feedback, limited review), built for non-specialists (answered 2): setup checklist, template parameters with defaults, preflight and preview before saving, impact before scope changes (§5.4) |
| 10 | Pipelines | A pipeline is a **template + parameters**; the source form shows the template's parameters; pipeline editors edit parameters. No node editor, no canvas (dropped from the roadmap) (§6) |
| 11 | Publish | **Plan → re-process → evaluation report → keep or roll back**; no `gating` state, no blocking gate; previous chunk sets kept 14 days for one-click rollback (§6.6) |
| 12 | Evaluation | **Advisory reports** (recall@10, MRR with counts, compared with the previous run) on the publish screen and the collection page; no floor, no `building` state, no overrides (§7) |
| 13 | Feedback | P1 page from metadata (rating, fixed reason category, outcome, coverage, cited titles); a comment opens after a one-click pre-filled reason, audited in `ConversationAccess`, once A1 allows stored content (the P1 pilot runs history mode `none`: no comments in our store, weakness review §4) (§8.1–§8.2) |
| 14 | Conversation review | `rag-reviewer` opens a conversation with a free-text reason (≥ 20 characters), 30-minute access window, audited; first for conversations behind a feedback item (`public`/`internal`), then search and the `sensitive` second-approver flow; **built when A1 allows stored content, not in P1** (weakness review §4) (§8.3–§8.4) |
| 15 | Model catalogue | `GatewayModels` sync in **P1** (rows with `Kind` and `Residencies[]`); on assistant save the model is also checked against the gateway's **`/v1/models` at the level's ceiling** (§4.6 V8, §4.10) |
| 16 | Notifications | In-app only (attention cards, nav badges) plus SIEM events; no e-mail in P1/P2 (§5.2) |
| 17 | Accessibility | Gateway target WCAG 2.2 AA; copied axe/target-size/reflow tests, a keyboard-only Playwright run of the pilot journey, one NVDA pass; tillgänglighetsredogörelse as the gateway's (§10.1) |
| 18 | Dependencies | No new npm or NuGet package: no Markdown, chart, JSON-schema-form, graph or i18n package (§10.2) |
| 19 | Platform objects | In P1a the platform team sets everything up through the OpenAPI UI (Scalar) only; from P1b settings, data protection, clients, templates, site profiles, embedding profiles, gateway models and keys use **generic schema-driven forms** plus Scalar, not dedicated pages (§5.8) |
| 20 | Site profiles | The source preview shows the profile's before/after text on sample pages; platform admins see profiles per host with versions and roll back (Scalar in P1a; generic form + preview from P1b). In P1 the profile is a manual, hand-checked Sitevision preset JSON; learned profiles, proposals and approve are **P2 (weakness review §1)** (§4.5, §6.4) |
| 21 | Explain access | `POST /api/access/explain` returns the stable id of the first failing `IAccessPolicy` step and its reason code for (client, subject, assistant); platform admins, and department admins for their own department's assistants and clients; no security event (§4.5, identity §4.9) |
| 22 | Assistant versions | **One assistant type**: no `rag`/`agent` choice; an assistant uses the agent loop (P2) exactly when a linked skill carries tools, shown as **Använder verktyg**. Edits go to a **draft** (`AssistantVersions`, `State = draft`); the test panel runs the draft; **Publicera** makes it the published version (`Assistants.PublishedVersionId`), the only one chat reads (data-model view `PublishedAssistantVersions`); history with one-click **Återställ** (= a new version; the version diff is **P2**, weakness review §1). Audience, level, retention, owner team, description and contact belong to the assistant, not to a version, so publishing never changes who may use it (§4.5, §5.5.4) |
| 23 | Assistant tests (**P2**, weakness review §1) | Each assistant has up to 50 **test questions** (question, expected behaviour as free text, optional expected document and phrase), run by `AssistantTestRunner` in `rag-admin` through `Ume.Rag.Answering` and the on-prem answer judge after every publish and on demand; **advisory**, shown as the §7.2 card with **Behåll** / **Återställ förra versionen** (§7.4) |
| 24 | Many assistants | An impact line on source and collection saves ("påverkar 3 assistenter"). **P2 (weakness review §1):** platform-owned **assistant templates** (generic form); **copy** an assistant; an attention item for assistants unused 90 days. **P1b:** the **Map** as a table (§5.5.6). P2: a department may limit writes to the object's **owner team** (§3.2) |
| 25 | Organisation | Decisions "Organisation: department-owned, municipality-shared knowledge bases with topics (user, 2026-11-02)": departments own; a knowledge base is shared with *only our department*, *selected departments* or the *whole municipality*, read-only through assistants; assistants link knowledge bases with optional **topics**; knowledge bases have a contact person; teams are hidden in create forms (§3.2, §4.5, §5.2, §5.5) |

## 2. Placement and interfaces

### 2.1 Projects

| Project | Owns (admin) | Notes |
|---|---|---|
| `Ume.Rag.AdminApi` | BFF (identity §1, adapted from `AdminApi/AdminAuthentication.cs`), endpoint groups (§4.5), `AdminContext` (scope, version lock, audit, save), `ConfigRules` (§4.6), `JobEnqueuer` (§4.7), `ContentReader`/`ChatReader` (§4.8), `TestPanelService` (§4.9), `AssistantVersions` (draft, publish, restore, diff; §4.5), `AssistantTestRunner` (§7.4), `DepartmentMap` (§5.5.6), `AccessExplainer`, `FormSchemas` (§5.8), `OrganisationSync`, `GatewayModelSync` (§4.10), `RetentionService` (data-model §8), `ConversationAccessService` (§8); P2 `AnswerEvaluationService` (run kind `answers`, processing §14.8), `DefinitionDriftMonitor` (tools §4.4), `SkillGateRunner` (tools §3.7) | serves `src/admin-ui/dist` as `wwwroot` (`PublishAdminUi=true`) |
| `Ume.Rag.Pipelines` | `PipelineValidator` (parameters against the template's schema, template version supported by a running worker), `FormModel` (JSON Schema → form description, §6.3) | shared with the worker |
| `Ume.Rag.Retrieval` | retrieval core | test panel |
| **`Ume.Rag.Answering`** | `KnowledgeService` core (access within a given scope, hydrate, sections, citations), chat answer core (`RagAnswerStrategy`, `PromptBuilder`, `TokenBudget`, `Condenser`, `AnswerFilter`, `CitationEmitter`, `LanguageGuess`), P2 `AgentAnswerStrategy` | the knowledge and chat roles keep HTTP, SSE, writer and importer; the test panel shows the real prompt, budget and filter (chat §9.4) |
| `Ume.Rag.Tools` (P2) | in-process hub and MCP client (tools §2.1) | discovery, connection test, drift leader, test-panel tool calls |
| `Ume.Rag.Domain` | `RagRole`, `AdminPolicy`, `PermissionMatrix` (data, §3.4), `ValidationCode`, `IAccessPolicy` (pure; used by explain access) | pure |
| `src/admin-ui` | Vue 3.5 + Vite 8 + Tailwind 4 + Pinia + vue-router (gateway `package.json`), copied shell, design system, client, composables (§5.1) | port 5174 |

### 2.2 Hosting, slots and background work

- One slot `rag-admin` in the single-host pilot (one-slot deployment with a short announced maintenance window,
  simplification pass §1); `rag-admin-b` with the a/b slots of P2. Control zone; sessions in Postgres
  (`config."AdminSessions"`, owned by `rag-admin`; identity §1.1), so either slot serves any user; no Redis in P1
  (simplification pass §14).
- Networks: `edge` (proxy, inbound only), `data` (Postgres), `llm` (the default route, so also the OIDC authority and,
  from P2, Graph for feature C; repo-and-deployment §6.1; gateway: test panel, model validation, answer-level evaluation, P2 skill gate, Data API sync), P2 `mcp` (hosted MCP servers:
  discovery, connection test, drift polling, test-panel and skill-gate tool calls; the worker is not on it).
- Background services:

| Service | Interval | Leader | Writes |
|---|---|---|---|
| `OrganisationSync` | 5 min + on demand | a config write (version lock, data-model §5, since the mirror tables carry outbox triggers), then `pg_try_advisory_xact_lock` per run, as `AdminApi/ModelListSync.cs:148-153`; an unchanged run writes nothing and doesn't bump the version | `Departments`, `Teams` (changed rows only) |
| `GatewayModelSync` | 5 min + on demand | same pattern | `GatewayModels` (changed rows only) |
| `RetentionService` | nightly, 03:00 Europe/Stockholm (batches of 5,000 rows; both constants, only `Admin:Retention` is a knob) | pilot: the one slot runs it; from P2 each step batch is idempotent under `pg_try_advisory_xact_lock(<step>)` on both slots (reasoning: [`../performance-pass.md`](../performance-pass.md) §3.5); `Admin:Retention` (default `true`) stays an off switch | chat/audit/config deletions, expired admin sessions; **bucket-key destruction only when the shredding setting is on, which stays off until A1 is answered** (simplification pass §5) |
| `DefinitionDriftMonitor` (P2) | 15 min | advisory lock (the one drift leader) | `Tools.State`, repins from pre-registrations (tools §4.4) |
| `ConfigVersionMonitor` | `rag_config` + 2 s poll | every instance | none (keeps the admin's own snapshot for validators and the test panel) |
| `AnswerEvaluationService` (P2, weakness review §1) | nightly | `pg_try_advisory_xact_lock` per assistant run | `EvaluationRuns` of kind `answers` (faithfulness, citation precision) through `Ume.Rag.Answering` and the on-prem judge (processing §14.8) |
| `AssistantTestRunner` (P2, weakness review §1) | on assistant publish/restore and **Kör testfrågor** | `pg_try_advisory_xact_lock` on (assistant, version); an unfinished run is restarted after a restart | `EvaluationRuns` of kind `assistant-tests` and their `EvaluationResults` (§7.4) |
| `SkillGateRunner` (P2) | on publish and **Kör igen** | `pg_try_advisory_xact_lock` on the version id; an unfinished run is restarted after a restart | `EvaluationRuns` with `Trigger = skill-publish` (tools §3.7) |

- Tick leaders (`OrganisationSync`, `GatewayModelSync`, P2 drift monitor) keep `pg_try_advisory_xact_lock` and no
  in-memory state the next run needs (the in-memory sync status is display only, §4.10); in P2, with two slots, the
  config singletons (`Admin:Retention`) run as lock-guarded idempotent batches.
- Request body: 1 MB like the gateway (`AdminApi/Program.cs:20`), except the upload file route (50 MB + multipart
  overhead, `IHttpMaxRequestBodySizeFeature` per endpoint) with its own rate-limit policy; the proxy's
  `client_max_body_size` for that path is 60 MB.

### 2.3 BFF, CSP and how the UI is served

- BFF exactly as identity §1, against RAG's own registration `ume-rag-admin`: cookie names `__Host-ume-rag-admin`,
  `__Host-ume-rag-csrf`, readable `RAG-XSRF-TOKEN` (so `ui/api/client.ts:3` changes only the constant),
  `/bff/login|logout|user|session/extend`, 401 for `/api` without a session, 403 for missing rights,
  6002/6003/6005/6006 events.
- Pipeline order copied from `AdminApi/Program.cs:67-98`: security headers with the same CSP string
  (`default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self';
  object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'`, `:68-73`), `no-store` on `/api` and
  `/bff`, Scalar with nonce for platform admins (`:74`, `:94-95`), `AdminActivityMiddleware`, authn/z, per-user rate
  limit (`Admin:RequestsPerMinute` 120), antiforgery, `/api` group with `AdminValidationFilter` (`:84-85`),
  `UseDefaultFiles`/`UseStaticFiles`/`MapFallbackToFile("index.html")` (`:96-98`). Authorization runs before the
  antiforgery check, as in the gateway, so an anonymous unsafe call is challenged before the CSRF check. OpenAPI and
  Scalar are the P1a set-up surface (§12).
- CSP holds for everything new: fonts self-hosted (fontsource), no inline script. Preview outputs, CSV exports and
  conversation views are fetched from `/api` (never blob URLs from another origin).
- Cross-links to the gateway admin (separate app, plan answered 1): `Admin:GatewayAdminUrl`; member groups →
  `{url}/organisation`, a team's budget → `{url}/budgets` (`docs/admin-ui.md:97-99`).

### 2.4 Interfaces to other components

| To | How | Used for |
|---|---|---|
| `config` | EF `RagDbContext`, role `ume_rag_admin` S I U D (data-model §1.2) | all configuration |
| `content` | S, plus I on `Jobs`, I U on `EvaluationRuns`, `EvaluationResults` (`answers` and `assistant-tests` runs, P2 skill gate) | documents, runs, reports, evaluation, previews, site profiles, test panel retrieval |
| `chat` | S, plus **U on `Feedback.ReviewState`** (§13) | feedback page, comment reveal, conversation review |
| `audit` | S I on `AuditLog`, `ConversationAccess`, `AuthFailures`; S on tool tables (P2 S I on `ToolDefinitionObservations`) | audit list, access log |
| Blobs | `rag-blobs` rw (uploads; preview outputs read); `internal` blobs unwrapped with the level's master key, `public` blobs are not envelope-encrypted (simplification pass §4) | uploads, preview outputs, PII highlight previews |
| `IKeyProvider` | chat master keys (file provider in the pilot), once A1 allows stored content (weakness review §4) | comment and conversation decryption (§8) |
| Gateway | `Gateway:*` delegating keys per level (`Gateway__Keys__OnPrem`, `__Eu`, `__External`; simplification pass §6; until gateway release 2 the pilot team's own keys per level, weakness review §5); Data API key `OrgSync__DataApiKey` (`catalog.read`) | test panel embedding + answer, `/v1/models` validation (§4.6 V8), answer-level evaluation and assistant test questions with the on-prem judge, P2 skill gate, §4.10 |
| Hosted MCP servers (P2) | in-process `Ume.Rag.Tools`, `client-credentials` with the `ume-rag-tools` certificate (tools §4.6) | discovery, connection test, drift polling, test-panel and skill-gate tool calls |

## 3. Roles and permissions

### 3.1 Roles and policies

Identity §1.3; policies stay `RequireRole(any of …)` as in the gateway (`AdminApi/AdminAuthentication.cs:151-158`), so
roles add up.

| Role | Phase | Policies | Scope |
|---|---|---|---|
| `rag-platform-admin` | P0 | `Admin`, `Manage`, `EditPipelines`, `EditSkills`, `Edit`, `Read` | every department; platform objects |
| `rag-department-admin` | P0 | `Manage`, `EditPipelines`, `EditSkills`, `Edit`, `Read`, **`FeedbackComments`** | `departmentCodes` + feature C |
| `rag-pipeline-editor` | P0 | `EditPipelines`, `Read` | same |
| **`rag-reviewer`** | P1 (defined; nothing to read until A1 allows stored content, weakness review §4) | **`Review`**, **`FeedbackComments`**, `Read` | same |
| `rag-skill-editor` | P2 | `EditSkills`, `Read` | same |
| `rag-editor` | P2 | `Edit`, `Read` | same |
| `rag-viewer` | P0 | `Read` | same |
| no `rag-*` role | – | none: every `/api` call 403, UI shows the no-access page | – |

`Review` and `FeedbackComments` are **not** part of `Admin`. A platform admin who must review is also given
`rag-reviewer` (visible in the audit actor roles). `rag-reviewer` is granted per department: in P1 as the realm role `rag-reviewer`
in the shared Keycloak, given to the account or a Keycloak group per department (identity §1.6, §11.2, 2026-10-31);
from P2 by assigning a group per department (Entra app role assignment on `ume-rag-admin` or
`UME_RAG_OIDC_GROUPS_REVIEWER`). The UI mirrors the policies
with an implied-roles map as the gateway does (`ui/stores/auth.ts:10-15`, `:60-69`), but the API decides.

### 3.2 Department scoping and sharing

- `AdminContext.Departments()` copied (`AdminApi/AdminContext.cs:81-90`) plus feature C (claims only in P1, identity
  §2.3); every query of a department-owned entity (`Collections`, `Sources`, `Pipelines`, `Assistants`, `Skills`, `Keys`
  via teams, evaluation and run data via their collection/source) filters by it. An id outside the scope is **404**,
  never 403.
- Platform objects (templates, site profiles, embedding profiles, clients, `GatewayModels`, tool servers, settings,
  data-protection policies) are readable where a form needs them (pickers show name, kind, level, residency only) and
  writable by `Admin` only.
- **Sharing** (Decisions "Organisation: department-owned, municipality-shared knowledge bases with topics (user, 2026-11-02)"): the owning department's `Manage` sets a collection's sharing level: *only our
  department* (default), *selected departments* (`CollectionShares`) or *whole municipality* (every active department in
  the gateway catalogue). Skills (P2) are shared with named departments (`SkillShares`, `EditSkills`). An assistant links
  collections of its own department and those shared with it, and skills of its own department, shared with it or
  platform skills (V11, V12). Sharing gives read use through assistants only: another department sees a shared
  collection's name, owning department, level, contact and topics (§4.5), never its sources, documents, settings or
  evaluation, and cannot edit or publish it. Narrowing the sharing is refused while another department's published
  assistant links the collection (V35).
- **Topics**: the tags the sources' path rules put on chunks (processing §7). A link from an assistant to a collection
  may name topics; retrieval then keeps only that collection's chunks carrying one of them (knowledge §4.2, §6.2).
- **Ownership of shared content**: content used across the municipality is owned by the department that runs it (umea.se:
  Communications; DoIT until Communications has admins). The platform team owns no content.
  Knowledge reaches an assistant only through its own collection links; skills carry know-how and tools, never
  collections (simplification pass §15).
- **Owner-team scoping (P2, simplification pass §15)**: assistants, collections (with their sources, pipelines and
  test questions) and skills keep an owner team (`OwnerTeamId`). A department may switch on
  `DepartmentSettings.EditOwnerTeamOnly` (`Manage`, audited `departmentsettings.update`); while it is on, an `Edit`,
  `EditPipelines` or `EditSkills` write to such an object needs membership of its owner team (identity §1.3) **or**
  `Manage` in the department, so department and platform admins are unaffected. Reads are unchanged. A refused write
  is 403 `owner_team_only` (6006); creating or copying sets the owner team to one of the writer's teams; changing an
  object's owner team needs `Manage` while the setting is on. `AdminContext.CanWrite(entity)` applies it after the
  department filter, and the generated role tests cover it (§11.2).
- A source belongs to exactly one collection; keys only for teams of the department (identity §6.4).
- Users with several departments get a department filter in the page header (URL query `dept=`), defaulting to all.

### 3.3 Pages per role

R read, W write, – hidden (route guard → `/forbidden`, copied `ui/router/index.ts:87-98`). PA platform admin, DA
department admin, PE pipeline editor, RV reviewer, SE skill editor (P2), ED editor (P2), VW viewer. "Generic" = generic
schema-driven form (§5.8).

| Page (Swedish label, §5.7) | Phase | PA | DA | PE | RV | SE | ED | VW |
|---|---|---|---|---|---|---|---|---|
| Översikt | P1 | R (all + platform health) | R | R | R | R | R | R |
| Karta (Map: department map) | P1b, table first (§5.5.6) | R | R | R | R | R | R | R |
| Kunskapsbanker (incl. Dokument, Testfrågor och kvalitet, Sökinställningar, Ämnen (Topics), Delning (Sharing, `Manage`)) | P1 | W | W | R | R | R | W (no sharing) | R |
| Källor, körningar, filer, borttagningar (incl. mallinställningar) | P1 | W | W | W (template parameters only) | R | R | W (no deletion decisions) | R |
| Publicering (plan, rapport, behåll/återställ) | P1 | W | W | W | R | – | W | R |
| Assistenter (incl. Testa, Versioner; Testfrågor P2, weakness review §1) | P1 | W | W | R | R | R (+ test) | W | R |
| Målgrupp (assistant audiences) | P1 | W | W | – | R | – | R | R |
| Återkoppling | P1 (comments when A1 allows stored content, weakness review §4) | R | R + comments | – | R + comments | – | – | R (no comments) |
| Samtalsgranskning, Åtkomstlogg | when A1 allows stored content (weakness review §4; limited first, then full) | access log R | access log R (own depts) | – | W | – | – | – |
| Nycklar (generic) | P1 | W | W (own teams) | – | – | – | – | – |
| Inställningar, Dataskydd, Klienter, Mallar, Assistentmallar (P2, weakness review §1), Webbplatsprofiler, Inbäddningsprofiler, Gateway-modeller, Organisation (generic) | P1 | W | R (data-protection matrix, models, clients) | – | – | – | R (models) | – |
| Förvaltningsinställningar (owner-team scoping, generic) | P2 | W | W (own departments) | – | – | – | R | R |
| Granskningslogg (list) | P1 | R (all) | R (own departments' entries) | – | – | – | – | – |
| Tolkningsförslag (parse-profile proposals) | P2 | W (approve, reject) | R | R | – | – | R | R |
| Färdigheter | P2 | W (platform skills) | W | – | R | W | R (link only) | R |
| Verktygsservrar | P2 | W (register, approve, grant) | R | – | – | R | – | – |

A system owner of a tool server (any `rag-*` role, tools §4.5) sees that server's approval and pre-registration page
regardless of the matrix; the API checks `ToolServers.SystemOwnerSubjects` (`oid`). Departments ask for a tool server
or a grant through the platform team's normal intake; there is no request flow in the admin.

### 3.4 API actions per policy

The matrix is data (`PermissionMatrix` in `Ume.Rag.Domain`), used for endpoint authorisation and generated tests (§11.2).

| Policy | Actions (department-scoped unless noted) |
|---|---|
| `Read` | list/detail of everything in scope; runs, reports, documents (metadata), evaluation reports, feedback metadata |
| `Edit` | collections (create, edit, retrieval settings, test retrieval, run evaluation), sources (create, edit, pause/resume, sync now, retry failed, upload, delete files), assistants (create, edit the draft, publish, restore a version, archive/unarchive, test panel; P2: create from a template, copy, assistant test questions and runs), human test questions, generate test questions, link served skills (P2); P2 limited to the owner team when the department says so (§3.2) |
| `EditPipelines` | template parameters of the department's sources (preview, plan, publish, roll back), pipeline test retrieval |
| `EditSkills` (P2) | skills of own departments: draft, publish, serve, roll back, share list, re-run the gate; skill test panel |
| `Manage` | collection publish, collection sharing level and departments, delete collections/sources, deletion confirm/reject, release holds, PII clear (a decision on the document), assistant audiences, retention and history mode within policy, keys of own teams, document reprocess, explain access for own department's assistants and clients, audit log (own departments, §9.1), P2 department settings (owner-team scoping) and owner-team changes while it is on |
| `FeedbackComments` | reveal one feedback comment (`public`/`internal`) |
| `Review` | open a conversation with a reason; P2: request/approve `sensitive` access, search conversations |
| `Admin` (platform) | templates, assistant templates (P2), site profiles (edit, roll back; approve P2), embedding profiles, clients, `GatewayModels` manual rows, settings, data protection, raised agent limits, tool servers, approvals (platform side, incl. pre-registrations), grants, platform skills, parse-profile proposals (P2), audit log (all), organisation sync now, explain access |

## 4. Admin API

### 4.1 Conventions

| Topic | Rule | Source |
|---|---|---|
| Shape | Minimal API groups per area under `/api`, `MapGroup(...).RequireAuthorization(policy).WithTags(...)`, `.WithName()`/`.WithSummary()`; thin endpoints, logic in services; JSON camelCase, enums as strings, ISO-8601 with offset, money in USD fields `…Usd` | `AdminApi/KeyEndpoints.cs:59-80`, `docs/admin-api.md:3-6` (copy) |
| Requests | sealed records deriving `AdminRequest` with DataAnnotations + `IValidatableObject` for field rules; these records are also the source of the generic forms' schemas (§5.8) | `KeyEndpoints.cs:11-55`, `AdminContext.cs:216-258` (copy) |
| Errors | RFC 9457 problem details via `ApiFaultException(status, detail, extensions)`; validation = 400 with `errors`; 23505/23503/concurrency → 409 | `ServiceDefaults/ApiFaultException.cs:12-33`, `Program.cs:20-27` (copy) |
| Error codes | every non-validation fault has extension `code`; validation faults add `errorCodes: { field: [{ code, params }] }` (§4.2) | new |
| CHECK violations | 23514 → 400 `code: platform_rule` with the constraint name | new |
| Concurrency | `version` in DTOs and update/delete bodies (§4.3) | new (gateway: keys only) |
| Audit | snapshot records `XxxAudit.Of(entity)` with own columns only, never secrets; `Action` verb + `EntityType`; actor `oid` (fallback `sub`); staged with the change | `AuditSnapshots.cs:11-80`, `AdminContext.cs:141-169` (adapt: §4.4) |
| Propagation | no Redis invalidation bus: the outbox is in the transaction (data-model §5) | replaces `AdminContext.cs:177-191` |
| Pagination | small lists `page`/`pageSize` (≤ 200, default 50) → `PageDto(items, total)`; large lists (run items, documents, feedback, audit, access log) keyset `after`/`limit` → `{ items, next }` | `ReportEndpoints.cs:268-271`; `DataEndpoints.cs:67-80` |
| Search | `q` on list endpoints; `GET /api/search?q=` for the command palette (collections, sources, assistants, skills, documents by title; ≤ 20 hits, scope-filtered) | new |
| Rate limit | per user 120/min (`Admin:RequestsPerMinute`); upload files and job polling have own policies (600/min each) | `Program.cs:53-64` (copy + 2 policies) |
| OpenAPI | `/openapi/v1.json` + Scalar for `Admin` (in P1 also the action UI for platform objects without a dedicated page); the error-code list is exported as `contracts/admin/v1/error-codes.json` (CI checks the UI covers every code) | `Program.cs:94-95` (copy) |
| Data reads | 6007 `rag.admin.data.read` on audit log, keys, feedback lists, access log, conversation views (not explain access: nothing is accessed, identity §4.9) | identity §10 |

### 4.2 Error codes and UI texts

The gateway UI prints the server's message (`ui/utils/problem.ts:7-8`). The API's `detail` is written for logs and
Scalar, not for department admins, so `problem.ts` is adapted: `problemMessage` looks up `problem.code` in
`ui/utils/messages.ts` (templates with `{params}`, English now, the place a Swedish translation goes later), then a
status text, and shows the server's `detail` only as a last resort. `mapFieldErrors` (`:26-36`) prefers `errorCodes[field]`.

```json
{ "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1", "title": "Check the highlighted fields.", "status": 400,
  "errors": { "classification": ["The assistant's level (public) is below the collection 'bygglov' (internal)."] },
  "errorCodes": { "classification": [ { "code": "assistant_below_collection",
                                         "params": { "collection": "Bygglov", "level": "internal" } } ] } }
```

```ts
// ui/utils/messages.ts (excerpt)
assistant_below_collection: "The assistant's level is below the knowledge base {collection} ({level}). Raise the assistant's level or remove the knowledge base.",
concurrent_change: 'Someone else changed this while you were working. Reload the page; your changes are still in the form.',
```

### 4.3 Concurrency

- Every editable aggregate root maps Postgres `xmin` as a row-version token (`uint`, Npgsql EF; *to verify*, §15).
  Child rows (`AssistantAudiences`, `CollectionShares`, `SkillVersionLinks`, …) are saved through the root, and every
  root save sets `UpdatedAt`/`UpdatedBy`, so `xmin` changes whenever any part of the aggregate does.
- DTOs carry `version` (string); `PUT`/`DELETE`/state actions require it; mismatch → 409 `concurrent_change` with the
  current `version`. The form keeps the user's input and offers "Reload and compare".
- An assistant's draft (its `AssistantVersions` row with `State = draft`) carries its own `version`, so editing the
  draft and editing the assistant's own fields (audience, level, retention) do not conflict; publish and restore
  require both.
- Singletons (`Settings`, `DataProtectionPolicies` per level) use the same rule; `Settings` has one `version` (the
  singleton's `xmin`) for all its sections (§4.5).
- Job-backed actions are idempotent by `DedupKey` (§4.7), not by version.

### 4.4 Write path (one transaction, serialised)

```
BEGIN; SET LOCAL lock_timeout = '5s'
  1. SELECT … FROM config."ConfigVersion" FOR UPDATE      ← every admin config transaction, before anything else
  2. load aggregate (+ version check)
  3. apply request; ConfigRules (§4.6) against state no other admin write can change until COMMIT
  4. SaveChanges → row triggers write ConfigOutbox and bump ConfigVersion (data-model §5)
  5. SELECT "Version" FROM config."ConfigVersion" → v
  6. INSERT audit."AuditLog" (ActorOid…, Action, EntityType, EntityId, Details {before, after}, DepartmentId,
     ConfigVersion = v, Reason, Classification, Service = 'rag-admin', the container and OTEL service name)
  7. INSERT content."Jobs" (§4.7) when the action enqueues work + pg_notify('rag_jobs')
COMMIT  → rag_config and rag_jobs notifications delivered
```

- **Why the lock first** (simplification pass §9): cross-rules read several aggregates (an assistant's level, its
  collections' levels, its clients' ceilings); two concurrent saves could each pass them and jointly break one (write
  skew). Taking the `ConfigVersion` row lock before reading makes admin config writes serial. Admin writes are a few
  per minute, so the cost is nil; a lock wait over 5 s answers 503 `config_busy` (retry) and is measured
  (`rag.admin.config_lock.wait`).
- Adapted from `AdminContext.SaveAsync` (`AdminContext.cs:158-169`): the audit entry is staged with the change; the
  actor stamp (`AdminActor.Stamp`, `:38-49`) and the 6001 event after save (`:50-58`) are copied.
- Audit-only actions (reveal a key, read a comment, read the audit log) use `AuditAsync` (`:148-152`) without the lock.
- Writes to `chat."Feedback".ReviewState` and `audit."ConversationAccess"` are two transactions; the access event is
  written **first**, so a crash never leaves an unaudited read (§8.2).

### 4.5 Resources per area

Policy in brackets; department scope applies. Bodies and DTOs are the data-model columns unless noted. Two policies
separated by a comma (`Edit, EditPipelines`) mean either suffices (a combined policy in `PermissionMatrix`).

**Organisation, models, access**

| Method and path | Policy | Notes |
|---|---|---|
| `GET /api/organisation/departments` | Read | the mirror: departments in scope (all for a platform admin), active first, each with `SyncedAt`, its teams nested (name, code, active), its member groups and a sharing summary (`sharing`: its live knowledge bases per sharing level, and how many other departments its `selected` ones are shared with); there is no separate teams route |
| `PUT /api/organisation/departments/{id}/member-groups` `{ memberGroups }` | Admin | member groups by hand until gateway release 2 (§4.10): ≤ 50 values, trimmed, empty ones dropped, de-duplicated and sorted; departments carry no `version`, so the save has no concurrency check (last write wins, audited `member-groups` on the department); the sync never touches the field |
| `GET /api/organisation/sync`, `POST /api/organisation/sync` | Admin | last runs and outcome (held in memory per instance, §4.10); run now |
| `GET /api/gateway/models` | Read | `GatewayModels` with residencies, `SupportsTools`, context window, prices, `Source` |
| `POST /api/gateway/sync`; `PUT /api/gateway/models/{name}` | Admin | run the model sync now; manual row (aliases, `SupportsTools` until the catalogue has `features`; not built yet) |
| `GET /api/gateway/status` | Admin | per level key: one `x-ume-max-residency` embedding call and (from gateway release 2, P2) the delegation echo |
| `GET /api/organisation/departments/{id}/map` | Read | the Map (§5.5.6) as rows: department → collection (own, and shared in with its owning department) → assistant (published links; draft-only links flagged; the link's topics) → audience (clients), each with its contact; a platform admin may ask for any department; from the admin's snapshot; no content |
| `GET/PUT /api/organisation/departments/{id}/settings` (P2) | Read / Manage | `DepartmentSettings` (`EditOwnerTeamOnly`, `version`); audited `departmentsettings.update` (§3.2) |
| `POST /api/access/explain` `{ clientId, subject, assistantId, action? }` | Admin; Manage (own department's assistants and clients) | runs the pure `IAccessPolicy` steps (identity §4.3) against the admin's snapshot for that client, subject (`oid`, key id or Open WebUI user) and assistant; returns `{ allowed, step, reason, status, effectiveLevel, maxResidency, configVersion }` (`step` = the stable step id, `reason` = the stable deny code, identity §4.9); no content; no security event |

**Collections and retrieval**

| Method and path | Policy | Notes |
|---|---|---|
| `GET/POST /api/collections`, `GET/PUT/DELETE /api/collections/{id}` | Read / Edit / Manage (delete) | create: name, slug (from name, ASCII), department, owner team, classification; profile = default; the detail carries `usedBy[]` (assistants whose published version or draft links it, with `published`/`draft`), which the save drawer shows as "Påverkar 3 assistenter: …"; delete refused while a published version or a draft links it (409 `collection_in_use` with the list; superseded versions do not block, a restore of one fails V34); also refused while it has sources that are not deleted (same code, `sources` in the list), so the worker never writes into a deleted collection: delete the sources first |
| `GET /api/collections?view=mine\|shared\|all` | Read | `mine` (default): the caller's departments' collections; `shared`: collections of other departments shared with one of the caller's departments or with the municipality, as the shared summary (name, owning department, level, contact, document count, topics; no sources); `all`: both. The assistant's collection picker reads `all` |
| `PUT /api/collections/{id}/sharing` `{ sharedWith, departmentIds, version }` | Manage | `sharedWith` = `department`/`selected`/`municipality`; `departmentIds` only with `selected` (active departments other than the owner); narrowing is refused (V35) with the linking assistants and their departments; audited `collection.share`; the detail's `usedBy[]` lists other departments' assistants too |
| `GET /api/collections/{id}/topics` | Read (also a department the collection is shared with) | the tags on the collection's live chunks with document counts, and the path rule that sets each one; read from `content`, no text |
| `PUT /api/collections/{id}/retrieval-settings` | Edit | saved directly within the V16 ranges; enqueues an advisory evaluation run (§4.7) whose report is compared with the previous run (none when the collection has no evaluation set) |
| `POST /api/collections/{id}/test-retrieval` | Edit, EditPipelines | retrieval-only test panel (§4.9), incl. unpublished |
| `POST /api/collections/{id}/publish` | Manage | sets `PublishedAt`; the latest evaluation report (if any) is shown before confirm; never blocked |

**Sources, runs, uploads**

| Method and path | Policy | Notes |
|---|---|---|
| `POST /api/collections/{id}/sources`, `GET/PUT/DELETE /api/sources/{id}` | Edit / Manage (delete) | settings per `contracts/sources/v1/{type}.schema.json`; template + parameters (§6.1); schedule default per type; `ContainsThirdPartyText` (default true) writable by `Admin` only (tools §5.4, V13) |
| `POST /api/sources/preflight` | Edit | unsaved settings → `preview` `preflight` job (§4.7) |
| `POST /api/sources/{id}/impact` | Edit | effect tags of a changed settings object with counts ("38 dokument tas bort") and the collection's `usedBy[]` ("påverkar 3 assistenter"); returns `impactToken` that the `PUT` must echo when the tag is `scope`/`identity` (V25). The token is a hash that proves the impact was asked for this exact change from this exact version; it is not an authorization |
| `POST /api/sources/{id}/sync`, `/retry-failed`, `/pause`, `/resume` | Edit | sync now answers 200 with the existing run when one is queued/running (dedup); sync now and retry failed are audited as actions on the source (`sync`, `retry-failed`) and move no config version |
| `GET /api/sources/{id}/runs` (keyset), `GET /api/runs/{id}`, `GET /api/runs/{id}/items` (keyset; `outcome`, `reason`, `q`) | Read | crawl report from `Runs.Report` (incl. the site-profile version used, extraction quality, the "no complete traversal for N runs" alert) |
| `POST /api/runs/{id}/cancel` | Edit | `command` `cancel-run` |
| `GET /api/runs/{id}/deletions` (+ `?format=csv`), `POST /api/runs/{id}/deletions/confirm`, `/reject` | Read / Manage | connectors §13.3–§13.5; body carries `proposedCount` seen by the user; 409 `deletion_superseded` if a newer run took over |
| `GET /api/sources/{id}/held`, `POST /api/sources/{id}/held/release` | Read / Manage | `command` `release-hold` |
| `POST /api/sources/{id}/upload-batches` → `{ batchId }`; `PUT /api/upload-batches/{batchId}/files/{n}` (raw body, `X-File-Name`); `POST /api/upload-batches/{batchId}/commit`; `DELETE /api/upload-batches/{batchId}` | Edit | per-file progress and retry; extension pre-check 415; SHA-256 while streaming; commit writes the manifest and the `sync` job; uncommitted batches swept after 24 h. Identity is (source, upload slot), so replacing a file keeps the document id |
| `GET /api/sources/{id}/files` (keyset), `POST /api/sources/{id}/files/delete`, `/restore` | Read / Edit | connectors §9.3 |
| `GET /api/sources/{id}/quarantine` | Read | metadata only |
| `GET /api/sources/{id}/pii-report` | Read | the web editors' personal-data report, a list per source: it applies only to a `piiMode: report` source in a `public` collection (any other answers `applies: false`); held documents first, then live documents whose live set is flagged, each with the non-zero counts per type and the number of fired rules (numbers only, never values) and the last `pii.flagged` run item; at most 500 documents (`truncated`) |

**Site profiles** (simplification pass §13; `content."SiteProfiles"`/`"SiteProfileVersions"` per (level, host), written by the worker, so changes are `command` jobs; connectors §5.6). In P1 every profile is `manual` (the hand-checked Sitevision preset for umea.se); `learned` versions, proposals and approve/reject are **P2** (weakness review §1)

| Method and path | Policy | Notes |
|---|---|---|
| `GET /api/site-profiles` (`host`, `level`) | Admin; from P1b's source page also Read for the hosts of the caller's sources (matching sources to hosts needs the worker's canonical hosts) | level, host, CMS preset, applied version, origin (`learned`/`manual`/`preset`), last re-learn, sources using it, open proposal |
| `GET /api/site-profiles/{id}/versions`, `GET …/versions/{v}` | Admin | versioned JSON, check results of a proposal (words lost per sample page, empty pages, boilerplate share), sample before/after texts |
| `POST /api/site-profiles/{id}/versions/{v}/approve`, `/reject` (P2) | Admin | a `proposed` version (re-learned profile that failed the auto-apply check); `command` `approve-site-profile`/`reject-site-profile`; audited |
| `POST /api/site-profiles` `{ host, level, presetId?, profile }` | Admin | a host's first profile as a `manual` version; `command` `edit-site-profile` (dedup `site-profile:{level}:{host}`); audited |
| `POST /api/site-profiles/{id}/rollback` `{ toVersion }` | Admin | one click; `command` `rollback-site-profile`; audited |
| `PUT /api/site-profiles/{id}` `{ profile, version }` | Admin | a platform admin's edit as a new `manual` version (generic JSON form, §5.8); `command` `edit-site-profile`; audited. A profile belongs to no department, so its commands are queued (`Jobs.DepartmentId`, fairness only) for the department of the oldest live source whose settings name the host, else the first department (409 `no_department` before the organisation sync) |

**Documents**

| Method and path | Policy | Notes |
|---|---|---|
| `GET /api/documents` (keyset; `collectionId`, `sourceId`, `q`, `pii`, `outcome`) | Read | `pii` is `flagged` (a held flagged set or a flagged live set) or `cleared` |
| `GET /api/documents/{id}` | Read | its source, chunk sets (pipeline version, site-profile version), run items 90 days, PII counts and state of the live chunk set |
| `GET /api/documents/{id}/chunks` | Edit, EditPipelines | chunk text with heading path (breadcrumb prefix), tokens |
| `POST /api/documents/{id}/pii-clear` | Manage | reason ≥ 10 (V26); `command` `clear-pii-flag` |
| `POST /api/documents/{id}/reprocess` | Manage | `command` `reprocess-document` |

**Pipelines (template + parameters)**

| Method and path | Policy | Notes |
|---|---|---|
| `GET /api/templates`, `GET /api/templates/{id}/versions/{v}` | Read | parameter schema (`paramsSchema` with `x-labels`, `x-help`, `x-unit`), defaults, effect tags per parameter, worker support |
| `GET /api/pipelines/{id}`, `GET /api/pipelines/{id}/versions` | Read | a source's pipeline: template version + parameters per version |
| `POST /api/pipelines/{id}/preview` `{ templateVersionId, parameters, documentIds \| urls }` | EditPipelines, Edit | `preview` `stage-preview` on the pipeline's saved draft version (§6.4) |
| `POST /api/pipelines/{id}/publications` `{ templateVersionId, parameters, version }` | EditPipelines | V24; enqueues `publish-plan`; returns `publicationId` |
| `GET /api/publications/{id}`, `POST /api/publications/{id}/confirm` `{ version }`, `/cancel` | Read / EditPipelines | §6.6; confirm refused (409) if the pipeline changed since the plan |
| `POST /api/pipelines/{id}/rollback` `{ versionId }` | EditPipelines | one click; within 14 days the previous chunk sets are swapped back without re-parsing (§6.7) |
| `PUT /api/templates/{id}/draft`, `POST /api/templates/{id}/publish` | Admin | generic form (§5.8); a template version never re-processes sources by itself |

**Jobs and previews**

| Method and path | Policy | Notes |
|---|---|---|
| `GET /api/jobs/{id}` | Read (job's department) | `State`, `Result` (secrets stripped), queue position and age |
| `GET /api/previews/{jobId}/documents/{documentId}/stages/{stage}/output` | Edit, EditPipelines | decrypted preview blob, ≤ 2 MB, `text/markdown` or JSON |
| `POST /api/documents/{id}/pii-preview` | Manage | `preview` job up to the personal-data stage; spans from preview blobs (24 h); audited `pii.view` |

**Evaluation**

| Method and path | Policy | Notes |
|---|---|---|
| `GET /api/collections/{id}/evaluation` | Read | set size per layer, stale/retired counts, latest reports |
| `GET /api/collections/{id}/evaluation/questions` (keyset; `layer`, `state`, `q`) | Read | question, expected answer, expected documents (titles), last rank |
| `POST /api/collections/{id}/evaluation/generate` | Edit | `eval` `generate-set`/`top-up-set` with cost forecast (≈ 400 prompt and 120 output tokens per candidate, around twice the mean eligible chunk; a top-up's forecast is a lower bound, since the stale questions its re-check retires are known only in the worker); P1b, with the evaluation items (P1a's evaluation is the human gold set plus on-demand runs; generated sets are not run on the pilot until an on-prem model exists, O1) |
| `POST/PUT/DELETE /api/collections/{id}/evaluation/questions[/{qid}]` | Edit | human layer via `command` `edit-eval-question`; expected documents picked by search |
| `POST /api/collections/{id}/evaluation/run` | Edit | on demand (§4.7); a refused `candidateSettings` value is reported as `settings.<key>`, as the retrieval-settings save does |
| `GET /api/evaluation-runs/{id}`, `GET /api/evaluation-runs/{id}/results` | Read | recall@10 and MRR with counts, the previous run's values, per-question rank changes (§7.2) |

**Assistants**

| Method and path | Policy | Notes |
|---|---|---|
| `GET/POST /api/assistants`, `GET/PUT/DELETE /api/assistants/{id}` | Read / Edit / Manage (delete) | the assistant's own fields, saved directly under the V-rules: name, slug, owner team, classification, **description** (≤ 500 characters) and **contact** (`ContactName`, `ContactEmail`); create takes an optional `templateId` (P2, weakness review §1) and starts with only a draft prefilled from the template (or `Settings.Chat` defaults), so `PublishedVersionId` is null and no client lists it; the detail carries `state` (`active`/`archived`), `publishedVersion`, `draft { number, updatedAt, baseChanged }`, `usesTools` (derived, never written: a linked skill's served version links a tool not excluded) and `lastUsedAt` |
| `GET/PUT/DELETE /api/assistants/{id}/draft` | Read / Edit / Edit | the version fields: instructions, model, collections, skills (with excluded tools), query steps, P2 agent limits, not-found texts and links. One draft per assistant (`DraftVersionId`), created on the first edit as a copy of the published version with `Number` + 1; saving applies field rules and returns the assistant V-rules as `warnings[]` (never refused, so work in progress can be saved); `DELETE` discards it |
| `POST /api/assistants/{id}/publish` `{ draftVersion, version, confirmBaseChanged? }` | Edit | V34, then every assistant rule (V3–V17) as refusals and V8's `/v1/models` call; one transaction (§4.4) marks the draft `published` and the previous one `superseded`, sets `PublishedVersionId` and clears `DraftVersionId` (data-model §2.1); starts the test run (§7.4; P2). A draft created before the current published version (a restore happened since; its `Number` is lower) answers 409 `draft_base_changed` (with the diff from P2) until confirmed |
| `GET /api/assistants/{id}/versions`, `GET …/versions/{n}`, `GET …/versions/{n}/diff?against={m}` (`m` = a number or `draft`; **diff P2**, weakness review §1) | Read | number, state, who and when, restored-from, test-run summary (P2); the diff is structured: changed settings by name, collections and skills added or removed, and a line diff of instructions and not-found texts (in-house Myers diff over ≤ 20,000 characters, no package) |
| `POST /api/assistants/{id}/versions/{n}/restore` `{ version }` | Edit | one click: a **new** version with version n's settings (`RestoredFromVersionId`), published at once exactly as publish; an open draft is kept (its base is now older); refused with the list when a link no longer resolves (V34); restoring the draft or the published version is refused (409 `version_not_restorable`) |
| `POST /api/assistants/{id}/copy` `{ name, slug, departmentId, ownerTeamId }` (P2, weakness review §1) | Read (source) + Edit (target department) | new assistant with only a draft = the source's published version (its draft if never published); test questions copied; audiences, keys, versions and statistics not copied; V11/V12 for the target department as warnings; audited `assistant.copy` |
| `POST /api/assistants/{id}/archive`, `/unarchive` | Edit | `State = archived` hides the assistant from every client (identity §4.3 step 5) and keeps versions, audiences and statistics; unarchive returns `State = active` with the same published version |
| `GET/POST/PUT/DELETE /api/assistants/{id}/test-questions[/{qid}]` (P2, weakness review §1) | Read / Edit | V33; the expected document is picked by title search in the draft's collections |
| `POST /api/assistants/{id}/test-runs` `{ target: draft \| published }`; `GET /api/assistants/{id}/test-runs` (P2, weakness review §1) | Edit / Read | §7.4: on demand (the publish run starts by itself); one running run per assistant (409 `test_run_running`); results through `GET /api/evaluation-runs/{id}/results` |
| `PUT /api/assistants/{id}/audiences` | Manage | P1 kind `client` only; V7–V9; not versioned |
| `PUT /api/assistants/{id}/retention` | Manage | `HistoryMode`, `RetentionDays` within the level's policy (V10); not versioned |
| `POST /api/assistants/{id}/test` `{ target: draft \| published, … }` | Edit, EditSkills | §4.9; never stored |
| `GET /api/assistants/{id}/stats?from=&to=` | Read | `AssistantDailyTotals` |
| `PUT /api/assistants/{id}/limits` (P2) | Edit (≤ defaults) / Admin (≤ hard caps) | writes the draft's `QuerySteps.agent`; V14 |
| `GET/POST/PUT/DELETE /api/assistant-templates[/{id}]` (P2, weakness review §1) | Read / Admin | platform-owned **assistant templates** through the generic form (§5.8): name, description, instructions, not-found texts sv/en with links, query steps; copied into a new assistant's draft at creation, never linked afterwards; audited `assistanttemplate.*` |

**Skills and tool servers (P2)** — rules as tools design §3.5–§3.7, §4.4–§4.5 and §9.2: `/api/skills` (draft, validate,
publish → skill gate (run in the admin, tools §3.7), gate report, serve, rollback, retire, share list, re-run the gate), `/api/tool-servers`
(register, update, connection test, discover), `/api/tool-definitions/{id}/approvals` (`platform-admin` by `Admin`,
`system-owner` by a listed subject; two different people for new and unexpected definitions),
`/api/tool-servers/{id}/pre-registrations` (golden `tools/list` upload by a listed system owner; one `Admin` approval),
`/api/tools/{id}/grants`, `/api/tool-observations`.

**Clients, keys, settings, data protection** (generic forms in P1, §5.8)

| Method and path | Policy | Notes |
|---|---|---|
| `GET/POST/PUT/DELETE /api/clients[/{id}]` | Read (pickers) / Admin | `CK_Clients_*` as field rules (V21); raising a ceiling or changing `StoresHistory` re-validates every assistant offered to the client |
| `GET /api/clients/{id}/health` | Admin | missing `X-Rag-Owui-Message-Id` warning (chat §11.4), feedback import state (§8.1) |
| `/api/keys…` | Manage (own teams) / Admin | copied from the gateway (`KeyEndpoints.cs:61-133`): create (secret once), update, rotate (immediate or 24 h grace), reveal/copy (Admin, audited), revoke; `ume-rag-sk-`/`-pk-` (identity §6.4) |
| `GET/PUT /api/settings/{section}` (`network`, `retention`, `deletion-guard`, `crawl`, `evaluation`, `retrieval`, `chat`, `pii`, `worker`; `tools` from P2) | Admin | sections of `config."Settings"`; `network` holds `TrustedProxies` (data-model §2.1), as on the gateway's settings page; one `version` (the singleton's `xmin`) shared by all sections, so a save in one section makes a concurrent save in another answer `concurrent_change` |
| `GET/PUT /api/data-protection/{level}` | Read / Admin | V29; key service status read-only; allowed history modes `none` only and the shredding switch off until A1 (weakness review §4) |
| `GET/PUT /api/embedding-profiles…` | Read / Admin | P1 read-only; changes P3 (R3). The pilot profile is seeded by the migrator from the deployment knobs `EmbeddingProfile:*` (data-model §2.1) |
| `GET /openapi/v1.json` | Admin | the generic forms read each operation's request schema from the OpenAPI document; the resources, list columns and actions are a registry in the UI (§5.8) |

**Feedback, conversations, audit, overview** — §8, §9; `GET /api/overview` returns the attention items of §5.5.1 for
the caller's scope.

### 4.6 Cross-rules (V1–V35; V20 and V27 retired)

Run by `ConfigRules` under the `ConfigVersion` lock (§4.4) on save of the entity, on every change that can break a
dependant (column *Re-checked when*), and again inside the publish transaction. A failing dependant lists every
affected object by name (problem extension `affected[]`). For an assistant, rules on version fields (instructions,
model, collections, skills, query steps, not-found texts) answer as `warnings[]` on draft save and refuse on publish and
restore; dependants are re-checked against the published version, and a draft is checked again when it is published.
Levels order `public < internal < sensitive`; residency
ladder `public → External`, `internal → Eu`, `sensitive → OnPrem`, ordered `OnPrem < Eu < External`. Retired numbers are
kept so references stay stable.

| # | Rule | Code | Re-checked when | Source |
|---|---|---|---|---|
| V1 | source level ≤ its collection's level | `source_above_collection` | source save; collection level lowered | data-model §2.1 |
| V2 | lowering a collection's level below a source or below a linking assistant's need is refused; raising it is a config change the worker applies (engine §15; no membership recompute) | `collection_level_conflict` | collection save | engine §15 |
| V3 | assistant level ≥ every linked collection (own or shared; collections are linked directly only) | `assistant_below_collection` | assistant save; publish; collection level raised | data-model §2.1, knowledge §4.3 |
| V4 | assistant level ≥ every linked skill; skill level ≥ its tools' **result level** | `assistant_below_skill`, `skill_below_link` | skill draft/serve; tool server result level | tools §3.10 |
| V5 | for every reachable tool server: max(assistant level, ceilings of history-resending audience clients) ≤ **clearance** | `tool_clearance` | publish; level or audience save; skill serve; server edit | tools §5.3 |
| V6 | every reachable tool server's result level ≤ assistant level | `tool_result_level` | as V5 | tools §5.3 |
| V7 | an audience client's ceiling ≥ assistant level (else the assistant is invisible to it) | `audience_above_ceiling` | audience save; client ceiling lowered; assistant level raised | identity §4.1 |
| V8 | **residency**: the `GatewayModel` servable at the ladder of max(assistant level, ceiling of every history-resending audience client): `min(GatewayModels.Residencies) ≤ ladder`; unknown model → refused; **on publish and on a level or audience save, the model is listed by the gateway's `/v1/models` called with `x-ume-max-residency` = the level's ceiling** (gateway unreachable → refused, retry; a model whose in-ceiling targets are all drained is not listed either, gateway-changes design §3.4, so the `model_not_listed` text suggests retrying later) | `model_residency`, `model_not_listed` (409), `gateway_unavailable` (503), `gateway_key_missing` (503: the service holds no gateway key for the ceiling, identity §4.7) | publish; level save; audience save; model sync changed residency (warning on Overview; chat checks per call) | chat §8.2, simplification pass §7 |
| V9 | `sensitive` assistants only for clients with `StoresHistory = false` when `DataProtectionPolicies.ClientsWithoutHistoryOnly` | `client_stores_history` | audience save; policy change | platform §8a |
| V10 | `HistoryMode` ∈ allowed modes, `RetentionDays` ≤ level max (default ≤ max ≤ 90) | `retention_outside_policy` | assistant save; policy tightened (lists assistants) | data-model §2.1 |
| V11 | ≤ 20 collection links per assistant; each collection of the assistant's department or shared with it (`SharedWith`, `CollectionShares`, §3.2); ≤ 20 topics per link, each in the chunk tag format (processing §7) | `too_many_collections`, `collection_not_shared`, `topic_invalid` | publish; sharing narrowed | knowledge §4.2, §3.2 here |
| V12 | ≤ 20 skills per assistant, each of its department, shared with it, or a platform skill; for an assistant without tools, skill bodies ≤ 6,000 estimated tokens in total | `too_many_skills`, `skill_not_shared`, `skill_bodies_too_long` | publish; skill serve; share list narrowed | tools §3.4, §3.9 |
| V13 | lethal trifecta false — a hard refusal, no acceptance in P2 | `lethal_trifecta` | publish; skill serve; tool approval/grant; `ContainsThirdPartyText` changed | tools §5.4 |
| V14 | assistant that uses tools (a linked skill's served version links a tool): `SupportsTools`, synced price; limits ≤ `AgentDefaults` unless `Admin`, always ≤ `AgentHardCaps` | `model_no_tools`, `price_unknown`, `limit_above_default`, `limit_above_cap` | publish; skill serve (it can switch an assistant to tools); settings change | tools §6.4–§6.5 |
| V15 | not-found texts ≤ 1,000 characters each, ≤ 5 links, `https`, host matches `Settings.Chat.allowedLinkHosts` (an empty or missing list refuses every link) | `link_host_not_allowed` | publish; allow-list narrowed | chat §9.4, §10.1 |
| V16 | `RetrievalSettings` ranges: `vectorWeight`, `keywordWeight` 0–5, `candidates` 20–200 (the range retrieval accepts, `RetrievalSettings.Validated()`; there is no `efSearch`, knowledge §12.1), `maxChunksPerDocument` 1–10, `coverageMaxDistance` 0.05–1.0, `coverageMinKeywordScore` null or > 0 (weakness review §2), `recency` null or `halfLifeDays` 30–3,650, `weight` 0–0.5, `kinds` non-empty subset of the document types (weakness review §3), `expandMaxTokens` 0–4,000; saved directly (an advisory evaluation run follows) | `retrieval_settings_invalid` | collection settings save | knowledge §12.1 |
| V17 | `QuerySteps`: `retrieval.topK` 1–50, `history.maxMessages` 0–20 and `history.maxTokens` ≤ 8,000, `answer.maxOutputTokens` 64–4,096, `answer.temperature` 0–1, `answer.language` `auto`/`sv`; not built yet: `contextTokens` ≤ the model's `ContextWindow` when known, and the condense model within the V32 ceiling | `query_steps_invalid` | publish; model sync | chat §5–§6, §10.3 |
| V18 | skill version label > every existing; **serve** only after the version's gate run finished, and only if V4–V6, V12–V14 and V19 hold for every assistant whose published version links the skill | `version_not_increasing`, `gate_not_finished`, `serve_breaks_assistants` | skill publish/serve/rollback | tools §3.5 |
| V19 | skill tool links need a `ToolDepartmentGrant` for the skill's department; platform skills only `AllDepartments` tools; sharing never grants: a linking assistant of another department sees a shared skill's tool only with its own grant (warning lists the tools) | `tool_grant_missing` | skill draft; grant revoked (lists skills); share list widened | tools §3.6 |
| V20 | *retired* (skill MCP exposure vs audience ceilings: per-skill endpoints moved to P3) | – | – | – |
| V21 | clients: `StoresHistory` ⇒ ceiling ≠ `sensitive`; `mcp-client` ⇒ ceiling ≤ `internal`; `ToolPolicy` per kind default | `client_ceiling_rule` | client save | data-model `CK_Clients_*`, tools §5.1 |
| V22 | tool servers: `CK_ToolServers_Clearance`; `Hosting = hosted` (P2); approvers differ for new and unexpected definitions; system-owner approval and pre-registration only by a listed subject; `write` tools refused while `WriteToolsEnabled = false` | `toolserver_rule`, `same_approver` | server save; approval; pre-registration | tools §4.4–§4.5, §5.3 |
| V23 | keys: team in the caller's departments; scopes only assistants/collections of the key team's department; `pk` keys only `public` assistants, exact origins | `key_scope_invalid` | key save; assistant level raised (lists `pk` keys) | identity §6.4–§6.5 |
| V24 | pipeline: parameters valid against the template version's `paramsSchema`; template version supported by a running worker; the template matches the source type | `pipeline_params_invalid`, `template_unsupported` | parameters save; publish; template version change | engine (templates and parameters) |
| V25 | source settings: URL list ≤ 5,000, exact duplicates refused (canonicalisation is the worker's: `UrlCanonicalizer` runs when it lists the URLs); crawl `include`/`exclude`/`demote` compile (`re:` with `NonBacktracking`, no match timeout, connectors §5.2), `demote` factor in (0, 1), default 0.5 (weakness review §3); `maxPages` ≤ the sources schema's maximum (there is no `Settings.Crawl` cap key); `maxFileSizeMb` ≤ 50; cron 5 fields; `collectionId` immutable; `scope`/`identity` changes need the `impactToken` | `source_settings_invalid`, `impact_not_confirmed` | source save | connectors §4–§5, §9, §12.3 |
| V26 | reasons: PII clear ≥ 10 and the document's source in the caller's departments; conversation access ≥ 20 (comment reveal: the fixed one-click text) | `reason_too_short`, `not_in_scope` | the action | processing §9.5 |
| V27 | *retired* (collection floor before publish: evaluation is advisory) | – | – | – |
| V28 | deletion decisions only by `Manage` of the source's department, while the run is `awaiting-deletion-confirmation` and the proposal is the current one | `deletion_superseded` | confirm/reject | connectors §13.3 |
| V29 | data protection: history modes other than `none` refused until stored chat content is built after A1 (weakness review §4); `internal`/`sensitive` ⇒ chat encryption required; default ≤ max ≤ 90; changes apply to messages written afterwards (their expiry-month bucket); bucket-key destruction cannot be switched on before A1 is recorded as answered | `policy_rule` | policy save | data-model `CK_DataProtectionPolicies_*`, simplification pass §5 |
| V30 | `Settings` ranges as the `CK_Settings_*` checks (guard 1–100 %, min/max counts, sample sizes, …) | `setting_out_of_range` | settings save | data-model §2.1 |
| V31 | embedding profile fixed per collection (`platform-default`); other profiles only `Admin` (P3) | `profile_admin_only` | collection save | R3 |
| V32 | platform model settings `Settings.EvaluationModel`, `Settings.EvaluationJudgeModel`, `Settings.Chat.CondenseModel`, `Settings.Tools.SkillGateModel` are `GatewayModels` rows servable at most at the platform ceiling: `Eu` until M2 (on-prem models once the on-prem hardware exists, register M2) (`SkillGateModel` also `SupportsTools`) | `settings_model_residency` | settings save; model sync changed residency (warning on Overview) | processing §13.1, chat §5, tools §3.7 |
| V33 | assistant test questions: ≤ 30 per assistant; question 1–2,000 characters; expected behaviour 1–2,000; expected phrase ≤ 200; an expected document must belong to a collection the draft or published version links (unlinking it later marks the question in the report, never refuses) | `test_question_invalid`, `test_question_document_unlinked` | test-question save | §7.4 |
| V34 | publish and restore: the definition resolves (collections not deleted, skills not retired and usable by the department, the model in `GatewayModels`); description and contact are set; a draft created before the current published version (lower `Number`) is published only when confirmed | `version_link_missing`, `assistant_contact_missing`, `draft_base_changed` | publish; restore | §4.5, simplification pass §15 |
| V35 | narrowing a collection's sharing (to a lower level, or removing a department from `selected`) is refused while a published assistant of a department that loses access links it; the refusal lists the assistants and their departments | `collection_shared_in_use` | sharing save | §3.2, §4.5 |

### 4.7 Jobs for the worker

The admin holds `INSERT` on `content."Jobs"`; payloads are ids and parameters only (≤ 4 KB). Enqueue = `INSERT … ON
CONFLICT DO NOTHING` against the partial unique index on `DedupKey WHERE State IN ('queued','running')` (simplification
pass §9) and `pg_notify('rag_jobs', class)` in the same transaction as any config change it belongs to. `DepartmentId`
= the subject's department (fairness), `TeamId` = owner team (cost).

| Admin action | Class / kind | Dedup key | Priority | Result read from |
|---|---|---|---|---|
| Preview (sample pages) | `preview` `stage-preview` (≤ 5 `documentIds`, `urls` or `uploadIds`; `collectionId`; optional `embed`); for a crawl host without a profile first `preview` `site-profile` (learns it; before/after; **P2**, weakness review §1: in P1 the host's manual profile or the Sitevision preset is applied) | – | – | `Jobs.Result` + preview blobs |
| Preflight (unsaved source) | `preview` `preflight` (URL lists: first 20 URLs; the unsaved settings as an object, checked first against the sources schema, V25) | – | – | `Jobs.Result`: worst state, one `<area>.<code>` code per check (no Swedish text; the UI maps codes to text in `messages.ts`), `estimatedPages`, `cms` |
| PII highlights | `preview` `stage-preview` until the personal-data stage, `piiSpans: true` | `pii-preview:{documentId}` | – | preview blobs (24 h) |
| Publish plan | `preview` `publish-plan` (P1: every document of the collection; from effect tags in P2, weakness review §1) | `plan:{pipelineId}` | – | `Jobs.Result` |
| Evaluation run (retrieval settings saved, **Kör utvärdering**) | `eval` `on-demand` (`variants` in one job; optional `candidateSettings`, runs marked `candidate`) | `eval:{collectionId}` | – | `EvaluationRuns` |
| Generate / top up test questions | `eval` `generate-set` / `top-up-set` | `eval-generate:{collectionId}` | – | `EvaluationSets`, questions |
| Sync now / retry failed | `sync` (`Trigger = manual`, `kind` `sync`/`retry-failed`) | `sync:{sourceId}` | 10 | `Runs` |
| Upload commit | `sync` (`Trigger = upload`) | `sync-upload:{batchId}` | – | `Runs`, `Uploads` |
| Publish confirm / rollback | `sync` `reprocess` per source (`Trigger = publish`/`rollback`) | `sync:{sourceId}` (a running sync gets a follow-up) | – | `Runs`; the post-publish evaluation run (`eval` `publish-report`, dedup `eval-report:{runId}`) is enqueued by the worker when re-processing completes (§6.6) |
| Deletion decision | `command` `confirm-deletion` / `reject-deletion` (with `decidedBy`) | `deletion:{runId}` | – | `Runs.State`, `DeletionProposals` |
| Release held references | `command` `release-hold` | `release-hold:{sourceId}` | – | `Documents.HeldAt` |
| PII clearance (a decision on the document) | `command` `clear-pii-flag` (with `decidedBy`; the reason stays in the audit event) | `pii-clear:{documentId}` | – | `Documents.PiiClearedAt`/`PiiClearedCounts`; the document's `held` set released (processing §9.5) |
| Human question edit | `command` `edit-eval-question` | – | – | `EvaluationQuestions` |
| Reprocess one document | `command` `reprocess-document` (`fromStage` default `parse`) | `reprocess:{documentId}` | – | run items |
| Cancel a run | `command` `cancel-run` | `cancel:{runId}` | – | `Runs.State` |
| Site profile approve / reject (P2) / roll back / edit | `command` `approve-site-profile` / `reject-site-profile` / `rollback-site-profile` / `edit-site-profile` | `site-profile:{siteProfileId}`; an `edit-site-profile` that creates a host's first profile: `site-profile:{level}:{host}` | – | `SiteProfileVersions`, `SiteProfiles.AppliedVersionId` |
| Approve / reject a parse-profile proposal (P2) | `command` `approve-profile` / `reject-profile` | `profile:{id}` | – | `ParseProfiles` |

Payload schemas are in `contracts/jobs/v1/` (engine design §9.1):
- The preview kind is `stage-preview`.
- The worker enqueues two kinds the table does not show: `eval` `nightly` (dedup `eval-nightly:{collectionId}`) and
  `publish-report`. `eval-nightly:` is kept apart from `eval:{collectionId}`, so a queued on-demand run and the
  nightly run never drop each other.
- Commands carry `decidedBy` (the admin's `oid`), because `Jobs` has no actor column.
- `edit-site-profile` carries `baseVersion` (`site_profile.version_conflict` on mismatch).
- Only P1 kinds exist; the P2 rows above have no schema yet.

What the commands do (engine design §9.1, §12):
- `reprocess-document` re-runs the document from stored content in a `reprocess` run whose id is the job id;
  `fromStage: fetch` is refused (`reprocess.fetch_unsupported`). It bypasses the stage caches from `fromStage` (default `parse`; `embed` re-runs through the caches), and a `sync` `reprocess` with
  `force` is accepted (default `fromStage` `parse`, `fetch` refused the same way; engine §8).
- `release-hold` clears deletion holds only; a PII-held set is released by `clear-pii-flag`, without re-processing.
- `edit-site-profile` and `rollback-site-profile` take an optional `decidedBy` (absent = `system:worker`) and enqueue
  a `sync` `reprocess` (`Trigger = site-profile`, dedup `sync:{sourceId}`) per source with live HTML on the host.
  When the version changes `excludedTypes`, `identityRule` or `ignoreQueryParams`, each crawl source with live
  documents on the host gets a `sync` of kind `sync` instead (connectors §5.6).
- `confirm-deletion` sets the run back to `running` for the finalizer; `reject-deletion` sets `degraded` without a
  stored reason.
- The worker enqueues `publish-report` for `reprocess` runs with `Trigger = publish` or `rollback` only, whatever
  state they finished in. The rollback is driven as for a publish: flip the version states and
  `Pipelines.PublishedVersionId`, then enqueue a `reprocess` with `Trigger = rollback`.

Assistant publish, restore and copy enqueue nothing: they are config changes only. Assistant test runs (§7.4) are not
worker jobs either: like answer-level evaluation and the skill gate they run in `rag-admin` (`AssistantTestRunner`,
§2.2), which needs `Ume.Rag.Answering` and the gateway keys of every level; the `EvaluationRuns` row is their durable
state.

The UI polls `GET /api/jobs/{id}` (or the run/evaluation it created) every 2 s, every 5 s after 30 s, every 15 s after 5
minutes, and not while the tab is hidden; there is no push channel. A `dead` or `failed` job shows its `LastError` code
through the §4.2 table.

### 4.8 Reading `content`, `chat` and `audit`

- **Scope first**: every content query starts from config ids in the caller's scope (collections, sources,
  assistants), so a row of another department is never selected. No SQL join between `config` and `content`/`chat`
  tables: config names are resolved from the admin's snapshot.
- **Counts**: run counters are derived; the overview uses `Runs` and `Jobs` indexes only, never a `RunItems` scan.
- **Display names**: a document belongs to one source, so its upload file name (`DisplayName`) is shown only to scopes
  that see the source.
- **Chat**: metadata columns only, except the two audited decryption paths of §8. **Audit**: §9.

### 4.9 Test panel

Runs `Ume.Rag.Answering` in the admin process (identity §4.8): principal `admin-console` with the admin's `oid`, access
by the admin's role scope (assistant's department ∈ caller's departments), assistant audiences ignored, **collections
not yet published included**, attribution to the assistant's owner team (knowledge §4.2, §4.5), residency ceiling at the
assistant's level (with test history: chat §8.2's rule if the assistant has a history-resending client in its
audience). It runs the assistant's **draft** by default (the published version when there is no draft, or on request
with `target = published`), so a change is tried before **Publicera**; the draft's V-rule warnings are shown above the
answer. Nothing is stored. Query embeddings use the in-process L1 cache only, keyed with a per-process random HMAC
key. **Release coupling**: `rag-admin` and `rag-api` are built and deployed from one release (`release.yml`), and the
panel ships in the same P1 release as the chat role, so it never runs a different `Ume.Rag.Answering` than chat does.

| Input | Output (JSON, non-streamed through the chat client; ≤ 60 s by its timeouts) |
|---|---|
| `question`; optional `history[]` (≤ 10 messages, to test condense); assistant id and `target` (`draft` default, `published`); optional `collectionIds` subset | `retrieval`: searched collections (with a not-published flag), coverage `{status, bestDistance, threshold, collection}`, candidates `{rank, title, url or file name, headingPath, text (600 characters, expandable), distance, keywordRank, vectorRank, fused, cited}`, timings; `condense` `{used, skipped, question}`; `prompt` token budget per part against the limit; `answer` `{raw, delivered, filtered[] (kind, text), citations [n] → document, outcome, language}`; `gateway` `{model, residency, latencyMs, ttftMs, inputTokens, outputTokens, costUsd}`; `usesTools`; P2 `toolCalls[]` `{tool, state, durationMs, arguments (admin view), resultExcerpt}`, skill inclusion (static/progressive) |

- Rate: 20 test questions per minute per user on the full panel (the retrieval-only variant has no limit), metric
  `rag.admin.test_panel{outcome}`; no audit entry, no 6007 (the content is the caller's own department's).
- The answer is shown as **plain text** with preserved line breaks and `[n]` chips (no Markdown renderer).
- P2: an assistant that uses tools (its draft, when testing the draft) calls hosted MCP servers through the admin's in-process hub as `admin-console` (needs
  `Clients.ToolPolicy = read` for that client, §13), with the same limits; write tools stay impossible.
- Retrieval-only variant `POST /api/collections/{id}/test-retrieval` for pipeline editors and collection editors.

### 4.10 Organisation and model catalogue sync

| Sync | Source (gateway Data API, `catalog.read` key `OrgSync__DataApiKey`) | Rule |
|---|---|---|
| Departments | `GET /v1/catalog/departments` (`DataEndpoints.cs:140-142`; `memberGroups` from gateway release 2, P2, weakness review §5) | upsert by id; only changed rows written; a department missing from the gateway → `IsActive = false`; member groups replace the stored array **only when the catalogue carries the field** (gateway release 2); until then they are entered by hand (P1) and the sync leaves them alone |
| Teams | `GET /v1/catalog/teams` (`:143-145`; `code` from gateway release 2) | as departments |
| Models | `GET /v1/catalog/models`, `/providers`, `/prices` (`:149-157`) | `GatewayModels` row per chat/embedding deployment: `Kind = model`, `Residencies = [provider residency]`, latest price; `SupportsTools`/`ContextWindow` manual until the catalogue carries them (gateway-changes §7.3); the rows of disabled or no longer served deployments are deleted (`GatewayModels` has no "disabled" column), so an assistant that still names one is refused by V34/V8 on its next publish |
| Route aliases | none today (`DataEndpoints.cs:139-160`) | manual rows `Kind = route`, `Residencies` = its targets' residencies, `Source = manual`; servable at a ceiling when any target is within it; replaced by a synced `/v1/catalog/routes` when the gateway adds it. Multi-target model names are routes, so they are not in the synced catalogue (nor in the FakeGateway's) |

The mirror feeds pickers and V8's first check; V8's `/v1/models` call at save is the authoritative check, because it
asks the gateway what it will serve at that ceiling. Failures keep the last mirror; after 3 failed runs the Overview
shows "Synkningen mot AI-gatewayen fungerar inte sedan {tid}" to platform admins, metric `rag.admin.sync{kind,outcome}`.
A stale mirror narrows or keeps sign-in scope, never widens it (identity §2.6). The sync status is held in memory per
instance (`GET /api/organisation/sync`); the run history and the 3-failures banner come with `Settings` and the
Overview page (P1).

## 5. UI information architecture

### 5.1 Stack and what is copied

- Copied unchanged: toolchain and scripts, `.npmrc`, lint (incl. `eslint-plugin-vuejs-accessibility`), design system
  (`styles/*`, `components/ui/*`, `components/layout/*`, icons subset), `design-system.lock` drift report. Shared
  components with built-in English text (`AsyncState`, `DataTable`, `ConfirmDialog`, `Pagination`, `UiDrawer`, the
  layout components) are **adapted** to take their text from `labels.ts` and marked `adapted` in the lock;
  gateway-only components (`KeyStatusBadge`, `ResidencyLabel`, `AddressListField`, `SecretDialog`) arrive with keys in
  P1. `package.json` pins the gateway's resolved versions exactly and the lock file is the gateway's, so CI and
  developers use `npm ci` only (npm's `allow-remote=none` refuses to re-resolve); `vite.config.ts` allows
  `contracts/admin` for the error-code spec;
  `api/client.ts` (constant `RAG-XSRF-TOKEN`), `composables/useFormErrors.ts:8-44`, `useTableView.ts:9-54`,
  `stores/loadable.ts`, `test/*` helpers; charts `components/usage/TimeSeriesChart.vue` for evaluation trends.
- Adapted: `utils/problem.ts` (§4.2), `stores/auth.ts` (RAG roles), `router/nav.ts` + `router/index.ts` (pages below;
  same `meta.roles` guard and focus handling; title suffix "· Knowledge platform", product name N1 open); `utils/format.ts`
  and `index.html` (`lang="en"`) as the gateway's.
- New: `utils/labels.ts`, `utils/messages.ts`, `composables/useJobPoller.ts`, `components/rag/*` (classification badge,
  run state badge, evaluation report card, setup checklist, preview viewer with before/after, test panel),
  `platform/*` (the generic forms of §5.8: resource registry, schema mapper, `GenericForm`, list and form views),
  `views/*`.

### 5.2 Navigation per role

Grouped by the job people come to do, as the gateway's side menu (`docs/admin-ui.md:5-13`); items filtered by role.
Ctrl K search copied (`AppShell.vue:62`), extended with `GET /api/search`. Phone bottom bar: Översikt, Kunskapsbanker,
Assistenter, Återkoppling, Meny.

```
┌──────────────────────────┬──────────────────────────────────────────────────────────────────────────────────────┐
│ ◧ Kunskapsplattform      │  Sök (Ctrl K)                                   Förvaltning: Alla ▾   ☾   Anna A. ⎋  │
│   Umeå kommun            ├──────────────────────────────────────────────────────────────────────────────────────┤
│ ÖVERBLICK                │                                                                                      │
│   Översikt          (3)  │   <main id="main">                                                                   │
│   Karta                  │                                                                                      │
│ KUNSKAP                  │                                                                                      │
│   Kunskapsbanker         │                                                                                      │
│ ASSISTENTER              │                                                                                      │
│   Assistenter            │                                                                                      │
│   Färdigheter        P2  │                                                                                      │
│   Verktygsservrar    P2  │                                                                                      │
│ UPPFÖLJNING              │                                                                                      │
│   Återkoppling           │                                                                                      │
│   Samtalsgranskning      │                                                                                      │
│ PLATTFORM (formulär)     │                                                                                      │
│   Inställningar · Dataskydd · Klienter · Nycklar                                                                  │
│   Mallar · Assistentmallar · Webbplatsprofiler · Inbäddningsprofiler                                              │
│   Gateway-modeller · Organisation · Granskningslogg                                                               │
│ ↗ API (Scalar) · ↗ AI-gatewayens admin                                                                            │
└──────────────────────────┴──────────────────────────────────────────────────────────────────────────────────────┘
```

The English UI groups the menu as **Home** (Översikt: the selected department's setup checklists, attention items and
a one-line summary with its contacts) and **Map**; **Knowledge bases** with the tabs *Ours*, *Shared with us* and *All*
(`view=`, §4.5); **Assistants**; **Feedback**; then **Organisation**, **Platform** and **Audit log**. The department
picker in the header filters every list; a user with one department never sees it.

Start page: Översikt (a reviewer-only user starts on Återkoppling). Badges: Översikt counts attention items (gateway
`badge` pattern, `nav.ts:25`). No e-mail: awaiting deletions, worse evaluation reports, site-profile proposals, P2
drift and pending approvals appear as attention cards and badges, plus SIEM events for the security-relevant ones.

### 5.3 Pages by phase

| Page | P1 | P2 | P3 |
|---|---|---|---|
| Översikt | attention items, setup checklist, sync health per department, queue age, org/model sync | drift, approvals, parse-profile proposals | trends |
| Karta | P1b: the Map as a table (§5.5.6) | the drawing, skills and workspaces in the map | – |
| Kunskapsbanker | list; detail tabs Översikt (checklist, sources, counts, state, contact), Källor, Dokument (incl. personuppgifter), Ämnen (Topics), Testfrågor och kvalitet, Sökinställningar, Delning (Sharing); the list's *Shared with us* and *All* tabs | Historik tab, coverage-threshold histogram and proposal | analytics |
| Källa | URL list, crawl, upload forms with template parameters; preflight; preview with site-profile before/after; impact; runs; crawl report; files; quarantine; deletions; held | SharePoint with preflight, crawl hierarchy tree, parameter diff | more connectors |
| Publicering | plan, re-process progress, evaluation report, keep/roll back | – | – |
| Assistenter | create, draft form with description and contact, test panel on the draft, Publicera, Versioner (restore), audiences (clients), retention, stats, archive | create from a template, copy, version diff, Testfrågor with the test report, latest answer evaluation (weakness review §1); skills (with **Använder verktyg** when one carries tools), limits, widget audiences | risk acceptance |
| Färdigheter, Verktygsservrar | – | tools §3, §4 pages | import/export |
| Återkoppling | ratings, import health; comment reveal when A1 allows stored content (weakness review §4) | rewrite into test question | trends |
| Samtalsgranskning | – (when A1 allows stored content, weakness review §4: conversation behind a feedback item, access log) | search by assistant and period, `sensitive` two-person flow | user notification |
| Platform objects | generic forms (§5.8) | dedicated pages where generic forms prove too thin; Tolkningsförslag | OBO configuration, profile changes |
| Granskningslogg | list with filters | field-level diff viewer | – |

### 5.4 The pilot department admin's journey (answered 2)

The platform team pairs with the department's admins for the first collection; the pages must work without them
afterwards. Every step is reachable from the **setup checklist** card ("Kom igång"), computed from state (no stored
progress flags), which disappears when the collection has a published assistant in Open WebUI.

| # | Step (Swedish UI) | Page | Defaults and guidance | Guard |
|---|---|---|---|---|
| 1 | Logga in | Översikt | empty state "Här bygger du kunskapsbanker och assistenter för din förvaltning" + **Skapa kunskapsbank** | role and department from sign-in |
| 2 | Skapa kunskapsbank | drawer | name → slug; contact (name, e-mail; the creator by default); no team field (the owner team is the creator's first team in the department, changeable under ⋯); **informationsklass** as three explained options, none preselected ("Vem får läsa innehållet?") | V1–V2 later |
| 3 | Lägg till källa | source drawer, type cards *Adresslista*, *Webbplats*, *Filer* | crawl: start URL; include `host/**`, default excludes, 10,000 pages, depth 10, incremental by sitemap `lastmod` once the host's site profile marks it reliable (umea.se: weekly full traversals until the 500-URL `lastmod` sample passes, weakness review §3), weekly; optional **Nedprioritera** patterns for archive sections (factor 0.5); the first `website` source of a collection turns on age decay for PDFs (730 days, weight 0.15) unless the collection already has `recency` set, shown in the drawer and editable in Sökinställningar (weakness review §3); URL list nightly; template chosen by type, its parameters under "Avancerat" | V25 |
| 4 | Kontrollera | same drawer | preflight: reachability, robots status, sitemap found, estimated pages, egress, "samma webbplats hämtas redan av X" (shared crawl frontier, P2; weakness review §1), CMS recognised ("Sitevision") | fail blocks save; warn explains |
| 5 | Förhandsgranska | preview panel | up to 5 URLs or uploaded files; for a crawl source the site profile the worker would apply (P1: the manual Sitevision preset; learned from P2), shown as **Före / Efter** text per sample page (boilerplate removed, breadcrumb, date); each stage's output (text, sections, chunks with token counts) | preview isolated |
| 6 | Spara och hämta | source page → run page | saves, enqueues the first sync; live counters, failures by reason in plain Swedish | – |
| 7 | Testfrågor | Testfrågor och kvalitet | generated after the first runs, cost shown before (at the collection's ceiling, processing §13.1); own questions with expected documents | – |
| 8 | Publicera kunskapsbanken | collection page | latest evaluation report shown ("Träffsäkerhet 0,78 · 86 frågor"); **Publicera** | – |
| 9 | Skapa assistent | assistant drawer → assistant page | pick an **assistant template** ("Allmän rådgivning" preselected; P2, weakness review §1: in P1 the draft starts from `Settings.Chat` defaults); name, description, contact person; collections (the new one preselected; unpublished ones work in Testa only), model picker showing only models servable for the level, not-found texts and contact link from the template; saved as **utkast** | warnings only (V3, V8, V15 at publish) |
| 10 | Testa | Testa tab | runs the draft; sample questions from the evaluation set as one-click prompts; sources, coverage, raw vs delivered answer; **Spara som testfråga** (P2) turns a tried question into a test question with the expected behaviour typed in | V33 |
| 11 | Publicera | assistant page | **Publicera**; the model is checked against the gateway (V8); from P2 the test-question report follows on the Testfrågor tab (advisory, §7.4) | V3–V17, V34 |
| 12 | Gör tillgänglig | Målgrupp | pick client "Open WebUI (pilot)"; residency check explained if refused ("Modellen kör utanför EU; välj en modell inom EU eller kommunens servrar") | V7–V9 |
| 13 | Öppna i Open WebUI | assistant page | link from the client's `PortalUrl`; the model appears under its slug | API lists it by audience |

### 5.5 Key pages (wireframe level)

#### 5.5.1 Översikt

```
Översikt                                                                    Förvaltning: Stadsbyggnad ▾
┌ Behöver åtgärd (3) ───────────────────────────────────────────────────────────────────────────────────────┐
│ ⚠ Borttagning väntar på beslut: Webbplats umea.se/bygga – 212 av 840 dokument (25 %)        [Granska]    │
│ ⚠ Utvärderingen blev sämre efter publicering: Webbplats umea.se/bygga (−6 punkter)          [Visa]       │
│ ● 4 dokument med personuppgifter kan inte visas i offentliga kunskapsbanker                  [Visa]       │
└───────────────────────────────────────────────────────────────────────────────────────────────────────────┘
┌ Hämtningar senaste dygnet ─────────────┐ ┌ Kö ───────────────────────┐ ┌ Assistenter, 7 dagar ─────────────┐
│ 12 lyckade · 1 med fel · 0 misslyckade │ │ 0 väntande, äldsta –      │ │ 1 240 frågor · 6 % utan svar      │
│ [Visa körningar]                        │ │ Bearbetning: 3 st, 2 min │ │ 👍 41  👎 9   [Återkoppling]       │
└────────────────────────────────────────┘ └───────────────────────────┘ └───────────────────────────────────┘
```

Attention item sources: runs `awaiting-deletion-confirmation`; evaluation reports ≥ 5 points worse than the previous run
(after a publish or nightly); PII-flagged documents in `public` collections; degraded runs; sources with ≥ 3 failed
runs; sources with N (default 3) runs without a complete traversal (simplification pass §2); feedback import failing ≥ 4
runs; org or model sync failing; template versions without a supporting worker; from P2 (weakness review §1): site-profile proposals (platform
admins) and published assistants without a message in 90 days (`AssistantDailyTotals`; published ≥ 90 days ago), with
**Arkivera** and **Behåll** (dismissed for 90 days) (simplification pass §15); a test report on an assistant's new
version with more questions not as expected than the previous version's (P2); P2 drift and pending approvals.

#### 5.5.2 Kunskapsbank

```
Kunskapsbanker › Bygglov                                       [Intern] [Ej publicerad]   [Publicera] [⋯]
Översikt  Källor (3)  Dokument (1 204)  Ämnen (4)  Testfrågor och kvalitet  Sökinställningar  Delning
┌ Kom igång ──────────────────────────────────────────┐ ┌ Läge ─────────────────────────────────────────────┐
│ ✓ Källa tillagd           ✓ Första hämtningen klar  │ │ Ej publicerad: syns bara i Testa.                 │
│ ✓ Testfrågor skapade (86) ◐ Utvärdering pågår       │ │ Senast hämtad 30 okt. 02:14 · 1 204 dokument       │
│ ○ Publicera   ○ Skapa assistent   ○ Gör tillgänglig  │ │ Används av: Bygglovsassistenten (utkast)           │
└─────────────────────────────────────────────────────┘ └───────────────────────────────────────────────────┘
Källor                                                                                       [Lägg till källa]
│ Namn                  │ Typ        │ Senaste körning        │ Dokument │ Nästa       │                      │
│ umea.se/bygga         │ Webbplats  │ ✓ 30 okt. 01:12        │ 840      │ lör 01:00   │ [Hämta nu] [⋯]       │
│ Blanketter            │ Filer      │ ⚠ 2 misslyckade        │ 41       │ –           │ [Ladda upp] [⋯]      │
```

Sharing and Topics tabs (English UI):

```
Knowledge bases › umea.se (Communications)                 [Public] [Published]        [Publish] [⋯]
Overview  Sources (1)  Documents (2 323)  Topics (9)  Quality  Search settings  Sharing
┌ Sharing ──────────────────────────────────────────────────────────────────────────────────────────┐
│ Who may use this knowledge base in their assistants?                                              │
│ ( ) Only Communications   ( ) Selected departments [ … ]   (•) Whole municipality                 │
│ Shared use is read-only: other departments use the content in their assistants, not the sources.  │
│ Used by 4 assistants in 3 departments                                                             │
│ │ Assistant            │ Department              │ Topics                 │ Status     │          │
│ │ Permits assistant    │ Building & Environment  │ Building and housing   │ Published  │          │
│ │ Customer service     │ Contact centre          │ all                    │ Draft      │          │
│                                                                                  [Save sharing]   │
└───────────────────────────────────────────────────────────────────────────────────────────────────┘
Topics (set by path rules on the sources, Processing › Topics)
│ Topic                │ Rule                               │ Documents │
│ building-and-housing │ www.umea.se/byggaboochmiljo/**     │ 412       │
│ politics             │ www.umea.se/kommunochpolitik/**    │ 655       │
```

A department the knowledge base is shared with opens it read-only: Overview (owner, contact, level, document count)
and Topics, nothing else.

#### 5.5.3 Källa (webbplats) and the deletion decision

```
Webbplats: umea.se/bygga                                                    [Hämta nu] [Pausa] [⋯]
Inställningar  Körningar  Genomsökningsrapport  Borttagningar (1)  Behållna (0)
┌ Startadresser ───────────────────────────────────────────────────────────────────────────────────────────┐
│ https://www.umea.se/bygga                                                                       [Ta bort] │
│ Ta med   www.umea.se/bygga/**                     Uteslut  **/sok?*  **?*print=*  [+]                      │
│ Nedprioritera  www.umea.se/bygga/**/arkiv/**  ×0,5  [+]                                                    │
│ Högst 10 000 sidor · djup 10 · ändrade sidor enligt webbplatskartan · lördagar 01:00                      │
│ Webbplatsprofil: Sitevision, version 3 (inlärd 30 okt.)                          [Förhandsgranska]         │
│ ▸ Avancerat (mallinställningar: textavsnittens längd, överlapp, …)                                         │
└───────────────────────────────────────────────────────────────────────────────────────────────────────────┘
  ⓘ Ändringen gör att 38 dokument hamnar utanför avgränsningen och tas bort direkt.
    Påverkar 2 assistenter: Bygglovsassistenten, Kundtjänst bygg (utkast).             [Avbryt] [Spara ändå]

Borttagning väntar på beslut · körning 30 okt. 01:12
  212 av 840 dokument (25 %) hittades inte. Gränsen är 20 % (minst 5).
  Varför: fullständig genomsökning; 3 sidor gav 404; 209 nåddes inte (förra körningen nådde 1 049 sidor, nu 631).
  Exempel (50)  │ https://www.umea.se/bygga/…/altan │ senast sedd 23 okt. │ 200 │    [Ladda ner alla (CSV)]
  [Behåll dokumenten]  [Ta bort 212 dokument]
```

#### 5.5.4 Assistent with test panel

The wireframe shows the P2 page. In P1 ([weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer)) the tab Testfrågor, **Jämför** in Versioner, **Kopiera**,
**Spara som testfråga**, the test summaries and the answer evaluation on Statistik are absent; Publicera utkastet shows
the warnings only.

```
Assistenter › Bygglovsassistenten  (modellnamn: bygglov)   [Intern] [Publicerad: version 3] [Utkast ändrat 30 okt.]
Svarar på frågor om bygglov, anmälan och avgifter. Kontakt: Anna Andersson, bygglov@umea.se  [Ändra]
Använder verktyg (P2)                                          [Publicera utkastet]  [⋯ Kopiera, Arkivera]
Utkast  Testa  Testfrågor (8)  Versioner (3)  Målgrupp  Lagring  Statistik
┌ Utkast (ändrat mot version 3) ───────────────────┐ ┌ Testa (kör utkastet ▾) ────────────────────────────┐
│ Instruktioner  [textarea, mall ifylld]           │ │ Fråga [Behöver jag bygglov för en altan?] [Fråga]  │
│ Kunskapsbanker [Bygglov ×] [Detaljplaner ×] [+]  │ │ Förslag: · Vad kostar ett bygglov? · …             │
│ Färdigheter (P2) [Bygglov ×] [+]                 │ │ Träffar (täckning: god, 0,31 ≤ 0,50)               │
│ Modell  [ume/chat-eu ▾] EU · 128k                │ │ 1 Altaner och uteplatser – umea.se/…  0,31  citerad│
│ När svar saknas (sv) [Jag hittar inget …]        │ │ Svar som användaren ser               | Råtext     │
│ När svar saknas (en) [I cannot find …]           │ │ Nej, om altanen är lägre än 1,8 m … [1]            │
│ Kontaktlänkar [Kontaktcenter 090-16 10 00]       │ │ Modell ume/chat-eu · EU · 2,1 s · 0,0012 USD       │
│ ▸ Avancerat: följdfrågor, historik, längd        │ │ [Spara som testfråga]                              │
│ ⚠ 1 varning (visas vid Publicera)                │ │                                                    │
│ [Spara utkast]  [Släng utkastet]                 │ │                                                    │
└──────────────────────────────────────────────────┘ └────────────────────────────────────────────────────┘
```

- **Knowledge** (Kunskapsbanker in the draft): each link shows the knowledge base, its owning department when it is
  shared ("umea.se (Communications, shared)"), and its topics ("all topics", or chips with **+ topic**); **Add
  knowledge base** opens a picker with *Ours*, *Shared with us* and *All*, showing level, document count and topics.
- **Who can use it** (Målgrupp) says in plain words what an audience means: "Open WebUI (pilot): everyone who can sign
  in to it"; department and group audiences follow the W3 spike and E1.
- **Badges**: `[Utkast]` alone before the first publish; `[Publicerad: version n]` afterwards, plus `[Utkast ändrat …]`
  while a draft exists; `[Arkiverad]` when archived. **Använder verktyg** is a derived label (P2), never a
  setting: it appears when a linked skill's served version carries tools, and the Färdigheter field explains that such
  a skill switches the assistant to the agent loop. Description and contact (name and e-mail) are the assistant's own
  fields, saved directly, like Målgrupp and Lagring; everything in the Utkast tab is versioned.
- **Publicera utkastet** opens a dialog with the diff against the published version (P2), the remaining warnings (refusals
  block) and the cost of the test run (P2); after publish (P2) the Testfrågor tab shows the
  report as it arrives.
- **Versioner**: a list (number, published by, when, "återställd från version n", test summary "7 av 8 som
  förväntat", P2); **Jämför** (P2) shows the structured diff (settings by name, collections and skills added or removed, a line
  diff of the instructions with additions and removals as text marked `<ins>`/`<del>`, not by colour alone);
  **Återställ** on any earlier version publishes it again as a new version after one confirmation ("Version 2
  publiceras igen som en ny version. Ditt utkast finns kvar.").
- **Testfrågor** (P2): the set (question, expected behaviour, expected document or phrase), **Lägg till**, **Kör
  testfrågor** (on the draft or the published version), and the latest report as the §7.2 card (§7.4).
- **Kopiera** (in ⋯, P2) asks for name, slug, department and owner team and opens the copy's draft.

The *Statistik* tab shows the date of last use and, from P2 (weakness review §1), the **latest answer evaluation**: faithfulness and citation precision of the last
`answers` run with question counts, compared with the previous run, next to the live `uncited_answer` rate
(processing §14.8, `AnswerEvaluationService` §2.2).

#### 5.5.5 Återkoppling

```
Återkoppling                                Period: 7 dagar ▾  Assistent: Alla ▾  Betyg: 👎 ▾  [Bara med kommentar]
│ Tid            │ Assistent           │ Betyg │ Skäl                  │ Utfall       │ Citerade dokument  │          │
│ 30 okt. 10:42  │ Bygglovsassistenten │ 👎    │ Inte korrekt          │ Besvarad     │ Altaner och …      │ [Läs kommentar] │
│ 30 okt. 09:15  │ Bygglovsassistenten │ 👎    │ Annat                 │ Inget svar   │ –                  │ [Läs kommentar] │
   Läs kommentar → dialog: "Du läser en användares kommentar. Läsningen loggas med skälet: Granskning av återkoppling."
   [Avbryt] [Läs kommentar]   (granskare ser även [Läs samtalet], som kräver eget skäl)
Import från Open WebUI: senast 10:45, lyckad · 2 kunde inte kopplas till ett svar senaste dygnet
```

#### 5.5.6 Karta (department map; P1b as a table)

P1b builds the **table**; the drawing below follows later. The table has one row per department → knowledge base →
assistant → audience path, with the responsible contact of each, and filters for department and object:

```
Map                                          Department: All ▾   Show: Everything ▾   [Table] [Drawing]
│ Department      │ Knowledge base     │ Shared       │ Assistant          │ Channel            │ Responsible  │
│ Communications  │ umea.se            │ municipality │ –                  │ –                  │ K. Lund      │
│ Building & Env. │ umea.se ↗ (shared) │ –            │ Permits assistant  │ Open WebUI (pilot) │ A. Andersson │
│ Building & Env. │ Permits forms      │ –            │ Permits assistant  │ Open WebUI (pilot) │ A. Andersson │
```

The drawing:

```
Karta · Stadsbyggnad                                                  Visa: [Allt ▾]  [Tabell]
Källor                    Kunskapsbanker               Assistenter                       Målgrupper
umea.se/bygga  ─────────▶ Bygglov (Intern) ──────────▶ Bygglovsassistenten (v3) ───────▶ Open WebUI (pilot)
Blanketter     ─────────▶                    └───────▶ Kundtjänst bygg (utkast)          –
umea.se/plan   ─────────▶ Detaljplaner ─────────────▶ Bygglovsassistenten (v3)
                          Avfall (delad från Teknik) ─▶ Kundtjänst bygg (utkast)
```

One column per stage, each item a link to its page; selecting an item highlights what it feeds and what feeds it. The
lines are drawn as plain SVG next to lists that carry the same relations as text ("matar: …", "används av: …"), and
**Tabell** shows one row per source → collection → assistant → audience path, so nothing depends on the drawing (no
graph library, §10.2). Shared collections appear with their owning department; assistants show their published
version, draft-only links are marked "(utkast)". Source: `GET /api/organisation/departments/{id}/map`.

### 5.6 Empty and error states

| Situation | Text and action |
|---|---|
| No collection | "Here you build knowledge bases and assistants for your department. Start by creating a knowledge base." [Create knowledge base] |
| No source | "The knowledge base has no content yet. Add a website, a list of addresses or files." [Add source] |
| No run yet | "Nothing has been fetched yet. The first fetch starts when you save the source." |
| Not published | "Not published: the knowledge base is only visible in Test until you publish it." |
| No test questions | "Test questions are created automatically when the first fetch is done (about 10–20 minutes)." |
| Assistant without test questions (P2) | "The assistant has no test questions yet. Add a few questions it should handle, and they run every time you publish." [Add test question] |
| No feedback | "No feedback yet. It arrives when users give a thumbs up or down in Open WebUI." |
| No role | copied no-access page: "Your role does not give access to this page. Contact the platform team if you need it." |
| Load failure | copied `AsyncState` with "Try again" (`components/ui/AsyncState.vue:14`) |

### 5.7 Wording conventions

- Plain English for non-specialists: "you", short sentences, verbs on buttons ("Publish", "Fetch now", "Keep the
  documents"), sentence case; technical terms (`recall@10`, `OCR`, `robots.txt`) are explained once. Swedish names and
  content (departments, collections, page titles, chunks) are shown as written and marked `lang="sv"`.
- Every error says what happened and what to do; destructive confirmations name the object, the count and the
  consequence, and the button repeats the action ("Remove 212 documents").
- Dates, numbers and times as the gateway's `utils/format.ts`, time zone Europe/Stockholm; money stays USD.
- Every UI string lives in `utils/labels.ts` or `utils/messages.ts`, so a Swedish translation later (Decisions, "Admin UI in
  English") touches only those files. Swedish page, button and step names quoted elsewhere in this document (Översikt,
  Lägg till källa, Kom igång, …) are the Swedish terms of that translation; the English UI uses the English term.
- One glossary, enforced in review (`utils/labels.ts` is the single source of these words). The English UI term is the
  concept in plain words, except: collection → "knowledge base", chunk → "passage", pipeline → "processing", sync now
  → "fetch now", PII → "personal data". The Swedish column is the later translation:

| Concept (code) | Swedish term | Concept (code) | Swedish term |
|---|---|---|---|
| collection | kunskapsbank | evaluation set / question | testfrågor / testfråga |
| source: `url-list`, `crawl`, `upload`, `sharepoint` | källa: adresslista, webbplats, filer, SharePoint | evaluation report | utvärdering |
| document / chunk | dokument / textavsnitt | recall@10 | träffsäkerhet (recall@10) |
| pipeline / template / parameters | bearbetning / mall / mallinställningar | coverage threshold | täckningsgräns |
| draft / published | utkast / publicerad | not published | ej publicerad |
| run / sync now | körning / hämta nu | deletion guard / held | borttagningsspärr / behållna |
| preview / preflight | förhandsgranska / kontrollera | PII / clear | personuppgifter / godkänn för offentlig |
| site profile | webbplatsprofil | share list | delning |
| assistant / audience | assistent / målgrupp | skill / tool server / grant | färdighet / verktygsserver / behörighet |
| feedback / comment / reason | återkoppling / kommentar / skäl | conversation / reviewer | samtal / granskare |
| `public` / `internal` / `sensitive` | offentlig / intern / känslig | `OnPrem` / `Eu` / `External` | kommunens servrar / EU / utanför EU |
| department / team | förvaltning / team | audit log | granskningslogg |
| assistant version / published version / draft | version / publicerad version / utkast | restore / copy / archive | återställ / kopiera / arkivera |
| assistant test question / expected behaviour | testfråga / förväntat beteende | uses tools (derived) | använder verktyg |
| assistant template / department map | assistentmall / karta | owner team | ägarteam |
| topic | ämne | sharing level: only our department / selected departments / whole municipality | delning: bara vår förvaltning / valda förvaltningar / hela kommunen |
| responsible (contact) | ansvarig | shared with us | delade med oss |
| `demote` pattern (weakness review §3) | nedprioritera | age decay `recency` (weakness review §3) | åldersavtagning |

### 5.8 Generic forms for platform objects (P1)

Platform-admin objects get no dedicated pages in P1 (reasoning: simplification pass §10). In P1a they are set up
through Scalar only; the generic forms below come in P1b (Decisions "P1a plan answers"): settings sections, data protection,
clients, keys, templates, assistant templates (simplification pass §15; P2, weakness review §1), site profiles, embedding profiles (read),
gateway models, organisation mirror (read); P2 department settings (owner-team scoping, also for department admins).

- One **Plattformsobjekt** page (`/plattform`, platform admins) lists the resources; each opens a list and a form in a
  drawer. A registry in the UI (`platform/resources.ts`) names each resource's list, create, update and action
  endpoints and its list columns; the form's fields come from that operation's request schema in the OpenAPI document
  (`/openapi/v1.json`, generated from the same DataAnnotations the API validates with, so the form and the server
  agree; Admin-only, the audience of these forms). Controls follow the table of §6.3; `jsonb` members (e.g. a site
  profile, `Settings.Crawl`) are a JSON text area validated by the server. A new, rotated or revealed key's secret is
  shown once with a copy button.
- Labels are the schema `title` (else the property name) and `description` as help.
- Actions that are not CRUD (rotate a key, run a sync, approve or roll back a site profile) are buttons from a small
  per-resource action list; anything rarer is done in Scalar (`/openapi/v1.json`).
- Site profiles: the list shows host, preset, version, state (applied/proposal), sources using it; the detail shows the
  version JSON, the proposal's check results and the preview viewer's before/after texts, with **Godkänn**,
  **Avvisa** and **Återställ till version …**. In P1 (weakness review §1) every version is a manual, hand-checked JSON
  edit (the umea.se Sitevision preset first): no proposals, so only editing and **Återställ**.
- The same concurrency (`version`), error codes, audit and accessibility rules apply.

## 6. Pipelines

### 6.1 Template parameters on the source

- A pipeline is a template version + a parameter set (versioned JSON); each source type has one linear pipeline
  (simplification pass §2). The source form shows the template's parameters under "Avancerat", generated from the
  template's `paramsSchema` (§6.3), with the template's defaults; most departments never open it.
- On a **new** source the pipeline is published with the source (no documents: plan empty, no evaluation yet). On an
  existing source a changed parameter opens the publish dialog (§6.6); the source page explains "Ändringen gör att 840
  dokument delas upp på nytt (cirka 25 minuter, 0,00 USD)", from the effect tags of the changed parameters (P2,
  weakness review §1; in P1 publish re-processes every document of the collection and the estimate counts them all, with
  unchanged stages free from the per-stage caches).
- `rag-pipeline-editor` may change only the parameters; other source settings need `Edit`.

### 6.2 Node editor

Removed (simplification pass): pipelines are template + parameters; there are no user-added nodes, ports or locks to
edit (§16).

### 6.3 Forms generated from schemas

`FormModel` (in `Ume.Rag.Pipelines`, so the server validates exactly what the UI offers) turns a JSON Schema 2020-12
into a form description for template parameters; the generic forms (§5.8) map the OpenAPI request schemas in the UI
(`platform/schema.ts`) with the same table. Both render with the copied `components/ui` fields. No JSON Schema form
library and no browser-side validator: the server validates.

| Schema | Control |
|---|---|
| `boolean` | `UiSwitch` |
| `enum` (≤ 5) / (> 5) | `SegmentedControl` / `SelectField`, labels from `x-labels.sv` |
| `integer`/`number` with `minimum`/`maximum` | `TextField` numeric with unit from `x-unit` |
| `string` (+ `pattern`, `maxLength`) | `TextField` |
| `array` of strings | `AddressListField`-style list editor (gateway component) |
| `object` with known properties | `FieldGroup` |
| `array` of objects | repeating `FieldGroup` rows with add/remove/move buttons (no drag) |
| anything else | a JSON text area for that member, with a note |

Template parameter labels and help come from `x-labels`/`x-help`; the UI shows their `en` text and `sv` is kept for the later translation (§5.7); a missing label fails a CI check of the
templates (`Ume.Rag.Pipelines.Tests`).

### 6.4 Preview

Preview runs on the form's values saved as the pipeline's draft version: the `stage-preview` payload names a
`pipelineVersionId` (engine design §9.1), and draft pipeline versions come with pipeline publish (§6.6). The result view: documents as tabs, each with
a stage strip (state, duration, cache hit, cost) and the selected stage's output (parsed Markdown with page markers,
sections, chunks with heading path and token count, PII counts). For a crawl source the first tab is **Före / Efter**:
the raw page text next to the extracted main text under the site profile the worker learned for the host or would apply
(preset or existing host profile), with the profile's version, CMS preset, breadcrumb and date found; a new source on a
host without a profile shows the profile learned from the preview sample (learning P2, weakness review §1; in P1 the manual
profile or the Sitevision preset). Changing a parameter re-runs from the first
affected stage.

### 6.5 Diff

Removed (simplification pass): no version diff; a parameter diff is a P2 option if needed (§16).

### 6.6 Publish flow

```
form change ──Publicera…──▶ plan (dialog) ──Bekräfta──▶ re-process (new chunk sets next to the live ones,
                               │                         swapped per document)
                            Avbryt                               │
                                                                 ▼
                                    evaluation report (full collection, vs the previous run)
                                                                 │
                                          Behåll (nothing to do)   │   Återställ (one click, ≤ 14 days)
```

1. `POST /api/pipelines/{id}/publications`: V24; enqueue `publish-plan`; the publication's id is the plan job's id
   (`GET /api/publications/{id}` reads the job's result with the draft and the pipeline version it was planned
   against). Nothing is frozen; the confirm carries the pipeline's `version` and is refused (409) if it changed since
   the plan. **Copy on write:** platform pipelines (`DepartmentId` null) are published by `rag-platform-admin` only (403
   `admin_only` otherwise). A department user's first publication on a platform pipeline creates a department pipeline
   whose version 1 is the platform pipeline's published version, re-points the collection's sources of that template to
   it, and publishes the draft there, in one config transaction; later publications of that collection go to the
   department pipeline.
2. Dialog: **Plan** ("1 240 dokument: 1 180 från steget Dela upp (ingen ny tolkning, ~25 min), 60 från Tolka (~900 sidor,
   ~1 h); uppskattad kostnad 0,00 USD"); **Påverkade kunskapsbanker och assistenter**.
   The per-stage split comes from effect tags and is **P2** (weakness review §1); in P1 the plan lists every document
   of the collection as re-processed, with unchanged stages free from the per-stage caches.
3. **Bekräfta**: one config transaction (§4.4) publishes the version, audits `pipeline.publish`, and enqueues one
   `reprocess` sync per source.
4. Progress is shown on the publish screen; live answers keep using the old chunk sets until each document's swap.
   When re-processing completes, the worker runs the `eval` `publish-report` on the full collection; its **report** (§7.2)
   appears on the publish screen and the collection page.
5. **Behåll** dismisses the card; **Återställ** rolls back (§6.7). Nothing blocks either choice.
6. Templates: platform admins edit and publish template versions through the generic form; a new template version
   never re-processes sources by itself. A source moves to the latest template version when its parameters are next
   published; the plan names the template change.

### 6.7 Rollback

**Återställ** on the report or on any earlier version: the confirmation names the version and the re-processing
("inom 14 dagar: de tidigare textavsnitten tas tillbaka, ingen ny tolkning"); never blocked; an evaluation run follows
as after a publish.

### 6.8 Canvas

Removed (simplification pass): the Vue Flow canvas is dropped from the roadmap; reconsidered only if editors
demonstrably need custom flows (§16).

## 7. Evaluation pages

### 7.1 Testfrågor och kvalitet (per collection)

- Set: size per layer (`generated`, `human`, P2 `skill`/`feedback`), stale and retired counts, last generation (model,
  cost), **Skapa fler** with the forecast (processing §13.1). Generated sets run at the collection's ceiling (processing
  §13.1); the `human` gold set stays the headline metric.
- Questions table (keyset): question, layer, expected documents (titles), last rank, state; editors add/edit/retire
  human questions (expected documents picked by title search); generated questions can be edited (become human) or
  retired.
- Reports: latest runs (after publish, nightly, after a retrieval-settings change, on demand **Kör utvärdering**), each
  as the §7.2 card; trend of recall@10 and MRR (`TimeSeriesChart`, table view); per-question drill-down (rank per run).
- **Headline metric** ([weakness review §2](../weakness-review.md#2-nothing-proves-the-answers-are-good-and-not-found-is-effectively-off-at-launch)): the card and the trend lead with the `human` layer (the
  pilot gold set: recall@10 on answerable questions, false not-found, unanswerable questions answered "not found");
  the generated layer follows as a regression signal only.
- **Coverage-threshold proposal** (P1, weakness review §2): the run proposes `coverageMaxDistance` and
  `coverageMinKeywordScore` as the values that best separate the `human` answerable and unanswerable questions
  (processing §13.6, knowledge design §7), shown as numbers on the card; the editor saves them in Sökinställningar. The
  histogram stays P2 (§16). Until `coverageMinKeywordScore` is set, not-found is decided by the vector side alone.
- Retrieval settings form (Sökinställningar tab): all values within V16 ranges saved directly; the following
  evaluation report shows the effect; the previous values can be restored from the report. It includes **Åldersavtagning**
  (`recency`: on/off, half-life in days, weight, document types; weakness review §3), whose effect the gold set's
  temporal questions show.

### 7.2 Evaluation report card (publish, collection, assistant; P2 skill)

```
Utvärdering · Webbplats umea.se/bygga, publicering 30 okt. 11:02 (jämfört med 29 okt.)
  Testfrågor från verksamheten (human) · 140 frågor
    Träffsäkerhet (recall@10)  0,81 → 0,79   Felaktigt "hittar inget" 6 %   Ovidkommande frågor besvarade "hittar inget" 84 %
  Genererade frågor (regression)
    Träffsäkerhet (recall@10)  0,74 → 0,68   (−6 punkter)    MRR 0,61 → 0,55
  86 frågor · 9 sämre, 3 bättre · hela kunskapsbanken
  ▸ Frågor som blev sämre (9)   ▸ Frågor som blev bättre (3)   ▸ Om utvärderingen
  [Behåll]  [Återställ till förra versionen]
```

"Om utvärderingen" explains: the report is advisory and stops nothing; differences of a few points can be chance;
below 20 questions it says "för få frågor för att jämföra". The skill gate's report (tools §3.7) uses the same card
per model with tool use and restraint lines and **Servera** instead of Behåll/Återställ. The assistant test report
(§7.4) uses it with the counts of §7.4.

### 7.3 Overrides

Removed (simplification pass): no gate, so no overrides (§16).

### 7.4 Testfrågor per assistent (simplification pass §15)

**Later (P2):** per-assistant test questions (weakness review §1).

- **Set**: `AssistantTestQuestions` per assistant (V33, ordered by `Ordinal`): question, expected behaviour as free text ("Ska hänvisa till
  kontaktcenter och inte ange någon avgift"), optional expected document (picked by title in the linked collections)
  and optional expected phrase. Added on the Testfrågor tab, from **Spara som testfråga** in Testa, or from a feedback
  row (P2, "rewrite into test question"). The set belongs to the assistant, not to a version, so every version is
  measured against the same questions; copying an assistant copies its set.
- **When**: after every publish and restore (on the new published version), and on demand with **Kör testfrågor** on
  the draft or the published version. One run per assistant at a time; an assistant without questions gets no run.
- **How**: `AssistantTestRunner` in `rag-admin` (§2.2) runs each question once through `Ume.Rag.Answering` exactly as
  the test panel does (principal `admin-console`, the target version's definition, residency at the assistant's level,
  temperature as configured, `x-ume-step: assistant-test`), then:
  - **expected document**: cited or not (deterministic, from the answer's citations);
  - **expected phrase**: present in the delivered answer or not (case-insensitive, after Unicode normalisation);
  - **expected behaviour**: the answer judge (`Settings.EvaluationJudgeModel`, on-prem, V32; the prompt family of the
    `answers` run, processing §14.8) gets the question, the expected behaviour, the delivered answer and the cited
    titles, and returns `pass`, `fail` or `unsure` with a short note.
  A question passes ("som förväntat") when every check it has passes. Stored (data-model §2.2): an `EvaluationRuns`
  row (`Kind = assistant-tests`, `SubjectId` = assistant, `SubjectVersion` = the version number, a draft's included,
  `ComparedRunId` = the previous published version's last run; the published version's `TestRunId` points at it) and
  one `EvaluationResults` row per question (`Verdict`, `Hit` for the expected document and phrase, the question as
  asked, the answer at the assistant's level, `JudgeNote`). The report shows question, answer and note; **Testa**
  re-runs one question in the panel.
- **Report**: the §7.2 card: "8 testfrågor · 7 som förväntat (förra versionen 8) · 1 sämre, 0 bättre" with the
  questions that changed and the judge's reasons; after a publish it carries **Behåll** and **Återställ förra
  versionen** (a restore, §4.5); a worse result than the previous version also becomes an Översikt attention item.
  "Om testfrågorna" says the judge can be wrong and that nothing is blocked.
- **Cost**: questions × (one answer + one judge call), shown before **Publicera** and **Kör testfrågor**; at most 50
  questions (V33), metric `rag.admin.assistant_tests{outcome}`. Old runs: the last 20 per assistant are kept (data-model
  §8).

## 8. Feedback, conversation review and access audit

**P1: ratings only** ([weakness review §4](../weakness-review.md#4-data-protection-guards-the-secondary-copy-while-the-primary-stays-unprotected)). The pilot assistant runs history mode `none` until A1 is
answered, so our store holds no comment and no conversation content: §8.1 shows ratings, reason categories and import
health, the "has comment" filter is empty, and comments stay in Open WebUI. §8.2–§8.5 (comment reveal, conversation
review, `sensitive` flow, access log) are built when A1 allows stored content.

### 8.1 Återkoppling (P1)

- Rows from `chat."Feedback"` of the caller's assistants (keyset by `CreatedAt`), joined in memory with message
  metadata (`Outcome`, `Coverage`, `Language`, the assistant version that answered from `Messages.AssistantVersionId`),
  cited document titles (`MessageSources.DocumentId` → `Documents.Title`), assistant and client names. **No content**: no question, answer, title or comment.
- **Reason category** in plaintext: `Feedback.ReasonCategory` filled by the importer and `/v1/feedback` only from the
  fixed Open WebUI keys (`RateComment.svelte:17-35`) and the API reasons of chat design §3.5 (`incorrect`, `incomplete`,
  `wrong_sources`, `not_relevant`, `other`), else `other` (the column has no CHECK, so a reason keeps its own category); the free text stays in the ciphertext (in P1 it is not stored, weakness review §4). Swedish
  labels in `labels.ts`.
- `ReviewState` (`open`/`reviewed`/`dismissed`) set by `FeedbackComments` holders ("Markera som hanterad"). Filters:
  period, assistant, rating, outcome, has comment, state. Totals from `AssistantDailyTotals`.
- Import health per Open WebUI client from `FeedbackImportState` (last run, outcome, unmapped) (chat §14.2–§14.3).

### 8.2 Comment reveal (`FeedbackComments`; when A1 allows stored content)

1. "Läs kommentar" → dialog with the fixed reason "Granskning av återkoppling" (one click, chat answered 7).
2. Server: feedback in scope; level `public`/`internal` (`sensitive` → 403 `review_flow_required`); write
   `ConversationAccess` events `requested` and `decrypted` (`Scope = comment`, `FeedbackId`, `AssistantId`,
   `DepartmentId`, reason) and event **6004** — before decrypting.
3. Unwrap the row's bucket key (`DataKeyId`: level and expiry month, simplification pass §5) with the level's master
   key, decrypt `CommentCiphertext`, return `{ reason, comment }` only; keys stay ≤ 5 minutes in memory; response
   `no-store`; the UI drops it when the dialog closes. One comment per request; no bulk reveal.

### 8.3 Conversation review (`Review`)

- First (when A1 allows stored content, weakness review §4): from a feedback row, **Läs samtalet** (reviewer only). Then also by assistant and period (metadata list).
- Request: free-text reason ≥ 20 characters (V26); `public`/`internal` → events `requested` and `decrypted`; access
  window **30 minutes** per request id; each fetch within the window checks scope again; one `decrypted` event per
  request id, 6004 per fetch.
- View: messages in order with times, assistant, outcome, coverage; cited snippets under each answer; feedback; P2 tool
  calls. Read-only, no export, no copy button; a banner names the reviewer and the logged reason.
- A conversation in history mode `none` shows metadata only; a deleted one "Samtalet har gallrats".

### 8.4 `sensitive` (P2; no `sensitive` data while file keys are used, platform §8a)

**Later (P2):** request (reason) → a second `rag-reviewer` with the department in scope approves (`approved`, approver ≠ requester)
within 24 h → the requester decrypts within 30 minutes. Comments of `sensitive` conversations only through this flow
(chat §14.4). The pending queue is a tab of Samtalsgranskning.

### 8.5 Åtkomstlogg (access audit)

`audit."ConversationAccess"` as a list: time, reviewer, scope (comment/conversation), assistant, level, reason, events
(requested → approved → decrypted). Visible to platform admins (all), department admins (their departments) and
reviewers (their own). Reading the list emits 6007. Export CSV (`;`, as `ReportEndpoints.cs:152-158`).

## 9. Audit log and security-relevant views

### 9.1 Granskningslogg

- A list page in P1: platform admins every `AuditLog` entry (filters: period, actor, action, entity type, entity id,
  department, service incl. `system:retention` and worker entries), keyset; the detail shows the stored before/after
  snapshot JSON as text. Policy `Manage` with department scope: department admins see their departments' entries
  through `AuditLog.DepartmentId` (§13). The
  field-level diff viewer and **Historik** tabs on entity pages are P2.
- Reading the audit log emits 6007 (identity §10).

### 9.2 Audit actions

`EntityType` + `Action` columns as the gateway (`AuditSnapshots.cs:11-51`); dotted names are `entity.action` shorthand.
Admin entries: `collection.create|update|delete|publish|share|retrieval_settings`,
`source.create|update|delete|pause|resume|sync|retry-failed`, `upload.batch|delete|restore`, `deletion.confirm|reject`,
`hold.release`, `pii.view|clear`, `pipeline.publish|cancel|rollback`, `template.*`,
`siteprofile.update|approve|reject|rollback`, `evaluation.generate|question|run`,
`assistant.create|update|delete|draft|discard_draft|publish|restore|copy|archive|unarchive|audience|retention|limits`,
`assistanttest.create|update|delete|run`, `assistanttemplate.*`, `departmentsettings.update` (P2), `client.*`,
`key.create|update|rotate|reveal|copy|revoke`, `gatewaymodel.update`, `settings.update`, `dataprotection.update`,
`organisation.sync`, `feedback.review_state`, plus tools design §9.2 and the conversation access events of §8 (in
`ConversationAccess`). The actor is the `oid`.

### 9.3 Security-relevant views

| View | Contents | Who |
|---|---|---|
| Nycklar | status, last use (`KeyLastUse`), grace, keys used after rotation | Manage (own teams), Admin |
| Inloggningsfel | `audit."AuthFailures"` aggregated by credential kind and client (never the presented value) | Admin |
| Personuppgifter | the per-source report (`GET /api/sources/{id}/pii-report`, §4.5): held and live flagged documents of a `piiMode: report` source in a `public` collection, non-zero counts per type and the fired-rule count (no values); per document the highlight preview and clearance with reason | Read (report); Manage (preview, clearance) |
| Karantän | quarantined files: name (own sources), type, signature name, date; no download | Read |
| Verktyg (P2) | drift observations, pending approvals and pre-registrations, grants, definition diff | Admin, system owners |
| Åtkomstlogg | §8.5 | §8.5 |

Security events themselves are not stored (ADR 015 pattern): the SIEM is where they are read.

## 10. Accessibility, performance, observability

### 10.1 Accessibility

- **Target**: WCAG 2.2 AA, the gateway's design target (`docs/accessibility-statement.md:9`), which contains the legal
  baseline (DOS-lagen 2018:1937 → EN 301 549 → WCAG 2.1 AA). An internal admin app is a digital public service under
  DOS-lagen, so it gets a tillgänglighetsredogörelse in the gateway's format.
- **Copied patterns**: skip link (`AppShell.vue:91-93`), focus to the page heading after navigation
  (`ui/router/index.ts:46-74`), first invalid field focused (`useFormErrors.ts`), `role="status"` loading and
  `aria-live="polite"` toasts, session expiry dialog with explicit extension (`AppShell.vue:249`), three themes, 24 px
  targets, charts with a table view.
- **New patterns**: job progress announced in one polite live region per page (state changes only); publish dialog
  sections are headings; evaluation reports as text first, chart second; before/after previews as two labelled regions;
  drag never required (rows move with buttons, files by a native file input); test panel announces "Svaret är klart"
  once; status never by colour alone; long tables with captions and sortable headers (`aria-sort`); generic forms use
  the same field components, so they inherit labels and error focus.
- **Verification**: copied axe spec (`tests/e2e/specs/accessibility.spec.ts:6-19`) over every routed page and every
  drawer/dialog in three themes; target size (`helpers.ts:160-172`); reflow at 320 px and 200 % text; **keyboard-only
  run of the pilot journey** (§11.4); Vitest component axe checks; one NVDA + Firefox pass of the pilot journey before
  the pilot (answered 4).

### 10.2 Performance budgets

| Item | Budget | How measured |
|---|---|---|
| Initial JS (shell + Översikt), gzip | ≤ 200 KB | CI build size report; fails above budget |
| Each lazy route chunk, gzip | ≤ 100 KB | same |
| API p95 (server, pilot data) | detail ≤ 200 ms, lists ≤ 400 ms, writes ≤ 500 ms (incl. the version lock), job enqueue ≤ 200 ms, overview ≤ 500 ms | `http.server.request.duration` by route; integration perf test with 50 sources, 100k run items, 10k documents |
| Test panel | retrieval as knowledge §11; answer as chat §16 plus ≤ 100 ms admin overhead | `rag.admin.test_panel.duration` |
| Preview | first document's first stages ≤ 30 s for HTML (worker-bound), deadline 2 min | E2E timing, `rag.jobs.age{class=preview}` |
| Interaction | visible response ≤ 100 ms (spinner/optimistic state) | manual + Playwright |

No new runtime dependency: no Markdown renderer, no chart library (gateway SVG charts), no JSON-schema form library
(§6.3), no graph library, no i18n library, no date library (`Intl`).

### 10.3 Observability

- Copied: `AdminActivityMiddleware` (6006 throttled), 6001–6007, log scopes incl. `ConfigVersion`, `RequestId`; no
  content in telemetry (test-panel questions, comments, conversation text, URLs of `sensitive` sources never logged).
- Metrics (meter `Ume.Rag`, low cardinality): `rag.admin.validation.refusals{code}`, `rag.admin.jobs.enqueued{class,
  kind}`, `rag.admin.sync{kind,outcome}`, `rag.admin.test_panel{outcome}` + `.duration`,
  `rag.admin.conversation_access{scope,level}`, `rag.admin.uploads{outcome}` + `.bytes`, `rag.admin.publish{kind,
  outcome}`, `rag.admin.config_lock.wait` (histogram, ms), `rag.admin.access_explain{allowed}`, `rag.admin.assistant_publish{kind}` (`publish`/`restore`),
  `rag.admin.assistant_tests{outcome}`.
- Dashboard *Config & security* gains admin panels (refusals by code, access events, sync health, lock waits).

## 11. Tests

### 11.1 Unit (`Ume.Rag.AdminApi.Tests`, `Ume.Rag.Domain.Tests`, `Ume.Rag.Pipelines.Tests`)

- `ConfigRules`: one table-driven test per active V-rule (pass, fail, boundary; the re-check triggers of §4.6).
- `PermissionMatrix` invariants: `Admin` never implies `Review`/`FeedbackComments`; every endpoint has a policy.
- `FormModel`: every template's `paramsSchema` renders; unsupported constructs fall back to JSON; labels present
  on templates. The UI's schema mapper: every generic-form operation maps to fields (vitest, against a cut of the
  OpenAPI document).
- `AccessExplainer`: one case per `IAccessPolicy` step (identity §4.3) returns that step and code.
- Assistant versions: the structured diff (P2) (settings, links, line diff of instructions incl. Swedish characters and
  empty texts); `usesTools` derivation (no skills, skills without tools, a tool excluded by `ExcludedToolIds`, a served
  version adding a tool); the test verdict rule (every check passes) and phrase matching.
- Audit snapshots never contain secret columns (reflection over `*Audit` records vs a deny list: `EncryptedSecret`,
  `KeyHash`, `EncryptedKey`).
- Error-code coverage: every `ValidationCode` has an English message and is in `contracts/admin/v1/error-codes.json`.

### 11.2 API integration (WebApplicationFactory; Testcontainers: the stock pgvector image, Keycloak; FakeGateway)

- **Role matrix**: generated from `PermissionMatrix`: every endpoint × every Keycloak test user → expected
  200/403/404; ids of another department → 404; shared collections usable for links but not editable.
- **Write path**: lock + change + outbox row + version bump + audit row with that `ConfigVersion` + job row in one
  transaction (kill the connection before commit → none of them); concurrent edits → 409 `concurrent_change`; 23514 →
  400 `platform_rule`.
- **Write skew**: two concurrent saves that each pass V3 alone but break it together (raise an assistant's level vs
  lower a linked collection's) → the second sees the first's state and is refused; lock wait > 5 s → 503
  `config_busy`.
- **Jobs**: dedup under parallel "Hämta nu" (partial unique index); payload ≤ 4 KB; `pg_notify` on commit only.
- **Publish**: plan, confirm with a changed pipeline → 409; rollback within 14 days enqueues reprocess; nothing blocks.
- **V8**: FakeGateway `/v1/models` at a ceiling without the model → `model_not_listed`; gateway down →
  `gateway_unavailable`.
- **Feedback and review**: comment reveal writes `ConversationAccess` before decrypting (fault injection after the
  insert → no plaintext returned, events present); department admin can reveal comments but gets 403 on conversations;
  platform admin without `rag-reviewer` gets 403 on both; `sensitive` → `review_flow_required`; access window expiry.
- **Sync**: org and model sync write nothing when unchanged; alias residency rule; failure keeps the mirror.
- **Assistant versions**: a draft save with a broken rule is stored with `warnings[]` and bumps no config version;
  publish refuses it; publish moves `PublishedVersionId` and the api's view `PublishedAssistantVersions` shows only that
  version; restore creates a new version and keeps the draft; `draft_base_changed` after a restore; audiences, level
  and retention unchanged by publish and restore; copy carries no audiences or keys; deleting a collection a draft
  links is refused.
- **Assistant tests** (P2): a publish starts one run (FakeGateway answers and a scripted judge); a restart mid-run restarts
  it; the report compares with the previous version's run; results are readable only within the assistant's scope.
- **Owner-team scoping (P2)**: with `EditOwnerTeamOnly` an editor outside the owner team gets 403 `owner_team_only`
  on assistant, collection, source and skill writes and still reads them; a department admin is unaffected.

### 11.3 Worker in the loop (`Ume.Rag.IntegrationTests`)

Admin-enqueued preview (with site-profile before/after on the umea.se fixture pages), publish plan, re-process and
post-publish evaluation, deletion confirm, PII clear, site-profile rollback and upload commit consumed by a real worker;
the admin reads the results through its own endpoints.

### 11.4 Browser (Playwright TypeScript, gateway `tests/e2e` setup; nightly and release)

- **Pilot journey** (`pilot.spec.ts`, department admin of department 1000): §5.4 steps 1–13 against a static fixture site
  (nginx container with saved Swedish pages incl. the umea.se fixture set, and 3 PDFs), FakeGateway, real worker and
  parser; ends by calling `/v1/models` with the Open WebUI client's key and finding the assistant. Run twice: mouse,
  and **keyboard only**.
- Role journeys: viewer read-only, pipeline editor (template parameters, preview, publish, roll back), platform admin
  (generic forms for settings, clients, a site-profile edit and rollback; proposal approve from P2), reviewer (comment reveal,
  conversation view, access log), no-role user sees the no-access page, deletion confirmation and rejection, upload with
  a refused file type; assistant change → Testa on the draft → Publicera → Återställ from Versioner; sharing a knowledge base with the
  municipality and linking it with a topic from another department's assistant; the Map table; from P2 the test
  report and copying an assistant.
- Accessibility spec copied and extended (§10.1); CSP violations collected with `page.on('console')` and
  `securitypolicyviolation` listeners must be zero.

### 11.5 Unit UI (Vitest)

Stores, `messages.ts` mapping, form generation (template and generic), checklist computation, polling back-off,
component axe checks.

## 12. Milestones

| Phase | Deliverable |
|---|---|
| P0 | BFF against `ume-rag-admin` + roles (incl. `rag-reviewer` in the realm and Bicep), design system and shell in Swedish, `labels.ts`/`messages.ts` skeleton, `AdminContext` write path (version lock + rules + audit + outbox + version), `PermissionMatrix` with generated role tests, org sync, error-code export. **Built**: pages Översikt (your roles and departments with their source) and Organisation (synced departments and teams; **Hämta nu** for platform admins); `/api/clients` is the first resource on the write path (its generic form is P1b, §5.8) |
| P1a (walking skeleton, weakness review §1) | none of the journey pages: the pilot's collection, sources and one assistant are created through the write path with Scalar (generic forms come in P1b); the umea.se site profile as a manual profile JSON; `GatewayModels` sync; clients; the pilot gold set entered as `human` questions through Scalar (weakness review §2) |
| P1b (1) | collections (incl. documents tab, PII view/clear), sources (URL list, crawl, upload batches), preflight, preview with site-profile before/after, impact, runs and crawl report, deletion decisions, template parameters, publish (plan of every document → re-process → report → keep or roll back), rollback |
| P1b (2) | evaluation set and reports (led by the gold set; coverage-threshold proposal as numbers, weakness review §2), retrieval settings, collection publish, assistants (draft → publish, versions with restore, archive, description and contact), test panel on the draft (`Ume.Rag.Answering`, same release as `rag-api`), impact lines, V8 `/v1/models` check, audiences (clients), retention, generic forms (settings, data protection, clients, keys, templates, site profiles, embedding profiles, gateway models, organisation), audit list, explain access |
| P1b (3) | feedback page (ratings, import health; comment reveal, conversation review behind feedback and the access log when A1 allows stored content, weakness review §4); accessibility pass and tillgänglighetsredogörelse; pilot journey E2E green |
| P1b (4) | organisation (Decisions "Organisation: department-owned, municipality-shared knowledge bases with topics"): sharing levels and `CollectionShares`, topics on links, contact on knowledge bases, V11 widened and V35, `view=` lists, the Map table, the menu regrouped, teams out of create forms |
| P2 | from weakness review §1: assistant templates and copy, version diff, assistant test questions and `AssistantTestRunner`, the Map drawing, unused-assistant attention item, latest answer evaluation (`AnswerEvaluationService`), site-profile proposals with learning, effect-tag publish plans; skills (share lists, gate report, serve); tool servers (registration, approvals, pre-registration, grants, drift leader); tool use through skills (the derived **Använder verktyg**, limits, tool calls in the test panel); owner-team scoping; parse-profile proposals; SharePoint; crawl hierarchy tree; parameter diff; coverage histogram and proposal; audit diff viewer and Historik tabs; conversation search and `sensitive` two-person flow; feedback → test question; widget keys; dedicated pages where generic forms prove too thin |
| P3 | analytics, OBO configuration, risk acceptance, skill import/export, embedding profile changes |

## 13. Changes to other documents

| Document | Change |
|---|---|
| `01-general-idea.md` §10 | row "Admin (step 10)" restated: journey pages only in P1, generic forms for platform objects, publish = plan → re-process → advisory report → keep or roll back, no canvas, serialised config writes, **V1–V34 with V20 and V27 retired (32 rules)**, one assistant type with draft/publish and versions (replaces "V1–V31", "`gating`", "Vue Flow 1.48.2", "`building` collections") |
| `HANDOVER.md` | admin row: drop "Vue Flow canvas P2"; cross-rules "V1–V34 (V20, V27 retired)" |
| `identity/design.md` §1.3, §1.4 | `rag-reviewer` → `Review`, `FeedbackComments`, `Read`; `rag-pipeline-editor` = template parameters; roles on `ume-rag-admin`; env `UME_RAG_OIDC_GROUPS_REVIEWER` |
| `identity/design.md` §4.3, §4.8 | explain-access endpoint consumes the pure `IAccessPolicy` steps and needs a stable step id and reason code per step; test panel runs `Ume.Rag.Answering` with the admin's `oid`, unpublished collections included; P2 agent test calls through the admin's in-process hub as `admin-console` (`ToolPolicy = read`) |
| `identity/design.md` §10 | 6004 attributes `RequestId`, `Event`, `Scope`, `ConversationId`, `Classification`, `AssistantId`; 6007 also for feedback lists, access log and conversation views (not explain access, identity §4.9); audit actor `oid` |
| `platform/data-model.md` §1.2 | admin: **U on `chat."Feedback"` column `ReviewState`**; S on site-profile tables (wherever the worker writes them) |
| `platform/data-model.md` §2.1 | `GatewayModels` P1 with `Kind`, `Residencies varchar(10)[]`, `Source`; **no** `PipelineVersions.State = gating`, **no** `PlanJobId`; `UpdatedAt`, `UpdatedBy` on every editable aggregate root, `xmin` as concurrency token; `Clients.PortalUrl varchar(500) NULL`; `CollectionShares` (`CollectionId`, `DepartmentId`, `SharedBy`, `SharedAt`; PK both ids) replacing the own-department-only link note; no `Clients.TaskModel`/`TaskModelResidency`; no `GatewayKeyLinks`; `Collections.PublishGateRunId` dropped |
| `platform/data-model.md` §2.3, §2.4 | `Feedback.ReasonCategory varchar(40) NULL`; `AuditLog.DepartmentId uuid NULL` (+ IX), actor column holds `oid`; `ConversationAccess` adds `Scope`, `FeedbackId`, `AssistantId`, `DepartmentId`, `ExpiresAt` |
| `platform/data-model.md` §5 | every admin config transaction starts with `SELECT … FOR UPDATE` on `config."ConfigVersion"` (state it next to the outbox trigger) |
| `platform/data-model.md` §1.2, §2.1, §5, §8, §11 | simplification pass §14: new `config."AdminSessions"` (`SessionId` PK, `Value bytea` (Data-Protection-encrypted ticket), `ExpiresAt` + IX; identity §1.1), admin S I U D, no api or worker grant, no outbox trigger (as `DataProtectionKeys`); retention deletes expired rows; §11 without `ume-rag:admin-session:*` |
| `ingestion-worker/design-engine.md` | `command` kinds `approve-site-profile`, `reject-site-profile`, `edit-site-profile` (admin actions on `proposed`/`manual` site-profile versions; only `rollback-site-profile` exists there) and P2 `approve-profile`/`reject-profile` for parse-profile proposals; `eval` `on-demand` also enqueued by the admin after a retrieval-settings save; preview returns site-profile before/after for crawl sources; preflight samples ≤ 20 URLs and reports the CMS preset; template `paramsSchema` annotations `x-labels`, `x-help`, `x-unit` |
| `ingestion-worker/design-connectors.md` §9.1 | uploads as batch create → one request per file → commit (manifest and `sync` job at commit; uncommitted batches swept after 24 h) |
| `ingestion-worker/design-processing.md` §13–§14 | evaluation reports as consumed in §7.2 (recall@10, MRR with counts, previous run, per-question changes); no gate states, floor or overrides |
| `chat-api/design.md` §2.1, §14.1–§14.4 | chat answer core in `Ume.Rag.Answering`; importer and `/v1/feedback` fill `ReasonCategory`; comments readable by `rag-reviewer` and `rag-department-admin`, conversations by `rag-reviewer` only; comment decryption with the bucket key |
| `knowledge-api/design.md` §2.1, §4.5 | `KnowledgeService` core in `Ume.Rag.Answering`; only the admin host passes unpublished collections (replaces `building`); collections shared with a department are in scope for its assistants |
| `platform/plan.md` §8a | review row: reason by `rag-reviewer`; comments of `public`/`internal` also by department admins after a one-click reason; no data tier configuration |
| `platform/repo-and-deployment.md` §1, §2.3, §3 | `src/Ume.Rag.Answering` (referenced by Api and AdminApi, never the Worker); `contracts/admin/v1/error-codes.json`; **no Vue Flow packages**; settings `Admin:GatewayAdminUrl`, `OrgSync:*`; `release.yml` deploys `rag-admin` and `rag-api` from the same release |
| `platform/repo-and-deployment.md` §6.1, §8.2, §9 | proxy `client_max_body_size` 60 MB on the admin upload route; e2e row = pilot journey (mouse and keyboard) + extended accessibility spec + static fixture site container with the umea.se fixtures; admin metrics of §10.3; `rag-admin` on network `mcp` in P2 |
| `gateway-changes/design.md` | done: §3.4 states today's `/v1/models` filtering (key residencies only) and that the combined release adds `x-ume-max-residency` for every key, delegating keys included (V8 relies on it); optional `GET /v1/catalog/routes` |
| `postponed-decisions.md` V1 | Vue Flow struck (canvas dropped) |
| `platform/data-model.md` §1.2, §2.1, §2.2, §5, §8 (simplification pass §15) | done in the parallel pass (`Assistants.PublishedVersionId`/`DraftVersionId`, `State` `active`/`archived`, `AssistantVersions` with `CollectionIds`/`Skills`, the view `PublishedAssistantVersions`, `AssistantTestQuestions`, `AssistantTemplates`, `DepartmentSettings.EditOwnerTeamOnly`, `Skills.OwnerTeamId`, `EvaluationRuns` kind `assistant-tests`, `Messages.AssistantVersionId`); this design uses those names. Remaining: the `EditOwnerTeamOnly` note should say it limits `Edit`, `EditPipelines` and `EditSkills` (§3.2) |
| `chat-api/design.md` (simplification pass §15) | done in the parallel pass (published version only, derived mode, description and contact in `/v1/models`); add `x-ume-step: assistant-test` for the test runner (§7.4) |
| `knowledge-api/design.md` §2.2, §4.2 (simplification pass §15) | done in the parallel pass (direct collections only, no `SkillVersionIds`) |
| `ingestion-worker/design-processing.md` §14.8 | the answer judge's prompt family also judges assistant test questions (expected behaviour as free text) in `rag-admin` (§7.4) |
| `gateway-changes/design.md` (P2, only if needed) | owner-team scoping reads team membership from `teamCodes`; if IT cannot supply that claim, member groups on teams as feature C does for departments, in both systems (§15) |
| `identity/design.md` §1.3, §4.3, §4.8 | done in the same pass: owner-team scoping, versions do not change access, test panel on the draft |
| `tools/design.md`, `tools/plan.md` | done in the same pass: skills without collections, mode derived from tool-bearing skills |

## 14. Questions

### 14.1 Answered

User, 2026-10-30 (this step):

1. **UI language**: **English**, as the gateway, every string in `labels.ts`/`messages.ts` so Swedish can follow as a
   translation (user, 2026-11-02; §1 row 3, §4.2, §5.7). Generic forms for platform admins use schema titles (§5.8).
2. **Pilot setup**: the pilot department's own admins use the P1 journey pages themselves, with the platform team
   pairing at first (§5.4, §6.1).
3. **Conversation review**: role `rag-reviewer`, granted per department to named reviewers, reads other users'
   conversations with a reason, audited; `sensitive` also needs a second approver. Feedback **comments** may be read by
   `rag-reviewer` and `rag-department-admin`; **full conversations only by `rag-reviewer`**; platform admins get neither
   implicitly (§3.1, §8).
4. **Screen-reader check**: one NVDA + Firefox pass of the pilot journey before the pilot (§10.1).
5. **Conversation review in P1**: limited to the conversation behind a feedback item (`public`/`internal`); general
   search and the `sensitive` two-person flow in P2 (§8.3–§8.4).

User, 2026-10-30 (simplification pass §1, §2, §8, §10, §13): journey pages only, generic forms for platform objects,
template + parameters, advisory evaluation on the publish screen, serialised config writes, share lists, explain
access, `/v1/models` validation, site profiles in the source preview and a small platform-admin view.

User, 2026-10-30 (simplification pass §15, many assistants per department): one assistant type with the mode derived
from tool-bearing skills; knowledge only through the assistant's own collection links; assistant drafts, publish,
version history with diff and one-click restore; per-assistant test questions judged after publish (advisory); copy,
platform-owned assistant templates, the department map and impact lines, the 90-day unused attention item, description
and contact; P2 owner-team scoping of writes (§1 rows 22–24, §3.2, §4.5, §5.5.4, §5.5.6, §7.4).

Earlier answers this design builds on: admin plan answered 1–3 (separate app; `rag-editor` from P2; tool servers
registered by platform admins); chat answered 7 (one-click comment reason); tools answered 2 (skill editors).

Decisions taken here that the user may want to revisit: platform admins without implicit content access (§3.1); no
e-mail notifications in P1/P2 (§5.2); the test panel shows answers as plain text (§4.9); explain access for platform
admins and for department admins on their own department's assistants and clients (§4.5); keys managed through a generic form in P1 (§5.8);
audience, level, retention, description and contact are not versioned (§4.5); publish needs `Edit` (not `Manage`)
and a description and contact (V34); the assistant test run follows publish instead of gating it (§7.4); owner-team
scoping also covers `EditPipelines` and `EditSkills` (§3.2).

### 14.2 Open (user)

None.

## 15. To verify

- **Npgsql EF Core 10.0.3**: mapping `xmin` as a `uint` row version (`IsRowVersion`) on every aggregate root, and its
  behaviour in `ExecuteUpdate` paths.
- **Version lock** (new): `SELECT … FOR UPDATE` on `config."ConfigVersion"` before `SaveChanges`, then reading the
  bumped value after the outbox trigger in the same transaction; no deadlock with the trigger's own update of that row;
  lock waits under the admin's real write rate (integration test + `rag.admin.config_lock.wait`).
- **Gateway**: whether release 2's `memberGroups`/`code` fields and the proposed `/v1/catalog/routes` land; that
  release 1's `/v1/models` test at a ceiling passes (with a delegating key from release 2; weakness review §5) (gateway-changes design §3.6; V8 relies
  on it); residency semantics of a route alias under the header.
- **Generic forms**: the .NET 10 OpenAPI generator emits DataAnnotations ranges, required members and enums as JSON
  Schema keywords the UI's mapper renders (checked against the live document: every create, update and action of the
  generic-form resources and all nine settings sections map); `jsonb` members round-trip.
- **Site profiles** (new, P2 with learning): the worker's preview can return before/after text for a host without a profile within the
  preview deadline (2 min).
- **Open WebUI v0.12.5**: the feedback `data.reason` values are always one of the fixed keys and never free text from
  the API; behaviour for older imported feedback.
- **Kestrel/proxy**: per-endpoint 60 MB body limit on the upload route while the rest keeps 1 MB; nginx streaming
  (`proxy_request_buffering off`) for that route.
- **DOS-lagen scope** for an intranet admin application and the required content of its tillgänglighetsredogörelse
  (municipal web/accessibility coordinator).
- **Budgets**: API p95 numbers and the overview query at 100k run items in the R1 test.
- **Assistant tests** (new, P2): that the on-prem judge's verdicts on free-text expected behaviour agree with a person's on
  the pilot assistant's first 20 questions (spot check before the report is shown to departments).
- **Open WebUI v0.12.5** (new): whether a description returned with an external connection's model (`/v1/models`)
  is shown to users at all, or must be set in Open WebUI's own model settings (chat design owns the field).
- **Team membership** (new, P2): the municipality has no team attribute, as it had no department attribute; whether
  owner-team scoping can use `teamCodes` or needs member groups on teams (identity §1.3).

## 16. Deferred or removed (simplification pass)

Per [`../simplification-pass.md`](../simplification-pass.md):

| Removed or deferred | Where it went |
|---|---|
| Vue Flow canvas, its packages, layout table `PipelineCanvasLayouts`, canvas a11y work (§6.8) | Dropped from the roadmap (§2) |
| P1 node editor, node palette, ports, locks, JSON definition view, rules 1–12 (§6.2) | Removed: template + parameters (§2) |
| Version diff (§6.5), rebase, bulk rebase, `locked_node_below_min` | Parameter diff P2 if needed; a source moves to the latest template on its next publish (§10) |
| `gating` state, `PlanJobId`, blocking pipeline/collection/retrieval gates, gate cards with intervals, escalation and McNemar, collection floor (V27), `building` state, platform-admin overrides (§7.3), `gate.override` | Removed; advisory evaluation report, keep or roll back (§8) |
| Coverage-threshold proposal and histogram | Histogram P2 (§10); the proposal as numbers on the report card is P1 (weakness review §2, §7.1) |
| Crawl hierarchy tree (`hierarchy` preview job) | P2 (§10) |
| Audit field-level diff viewer, Historik tabs | P2 (§10); the audit list stays |
| Dedicated P1 pages for settings, data protection, clients, keys, gateway, embedding profiles, organisation | Generic schema-driven forms + Scalar (§10) |
| Parse-profile auto-apply UI (confidence, trials, kill switch, rollback) | P2 proposals approved by platform admins (§2) |
| Tier fan-out readers, `ITierResolver`, tier matrix test, data tier configuration | Removed (§5) |
| Interim per-team gateway key links (`/api/gateway/key-links`, `gatewaylink.*`) | Removed (§7) |
| Clients' task model (`owui-tasks`, `TaskModelResidency` in V21) | Removed (§1) |
| Shared document store views ("used by another department"), `recompute-membership` | Removed: documents belong to one source (§2) |
| Own-department-only links | Collection sharing in P1b (Decisions "Organisation: department-owned, municipality-shared knowledge bases with topics (user, 2026-11-02)"); skill share lists P2 |
| Skill MCP exposure and audiences (V20), MAJOR auto-pin preview | P3 / removed (§10) |
| Risk acceptance (`/risk-acceptance`, `assistant.risk_accept`) | P3 (§10) |
| "(+ request)" right on tool servers | Removed: platform team's intake (§10) |
| `rag-admin` → `rag-tools` hop | In-process hub in P2; `rag-tools` P3 (§10) |
| Second admin slot in the pilot | P2 with the a/b slots (§1) |
| Shred-bucket destruction in `RetentionService` | Off until A1 (§5) |
| Admin sessions in Redis (`ume-rag:admin-session:{sessionId}`), `data` network reaching Redis | Postgres `config."AdminSessions"`, expired rows deleted by `RetentionService`; Redis returns in P2 only for shared limits, leases and the embedding cache (§14) |
| Assistant `Mode` choice (`rag`/`agent`), `activate`/`deactivate` (`assistant.activate`), the unsaved-form test route `POST /api/assistants/test`, collections reached through skills (V3, V11 "union with skills'") | Removed (§15): mode derived from tool-bearing skills; draft → **Publicera**, `archive`/`unarchive`; the test panel runs the saved draft; collections only through the assistant's own links |

## 17. Deferred or removed (weakness review)

Per [weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer):

| Item | Where it went |
|---|---|
| Assistant version diff (`…/diff`, **Jämför**, the diff in the publish dialog) | P2 |
| Assistant test questions, `AssistantTestRunner`, test reports, **Spara som testfråga** (§7.4) | P2 |
| Assistant templates (generic form, `templateId` on create), copy an assistant | P2 |
| Department map (Karta, §5.5.6, `GET /api/organisation/departments/{id}/map`), unused-assistant attention item | the map as a table in P1b (Decisions "Organisation: department-owned, municipality-shared knowledge bases with topics (user, 2026-11-02)"), its drawing and the attention item P2; the impact line on saves stays in P1 |
| Latest answer evaluation (`AnswerEvaluationService`, nightly `answers` run) | P2 |
| Learned site profiles, proposals, approve/reject | P2; P1 has manual profile versions (edit, roll back), the umea.se one a hand-checked Sitevision preset |
| Publish plan split by effect tags | P2; P1 re-processes every document of the collection, still with report and keep or roll back |
| Dedicated journey pages before the first answer | P1b; the walking skeleton (P1a) uses Scalar only, generic forms come in P1b |

Per [weakness review §4](../weakness-review.md#4-data-protection-guards-the-secondary-copy-while-the-primary-stays-unprotected):

| Item | Where it went |
|---|---|
| Comment reveal (§8.2), conversation review behind a feedback item (§8.3), Åtkomstlogg (§8.5), `ConversationAccess` writes and event 6004 | When A1 allows stored content; P1 pilot runs history mode `none`, the Feedback page shows ratings only |
| Chat master keys in the admin (`IKeyProvider` for comment and conversation decryption) | Same; the admin still unwraps `internal` blobs |
| History modes `stored`/`session` in the data-protection form and the assistant retention form (V10, V29) | Same; P1 allows `none` only |

Per [weakness review §5](../weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control):

| Item | Where it went |
|---|---|
| Delegation echo in the gateway status check (§4); `memberGroups` and team `code` in the organisation sync | P2 with gateway release 2; P1 checks the ceiling with the pilot team's per-level keys and syncs departments and teams without them |
