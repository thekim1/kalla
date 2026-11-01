# Knowledge & assistant services: general idea

Status: draft for discussion, 2026-10-29. Builds on the Umeå kommun LLM gateway
(<https://github.com/thekim1/llmgateway>, local clone `~/git/llmgateway`).

## 1. Goal

Give the municipality a base platform for **retrieval (RAG), crawling and chatbots**, the way the LLM gateway is the base
platform for model access, keys and budgets:

- Departments add **specific URLs / websites**, **SharePoint** sites and libraries and **files** as knowledge sources.
- They build **assistants** on top of those sources, for staff (internal) and for citizens (public website).
- Other applications (intranet, Teams, case systems, Open WebUI, a web widget) use the assistants and the retrieval
  through **APIs**.
- Easy to develop and easy to administer; deployable with Docker on **Azure or on-prem** with the same artifacts.

## 2. Design principles (learning from Eneo)

Eneo covers the same ground but is one large platform (chat UI, assistants, crawler, integrations, apps, workflows in
one deployable) and is slow. We aim for the opposite:

1. **Small services, one job each, standard APIs.** No service owns a UI it does not need. Chat front ends are clients.
2. **Data plane and control plane are separate**, as in the gateway: the APIs apps call never host admin endpoints, the
   admin API never sits in the request path.
3. **Every model call goes through the gateway** (chat and embeddings). No provider credentials, no routing, no
   budgets in these services. PII routing, residency and cost reporting come for free.
4. **API first, OpenAI-compatible where it exists.** An assistant is exposed as a "model" on `/v1/chat/completions`, so
   any OpenAI client works unchanged.
5. **Fast request path.** Retrieval is one Postgres query (hybrid search) with warm caches (as designed: three short
   statements, keyword in parallel with the query embedding, fused in .NET; `plans/knowledge-api/design.md` §6);
   ingestion is entirely off the request path in a worker.
6. **Same stack and ops model as the gateway**: .NET 10, Aspire for local dev, PostgreSQL, Redis, Vue 3 admin UI with
   the gateway design system, OIDC/BFF login, Docker Compose release bundle, Kubernetes optional.
7. **Content is stored here, unlike the gateway.** Documents, chunks and chat history are personal-data-bearing, so
   retention, deletion and audit are first-class features, not afterthoughts.
8. **Failures are visible, never silent.** Every document has a run record with outcome and reason; a sync that
   loses content is shown as degraded, and large deletions need confirmation (Onyx deleted 189,833 documents and
   reported success, see `platforms/onyx.md`).
9. **Small footprint.** Pilot target: the whole platform on one 8 vCPU / 16–24 GB host in two resource zones
   (Dify ships 16 containers, Onyx 13 with ≥10 GB RAM). Services are one binary that can run in several roles.
10. **Built for Swedish.** Swedish full-text search with compound-word splitting and measured Swedish recall. None of
    the platforms analysed does this.

Evidence and the full flaw table: `platforms/00-comparison.md`.

## 3. Services

```
 Clients: Open WebUI · web widget · Teams bot · intranet / case systems
        │  OpenAI-compatible API (assistant = "model"), RAG key or OIDC token
 ┌──────▼───────────┐  retrieve  ┌──────────────────────────┐
 │ Chat API          │──────────▶│ Knowledge API             │
 │ assistants,       │           │ hybrid search, citations  │
 │ history+retention │           └───────────┬──────────────┘
 └──────┬────────────┘                       │ reads
        │                      ┌─────────────▼──────────────┐
        │                      │ PostgreSQL + pgvector       │◀── Admin API + Admin UI
        │                      │ sources, docs, chunks, chat │    (control plane)
        │                      └─────────────▲──────────────┘
        │                                    │ writes
        │                      ┌─────────────┴──────────────┐   ┌──────────────────┐
        │                      │ Ingestion worker            │──▶│ Parser sidecar   │
        │                      │ crawler · SharePoint (Graph)│   │ Python, Docling  │
        │                      │ · uploads · chunk · embed   │   │ stateless        │
        │                      └─────────────┬──────────────┘   └──────────────────┘
        ▼                                    ▼
                      LLM gateway (chat completions + embeddings)
```

| Service | Plane | Responsibility |
|---|---|---|
| **Knowledge API** | data | `POST /v1/retrieve` over one or more collections: hybrid search, optional rerank, returns chunks with citations (title, URL, SharePoint link, page). Usable on its own by apps that do their own prompting. |
| **Chat API** | data | OpenAI-compatible chat where `model` is an assistant. Resolves the assistant, calls Knowledge API, builds the prompt, streams the answer from the gateway with citations. Stores conversations with retention and feedback (thumbs, comment). |
| **Ingestion worker** | background | Executes each source's **pipeline** (section 5a): crawling, SharePoint delta sync, uploads, change detection, parse (via sidecar), chunk, embed (via gateway), upsert. Scales separately; a slow crawl never touches chat latency. |
| **Parser sidecar** | internal | Python + Docling in one stateless container: PDF, Office, HTML to structured Markdown, tables, OCR for scans. No database, no state, only reachable from the worker. |
| **Admin API + Admin UI** | control | Departments, teams, collections, sources, assistants, keys, sync status, retention, audit log. Own app, same design system and login pattern as the gateway admin. |

Chat API and Knowledge API may start as **one deployable with two endpoint groups** and split later if needed; what
matters is that the boundary is an HTTP API, not shared internals.

## 4. Domain model (first cut)

- **Department → Team**: same structure and codes as the gateway (cost-centre code, team code), so roles from the
  IdP (`departmentCodes`, `teamCodes`) mean the same in both systems.
- **Collection**: a named knowledge base owned by a team, with a classification. All collections share one
  platform embedding profile (EU `text-embedding-3-large` at 1024 dimensions for the pilot, on-prem BGE-M3 when the hardware exists). Public collections may only contain public sources.
- **Source**: belongs to a collection; one of
  - *URL list*: specific pages the editor pins ("use exactly these pages").
  - *Website*: crawl from a start URL or sitemap, with include/exclude patterns, depth and schedule.
  - *SharePoint*: site, library or folder via Microsoft Graph, delta sync.
  - *Files*: uploaded documents.
  - Later: other connectors through the same connector interface.
- **Document → Chunk**: content hash, source URL/ID, last modified, metadata; chunks with embedding and Swedish
  full-text vector.
- **Assistant**: owned by a team; system prompt, gateway model name, linked collections, retrieval settings, audience
  (`internal` / `public`), history retention period, allowed origins (public widget).
- **Conversation / Message / Feedback**: stored with retention per assistant; deleted by a nightly job.
- **API key** (RAG side): HMAC-stored, per team, scoped to assistants and/or collections.
- **Gateway keys**: per level (`OnPrem`-only, `Eu`-max, open) and deployable, held as secret files in the deployment, not in the database; the pilot team's own keys until gateway release 2, then delegating keys (`plans/gateway-changes/design.md` §13.4).

## 5. Ingestion

1. **Trigger**: schedule per source, manual "sync now", upload, or a SharePoint change notification.
2. **Discover**: sitemap-first crawling within scope, robots.txt respected, polite rate limits; URL lists fetch exactly
   their URLs; SharePoint uses Graph delta queries, so only changes come back.
3. **Change detection**: ETag / Last-Modified / content hash; unchanged documents are skipped, removed ones deleted
   (with their chunks).
4. **Parse**: HTML boilerplate removal in .NET (AngleSharp); PDF/Office/scans through the parser sidecar (Docling) into
   Markdown with headings and tables kept.
5. **Chunk**: structure-aware (headings, sections), with the document title and heading path prefixed to each chunk.
6. **Embed**: batched through the gateway's `/v1/embeddings` with the team's gateway key; one platform embedding profile: an EU-hosted model for the pilot, an on-prem multilingual model later (Swedish quality to be benchmarked).
7. **Upsert** chunks; sync status and per-document errors visible in the admin UI.

Jobs are queued in Postgres (`SKIP LOCKED`) with retries and back-off; one failing document never fails a whole
source. JavaScript-heavy pages: a headless browser renderer as an option on a source, not the default.

**Connector contract** (after Onyx `connectors/interfaces.py`). A connector implements small capability interfaces
and opts in to what it supports:

- *Fetch*: resumable, yields documents **and failures** as data, returns a typed checkpoint.
- *List ids*: cheap listing used to detect deletions.
- *Retry failed*: re-fetch only the items that failed last time.
- *Hierarchy* (optional): folders/sites for the admin UI.
- *Pre-flight check*: runs on the unsaved source form and says how to fix it (e.g. a missing `Sites.Selected` grant).

**Safety and efficiency rules learned from the others**

- *Deletion guard*: if a sync would remove more than 20% of a source (configurable) or the source returns nothing,
  the deletion pauses for confirmation. Deleted documents are soft-deleted with a grace period.
- *SharePoint delta done properly*: process `deleted` facets and moves, recover from an expired token (410) with a
  full resync, renew change subscriptions on a schedule. Onyx's and Eneo's SharePoint issues become acceptance tests.
- *Two-stage change detection*: timestamp/ETag first, then content hash; nothing unchanged is re-parsed or re-embedded.
  Embeddings are cached by text hash, so re-chunking only embeds new text.
- *Run record per document* with an execution id: a newer sync cancels an older one still in progress (Kernel Memory).
- *Job classes* (crawl, parse, embed, sync; as designed: `preview`, `command`, `sync`, `fetch`, `parse`, `embed`,
  `eval`, crawling inside `sync`/`fetch`, `plans/platform/data-model.md` §4.2) with their own concurrency limits, fair per department, so one large
  crawl cannot starve everyone else (Eneo and Dify run everything through one worker queue).
- *Crawl reports*: every run lists failed pages with reasons; per-department limits on concurrent crawls.
- *Source settings tagged by effect* (identity, scope, behaviour, cosmetic) so an edit triggers the smallest
  re-sync (Onyx `field_policy.py`).

## 5a. Pipelines and the visual builder

> **Superseded in large part:** pipelines are fixed and linear (template + parameters), there is no canvas and no
> auto-applied parse-strategy learning; site profiles (hand-checked in P1, learned from P2) configure the HTML stages
> per host. The visual builder is a **P3 item behind trigger C2** (built only when editors ask for a flow template
> parameters cannot express, in at least two departments or as one recurring platform-team request). The text below is
> the original idea.

The steps above are not hard-coded: each source runs a **pipeline**, a directed graph of typed nodes stored as
versioned JSON. The ingestion worker executes it; the admin UI edits it on a canvas (Vue Flow, in the gateway design
system).

**Node catalogue (first cut)**

| Group | Nodes |
|---|---|
| Fetch | HTTP fetch, Sitemap/crawl, Headless render, SharePoint (Graph), Upload |
| Route | **Detect** (MIME, language, structure, scanned or not), **Switch** on detected properties, **Auto-parse** (see below) |
| Parse | HTML main-content, Docling (PDF/Office), OCR, Table extract, Plain text |
| Transform | Clean (boilerplate, headers/footers), Split by headings, Fixed-size chunk, Merge small chunks, PII mask |
| Enrich | Metadata from URL/path, LLM summary or keywords (via gateway), Date/document-type extraction |
| Index | Embed (via gateway), Write to collection |
| Quality | Quality check (text yield, empty chunks, OCR confidence, duplicates); routes bad output to a fallback branch or review |

**Dynamic per source.** Each source type comes with a **default template** (website, URL list, SharePoint, files),
so most editors never open the canvas. The canvas only offers nodes whose inputs fit what the previous node produces
(typed ports: raw bytes → document → sections → chunks → vectors), so invalid graphs cannot be built.

**Graph and layout are separate.** The executable graph (nodes, typed ports, parameters) and the canvas layout
(positions, colours) are stored separately; Dify and RAGFlow mix them. The server validates port types and required
nodes on every save and publish; the browser only helps.

**Templates as forms.** A template exposes a few parameters (chunk size, include/exclude paths, OCR on/off) as a
form, so editors set up a source without seeing the canvas (as Dify's `rag_pipeline_variables`).

**Preview before publish.** As with the gateway's routing-rule dry run: pick a few real documents and see each
node's output (parsed Markdown, chunks, metadata, estimated LLM cost) before a pipeline version goes live. A single
node can be re-run with the cached output of the nodes before it, and preview never writes to the index.

**Versions.** Draft and published versions with restore; each document records the pipeline version that processed
it. Publishing computes a re-processing plan (which documents, which nodes onwards) and shows it before it runs.

**Chunk structures.** Flat chunks and parent-child chunks (search on the small child, return the parent section as
context), declared as the output type of the chunk node so the index knows what it receives. (As decided: flat chunks
in P1, sections rebuilt at query time from neighbouring chunks; parent-child only if evaluation shows the need,
`plans/ingestion-worker/design-processing.md` §5.3, `plans/knowledge-api/design.md` §9.)

**Auto-parse and learning.** The *Auto-parse* node chooses a parse strategy per document:

1. **Rules first**: learned and admin-made *parse profiles* (e.g. "PDFs from `*.umea.se/kallelser/*` → OCR";
   "domain X needs headless render"). Fast and explainable.
2. **Heuristics, per page**: detected properties pick a default for each page, as RAGFlow does: a clean text layer
   is used directly (the parser's pypdfium2 fast path), an empty or garbled one goes to OCR, a page with nothing found is re-rendered at
   higher resolution; table-heavy pages go to Docling's table extraction. Garbled-text detectors also feed the
   Quality check.
3. **Try and score** when unsure: run two or three candidate strategies on the document, score the outputs with the
   Quality check (and optionally a small LLM judge via the gateway), keep the best.
4. **Learn**: each choice and score is recorded per domain/path/document type. When a pattern is consistently won by
   one strategy, the platform creates a **parse profile**, after which step 1 handles those documents with no trial
   runs. Later signals feed the same loop: chunks that are cited in answers with positive feedback, or never
   retrieved at all, and manual corrections by editors. The same mechanism learns per-site cleanup rules (menus,
   footers, cookie banners) for crawled websites.

Profiles above the confidence threshold are **applied automatically** (decision 2026-10-29), with guardrails: a
minimum sample count, scoped to the department's own sources, a trial run alongside the current strategy before it
takes over, automatic rollback when quality metrics drop, a notification and audit entry, and a kill switch. Below
the threshold a profile is proposed for approval. Every decision stays explainable: the run record says which profile
or heuristic chose the strategy.

**Scope guard.** The builder is for *knowledge pipelines* (ingestion, and possibly retrieval). It is not a general
automation or agent tool like n8n or Dify. Custom code nodes are left out at first; new node types are added in
code (a small `IPipelineNode` interface), so the catalogue stays tested and supported.

## 6. Retrieval and answering

- **Hybrid search in one SQL query we write ourselves**: pgvector HNSW similarity + Postgres full-text search with a
  Swedish configuration using a Hunspell `sv_SE` dictionary for compound-word splitting, merged with reciprocal rank
  fusion. Good for compound words, names, diarienummer and form numbers where pure vectors miss. (The PgVector
  connector's built-in hybrid search requires every keyword to match, so we do not rely on it.) As designed: three
  short statements and RRF in .NET (`plans/knowledge-api/design.md` §6).
- **Filters inside the same query** (collection, GIN-indexed tags, document type, date), applied before ranking, not
  as a list of ids afterwards (Dify's flaw).
- Optional **reranker** (via the gateway if it can serve one, otherwise a later decision).
- **Citations are mandatory and precise**: an answer cites the chunks it used, not everything retrieved; the
  assistant says plainly when the sources do not cover the question.
- **Search backend**: Postgres only for the MVP, behind our own search interface, with Postgres as the master copy so
  a search index can always be rebuilt. **OpenSearch** becomes an optional backend if a trigger fires: Swedish
  recall@10 at least 5 points better than the best Postgres setup, more than 2–3M chunks, p95 search latency above
  300 ms after tuning, or a real need for facets/highlighting (`platforms/opensearch.md`).
- **Swedish retrieval benchmark** before choosing chunking and backend: 5–20k real Umeå chunks, 150–300 tagged
  questions, six setups from vector-only to OpenSearch with decompounding; recall, MRR, nDCG and latency.

**Chat request path budget.** Eneo spends 78 SQL statements and 54–68 ms per message before the model is called.
Ours: assistant and key configuration from an in-memory cache invalidated over Redis (as in the gateway; as designed:
by the Postgres config outbox and `LISTEN/NOTIFY`, `plans/platform/data-model.md` §5), one retrieval query (three short
statements), conversation writes off the request path (0 SQL on the hot path, `plans/chat-api/design.md`). Overhead is measured with the gateway's benchmark harness
and must stay a small fraction of Eneo's.

## 7. Identity, keys and the gateway

| Caller | Authenticates with | Gets |
|---|---|---|
| Staff in the admin UI | Municipal OIDC (Entra ID / Keycloak / AD FS), BFF cookie | Roles as in the gateway: platform admin, department admin, team admin, viewer (as designed: the `rag-*` roles of `plans/identity/design.md` §1.3) |
| Staff-facing apps (Open WebUI, intranet) | OIDC user token, or a RAG API key | Internal assistants their team/department allows |
| Integrations | RAG API key (team-scoped) | Named assistants and collections |
| Public web widget | No login; origin allow-list + rate limit | Only assistants marked `public` |
| These services → gateway | One key per level and deployable with `x-ume-max-residency: OnPrem \| Eu \| External` on every call; the pilot team's own keys until gateway release 2 (P2), then delegating keys with `x-ume-on-behalf-of: <team id>` | Gateway budgets, PII routing, residency and usage per team |

The RAG services issue **their own keys** in the gateway's style rather than reusing gateway keys: the two systems
stay independently deployable and their key stores do not couple. Cost attribution uses the gateway's delegated
attribution (`plans/gateway-changes/`, release 2, P2); until then the pilot team's keys carry it.

Two key kinds, as in Eneo: **public keys** (`pk_`, read-only, bound to allowed origins, for the web widget) and
**secret keys** (`sk_`, for integrations). (As designed: prefixes `ume-rag-pk-` / `ume-rag-sk-`,
`plans/platform/data-model.md` §12.22.)

**Security classification.** Collections and assistants carry a classification (e.g. public, internal, sensitive);
an assistant may only link collections at or below its level, and the level decides which gateway residencies are
allowed (sensitive → on-prem models only). Taken from Eneo's space classifications, enforced through the gateway's
residency policy.

**Retention and audit.** Retention is set per assistant, falling back to department and platform defaults (90 days);
a nightly job deletes expired conversations. Reading another person's conversations or the audit log requires a
stated reason, which is itself audited.

## 8. Front ends

- **API only** in the platform itself.
- **Open WebUI** for the staff pilot: it is pointed at the Chat API, and each assistant appears as a model. Its own
  RAG and tools are switched off. The integration contract is in `platforms/open-webui.md` §9:
  - it forwards the signed user JWT (`ENABLE_FORWARD_USER_INFO_HEADERS`), and our API decides access from it;
  - `/v1/models` is filtered in our API (`BYPASS_MODEL_ACCESS_CONTROL=true`);
  - citations travel as streaming `source` events;
  - title, tag and follow-up generation go to our virtual task model `owui-tasks` (an on-prem model through the
    Chat API, `plans/chat-api/design.md` §4.2), so Open WebUI has only one connection.

  Licence: BSD-3 plus a branding clause. It is free at any scale, but the "Open WebUI" branding must stay above 50
  users unless there is written permission or an enterprise licence. That is fine for a branded pilot. If
  municipal branding is needed, use our own client or the widget instead.
- **Web widget**: a small script for the municipal website, separate static package, calls the Chat API.
- **Teams bot**: later, as a thin client of the Chat API.

## 9. Technology

- .NET 10, ASP.NET Core minimal APIs, EF Core, Aspire 13 for local orchestration (same as the gateway).
- Microsoft.Extensions.AI for the gateway clients (OpenAI-compatible); `CommunityToolkit.VectorData.PgVector` and
  `Pgvector.EntityFrameworkCore`, with our own hybrid search SQL. (As designed: the chat role calls the gateway with a
  raw `HttpClient`, and `CommunityToolkit.VectorData.PgVector` is not used; `plans/platform/repo-and-deployment.md` §2.2.)
- AngleSharp + SmartReader (HTML main content), Microsoft.Graph (SharePoint delta); every PDF goes to the parser (no
  PDF library in the worker).
- Pipeline engine: a Postgres queue (`SKIP LOCKED`) running fixed linear pipelines (template + parameters). Not
  Elsa, Agent Framework Workflows or Microsoft.Extensions.DataIngestion (preview, linear pipelines).
- Not used: Kernel Memory (archived), Semantic Kernel, TickerQ (commercial licence since 2026-10-13).
- PostgreSQL (pgvector, Swedish FTS with Hunspell): run as our own container so custom dictionaries can be loaded
  (check whether the Azure managed offering allows this), Redis for caches/rate limits (invalidation: the Postgres config outbox).
- Python sidecar: Docling, behind a small HTTP API; replaceable.
- Vue 3 + the gateway design system for the admin UI.
- Release bundle with Docker Compose (Azure VM, on-prem Docker host) and Kubernetes manifests; storage for uploaded
  files on a volume or S3-compatible / Azure Blob behind an interface.

## 10. Decisions (2026-10-29)

Rows marked `current` bind. A `partly superseded` row binds only for what its status says still holds, and a
`superseded` row no longer binds; the status names the later row (by its Topic) that changed it. New rows are appended
with status `current`, and an older row they change gets its status updated in the same commit. Rows keep their place.

| Topic | Decision | Status |
|---|---|---|
| Builder scope | Full canvas for **ingestion** pipelines. The **query side** is a fixed, linear sequence of steps per assistant (query rewrite, hybrid weights, rerank, top-k, filters, prompt template), set in a form with a test panel (refined by steps 7–8: no query rewriting in P1, condense on follow-ups is a chat step; rerank P3). It opens up to the canvas later, once test-question sets per assistant show that a change helps. Keeps the latency-critical path predictable. | partly superseded by "Visual pipeline builder (user, 2026-10-30)": the query side is a fixed linear step sequence per assistant, set in a form |
| Who edits pipelines | Department editors with a *pipeline editor* right: copy templates, change parameters, add nodes from the approved catalogue. The platform team owns templates and the node catalogue, and can lock required nodes (e.g. PII mask, Index). | partly superseded by "Simplification pass (2026-10-30, all ten review changes adopted)": department editors with the pipeline-editor right change template parameters; platform team owns templates |
| Learning | Parse profiles are **applied automatically above a confidence threshold**, with guardrails: minimum sample count, scoped to the department's own sources, trial run alongside the current strategy before taking over, automatic rollback on quality drop, notification + audit entry, kill switch. Below the threshold: proposed for approval. | superseded by "Simplification pass (2026-10-30, all ten review changes adopted)" |
| Chat history | Kept **90 days** by default (configurable per assistant). **Blocker for the pilot:** confirm with the registrar/archivist whether chat logs are allmänna handlingar and whether deletion needs a gallringsbeslut. | partly superseded by "Weakness review 4: data protection (user, 2026-10-30)": 90-day default retention per assistant; A1 (registrar/archivist) still open |
| First version | URL lists, website crawl, file uploads; Chat API + Knowledge API; Open WebUI for one pilot department. | current |
| Search backend | Postgres only for the MVP behind our own search interface; OpenSearch as an optional backend when a measured trigger fires (section 6). | current |
| Postgres hosting | Our own Postgres container everywhere, Azure included (needed for the Swedish Hunspell dictionary). | superseded by "Simplification pass (2026-10-30, all ten review changes adopted)" |
| CI/CD | GitHub Actions for everything, as the gateway does (build, test, release, and the app-registration workflows). | current |
| Key service | **Postponed**: the pilot stores master keys as protected files (file key provider, no `sensitive` data); Azure Key Vault is the target, adopted later with a re-wrap (`plans/postponed-decisions.md` K1). | current |
| Entra IaC | Bicep with the Microsoft Graph extension, applied by a GitHub Actions workflow. | current |
| ClamAV updates | Directly from database.clamav.net; no internal mirror. | current |
| Self-hosted runner | Open: IT security to approve one runner on the test host for the R1 test and nightly evaluation. | partly superseded by "CI on the self-hosted runner for every pull request (user, 2026-10-31)": a runner on the test host for R1 and nightly evaluation is still open |
| Database separation | Own Postgres instance and own Redis, separate from the gateway's; no shared database or server (`plans/platform/` §2). | partly superseded by "Fewer pilot containers (user, 2026-10-30)": own Postgres, separate from the gateway; own Redis when Redis returns in P2 |
| Classification | Levels `public`, `internal`, `sensitive`; the highest (`sensitive`) may only use on-prem models through the gateway. | current |
| Swedish benchmark | Postponed. Until then a small smoke set of questions made by the team; chunking and backend choices are revisited when the benchmark exists. | partly superseded by "Weakness review 2: answer quality (user, 2026-10-30)": the full Swedish benchmark stays postponed |
| Plans | One folder per service under `plans/`, each with a `plan.md`; detailed per service later. | current |
| Architecture review | All seven improvements in `02-architecture-review.md` adopted: resource zones, shared document store, one on-prem embedding profile, gateway delegation + residency ceiling, client registry with classification ceilings, evaluation as a platform component, versioned fail-closed configuration. | partly superseded by "Simplification pass (2026-10-30, all ten review changes adopted)": resource zones, residency ceiling, client registry and evaluation as a platform component |
| Skills | Both know-how packages and tool bundles; assistants are built from skills (`plans/tools/`). | current |
| MCP | All three roles: platform as MCP server, assistants call MCP tools through a tool hub, platform hosts municipal MCP servers. | current |
| Tool actions | Read-only first; write tools later, always with user confirmation. | current |
| Identity | Entra ID; user-delegated tool access via On-Behalf-Of designed now, built in a later phase (`plans/identity/`). | partly superseded by "P1 pilot sign-in (user, 2026-10-30)": Entra ID from P2; On-Behalf-Of designed now, built later |
| Classification | Three levels stay; `sensitive` is the municipality's highest category. | current |
| Chat-log protection | Configurable per level (history mode, retention, encryption, review rules) within limits set by platform admins; per-conversation envelope encryption with crypto-shredding (`plans/platform/` §8a). | partly superseded by "Weakness review 4: data protection (user, 2026-10-30)": per-level settings within admin limits; encrypted chat storage waits for A1 |
| Separate data tier for `sensitive` | Designed now (tier resolver), decided before the first `sensitive` source is onboarded. | superseded by "Simplification pass (2026-10-30, all ten review changes adopted)" |
| Residency per level | `public` any provider; `internal` on-prem or EU; `sensitive` on-prem only; for every model call, ingestion included. | current |
| MCP clients | Ceiling `public` by default; an admin may raise a vetted client to `internal`; never `sensitive`. | current |
| Feedback in evaluation | Only as editor-rewritten questions without personal data. | current |
| Skills timing | P2, with agent assistants; P1 assistants are prompt + collections. | current |
| Identity compatibility | Same identity model as the gateway: same OIDC/BFF, claims, config keys (`UME_RAG_OIDC_*`), key and Data API patterns, security events; preferably one shared admin registration with `rag-*` roles. Department scope from groups is added to both systems. No registration needed before the pilot. | partly superseded by "Simplification pass (2026-10-30, all ten review changes adopted)": the gateway's OIDC/BFF, claims, config keys, key and Data API patterns, security events |
| Data model (step 1) | `plans/platform/data-model.md`: one `ragdb` with schemas `config`/`content`/`chat`/`audit`, one DB role per service with connection caps and timeouts, grants table by table; Swedish Hunspell FTS configuration `content.rag_swedish`; day-partitioned `Jobs`/`RunItems` queue (`SKIP LOCKED`, dedup, per-department fairness, `command` jobs for admin decisions); config outbox with a monotonic version and fail-closed reads; tier resolver with composite `Classification` FKs; per-conversation data keys in 7-day epochs under monthly shred-bucket master keys; retention by `SECURITY DEFINER` step functions; key prefixes `ume-rag-sk-`/`ume-rag-pk-`. | partly superseded by "Simplification pass (2026-10-30, all ten review changes adopted)": one `ragdb`, its schemas, per-service roles, Hunspell FTS, config outbox, key prefixes |
| Repo and deployment (step 2) | `plans/platform/repo-and-deployment.md`: repo `ume-rag` copying the gateway's conventions; one `Ume.Rag.Api` binary with roles; own `Ume.Rag.FakeGateway` (1024-dim embeddings, residency headers); own Postgres image (pgvector + Hunspell, compound test in CI); resource zones with an ingestion cgroup slice and fixed networks with firewall egress rules; digest-pinned release bundle; GitHub Actions incl. the R1 release test and `entra.yml`; ordered P0 checklist (§11). | partly superseded by "P0 set-up (questions, 2026-10-30)": `Ume.Rag.Api` with roles, FakeGateway, resource zones, digest-pinned bundle, GitHub Actions |
| Identity detail (step 3) | `plans/identity/design.md`. Member groups are group claim values (Entra object ids; names on Keycloak/AD FS); Graph lookup failure narrows to claims-only, never fails open. `ume-rag/infra/entra/` (Bicep) owns the shared admin registration incl. the gateway's roles; consent, role assignments and secrets stay manual. The RAG BFF uses its own client secret, as the gateway (certificates later, for both). Policy denials above ceiling or outside audience answer 404. | partly superseded by "P1 pilot sign-in (user, 2026-10-30)": claims-only narrowing on lookup failure, own BFF secret, 404 for policy denials |
| Gateway changes (step 4 questions) | Delegated requests charge the effective team + department budgets, with the platform key's own budget and rate limit as an overall cap; any key may send the residency ceiling; three releases B → C → A; drafts stay in this repo until implementation. | partly superseded by "Weakness review 5: critical path (user, 2026-10-30)": delegated budget charging with the key as cap; any key may send the ceiling |
| Gateway changes (step 4) | `plans/gateway-changes/design.md`, `adr-drafts.md` (ADR 018–020): B = `x-ume-max-residency` as one more hard constraint in candidate selection, `403 max_residency_not_met`, FakeLlm `fake-embed-<n>`, tool passthrough tests; C = `Departments.MemberGroups`, Graph `checkMemberGroups` without MSAL, tenant/guest refusal, events 2007/2008/3003, team `code` on the catalogue; A = `VirtualKeys.DelegatedTeamIds`, effective-team policy and budgets with the key as overall cap, echo header, event 1006, run grouping headers. No change in the gateway repo until implementation. | partly superseded by "Weakness review 5: critical path (user, 2026-10-30)": the contents of features B, C and A; their release order changed |
| Parser formats and pictures (step 5 questions) | Pilot parses PDF, Office (incl. legacy .doc/.xls/.ppt via LibreOffice headless), OpenDocument, RTF, images (OCR), email (.eml/.msg with attachments as separate documents), plain text/Markdown/CSV. Pictures: captions and OCR text only in P1; picture crops available on request for a P2 describe-pictures node. | partly superseded by "Simplification pass (2026-10-30, all ten review changes adopted)": PDF, modern Office, ODF, images, text in P1; pictures as captions and OCR |
| Parser detail (step 5) | `plans/parser/design.md`: worker keeps HTML, text, Markdown, CSV and clean PDF pages; parser handles the rest through Docling's native backends (legacy Office via its LibreOffice path behind our sandboxed wrapper, ODF native, email via `python-oxmsg`), plus a cheap `/v1/inspect`; multipart contract; the worker stores `result.json` and the `DoclingDocument`, so re-chunking never re-parses; own supervisor with pre-warmed processes; OCR engine always explicit. Zip and macro-enabled Office refused in P1 (Z1); email senders/recipients metadata only. | partly superseded by "Simplification pass (2026-10-30, all ten review changes adopted)": Docling parser, `/v1/inspect`, stored `DoclingDocument`, supervisor, explicit OCR, Z1 refusals |
| Ingestion worker (step 6 questions) | Detail in three documents (engine; connectors; processing). Names for personal-data detection by a Swedish NER model in the parser image; patterns in the worker. Default sync: URL lists nightly, crawls weekly at night. Default chunks: heading-split, ≈ 400 tokens, ≈ 15 % overlap, heading path prepended, flat in P1. | partly superseded by "Simplification pass (2026-10-30, all ten review changes adopted)": three design documents, default sync schedule and default chunking |
| Ingestion engine (step 6a) | `plans/ingestion-worker/design-engine.md`: reconciliation (`EnsureCurrent` + nightly drift sweep), job segments by node job class, Merkle stage hashes for sharing, execution-id fence, at-least-once with effectively-once effects, per-department fairness and parse slicing, cron scheduler with jitter, derived run counters, isolated preview, publish = plan → gate (shadow sets) → confirm, rollback never gated. Shared documents update every referencing department at once (follow runs). | partly superseded by "Simplification pass (2026-10-30, all ten review changes adopted)": reconciliation, execution-id fence, at-least-once, fairness, jittered cron, derived counters, ungated rollback |
| Crawler (step 6b questions) | User agent `UmeaKommunBot/1.0 (+about page)`; robots.txt always honoured; page-level `noindex`/`nofollow` honoured on external hosts, ignored on allow-listed municipal hosts; default 10 000 pages and depth 10 per crawl; off-site links never followed unless allow-listed. | current |
| Crawler build vs buy | Own crawler (6b), not a self-hosted Firecrawl: it would add a second queue stack and an AGPL component while the hard parts (shared frontier, change detection, deletion guard, crawl rules, SSRF guard) stay ours. Firecrawl-like services only as a possible renderer backend for anti-bot sites. | current |
| Connectors (step 6b) | `plans/ingestion-worker/design-connectors.md`: identity = final URL after redirects, `rel=canonical` never trusted; shared frontier with per-source claims and a rate-paced releaser; full traversals with conditional GETs, deletions only after complete traversals via a guarded reconcile phase; DNS-pinned egress guard plus firewall canary; renderer fed entirely by worker-side fetches; rendered and origin content as separate document variants; uploads by content hash with names per source; ClamAV fail-closed. External hosts open to all departments (admins can block); SharePoint ACLs not mirrored; malware metadata only. | partly superseded by "Weakness review 3: freshness (user, 2026-10-30)": final-URL identity, conditional GETs, guarded deletions, egress guard, upload hashing, ClamAV fail-closed |
| Processing (step 6c questions) | Names by KBLab BERT NER (MIT) in the parser image; publish-gate override only by platform admins with an audited reason; evaluation sets of ≤ 100 generated questions, gates sample 50; a new collection's first publish needs an absolute recall@10 floor (≈ 0.7). | partly superseded by "Simplification pass (2026-10-30, all ten review changes adopted)": names by KBLab BERT NER; evaluation sets of up to 100 generated questions |
| Processing (step 6c) | `plans/ingestion-worker/design-processing.md`: per-page parse rules on inspect signals; built-in parse profiles with observations recorded from P1; BGE-M3 tokenizer in .NET; 400-token budget incl. heading path, sentence overlap, row-group tables; PII patterns in .NET plus `/v1/ner` on its own lane and a `ner` job class; public collections blocked for personnummer/samordningsnummer/external email always, names and contacts only in list-like documents; noise-only changes re-key chunk sets; evaluation on documents with intervals; sub-corpus gate with escalation; collections `building` until 0.70; flat chunks with section expansion in the knowledge API. | partly superseded by "Simplification pass (2026-10-30, all ten review changes adopted)": parse rules, tokenizer, 400-token budget, PII patterns, flat chunks with section expansion |
| Knowledge API (step 7 questions) | Top-k default 10, max 50. Citations without a public URL link to an access-checked download via 7-day signed grant links (staff network only, access re-checked at each download). No query rewriting in P1. No recency boost. | partly superseded by "Weakness review 3: freshness (user, 2026-10-30)": top-k 10 (max 50), signed grant links, no query rewriting in P1 |
| Knowledge API (step 7) | `plans/knowledge-api/design.md`: shared `Ume.Rag.Retrieval` engine (API, evaluator, test panel); keyword search in parallel with the query embedding, then vector, then one hydrate batch, RRF in .NET; exact scan for scopes ≤ 10,000 chunks; degrade to keyword-only if embedding fails; coverage per collection by cosine distance; dedup + one variant per URL + max 3 chunks per document; section expansion from neighbouring chunks (`OverlapChars`); no result cache; query text never stored. | current |
| Chat API (step 8 questions) | Condense on, follow-ups only, on an on-prem task model. History: last 10 messages, ≤ 4k tokens. Not found: fixed sv/en text + contacts, no model call. Feedback in P1, incl. import of Open WebUI ratings. C1 chat attachments (P2): chat-scoped with conversation retention. P1 uploads disabled in Open WebUI. Editors read feedback comments after a one-click audited reason (`sensitive`: full review flow); **superseded in part** by Admin (step 10 questions): department admins and `rag-reviewer`s, not editors or platform admins. | partly superseded by "M2 and test hosting (user, 2026-10-30)": condense on follow-ups, history limits, fixed not-found text, feedback in P1 |
| Chat API (step 8) | `plans/chat-api/design.md`: client-sent history authoritative, 0 SQL on the hot path; Open WebUI with one connection and our `owui-tasks` task model; residency checked before the first byte, follow-ups with history at max(level, client ceiling); `[n]` renumbered by first citation, Open WebUI `source` events vs `url_citation` for others; output filter against exfiltration; signed issued conversation ids; background writer with per-conversation 7-day key epochs, no journal; Open WebUI pruning job. | partly superseded by "Weakness review 4: data protection (user, 2026-10-30)": client-sent history, residency before first byte, citation numbering, output filter, signed ids |
| Tools (step 9 questions) | Agent limits per user message: 8 tool calls, 60 s, $0.05 (hard cap 20 / 120 s / $0.25). Department skill editors publish behind the evaluation gate; tool links need a department grant; the platform team owns platform skills. MCP server template .NET only; other stacks via the OpenAPI → MCP generator. | partly superseded by "Simplification pass (2026-10-30, all ten review changes adopted)": agent limits, department grants for tool links, platform team owns platform skills |
| Tools (step 9) | `plans/tools/design.md`: tool hub `rag-tools` as the only credential holder (mTLS + service key hop); skills in `config` per the Agent Skills format (no scripts), SemVer with served pointer, MAJOR serve auto-pins other departments' assistants; skill gate = retrieval + routing layers, no tool execution; tool servers have clearance and result level, so an agent run's level is fixed at start; deny-by-default policy with one function for listing and calling; pinned definitions with 15-min drift checks and two-person re-approval; stateless `/mcp`, hub tools only on per-skill endpoints with `SkillAudiences`; .NET template + OpenAPI code generator; mock fee calculator `ume-mcp-avgifter` first; events 5012–5015. | partly superseded by "Simplification pass (2026-10-30, all ten review changes adopted)": deny-by-default policy, fixed run level, pinned definitions with drift checks, events 5012–5015 |
| Admin (step 10 questions) | Swedish admin UI (the gateway's mechanism: texts in components and a labels file, no i18n; the gateway's own UI is English). Pilot department admins set up their own collections and assistants, with the platform team pairing. New `rag-reviewer` role reads conversations (reason, audited; `sensitive` needs a second approver); department admins and reviewers read single feedback comments after a one-click reason; platform admins neither implicitly. One NVDA screen-reader pass of the pilot journey before the pilot. Limited conversation review (behind a feedback item, `public`/`internal`) in P1. | partly superseded by "Weakness review 4: data protection (user, 2026-10-30)": Swedish UI, pilot self-setup, `rag-reviewer`, feedback-comment reading rules, NVDA pass |
| Admin (step 10) | `plans/admin/design.md`: the gateway's BFF, CSP and problem details copied; new `Ume.Rag.Answering` library so the test panel runs the real chat path in process (incl. `building` collections); stable error codes mapped to Swedish texts in the UI; `version` (`xmin`) concurrency; one-transaction write path (change + outbox + audit + jobs); cross-rules V1–V31 in one `ConfigRules` service; pipeline publish via a `gating` state; `GatewayModels` sync in P1; setup checklist for the pilot journey; Vue Flow 1.48.2 canvas in P2 with a keyboard list editor; WCAG 2.2 AA. | partly superseded by "Many assistants per department (questions, 2026-10-30)": copied BFF/CSP, `Ume.Rag.Answering`, Swedish error texts, `xmin`, one-transaction writes, WCAG |
| Consistency pass (step 11) | `plans/consistency-pass.md`: all documents aligned with each other and with this table; settings rule (deployment knobs `Section:Key`, run-time values `Settings.<Section>.<Key>`); to-verify register `plans/to-verify.md`; P2: Open WebUI on its own VM, 24 GB RAG host keeps both slots. | current |
| Performance (questions) | Scale target ≈ 10× the pilot on 3–5 hosts (~100,000 pages/hour checked, 5–10M chunks, 20+ departments, several hundred concurrent chat users), beyond that only by measured triggers. Scale-out = Compose per host with roles per host, one shared Postgres, Redis and object store; Kubernetes optional later. Blobs beyond one host in an S3-compatible object store behind `IBlobStore`, file system for dev and the single-host pilot, envelope encryption unchanged. Job queue stays Postgres behind an `IJobQueue` seam, broker only by trigger. Object store server: the municipality's S3 service if IT offers one, else SeaweedFS, never MinIO from archived source; decided with IT before P2 (`plans/postponed-decisions.md` S2). HNSW on halfvec from P1 (column stays `vector`); O1 trigger = measured HNSW memory, latency and filtered recall. Crawl rules stay; higher municipal rates and a daytime trickle are per-host settings, off by default, only with the web team's written consent. Worker-only gateway key with `MaxConcurrentRequests` proposed now; BGE-M3 query lane only by trigger. | current |
| Performance pass | `plans/performance-pass.md`: binding resources are politeness, the on-prem BGE-M3, the NER lane and HNSW memory; Compose per host with roles; `IJobQueue` seam with batched dequeue and debounced `NOTIFY`; lease leaders (`content."LeaderLeases"`); Redis per-host crawl in-flight leases; S3-safe `IBlobStore` with `AWSSDK.S3`; halfvec HNSW; connection budget pooler-safe, no PgBouncer up to 5 hosts; `Worker:Concurrency:<class>`; scale test (`scale.yml`) and numeric scaling triggers. | current |
| Simplification pass (2026-10-30, all ten review changes adopted) | `plans/simplification-pass.md`; supersedes earlier rows where they differ. **Scope**: thin P0 (skeleton, migrations, PITR restore drill, verify/release workflows; multi-host seams, a/b slots, perf/scale/fuzz workflows later) and P1 (URL list, crawl, upload; HTML, PDF, modern Office, ODF, images, text; legacy Office, RTF, email and the renderer P2; Tesseract `swe` only; NER only when it can change the personal-data flag, no `ner` job class; Open WebUI title/tag/follow-up generation off, no `owui-tasks`). **Ingestion**: documents and chunk sets per source, scalar `CollectionId`, reuse by content-addressed caches (no shared chunk sets, follow runs or Merkle graph hashes); upload identity (source, slot); fixed linear pipelines = template + parameters (no DAG, no canvas: dropped from the roadmap); no auto-applied parse-strategy learning (proposals from P2); personal-data state on the chunk set. **Platform**: compounds split in .NET, stock pgvector Postgres, managed Postgres allowed (supersedes "Postgres hosting"); pgBackRest PITR, standby before production, write-once blobs, escrowed long-lived keys, `public` blobs unencrypted; chat keys per (level, expiry month) bucket, key destruction off until A1; tier machinery removed (`sensitive` isolation = separate deployment). **Security**: gateway keys per level, explicit ceiling on every call, delegated calls apply only the key's request policy, 503 for drained targets, same effective level every turn. **Identity/gateway**: one combined gateway release (ceiling, delegation, claims-only member groups); no interim per-team keys; Graph group lookup P2; RAG's own Entra registrations (supersedes "preferably one shared admin registration"); audit actor `oid`. **Quality**: not-found only when vector and keyword are both weak; reranker seam P1, rerank P2; condense skip + 600 ms timeout; evaluation advisory on the full collection (no gates, `building` state or 0.70 floor; supersedes the step 6c gate rows); nightly answer judge; breadcrumbs in the chunk prefix. **Robustness**: 90 s config grace window; no binary parsing in the worker (all PDFs to the parser, pypdfium2 fast path); serialised config writes; unpartitioned `Jobs`; short chunk-write transactions; smaller pools. **P2 tools**: in-process hub, `hosted` servers only, one `ume-mcp-kommun`, no OpenAPI generator, served pointer + rollback, static small skills, sharing with named departments, third-party-text attribute with hard refusal, drift pre-registration. **Admin P1**: Swedish journey pages only, generic forms for platform objects; cross-rules V1–V32 with V20 and V27 retired. **Registers**: H1 and P1 (OCR) closed; new D1 (Postgres standby), W1 (existing chat on umea.se), W2 (Open WebUI chat-copy retention). Conflicts settled during the rewrite: `plans/simplification-pass.md` §11a. | partly superseded by "Weakness review 5: critical path (user, 2026-10-30)": all else; later rows change canvas, journey pages, judge, chat keys, gateway release |
| Site profiles (user request, 2026-10-30) | Checked against umea.se (Sitevision, server-rendered, no `ETag`/`Last-Modified`, sitemap with `lastmod` on all 24,184 URLs: 6,159 HTML, 7,959 PDF): **learned site profiles** per host (main root, boilerplate, breadcrumb, date pattern, verified identity rule, `lastmod` reliability, JavaScript need) auto-configure the HTML stages of the fixed pipeline, with a built-in Sitevision preset; applied automatically on new sources (previewed) and on re-learning only when the sample check passes, else proposed; versioned, audited, one-click rollback; documents are routed dynamically (profile for HTML, `/v1/inspect` for PDFs). `lastmod` incremental runs are the default where reliable; images and video excluded by default; canonical and aliases trusted only when verified. umea.se acceptance page and fixture corpus in P1 (`plans/simplification-pass.md` §13). | partly superseded by "Weakness review 3: freshness (user, 2026-10-30)": Sitevision preset, profile routing, verified canonical; learning is P2 (weakness review 1) |
| Visual pipeline builder (user, 2026-10-30) | Kept as a **P3 item behind a trigger** (register C2) instead of dropped: built only when editors ask for a flow that template parameters cannot express, seen in at least two departments or one recurring platform-team request. Until then fixed pipelines with template parameters and learned site profiles; `IPipelineNode` and versioned JSON pipeline definitions keep it addable. | current |
| Fewer pilot containers (user, 2026-10-30) | Six long-running containers in the pilot (`rag-api`, `rag-admin`, `rag-worker`, `rag-parser`, `rag-clamav`, `postgres`) plus the one-shot migrator: **no Redis in P1** (admin sessions in Postgres, rate limits and the query-embedding cache in process behind `IRateLimitStore`/`ISharedCache`; Redis returns with the first multi-host deployment in P2), and **pgBackRest inside a derived Postgres image** (no sidecar). Merging api and admin, ClamAV and parser, or worker and api was rejected (`plans/simplification-pass.md` §14). | current |
| Ask-a-file workspaces (questions, 2026-10-30) | Self-service knowledge bases from uploaded files, as a light kind of collection (`Kind = workspace`) reusing the whole pipeline, not Open WebUI's built-in Knowledge: **P2**, after the pilot; owned by **teams and individuals**; created and managed in a **small self-service area in our app** (rag-admin's BFF, open to all staff), chat in Open WebUI where each workspace appears as a model to its members; **whole-document tasks from the start** (per-document summaries made on-prem at indexing, full-text mode when a workspace fits the model's context). Plan: `plans/workspaces/plan.md`. | current |
| Many assistants per department (questions, 2026-10-30) | `plans/simplification-pass.md` §15: knowledge only through direct links (skills carry know-how and tools, never collections); one assistant type with the mode derived from tool-bearing skills (no `rag`/`agent` choice); light assistant versioning in **P1** (draft, test, publish, history with diff and restore, small per-assistant test-question set re-run on publish, advisory); owner-team edit scoping in **P2**; plus copy assistant, assistant templates, a department map with impact notices, unused-assistant attention items, description and contact owner per assistant. Assistant versions are `AssistantVersions` snapshots (the published one is served; `Mode` removed); cross-rules now V1–V34 (V20, V27 retired; 32 rules). | partly superseded by "Weakness review 1: P1 scope (user, 2026-10-30)": knowledge by direct links, one assistant type, draft/publish with history and restore |
| P0 set-up (questions, 2026-10-30) | **Code lives in this repo** (`ume-rag-platform`): the layout of `plans/platform/repo-and-deployment.md` §1 at the repo root beside the design folders (`plans/`, `platforms/`, the numbered documents), no separate `ume-rag` repo; solution, namespaces, image repositories and Compose project keep the `ume-rag`/`Ume.Rag` names. P0 is built in **two parallel streams** after a short **Stage 0** done by the main session (shared files fixed first): stream A platform and the ingestion side, stream B application and the serving side (`plans/p0/`). The **combined gateway release is agreed in P0 and built in P1**, before the pilot; RAG develops against `Ume.Rag.FakeGateway` until then. Items that need other people (Entra package 1, gateway-team agreement, backup target and key escrow with IT, test-host access) are **owned by the user**, outside both streams. | partly superseded by "Interim gateway for P1a (user, 2026-10-31)": code in this repo, two streams after Stage 0, user-owned external items |
| Weakness review 1: P1 scope (user, 2026-10-30) | `plans/weakness-review.md` §1: **P1a walking skeleton** (URL list, umea.se crawl with a hand-checked Sitevision preset as a manual profile, upload, hybrid retrieval with citations, chat API, one assistant in Open WebUI) then **P1b pilot hardening**; moved to **P2**: assistant version diff, test-question sets, templates, copy, department map, site-profile learning and re-learning, the crawl frontier shared between sources, the nightly answer judge, publish plans from effect tags (P1 publish re-processes the collection, with report and keep/roll back), adaptive embedding concurrency. Assistant draft → publish with history and restore stays in P1. | current |
| Weakness review 2: answer quality (user, 2026-10-30) | `plans/weakness-review.md` §2: a **pilot gold set** written with the department (100–150 answerable, 30–50 off-topic and near-miss, ≥ 10 temporal questions; `human` layer, the start of B1) is the headline metric; a **people go-live gate** on hand-graded answers (starting thresholds: correct ≥ 80 %, citation precision ≥ 90 %, false not-found ≤ 10 %, off-topic not-found ≥ 80 %); pilot success = gate met at go-live and at the end, and the department continues; `low` coverage from the vector side alone until the keyword floor is calibrated, both thresholds set from answerable and unanswerable questions; hybrid and the decompounder stay only if they beat vector-only on the gold set. | current |
| Weakness review 3: freshness (user, 2026-10-30) | `plans/weakness-review.md` §3: per-collection **age decay** after fusion (off by default, on for PDFs in the `website` template; replaces "no recency boost"); `demote` URL patterns beside include/exclude; a traversal is complete at **≥ 99 % of seeds resolved** (failed seeds' documents never deletion candidates in that run); URLs leaving the sitemap are re-requested and become `Gone` on 404/410; the `lastmod` skip only after a 500-URL reliability sample. | current |
| Weakness review 4: data protection (user, 2026-10-30) | `plans/weakness-review.md` §4: the pilot runs **history mode `none`** until A1 is answered, so chat bucket keys, content encryption, crypto-shredding and conversation review are not built in P1 (schema kept); a **DPIA** (register G1, owner the user with the data protection officer) starts in P0, covers Open WebUI's own store as the primary copy and decides W2; runbook section for public-records requests; a fixed Open WebUI banner against confidential input; the pilot client keeps `internal` only for EU providers under a processor agreement, else its ceiling drops to `onprem`. | partly superseded by "M2 and test hosting (user, 2026-10-30)": history mode `none` until A1, DPIA scope, records runbook, banner, EU processor rule |
| Weakness review 5: critical path (user, 2026-10-30) | `plans/weakness-review.md` §5: the gateway release is **split**: release 1 before the pilot (feature B residency ceiling, ADR 018, FakeLlm `dimensions`), release 2 in P2 (member groups ADR 019, delegation ADR 020); pilot attribution through the pilot team's per-level gateway keys (reverses "no interim per-team keys" for that team); **on-prem model capacity measured in P0** (checklist #22, register M2); fallback **one Open WebUI instance per department** until W3/E1 are solved, E1 and W3 spikes in late P1; every phase-blocking register item has a named person and a date before the phase starts. | partly superseded by "P1b briefs (main session, 2026-10-31)": split gateway release, pilot per-level keys, per-department Open WebUI fallback, named owners |
| P0 parser image (user, 2026-10-30) | The KB NER model (`KBLab/bert-base-swedish-cased-ner`) is **baked into the parser image from P0** (+0.5 GB, not loaded until the gated `/v1/ner` lane; P0 size and memory checks `to-verify.md` #78, #80 need it). The parser listens on **port 8000**: plain HTTP in dev (AppHost contract from Stage 0), **TLS on in production Compose** (`PARSER_TLS_*`, P0 #14), replacing the design's 8443 (PR #9) | current |
| P0 Postgres image in CI (user, 2026-10-30) | Hand-off H3 is met by **building `ume-rag/postgres` in the `backend` job** (`ume-rag.local/postgres:ci`, `UME_RAG_TEST_POSTGRES_IMAGE`), so every PR's integration tests run on the image it builds; images are pushed to ghcr only by `release.yml`. Local runs keep the stock pgvector image (PR #5) | current |
| P0 parser contract (user, 2026-10-30) | **No `JsonSchema.Net` binaries** (Open Source Maintenance Fee Agreement): P0 contract tests use a test-only subset validator; P1 picks a permissively licensed JSON Schema 2020-12 validator (Corvus.JsonSchema first) or builds `JsonSchema.Net` from source (`to-verify.md` #17). The Docling option-set cache (≈ 0.5 GiB each, three until then) is sized with the P1 RSS watchdog (#65). `/v1/inspect` answers 501 `not_implemented` in P0 (PR #16) | current |
| P0 session 7 (main session, 2026-10-30) | **`DOTNET_GCHeapHardLimitPercent=0x4B`** on every .NET container: GC settings in the environment are hexadecimal, and `75` is read as 117 % and ignored (measured, PR #31, to-verify #124). The query-embedding gateway step is **`x-ume-step: query-embed`** everywhere (knowledge design §5.2; performance pass §3.9 aligned). **`audit."AuditLog"."Service"`** is the container/OTEL service name (`rag-admin`, `rag-worker`, `rag-api`; admin design §4.4 aligned, review of #38). The pilot Open WebUI is pinned to **v0.12.3** until a later release passes the 7-day rule (PR #31). | partly superseded by "P1b briefs (main session, 2026-10-31)": GC hex limit, `query-embed` step name, `AuditLog.Service`; pin bump at go-live review |
| P1 pilot sign-in (user, 2026-10-30) | **The pilot signs in with Keycloak, not Entra ID**, and that Keycloak is **shared with the LLM gateway** (which so far runs only in dev, so both go live together for the pilot): its own Compose project on the **gateway's production host**, defined in the gateway repo, database `keycloak` in the gateway's Postgres (covered by its backups), one realm `ume` holding both systems' clients and roles (no name collisions; `departmentCodes` shared; per-client "full scope allowed" off), RAG contributing its clients and `rag-*` roles as a realm-additions file; each repo keeps its own dev Keycloak in its AppHost. **Accounts: local Keycloak accounts for the pilot (admins with OTP), LDAP federation to the municipal AD later** when IT provides a read-only bind account (no app change). Sizing per Keycloak's guide: ≈ 1.25 GB RAM per instance, which is why it is not on the fully allocated 16 GB RAG host. It holds the admins' and pilot users' accounts; the admin UI (BFF) and Open WebUI both use it over OIDC, with the `rag-*` roles and the `departmentCodes` claim from stream B's realm (#35, a production realm without test users). Reason: as simple as possible, and no IT approval on the pilot's critical path. **Entra ID moves to P2**: approval package 1 (`infra/entra/`, PR #36) is kept but not submitted (H7 deferred); switching is configuration (`Oidc:*`), not code. Supersedes "Entra ID in production" for the pilot (identity plan §1). | current |
| P1 planning (user, 2026-10-30) | **P1 runs as two streams on P0's seam** (P0 README §6), planned now while P0's tail (user hand-offs, release) finishes; no P1 code before `v0.1.0`. **The first briefs cover P1a only** (walking skeleton, done = a pilot member gets a cited answer from umea.se content in Open WebUI); P1b's briefs are written when P1a is done, from what it taught. **Stream B owns all admin UI and admin API work, ingestion pages included**; stream A delivers the worker side (job kinds, payloads, result JSON, preview and evaluation engine) against written contracts in `contracts/`. P1a itself has no journey pages (Scalar and the generic forms, weakness review §1). | partly superseded by "Code first, live testing last (user, 2026-10-31)": two streams on P0's seam; stream B owns admin UI and API; written contracts |
| M2 and test hosting (user, 2026-10-30) | **Register M2 is triggered: there is no on-prem model hardware yet.** Until there is, **everything is EU-hosted through the gateway**: the pilot's embedding profile is an EU provider's embedding model (moving to on-prem BGE-M3 later re-embeds every chunk), and the condense and answer models are EU-hosted; the pilot client's ceiling allows `Eu`, and the processor agreement is listed in G1. The H8 capacity run waits for hardware. **Testing runs on the cheapest possible host, and production-style tests are postponed as long as possible:** P0 item 12 (deploy `v0.1.0` to the test host), the test-host restore drill and to-verify #90 move to before go-live (P1b). **W1 answered:** `chat.umea.se` is built by the user's team (Team Turkos). **G1 (DPIA) postponed** to P1b, still before go-live. Gateway release 1, its production deploy and the shared Keycloak are built in a separate session in the gateway repo. | current |
| P1a plan answers (user, 2026-10-30) | The P1a plan is `plans/p1/` (checklist, briefs, progress, prompts). **P1a is shown locally** with the pilot member present (no demo host). **P1a calls the gateway as it is** (v0.8.5: keys enforce `AllowedResidencies`, answers carry `x-ume-residency`, which RAG checks); release 1 lands in P1b before go-live. **File upload stays in P1a.** **Accepted risk:** with no on-prem hardware there is no on-prem fallback if the DPIA (G1, P1b) refuses EU processing; the pilot would then wait for hardware or another provider. Main session: the embedding ceiling becomes `EmbeddingProfiles.MaxResidency` (B migration, P1a item 2); Scalar only in P1a, generic forms in P1b; re-embedding onto on-prem BGE-M3 is a P1b/P2 item. | current |
| P0 integration, stream A (main session, 2026-10-30) | **Stream A's merged P0 PRs are settled in the design** (#5–#50): results folded into `to-verify.md` (22 rows; #63 and #72 move to P1, #151 to P2), each PR's "Design deviations" written into the detail documents as built. Choices made here: blob keys use only lower-case `a–z0–9-_.`, so `{host}` is lower-case punycode with a port as `_{port}` and `{fetchedAt}` is Unix seconds (data-model §9); blob tombstones are database rows, not store markers (#33, issue #34); the pilot keeps Open WebUI's legacy OIDC callback `/oauth/oidc/callback` that P0 built, and moves to `/oauth/oidc/login/callback` everywhere at the next Open WebUI pin bump (identity §5.3); parser and contract CI run in their own always-running `parser.yml`, and `release.yml` calls both workflows (#50). **Open:** `Group.Create` with ownership vs `Group.ReadWrite.All` for the Entra deployer (P2, asked of IT with H7); identity design not yet reworked for the Keycloak pilot. Stream B's deviations are settled when its last PRs merge; then stream A cuts `v0.1.0` (after #27). | current |
| P0 integration, stream B (main session, 2026-10-31) | **Stream B's merged P0 PRs are settled in the design** (#23–#25, #32, #35, #38, #39, #45, #46, #53–#55; its results were already folded at the stream A sync point, no rows added since). Written into the detail documents as built: the dev realm file is `dev/keycloak/ume-realm.json` (Keycloak's import needs `<realm>-realm.json`), it lists Keycloak's built-in client scopes, `ume-rag-api-dev` carries `service_account`, `rag-group-member` has `rag-viewer`, Keycloak pinned at 26.6.4 by digest (identity §11, repo-and-deployment §4.2); refused sign-ins redirect to `/?signin=refused&reason=…` (identity §1.7); `SourceAddress` is the connection address until `Settings.TrustedProxies` arrives in P1a (identity §1.2); the org sync is a config write under the version lock and replaces member groups only when the catalogue carries them; `RetentionService` at 03:00 Stockholm in 5,000-row batches as constants; `Keys.TeamId` FK → `Teams` (admin §2.2, §4.10, data-model §2.1); adapted UI components, exact npm pins and `npm ci` only (admin §5.1); the pooler-safe scan is a literal scanner that skips direct-connection code (performance pass §3.1). **Moved to P1a:** the admin's `cancel-run` enqueue (needs `Runs`, item 14). **Settled with #56:** H6 and H8 do not block J1 (test-host work moved to P1b on 2026-10-30); stream B may start P1a item 2 (schema part 1) before J1 as a draft PR merged only after `v0.1.0`. | current |
| 7-day rule in development (user, 2026-10-31) | **The 7-day package rule does not apply while the platform is in development** (until the first production deployment, i.e. before the pilot goes live): newer NuGet, npm, PyPI releases and images may be pinned. Exact versions and image digests stay required, and the rule applies again before go-live (P1b), when every pin younger than 7 days is reviewed. First uses: ClamAV `1.4.6_base` for `v0.1.0` (#27, PR #67); the `admin-ui` audit fix (#61) may take the current `minimatch`/`glob` instead of waiting, and the dated exceptions of #64 are then removed. | current |
| Licence (user, 2026-10-31) | **Ume.Rag is licensed under EUPL-1.2** (`LICENSE`, PR #65): copyleft that also covers running a changed copy as a network service, official texts in every EU language, compatible with every dependency checked in `plans/licence-review-p2.md` (incl. the LGPL-3.0 `sv_SE` list, #52). The repository stays private; the licence takes effect on publication or distribution, which waits for the municipality's formal approval and the third-party notices (#51, #52). **The gateway gets the same licence** (it has no `LICENSE` today, and Ume.Rag copies gateway files by design): carried to the gateway session. | current |
| Interim gateway for P1a (user, 2026-10-31) | A **test/dev gateway instance** on a fixed host (HTTPS, public certificate) with EU embedding and chat models is the **interim J10** for P1a development; FakeGateway stays for CI and tests. Address and keys only in user secrets or `deploy/.env` and secret files, never in the repository. **The same host's Keycloak holds the shared realm `ume` (J11).** U-b (the embedding model and its dimensions) is named once the catalogue is listed with a key (`plans/p1/README.md` §4). | current |
| Low CPU during development (user, 2026-10-31) | **The base Compose files keep every container's `cpus` cap at 2 or less while in development** (postgres 3 → 2, parser 4 → 2 with one worker × 2 threads), so the stack runs on a 2-core host or CI runner. `compose.host-24g.yaml` carries the pilot caps (postgres 3, parser 4); the pilot CPU sizing is re-set before go-live with the R1 test (repo-and-deployment §6.2, §8.3). Memory limits unchanged. Slower parsing in development is accepted. | current |
| Code first, live testing last (user, 2026-10-31) | **P1 builds as much code as possible before anything is tested live or published.** Both streams implement every P1a code item against FakeGateway, fixtures and the dev Keycloak, then continue into P1b's code without pausing at the P1a demo. The live block comes at the end, in one go: the real gateway and EU routes (J10), the umea.se crawl (U1/J12, items 19–20), the gold set (J13, item 21), the shared Keycloak (J11), the pilot member's first answer (item 22), the test-host deploy and restore drill. Consequence: **the P1b briefs are written now**, not after P1a (overrides `plans/p1/README.md` "P1b's briefs are written when P1a is done"); fixture work that only reads a few umea.se pages or the sitemap (items 1 and 8) may still run early. | current |
| P1b briefs (main session, 2026-10-31) | `plans/p1b/` written early (code first): 32 items, 19 code and 13 live, hand-offs L1–L12. Settled there (§7): generated evaluation sets built and tested on FakeGateway but not run on the pilot until an on-prem model exists (O1); share lists stay P2 (O2); the W3 spike in P1b, **E1 moves to P2** with Entra (O3); `evaluation.yml` runs against the deployment's own (EU) profile (O4); the `lastmod` sample's first pass starts as soon as U1 is done, not in the end block (O5); H8 is no P1b gate (O6); one Open WebUI pin bump in the go-live pin review, callback change in matching A and B PRs (O8); the preview kind is `stage-preview` (O9). **Open for the user:** O7, the go-live host. | current |
| Keyword stop list before the pilot crawl (main session, 2026-10-31) | Stream B measured statement K (to-verify #5, PR #112) at 375–659 ms p95 for frequent terms on 100k chunks against the 30 ms trigger. The pilot collection is umea.se (≈ 14,300 HTML and PDF documents, #139), not "a few thousand chunks", so **the stop list (terms in > 30 % of the scope's chunks dropped in the .NET query builder, knowledge §6.2, §11) is a P1b code item of stream B, in item 3 with decay and `demote`**, re-measured on the 100k synthetic scope before the live block. `exactScanMaxChunks` stays 10,000 (#6: 27.3 ms on the laptop); re-measured on the test host. | current |
| CI on a self-hosted runner, Actions off for now (user, 2026-10-31) | GitHub-hosted Actions minutes are used up. **Every workflow runs on the self-hosted runner `ume-rag` (`[self-hosted, Linux, X64]`, PR #124)**, but **Actions stays switched off** until the user re-enables it. Until then every PR is verified locally (build, suites under `taskset -c 0,1`, `scripts/check-ownership.py`) with the evidence in the PR body, and merged on that evidence. New jobs use the self-hosted labels. Overrides repo-and-deployment §8's GitHub-hosted runners. | partly superseded by "CI on the self-hosted runner for every pull request (user, 2026-10-31)": every workflow on the self-hosted runner `ume-rag`; scheduled workflows stay off |
| Pilot embedding model, U-b (user, 2026-10-31) | The interim gateway (address and keys outside the repository) serves **`text-embedding-3-large`** from an **`Eu`** provider account. The pilot embedding profile uses it at **1024 dimensions**, requested with OpenAI's `dimensions` parameter (the gateway passes the embeddings body through unchanged): a third of the storage and HNSW memory of the native 3072, the same size as the planned on-prem BGE-M3 and FakeGateway. Every embedding call for a profile (worker embed and canary, admin canary, query embedding, evaluation) sends `dimensions` when the profile asks for it; an on-prem BGE-M3 profile never sends it. `MaxInputTokens` 8191. The gateway deployment's `ContextWindow` is left empty until the gateway checks embeddings per input (gateway session prompt §1). | current |
| CI on the self-hosted runner for every pull request (user, 2026-10-31) | Replaces "Actions off for now" for pull requests: `verify.yml` and `parser.yml` run on the self-hosted runner `ume-rag` for every non-draft pull request (no `ci` label; #151), at no GitHub-minute cost. The main session still runs the local suites (build, Worker.Tests twice, Pipelines, Contract, ownership) and merges when both the local suites and the runner's run are green. GitHub cannot enforce required checks on the private free-plan repository, so "merge only on green" stays a rule. The scheduled workflows (`perf.yml`, `evaluation.yml`, `e2e.yml`) stay off until the test host exists. | partly superseded by "Merge on local evidence, then check main (user, 2026-10-31)": `verify.yml` and `parser.yml` run on the runner for every non-draft PR |
| Merge on local evidence, then check main (user, 2026-10-31) | Refines the row above: the single runner queues behind every PR, so the main session merges a PR on its **local** evidence (build, Worker.Tests twice, Pipelines, Contract, ownership) without waiting for the runner's PR run, and after a batch of merges checks that `main` builds and passes: a `verify.yml` run on `main` dispatched to the self-hosted runner (`gh workflow run verify.yml --ref main`) plus the local suites on `main`. A red `main` is fixed before the next merge. The PR runs still start and are read when they finish. | current |
| Personal data on the municipality's own published site (user, 2026-11-01) | Content the municipality already publishes (the umea.se crawl; the communication department owns and vets the site) is public information. A **source setting `piiMode`** (`hold`, the default, or `report`; in the source's settings, contracts/sources v1) decides what a personal-data flag does in a `public` collection. With **`report`** the flags are still counted on the chunk set, raised as event 5103 and listed for admins as a report for the web editors, but the set is swapped in `live`, not `held`; **personnummer and samordningsnummer still hold** (one on the public site is an error the editors fix). `hold` keeps processing design §9.5 unchanged. The umea.se crawl source gets `report`; `Settings.Engine.PiiOwnDomains` lists `umea.se` and the municipal companies' domains. Found in milestone 1: two umea.se pages were held for e-mail addresses only, because the own-domain list was empty. Refines processing design §9.5 and the weakness review's 'single e-mail address holds a whole public document' finding. | current |
| Pilot set-up inputs, P1a item 20 (user, 2026-11-01) | The pilot collection is **`Turkos`** (slug `turkos`, `public`), owned by the department **DoIT**. The first umea.se crawl covers **one section, Kommun och politik**: start URL `https://www.umea.se/kommunochpolitik`, `include` `www.umea.se/kommunochpolitik` and `…/kommunochpolitik/**` (≈ 1,170 sitemap pages), `piiMode: report`, `maxPages` 2,000; linked files under `/download/` are left out of this first run (the sitemap lists ≈ 8,700 of them for the whole site, which an include of `/download/**` would admit). `Settings.Engine.PiiOwnDomains` stays `umea.se` until the report run shows which municipal-company domains occur. The interim gateway has no Data API key for the org sync yet, so **DoIT is seeded in the local database only** (cost-centre code `dev-seed-doit`); the gateway session provides the `catalog.read` key, and the local department is then replaced by the synced one. The first run found that path globs never matched on umea.se (the verified identity rule maps every node-id URL to `/4.<id>.html` before the scope check); scope patterns are matched on the URL before the identity rule (stream A, P1a item 20 fix). | partly superseded by "Municipal companies' e-mail domains, `PiiOwnDomains` (user, 2026-11-01)": Turkos collection, DoIT owner, Kommun och politik scope, local DoIT seed |
| Municipal companies' e-mail domains, `PiiOwnDomains` (user, 2026-11-01) | After the Kommun och politik crawl's personal-data report (43 pages flagged, none with a personnummer; external e-mail on 18 pages), `Settings.Engine.PiiOwnDomains` is **`umea.se`, `vakin.se`, `umeaenergi.se`, `visitumea.se`** (subdomains such as `edu.umea.se` match their domain). Parties, Region Västerbotten, universities, firms and private addresses stay external. Set through the admin API on the local pilot stack; the seed (`SettingsSeed:PiiOwnDomains`) still has only `umea.se`. | current |
| Gold set drafted by the main session, reviewed by DoIT (user, 2026-11-01) | For P1a item 21 (J13), the main session drafts the candidate gold set from the Turkos crawl: about 120–150 answerable questions with their expected documents, 30–50 unanswerable ones and at least 10 temporal ones. DoIT reviews, edits and approves them before they go in as `human` questions. To limit the bias of questions written from the pages, the draft uses residents' wording rather than page headings, mixes short and long questions, and marks every question as a draft until DoIT approves it. DoIT may also add questions of its own. The owner and date (U-f) come from the user later (J13, item 21). | current |
| No backward compatibility before production (user, 2026-11-01) | Until the user states explicitly that the platform runs in production, **any change is allowed** and nothing has to stay backward compatible: database schemas, migrations, contracts (`contracts/**`), job payloads, APIs, configuration keys and file layouts may change in one step. Don't write expand/contract migration steps, fallbacks for old rows or old payloads, tolerant readers for earlier versions, or compatibility shims, and existing ones may be removed. Development and pilot databases are rebuilt or re-crawled instead (the local Turkos data may be dropped). Two limits stay: the code must still match the gateway's actual API and conventions (an external system), and each PR still lists such changes and their hand-offs to the other stream. When the user announces the first production deployment, compatibility rules apply again from then on (as the 7-day rule). | current |
| Design documents: one layer (user, 2026-11-01) | The cross-cutting passes (weakness review, simplification pass, performance pass, consistency pass) are folded into the detail documents and no longer bind. Two layers of truth remain: this table (rows with status `current`) and the detail document for the area, written in the present tense. Each pass ends with a ledger of where every rule went and keeps only the reasoning; the performance pass stays binding as reference for its capacity model, §3 tables, scaling triggers and scale tests (its header lists them). PR history is not kept in design text (git has it): the design sync edits a sentence in place. `scripts/check-doc-refs.py` runs in `verify.yml` (job `doc-refs`) so section citations and links can't break. Section numbers and linked headings never change (`plans/doc-restructure.md`). | current |

Sections 3–9 above describe the original idea; where they differ from the plans, the plans and this table's binding rows win.

Comparable platforms were analysed before any implementation (`platforms/`: Dify, RAGFlow, Onyx, Open WebUI, Eneo,
Kernel Memory/.NET ecosystem, OpenSearch); the synthesis is `platforms/00-comparison.md` and its lessons are folded
into sections 2, 5, 5a, 6, 7, 8 and 9.

## 11. Open questions for the detailed plan

1. **Open, gates stored chat content** (no longer a pilot blocker: the pilot runs history mode `none`, weakness review
   §4; owner named before P0 ends): are chat logs allmänna handlingar, and is a gallringsbeslut needed for 90-day
   deletion? (Default retention decided: 90 days; deletion by crypto-shredding.)
2. ~~PII in sources~~ Answered: detect and flag at ingestion; flagged documents blocked from public collections.
3. ~~Embedding model~~ Answered: BGE-M3 on-prem via the gateway, confirmed by evaluation sets.
4. ~~SharePoint access~~ Answered: one `rag-ingestion` registration with `Sites.Selected`, granted per site by admins.
5. ~~Evaluation~~ Answered: evaluation sets per collection with gates (`plans/ingestion-worker/` §7).
6. ~~Who runs it~~ Answered: the gateway team builds and operates both, on separate hosts and databases.
7. ~~MVP scope~~ Answered: see decisions table.
8. ~~Auto-apply thresholds~~ Answered: confidence ≥ 0.9 over ≥ 20 documents; rollback on a 3-point recall@10 drop.
9. Who builds the Swedish benchmark question set, and from which content? (Postponed.)
