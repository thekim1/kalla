# Dify: competitive analysis

Analysed: `langgenius/dify` shallow clone at `~/git/rag-platforms/dify`, commit `a544e2c` (2026-10-29), release
**1.18.0** (2026-10-23). Paths below are relative to the repo root unless noted. "Verified" means read in code or
measured. "Inferred" means a conclusion drawn from code/issues but not reproduced.

## 1. Summary

- **What it is:** a general LLM-app platform: chatbots, agents, "chatflows" and workflows on a canvas, plus a
  knowledge-base (dataset) subsystem. Since 1.9 (autumn 2025) it also has a **Knowledge Pipeline** (RAG pipeline):
  the same workflow canvas and engine, used to define ingestion (datasource → extract → chunk → index).
- **Stack:** Python 3.13 backend (Flask 3, flask-restx, SQLAlchemy, Celery 5, gunicorn+gevent), `api/pyproject.toml:3-49`;
  Next.js/React front end (`web/`, ~937k lines of TS/TSX, measured); Go plugin daemon (separate image
  `langgenius/dify-plugin-daemon`); graph engine extracted to a separate package **graphon** (`graphon==0.8.0`, pinned to
  a git commit, `api/pyproject.toml:49,68`; Apache-2.0, 85 stars). Backend is ~477k lines of non-test Python
  (measured), 242 Alembic migrations.
- **Licence (verified, `LICENSE`):** "modified Apache 2.0". Restrictions: (a) **no multi-tenant service** without a
  written licence, where *one workspace = one tenant*; (b) the **logo/copyright in the web console and web apps may
  not be removed**; contributors grant LangGenius the right to change the licence; plus a claim that "the interactive
  design is protected by appearance patent". For a municipality, (a) is ambiguous: one workspace per department would
  arguably be "multi-tenant". SSO, fine-grained RBAC and web-app auth live in a closed **Enterprise** service
  (`api/services/enterprise/base.py:157` calls `ENTERPRISE_API_URL`; `services/enterprise/rbac_service.py:1169`
  `/inner/api/rbac/check-access`; SSO flags read from `enterprise_info`, `services/system_feature_service.py:165-198`).
  The new **KnowledgeFS** service is a *private repository*; only its contract JSON is vendored
  (`api/knowledge-fs-contract/README.md:3`).
