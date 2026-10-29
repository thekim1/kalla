# Platform comparison: what to copy, what to fix

Date: 2026-10-29. Sources: the per-platform reports in this folder (each with file paths, issue links and a
verified/inferred split). This page is the synthesis; follow the links for evidence.

| Report | Version analysed |
|---|---|
| [eneo.md](eneo.md) | Eneo v2.1.1 code, v2.2.3 benchmark |
| [dify.md](dify.md) | Dify 1.18.0 (`a544e2c`) |
| [ragflow.md](ragflow.md) | RAGFlow v1.0.0-rc2 (`a609e62`), mid Python→Go rewrite |
| [onyx.md](onyx.md) | Onyx `a4e97fc` (now OpenSearch 3.6, Vespa removed) |
| [open-webui.md](open-webui.md) | Open WebUI v0.12.5 (`f7ac7ee`) |
| [kernel-memory-and-dotnet.md](kernel-memory-and-dotnet.md) | Kernel Memory (archived) + .NET packages |
| [opensearch.md](opensearch.md) | OpenSearch 3.9 vs Postgres options |
| [bifrost.md](bifrost.md) | Bifrost `a09cea0` (Go LLM gateway with MCP and skills); lessons applied in `plans/tools`, `plans/identity`, `plans/gateway-changes` |

## 1. At a glance

| | Eneo | Dify | RAGFlow | Onyx | Open WebUI |
|---|---|---|---|---|---|
| Default containers | API+worker same image, + Postgres, Redis | 16 (47 defined) | 6, one app container runs every process | 13 | 1 (+ optional) |
| Minimum RAM (official/observed) | – | – | 16 GB recommended | 10 GB min, 16 GB preferred | – |
| Pipeline builder | none (apps/workflows) | canvas, layout mixed with logic, browser-only validation | canvas, but a fixed linear chain | none; fixed pipeline | none (plugins) |
| Parse choice | by file type | by extension + one global ETL setting | per page (text layer vs OCR), template by hand per dataset | fixed | fixed per instance |
| Change detection / scheduled sync | crawl re-embeds everything | none (Notion manual only) | partial | yes (timestamp → hash), weekly deletion check | none |
| Hybrid search | no (vector only, no index) | yes; keyword uses jieba (Chinese) | yes (Elasticsearch) | yes (OpenSearch) | BM25 rebuilt in memory per query |
| Swedish text analysis | no | no | no | no (English default) | no (`simple`) |
| SSO / RBAC / audit in free version | yes | SSO/RBAC closed source | no (open-core stubs) | permission sync, retention deletion are Enterprise | yes |
| Licence catch | – | no multi-tenant use, logo must stay | open-core | CE/EE split | branding must stay above 50 users |

**Nobody handles Swedish properly, nobody chooses the parse strategy automatically per document, and every
platform with a canvas stores layout with logic and validates only in the browser.** Those three gaps are where
this design can be clearly better.

## 2. Recurring flaws and our answer

