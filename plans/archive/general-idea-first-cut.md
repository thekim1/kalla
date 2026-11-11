# General idea: the first cut (§3–§9, §11)

> Archived 2026-11-08 from `01-general-idea.md`, which keeps the goal and the design principles (§1–§2). This is the
> first sketch of 2026-10-29, written before the design documents; it does not bind, and much of it was changed later.
> Section numbers are the original ones. What binds: [`../decisions.md`](../decisions.md) and the design documents.

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
