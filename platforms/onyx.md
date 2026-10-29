# Onyx (formerly Danswer): platform analysis

Analysed: shallow clone `~/git/rag-platforms/onyx` (onyx-dot-app/onyx), HEAD `a4e97fc`, 2026-10-29.
Paths below are relative to the repo root; `backend/onyx/...` unless stated. Facts are **verified** (read in code,
measured, or stated in an issue) unless marked *inferred*.

## 1. Summary

- **What it is**: an enterprise "AI assistant + search over company knowledge" product. Connectors pull from ~60
  sources (`backend/onyx/connectors/` has 85 entries, incl. SharePoint, web, file, Confluence, Drive, Slack, Teams),
  index into a search engine, and a chat UI with agents ("personas"), tools (search, web search, MCP, code
  interpreter, image gen), Slack/Discord bots and a deep-research mode.
- **Stack**: Python 3 / FastAPI / SQLAlchemy + Alembic, Celery (Redis broker) under supervisord, PostgreSQL 15,
  **OpenSearch 3.6** as document index (Vespa was the index until v3.x; the Vespa→OpenSearch migration code and
  `tasks/vespa/` queue names remain), Redis, S3-compatible file store (SeaweedFS/MinIO), Next.js web app, separate
  Python "model server" images for local embedding/rerank. ~510k lines of non-test Python in `backend/`.
- **Licence**: open core. Everything outside `ee/` directories is MIT (`LICENSE`); `backend/ee/`,
  `web/src/app/ee/`, `web/src/ee/` are under the **Onyx Enterprise License** (`backend/ee/LICENSE`): production use
  requires a paid subscription per user seat; modifications to EE code belong to DanswerAI. CE loads EE code via
  `fetch_ee_implementation_or_noop` / `fetch_versioned_implementation` (`utils/variable_functionality.py`), so CE
  and EE are the same image with features switched off.
- **What is EE-only (verified by location under `backend/ee/`)**: external permission sync (doc + group sync for
  SharePoint, Drive, Confluence, Slack, …: `ee/onyx/external_permissions/`), user groups and curator scoping
  (`ee/onyx/db/user_group.py`), chat-history TTL deletion (`ee/onyx/background/celery/tasks/ttl_management/`),
  query history/analytics, SAML/SCIM, token rate limits, ingestion/query **hooks** (CE executor is a no-op:
  `hooks/executor.py:1-10`), the OpenAI-compatible LLM gateway (`ee/onyx/server/gateway/api.py:1447`), multi-tenant
  cloud tasks. CE has connectors, indexing, hybrid search, agents, document sets, citations, basic auth/OIDC.
- **Maturity**: very mature and very active. Repo created 2023-04, ~33k stars, 535 open issues, helm releases
  several per day (onyx-0.9.25 → 0.9.27 within 3 days). Code quality on connectors is high (typed checkpoints,
  explicit Graph error handling, capability checks) but the system is large and deeply stateful.

## 2. Architecture & deployment footprint

**Containers in `deployment/docker_compose/docker-compose.yml` (721 lines)**: 14 service definitions.
Started by default with the shipped `env.template` (`COMPOSE_PROFILES=s3-filestore`, line 184): **13 containers**:

| Service | Role |
|---|---|
| `api_server` | FastAPI backend (chat, search, admin APIs) |
| `background` | **one container running ~12 processes under supervisord**: 9 Celery workers + beat + beat watchdog + Slack bot + Discord bot (`backend/supervisord.conf`) |
| `web_server` | Next.js UI |
| `mcp_server` | MCP server exposing Onyx search |
| `inference_model_server` | local query embedding / rerank models (HF) |
| `indexing_model_server` | local document embedding |
| `relational_db` | Postgres 15.2 |
| `opensearch` | single-node OpenSearch, 2 GB JVM heap, memlock unlimited (lines 415-453) |
| `cache` | Redis 7.4 (Celery broker, locks, fences, cache) |
| `nginx` | reverse proxy |
| `code-interpreter` | Python sandbox for the code tool |
| `object-store` + `minio` + `object-store-copy` | S3 file store (SeaweedFS) + legacy MinIO + one-shot copier (profile `s3-filestore`) |

