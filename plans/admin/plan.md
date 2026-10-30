# Admin API and Admin UI: plan

The control plane: where departments set up sources, collections and assistants (later skills), and where the
platform team runs the platform. Its own app (decision), with the gateway's design system, login pattern and role
model, so it feels like the same family without being coupled to the gateway's releases. Review items applied: R2–R7.
Simplified on 2026-10-30 ([`../simplification-pass.md`](../simplification-pass.md) §1, §2, §8, §10, §15) and thinned by
the [weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer) (P1 split into P1a and P1b; assistant extras to P2, §8). Detailed design:
[`design.md`](design.md) (detail step 10); where they differ, the design wins.

## 1. Scope

**Does:** configuration CRUD with validation and audit; the department journey in Swedish pages (collection, source
with template parameters and preview, publish with the evaluation report, assistant with draft, publish, versions and
restore, test panel, audience, feedback, limited review; P2: version diff, test questions, department map); platform-admin objects through generic schema-driven forms and the OpenAPI UI; site profiles;
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
  **Publicera** makes it the published version (the only one chat serves); version history with one-click restore (= a
  new version). **P2 ([weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer)):** a version diff; a small set of **test questions** per assistant (question,
  expected behaviour as free text, optional expected document or phrase), run after every publish by the answer judge
  in `rag-admin`, advisory like collection evaluation; copy and platform-owned **templates**. Audience, level,
  retention, description and contact belong to the assistant, not to a version.
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
| `rag-reviewer` (P1) | Read feedback comments (`public`/`internal`, one-click reason) and, with a free-text reason, the conversation behind a feedback item, audited; `sensitive` also needs a second reviewer (P2); policies `Review`, `FeedbackComments`, `Read`. Nothing to read in P1: the pilot runs history mode `none` until A1 allows stored content ([weakness review §4](../weakness-review.md#4-data-protection-guards-the-secondary-copy-while-the-primary-stays-unprotected)) |
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
| Overview | Setup checklist, paused deletions waiting for confirmation, evaluation reports that got worse, PII-flagged documents in `public` collections, recent failures; assistants unused for 90 days (suggest archiving) from P2 ([weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer)) |
| Map (**P2**, [weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer)) | The department's sources → collections → assistants → audiences, with a table view. The impact notice on saving a source or collection ("påverkar 3 assistenter") stays in P1 on the Sources and Collections pages |
| Collections | Classification, sources, documents (incl. PII list per chunk set and clearance of the document with a reason), share list (named departments), retrieval settings (saved directly, incl. age decay `recency`, which the `website` template turns on for PDFs; weakness review §3; knowledge design §12.1), **evaluation set and latest reports**: reports lead with the `human` gold-set results, generated questions are a regression signal only, and the run proposes the coverage thresholds from answerable and unanswerable questions together ([weakness review §2](../weakness-review.md#2-nothing-proves-the-answers-are-good-and-not-found-is-effectively-off-at-launch); design §7.1) |
| Sources | Type-specific form (URL list, crawl, upload) with template parameters under "Avancerat", crawl `demote` patterns beside include/exclude shown on the source page (weakness review §3), pre-flight check, preview of sample pages (for a crawl source with the site profile's before/after text; the profile is the manual Sitevision preset in P1, learned from P2), impact count before saving scope/identity changes, schedule, "sync now", run history, crawl reports, failed items, upload list, quarantine list, deletion confirmations, held references |
| Publish | Plan (counts, time, cost; in P1 every document of the collection, effect-tag plans from P2, [weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer)) → re-process → **evaluation report** (recall@10 and MRR with counts against the previous run) → keep or roll back (one click within 14 days) |
| Assistants | Create (from a template or copy from P2); description and contact; **draft** with prompt, model (validated against the gateway at the level's ceiling on publish), collections, query-step form, not-found texts sv/en + contact links (P2 skills, "Använder verktyg" when one carries tools); **test panel** on the draft (retrieved chunks, raw vs delivered answer; P2 tool calls), running `Ume.Rag.Answering` in the admin, deployed from the same release as `rag-api`; **Publicera**; **Versioner** (restore; diff from P2); classification, retention, archive. **P2** ([weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer)): **Testfrågor** with the advisory test report after each publish; the **latest answer evaluation** (faithfulness and citation precision of the nightly `answers` run, run in the admin) |
| Audience | Clients the assistant is offered to, with the residency check explained |
| Feedback | Department-scoped ratings without content; import health. A comment after a one-click reason only once A1 allows stored content; in P1 comments stay in Open WebUI (weakness review §4) |
| Conversations | **Built when A1 allows stored content** (weakness review §4; not in P1). `rag-reviewer` only; first limited to the conversation behind a feedback item (`public`/`internal`) plus the access log |

**P1 platform objects (generic schema-driven forms + OpenAPI UI, platform admins):** settings, data protection
(history mode `none` only and shredding switched off until A1, weakness review §4), clients, templates, site profiles (manual profile JSON: versions, rollback; proposals with learning in P2),
embedding profiles (read), gateway models, keys, organisation mirror (read), audit log list.

**P2 pages:** Skills (draft editor, gate report from the skill gate the admin runs, serve/rollback, share list), Tool servers (registration, discovery,
approvals by platform admin and system owner, pre-registration, grants, drift), parse-profile proposals, conversation
search and the `sensitive` two-person flow, dedicated pages for keys and clients if the generic forms prove too thin; from [weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer): department map,
assistant templates (generic form), assistant copy, version diff, test questions, latest answer evaluation.

Search (Ctrl K), mobile layout and accessibility as in the gateway admin UI (`~/git/llmgateway/docs/admin-ui.md`), with
Swedish texts (answered 4) and one NVDA screen-reader pass of the pilot journey before the pilot (answered 7).

## 5. Milestones

| Phase | Deliverable |
|---|---|
| P0 | Entra/Keycloak BFF login against RAG's own registration, app roles (incl. `rag-reviewer`), audit infrastructure, write path (lock + rules + change + outbox + version + audit), design system and shell in Swedish, error-code export, org sync |
| P1a (walking skeleton) | The pilot's collection, crawl, URL-list and upload sources and its one assistant created through the write path with the OpenAPI UI and generic forms; the umea.se site profile entered as a manual profile JSON (hand-checked Sitevision preset); `GatewayModels` sync; clients; the pilot gold set entered as `human` evaluation questions through the OpenAPI UI (weakness review §2) |
| P1b (1) | Collections, sources (URL list, crawl, upload) with preflight, preview (site-profile before/after), impact and run history, deletion decisions, documents tab with PII view/clear, template parameters, publish (re-process every document → evaluation report → keep or roll back) |
| P1b (2) | Evaluation set and reports, retrieval settings, assistants (draft → publish, versions with restore) with test panel (same release as `rag-api`), audiences (clients), retention, generic forms for platform objects (settings, data protection, clients, templates, site profiles, embedding profiles, gateway models, keys), audit list, explain access |
| P1b (3) | **Feedback page** (ratings, reason categories, import health; comment reveal, conversation review behind feedback and the access log move to when A1 allows stored content, weakness review §4); accessibility pass (incl. NVDA) and tillgänglighetsredogörelse; pilot journey E2E green |
| P2 | From [weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer): assistant version diff, test questions and the answer evaluation, templates and copy, department map and 90-day attention item, site-profile proposals (learning), effect-tag publish plans. Skills (`rag-skill-editor`, share lists, gate report, serve), tool servers (approvals, pre-registration, grants, drift), tool use through skills, owner-team scoping, parse-profile proposals, SharePoint source, crawl hierarchy tree, parameter diff, conversation search and `sensitive` two-person review, feedback → test question, widget configuration |
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
   search and the `sensitive` two-person flow in P2. *Later change ([weakness review §4](../weakness-review.md#4-data-protection-guards-the-secondary-copy-while-the-primary-stays-unprotected)):* not in P1;
   comment reveal and conversation review are built when A1 allows stored content.
9. **Many assistants per department** (simplification pass §15): one assistant type (mode derived from tool-bearing
   skills); knowledge only through direct collection links; drafts, publish, versions with diff and restore; test
   questions per assistant, advisory; copy, templates, department map, impact notice, 90-day attention item,
   description and contact; P2 owner-team scoping (design §3.2, §4.5, §5.5.4, §5.5.6, §7.4). Diff, test questions,
   copy, templates, department map and the 90-day attention item later moved to P2 ([weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer)).

## 7. Deferred or removed (simplification pass)

Per [`../simplification-pass.md`](../simplification-pass.md):

| Item | Where it went |
|---|---|
| Pipeline canvas (Vue Flow), node palette, typed ports, locked nodes | P3 behind a trigger (register C2, `../simplification-pass.md` §2) |
| P1 node editor and JSON view | Removed: pipelines are template + parameters (§2) |
| Version diff, rebase | P2 if needed (parameter diff) (§10) |
| Blocking evaluation gates, gate cards with intervals/escalation, collection floor, `gating` and `building` states, platform-admin gate overrides | Removed; advisory evaluation report on the publish screen (§8) |
| Parse-profile auto-apply (confidence, trials, kill switch) | Removed; P2 proposals approved by platform admins (§2) |
| Crawl hierarchy tree, coverage-threshold histogram, audit diff viewer | P2 (§10); the audit list stays in P1; the threshold proposal as numbers on the report is P1 (weakness review §2) |
| Dedicated P1 pages for settings, data protection, clients, keys, gateway, embedding profiles | Generic schema-driven forms + OpenAPI UI in P1 (§10) |
| Interim per-team gateway key links | Removed with `GatewayKeyLinks` (§7) |
| Data tier configuration | Removed (§5) |
| Open WebUI task model on clients (`owui-tasks`) | Removed (§1) |
| Own-department-only collection and skill links | Share lists to named departments (§10) |
| Risk acceptance for the lethal trifecta | P3 (§10) |
| "(+ request)" right for tool servers | Removed: requests through the platform team's intake (§10) |
| Assistant mode choice (`rag`/`agent`), activate/deactivate, collections reached through skills | Removed: mode derived from tool-bearing skills, draft → publish, direct collection links only (§15) |

## 8. Deferred or removed (weakness review)

Per [weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer):

| Item | Where it went |
|---|---|
| Assistant version diff, per-assistant test questions with the advisory test report | P2 |
| Assistant templates, copy an assistant | P2 |
| Department map page, assistants-unused-for-90-days attention item | P2 (the impact notice on saving stays in P1) |
| Latest answer evaluation (nightly `answers` run) on the assistant page | P2 |
| Site-profile proposals, approval of re-learned profiles | P2 with learning; P1 edits the manual profile JSON |
| Publish plan from effect tags | P2; P1 publish re-processes every document of the collection, still with report and keep or roll back |
| Journey pages for sources and the assistant | P1b; the walking skeleton (P1a) uses the OpenAPI UI and generic forms |

Per [weakness review §4](../weakness-review.md#4-data-protection-guards-the-secondary-copy-while-the-primary-stays-unprotected):

| Item | Where it went |
|---|---|
| Comment reveal on the Feedback page, conversation review behind a feedback item, access log (Åtkomstlogg) | When A1 allows stored content; the P1 pilot runs history mode `none` (ratings only), comments stay in Open WebUI |
| History modes `stored`/`session` in the data-protection form | Same; P1 offers `none` only |
