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
5. **Fast request path.** Retrieval is one Postgres query (hybrid search) with warm caches; ingestion is entirely
   off the request path in a worker.
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
| **Admin API + Admin UI** | control | Departments, teams, collections, sources, assistants, keys, gateway-key links, sync status, retention, audit log. Own app, same design system and login pattern as the gateway admin. |

Chat API and Knowledge API may start as **one deployable with two endpoint groups** and split later if needed; what
matters is that the boundary is an HTTP API, not shared internals.

## 4. Domain model (first cut)

- **Department → Team**: same structure and codes as the gateway (cost-centre code, team code), so roles from the
  IdP (`departmentCodes`, `teamCodes`) mean the same in both systems.
- **Collection**: a named knowledge base owned by a team, with a classification. All collections share the on-prem
  platform embedding profile (BGE-M3). Public collections may only contain public sources.
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
- **Gateway link**: per team, the encrypted gateway virtual key used for all model calls of that team.

## 5. Ingestion

1. **Trigger**: schedule per source, manual "sync now", upload, or a SharePoint change notification.
2. **Discover**: sitemap-first crawling within scope, robots.txt respected, polite rate limits; URL lists fetch exactly
   their URLs; SharePoint uses Graph delta queries, so only changes come back.
3. **Change detection**: ETag / Last-Modified / content hash; unchanged documents are skipped, removed ones deleted
   (with their chunks).
4. **Parse**: HTML boilerplate removal in .NET (AngleSharp); PDF/Office/scans through the Docling sidecar into
   Markdown with headings and tables kept.
5. **Chunk**: structure-aware (headings, sections), with the document title and heading path prefixed to each chunk.
6. **Embed**: batched through the gateway's `/v1/embeddings` with the team's gateway key; an on-prem multilingual
   model is the expected default (Swedish quality to be benchmarked).
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
- *Job classes* (crawl, parse, embed, sync) with their own concurrency limits, fair per department, so one large
  crawl cannot starve everyone else (Eneo and Dify run everything through one worker queue).
- *Crawl reports*: every run lists failed pages with reasons; per-department limits on concurrent crawls.
- *Source settings tagged by effect* (identity, scope, behaviour, cosmetic) so an edit triggers the smallest
  re-sync (Onyx `field_policy.py`).

## 5a. Pipelines and the visual builder

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
context), declared as the output type of the chunk node so the index knows what it receives.

**Auto-parse and learning.** The *Auto-parse* node chooses a parse strategy per document:

1. **Rules first**: learned and admin-made *parse profiles* (e.g. "PDFs from `*.umea.se/kallelser/*` → OCR";
   "domain X needs headless render"). Fast and explainable.
2. **Heuristics, per page**: detected properties pick a default for each page, as RAGFlow does: a clean text layer
   is used directly (PdfPig, fast), an empty or garbled one goes to OCR, a page with nothing found is re-rendered at
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
  connector's built-in hybrid search requires every keyword to match, so we do not rely on it.)
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
Ours: assistant and key configuration from an in-memory cache invalidated over Redis (as in the gateway), one
retrieval query, conversation writes off the request path. Overhead is measured with the gateway's benchmark harness
and must stay a small fraction of Eneo's.

## 7. Identity, keys and the gateway

| Caller | Authenticates with | Gets |
|---|---|---|
| Staff in the admin UI | Municipal OIDC (Entra ID / Keycloak / AD FS), BFF cookie | Roles as in the gateway: platform admin, department admin, team admin, viewer |
| Staff-facing apps (Open WebUI, intranet) | OIDC user token, or a RAG API key | Internal assistants their team/department allows |
| Integrations | RAG API key (team-scoped) | Named assistants and collections |
| Public web widget | No login; origin allow-list + rate limit | Only assistants marked `public` |
| These services → gateway | One platform service key with `x-ume-on-behalf-of: <team id>` and `x-ume-max-residency: Eu \| OnPrem` (interim: per-team keys; gateway releases B → C → A) | Gateway budgets, PII routing, residency and usage per team |

The RAG services issue **their own keys** in the gateway's style rather than reusing gateway keys: the two systems
stay independently deployable and their key stores do not couple. Cost attribution uses the gateway's new delegated
attribution (`plans/gateway-changes/`); per-team keys are only the interim until that ships.