**Celery topology** (`backend/supervisord.conf:30-127`, schedule in `background/celery/tasks/beat_schedule.py`):
`primary` (queue `celery`), `light` (vespa_metadata_sync, connector_deletion, doc_permissions_upsert, cleanup,
chat_ttl_deletion…), `heavy` (pruning, permission sync, group sync, hierarchy fetching, capability checks),
`docfetching` (connector_doc_fetching), `docprocessing` (docprocessing, port), `user_file_processing`,
`scheduled_tasks`, `monitoring`, plus `beat`. Beat polls rather than schedules jobs directly:
`check-for-indexing` every 15 s, `check-for-pruning`/`check-for-vespa-sync`/`check-for-connector-deletion` every 20 s,
permission sync 30 s, external group sync 20 s, and ~20 more housekeeping tasks. Each "check" reads Postgres and
fans out tasks guarded by **Redis fences/locks/tasksets** (e.g. `pruning/tasks.py:307-420`).

**Indexing data flow**: docfetching worker runs the connector → stores each document batch as a blob in the file
store (`file_store/document_batch_storage.py`, `background/indexing/run_docfetching.py:987`) → sends a
docprocessing task per batch → docprocessing chunks, embeds (model server or API), writes OpenSearch, updates
Postgres. Metadata changes (ACL, document-set membership, boost, hidden) are applied later by the "vespa sync" beat
loop to every chunk (`tasks/vespa/document_sync.py`).

**Resource needs** (official, `documentation/snippets/resourcing-overview.mdx`): Standard **min 4 vCPU / 10 GB RAM,
preferred 8+ vCPU / 16+ GB**, disk 32 GB + ~2.5× indexed data. Compose limits in `docker-compose.resources.yml`:
background 6 CPU / 10 GB ("very generous, primarily to prevent OOM crashing the host"), api_server 2 CPU / 4 GB,
each model server 5 GB, Postgres 2 CPU / 4 GB, nginx 1 GB; OpenSearch heap 2 GB (container can use more).
Sum of the stated limits ≈ 29 GB. "Onyx Lite" overlay (`docker-compose.onyx-lite.yml`) drops OpenSearch, Redis, model
servers and workers, but then **connectors and RAG are disabled**: only chat + uploaded files.

## 3. Ingestion & connectors

### 3.1 Connector interface (`connectors/interfaces.py`, 361 lines): the best part of Onyx

A connector is one class that mixes in **capability interfaces**; the runtime checks `isinstance` to decide what it can do:

| Interface (line) | Method | Used for |
|---|---|---|
| `BaseConnector` (45) | `load_credentials`, `validate_connector_settings`, `normalize_url`, `set_raw_file_callback` | common; generic over a checkpoint type `CT` |
| `LoadConnector` (124) | `load_from_state()` → iterator of batches | full snapshot every run |
| `PollConnector` (131) | `poll_source(start, end)` | incremental by time window |
| `SlimConnector` (140) | `retrieve_all_slim_docs(start, end, callback)` → ids only | **pruning** (deletion detection) |
| `SlimConnectorWithPermSync` (153) | same + `external_access` per id | permission sync without content |
| `CheckpointedConnector[CT]` (274) | `load_from_checkpoint(start, end, checkpoint)` → generator yielding `Document | HierarchyNode | ConnectorFailure`, **returns** the next checkpoint | resumable incremental sync; the preferred modern interface |
| `CheckpointedConnectorWithPermSync` (313) | same, docs carry ACLs | index + perms in one pass |
| `Resolver` (324) | `reindex(errors)` | retry only the documents that failed |
| `HierarchyConnector` (340) | `load_hierarchy` | folders/sites/spaces as nodes (for scoping agents) |
| `CredentialsConnector` (255) + `CredentialsProviderInterface` (207) | refreshable, lockable shared credentials | OAuth token rotation across workers |
| `OAuthConnector` (164) | OAuth URL / code→token, PKCE flag | in-app OAuth setup |

