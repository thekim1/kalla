# Open WebUI: platform analysis

Analysed: `open-webui/open-webui` shallow clone at `~/git/rag-platforms/open-webui`, commit `f7ac7ee` (2026-10-29),
`package.json` version **0.12.5**. Paths below are relative to the repo root; `ow/` = `backend/open_webui/`.
Evidence labels: **[V]** verified in code/changelog/issue tracker, **[I]** inferred.

## 1. Summary

- **What it is**: a self-hosted chat front end that has grown into a full AI workspace: chat, model "workspace"
  (custom models = base model + prompt + knowledge + tools), knowledge bases (RAG), web search, code interpreter,
  image generation, audio, notes, channels, calendar, automations, terminals, MCP/OpenAPI tool servers, plugins.
  `ow/routers/` has 33 routers; `ow/utils/middleware.py` alone is 6,552 lines, `ow/main.py` 3,203, `ow/config.py` 3,372.
- **Stack**: Python 3.12 / FastAPI / SQLAlchemy (SQLite default, Postgres supported) / python-socketio, SvelteKit
  front end, LangChain pieces for loaders/splitters/retrievers, sentence-transformers in-process (unless "slim"
  image). 66 Alembic migrations.
- **Maturity**: very popular and fast-moving. Releases roughly weekly (0.12.0 → 0.12.5 in 19 days, `CHANGELOG.md:8-384`);
  the CHANGELOG is 6,600 lines. Frequent schema migrations; many regressions are fixed in follow-up patch releases.
- **Licence** (`LICENSE`, "Open WebUI License", copyright Open WebUI Inc.): BSD-3-Clause text plus a fourth clause.
  Exact text of the branding clause:

  > 4. Notwithstanding any other provision of this License, and as a material condition of the rights granted
  > herein, licensees are strictly prohibited from altering, removing, obscuring, or replacing any "Open WebUI"
  > branding, including but not limited to the name, logo, or any visual, textual, or symbolic identifiers that
  > distinguish the software and its interfaces, in any deployment or distribution, except in the following
  > circumstances: (i) deployments or distributions where the total number of end users (defined as individual
  > natural persons with direct access to the application) does not exceed fifty (50) within any rolling thirty
  > (30) day period; (ii) the licensee has obtained specific prior written permission from the copyright holder;
  > or (iii) where the licensee has obtained a duly executed enterprise license expressly permitting such
  > modification. For all other cases, any removal or alteration of the "Open WebUI" branding shall constitute a
  > material breach of license.

  Precisely: **use, modification and redistribution are free at any scale**; what is restricted is changing the
  branding. Rebranding is allowed only if ≤50 distinct natural persons have direct access in any rolling 30-day
  window, or with written permission, or with an enterprise licence. Contributions also fall under a CLA
  (`CONTRIBUTOR_LICENSE_AGREEMENT`). `LICENSE_NOTICE` splits history: MIT before commit `a76068d`, BSD-3 up to
  `60d84a3`, Open WebUI License after. Enforcement is visible in code: setting `WEBUI_NAME` appends
  " (Open WebUI)" (`ow/env.py:995-997`), and 46 backend + 72 frontend files carry "LICENSE covers this … branding"
  comments (e.g. `ow/utils/headers.py:181-187`). It is **not OSI open source** any more.

## 2. Architecture & deployment footprint

- **One process does everything** [V]: the same FastAPI app serves the SPA, REST API, Socket.IO, chat orchestration
  (middleware pipeline: filters → tools → file/knowledge retrieval → upstream call → stream post-processing),
  document parsing, chunking, **embedding** (local sentence-transformers or remote), reranking, web fetching and
  plugin execution (`exec()` of DB-stored Python, `ow/utils/plugin.py:242,291`). There is no worker tier; long
  ingestion runs in threads of the web process (`ow/routers/retrieval.py:1381` + `asyncio.run_coroutine_threadsafe`
  back onto the main loop).
- **Images**: default image bakes in torch + `all-MiniLM-L6-v2` + a reranker/auxiliary model (`Dockerfile:14-16`);
  `USE_SLIM=true` drops local models and defaults the vector DB to pgvector (`ow/config.py:509`). [I] full image
  is several GB.
- **State**: SQL DB (`DATABASE_URL`, default SQLite in `DATA_DIR`, `ow/env.py:271`), vector DB (`VECTOR_DB`, default
  **embedded Chroma on local disk**, `ow/config.py:509`), file storage (`STORAGE_PROVIDER` local/S3/GCS/Azure,
  `ow/config.py:156-174`), Redis optional (`REDIS_URL`, sentinel/cluster supported, `ow/env.py:381-403`).
