# Skills, tools and MCP: plan

How assistants gain know-how (skills) and capabilities (tools), how the platform speaks MCP in both directions, and
how municipal MCP servers are built and hosted. Decisions (2026-10-29): skills are **both** know-how packages and
tool bundles; all three MCP roles are in scope; tools are **read-only first**; user-delegated access (Entra OBO) is
**designed now, built later** (`../identity/plan.md` §7).

## 1. Concepts

| Concept | What it is |
|---|---|
| **Tool server** | A registered MCP server (external, or hosted by the municipality), reached over Streamable HTTP |
| **Tool** | One tool on a tool server, individually allowed or not, with a risk level (`read` / `write`) and a pinned definition |
| **Skill** | A versioned package: instructions, reference files, examples, optional linked collections and tools, test questions, classification |
| **Assistant** | Model + persona prompt + skills (+ direct collections). Mode `rag` (fixed steps, no tools) or `agent` (tool loop) |

An assistant is assembled from skills, so know-how is written once and reused: e.g. a skill "Bygglov" (instructions on
how the municipality answers building-permit questions, the bygglov collection, a read-only tool for case status
lookups, 30 test questions) used by both the citizen-facing and the internal building-department assistant.

## 2. Skills

- **Format**: compatible with the Agent Skills format (a folder with `SKILL.md`: name, description, instruction body,
  plus reference files), so skills can be exported to and imported from other tools that support it (format details
  to verify). A platform manifest adds what the format does not cover: linked collections, linked tools,
  classification, owner department, test questions.
- **Progressive loading**: the agent sees only each skill's name and description; the model calls `load_skill` to get
  the full instructions, and the skill's tools become callable once it is loaded. Keeps prompts short and makes
  many skills per assistant affordable. RAG-mode assistants include their skills' instructions statically instead.
- **No executable scripts** in skills at first: running scripts needs a sandbox, which is a separate decision (P3+).
- **Versioning**: immutable published versions with increasing SemVer numbers, plus a separate **served** pointer per
  skill (as in Bifrost), so rollback is moving the pointer; assistants pin a version or follow the served one.
  Publishing runs the skill's test questions through the evaluation gate (`../ingestion-worker/plan.md` §7).
- **Skills are never served unauthenticated** (Bifrost serves its skill marketplace publicly); export requires the
  same access as editing.
- **Ownership**: department-owned; platform skills (e.g. "Klarspråk", "Hänvisa till kontaktcenter") shared by all.

## 3. Tool hub (new service, `Ume.Rag.Tools`)

A small data-plane service between assistants and tool servers, so the Chat API stays lean and no other service holds
tool credentials.

- **Registry**: tool servers, their tools, auth mode, classification, allowed tools, risk levels, timeouts.
- **Pinned definitions**: tool names, descriptions and schemas are stored at approval; if a server changes them, the
  changed tools are disabled until re-approved (protects against tool poisoning and "rug pulls").
- **Policy enforcement** on every call: assistant may use the tool, skill loaded, subject and client allowed, tool
  server classification ≥ the conversation's data (no `internal` data sent to a `public` tool server), risk `read`
  only (until write is enabled), rate limits per user and per tool.
- **Credentials**: service identity (`rag-tools` client credentials or an API key held encrypted) in P2; OBO tokens in
  P3. Credentials never reach the model or the Chat API.
