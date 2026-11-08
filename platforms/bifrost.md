# Bifrost (maximhq/bifrost): MCP, skills and tool governance analysis

> Background research from 2026-10-29, kept for its evidence. It does not bind: the Decisions table and the design
> documents decide. Facts are as of the commits named below and are not kept up to date.

Analysed: shallow clone `~/git/rag-platforms/bifrost`, HEAD `a09cea0` (2026-10-29). Paths are relative to the repo
root. Facts are **verified** (read in code or docs in the repo, or stated in an issue) unless marked *inferred*.
Focus: what the municipality's tool hub (`plans/tools`), identity plan (`plans/identity`) and LLM gateway
(`plans/gateway-changes`) can learn. Bifrost is the closest analogue so far to "our LLM gateway, plus MCP".

## 1. Summary

- **What it is**: an LLM gateway (OpenAI-compatible API over ~23 providers, virtual keys, budgets, rate limits,
  routing, fallbacks, semantic cache, logging), which has grown into an **MCP gateway** (MCP client to upstream
  servers, MCP server to clients, OAuth broker, tool governance, code-mode sandbox), an A2A agent proxy
  (`core/agent/`) and a **skills repository** (an Agent Skills marketplace for coding harnesses).
- **Stack**: Go. `core/` (provider adapters, request engine, MCP manager; `core/bifrost.go` is 11.4k lines,
  `core/mcp/clientmanager.go` 4.1k), `framework/` (config store on GORM with SQLite/Postgres, log store incl.
  ClickHouse, OAuth2, encryption), `plugins/` (governance, logging, otel, semantic cache, …), `transports/bifrost-http`
  (fasthttp server, handlers), `ui/` (Next.js). MCP library: `mark3labs/mcp-go v1.3.0` (`core/go.mod`). About 630k
  lines of non-test Go.
- **Licence**: `LICENSE` is Apache-2.0 for everything in the repo. **Enterprise is a separate, closed build**
  (*inferred* from code comments: "populated by enterprise middleware", `framework/configstore/tables/mcpoauth2.go:220`;
  "the DAC scoping logic lives in enterprise", `tables/virtualmcp.go:34-35`; feature flags with `EnterpriseOnly`,
  `framework/featureflags/registry.go:21-35`). No enterprise source is in the clone.
- **OSS vs enterprise for the features in scope** (from `docs/`):

  | Feature | OSS | Enterprise only |
  |---|---|---|
  | MCP client (HTTP/SSE/STDIO/in-process/OpenAPI), tool filtering, agent mode, code mode | yes | |
  | MCP server `/mcp`, `/mcp/<slug>`, OAuth 2.1 AS for MCP clients (VK or session binding) | yes | "user" consent mode (needs SSO/SCIM), `docs/mcp/gateway-auth.mdx:98` |
  | Virtual MCPs (tool groups per virtual key) | yes (needs governance) | access-profile, DAC, project scoping (`docs/mcp/virtual-mcps.mdx:255-264`) |
  | Upstream auth: none / headers / shared OAuth / per-user OAuth / per-user headers | yes | user-mode identity for them |
  | **Token exchange (OBO / RFC 8693)** and Cross App Access (ID-JAG) | | yes (`docs/mcp/auth/token-exchange.mdx:19`, `cross-app-access.mdx:26`) |
  | Skills repository + marketplace serving | yes | |
  | SSO/OIDC login, RBAC, audit logs of config changes, guardrails, clustering | | yes (`docs/enterprise/overview.mdx`) |

- **Maturity**: very active and fast-moving: repo created 2025-03, ~9.2k stars, releases per plugin/module, 46 open
  issues with "mcp" in the title. Code is heavily commented (often with the "why"), with very many targeted tests
  (`core/mcp/` has ~45 test files). MCP features churn quickly (renames: tool groups → Virtual MCPs,
  `allow_on_all_virtual_keys` → `AllowByDefault`, `core/schemas/mcp.go:863-868`).