Two key kinds, as in Eneo: **public keys** (`pk_`, read-only, bound to allowed origins, for the web widget) and
**secret keys** (`sk_`, for integrations).

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
  - title and follow-up generation go to a separate task model.

  Licence: BSD-3 plus a branding clause. It is free at any scale, but the "Open WebUI" branding must stay above 50
  users unless there is written permission or an enterprise licence. That is fine for a branded pilot. If
  municipal branding is needed, use our own client or the widget instead.
- **Web widget**: a small script for the municipal website, separate static package, calls the Chat API.
- **Teams bot**: later, as a thin client of the Chat API.

## 9. Technology

- .NET 10, ASP.NET Core minimal APIs, EF Core, Aspire 13 for local orchestration (same as the gateway).
- Microsoft.Extensions.AI for the gateway clients (OpenAI-compatible); `CommunityToolkit.VectorData.PgVector` and
  `Pgvector.EntityFrameworkCore`, with our own hybrid search SQL.
- AngleSharp + SmartReader (HTML main content), PdfPig (fast PDF text path and detection), Microsoft.Graph
  (SharePoint delta).
- Pipeline engine: a small custom DAG executor (typed ports, ~1–2k lines) on a Postgres queue (`SKIP LOCKED`).
  Not Elsa, Agent Framework Workflows or Microsoft.Extensions.DataIngestion (preview, linear pipelines).
- Not used: Kernel Memory (archived), Semantic Kernel, TickerQ (commercial licence since 2026-10-13).
- PostgreSQL (pgvector, Swedish FTS with Hunspell): run as our own container so custom dictionaries can be loaded
  (check whether the Azure managed offering allows this), Redis for caches/rate limits/invalidation.
- Python sidecar: Docling, behind a small HTTP API; replaceable.
- Vue 3 + the gateway design system for the admin UI.
- Release bundle with Docker Compose (Azure VM, on-prem Docker host) and Kubernetes manifests; storage for uploaded
  files on a volume or S3-compatible / Azure Blob behind an interface.

## 10. Decisions (2026-10-29)

