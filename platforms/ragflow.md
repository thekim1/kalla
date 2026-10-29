# RAGFlow (infiniflow/ragflow): competitive analysis

Analysed: shallow clone at `~/git/rag-platforms/ragflow`, HEAD `a609e62` (2026-10-29), version `1.0.0-rc2`
(`pyproject.toml:3`, `docker/.env:316`). Paths below are relative to the repo root. GitHub data from read-only `gh`
queries on 2026-10-29. "Verified" means read in code/config or measured; "inferred" means my reading, not proven.

## 1. Summary

- **What it is**: a "deep document understanding" RAG engine plus everything around it: datasets (knowledge bases),
  chat assistants, search apps, an agent canvas, an ingestion-pipeline canvas, 40+ data-source connectors, memory,
  "knowledge compilation" (GraphRAG-style structure, RAPTOR tree, wiki, mind map), MCP server, sandboxed code
  execution, chat channels (WeChat/DingTalk/Feishu…), CLI. Very popular: ~93k stars, ~1,300 open issues, repo
  created 2023-12 (`gh api repos/infiniflow/ragflow`).
- **Stack (as of this commit)**: the backend has been **rewritten from Python to Go**. `AGENTS.md` ("Go-only target")
  says Go now implements API, admin, ingestor and syncer (`cmd/ragflow_server.go`, modes `--api --admin --ingestor
  --syncer`) and orders removal of all Python; the old `api/`, `rag/`, `deepdoc/`, `agent/` directories are already
  gone from the tree. Go 494k non-test lines under `internal/` (measured), React + TypeScript + Vite UI in `web/`
  (1,871 files), a C++ tokenizer via cgo (`internal/binding/cpp`), DeepDoc models run in-process with ONNX Runtime,
  PDF via pdfium/pdf_oxide native libs. Workflow engine is CloudWeGo **Eino** (`go.mod`: `eino v0.10.0-alpha.33`,
  `internal/agent/canvas/compile.go`). HTTP via Gin, ORM via GORM (MySQL default; PostgreSQL also mentioned).