## 2. MCP implementation

### 2.1 Client side (Bifrost → upstream MCP servers)

- **Registry entry** = `MCPClientConfig` (`core/schemas/mcp.go:788-868`): connection type (HTTP, SSE, STDIO,
  in-process Go server, **OpenAPI**), auth type, `ToolsToExecute` (allow-list, deny-by-default: nil/`[]` = no tools,
  `:825-830`), `ToolsToAutoExecute` (subset that agent mode may run unattended, `:831-837`), `PromptsToExpose`,
  `ResourcesToExpose`, `AllowedExtraHeaders` (`:823`), per-client timeout and instruction cap (`:858-859`),
  `ToolPricing` (`:860`), `RequirePublicTarget` (SSRF guard for clients registered without admin auth, `:855`),
  `AllowByDefault` (open to every caller with no explicit assignment, `:863`).
- **Connections**: shared persistent connections with a background connection checker, or **per-call ephemeral
  connections** (default for new HTTP clients and always for per-user auth: `core/mcp/credstore/credstore.go:83-125`).
  Upstream `tools/list_changed` is subscribed to and triggers re-listing (`core/mcp/toolslistchanged.go:13-19`).
- **Tool naming**: tools are exposed as `<client>-<tool>`; filters use `client-tool` or `client-*`
  (`core/schemas/bifrost.go:277`).
- **OpenAPI → MCP** (beta): paste a Swagger/OpenAPI spec, pick operations, each becomes a tool, executed in-process as
  HTTP calls; `GET/HEAD` annotated read-only, `DELETE` destructive, credentials from config never from the model,
  responses capped at 64 KiB (`docs/mcp/openapi-servers.mdx`, `framework/openapimcp/`).
- **Server instructions**: upstream MCP servers' `instructions` are aggregated, each wrapped in
  `<mcp_server name="…">` blocks with tag-forging neutralised (`core/mcp/instructions.go:86-165`), and **prepended as
  a system message** to every MCP-bearing chat request (`core/mcp/toolmanager.go:600-640`).

### 2.2 Server side (Bifrost as MCP server)

- `/mcp` serves the union of tools the caller may reach; `/mcp/<slug>` serves one Virtual MCP or one client
  (`transports/bifrost-http/handlers/mcpserver.go:601-682`). Admission is a single function: authenticate, ask
  governance what the credential grants, stamp the allowed tool list on the context; tools/list and tools/call both
  read that list, so listing and calling are refused by the same rule (`mcpserver.go:592-626`, `core/mcp/exec.go:202-237`).
- **Inbound auth** (`docs/mcp/gateway-auth.mdx`): mode `headers` (virtual key / API key), `oauth` (Bifrost-issued
  JWTs) or `both`. In OAuth mode **Bifrost is its own authorisation server** with RFC 9728 metadata, **Dynamic Client
  Registration** (RFC 7591), PKCE and RFC 8707 resource binding. At consent the user binds the grant to a virtual
  key, an anonymous session, or (enterprise) their SSO user. Exactly one credential type per request; mixing is
  rejected. Origin allow-list against DNS rebinding (`mcpserver.go:721+`).

### 2.3 Filtering (what a request may see and call)

Layered, narrowing only (`core/mcp/utils.go:149-210`, `core/mcp/exec.go:202-237`):
1. client disabled? → nothing; 2. client in the request's include list; 3. client-level `ToolsToExecute`;
4. request-level include list. The request list comes from the virtual key's MCP configs and Virtual MCPs
(`plugins/governance/mcp.go:20-195`), the `/mcp/<slug>`, or caller headers `x-bf-mcp-include-clients/-tools`
(`transports/bifrost-http/lib/ctx.go:247`); a caller list is intersected with the key's grant, never widens it
(`mcpserver.go:701-717`). **Execution re-checks the same filters**, so a hidden tool cannot be called by name
(`exec.go:202-205`) and governance checks the permit again in `PreMCPHook` (`plugins/governance/main.go:1774`).

