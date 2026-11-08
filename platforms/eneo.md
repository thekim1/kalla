# Eneo (eneo-ai/eneo, formerly intric): platform analysis

> Background research from 2026-10-29, kept for its evidence. It does not bind: the Decisions table and the design
> documents decide. Facts are as of the commits named below and are not kept up to date.

Analysed 2026-10-29. Local clone `~/git/eneo` is detached at **v2.1.1** (Aug 2026); upstream is at v2.3.0-rc.5
(2026-10-28). Code references below are to v2.1.1 under `backend/src/intric/` unless stated. Upstream changes after
v2.1.1 that matter (reranking, "web-next" frontend, Skills runtime) are taken from GitHub issues and marked as such.
Measured numbers come from the Umeå LLM gateway benchmark against eneo **v2.2.3** (`~/git/llmgateway/docs/performance.md`,
"Comparison with eneo").

## 1. Summary

- **What**: an all-in-one generative-AI workspace for Swedish public sector: personal chat, shared "spaces" with
  assistants, group chats, form-style "apps", knowledge (collections, websites, SharePoint/OneDrive, Confluence),
  transcription, MCP tools, web search, insights/analytics, audit, admin, SCIM, sysadmin.
- **Who**: built by Sundsvall and Ånge kommun (README.md:24,169), governed by a user association of public bodies
  (README.md:154); forum and chat restricted to government/municipality e-mail. Used by a number of Swedish
  municipalities (adopter list not verified here).
- **Stack**: Python 3.11, FastAPI + gunicorn/uvicorn, SQLAlchemy async, `dependency-injector`, ARQ (Redis) worker,
  Scrapy (via crochet/Twisted) crawler, litellm for all model calls, PostgreSQL + pgvector, Redis, SvelteKit frontend
  (bun monorepo, `frontend/apps/web` + docs site).
- **Licence**: AGPL-3.0 (LICENSE). Relevant if Umeå ever forks or embeds code: AGPL network clause applies.
- **Size**: backend **132.7k LOC Python in 754 files across 63 top-level modules**; frontend ~123k LOC (.svelte/.ts);
  **354 Alembic migrations**; 53 table files; `main/config.py` is 937 lines with ~138 settings.
