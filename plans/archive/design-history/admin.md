# admin: history moved out of the design

The process sections of [`../../admin/design.md`](../../admin/design.md) (summary of decisions, milestones, changes to other documents, questions and their answers, deferred and removed items), moved here unchanged on 2026-11-13 (M-14) under their original numbers, so the citations of them still resolve. History: it does not bind; `plans/decisions.md` and the design do.

## 1. Summary of decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Placement | `Ume.Rag.AdminApi` (`rag-admin`; `rag-admin-b` with the a/b slots of P2) is the BFF, the only writer of `config`, the job producer for the worker, the reader of `content`/`chat`/`audit` and the host of the built UI (`src/admin-ui/dist` as `wwwroot`); copied gateway BFF, CSP, rate limit, validation filter and problem details (§2) |
| 2 | Answer code in the admin | Library **`Ume.Rag.Answering`** holds the knowledge-role core and the chat answer core, so the **test panel** runs the exact chat path in process, including collections that are not yet published, without an API route that could include them; `rag-admin` and `rag-api` are deployed from the **same release**, so the panel always runs the code chat runs (§2.1, §4.9) |
| 3 | Language | **English** UI (Decisions, "Admin UI in English", 2026-11-02): `lang="en"`, every string in `ui/utils/labels.ts` and `ui/utils/messages.ts`, so Swedish can be added later as a translation; no i18n library until then. API, logs, OpenAPI and docs in English. Server errors carry a stable **`code`**; the UI renders its texts from a code table (§4.2) |
| 4 | Roles | **`rag-reviewer`** (answered 3): policies `Review`, `FeedbackComments`, `Read`. Feedback **comments**: `rag-reviewer` and `rag-department-admin`. **Full conversations: `rag-reviewer` only.** No administrative role implies conversation content (§3). Roles: **P1** realm roles `rag-*` on the shared Keycloak's client `ume-rag-admin`, audit actor `sub`; **P2** app roles on RAG's own Entra registration `ume-rag-admin`, actor `oid` (fallback `sub`) (identity §1.1, §1.6, 2026-10-31) |
| 5 | Concurrency | Every editable aggregate carries `version` (Postgres `xmin`); a mismatch is 409 `concurrent_change` (§4.3) |
| 6 | Write path | One transaction: **`SELECT … FOR UPDATE` on `config."ConfigVersion"` first** (serialises admin config writes) → cross-rules → entity change → outbox trigger (version bump) → audit row with that `ConfigVersion` → optional job rows; `pg_notify` at commit (§4.4) |
| 7 | Validation | Field rules on request records; **cross-rules** in one `ConfigRules` service: **V1–V39 with V20 and V27 retired (36 rules)** (§4.6), run on save, on dependent changes and again before publish, under the version lock; CHECK violations (23514) answer 400 |
| 8 | Jobs | The admin inserts `content."Jobs"` rows only (preview, sync, command, eval); every result is read from `Jobs.Result`, `Runs`, `EvaluationRuns` (§4.7) |
| 9 | Pilot UI | P1 has **dedicated pages only for the department journey** (collection, source, preview, publish with the evaluation report, assistant, test panel, audience, feedback, limited review), built for non-specialists (answered 2): setup checklist, template parameters with defaults, preflight and preview before saving, impact before scope changes (§5.4) |
| 10 | Pipelines | A pipeline is a **template + parameters**; the source form shows the template's parameters; pipeline editors edit parameters. No node editor, no canvas (dropped from the roadmap) (§6) |
| 11 | Publish | **Plan → re-process → evaluation report → keep or roll back**; no `gating` state, no blocking gate; previous chunk sets kept 14 days for one-click rollback (§6.6) |
| 12 | Evaluation | **Advisory reports** (recall@10, MRR with counts, compared with the previous run) on the publish screen and the collection page; no floor, no `building` state, no overrides (§7) |
| 13 | Feedback | P1 page from metadata (rating, fixed reason category, outcome, coverage, cited titles); a comment opens after a one-click pre-filled reason, audited in `ConversationAccess`, once A1 allows stored content (the P1 pilot runs history mode `none`: no comments in our store, weakness review §4) (§8.1–§8.2) |
| 14 | Conversation review | `rag-reviewer` opens a conversation with a free-text reason (≥ 20 characters), 30-minute access window, audited; first for conversations behind a feedback item (`public`/`internal`), then search and the `sensitive` second-approver flow; **built when A1 allows stored content, not in P1** (weakness review §4) (§8.3–§8.4) |
| 15 | Model catalogue | `GatewayModels` sync in **P1** (rows with `Kind` and `Residencies[]`), from `GET /v1/models` once the gateway's integration phase 1 runs; on assistant save the model is also checked against the gateway's **`/v1/models` at the level's ceiling**, as the assistant's gateway key from phase 2 (§4.6 V8, V39, §4.10) |
| 16 | Notifications | In-app only (attention cards, nav badges) plus SIEM events; no e-mail in P1/P2 (§5.2) |
| 17 | Accessibility | Gateway target WCAG 2.2 AA; copied axe/target-size/reflow tests, a keyboard-only Playwright run of the pilot journey, one NVDA pass; *tillgänglighetsredogörelse* (accessibility statement) as the gateway's (§10.1) |
| 18 | Dependencies | No new npm or NuGet package: no Markdown, chart, JSON-schema-form, graph or i18n package (§10.2) |
| 19 | Platform objects | In P1a the platform team sets everything up through the OpenAPI UI (Scalar) only; from P1b settings, data protection, clients, templates, site profiles, embedding profiles, gateway models and keys use **generic schema-driven forms** plus Scalar, not dedicated pages (§5.8) |
| 20 | Site profiles | The source preview shows the profile's before/after text on sample pages; platform admins see profiles per host with versions and roll back (Scalar in P1a; generic form + preview from P1b). In P1 the profile is a manual, hand-checked Sitevision preset JSON; learned profiles, proposals and approve are **P2 (weakness review §1)** (§4.5, §6.4) |
| 21 | Explain access | `POST /api/access/explain` returns the stable id of the first failing `IAccessPolicy` step and its reason code for (client, subject, assistant); platform admins, and department admins for their own department's assistants and clients; no security event (§4.5, identity §4.9) |
| 22 | Assistant versions | **One assistant type**: no `rag`/`agent` choice; an assistant uses the agent loop (P2) exactly when a linked skill carries tools, shown as **Uses tools**. Edits go to a **draft** (`AssistantVersions`, `State = draft`); the test panel runs the draft; **Publish** makes it the published version (`Assistants.PublishedVersionId`), the only one chat reads (data-model view `PublishedAssistantVersions`); history with one-click **Restore** (= a new version; the version diff is **P2**, weakness review §1). Audience, level, retention, owner team, description and contact belong to the assistant, not to a version, so publishing never changes who may use it (§4.5, §5.5.4) |
| 23 | Assistant tests (**P2**, weakness review §1) | Each assistant has up to 50 **test questions** (question, expected behaviour as free text, optional expected document and phrase), run by `AssistantTestRunner` in `rag-admin` through `Ume.Rag.Answering` and the answer judge (at the assistant's level) after every publish and on demand; **advisory**, shown as the §7.2 card with **Keep** / **Restore the previous version** (§7.4) |
| 24 | Many assistants | An impact line on source and collection saves ("Affects the assistants: …"). **P2 (weakness review §1):** platform-owned **assistant templates** (generic form); **copy** an assistant; an attention item for assistants unused 90 days. **P1b:** the **Map** as a table (§5.5.6). P2: a department may limit writes to the object's **owner team** (§3.2) |
| 25 | Organisation | Decisions "Organisation: department-owned, municipality-shared knowledge bases with topics (user, 2026-11-02)": departments own; a knowledge base is shared with *only our department*, *selected departments* or the *whole municipality*, read-only through assistants; assistants link knowledge bases with optional **topics**; knowledge bases have a contact person; teams are hidden in create forms (§3.2, §4.5, §5.2, §5.5) |

## 12. Milestones

| Phase | Deliverable |
|---|---|
| P0 | BFF against `ume-rag-admin` + roles (incl. `rag-reviewer` in the realm and Bicep), design system and shell in English, `labels.ts`/`messages.ts` skeleton, `AdminContext` write path (version lock + rules + audit + outbox + version), `PermissionMatrix` with generated role tests, org sync, error-code export. **Built**: pages Home (your roles and departments with their source) and Organisation (synced departments and teams; **Fetch now** for platform admins); `/api/clients` is the first resource on the write path (its generic form is P1b, §5.8) |
| P1a (walking skeleton, weakness review §1) | none of the journey pages: the pilot's collection, sources and one assistant are created through the write path with Scalar (generic forms come in P1b); the umea.se site profile as a manual profile JSON; `GatewayModels` sync; clients; the pilot gold set entered as `human` questions through Scalar (weakness review §2) |
| P1b (1) | collections (incl. documents tab, PII view/clear), sources (URL list, crawl, upload batches), preflight, preview with site-profile before/after, impact, runs and crawl report, deletion decisions, template parameters, publish (plan of every document → re-process → report → keep or roll back), rollback |
| P1b (2) | evaluation set and reports (led by the gold set; coverage-threshold proposal as numbers, weakness review §2), retrieval settings, collection publish, assistants (draft → publish, versions with restore, archive, description and contact), test panel on the draft (`Ume.Rag.Answering`, same release as `rag-api`), impact lines, V8 `/v1/models` check, audiences (clients), retention, generic forms (settings, data protection, clients, keys, templates, site profiles, embedding profiles, gateway models, organisation), audit list, explain access |
| P1b (3) | feedback page (ratings, import health; comment reveal, conversation review behind feedback and the access log when A1 allows stored content, weakness review §4); accessibility pass and tillgänglighetsredogörelse; pilot journey E2E green |
| P1b (4) | organisation (Decisions "Organisation: department-owned, municipality-shared knowledge bases with topics"): sharing levels and `CollectionShares`, topics on links, contact on knowledge bases, V11 widened and V35, `view=` lists, the Map table, the menu regrouped, teams out of create forms |
| P2 | from weakness review §1: assistant templates and copy, version diff, assistant test questions and `AssistantTestRunner`, the Map drawing, unused-assistant attention item, latest answer evaluation (`AnswerEvaluationService`), site-profile proposals with learning, effect-tag publish plans; skills (share lists, gate report, serve); tool servers (registration, approvals, pre-registration, grants, drift leader); tool use through skills (the derived **Uses tools**, limits, tool calls in the test panel); owner-team scoping; parse-profile proposals; SharePoint; crawl hierarchy tree; parameter diff; coverage histogram and proposal; audit diff viewer and History tabs; conversation search and `sensitive` two-person flow; feedback → test question; widget keys; dedicated pages where generic forms prove too thin |
| P3 | analytics, OBO configuration, risk acceptance, skill import/export, embedding profile migration (of a profile in use; editing one before use is P1b, Decisions "Editable embedding profiles before use") |

## 13. Changes to other documents

| Document | Change |
|---|---|
| `archive/decisions-log.md` | row "Admin (step 10)" restated: journey pages only in P1, generic forms for platform objects, publish = plan → re-process → advisory report → keep or roll back, no canvas, serialised config writes, **V1–V34 with V20 and V27 retired (32 rules)**, one assistant type with draft/publish and versions (replaces "V1–V31", "`gating`", "Vue Flow 1.48.2", "`building` collections") |
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
| `archive/area-plans/platform.md` §8a | review row: reason by `rag-reviewer`; comments of `public`/`internal` also by department admins after a one-click reason; no data tier configuration |
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
| `tools/design.md`, `archive/area-plans/tools.md` | done in the same pass: skills without collections, mode derived from tool-bearing skills |

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

## 16. Deferred or removed (simplification pass)

Per [`../archive/passes/simplification-pass.md`](../passes/simplification-pass.md):

| Removed or deferred | Where it went |
|---|---|
| Vue Flow canvas, its packages, layout table `PipelineCanvasLayouts`, canvas a11y work (§6.8) | Dropped from the roadmap (§2) |
| P1 node editor, node palette, ports, locks, JSON definition view, rules 1–12 (§6.2) | Removed: template + parameters (§2) |
| Version diff (§6.5), rebase, bulk rebase, `locked_node_below_min` | Parameter diff P2 if needed; a source moves to the latest template on its next publish (§10) |
| `gating` state, `PlanJobId`, blocking pipeline/collection/retrieval gates, gate cards with intervals, escalation and McNemar, collection floor (V27), `building` state, platform-admin overrides (§7.3), `gate.override` | Removed; advisory evaluation report, keep or roll back (§8) |
| Coverage-threshold proposal and histogram | Histogram P2 (§10); the proposal as numbers on the report card is P1 (weakness review §2, §7.1) |
| Crawl hierarchy tree (`hierarchy` preview job) | P2 (§10) |
| Audit field-level diff viewer, History tabs | P2 (§10); the audit list stays |
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
| Assistant `Mode` choice (`rag`/`agent`), `activate`/`deactivate` (`assistant.activate`), the unsaved-form test route `POST /api/assistants/test`, collections reached through skills (V3, V11 "union with skills'") | Removed (§15): mode derived from tool-bearing skills; draft → **Publish**, `archive`/`unarchive`; the test panel runs the saved draft; collections only through the assistant's own links |

## 17. Deferred or removed (weakness review)

Per [weakness review §1](../passes/weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer):

| Item | Where it went |
|---|---|
| Assistant version diff (`…/diff`, **Compare**, the diff in the publish dialog) | P2 |
| Assistant test questions, `AssistantTestRunner`, test reports, **Save as test question** (§7.4) | P2 |
| Assistant templates (generic form, `templateId` on create), copy an assistant | P2 |
| Department map (Map, §5.5.6, `GET /api/organisation/departments/{id}/map`), unused-assistant attention item | the map as a table in P1b (Decisions "Organisation: department-owned, municipality-shared knowledge bases with topics (user, 2026-11-02)"), its drawing and the attention item P2; the impact line on saves stays in P1 |
| Latest answer evaluation (`AnswerEvaluationService`, nightly `answers` run) | P2 |
| Learned site profiles, proposals, approve/reject | P2; P1 has manual profile versions (edit, roll back), the umea.se one a hand-checked Sitevision preset |
| Publish plan split by effect tags | P2; P1 re-processes every document of the collection, still with report and keep or roll back |
| Dedicated journey pages before the first answer | P1b; the walking skeleton (P1a) uses Scalar only, generic forms come in P1b |

Per [weakness review §4](../passes/weakness-review.md#4-data-protection-guards-the-secondary-copy-while-the-primary-stays-unprotected):

| Item | Where it went |
|---|---|
| Comment reveal (§8.2), conversation review behind a feedback item (§8.3), Access log (§8.5), `ConversationAccess` writes and event 6004 | When A1 allows stored content; P1 pilot runs history mode `none`, the Feedback page shows ratings only |
| Chat master keys in the admin (`IKeyProvider` for comment and conversation decryption) | Same; the admin still unwraps `internal` blobs |
| History modes `stored`/`session` in the data-protection form and the assistant retention form (V10, V29) | Same; P1 allows `none` only |

Per [weakness review §5](../passes/weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control):

| Item | Where it went |
|---|---|
| Delegation echo in the gateway status check (§4); `memberGroups` and team `code` in the organisation sync | the echo with the gateway's integration phase 2, member groups and codes with release 2; P1 checks the ceiling with the pilot team's per-level keys and syncs departments and teams without them |

## 5.9 Clients and keys: the two passes (moved 2026-11-21)

Moved here from the design in session 53's clean-up; the design states the page as built (0.6.0, M-16).

**First pass (user, 2026-11-13).** Connecting an app took two generic forms, free-text kinds and levels, a client GUID
nobody could see and a JSON scope object (session 34 review). Clients got a page of their own, replacing the generic
resource `clients` and keeping its place in the Platform index; keys were made from their client; *Add client* had three
steps; an `open-webui` client's result showed the stack lines `KALLA_OPENWEBUI_API_KEY` and `KALLA_OPENWEBUI_CLIENT_ID`;
the drawer had *Used by*; the Keys page picked a client first.

**Second pass (§5.9.1).** After the first pass a new client still could not use anything (access was given on each
assistant's page, one at a time), an integration's result was a secret and a GUID with no address or model name, the
key form showed every assistant and knowledge base of the platform, and keys had a second page. The pass added *What it
may use*, *How to connect*, the collapsed *Restrictions*, the two tabs, and took `open-webui` out of *Add app*.

**§5.10 before it was built.** The side menu's Platform group had one item, *Platform*, that opened an index of seven
cards (§5.8), so every platform object was two clicks away; Organisation was both a menu item and a card; Settings was a
table of nine sections that each opened a drawer with a generated form, several members as JSON text.