### 2.4 Execution modes

- **Tool injection**: by default every chat/responses request gets **all tools the caller may reach appended** to
  `tools` (`toolmanager.go:481-596`); `mcp_disable_auto_tool_inject` makes it opt-in via include headers (`:487-495`).
- **Manual (default)**: model returns tool calls to the caller; the caller executes them via Bifrost's tool-execute
  endpoint and continues the conversation (`docs/mcp/tool-execution.mdx`). "Approval" is the caller's responsibility.
- **Agent mode**: Bifrost loops itself; tools in `ToolsToAutoExecute` run **in parallel** inside the gateway
  (`core/mcp/agent.go:293-330`), others are returned to the caller for approval; max depth 10, tool timeout 30 s by
  default (`core/schemas/mcp.go:480-481`). Entered from the core chat path (`core/bifrost.go:1065-1071`).
- **Code mode**: replaces N tool definitions with 4 meta-tools (`listToolFiles`, `readToolFile`, `getToolDocs`,
  `executeToolCode`); the model writes Starlark that calls tools inside an in-process sandbox with source-size and
  time limits (`core/mcp/codemode/starlark/executecode.go:208-232`). Nested calls go through the same plugin
  pipeline and, when unattended, are limited to auto-execute tools (`core/mcp/utils.go:995-1003`). Claimed
  58-93 % fewer input tokens at 96-508 tools (`docs/mcp/code-mode.mdx`, vendor benchmark).

### 2.5 Upstream auth, OAuth and identity forwarding

Six auth types, one resolver each (`core/mcp/credstore/credstore.go:47-59`, `core/schemas/mcp.go:545-550`):

| Type | Who the upstream sees | Storage |
|---|---|---|
| `none`, `headers` | Bifrost (static secret) | config (secret refs: env/vault) |
| `oauth` | Bifrost (admin authorises once) | token row, refresh worker |
| `per_user_oauth` | each user (consent flow, URL returned in-band as a tool result) | per-identity token rows |
| `per_user_headers` | each user (user submits API key on a Bifrost page) | `mcp_per_user_header_credentials` |
| `token_exchange` | **each user, via OBO/RFC 8693**, no stored per-user credential | in-memory cache for token lifetime |

- **Token exchange** (`core/mcp/credstore/token_exchange.go:13-71`, `framework/oauth2/tokenexchange.go:116`):
  the caller's IdP access token is exchanged for a token with the MCP server's audience; always per-call
  connections; clear typed errors (token missing / rejected / sign-in expired). The config documents the **Entra OBO
  constraint**: Entra only accepts an assertion whose audience is the exchanging app, so the exchange must use the
  login app's own credentials (`UseIdPCredentials`, `core/schemas/mcp.go:575-588`). Offboarding takes effect when
  the cached token expires (`docs/mcp/auth/overview.mdx:55`). A retained **admin bootstrap token** is used for
  periodic tool discovery (`token_exchange.go:122-135`).
- **Identity keying for per-user creds**: `user` > `vk` > `session`, where session is "**any opaque value asserted
  by the caller** via `x-bf-mcp-session-id`" (`core/schemas/bifrost.go:359`, `docs/mcp/auth/overview.mdx:91-99`).
- **Header forwarding**: caller headers on a combined allow-list are collected (`lib/ctx.go:713-715`) and forwarded
  to an MCP server only if in that server's `AllowedExtraHeaders` (`core/mcp/utils/utils.go:171-193`). There is **no
  signed identity header**: forwarded user ids are caller-asserted. Real user identity reaches upstreams only via
  per-user OAuth or token exchange.
- **Encryption**: per-user header values and OAuth tokens are encrypted with `BIFROST_ENCRYPTION_KEY`, but **stored
  in plaintext when no key is configured** (`framework/configstore/tables/mcpheaders.go:94-110`,
  `tables/mcpoauth2.go:143-159`).

