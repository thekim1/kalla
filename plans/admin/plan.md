# Admin API and Admin UI: plan

The control plane: where departments set up sources, pipelines, skills and assistants, and where the platform team
runs the platform. Its own app (decision), with the gateway's design system, login pattern and role model, so it
feels like the same family without being coupled to the gateway's releases. Review items applied: R2–R7.

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
- Reads departments and teams from the gateway (single source of truth for codes, R4).
- OpenAPI + Scalar for platform admins.

## 3. Roles

Same model as the gateway (`../identity/plan.md` §3): roles from the `roles` claim (Entra app roles assigned to
groups, values `rag-*`) or from `UME_RAG_OIDC_GROUPS_<ROLE>`; departments from `departmentCodes` plus the departments'
member groups (feature C, shared with the gateway; member groups are edited in the gateway's *Organisation* page and
read here). Assistant audiences by group (P2) are picked by searching Entra groups.

| Role | Can |
|---|---|
| `rag-platform-admin` | Everything; node catalogue, templates, locked nodes, embedding profiles, clients, tool servers, platform settings, audit log |
| `rag-department-admin` | Everything inside own departments: sources, collections, assistants, skills, keys, evaluation |
| `rag-pipeline-editor` | Edit pipelines on the canvas in own departments (copy templates, change parameters, add approved nodes) |
| `rag-editor` (from P2) | Sources, collections, assistants and skills in own departments; template forms, not the canvas |
| `rag-viewer` | Read status, runs, evaluation results and usage in own departments |

Scoped by `departmentCodes` / `teamCodes` claims, as in the gateway. A signed-in user with no `rag-*` role gets 403 on
every page and API call (the gateway shows such users its catalogue; we have no public page). The organisation page
shows member groups read-only and links to the gateway's *Organisation* page to edit them. Sign-in, sessions, keys page
and security events: `../identity/design.md` §1, §6.4, §10.

## 4. Admin UI pages

| Page | Contents |
|---|---|
| Overview | Sync health per department, degraded sources, paused deletions waiting for confirmation, failed evaluation gates, recent failures |
| Documents | The shared document store: which collections and sources use a document, parse strategy and profile, last fetch (R2) |
| Collections | Classification, sources, document counts, retrieval settings, **evaluation set and latest results** |
| Sources | Type-specific form, pre-flight check, schedule, "sync now", run history, crawl reports, failed items, deletion confirmations; notice when another department already crawls the same site |
| Pipelines | Templates; canvas (Vue Flow) for pipeline editors with typed ports and the node palette from the manifest; generated parameter forms; per-node preview; draft/published versions, diff, restore; publish with re-processing plan, cost estimate and **evaluation gate result** |
| Parse profiles | Learned and manual profiles, confidence, sample count, trial results, gate results, auto-applied/proposed state, rollback, kill switch |
| Evaluation | Per collection and assistant: generated, skill and human questions; add/edit questions; turn flagged feedback into rewritten questions without personal data; run history per version; gate thresholds (R6) |
| Assistants | Mode (RAG / agent), prompt, model, collections, skills, query-step form, classification, audience (who may use it), retention, allowed origins; **test panel** with retrieved chunks, tool calls and the answer |
| Skills | Versioned skill packages: instructions, references, linked collections and tools, test questions, classification; import/export in Agent Skills format (`../tools/plan.md`) |
| Tool servers | Registered MCP servers: URL, auth mode, classification, allowed tools, pinned tool definitions with re-approval on change |
| Clients | Client registry (R5): Entra app registrations, keys, Open WebUI and widget instances; classification ceiling; data-handling profile (stores history? retention? task model?) |
| Keys | `ume-rag-sk-`/`ume-rag-pk-` keys per team: scopes, origins, rotation, revoke |
| Gateway | Platform service key status and delegation check; interim per-team key links; residency verification for sensitive use (R4) |
| Embedding profiles | Platform admins: the on-prem default profile, exceptions, migration status (R3) |
| Conversations | Feedback review; reading a conversation requires a reason, audited; for `sensitive` also a second approver before content is decrypted |
| Audit | All changes and privileged reads |
| Settings | Retention defaults, deletion guard thresholds, crawl defaults, evaluation gate defaults |
| Data protection | Per classification level: history modes allowed, retention default and maximum, encryption required, review rules, allowed clients; key service status and master-key rotation; data tier configuration (`../platform/plan.md` §8a). Platform admins only |

Search (Ctrl K), mobile layout and accessibility as in the gateway admin UI (`~/git/llmgateway/docs/admin-ui.md`).

## 5. Milestones

| Phase | Deliverable |
|---|---|
| P0 | Entra/Keycloak BFF login, app roles, audit infrastructure, config outbox, design system wired in |
| P1 | Collections, sources (URL list, crawl, upload) with run history, documents page, template-based pipelines with preview, publish and gate, evaluation page (generated sets), RAG assistants with test panel, clients registry, keys, gateway page, retention settings |
| P2 | Canvas for pipeline editors, parse profiles, SharePoint source with pre-flight, skills, tool servers, agent assistants, conversation review, widget configuration |
| P3 | Query-side canvas, analytics (retrieval, feedback and evaluation trends), OBO tool configuration |

## 6. Open questions

Answered (2026-10-29):

1. **One or two admin UIs**: separate apps, same design system and sign-in, cross-linked between related pages
   (e.g. a team's gateway budget from its RAG department page); independent releases.
2. **Editor role**: not for the pilot; the pilot uses `department-admin` and `pipeline-editor`. `editor` is added
   when more departments join (P2).

3. **Tool servers**: registered by platform admins only, on request from departments; new servers and changed tool
   definitions need approval by the platform admin and the wrapped system's owner (`../tools/plan.md` §9).
