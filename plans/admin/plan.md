# Admin API and Admin UI: plan

The control plane: where departments set up sources, collections and assistants (later skills), and where the
platform team runs the platform. Its own app (decision), with the gateway's design system, login pattern and role
model, so it feels like the same family without being coupled to the gateway's releases. Review items applied: R2–R7.
Simplified on 2026-10-30 ([`../simplification-pass.md`](../simplification-pass.md) §1, §2, §8, §10, §15). Detailed design:
[`design.md`](design.md) (detail step 10); where they differ, the design wins.

## 1. Scope

**Does:** configuration CRUD with validation and audit; the department journey in Swedish pages (collection, source
with template parameters and preview, publish with the evaluation report, assistant with draft, publish, versions and
test questions, test panel, audience, department map, feedback, limited review); platform-admin objects through generic schema-driven forms and the OpenAPI UI; site profiles;
status of syncs and runs; evaluation sets and reports; skills and tool servers (P2); clients and keys; settings and
retention.

**Does not:** sit in any data-plane request path; execute pipelines or evaluations (it asks the worker through jobs).

## 2. Admin API

- ASP.NET Core BFF like the gateway's Admin API: Entra OIDC code + PKCE against RAG's own registration
  (`ume-rag-admin`), secure HttpOnly cookie, CSRF, no tokens in the browser; thin endpoints, logic in services, typed
  contracts, audit snapshot per change with the actor's `oid`.
- Writes the `config` schema; **every config transaction first locks `config."ConfigVersion"` (`SELECT … FOR UPDATE`),
  then runs the cross-rules, then writes the change, the outbox row, the version bump and the audit row** (R7;
  simplification pass §9: two concurrent saves cannot each pass the cross-rules). Reads `content` and `chat` (chat only
  with justification); appends to `audit`.
- Pipelines are templates + parameters: the admin validates parameters against the template's schema and refuses a
  template version that no running worker supports (R7).
- **Assistants** (simplification pass §15): one type; it uses the agent loop (P2) exactly when a linked skill carries
  tools (label "Använder verktyg", never a setting). Edits go to a **draft**, the test panel runs the draft,
  **Publicera** makes it the published version (the only one chat serves); version history with a diff and one-click
  restore (= a new version). Each assistant has a small set of **test questions** (question, expected behaviour as
  free text, optional expected document or phrase), run after every publish by the answer judge in `rag-admin`,
  advisory like collection evaluation. Audience, level, retention, description and contact belong to the assistant,
  not to a version. Assistants can be copied and created from platform-owned **templates**.
- Validates assistant composition: collections (own or shared with the department; knowledge reaches an assistant only
  through its own collection links, never through skills), skills and tool servers' **result level** ≤ assistant
  level, tool servers' **clearance** ≥ max(assistant level, ceiling of history-resending audience clients) (design
  §4.6); the lethal-trifecta refusal (`../tools/plan.md` §6). On a draft these are warnings; publish refuses.
- Validates residency on publish and on every audience link (chat design §8.2): the `GatewayModel` must be servable at
  max(assistant level, client ceiling), and on publish the model must be listed by the gateway's `/v1/models` called at the
  level's ceiling (simplification pass §7). Not-found contact links pass the chat link allow-list.
- **Explain access**: `POST /api/access/explain` returns, for (client, subject, assistant), the stable id of the first
  failing `IAccessPolicy` step and its reason code (`../identity/design.md` §4.9); for platform admins, and for
  department admins on their own department's assistants and clients; no security event (simplification pass §7).
- Reads departments and teams from the gateway (single source of truth for codes, R4).
- OpenAPI + Scalar for platform admins; in P1 it is also their UI for objects without a dedicated page.
- **Swedish-only UI** (answered 4): `lang="sv"`, texts in components and a labels file as in the gateway, no i18n
  library; the API, logs, OpenAPI and docs stay English, and server errors carry a stable `code` the UI maps to a
  Swedish text (design §4.2).

## 3. Roles

Same model as the gateway (`../identity/plan.md` §3): roles from the `roles` claim of RAG's own Entra registration
(values `rag-*`) or from `UME_RAG_OIDC_GROUPS_<ROLE>`; departments from `departmentCodes` plus the departments' member
groups (feature C; claims only in P1, Graph lookup in P2; member groups are edited in the gateway's *Organisation* page
and read here).