Key design details (verified):
- **Checkpoint is a typed pydantic model** per connector (`ConnectorCheckpoint{has_more}` in `models.py:511`;
  SharePoint's adds site/drive queues, delta `nextLink`, seen ids, permission cache: `sharepoint/connector.py:210-257`).
  It is persisted after each batch, so a crash resumes mid-drive; `validate_checkpoint_json` versions it.
- **Failures are data, not exceptions**: `ConnectorFailure{failed_document | failed_entity(missed_time_range),
  message}` (`models.py:525-553`) is yielded inline. One bad document does not fail the run, and failed ids are
  **protected from pruning** (`background/celery/celery_utils.py:158-159`) and retried via `Resolver.reindex`.
- `ConnectorRunner` (`connector_runner.py`) adapts all interface styles to one stream of
  `(docs, hierarchy_nodes, failure, checkpoint)` tuples and enforces "exactly one checkpoint, at the end".
- **Document model** (`models.py:196-430`): `id`, typed `sections` (`TextSection | ImageSection | TabularSection`,
  each with its own `link` and optional `heading`), `semantic_identifier`, `title`, `metadata: dict[str, str|list]`,
  `doc_updated_at`, owners, `external_access`, `parent_hierarchy_raw_node_id`. Section-level links make citations
  point to the exact page/anchor.
- **Typed connector config with edit policies** (`field_policy.py`, `config_diff.py`, `edit_plan/planner.py`): every
  config field is annotated `IDENTITY` (part of doc ids → full re-index + prune), `SCOPE` (with
  `ScopeInclude/Exclude/Toggle/Ordered` → widen = scoped backfill, narrow = prune), `BEHAVIOR` (re-index) or `COSMETIC`.
  Editing a connector produces an **EditPlan** of minimal steps instead of "delete and re-create". Example:
  `sharepoint/config.py:52-70`, `web/config.py:41-58`.
- **Capability checks** (`capability_checks/`, SharePoint's alone is 2,169 lines): pre-flight probes of each
  permission the credential needs (read, download, delta, REST, groups), run as **draft runs on the unsaved form**
  (`draft_runs.py`) with remediation text such as "Grant `Sites.Read.All` … or with `Sites.Selected` grant the app
  read access on each configured site" (`sharepoint/capability_checks.py:155-170`). A first index attempt is **held**
  until required checks pass (`indexing_hold.py`).

### 3.2 Change detection, deletion, permission sync

- **Incremental**: scheduler compares `refresh_freq` (min 60 s, `db/models.py:2106`) with the last attempt; poll /
  checkpointed connectors get `(start = last successful poll - buffer, end = now)`.
- **Skip unchanged** (`indexing/indexing_pipeline.py:411-493`): gate 1 `doc_updated_at` not advanced → skip; gate 2
  `content_hash` equal → skip; ACL change bypasses both. Good, but the hash is computed *after* fetching and parsing.
- **Deletion = pruning**, a separate job (`background/celery/tasks/pruning/tasks.py`): list all ids via the slim
  interface, `doc_ids_to_remove = all_indexed_document_ids - all_connector_doc_ids` (line 816), fan out delete tasks.
  Default `prune_freq` 7 days (`configs/app_configs.py:1479`). Deletions are **not** taken from the delta feed: SharePoint
  delta tombstones are skipped (`microsoft_utils/drive_items.py:688`), so a deleted file stays searchable up to a week.
  **No sanity check** on the diff size (grep for empty/threshold in `pruning/tasks.py` finds nothing; see 8).
- **Permission sync (EE)**: per-source `SyncConfig` (`ee/onyx/external_permissions/sync_params.py:509-520`) with doc
  sync (slim listing with ACLs → `doc_permissions_upsert`) and group sync (Entra/SharePoint groups expanded
  recursively, `sharepoint/permission_utils.py`). ACLs are stored **denormalised on every chunk**
  (`access_control_list` field, `document_index/opensearch/schema.py:53`) and filtered at query time; changes reach
  the index through the async metadata-sync loop.

### 3.3 SharePoint connector (`connectors/sharepoint/`, ~5,600 lines incl. checks; `microsoft_utils/`)

- Implements `SlimConnector, SlimConnectorWithPermSync, CheckpointedConnectorWithPermSync, CredentialsConnector,
  Resolver` (`connector.py:726-731`).
- **Auth**: app-only, client secret **or certificate** (`microsoft_utils/graph_auth.py:49-60`). Sovereign clouds via
  `graph_api_host/authority_host`. SharePoint REST (needed for role assignments / sharing links in perm sync) only
  accepts certificate tokens, so perm sync needs a certificate + `Sites.FullControl.All` (or per-site full control
  under `Sites.Selected`) + `GroupMember.Read.All` (`capability_checks.py:155-170`). Without `Sites.Read.All`,
  "all sites" discovery is impossible (`capability_checks.py:1150`).
- **Scope**: `sites` (empty = all), `excluded_sites`, `excluded_paths` (glob), site URL may carry a library and folder
  (`extract_site_descriptors`, line 621), toggles for site pages and documents, `treat_sharing_link_as_public`.
- **Delta**: per drive `GET /drives/{id}/root/delta?token=<ISO timestamp>` (`microsoft_utils/drive_delta.py:210-221`):
  it uses Graph's **timestamp token**, not a stored `deltaLink`; items are then filtered by
  `lastModifiedDateTime` in the window (`drive_items.py:682-693`). The `nextLink` is checkpointed per page and 410
  resync is handled (`drive_delta.py:275-312`). Folder-scoped connectors use BFS listing instead of delta
  (`connector.py:836-860`). Site pages are fetched separately (`_fetch_site_pages`, line 903).
- Hierarchy nodes (site → drive → folder) are emitted so agents can be scoped to a folder.

### 3.4 Web connector (`connectors/web/connector.py`, 872 lines)

- `WebConnector(LoadConnector, SlimConnector)` (line 403): modes `recursive`, `single`, `sitemap` (`web/config.py:14-23`).
- **Every page is rendered in headless Chromium via Playwright**, also during pruning (`load_from_state` docstring,
  line 682-688; `retrieve_all_slim_docs` line 794).
- Scope = same host and path prefix of the base URL (`_same_site`, line 255). **No include/exclude patterns, no depth
  or page limit, no politeness delay** (only retry back-off), **robots.txt is read only to find sitemaps**
  (`utils/sitemap.py:22-42`), Disallow is not honoured (feature request #1058 closed without it).
- **No incremental mode**: being a `LoadConnector`, each refresh re-crawls and re-renders the whole site; no
  ETag/Last-Modified. Unchanged pages are only skipped after fetch, by content hash.
- Cleanup: BeautifulSoup + `web_html_cleanup` with an optional "Mintlify" rule set; URL rewrite rules (identity
  fields). SSRF guard (`protected_url_check`, line 153).

### 3.5 Parsing and chunking

- Parsing in-process (`file_processing/extract_file_text.py`): PDF via pypdfium2 with pypdf fallback, DOCX/PPTX via
  **MarkItDown**, XLSX via openpyxl (tabular sections stored as CSV), optional **Unstructured API** if a key is set
  (`file_processing/unstructured.py`). Images (embedded or standalone) can be summarised by a vision LLM
  (`indexing_pipeline.py:1010-1098`). No layout-aware parser (Docling-class) and no OCR path except via Unstructured
  or vision LLM (*inferred* from the imports; no tesseract/OCR library found).
- Chunking (`indexing/chunker.py`): token-based, **512 tokens** (`DOC_EMBEDDING_CONTEXT_SIZE`,
  `shared_configs/configs.py:43`), **overlap 0** (line 27), sentence-aware, title prefix + metadata suffix included in
  the embedded text, 128-token blurb, optional mini-chunks (150) and large chunks (4×) for "multipass"
  (not yet on OpenSearch, #15888), optional **contextual RAG** (LLM-written doc/chunk summaries, lines 1125-1260).
- Embedding: default local `ibm-granite/granite-embedding-97m-multilingual-r2` on the model server
  (`shared_configs/configs.py:39`), or API providers (OpenAI, Cohere, Voyage, Azure, LiteLLM, OpenAI-compatible:
  `shared_configs/enums.py:4-12`). **One embedding model per deployment**; changing it builds a FUTURE index,
  dual-writes and swaps (`db/search_settings.py:48-193`).
- **Extension points** (EE only): HTTP hooks `DOCUMENT_INGESTION` (rewrite/drop sections), `DOCUMENT_PUSH`,
  `QUERY_PROCESSING` (`hooks/registry.py`), each with a soft/hard fail strategy.
- Push API: `POST /onyx-api/ingestion` and `DELETE /onyx-api/ingestion/{id}` (`server/onyx_api/ingestion.py:104,262`).

## 4. Pipeline / workflow builder

None. The pipeline is fixed code: connector → batch blob → `index_doc_batch` (`indexing_pipeline.py:1495`):
filter → hook (EE) → image summaries → chunk → optional contextual RAG → embed → write index → update Postgres.
Per-connector knobs are only the connector's typed config (scope fields, `include_attachments`, cleanup flags,
`refresh_freq`, `prune_freq`, indexing start date). Chunk size, analyzer, embedding model and contextual RAG are
**global** search settings. Customisation beyond that means writing a connector or an EE hook endpoint.

## 5. Retrieval & answering

- **Hybrid** in OpenSearch: kNN on content vector + BM25 on title/content, combined by a search pipeline with
  **min-max (or z-score) normalisation and arithmetic mean, weights 0.5/0.5** (`document_index/opensearch/search.py:78-140`),
  500 candidates per sub-query (`opensearch/constants.py:44-46`), recency bias, time cutoff, title vector field.
  Text analyzer is global, **`english` by default** (`configs/app_configs.py:547`); changing it needs a reindex.
- **Search tool flow** (`tools/tool_implementations/search/search_tool.py:1-35`): the chat LLM issues queries → a
  separate LLM rephrase for semantic search + LLM keyword expansions → parallel searches → **weighted RRF** across
  queries → adjacent-chunk merge → **LLM selects** which documents to read → per-document **LLM expansion** of
  surrounding chunks (parallel) → JSON context to the LLM. Search is a tool the agent may or may not call (#4020
  "The AI decided this query didn't need a search").
- **Rerank**: API rerankers (Cohere, Cohere-on-AWS, LiteLLM: `natural_language_processing/search_nlp_models.py:1083-1160`)
  or local cross-encoder on the model server; in practice the LLM selection step does most of the work.
- **Citations**: documents numbered `[1]..[n]` in the tool result; a streaming `CitationProcessor`
  (`chat/citation_processor.py`) maps markers to `SearchDoc`s and emits `CitationInfo` (modes REMOVE / KEEP_MARKERS /
  HYPERLINK); a citation reminder is appended after each search (`chat/README.md`).
- **Filters**: source type, document set, tags/metadata, time, hierarchy node, attached documents, user ACL
  (`context/search/pipeline.py:41-140, 260-331`); results also post-censored per user (`censor_chunks_for_user`, EE
  `post_query_censoring.py`).
- Federated search (live Slack search instead of indexing) exists (`federated_connectors/`).

## 6. Admin, multi-tenancy & permissions

- **Agents (Persona)** (`db/models.py:4573+`): name, system/task prompt, default model, starter messages, tools,
  **document sets**, `search_start_date`, attached documents/hierarchy nodes, owner user or owner group, listed/featured.
- **Document set** (`db/models.py:4335+`): named group of connector-credential pairs, public or restricted; has
  `is_up_to_date` because membership is written onto each chunk and synced asynchronously.
- **Permissions**: token-based `Permission` enum (`db/enums.py:704+`) assigned to groups; legacy roles
  (admin/curator/global curator/basic). Groups, curators and group-scoped connectors/agents are EE.
  #15022: in CE ≥4.7 non-admins cannot create agents because of a licence-enforcement default.
- **Multi-tenancy**: `MULTI_TENANT=true` mode = Postgres **schema per tenant** (`db/engine/async_sql_engine.py:213`),
  tenant-prefixed Celery tasks and Redis keys; built for Onyx Cloud. Self-hosted is single-tenant with groups.
- **Chat retention**: one global `maximum_chat_retention_days` (`server/settings/models.py:49`); the deletion task is EE.
  No per-agent retention.
- API access: personal access tokens scoped by API-surface permissions (`read:search`, `write:chat` …).

## 7. Strengths worth copying

1. **Capability-interface connector contract** (`connectors/interfaces.py`): small interfaces a connector opts into
   (checkpointed fetch, slim id listing, perm-sync listing, resolver for failed docs, hierarchy). Copy as .NET
   interfaces: `IFetchSource` with `IAsyncEnumerable<SourceItem>` + returned checkpoint, `IListIds` for pruning,
   `IListAcl`, `IRetryFailed`, `IHierarchy`.
2. **Typed, persisted, resumable checkpoint returned from the generator** (`connector_runner.py:53-95`,
   `sharepoint/connector.py:210-257`): exactly one checkpoint per run, saved after every batch; resume mid-library.
3. **Failures as yielded values with entity + missed time range** (`models.py:525-553`), failed ids protected from
   pruning (`celery_utils.py:158`), targeted re-try of only failed items (`Resolver`).
4. **Config-field edit policies → minimal re-sync plan** (`field_policy.py`, `edit_plan/planner.py`): classify each
   source setting as identity/scope/behaviour/cosmetic so editing "exclude path X" prunes only X instead of a full
   re-index. Maps directly to the design's "re-process only what the change affects" for pipeline versions.
5. **Capability checks with remediation text and draft runs** (`capability_checks/`): test the SharePoint grant
   (Sites.Selected, download, delta) on the unsaved form and hold the first sync until it passes. Ideal for
   self-service department editors.
6. **Two-gate change detection** (`indexing_pipeline.py:411-493`): timestamp first, content hash second, ACL change
   always passes; hashes persisted only after a successful write.
7. **Section-level links and headings** in the document model (`models.py:40-82`): citations point to the page /
   anchor, not just the file.
8. **Blue/green re-embedding** (FUTURE search settings + dual write + swap, `db/search_settings.py`) for model changes.
9. **Graph details done right**: certificate auth, sovereign clouds, per-page `nextLink` checkpointing, 410 resync,
   duplicate-item suppression across delta pages, permanent-refusal vs throttling distinction
   (`sharepoint/connector.py:286, 855-870`, `microsoft_utils/drive_delta.py`).
10. **Separating fetch from process** via staged batches (docfetching → blob → docprocessing) so a slow parser does
    not hold the source connection; maps to worker → Docling sidecar.

## 8. Flaws & pain points

**Verified (code / issue / measured)**

| # | Flaw | Evidence |
|---|---|---|
| F1 | **Heavy footprint**: 13 containers by default, ~12 processes in one `background` container, OpenSearch + 2 model servers + Redis + S3 store; official minimum 10 GB RAM, preferred 16+ GB; summed compose limits ≈ 29 GB. | `docker-compose.yml`, `supervisord.conf`, `resourcing-overview.mdx`, `docker-compose.resources.yml` |
| F2 | **Memory/OOM history** of the index and workers: Vespa OOM-killed on a 64 GB host; SharePoint connector materialised all drive items in memory until rewritten; background limit explained as "to prevent OOM crashing the host". | #3427, #7860, `docker-compose.resources.yml:9` |
| F3 | **Pruning can wipe an index**: SharePoint listing returned 0 docs, prune deleted 189,833 docs and recorded SUCCESS. No guard at HEAD. | #16221 (open), `pruning/tasks.py:816` |
| F4 | **Deletion latency**: deletes only via weekly prune (default `prune_freq` 7 d); Graph delta tombstones ignored. | `app_configs.py:1479`, `drive_items.py:688` |
| F5 | **SharePoint incremental misses files**: timestamp-token delta + `lastModifiedDateTime` window skipped copied/synced files (fixed with `max(created, modified)`); files *moved* into a folder-scoped connector are still never indexed, silently. | #13583, #16590 (open), `drive_delta.py:210-221` |
| F6 | **Silent indexing stalls** from orchestration state: scheduler sorted by `time_updated`, which an hourly cleanup bumped, so connectors with `refresh_freq ≥ 1 h` stopped forever with UI showing success (fixed: `db/index_attempt.py:63` now `time_created`); connectors stuck in "Deleting"; Confluence stuck after docfetching finished; prune SyncRecord stuck IN_PROGRESS. | #14064, #1378, #11379, #14261 |
| F7 | **Silent content loss in parsing**: mail bodies, undecoded attachments, HTML-only tables, legacy .xls dropped with no error; "a document is indexed, the content is just not in it". | #15077 (open) |
| F8 | **Denormalised ACL/document-set on every chunk** needs an async sync loop (`is_up_to_date`, 20 s beat); bugs here are privacy bugs: PUBLIC→PRIVATE change not written to OpenSearch (reported open; HEAD code now writes `public`, `opensearch_document_index.py:778`). | #14981, `schema.py:53-62`, `tasks/vespa/document_sync.py` |
| F9 | **Index engine churn**: Vespa → OpenSearch migration; migration could stall forever on a >32 KB metadata term; multipass chunks not yet ported. | #10459, #15888 |
| F10 | **Web crawler is weak for a public-sector site**: Playwright for every page incl. pruning, no robots Disallow, no include/exclude/depth/rate limit, full re-crawl every refresh, no conditional GET. | `web/connector.py:403-800`, `utils/sitemap.py:22-42`, #1058 |
| F11 | **Not language-adaptive**: global `english` analyzer; Swedish needs env change + reindex; one embedding model and chunk size for all sources. | `app_configs.py:547`, `shared_configs/configs.py:39-43` |
| F12 | **No configurable pipeline**: parse/chunk strategy cannot vary by source or document type; only EE HTTP hooks. | §4, `hooks/executor.py:1-10` |
| F13 | **Open-core gating of features a municipality needs**: permission sync, groups/curators, chat TTL deletion, hooks, OpenAI-compatible endpoint, audit/query history are EE (per-seat licence); CE regressions like #15022. | `backend/ee/LICENSE`, `ee/onyx/...` |
| F14 | **Many LLM calls per question** in the search tool (query generation, rephrase, keyword expansion, selection, per-doc expansion) and search only when the agent decides; "MCP search always times out". | `search_tool.py:1-35`, #4020, #16500 |
| F15 | **Retention is global, not per assistant**; no per-collection embedding. | `server/settings/models.py:49` |

**Inferred (not measured)**
- Beat-driven polling (15-30 s loops over all cc-pairs) plus Redis fences spread a job's state across Postgres,
  Redis and blob storage; the stuck-state issues (F6) are the visible cost. A single durable job table would be simpler.
- Indexing throughput is limited by CPU embedding on the model server for small installations (#1546 reported ~1 doc/s
  and serial connectors in 2024; the worker split has since improved this).
- At <100k chunks, OpenSearch + model servers are oversized; Postgres + pgvector would cover the same retrieval.

## 9. Implications for the municipality design

**Copy**
- §4/§5 "same connector interface": adopt Onyx's capability-interface split for Fetch nodes in §5a: checkpointed
  `IAsyncEnumerable` fetch that **returns a typed checkpoint**, a separate **id-listing** capability for deletion
  checks, an ACL-listing capability (later), and a **retry-failed-items** capability. Persist the checkpoint per batch.
- §5 step 7 "one failing document never fails a whole source": model failures as yielded items with document id or
  entity + missed time range, as Onyx does; exclude failed ids from deletion.
- §5a "publishing re-processes only what the change affects": tag every node/source parameter as
  identity / scope (widen/narrow) / behaviour / cosmetic, and compute a re-sync plan (Onyx `edit_plan`). This is the
  cheapest way to make versioned pipelines safe to change.
- §11 Q4 (SharePoint access): copy capability checks with remediation text and draft runs on the unsaved source
  form; hold the first sync until they pass. Use certificate auth from day one (needed later for SharePoint REST/ACLs).
- §5 step 3: two-gate change detection (timestamp/ETag, then content hash; ACL change always passes).
- Section-level `link` + `heading` in the parsed document so citations (§6) point to page/anchor.
- Blue/green re-embedding per collection when the gateway embedding model changes (§4 "one embedding model").

**Avoid**
- Onyx's footprint (F1, F2): keep the §3 service list (5 services + Postgres + Redis) and do not add a search engine or
  model servers; embeddings and rerank go through the gateway (§2.3). This also answers §10 "search backend":
  Onyx's own resourcing (10-16 GB, OpenSearch disk watermarks, OOM history, engine migration F9) argues for pgvector at
  <100k chunks.
- Denormalising ACLs and collection membership onto every chunk with an eventual-consistency sync (F8). In
  Postgres, join chunk → document → source → collection and filter in the same query (§2.5).
- Deletion only via periodic full listing (F4) and **unguarded** pruning (F3).
- A crawler that renders everything in a browser and re-crawls fully (F10); §5 already plans sitemap-first,
  robots, rate limits and headless as opt-in. Keep it so.
- Open-core gating of compliance features (F13): retention, audit and per-assistant access must be core (§2.7).

**Do better**
- **Pruning guard**: refuse to delete more than N % (or any, if the listing is empty) of a source's documents without
  confirmation; record "suspicious listing" as a failed sync, not SUCCESS (fixes F3).
- **Use Graph delta properly** (§5 step 2): store the opaque `deltaLink` per drive and process `deleted` / `@removed`
  items as deletes immediately; add change notifications (§5 step 1) as triggers; run the id-listing sweep only as a
  weekly safety net. This avoids F4 and F5 (moved/copied files with old `lastModifiedDateTime`).
- **Quality check node as the cure for silent loss** (F7): the §5a Quality node (text yield per page/byte, empty
  sections, table presence vs source, OCR confidence) should flag "indexed but empty" documents in the admin UI, and
  feed the Auto-parse learning loop. Onyx has no such signal; issue #15077 was found only by poor eval scores.
- **Observable job state** (F6): one Postgres job table with explicit state machine, heartbeat and timeouts; derive
  "last successful sync" from immutable timestamps (`started_at`, `finished_at`), never from `updated_at`.
- **Per-source / per-document-type strategy** (F11, F12): Swedish FTS config, chunk sizes and parse strategy belong to
  the pipeline (§5a), not to global settings; this is where the visual builder and Auto-parse beat Onyx directly.
- **Fixed, cheap query path** (§10 builder scope): Onyx's 4-5 LLM calls per search (F14) explain its latency; keep
  the per-assistant linear query sequence with optional rewrite + one rerank, and always retrieve for RAG assistants.
- Per-assistant retention (§4, §10: 90 days default) instead of Onyx's single global TTL (F15).
