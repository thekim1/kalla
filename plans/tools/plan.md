# Skills, tools and MCP: plan

How assistants gain know-how (skills) and capabilities (tools), how the platform speaks MCP, and how municipal MCP
servers are built and hosted. Decisions (2026-10-29): skills are **both** know-how packages and tool bundles; tools are
**read-only first**; user-delegated access (Entra OBO) is **designed now, built later** (`../identity/plan.md` §7).
Simplified on 2026-10-30 ([`../simplification-pass.md`](../simplification-pass.md) §10, §15): a smaller P2 with the
tool hub in process, `hosted` servers only and one first server; skills without collections; one assistant type.
Detail: [`design.md`](design.md) (detail step 9).

## 1. Concepts

| Concept | What it is |
|---|---|
| **Tool server** | A registered MCP server hosted by the municipality (P2; servers run by others in P3), reached over Streamable HTTP |
| **Tool** | One tool on a tool server, individually allowed or not, with a risk level (`read` / `write`) and a pinned definition |
| **Skill** | A versioned package of know-how and tools: instructions, reference files, examples, optional linked tools, test questions, classification; **never collections** |
| **Assistant** | Model + persona prompt + its own collections + skills. One type: the fixed RAG steps, or the tool loop (P2) when a linked skill carries tools (derived, shown as "Använder verktyg") |

An assistant is assembled from skills, so know-how is written once and reused: e.g. a skill "Bygglov" (instructions on
how the municipality answers building-permit questions, the fee calculator tool, 30 test questions) used by both the
citizen-facing and the internal building-department assistant, each of which links the bygglov collection itself:
what an assistant knows is always its own collection links, shown in one place.

## 2. Skills

- **Format**: compatible with the Agent Skills format (a folder with `SKILL.md`: name, description, instruction body,
  plus reference files), checked against the spec (design §3.1). A platform manifest (`ume-rag.json`) adds linked
  tools, classification, owner department; test questions in `evals/evals.json` (the spec's eval
  convention). `allowed-tools` is ignored: tool access comes only from manifest links and the tool policy.
- **Storage**: everything in `config` (instructions, manifest, test questions, text reference files in
  `SkillVersionFiles`); no blobs (design §3.3).
- **Static first**: when an assistant's skills total ≤ about **4k tokens** they are included in the system prompt and
  their tools are visible from the first step; only above that does the agent see a catalogue (name + description) and
  call `load_skill` (design §3.8–§3.9). Assistants without tools (the RAG path) always include their skills statically.
- **No executable scripts**: `scripts/` is refused on save and import; running scripts needs a sandbox (P3+).
- **Lifecycle**: immutable published versions with SemVer labels (for display); one **served** pointer per skill;
  every assistant linking the skill uses the served version; rollback is moving the pointer (design §3.5). Publishing
  runs the **skill gate** (design §3.7) in `rag-admin`: the skill's test questions end to end on the linked assistants'
  models against read-only tools; the report is advisory and the skill's owner serves the version. Assistant versions
  pin nothing about skills: a version names its skills, and every assistant always uses each skill's served version.
  Serving a version that adds a skill's first tool switches the linking assistants to the agent loop, so serve checks
  those assistants too.
- **Sharing**: department skills are usable by their own department and by departments on the skill's **share list**;
  platform skills (e.g. "Klarspråk", "Hänvisa till kontaktcenter") by all. Sharing never grants tools (design §3.6).
- **Never served unauthenticated**; export requires the same access as editing (P3).
- **Ownership**: department-owned, edited and published by `rag-skill-editor` (own departments); platform skills owned
  by the platform team (answered 6).

## 3. Tool hub (`IToolHub`, in process in P2)

The tool hub runs **in the chat role** of `Ume.Rag.Api` behind `IToolHub`, with **bulkheads** per server (concurrency
limit, timeout, circuit breaker), so a slow server cannot exhaust the chat role (design §2). `rag-admin` uses the same
library for discovery, the test panel and the skill gate; the worker has no tool access. A separate `rag-tools`
service (mTLS hop, own database role, own snapshot consumer) comes only with the first external, OBO or write tool
(P3); `IToolHub` is the seam.