| Role | Can |
|---|---|
| `rag-platform-admin` | Everything except conversation content (comments and conversations only with `rag-reviewer` as well); templates, site profiles, embedding profiles, clients, tool servers, tool grants, platform skills, parse-profile proposals (P2), platform settings, audit log |
| `rag-department-admin` | Everything inside own departments: sources, collections (incl. share lists), assistants, skills, keys, evaluation; feedback comments (`public`/`internal`) after a one-click reason (`FeedbackComments`), no conversations |
| `rag-reviewer` (P1) | Read feedback comments (`public`/`internal`, one-click reason) and, with a free-text reason, the conversation behind a feedback item, audited; `sensitive` also needs a second reviewer (P2); policies `Review`, `FeedbackComments`, `Read` |
| `rag-pipeline-editor` | Edit template parameters of sources in own departments |
| `rag-editor` (from P2) | Sources, collections and assistants in own departments; links served skills usable by the department to assistants but cannot edit skills |

Owner-team scoping (P2, simplification pass §15): assistants, collections and skills keep an owner team; a department
may limit writes by editors (`Edit`, `EditPipelines`, `EditSkills`) to members of the object's owner team; department
and platform admins still see and edit everything (design §3.2).
| `rag-skill-editor` (from P2) | Draft, publish, serve, roll back and share skills of own departments (`EditSkills`, tools design §3.6) |
| `rag-viewer` | Read status, runs, evaluation results and usage in own departments |

`Review` and `FeedbackComments` are not part of `Admin`: no administrative role implies conversation content. A user
with no `rag-*` role gets 403 on every page and API call.

## 4. Admin UI pages

**P1 department journey (dedicated Swedish pages):**