- **Maturity:** very high adoption (≈160k GitHub stars, 25k forks, created 2023-04), fast release cadence, but heavy
  churn: large ongoing refactors (TypedDict migration #32863, "pass session as parameter" #40372, #37403), and
  upgrade breakages (#27291: knowledge bases created before 1.9.1 unusable after 1.9.2, 100 comments).

## 2. Architecture & deployment footprint

**Compose file (verified, `docker/docker-compose.yaml`, 1,515 lines):** 47 service definitions, most behind
profiles (one per supported vector DB: Weaviate, Qdrant, Milvus+etcd+MinIO, OpenSearch+Dashboards, Elasticsearch+Kibana,
pgvector, Oracle, IRIS, OceanBase, Couchbase, …). Default `COMPOSE_PROFILES=weaviate,postgresql,collaboration`
(`docker/.env.example:303`) starts **16 containers (15 long-running + 1 init)**:

| Container | Role |
|---|---|
| `api` | Flask API (console API, service API, web-app API, inner API for plugins) |
| `api_websocket` | collaboration (multi-user canvas editing) |
| `worker` | Celery worker, **one process subscribing to ~24 queues** (`api/docker/entrypoint.sh:38`: dataset, pipeline, priority_pipeline, mail, workflow, plugin, retention, schedule_* …) |
| `worker_beat` | Celery beat scheduler |
| `web` | Next.js front end |
| `db_postgres` | Postgres 15 (app DB **and** separate `dify_plugin` DB for the daemon) |
| `redis` | Celery broker, cache, locks, tenant queues |
| `weaviate` | default vector store |
| `plugin_daemon` | Go service that installs and runs plugins (Python subprocesses, pip installs) |
| `sandbox` | code-execution sandbox for Code nodes |
| `local_sandbox`, `agent_backend`, `agent_ssrf_proxy` | new agent runtime (agent_v2), its sandbox and egress proxy |
| `ssrf_proxy` | Squid egress proxy for HTTP/tool calls |
| `nginx` | reverse proxy |
| `init_permissions` | one-shot volume chown |

Opt-in extras: `knowledge_fs` + 5 `knowledge_fs_*_worker`s + `worker_knowledge_fs_upgrade` + `knowledge_fs_unstructured`
(8 more containers, KnowledgeFS needs its **own** database, `docker/envs/core-services/knowledge-fs.env.example:14`),
`unstructured` (ETL), `certbot`.

**Configuration surface (measured):** 244 variables in `docker/.env.example`; 1,157 `KEY=` lines across
`docker/envs/**`. Many images use `:latest` (`nginx`, `ubuntu/squid`, `busybox`, `opensearch`, `unstructured-api`).

**Datastores/queues:** Postgres (or MySQL/OceanBase) for metadata, segments, workflow runs, node executions,
conversations; Redis as Celery broker + Redis lists as per-tenant waiting queues (`api/core/rag/pipeline/queue.py`);
object storage (local/S3/Azure Blob/…) for uploads and also for **serialised pipeline task payloads**
(`api/services/rag_pipeline/rag_pipeline_task_proxy.py:35-46` uploads the invoke entities as a JSON file and passes the
file id to Celery); a vector DB chosen from 32 provider packages (`api/providers/vdb/`, one package each).

## 3. Ingestion & connectors

There are **two parallel ingestion paths** (verified):

1. **Classic dataset** (process rules on a form): upload / Notion / website crawl → `ExtractProcessor` →
   index processor → vector + keyword index. Stage machine `parsing → splitting → indexing → completed|error`, resumable
   from a stage (`api/services/knowledge/indexing/execution.py:96-152`), Celery tasks per operation
   (`api/tasks/document_indexing_task.py`, `retry_document_indexing_task.py`, `sync_website_document_indexing_task.py`,
   `add/remove/enable/disable_segment*_task.py`, ~20 dataset-related tasks).
2. **Knowledge Pipeline**: a `Workflow` of type `rag-pipeline` whose root is a **Datasource node**; one workflow run
   **per document** (section 4). Built-in templates (`api/services/rag_pipeline/transform/*.yml`, 9 templates =
   {file, notion, website-crawl} × {general-economy, general-high-quality, parent-child}) are also used to convert
   classic datasets into pipelines (`rag_pipeline_transform_service.py`).

**Sources.** Datasource provider types are a fixed enum of four (`api/core/datasource/entities/datasource_entities.py:29-37`):
`local_file`, `online_document` (Notion-like), `online_drive` (S3/Drive/SharePoint-like, browse + download),
`website_crawl`. Everything except local upload is a **plugin** from the marketplace. Built-in website crawling in the
classic path delegates to SaaS crawlers: Firecrawl, Jina Reader, WaterCrawl (`api/core/rag/extractor/{firecrawl,watercrawl,jina_reader_extractor.py}`).
SharePoint exists only as a community datasource plugin, with bugs reported (#27084, #36281 "empty folder recursion error").

**Parsing.** `ExtractProcessor` dispatches on **file extension** with a **deployment-wide** `ETL_TYPE`
(`dify` vs `Unstructured`) (`api/core/rag/extractor/extract_processor.py:136-221`). PDFs always go through
`pypdfium2` text extraction, even with Unstructured selected (`extract_processor.py:151`, `pdf_extractor.py:11`):
**no OCR for scanned PDFs** in core (users ask, #22869). In pipelines, parsing is a tool node from plugins
(`langgenius/dify_extractor`, MinerU, Unstructured, LlamaParse …), routed by an IF/ELSE on `file.extension` with
ten hard-coded conditions (`transform/file-parentchild.yml:275-370`).

**Chunking modes** (`api/core/rag/index_processor/constant/index_type.py:5-7`, processors in
`index_processor/processor/`): `text_model` (general: separator + max length + overlap), `qa_model` (LLM generates
Q&A pairs per chunk), `hierarchical_model` (**parent-child**: parent = paragraph or full document, children indexed for
search, parent returned as context; `ChildChunk` table, `models/dataset.py:597`). Optional **summary index**
(LLM summary per chunk vectorised alongside, `core/rag/summary_index`, `tasks/generate_summary_index_task.py`) and
multimodal/image attachments per segment. Pre-processing rules: collapse whitespace, strip URLs/emails.
Indexing technique: `high_quality` (embeddings) or `economy` (jieba keyword table only, no embeddings).

**Sync / change detection (verified):** essentially none. Notion documents can be re-synced on demand
(`services/knowledge/document_sync.py:58-69`, Notion-only); website documents can be re-synced by an explicit call
(`dataset_service.py:2143`). There is no scheduled re-crawl, no ETag/Last-Modified, no document-level content hash
(only `index_node_hash` per segment, `models/dataset.py:576`, used for embedding cache/dedupe). The beat schedule
(`api/extensions/ext_celery.py:213-375`) contains no source-sync job. Embeddings are cached by text hash in the
`embeddings` table (`models/dataset.py:711`, `core/rag/embedding/cached_embedding.py:24`), which saves cost on re-runs.

## 4. Pipeline / workflow builder

**Node model.** Nodes are Python classes `Node[TNodeData]` with a pydantic `NodeData`, an `execution_type`
(ROOT, RESPONSE, …) and `_run()` generator yielding events (`api/core/workflow/nodes/datasource/datasource_node.py:27-35`).
RAG-specific nodes: `datasource` (ROOT), `knowledge-index` (terminal), plus the general nodes reused from workflows:
`tool` (plugin tools: extractors, chunkers), `if-else`, `variable-aggregator`, `document-extractor`, `llm`, `code`,
`http-request`, `iteration`, `loop`. Node *versions* exist (`version() -> "1"`).

**Storage format (verified).** One row in `workflows` per draft (`version="draft"`) and per published version
(`version=str(datetime)`, monotonically increasing `version_number`, `marked_name`) with the graph as a JSON
`LongText` column (`api/models/workflow.py:221-232`). The JSON is the **React-Flow canvas state verbatim**: nodes carry
`position`, `positionAbsolute`, `width`, `height`, `selected`, `zIndex`, `sourcePosition`, and edges carry
`isInIteration`, `isInLoop` next to the semantics (`transform/file-parentchild.yml:22-60, 370-405`). Export/import is a
YAML DSL (`kind: rag_pipeline`, `version: 0.1.0`) with a `dependencies` block pinning marketplace plugins by
hash (`file-parentchild.yml:1-17`), see `services/rag_pipeline/rag_pipeline_dsl_service.py` (1,000 lines).
Pipeline user-input forms are stored separately as `rag_pipeline_variables` (with `belong_to_node_id`), e.g. chunk length,
parent mode (`file-parentchild.yml:760-814`).

**Execution engine.** `graphon` (`ARCHITECTURE.md` in langgenius/graphon): an in-process, queue-based engine with a
fixed worker pool, dispatcher, event stream, frames for iteration/loop, retry and failure-branch policies, pause/resume
for human input. Variables live in a `VariablePool` addressed by selectors `[node_id, var]`; system variables carry
`dataset_id`, `document_id`, `batch`, `datasource_type/info` (`knowledge_index_node.py:56-110`).
Ingestion flow: `PipelineGenerator.generate` creates one `Document` row and one `RagPipelineGenerateEntity` per
selected item (`api/core/app/apps/pipeline/pipeline_generator.py:150-280`), uploads the batch to object storage and
enqueues one Celery task; the task runs up to **10 documents in a `ThreadPoolExecutor` inside one Celery worker**
(`tasks/rag_pipeline/priority_rag_pipeline_run_task.py:64`). Self-hosted goes to `priority_pipeline` without tenant
isolation; Cloud uses Redis per-tenant queues and plan-based priority (`rag_pipeline_task_proxy.py:98-118`).
Every node execution is persisted (`workflow_node_executions`), which is excellent for debugging but means a
pipeline run writes many rows per document.

**Typing/validation.** Variables have runtime `SegmentType`s (string, number, object, file, array[...]); tool/datasource
outputs declare JSON-schema-ish `output_schema`. But the **chunk contract is untyped**: `knowledge-index` reads
`index_chunk_variable_selector` and treats whatever it gets as `chunks: Any`, interpreted according to a string
`chunk_structure` (`knowledge_index/entities.py:65-100`, `knowledge_index_node.py:100-104`); mismatches surface at
run time. Graph validation is mostly **client-side** (`web/app/components/workflow/hooks/use-checklist.ts`, 985
lines). Server-side `publish_workflow` only validates LLM model references, then copies the draft
(`services/rag_pipeline/rag_pipeline.py:458-518`).

**Preview/debug (strong).** Draft runs ("test run") per datasource with the knowledge-index node returning a **chunk
preview without writing** (`knowledge_index_node.py:115-131`, `is_preview`); single-node runs
(`rag_pipeline.py:581 run_draft_workflow_node`, `:833 run_datasource_node_preview`); last-run variables per node are
cached as "draft variables" (`services/workflow_draft_variable_service.py`); front-end chunk cards
(`web/app/components/rag-pipeline/components/chunk-card-list`). Optimistic concurrency on the draft via
`unique_hash` (`rag_pipeline.py:384`) plus real-time collaboration (`api_websocket`).

**Versioning.** Publish = snapshot; restore any published version to the draft (`rag_pipeline.py:421`).
But publishing **does not re-process existing documents** with the new graph; `chunk_structure` is frozen after first
publish ("Chunk structure is not allowed to be updated", `services/knowledge/dataset_service.py:1140`); only an
embedding-model change triggers a vector re-index. Documents record which pipeline input produced them
(`DocumentPipelineExecutionLog`, `models/dataset.py:1104`) but not which *workflow version* (inferred from the model).

## 5. Retrieval & answering

- **Search types** (`api/core/rag/retrieval/retrieval_methods.py`): semantic, full-text, hybrid, keyword (economy).
  Hybrid = vector search and full-text search run in parallel threads, deduplicated, then either a **rerank model**
  or **weighted score** (`core/rag/datasource/retrieval_service.py:880-925`). No reciprocal rank fusion.
- **Weighted score** = `vector_weight × cosine + keyword_weight × TF-IDF over jieba keywords`; the keyword part uses the
  **Chinese jieba tokenizer** for any language (`core/rag/rerank/weight_rerank.py:10, 90-94`).
- **pgvector backend** (`api/providers/vdb/vdb-pgvector/src/dify_vdb_pgvector/pgvector.py`): one table per dataset
  (`embedding_{collection}`, `:82`), HNSW index (`:68-69`); full-text is
  `to_tsvector(text) @@ plainto_tsquery(%s)` **without a language config and without a tsvector index** (`:235-237`),
  i.e. computed per row at query time; only the optional pg_bigm path gets a GIN index (`:73-74`).
- **Metadata filters:** per-dataset metadata fields (`dataset_metadatas`, bindings), filters in *manual* mode
  (conditions, can reference variables) or *automatic* mode (an LLM extracts filter conditions from the query)
  (`core/rag/retrieval/dataset_retrieval.py:1509-1660`). Implementation: a SQL query on `documents.doc_metadata` yields a
  document-id list, which is passed to the vector store as `meta->>'document_id' IN ('…','…')` string-built into the SQL
  (`pgvector.py:187-191, 216-220`): a post-filter on an HNSW scan (recall loss with selective filters, inferred).
- **Multi-dataset:** "single" mode routes to one dataset by an LLM (ReAct or function-call router,
  `dataset_retrieval.py:649-694`); "multiple" mode queries all in threads and reranks.
- **Rerank:** model rerank via plugin providers (`core/rag/rerank/rerank_model.py`), score threshold, top-k. Rerank
  timeout errors are swallowed and return 0 documents (#30871).
- **Citations:** `retriever_resources` with dataset/document/segment ids, score, position, hit count
  (`core/rag/entities/citation_metadata.py:7-22`) streamed to the client; segment `hit_count` is tracked.
- External knowledge APIs (bring your own retriever) are supported (`ExternalKnowledgeApis`, `models/dataset.py:844`).

## 6. Admin, multi-tenancy & permissions

- **Tenancy:** Tenant = workspace; accounts join workspaces with role `owner | admin | editor | normal |
  dataset_operator` (`api/models/account.py:131-166`). Community edition allows multiple workspaces technically, but the
  licence forbids operating it as a multi-tenant service.
- **Dataset permissions:** `only_me | all_team_members | partial_members` (`models/dataset.py:176`,
  `DatasetPermission` `:819`). Apps bind datasets via `app_dataset_joins`.
- **Fine-grained RBAC, SSO (OIDC/SAML), web-app access control, branding** come from the closed Enterprise service
  (`core/rbac/entities.py`, `services/enterprise/*`; `RBAC_ENABLED`, `system_feature_service.py:33`). Community has
  email/password and GitHub/Google OAuth.
- **API keys:** per app and per dataset (Knowledge API), not scoped per collection subset.
- **Retention:** no per-assistant chat retention; the `clean_messages` job is driven by `SANDBOX_EXPIRED_RECORDS_*`
  settings (cloud free-plan cleanup, `api/schedule/clean_messages.py:31-42`); workflow-run log cleanup is global.
- **Observability:** workflow run and node execution logs in the UI, OpenTelemetry, Langfuse/LangSmith/Opik tracing.

## 7. Strengths worth copying

1. **Per-node preview and single-node re-run on the ingestion canvas.** Preview mode in the index node returns chunks
   without writing (`api/core/workflow/nodes/knowledge_index/knowledge_index_node.py:115-131`); last-run outputs are
   cached per node so an editor can tweak one node and re-run only it (`services/workflow_draft_variable_service.py`,
   `rag_pipeline.py:581,833`). This is exactly the "preview before publish" of 01 §5a, and Dify proves it is the most
   valued part of the UX.
2. **Pipeline-defined input forms** (`rag_pipeline_variables` with `belong_to_node_id`, `file-parentchild.yml:760-814`):
   the template author exposes a few parameters (chunk size, parent mode) and the editor fills a form; the canvas stays
   hidden. Matches "most editors never open the canvas".
3. **Parent-child chunking** with search on children and context from parents (`index_processor/processor/parent_child_index_processor.py`,
   `ChildChunk` model) and the explicit chunk-structure contract between chunker and indexer
   (`knowledge_index/entities.py:65-100`). Copy the idea, but as a typed port.
4. **Templates as data + DSL export with pinned dependencies** (`services/rag_pipeline/transform/*.yml`, `dependencies:`
   block with content hashes). Built-in, customised and remote templates (`pipeline_template/{built_in,customized,database,remote}`).
5. **Draft/published workflow rows with optimistic-lock hash** (`models/workflow.py:221-232`, `rag_pipeline.py:384`) and
   restore-to-draft (`:421`). Simple and auditable.
6. **Resumable stage machine for indexing** (`services/knowledge/indexing/execution.py:96-152`: start at parsing,
   splitting or indexing; pause; discard on delete) and the **embedding cache keyed by text hash**
   (`core/rag/embedding/cached_embedding.py`): re-runs after a pipeline change do not re-pay for unchanged chunks.
7. **Per-tenant fair queueing** in Redis with bounded concurrency (`core/rag/pipeline/queue.py`,
   `rag_pipeline_task_proxy.py:56-72`): one department's bulk upload cannot starve others. Worth copying as
   per-collection/per-department fairness in the Postgres job queue.
8. **Citation payload** with segment ids, scores, positions and hit counts (`core/rag/entities/citation_metadata.py`),
   plus segment hit counting, which is a ready signal for 01 §5a "chunks never retrieved".
9. **Metadata filter modes** (manual vs LLM-extracted automatic) (`dataset_retrieval.py:1509-1660`): automatic mode is a
   cheap win for queries like "beslut från 2025 i byggnadsnämnden".

## 8. Flaws & pain points

**Verified (code / measurement):**

1. **Deployment weight.** 16 default containers, 47 in the compose file, ~1,400 configuration variables, three
   different runtimes (Python, Go, Node) plus two sandboxes and two Squid proxies (section 2). A minimal RAG use case
   still needs plugin daemon, sandbox, agent backend, web, nginx, Weaviate.
2. **One Celery worker consumes ~24 queues** (`api/docker/entrypoint.sh:38`): ingestion, mail, workflow runs,
   plugin tasks and retention all compete unless the operator splits workers by hand. Pipeline runs use a 10-thread
   pool inside a Celery task (`priority_rag_pipeline_run_task.py:64`); Python GIL + gevent for CPU-bound parsing.
3. **Graph JSON = UI state.** Positions, selection, z-index and edge "isInLoop" flags are stored in the executable
   graph (`file-parentchild.yml`), so diffs are noisy and semantic validation is hard. Server-side publish does no
   structural/type validation (`rag_pipeline.py:458-518`); validation is a 985-line React hook.
4. **Untyped chunk contract**: `chunks: Any` + string `chunk_structure` (`knowledge_index_node.py:100-104`);
   chunk structure cannot change after first publish (`dataset_service.py:1140`), and publishing a new version does not
   re-process existing documents, so a knowledge base silently mixes outputs of several pipeline versions (inferred).
5. **Routing by file extension**, not detected content: templates hard-code ten `extension is .xlsx/.md/…` conditions
   (`file-parentchild.yml:275-370`); core `ExtractProcessor` chooses parser by extension with a global `ETL_TYPE`
   (`extract_processor.py:136-221`). There is no "detect" step (scanned vs text PDF, language, layout).
6. **No OCR for scanned PDFs in core** (`pypdfium2` text only, `pdf_extractor.py:11`); OCR requires a marketplace plugin.
7. **No change detection or scheduled sync** (no ETag/Last-Modified/content hash, no sync job in `ext_celery.py`
   beat schedule); Notion-only manual sync (`document_sync.py:58-69`). Websites must be re-imported.
8. **Swedish-hostile lexical search:** weighted hybrid uses jieba (Chinese) keywords (`weight_rerank.py:10,90-94`);
   pgvector full-text uses `to_tsvector(text)` with no language config and **no index** (`pgvector.py:235-237`).
9. **Metadata filter as an IN-list of document ids built into SQL** and applied as a post-filter on HNSW
   (`pgvector.py:187-191`); for large filtered sets this degrades both latency and recall (latency/recall inferred).
10. **Core features behind closed components:** SSO/RBAC (Enterprise API), KnowledgeFS (private repo, own DB, 8
    extra containers, `knowledge-fs.env.example`), plus a licence that forbids multi-tenant use and logo removal.
11. **Plugin dependency for basics:** chunkers and extractors in the default pipeline templates are marketplace plugins
    (`langgenius/parentchild_chunker`, `langgenius/dify_extractor`, `file-parentchild.yml:1-9`); `MARKETPLACE_API_URL`
    defaults to `https://marketplace.dify.ai` and `FORCE_VERIFYING_SIGNATURE=true` (`docker/.env.example:240-256`); plugin
    installs pip-install at runtime (`PIP_MIRROR_URL`, `PLUGIN_PYTHON_ENV_INIT_TIMEOUT=120`). Air-gapped on-prem needs
    repackaging (#35966 "repackaging plugin but still failed to install", #20387).
12. **No per-assistant chat retention** (`schedule/clean_messages.py` is plan/sandbox based).

**From GitHub issues (user-reported):**

- Upgrade breaks existing knowledge: #27291 (1.9.1→1.9.2, Weaviate named-vector error, 100 comments).
- Documents stuck in "Queuing"/"Indexing", typical fix is SQL in the DB or restarting workers: #30237, #29901,
  #26847 (Cloud), #7747, #39805 ("Pipeline Knowledge Base Exception").
- Retrieval in workflows fails beyond 100 documents while "retrieval testing" works: #29750 (thread exception).
- Retrieval regressions between versions: #20555, #19839 ("1.4 much slower than 1.3").
- Rerank timeout swallowed → 0 results: #30871. Keyword search silently empty for parent-child: #40680 (open).
- Plugin system migration (1.0) made model setup fail behind timeouts: #14603 (74 comments), #14654 (60).
- Features shipped that need services not in the standard compose: #39161 (agent_v2 needs undeployed agent backend).
- Large uploads stall api/web: #5683.

**Inferred (design-level):**

- The canvas is a *general automation* engine reused for ingestion, so ingestion inherits its weight (event streams,
  per-node DB rows, human-input pauses, iteration frames) without ingestion-specific concepts (source state,
  incremental sync, per-document provenance by pipeline version, quality gates).
- The private KnowledgeFS rewrite (TypeScript, own DB, separate workers) signals that the vendor considers the current
  Python knowledge stack insufficient at scale; open-source users get the older path.
- 32 vector-store backends × 4 search modes is a large test matrix; correctness bugs cluster at the edges (#40680,
  #27291).

## 9. Implications for the municipality design

**Copy**

- *Preview per node with no writes, and single-node re-run with cached upstream outputs* (01 §5a "Preview before
  publish"). Store last preview outputs per node in the draft so editors iterate on one node.
- *Template-exposed parameters as a form* (Dify `rag_pipeline_variables`): templates declare which node parameters are
  editable; editors with the *pipeline editor* right (01 §10) see a form, and locked nodes stay locked.
- *Parent-child chunking* as a Transform node with a typed `ParentChildChunks` output; search children, return
  parent + heading path (01 §5 step 5).
- *Draft/published rows + optimistic concurrency hash + restore*, and a DSL export with pinned node versions (01 §5a
  "versioned JSON").
- *Embedding cache by (model, text hash)* and resumable per-document stages: makes "re-process only what the change
  affects" cheap.
- *Per-department fair queueing* in the Postgres job queue (01 §5 "Jobs are queued").
- *Citation payload with chunk ids, scores, hit counts*; feed hit counts and feedback into the Auto-parse learning
  loop (01 §5a step 4).
- *Automatic metadata filter extraction* as an optional step in the fixed query sequence (01 §10 "query side").

**Avoid**

- A general workflow engine for ingestion (01 §5a "Scope guard" is right). Dify shows the cost: per-node event rows,
  generic variable pools, client-side validation.
- Storing canvas layout inside the executable graph. Keep `{nodes, edges, params}` semantic JSON and a separate
  `layout` blob, so diffs, audits and rollbacks (01 §5a) read cleanly.
- Extension-based routing and a global parser switch. Make **Detect** (01 §5a) the first route node: MIME sniffing,
  text-layer presence, language, table density, and let Auto-parse act on those properties.
- Marketplace/pip-at-runtime plugins for core nodes; ship the node catalogue in the image (01 §5a `IPipelineNode`),
  which also keeps on-prem/air-gapped installs simple.
- One worker for every queue type; and a dozen optional backends. Pick Postgres (+ optional OpenSearch per 01 §10)
  and test that path deeply.
- Licences or closed components for SSO/RBAC/multi-department use: the municipality needs OIDC and per-department
  roles in the open core (01 §7).

**Do better**

- **Typed ports enforced server-side** (01 §5a "typed ports"): validate the graph on save and on publish in the Admin
  API (not only in Vue), with node versions and input/output schemas; reject invalid graphs instead of failing per
  document at run time.
- **Version-aware re-processing**: record the pipeline version on every document/chunk; on publish compute which
  nodes changed and re-run only from the first changed node, reusing cached upstream outputs (Dify cannot do this:
  `dataset_service.py:1140` and no re-run on publish).
- **Incremental sync as a first-class source concept** (01 §5 steps 2-3): ETag/Last-Modified/content hash,
  sitemap `lastmod`, Graph delta tokens, deletions propagated. Dify's biggest ingestion gap.
- **Swedish lexical search done right** (01 §6): stored `tsvector` column with `swedish` config + GIN index,
  RRF fusion in one SQL query, metadata as indexed columns pre-filtered in the same query (not an IN-list post-filter).
- **Stuck-job hygiene**: leases with heartbeats, visible per-document state and an admin "retry from stage" action, so
  no one has to edit the DB (Dify #30237).
- **Quality gates in the pipeline** (01 §5a Quality node): Dify has none; text-yield and OCR-confidence checks with a
  fallback branch are a clear differentiator and the data source for Auto-parse learning (01 §10 auto-apply
  thresholds).
- **Retention per assistant** with a nightly deletion job and audit (01 §4, §10: 90 days); Dify has no equivalent.
- **Footprint target**: Knowledge+Chat API, worker, parser sidecar, admin API/UI, Postgres, Redis: about 6-7
  containers versus Dify's 16 by default, with a single .NET runtime plus one Python sidecar.