- **Config is stored in the DB** [V]: `ENABLE_PERSISTENT_CONFIG=True` by default (`ow/config.py:3365`); env vars seed
  config on first start and the admin UI then owns it. GitOps-style config needs `ENABLE_PERSISTENT_CONFIG=false`.
- **Multi-replica**: possible but strict [V]: Postgres for `DATABASE_URL`, a server vector DB (not Chroma), shared
  object storage, `REDIS_URL` + `WEBSOCKET_MANAGER=redis` for Socket.IO fan-out, identical `WEBUI_SECRET_KEY`
  everywhere, sticky sessions recommended. Every recent minor release states "rolling updates are not supported and
  will cause application failures due to schema incompatibility" (`CHANGELOG.md:380,728,1361,1651,1698,2051,2193,2467`).
- **Streaming path**: the browser posts a chat; the backend calls the upstream model, parses the SSE stream itself,
  writes the message to the DB and pushes deltas to the browser over Socket.IO (`ow/socket/main.py:1600-1770`). So
  Open WebUI is a stateful proxy in the middle of every token, not a thin client.

## 3. Ingestion & connectors

- **Sources**: uploads (chat or knowledge base), URLs/web pages, web-search results, YouTube transcripts, cloud
  pickers (Google Drive, OneDrive/SharePoint picker in the browser), and a **directory sync API** (`SyncDiffForm`,
  `ow/routers/knowledge.py:1846`) where a client pushes a file manifest. No crawler with schedules, no Graph delta
  sync, no sitemap crawling [V by absence in `ow/retrieval/web/utils.py`; web loaders fetch given URLs only].
- **"External knowledge"** (new): a knowledge base can point at an *existing* Qdrant/Milvus/pgvector collection with a
  field mapping (`ow/retrieval/external.py:33-68,293-360`); Open WebUI embeds the query itself and queries the DB
  directly. It does not call an HTTP retrieval API.
- **Parsers** (`ow/retrieval/loaders/main.py:556-838`): one global `CONTENT_EXTRACTION_ENGINE`: default
  (pypdf/unstructured/docx/pptx loaders), Tika, **Docling (docling-serve `/v1/convert/file`)**, Azure Document
  Intelligence, Mistral OCR, MinerU, Datalab Marker, PaddleOCR-VL, or a generic `external` HTTP loader. Docling output
  is split per page using a form-feed marker and page numbers from Docling JSON so citations can say "page N"
  (`loaders/main.py:256-340`) [V]. That call is a synchronous `requests.post` without a timeout.
- **Web loader engines** (`ow/retrieval/web/utils.py:1172-1275`): requests-based `SafeWebBaseLoader`, Playwright,
  Firecrawl, Tavily, Exa, external. Good SSRF protection (IP allow/deny with DNS pinning, `web/utils.py:62-300`).
- **Chunking** (`ow/routers/retrieval.py:1416-1480`): optional Markdown-header pre-split, then recursive character
  (default 1000 chars / 100 overlap, `ow/config.py:1081-1085`), tiktoken, or HF-tokenizer splitter, optional merging
  of small chunks. **One global setting for the whole instance.**
- **Change detection**: SHA-256 of extracted text per file, duplicate content rejected per collection
  (`retrieval.py:1360-1415`). Embedding model + engine stored on each chunk's metadata.

## 4. Pipeline / workflow builder (Pipelines/Functions/Tools)

No visual builder. Extensibility is code-first:

- **Functions** (in-process Python stored in the DB, executed with `exec`): *Pipe* (acts as a model), *Filter*
  (inlet/stream/outlet hooks on every request), *Action* (buttons on messages) (`ow/utils/plugin.py:298-302`).
  Optional `pip install` of frontmatter requirements at runtime (`plugin.py:434`).
- **Tools**: Python tools (same mechanism), OpenAPI tool servers, MCP servers; native function calling.
- **Pipelines**: a separate OpenAI-compatible server (`open-webui/pipelines`) that Open WebUI calls like any
  model connection (`ow/routers/pipelines.py`).
- **Skills, prompts, automations** also live in the DB.
- Assessment: very flexible, but it is admin-supplied arbitrary code running inside the web process with DB access;
  0.9.0 broke plugins with an async migration (`CHANGELOG.md:2468`). Nothing here is a typed, testable ingestion
  graph.

## 5. Retrieval & answering

