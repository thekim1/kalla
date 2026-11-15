# Eneo and Ume.Rag + the gateway: where we are better, and what to build next

> Background research from 2026-11-15. It does not bind: the Decisions and the design documents decide. Facts are as
> of the commits named below and are not kept up to date.

Compared:

- **Eneo**, local clone `~/git/eneo` on `develop` at `df28cfe8b` (2026-11-11, `v2.3.0-rc.0-159`). Paths are relative to
  `~/git/eneo`; `B/` means `backend/src/eneo/`. That is the module root now. It was `backend/src/intric/` in v2.1.1,
  which [eneo.md](eneo.md) analysed.
- **Ume.Rag** `main` at `b611a760` (2026-11-15, after `v0.5.0`). Paths are relative to this repository.
- **The gateway**, `~/git/llmgateway`, at the version the test chatbot runs (v0.10.x).

Labels: **built** means the code exists on `main`. **In review** means there is an open PR. **Designed** means a
design or decision exists but no code. **Planned** means it is only a roadmap theme. Every "Eneo lacks" names the grep
that found nothing. The searches ran over `B/` and, for the UI, over `frontend/apps/web` and `frontend/apps/web-next`.

**Correction to [eneo.md](eneo.md).** Eneo moved a lot between v2.1.1 and today. These points in eneo.md no longer
hold:

- Scrapy has been replaced by its own aiohttp crawler (`B/crawler/python_engine.py`).
- The crawler sends conditional GETs (`python_engine.py:1403-1405`, 304 handled at `:867`).
- An unchanged page is no longer re-embedded (`B/worker/crawl/persistence.py:263,587-595`).
- Main-content extraction picks `main`/`article`/`role=main` (`B/crawler/extraction.py:148-216`).
- The crawler has an SSRF guard (`B/crawler/destination_policy.py`).
- Rerank exists per assistant (`B/database/tables/assistant_table.py:76-80`).
- PDF tables are kept as Markdown (`B/files/text.py:193-336`).
- A relevance floor and autocut exist (`B/embedding_models/infrastructure/datastore.py:46-76,255-262`).
- A tool-calling retrieval mode exists (`knowledge_mode` TOOL, `B/internal_mcp/knowledge.py:686-1054`).

These still hold: vector-only search, no ANN index, a vector column without a dimension, install-wide 200/40-token
chunks, `<inref>` citations and no OCR. The backend has grown to 77 modules, about 247k lines of Python and 503
Alembic migration files (`ls B | wc -l`; `find backend -name '*.py' | xargs wc -l`; `find backend -path
'*alembic/versions*' -name '*.py'`).

## Part 1. What Ume.Rag + the gateway do better

### 1.1 The RAG pipeline, feature by feature