## 3. Skills implementation

- **What a skill is here**: an [Agent Skills](https://agentskills.io) package (`SKILL.md` frontmatter + body +
  files). Tables `skills`, `skill_versions`, `skill_files` (`framework/configstore/tables/skills.go:85-150`): name
  (immutable), description, licence, compatibility, `allowed_tools` (free text), metadata, extra frontmatter, body;
  versions are **immutable SemVer snapshots**, monotonic (cannot publish lower than the highest), with a separately
  chosen **served version** ("Save" vs "Save & Serve", rollback by shifting the served version)
  (`docs/features/skills-repository.mdx`). Files: upload / text / data URL / **live URL fetched at serve time**;
  blobs deduplicated across versions.
- **How it is used**: **not at all in the LLM or MCP request path.** No code in `core/` loads skills into prompts
  (only `core/agent/manager.go:455-889` mentions skills, and those are A2A agent-card skills). Skills are
  *distributed*: Bifrost builds in-memory git repos and serves them as a **plugin marketplace for Claude Code,
  Codex and Claude Desktop/Cowork**, plus zip downloads (`transports/bifrost-http/handlers/skills_serving.go:172-200`).
  Progressive disclosure is left entirely to the harness.
- **Access**: management is permission-gated, but **serving/marketplace endpoints are public** without credentials
  (`handlers/middlewares.go:1739-1742`; doc note: "Marketplace and download URLs are public").
- **No link between skills and tools**: `allowed_tools` is passed through as frontmatter; Bifrost does not bind a
  skill to MCP clients/tools or enforce it.

## 4. Governance & security for tools

- **Keys/teams**: virtual keys get MCP grants per client (`governance_virtual_key_mcp_configs`,
  `tables/virtualkey.go:206-217`) and Virtual MCPs (named tool bundles, served at `/mcp/<slug>`,
  `tables/virtualmcp.go:42`). Clients are deny-by-default unless `AllowByDefault`.
- **Budgets**: tool calls count as requests against VK rate limits and their `ToolPricing` cost against VK budgets
  (`plugins/governance/main.go:1830-1869`, `framework/mcpcatalog/main.go`).
- **Audit**: one `MCPToolLog` row per call with VK, user, team, customer, business unit, project, user agent/app,
  linked LLM request id, session, latency, cost, status, **full arguments and results** (`framework/logstore/tables.go:1923-1975`);
  content logging can be disabled. Config-change audit logs are enterprise.
- **Approvals of tool definitions**: **none**. Discovered tools are persisted and re-served automatically when they
  change (`transports/bifrost-http/server/server.go:3765-3768`); the discovery hash only de-duplicates the change
  callback (`core/mcp/mcp.go:117-135`). An allow-list by tool *name* survives a changed description/schema.
- **Injection defences**: server-instruction blocks are delimited and tag-escaped (`instructions.go:155-165`); tool
  results are passed to the model as plain content with **no untrusted labelling and no size cap** in core
  (`core/mcp/toolresult.go:13-56`; only OpenAPI tools cap at 64 KiB). Write-tool protection = keep them out of
  `ToolsToAutoExecute` and approve in the caller. Guardrails (content filters, redaction) are enterprise.
- **SSRF**: outbound MCP/skill fetches use an SSRF-guarded client; clients registered without admin auth must
  resolve to public addresses (`core/mcp/clientmanager.go:30, 3534`; `schemas/mcp.go:855`).

## 5. Architecture: where tools run

- **MCP is inside the gateway data plane.** `MCPManager` is a field of the core engine (`core/bifrost.go:150, 429`);
  tools are injected in the core request path (`core/bifrost.go:6468, 6759`), agent loops wrap the chat call
  (`:1065`), and tool calls run through the same plugin pipeline (pre/post MCP hooks) as LLM calls
  (`core/mcp/pluginpipeline.go`). Upstream OAuth flows, consent pages, per-user credential vaults, a Starlark
  sandbox and an OAuth authorisation server all live in the same process as the LLM proxy.