| Topic | Decision |
|---|---|
| Builder scope | Full canvas for **ingestion** pipelines. The **query side** is a fixed, linear sequence of steps per assistant (query rewrite, hybrid weights, rerank, top-k, filters, prompt template), set in a form with a test panel. It opens up to the canvas later, once test-question sets per assistant show that a change helps. Keeps the latency-critical path predictable. |
| Who edits pipelines | Department editors with a *pipeline editor* right: copy templates, change parameters, add nodes from the approved catalogue. The platform team owns templates and the node catalogue, and can lock required nodes (e.g. PII mask, Index). |
| Learning | Parse profiles are **applied automatically above a confidence threshold**, with guardrails: minimum sample count, scoped to the department's own sources, trial run alongside the current strategy before taking over, automatic rollback on quality drop, notification + audit entry, kill switch. Below the threshold: proposed for approval. |
| Chat history | Kept **90 days** by default (configurable per assistant). **Blocker for the pilot:** confirm with the registrar/archivist whether chat logs are allmänna handlingar and whether deletion needs a gallringsbeslut. |
| First version | URL lists, website crawl, file uploads; Chat API + Knowledge API; Open WebUI for one pilot department. |
| Search backend | Postgres only for the MVP behind our own search interface; OpenSearch as an optional backend when a measured trigger fires (section 6). |
| Postgres hosting | Our own Postgres container everywhere, Azure included (needed for the Swedish Hunspell dictionary). |
| CI/CD | GitHub Actions for everything, as the gateway does (build, test, release, and the app-registration workflows). |
| Key service | **Postponed**: the pilot stores master keys as protected files (file key provider, no `sensitive` data); Azure Key Vault is the target, adopted later with a re-wrap (`plans/postponed-decisions.md` K1). |
| Entra IaC | Bicep with the Microsoft Graph extension, applied by a GitHub Actions workflow. |
| ClamAV updates | Directly from database.clamav.net; no internal mirror. |
| Self-hosted runner | Open: IT security to approve one runner on the test host for the R1 test and nightly evaluation. |
| Database separation | Own Postgres instance and own Redis, separate from the gateway's; no shared database or server (`plans/platform/` §2). |
| Classification | Levels `public`, `internal`, `sensitive`; the highest (`sensitive`) may only use on-prem models through the gateway. |
| Swedish benchmark | Postponed. Until then a small smoke set of questions made by the team; chunking and backend choices are revisited when the benchmark exists. |
| Plans | One folder per service under `plans/`, each with a `plan.md`; detailed per service later. |
| Architecture review | All seven improvements in `02-architecture-review.md` adopted: resource zones, shared document store, one on-prem embedding profile, gateway delegation + residency ceiling, client registry with classification ceilings, evaluation as a platform component, versioned fail-closed configuration. |
| Skills | Both know-how packages and tool bundles; assistants are built from skills (`plans/tools/`). |
| MCP | All three roles: platform as MCP server, assistants call MCP tools through a tool hub, platform hosts municipal MCP servers. |
| Tool actions | Read-only first; write tools later, always with user confirmation. |
| Identity | Entra ID; user-delegated tool access via On-Behalf-Of designed now, built in a later phase (`plans/identity/`). |
| Classification | Three levels stay; `sensitive` is the municipality's highest category. |
| Chat-log protection | Configurable per level (history mode, retention, encryption, review rules) within limits set by platform admins; per-conversation envelope encryption with crypto-shredding (`plans/platform/` §8a). |
| Separate data tier for `sensitive` | Designed now (tier resolver), decided before the first `sensitive` source is onboarded. |
| Residency per level | `public` any provider; `internal` on-prem or EU; `sensitive` on-prem only; for every model call, ingestion included. |
| MCP clients | Ceiling `public` by default; an admin may raise a vetted client to `internal`; never `sensitive`. |
| Feedback in evaluation | Only as editor-rewritten questions without personal data. |
| Skills timing | P2, with agent assistants; P1 assistants are prompt + collections. |
| Identity compatibility | Same identity model as the gateway: same OIDC/BFF, claims, config keys (`UME_RAG_OIDC_*`), key and Data API patterns, security events; preferably one shared admin registration with `rag-*` roles. Department scope from groups is added to both systems. No registration needed before the pilot. |
| Identity detail (step 3) | `plans/identity/design.md`. Member groups are group claim values (Entra object ids; names on Keycloak/AD FS); Graph lookup failure narrows to claims-only, never fails open. `ume-rag/infra/entra/` (Bicep) owns the shared admin registration incl. the gateway's roles; consent, role assignments and secrets stay manual. The RAG BFF uses its own client secret, as the gateway (certificates later, for both). Policy denials above ceiling or outside audience answer 404. |
| Gateway changes (step 4 questions) | Delegated requests charge the effective team + department budgets, with the platform key's own budget and rate limit as an overall cap; any key may send the residency ceiling; three releases B → C → A; drafts stay in this repo until implementation. |
| Parser formats and pictures (step 5 questions) | Pilot parses PDF, Office (incl. legacy .doc/.xls/.ppt via LibreOffice headless), OpenDocument, RTF, images (OCR), email (.eml/.msg with attachments as separate documents), plain text/Markdown/CSV. Pictures: captions and OCR text only in P1; picture crops available on request for a P2 describe-pictures node. |
| Parser detail (step 5) | `plans/parser/design.md`: worker keeps HTML, text, Markdown, CSV and clean PDF pages; parser handles the rest through Docling's native backends (legacy Office via its LibreOffice path behind our sandboxed wrapper, ODF native, email via `python-oxmsg`), plus a cheap `/v1/inspect`; multipart contract; the worker stores `result.json` and the `DoclingDocument`, so re-chunking never re-parses; own supervisor with pre-warmed processes; OCR engine always explicit. Zip and macro-enabled Office refused in P1 (Z1); email senders/recipients metadata only. |
| Ingestion worker (step 6 questions) | Detail in three documents (engine; connectors; processing). Names for personal-data detection by a Swedish NER model in the parser image; patterns in the worker. Default sync: URL lists nightly, crawls weekly at night. Default chunks: heading-split, ≈ 400 tokens, ≈ 15 % overlap, heading path prepended, flat in P1. |
| Ingestion engine (step 6a) | `plans/ingestion-worker/design-engine.md`: reconciliation (`EnsureCurrent` + nightly drift sweep), job segments by node job class, Merkle stage hashes for sharing, execution-id fence, at-least-once with effectively-once effects, per-department fairness and parse slicing, cron scheduler with jitter, derived run counters, isolated preview, publish = plan → gate (shadow sets) → confirm, rollback never gated. Shared documents update every referencing department at once (follow runs). |
| Crawler (step 6b questions) | User agent `UmeaKommunBot/1.0 (+about page)`; robots.txt always honoured; page-level `noindex`/`nofollow` honoured on external hosts, ignored on allow-listed municipal hosts; default 10 000 pages and depth 10 per crawl; off-site links never followed unless allow-listed. |
| Crawler build vs buy | Own crawler (6b), not a self-hosted Firecrawl: it would add a second queue stack and an AGPL component while the hard parts (shared frontier, change detection, deletion guard, crawl rules, SSRF guard) stay ours. Firecrawl-like services only as a possible renderer backend for anti-bot sites. |
| Connectors (step 6b) | `plans/ingestion-worker/design-connectors.md`: identity = final URL after redirects, `rel=canonical` never trusted; shared frontier with per-source claims and a rate-paced releaser; full traversals with conditional GETs, deletions only after complete traversals via a guarded reconcile phase; DNS-pinned egress guard plus firewall canary; renderer fed entirely by worker-side fetches; rendered and origin content as separate document variants; uploads by content hash with names per source; ClamAV fail-closed. External hosts open to all departments (admins can block); SharePoint ACLs not mirrored; malware metadata only. |
| Processing (step 6c questions) | Names by KBLab BERT NER (MIT) in the parser image; publish-gate override only by platform admins with an audited reason; evaluation sets of ≤ 100 generated questions, gates sample 50; a new collection's first publish needs an absolute recall@10 floor (≈ 0.7). |
| Processing (step 6c) | `plans/ingestion-worker/design-processing.md`: per-page parse rules on inspect signals; built-in parse profiles with observations recorded from P1; BGE-M3 tokenizer in .NET; 400-token budget incl. heading path, sentence overlap, row-group tables; PII patterns in .NET plus `/v1/ner` on its own lane and a `ner` job class; public collections blocked for personnummer/samordningsnummer/external email always, names and contacts only in list-like documents; noise-only changes re-key chunk sets; evaluation on documents with intervals; sub-corpus gate with escalation; collections `building` until 0.70; flat chunks with section expansion in the knowledge API. |
| Knowledge API (step 7 questions) | Top-k default 10, max 50. Citations without a public URL link to an access-checked download via 7-day signed grant links (staff network only, access re-checked at each download). No query rewriting in P1. No recency boost. |
| Knowledge API (step 7) | `plans/knowledge-api/design.md`: shared `Ume.Rag.Retrieval` engine (API, evaluator, test panel); keyword search in parallel with the query embedding, then vector, then one hydrate batch, RRF in .NET; exact scan for scopes ≤ 10,000 chunks; degrade to keyword-only if embedding fails; coverage per collection by cosine distance; dedup + one variant per URL + max 3 chunks per document; section expansion from neighbouring chunks (`OverlapChars`); no result cache; query text never stored. |

