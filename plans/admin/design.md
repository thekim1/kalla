# Admin API and admin UI: detailed design

Detail step 10 of the Ume.Rag design, the last before the consistency pass. Details [`plan.md`](plan.md) (binding,
incl. its answered questions 1–3 and §14 here) and the Decisions table in
[`../../01-general-idea.md`](../../01-general-idea.md) §10. Consumes, without redesigning: the BFF, claim mapping,
`RagPrincipal`, internal callers, keys and security events of [`../identity/design.md`](../identity/design.md) (§1–§4,
§6, §10–§12); every table, grant, the outbox, the tier rules, encryption and retention of
[`../platform/data-model.md`](../platform/data-model.md) (§1.2, §2, §4–§8); definition format, validator, manifest,
preview, publish, rollback and templates of [`../ingestion-worker/design-engine.md`](../ingestion-worker/design-engine.md)
(§3–§5, §9–§13, §15); source settings, preflight, crawl report, uploads, effect tags and the deletion guard of
[`../ingestion-worker/design-connectors.md`](../ingestion-worker/design-connectors.md) (§4–§5, §9, §12.3, §13); PII
flags, evaluation sets and the gate of [`../ingestion-worker/design-processing.md`](../ingestion-worker/design-processing.md)
(§9.4–§9.5, §13–§14); `RetrievalSettings`, coverage and the test-panel scope of
[`../knowledge-api/design.md`](../knowledge-api/design.md) (§4.2, §4.5, §7, §12.1); residency, not-found texts, output
filter, retention and feedback of [`../chat-api/design.md`](../chat-api/design.md) (§8.2, §9.4, §10, §13–§14); skills,
tool servers, approvals, grants, drift, policy, risk acceptance, agent limits and `GatewayModels` of
[`../tools/design.md`](../tools/design.md) (§3–§6, §9); projects, build, images, networks, CI and observability of
[`../platform/repo-and-deployment.md`](../platform/repo-and-deployment.md) (§1–§3, §6, §8–§9). Nothing is implemented.
Changes needed elsewhere: §13; questions: §14; unverified facts: §15.

Gateway citations are `path:line` in `~/git/llmgateway` at `e25b491` (v0.8.5); `AdminApi/` = `src/Ume.LlmGateway.AdminApi/`,
`ui/` = `src/admin-ui/src/`. "Copy" = copied with names changed; "adapt" = copied and changed as stated; "new" = no
gateway counterpart.

**Sources read for this step** (all read only, never executed):

| Source | Version | Used for |
|---|---|---|
| Gateway `~/git/llmgateway` | `e25b491` (v0.8.5) | admin API conventions (endpoints, validation, problem details, audit, pagination, CSP), admin UI (stack, shell, router, client, forms, tables, charts), Data API catalogue, E2E and accessibility tests, `docs/admin-ui.md`, `docs/admin-api.md`, `docs/accessibility-statement.md` |
| npm `@vue-flow/core` | **1.48.2** (published 2026-01-28, `latest` on 2026-10-30), MIT | canvas (§6.8): dependencies, keyboard/ARIA support, `isValidConnection`, `connectOnClick`, CSS |
| npm `@vue-flow/background` / `@vue-flow/controls` | **1.3.2** (2024-11-13) / **1.1.3** (2025-08-07), MIT, no dependencies, peers `@vue-flow/core ^1.23.0`, `vue ^3.3.0` | canvas |
| npm `@vueuse/core` (transitive) / `vue-demi` | 10.11.1 (2024-08-08) / 0.14.10 (2024-07-25) | what the canvas pulls in; `vue-demi`'s default `lib/index.mjs` is the Vue 3 build, so `ignore-scripts=true` (its `postinstall` never runs) is safe |
| Open WebUI `~/git/rag-platforms/open-webui` | `f7ac7ee` (v0.12.5) | fixed feedback reason keys (`src/lib/components/chat/Messages/RateComment.svelte:17-35`, `:186-212`) |

**Gateway facts this design relies on** (checked in the source):

- The gateway admin UI's **texts are English**: `index.html:2` `lang="en"`, labels in `ui/utils/labels.ts` and
  components ("Sign in", "No access", nav labels in `ui/router/nav.ts:43-150`); only numbers use `sv-SE`
  (`ui/utils/format.ts:2-6`), dates `en-GB` (`:7-10`), time zone `Europe/Stockholm` (`:1`); Swedish names are marked
  `lang="sv"` (`ui/views/OrganisationView.vue:150`; `docs/accessibility-statement.md:23`). "Swedish only, like the
  gateway" (answered 1) is therefore read as: the gateway's **mechanism** (texts in components and a labels file, no
  i18n library), with **Swedish** texts and `lang="sv"`.
- No general ETag/If-Match handling: concurrency tokens only on keys (`src/Ume.LlmGateway.Infrastructure/Persistence/
  GatewayDbContext.cs:71-72`); any `DbUpdateConcurrencyException`, unique (23505) or FK (23503) violation becomes a 409
  (`AdminApi/Program.cs:20-27`); settings answer "Someone else changed the settings at the same time…"
  (`AdminApi/SettingsEndpoints.cs:91-94`). The client has `GET/POST/PUT/DELETE` only (`ui/api/client.ts:8`).
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
| 1 | Placement | `Ume.Rag.AdminApi` (`rag-admin`, `rag-admin-b`) is the BFF, the only writer of `config`, the job producer for the worker, the reader of `content`/`chat`/`audit` and the host of the built UI (`src/admin-ui/dist` as `wwwroot`); copied gateway BFF, CSP, rate limit, validation filter and problem details (§2) |
| 2 | Answer code in the admin | New library **`Ume.Rag.Answering`** holds the knowledge-role core (`KnowledgeService` without HTTP) and the chat answer pipeline core (`RagAnswerStrategy`, `PromptBuilder`, `TokenBudget`, `Condenser`, `AnswerFilter`, `CitationEmitter`, `LanguageGuess`), so the **test panel** runs the exact chat path in process over `building` collections without an API route that could include them (§2.1, §4.9) |
| 3 | Language | **Swedish only** (answered 1): `lang="sv"`, texts in components and `ui/utils/labels.ts`, no i18n; API, logs, OpenAPI and docs in English. Server errors carry a stable **`code`** (and per-field codes); the UI renders Swedish texts from a code table and shows the English `detail` only as fallback (§4.2) |
| 4 | Roles | New **`rag-reviewer`** (answered 3): policies `Review`, `FeedbackComments`, `Read`. Feedback **comments** (`public`/`internal`, one-click reason): `rag-reviewer` and `rag-department-admin`. **Full conversations: `rag-reviewer` only.** No administrative role implies conversation content, `rag-platform-admin` included (§3) |
| 5 | Concurrency | Every editable aggregate carries `version` (Postgres `xmin`); requests send it back; a mismatch is 409 `concurrent_change` (§4.3). Body field, not `If-Match`, so the copied client stays unchanged |
| 6 | Write path | One transaction: entity change → outbox trigger (version bump) → audit row with that `ConfigVersion` → optional job rows (`Jobs` is main tier, same database); `pg_notify` at commit (§4.4) |
| 7 | Validation | Field rules on request records (gateway style); **cross-rules** in one `ConfigRules` service (V1–V32, §4.6), run on save, on dependent changes and again before publish; CHECK violations (23514) answer 400, never 500 |
| 8 | Jobs | The admin inserts `content."Jobs"` rows only (preview, sync, command, eval); decisions on content are `command` jobs; every result is read from `Jobs.Result`, `Runs`, `EvaluationRuns` (§4.7) |
| 9 | Pilot UI | P1 pages are built for non-specialists (answered 2): one **setup checklist** per collection and assistant derived from state, template forms with defaults, preflight and preview before saving, impact shown before scope changes, publish as plan → quality check → confirm, consequences with counts, undo where the platform has one (§5.4) |
| 10 | Pipelines | P1: template forms generated from the template's `form` list and the manifest's `paramsSchema`; pipeline editors also get a node list with generated forms and a JSON view. P2: canvas on **Vue Flow 1.48.2** (lazy route), always with an equivalent keyboard editor (§6) |
| 11 | Publish | Draft → **`gating`** (frozen while plan + gate run) → confirm; cancel returns it to `draft`; confirm checks the hash the gate measured (§6.6) |
| 12 | Gate overrides | Only `rag-platform-admin`, per run, reason ≥ 20 characters (processing §14.4); rollbacks never gated (§7.5) |
| 13 | Feedback | P1 page from metadata (rating, fixed reason category, outcome, coverage, cited titles); a comment opens after a one-click pre-filled reason "Granskning av återkoppling", audited in `ConversationAccess` (`Scope = comment`) (§8.1–§8.2) |
| 14 | Conversation review | `rag-reviewer` opens a conversation with a free-text reason (≥ 20 characters), 30-minute access window, audited; **P1 for conversations behind a feedback item** (`public`/`internal`), P2 general search and the `sensitive` second-approver flow (§8.3–§8.4) |
| 15 | Model catalogue | `GatewayModels` sync moves to **P1** (residency rule V8 needs it); rows get `Kind` and `Residencies[]` so a route alias is servable when any target is within the ceiling; aliases are manual rows until the gateway exposes routes (§4.10) |
| 16 | Notifications | In-app only (attention cards, nav badges) plus SIEM events; no e-mail in P1/P2 (§5.2) |
| 17 | Accessibility | Gateway target WCAG 2.2 AA (⊇ the legal 2.1 AA of EN 301 549 / DOS-lagen); copied axe/target-size/reflow tests, a keyboard-only Playwright run of the pilot journey, canvas never the only way to edit; tillgänglighetsredogörelse as the gateway's (§10.1) |
| 18 | Dependencies | Only `@vue-flow/core`, `/background`, `/controls` added (P2); no Markdown, chart, JSON-schema-form or i18n package; no new NuGet package (§10.2, §6.8) |

## 2. Placement and interfaces

### 2.1 Projects

| Project | Owns (admin) | Notes |
|---|---|---|
| `Ume.Rag.AdminApi` | BFF (identity §1, adapted from `AdminApi/AdminAuthentication.cs`), endpoint groups (§4.5), `AdminContext` (scope, audit, save), `ConfigRules` (§4.6), `JobEnqueuer` (§4.7), `ContentReader`/`ChatReader` (tier fan-out, §4.8), `TestPanelService` (§4.9), `OrganisationSync`, `GatewayModelSync` (§4.10), `DriftWatcher` (P2, tools §4.4), `RetentionService` (data-model §8; slot a only), `ConversationAccessService` (§8) | serves `src/admin-ui/dist` as `wwwroot` (`PublishAdminUi=true`, repo-and-deployment §3) |
| `Ume.Rag.Pipelines` | `PipelineValidator`, Merkle hashes, `FormModel` (template form + manifest → form description, §6.3) | shared with the worker (engine §4) |
| `Ume.Rag.Retrieval` | retrieval core | test panel, coverage proposal (knowledge §2.1) |
| **`Ume.Rag.Answering`** (new) | `KnowledgeService` core (access within a given scope, hydrate, sections, citations), chat answer core (`RagAnswerStrategy`, `PromptBuilder`, `TokenBudget`, `Condenser`, `AnswerFilter`, `CitationEmitter`, `LanguageGuess`), P2 `AgentAnswerStrategy` | moved out of `Ume.Rag.Api`; the knowledge and chat roles keep HTTP, SSE, writer and importer. Reason: the test panel must show the real prompt, budget and filter (chat §9.4), and knowledge §4.5 forbids any API path that includes `building` collections (§13) |
| `Ume.Rag.Domain` | `RagRole`, `AdminPolicy`, `PermissionMatrix` (data, §3.4), `ValidationCode` | pure |
| `src/admin-ui` | Vue 3.5 + Vite 8 + Tailwind 4 + Pinia + vue-router (gateway `package.json`), copied shell, design system, client, composables (§5.1) | port 5174 (repo-and-deployment §4.1) |

### 2.2 Hosting, slots and background work

- Two slots `rag-admin`, `rag-admin-b` (compose rolling overlay; proxy `127.0.0.1:9543`/`:9544`, repo-and-deployment
  §6.1), control zone; sessions in Redis (`ume-rag:admin-session:{sessionId}`), so either slot serves any user.
- Networks: `edge` (proxy; OIDC authority, Graph for feature C), `data` (Postgres all tiers, Redis), `llm` (gateway:
  test panel, Data API sync), P2 `tools` (hub: discovery, connection test, test-panel tool calls).
- Background services and who runs them:

| Service | Interval | Leader | Writes |
|---|---|---|---|
| `OrganisationSync` | 5 min + on demand | `pg_try_advisory_xact_lock` per run, as `AdminApi/ModelListSync.cs:148-153` | `Departments`, `Teams` (changed rows only) |
| `GatewayModelSync` (P1, moved from P2) | 5 min + on demand | same pattern | `GatewayModels` (changed rows only) |
| `RetentionService` | nightly | **both slots**; each step batch is idempotent and runs under `pg_try_advisory_xact_lock(<step>)`, so the slots share or skip work and either slot's host may be down ([`../performance-pass.md`](../performance-pass.md) §3.5); `Admin:Retention` (default `true`) stays as an off switch | chat/audit/config steps, shred-bucket destruction (data-model §7–§8) |
| `DriftWatcher` (P2) | 60 s | advisory lock | `Tools.State` from `ToolDefinitionObservations` (tools §4.4) |
| `ConfigVersionMonitor` | `rag_config` + 2 s poll | every instance | none (keeps the admin's own snapshot for validators and the test panel) |

- Request body: 1 MB like the gateway (`AdminApi/Program.cs:20`), except the upload file route (50 MB + multipart
  overhead, `IHttpMaxRequestBodySizeFeature` per endpoint) and its own rate-limit policy (§4.5 *Uploads*); the proxy's
  `client_max_body_size` for that path is 60 MB.

### 2.3 BFF, CSP and how the UI is served

- BFF exactly as identity §1: cookie names `__Host-ume-rag-admin`, `__Host-ume-rag-csrf`, readable `RAG-XSRF-TOKEN`
  (so `ui/api/client.ts:3` changes only the constant), `/bff/login|logout|user|session/extend`, 401 for `/api` without a
  session, 403 for missing rights, 6002/6003/6005/6006 events.
- Pipeline order copied from `AdminApi/Program.cs:67-98`: security headers with the same CSP string
  (`default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; connect-src 'self';
  object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'`, `:68-73`), `no-store` on `/api` and
  `/bff`, Scalar with nonce for platform admins (`:74`, `:94-95`), `AdminActivityMiddleware`, authn/z, per-user rate
  limit (`Admin:RequestsPerMinute` 120), antiforgery, `/api` group with `AdminValidationFilter`
  (`:84-85`), `UseDefaultFiles`/`UseStaticFiles`/`MapFallbackToFile("index.html")` (`:96-98`).
- CSP holds for everything new: fonts self-hosted (fontsource), Vue Flow's `style.css` imported into the bundle, no
  inline script; Vue's `:style` bindings set styles through the CSSOM, which `style-src 'self'` does not block
  (*to verify* with Vue Flow under an enforced CSP in E2E, §15). Preview outputs, CSV exports and conversation views
  are fetched from `/api` (never blob URLs from another origin).
- Cross-links to the gateway admin (separate app, plan answered 1): `Admin:GatewayAdminUrl`; member groups →
  `{url}/organisation`, a team's budget → `{url}/budgets`, a team's failed model calls →
  `{url}/usage?tab=requests&outcome=errors&teamId=…` (`docs/admin-ui.md:97-99`).

### 2.4 Interfaces to other components

| To | How | Used for |
|---|---|---|
| `config` (main) | EF `RagDbContext`, role `ume_rag_admin` S I U D (data-model §1.2) | all configuration |
| `content` (each tier) | S, plus I on `Jobs` (main) | documents, runs, reports, evaluation, previews, test panel retrieval |
| `chat` (each tier) | S, plus **U on `Feedback.ReviewState`** (new grant, §13) | feedback page, comment reveal, conversation review |
| `audit` (main) | S I on `AuditLog`, `ConversationAccess`, `AuthFailures`; S on tool tables | audit page, access log |
| Blobs | `rag-blobs` rw (uploads; preview outputs read) + `rag-blob-<level>` unwrap | uploads, preview outputs, PII highlight previews |
| `IKeyProvider` | chat master keys (file provider in the pilot) | comment and conversation decryption (§8) |
| Gateway | `Gateway:*` (platform key or interim team keys) | test panel embedding + answer; Data API key `OrgSync__DataApiKey` (`catalog.read`) for §4.10 |
| `rag-tools` (P2) | mTLS + `admin-console` service key (tools §2.2) | discovery, connection test, agent test-panel tool calls |

## 3. Roles and permissions

### 3.1 Roles and policies

Identity §1.3 is extended by one role; policies stay `RequireRole(any of …)` as in the gateway
(`AdminApi/AdminAuthentication.cs:151-158`), so roles add up.

| Role | Phase | Policies | Scope |
|---|---|---|---|
| `rag-platform-admin` | P0 | `Admin`, `Manage`, `EditPipelines`, `EditSkills`, `Edit`, `Read` | every department; platform objects |
| `rag-department-admin` | P0 | `Manage`, `EditPipelines`, `EditSkills`, `Edit`, `Read`, **`FeedbackComments`** | `departmentCodes` + feature C |
| `rag-pipeline-editor` | P0 | `EditPipelines`, `Read` | same |
| **`rag-reviewer`** (new) | P1 | **`Review`**, **`FeedbackComments`**, `Read` | same |
| `rag-skill-editor` | P2 | `EditSkills`, `Read` | same |
| `rag-editor` | P2 | `Edit`, `Read` | same |
| `rag-viewer` | P0 | `Read` | same |
| no `rag-*` role | – | none: every `/api` call 403, UI shows the no-access page | – |

`Review` and `FeedbackComments` are **not** part of `Admin`: content of conversations is never implied by an
administrative role. A platform admin who must review is also given `rag-reviewer` (visible in the audit actor roles).
`rag-reviewer` is granted per department by assigning a group per department (Entra app role assignment or
`UME_RAG_OIDC_GROUPS_REVIEWER`); its department scope is the user's `departmentCodes` plus feature C member groups, like
every role. The UI mirrors the policies with an implied-roles map as the gateway does (`ui/stores/auth.ts:10-15`,
`:60-69`), but the API decides.

### 3.2 Department scoping

- `AdminContext.Departments()` copied (`AdminApi/AdminContext.cs:81-90`) plus feature C (identity §2.3); every query of
  a department-owned entity (`Collections`, `Sources`, `Pipelines` (non-template), `Assistants`, `Skills`, `Keys` via
  teams, evaluation and run data via their collection/source) filters by it. An id outside the scope is **404**, never
  403 (no existence oracle; identity's "denials answer 404" rule).
- Platform objects (templates, node manifest, embedding profiles, clients, `GatewayModels`, tool servers, settings,
  data-protection policies) are readable where a form needs them (pickers show name, kind, level, residency only) and
  writable by `Admin` only.
- References must stay inside one department: a source uses its department's pipelines or a template (engine §13.3);
  an assistant links collections and skills of **its own department** plus platform skills (decision: no cross-department
  collection links in P1/P2; sharing works through the shared document store, R2: the other department adds the same
  source at no extra fetch cost); keys only for teams of the department (identity §6.4).
- Users with several departments get a department filter in the page header (URL query `dept=`), defaulting to all.

### 3.3 Pages per role

R read, W write, – hidden (route guard → `/forbidden`, copied `ui/router/index.ts:87-98`). PA platform admin, DA
department admin, PE pipeline editor, RV reviewer, SE skill editor (P2), ED editor (P2), VW viewer.

| Page (Swedish label) | Phase | PA | DA | PE | RV | SE | ED | VW |
|---|---|---|---|---|---|---|---|---|
| Översikt | P1 | R (all + platform health) | R | R | R | R | R | R |
| Kunskapsbanker (collections; incl. sökinställningar, testfrågor) | P1 | W | W | R | R | R | W | R |
| Källor, körningar, filer, borttagningar | P1 | W | W | R | R | R | W (no deletion decisions) | R |
| Dokument (incl. personuppgifter) | P1 | W | W (clear PII) | R | R | R | R | R |
| Bearbetning (pipelines, templates) | P1 | W (templates, locks) | W | W | R | – | R | R |
| Utvärdering och kvalitetskontroller | P1 | W (+ override) | W | W (pipeline gates) | R | R | W | R |
| Assistenter (incl. Testa) | P1 | W | W | R | R | R (+ test) | W | R |
| Målgrupp (assistant audiences) | P1 | W | W | – | R | – | R | R |
| Återkoppling | P1 | R | R + comments | – | R + comments | – | – | R (no comments) |
| Samtalsgranskning, Åtkomstlogg | P1 (limited) / P2 | access log R | access log R (own depts) | – | W | – | – | – |
| Nycklar | P1 | W | W (own teams) | – | – | – | – | – |
| Klienter | P1 | W | R | – | – | – | R | – |
| Organisation (mirror) | P1 | W (sync now) | R | R | R | R | R | R |
| Gateway, modeller | P1 | W | R (models) | – | – | – | R (models) | – |
| Inställningar, Dataskydd, Inbäddningsprofiler | P1 | W | R (data protection matrix) | – | – | – | – | – |
| Granskningslogg (audit) | P1 | R (all) | R (own departments' entries) | – | – | – | – | – |
| Tolkningsprofiler | P2 | W | W (approve, roll back) | R | – | – | R | R |
| Färdigheter | P2 | W (platform skills) | W | – | R | W | R (link only) | R |
| Verktygsservrar | P2 | W (register, approve, grant) | R (+ request) | – | – | R | – | – |

A system owner of a tool server (any `rag-*` role, tools §4.5) sees that server's approval page regardless of the
matrix; the API checks `ToolServers.SystemOwnerSubjects`.

### 3.4 API actions per policy

The matrix is data (`PermissionMatrix` in `Ume.Rag.Domain`), used for endpoint authorisation and generated tests (§11.2).

| Policy | Actions (department-scoped unless noted) |
|---|---|
| `Read` | list/detail of everything in scope; runs, reports, documents (metadata), evaluation results, feedback metadata, entity history |
| `Edit` | collections (create, edit, retrieval settings, test retrieval), sources (create, edit, pause/resume, sync now, retry failed, upload, delete files), assistants (create, edit, activate, test panel), human test questions, generate test questions, link published skills (P2) |
| `EditPipelines` | department pipelines (copy, draft, preview, validate, publish, rollback, rebase), source template form, pipeline test retrieval |
| `EditSkills` (P2) | skills of own departments (tools §3.6), skill test panel |
| `Manage` | collection publish (after the floor), delete collections/sources, deletion confirm/reject, release holds, PII clear, assistant audiences, retention and history mode within policy, keys of own teams, document reprocess |
| `FeedbackComments` | reveal one feedback comment (`public`/`internal`) |
| `Review` | open a conversation with a reason; P2: request/approve `sensitive` access, search conversations |
| `Admin` (platform) | templates, locks, node `deprecated` flags, embedding profiles, clients, `GatewayModels` manual rows, gateway links, settings, data protection, gate overrides, risk acceptance, raised agent limits, tool servers, approvals (platform side), grants, platform skills, audit log (all), organisation sync now |

## 4. Admin API

### 4.1 Conventions

| Topic | Rule | Source |
|---|---|---|
| Shape | Minimal API groups per area under `/api`, `MapGroup(...).RequireAuthorization(policy).WithTags(...)`, `.WithName()`/`.WithSummary()`; thin endpoints, logic in services; JSON camelCase, enums as strings, ISO-8601 with offset, money in USD fields `…Usd` | `AdminApi/KeyEndpoints.cs:59-80`, `docs/admin-api.md:3-6` (copy) |
| Requests | sealed records deriving `AdminRequest` with DataAnnotations + `IValidatableObject` for field rules; nested requests validated with prefixed names | `KeyEndpoints.cs:11-55`, `AdminContext.cs:216-258` (copy) |
| Errors | RFC 9457 problem details via `ApiFaultException(status, detail, extensions)`; validation = 400 with `errors`; 23505/23503/concurrency → 409 | `ServiceDefaults/ApiFaultException.cs:12-33`, `Program.cs:20-27` (copy) |
| Error codes | every non-validation fault has extension `code`; validation faults add `errorCodes: { field: [{ code, params }] }` beside the English `errors` (§4.2) | new |
| CHECK violations | 23514 → 400 `code: platform_rule` with the constraint name (should never happen: §4.6 runs first) | new |
| Concurrency | `version` in DTOs and update/delete bodies (§4.3) | new (gateway: keys only) |
| Audit | snapshot records `XxxAudit.Of(entity)` with own columns only, never secrets; `Action` verb + `EntityType`; staged with the change | `AuditSnapshots.cs:11-80`, `AdminContext.cs:141-169` (adapt: §4.4) |
| Propagation | no Redis invalidation bus and no `Ume-Change-Propagation` header: the outbox is in the transaction (data-model §5) | replaces `AdminContext.cs:177-191` |
| Pagination | small lists `page`/`pageSize` (≤ 200, default 50) → `PageDto(items, total)`; large tier-local lists (run items, documents, feedback, audit, access log) keyset `after`/`limit` → `{ items, next }` | `ReportEndpoints.cs:268-271`; `DataEndpoints.cs:67-80` |
| Search | `q` on list endpoints: prefix/contains on name, slug, URL, title within the scope; `GET /api/search?q=` for the command palette (collections, sources, assistants, skills, documents by title; ≤ 20 hits, scope-filtered) | new |
| Rate limit | per user 120/min (`Admin:RequestsPerMinute`); upload files and job polling have own policies (600/min each) | `Program.cs:53-64` (copy + 2 policies) |
| OpenAPI | `/openapi/v1.json` + Scalar for `Admin` only; the error-code list is exported as `contracts/admin/v1/error-codes.json` (CI checks the UI covers every code, §11) | `Program.cs:94-95` (copy) |
| Data reads | 6007 `rag.admin.data.read` on audit log, keys, feedback lists, access log, conversation views | identity §10 |

### 4.2 Error codes and Swedish texts

The gateway UI prints the server's message (`ui/utils/problem.ts:7-8`). Our API speaks English (answered 1) and the UI
speaks Swedish, so `problem.ts` is adapted: `problemMessage` looks up `problem.code` in `ui/utils/messages.ts`
(Swedish templates with `{params}`), then a status text (Swedish versions of `:9-21`), and shows the English `detail`
only as a last resort inside `<span lang="en">`. `mapFieldErrors` (`:26-36`) prefers `errorCodes[field]`.

```json
{ "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1", "title": "Check the highlighted fields.", "status": 400,
  "errors": { "classification": ["The assistant's level (public) is below the collection 'bygglov' (internal)."] },
  "errorCodes": { "classification": [ { "code": "assistant_below_collection",
                                         "params": { "collection": "Bygglov", "level": "internal" } } ] } }
```

```ts
// ui/utils/messages.ts (excerpt)
assistant_below_collection: 'Assistentens informationsklass är lägre än kunskapsbanken {collection} ({level}). Höj assistentens klass eller ta bort kunskapsbanken.',
concurrent_change: 'Någon annan har ändrat detta medan du arbetade. Ladda om sidan; dina ändringar finns kvar i formuläret.',
```

### 4.3 Concurrency

- Every editable aggregate root maps Postgres `xmin` as a row-version token (`uint`, Npgsql EF; *to verify* for EF
  10.0.3, §15). Child rows (`AssistantCollections`, `AssistantAudiences`, `SkillVersionLinks`, …) are saved through the
  root, and every root save sets `UpdatedAt`/`UpdatedBy` (new columns where missing, §13), so `xmin` changes whenever
  any part of the aggregate does.
- DTOs carry `version` (string); `PUT`/`DELETE`/state actions require it; mismatch → 409 `concurrent_change` with the
  current `version` in the extensions. The form keeps the user's input and offers "Ladda om och jämför" (reload the
  server state beside the user's values).
- Singletons (`Settings` per section, `DataProtectionPolicies` per level) use the same rule; the gateway's generic
  "Someone else changed the settings…" (`SettingsEndpoints.cs:91-94`) becomes the `concurrent_change` code.
- Job-backed actions are idempotent by `DedupKey` (§4.7), not by version.

### 4.4 Write path (one transaction)

```
BEGIN
  1. load aggregate (+ version check)            6. INSERT audit."AuditLog" (Actor…, Action, EntityType, EntityId,
  2. apply request; ConfigRules (§4.6)                 Details {before, after}, DepartmentId, ConfigVersion = v,
  3. SaveChanges → row triggers write ConfigOutbox     Reason, Classification, Service = 'admin')
     and bump ConfigVersion (data-model §5)      7. INSERT content."Jobs" (§4.7) when the action enqueues work
  4. SELECT "Version" FROM config."ConfigVersion" → v   (same database: Jobs is main tier) + pg_notify('rag_jobs')
  5. (version is ours: the row lock is held)     8. COMMIT  → rag_config and rag_jobs notifications delivered
```

- Adapted from `AdminContext.SaveAsync` (`AdminContext.cs:158-169`): the audit entry is staged with the change; the
  actor stamp (`AdminActor.Stamp`, `:38-49`) and the 6001 event after save (`:50-58`) are copied.
- Audit-only actions (reveal a key, read a comment, read the audit log) use `AuditAsync` (`:148-152`) without a version.
- Writes to `chat."Feedback".ReviewState` (tier-local) and `audit."ConversationAccess"` (main) are two transactions;
  the access event is written **first**, so a crash never leaves an unaudited read (§8.2).

### 4.5 Resources per area

Policy in brackets; `{dept}` = department scope applies. Bodies and DTOs are the data-model columns unless noted.

**Organisation and models**

| Method and path | Policy | Notes |
|---|---|---|
| `GET /api/departments`, `GET /api/teams` | Read | mirror with `SyncedAt`, member groups (read-only; link to gateway) |
| `GET /api/organisation/sync`, `POST /api/organisation/sync` | Read / Admin | last run, outcome, changed rows; run now |
| `GET /api/gateway/models` | Read | `GatewayModels` with residencies, `SupportsTools`, context window, prices, `Source` |
| `POST /api/gateway/models/sync`; `PUT /api/gateway/models/{name}` | Admin | run now; manual row (aliases, `SupportsTools` until the catalogue has `features`) |
| `GET /api/gateway/status`; `GET/PUT/DELETE /api/gateway/key-links/{teamId}/{purpose}` | Admin | platform key check (one `x-ume-max-residency: OnPrem` embedding call and the delegation echo), interim team keys (secret write-only, prefix shown) |

**Collections and retrieval**

| Method and path | Policy | Notes |
|---|---|---|
| `GET/POST /api/collections`, `GET/PUT/DELETE /api/collections/{id}` | Read / Edit / Manage (delete) | create: name, slug (from name, ASCII), department, owner team, classification; profile = default; delete refused while an assistant links it (409 `collection_in_use` with the list) |
| `PUT /api/collections/{id}/retrieval-settings` | Edit | ungated values saved at once; gated values need `gateRunId` of a passed or overridden `retrieval-change` run whose candidate hash equals the body (V16) |
| `POST /api/collections/{id}/retrieval-settings/gate` | Edit | enqueues the `retrieval-change` gate (§4.7), returns the job |
| `GET /api/collections/{id}/coverage-proposal` | Read | distribution of `best(C)` over the set's questions, p95 + 0.03 (knowledge §7) |
| `POST /api/collections/{id}/test-retrieval` | Edit, EditPipelines | retrieval-only test panel (§4.9), incl. `building` |
| `POST /api/collections/{id}/publish` | Manage | requires the latest `collection-publish` run `pass` or overridden (V27); sets `PublishedAt`, `PublishGateRunId` |

**Sources, runs, uploads**

| Method and path | Policy | Notes |
|---|---|---|
| `POST /api/collections/{id}/sources`, `GET/PUT/DELETE /api/sources/{id}` | Edit / Manage (delete) | settings per `contracts/sources/v1/{type}.schema.json`; template + form values (§6.1); schedule default per type (engine §9.6) |
| `POST /api/sources/preflight` | Edit | unsaved settings → `preview` `preflight` job (§4.7) |
| `POST /api/sources/{id}/impact` | Edit | effect tags of a changed settings object with counts ("38 dokument tas bort"); returns `impactToken` that the `PUT` must echo when the tag is `scope`/`identity` (V25) |
| `POST /api/sources/{id}/hierarchy` | Edit | `preview` `hierarchy` job (crawl: host → path prefixes) |
| `POST /api/sources/{id}/sync`, `/retry-failed`, `/pause`, `/resume` | Edit | sync now answers 200 with the existing run when one is queued/running (dedup) |
| `GET /api/sources/{id}/runs` (keyset), `GET /api/runs/{id}`, `GET /api/runs/{id}/items` (keyset; `outcome`, `reason`, `q`) | Read | engine §10.5 views; crawl report from `Runs.Report` |
| `POST /api/runs/{id}/cancel` | Edit | `command` `cancel-run` |
| `GET /api/runs/{id}/deletions` (+ `?format=csv`), `POST /api/runs/{id}/deletions/confirm`, `/reject` | Read / Manage | connectors §13.3–§13.5; body carries `proposedCount` seen by the user; 409 `deletion_superseded` if a newer run took over |
| `GET /api/sources/{id}/held`, `POST /api/sources/{id}/held/release` | Read / Manage | `command` `release-hold` (new kind, §13) |
| `POST /api/sources/{id}/upload-batches` → `{ batchId }`; `PUT /api/upload-batches/{batchId}/files/{n}` (one file, raw body, `X-File-Name`); `POST /api/upload-batches/{batchId}/commit`; `DELETE /api/upload-batches/{batchId}` | Edit | per-file requests give per-file progress and retry; extension pre-check 415; SHA-256 while streaming; encrypted blob; commit writes the manifest and the `sync` job (adapts connectors §9.1); uncommitted batches swept after 24 h |
| `GET /api/sources/{id}/files` (keyset), `POST /api/sources/{id}/files/delete`, `/restore` | Read / Edit | delete/restore = a batch with entries (connectors §9.3) |
| `GET /api/sources/{id}/quarantine` | Read | metadata only (connectors answered 3) |

**Documents**

| Method and path | Policy | Notes |
|---|---|---|
| `GET /api/documents` (keyset; `collectionId`, `sourceId`, `q`, `pii`, `outcome`) | Read | tier fan-out, in-memory join with config (§4.8) |
| `GET /api/documents/{id}` | Read | sources and display names **of the caller's departments only** (connectors §9.4), chunk sets, pipeline version per source, run items 90 days, PII counts and state |
| `GET /api/documents/{id}/chunks` | Edit, EditPipelines | chunk text with heading path, tokens, `OverlapChars` |
| `POST /api/documents/{id}/pii-preview` | Manage | `preview` job up to *Detect personal data*; spans shown from preview blobs (24 h); audited `pii.view` |
| `POST /api/documents/{id}/pii-clear` | Manage | reason ≥ 10; department must have a live reference (V26); `command` `clear-pii-flag` |
| `POST /api/documents/{id}/reprocess` | Manage | `command` `reprocess-document` |

**Pipelines**

| Method and path | Policy | Notes |
|---|---|---|
| `GET /api/node-manifest` | Read | types, versions, ports, `paramsSchema`, metadata, live support (`WorkerNodeSupport` < 60 s) |
| `GET /api/pipelines` (`templates`, `departmentId`), `GET /api/pipelines/{id}`, `GET /api/pipelines/{id}/versions` | Read | – |
| `POST /api/pipelines` `{ templateId, departmentId, name }` | EditPipelines | copy (engine §13.3) |
| `PUT /api/pipelines/{id}/draft` `{ definition, version }` | EditPipelines | save-time rules 1–9 (warnings allowed, returned with `nodeId`/`port`/`code`) |
| `POST /api/pipelines/{id}/draft/validate` | EditPipelines | full rules 1–12 without saving |
| `POST /api/pipelines/{id}/draft/preview` `{ documentIds | urls, untilNodeId, basePreviewJobId }` | EditPipelines, Edit | `preview` `node-preview` |
| `GET /api/pipelines/{id}/diff?from=&to=` | Read | §6.5 |
| `POST /api/pipelines/{id}/publications` | EditPipelines | draft → `gating`; enqueues plan + gate; returns `publicationId` (= the version id) |
| `GET /api/publications/{id}`, `POST /api/publications/{id}/confirm`, `/cancel` | Read / EditPipelines | §6.6 |
| `POST /api/pipelines/{id}/rollback` `{ versionId }` | EditPipelines | never gated; gate result shown afterwards (engine §12.3) |
| `POST /api/pipelines/{id}/rebase` | EditPipelines | new draft from the updated template keeping the copy's form values |
| `PUT /api/pipeline-versions/{id}/layout` (P2) | EditPipelines | `PipelineCanvasLayouts`, not audited, no outbox (no trigger on that table, data-model §5) |
| `PUT /api/templates/{id}/draft`, template publish, locks, `minVersion` | Admin | as department pipelines; template versions never re-process copies |

**Jobs and previews**

| Method and path | Policy | Notes |
|---|---|---|
| `GET /api/jobs/{id}` | Read (job's department) | `State`, `Result` (secrets and wrapped keys stripped), queue position and age |
| `GET /api/previews/{jobId}/documents/{documentId}/nodes/{nodeId}/output` | Edit, EditPipelines | decrypted preview blob, ≤ 2 MB, `text/markdown` or JSON (engine §11) |

**Evaluation and gates**

| Method and path | Policy | Notes |
|---|---|---|
| `GET /api/collections/{id}/evaluation` | Read | set size per layer, stale/retired counts, latest runs, trend (weekly scheduled runs) |
| `GET /api/collections/{id}/evaluation/questions` (keyset; `layer`, `state`, `q`) | Read | question, expected answer, expected documents (titles), last rank |
| `POST /api/collections/{id}/evaluation/generate` | Edit | `eval` `generate-set`/`top-up-set` with cost forecast in the response; refused while a gate is open (processing §13.3) |
| `POST/PUT/DELETE /api/collections/{id}/evaluation/questions[/{qid}]` | Edit | human layer via `command` `edit-eval-question`; expected documents picked by search (P1) |
| `GET /api/evaluation-runs/{id}`, `GET /api/evaluation-runs/{id}/results` | Read | arms, intervals, wins/losses, McNemar p, escalation, per-question ranks (§7.2) |
| `POST /api/evaluation-runs/{id}/override` `{ reason }` | Admin | reason ≥ 20; per run (processing §14.4) |

**Assistants**

| Method and path | Policy | Notes |
|---|---|---|
| `GET/POST /api/assistants`, `GET/PUT/DELETE /api/assistants/{id}` | Read / Edit / Manage (delete) | form of §5.5.4; `Mode` `rag` (P1), `agent` (P2) |
| `POST /api/assistants/{id}/activate`, `/deactivate` | Edit | `State`; activation runs every V-rule incl. residency (V8) |
| `PUT /api/assistants/{id}/audiences` | Manage | P1 kind `client` only; V7–V9 |
| `PUT /api/assistants/{id}/retention` | Manage | `HistoryMode`, `RetentionDays` within the level's policy (V10) |
| `POST /api/assistants/{id}/test`; `POST /api/assistants/test` (unsaved form) | Edit, EditSkills | §4.9; never stored |
| `GET /api/assistants/{id}/stats?from=&to=` | Read | `AssistantDailyTotals` |
| `PUT /api/assistants/{id}/limits` (P2) | Edit (≤ defaults) / Admin (≤ hard caps) | V14 |
| `POST /api/assistants/{id}/risk-acceptance` (P2) | Admin | reason ≥ 40, binds `compositionHash` (tools §5.4) |

**Skills and tool servers (P2)** — routes, bodies and rules exactly as tools design §3.5–§3.7, §4.4–§4.5 and §9.2:
`/api/skills` (draft, validate, publish → `skill-gate`, serve with the MAJOR auto-pin preview list, rollback, retire,
audiences; export/import P3), `/api/tool-servers` (`test`, register, update, credential (write-only), discover),
`/api/tool-definitions/{id}/approvals` (`platform-admin` by `Admin`, `system-owner` by a listed subject; the two
approvers differ), `/api/tools/{id}/grants`, `/api/tools/{id}/reapprove`, `/api/tool-observations`.

**Clients, keys, settings**

| Method and path | Policy | Notes |
|---|---|---|
| `GET/POST/PUT/DELETE /api/clients[/{id}]` | Read (pickers) / Admin | `CK_Clients_*` as field rules (V21); raising a ceiling or changing `StoresHistory` re-validates every assistant offered to the client |
| `GET /api/clients/{id}/health` | Admin | missing `X-Rag-Owui-Message-Id` warning (chat §11.4), feedback import state (§8.6) |
| `/api/keys…` | Manage (own teams) / Admin | copied from the gateway (`KeyEndpoints.cs:61-133`): create (secret once), update, rotate (immediate or 24 h grace), reveal/copy (Admin, audited), revoke; `ume-rag-sk-`/`-pk-` (identity §6.4) |
| `GET/PUT /api/settings/{section}` (`retention`, `deletion-guard`, `crawl`, `gates`, `retrieval`, `chat`, `tools`, `pii`, `worker`) | Admin | sections of `config."Settings"` with `version` each |
| `GET/PUT /api/data-protection/{level}` | Read / Admin | V29 |
| `GET/PUT /api/embedding-profiles…` | Read / Admin | P1 read-only; changes P3 (R3) |

**Feedback, conversations, audit, overview** — §8, §9; `GET /api/overview` returns the attention items of §5.5.1 for
the caller's scope.

### 4.6 Cross-rules (one table for steps 1–9)

Run by `ConfigRules` on save of the entity, on every change that can break a dependant (column *Re-checked when*), and
again inside the publish/activate transaction against the snapshot being written. A failing dependant lists every
affected object by name (problem extension `affected[]`). Levels order `public < internal < sensitive`; residency
ladder `public → External`, `internal → Eu`, `sensitive → OnPrem`, ordered `OnPrem < Eu < External`.

| # | Rule | Code | Re-checked when | Source |
|---|---|---|---|---|
| V1 | source level ≤ its collection's level | `source_above_collection` | source save; collection level lowered | data-model §2.1 |
| V2 | raising a collection's level enqueues `recompute-membership`; lowering below a source or below a linking assistant's need is refused | `collection_level_conflict` | collection save | engine §15 |
| V3 | assistant level ≥ every linked collection (direct and through linked skills) | `assistant_below_collection` | assistant save; collection level raised; skill served/pinned | data-model §2.1, knowledge §4.3 |
| V4 | assistant level ≥ every linked skill; skill level ≥ its collections and its tools' **result level** | `assistant_below_skill`, `skill_below_link` | skill draft/serve; tool server result level | tools §3.10 |
| V5 | for every reachable tool server: max(assistant level, ceilings of history-resending audience clients) ≤ **clearance** | `tool_clearance` | assistant save/audience; skill serve; server edit | tools §5.3 |
| V6 | every reachable tool server's result level ≤ assistant level | `tool_result_level` | as V5 | tools §5.3 |
| V7 | an audience client's ceiling ≥ assistant level (else the assistant is invisible to it, identity §4.3) | `audience_above_ceiling` | audience save; client ceiling lowered; assistant level raised | identity §4.1 |
| V8 | **residency**: `GatewayModel` (and, for agent assistants, the tool-capable model) servable at the ladder of max(assistant level, ceiling of every history-resending audience client): `min(GatewayModels.Residencies) ≤ ladder`; unknown model → refused | `model_residency` | activate; audience save; model sync changed residency (warning on Overview, assistant left active until the next save, chat checks per call) | chat §8.2, tools §6.4 |
| V9 | `sensitive` assistants only for clients with `StoresHistory = false` when `DataProtectionPolicies.ClientsWithoutHistoryOnly` | `client_stores_history` | audience save; policy change | platform §8a |
| V10 | `HistoryMode` ∈ allowed modes, `RetentionDays` ≤ level max (default ≤ max ≤ 90) | `retention_outside_policy` | assistant save; policy tightened (lists assistants; they keep their value until saved) | data-model §2.1 |
| V11 | ≤ 20 collections per assistant (union with skills'), all of the assistant's department | `too_many_collections`, `collection_other_department` | assistant save; skill serve | knowledge §4.2, §3.2 here |
| V12 | ≤ 20 skills per assistant; RAG-mode skill bodies ≤ 6,000 estimated tokens in total | `too_many_skills`, `skill_bodies_too_long` | assistant save; skill serve | tools §3.4, §3.9 |
| V13 | lethal trifecta false, or a risk acceptance whose `compositionHash` matches | `lethal_trifecta` | assistant save/activate; skill link/serve; tool approval/grant | tools §5.4 |
| V14 | agent: `SupportsTools`, synced price; limits ≤ `AgentDefaults` unless `Admin`, always ≤ `AgentHardCaps` | `model_no_tools`, `price_unknown`, `limit_above_default`, `limit_above_cap` | assistant save; settings change | tools §6.4–§6.5 |
| V15 | not-found texts ≤ 1,000 characters each, ≤ 5 links, `https`, host matches `Settings.Chat.AllowedLinkHosts` | `link_host_not_allowed` | assistant save; allow-list narrowed | chat §9.4, §10.1 |
| V16 | `RetrievalSettings`: `vectorWeight`, `keywordWeight` 0–5, `candidates` 20–400, `efSearch` 40–400, `maxChunksPerDocument` 1–10 only with a passed/overridden `retrieval-change` run of the same values; `coverageMaxDistance` 0.05–1.0, `expandMaxTokens` 0–4,000 directly | `retrieval_change_not_gated` | collection settings save | knowledge §12.1, processing §14.5 |
| V17 | `QuerySteps`: `topK` 1–50, history 0–20 messages / ≤ 8,000 tokens, `contextTokens` ≤ the model's `ContextWindow` when known, `maxOutputTokens` 64–4,096, temperature 0–1, language `auto`/`sv`; condense model on-prem | `query_steps_invalid` | assistant save; model sync | chat §5–§6, §10.3 |
| V18 | skill version > every existing; bump rule (removed link/level change/MCP off ⇒ MAJOR; link added/body ⇒ MINOR); serve validated against every following assistant; MAJOR serve auto-pins other departments' assistants (preview list shown before confirm) | `version_not_increasing`, `bump_too_small`, `serve_breaks_assistants` | skill publish/serve | tools §3.5 |
| V19 | skill tool links need a `ToolDepartmentGrant` for the skill's department; platform skills only `AllDepartments` tools | `tool_grant_missing` | skill draft; grant revoked (lists skills) | tools §3.6 |
| V20 | skill MCP exposure: skill level ≤ every audience client's ceiling | `skill_audience_ceiling` | skill audiences; client ceiling | tools §3.10 |
| V21 | clients: `StoresHistory` ⇒ ceiling ≠ `sensitive`; `mcp-client` ⇒ ceiling ≤ `internal`; `TaskModel` ⇒ `TaskModelResidency = OnPrem`; `ToolPolicy` per kind default | `client_ceiling_rule` | client save | data-model `CK_Clients_*`, tools §5.1 |
| V22 | tool servers: `CK_ToolServers_Clearance`; approvers differ; system-owner approval only by a listed subject; `write` tools refused while `WriteToolsEnabled = false` | `toolserver_rule`, `same_approver` | server save; approval | tools §4.5, §5.3 |
| V23 | keys: team in the caller's departments; scopes only assistants/collections of the key team's department; `pk` keys only `public` assistants, exact origins | `key_scope_invalid` | key save; assistant level raised (lists `pk` keys) | identity §6.4–§6.5 |
| V24 | pipeline rules 1–9 on save, 10–12 on publish; source uses its department's pipelines or a template | engine codes (`cycle`, `locked_node_missing`, `node_version_unsupported`, …) | draft save; publish; template `minVersion` raised | engine §4, §13.3 |
| V25 | source settings: URL list ≤ 5,000 (canonicalised, duplicates listed); crawl `include`/`exclude` compile (`re:` with `NonBacktracking`, 50 ms); `maxPages` ≤ platform cap; `maxFileSizeMb` ≤ 50; cron 5 fields; `collectionId` immutable; `scope`/`identity` changes need the `impactToken` | `source_settings_invalid`, `impact_not_confirmed` | source save | connectors §4–§5, §9, §12.3 |
| V26 | reasons: gate override ≥ 20 (`Admin`), PII clear ≥ 10 and a live reference in the caller's departments, risk acceptance ≥ 40, conversation access ≥ 20 (comment reveal: the fixed one-click text) | `reason_too_short`, `no_live_reference` | the action | processing §9.5, §14.4; tools §5.4 |
| V27 | collection publish only after its latest `collection-publish` run passed the floor (≥ `GateFloor` 0.70) or was overridden | `floor_not_met` | publish | processing §14.3 |
| V28 | deletion decisions only by `Manage` of the source's department, while the run is `awaiting-deletion-confirmation` and the proposal is the current one | `deletion_superseded` | confirm/reject | connectors §13.3 |
| V29 | data protection: `internal`/`sensitive` ⇒ encryption required; default ≤ max ≤ 90; changes apply to new key epochs only | `policy_rule` | policy save | data-model `CK_DataProtectionPolicies_*`, §7 |
| V30 | `Settings` ranges as the `CK_Settings_*` checks (guard 1–100 %, min/max counts, floors 0.5–0.95, sample 20–100, …) | `setting_out_of_range` | settings save | data-model §2.1 |
| V31 | embedding profile fixed per collection (`platform-default`); other profiles only `Admin` (P3) | `profile_admin_only` | collection save | R3 |
| V32 | platform model settings `Settings.EvaluationModel`, `Settings.EvaluationJudgeModel`, `Settings.Chat.CondenseModel`, `Settings.Tools.SkillGateModel` are `GatewayModels` rows servable `OnPrem` (`SkillGateModel` also `SupportsTools`, tools §3.7 routing) | `settings_model_residency` | settings save; model sync changed residency (warning on Overview) | processing §13.1, chat §5, tools §3.7 |

### 4.7 Jobs for the worker

The admin holds `INSERT` on `content."Jobs"` (data-model §1.2); payloads are ids and parameters only (≤ 4 KB, engine
§9.1). Enqueue = the data-model §4.2 dedup (`pg_advisory_xact_lock(hashtextextended(DedupKey))`, insert only without an
open job of that key) and `pg_notify('rag_jobs', class)` in the same transaction as any config change it belongs to.
`DepartmentId` = the subject's department (fairness), `TeamId` = owner team (cost), `Classification` = the subject's
level (tier).

| Admin action | Class / kind | Dedup key | Priority | Result read from |
|---|---|---|---|---|
| Preview a draft | `preview` `node-preview` | – | – | `Jobs.Result` + preview blobs |
| Preflight (unsaved source) | `preview` `preflight` (URL lists: first 20 URLs) | – | – | `Jobs.Result` |
| Crawl tree for patterns | `preview` `hierarchy` | `hierarchy:{sourceId}:{parent}` | – | `Jobs.Result` |
| PII highlights | `preview` `node-preview` until *Detect personal data*, spans on | `pii-preview:{documentId}` | – | preview blobs (24 h) |
| Publish plan | `preview` `publish-plan` | `plan:{versionId}` | – | `Jobs.Result` |
| Pipeline gate | `eval` (gate hook, `pipeline-publish`) | `gate:{versionId}` | – | `EvaluationRuns` |
| Collection floor | `eval` `collection-publish` | `gate:collection:{id}` | – | `EvaluationRuns` |
| Retrieval-settings gate | `eval` `retrieval-change` (candidate in payload) | `gate:retrieval:{id}` | – | `EvaluationRuns` |
| Skill gate (P2) | `eval` `skill-gate` | `gate:skill:{versionId}` | – | `EvaluationRuns` |
| Generate / top up test questions | `eval` `generate-set` / `top-up-set` | `eval-generate:{collectionId}` | – | `EvaluationSets`, questions |
| Sync now / retry failed | `sync` (`Trigger = manual`, `kind` `sync`/`retry-failed`) | `sync:{sourceId}` | 10 | `Runs` |
| Upload commit | `sync` (`Trigger = upload`) | `sync-upload:{batchId}` | – | `Runs`, `Uploads` |
| Pipeline publish confirm / rollback | `sync` `reprocess` per source (`Trigger = publish`/`rollback`) | `sync:{sourceId}` (a running sync gets a follow-up) | – | `Runs` |
| Deletion decision | `command` `confirm-deletion` / `reject-deletion` | `deletion:{runId}` | – | `Runs.State`, `DeletionProposals` |
| Release held references | `command` `release-hold` (new) | `release-hold:{sourceId}` | – | `DocumentSources.HeldAt` |
| PII clearance | `command` `clear-pii-flag` | `pii-clear:{documentId}` | – | `Documents.PiiClearedAt` |
| Human question edit | `command` `edit-eval-question` | – | – | `EvaluationQuestions` |
| Reprocess one document | `command` `reprocess-document` | `reprocess:{documentId}` | – | run items |
| Cancel a run | `command` `cancel-run` | `cancel:{runId}` | – | `Runs.State` |
| Raise a collection's level (V2) | `command` `recompute-membership` | `membership:{collectionId}` | – | `Chunks.CollectionIds` (engine §15) |
| Approve / roll back a parse profile (P2) | `command` `approve-profile` / `rollback-profile` | `profile:{id}` | – | `ParseProfiles` |

The UI polls `GET /api/jobs/{id}` (or the run/evaluation it created) every 2 s, every 5 s after 30 s, every 15 s after 5
minutes, and not while the tab is hidden (`document.visibilityState`); there is no push channel (the gateway has none
either). A `dead` or `failed` job shows its `LastError` code through the §4.2 table.

### 4.8 Reading `content`, `chat` and `audit`

- **Tiers**: `ContentReader`/`ChatReader` take a level set, resolve tiers with `ITierResolver` (data-model §6) and run
  one query per tier, merging in memory; keyset cursors encode (sort key, id, tier). No SQL join between `config` and
  tier-local tables (data-model §6 rule 3): config names are resolved from the admin's snapshot.
- **Scope first**: every content query starts from config ids in the caller's scope (collections, sources,
  assistants), so a tier-local row of another department is never selected.
- **Counts**: run counters are derived (engine §10.4); the overview uses `Runs` and `Jobs` indexes only, never a
  `RunItems` scan.
- **Display names**: upload file names come from `DocumentSources.DisplayName` of the caller's sources only; another
  department's file name is never shown (connectors §9.4); a shared document shows "används även av 1 annan förvaltning"
  without naming the source.
- **Chat**: metadata columns only, except the two audited decryption paths of §8. **Audit**: §9.

### 4.9 Test panel

Runs `Ume.Rag.Answering` in the admin process (identity §4.8): principal `admin-console` with the admin's `sub`,
access by the admin's role scope (assistant's department ∈ caller's departments), assistant audiences ignored,
`building` collections included, attribution to the assistant's owner team (knowledge §4.2, §4.5), residency ceiling at
the assistant's level (no history: first-message rule; with test history: chat §8.2's rule for Open WebUI if the
assistant has such a client in its audience). Nothing is stored (no conversation, message, feedback or statistics row).
Query embeddings use the in-process L1 cache only (knowledge §5.4), keyed with a per-process random HMAC key: the
admin host reads no Redis `qemb` keys and holds no `Security:CacheKeyPepper`.

| Input | Output (JSON, non-streamed; the answer arrives within the chat budget, ≤ 60 s) |
|---|---|
| `question`; optional `history[]` (≤ 10 messages, to test condense); saved assistant id **or** the unsaved form (validated with warnings allowed, so a non-specialist tries before saving); optional `collectionIds` subset | `retrieval`: searched collections (with `building` flag), coverage `{status, bestDistance, threshold, collection}`, candidates `{rank, title, url or file name, headingPath, text (600 characters, expandable), distance, keywordRank, vectorRank, fused, cited}`, timings; `condense` `{used, question}`; `prompt` token budget per part (frame, system, skills, history, sources, question) against the limit; `answer` `{raw, delivered, filtered[] (kind, text), citations [n] → document, outcome, language}`; `gateway` `{model, residency, latencyMs, ttftMs, inputTokens, outputTokens, costUsd}`; P2 `toolCalls[]` `{tool, state, durationMs, arguments (admin view), resultExcerpt}` |

- Rate: 20 test questions per minute per user (`Settings.Chat.MessagesPerMinutePerUser`), metric
  `rag.admin.test_panel{outcome}`; no audit entry (not a change), no 6007 (the content is the caller's own department's).
- The answer is shown as **plain text** with preserved line breaks and `[n]` chips (no Markdown renderer, §10.2);
  "Visa som i chatten" is not offered: Open WebUI is the place to see rendering.
- P2 agent assistants call the hub as `admin-console` (needs `Clients.ToolPolicy = read` for that client, §13), with
  the same limits and the status events listed as steps; write tools stay impossible (`Tools:AllowWrite=false`).
- Retrieval-only variant `POST /api/collections/{id}/test-retrieval` for pipeline editors and collection editors.

### 4.10 Organisation and model catalogue sync

| Sync | Source (gateway Data API, `catalog.read` key `OrgSync__DataApiKey`) | Rule |
|---|---|---|
| Departments | `GET /v1/catalog/departments` (`DataEndpoints.cs:140-142`; `memberGroups` after gateway release C) | upsert by id; only changed rows written (no version bump without change, data-model §2.1); a department missing from the gateway → `IsActive = false` (FKs are Restrict); member groups replace the stored array |
| Teams | `GET /v1/catalog/teams` (`:143-145`; `code` after release C) | as departments |
| Models | `GET /v1/catalog/models`, `/providers`, `/prices` (`:149-157`) | `GatewayModels` row per chat/embedding deployment: `Kind = model`, `Residencies = [provider residency]`, latest price with `EffectiveFrom ≤ now`; `SupportsTools`/`ContextWindow` manual until the catalogue carries `features`/`contextWindow` (gateway-changes §7.3); disabled models removed from pickers, kept for existing assistants with a warning |
| Route aliases | none today (the Data API has no routes, `DataEndpoints.cs:139-160`) | manual rows `Kind = route`, `Residencies` = its targets' residencies, `Source = manual` by a platform admin; servable at a ceiling when any target is within it (the gateway then routes within the ceiling, gateway-changes §3); replaced by a synced `/v1/catalog/routes` when the gateway adds it (§13) |

Failures keep the last mirror; after 3 failed runs (15 min) the Overview shows "Synkningen mot AI-gatewayen fungerar
inte sedan {tid}" to platform admins, metric `rag.admin.sync{kind,outcome}`. Sign-in scope (feature C) reads the mirror,
so a stale mirror narrows or keeps scope, never widens it (identity §2.6).

## 5. UI information architecture

### 5.1 Stack and what is copied

- Copied unchanged: toolchain and scripts, `.npmrc`, lint (incl. `eslint-plugin-vuejs-accessibility`), design system
  (`styles/*`, `components/ui/*`, `components/layout/*`, icons subset), `design-system.lock` drift report
  (repo-and-deployment §2.3); `api/client.ts` (constant `RAG-XSRF-TOKEN`), `composables/useFormErrors.ts:8-44`,
  `useTableView.ts:9-54`, `stores/loadable.ts`, `test/*` helpers; charts `components/usage/TimeSeriesChart.vue` (bar/line
  with table view) for trends.
- Adapted: `utils/problem.ts` (§4.2), `utils/format.ts` (dates `sv-SE`, e.g. "30 okt. 2026 14:05"), `stores/auth.ts`
  (RAG roles), `router/nav.ts` + `router/index.ts` (pages below; same `meta.roles` guard and focus handling,
  `:46-54`, `:87-111`; title suffix "· Kunskapsplattform", product name N1 open), `index.html` `lang="sv"`.
- New: `utils/labels.ts` (Swedish, from scratch: the gateway's is English), `utils/messages.ts` (error codes),
  `composables/useJobPoller.ts`, `components/rag/*` (classification badge, run state badge, gate result card, setup
  checklist, preview viewer, test panel, diff view), `views/*`.

### 5.2 Navigation per role

Grouped by the job people come to do, as the gateway's side menu (`docs/admin-ui.md:5-13`); groups and items are
filtered by role (`roles` per item, `ui/router/nav.ts:11-26`). Ctrl K search copied (`AppShell.vue:62`), extended
with `GET /api/search` hits. Phone bottom bar (`AppShell.vue:208-209`): Översikt, Kunskapsbanker, Assistenter,
Återkoppling, Meny.

```
┌──────────────────────────┬──────────────────────────────────────────────────────────────────────────────────────┐
│ ◧ Kunskapsplattform      │  Sök (Ctrl K)                                   Förvaltning: Alla ▾   ☾   Anna A. ⎋  │
│   Umeå kommun            ├──────────────────────────────────────────────────────────────────────────────────────┤
│ ÖVERBLICK                │                                                                                      │
│   Översikt          (3)  │   <main id="main">                                                                   │
│ KUNSKAP                  │                                                                                      │
│   Kunskapsbanker         │                                                                                      │
│   Dokument               │                                                                                      │
│   Bearbetning            │                                                                                      │
│   Tolkningsprofiler  P2  │                                                                                      │
│ ASSISTENTER              │                                                                                      │
│   Assistenter            │                                                                                      │
│   Färdigheter        P2  │                                                                                      │
│   Verktygsservrar    P2  │                                                                                      │
│ UPPFÖLJNING              │                                                                                      │
│   Återkoppling           │                                                                                      │
│   Samtalsgranskning      │                                                                                      │
│ ÅTKOMST                  │                                                                                      │
│   Klienter · Nycklar     │                                                                                      │
│   Organisation           │                                                                                      │
│ PLATTFORM                │                                                                                      │
│   Gateway och modeller   │                                                                                      │
│   Dataskydd              │                                                                                      │
│   Inställningar          │                                                                                      │
│   Granskningslogg        │                                                                                      │
│ ↗ AI-gatewayens admin    │                                                                                      │
└──────────────────────────┴──────────────────────────────────────────────────────────────────────────────────────┘
```

Start page per role: Översikt (everyone except a reviewer-only user, who starts on Återkoppling). Badges: Översikt
counts attention items (gateway `badge` pattern, `nav.ts:25`). No e-mail: drift, pending approvals, awaiting deletions
and failed gates appear as attention cards and badges, plus SIEM events for the security-relevant ones (decision 16).

### 5.3 Pages by phase

| Page | P1 | P2 | P3 |
|---|---|---|---|
| Översikt | attention items, sync health per department, queue age, worker/node support, org/model sync | drift, approvals, profile trials | trends |
| Kunskapsbanker | list; detail tabs Översikt (checklist, sources, counts, state), Källor, Dokument, Testfrågor och kvalitet, Sökinställningar, Historik | audiences for token callers (`CollectionAudiences`) | analytics |
| Källa | URL list, crawl, upload forms; preflight; hierarchy; impact; runs; crawl report; files; quarantine; deletions; held | SharePoint with preflight | more connectors |
| Dokument | list, detail, chunks, PII highlight + clear, reprocess | – | – |
| Bearbetning | templates, department copies, template form, node list + generated forms + JSON view, preview, diff, publish, rollback, rebase | **canvas** (Vue Flow) | query-side canvas |
| Tolkningsprofiler | – | list, trial results, approve, rollback, kill switch | – |
| Assistenter | RAG form, test panel, audiences (clients), retention, stats | agent mode, skills, limits, risk acceptance, widget audiences | – |
| Färdigheter, Verktygsservrar | – | tools §3, §4 pages | import/export |
| Återkoppling | ratings, comment reveal, import health | rewrite into test question | trends |
| Samtalsgranskning | conversation behind a feedback item (`public`/`internal`), access log | search by assistant and period, `sensitive` two-person flow | user notification |
| Klienter, Nycklar, Organisation, Gateway och modeller | yes | widget `pk` keys and origins | OBO configuration |
| Dataskydd, Inställningar, Granskningslogg, Inbäddningsprofiler (read) | yes | – | profile changes |

### 5.4 The pilot department admin's journey (answered 2)

The platform team pairs with the department's admins for the first collection; the pages must work without them
afterwards. Every step is reachable from the **setup checklist** card ("Kom igång"), which is computed from state (no
stored progress flags) and disappears when the collection has a published assistant in Open WebUI.

| # | Step (Swedish UI) | Page | Defaults and guidance | Guard |
|---|---|---|---|---|
| 1 | Logga in | Översikt | empty state "Här bygger du kunskapsbanker och assistenter för din förvaltning" + **Skapa kunskapsbank** | role and department from sign-in |
| 2 | Skapa kunskapsbank | drawer | name → slug; owner team (first team of the department); **informationsklass** as three explained options, none preselected ("Vem får läsa innehållet?") | V1–V2 later |
| 3 | Lägg till källa | source drawer, type cards *Adresslista*, *Webbplats*, *Filer* | crawl: start URL; include `host/**`, default excludes, 10,000 pages, depth 10, weekly Saturday night, render `auto`; URL list nightly; template chosen by type (`website`, `url-list`, `files`), its form fields under "Avancerat" | V25 |
| 4 | Kontrollera | same drawer | preflight: reachability, robots status, sitemap found, estimated pages, egress, "samma webbplats hämtas redan av X" | fail blocks save; warn explains |
| 5 | Förhandsgranska | preview panel | up to 5 URLs (from the preflight sample or typed) or uploaded files; each step's output (text, sections, chunks with token counts) | preview isolated (engine §11) |
| 6 | Spara och hämta | source page → run page | saves, enqueues the first sync; live counters, failures by reason in plain Swedish | – |
| 7 | Testfrågor | Testfrågor och kvalitet | generated after the first runs (worker); cost shown before; the admin may add own questions with expected documents | – |
| 8 | Publicera kunskapsbanken | collection page | floor result "Träffsäkerhet 0,78 (gräns 0,70)" with interval; **Publicera** | V27 |
| 9 | Skapa assistent | assistant drawer | instructions template, collections (the new one preselected, `building` allowed), model picker showing only models servable for the level (V8), not-found texts prefilled from platform defaults, contact link to umea.se | V3, V8, V15 |
| 10 | Testa | test panel | sample questions from the evaluation set as one-click prompts; sources, coverage, raw vs delivered answer | – |
| 11 | Gör tillgänglig | Målgrupp | pick client "Open WebUI (pilot)"; residency check explained if refused ("Modellen kör utanför EU; välj en modell inom EU eller kommunens servrar") | V7–V9 |
| 12 | Öppna i Open WebUI | assistant page | link from the client's `PortalUrl` (new column, §13); the model appears under its slug | API lists it by audience |

### 5.5 Key pages (wireframe level)

#### 5.5.1 Översikt

```
Översikt                                                                    Förvaltning: Stadsbyggnad ▾
┌ Behöver åtgärd (3) ───────────────────────────────────────────────────────────────────────────────────────┐
│ ⚠ Borttagning väntar på beslut: Webbplats umea.se/bygga – 212 av 840 dokument (25 %)        [Granska]    │
│ ⚠ Kvalitetskontroll underkänd: Bearbetning "Webbplats – Bygglov" v4 (−6 punkter)            [Visa]       │
│ ● 4 dokument med personuppgifter kan inte visas i offentliga kunskapsbanker                  [Visa]       │
└───────────────────────────────────────────────────────────────────────────────────────────────────────────┘
┌ Hämtningar senaste dygnet ─────────────┐ ┌ Kö ───────────────────────┐ ┌ Assistenter, 7 dagar ─────────────┐
│ 12 lyckade · 1 med fel · 0 misslyckade │ │ 0 väntande, äldsta –      │ │ 1 240 frågor · 6 % utan svar      │
│ [Visa körningar]                        │ │ Bearbetning: 3 st, 2 min │ │ 👍 41  👎 9   [Återkoppling]       │
└────────────────────────────────────────┘ └───────────────────────────┘ └───────────────────────────────────┘
```

Attention item sources: runs `awaiting-deletion-confirmation`; failed gates without override; PII-flagged documents
referenced by `public` collections; degraded runs (last run per source); sources with ≥ 3 failed runs; scheduled
evaluation below floor or 5 points under the 4-week median (processing §14.8); feedback import failing ≥ 4 runs; org
or model sync failing; published pipeline versions without a supporting worker; P2 drift, pending approvals, MAJOR
auto-pins of own assistants.

#### 5.5.2 Kunskapsbank

```
Kunskapsbanker › Bygglov                                       [Intern] [Under uppbyggnad]   [Publicera] [⋯]
Översikt  Källor (3)  Dokument (1 204)  Testfrågor och kvalitet  Sökinställningar  Historik
┌ Kom igång ──────────────────────────────────────────┐ ┌ Läge ─────────────────────────────────────────────┐
│ ✓ Källa tillagd           ✓ Första hämtningen klar  │ │ Under uppbyggnad: syns bara i Testa.              │
│ ✓ Testfrågor skapade (86) ◐ Kvalitetskontroll pågår │ │ Senast hämtad 30 okt. 02:14 · 1 204 dokument       │
│ ○ Publicera   ○ Skapa assistent   ○ Gör tillgänglig  │ │ Används av: Bygglovsassistenten (utkast)           │
└─────────────────────────────────────────────────────┘ └───────────────────────────────────────────────────┘
Källor                                                                                       [Lägg till källa]
│ Namn                  │ Typ        │ Senaste körning        │ Dokument │ Nästa       │                      │
│ umea.se/bygga         │ Webbplats  │ ✓ 30 okt. 01:12        │ 840      │ lör 01:00   │ [Hämta nu] [⋯]       │
│ Blanketter            │ Filer      │ ⚠ 2 misslyckade        │ 41       │ –           │ [Ladda upp] [⋯]      │
```

#### 5.5.3 Källa (webbplats) and the deletion decision

```
Webbplats: umea.se/bygga                                                    [Hämta nu] [Pausa] [⋯]
Inställningar  Körningar  Genomsökningsrapport  Borttagningar (1)  Behållna (0)
┌ Startadresser ───────────────────────────────────────────────────────────────────────────────────────────┐
│ https://www.umea.se/bygga                                                                       [Ta bort] │
│ Ta med   www.umea.se/bygga/**                     Uteslut  **/sok?*  **?*print=*  [+]   [Visa webbplatsträd]│
│ Högst 10 000 sidor · djup 10 · lördagar 01:00 (natt) · JavaScript-sidor: automatiskt                       │
└───────────────────────────────────────────────────────────────────────────────────────────────────────────┘
  ⓘ Ändringen gör att 38 dokument hamnar utanför avgränsningen och tas bort direkt.   [Avbryt] [Spara ändå]

Borttagning väntar på beslut · körning 30 okt. 01:12
  212 av 840 dokument (25 %) hittades inte. Gränsen är 20 % (minst 5).
  Varför: fullständig genomsökning; 3 sidor gav 404; 209 nåddes inte (förra körningen nådde 1 049 sidor, nu 631).
  Exempel (50)  │ https://www.umea.se/bygga/…/altan │ senast sedd 23 okt. │ 200 │    [Ladda ner alla (CSV)]
  [Behåll dokumenten]  [Ta bort 212 dokument]
```

#### 5.5.4 Assistent with test panel

```
Assistenter › Bygglovsassistenten  (modellnamn: bygglov)          [Intern] [Utkast]          [Aktivera] [⋯]
Inställningar  Testa  Målgrupp  Lagring  Statistik  Historik
┌ Inställningar ───────────────────────────────┐ ┌ Testa ────────────────────────────────────────────────┐
│ Instruktioner  [textarea, mall ifylld]       │ │ Fråga [Behöver jag bygglov för en altan?     ] [Fråga]│
│ Kunskapsbanker [Bygglov ×] [+]               │ │ Förslag: · Vad kostar ett bygglov? · …               │
│ Modell  [ume/chat-eu ▾] EU · 128k            │ │ Träffar (täckning: god, 0,31 ≤ 0,50)                  │
│ När svar saknas (sv) [Jag hittar inget …]    │ │ 1 Altaner och uteplatser – umea.se/…  0,31  citerad   │
│ När svar saknas (en) [I cannot find …]       │ │ 2 Bygglov – vad gäller   – umea.se/…  0,37            │
│ Kontaktlänkar [Kontaktcenter 090-16 10 00]   │ │ Svar som användaren ser                    | Råtext   │
│ ▸ Avancerat: följdfrågor, historik, längd    │ │ Nej, om altanen är lägre än 1,8 m … [1]               │
│                                              │ │ Modell ume/chat-eu · EU · 2,1 s · 0,0012 USD          │
└──────────────────────────────────────────────┘ └───────────────────────────────────────────────────────┘
```

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

### 5.6 Empty and error states

| Situation | Text (Swedish) and action |
|---|---|
| No collection | "Här bygger du kunskapsbanker och assistenter för din förvaltning. Börja med att skapa en kunskapsbank." [Skapa kunskapsbank] |
| No source | "Kunskapsbanken har inget innehåll än. Lägg till en webbplats, en lista med adresser eller filer." [Lägg till källa] |
| No run yet | "Ingen hämtning har gjorts. Den första startar när du sparar källan." |
| Building | "Under uppbyggnad: kunskapsbanken syns bara i Testa tills den är publicerad." |
| No test questions | "Testfrågor skapas automatiskt när den första hämtningen är klar (cirka 10–20 minuter)." |
| No feedback | "Ingen återkoppling ännu. Den kommer när användare ger tummen upp eller ned i Open WebUI." |
| No role | copied no-access page in Swedish: "Din roll ger inte åtkomst till den här sidan. Kontakta plattformsteamet om du behöver den." |
| Load failure | copied `AsyncState` with "Försök igen" (`components/ui/AsyncState.vue:14`) |

### 5.7 Swedish wording conventions

- Klarspråk (språklagen 2009:600, 11 §): du-tilltal, short sentences, verbs on buttons ("Publicera", "Hämta nu",
  "Behåll dokumenten"), sentence case, no English UI words; technical terms that must stay English (`recall@10`, `OCR`,
  `robots.txt`) are explained once in help text and marked `lang="en"` where a screen reader would mispronounce them.
- Every error says what happened and what to do; destructive confirmations name the object, the count and the
  consequence, and the button repeats the action ("Ta bort 212 dokument").
- Numbers `sv-SE` (space thousands separator, decimal comma), dates "30 okt. 2026", times 24 h, Europe/Stockholm;
  money stays USD as in the gateway (`…Usd`), shown "0,0012 USD".
- One glossary, enforced in review (`utils/labels.ts` is the single source of these words):

| Concept (code) | UI term | Concept (code) | UI term |
|---|---|---|---|
| collection | kunskapsbank | evaluation set / question | testfrågor / testfråga |
| source: `url-list`, `crawl`, `upload`, `sharepoint` | källa: adresslista, webbplats, filer, SharePoint | gate | kvalitetskontroll |
| document / chunk | dokument / textavsnitt | recall@10 | träffsäkerhet (recall@10) |
| pipeline / template / node | bearbetning / mall / steg | coverage threshold | täckningsgräns |
| draft / published / gating | utkast / publicerad / kontrolleras | `building` | under uppbyggnad |
| run / sync now | körning / hämta nu | deletion guard / held | borttagningsspärr / behållna |
| preview / preflight | förhandsgranska / kontrollera | PII / clear | personuppgifter / godkänn för offentlig |
| assistant / audience | assistent / målgrupp | skill / tool server / grant | färdighet / verktygsserver / behörighet |
| feedback / comment / reason | återkoppling / kommentar / skäl | conversation / reviewer | samtal / granskare |
| `public` / `internal` / `sensitive` | offentlig / intern / känslig | `OnPrem` / `Eu` / `External` | kommunens servrar / EU / utanför EU |
| department / team | förvaltning / team | audit log / history | granskningslogg / historik |

## 6. Pipelines

### 6.1 P1 template form (department admins)

- A source has a template and form values. The form lists exactly the template's `form` entries (engine §3.1:
  `field`, `node`, `param`, `label.sv`); the control comes from the node's `paramsSchema` property (§6.3).
- Saving stores a **department copy** that differs from the template only in those params (engine §13.2). Copies are
  reused: the admin looks up a department pipeline with `CopiedFromId` = the template version and an equal
  `PipelineHash`; sources with the same choices share one copy (and therefore chunk sets, R2).
- On a **new** source the copy is published at once (no documents: plan empty, gate `not-measurable`). On an existing
  source a changed form value creates a new draft of its copy and opens the publish dialog (§6.6); the source page
  explains "Ändringen gör att 840 dokument delas upp på nytt (cirka 25 minuter, 0,00 USD)".

### 6.2 P1 node editor (pipeline editors)

Pipeline page for `EditPipelines`: the version's nodes as an ordered list (topological order, grouped by branch from
each *Switch*), each with its generated parameter form, lock icon for locked nodes/params, the validator's messages
next to the node (`nodeId`/`port`/`code`), "Lägg till steg" from the manifest palette (category, display name, ports),
"Koppla" with selects for output port → input port (only type-compatible ports offered, engine §4 rule 4), and a
read/write **JSON view** of the definition (engine §13.2: "JSON editor (P1)"). Save validates rules 1–9; publish
1–12. This list editor stays in P2 as the keyboard and screen-reader equivalent of the canvas (§6.8).

### 6.3 Forms generated from the manifest

`FormModel` (in `Ume.Rag.Pipelines`, so the server validates exactly what the UI offers) turns a JSON Schema
2020-12 `paramsSchema` into a form description; the UI renders it with the copied `components/ui` fields. No JSON
Schema form library and no browser-side validator: the UI checks `required`, ranges and patterns from the form
description and the server validates fully (engine §4 rule 3).

| Schema | Control |
|---|---|
| `boolean` | `UiSwitch` |
| `enum` (≤ 5) / (> 5) | `SegmentedControl` / `SelectField`, labels from `x-labels.sv` (new manifest annotation) |
| `integer`/`number` with `minimum`/`maximum` | `TextField` numeric with unit from `x-unit` |
| `string` (+ `pattern`, `maxLength`) | `TextField` |
| `array` of strings | `AddressListField`-style list editor (gateway component) |
| `object` with known properties | `FieldGroup` |
| `array` of objects (e.g. `metadata-from-path` rules, `switch` cases) | repeating `FieldGroup` rows with add/remove/move buttons (no drag) |
| `if`/`then`, `oneOf` | shown as dependent fields; anything else → the JSON view for that param, with a note |

Labels and help come from `x-labels`/`x-help` annotations (Swedish) in the manifest; a missing label falls back to the
param name and fails a CI check of the manifest (`Ume.Rag.Pipelines.Tests`).

### 6.4 Preview

Preview runs on the saved draft (autosave every change in the node editor) or the template form's unsaved values
(sent as a transient definition; the job never needs a saved version for a new source). The result view: documents as
tabs, each with a step strip (state, duration, cache hit, active output port, cost) and the selected step's output
(parsed Markdown with page markers, sections, chunks with heading path and token count, facts, PII counts). Changing one
step re-runs from it (`basePreviewJobId`, engine §11). LLM steps (P2) above `PreviewMaxUsd` ask for confirmation.

### 6.5 Diff

Between two versions (draft vs published, or any two): nodes added/removed/changed with parameter-level differences
(`chunk.maxTokens: 400 → 300`), edges added/removed, locks changed, and the first changed node per document group from
the publish plan. Textual, grouped, keyboard-navigable; no graphical diff (the canvas highlights changed nodes in P2).

### 6.6 Publish flow

```
draft ──Publicera…──▶ gating ──plan + gate done──▶ (dialog: plan, cost, quality check) ──Bekräfta──▶ published
  ▲                     │                                     │                                    previous → superseded
  └──── Avbryt ─────────┘◀──────── gate fail (no override) ───┘                                    reprocess runs
```

1. `POST /publications`: validate rules 1–12 (V24); version `State = gating` (new value, mirrors `SkillVersions`); the
   draft is read-only; enqueue `publish-plan` and the gate (§4.7); store the job ids on the version (`PlanJobId`, new;
   `GateRunId` exists).
2. Dialog sections, filled as results arrive: **Plan** ("1 240 dokument: 1 180 från steget Dela upp (ingen ny
   tolkning, ~25 min), 60 från Tolka (~900 sidor, ~1 h); uppskattad kostnad 0,00 USD"); **Kvalitetskontroll** (§7.2);
   **Påverkade kunskapsbanker och assistenter**.
3. **Bekräfta** (only when the gate passed, passed-with-warning, was `not-measurable`, or was overridden): the config
   transaction of engine §12.1 step 5 checks that the version's stored hashes equal the hashes the gate measured
   (`EvaluationRuns.SubjectVersion`), sets `published`/`superseded`, `Pipelines.PublishedVersionId`, audits
   `pipeline.publish`, and enqueues one `reprocess` sync per source in the same transaction.
4. **Avbryt** or a gate failure returns the version to `draft` (shadow sets expire by themselves, data-model §2.2).
5. Template publish (platform admins) follows the same flow with the gate on the 10 largest affected collections
   (processing §14.1); copies are never changed (engine §13.3).

### 6.7 Rollback and template updates

- **Återställ** on any earlier version: confirmation names the version and the re-processing ("inom 14 dagar: bara
  medlemskap ändras, ingen ny tolkning"), never blocked by a gate; the gate runs afterwards and is shown (engine §12.3).
- A copy whose template has a newer version shows "Mallen har uppdaterats" with the diff and **Uppdatera från mallen**
  (rebase into a new draft). A raised locked-node `minVersion` makes publishing the copy fail with
  `locked_node_below_min` until rebased; platform admins may bulk-create the rebased drafts.

### 6.8 P2 canvas (Vue Flow)

Checked against the package tarballs (`@vue-flow/core` 1.48.2, `package/dist/vue-flow-core.mjs`, `package.json`):

| Fact | Consequence |
|---|---|
| Dependencies: `@vueuse/core ^10.5.0`, `d3-drag`, `d3-interpolate`, `d3-selection`, `d3-zoom` (`^3`); peer `vue ^3.3.0`; MIT | repo-and-deployment §2.3 said "d3 zoom/drag … nothing else": add `@vueuse/core` (→ `@vueuse/shared`, `@vueuse/metadata`, `@types/web-bluetooth`, `vue-demi`) to the justification (§13); `vue-demi`'s default build is Vue 3, so `ignore-scripts` is safe |
| Keyboard: nodes and edges focusable (`nodesFocusable`, `edgesFocusable`), Enter/Space select, arrow keys move, Delete removes, `disableKeyboardA11y` switch; `aria-describedby` and an `aria-live` region (`:3478-3480`, `:8729-8746`, `:9700`) | usable, but the description and live texts are **hard-coded English** ("Press enter or space to select a node…", "Moved selected node …") with no option to replace them found in 1.48.2, on a `lang="sv"` page. We keep Vue Flow's keyboard support, give every node and edge a Swedish `ariaLabel` (supported, `:8936`, `:9590`), announce our own actions (add, connect, remove) in our own Swedish live region, and treat the English descriptions as a known gap (§15); the list editor (§6.2) remains the accessible way to edit (WCAG 2.1.1, 2.5.7) |
| `isValidConnection` and `connectOnClick` options (`:4571`, `:5192-5216`) | port typing from the manifest enforced while connecting; connections by click-click instead of drag (2.5.7) |
| `dist/style.css` 3.9 KB, `theme-default.css` 3.5 KB; ESM bundle 345 KB unminified | imported into the canvas route chunk (CSP-safe); route lazy-loaded; budget §10.2 |

Design: palette from the manifest (category groups, search), nodes show display name, key params, lock, validator
state; edges coloured by port type with a text label (never colour alone); selection opens the same generated form in a
side panel; positions saved to `PipelineCanvasLayouts` (never in the definition; not audited); no minimap package.
`@vue-flow/background` and `@vue-flow/controls` (zoom buttons with labels) only. The canvas and the list editor edit
the same draft; switching is lossless.

## 7. Evaluation and gate pages

### 7.1 Testfrågor och kvalitet (per collection)

- Set: size per layer (`generated`, `human`, P2 `skill`/`feedback`), stale and retired counts, last generation (model,
  prompt version, cost), **Skapa fler** with the forecast (≈ 200,000 on-prem tokens for a full set, processing §13.1).
- Questions table (keyset): question, layer, expected documents (titles), last rank, state; editors add/edit/retire
  human questions (expected documents picked by title search in the collection); generated questions can be edited
  (becomes human) or retired.
- Results: latest runs with decision badges; weekly trend of recall@10 and MRR@10 (`TimeSeriesChart`, table view) with
  the floor line; per-question drill-down (rank per run).
- Coverage threshold proposal: histogram of `best(C)` over the set's questions, proposed value p95 + 0.03, current
  value, **Använd förslaget** (not gated, V16).
- Retrieval settings form: gated values with **Kontrollera** (runs the `retrieval-change` gate) and **Spara** enabled
  only after a pass/override for exactly these values; ungated values saved directly (knowledge §12.1).

### 7.2 Gate result card (pipeline, collection, retrieval, skill)

```
Kvalitetskontroll · Bearbetning "Webbplats – Bygglov" v5 mot v4            Underkänd   30 okt. 11:02
  Träffsäkerhet (recall@10)  0,74 → 0,68   (−6 punkter, gräns −3)    MRR 0,61 → 0,55
  Frågor: 50 i urvalet, utökat till 100 · 9 sämre, 3 bättre (netto −6) · McNemar p = 0,07
  Intervall för skillnaden: −11 till −1 punkter · Delkorpus 300 dokument (optimistiska absolutvärden)
  ▸ Frågor som blev sämre (9)   ▸ Frågor som blev bättre (3)   ▸ Så fungerar kontrollen
  Plattformsadministratörer: [Godkänn ändå…]  (skäl, minst 20 tecken; loggas)
```

States and texts: `pass` "Godkänd", `pass-noise` "Godkänd (inom brus efter utökning)", `pass-not-significant`
"Godkänd med varning", `fail` "Underkänd", `not-measurable` "Kan inte mätas (för få frågor), blockerar inte"; the
floor card shows the absolute value against `GateFloor` with the Wilson interval and "för få frågor" below 20.

### 7.3 Overrides

Only `Admin`; per run; reason ≥ 20 characters; stored on the run (`OverriddenBy`, `OverrideReason`, `OverriddenAt`),
audited `gate.override`, shown on the card for everyone ("Godkänd av plattformsteamet trots underkänd kontroll: skäl …").
Department users see "Kontakta plattformsteamet om du anser att resultatet är fel" instead of the button.

## 8. Feedback, conversation review and access audit

### 8.1 Återkoppling (P1)

- Rows from `chat."Feedback"` of the caller's assistants (tier fan-out, keyset by `CreatedAt`), joined in memory with
  message metadata (`Outcome`, `Coverage`, `Language`), cited document titles (`MessageSources.DocumentId` →
  `Documents.Title`), assistant and client names. **No content**: no question, answer, title or comment.
- **Reason category** in plaintext: new column `Feedback.ReasonCategory` filled by the importer and `/v1/feedback` only
  from the fixed Open WebUI keys (`RateComment.svelte:17-35`: `accurate_information`, …, `not_factually_correct`,
  `other`), else `other`; the free text stays in the ciphertext (§13). Swedish labels in `labels.ts`.
- `ReviewState` (`open`/`reviewed`/`dismissed`) set by `FeedbackComments` holders ("Markera som hanterad"; needs the new
  column grant). Filters: period, assistant, rating, outcome, has comment, state. Totals from `AssistantDailyTotals`.
- Import health per Open WebUI client from `FeedbackImportState` (last run, outcome, unmapped) (chat §14.2–§14.3).

### 8.2 Comment reveal (`FeedbackComments`, P1)

1. "Läs kommentar" → dialog with the fixed reason "Granskning av återkoppling" (one click, chat answered 7).
2. Server: feedback in scope; level `public`/`internal` (`sensitive` → 403 `review_flow_required`); write
   `ConversationAccess` events `requested` and `decrypted` (`Scope = comment`, `FeedbackId`, `AssistantId`,
   `DepartmentId`, reason) and event **6004** — before decrypting.
3. Unwrap the row's `DataKeyId` with the level's shred-bucket key, decrypt `CommentCiphertext`, return `{ reason,
   comment }` only; keys stay ≤ 5 minutes in memory (data-model §7); response `no-store`; the UI drops it when the dialog
   closes. One comment per request; no bulk reveal.

### 8.3 Conversation review (`Review`)

- P1: from a feedback row, **Läs samtalet** (reviewer only). P2: also by assistant and period (list shows metadata only).
- Request: free-text reason ≥ 20 characters (V26); `public`/`internal` → events `requested` and `decrypted` (no
  `approved` event below `sensitive`, data-model §2.4); access window **30 minutes** per request id; each fetch within
  the window checks scope again; one `decrypted` event per request id, 6004 per fetch.
- View: messages in order with times, assistant, outcome, coverage; cited snippets (`SnippetCiphertext`) under each
  answer; feedback; P2 tool calls (arguments/results). Read-only, no export, no copy button; a banner names the reviewer
  and the logged reason.
- A conversation in history mode `none` shows metadata only; a shredded one "Samtalet har gallrats".

### 8.4 `sensitive` (P2; no `sensitive` data while file keys are used, platform §8a)

Request (reason) → a second `rag-reviewer` with the department in scope approves (`approved`, approver ≠ requester,
refused otherwise) within 24 h → the requester decrypts within 30 minutes. Comments of `sensitive` conversations only
through this flow (chat §14.4). The pending queue is a tab of Samtalsgranskning.

### 8.5 Åtkomstlogg (access audit)

`audit."ConversationAccess"` as a list: time, reviewer, scope (comment/conversation), assistant, level, reason,
events (requested → approved → decrypted). Visible to platform admins (all), department admins (their departments) and
reviewers (their own). Reading the list emits 6007. Export CSV (`;`, as `ReportEndpoints.cs:152-158`).

## 9. Audit log and security-relevant views

### 9.1 Granskningslogg

- Platform admins: every `AuditLog` entry (filters: period, actor, action, entity type, entity id, department,
  service incl. `system:retention` and worker/tools entries), keyset; detail shows before/after as a field-level diff of
  the snapshot JSON. The gateway restricts its audit list to gateway admins (`ReportEndpoints.cs:160-169`); ours also
  gives department admins their departments' entries through the new `AuditLog.DepartmentId` (§13).
- **Historik** tab on every entity page (collection, source, pipeline, assistant, client, key, skill, tool server):
  the entity's entries, same scope rules.
- Reading the audit log emits 6007 (identity §10).

### 9.2 Audit actions

`EntityType` + `Action` columns as the gateway (`AuditSnapshots.cs:11-51`); the dotted names used in steps 1–9 are
`entity.action` shorthand (e.g. `gate.override` = `Gate`/`override`). Admin entries: `collection.create|update|delete|
publish|retrieval_settings`, `source.create|update|delete|pause|resume|sync`, `upload.batch|delete|restore`,
`deletion.confirm|reject`, `hold.release`, `pii.view|clear`, `pipeline.copy|draft|publish|cancel|rollback|rebase`,
`template.*`, `evaluation.generate|question`, `gate.override`, `assistant.create|update|delete|activate|deactivate|
audience|retention|limits|risk_accept`, `client.*`, `key.create|update|rotate|reveal|copy|revoke`, `gatewaylink.*`,
`gatewaymodel.update`, `settings.update`, `dataprotection.update`, `organisation.sync`, `feedback.review_state`, plus
tools design §9.2 and the conversation access events of §8 (in `ConversationAccess`, not `AuditLog`).

### 9.3 Security-relevant views

| View | Contents | Who |
|---|---|---|
| Nycklar | status, last use (`KeyLastUse`), grace, unexpected-use warnings (5008 is SIEM; the page shows keys used after rotation from `KeyLastUse`) | Manage (own teams), Admin |
| Inloggningsfel | `audit."AuthFailures"` aggregated by credential kind and client (never the presented value) | Admin |
| Personuppgifter | flagged documents in scope, types and counts (no values), public references, highlight preview, clearance with reason | Manage |
| Karantän | quarantined files: name (own sources), type, signature name, date; no download (connectors answered 3) | Read |
| Verktyg (P2) | drift observations, pending approvals, grants, definition diff | Admin, system owners |
| Åtkomstlogg | §8.5 | §8.5 |

Security events themselves are not stored (ADR 015 pattern): the SIEM is where they are read.

## 10. Accessibility, performance, observability

### 10.1 Accessibility

- **Target**: WCAG 2.2 AA, the gateway's design target (`docs/accessibility-statement.md:9`), which contains the legal
  baseline (DOS-lagen 2018:1937 → EN 301 549 → WCAG 2.1 AA). An internal admin app is a digital public service under
  DOS-lagen, so it gets a tillgänglighetsredogörelse in the gateway's format.
- **Copied patterns**: skip link (`AppShell.vue:91-93`), focus to the page heading after navigation and to sections
  (`ui/router/index.ts:46-74`), first invalid field focused (`useFormErrors.ts`), `role="status"` loading and
  `aria-live="polite"` toasts (`AsyncState.vue:14`, `ToastRegion.vue:16`), session expiry dialog with explicit
  extension (`AppShell.vue:249`), three themes, 24 px targets, charts with a table view.
- **New patterns**: job progress announced in one polite live region per page (state changes only, not every poll);
  publish dialog sections are headings; gate results as text first, chart second; diff as lists; drag never required
  (rows move with buttons, canvas connects by click, files by a native file input — drop zone optional); test panel
  announces "Svaret är klart" once; status never by colour alone (icon + text); long tables with captions and sortable
  headers (`aria-sort`).
- **Verification**: copied axe spec (WCAG A/AA tags, `tests/e2e/specs/accessibility.spec.ts:6-19`) over every routed
  page incl. the new ones and every drawer/dialog in three themes; target size (`helpers.ts:160-172`); reflow at 320 px
  and 200 % text; **keyboard-only run of the pilot journey** (§11.4) without a mouse event; Vitest component axe
  checks; Vue Flow page also with the list editor. Screen reader: the gateway decided not to test with one
  (`accessibility-statement.md:31`); for non-specialist pilot users one NVDA + Firefox pass of the pilot journey runs
  before the pilot (answered 4).

### 10.2 Performance budgets

| Item | Budget | How measured |
|---|---|---|
| Initial JS (shell + Översikt), gzip | ≤ 200 KB | CI build size report (`vite build` + gzip sizes); fails above budget (gateway only warns at 900 KB raw, `vite.config.ts:50`) |
| Each lazy route chunk, gzip | ≤ 100 KB; canvas route ≤ 180 KB (Vue Flow + d3 + vueuse, *estimate to verify*, §15) | same |
| API p95 (server, pilot data) | detail ≤ 200 ms, lists ≤ 400 ms, writes ≤ 500 ms, job enqueue ≤ 200 ms, overview ≤ 500 ms | `http.server.request.duration` by route; integration perf test with 50 sources, 100k run items, 10k documents |
| Test panel | retrieval as knowledge §11; answer as chat §16 plus ≤ 100 ms admin overhead | `rag.admin.test_panel.duration` |
| Preview | first document's first steps ≤ 30 s for HTML (worker-bound), deadline 2 min (engine §11) | E2E timing, `rag.jobs.age{class=preview}` |
| Interaction | visible response ≤ 100 ms (spinner/optimistic state) | manual + Playwright traces off (artifacts disabled as the gateway, `playwright.config.ts:17-20`) |

No new runtime dependency besides Vue Flow (P2): no Markdown renderer (skill bodies are edited and shown as monospace
text; the test panel shows answers as text), no chart library (gateway SVG charts), no JSON-schema form library (§6.3),
no i18n library (answered 1), no date library (`Intl`).

### 10.3 Observability

- Copied: `AdminActivityMiddleware` (6006 throttled), 6001–6007, log scopes incl. `ConfigVersion`, `RequestId`; no
  content in telemetry (questions in the test panel, comments, conversation text, URLs of `sensitive` sources never
  logged).
- Metrics (meter `Ume.Rag`, low cardinality): `rag.admin.validation.refusals{code}`, `rag.admin.jobs.enqueued{class,
  kind}`, `rag.admin.sync{kind,outcome}`, `rag.admin.test_panel{outcome}` + `.duration`, `rag.admin.conversation_access
  {scope,level}`, `rag.admin.uploads{outcome}` + `.bytes`, `rag.admin.publish{kind,decision}`.
- Dashboard *Config & security* gains admin panels (refusals by code, access events, sync health).

## 11. Tests

### 11.1 Unit (`Ume.Rag.AdminApi.Tests`, `Ume.Rag.Domain.Tests`, `Ume.Rag.Pipelines.Tests`)

- `ConfigRules`: one table-driven test per V-rule (pass, fail, boundary; the re-check triggers of §4.6 column 4).
- `PermissionMatrix` invariants: `Admin` never implies `Review`/`FeedbackComments`; every endpoint has a policy.
- `FormModel`: every P1 manifest entry renders; unsupported constructs fall back to JSON; Swedish labels present.
- Audit snapshots never contain secret columns (reflection over `*Audit` records vs a deny list: `EncryptedCredential`,
  `EncryptedSecret`, `KeyHash`, `EncryptedKey`).
- Error-code coverage: every `ValidationCode` has an English message and is in `contracts/admin/v1/error-codes.json`.

### 11.2 API integration (WebApplicationFactory; Testcontainers: our Postgres image, Redis, Keycloak; FakeGateway)

- **Role matrix**: generated from `PermissionMatrix`: every endpoint × every Keycloak test user (identity §11 plus new
  `rag-reviewer`) → expected 200/403/404; ids of another department → 404 (no leak).
- **Write path**: change + outbox row + version bump + audit row with that `ConfigVersion` + job row in one
  transaction (kill the connection before commit → none of them); concurrent edits → 409 `concurrent_change`;
  23514 → 400 `platform_rule`.
- **Jobs**: dedup under parallel "Hämta nu"; payload ≤ 4 KB; `pg_notify` on commit only.
- **Publish**: `gating` lock; confirm refused when hashes differ; cancel returns to draft; rollback never blocked.
- **Feedback and review**: comment reveal writes `ConversationAccess` before decrypting (fault injection after the
  insert → no plaintext returned, events present); department admin can reveal comments but gets 403 on conversations;
  platform admin without `rag-reviewer` gets 403 on both; `sensitive` → `review_flow_required`; access window expiry.
- **Sync**: org and model sync write nothing when unchanged; alias residency rule; failure keeps the mirror.
- **Tier matrix**: the suite runs with a second database as the `sensitive` tier (data-model §6.5).

### 11.3 Worker in the loop (`Ume.Rag.IntegrationTests`)

Admin-enqueued preview, publish plan, gate (FakeGateway deterministic embeddings and judge), deletion confirm, PII
clear and upload commit consumed by a real worker; the admin reads the results through its own endpoints.

### 11.4 Browser (Playwright TypeScript, gateway `tests/e2e` setup; nightly and release)

- **Pilot journey** (`pilot.spec.ts`, department admin of department 1000): §5.4 steps 1–12 against a static fixture
  site (nginx container with 30 Swedish pages and 3 PDFs), FakeGateway, real worker and parser; ends by calling
  `/v1/models` with the Open WebUI client's key and finding the assistant. Run twice: mouse, and **keyboard only**.
- Role journeys: viewer read-only, pipeline editor (copy, node form, JSON view, publish with a failing gate fixture),
  platform admin override, reviewer (comment reveal, conversation view of a seeded conversation, access log), no-role
  user sees the no-access page, deletion confirmation and rejection, upload with a refused file type.
- Accessibility spec copied and extended (§10.1); CSP violations collected with `page.on('console')` and
  `securitypolicyviolation` listeners must be zero.

### 11.5 Unit UI (Vitest)

Stores, `messages.ts` mapping, form generation, checklist computation, polling back-off, component axe checks.

## 12. Milestones

| Phase | Deliverable |
|---|---|
| P0 | BFF + roles (incl. `rag-reviewer` in the realm and Bicep), design system and shell in Swedish, `labels.ts`/`messages.ts` skeleton, `AdminContext` write path (audit + outbox + version), `PermissionMatrix` with generated role tests, org sync, error-code export |
| P1 a | collections, sources (URL list, crawl, upload batches), preflight, hierarchy, impact, runs and crawl report, deletion decisions, documents, PII view/clear, template forms, node editor + JSON view, preview, diff, publish (plan → gate → confirm), rollback |
| P1 b | evaluation page, gate cards, floor + collection publish, retrieval settings with gate, coverage proposal, assistants (RAG), test panel (`Ume.Rag.Answering`), audiences (clients), retention, `GatewayModels` sync, clients, keys, gateway page, settings, data protection, audit log and history |
| P1 c | feedback page, comment reveal, conversation review behind feedback (`public`/`internal`), access log, import health; accessibility pass and tillgänglighetsredogörelse; pilot journey E2E green |
| P2 | canvas; parse profiles; SharePoint; skills; tool servers (registration, approvals, grants, drift); agent assistants (limits, risk acceptance, tool test panel); conversation search and `sensitive` two-person flow; feedback → test question; widget keys |
| P3 | query-side canvas, analytics, OBO configuration, skill import/export, embedding profile changes |

## 13. Changes to other documents

| Document | Change |
|---|---|
| `01-general-idea.md` §10 | rows "Admin (step 10 questions)" (answers 1–3 of §14.1) and "Admin (step 10)" summarising §1 (BFF host, `Ume.Rag.Answering` for the test panel, Swedish via code table, `rag-reviewer`, `version` concurrency, one-transaction write path, V1–V31, `gating`, `GatewayModels` in P1, Vue Flow 1.48.2) |
| `admin/plan.md` §2–§6 | Swedish-only UI (answered 4 new); roles table adds `rag-reviewer` and the policy split (Review, FeedbackComments, not implied by Admin); Conversations page: P1 limited to conversations behind feedback; Feedback page comment rule (reviewer + department admin); `GatewayModels` sync in P1; milestones of §12; answered 4–8 = §14.1 answers 1–5 |
| `identity/design.md` §1.3, §1.4 | role `rag-reviewer` → `Review`, `FeedbackComments`, `Read`; `rag-department-admin` adds `FeedbackComments`; `rag-platform-admin` has neither; env `UME_RAG_OIDC_GROUPS_REVIEWER` |
| `identity/design.md` §4.8 | test panel runs `Ume.Rag.Answering`; P2 agent test calls go to the hub as `admin-console` (client `ToolPolicy = read`) |
| `identity/design.md` §10 | 6004 attributes `RequestId`, `Event`, `Scope` (`comment`/`conversation`), `ConversationId`, `Classification`, `AssistantId`; 6007 also for feedback lists, access log and conversation views |
| `identity/design.md` §11, §12 | realm role and user `rag-reviewer` (1000); Bicep app role `rag-reviewer` |
| `platform/data-model.md` §1.2 | admin: **U on `chat."Feedback"` column `ReviewState`** |
| `platform/data-model.md` §2.1 | `GatewayModels` **P1** with `Kind` (`model`/`route`), `Residencies varchar(10)[]` (replaces single `ProviderResidency`), `Source` (`catalog`/`manual`); `PipelineVersions.State` adds `gating`, new `PlanJobId uuid NULL`; `UpdatedAt`, `UpdatedBy` on every editable aggregate root (`Collections`, `Sources`, `Pipelines`, `Assistants`, `Clients`, `Skills`, `ToolServers`, `EmbeddingProfiles`, `Settings`, `DataProtectionPolicies`), `xmin` as concurrency token; `Clients.PortalUrl varchar(500) NULL`; note "assistants link collections of their own department only" |
| `platform/data-model.md` §2.3 `Feedback` | `ReasonCategory varchar(40) NULL` (fixed Open WebUI keys or `other`, plaintext) |
| `platform/data-model.md` §2.4 | `AuditLog.DepartmentId uuid NULL` (+ IX); `ConversationAccess` adds `Scope varchar(20)`, `FeedbackId uuid NULL`, `AssistantId uuid`, `DepartmentId uuid`, `ExpiresAt timestamptz` |
| `ingestion-worker/design-engine.md` §9.1, §12.1 | `command` kind `release-hold`; preflight payload samples ≤ 20 URLs; publish step 1 sets `gating` and step 5 checks the gate's hashes; cancel returns to `draft` |
| `ingestion-worker/design-engine.md` §3.2 | manifest `paramsSchema` annotations `x-labels`, `x-help`, `x-unit` (Swedish) for generated forms |
| `ingestion-worker/design-connectors.md` §9.1 | uploads as batch create → one request per file → commit (manifest and `sync` job at commit; uncommitted batches swept after 24 h) |
| `chat-api/design.md` §2.1, §14.1–§14.4 | chat answer core moves to `Ume.Rag.Answering`; importer and `/v1/feedback` fill `ReasonCategory`; comments readable by `rag-reviewer` and `rag-department-admin`, conversations by `rag-reviewer` only, platform admins not implicitly |
| `knowledge-api/design.md` §2.1 | `KnowledgeService` core in `Ume.Rag.Answering` (the role keeps HTTP); the `building` rule unchanged (only the admin host passes them) |
| `platform/plan.md` §8a | review row: reason by `rag-reviewer`; comments of `public`/`internal` also by department admins after a one-click reason |
| `tools/design.md` §2.2, §12 | `rag-admin` → `rag-tools` also carries test-panel tool calls (P2); `GatewayModels` sync is P1 (admin) |
| `platform/repo-and-deployment.md` §1, §3 | new `src/Ume.Rag.Answering` (referenced by Api and AdminApi); `contracts/admin/v1/error-codes.json`; settings `Admin:GatewayAdminUrl`, `OrgSync:*` |
| `platform/repo-and-deployment.md` §2.3 | Vue Flow **checked**: `@vue-flow/core` 1.48.2, `@vue-flow/background` 1.3.2, `@vue-flow/controls` 1.1.3 (MIT); transitive `@vueuse/core` 10.11.1 (+ shared, metadata, `@types/web-bluetooth`, `vue-demi` 0.14.10) and `d3-drag`, `d3-interpolate`, `d3-selection`, `d3-zoom`; the gateway's texts are English, so `labels.ts`, `problem.ts`, `format.ts`, `index.html` are adapted, not copied; gzip size budget step |
| `platform/repo-and-deployment.md` §6.1, §8.2, §9 | proxy `client_max_body_size` 60 MB on the admin upload route; e2e row = pilot journey (mouse and keyboard) + extended accessibility spec + static fixture site container; admin metrics of §10.3 |
| `gateway-changes/design.md` §7.3 | optional item: `GET /v1/catalog/routes` (name, kind, target model ids) next to `features`/`contextWindow`, so route aliases need no manual `GatewayModels` rows |
| `postponed-decisions.md` V1 | Vue Flow struck from the to-verify list (checked 2026-10-30) |

## 14. Questions

### 14.1 Answered

User, 2026-10-30 (this step):

1. **UI language**: **Swedish**, with the gateway's mechanism (texts in components and a labels file, no i18n); API,
   logs and docs in English (§1 row 3, §4.2, §5.7). Confirmed again after the correction that the gateway's own UI
   texts are English (sources above): the RAG admin deliberately diverges for its non-specialist users.
2. **Pilot setup**: the pilot department's own admins use the P1 pages (forms and templates) themselves, with the
   platform team pairing at first; P1 pages are built for non-specialists (§5.4, §6.1).
3. **Conversation review**: new role `rag-reviewer`, granted per department to named reviewers, reads other users'
   conversations with a reason, audited; `sensitive` also needs a second approver; department admins get no
   conversation access by default. Reconciled with chat answered 7: feedback **comments** (`public`/`internal`, one-click
   reason) may be read by `rag-reviewer` and `rag-department-admin`; **full conversations only by `rag-reviewer`**;
   platform admins get neither implicitly (§3.1, §8).

Earlier answers this design builds on: admin plan answered 1–3 (2026-10-29: separate app, same design system and
sign-in, cross-linked; `rag-editor` from P2; tool servers registered by platform admins with two approvals); engine
(P1 template forms, P2 canvas); processing answered 19 (gate overrides only by platform admins with a reason) and 21
(collection floor); chat answered 7 (one-click comment reason); tools answered 2 (skill editors behind the gate).

Decisions taken here that the user may want to revisit: platform admins without implicit content access (§3.1);
assistants link only their own department's collections (§3.2); no e-mail notifications in P1/P2 (§5.2); the test
panel shows answers as plain text (§4.9).

4. **Screen-reader check**: yes, one NVDA + Firefox pass of the pilot journey (§5.4) before the pilot (≈ half a day);
   findings fixed or listed in the tillgänglighetsredogörelse (§10.1).
5. **Conversation review in P1**: limited version in P1 — a `rag-reviewer` opens the conversation behind a feedback item
   (`public`/`internal`); general search and the `sensitive` two-person flow in P2 (§8.3–§8.4).

### 14.2 Open (user)

None.

## 15. To verify

- **Vue Flow 1.48.2** in a browser: no `<style>` element injected at runtime and no CSP violation with our policy;
  whether the English ARIA description texts (`vue-flow-core.mjs:8729-8746`) can be replaced (props or slots in a later
  version, else an upstream PR); gzip size of the canvas chunk (budget 180 KB); `connectOnClick` with keyboard
  activation; the package version again at P2 start (7-day npm rule).
- **Npgsql EF Core 10.0.3**: mapping `xmin` as a `uint` row version (`IsRowVersion`) on every aggregate root, and its
  behaviour in `ExecuteUpdate` paths.
- **Audit `ConfigVersion`**: reading `config."ConfigVersion"` after `SaveChanges` inside the same transaction returns the
  bumped value (the trigger's row lock is ours) — integration test.
- **Gateway**: whether release C's `memberGroups`/`code` fields and the proposed `/v1/catalog/routes` land; until then
  manual alias rows. Residency semantics of a route alias under `x-ume-max-residency` (any target within the ceiling
  serves) against `RouteSelector.EffectiveResidencies` after release B.
- **Open WebUI v0.12.5**: the feedback `data.reason` values are always one of the fixed keys (the UI's list) and never
  free text from the API; behaviour for older imported feedback.
- **Kestrel/proxy**: per-endpoint 60 MB body limit on the upload route while the rest keeps 1 MB; nginx streaming
  (`proxy_request_buffering off`) for that route.
- **DOS-lagen scope** for an intranet admin application and the required content of its tillgänglighetsredogörelse
  (municipal web/accessibility coordinator).
- **Budgets**: API p95 numbers and the overview query at 100k run items in the R1 test.