| # | Flaw seen | Where (evidence) | Our design answer |
|---|---|---|---|
| F1 | Monolith / kitchen sink: one process or one image does API, worker, parsing, plugins | Eneo 63 modules, 154 import cycles, API = worker image; Open WebUI freezes during ingestion (#19421); RAGFlow one container runs all processes | Separate data-plane APIs, worker, parser sidecar, admin. Ingestion can never block chat. |
| F2 | Heavy footprint | Dify 16 containers / ~1,400 env vars; Onyx 13 / ≥10 GB; RAGFlow 16 GB | Target: the whole platform on one 8 vCPU / 16–24 GB host for the pilot, in two resource zones. One binary in several roles. |
| F3 | Slow chat request path | Eneo: 78 SQL statements, +54–68 ms per message, p95 time to first token 11 s at 64 users, failures at 128+ | Budget: assistant config from cache, retrieval in one SQL query, no full-object loads; measured with the gateway's benchmark harness before release. |
| F4 | One shared queue for everything | Eneo one ARQ worker; Dify one Celery worker for ~24 queues | Job classes with their own concurrency limits (crawl, parse, embed, sync) on a Postgres queue; fair per department. |
| F5 | Parse choice manual or by extension | Dify extension + global ETL; RAGFlow template per dataset by hand, "laws/manual/paper" models identical to general | Auto-parse node: profiles → per-page heuristics → try-and-score → learned profiles (auto-applied above threshold). |
| F6 | Silent failures, success shown on failure | Onyx #14064, #15077; Eneo crawl errors only partly visible | Failures are data: every document gets a run record with outcome and reason; a sync that drops >N% is marked degraded, not green. |
| F7 | Destructive deletion bugs | Onyx deleted 189,833 docs as "success" (#16221), no guard | Deletion guard: if a sync would remove more than X% (default 20%) or the source returns empty, pause and ask. Soft delete with grace period. |
| F8 | SharePoint delta mishandled | Onyx skips delete markers, moved/copied files missed (#13583, #16590); Eneo's closed issues #427, #521, #522, #429, #1081 | Proper Graph delta: process `deleted` facets, handle moves, 410 → full resync, subscription renewal; use both platforms' issues as acceptance tests. |
| F9 | Re-processing everything | Eneo re-embeds every page every crawl; Dify publish does not re-process, chunk structure locked | Two-stage change detection (timestamp → content hash); embedding cache by text hash; publish computes a minimal re-processing plan. |
| F10 | Boilerplate indexed | Eneo indexes whole pages incl. menus; KM uses `InnerText` | Main-content extraction (SmartReader/AngleSharp) + per-site cleanup rules learned like parse profiles. |
| F11 | Weak or non-Swedish retrieval | Eneo vector-only, no index, fixed 200-token chunks; Dify jieba; Open WebUI `simple` FTS | Own hybrid SQL: HNSW + Swedish FTS (Hunspell decompounding) + RRF; structure-aware and parent-child chunks; measured by the Swedish benchmark. |
| F12 | Metadata filtering after vector search | Dify filters a list of document ids after the search | Filters inside the same SQL query (GIN-indexed tags), applied before ranking. |
| F13 | Canvas graph = layout + logic, client-only validation, untyped data | Dify, RAGFlow | Executable graph and canvas layout stored separately; typed ports validated server-side on save and publish. |
| F14 | Global, instance-wide config | Open WebUI one RAG config; Onyx one embedding model and chunk size | Per collection: pipeline and retrieval settings; per assistant: query steps. One platform embedding profile (on-prem BGE-M3) by design, so assistants can span collections (review R3). |
| F15 | Essential features behind paywalls / closed source | Dify SSO/RBAC, Onyx retention deletion, RAGFlow OIDC/RBAC/audit | All of it in the base platform; it is the municipality's own code. |
| F16 | Risky upgrades | Open WebUI migrations in almost every minor release; RAGFlow #14307 metadata loss | The gateway's practice: one-shot migration service, rolling updates, upgrade guide, backup/restore scripts. |
| F17 | Costly "smart" features with no budget | RAGFlow GraphRAG "36 hours and $200" (#7957) | Every LLM step in a pipeline goes through the gateway with the team's key, so budgets apply; the preview shows the estimated cost before publishing. |

## 3. Best ideas to copy

| Idea | From | Used in |
|---|---|---|
| Connector capability interfaces (checkpointed fetch, slim id listing for deletions, retry-failed-only, hierarchy) | Onyx `connectors/interfaces.py` | Connector SDK |
| Settings tagged by effect (identity/scope/behaviour/cosmetic) → minimal re-sync plan | Onyx `field_policy.py`, `edit_plan/planner.py` | Source edits and pipeline publish |
| Pre-flight checks for SharePoint grants (`Sites.Selected`) on the unsaved form | Onyx | SharePoint source setup |
| Per-node preview using cached upstream output, no writes | Dify | Pipeline builder |
| Templates exposing a few parameters as a form | Dify `rag_pipeline_variables` | Editors never need the canvas |
| Parent-child chunks with an explicit chunk-structure contract | Dify | Chunk nodes + retrieval |
| Draft/published versions with restore; documents pinned to the pipeline version that processed them | Dify, RAGFlow | Pipeline versioning |
| Per-page parse choice (text layer / OCR / re-render) and garbled-text detectors | RAGFlow `parser.go:227-325`, `util/garbled.go` | Auto-parse heuristics + Quality check |
| Per-task cache of LLM/embedding results so retries do not pay twice | RAGFlow, Dify | Worker |
| Run record per document with execution id; newer run cancels older | Kernel Memory | Worker |
| One binary in API / worker / both roles | Kernel Memory | Deployment |
| Security classification on spaces limiting allowed models | Eneo `spaces/space.py:671` | Collections/assistants ↔ gateway residency |
| Retention hierarchy (assistant → space → tenant) and audit read with justification | Eneo | Retention + audit |
| `pk_` public, origin-bound read-only keys vs `sk_` secret keys | Eneo | API keys |
| Crawl run records with failure reasons; per-tenant crawl concurrency limits | Eneo | Crawler |

## 4. Decisions this research changes or confirms

- **Confirmed:** build our own in .NET; small services; Postgres-first; Python only as parser sidecar.
- **Search backend:** Postgres only for the MVP behind our own search interface; Postgres is the master copy so an
  OpenSearch index can be rebuilt from it. Add OpenSearch only if a trigger fires (Swedish recall@10 ≥ 5 points
  better, >2–3M chunks, p95 > 300 ms after tuning, real need for facets/highlighting). Run the Swedish benchmark early.
- **Postgres must be our own container** to get Swedish Hunspell decompounding (decided: our own container
  everywhere, Azure included).
- **Pipeline engine:** a small custom DAG executor on a Postgres queue (`SKIP LOCKED`); not Elsa, not Agent Framework
  Workflows, not Microsoft.Extensions.DataIngestion (preview, linear).
- **Packages:** Microsoft.Extensions.AI, CommunityToolkit.VectorData.PgVector (plus our own hybrid SQL),
  Pgvector.EntityFrameworkCore, AngleSharp + SmartReader, PdfPig (fast path/detection), Microsoft.Graph.
  Avoid Kernel Memory (archived), Semantic Kernel, TickerQ (commercial licence since 2026-10-13).
- **Open WebUI:** fine for the pilot with branding kept; integration contract in `open-webui.md` §9 (forwarded user
  JWT, our own `/v1/models` filtering, streaming source events for citations, task model set separately).