Sections 3–9 above describe the original idea; where they differ from the plans, the plans and this table win.

Comparable platforms were analysed before any implementation (`platforms/`: Dify, RAGFlow, Onyx, Open WebUI, Eneo,
Kernel Memory/.NET ecosystem, OpenSearch); the synthesis is `platforms/00-comparison.md` and its lessons are folded
into sections 2, 5, 5a, 6, 7, 8 and 9.

## 11. Open questions for the detailed plan

1. **Open blocker for the pilot** (owner to be named later): are chat logs allmänna handlingar, and is a
   gallringsbeslut needed for 90-day deletion? (Default retention decided: 90 days; deletion by crypto-shredding.)
2. ~~PII in sources~~ Answered: detect and flag at ingestion; flagged documents blocked from public collections.
3. ~~Embedding model~~ Answered: BGE-M3 on-prem via the gateway, confirmed by evaluation sets.
4. ~~SharePoint access~~ Answered: one `rag-ingestion` registration with `Sites.Selected`, granted per site by admins.
5. ~~Evaluation~~ Answered: evaluation sets per collection with gates (`plans/ingestion-worker/` §7).
6. ~~Who runs it~~ Answered: the gateway team builds and operates both, on separate hosts and databases.
7. ~~MVP scope~~ Answered: see decisions table.
8. ~~Auto-apply thresholds~~ Answered: confidence ≥ 0.9 over ≥ 20 documents; rollback on a 3-point recall@10 drop.
9. Who builds the Swedish benchmark question set, and from which content? (Postponed.)
