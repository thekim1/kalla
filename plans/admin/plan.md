# Admin API and Admin UI: plan

The control plane: where departments set up sources, pipelines, skills and assistants, and where the platform team
runs the platform. Its own app (decision), with the gateway's design system, login pattern and role model, so it
feels like the same family without being coupled to the gateway's releases. Review items applied: R2–R7. Detailed
design: [`design.md`](design.md) (detail step 10); where they differ, the design wins.

## 1. Scope

**Does:** configuration CRUD with validation and audit; pipeline canvas and template forms; preview and publish
flows; status of syncs and runs; parse profiles; evaluation sets and gates; assistants and skills with a test panel;
tool servers; clients and keys; conversation review with justification; settings and retention.

**Does not:** sit in any data-plane request path; execute pipelines or evaluations (it asks the worker through jobs).

## 2. Admin API

- ASP.NET Core BFF like the gateway's Admin API: Entra OIDC code + PKCE, secure HttpOnly cookie, CSRF, no tokens in
  the browser; thin endpoints, logic in services, typed contracts, audit snapshot per change.
- Writes the `config` schema; **every change writes the config outbox row and bumps the config version in the same
  transaction** (R7); reads `content` and `chat` (chat only with justification); appends to `audit`.
- Validates pipelines and generates node forms from the **registered node manifest**, not a compiled-in library, and
  refuses to publish a pipeline that no running worker supports (R7).
- Validates assistant composition: classification of collections, skills and tool servers ≤ assistant level; the
  prompt-injection rule in `../tools/plan.md` §6.
- Validates residency on publish and on every audience link (chat design §8.2): an assistant offered to a client that
  resends history (Open WebUI) runs follow-ups at max(assistant level, client ceiling), so its `GatewayModel` must be
  servable within that residency; otherwise the publish or link is refused (pilot: `public` assistants in Open WebUI
  need EU or on-prem models). Not-found contact links must pass the chat link allow-list (chat design §9.4, §10.1).
- Reads departments and teams from the gateway (single source of truth for codes, R4).
- OpenAPI + Scalar for platform admins.
- **Swedish-only UI** (answered 4): `lang="sv"`, texts in components and a labels file as in the gateway, no i18n
  library; the API, logs, OpenAPI and docs stay English, and server errors carry a stable `code` the UI maps to a
  Swedish text (design §4.2). The gateway's own UI texts are English; we diverge on purpose for non-specialist users.

## 3. Roles

Same model as the gateway (`../identity/plan.md` §3): roles from the `roles` claim (Entra app roles assigned to
groups, values `rag-*`) or from `UME_RAG_OIDC_GROUPS_<ROLE>`; departments from `departmentCodes` plus the departments'
member groups (feature C, shared with the gateway; member groups are edited in the gateway's *Organisation* page and
read here). Assistant audiences by group (P2) are picked by searching Entra groups.

| Role | Can |
|---|---|
| `rag-platform-admin` | Everything except conversation content (comments and conversations only with `rag-reviewer` as well); node catalogue, templates, locked nodes, embedding profiles, clients, tool servers, tool grants, platform skills, gate overrides, risk acceptances, platform settings, audit log |
| `rag-department-admin` | Everything inside own departments: sources, collections, assistants, skills, keys, evaluation; feedback comments (`public`/`internal`) after a one-click reason (`FeedbackComments`), no conversations |
| `rag-reviewer` (P1) | Read feedback comments (`public`/`internal`, one-click reason) and, with a free-text reason, the conversation behind a feedback item, audited; `sensitive` also needs a second reviewer (P2); policies `Review`, `FeedbackComments`, `Read` (design §3.1, §8) |
| `rag-pipeline-editor` | Edit pipelines on the canvas in own departments (copy templates, change parameters, add approved nodes) |
| `rag-editor` (from P2) | Sources, collections and assistants in own departments; links published skills (own department + platform skills) to assistants but cannot edit skills; template forms, not the canvas |
| `rag-skill-editor` (from P2) | Draft, publish (behind the skill gate), serve and roll back skills of own departments (`EditSkills`, tools design §3.6) |
| `rag-viewer` | Read status, runs, evaluation results and usage in own departments |