| Page | Contents |
|---|---|
| Overview | Setup checklist, paused deletions waiting for confirmation, evaluation reports that got worse, PII-flagged documents in `public` collections, recent failures, assistants unused for 90 days (suggest archiving) |
| Map | The department's sources → collections → assistants → audiences, with a table view; saving a source or collection names the assistants it affects ("påverkar 3 assistenter") |
| Collections | Classification, sources, documents (incl. PII list per chunk set and clearance of the document with a reason), share list (named departments), retrieval settings (saved directly; knowledge design §12.1), **evaluation set and latest reports** |
| Sources | Type-specific form (URL list, crawl, upload) with template parameters under "Avancerat", pre-flight check, preview of sample pages (for a crawl source with the learned site profile's before/after text), impact count before saving scope/identity changes, schedule, "sync now", run history, crawl reports, failed items, upload list, quarantine list, deletion confirmations, held references |
| Publish | Plan (counts, time, cost) → re-process → **evaluation report** (recall@10 and MRR with counts against the previous run) → keep or roll back (one click within 14 days) |
| Assistants | Create from a template or copy; description and contact; **draft** with prompt, model (validated against the gateway at the level's ceiling on publish), collections, query-step form, not-found texts sv/en + contact links (P2 skills, "Använder verktyg" when one carries tools); **test panel** on the draft (retrieved chunks, raw vs delivered answer; P2 tool calls), running `Ume.Rag.Answering` in the admin, deployed from the same release as `rag-api`; **Publicera**; **Versioner** (diff, restore); **Testfrågor** with the advisory test report after each publish; classification, retention, archive; the **latest answer evaluation** (faithfulness and citation precision of the nightly `answers` run, late P1, run in the admin) |
| Audience | Clients the assistant is offered to, with the residency check explained |
| Feedback | Department-scoped ratings without content; a comment after a one-click reason; import health |
| Conversations | `rag-reviewer` only; **P1 limited to the conversation behind a feedback item** (`public`/`internal`) plus the access log |

**P1 platform objects (generic schema-driven forms + OpenAPI UI, platform admins):** settings, data protection
(shredding switched off until A1), clients, templates, assistant templates, site profiles (versions, proposals, approve/rollback),
embedding profiles (read), gateway models, keys, organisation mirror (read), audit log list.

**P2 pages:** Skills (draft editor, gate report from the skill gate the admin runs, serve/rollback, share list), Tool servers (registration, discovery,
approvals by platform admin and system owner, pre-registration, grants, drift), parse-profile proposals, conversation
search and the `sensitive` two-person flow, dedicated pages for keys and clients if the generic forms prove too thin.

Search (Ctrl K), mobile layout and accessibility as in the gateway admin UI (`~/git/llmgateway/docs/admin-ui.md`), with
Swedish texts (answered 4) and one NVDA screen-reader pass of the pilot journey before the pilot (answered 7).

## 5. Milestones

| Phase | Deliverable |
|---|---|
| P0 | Entra/Keycloak BFF login against RAG's own registration, app roles (incl. `rag-reviewer`), audit infrastructure, write path (lock + rules + change + outbox + version + audit), design system and shell in Swedish, error-code export, org sync |
| P1 a | Collections, sources (URL list, crawl, upload) with preflight, preview (site-profile before/after), impact and run history, deletion decisions, documents tab with PII view/clear, template parameters, publish (plan → re-process → evaluation report → keep or roll back) |
| P1 b | Evaluation set and reports, retrieval settings, assistants (templates, copy, draft → publish, versions with diff and restore, test questions) with test panel (same release as `rag-api`), department map, audiences (clients), retention, `GatewayModels` sync, generic forms for platform objects (settings, data protection, clients, templates, assistant templates, site profiles, embedding profiles, gateway models, keys), audit list, explain access |
| P1 c | **Feedback page** with comment reveal, conversation review behind feedback (`public`/`internal`), access log, import health; accessibility pass (incl. NVDA) and tillgänglighetsredogörelse; pilot journey E2E green |
| P2 | Skills (`rag-skill-editor`, share lists, gate report, serve), tool servers (approvals, pre-registration, grants, drift), tool use through skills, owner-team scoping, parse-profile proposals, SharePoint source, crawl hierarchy tree, parameter diff, conversation search and `sensitive` two-person review, feedback → test question, widget configuration |
| P3 | Analytics (retrieval, feedback and evaluation trends), OBO tool configuration, risk acceptance, skill import/export, embedding profile changes |

## 6. Open questions

Answered (2026-10-29):

1. **One or two admin UIs**: separate apps, same design system and sign-in, cross-linked between related pages;
   independent releases from the gateway (the RAG admin is released together with `rag-api`).
2. **Editor role**: not for the pilot; the pilot uses `department-admin` and `pipeline-editor`. `editor` is added
   when more departments join (P2).
3. **Tool servers**: registered by platform admins only; departments ask through the platform team's normal intake
   (no request flow in the admin); new tool definitions need approval by the platform admin and the system owner, a
   pre-registered change one approver, unexpected drift two (`../tools/plan.md` §9).

Answered (2026-10-30; detail in design.md §14.1):

4. **UI language**: Swedish, with the gateway's mechanism; API, logs and docs in English. The generic forms for
   platform objects show schema titles (Swedish where given).
5. **Pilot setup**: the pilot department's own admins set up their collections and assistants in the P1 journey
   pages, with the platform team pairing at first (setup checklist, design §5.4).
6. **Conversation review**: role `rag-reviewer`, granted per department, reads conversations with a reason, audited;
   `sensitive` also needs a second approver. Feedback comments are readable by `rag-reviewer` and
   `rag-department-admin`; full conversations only by `rag-reviewer`; platform admins get neither implicitly.
7. **Screen reader**: one NVDA + Firefox pass of the pilot journey before the pilot.
8. **Conversation review in P1**: limited to the conversation behind a feedback item (`public`/`internal`); general
   search and the `sensitive` two-person flow in P2.
9. **Many assistants per department** (simplification pass §15): one assistant type (mode derived from tool-bearing
   skills); knowledge only through direct collection links; drafts, publish, versions with diff and restore; test
   questions per assistant, advisory; copy, templates, department map, impact notice, 90-day attention item,
   description and contact; P2 owner-team scoping (design §3.2, §4.5, §5.5.4, §5.5.6, §7.4).

## 7. Deferred or removed (simplification pass)

Per [`../simplification-pass.md`](../simplification-pass.md):

| Item | Where it went |
|---|---|
| Pipeline canvas (Vue Flow), node palette, typed ports, locked nodes | P3 behind a trigger (register C2, `../simplification-pass.md` §2) |
| P1 node editor and JSON view | Removed: pipelines are template + parameters (§2) |
| Version diff, rebase | P2 if needed (parameter diff) (§10) |
| Blocking evaluation gates, gate cards with intervals/escalation, collection floor, `gating` and `building` states, platform-admin gate overrides | Removed; advisory evaluation report on the publish screen (§8) |
| Parse-profile auto-apply (confidence, trials, kill switch) | Removed; P2 proposals approved by platform admins (§2) |
| Crawl hierarchy tree, coverage-threshold histogram, audit diff viewer | P2 (§10); the audit list stays in P1 |
| Dedicated P1 pages for settings, data protection, clients, keys, gateway, embedding profiles | Generic schema-driven forms + OpenAPI UI in P1 (§10) |
| Interim per-team gateway key links | Removed with `GatewayKeyLinks` (§7) |
| Data tier configuration | Removed (§5) |
| Open WebUI task model on clients (`owui-tasks`) | Removed (§1) |
| Own-department-only collection and skill links | Share lists to named departments (§10) |
| Risk acceptance for the lethal trifecta | P3 (§10) |
| "(+ request)" right for tool servers | Removed: requests through the platform team's intake (§10) |
| Assistant mode choice (`rag`/`agent`), activate/deactivate, collections reached through skills | Removed: mode derived from tool-bearing skills, draft → publish, direct collection links only (§15) |
