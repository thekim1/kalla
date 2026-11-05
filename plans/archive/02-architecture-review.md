# Architecture review: top 7 improvements

Date: 2026-10-29. Reviewed: `../../01-general-idea.md`, `../../platforms/00-comparison.md` and every `plans/*/plan.md`.
Ordered by impact. Each item names the problem, why it matters, and the concrete change.

**Status: all seven applied to the plans on 2026-10-29** (referred to there as R1–R7). Where they landed: R1 platform
§2, worker §1, parser; R2 platform §3, worker §3–4; R3 platform §6, worker §5, knowledge API §3; R4 new
`../gateway-changes/`, chat API §4; R5 identity §5, chat API §6; R6 platform §9, worker §7, admin; R7 platform §4,
worker §4, admin §2.

## 1. Keep ingestion from competing with chat for the same CPU and database

**Problem.** The plan promises that ingestion never affects chat, but the pilot target puts everything on one
4 vCPU host and one Postgres. Docling OCR and layout models are CPU-heavy (minutes per scanned document), HNSW index
inserts are expensive, and the job queue and run tables churn constantly (`SKIP LOCKED` dequeues, updates, deletes →
vacuum pressure). A large crawl or a batch of scanned PDFs will raise chat latency: the same failure as Open WebUI
freezing during ingestion (#19421), only in separate containers.

**Change.**
- Two resource zones from day one, even on one host: data plane (`rag-api`, Postgres, Redis) and ingestion
  (`rag-worker`, `rag-parser`) with hard CPU/memory limits and lower CPU priority for ingestion. In production, a
  separate host or node pool for ingestion; the parser is the first thing to move.
- Postgres: one connection pool per role with a small cap for the worker, `statement_timeout` per role, chunk
  writes in batches; jobs and run items in their own tables with aggressive autovacuum settings (or partitioned by
  day so old ones are dropped, not deleted).
- Make "chat p95 during a full crawl and OCR batch" a release test, measured with the gateway's harness.

## 2. Fetch and parse each source document once, not once per collection

**Problem.** Documents belong to a source, and sources belong to one collection. In a municipality many departments
point at the same pages: umea.se, shared policies, the same SharePoint libraries. Each would crawl, parse, embed
and learn parse profiles separately: duplicated load on our own websites, duplicated storage and embedding cost,
and inconsistent answers when copies are out of sync.

**Change.** Split the content model into two layers:
- **Document store** (platform-wide): one document per canonical identity (normalised URL, SharePoint item id,
  file hash), fetched and parsed once, with its parse outcome and strategy. Crawls of the same site are merged into
  one crawl frontier with one politeness budget.
- **Collection index**: collections reference documents and own their chunking/embedding outputs. Two collections
  with the same pipeline from the parse node onwards share chunks (the output cache keyed by content hash +
  params hash already makes this almost free).

Classification and access stay on the collection; the document store is internal and never queried directly.

## 3. One platform embedding model, on-prem, instead of one per collection

**Problem.** Each collection has its own embedding model, and `sensitive` content must embed on-prem. An assistant
that links an `internal` and a `sensitive` collection then needs two query embeddings, two searches and a fusion
across incomparable score spaces. The Knowledge API plan does not handle this, the partial HNSW index per model
multiplies, and changing models means re-embedding per collection.

**Change.**
- An **embedding profile** registry with one default: a multilingual model served **on-prem** through the gateway,
  used for every classification. That removes embeddings from the classification problem entirely: content never
  leaves the municipality to be indexed, and the query embedding is computed once.
- Other profiles only as an explicit exception; retrieval fans out per profile and fuses with RRF (rank-based, so
  score spaces do not matter) when an assistant spans profiles.
- A model change is a platform operation: re-embed into a new profile in the background, then swap (the Onyx
  pattern already in the plan).

## 4. Add two small gateway features instead of storing many gateway keys

**Problem.** Each team links gateway keys (two for teams with sensitive assistants) that we store encrypted. That
means many secrets to rotate, and classification depends on the key's settings, which a gateway team admin can
change later (team admins may change a key's residencies, per `identity-and-roles.md`). The check "only on-prem
models" happens once at link time; a later widening silently breaks the `sensitive` guarantee.

**Change.** We own the gateway, so close the gap there:
- **Delegated attribution**: one platform service key that may act for listed teams with a header such as
  `x-ume-on-behalf-of: <team code>`; the gateway charges that team's budgets and applies its policies. One secret,
  no per-team key management.
- **Per-request residency floor**: a header such as `x-ume-max-residency: OnPrem` that can only narrow what the key
  allows. Chat API and worker send it for `sensitive` work.
- **Fail closed**: for `sensitive` requests the Chat API checks the `x-ume-residency` response header (which the
  gateway already returns, `GatewayHeaders.cs:15`) before streaming a byte, and aborts if it is not on-prem.
  Until the gateway features exist, this header check alone already catches a widened key.

## 5. Treat chat clients as data stores, with a classification ceiling per client

**Problem.** Retention and classification are designed for our database, but clients keep copies. Open WebUI stores
every chat in its own database with no automatic 90-day deletion, and sends conversation content to its task model
for titles, tags and follow-ups (`../../platforms/open-webui.md` §9). A `sensitive` assistant used through Open WebUI
can therefore keep content forever, outside our audit, and send it to a non-on-prem model.

**Change.**
- A **client registry** in the admin: each client (Open WebUI instance, widget, Teams, an integration) has a maximum
  classification and a data-handling profile (stores history? retention? task model?). `/v1/models` and
  `/v1/chat/completions` only expose assistants at or below the client's ceiling.
- Open WebUI in the pilot: ceiling `internal`; task model pointed at an on-prem model through the gateway; chat
  deletion after 90 days in its own database (a scheduled job against its API or DB until it supports retention).
- `sensitive` assistants only through clients that keep no history of their own (our own client or the API).

## 6. Make evaluation a platform component, not a postponed benchmark

**Problem.** The Swedish benchmark is postponed, yet several features depend on a quality signal: auto-applied
parse profiles with "rollback on quality drop", pipeline publish, retrieval tuning per collection, and the
OpenSearch trigger ("recall@10 ≥ 5 points better"). Text yield and OCR confidence measure parsing, not whether the
right answer is found. Without evaluation, auto-apply has nothing reliable to roll back on.

**Change.** A small **evaluation component** (in the worker plus a page in the admin):
- **Bootstrap without humans**: generate question/answer pairs from a sample of each collection's chunks with an
  LLM through the gateway (on-prem model), keyed to the source chunk. Cheap, automatic, good enough for regression.
- Human questions and feedback-marked answers are added to the same set over time; the postponed benchmark becomes
  the curated part of it, not a separate project.
- **Gates**: pipeline publish, parse-profile auto-apply and retrieval setting changes run the collection's set on a
  sample and block or roll back when recall@k drops beyond a threshold. Results are stored per version.

## 7. Make configuration changes transactional and versioned

**Problem.** Configuration is written to Postgres by the admin API and invalidated over Redis. Redis runs without
persistence and pub/sub is fire-and-forget, so a missed message leaves stale config until the TTL expires. For
most settings that is fine, but key revocation, access changes and classification changes are security decisions;
stale caches mean a revoked key or a downgraded assistant keeps working. There is a second skew: the admin API
validates pipelines against node types it learns from a shared library, while the worker may run another version
during a rolling update.

**Change.**
- **Transactional outbox** in the `config` schema: every change writes a monotonically increasing config version in
  the same transaction; Postgres `LISTEN/NOTIFY` wakes the data plane and worker (also used to wake the job queue),
  and a cheap version check on a short interval catches anything missed. Redis stays for rate limits and caches.
- **Fail-closed for security-relevant entities**: keys, access and classification are re-checked against the current
  config version before use; an unknown version blocks rather than serves stale.
- **Node catalogue manifest**: the worker registers its node types, versions and parameter schemas in the database
  at startup; the admin validates and generates forms from the registered manifest, and pipelines pin
  `typeVersion`. Publishing is refused if no running worker supports a node version.

## Smaller findings (for the per-service detail rounds)

- Chat API: deriving a conversation id from a history hash is fragile; require a client-supplied id or issue one.
- Parser: synchronous HTTP for multi-minute OCR jobs needs long timeouts; use page-range requests or async jobs.
- Intermediate outputs expire after 30 days while "minimal re-processing" relies on them; keep the last output of
  the parse node per document as long as the document lives.
- Preview jobs need priority over bulk jobs or the canvas feels broken while a crawl runs.
- Open WebUI file uploads do not reach our API; decide before the pilot whether ad-hoc document chat is in scope.