`Review` and `FeedbackComments` are not part of `Admin`: no administrative role implies conversation content.
Scoped by `departmentCodes` / `teamCodes` claims, as in the gateway. A signed-in user with no `rag-*` role gets 403 on
every page and API call (the gateway shows such users its catalogue; we have no public page). The organisation page
shows member groups read-only and links to the gateway's *Organisation* page to edit them. Sign-in, sessions, keys page
and security events: `../identity/design.md` §1, §6.4, §10.

## 4. Admin UI pages

| Page | Contents |
|---|---|
| Overview | Sync health per department, degraded sources, paused deletions waiting for confirmation, failed evaluation gates, recent failures |
| Documents | The shared document store: which collections and sources use a document, parse strategy and profile, last fetch (R2) |
| Collections | Classification, sources, document counts, retrieval settings (`RetrievalSettings` form: weights, candidates, `efSearch`, chunks per document behind the evaluation gate; coverage threshold and section budget saved directly; knowledge design §12.1), coverage-threshold proposal from the evaluation set's best-distance distribution (knowledge design §7), **evaluation set and latest results** |
| Sources | Type-specific form, pre-flight check, schedule, "sync now", run history, crawl reports, failed items, deletion confirmations; notice when another department already crawls the same site |
| Pipelines | Templates; canvas (Vue Flow) for pipeline editors with typed ports and the node palette from the manifest; generated parameter forms; per-node preview; draft/published versions, diff, restore; publish with re-processing plan, cost estimate and **evaluation gate result** |
| Parse profiles | Learned and manual profiles, confidence, sample count, trial results, gate results, auto-applied/proposed state, rollback, kill switch |
| Evaluation | Per collection and assistant: generated, skill and human questions; add/edit questions; turn flagged feedback into rewritten questions without personal data; run history per version; gate thresholds (R6) |
| Assistants | Mode (RAG / agent), prompt, model (agent mode: `SupportsTools` required), collections, skills (pin or follow, tool exclusions), agent limits within the platform caps (tools design §6.5), lethal-trifecta result and risk acceptance (platform admins, tools design §5.4), query-step form (condense toggle, history window messages/tokens, context and output tokens, temperature, language mode `auto`/`sv`; chat design §5–§6, §10.3), not-found texts sv/en + contact links (chat design §10.1), classification, audience (who may use it), retention, allowed origins; **test panel** with retrieved chunks, tool calls and the answer (raw model text next to the filtered, delivered text, chat design §9.4); the panel runs the real chat path (`Ume.Rag.Answering`, design §4.9) in the admin process (also over `building` collections, with the admin's role scope; knowledge design §4.2), so the admin host needs `Gateway:*` settings for query embeddings |
| Skills | Versioned skill packages (tools design §3): draft editor (Markdown body with token estimate, reference files, linked collections and tools, test questions, classification); skill gate result; publish/serve/rollback with the served pointer; MAJOR auto-pin notice for other departments' assistants; MCP exposure and audiences (`SkillAudiences`); import/export in Agent Skills format (P3) |
| Tool servers | Registered MCP servers (tools design §4): registration (URL, slug, hosting, auth mode and credential, clearance, result level, residency, owner team, system owners) with connection test; discovery; approvals by platform admin and system owner with a definition diff; department grants; drift observations; catalogue of hosted servers (owner, wrapped systems, repository, image digest, version) |
| Clients | Client registry (R5): Entra app registrations, keys, Open WebUI and widget instances; classification ceiling; data-handling profile (stores history? retention? task model: the on-prem alias behind `owui-tasks`, chat design §4.2); warning when an `open-webui` client's requests arrive without the `X-Rag-Owui-Message-Id` header (chat design §11.4); tool policy `ToolPolicy` (`none`/`read`, tools design §5.1) |
| Keys | `ume-rag-sk-`/`ume-rag-pk-` keys per team: scopes, origins, rotation, revoke |
| Gateway | Platform service key status and delegation check; interim per-team key links; residency verification for sensitive use (R4); `GatewayModels` sync (residency, `SupportsTools`, context window, prices; tools design §6.4) |
| Embedding profiles | Platform admins: the on-prem default profile, exceptions, migration status (R3) |
| Feedback (P1) | Department-scoped ratings from `/v1/feedback` and the Open WebUI import: rating, reason category, assistant, time, outcome, cited documents (titles), without content; import health. A comment (`public`/`internal`) is shown after a one-click, pre-filled reason ("Granskning av återkoppling"), audited as a conversation access; only the comment, never the conversation; readable by `rag-reviewer` and `rag-department-admin`, not by platform admins implicitly; `sensitive` comments only through the full review flow with a second approver (chat design §14.4) |
| Conversations | `rag-reviewer` only; reading a conversation requires a reason, audited; **P1 limited to the conversation behind a feedback item** (`public`/`internal`) plus the access log (answered 8); P2 search by assistant and period and, for `sensitive`, a second approver before content is decrypted |
| Audit | All changes and privileged reads |
| Settings | Retention defaults, deletion guard thresholds, crawl defaults, evaluation gate defaults |
| Data protection | Per classification level: history modes allowed, retention default and maximum, encryption required, review rules, allowed clients; key service status and master-key rotation; data tier configuration (`../platform/plan.md` §8a). Platform admins only |

Search (Ctrl K), mobile layout and accessibility as in the gateway admin UI (`~/git/llmgateway/docs/admin-ui.md`), with
Swedish texts (answered 4) and, beyond the gateway, one NVDA screen-reader pass of the pilot journey before the pilot
(answered 7).

## 5. Milestones

| Phase | Deliverable |
|---|---|
| P0 | Entra/Keycloak BFF login, app roles (incl. `rag-reviewer`), audit infrastructure, config outbox, write path (audit + outbox + version), design system and shell in Swedish, error-code export, org sync |
| P1 a | Collections, sources (URL list, crawl, upload) with preflight, impact and run history, deletion decisions, documents page with PII view/clear, template-based pipelines (template forms, node list + JSON view) with preview, diff, publish (plan → gate → confirm) and rollback |
| P1 b | Evaluation page (generated sets), gate cards, collection floor and publish, retrieval settings, RAG assistants with test panel, audiences (clients), retention, `GatewayModels` sync, clients registry, keys, gateway page, settings, data protection, audit log |
| P1 c | **Feedback page** (chat design §14.4) with comment reveal, conversation review behind feedback (`public`/`internal`), access log, import health; accessibility pass (incl. NVDA) and tillgänglighetsredogörelse; pilot journey E2E green |
| P2 | Canvas for pipeline editors, parse profiles, SharePoint source with pre-flight, skills (`rag-skill-editor`, skill gate), tool servers (approvals, grants, drift), agent assistants, conversation search and `sensitive` two-person review, feedback → test question, widget configuration |
| P3 | Query-side canvas, analytics (retrieval, feedback and evaluation trends), OBO tool configuration, skill import/export, embedding profile changes |

## 6. Open questions

Answered (2026-10-29):

1. **One or two admin UIs**: separate apps, same design system and sign-in, cross-linked between related pages
   (e.g. a team's gateway budget from its RAG department page); independent releases.
2. **Editor role**: not for the pilot; the pilot uses `department-admin` and `pipeline-editor`. `editor` is added
   when more departments join (P2).

3. **Tool servers**: registered by platform admins only, on request from departments; new servers and changed tool
   definitions need approval by the platform admin and the wrapped system's owner (`../tools/plan.md` §9).

Answered (2026-10-30; detail in design.md §14.1):

4. **UI language**: Swedish, with the gateway's mechanism (texts in components and a labels file, no i18n); API, logs
   and docs in English. The gateway's own UI texts are English: the RAG admin diverges on purpose for its
   non-specialist users.
5. **Pilot setup**: the pilot department's own admins set up their collections and assistants in the P1 pages (forms
   and templates), with the platform team pairing at first; P1 pages are built for non-specialists (setup checklist,
   design §5.4).
6. **Conversation review**: new role `rag-reviewer`, granted per department, reads conversations with a reason,
   audited; `sensitive` also needs a second approver. Feedback comments (`public`/`internal`, one-click reason) are
   readable by `rag-reviewer` and `rag-department-admin`; full conversations only by `rag-reviewer`; platform admins
   get neither implicitly.
7. **Screen reader**: one NVDA + Firefox pass of the pilot journey before the pilot; findings fixed or listed in the
   tillgänglighetsredogörelse.
8. **Conversation review in P1**: limited: a `rag-reviewer` opens the conversation behind a feedback item
   (`public`/`internal`); general search and the `sensitive` two-person flow in P2.