- **Licence**: Apache-2.0 (`LICENSE`). **But open-core**: build tag `!ee` stubs show that the community build has
  **no OAuth/OIDC login** (`internal/server/config/stub.go:86-94`: "The OSS build has no OAuth login"), **no
  role-based permissions** (`internal/permission/policy_ce.go:59`: "Community edition has no role-based feature
  restrictions"; `internal/permission/stub.go`), no analytics/operation log (`internal/engine/clickhouse/stub.go`
  drops all usage and operation logs), no admin ingestion inspection in the CLI (`internal/cli/stub.go`), no billing.
  Maintainers answer RBAC requests with "contact sales@ragflow.io" (issue #18059).
- **Maturity**: feature-rich and widely deployed, but **in the middle of a full-language rewrite** (1.0 RCs Sep-Oct
  2026, 0.27.x still being patched in parallel per `gh release list`). Many Go files say "mirrors Python …" and keep
  parity/golden tests, and `AGENTS.md` describes a shared MySQL schema written by both Go and Python ORMs with
  index-name reconciliation rules. High churn; expect breaking upgrades (see section 8).

## 2. Architecture & deployment footprint

**One fat application container + 5 infrastructure containers by default** (verified, `docker/docker-compose.yml`,
`docker/docker-compose-base.yml`, `docker/.env:36-53`; `COMPOSE_PROFILES=${DOC_ENGINE},${DEVICE},metadata-mysql`):

| Container | Role | Notes |
|---|---|---|
| `ragflow-cpu` (or `-gpu`) | nginx (UI) + Go API (9380) + Go admin (9381) + syncer + N ingestors | `docker/entrypoint.sh`: bash loops `run_with_restart … &` for each process, then `wait` |
| `es01` Elasticsearch 8.11.3 | chunks, full-text + vectors | `mem_limit: ${MEM_LIMIT}` = 8 GB (`.env:90`), needs host `vm.max_map_count=262144` (README:166) |
| `mysql` 8.0.40 | metadata (users, tenants, datasets, documents, tasks, canvases) | |
| `minio` (pgsty/silo image) | object storage for files, page images, chunk images | |
| `kvrocks` 2.16 | Redis-compatible cache / locks / run tracker / media cache | replaced Valkey/Redis in the Go path |
| `nats` 2.14 JetStream | task queue (`tasks.RAGFLOW`), syncer queue, knowledge-compile queue | `internal/engine/nats/*.go` |

Optional profiles: Infinity, OpenSearch, OceanBase, SeekDB, SereneDB, Vastbase (alternative doc engines), Kibana,
Jaeger, ClickHouse (EE analytics), TEI embedding (cpu/gpu), sandbox-executor-manager (gVisor code sandbox). That is
**7 interchangeable chunk stores** behind `internal/engine/` (27.6k lines).

- **Resources**: README:158 "Recommended starting configuration: 4 CPU cores, 16 GB RAM, and 50 GB of available
  disk". ES alone is capped at 8 GB. Issue #8490: 16 cores/128 GB "page freezes" when parsing four files at once.
- **Process model**: no per-process container separation; API, admin, syncer and ingestors share one container,
  one image and one `service_conf.yaml`; scaling ingestion = `--ingestors=<n>` or `--disable-api-server`
  (`entrypoint.sh` usage). DeepDoc OCR/layout/table models run **in the ingestor process** ("there is no separate
  DeepDoc service", `.env` DeepDoc section). Memory fragmentation is acknowledged: `MAX_TASKS_PER_WORKER` recycles
  the worker "to release the ONNX Runtime / CUDA arena fragmentation" (`.env` ~line 535).
- **Queueing**: JetStream work-queue stream, durable consumer `RAGFLOW_CONSUMER`, explicit ack, `MaxDeliver: 16`,
  backoff 5s/15s/30s/60s, worker heartbeats `InProgress` (must be < 5 s) (`internal/engine/nats/nats.go:259-290`).
  DB remains source of truth for task state; Kvrocks holds run tracker and a per-task model-result cache.
  Default concurrency is tiny: `ingestor.concurrent_workers: 1`, `page_concurrency: 2`
  (`docker/service_conf.yaml.template:126-138`).

## 3. Ingestion & connectors

- **Sources**: local upload plus ~45 connectors in `internal/syncer/connector/` (SharePoint, OneDrive, Teams,
  Outlook, Confluence, Notion, Google Drive, S3/Azure Blob/GCS, sitemap, RSS, REST API, IMAP, Jira, GitHub, MySQL,
  PostgreSQL, Zendesk, Moodle, …). No general website crawler: web is `sitemap.go` (sitemap walk with depth cap,
  URL include patterns, PDF-link discovery `:381`) and Firecrawl as an external tool (`tools/firecrawl`).
- **Syncer design** (`internal/syncer/README.md`): DB `sync_logs` → NATS wake-up → worker pool (5) → per
  connector+KB lock → `OpenSync/NextBatch` with fixed time window and checkpoint resume → **fingerprint skip**
  (`connector/fingerprint.go`: `stableFingerprint`, `contentFingerprint`) → upsert document → enqueue parse task.
  Deletions are a separate **PRUNE** run that takes a full "slim snapshot" and deletes the stale set.
- **SharePoint change detection** is a `lastModifiedDateTime` window filter (`connector/sharepoint.go:739-753`),
  not Graph delta queries; deletes need a full prune listing. No ACL/permission sync seen (inferred from grep).
- **Parsing**: the `Parser` node routes **by file-extension family** (`internal/ingestion/component/
  parser_dispatch.go:213-290`: pdf, docx, slides, spreadsheet, html, markdown, text&code, epub, json, email, audio,
  video, image). Each family has its own setup in the node (suffix list, `parse_method`, `preprocess`, VLM).
  PDF `parse_method`: DeepDOC (default, local) or remote engines MinerU, PaddleOCR, Docling, SoMark,
  OpenDataLoader, VLM-per-page (`pdf_vision_dispatch.go`, `pdf_parser_model.go:39-41`).
- **DeepDoc (Go port)** `internal/deepdoc/`:
  - Models: `det.ort`, `rec.ort` (OCR detect/recognise), `layout.ort` (YOLOv10, 10 classes: title, text,
    reference, figure, figure caption, table, table caption, equation; `native/dla.go:46-54`), `tsr.ort` (table
    structure). Downloaded from HF `InfiniFlow/deepdoc` (`deps/download_deps.py:103-104`).
  - **Per-page OCR decision**: use embedded PDF chars when clean, full OCR when chars are empty, garbled (CID
    placeholders, subset-font encoding, alphabet coverage) or the page looks like scan noise
    (`parser/pdf/parser.go:310-325`, `parser/pdf/util/garbled.go:59-430`). If a page yields no boxes, **re-render
    at higher zoom and retry** OCR+layout, zoom capped at 9 to protect memory (`parser.go:227-246`).
  - Layout: column detection, dehyphenation, TOC/header/footer removal (`parser/pdf/layout/*.go`); tables: TSR →
    cells → spans → HTML, caption merge, cross-page merge, rotation (`parser/pdf/table/*.go`, ~15 files).
  - Per-template layout models are a fiction now: the Dockerfile notes `layout.laws/manual/paper.onnx are
    byte-identical to layout.onnx` (`Dockerfile:29-50`).
- **Chunking "templates"** are now **built-in pipeline DSLs**: `internal/ingestion/pipeline/template/
  ingestion_pipeline_{general,qa,manual,table,paper,book,laws,presentation,picture,one,audio,email}.json`
  (343 KB total, each 20-42 KB because they embed canvas UI state and a base64 avatar). Registry:
  `pipeline/builtin_registry.go` (legacy `parser_id="naive"` aliased to `general`, `:136-142`). Each is
  File → Parser → *Chunker* → Extractor → Tokenizer, differing in chunker and parser params:
  general = `GeneralChunker` (512 tokens, delimiter-based); paper/book/laws = `TitleChunker` with hierarchy regex
  sets (Markdown `#`, Chinese 第…章/节/条, "Chapter/Section/Article N", numeric 1.2.3) and `hierarchy` depth;
  manual/qa/table/one/page/token chunkers for the others (`internal/ingestion/component/chunker/*.go`).
- **How a template is chosen**: **by a human**, per dataset (`parser_id`) with optional per-document override
  (`Document.ParserID`/`PipelineID`, `internal/entity/document.go:26-29`; resolved in
  `internal/ingestion/task/task_context.go:155-170`). There is no automatic detection of "this is a law / a paper /
  a Q&A sheet". Automation exists only *inside* the parse (per-page OCR choice, garble detection, zoom retry).
- **Enrichment**: one `Extractor` node per pipeline (LLM keywords, generated questions, tags, summary, metadata
  schema). Embedding mixes title and content vectors: `0.1·title + 0.9·content` (`component/tokenizer.go:636-711`,
  `filename_embd_weight`).

## 4. Pipeline / workflow builder

- **Two canvases, one runtime**: the agent canvas and the "dataflow" ingestion canvas share `internal/agent/canvas`
  (Eino `compose` graph) and the component registry `internal/agent/runtime/registry.go`; ingestion components
  register under `CategoryIngestion` (`ingestion/component/chunker/register.go:43-60`, `component/parser.go:714`).
- **Node model**: component = `{component_name, params}` with `upstream`/`downstream` id lists; ids like
  `Parser:HipSignsRhyme` (random words). **Storage**: one JSON DSL per canvas in MySQL (`user_canvas` + versions),
  holding both the execution graph (`dsl.components`) **and** the React Flow UI graph (`dsl.graph.nodes/edges` with
  positions, measured sizes, hover state), plus `globals`, `history`, `messages`, `path`, `retrieval`. Execution
  and presentation are not separated.
- **Typing/validation**: weak. Ports are `map[string]string` *descriptions* (`runtime/registry.go:64-67`), not
  types. Backend validation of an ingestion graph is a single rule: "at most 1 Extractor"
  (`pipeline/pipeline.go:140-154`). Topology rules live only in the frontend: Parser may only connect to a chunker,
  chunker may not connect to a chunker, one chunker per canvas (`web/src/pages/agent/utils/pipeline-connection.ts:
  94-105`). Effectively the ingestion pipeline is a **fixed linear chain** with swappable stages; there is no
  routing/switch node for ingestion (per-format branching is done inside the Parser node's per-family config).
- **Execution**: `Pipeline.Run` compiles the canvas and invokes the whole graph per document
  (`pipeline/pipeline.go:199-280`). "Resume-from-checkpoint is intentionally not supported"; retries rerun the full
  graph and rely on component-level caches (LLM/embedding results in Kvrocks keyed per task) to avoid paying twice.
  Progress events per component are written as `ingestion_task_log` rows.
- **Preview/debug**: a dry run on an uploaded file with no knowledge base: skips embedding, image upload and
  persistence, parses only the first pages and keeps the first **3 chunks** (`ingestion/task/debug.go:33-91`;
  `parser_page_cap.go`). Results rendered in `web/src/pages/dataflow-result`.
- **Versioning**: dataflow canvases are versioned; a document can **pin a pipeline version**, otherwise "latest"
  (`task_context.go:59-63,163-170`; `task/pipeline_executor.go:1051-1060`). No evidence of "re-process only what the
  change affects" or of diff/rollback UX (inferred from code search).

## 5. Retrieval & answering

- **Hybrid**: BM25 on C++-tokenised text + dense vectors in ES (or engine-native fusion in Infinity). Final score
  `sim = tkWeight·token_sim + vtWeight·vector_sim` with a user-set `vector_similarity_weight` (column default 0.3
  vector / 0.7 keyword, `internal/entity/dataset.go:117`; `service/similarity_weight.go`, `service/nlp/reranker.go:350-390`), plus a
  `similarity_threshold`, `top_k`, optional tag "rank features". Weighted-sum, not RRF.
- **Rerank**: optional rerank model (`reranker.go:56-110 RerankByModel`) over `rerank_candidates_count`; normalised
  scores blended with token similarity.
- **Swedish**: the analyzer has a Snowball Swedish stemmer (`internal/binding/cpp/rag_analyzer.cpp:52-71`) but no
  compound splitting; the tokenizer is Chinese-first (dictionary + OpenCC). OCR alphabet (`ocr.res`) is Chinese/
  English-oriented; whether å/ä/ö are covered was not verifiable (models not in the clone) — **inferred risk**.
- **Citations**: post-hoc, embedding-based: answer split into sentences, each sentence embedded, cosine vs. chunk
  vectors, threshold descent 0.63 → 0.3, up to 4 chunks per sentence, `[ID:n]` inserted
  (`service/citation.go:55-90,251`). Costs an extra embedding call per answer; citations can be attached to
  sentences the model did not actually derive from that chunk (inferred).
- **Metadata filters**: `metadata_condition` over document metadata → doc-id filter (`service/metadata_filter.go:
  560`). Retrieval test page per dataset. Knowledge-graph, RAPTOR-tree and "structure" navigation are exposed as
  tools to an agentic retriever (`internal/agentic_rag/tool_*.go`, 15.6k lines).
- **APIs**: OpenAI-compatible endpoint per chat assistant `POST /api/v1/openai/:chat_id/chat/completions`
  (`internal/router/router.go:335`), plus native chat/agent/search completions and an MCP endpoint.
- **GraphRAG / RAPTOR** are now "knowledge compiler" variants run as a pipeline component or dataset task:
  `structure` (LLM entity → relation extraction, LLM-judged merge; `knowledge_compiler/structure/`), `tree` (RAPTOR:
  1D watershed clustering over document-ordered embeddings + LLM summaries per level; `tree/raptor.go`), `wiki`,
  `mindmap`. All are LLM-heavy, batched on a shared pool (`.env` WIKI_*/DOC_STRUCTURE_* knobs).

## 6. Admin, multi-tenancy & permissions

- **Tenancy**: a "tenant" is effectively a user account; "team" = other users joined to that tenant. Datasets have
  `permission` `me` or `team` (`internal/entity/dataset.go:37-44,111`). Every team member gets the same rights; no
  roles, no departments, no per-assistant ACL in OSS (`policy_ce.go`; issue #18059 analysis: "no role or action
  vocabulary anywhere in that path").
- **Admin**: separate admin server (`--admin`, port 9381) for users/services; superuser created by
  `--init-superuser`. OIDC/SSO, RBAC, operation logs and usage analytics are **enterprise-only** (section 1).
- **Models**: per-tenant model providers incl. "OpenAI-API-Compatible" (`conf/models/openai-api-compatible.json`),
  so a gateway can be plugged in, but credentials and model selection are stored per tenant inside RAGFlow.
- **Retention**: no chat-history retention policy found (grep for retention only hits memory and ingestion logs).

## 7. Strengths worth copying

1. **Per-page parse strategy with cheap signals, then escalation** (`internal/deepdoc/parser/pdf/parser.go:
   227-325`, `util/garbled.go`): text layer if clean → OCR if empty/garbled/scan-noise → re-render at higher zoom
   if nothing found, with a memory cap. This is exactly the "heuristics" tier of our Auto-parse node, and it works
   at page level, not document level (mixed PDFs with a few scanned pages are common in municipal archives).
2. **Garble detectors** worth porting as Quality-check signals: CID placeholders, subset-font prefixes, font
   encoding checks, OCR-alphabet coverage, scan-noise detection (`util/garbled.go:35-430`).
3. **Templates = pipeline definitions**, not code paths: the legacy chunk methods became 12 built-in DSLs in one
   embedded folder with an alias table for old ids (`pipeline/builtin_registry.go`). Same idea as our default
   templates per source type; keep an alias mechanism for renamed templates.
4. **Title/heading hierarchy chunker driven by regex level sets** (`chunker/title.go`, templates `paper`/`laws`):
   configurable heading patterns per document family. For us: Swedish patterns ("Kapitel N", "§ N", "N kap.",
   "Bilaga") as a laws/regulations profile for kommunala föreskrifter and taxor.
5. **Pinned pipeline version per document** (`task_context.go:59-63`) so re-runs are reproducible and a new
   version does not silently change old documents.
6. **Side-effect-free debug mode** keyed on "no target collection": skips embed/persist, caps pages and chunks
   (`task/debug.go`). Cheap way to implement our preview.
7. **Model-result cache per task** so a crashed/redelivered run does not pay for LLM/embedding calls again
   (`pipeline.go` comments around `:214`, `ingestion/chunkcache`). Important when LLM enrichment goes through a
   budgeted gateway.
8. **Title-vector blending** (`tokenizer.go:702-711`) and **positions/page images per chunk** for citations that
   point at the exact page region.
9. **Explicit queue semantics**: durable consumer, explicit ack, bounded redelivery with backoff, heartbeat
   invariant documented in code (`engine/nats/nats.go:262-285`).

## 8. Flaws & pain points

Verified (code, config or issue text):

1. **Heavy footprint for what it does**: 6 containers by default incl. Elasticsearch with an 8 GB limit and a host
   kernel tweak; 4 CPU / 16 GB / 50 GB recommended (README:158, `.env:90`, README:166). Seven pluggable chunk
   stores multiply the test matrix. Users repeatedly hit infra failures: ES unreachable (#1135, #6186),
   MySQL unhealthy (#329), swapping MinIO for Azure Blob/OSS (#13310, #10979).
2. **Monolithic app container**: API, admin, syncer and ingestors are bash background jobs with restart loops in
   one container (`docker/entrypoint.sh`); a parsing spike competes with chat on the same box unless the operator
   hand-splits with flags. DeepDoc runs in-process, and ONNX memory fragmentation needs periodic worker recycling
   (`MAX_TASKS_PER_WORKER`, `.env`).
3. **Parsing stuck / slow is the top recurring complaint**: #8343 ("parsing in stuck", 30 comments), #3022
   ("Hanging at parsing files"), #4750 ("parsing task is queued"), #11144 ("not parsed"), #8490 (16C/128 GB
   freezing on 4 files), #5598 (which GPU to speed up parsing), #7995 (memory leak after upgrade), #9992 (onnx GPU
   memory). Default `concurrent_workers: 1`, `page_concurrency: 2` (`service_conf.yaml.template:132-138`).
4. **GraphRAG cost and reliability**: #7957 "will not finish … after 36 hours and $200", #5088 (68-page PDF > 3 h on
   an A100), #9647, #10778 (KG build never successful after upgrade). Root cause pattern: whole-run retries + many
   LLM calls per chunk; the Go port added a result cache but the approach is inherently LLM-heavy.
5. **Template choice is manual and dataset-wide**: users must know that "laws" or "manual" fits; there is no
   detection or feedback loop (section 3). The per-template layout models are identical files (`Dockerfile:29`), so
   the "template" mostly selects regexes and chunk sizes, largely tuned for Chinese headings.
6. **Pipeline builder is a canvas over a fixed chain**: no typed ports, backend checks one rule, topology rules only
   in the React code (`pipeline-connection.ts:94-105`, `pipeline.go:140-154`); the DSL mixes UI layout with
   execution (`template/*.json`). Bugs where configured steps silently do nothing: #14376 (metadata extraction
   "works in logs but metadata does not appear in final chunks").
7. **Open-core gaps hit public-sector needs**: no OIDC/SSO, no RBAC, no audit/operation log in OSS
   (`server/config/stub.go:86-94`, `policy_ce.go:59`, `clickhouse/stub.go`); RBAC questions redirected to sales
   (#18059, #6076). Flat "team" permission only.
8. **Upgrade fragility and rewrite churn**: #14307 (0.24 → 0.25 "all metadata has been lost", models invalid),
   #7023 (how to upgrade without losing data), #12581 (missing tables). Python → Go rewrite with a shared schema owned
   by two ORMs (`AGENTS.md` "Shared database schema"). 1,300 open issues.
9. **Two stores for one truth**: documents/tasks in MySQL, chunks in ES/Infinity, files in MinIO, cache in Kvrocks,
   queue in NATS. Inconsistency shows up as "nothing found in chat but retrieval testing finds it" (#8619).
10. **Kitchen-sink scope**: agents, sandboxes, chat channels, memory, wiki compilation, deep research, billing hooks,
    MCP: ~494k lines of Go before tests. Comparable to the Eneo concern, and larger.

Inferred (not proven here):

- Swedish quality is untested territory: Chinese-first tokenizer, no decompounding, OCR alphabet of unknown Latin-1
  coverage, heading regexes for Chinese/English only.
- SharePoint sync via modified-time windows plus full prune listings will be slower and heavier than Graph delta
  on large libraries, and does not carry SharePoint permissions into retrieval.
- Embedding-based post-hoc citations can mis-attribute sentences; citations are not guaranteed to be the chunks the
  model used.
- A conversation-retention/deletion story (GDPR, arkivlagen) would have to be built on top.

## 9. Implications for the municipality design

Copy (with reference to `01-general-idea.md`):

- **§5a Auto-parse, tier 2 "heuristics"**: port RAGFlow's page-level decision (text layer clean? garbled? scan
  noise? empty after layout?) into the Detect node and run it per page, not per document. Use its garble signals
  as Quality-check metrics (§5a "Quality" row) and as the score in "try and score".
- **§5a default templates**: model templates as stored pipeline versions (like `ingestion_pipeline_*.json`), with
  an alias table for renamed ones. Add a Swedish "regulations" heading profile (kap./§/Bilaga) as a parse profile.
- **§5a versioning**: pin pipeline version per document (RAGFlow does) and add what it lacks: impact analysis
  ("which documents would re-run") and rollback.
- **§5a preview**: the "no target collection = dry run" trick, but show every node's output and full documents,
  not 3 chunks.
- **§5 job queue**: explicit ack, bounded redelivery with backoff, heartbeats, and a per-job cache of
  gateway results so retries do not re-spend department budgets (§2 principle 3).

Avoid:

- A dataset-wide, human-picked chunk method as the only mechanism (RAGFlow's main parse-quality UX problem). Our
  rules → heuristics → try-and-score → proposed-profile loop is the differentiator; keep it.
- In-process heavy models next to the API. Keep Docling/OCR in the stateless sidecar (§3) with its own memory
  limits and horizontal scaling, and keep the worker separate from the Chat/Knowledge API (§2.1, §2.5).
- Separate chunk store, cache, queue and object store as mandatory infra. Our Postgres+pgvector+Redis (+ volume/
  Blob) is 2-3 containers vs RAGFlow's 6 (§6 scale assumption of <100k chunks makes ES unnecessary).
- Mixing UI layout into the executable DSL; store execution graph and canvas layout separately so pipelines can be
  diffed, validated server-side and edited by API.
- LLM-heavy GraphRAG/RAPTOR as default features; if ever added, make them opt-in per collection with a cost
  estimate from the gateway before running.

Do better:

- **Typed ports validated on the server** (§5a "raw bytes → document → sections → chunks → vectors"), not only in
  the canvas; RAGFlow's frontend-only rules are the counter-example.
- **Real routing**: Detect/Switch nodes so a source can branch per MIME/scan/language, rather than RAGFlow's
  per-family config hidden inside one Parser node.
- **Identity and governance in the free core**: OIDC, roles (platform/department/team admin), audit log, chat
  retention (§7, §2.7) are paid extras in RAGFlow and exactly what a municipality needs.
- **SharePoint**: Graph delta + change notifications (§5.2) instead of time windows + full prune.
- **Retrieval**: RRF in one Postgres query with Swedish FTS (§6) is simpler than weighted sums with tuned
  thresholds; let the model cite chunk ids from the prompt and verify them, instead of post-hoc embedding matching
  (§6 "citations are mandatory").
- **Swedish benchmark** (§10.3): measure OCR and chunking on real municipal PDFs before choosing Docling vs.
  alternatives; RAGFlow gives no evidence for Swedish.