- **Vector DB abstraction** (`ow/retrieval/vector/main.py`, `vector/factory.py`): 15 backends (Chroma, pgvector,
  Qdrant (+multitenancy), Milvus (+multitenancy), Elasticsearch, OpenSearch, Weaviate, Pinecone, S3 Vectors, Oracle,
  MariaDB, openGauss, Valkey). Interface: `insert/upsert/search/query/get/delete/reset` plus optional `hybrid_search`.
  All clients are synchronous; `vector/async_client.py` wraps them in `asyncio.to_thread`.
- **Collection model**: one vector-DB collection per file and per knowledge base (chunks are written to both) [I from
  `retrieval.py` call sites]. pgvector uses **one shared `document_chunk` table** with a `collection_name` column and a
  single fixed dimension (`PGVECTOR_INITIALIZE_MAX_VECTOR_LENGTH`, default 1536, `ow/config.py:660`).
- **Hybrid search**, two paths (`ow/retrieval/utils.py:412-606`):
  - *Native* (only pgvector and Milvus implement `hybrid_search`): pgvector runs `plainto_tsquery('simple', q)` with
    `ts_rank_cd` over a GIN index on `to_tsvector('simple', text)` and fuses with vector results by weighted RRF
    (k=60, `ow/retrieval/vector/utils.py:91-130`, `pgvector.py:277-288,600-630`).
  - *Legacy* (every other backend, or when "enriched texts" is on): **fetches the entire collection** with `get()`,
    builds an in-memory `BM25Okapi` over `text.split()` per query (`utils.py:516-541`), then LangChain
    `EnsembleRetriever`.
- **Rerank** (`utils.py:1823-1896`): local CrossEncoder, ColBERT, or external reranker API. **Without a reranker the
  "compressor" re-embeds every candidate chunk at query time** to compute cosine scores.
- **Defaults**: `RAG_TOP_K=3`, `RAG_TOP_K_RERANKER=3`, BM25 weight 0.5, hybrid off, threshold 0 (`config.py:985-990`),
  embedding `all-MiniLM-L6-v2` (English-only, `config.py:1025`).
- **Answering**: optional LLM query rewriting (`ENABLE_RETRIEVAL_QUERY_GENERATION`, on by default,
  `middleware.py:1255`), then sources are injected with `RAG_TEMPLATE` as `<source id="n">` blocks and the model is
  told to cite `[n]` (`config.py:1087-1111`). "Full context" mode and `BYPASS_EMBEDDING_AND_RETRIEVAL` inject whole
  documents instead.
- **Citation UI**: sources are attached to the message and rendered as chips plus a modal with snippet, relevance,
  page and link (`src/lib/components/chat/Messages/Citations.svelte`, `Citations/CitationModal.svelte:70-120`);
  inline `[1]`, `[1,2]` markers become hover chips (`src/lib/utils/marked/citation-extension.ts:19`).

## 6. Admin, multi-tenancy & permissions