- **Registry**: tool servers, their tools, two levels (below), allowed tools, risk levels, timeouts. Hosting kind
  **`hosted`** only in P2; tools are usable by a department only with a **department grant** (design §4.5).
- **Pinned definitions**: tool names, descriptions and schemas are stored at approval; a change disables the changed
  tools until approved. Owners **pre-register** the definition of a planned release (one approver); **unexpected**
  drift needs two-person re-approval. Drift polling runs under one leader; hosted servers are also verified on every
  connection (design §4.4–§4.5).
- **Policy enforcement** on every call: assistant may use the tool, skill included or loaded, department grant,
  subject and client allowed, server **clearance** ≥ the conversation's data and **result level** ≤ the assistant's
  level (design §5.3), risk `read` only, rate limits per user and per tool.
- **Credentials**: service identity (Entra client credentials with the `ume-rag-tools` certificate, held by `rag-api`
  and `rag-admin` only) in P2; OBO tokens in P3.
  Credentials never reach the model.
- **Execution**: MCP client (official MCP C# SDK 2.3.0; recheck at P2 start, design §15), timeouts, response size
  limits, output normalised and labelled as **untrusted** before it reaches the model.
- **Audit**: every call recorded (assistant, tool, subject, client, duration, outcome, sizes, keyed hash of the
  arguments); full arguments/results only in the conversation record, under the same 90-day retention.
- **Agent loop limits** per user message: **8 tool calls, 60 s, $0.05**; assistants may lower, platform admins raise to
  ≤ 20 / 120 s / $0.25 (answered 5). An unknown model price **fails closed** (design §6.5).
- **Where the loop lives**: the P2 `AgentAnswerStrategy` behind `IAnswerStrategy` (`../chat-api/design.md` §2.2), in
  `Ume.Rag.Answering`. It yields the same `AnswerEvent`s as the RAG strategy plus `ToolCall` events, so residency
  checks, output filter, citations and the writer are reused; `search_knowledge` is **prefetched** with the user
  question as on the RAG path, and the effective level is fixed at the start of the run and never rises (design §5.3).

**Lessons from Bifrost** (`../../platforms/bifrost.md`), adopted:

- **Deny by default, narrow by layer**: tool access is the intersection of tool server → skill → assistant → client
  allow-lists; the **same filter** runs when tools are listed to the model and when a call arrives (Bifrost
  `core/mcp/exec.go:202-237`).
- **Two lists per tool server**: tools that may run, and the subset that may run **without user confirmation**
  (Bifrost `schemas/mcp.go:825-837`). With read-only first, the second list equals the first.
- **Upstream MCP server `instructions` are never passed to the model**; they are shown to the approving admin only.
  Tool results get the untrusted label and a size cap.
- **Only the tools of included or loaded skills are sent**, never everything the caller could reach.
- No "governance off" mode: without configuration nothing is allowed; startup fails without the credential.

## 4. Platform as MCP server

Endpoint group `/mcp` in `Ume.Rag.Api` (stateless Streamable HTTP), authorised with Entra (`../identity/plan.md` §6),
**early in P2**, ahead of the tool hub:

| Tool | Purpose |
|---|---|
| `search_knowledge(query, collections?, top_k?, document_types?, modified_after?)` | Knowledge API retrieval with citations; schema and output shaping in knowledge design §14 (`top_k` ≤ 20, no section expansion, each text ≤ 2,000 and total ≤ 24,000 characters, results wrapped as untrusted `<source>` blocks plus `structuredContent` with `coverage`) |
| `list_collections()` | Collections the user may search through this client |

Everything is capped by the MCP client's classification ceiling: **`public` by default**, because the MCP client's own
model receives the results outside our residency rules; a platform admin may raise a vetted client to `internal`,
never to `sensitive` (`../identity/plan.md` §5). Citation download links are signed grants bound to the MCP client with
a lifetime of **1 hour** (knowledge design §4.6, §14). Tool-hub tools are not offered over `/mcp` in P2.

## 5. Hosting municipal MCP servers

- **Template** (`templates/mcp-server/`), **.NET only** (answered 7): MCP C# SDK (stateless), Entra token validation
  (`aud` = the server's own app registration, app-only tokens with app role `Tools.Invoke` assigned only to the
  platform's tool identity), OpenTelemetry, health endpoints, read-only conventions, golden `tools/list`, contract
  tests and a Dockerfile.
- **First server**: the first tools live in **one** server **`ume-mcp-kommun`** (one container, one registration),
  starting with the **mock fee calculator**; later first tools (§7) are added to it.
- **Deployment**: each server is its own container on an internal network (`mcp`) reachable only by the processes
  that run the hub, with egress only to the systems it wraps (design §8.3).
- **Registration**: deployed servers are registered like any server (review, pinning, classification); a catalogue
  page in the admin shows owner, systems wrapped and tools.
- **Ownership**: the platform team owns `ume-mcp-kommun` and the template; each tool has a system owner (the team
  owning its data or wrapped system) who approves its definition.

## 6. Security: prompt injection and data flow

Retrieved web pages, documents and tool results are untrusted text that may contain instructions. Rules:

1. **Untrusted labelling**: retrieved content and tool output are delimited and labelled as data in the prompt;
   system prompts tell the model not to follow instructions in them. (Necessary, not sufficient.) Sources and tool
   servers carry **`ContainsThirdPartyText`** (default true, set at approval), which drives the labelling.
2. **No "lethal trifecta"**: the admin **refuses** an assistant configuration that combines (a) access to non-public
   data, (b) third-party text (`ContainsThirdPartyText`), and (c) any channel that can send data out. A hard refusal in
   P2; risk acceptance comes with write and OBO tools (P3).
3. **Data-flow classification**: an assistant's level ≤ the tool server's clearance (arguments carry conversation
   data) and ≥ its result level (results flow into the answer) (design §5.3).
4. **Output channels**: external images and auto-loaded links are stripped from answers.
5. **Write tools (P3)**: always behind explicit user confirmation, with idempotency keys and audit; never available to
   `public` assistants.
6. **Residency**: the residency matrix (`../platform/plan.md` §6) applies to tool servers: `sensitive` assistants only
   on-prem tool servers; `internal` only on-prem or EU.
7. **Imported skills** (P3) need a human platform-admin approval before they can be published.

## 7. First tools (recommendation)

**Rule of thumb: documents are knowledge, not tools.** Anything that can be crawled or synced goes into collections.
Tools are for answers that are **live, per entity, or computed**, where an index would be stale or wrong.

Selection criteria: read-only; service identity (no OBO); public or internal data, no personal data; an existing API;
frequent questions; a clear gain over plain retrieval. All three live in `ume-mcp-kommun`.

| # | Tool | Answers | Why first | Data source (to confirm) |
|---|---|---|---|---|
| 1 | **Fee calculator** (`calculate_fee`) | "What does a building permit for a 25 m² extension cost?" from the published fee tables (taxa) | Pure logic, no external system; LLMs are unreliable at arithmetic on tariff tables. Public data. Starts as a **mock** with sample tariffs | Published taxa tables, maintained as data by the owning department |
| 2 | **Address lookup** (`lookup_address`) | Property designation, detaljplan, school catchment area, district, nearest recycling station | Common citizen and contact-centre questions; per-address answers cannot be indexed sensibly | Municipal GIS services (WFS / OGC API Features) |
| 3 | **Organisation and contact directory** (`find_contact`) | Which unit handles a matter, phone numbers, opening hours, visiting addresses | Changes often, so crawled copies go stale | Contact-centre directory or the website CMS's API |

Built-in tools in every assistant that uses tools: `search_knowledge`, `current_date`, and `load_skill` when skills exceed the
static budget.

**Pilot fit**: a contact-centre assistant would use all three plus the website collection (P2 candidate).
**Next, with OBO (P3)**: "my case status", room booking (read first), the user's own SharePoint documents.
**Not first**: anything that writes; person lookups or case contents; generic web fetch (an exfiltration channel).

## 8. Milestones

| Phase | Deliverable |
|---|---|
| P1 | Client registry only (needed for ceilings); P1 assistants have a prompt and collections, no skills (decision) |
| P2 (early) | `/mcp` with `search_knowledge` and `list_collections` (client ceiling `public`) |
| P2 | In-process tool hub with bulkheads, read-only tools, service identity, `hosted` servers; `ume-mcp-kommun` with the **mock** fee calculator; skills (static and progressive), share lists, skill gate; the agent loop for assistants with tool-bearing skills; real tools (§7) after the data sources are inventoried |
| P3 | `rag-tools` as a separate service (with the first external, OBO or write tool); `municipal`/`external` servers; Entra OBO; write tools with confirmation and risk acceptance; per-skill MCP endpoints, `ask_assistant`, MCP prompts and resources; skill import/export; sandbox evaluation for skill scripts |

## 9. Open questions

1. **First tools**: start with **one mock tool server** (`ume-mcp-kommun` with a fee calculator on sample tariff data)
   as the reference for the template, the tool hub and the agent loop. The real tools in §7 are inventoried and
   built later.

Answered (2026-10-29):

2. **Registration and approval**: only platform admins register tool servers; departments request them through the
   platform team's normal intake (no request flow in the admin). A new tool definition needs two approvals, recorded in
   the audit log: the platform admin (security, classification) and the system owner (correctness). A planned change
   pre-registered by the owner needs one approver; unexpected drift needs two (simplification pass §10).
3. **Anonymous MCP**: no. The MCP server always requires Entra login; citizens reach public assistants through the web
   widget.
4. **Open WebUI's own tools/MCP**: always off. Every tool call goes through the tool hub.

Answered (2026-10-30; detail in design.md §14.1):

5. **Agent loop limits** per user message: 8 tool calls (built-ins included), 60 s until the final answer starts, $0.05
   model cost; assistants may lower; platform admins raise up to 20 / 120 s / $0.25.
6. **Skill authors**: department skill editors (`rag-skill-editor`) draft, publish (skill gate, advisory) and serve;
   linking a tool needs a department grant for it; the platform team owns platform skills.
7. **MCP server template**: .NET only.

## 10. Deferred or removed (simplification pass)

Per [`../simplification-pass.md`](../simplification-pass.md) §10, §15:

| Item | Where it went |
|---|---|
| `rag-tools` as a separate service, mTLS hop, own DB role | P3, with the first external, OBO or write tool; `IToolHub` in process in P2 |
| `municipal` and `external` hosting, `none`/`api-key` resolvers, per-call connections, `tools-egress` | P3 |
| OpenAPI → MCP generator | Dropped (none of the first tools has an OpenAPI source) |
| One server per wrapped system | First tools in one server `ume-mcp-kommun` |
| Pin/follow of skill versions, MAJOR auto-pin, weekly re-gate | Removed; served pointer + rollback |
| Per-skill MCP endpoints, `SkillAudiences`, `ask_assistant`, `get_document`, MCP prompts and resources | P3 |
| Progressive loading as the only mode | Static inclusion ≤ ~4k tokens; `load_skill` only above |
| Risk acceptance for the lethal trifecta | P3 (with write/OBO tools); hard refusal in P2 |
| Own-department-only links for skills | Replaced by share lists |
| Two approvals for every definition change | Only for unexpected drift; planned changes pre-registered with one approver |
| Collections linked by skills (`SkillVersionLinks` kind `collection`, `collections` in `ume-rag.json`) and the `rag`/`agent` assistant mode | Removed (§15): skills carry know-how and tools only; the mode is derived from tool-bearing skills |