| Feature | Ume.Rag + gateway | Eneo | Evidence (Ume.Rag; Eneo; grep) |
|---|---|---|---|
| Vector search | **Built.** `vector` storage with a partial `halfvec(N)` HNSW index per embedding profile. A scope of up to 10,000 chunks is searched exactly, a larger one through HNSW, and the worker samples recall | Pure cosine scan with `SET LOCAL enable_seqscan = off`. No ANN index. `Vector()` has no dimension | `src/Ume.Rag.Retrieval/Postgres/RetrievalStatements.cs:113-130`; Decisions "Search cost at scale". Eneo: `B/info_blobs/info_blob_chunk_repo.py:113-143`, `B/database/tables/info_blob_chunk_table.py:16`, `backend/alembic/versions/202608251200_add_info_blobs_integration_knowledge_index.py:6` ("There is no vector index"). grep `hnsw\|ivfflat\|vector_cosine_ops` |
| Keyword / full-text | **Built.** Swedish `tsvector` (heading weighted A, text B) on a GIN index, `ts_rank_cd`. Swedish compounds are split in .NET with the `sv_SE` Hunspell list. Terms found in more than 30 % of the scope are dropped | `keyword_search` (`text.match`) exists but nothing calls it. No tsvector and no Swedish config | `RetrievalStatements.cs:60-110`, `src/Ume.Rag.Retrieval/Text/SwedishDecompounder.cs`, `src/Ume.Rag.Worker/Engine/KeywordStatistics.cs`. Eneo: `info_blob_chunk_repo.py:253-273`. grep `tsvector\|to_tsvector\|ts_rank\|bm25\|'swedish'`; `keyword_search(` (no callers) |
| Hybrid fusion | **Built.** Weighted RRF in .NET, then age decay and `demote` patterns per collection, then at most 3 chunks per document | Lacks. The `1/(index+1)` sum only orders prompt groups | `src/Ume.Rag.Retrieval/RrfFusion.cs:43-86`, `RecencyAdjuster.cs`, `DemoteAdjuster.cs`, `ResultDiversifier.cs`. Eneo: `B/completion_models/infrastructure/context_builder.py:611`. grep `reciprocal\|rrf` |
| Chunking | **Built.** Split on headings. 400 tokens by default, including a breadcrumb › heading prefix, with about 15 % whole-sentence overlap. Tables become row groups. BGE-M3 tokenizer. Set per pipeline template. The Knowledge API expands sections | `RecursiveCharacterTextSplitter`, 200/40 tokens, install-wide (`CHUNK_SIZE` env). Not heading-aware. A title/metadata header is added for embedding only | `src/Ume.Rag.Worker/Processing/Chunking/SectionSplitter.cs`, `Chunker.cs`; Decisions "Chunking"; `src/Ume.Rag.Retrieval/SectionAssembler.cs`. Eneo: `B/embedding_models/domain/chunking.py:3-4,27,139-150`, `datastore.py:92-111`. grep `chunk_size\|chunk_overlap` |
| Embedding model and dimensions | **Built.** One platform embedding profile with fixed dimensions, measured by a canary on save. The pilot uses `text-embedding-3-large` at 1024 dimensions from an `Eu` account, through the gateway with a residency ceiling. Unchanged chunks hit a cache keyed by text hash | Any LiteLLM model per tenant, with optional `dimensions`. Called with the provider credentials stored in Eneo. Only the first source's model is used across sources | Decisions "Pilot embedding model, U-b", "Editable embedding profiles before use"; `src/Ume.Rag.Worker/Processing/Embedding/EmbedStage.cs:54-78`. Eneo: `B/embedding_models/infrastructure/adapters/litellm_embeddings.py:94,130,180`, `B/assistants/references.py:71-77` |
| Coverage / not-found gate | **Built.** A `CoverageEvaluator` with vector and keyword floors, set from answerable and unanswerable gold-set questions. Low coverage gives a fixed not-found text with contact links and makes no model call. `ume.outcome` reports it | Partial. Autocut, plus an optional deployment-wide `min_score` (unset by default, and not used in TOOL mode). "Not found" is left to the model through the prompt | `src/Ume.Rag.Retrieval/CoverageEvaluator.cs:37-73`; Decisions "Not-found gate". Eneo: `datastore.py:46-76,255-262`, `B/main/config.py:826`, `B/completion_models/infrastructure/static_prompts.py:93-100`. grep `autocut\|min_score\|cannot be found` |
| Query condensing | **Built.** A follow-up is rewritten into a standalone search query (1.2 s budget, raw question on timeout). A ticket over 2,000 characters becomes a search query while the answer model reads the full text | Lacks as a step. INJECT embeds the whole session concatenated. In TOOL mode the model writes its own query | `src/Ume.Rag.Answering/Chat/Condenser.cs:150-171`; Decisions "Condense on the EU route". Eneo: `B/assistants/references.py:27-30,159-214`. grep `condense\|standalone question\|hyde` |
| Multi-collection search | **Built.** An assistant links several knowledge bases, optionally narrowed to topics. Shared knowledge bases cross departments read-only | **Level.** Sources are OR-ed in one query. TOOL `within=` narrows to one source | `src/Ume.Rag.Retrieval/SearchScope.cs:26-30`; Decisions "Organisation". Eneo: `info_blob_chunk_repo.py:56-61`, `B/internal_mcp/knowledge.py:706-709` |
| Topics / metadata filter | **Built.** Path-rule topics are GIN-indexed tags applied inside the same SQL, before ranking | Lacks. Filters by source id only. SharePoint metadata is embedded in the text but never filtered on | knowledge design §6.2. Eneo: `B/info_blobs/source_metadata.py:1-7`. grep `_filter_on_sources -A30 \| grep metadata` |
| Rerank | **Built, optional.** Per assistant: `chat` (a small chat model, 3 s) or `gateway` (the gateway's Cohere-compatible `POST /v1/rerank`, 1.5 s). It reranks the top 20 fused candidates at the turn's residency ceiling, and an error keeps the fused order. Turned on only when the gold set shows a gain | **Level.** Per-assistant `rerank_model_id` through LiteLLM `arerank`, candidates min(k×5, 100), gated by classification, vector order on failure | `src/Ume.Rag.Retrieval/GatewayReranker.cs`, `ChatModelReranker.cs`; Decisions "Rerank pulled forward"; gateway `src/Ume.LlmGateway.Domain/GatewayEndpoint.cs`. Eneo: `B/rerank_models/infrastructure/litellm_reranker.py`, `B/rerank_models/domain/rerank_spec.py:6-25` |
| Related documents | **Built.** `ume.related_documents` (1–10) from the same retrieval, cited and uncited, with links. Empty on low coverage | Lacks. Only cited sources are shown | `src/Ume.Rag.Answering/Chat/RelatedDocuments.cs`; chat design §9.7. Eneo: `B/assistants/reference_tags.py:89-120`. grep `related.?doc\|see also` |
| Follow-up suggestions | **Built.** `ume.follow_ups` (1–5) from one condense-model call after the answer, sent as a second streamed chunk. Lines with markers, links or personal data are dropped. Never stored | Partial. Up to 4 fixed starter questions per widget, none generated | `src/Ume.Rag.Answering/Chat/FollowUps.cs`, `src/Ume.Rag.Api/ChatStreaming.cs:84-93`. Eneo: `B/widgets/domain/widget.py:37-40,144-181`. grep `follow.?up question\|suggested.?question\|follow_ups` |
| Citation markers and validation | **Built.** `[n]`, `[n, m]`, `[Källa n]`. Invalid numbers are removed from the text. Sources are renumbered by first citation and only cited sources are shown. An output filter de-links hosts outside the allow-list and removes images | Partial. `<inref id="8hex"/>`. Only tags that match a retrieved blob become stored references, but an invalid tag stays in the text and the UI renders nothing for it | `src/Ume.Rag.Answering/Chat/Citations.cs:14-72`; chat design §9.2–§9.4. Eneo: `B/assistants/reference_tags.py:107-119`, `frontend/apps/web/src/lib/components/markdown/renderers/Inref.svelte:18-25` |
| PDF page links and content route | **Built.** A crawled citation is its page URL. An uploaded one is a 7-day signed grant link with `#page=` of the first cited page. `GET /v1/documents/{id}/content` re-checks access on every download and opens PDF/PNG/JPEG inline | Partial. A source list links out or opens a text preview with a signed download. The extractor writes `[PAGE n]` into the text, but there is no page link | `Citations.cs:66-72`, `src/Ume.Rag.Api/KnowledgeEndpoints.cs:38-40`, `src/Ume.Rag.Answering/DocumentContentReader.cs:76-89`. Eneo: `frontend/apps/web/src/lib/features/knowledge/components/BlobPreview.svelte:41-84`, `B/files/text.py:317`. grep `#page=\|page_number` |
| General-knowledge level | **Built** per assistant: `off`/`supplement`/`fallback`/`open`, each with a locked frame. A `fallback` answer carries a platform caveat and is counted `answered-general`. **In review** (PR #736): `ume.general_knowledge` per request, which can only tighten | Prompt text only ("Only use the sources", `HALLUCINATION_GUARD`, TOOL rule 4). No toggle per assistant or per request | `src/Ume.Rag.Answering/Chat/AssistantChatSettings.cs:11-47`; Decisions "General knowledge per assistant". Eneo: `static_prompts.py:84-100`, `B/assistants/assistant.py:355-404`. grep `general.?knowledge\|knowledge_only\|grounding` |
| Evaluation | **Built.** Retrieval recall@10 and MRR after every publish and nightly, on human and generated question sets. A test panel runs chat's code on the draft. `scripts/grading/` grades the gold set. The go-live gate is hand-graded (correct ≥ 80 %, citation precision ≥ 90 %). The retrieval evaluation itself is advisory and never blocks a publish | Lacks. No gold set, metrics or gate. The benchmark scripts cover crawler CPU and extraction only | `src/Ume.Rag.Worker/Evaluation/EvaluationScoring.cs:45`, `EvalScheduler.cs`, `src/Ume.Rag.AdminApi/AssistantTestEndpoints.cs`; Decisions "Gold set …", "Evaluation models at the collection's ceiling". Eneo: `backend/scripts/benchmark_*.py`. grep `ragas\|gold.?set\|recall@\|ndcg\|mrr\b\|test.?questions\|publish.?gate` |

| Ingestion | Ume.Rag | Eneo | Evidence |
|---|---|---|---|
| PDF | **Built.** Docling parser with a pypdfium2 fast path. Pages are routed by `/v1/inspect` signals (text layer, OCR or re-render) | pdfplumber with tables as Markdown. Above 200 pages, pypdfium2 text without tables | `src/parser/ume_parser/inspect.py`, `textlayer.py`; parser design §3. Eneo: `B/files/text.py:193-336` |
| OCR | **Built.** Tesseract `swe` for scanned pages and images, plus image captions | Lacks. A scanned PDF returns "" with a warning. Vision is used for chat attachments only | Decisions "Parser". Eneo: `text.py:327-334`. grep `tesseract\|ocr\b\|docling\|document.?intelligence` |
| DOCX / XLSX / PPTX | **Built** (Docling, structure kept) | **Level** (docx2python, pandas+calamine, python-pptx with notes) | `src/Ume.Rag.AdminApi/UploadEndpoints.cs:45-49`. Eneo: `text.py:351-481` |
| ODF (odt/ods/odp) | **Built** | Lacks | `UploadEndpoints.cs:47`. Eneo: grep `odt\|opendocument` |
| Legacy doc/xls/ppt, RTF | **Built** for uploads: LibreOffice in the parser image, a profile per job, process-group kill. **In review:** prlimit and a zip pre-check on its output (PR #735) | Rejected ("please save as .docx") | Decisions "Parser". Eneo: `text.py:484-490`. grep `soffice\|libreoffice\|rtf` |
| E-mail (.eml/.msg) | **Planned** (P2, Z1) | Lacks | roadmap *P2 Ingestion*. Eneo: grep `\.eml\|\.msg\|extract_msg` |
| HTML upload, images | **Built** (HTML, JPG/PNG/TIFF with OCR) | HTML only from the crawler. Images not indexed | `UploadEndpoints.cs:47`. Eneo: `text.py:106-128` (allowlist) |
| Audio | Not supported | **Eneo ahead:** transcription into knowledge | Eneo: `B/files/transcriber.py`, `B/worker/upload_tasks.py:422-445` |
| Size and bomb limits | **Built.** 50 MB upload. The zip pre-check reads the central directory (10,000 entries, 512 MiB, ratio 200, nested archives refused). Limits on pixels, TIFF frames, cells and pages | **Level.** Extraction in a subprocess with RLIMIT_AS 1 GiB, 30 s CPU, 1,000 PDF pages, archive guard 128 MiB / 10k entries | `src/parser/ume_parser/limits.py:17-40`, `UploadEndpoints.cs:31`. Eneo: `B/files/extraction_limits.py:7-23` |
| Queue and workers | **Built.** A Postgres queue (`SKIP LOCKED`, leases) with job classes `fetch`/`parse`/`embed`/`sync`/`eval`/…, each with its own concurrency. The parser, ClamAV and the renderer are separate containers | ARQ on Redis, two queues (general, crawler), `worker_max_jobs` 15. The worker uses the API image | `src/Ume.Rag.Infrastructure/Jobs/PostgresJobQueue.cs:10-48`, `src/Ume.Rag.Domain/Entities/ContentEntities.cs:4-15`. Eneo: `B/jobs/job_manager.py:29-30`, `docs/deployment/docker-compose.yml:180-227` |
| Incremental re-index | **Built.** Conditional GET, then a content hash. Only changed content is re-parsed. Embedding cache by text hash. A noise-only change re-keys the chunks without re-embedding | **Level** for crawled pages and SharePoint (hash plus conditional GET, delta tokens) | `src/Ume.Rag.Worker/Connectors/Http/HttpFetcher.cs:199-206`, `src/Ume.Rag.Worker/Connectors/Changes/HtmlNoiseNormalizer.cs`. Eneo: `B/worker/crawl/persistence.py:587-595`, `B/integration/.../sharepoint_index.py:4-5` |
| Malware scan | **Built.** ClamAV on every upload and every fetched non-HTML file, fail-closed. Optional per deployment, and "not scanned" is shown when it is off | Lacks | `src/Ume.Rag.Worker/Connectors/Malware/MalwareScanner.cs`; Decisions "Malware scan optional". Eneo: grep `clamav\|clamd\|virus\|malware` |
| Personal data in content | **Built.** .NET patterns on every chunk set, plus KBLab BERT NER when names can change the flag. `piiMode` `hold`/`report`. Counts are stored, never values | Lacks (redaction exists only for logs) | `src/Ume.Rag.Worker/Processing/Pii/PiiPatterns.cs`, `Ner.cs`, `PersonalDataStage.cs`; Decisions "Personal data". Eneo: `B/observability/redaction.py`. grep `presidio\|personnummer\|pii` |

| Crawling | Ume.Rag | Eneo | Evidence |
|---|---|---|---|
| Sitemaps, `lastmod` | **Built.** Sitemaps, text sitemaps, `/sitemap_index.xml`, and a reliability score for `lastmod` per host | Has a bounded sitemap parser and stores a sitemap fingerprint | `src/Ume.Rag.Worker/Connectors/Sitemaps/SitemapParser.cs`, `Crawl/LastmodReliability.cs`. Eneo: `B/crawler/sitemap.py:64-158` |
| Depth and page limits | **Built.** Defaults 10,000 pages and depth 10. Trap and soft-404 detection | Item cap and page byte cap. **No depth limit** | `Crawl/CrawlStores.cs:193-195`, `TrapDetector.cs`, `SoftNotFound.cs`. Eneo: `B/worker/crawl_tasks.py:616,949`. grep `DEPTH_LIMIT\|max_depth` |
| JavaScript rendering | **Built.** `rag-renderer` (Playwright, no route out). Every browser request goes through the worker's egress guard. Quiet-period or `readySelector` readiness | Lacks | `src/Ume.Rag.Worker/Connectors/Render/PlaywrightRenderer.cs`; Decisions "JavaScript rendering". Eneo: grep `playwright\|splash\|selenium` |
| Politeness per host | **Built.** Per-host rate, connections, `Crawl-delay`, latency throttle, night window, breaker. Presets and audited per-host overrides | Global delay, concurrency 4 per crawl and 20 global, per-origin `Crawl-delay` | `src/Ume.Rag.Worker/Connectors/Hosts/PolitenessProfile.cs`, `HostBudget.cs`; Decisions "Configurable politeness per host". Eneo: `B/crawler/python_engine.py:127-150,497-505` |
| robots.txt | **Built.** Can be ignored per host only with a recorded reason | **Level** (`obey_robots`) | `Connectors/Robots/RobotsTxt.cs`. Eneo: `python_engine.py:390-472` |
| Deletion guard | **Built.** A crawl counts as complete at ≥ 99 % of seeds. Deletions above 20 % (min 5) are proposed, not executed. A 14-day grace period with restore | Partial. Website deletions only after a successful crawl, with no ratio cap. SharePoint has a 50 % cap | `Connectors/Deletion/DeletionGuard.cs:36-85`, `TraversalCompleteness.cs`. Eneo: `B/worker/crawl_tasks.py:1304-1345`, `B/integration/infrastructure/content_service/sharepoint_reconciliation.py:42-44` |
| SSRF / egress | **Built.** DNS-pinned egress guard. Private ranges are blocked unless listed in `Crawl:AllowedPrivateRanges`, backed by the host firewall and a startup canary. No cookies or credentials sent | **Level.** Every resolved address is checked. Private ranges are **allowed by default** (`CRAWLER_BLOCK_PRIVATE_NETWORKS` opts in to blocking) | `Connectors/Egress/EgressGuard.cs:97-181`. Eneo: `B/crawler/destination_policy.py:3-57` |
| URL include/exclude | **Built.** Globs, where exclude wins and off-site URLs are admitted as leaves only when named | Lacks. Path-prefix scope only | `Connectors/Crawl/CrawlScope.cs:22-40`. Eneo: `B/crawler/extraction.py:109-121` |
| Main content | **Built.** Per-host site profile: root selector, boilerplate selectors, link-list blocks dropped, breadcrumb and date facts | Partial. Landmark fallback, header/nav/footer stripped | `src/Ume.Rag.Worker/Processing/Html/HtmlExtractor.cs:47-60`. Eneo: `extraction.py:148-216` |

### 1.2 Why each area matters, and what lies behind it

**Architecture and performance (built; the full load test is open).**

- *What we do:* small services, each with one job. `rag-api` is the data plane, `rag-admin` the control plane,
  `rag-worker` does ingestion, and `rag-parser`, `rag-renderer` and `rag-clamav` run in separate zones
  (Decisions "Pilot containers"). The chat hot path reads the assistant from a cached policy snapshot and runs no SQL
  (Decisions "Chat API (step 8)"). Retrieval is three short statements, with keyword search running in parallel with
  the query embedding (knowledge design §6).
- *Eneo:* nine compose services (`docs/deployment/docker-compose.yml:53-316`), but the API, the worker and the crawler
  worker run one image (`RUN_AS_WORKER`, `backend/run.sh:6`). Migrations run when the API starts (`run.sh:27`). Each
  request still builds a DI container (`B/server/dependencies/container.py:46`). The ask path loads the whole space
  (`B/spaces/space_repo.py:1589`, 41 `selectinload`s). Nothing caches the space or assistant (grep `redis.*cache\|@cached\|lru_cache`).
- *Measured:* Eneo v2.2.3 adds 53–66 ms per message, issues 78 SQL statements, peaks at 49 msg/s on 4 vCPUs and fails
  above 128 clients (`~/git/llmgateway/docs/performance.md`, "eneo v2.2.3"). That benchmark measured the gateway, not
  Ume.Rag's chat path. Ume.Rag's figures so far are local only:
  - retrieval: p95 19 ms at 8 clients (results-b, B-27);
  - platform share of a chat turn during a crawl: p95 307 ms against 290 ms idle (results-a, A-13);
  - keyword statement: within budget for frequent terms, 60–95 ms for terms in 10–30 % of a 100k-chunk scope (results-b, row 5).

  R1 on the test host is still open (roadmap step 4).
- *Why it matters:* a municipality runs this on its own hosts. Ingestion that cannot starve chat, and a chat path that
  slows down instead of failing at peak, decide whether one 8 vCPU host is enough.

**Retrieval quality and citations (built).**

- *What we do:* the table above. Swedish full-text search with compound splitting, hybrid fusion, a calibrated
  not-found gate, condensed follow-ups, citations validated and renumbered, and page-level PDF links.
- *Eneo:* vector-only retrieval with no ANN index, and "not found" left to the model.
- *Why it matters:* Swedish municipal text is full of compounds and proper names (e.g. *förskoleplats*,
  *bygglovsansökan*), and pure embeddings miss exact terms. A fixed not-found text with no model call cannot be
  hallucinated. A staff member who sees `#page=14` checks the claim in seconds.

**Crawling and ingestion (built, except e-mail).**

- *What we do:* JavaScript rendering, OCR, ODF and legacy Office, include/exclude globs, a ratio deletion guard, ClamAV,
  and personal-data flags on content.
- *Eneo:* has none of these. It has closed much of its old gap (conditional GET, hash skip, landmark extraction).
- *Why it matters:* municipal archives hold scanned PDFs and old `.doc` files, intranets are often built in JavaScript,
  and a bad sitemap must not wipe a knowledge base.

**Security and data residency (built).**

- *What we do:* every model call goes through the gateway with an explicit residency ceiling from the data's level:
  `public` External, `internal` Eu, `sensitive` OnPrem. The worker never sends `External`. The answer's
  `x-ume-residency` is checked (Decisions "Residency per level"). The gateway adds a PII policy per key (detect, redact,
  block, or reroute on-prem, with Swedish personnummer and samordningsnummer, `~/git/llmgateway/docs/personal-data.md`),
  a content guard, and routing rules that keep personal data on-prem (`docs/routing-rules.md`). No provider secret lives
  in Ume.Rag.
- *Eneo:* calls LiteLLM directly (`B/model_providers/infrastructure/litellm_transport.py:211`) with provider credentials
  stored per tenant (`B/database/tables/model_providers_table.py:14,25`). A model's `hosting` is a free-text label
  defaulting to "swe" that nothing enforces (`B/database/tables/ai_models_table.py:38`). It has no PII detection
  (grep `presidio\|personnummer\|pii`). Its security classifications on spaces and models are a real control and
  match our levels (`B/spaces/space.py:369-393`).
- *Why it matters:* under GDPR and Schrems II, "this assistant can only reach EU or on-prem models" has to be enforced
  at the point of the call, not written on a label.

**Access control and departments (built for one department; several departments planned).**

- *What we do:* departments come from the gateway's catalogue and own all content. A knowledge base is shared
  read-only with its department, named departments or the whole municipality, optionally narrowed to topics
  (Decisions "Organisation"). Each assistant has an audience of clients and groups, and `rag-*` roles are scoped per
  department. Each knowledge base and assistant runs on its department's gateway key.
- *Eneo:* has tenants, spaces with admin/editor/viewer roles, SCIM and group members
  (`B/spaces/api/space_models.py:81-84`, `B/scim`). Content is shared per space. Cross-space sharing of one knowledge
  base was not checked (unverified). Neither platform trims SharePoint permissions: Eneo has no ACL columns
  (grep `permissions\|acl` in `B/integration`), and we have no SharePoint connector yet (P2).
- *Why it matters:* DoIT's support knowledge base can serve another department's assistant without a copy and without
  that department seeing the documents' content.

**Cost attribution and budgets (built; the live check is open, B-42–B-46).**

- *What we do:* every call of a chat turn runs as the assistant's assigned gateway key, and indexing runs as the
  knowledge base's key. The gateway charges, limits and budgets each call per key, team and department in USD (or token
  quotas on-prem). Ume.Rag stores a split per step and day (`chat."AssistantDailyUsage"`,
  `src/Ume.Rag.Domain/Entities/ChatEntities.cs:262-284`) and shows it beside the gateway's figure (`CostsView.vue`).
  A spent budget is refused before the first byte (Decisions "Cost attribution").
- *Eneo:* counts tokens per tenant and user (`B/token_usage`). Cost is estimated in the browser from per-token prices
  (`frontend/apps/web-next/src/features/admin/usage/usage-page.tsx:33,183`). The only enforced budget is a widget's
  `daily_token_budget` (`B/widgets/domain/widget.py:282`). There is no per-space breakdown and no export
  (grep `budget\|spending_limit\|chargeback\|invoice`).
- *Why it matters:* municipal departments pay their own way. A budget that stops a runaway assistant, and a figure
  finance can trust, are required for internal chargeback.

**Admin experience (built; Swedish UI not done).**

- *What we do:* the department journey in one admin UI:
  - preview before the first sync, run reports with reasons per document, and an Overview with sync health;
  - drafts with publish, history and restore, and a test panel showing coverage and sources;
  - a Files tab with folder topics, *Retry failed* and 14-day restore;
  - model pickers that grey out what the key may not use.

  References: admin design §5; Decisions "Admin API and UI", "Upload knowledge bases".
- *Eneo:* a broad product UI in Swedish and English, with insights, templates, help assistants and a "What's new" feed.
- *Why it matters:* we are ahead on transparency (why a page failed, what a sync would delete, what a draft answers)
  and behind on language (our UI is English only for now; Decisions "Admin UI in English").

**Operations and deployment (built).**

- *What we do:* a digest-pinned release bundle, Compose with resource zones, Portainer, optional Kubernetes
  (`deploy/kubernetes/`), pgBackRest with point-in-time restore and a drilled restore (`deploy/restore-pitr.sh`),
  rolling updates (`deploy/rolling-update.sh`), a one-shot migration service, OpenTelemetry metrics and traces
  (`src/Ume.Rag.ServiceDefaults/Extensions.cs`), and numbered security events (`docs/security-events.md`).
- *Eneo:* no Kubernetes or Helm (`find -iname '*helm*' -o -name Chart.yaml`), and backup is manual `pg_dump` steps in
  `docs/DEPLOYMENT.md:261-311`. Its OpenTelemetry has no metrics or OTLP export (`docs/OBSERVABILITY.md:347-353`,
  `B/main/observability.py:154` `NoOpMeterProvider`). Migrations run when the API starts.
- *Why it matters:* the IT operations team needs metrics, alarms and a tested restore before it signs off on running
  the platform.

**Evaluation and quality gates (built; the gate is applied by hand).** See the table.

- *Eneo:* has nothing comparable (grep `ragas\|gold.?set\|recall@`).
- *Why it matters:* "how often is it right" is the question politicians and the data protection officer ask. We
  answer it with numbers per publish.

**Integrations and APIs (built; MCP designed).**

- *What we do:* `GET /v1/models` and `POST /v1/chat/completions` are OpenAI-compatible, so an assistant is a "model"
  any OpenAI SDK can call (`src/Ume.Rag.Api/ChatEndpoints.cs:26,50`). The `ume` extension adds `outcome`, `degraded`,
  `related_documents`, `follow_ups`, and (in review) `general_knowledge`. `/v1/retrieve` and `/v1/documents` serve
  retrieval and documents. Open WebUI is integrated: forwarded user JWT, streamed citations, feedback import
  (`src/Ume.Rag.Api/OpenWebUiUserJwt.cs`, `FeedbackImporter.cs`). `/mcp` with `search_knowledge` is designed for P2
  (tools design; Decisions "Tools and MCP").
- *Eneo:* has its own REST API, with no `chat/completions` (0 hits in `B/`) and no Open WebUI integration (grep
  `open.?webui`: one comment). It is an MCP **client** with loopback-only internal servers (`B/internal_mcp/foundation.py:34-41`),
  not an MCP server for outside clients. It has `pk_`/`sk_` keys and an embeddable widget (`B/widgets`).
- *Why it matters:* the intranet team, the case-system vendors and the support frontend team can use their existing
  OpenAI SDK without an adapter.

**Privacy and retention (built).**

- *What we do:*
  - Chat history mode `none`: no question or answer text is stored, only metadata and ratings. Query text is never
    stored (Decisions "Chat history", "Knowledge API (step 7)").
  - Feedback comments are dropped in mode `none`.
  - Content retention runs (`src/Ume.Rag.AdminApi/RetentionService.cs`).
  - Personal data in indexed content is flagged and held.
  - The gateway stores no prompt or response content (`~/git/llmgateway/README.md`).
- *Eneo:*
  - stores every conversation, with a well-designed retention hierarchy (organisation → space → assistant,
    `B/data_retention/domain/rules.py:42-48`);
  - has audit reads that require a justification (`B/api/audit/routes.py:22-23,74,256`);
  - has no PII redaction in stored questions and no anonymisation (grep `anonymi\|pseudonym`).
- *Why it matters:* until the archive-law question (A1) is answered, storing nothing is the safest position. Note that
  Open WebUI keeps its own copy of each chat (W2), so our "store nothing" holds for Ume.Rag only.

### 1.3 Where Eneo is ahead or level

- **Product breadth:** Eneo is a complete workspace. Ume.Rag relies on Open WebUI or a team's own app. Eneo has, and
  we lack:
  - personal chat and group chat;
  - form apps (`B/apps`);
  - an Agent Skills runtime and an isolated tool runtime (`B/skills`, `tool-runtime/`);
  - web search, image generation, file creation with Word templates, charts and compute (`docs/CAPABILITIES.md`);
  - transcription of audio into knowledge;
  - insights with LLM Q&A over conversations (`B/analysis/analysis_router.py:667`);
  - templates and help assistants.
- **SharePoint and Confluence are built** in Eneo: delta, webhooks, renewal, a 50 % deletion cap. Ours is P2.
- **MCP tools are built** in Eneo (client and proxy). Ours are P2.
- **Public widget:** Eneo has one with `pk_` keys, an origin allow-list, a captcha and a daily token budget. Ours is P2.
- **Swedish UI:** Eneo's two frontends are in Swedish. Ours is English only for now.
- **Stored conversations with retention and audited access:** Eneo has them now. Ours wait for A1.
- **SCIM provisioning and per-tenant OIDC federation:** Eneo has them. We take departments and member groups from the
  gateway and sign in through Keycloak.
- **Level:** rerank, multi-source search, incremental crawling, robots.txt, the SSRF guard, Office formats, upload
  limits and security classifications.
- **Maturity and community:** Eneo is used by several Swedish municipalities and has an association of public bodies
  behind it. Ume.Rag is pre-pilot: v0.5.0, test chatbot T2–T6 open, no production use.

## Part 2. Ten features for the Support Assistant that Eneo does not support

The case is an IT support chatbot for municipal staff (`plans/cases/support-assistant/`). None of these features is
built, and none is a queue item in `plans/work/` or a theme in `plans/roadmap.md`. I checked "per-assistant
test-question sets", "nightly answer judge", "unused-assistant attention items", "public widget", "/mcp", picture
descriptions (U1) and the Git connector (SA-10), and all are excluded because they are already planned. Sizes as in
`06-gaps.md`: S is days, M is a week or two, L is several weeks. Several features add `ume` members. Each needs a chat
design section and a Decisions line from the main session first (`06-gaps.md` preamble).

### F1. Exact identifier search for error codes and product names (M)

- **User value:** pasted tickets carry `0x80070005`, `AADSTS50076`, `KB5034441`, printer models and app versions. The
  document that names that exact code is the best answer, and the Swedish stemmer and embeddings may both blur it.
- **Fit:** in `Ume.Rag.Retrieval`, a third candidate list next to keyword and vector. Identifier-shaped tokens
  (regex: hex codes, letter+digit runs, versions) are extracted from the query and matched on a `simple`-config or
  trigram index over chunk text. The worker adds the index. The list joins `RrfFusion` with its own weight, and
  `CoverageEvaluator` may count an exact hit as coverage. Measure first: how the `swedish` config tokenises such codes
  is unverified.
- **Eneo:** has no keyword search in the answer path (`keyword_search` has no callers; grep `tsvector\|ts_rank\|pg_trgm`
  finds only audit indexes).

### F2. Ticket triage as structured output: `ume.triage` (S–M)

- **User value:** with the answer, the app gets `{category, system, urgency_hint, missing: ["device", "error text"]}`.
  The frontend pre-fills the ticket fields and asks for the missing facts.
- **Fit:** in `Ume.Rag.Answering`, one call on the condense model, run like `FollowUps.cs` (its own budget, second
  `ume` chunk, `[]` on failure, never stored). It uses the same personal-data and no-link filters, with a category list
  set per assistant version. It runs on the assistant's gateway key, so its cost is attributed per step.
- **Eneo:** has no structured answer metadata in the conversation API (grep `triage`: only a help-assistant template;
  `response_format\|json_schema` in `B/conversations`, `B/assistants`: only OpenAPI deprecation flags).

### F3. Escalation hand-off: `ume.escalation` (S)

- **User value:** when the outcome is `not-found` or `answered-general`, the app gets the escalation route (queue
  name, link from the assistant's not-found contacts) and a short hand-off summary to paste into the second-line
  ticket. Nobody retypes the ticket.
- **Fit:** reuses `NotFound` contact links (chat §10.1), `ume.outcome` and the condense model for the summary. The
  output filter's `allowedLinkHosts` keeps the links honest. Ticket-system write-back stays "later" (`07-rollout.md`).
- **Eneo:** lacks it (grep `ticket\|escalat\|handoff\|servicedesk\|topdesk\|jira\|servicenow`: hits only in module
  login and template text, `B/modules/module.py:37`).

### F4. Freshness on every source (S)

- **User value:** each citation and related document shows "updated 2026-03-02, checked yesterday". A stale guide is
  visible before staff follow it.
- **Fit:** the worker already keeps the visible date (`HtmlExtractor` date facts), `Last-Modified`, the content-hash
  version and the last successful fetch. Add `updated_at` and `checked_at` to citation and `related_documents` entries
  (chat §3.8). Retrieval's age decay is already per collection.
- **Eneo:** lacks it (no dates in `frontend/apps/web-next/src/features/chat/activity-sources.tsx`, `answer-links.tsx`;
  grep `updated\|modified\|ago` there).

### F5. Operational notices: `ume.notices` (M)

- **User value:** during an outage ("Teams down since 08:10"), every matching question first gets the notice and the
  status link, before stale self-help steps.
- **Fit:** a `notice` knowledge base kept fresh by a short-interval crawl of the IT status page (the URL-list source
  with a 5-minute schedule) or by admin-entered items with an expiry. A matching notice (topics or keyword hit) is
  returned in `ume.notices` and placed in the prompt as data. Reuses the source types, the scheduler, topics and the
  output filter.
- **Eneo:** lacks it (grep `incident\|outage\|announcement\|banner\|maintenance\|motd`: only a connection banner and
  a crawl notice in `frontend/apps/web-next/src/features/knowledge/notices.tsx`).

### F6. Downvotes traced to documents (M)

- **User value:** the knowledge-base editor sees "this guide was cited in 7 thumbs-down answers this month" and fixes
  the guide, not the prompt.
- **Fit:** `chat."Feedback"` (API and Open WebUI import) already references the message. `MessageSource` already holds
  the cited document ids, even in history mode `none` (`ChatEntities.cs:155-199`). Add a per-document aggregate and an
  attention item on the knowledge-base page. It works without stored content.
- **Eneo:** partial. It counts feedback per assistant (`B/analysis/analysis_router.py:355`) and lists questions with
  their feedback, but has no per-source aggregation (grep `feedback` in `B/analysis` with `blob\|source\|reference`:
  nothing).

### F7. Content-gap report from outcomes (M)

- **User value:** "18 % of questions this week ended `not-found`, mostly near the topic *VPN*". DoIT knows which
  guide to write next.
- **Fit:** outcomes are stored per message (`MessageOutcomes`, `ChatEntities.cs:27-38`). Add the nearest-hit topic and
  collection for low-coverage turns. Query text stays unstored, so the report is counts per topic and day. Example
  questions only come later through the A1 path, or as editor-rewritten questions (Decisions "Evaluation models …").
  It feeds the gold set.
- **Eneo:** has no outcome classification (grep `answer_outcome\|unanswered\|no_answer`: prompt text only) and no gap
  report (grep `content.?gap\|knowledge.?gap`). Insights Q&A over stored conversations (`analysis_router.py:667`) is a
  manual, partial substitute.

### F8. Narrow by topic per request: `ume.topics` (S)

- **User value:** the support app has a "Printers / Teams / Accounts" selector, and the search stays inside that topic.
  This raises precision on short tickets.
- **Fit:** topics are already GIN-indexed tags in the retrieval SQL (`SearchScope.cs:26-30`). A request may only
  narrow the assistant's linked topics, never widen them, the same rule as `ume.general_knowledge`.
- **Eneo:** has no metadata or tag filter at search time (`B/info_blobs/source_metadata.py:1-7`). The TOOL mode's
  `within=` narrows to one source chosen by the model, not by the client (`B/internal_mcp/knowledge.py:706-709`).

### F9. Owner digest for knowledge at risk (S–M)

- **User value:** the knowledge base's contact person gets a weekly mail listing three things: heavily cited documents
  that went `Gone`, failed to parse, or haven't changed in 12 months; deletions the guard is holding; and the F6
  downvote counts.
- **Fit:** run reports, `DeletionProposals`, document states and the contact per knowledge base already exist
  (Decisions "Organisation", connectors §13). A worker sweep and a mail or webhook sender (new) are needed. The Overview
  attention items in admin design are a P2 theme. This feature is the outbound digest, not the page.
- **Eneo:** lacks it (grep `stale`: only failed SharePoint subscriptions, `sharepoint_subscription_worker.py:6`; no
  owner notification).

### F10. Cost per outcome (S)

- **User value:** "the support assistant costs $0.004 per answered ticket and $0.001 per not-found". This gives a
  basis for chargeback and for choosing between models on the gold set.
- **Fit:** `AssistantDailyUsage` already splits cost per step from the gateway's `x-ume-cost-usd`, and outcomes are
  stored per message. Join them on the run (`x-ume-run-id`) into a per-outcome column on the Costs page.
- **Eneo:** has no per-space or per-assistant cost (`B/token_usage` routers: tenant and user only). Cost is estimated
  in the browser (`usage-page.tsx:183`).

## Not verified

- Whether Eneo can share one knowledge base across spaces without copying it: not searched.
- Whether Eneo's `workflows` module can express branching flows: not read.
- How Postgres' `swedish` configuration tokenises error codes such as `0x80070005` (F1's premise): not measured.
- Ume.Rag's chat latency under load on the test host (R1): not yet run. The Eneo comparison figures are the gateway
  benchmark's, for an Eneo assistant **without** knowledge.
- Eneo's adopter list and its community size: not checked.