- **Maturity**: active and fast-moving (v2.3.0 RCs daily in Oct 2026, issues past #1250), heavy recent investment in
  security hardening, audit and tests. Product scope keeps growing (Skills with an isolated script runtime #660,
  widgets #849, a second frontend "web-next").

## 2. Architecture & deployment footprint

Deployables (`docs/deployment/docker-compose.yml`): Traefik, `eneo-frontend` (SvelteKit/Node), `eneo-backend`
(FastAPI), **the same `eneo-backend` image again as worker** (`RUN_AS_WORKER=true`, `backend/run.sh`), Postgres 16 +
pgvector, Redis, and a one-shot `init_db.py` container. API containers run `alembic upgrade head` on start
(`backend/run.sh`).

So despite `docs/ARCHITECTURE.md` calling it "a microservices architecture", it is **one modular monolith in two
processes**: API and worker share the whole codebase, the DI container and the database models.

**Module sprawl (LOC per top-level module, Python):**

| Area | Modules (LOC) |
|---|---|
| Integrations | integration 13.6k (SharePoint alone: content service 1.7k, admin router 1.2k, webhook 635, subscriptions 570) |
| Auth & identity | authentication 9.2k, users 4.2k, scim 2.8k, roles 0.7k, user_groups 0.5k, groups_legacy 1.1k |
| Worker & jobs | worker 8.4k (crawl_tasks.py alone 1.7k, feeder/watchdog 1k), jobs 0.6k |
| Workspace core | spaces 6.7k, assistants 5.3k, sessions 1.2k, conversations 1.3k, questions 0.5k, group_chat 1.2k |
| Models | completion_models 6.0k, ai_models 2.5k, model_providers 1.7k, transcription_models 1.6k, embedding_models 1.6k, tenant_models 0.8k |
| Governance | audit 5.9k, security_classifications 1.1k, data_retention 0.6k, token_usage 1.3k, analysis (insights) 3.0k |
| Other features | mcp_servers 4.3k, templates 4.1k, apps 2.8k, help_assistants 2.7k, services 1.2k, admin 2.7k, sysadmin 1.7k |
| Knowledge | websites 2.8k, crawler 1.0k, files 1.7k, info_blobs 1.5k, collections 0.3k |

**Coupling (computed from `from intric.X import` statements):** 63 modules, **650 inter-module import edges**, **154
module pairs that import each other**; 61 mutual pairs even after excluding the hubs `main`, `server`, `database`.
Examples: `embedding_models ⇄ worker/websites/integration/info_blobs`, `completion_models ⇄ sessions/questions/worker`,
`apps ⇄ worker/spaces`, `audit ⇄ authentication/worker`. `main/container/container.py` is a 1 544-line DI container
with ~213 providers wiring everything together; `assistants/assistant_service.py` imports `workflows.step_repo`.
Knowledge retrieval is not a separable component: it lives in `embedding_models/infrastructure/datastore.py` and is
called from assistants, services, group chat and a legacy group router.

The single ARQ worker runs **everything asynchronous**: crawls (Scrapy inside a Twisted reactor thread via crochet,
`worker/worker.py:272`, `crawler/crawler.py`), file uploads, SharePoint/Confluence pulls and delta sync,
transcription, app runs, insights analysis, audit-log writes (`LOG_AUDIT_EVENT`) and exports, usage stats, plus 12
cron jobs (`jobs/job_models.py:11`, `worker/routes.py`).

## 3. Ingestion & connectors

**Crawler** (`crawler/`, `worker/crawl_tasks.py`, `worker/crawl/*`)
- Scrapy `CrawlSpider` (`crawler/spiders/crawl_spider.py`) restricted to the start URL's domain and prefix
  (`LinkExtractor(allow=url)`), plus `SitemapSpider`. Optional HTTP basic auth (password Fernet-encrypted; username
  still plaintext, #870). Tenant-tunable Scrapy settings: robots.txt, autothrottle, timeouts, item cap
  (`crawler/crawler.py:217-233`).
- Run in-process in the worker via crochet; much of the 8.4k-LOC worker is lifecycle plumbing around that: crawl
  manager stop/timeout handling (`crawler/crawler.py:45-75`, comments cite "GPT-5.2 and Gemini consultation"), a Redis
  "feeder" with leader election, per-tenant concurrency slots in Lua, heartbeats, a watchdog and recovery
  (`worker/feeder/*`, `worker/crawl/recovery.py`).
- **Parsing**: `BeautifulSoup` + `html2text` on the **whole page** (`crawler/parse_html.py:37-45`): no main-content
  extraction, so menus, footers and cookie banners become chunks. Linked files of supported types are downloaded.
- **Scheduling**: per-website `UpdateInterval` (never/daily/every other day/weekly, `websites/domain/website.py:25`),
  hourly cron that queues due sites (`worker/routes.py:94`).
- **Change detection: effectively none for pages.** Every run is a full re-crawl; each page's SHA-256 is computed but
  only stored ("future deduplication", `worker/crawl_context.py:160`); persistence deletes and re-inserts every page and
  **re-embeds all chunks** (`worker/crawl/persistence.py:89-110,235`). Only downloaded *files* skip re-processing on hash
  match (`worker/crawl_tasks.py:913-923,1143`). No ETag/Last-Modified. Pages not seen in a run are deleted
  (`worker/crawl_tasks.py:1206-1232`), except failed ones.

**SharePoint / OneDrive** (`integration/`, the largest module)
- Three auth modes: per-user delegated OAuth (user's own integration), a **service account** (delegated, scopes
  `Files.Read.All Sites.Read.All User.Read`, `auth_service/service_account_auth_service.py:61`) and a **tenant app**
  with client credentials (`auth_service/tenant_app_auth_service.py`).
- Change tracking: Graph **delta** per drive (`clients/sharepoint_content_client.py:505-540`, handles 410 Gone) plus
  Graph **change-notification subscriptions** per site/drive, renewed by a 12-hourly cron
  (`sharepoint_subscription_worker.py:125`); webhooks hit the main API (`presentation/sharepoint_webhook_router.py`) and
  enqueue `SYNC_SHAREPOINT_DELTA` jobs. `clientState` is only checked if configured
  (`sharepoint_webhook_service.py:84`).
- **No permission trimming**: SharePoint ACLs are not read or stored; once imported, content is visible to everyone
  with access to the space (no `permissions`/ACL handling in `sharepoint_content_service.py`).
- History of operational pain (all closed): subscriptions dying silently with no monitoring (#521), orphaned blobs when
  files move out of scope (#522), no Graph 429 handling (#427), scope-path comparison bugs (#429), "make sync health
  visible and harden sync" (#1081). Upstream later added library properties/metadata filters (#1041/#1042).
- Confluence is also supported (`content_service/confluence_content_service.py`).

**Files**: `files/text.py` uses pdfplumber, docx2python, python-pptx, pandas/calamine for Excel; **no OCR** anywhere
(no tesseract/OCR in the codebase), no layout/table structure. Audio goes to transcription models.

## 4. Pipeline / workflow builder

**None.** Ingestion is hard-coded: fetch → html2text/pdfplumber → `RecursiveCharacterTextSplitter` (200 tokens, 40
overlap, global setting, `embedding_models/infrastructure/datastore.py:33-35`) → embed → insert.
- `workflows/` (294 LOC: `Step`, `Workflow`, filters, repos) has **no router**; it is vestigial and only referenced by
  the DI container and `assistant_service.py:61`.
- `apps/` are **form apps**: input fields (text/file/audio) + one prompt + one model → a run (`apps/apps/app.py:72-87`),
  executed as a worker job (`RUN_APP`). Useful for "summarise this protocol"-type tasks, but not a graph builder.
- `templates/` ships assistant and app templates; `help_assistants/` ships built-in helper assistants.
- Per-source chunk settings are still an open item upstream (#654, RAG overhaul epic #537).

## 5. Retrieval & answering

**Storage**: one table `info_blob_chunks(text, chunk_no, size, embedding vector, info_blob_id, tenant_id)`
(`database/tables/info_blob_chunk_table.py`). The vector column is **dimensionless** (`Vector()`) and the migrations
create **no HNSW/IVFFlat index** on it (only a `groups.index_type='hnsw'` label, alembic `81d218d44896`). Search is an
exact scan filtered by source ids.

**Search** (`info_blobs/info_blob_chunk_repo.py:67-122`): pure cosine distance, top 30, with
`SET LOCAL enable_seqscan = off` as a planner workaround (pgvector#662) commented "the first place we look at if
something is slow". A `keyword_search` (`text.match`) exists but is not used in the answer path. **No hybrid search,
no Swedish FTS, no reranking** in v2.1.1; upstream added an optional rerank step and per-assistant rerank model in
Oct 2026 (#1035, #1093, #1143-#1146, #1206). Query embedding is the last question or the concatenated history
(`assistants/references.py`, `EmbedMethod`), an optional "autocut" trims low-score tails.

**Citations**: the system prompt asks the model to emit `<inref id="xxxxxxxx"/>` tags with 8-char source ids
(`completion_models/infrastructure/static_prompts.py:4-9`); the frontend renders them.

**API**: own REST API (`POST /api/v1/conversations/`), not OpenAI-compatible. Model calls go through litellm directly
to providers configured per tenant (credentials stored in Eneo, `tenants/presentation/tenant_credentials_router.py`).

**Request path of one message** (`conversations/conversations_router.py:274` → `conversation_service.ask_conversation`
→ `assistant_service.ask`, `assistants/assistant_service.py:1151-1290`):
1. FastAPI dependency builds a **new DI `Container` per request** (`server/dependencies/container.py:51`) and opens a
   transaction; authenticates JWT or API key (key, user, roles, tenant; API key also: guardrails, scope, Redis rate
   limit, audit event via worker).
2. `_validate_conversation_scope` (router:87) loads the space again for scope checks.
3. `space_repo.get_space_by_assistant` (`spaces/space_repo.py:1520`) → `_get_from_query` (:1236) loads **the whole
   space** with ~15 `selectinload`s (members, group members and their users, services, integration knowledge with
   integrations, model mappings, MCP servers, security classification, `space_repo.py:140-162`), then collections,
   websites, integration knowledge, **all tenant completion/embedding/transcription models**, all assistants with
   websites/groups/attachments/templates/MCP servers and their tool overrides (`space_repo.py:800-994`).
4. Helper-assistant checks, files by id, session create or load, **question placeholder insert** (re-read after
   `INSERT … RETURNING`), optional web search (Tavily), embedding + vector search if knowledge is attached.
5. litellm call, streamed via SSE with pydantic serialisation per chunk; tokens counted several times (tiktoken);
   final `UPDATE` of the question and a `logging` row.

Measured (gateway benchmark, eneo v2.2.3, 4 vCPUs, fake LLM answering instantly, assistant **without** knowledge):

| Metric | eneo v2.2.3 | Umeå gateway (for scale) |
|---|---:|---:|
| Postgres statements per message | **78** | 0.7 |
| Redis commands per message | 31 | 21 |
| Added latency, 1 client, non-streaming / streaming whole (median) | **54 ms / 68 ms** | 0.97 / 1.27 ms |
| TTFB streaming, 1 client (median) | 53 ms | 1.28 ms |
| Peak throughput streaming / non-streaming | **51 msg/s** (8 clients) / 56 msg/s | 17 888 / 16 805 msg/s |
| Median / p99 at 64 clients, streaming | **1.0 s / 13.8 s** | 4.6 / 17 ms |
| App CPU per message (16 clients) | **79 ms** (+6.4 ms DB/Redis) | 0.49 ms |
| Memory, platform process | 2.2 GB | 0.4-1.7 GB |
| At 128 / 256 clients | 6 % / 53 % errors, 31.8 s median | 0 errors |
| Real-provider timing, TTFT p95 at 16 / 64 / 256 users | 2.3 s / 11.2 s / 33.5 s (137 failed) | 514 / 519 / 610 ms |

Scaling is near-linear in cores (~13 msg/s per core, 73-82 ms CPU/message) but collapses under concurrency: each
request holds 2+ pooled connections, the per-worker pool (20 + 10 overflow, 30 s timeout, `database/database.py:49`,
`main/config.py:265-269`) starves and requests fail with HTTP 500 after 30 s. The benchmark authors attribute about
half the statements to the whole-space load and note caching space/assistant resolution would remove most DB work
(inferred from code reading, not profiled). These numbers **exclude retrieval**; with knowledge, an embedding call
and an unindexed vector scan are added.

## 6. Admin, multi-tenancy & permissions

- **Tenants**: multi-tenant in one deployment, per-tenant OIDC federation (`docs/FEDERATION_PER_TENANT.md`,
  `authentication/federation_router.py`), per-tenant model credentials and crawler settings, sysadmin API across
  tenants. Upstream's rerank PRD (#1035) mentions "the decision to remove multi-tenancy", so this is being reversed.
- **Org roles**: custom roles composed of permissions `assistants, personal_chat, group_chats, apps, services,
  collections, insights, AI, editor, admin, websites, integrations, shared_spaces, api_keys` (`roles/permissions.py:16`).
- **Spaces**: personal space per user, shared spaces, an org space; space roles `admin/editor/viewer`
  (`spaces/api/space_models.py:50`), members can be users or user groups (SCIM-provisioned, `scim/`). All knowledge,
  assistants, apps and model choices are scoped to a space.
- **Security classifications** (`security_classifications/`): tenant-defined ordered levels; a space has a level and
  only accepts models and MCP servers with an equal or higher level (`spaces/space.py:671-685`). Static, set by an
  admin; no content inspection.
- **API keys v2** (`authentication/`): `sk_` secret keys and `pk_` publishable keys; pk_ are read-only and must have
  an origin allow-list (`authentication/api_key_policy.py:197-236`); scopes tenant/space/assistant/app; per-key rate
  limit, expiry, rotation notifications, revocation when scope disappears.
- **Audit**: 112 action types (`audit/domain/action_types.py`), per-action enable config, export jobs, retention purge
  cron, and an "access justification" flow for viewing audit data (`audit/infrastructure/audit_session_service.py`).
- **Retention**: conversation retention resolved assistant → space → tenant policy, purged by a daily cron
  (`data_retention/infrastructure/data_retention_service.py:147-168`, `worker/routes.py:551`).
- **No PII handling** on content: the gateway benchmark sent a test personnummer, e-mail and phone number; they reached
  the model unchanged and were stored in `questions` (performance.md, "Data protection").

## 7. Strengths worth copying

1. **Security classification on spaces vs models** (`spaces/space.py:671`): a simple, explainable rule that a
   collection/assistant marked "confidential" cannot be wired to a model below that level. Maps directly onto Umeå's
   gateway residency/PII policies; express it as "assistant classification ≤ gateway key's allowed residency".
2. **Retention hierarchy** assistant → space → tenant with a nightly purge, and audit retention separate from chat
   retention. Exactly the shape needed for arkivlagen/GDPR discussions.
3. **Audit with purpose**: rich action taxonomy, exportable, and *justification required to read audit logs*. Public
   sector auditors ask for this.
4. **`pk_` vs `sk_` keys**: publishable, read-only, origin-bound keys for public web widgets; secret keys for
   integrations. Directly applicable to the planned web widget.
5. **SharePoint done the hard way**: delta tokens with 410 recovery, Graph subscriptions with renewal cron,
   app-only (tenant app) mode to avoid person-dependency, and their issue list (#427 throttling, #521 silent
   subscription death, #522 orphaned docs after moves, #429 path normalisation, #1081 sync health) is a ready-made
   test checklist.
6. **Crawl operability**: per-tenant concurrency slots, heartbeats, stale-job preemption, per-page failure reasons
   aggregated into a crawl-run summary (`worker/crawl_tasks.py` failure_counts). Users see *why* pages failed.
7. **Space roles + SCIM groups**: department self-service via admin/editor/viewer on a workspace, membership by IdP
   group. Matches Umeå's department/team model.
8. **Templates and help assistants**: departments start from a vetted template rather than an empty prompt; upstream
   also ships widgets (#849) and form apps. Worth a thin version on top of the Chat API later.
9. **Inline citation protocol** (`<inref id/>` with short ids) is cheap and robust across models.

## 8. Flaws & pain points

**Verified (code or measurement):**
1. **Slow, DB-heavy chat path.** 78 SQL statements, 54-68 ms added latency and 79 ms CPU per message with no knowledge
   attached; peak ~51 msg/s on 4 vCPUs; p95 TTFT 11 s at 64 users and failures at 128+ (performance.md, eneo v2.2.3).
   Cause visible in code: whole-space load per message (`spaces/space_repo.py:140-162,1236-1257`), per-request DI
   container (`server/dependencies/container.py:51`), several transactions over two pooled connections.
2. **Kitchen-sink monolith with circular coupling.** 63 modules, 132.7k LOC backend, 650 import edges, 154 mutual
   module pairs (61 excluding hubs); one 1 544-line DI container; API and worker are the same image; migrations run
   on API start. Knowledge retrieval cannot be deployed or scaled separately from chat, admin and SCIM.
3. **One worker for everything.** Crawls (Twisted reactor in a thread), audit writes, transcription, app runs,
   SharePoint sync and insights share one ARQ worker (`jobs/job_models.py:11-24`); ~3.6k LOC of feeder, watchdog, recovery, heartbeat and concurrency code
   exist to keep crawls from starving or leaking (`worker/feeder/*`, `crawler/crawler.py:45-75`).
4. **Retrieval quality basics missing (v2.1.1)**: pure vector search, no hybrid/Swedish FTS, no ANN index on a
   dimensionless vector column, global 200/40-token chunks with no structure or title prefix, no reranking
   (`info_blob_chunk_repo.py:67-122`, `datastore.py:33-35`). Reranking only arrived Oct 2026 (#1035).
5. **Crawler wastes work and indexes noise**: full re-crawl and full re-embed of every page each interval (hash stored
   but not compared, `worker/crawl/persistence.py:235`, `crawl_context.py:160`); no ETag/Last-Modified; html2text of
   whole pages including navigation (`crawler/parse_html.py:37-45`).
6. **No OCR / layout-aware parsing** (pdfplumber/docx2python only): scanned PDFs, common in municipal archives, yield
   nothing.
7. **SharePoint content loses its ACLs**: anyone in the space sees everything imported; a whole operational issue
   history around silent sync failures (#521, #522, #427, #1081).
8. **Not OpenAI-compatible**: own conversations API, so Open WebUI/other clients need adapters; provider credentials
   and routing live inside Eneo (litellm), duplicating what a gateway does; no PII protection on content
   (performance.md data-protection table).
9. **Dead/legacy code paths**: `workflows/` without a router, `groups_legacy/` alongside `collections/`, two key repos
   (`api_key_repo.py`, `api_key_v2_repo.py`), dependency pins dictated by litellm (`pyproject.toml`, CRITICAL litellm
   pin #302).

**Inferred (not measured here):**
- Retrieval latency grows linearly with chunks per scope because of the exact scan; acceptable at small sizes,
  painful for large website collections.
- Scope creep continues (Skills with an isolated script runtime #660, second frontend web-next, widgets), so the
  surface to secure and upgrade keeps growing; 354 migrations make self-hosted upgrades heavier.
- The multi-tenancy layer adds cost to every query and is being removed upstream (#1035 PRD note), suggesting it did
  not pay off for single-organisation installs.

## 9. Implications for the municipality design

1. **The design's core bets are confirmed by evidence**: separate data plane (Chat/Knowledge API) from admin, keep
   ingestion in its own worker, route all model calls through the gateway. Eneo's 78 statements per message come mostly
   from resolving admin-shaped aggregates in the hot path; Umeå's Chat API should resolve an assistant from a
   **cached, flat read model** (assistant → collections, model, prompt, retention), invalidated via Redis on admin
   change. Target: ≤ 3 statements per message (history insert can be batched/async).
2. **Set a performance budget and benchmark it with the same harness** (`benchmarks/compare`) from day one, including
   a knowledge-backed assistant, so the "faster than Eneo" claim is measured, not asserted.
3. **Retrieval**: do what Eneo lacks: hybrid (pgvector HNSW with fixed dimensions per collection + Swedish FTS, RRF),
   structure-aware chunks with title/heading prefix, rerank optional. Fix the vector dimension per collection so an
   HNSW index is possible.
4. **Crawler**: implement change detection that actually skips work (conditional GET, content hash compared before
   re-chunk/re-embed), main-content extraction, and Docling with OCR. Avoid running a crawler framework inside a
   general job worker; one crawler process per job type, or a dedicated queue, keeps the Twisted-style lifecycle
   problems out.
5. **SharePoint**: copy Eneo's mechanics (app-only via `Sites.Selected`, delta + subscriptions + renewal + 410
   recovery + 429 back-off + reconciliation + sync health UI) and use its closed issues as acceptance tests. Decide
   explicitly about ACLs: either only index libraries whose audience equals the assistant's audience, or store
   SharePoint permissions and filter at query time. Eneo's "space membership wins" model is a known gap.
6. **Copy the governance features, not the platform**: security classification ↔ gateway key policy, retention
   hierarchy (assistant → team → platform), audit with justification for reading logs, `pk_`/`sk_` key split for the
   widget, space-like roles mapped to department/team codes.
7. **Keep the pipeline builder in scope-guard mode**: Eneo shows the opposite failure (a hard-coded pipeline and a dead
   `workflows` module), but also that product scope creep (apps, Skills, group chat, insights, transcription) is what
   made it a kitchen sink. Each new capability should be a client of the Chat/Knowledge APIs, not a module inside them.
8. **Watch the module graph**: enforce one-way dependencies between services and projects (architecture tests), so the
   .NET solution does not drift into Eneo's 154 mutual-import pairs.
9. **Interoperability**: if some departments already use Eneo, Eneo can point at the gateway as its OpenAI-compatible
   provider (tested in performance.md); a later option is exposing the Knowledge API to Eneo as an MCP tool, which
   lets both coexist during migration.
