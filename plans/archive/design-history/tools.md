# tools: history moved out of the design

The process sections of [`../../tools/design.md`](../../tools/design.md) (summary of decisions, milestones, changes to other documents, questions and their answers, deferred and removed items), moved here unchanged on 2026-11-13 (M-14) under their original numbers, so the citations of them still resolve. History: it does not bind; `plans/decisions.md` and the design do.

## 1. Summary of decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Placement | **`IToolHub` in process** in the chat role of `Ume.Rag.Api` (library `Ume.Rag.Tools`): registry snapshot, MCP client, credential resolver, policy re-check, result normalisation, tool-call audit, with **bulkheads** per server (concurrency, timeout, circuit). The agent loop and skills runtime run in the same role (code in `Ume.Rag.Answering`). `rag-admin` runs the same library for discovery, drift polling, the test panel and the skill gate; the worker has no tool access. `rag-tools` as a separate service comes only with the first external, OBO or write tool (P3) (§2) |
| 2 | Skill format | Agent Skills folder (`SKILL.md` with `name`, `description`, optional `license`, `compatibility`, `metadata`, `allowed-tools`; `references/`, `assets/`) verified against the spec; platform data in `ume-rag.json`, test questions in `evals/evals.json`; `scripts/` refused (§3.1–§3.2) |
| 3 | Skill storage | Everything in `config` (instructions, manifest, test questions, **text** reference files ≤ 200,000 characters per version in `SkillVersionFiles`); no blobs (§3.3) |
| 4 | Versioning | Immutable published versions with SemVer labels (display); one **served** pointer; every linking assistant uses the served version; rollback = move the pointer (§3.5) |
| 5 | Authoring | Role **`rag-skill-editor`** (policy `EditSkills`, own departments) drafts, publishes and serves; publishing runs the **skill gate** (advisory) in `rag-admin`; the skill's owner serves; linking a tool needs a **department grant**; **share lists** to named departments (§3.6–§3.7) |
| 6 | Skill inclusion | Skills ≤ about **4k tokens** in total are included statically (tools visible from step 1); above that a catalogue + `load_skill(name)`; `read_skill_file` for references; assistants without tools always static (§3.8–§3.9) |
| 7 | MCP client | Official SDK 2.3.0; **Streamable HTTP only**; protocol version negotiated by the SDK (2026-07-28 first); pooled client per (server, config version); `hosted` servers only in P2 (§4.2–§4.3) |
| 8 | Pinning and drift | Definition hash = SHA-256 of canonical `{name, title, description, inputSchema, outputSchema, annotations}`; verified on every connection and by one drift leader every 15 min; a **pre-registered** definition (owner, one approver) is pinned when observed; any other difference disables the tool until **two-person** re-approval (§4.4–§4.5) |
| 9 | Credentials | P2: `client-credentials` only (certificate of the platform's tool identity `ume-rag-tools`, held by `rag-api` and `rag-admin` only; in-house token client); `obo` designed (P3); the hub refuses to start without its certificate when an active server needs it (§4.6) |
| 10 | Results | Per call ≤ 256 KiB on the wire, ≤ 16,000 characters to the model, ≤ 48,000 per message; text and structured content kept, images/audio/binary dropped, resource links listed never fetched; wrapped as `<tool_result … trust="untrusted">` with tag neutralisation; no result cache (§4.8, §4.10) |
| 11 | Policy | Deny by default; tools visible = server layer (approved, `read`, department grant) ∩ included/loaded skill ∩ assistant ∩ client (`ToolPolicy`); **one function** builds the model's `tools` list and authorises each call (§5.1–§5.2) |
| 12 | Levels | Tool servers get **clearance** (highest level of conversation data they may receive; residency-bound) and **result level**; assistant level ≤ clearance and ≥ result level, so the run's effective level is fixed at its start and never rises (§5.3) |
| 13 | Lethal trifecta | Computed per assistant on save, serve and grant changes from `ContainsThirdPartyText` and the composition; a **hard refusal** in P2 (§5.4) |
| 14 | Agent loop | Limits per user message: **8 tool calls, 60 s until the final answer starts, $0.05**; assistants may lower, platform admins raise to ≤ 20 / 120 s / $0.25 (answered 1); cost from usage × synced prices; **unknown price fails closed**; `search_knowledge` prefetched; a final `tool_choice: none` call is always reserved (§6) |
| 15 | Gateway | OpenAI function tools only (`type: function`); tool-capable models from the gateway's `tools` feature tag; residency per call as chat §8.2 (§6.4) |
| 16 | Streaming | Status events for Open WebUI; content of a step held back ≤ 200 characters until it is clear the step is the answer; `[n]` citations only for `search_knowledge` documents (§6.7–§6.8) |
| 17 | `/mcp` | Stateless Streamable HTTP, **early in P2**: `search_knowledge` and `list_collections`, client ceiling `public` by default; no tool-hub tools (§7) |
| 18 | Hosted servers | .NET template only (answered 3): stateless MCP SDK server, Entra token with app role `Tools.Invoke`, read-only conventions, golden `tools/list`, contract tests, chiseled image; own internal network plus one egress network per server (§8) |
| 19 | First server | **`ume-mcp-kommun`** (one container, one registration) starting with the mock fee calculator: tariffs as versioned JSON data (sample values), decimal arithmetic, structured output (§8.5) |
| 20 | Audit | Every hub call in `audit."ToolCallEvents"` (metadata + keyed HMAC of arguments); full arguments/results only encrypted in `chat."ToolCalls"`; events 5012–5015 (§9) |
| 21 | Skills and assistants | **Skills carry know-how and tools, never collections**: an assistant's knowledge is only its own collection links (knowledge §4.2). **One assistant type**: it runs the agent loop exactly when its published version links a skill whose served version has a tool it does not exclude, else the fixed RAG path; no mode field (admin shows "Använder verktyg"). Assistant versions pin nothing about skills: every assistant uses each linked skill's served version (§3.5, §5.1, §6.1) |

## 12. Milestones

| Phase | Deliverable |
|---|---|
| P2 start | M1 checks (SDK version, real MCP clients, Entra metadata questions); gateway `features` on the catalogue if released |
| P2 a (early) | `/mcp` with `search_knowledge`, `list_collections` (§7) |
| P2 b | `Ume.Rag.Tools` in process (snapshot, MCP client, `client-credentials`, bulkheads, policy re-check, normalisation, rate limits, audit, verify on connect, drift leader); admin: registration, discovery, approvals, pre-registration, grants; template + `ume-mcp-kommun` (mock fee calculator) on `mcp` |
| P2 c | `AgentAnswerStrategy` with prefetch, built-ins, limits, status events, hold-back, citations, `ToolCalls` storage; FakeGateway scripted tool modes; injection suites |
| P2 d | Skills: editor role, draft/publish/serve/rollback, share lists, `SkillVersionFiles`, static and progressive inclusion, skill gate (end to end, advisory); lethal-trifecta validator |
| P2 e | Real tools (O2) in `ume-mcp-kommun` after the data-source inventory |
| P3 | `rag-tools` service; `municipal`/`external` servers, `none`/`api-key` resolvers, per-call connections, `tools-egress`; OBO; write tools with confirmation and risk acceptance; per-skill endpoints, `ask_assistant`, `get_document`, MCP prompts and resources; skill import (with platform-admin approval) and export; assertion layer of the gate; sandbox evaluation for skill scripts |

## 13. Changes to other documents

| Document | Change |
|---|---|
| `archive/decisions-log.md` | row "Tools (step 9)" restated: in-process hub with bulkheads, `hosted` only, one server `ume-mcp-kommun`, served pointer + rollback, static skills ≤ ~4k tokens, advisory skill gate end to end, share lists, lethal trifecta as hard refusal, early `/mcp` with two tools |
| `postponed-decisions.md` M1 | still open: Entra CIMD, RFC 8414 vs OIDC discovery, RFC 8707 `resource`, pre-authorisable client ids, client support for resources/prompts (P3), SDK recheck at P2 start (§15) |
| `postponed-decisions.md` O2 | stopgap: `ume-mcp-kommun` with sample tariffs (§8.5) |
| `archive/area-plans/platform.md` §1, §2 | `Ume.Rag.Tools` is a library (in-process hub in the chat role, also used by the admin; never the worker); no `rag-tools` container in P2; zone `tools-hosted` for hosted MCP servers |
| `archive/area-plans/platform.md` §6 | tool servers: clearance and result level; the agent run's effective level fixed at its start |
| `platform/data-model.md` §1.2 | no `ume_rag_tools` role in P2; `api`: S on `SkillVersionFiles`, `SkillShares`, `ToolDepartmentGrants`, `GatewayModels`, `ToolServers` (no encrypted credential column in P2), I on `audit."ToolCallEvents"`, `audit."ToolDefinitionObservations"`; `worker`: no skill or tool tables; `admin`: S I U D on the new config tables, I on `audit."ToolCallEvents"`, S I on `ToolDefinitionObservations` |
| `platform/data-model.md` §2.1 `Skills`, `SkillVersions` | `Slug` per spec `name` (ASCII, ≤ 64); add `Name`, `CreatedBy`; versions add `License`, `Metadata jsonb`, `ContentHash bytea`, `CreatedBy`, `PublishedBy`, `GateRunId`; `State` `draft`/`published`/`retired` (no `gating`, `gate-failed`); drop `ReferenceBlobPrefix` |
| `platform/data-model.md` §2.1 new | `SkillVersionFiles` (§3.3); `SkillShares` (`SkillId`, `DepartmentId`, `SharedBy`, `SharedAt`); `ToolDepartmentGrants`; no `SkillAudiences` in P2 |
| `platform/data-model.md` §2.1 `AssistantVersions.Skills` | `[{ skillId, excludedToolIds[] }]` per version (replaces `AssistantSkills`); **no** pinned skill version; `SkillVersionLinks` (`SkillVersionId`, `ToolId`) without a kind |
| `platform/data-model.md` §2.1 `ToolServers` | add `Slug varchar(20)` (U); `Classification` → `Clearance`; add `ResultClassification`, `ContainsThirdPartyText bool NOT NULL DEFAULT true`, `MaxConcurrentCalls smallint` (8); `Residency` `OnPrem`/`Eu`/`External` with `CK_ToolServers_Clearance`; `Hosting` = `hosted` in P2 (`municipal`/`external` P3); `SystemOwnerSubjects text[]` (`oid`); add `OwnerTeamId`, `AuthSettings jsonb`, `ProtocolVersion`, `ServerInfo jsonb`, `InstructionsHash bytea`, `LastDiscoveredAt`; `TimeoutMs` default 10,000 (≤ 30,000), `MaxResponseBytes` 262,144; `EncryptedCredential` and `PerCallConnection` P3 |
| `platform/data-model.md` §2.1 `Tools`, `ToolDefinitions`, `ToolApprovals` | `Tools` add `Title`, `AllDepartments`, `CallsPerMinute` (600), `CallsPerMinutePerUser` (30), `State` adds `removed`; `ToolDefinitions` add `Title`, `Annotations jsonb`, `ObservationId bigint NULL`, `PreRegistered bool`, `PreRegisteredBy`; `ToolApprovals` add `Comment`; app rule: two different approvers for new and unexpected definitions, one for pre-registered |
| `platform/data-model.md` §2.1 `Sources` | `ContainsThirdPartyText bool NOT NULL DEFAULT true` (platform admin sets false) |
| `platform/data-model.md` §2.1 `Clients`, `Assistants`, `Settings` | `Clients.ToolPolicy` (`none`/`read`); `AssistantVersions.QuerySteps.agent` as §6.5, agent `contextTokens` 32,768; **no** `Assistants.RiskAcceptance` in P2; `Settings.Tools` = `AgentDefaults`, `AgentHardCaps`, `WriteToolsEnabled` false, `SkillGateModel`, `StaticSkillTokens` 4,000, `CallsPerMinutePerUser` 60 (no `SkillRoutingFloor`) |
| `platform/data-model.md` §2.3, §2.4 | `ToolCalls` as §6.11 (bucket key); `AssistantDailyTotals` add `ToolCalls`, `ToolErrors`, `AgentLimitHits`; `ToolCallEvents` as §9.1; `ToolDefinitionObservations` add `ToolServerId`, `ToolName`, `Kind`, `ToolId` nullable |
| `platform/data-model.md` §5, §9, §11 | `SecurityRelevant` adds `ToolDepartmentGrants`, `SkillShares`, `SkillVersions`; remove the skill-files blob row; Redis keys `rl:tooluser`, `rl:tools`, `rl:mcp` |
| `identity/design.md` §1.3, §11, §12 | `rag-skill-editor` → `EditSkills`, `Read`; RAG's Bicep adds app registration `ume-rag-tools` (certificate credential) and `Tools.Invoke` assignments on each `ume-mcp-*` registration (first: `ume-mcp-kommun`) |
| `identity/design.md` §3, §4.3, §4.7, §8 | no `rag-api`/`rag-admin` → `rag-tools` callers in P2; step 12 = §5.1; effective level fixed at start; `/mcp` only (no per-skill endpoints in P2) |
| `identity/design.md` §10 | 5002 reasons of §9.3; 5012–5015 emitted by api and admin; SIEM rules |
| `chat-api/design.md` §2.2, §7.2–§7.4 | agent effective level fixed at start; neutralised tags of §3.8; RAG skills as `<skill_content>` ≤ 6,000 tokens; lethal trifecta → tools §5.4 (hard refusal); agent prefetch of `search_knowledge` |
| `archive/area-plans/chat-api.md` §3 | agent: limits 8 / 60 s / $0.05, prefetch with the user question, static skills ≤ ~4k tokens |
| `knowledge-api/design.md` §2.2, §4.2, §14 | no `RetrievalRequest.SkillVersionIds`: an assistant searches only its published version's collections, whatever its skills; `/mcp` = `search_knowledge` + `list_collections` only, no `document://`, `get_document`, `ask_assistant` or skill endpoints in P2 |
| `ingestion-worker/design-processing.md` §13.4, §14 | skill gate = end-to-end run on linked models against read-only tools, advisory (no floor, no relative rule, no weekly re-check); `Settings.Tools.SkillGateModel` for first versions |
| `ingestion-worker/design-engine.md` | no `eval` kind `skill-gate` (the gate runs in `rag-admin`); the worker is not on network `mcp` and holds no tool certificate |
| `platform/repo-and-deployment.md` §1, §2.2, §3 | `Ume.Rag.Tools` as a library; no `tools/Ume.Rag.McpGen`, no `Microsoft.OpenApi`; `templates/mcp-server/`, `deploy/compose.mcp.yaml`; settings `Tools:*` (`DriftCheckInterval`, `MaxResultChars`, `MaxArgumentBytes`, `MaxPooledClients`, `AllowWrite=false`), `Agent:*`, `Mcp:CallsPerMinutePerUser`; no `ToolHub:*` settings in P2 |
| `platform/repo-and-deployment.md` §4, §6 | AppHost adds `ume-mcp-kommun`; networks `mcp` (members `rag-api`, `rag-admin`, servers), `mcp-<name>-egress`; no `tools`, `tools-egress` networks in P2; secrets: `ume-rag-tools-entra.pfx` and `Security__ToolAuditKey` for `rag-api` and `rag-admin` |
| `archive/area-plans/admin.md`, `admin/design.md` | done in the same pass (Skills: served pointer, share list, gate report and serve; Tool servers: pre-registration, approvals, grants, drift; no request flow); §15: V3, V4, V11, V12, V14, V18 without skill collections and with the derived mode, "Använder verktyg" |
| `gateway-changes/design.md` §7.3 | optional item: `features` (incl. `tools`) next to `contextWindow` on `/v1/catalog/models` |

## 14. Questions

### 14.1 Answered

User, 2026-10-30 (this step):

1. **Agent loop limits per user message**: defaults **8 tool calls** (including built-ins), **60 s** wall clock,
   **$0.05** model cost; assistants may lower them; platform admins may raise them up to **20 calls / 120 s / $0.25**
   (§6.5).
2. **Skill authors**: department skill editors (`rag-skill-editor`) draft, publish and serve their department's skills;
   publishing runs the skill's test questions through the skill gate (advisory); linking a
   tool needs a department grant; the platform team owns platform skills (§3.6–§3.7).
3. **MCP server template**: **.NET only** (§8.1); the OpenAPI → MCP generator is dropped.

User, 2026-10-30 (simplification pass §15): skills carry know-how and tools, never collections; one assistant type, the
agent loop starting when the published version links at least one tool-bearing skill; assistant versions pin nothing
about skills; the skill gate and the served-pointer flow unchanged (§1 row 21, §3.5, §5.1, §6.1).

User, 2026-10-30 (simplification pass §10): in-process hub with bulkheads; `hosted` only; one first server
`ume-mcp-kommun`; served pointer + rollback; static skills ≤ ~4k tokens; early `/mcp`; share lists;
`ContainsThirdPartyText` and hard trifecta refusal; drift pre-registration; skill gate on linked models end to end; cost
limit fails closed.

Earlier answers this design builds on (tools plan §9, 2026-10-29): only platform admins register tool servers (§4.5);
no anonymous MCP (§7.3); Open WebUI's own tools/MCP always off. Question 1's recommendation is §8.5.

### 14.2 Open (user)

None. Decisions taken here that the user may want to revisit: the skill gate's owner-decides serve with no floor
(§3.5, §3.7); `ContainsThirdPartyText`
labelling municipal tool results `trust="municipal"` (§4.8).

## 16. Deferred or removed (simplification pass)

Per [`../archive/passes/simplification-pass.md`](../passes/simplification-pass.md) §10, §14, §15:

| Removed or deferred | Where it went |
|---|---|
| `rag-tools` service, mTLS hop, `ume_rag_tools` role, `ToolHubClient`, internal routes (§2.4), `tools` network, `ToolHub:*` settings | P3, with the first external, OBO or write tool; `IToolHub` stays the seam |
| `municipal`/`external` hosting, SSRF rules for them, `tools-egress`, `UME_RAG_TOOLS_ALLOW`/`_INTERNAL_ALLOW` | P3 |
| `none` and `api-key` resolvers, `EncryptedCredential` with Data Protection purpose `Ume.Rag.ToolCredential.v1` | P3 |
| Per-call connections, `PerCallConnection` | P3 (servers we do not run, OBO) |
| OpenAPI → MCP generator (`tools/Ume.Rag.McpGen`, `Microsoft.OpenApi`) | Dropped |
| One server per wrapped system, `ume-mcp-avgifter` | One server `ume-mcp-kommun` |
| Pin/follow (`PinnedVersionId`, `PinnedReason`), MAJOR auto-pin, enforced bump rule | Removed; served pointer + rollback, SemVer labels for display |
| Skill version states `gating`, `gate-failed`; blocking skill gate (floor 0.70, relative rule, `SkillRoutingFloor`), routing-only gate without tool execution, gate overrides | Removed; advisory end-to-end gate on linked models, owner serves |
| Weekly re-gate and re-gate on re-approval | Removed; **Kör igen** on demand |
| Progressive loading as the only agent mode | Static ≤ ~4k tokens; progressive above |
| Per-skill MCP endpoints, `SkillAudiences`, `ask_assistant` and its conversations, `get_document`, `document://`, MCP prompts/resources | P3 |
| Risk acceptance (`Assistants.RiskAcceptance`, `assistant.risk_accept`) | P3 with write/OBO tools; hard refusal in P2 |
| Own-department-only skill links | Share lists (`SkillShares`) |
| Two approvals for every definition change | Pre-registration with one approver; two only for new tools and unexpected drift |
| Drift monitor in every hub instance, `DriftWatcher` indirection | One leader (admin); verify on connect everywhere |
| Unknown price "not limited" | Fails closed (§6.5) |
| Hub-unreachable rule (2 failures drop tools) | Replaced by per-server bulkheads and circuits |
| Skill gate as a worker `eval` job, the worker on network `mcp` with the tool certificate | The gate runs in `rag-admin`; the worker has no tool access (§3.7) |
| Tool rate limits always in Redis | `IRateLimitStore`: in process on one host, Redis only when tools run on several hosts (§4.9, §7.6) |
| Collections linked by skills (`SkillVersionLinks` kind `collection`, `collections` in `ume-rag.json`), skill levels over collections, skill collections in retrieval scope (`RetrievalRequest.SkillVersionIds`, `load_skill` widening `search_knowledge`), the assistant `Mode` (`rag`/`agent`) | Removed (§15): skills carry know-how and tools only; an assistant searches its own collections; the agent loop starts when a linked skill carries a tool |