- **Performance**: README claims 11 µs added overhead at 5k RPS (`README.md:200-212`) for LLM proxying; tool calls
  are network-bound (30 s default timeout) and agent mode adds whole LLM round-trips, so that number says nothing
  about MCP. Coupling cost shows in issues: tool registry not resynced after restart, `/mcp` serving 0 tools
  (#4998, #4314), per-call client "healthy with zero tools" (#8553), hangs on OAuth initialise (#4173), protocol
  version negotiation (#6391), non-deterministic tool order breaking prompt caching (#2347), include-clients
  header not filtering `tools/list` (#1697). Skills: marketplace needs `git` in the image (#8486), drops port behind
  a reverse proxy (#7762).

## 6. Strengths worth copying

1. **Deny-by-default allow-lists with "narrow only" layering**, and the *same* filter applied at list time and at
   call time (`core/mcp/exec.go:202-237`, `mcpserver.go:592-626`). Our `IAccessPolicy` should be the one function
   both `tools/list` and `tools/call` go through.
2. **Two lists per server: `tools_to_execute` and `tools_to_auto_execute`** (`schemas/mcp.go:825-837`) and the
   rule that unattended nested calls are restricted to the auto list (`core/mcp/utils.go:995-1003`). Maps directly
   onto our `read`/`write` risk level + "write needs confirmation".
3. **Credential resolver per auth type behind one interface** (`credstore/credstore.go:21-59`) with typed
   "auth required" errors carrying the next action (`token_exchange.go:33-71`). Good template for `Ume.Rag.Tools`:
   `ServiceIdentity` (P2), `EntraObo` (P3), `PerUserOAuth` (P3+).
4. **The Entra OBO audience detail** (`schemas/mcp.go:575-588`): the token reaching the tool hub must be audienced
   to the app that performs OBO. Confirms our plan (OBO from the `rag-api` token, with `rag-api`'s credentials,
   or a hand-off design that keeps that true) and gives us the pitfall to write down.
5. **Per-call connections for user-scoped credentials** (`credstore.go:83-125`): never share an upstream session
   between users.
6. **Virtual MCPs**: named, curated tool bundles served at their own URL (`/mcp/<slug>`). Same shape as our "skill
   as tool bundle", and a good way to expose a *subset* of platform tools to an external MCP client.
7. **OpenAPI → MCP tools with method-based read-only annotation** and credentials never model-visible
   (`docs/mcp/openapi-servers.mdx`). For GIS (OGC API Features has OpenAPI) and the contact directory this could
   replace hand-written MCP servers; worth a spike in our template.
8. **Tool-call audit linked to the LLM request id** and to team/user/key (`logstore/tables.go:1923-1975`).
9. **Skill versioning model**: immutable SemVer versions, monotonic, separate "served" pointer with rollback,
   blob dedup (`tables/skills.go`). Matches our draft/published + pin/"latest" plan; add the monotonic rule.
10. **Agent Skills export as a marketplace** (git-served plugin repo for Claude Code/Codex): a cheap way to deliver
    our "skill export" (P3) to staff using coding assistants.
11. **Server instructions delimited and tag-escaped** (`instructions.go:86-165`): the minimum for any untrusted text
    we put in prompts.

## 7. Flaws & risks

**Verified**
1. **No pinning/approval of tool definitions**: changed tools are persisted and served automatically
   (`server.go:3765-3768`, `mcp.go:117-135`). Name-based allow-lists do not protect against rug-pulls or poisoned
   descriptions.
2. **Upstream server instructions become a system message** on every MCP-bearing request (`toolmanager.go:600-640`):
   third-party text gets system-role authority. Delimiting helps against tag forgery, not against instructions.
3. **Tool output not labelled as untrusted, no generic size cap** (`toolresult.go:13-56`).
4. **Auto-injection of every reachable tool** into every request by default (`toolmanager.go:481-596`): context
   bloat and the widest possible blast radius per request; the opposite of progressive disclosure (code mode is the
   workaround).
5. **Fail-open when governance is absent**: "Without an admitter nothing is governed … no restriction"
   (`mcpserver.go:665-667, 685-692`); an unnarrowed request may use every tool (`exec.go:185-189`).
6. **Session identity is caller-asserted** (`x-bf-mcp-session-id`, any opaque value, `schemas/bifrost.go:359`):
   it keys per-user upstream credentials, so it is a bearer secret whose strength the caller chooses.
7. **Credentials in plaintext without `BIFROST_ENCRYPTION_KEY`** (`mcpheaders.go:94-110`).
8. **Skills served publicly** without auth (`middlewares.go:1739-1742`); URL-sourced skill files are fetched at
   serve time, so an "immutable" version can change content.
9. **Identity forwarding is unsigned**: allow-listed caller headers are forwarded verbatim (`utils/utils.go:171-193`).
10. **Key security features are enterprise-only**: OBO/token exchange, SSO user binding, guardrails, config audit.
    In OSS, per-user identity reduces to virtual keys or caller-asserted sessions.
11. **Recurring MCP reliability bugs** (tool registry resync, protocol negotiation, OAuth hangs; §5 issue numbers).

**Inferred**
- Running OAuth AS, consent UI, credential vault, sandbox and agent loops in the LLM proxy process enlarges the
  attack surface and the blast radius of the hot path; a bug in MCP handling can affect LLM routing availability.
- "Approval" of non-auto tools is only "the caller called the execute endpoint"; the gateway cannot tell a human
  confirmation from an automated loop. Meaningful confirmation needs the client UI (our plan §6.5 is right).
- Agent mode in the gateway is conversation logic without conversation state; it fits a stateless API client, not
  an assistant platform that owns conversations, citations and audiences.

## 8. Implications for the municipality

### (a) Changes to `plans/tools` and `plans/identity`

`plans/tools`:
- §3 *Registry*: make allow-lists **deny-by-default** per tool server, with two lists, *allowed* and
  *auto-run (no confirmation)*; auto-run requires risk `read`. Apply one policy function to both list and call
  (copy Bifrost's list/call symmetry; add a test that a hidden tool cannot be called by name).
- §3 *Pinned definitions*: keep (Bifrost lacks it and it is the most visible gap). Pin by hash of
  name+description+schema+annotations; also pin **server instructions**, and include them in the prompt only as
  labelled untrusted data, never as system text (or drop them; add to §6.1).
- §3 *Execution*: add a **result size cap per tool** and per message, and a per-call connection for user-scoped
  credentials (never share an MCP session across users).
- §3 *Credentials*: model credentials as **resolver per auth mode** (`service`, `entra_obo`, later `per_user_oauth`)
  with typed "auth required" outcomes the Chat API can turn into a sign-in prompt. Refuse to start without an
  encryption key for stored credentials (fail closed, unlike Bifrost).
- §2 *Skills*: keep progressive loading (`load_skill`) — Bifrost confirms nobody else does this server-side.
  Add: monotonic SemVer, a "served/pinned" pointer separate from latest, **no live-URL files** (snapshot content at
  publish), and an Agent Skills **export as a git/zip marketplace** (P3) for staff using Claude Code/Codex. Skill
  `allowed_tools` should map to tool-hub policy, not be informational as in Bifrost.
- §4 *Platform as MCP server*: add **named tool bundles at their own URL** (`/mcp/{bundle}`, cf. Virtual MCPs) so a
  client can be given e.g. only `search_knowledge` for one collection set; bundle reachability = client ceiling ∩
  user audience.
- §5 *Hosted servers*: add an **OpenAPI-to-MCP option** to the template spike (GET-only operations, method-derived
  read-only annotation, credentials in config) for systems that already publish OpenAPI (GIS/OGC API Features).
- §7 *Built-in tools*: unchanged; with ≤ ~20 tools per assistant, code mode is not needed (it also implies a
  sandbox, deferred to P3+).

`plans/identity`:
- §6: note the alternative Bifrost uses — being its own OAuth AS with DCR and binding grants to a key/user — and
  why we do not (tokens would not be Entra tokens, so no OBO and no Conditional Access). Keep Entra + pre-registered
  clients; revisit only if a target client cannot be pre-registered.
- §7: record the **Entra OBO audience constraint** (assertion `aud` must be the app doing OBO): the tool hub must
  perform OBO **as `rag-api`** (its credentials/certificate) or receive a token audienced to `rag-tools`; the
  current hand-off text ("exchanges the user's `rag-api` access token") is only valid with the first option. Also
  record that revocation is bounded by the OBO token lifetime cache, and that discovery for OBO servers needs a
  separate service credential (Bifrost keeps an admin token).
- Never key per-user credentials on caller-asserted ids (cf. Bifrost's session mode): only on validated `oid`.

### (b) MCP/tool governance in the Umeå LLM gateway or in the RAG tool hub?

**For the gateway**: one choke point for every LLM consumer (not only the RAG platform), so teams calling the
gateway directly would also get governed tools; budgets, keys and teams already live there; Bifrost shows it is
technically feasible and that tool cost/rate accounting reuses LLM governance; one audit trail per request.

**For the tool hub**: (1) the gateway's identity is a *team key*, while tool authorisation needs *subject + client +
assistant + skill + classification* — data the gateway does not have and should not get; (2) the gateway stores no
prompt content by design, but tool governance needs arguments/results (audit, untrusted labelling, pinning
checks) — moving it in breaks that property; (3) the gateway is a hot, latency-critical, small-surface service;
Bifrost's issues show MCP (OAuth flows, connection lifecycle, sandbox) is the least stable part of its codebase;
(4) OBO needs the user's Entra token, which the gateway never sees (it gets HMAC-authenticated team keys);
(5) agent loops need conversation state, which belongs to the Chat API; (6) Bifrost's own design leaves approval
to the caller and fails open without governance — the parts we would most need are the parts a gateway does badly.

**Recommendation**: keep MCP client, tool policy, credentials, pinning and agent loops in **`Ume.Rag.Tools`** (and
the Chat API), as planned. The LLM gateway should stay tool-agnostic: it passes `tools` through and never executes
them. Revisit only if non-RAG teams need governed tools *without* the RAG platform; then expose the tool hub (or
its MCP server endpoint) to them as a separate service, not inside the gateway.

### (c) Additions to `plans/gateway-changes`

1. **Tool-call attribution metadata, no content**: accept optional `x-ume-tool-calls: <count>` /
   `x-ume-agent-run: <id>` (or reuse a generic correlation header) so gateway usage for agent turns can be grouped
   per agent run and joined with the tool hub's audit (Bifrost links tool logs to `llm_request_id`,
   `logstore/tables.go:1948`). Pure metadata, consistent with the no-content rule.
2. **Pass-through guarantee**: state explicitly that the gateway never injects or executes tools and forwards
   `tools`/`tool_choice` unchanged across providers (add a regression test per provider; Bifrost had tool-payload
   translation regressions, #3511/#3512).
3. **Stable ordering**: the gateway must not reorder `tools`, so the Chat API's deterministic tool order keeps
   prompt-cache hits (Bifrost #2347).
4. Open question 2 (signed end-user claim): Bifrost shows the failure mode of unsigned forwarded identity
   (`x-bf-mcp-session-id`, allow-listed headers). If `x-ume-app-user` is used for per-user rate limits, it should be
   a pseudonymous id that only delegating keys may set (as already planned), and documented as caller-asserted.