- **Execution**: MCP client (official MCP C# SDK, to verify its version and maturity), timeouts, response size
  limits, output normalised and labelled as **untrusted** before it reaches the model.
- **Audit**: every call recorded (assistant, tool, subject, client, duration, outcome, argument and result sizes,
  hashed arguments); full arguments/results only in the conversation record, under the same 90-day retention.
- **Agent loop limits** enforced by the Chat API with the tool hub: max tool calls per message, wall-clock time,
  and cost per message.

**Lessons from Bifrost** (`../../platforms/bifrost.md`), adopted:

- **Deny by default, narrow by layer**: tool access is the intersection of tool server → skill → assistant → client
  allow-lists; an empty list means nothing, and no layer can widen another. The **same filter** runs when tools are
  listed to the model and when a call arrives, so a hidden tool cannot be called by name (Bifrost
  `core/mcp/exec.go:202-237`).
- **Two lists per tool server**: tools that may run, and the subset that may run **without user confirmation**
  (Bifrost `schemas/mcp.go:825-837`). With read-only first, the second list equals the first; it becomes the
  confirmation mechanism once write tools arrive.
- **Credential resolvers** per auth mode (none, API key, client credentials, OBO), and user-scoped calls use a fresh
  connection per call, so one user's token never reaches another user's request.
- **Upstream MCP server `instructions` are never passed to the model as a system message** (Bifrost does this); they
  are shown to the approving admin only. Tool results get the untrusted label and a size cap (Bifrost has neither).
- **Only the tools of loaded skills are sent**, never everything the caller could reach (Bifrost's default adds all).
- No "governance off" mode: without configuration nothing is allowed. Credentials are always encrypted; startup fails
  without a key.

## 4. Platform as MCP server

Endpoint group `/mcp` in `Ume.Rag.Api` (Streamable HTTP), authorised with Entra (`../identity/plan.md` §6):

| Tool / resource | Purpose |
|---|---|
| `search_knowledge(query, collections?, filters?)` | Knowledge API retrieval with citations |
| `list_collections()` | Collections the user may search through this client |
| `ask_assistant(assistant, question, conversation_id?)` | Run an assistant and return the answer with sources |
| Resource `document://{id}` | Document metadata and the cited text |

Everything is capped by the MCP client's classification ceiling, like any other client: **`public` by default**,
because the MCP client's own model receives the results outside our residency rules; a platform admin may raise a
vetted client to `internal`, never to `sensitive` (`../identity/plan.md` §5). This lets Copilot, Claude, VS Code and
agent frameworks use municipal knowledge without their own RAG.

**Per-skill endpoints** (after Bifrost's "virtual MCPs"): each published skill can also be exposed at
`/mcp/skills/<slug>`, offering only that skill's knowledge search and tools. A department can give an MCP client
exactly "Bygglov" and nothing else, with the same authorisation.

## 5. Hosting municipal MCP servers

The platform hosts in-house MCP servers that wrap internal systems (case management, GIS, booking, the
municipality's open data), without becoming a general code runner:

- **Template** (`templates/mcp-server/`): .NET project with the MCP C# SDK, Entra token validation (`aud` = the
  server's own app registration), OpenTelemetry, health endpoints, read-only tool conventions, contract tests and a
  Dockerfile.
- **OpenAPI → MCP generator** (after Bifrost): many municipal systems already have OpenAPI descriptions; a generator
  produces a server from one, marking `GET` operations `read` and everything else `write` (disabled until write tools
  are allowed). The generated tool list still goes through normal review and pinning.
- **Deployment**: each server is its own container in the platform's compose/Kubernetes setup, on an internal
  network reachable only by the tool hub, with egress only to the system it wraps.
- **Registration**: deployed servers are registered in the tool hub like external ones (same review, pinning and
  classification); a catalogue page in the admin shows owner, systems wrapped and tools.
- **Ownership**: the team owning the wrapped system owns its MCP server; the platform team owns the template and the
  hosting conventions.

## 6. Security: prompt injection and data flow

Retrieved web pages, documents and tool results are untrusted text that may contain instructions. Rules:

1. **Untrusted labelling**: retrieved content and tool output are delimited and labelled as data in the prompt;
   system prompts tell the model not to follow instructions in them. (Necessary, not sufficient.)
2. **No "lethal trifecta" without a decision**: the admin refuses an assistant configuration that combines (a) access
   to non-public data, (b) untrusted content (external crawls, external tool servers), and (c) any channel that can
   send data out (write tools, tools that call external services with model-chosen arguments, fetching model-chosen
   URLs), unless a platform admin records an explicit risk acceptance.
3. **Data-flow classification**: tool arguments can carry conversation data, so a tool server may only be used by
   assistants whose classification is ≤ the tool server's classification.
4. **Output channels**: external images and auto-loaded links are stripped from answers (common exfiltration path).
5. **Write tools (later)**: always behind explicit user confirmation shown by the client (MCP elicitation or our own
   UI), with idempotency keys and audit; never available to `public` assistants.
6. **Residency**: the residency matrix (`../platform/plan.md` §6) applies to tool servers too: `sensitive`
   assistants only use on-prem models and on-prem tool servers; `internal` assistants only on-prem or EU tool servers.

## 7. First tools (recommendation)

**Rule of thumb: documents are knowledge, not tools.** Anything that can be crawled or synced (meeting records,
policies, web pages) goes into collections. Tools are for answers that are **live, per entity, or computed**, where an
index would be stale or wrong.

Selection criteria for the first tools: read-only; service identity (no OBO); public or internal data, no personal
data; an existing API; frequent questions; and a clear gain over plain retrieval.

| # | Tool | Answers | Why first | Data source (to confirm) |
|---|---|---|---|---|
| 1 | **Fee calculator** (`calculate_fee`) | "What does a building permit for a 25 m² extension cost?" from the published fee tables (taxa) | Pure logic, no external system: the ideal **reference server** for the MCP template. LLMs are unreliable at arithmetic on tariff tables. Public data. | Published taxa tables, maintained as data by the owning department |
| 2 | **Address lookup** (`lookup_address`) | Property designation, applicable detaljplan (link and status), school catchment area, district, nearest recycling station for an address | Very common citizen and contact-centre questions; per-address answers cannot be indexed sensibly; public data | Municipal GIS services (WFS / OGC API Features); Lantmäteriet only if an agreement exists |
| 3 | **Organisation and contact directory** (`find_contact`) | Which unit handles a matter, phone numbers, opening hours, visiting addresses | Changes often, so crawled copies go stale; wrong phone numbers are a visible failure; public/internal | Contact-centre directory or the website CMS's API |

Built-in tools in every agent assistant: `search_knowledge`, `load_skill`, and `current_date` (dates and working days;
models get these wrong without it).

**Pilot fit**: a contact-centre (kontaktcenter) assistant would use all three plus the website collection, which makes
it a strong pilot candidate for P2.

**Next, with OBO (P3)**: "my case status" in the case system, room and resource booking (read first), the user's own
SharePoint documents.

**Not first**: anything that writes; person lookups or case contents (personal data); generic web fetch (an
exfiltration channel, see §6).

## 8. Milestones

| Phase | Deliverable |
|---|---|
| P1 | Client registry only (needed for ceilings); P1 assistants have a prompt and collections, no skills (decision) |
| P2 (skills) | Skills and the Skills admin page, together with agent assistants |
| P2 | Tool hub with read-only tools and service identity; agent assistants with progressive skill loading; platform MCP server (`search_knowledge`, `list_collections`, `ask_assistant`); MCP server template with a **mock** fee calculator (sample data) as the reference server; real tools (§7) after the data sources are inventoried |
| P3 | Entra OBO for user-delegated tools; write tools with confirmation; skill import/export; more hosted MCP servers; evaluation of a sandbox for skill scripts |

## 9. Open questions

1. **First tools**: start with **one mock tool server** (a fee calculator with sample tariff data) as the reference for
   the template, the tool hub and agent assistants. The three real tools in §7 are inventoried and built later.
Answered (2026-10-29):

2. **Registration and approval**: only platform admins register tool servers (departments request them). A new
   server or a changed tool definition needs two approvals, recorded in the audit log: the platform admin (security,
   classification) and the owner of the wrapped system (correctness of the tools).
3. **Anonymous MCP**: no. The MCP server always requires Entra login; citizens reach public assistants through the web
   widget.
4. **Open WebUI's own tools/MCP**: always off. Every tool call goes through the tool hub.