- **Roles**: `admin`, `user`, `pending`. No scoped/department admin (feature request #23756, open). [V]
- **Groups** with permission toggles (workspace access, file upload, web search, code interpreter, sharing…,
  `ow/config.py:1777-1915`) and **access grants** per resource (model, knowledge, tool, prompt) to users/groups.
- **Model access** [V] (`ow/utils/models.py:568-620`): a model from a connection with no DB entry is **visible only to
  admins** ("No DB entry means no access control configured yet"); an admin must register it and grant groups read
  access. `BYPASS_MODEL_ACCESS_CONTROL=true` makes every model visible to everyone.
- **OIDC**: generic OIDC (`OPENID_PROVIDER_URL`, `OAUTH_CLIENT_ID/SECRET`), Microsoft (`MICROSOFT_CLIENT_ID/_SECRET/
  _TENANT_ID`), Google, GitHub, Feishu; LDAP; trusted-header auth (`WEBUI_AUTH_TRUSTED_EMAIL_HEADER`); SCIM 2.0
  provisioning (`ENABLE_SCIM`). Group sync from the `groups` claim (`ENABLE_OAUTH_GROUP_MANAGEMENT`,
  `ENABLE_OAUTH_GROUP_CREATION`, `OAUTH_GROUPS_CLAIM`), role mapping (`OAUTH_ROLES_CLAIM`, `OAUTH_ALLOWED_ROLES`,
  `OAUTH_ADMIN_ROLES`) (`ow/config.py:2586-2704`). **Entra group overage (>200 groups) is not handled**: no
  `_claim_names`/Graph lookup in `ow/utils/oauth.py`; issue #17510 open. Disable the local form with
  `ENABLE_LOGIN_FORM=false`.
- **Multi-tenancy**: none. One instance = one tenant; separation is by groups and grants only.

## 7. Strengths worth copying

1. **Identity forwarding to backends** (`ow/utils/headers.py:56-224`): plain headers, or one **signed HS256 JWT**
   (`sub,email,name,role,iss=open-webui,exp`) per request, plus per-connection header templates with
   `{{USER_EMAIL}}`, `{{USER_GROUPS}}`, `{{CHAT_ID}}`, `{{MESSAGE_ID}}` placeholders, plus `auth_type=system_oauth`
   that forwards the user's own OIDC access token. A clean, generic pattern for our clients too.
2. **Citation contract that degrades gracefully**: OpenAI `url_citation` annotations work with zero coupling; a
   richer side channel adds snippets/pages/scores. Copy the idea: one canonical citation object, several encodings.
3. **Docling page-split with page numbers** for "page N" citations, and docling-serve over HTTP as a swappable sidecar.
4. **SSRF-safe fetcher** with DNS pinning (`ow/retrieval/web/utils.py:62-300`), important for user-supplied URLs.
5. **Native hybrid search in Postgres with weighted RRF** and a GIN FTS index; same shape as our plan.
6. **Chunk-level provenance**: embedding engine/model and content hash stored per chunk, enabling targeted re-index.
7. **Per-model capability flags** (`citations`, `file_context`, …) that switch features per model in the UI.

## 8. Flaws & pain points

**Verified**

1. **Kitchen-sink monolith in the request path.** Parsing, embedding, BM25, reranking, web fetch and plugin code run
   in the chat web process. Users report the whole app freezing while embeddings are saved (#19421, 51 comments) and
   an external reranker blocking the event loop (#19900). Code now offloads to threads, which shows the design debt.
2. **Retrieval does not scale with corpus size.** Legacy hybrid search loads the whole collection and rebuilds BM25
   per query (`retrieval/utils.py:516-541`); "70K documents in multiple collections causes severe slowdown" (#17998,
   48 comments); "unable to use any version newer than 0.6.25 due to hybrid search performance" (#20327); "RAG
   hybrid search still broken" (#15915, 40 comments). Without a reranker every candidate is re-embedded per query.
3. **Weak lexical search for Swedish.** Native pgvector FTS uses the `simple` config and `plainto_tsquery`
   (all terms ANDed, no stemming, no compound splitting); legacy BM25 tokenises with `str.split()`. Default embedding
   is English-only MiniLM; default top-k 3.
4. **Global, not per-collection, RAG config.** One parser engine, one splitter, one chunk size, one embedding model
   per instance; changing the embedding model means re-indexing everything. pgvector has one fixed dimension.
5. **Heading metadata lost in chunking.** The Markdown-header splitter's per-chunk header metadata is discarded
   (`metadata={**doc.metadata}`, `routers/retrieval.py:1438`), so no heading path on chunks or citations.
6. **Upgrades are risky.** Schema migrations in nearly every minor release, no rolling updates, no downgrade
   (`CHANGELOG.md:380,1913`); issues like "docker endlessly restarts after 0.10.2→0.11.0" (#27645, 51 comments),
   "loading and login issue after 0.9.0/0.9.1" (#23939), RAG behaviour changes ("models do not use associated
   knowledge as before 0.7.0", #21164), plugin API break in 0.9.0.
7. **Scaling needs care.** Default Chroma/SQLite are single-node; multi-worker Socket.IO routing problems (#15162),
   Redis cluster/connection issues (#16157, #26425), the long-running "Network Problem 0.5+" websocket thread (#8074,
   100 comments).
8. **Licence is no longer open source**; branding cannot be removed above 50 users/30 days without a paid or written
   exception, and `WEBUI_NAME` is forcibly suffixed.
9. **Entra group overage unsupported** (#17510 open); no scoped admins (#23756).
10. **Plugins are arbitrary in-process Python** with optional runtime `pip install`: a security and upgrade liability.

**Inferred**

- Config living in the DB plus UI-edited state (models, grants, prompts) makes environments hard to reproduce and
  review; drift between replicas/environments is likely without `ENABLE_PERSISTENT_CONFIG=false`.
- Chunks written to both a per-file and a per-knowledge collection doubles storage and embedding work.
- No ingestion observability (per-document status beyond "processing/failed"), no scheduled re-sync, no deletion
  propagation from source systems.

## 9. Fit as a front end for the municipality Chat API

**Verdict**: workable and the best-known staff chat UI, provided our Chat API owns all RAG and Open WebUI is
configured as a thin client. Expect it to remain a stateful, frequently upgraded, branded proxy.

**Connection & model listing**

- `ENABLE_OPENAI_API=true`, `OPENAI_API_BASE_URLS=https://chat-api.umea.se/v1`, `OPENAI_API_KEYS=<RAG key>`;
  `ENABLE_OLLAMA_API=false`. Per-connection JSON in `OPENAI_API_CONFIGS` (keyed by index):
  `{"0":{"auth_type":"bearer|system_oauth|none","prefix_id":"umea","model_ids":[],"tags":[],"headers":{…}}}`
  (`ow/routers/openai.py:610-670`, `ow/utils/headers.py:171-224`).
- Our `GET /v1/models` must return OpenAI-style `{"data":[{"id","name"?,"owned_by"?}]}`; it is called with the
  forwarded user headers, so we can return a **per-user list** (but Open WebUI caches lists: `MODELS_CACHE_TTL`,
  `ENABLE_BASE_MODELS_CACHE`).
- Access: either (a) admins register each assistant in Open WebUI and grant groups (double administration), or
  (b) `BYPASS_MODEL_ACCESS_CONTROL=true` and **our API filters** `/v1/models` and rejects unauthorised
  `/v1/chat/completions` per forwarded identity. (b) keeps one source of truth; recommended.
- Set `ENABLE_PERSISTENT_CONFIG=false` so env vars stay authoritative.

**Identity to our API**

- `ENABLE_FORWARD_USER_INFO_HEADERS=true` sends `X-OpenWebUI-User-Name/-Id/-Email/-Role` and `X-OpenWebUI-Chat-Id`
  (`ow/env.py:1035-1043`) on model listing and chat calls. Plain headers are spoofable by anyone holding the RAG key,
  so also set **`FORWARD_USER_INFO_HEADER_JWT_SECRET`**: Open WebUI then sends one HS256 JWT in `X-OpenWebUI-User-Jwt`
  (`sub,email,name,role,iss=open-webui,iat,exp`, 300 s default, `ow/utils/headers.py:42-86`). Our API verifies it
  with the shared secret and maps `email` → municipal user → department/team.
- Groups are **not** in that JWT; for group-based assistant access add a connection header template, e.g.
  `"X-Umea-Groups":"{{USER_GROUPS}}"` (group names, `headers.py:110-168`), only trustworthy together with the
  service key, or resolve groups ourselves from Entra/our DB by email (preferred).
- Alternative: `auth_type: "system_oauth"` forwards the user's Entra access token as `Authorization: Bearer`
  (`headers.py:211-214`, `ow/utils/oauth.py:131-160`). [I] The token's audience must be our API, which requires
  Open WebUI's `OAUTH_SCOPES` to request our API scope; workable but couples their login config to our API and
  loses the service key. Prefer service key + signed JWT.

**Streaming & citations: what our API must return**

- Must support `stream: true` SSE `chat.completion.chunk` (Open WebUI streams by default).
- **Minimal (portable)**: put OpenAI `url_citation` annotations in a delta:
  `{"choices":[{"delta":{"annotations":[{"type":"url_citation","url_citation":{"url":"…","title":"…"}}]}}]}`
  (`ow/utils/middleware.py:5001-5032`). Rendered as a source chip with title + link; no snippet, no page.
- **Rich (Open WebUI-specific)**: any SSE chunk with a top-level `event` is forwarded to the UI and persisted
  (`middleware.py:4838`, `ow/socket/main.py:1749-1765`):
  ```json
  {"event":{"type":"source","data":{
    "source":{"id":"<doc id>","name":"<title>","url":"<link>"},
    "document":["<chunk text>"],
    "metadata":[{"source":"<doc url or id>","name":"<title>","page":3}],
    "distances":[0.82]}}}
  ```
  `document[i]`, `metadata[i]`, `distances[i]` align. `page` is 0-based (UI shows `page+1`). An `http(s)` value in
  `metadata.source` becomes the link. Send these chunks **before** the answer text.
- **Inline markers**: the answer should contain `[1]`, `[2]`…; `[n]` maps to the n-th **distinct `metadata.source`
  in emission order** (`src/.../ContentRenderer.svelte:107-131`), so our numbering must use the same dedup key and
  order. Mark the model's `citations` capability on (default) or markers are stripped.
- [I] Non-streaming responses do not pass `event`/annotations through the same path; treat streaming as required.

**Switching Open WebUI's own features off**

- RAG/files: `USER_PERMISSIONS_CHAT_FILE_UPLOAD=false`, workspace knowledge off
  (`USER_PERMISSIONS_WORKSPACE_KNOWLEDGE_ACCESS=false`), per-model capability `file_context=false`; web:
  `ENABLE_WEB_SEARCH=false`, `USER_PERMISSIONS_CHAT_WEB_UPLOAD=false`; code: `ENABLE_CODE_INTERPRETER=false`,
  `ENABLE_CODE_EXECUTION=false`; tools/functions: workspace tools/models/skills access off; `ENABLE_MEMORIES=false`;
  `ENABLE_IMAGE_GENERATION=false`; `ENABLE_DIRECT_CONNECTIONS=false`; use a slim build (`USE_SLIM=true` build arg, runtime flag `USE_SLIM_DOCKER`, `ow/env.py:45`) + `VECTOR_DB=pgvector`.
- **Background tasks** (title, tags, follow-ups, query generation) call a model; by default the *current* model,
  i.e. our assistant, producing extra un-attributed requests to our Chat API. Set
  `TASK_MODEL_EXTERNAL` to a cheap gateway model or turn off `ENABLE_TITLE_GENERATION`, `ENABLE_TAGS_GENERATION`,
  `ENABLE_FOLLOW_UP_GENERATION`, `ENABLE_RETRIEVAL_QUERY_GENERATION`, `ENABLE_SEARCH_QUERY_GENERATION`
  (`ow/config.py:2248-2414`). Our API should detect them (`{{TASK}}` header template) and skip retrieval.

**File upload passthrough**: effectively **none** [V]. Uploaded files are stored and parsed by Open WebUI and only
text/images reach the model; there is no OpenAI `file`/`input_file` passthrough (`middleware.py:1448-1486,
2963-2968`). If staff must "chat with this document" via our platform, it needs a separate path (our upload API, or a
Tool that posts the file to us), or accept Open WebUI's own RAG for ad-hoc uploads (which conflicts with "own RAG
off" and with our retention rules).

**Risks**

- Branding: above 50 users we must keep "Open WebUI" branding (fine for an internal tool if accepted) or buy an
  enterprise licence.
- Chat history is stored in **Open WebUI's DB as well as ours**: two retention regimes for personal data
  (offentlighetsprincipen/GDPR), and its own feedback store.
- Fast release cadence with breaking migrations and no rolling updates: plan pinned versions, staging, backups.
- Feature creep: every release adds features that may bypass our controls (new tools, connectors) unless
  permissions are re-checked after each upgrade.
- Entra groups overage: use app roles or "groups assigned to the application" claims.

## 10. Implications for the municipality design

1. **Keep Open WebUI strictly as a client** and design the Chat API to serve it well: OpenAI SSE, `url_citation`
   annotations as the portable format, plus an optional `event: source` frame (behind a request header or
   per-key flag) for Open WebUI snippets/pages. Define one internal citation DTO and serialise it both ways.
2. **Own authorisation in our API**: filter `/v1/models` and enforce access per forwarded identity
   (`X-OpenWebUI-User-Jwt` verified with a shared secret). Use `BYPASS_MODEL_ACCESS_CONTROL` in Open WebUI so access is
   not administered twice. Document a generic "trusted client + signed user assertion" contract for intranet/Teams.
3. **Treat task calls as cheap**: recognise title/tag/follow-up requests and skip retrieval and history storage, or
   require `TASK_MODEL_EXTERNAL` pointing at the gateway.
4. **Avoid Open WebUI's RAG mistakes**: ingestion in a separate worker (never in the web process), hybrid search as
   one indexed SQL query (never load a collection into memory), a **stored `tsvector` with the `swedish` config and
   `websearch_to_tsquery`/OR semantics** rather than `simple` + `plainto`, rerank via the gateway and never re-embed
   stored chunks at query time, **per-collection** parser/chunking/embedding settings, heading path kept on every
   chunk, page numbers from Docling JSON kept for citations.
5. **Ad-hoc document chat** needs an explicit decision: an upload endpoint/Tool into our platform (with retention),
   or knowingly allow Open WebUI's own file RAG for personal uploads.
6. **Ops lessons**: config in files/env (not DB-only), expand-and-contract migrations so rolling updates work,
   typed nodes instead of `exec()` plugins, and a chat-history retention story that covers every copy (ours and the
   front end's).
