# Performance and scalability pass

> **Simplification pass (2026-10-30).** This document is rewritten to the decided form of
> [`simplification-pass.md`](simplification-pass.md): the pilot is one host and one slot; the multi-host mechanisms
> (`LeaderLeases`, batched dequeue, debounced `NOTIFY`, Redis crawl and embedding leases, connection-budget script,
> `ume_rag_monitor`, a/b slots) arrive with the first multi-host deployment (P2); `Jobs` is unpartitioned; the NER lane
> and Hunspell in Postgres are gone; chunks carry a scalar `CollectionId`; every PDF goes to the parser; umea.se
> numbers (§13.1 there) replace estimates where they apply. The pilot runs six long-running containers (§14 there):
> no Redis in P1 (rate limits and the query-embedding cache in process, admin sessions in Postgres) and pgBackRest
> inside the Postgres image; Redis returns on host D with the first multi-host deployment.

Cross-cutting pass over the Ume.Rag design (after the consistency pass, [`consistency-pass.md`](consistency-pass.md)):
good parallelism now, and the ability to scale the crawler, parser, embedding and data plane out over several hosts
later. Binding inputs: the Decisions table in [`../01-general-idea.md`](../01-general-idea.md) §10, the user's
answers of 2026-10-30 (§10.1 here), resource zones and the R1 release test of [`platform/plan.md`](platform/plan.md) §2,
roles, caps, queue, outbox, blobs and Redis of [`platform/data-model.md`](platform/data-model.md) (§1, §3, §4, §5, §9,
§11), settings rule, compose, zones, R1 test, observability and the Kubernetes outline of
[`platform/repo-and-deployment.md`](platform/repo-and-deployment.md) (§3, §6, §7.3, §8.3, §9, §10, §11), execution,
jobs, fairness, scheduler, index writer and capacity of [`ingestion-worker/design-engine.md`](ingestion-worker/design-engine.md)
(§7–§9, §14, §18), frontier, releaser, host budgets, ClamAV and change detection of
[`ingestion-worker/design-connectors.md`](ingestion-worker/design-connectors.md) (§5–§8, §11, §12, §17), personal data
and embed of [`ingestion-worker/design-processing.md`](ingestion-worker/design-processing.md) (§10, §11, §16), the parser
supervisor of [`parser/design.md`](parser/design.md) (§10, §13, §14), retrieval statements, exact vs HNSW, pooling and
latency of [`knowledge-api/design.md`](knowledge-api/design.md) (§5, §6, §11, §13), the writer and latency of
[`chat-api/design.md`](chat-api/design.md) (§12, §16), the hub of [`tools/design.md`](tools/design.md) (§2.2) and the
admin's background leaders of [`admin/design.md`](admin/design.md) (§2.2). Nothing is implemented. Changes needed
elsewhere: §9; questions: §10; unverified facts: §11 (rows for [`to-verify.md`](to-verify.md) in §9).

**Sources read for this pass** (read only, never executed; packages in the session scratchpad `perf/`):

| Source | Version | Used for |
|---|---|---|
| Gateway `~/git/llmgateway` | `e25b491` (v0.8.5, 2026-10-29) | measured CPU per message and scaling (`docs/performance.md`), horizontal-scaling plan (`docs/performance-improvement-plan.md`), Kubernetes path (`docs/kubernetes.md`), Redis in-flight leases, key `MaxConcurrentRequests`, binary `COPY` writer, HTTP handler, advisory-lock leader |
| NuGet `AWSSDK.S3` / `AWSSDK.Core` | 4.0.104.6 (2026-10-21) / 4.0.102.13; latest 4.0.104.8 (2026-10-29) | S3 client, checksum defaults, conditional writes (XML docs) |
| NuGet `Minio` | 7.0.0 (2025-11-05) | client comparison (nuspec dependencies) |
| NuGet `Npgsql` | 10.0.3 | `NpgsqlBinaryImporter`, `NpgsqlBatch`, `No Reset On Close`, `Multiplexing` (XML docs) |
| `pgvector-dotnet` (`Pgvector`) | 0.3.2 (tag `v0.3.2`) | `VectorTypeInfoResolverFactory.cs`, `HalfVector.cs`, `examples/Loading/Program.cs` |
| pgvector | 0.8.7 (`README.md` at tag `v0.8.7`) | halfvec, binary quantization, build memory, vacuum, scaling |
| PgBouncer | 1.26.1 (2026-10-14); Debian trixie 1.24.1, PGDG 1.26.0 | `doc/config.md` (master), feature map `pgbouncer.org/features.html` |
| PostgreSQL | `REL_17_STABLE` `src/backend/commands/async.c` | `NOTIFY` commit serialisation |
| MinIO `github.com/minio/minio` | archived; last release `RELEASE.2025-10-15T17-29-55Z` | licence and distribution status (`README.md`, GitHub API) |
| SeaweedFS, RustFS, Versity S3 Gateway | GitHub API 2026-10-30; image `chrislusf/seaweedfs` 4.51 | alternative S3 servers (licence, activity) |
| Open WebUI `~/git/rag-platforms/open-webui` | v0.12.5 | `backend/open_webui/env.py` database and worker settings |
| umea.se (live site) | fetched 2026-10-30 (`simplification-pass.md` §13.1) | sitemap size and types, `lastmod` coverage, change headers, rendering |

## 1. Summary of decisions

The user answered the pass's questions on 2026-10-30 (§10.1 answers 5–8: municipal S3 else SeaweedFS; halfvec HNSW
from P1; crawl rules stay with consent-gated per-host overrides; worker-only gateway key now, query lane by trigger).
The simplification pass then moved the multi-host mechanisms to P2. The other documents carry the decided form.

| # | Topic | Decision |
|---|---|---|
| 1 | Scale target | ≈ 10× the pilot on 3–5 hosts (answered 1): ~100,000 pages/hour **checked**, downstream sized for 10–20k pages/hour **changed** sustained and bulk loads throttled to the embedding budget; 5–10M chunks; 20+ departments; several hundred concurrent chat users (≈ 20 messages/s peak). Beyond that only behind §6 triggers |
| 2 | Binding resources | Per-host politeness binds crawling; the gateway's on-prem **BGE-M3** binds embedding (bulk); the **parser** binds documents (every PDF goes through it, and PDFs are the larger half of umea.se: 7,959 PDF vs 6,159 HTML URLs) and OCR; names (NER) run only for gated documents; Postgres binds only through HNSW memory and vacuum; chat is bound by the gateway's answer models, not by us (§2) |
| 3 | Scale-out shape | Pilot: one host, one slot, six long-running containers, no Redis. From P2: Compose per host, roles per host (answered 2): same images, `Worker:Classes` + deployment settings per instance, one Postgres (+ streaming standby), one Redis, one object store; routed LAN with TLS and host firewalls, no overlay network (§5) |
| 4 | Per-process vs fleet settings | Per-instance concurrency is **`Worker:Concurrency:<class>`** (deployment, per the settings rule); `Settings.WorkerConcurrency` keeps only fleet-wide shares and caps (§3.4) |
| 5 | Connections | Small pools (`rag-api` 12, `rag-admin` 5, `rag-worker` 10, `max_connections` 100; starting values measured in P0) and an explicit budget per role × instances (§3.1); **no PgBouncer** up to 5 hosts, but code is **pooler-safe from P0** (no session state; LISTEN on its own connection string); budget script from P2; PgBouncer ≥ 1.21 in transaction mode behind a trigger |
| 6 | Vectors | **HNSW on `("Embedding"::halfvec(1024))`** (column stays `vector`) keeps Postgres viable to ≈ 10M chunks on a 64 GB database host; the O1 trigger is measured HNSW memory, latency and filtered recall instead of "> 2–3M chunks" (§3.3) |
| 7 | Job queue | Stays Postgres (answered 4) behind an **`IJobQueue`** seam; `Jobs` **unpartitioned** with a partial unique dedup index; batched dequeue and completion, coalesced wake-ups and debounced `NOTIFY` (it serialises commits, `async.c:905-922`) with the first multi-host deployment (P2); broker only by §6 trigger |
| 8 | Leaders | Tick leaders keep `pg_try_advisory_xact_lock` (failover = next tick); config singletons (`Worker:Maintenance`, `Admin:Retention`) stay singletons on one host and become **lease leaders** (`content."LeaderLeases"`, failover ≤ 30 s) or idempotent lock-guarded batches in P2 (§3.5) |
| 9 | Crawl connections | One `fetch` instance in the pilot keeps the per-instance cap exact; from the second instance (P2) a **Redis in-flight lease per host** (gateway pattern, `RedisStores.cs:36-38`), with ⌈cap ÷ k⌉ as the Redis-down fallback (§3.6) |
| 10 | Parser scale-out | Default: a `parse` worker **co-located** with each parser host on its internal `parser` network (no cross-host parser traffic, no new auth); multi-endpoint `Parser:Endpoints` only as an option (§3.8) |
| 11 | Embedding | Per-instance AIMD (P2, weakness review §1; P1 a fixed per-instance limit from configuration); fleet-wide in-flight cap (Redis lease, `Embedding:GlobalMaxInFlight`) from P2; batches ≤ 32 inputs / 8,192 tokens at scale; the worker's per-level gateway keys are worker-only by construction; separate query lane by trigger (§3.9) |
| 12 | Blobs | `IBlobStore` **write-once** contract in P0 (keys never overwritten, put-if-absent, tombstone + delayed purge, no rename or listing on hot paths); **`AWSSDK.S3`** client with `ForcePathStyle` and checksums `WHEN_REQUIRED`; `internal` blobs envelope-encrypted, `public` blobs on encrypted disk only, no SSE (§3.13) |
| 13 | Object store server | MinIO's community edition is **archived and source-only** (no maintained images): the municipality's S3 service if IT offers one, else SeaweedFS (answered 5; register S2) |
| 14 | Chat path | Stateless instances, no sticky sessions; `Retrieval:MaxConcurrentSearches` 4 against a pool of 12 (⌊(pool − 3) ÷ 2⌋); hot-row upserts sorted and placed last; read traffic to the standby only by trigger (§3.11) |
| 15 | Tests | R1 stays (`perf.yml`, P1); **scale test** (`scale.yml`, P2): synthetic multi-host site, 5M/10M-chunk seed, FakeGateway with a GPU throughput model, 300-VU chat; each decision has a proving metric (§7) |
| 16 | Change detection on umea.se | No `ETag`/`Last-Modified` (`Cache-Control: no-store`), so conditional GETs save nothing; sitemap `lastmod` (on all 24,184 URLs) + content hash; **incremental `lastmod` runs are the default** for hosts whose sitemaps carry it (§2.3); later ([weakness review §3](weakness-review.md#3-outdated-or-removed-content-keeps-being-answered)): only once a 500-URL sample shows `lastmod` reliable, umea.se runs full traversals until then |

## 2. Capacity model

Workload assumptions (all *estimates* unless cited): a typical web page ≈ 600 words ≈ 8 chunks ≈ 3,600 embedding
tokens (engine §18); a document (PDF/Office) ≈ 40 chunks; weekly full traversals with daily incremental `lastmod` runs;
10 % of checked pages changed per full run (pilot data will replace this); chat: 300 active users, one message per
60–90 s each, bursts to **20 messages/s**, ≈ 8 s per answer stream.

**umea.se** (measured 2026-10-30, `simplification-pass.md` §13.1): the sitemap lists 24,184 URLs, all with `lastmod`:
6,159 HTML, 7,959 PDF, ≈ 8,900 images and 233 videos (images and video excluded by default). Crawled content is
14,118 URLs ≈ 6,159 × 8 + 7,959 × 40 ≈ **0.37M chunks** (chunks per document estimated) ≈ 165M embedding tokens.

**Chunk sets belong to one source** (simplification §2): the same page in N departments' sources is N documents and N
chunk sets. Fetches, parse output and embeddings are shared through content-addressed caches, so each copy after the
first costs no request, parse or embed; it does cost chunk rows, index writes and HNSW entries. 10M chunks therefore
counts copies: e.g. 20 departments each indexing umea.se ≈ 7.4M chunk rows.

### 2.1 Ingestion per stage

| Stage | Cost per unit (source) | Pilot: 10k pages/h checked, all changed (R1) | 10×: 100k/h checked, 10k/h changed (steady) | 10×: bulk, 100k/h changed | Binds |
|---|---|---|---|---|---|
| List / sync, claims | 1 claims upsert of ≈ 100 links + ≈ 3 statements per page (connectors §17) | ≈ 11 statements/s | ≈ 110 statements/s, ≈ 2,800 claim-row probes/s (−60 % with the nav-link cache, §3.6) | same | Postgres (cheap) |
| Fetch | 1 politeness slot, ~200 ms latency; AngleSharp + hash 10–15 ms CPU (connectors §17) | 2.8 req/s | **27.8 req/s aggregate**; ≈ 0.4 worker cores | same | **politeness** (§2.3) |
| ClamAV | files only, PDF 50–200 ms (connectors §17) | small (umea.se: more PDFs than pages) | ≈ 10 % files → ≈ 0.3 cores | ≈ 0.3 cores | CPU, local |
| Worker CPU (HTML extract with the site profile, clean, PII patterns, decompounder, tokenizer, chunk, JSON, AES) | ≈ 80 ms per changed page (engine §18, processing §16) + 15 ms per checked page | 0.25 cores | ≈ 0.65 cores | ≈ 2.6 cores | CPU (cheap) |
| Parse, PDF text layer (parser, pypdfium2 fast path) | every PDF; per-page cost *to measure* (§11 #132) | umea.se onboarding: 7,959 PDFs | share of changed documents | share of bulk | CPU |
| Parse, layout (Docling) | p50 1.0 s × 4 threads ≈ 4 core-s per page (parser §14), only pages `/v1/inspect` routes to layout | pages without a usable text layer or with tables | 2–4 cores | 10–20 cores | CPU |
| Parse, OCR (Tesseract `swe`) | p50 4 s × 4 threads ≈ 16 core-s per page (parser §14) | 3,000 pages ≈ 3.3 h (parser §14) | onboarding 30k pages ≈ 133 core-h (one night on ≈ 16–20 cores) | – | **CPU** (GPU optional, trigger) |
| Name check (NER), gated | 0.3–1.0 s on a 2-thread lane ≈ 0.6–2 core-s per gated page (processing §16); only documents in a source of a `public` collection, not already flagged, with ≥ 5 contact values | *g* × 1.7–5.6 cores | *g* × 1.7–5.6 cores | *g* × 17–56 cores | CPU (*g* = gated share, *to measure*) |
| Chunk / tokenizer | 5–10 ms per page (processing §16) | < 0.1 core | < 0.1 core | 0.3 cores | CPU |
| Embed | ≈ 3,600 tokens per changed page (engine §18); cache hits for texts already embedded under the profile | **10k tokens/s** | ≈ 10k tokens/s | **≈ 100k tokens/s** | **gateway BGE-M3** (§3.9) |
| Index write | 8 chunk inserts + HNSW insert 1–3 ms each in RAM (engine §18), in short batches of ≤ 1,000 then a swap; WAL ≈ 10–15 KB per chunk (estimate), archived by pgBackRest | 22 inserts/s | 22 inserts/s, ≈ 0.3 MB/s WAL | 222 inserts/s (≥ 2 concurrent writers), ≈ 3 MB/s WAL | Postgres I/O, HNSW memory |
| Jobs | ≈ 2 jobs per changed page (fetch, embed; + parse for every PDF and file), ≈ 4 queue statements each | ≈ 22–35 statements/s | ≈ 125–140 statements/s | ≈ 220–330 statements/s | Postgres (cheap) |
| Evaluation (advisory) | per run ≈ 200 retrievals on the full collection (*estimate*) after each publish and nightly; set generation ≤ 200k on-prem LLM tokens (processing §14.7); nightly answer-level judge in `rag-admin` (P2, weakness review §1) | one or two collections nightly | 100 collections nightly ≈ 20k retrievals/night + judge tokens (*to measure*) | – | on-prem LLM |

**Onboarding time** = chunks × 450 tokens ÷ measured embedding tokens/s: umea.se (≈ 165M tokens) ≈ 4.6 h at 10k
tokens/s; 5M chunks at 20k tokens/s ≈ 31 h (several nights); the scheduler throttles bulk loads to the budget instead
of sizing GPUs for the peak (§3.9). A second department indexing the same site costs no embedding (cache hits).

### 2.2 Chat, knowledge, admin

| Stage | Per message (source) | 10× (20 msg/s peak) | Binds |
|---|---|---|---|
| Authentication, policy, rate limit | memory; pilot: in-process rate limit; from P2 + 1 Redis command (chat §16.1), per-instance in-memory limits if Redis is down | 20 Redis cmd/s | – |
| Chat overhead, re-streaming ≈ 200 events | < 10 ms budget (chat §16.1); the gateway spends ≈ 6.5–7 ms CPU per realistic stream (`docs/performance.md:211-213`) | ≈ 0.3 cores across instances; ≈ 160 open streams | – |
| Condense | skipped for self-contained questions (length, no anaphora); otherwise one on-prem call with a 600 ms timeout | share of follow-ups (*to measure*) | gateway providers |
| Query embedding | ≈ 30 tokens; ≤ 60 ms miss (knowledge §11) | 600 tokens/s, but **latency** behind ingestion batches | BGE-M3 queueing (§3.9) |
| Decompounder (query side) | in-process, microseconds per query (*estimate*) | negligible | – |
| K, V, H+D (3 round trips, knowledge §11) | K ≤ 30 ms at 100k chunks; frequent OR terms at 10M *to measure* (to-verify #5); V ≤ 15 ms in RAM | 60 statements/s, ≈ 0.5–2 Postgres cores | Postgres CPU, HNSW memory |
| Rerank (P2, per assistant when the evaluation shows a gain) | gateway endpoint for `bge-reranker-v2-m3` on-prem (a separate P2 gateway release, gateway-changes plan §3d) over the fused candidates | *to measure* | gateway providers |
| Answer model | on-prem/EU models through the gateway (chat §5, §8) | ≈ 200k prompt tokens/s prefill (≤ 8,000 source tokens each), ≈ 6k output tokens/s | **gateway providers** (not ours) |
| Open WebUI tasks | title, tag and follow-up generation switched off | no task-model calls | – |
| Writer | 1 transaction per ≤ 200 turns / 250 ms per instance (chat §12) | ≈ 8–16 transactions/s | – |
| Admin | BFF pages, preview, test panel (admin §10.2) | negligible | – |

### 2.3 Politeness: what actually caps crawling

| Fact | Number |
|---|---|
| Municipal host budget (answered 4, connectors §6.3) | ≤ 10 req/s, 4 connections → 36,000 requests/h, 288,000 per 8-h night window |
| External host | 1 req/s, 1 connection → 3,600/h (a 10,000-page external crawl ≈ 2.8 h, connectors §6.5) |
| Little's law at 200 ms latency | the interval rule `max(1/rate, LatencyEwma ÷ 4)` already keeps ≤ 4 requests in flight, so a municipal host tops out at **min(10, 4 ÷ latency) req/s** = 10 at 200 ms, 5 at 800 ms |
| 100k pages/h checked | needs ≥ 27.8 req/s aggregate: **≥ 3 municipal hosts crawled at full rate at once**, or a per-host override above 10 req/s (answered 7) |
| umea.se alone | 14,118 HTML + PDF URLs: a full traversal at 10 req/s ≈ 24 min (≈ 47 min at 5 req/s); a daily incremental run fetches only URLs whose `lastmod` changed. Politeness is not the limit for the real corpus, only for a single-host "pages per hour" figure |

So the 10× target is an **aggregate** over many hosts and departments, not one site. Levers, none of which breaks the
crawler decisions (user agent, robots always, night window, external 1 req/s, per-host shared budgets):

| Lever | Exists? | Effect | Change |
|---|---|---|---|
| Sitemap `lastmod` incremental runs (connectors §5.4) | yes, **default** for hosts whose sitemaps carry `lastmod` | a daily run fetches only changed + new URLs (≈ 1–5 % of requests) | the site profile records `lastmod` reliability (visible dates and content-hash changes across runs); unreliable → full traversals; set only after a 500-URL sample (weakness review §3) |
| Conditional GETs, stage-2 hash (connectors §12) | yes | saves bytes, CPU, parse and embed where the host sends validators; **not** requests; umea.se sends none | none |
| Shared frontier + freshness window 12 h (connectors §6.2; P2, weakness review §1) | yes (from P2) | 20 departments crawling umea.se cost one request per URL per window | none |
| Content-addressed caches (parse by content hash + settings + build, embeddings by text hash + profile) | yes | an unchanged or already-seen content skips parse and embed; removes ≈ 90 % of downstream cost on re-crawls | none |
| `revalidateEvery` 4 (connectors §5.4) | yes | one unconditional traversal in four | none |
| Allow-listed municipal host override (`Settings.Crawl.HostOverrides`) | yes (admins) | umea.se at e.g. 20 req/s / 8 connections | per-host setting, off by default, only with the web team's written consent (answered 7) |
| Day-time trickle for municipal hosts (e.g. 2 req/s outside the window) | per-host setting | spreads traversals; keeps freshness during the day | off by default, consent as above (answered 7) |
| CMS change feed connector (Sitevision export) | no | zero-request change detection | P3 idea, needs the web team |

## 3. Bottlenecks and decisions

### 3.1 Postgres connections, pooler, timeouts

**Budget rule**: for each role, Σ over instances (`Maximum Pool Size` + 1 LISTEN) ≤ role `CONNECTION LIMIT`; Σ role
limits (migrator and monitor included) + 3 (`superuser_reserved_connections`) ≤ `max_connections`. From the first
multi-host deployment (P2) a deploy-time script checks it (`deploy/postgres/connection-budget.sh`: reads each host's
`.env` instance list and fails the rollout when the sum exceeds a limit) and the role `ume_rag_monitor` (`pg_monitor`)
is added; the single-host pilot's values are fixed in its `.env`.

| Role | Pool per instance (+ LISTEN) | Pilot (1 host, one slot) | 3 hosts | 5 hosts | Limit at 5 hosts |
|---|---|---|---|---|---|
| `ume_rag_api` | 12 (+1); `MaxConcurrentSearches` 4 | 1 → 13 | 2 (a/b) → 26 | 4 (a/b on two hosts) → 52 | 52 |
| `ume_rag_admin` | 5 (+1) | 1 → 6 | 2 → 12 | 2 → 12 | 12 |
| `ume_rag_worker` | 10 (+1) per instance | 1 → 11 | 1 → 11 | 2 (F, P) → 22 | 22 |
| `ume_rag_migrator`, `ume_rag_monitor` (P2), reserved | 2, 2, 3 | 5 | 7 | 7 | – |
| **Sum** / `max_connections` | | 35 / 100 | 56 / 100 | **93 / 100** | – |

The tool hub runs in-process in `rag-api` and `rag-admin` (P2); a `ume_rag_tools` role comes only with a separate
`rag-tools` service. A further P host (+11 worker connections) at 5-host scale exceeds 100: that is the §6 connection
trigger (PgBouncer or a higher `max_connections`).

- **No pooler up to 5 hosts.** The counts fit in 100 connections (93 at 5 hosts); backends hold no dictionaries (stock image,
  decompounding in .NET), so memory per backend is ≈ 10 MB (*estimate*); the Postgres container memory limit is sized
  from the P0 measurement.
- **Pooler-safe from P0** (cheap, code rules + one test): no session-level `SET` (role defaults and `SET LOCAL` only,
  as today), no session advisory locks (only `pg_try_advisory_xact_lock`), no `WITH HOLD` cursors, no non-`ON COMMIT
  DROP` temp tables, no SQL-level `PREPARE`; LISTEN uses its own connection string `ConnectionStrings__ragdb-listen`
  (same server today). A unit test scans SQL constants for the forbidden forms.
- **PgBouncer, when the trigger fires** (§6): ≥ 1.21 (Debian trixie ships 1.24.1, PGDG 1.26.0) in **transaction
  mode** for `ume_rag_api` and `ume_rag_worker`, with `max_prepared_statements` > 0 (default 200 on master,
  `doc/config.md`), because Npgsql's auto-prepare uses protocol-level prepared statements, which transaction pooling
  supports only then (feature map: "Protocol-level prepared plans: Yes²"; `SET/RESET`, `LISTEN`, `WITH HOLD`,
  `PREPARE`, session advisory locks: "Never"). Npgsql `No Reset On Close=true` on the pooled strings (its `DISCARD ALL`
  would clear PgBouncer's tracked statements). Direct (not pooled): LISTEN, migrations, admin (few connections, EF),
  COPY works through it inside a transaction. Known caveat after column changes: "cached plan must not change result
  type" until `RECONNECT` (`doc/config.md`, prepared-statement section); expand/contract migrations avoid it.
- **`MaxConcurrentSearches` vs pool**: `rag-api` starts with pool 12 and `Retrieval:MaxConcurrentSearches` 4 =
  ⌊(pool − 3) ÷ 2⌋ (simplification §9): each search may hold 2 connections while K and V overlap (knowledge §6.6), and
  3 stay for chat writes and the stats flush, so retrieval can never take the whole pool; the start-up validation
  against the pool size stays. The values are measured in P0 (§11 #140); more chat throughput comes from more
  instances, not a larger pool.
- **Short transactions for chunk writes**: a chunk is searchable iff its `DeletedAt` is null. A new chunk set
  (`writing`) is written with `DeletedAt` set, invisible to K and V, in batches of ≤ 1,000 chunks (one short
  transaction each, worker `statement_timeout` 60 s per batch, `idle_in_transaction_session_timeout` 60 s), then one
  short swap transaction per document (fence; `Documents."LiveChunkSetId"` to the new set; clear `DeletedAt` on its
  chunks and set it on the old set's, tens to hundreds of rows; set states); an orphan sweep removes sets never
  swapped. Rollback within 14 days is the same swap back. No
  transaction spans a whole set, so a 20,000-chunk set never pins the vacuum horizon.
- **Standby and read traffic**: a streaming standby is an availability requirement before production beyond the pilot
  (P2, register D1). Read traffic goes to it only by trigger (§6): `hot_standby_feedback = on`,
  `max_standby_streaming_delay = 30s`; `ConnectionStrings__ragdb-read` used by K, V, H+D and the evaluator only (lag of
  seconds is acceptable: a publish swap becomes visible a few seconds later); writes (stats flush, writer) stay on the
  primary.

### 3.2 Postgres write path, vacuum, partitions

| Hot spot | At 10× | Decision |
|---|---|---|
| Chunk insert amplification: heap + TOAST (4 KB vector) + GIN `SearchVector`, `Tags` + B-trees (`CollectionId` scalar, keys) + HNSW | ≈ 10–15 KB WAL per chunk (estimate) | binary `COPY` (engine §14) stays; `wal_compression = lz4` (PG 15+, stock image *to verify*), `max_wal_size` 16 GB, `checkpoint_timeout` 15 min on the 64 GB host |
| WAL archiving (pgBackRest, `archive_timeout` 60 s) | ≈ 0.3 MB/s steady, ≈ 3 MB/s bulk (estimate) | archive throughput and backup storage sized from the WAL rate; `pg_stat_archiver` lag watched against RPO ≤ 5 min |
| Per-source chunk sets (§2) | N departments indexing one site write N chunk sets and N HNSW entries per chunk (embeddings from the cache) | counted in the capacity model; the HNSW size trigger (§3.3) watches it |
| HNSW vacuum ("can take a while", pgvector `README.md:742-748`) | swapped-out sets hard-deleted after 14 days (rollback window); re-processing leaves dead tuples | hard deletes in weekly batches; `autovacuum_work_mem` 2 GB; per-table `autovacuum_vacuum_scale_factor = 0.05` on `Chunks`; metric: vacuum duration; trigger: > 6 h → monthly `REINDEX INDEX CONCURRENTLY` (needs 2× index space) |
| GIN pending list on `SearchVector` (fastupdate) | K latency spikes when the list is long | keep defaults; trigger: K p95 spikes correlated with insert bursts → `fastupdate = off` on that index |
| `EmbeddingCache` duplicates vectors (processing §11) | a 10M-chunk onboarding stores ≈ 10M × 4 KB = 40 GB again (30-day retention) | P2: look up `Chunks` first (`IX_Chunks_Profile_TextHash` on (`EmbeddingProfileId`, `TextHash`) `WHERE "Kind" = 'child'`, ≈ 0.5 GB), cache only texts not in a live set; store cache vectors as `halfvec` |
| `Jobs` (unpartitioned) | ≈ 2.4M job rows per busy night, ≈ 4 versions each | partial unique index on `DedupKey WHERE "State" IN ('queued','running')` replaces the advisory-lock dedup; terminal jobs deleted nightly in batches (7 days); per-table autovacuum (scale 0.02); dequeue cost watched via dead tuples in `IX_Jobs_Ready` (trigger §6). `RunItems` stays day-partitioned |
| Single-row hot spots: `CrawlHosts."NextAllowedAt"` (one per request), `CrawlRuns."Admitted"` (per claim batch) | ≤ 30 updates/s per row | non-indexed columns → HOT updates; `fillfactor = 50` on `CrawlHosts` and `CrawlRuns`, aggressive autovacuum on both |
| Multi-row upserts from several instances: `RetrievalStats` flush (knowledge §4.7), `AssistantDailyTotals` (chat §12.2) | deadlocks between instances flushing overlapping key sets; one totals row per (day, assistant, client, level) locked by every batch | **sort keys** before every multi-row upsert (one rule in `Ume.Rag.Infrastructure`); totals upsert as the **last** statement of the writer batch so its row lock is held for one statement |

**Postgres host at 10×** (estimate, confirmed by the scale test): 8–16 vCPU, **64 GB RAM** (`shared_buffers` 16 GB,
`effective_cache_size` 48 GB), NVMe, ≈ 150 GB data at 10M chunks with the halfvec index (§3.3), `random_page_cost`
1.1, `effective_io_concurrency` 200, `max_parallel_maintenance_workers` 4, `autovacuum_max_workers` 5. The stock
`pgvector/pgvector` image takes these as server settings; managed Postgres qualifies where pgvector ≥ 0.8 and these
settings are available (§11 #130).

### 3.3 Vectors: HNSW at 5–10M × 1024 dimensions

Sizes per element (estimate from pgvector's layout: an element tuple holds the vector, a neighbour tuple ≈ 2m × 6 B =
192 B at level 0; pages are 8 KB; measured with `pg_relation_size` in the scale test):

| Variant | Element + neighbours | Per element on disk | 5M | 10M | Fits a 64 GB host's cache? |
|---|---|---|---|---|---|
| `vector(1024)` HNSW | ≈ 4.1 KB + 0.2 KB | ≈ 4.3 KB, up to **8 KB** when only one element fits a page | 22–40 GB | 43–80 GB | no at 10M (the O1 "> 2–3M chunks / not in RAM" figure is this case on a 32 GB host) |
| `halfvec(1024)` expression index | ≈ 2.1 KB + 0.2 KB | ≈ 2.7 KB (three per page) | ≈ 14 GB | ≈ 27 GB | **yes** |
| binary quantization `bit(1024)` + re-rank | ≈ 0.13 KB + 0.2 KB | ≈ 0.35–0.5 KB | ≈ 2.5 GB | ≈ 5 GB | yes, recall needs re-rank (pgvector `README.md:589-606`) |

**Decision (answered 6):** the profile's HNSW index is created on the half-precision expression from the first
migration (pgvector `README.md:558-571`, "Index vectors at half precision for smaller indexes"):

```sql
CREATE INDEX "IX_Chunks_Embedding_bge_m3" ON content."Chunks"
    USING hnsw (("Embedding"::halfvec(1024)) halfvec_cosine_ops) WITH (m = 16, ef_construction = 64)
    WHERE "EmbeddingProfileId" = '01929b6e-0000-7000-8000-00000000b6e3' AND "DeletedAt" IS NULL;
-- V (approximate): ORDER BY "Embedding"::halfvec(1024) <=> $1::halfvec(1024) LIMIT 100
-- V (exact, scopes ≤ exactScanMaxChunks): ORDER BY ("Embedding"::vector(1024) <=> $1) + 0 — full precision, unchanged
```

- The column stays `vector` (full precision for exact scans, the evaluator and a later profile migration);
  `content.rag_ensure_profile_index` builds the halfvec form for new profiles. `halfvec` indexes up to 4,000 dimensions
  (`README.md:252-253`). Recall cost of fp16 rounding on normalised BGE-M3 vectors is expected to be negligible and is
  **measured** with the evaluation sets (HNSW vs exact on the same collection) before the pilot's first large
  collection (data-model §12.16).
- The **O1 trigger** (knowledge §13): not a chunk count but (a) HNSW index size > 50 % of the database host's RAM, or
  (b) V p95 > 50 ms / retrieval p95 > 300 ms after tuning, or (c) filtered recall: HNSW recall@100 against exact < 0.95
  on mid-size collections (below), or (d) the existing recall and facet triggers. With halfvec a 64 GB host keeps 10M
  chunks below (a).
- **Partitioning `Chunks`** by profile does not shrink a profile's index; only a second profile (P3 migration) gets its
  own partial index, built **after** the shadow load with `CREATE INDEX CONCURRENTLY` (faster than incremental inserts,
  `README.md:289-319`), replacing the "index before any row" rule for large migrations. With a scalar `CollectionId`,
  per-collection partial indexes for large collections are possible; they are a §6 fallback for filtered recall.
- **Builds and rebuilds** need the graph in `maintenance_work_mem` to be fast (`README.md:291-305`): ≈ 27 GB at 10M is
  too much on a live 64 GB host, so rebuilds run in a maintenance window with 16 GB and parallel workers, or switch to
  the binary-quantized index (fast builds, `README.md:319`) — that switch is the §6 fallback before OpenSearch.

**Filtered search, the weak spot at scale.** HNSW filters after the walk; with `relaxed_order` it stops at
`hnsw.max_scan_tuples` (pgvector default 20,000). A collection of selectivity *s* (its share of the profile's index)
needs ≈ 100 ÷ *s* visited tuples for 100 hits, so with the pgvector default only *s* ≥ 0.5 % is safe: at 10M chunks
that is ≥ 50k chunks. Decision: the V variant is chosen by **selectivity**, not size: exact when scope ≤
`exactScanMaxChunks`; else HNSW when *s* ≥ `Settings.Retrieval.minHnswSelectivity` (0.2 %); else exact, counted as
`rag.retrieval.exact_large` (knowledge §6.4). `hnsw.max_scan_tuples` is a **role default** of 100,000 (with
`hnsw.ef_search` 100 and `hnsw.iterative_scan = relaxed_order`; data-model §1.1), which is 2 × the ≈ 50,000 tuples a
scope at the 0.2 % floor needs, so no per-query `SET LOCAL` is needed; a walk stops at 100 matching rows, so the bound
only costs time near the floor. The scale test
measures recall and latency per band; a failing band is trigger (c) for O1.

### 3.4 Job queue

| Issue | At 10× | Decision |
|---|---|---|
| Seam for a later broker (answered 4) | – | **`IJobQueue`** port in `Ume.Rag.Domain/Ports`, Postgres implementation in `Ume.Rag.Infrastructure` (P0, sketch below); the Postgres implementation is plain SQL over the unpartitioned `Jobs`; enqueue takes the caller's transaction, so job creation stays atomic with domain writes (a broker would sit behind an outbox relay) |
| Dedup | batch enqueues of 500 rows | partial unique index on `DedupKey WHERE "State" IN ('queued','running')` (P0); replaces the advisory-lock dedup |
| Dequeue round trips | one statement per job and department probe | P2: **batched dequeue**: `LIMIT @n` = free slots the department may take (fairness share, engine §9.3), one statement for several jobs |
| Completion round trips | one `UPDATE` per job | P2: completions that are not part of a fenced effect transaction (e.g. fetch `unchanged`) are buffered ≤ 100 ms and written with one `UPDATE … FROM unnest(…)`; fenced completions stay in their transaction (engine §7.6) |
| `NOTIFY` serialises committing transactions on a database-wide lock (`async.c:905-922`, REL_17) | ≈ 85 notifying commits/s, each holding the lock through commit | P2: **debounce**: a process sends `rag_jobs` for a class at most once per 200 ms, and not at all when it runs that class itself and has a free slot (it wakes locally); polling (2 s) remains the safety net |
| LISTEN fan-out to many instances | 85 notifications/s × 6 instances = 500 wake-ups/s | P2: wake-ups are **coalesced** per class (≤ 1 probe round per 50 ms) and ignored when the instance has no free slot for the class (local check, no SQL) |
| Fairness across instances | per instance, approximate, from P0 with a `GROUP BY` department list per probe (engine §9.3) | stays; at 20+ departments the loose-index-scan department list (every 5 s) is shared per instance; per-department cap `max(1, ⌈slots ÷ departments⌉)` per instance |
| Per-process concurrency is a deployment knob (repo §3) | heterogeneous hosts need different values per instance | **`Worker:Concurrency:<class>`** (deployment, P0); `Settings.WorkerConcurrency` keeps run-time fleet values only (e.g. `departmentShare`); the fleet-wide embedding cap is infrastructure, so a deployment value (`Embedding:GlobalMaxInFlight`, §3.9) |
| Mixed versions during a rolling update (P2, several instances) | an old worker may dequeue a job enqueued by new code | payloads are additive (tolerant reader); a job that needs a newer build carries `MinBuild` in its payload and older instances requeue it without an attempt (`rag.jobs.requeued{reason=build}`) |
| Reclaims | an instance dies mid-job | a reclaim does not count as an attempt; a job reclaimed 3 times is dead-lettered as a poison suspect |
| Broker | – | not before the §6 trigger; `fetch` is the first candidate, but its rate is capped by politeness (≤ Σ host rates), so the queue is unlikely to bind first |

As built in P0 #20 (PR #20; settled at the integration sync point): the port lives in `Ume.Rag.Domain/Ports` and takes
`DbTransaction`, so the domain does not reference Npgsql; dequeue returns one job (the batched form is P2); fail,
release and cancel are separate members; the heartbeat renews each job's own lease. Wake-ups are not a queue member:
they live in Infrastructure (`JobWakeUps`, fed by local enqueues and by `rag_jobs` on the config monitor's listening
connection, data-model §4.3), so a broker brings its own.

```csharp
// Ume.Rag.Domain/Ports (Postgres implementation: Ume.Rag.Infrastructure/Jobs)
public interface IJobQueue
{
    Task<IReadOnlySet<Guid>> EnqueueAsync(IReadOnlyList<JobRequest> jobs, DbTransaction? tx, CancellationToken ct); // dedup, pg_notify
    Task<IReadOnlyList<ReadyDepartment>> ReadyDepartmentsAsync(string jobClass, CancellationToken ct);  // fairness (GROUP BY)
    Task<LeasedJob?> DequeueAsync(DequeueRequest request, CancellationToken ct);                         // class, dept: one job
    Task<IReadOnlySet<Guid>> HeartbeatAsync(Guid owner, IReadOnlyList<JobLease> running, CancellationToken ct); // lost = missing
    Task<bool> CompleteAsync(Guid jobId, Guid owner, string? resultJson, DbTransaction? tx, CancellationToken ct);
    Task<string?> FailAsync(Guid jobId, Guid owner, string error, bool fatal, TimeSpan? retryAfter, CancellationToken ct);
    Task<bool> ReleaseAsync(Guid jobId, Guid owner, string reason, TimeSpan delay, CancellationToken ct); // no attempt
    Task<int> CancelRunAsync(Guid runId, string reason, Guid? exceptJobId, CancellationToken ct);
}
```

```sql
-- Batched dequeue (P2; data-model §4.2 with LIMIT @n; Jobs unpartitioned)
WITH next AS (
    SELECT "Id" FROM content."Jobs"
    WHERE "State" = 'queued' AND "Class" = $1 AND "DepartmentId" = $2 AND "AvailableAt" <= now()
    ORDER BY "Priority" DESC, "AvailableAt"
    LIMIT $5
    FOR UPDATE SKIP LOCKED)
UPDATE content."Jobs" j
SET "State" = 'running', "LeaseOwner" = $3, "LeaseUntil" = now() + $4::interval, "Attempts" = j."Attempts" + 1
FROM next WHERE j."Id" = next."Id"
RETURNING j.*;
```

### 3.5 Leaders and background services across hosts

| Service (source) | Today | Failover | Decision |
|---|---|---|---|
| Frontier releaser per tier (connectors §6.3), scheduler (engine §9.6), reaper (engine §9.4), admin `OrganisationSync`, `GatewayModelSync`, P2 `DriftWatcher` (admin §2.2) | `pg_try_advisory_xact_lock` per tick (gateway `ModelListSync.cs:152`) | next tick (1 s – 5 min); no state held between ticks | **keep**; rule: a tick leader keeps no in-memory state that the next leader needs (the releaser counts `released` rows from `CrawlFrontier`, not memory) |
| Partition maintenance (`RunItems`), terminal-job deletion, content retention, nightly drift sweep (`Worker:Maintenance`, "exactly one instance") | config singleton | none: if that host is down nothing runs | pilot: singleton on the one host. P2: **lease leader**: `Worker:Maintenance=true` allowed on ≥ 2 instances; each task takes a row in `content."LeaderLeases"` (below), renewed every 10 s, lease 30 s → failover ≤ 30 s; a task resumes from its batch checkpoint |
| Admin retention steps (`Admin:Retention`) | config singleton | none | pilot: one slot. P2: each step batch is idempotent; run on **both slots** under `pg_try_advisory_xact_lock(<step>)` per batch, so the slots share or skip work; `Admin:Retention` stays as an off switch |
| `ConfigVersionMonitor` | every instance | – | unchanged; config lag per instance is a metric (repo §9); grace window `Config:MaxUnconfirmedSeconds` (90 s) |

```sql
CREATE TABLE content."LeaderLeases" (            -- P2; worker S I U
    "Name" varchar(50) PRIMARY KEY, "Owner" uuid, "Until" timestamptz NOT NULL, "Fence" bigint NOT NULL DEFAULT 0,
    "Checkpoint" jsonb);
UPDATE content."LeaderLeases"
   SET "Owner" = @me, "Until" = now() + interval '30 seconds',
       "Fence" = CASE WHEN "Owner" = @me THEN "Fence" ELSE "Fence" + 1 END
 WHERE "Name" = @task AND ("Owner" = @me OR "Until" < now())
RETURNING "Fence", "Checkpoint";                 -- no row: someone else leads; writes carry the fence
```

Pooler-safe (no session lock), visible in the admin's worker page, and the fence stops a paused former leader from
writing after it lost the lease.

### 3.6 Crawler frontier, releaser and host budgets across hosts

- **Shared state is already in Postgres** (frontier, claims, `CrawlHosts.NextAllowedAt` slot pacing): rates are exact
  across instances (connectors §6.3).
- **Connection cap**: with one `fetch` instance (pilot) the in-memory per-instance cap is exact. From the second
  instance (P2) a Redis sorted-set lease per host, copied from the gateway's in-flight limiter
  (`src/Ume.LlmGateway.Infrastructure/Stores/RedisStores.cs:36-38` acquire, `:78-79` release); lease 60 s (> fetch
  timeout), released in `finally`:

```lua
-- KEYS[1] = ume-rag:crawl:inflight:{hostMac}; ARGV: now ms, max, expiry ms, lease id, key TTL ms
redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[1])
if redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[2]) then return 0 end
redis.call('ZADD', KEYS[1], ARGV[3], ARGV[4])
redis.call('PEXPIRE', KEYS[1], ARGV[5])
return 1
```

  Refused → the job requeues at `now + interval` without an attempt (`rate_limited`). `hostMac` = HMAC of the host name
  with a worker secret `Security__CrawlKeyPepper` (no host names of possibly `sensitive` sources in Redis).
  **Redis down**: fall back to ⌈`MaxConnections` ÷ k⌉ per instance, k = live `WorkerInstances` running `fetch`
  (heartbeat < 45 s); slot pacing still bounds in-flight requests by Little's law.
- **Nav-link claim cache**: per run and instance, an LRU (100k entries) of URL hashes already admitted in this run;
  site-chrome links (header, footer, menus: most of a page's ≈ 100 links; on umea.se 4 `nav`s outside `main`) skip the
  claims upsert after the first page. Exactness is kept because the upsert remains the authority for the first
  sighting on each instance.
- **Releaser** stays one leader per tier (1 s tick); its query cost grows with hosts with pending entries, measured as
  `rag.crawl.releaser.duration`; trigger to shard by host hash across two leaders: p95 > 500 ms.
- **Crawl egress** (connectors §7): every host that runs `fetch` gets the same `crawl` network, firewall script and
  `UME_RAG_CRAWL_ALLOW`; the egress canary runs per instance.

### 3.7 Renderer and ClamAV

- **Renderer** (P2, only if a pilot-start check or a source shows that pages need JavaScript; umea.se is
  server-rendered): the worker fulfils every browser request itself (connectors §8.3), so a renderer must be
  **host-local to its fetch worker** (chatty WebSocket + per-request fulfilment). Scale = one renderer per fetch host,
  `cpus` 1–2, `Renderer:MaxPages` = 2 per CPU, ≈ 1–3 s per page (connectors §17). A per-host render lease (1, shared
  across instances via `CrawlHosts`) means one JavaScript-only site renders at ≈ 1 page/s regardless of renderers.
- **ClamAV**: clamd has no TLS or authentication on TCP 3310, so it stays **host-local** on the `ingestion` network of
  every host that runs `sync`/`fetch` (uploads are scanned by the `sync-upload` path). Memory 1.2–3 GB each
  (signatures, doubled during `ConcurrentDatabaseReload`, repo §5.5); each instance updates from database.clamav.net
  through its host's `mirror` network (decision: no mirror). Load is small (files only, §2.1).

### 3.8 Parser pool and OCR

| Topic | Decision |
|---|---|
| Every PDF | the worker parses only HTML, text, Markdown and CSV; every PDF goes to the parser, where `/v1/inspect` routes pages: pypdfium2 text-layer fast path, Docling layout, OCR. On umea.se PDFs are the larger half of the corpus (7,959 vs 6,159 pages), so parser throughput matters more than HTML extraction; fast-path throughput is measured in P1 (§11 #132) |
| Placement | A parser host runs `rag-parser` **and** a worker with `Worker:Classes=parse` on the host's internal `parser` network: the security property "parser has no other network" (repo §6.1) holds unchanged, no parser mTLS or discovery is needed, and blobs come from the object store. The ingestion-zone host keeps a small parser for `preview` and `/v1/inspect` |
| Capacity | each worker reads `capacity.convert_slots` from its local `/v1/info` (parser §10.1); `busy` requeues without an attempt (engine §7.3), so two worker instances can never wedge a parser |
| Optional multi-endpoint mode | `Parser:Endpoints` (deployment list) for Kubernetes or a shared parser pool: the client polls each `/v1/info` every 15 s, tracks its own in-flight per endpoint, picks the endpoint with most free slots, marks an endpoint full for 2 s on `busy`; cross-host parser traffic then needs mTLS (`PARSER_TLS_CLIENT_CA`, uvicorn client-certificate verification, *to verify*). Not used in the 3- and 5-host topologies |
| Slot layout | parser hosts use 2 × 2 or 4 × 2 slots (throughput, parser §14 expectation); `PARSER_WORKERS × PARSER_THREADS` = the container's `cpus` |
| Names (NER) | gated: called only when the result can change the flag (source of a `public` collection, not already flagged, ≥ 5 contact values); the `/v1/ner` call runs inside the document's processing, no `ner` job class and no lane sizing; the gated share *g* (§2.1) is a metric. NER is fail-closed per document (processing §9.6), so a slow check delays, never leaks |
| OCR CPU vs GPU | CPU (Tesseract `swe`, Docling layout on CPU) is enough for onboarding in nights (§2.1); a CUDA parser image (Docling models, NER on GPU) is a §6 trigger, not a P1/P2 item: image size and driver coupling are costly |
| Preview priority | the in-process preview gate (engine §9.5) applies per worker; previews run on the ingestion-zone host's parser, never queued behind a parser host's bulk OCR |

### 3.9 Embedding throughput through the gateway

The embedding model is the binding constraint for bulk ingestion and the main R1 risk (chat query embeddings share it).
Estimated BGE-M3 throughput (≈ 568M parameters ≈ 1.1 GFLOP per token; *estimates*, to-verify #98 measures them).
**Measured in P0** on the real gateway hardware (repo-and-deployment §11 #22, [weakness review §5](weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control)),
together with the condense and answer models' latency, and recorded against these estimates and the §2 chat table. If
capacity falls short, register M2 decides before P1 between more on-prem capacity and a longer initial-load window;
the embedding profile stays on-prem BGE-M3:

| Hardware at the gateway's provider | Tokens/s | Pages/h (3,600 tokens) |
|---|---|---|
| 8 CPU cores (ONNX/Ollama) | 0.5–1.5k | 0.5–1.5k |
| One L4/A10-class GPU | 8–20k | 8–20k |
| One A100/H100-class GPU | 30–80k | 30–80k |

| Topic | Decision |
|---|---|
| Fleet-wide cap | per-instance AIMD (engine §18); from P2 (several worker instances) a Redis lease set `ume-rag:embed:inflight` (same Lua as §3.6) limits in-flight embedding requests of **all** worker instances to `Embedding:GlobalMaxInFlight` (deployment; default 2 × provider replicas); Redis down → per-instance AIMD only |
| Gateway-side cap (defence) | gateway keys are issued per level and per deployable (simplification §6): the worker holds only its `OnPrem`-only and `Eu`-max keys, never the open key, so its keys are worker-only by construction (answered 8); their `VirtualKeys.MaxConcurrentRequests` (`src/Ume.LlmGateway.Domain/Entities/Entities.cs:108-110`, enforced with a 429 + `Retry-After` in `Pipeline/GatewayRequestHandler.Stages.cs:82-114`) protects chat even from a misconfigured worker fleet |
| Query latency | an ingestion batch of 64 × 450 tokens is ≈ 29k tokens, ≈ 1–3 s on an L4: a chat query queued behind a few batches misses its 60 ms budget although AIMD reacts to p95. A **query lane** (a separate gateway route/replica of the same weights for `x-ume-step: query-embed` (the knowledge API's step name, knowledge design §5.2), or a CPU replica: a 30-token query is cheap) comes only when the §6 trigger fires (answered 8); the canary (processing §11) then checks both routes against `EmbeddingProfiles.CanaryEmbedding` |
| Batch size at scale | `batchSize` 32 and ≤ 8,192 tokens per request when `Embedding:GlobalMaxInFlight` > 4 (shorter head-of-line blocking at equal throughput, *to measure*); 64 / 16,384 stays the single-host default |
| Back-pressure | 429 and capacity refusals requeue without an attempt (engine §7.3) and halve AIMD; `Retry-After` honoured |
| Cache hits | embedding cache by (text hash, profile) (processing §11): one embed per text for every source and department; P2 lookup via `Chunks` (§3.2) |
| Bulk loads | onboarding and publishes that re-process large collections run at `Priority` −5 inside the night window and are forecast in the admin from measured tokens/s (processing §14.7 forecast); a "bulk" run never takes more than `Settings.Ingestion.bulkEmbedShare` (0.75) of the global cap |

### 3.10 Index writer, evaluation

- **Index writer contention**: a new chunk set is written with `DeletedAt` set in short batches, then swapped in one
  short transaction per document (§3.1); contention is per document only (`ChunkSets` unique key). Run counters are derived
  (engine §10.4), so no hot run row. At bulk rates ≥ 2 writers run in parallel (separate `embed` jobs); HNSW concurrent
  inserts scale to a few writers (page locks), measured as `rag.index.write.duration` per chunk.
- **Grouped writes**: an `embed` job serving several small documents swaps each document separately (fence per
  document) but reuses one connection and one `NpgsqlBatch` for the fence + swap statements; COPY per batch.
- **Evaluation isolation**: `eval` (retrieval evaluation only; the answer-level judge and the P2 skill gate run in
  `rag-admin`) runs on at most two worker instances (`Worker:Classes`), concurrency 1 each; runs after
  each publish and nightly on the **full collection** (advisory, no gate), spread over the night window with the
  scheduler's jitter; retrieval uses the normal V variant (§3.3) and reads the standby when read routing is enabled;
  judge and generation calls go to on-prem models whose capacity is the gateway's.

### 3.11 Chat, knowledge and admin path

| Topic | Decision |
|---|---|
| Instances | pilot: one slot, updates in an announced maintenance window. From P2: 2 per data-plane host (a/b slots, rolling overlay), 2 hosts at 5-host scale; the municipal proxy load-balances all four with `proxy_buffering off`; CPU need is < 1 core at 20 msg/s (§2.2), so instances are for availability |
| Sticky sessions | **none needed**: config snapshot per instance (grace window `Config:MaxUnconfirmedSeconds` 90 s, then 503 for keyed requests), admin sessions in `config."AdminSessions"`, rate limits in Redis (P2; in process in the one-slot pilot), Data Protection keys in `config."DataProtectionKeys"`, signed conversation ids and download grants (chat §11.1, knowledge §4.6), MCP stateless (tools §7.1). Per-instance only: L1 query-embedding cache (from P2 the L2 in Redis shares hits), the writer queue (bounded loss, chat §12.4) |
| Connection pools | §3.1 (pool 12, `MaxConcurrentSearches` 4, measured in P0) |
| Writer across instances | `Seq` under the conversation row lock (chat §12.2) is correct across instances; totals upsert sorted and last (§3.2) |
| Read routing | §3.1, to the standby only by trigger |
| Tool hub (P2) | in-process in the chat role and `rag-admin` (`IToolHub`; never the worker) with bulkheads per server (concurrency, timeout, circuit); pooled MCP clients per instance; a separate `rag-tools` service only with the first external, OBO or write tool |
| Admin | one slot in the pilot, two from P2; background work per §3.5; previews and test panel use the same engines, bounded by their own rate limits |
| Open WebUI (P2: own VM) | defaults to SQLite and one worker (`backend/open_webui/env.py:271`, `:462`); title, tag and follow-up generation are off, so no task-model calls; several hundred concurrent users may saturate it: §6 trigger → its own Postgres (never `ragdb`), `UVICORN_WORKERS` > 1 and `WEBSOCKET_MANAGER=redis` (`env.py:476`) with its own Redis |

### 3.12 Redis

None in the single-host pilot (simplification pass §14); Redis arrives on host D with the first multi-host deployment
(P2), enabled by `ConnectionStrings:redis`. One instance stays enough at 10×: ≈ 2–3 commands per chat message (≈ 60/s),
crawl and embed leases (≈ 60–100/s); the gateway's own Redis used about half a command thread at 17,888 msg/s (`docs/performance.md:129-131`).
Memory: query embeddings 4 KB × entries of 5 min (≈ 6,000 at 20 msg/s ≈ 24 MB) + small keys → `mem_limit` 256 MB
stays. P2 keys (data-model §11): `ume-rag:crawl:inflight:{hostMac}` and `ume-rag:embed:inflight` (sorted sets, TTL =
lease). No persistence: a restart loses rate-limit windows (per-instance in-memory limits meanwhile), L2 cache and leases
(fallbacks above); admin sessions live in Postgres. Sentinel/replica only by trigger (availability requirement
or > 50 % of one thread).

### 3.13 Object store and `IBlobStore`

**Contract (P0, file system implementation):** blobs are **write-once**: a key is never overwritten (new content = new
key) and deletion is a tombstone row in the database (`content."BlobTombstones"`, P1) plus a delayed purge. S3 has no
rename, so the contract is S3-safe on both implementations. The interface is data-model §9's, as built in P0 #21
(PR #33; settled at the integration sync point):

```csharp
// Ume.Rag.Domain/Ports (file system implementation: Ume.Rag.Infrastructure/Blobs)
public interface IBlobStore
{
    // Write-once and atomic: visible only when complete (FS: staging file, fsync, POSIX link(2), which fails on an
    // existing key; S3: PUT with If-None-Match "*" or a completed multipart upload). An existing key is AlreadyExisted.
    Task<BlobWriteResult> PutAsync(BlobKey key, Stream content, long? length, CancellationToken ct);
    Task<Stream?> OpenReadAsync(BlobKey key, BlobRange? range, CancellationToken ct);  // null = absent; no suffix ranges
    Task<bool> ExistsAsync(BlobKey key, CancellationToken ct);
    Task<BlobWriteResult> PromoteAsync(BlobKey from, BlobKey to, CancellationToken ct); // tmp/ only, no overwrite
    Task DeleteAsync(BlobKey key, CancellationToken ct);           // the purge, idempotent; called by the purge sweep
    Task DeletePrefixAsync(BlobKey prefix, CancellationToken ct);  // tmp/ and previews/ sweeps only
    IAsyncEnumerable<BlobInfo> ListAsync(BlobKey prefix, CancellationToken ct); // those sweeps, weekly orphan audit only
}
```

- **Content-addressed writes**: raw content is keyed by content hash, so two writers of one key carry the same
  plaintext; the first complete write wins and the second sees "exists" (412 on S3) and reports success. Server support
  for `If-None-Match: *` is part of the contract test (§11 #114).
- **ClamAV path**: the worker streams the body to a **local** temp file (`/tmp` tmpfs or a scratch volume, ≤ 60 MB),
  scans it, then `PutAsync` to the final key; no `tmp/` object and no copy on S3. Uploads arrive at the admin and are
  already blobs (`uploads/…`), scanned by the worker reading them.
- **Multipart**: uploads (50 MB) and fetched files have a known stored length (encrypted: header + 64 KiB segments +
  16 B tag each, data-model §7), so a single PUT suffices (≤ 5 GB); multipart (8 MiB parts) only for unknown lengths
  > 64 MiB.
- **Listing**: no hot-path listing; orphan detection is DB-driven (cache expiry, tombstones, purges) plus a weekly
  `ListObjectsV2` audit (≈ 4–5M objects at 10M chunks → ≈ 5,000 list calls, fine weekly).
- **Client**: **`AWSSDK.S3`** (Apache-2.0; only dependency `AWSSDK.Core`), not `Minio` 7.0.0 (pulls `System.Reactive`,
  `CommunityToolkit.HighPerformance`, `System.Net.Http` 4.3.4). Since the SDK's flexible-checksum change the defaults
  are `RequestChecksumCalculation.WHEN_SUPPORTED` and `ResponseChecksumValidation.WHEN_SUPPORTED`
  (AWSSDK.Core 4.0.102.13 XML docs), which send trailing checksums with chunked payloads that some S3-compatible
  servers reject; we set both to `WHEN_REQUIRED` and `ForcePathStyle = true`, and a contract test runs every operation
  against the chosen server in CI (to verify: `UseChunkEncoding`, `DisablePayloadSigning` per server).

```csharp
var s3 = new AmazonS3Client(credentials, new AmazonS3Config
{
    ServiceURL = options.ServiceUrl, ForcePathStyle = true,
    RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
    ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
    Timeout = TimeSpan.FromSeconds(30), MaxErrorRetry = 2,
});
```

- **Settings**: `BlobStore:Kind` (`FileSystem` | `S3`), `BlobStore:S3:ServiceUrl`, `BlobStore:S3:Bucket` (one bucket
  per deployment; `sensitive` would be a separate deployment), secrets `BlobStore__S3__AccessKey`/`SecretKey` **per
  service** with bucket policies mirroring today's mounts: worker read/write, admin write on `uploads/` and read, api
  read-only (P2: write on `chat-tmp/`).
- **Encryption**: `internal` blobs are envelope-encrypted, `public` blobs are not (encrypted disk only); no SSE/SSE-C
  (no KMS dependency; disks of the object store host are LUKS-encrypted like `postgres-data`).
- **Backups**: uploads (the only content that cannot be fetched again) are copied to the backup target within 15
  minutes; derived blobs (raw fetches, parse outputs) are re-creatable and backed up nightly or not at all. On S3:
  bucket replication or `rclone`/server tooling.
- **Latency**: a GET on the LAN costs ≈ 5–20 ms (*estimate*), negligible next to parse (≥ 1 s) and preview; download
  grants stream through the api with range requests. No decrypted caches on disk; within a job outputs are passed in
  memory.
- **Server** (answered 5): MinIO's repository is **archived** ("THIS REPOSITORY IS NO LONGER MAINTAINED", community
  edition "distributed as source code only", last release `RELEASE.2025-10-15T17-29-55Z`, `minio/minio` `README.md`;
  anonymous pulls of `minio/minio` refused by the registry on 2026-10-30), AGPL-3.0, and its successor "AIStor Free"
  has its own licence; an **unmaintained** storage server holding every document is the real risk. Decided: the
  municipality's existing S3-capable storage if IT runs one, else **SeaweedFS** (Apache-2.0, active, image
  `chrislusf/seaweedfs` 4.51). Single node with a separate encrypted disk at 3–5 hosts; distributed only by trigger.

### 3.14 Observability for capacity and autoscaling signals

New metrics (meter `Ume.Rag`, low-cardinality; repo §9 conventions):

| Metric | Type | Dimensions | Saturation signal for |
|---|---|---|---|
| `rag.jobs.age` p95 (existing) | histogram | class | **the** queue signal: per-class lag; later the KEDA/autoscaling input |
| `rag.jobs.slots.busy`, `.slots.total` | gauge | class, instance role | per-class saturation per instance |
| `rag.crawl.host_budget.utilisation` | gauge | host kind (`municipal`/`external`) | used vs allowed slots; politeness-bound when ≈ 1 |
| `rag.crawl.inflight_refusals`, `rag.embed.global_refusals` (P2) | counter | – | Redis lease saturation |
| `rag.embed.tokens_per_second`, `rag.embed.queue_wait` | gauge, histogram | lane (`ingest`/`query`) | BGE-M3 capacity, head-of-line blocking |
| `rag.parser.slots_busy` / `slots_ready`, `rag.parser.pages` | gauge, counter | host; route (`text_layer`/`layout`/`ocr`) | parser saturation; PDF route mix for the capacity model |
| `rag.pii.name_checks` | counter | outcome (`gated`/`skipped`) | gated share *g* (§2.1) |
| `rag.db.connections` per role vs limit, `rag.db.pool.wait` | gauge, histogram | role | budget (§3.1) |
| `rag.db.hnsw.bytes`, `rag.db.hnsw.cache_hit` | gauge | profile | O1 trigger (a) |
| `rag.db.vacuum.duration`, `rag.db.dead_tuples` | histogram, gauge | table | §3.2 triggers |
| `rag.db.wal.bytes_per_second`, `rag.db.archive.lag`, `rag.db.replication.lag` | gauge | – | write load, RPO (archive), standby |
| `rag.blob.duration` | histogram | op, store kind | object store latency |
| `rag.leader.changes` (P2) | counter | task | §3.5 failovers |

Postgres-side metrics come from the OpenTelemetry Collector's `postgresql` receiver (collector already in
`deploy/otel/`; receiver metric set *to verify*; with the `ume_rag_monitor` role from P2), `pg_stat_archiver` for the
archive lag, plus `pg_stat_statements` (needs `shared_preload_libraries`, set on the stock image's server command;
repo §6.2 change). Dashboards: a *Capacity* board (per-stage saturation, queue age per class, host budget utilisation,
embedding tokens/s vs cap, HNSW memory, connections vs budget, archive lag).

## 4. In-process parallelism

| Area | Rule |
|---|---|
| I/O | async end to end; no `.Result`/`.Wait()`; one `CancellationToken` per job linked to the lease (engine §9.4) |
| Dispatcher | one loop per instance; per class a `SemaphoreSlim(Worker:Concurrency:<class>)`; jobs run as tasks, not threads; wake-ups via a coalescing `Channel<string>` (capacity 1 per class, `DropWrite`) |
| CPU-bound work | AngleSharp/SmartReader with the site profile, `SwedishDecompounder`, tokenizer, SHA-256 (`IncrementalHash`), AES-GCM segments, JSON of large outputs run in a **CPU lane**: a bounded `Channel<CpuWork>` with `ProcessorCount` consumers, so long CPU work never occupies all thread-pool threads that I/O continuations need; each item checks cancellation between pages/segments |
| Thread safety | `AesGcm` instances per lane consumer (not shared); `Tokenizer` shared only if verified thread-safe (to-verify #26), else one per consumer; compiled regexes and the decompounder's word list shared (read-only) |
| Pipelines within a job | fetch → hash → links → store as a streaming pipeline (no full-body buffers beyond 60 MB; `ArrayPool` as the gateway, `src/Ume.LlmGateway.Infrastructure/Providers/ProviderJson.cs:264-286`) |
| Batching | claims upsert per page (`unnest`), run items in batches, embeddings ≤ 64 (32 at scale) inputs, chunk writes ≤ 1,000 per batch, completions buffered from P2 (§3.4), stats flush 30 s |
| Npgsql | one `NpgsqlDataSource` with `UseVector()`; `NpgsqlBatch` for multi-statement round trips (to-verify #19, #20); **binary COPY** for chunks (below); `Max Auto Prepare` 64; `Multiplexing` not used (complexity; revisit only with a trigger) |
| HTTP | `IHttpClientFactory` with one `SocketsHttpHandler` per downstream: gateway (`PooledConnectionLifetime` 5 min as `ProviderHttpClient.cs:50-56`, `MaxConnectionsPerServer` = `Embedding:GlobalMaxInFlight` share + query lane), parser (`MaxConnectionsPerServer` = slots + 2, long timeouts), crawl (egress-guard `ConnectCallback`, `MaxConnectionsPerServer` 4) |
| GC | Server GC (Web SDK default) with DATAS (default since .NET 9, *to verify* for .NET 10); `DOTNET_GCHeapHardLimitPercent=0x4B` on **every** .NET container (today only api, repo §6.2); the gateway's memory study (`docs/performance-improvement-plan.md:72-81`) decides further knobs for both repos |

```csharp
// Index writer: chunks with binary COPY; Pgvector 0.3.2 maps Vector → "vector" and HalfVector → "halfvec" by default
// (VectorTypeInfoResolverFactory.cs:22-29), so no NpgsqlDbType is needed; pattern as the gateway's
// UsageRecordCopy.cs:39 and pgvector-dotnet examples/Loading/Program.cs:37-51. SearchText = text + decompounded parts;
// SearchVector is generated from it (built-in swedish), not copied.
await using var copy = await conn.BeginBinaryImportAsync(
    """COPY content."Chunks" ("Id","ChunkSetId","DocumentId","CollectionId","Classification","Kind","Ordinal",
       "HeadingPath","Text","SearchText","OverlapChars","TextHash","TokenCount","Page","Tags","DocumentType",
       "SourceModifiedAt","EmbeddingProfileId","DeletedAt","Embedding") FROM STDIN (FORMAT BINARY)""", ct);
foreach (var c in batch)                                   // ≤ 1,000 rows per COPY and short transaction; DeletedAt set
{
    await copy.StartRowAsync(ct);
    await copy.WriteAsync(c.Id, NpgsqlDbType.Uuid, ct);
    // … remaining columns …
    await copy.WriteAsync(new Vector(c.Embedding), ct);   // ReadOnlyMemory<float>; column stays vector (§3.3)
}
await copy.CompleteAsync(ct);
```

## 5. Topologies

### 5.1 One host (pilot)

repo §6.2: 8 vCPU / 24 GB, zones by `cpu_shares` and the ingestion slice, file-system blobs, no pooler, one worker
with all classes, **one slot** per service (updates in an announced maintenance window), six long-running containers
(no Redis: rate limits and the query-embedding cache in process, admin sessions in Postgres), the pgvector Postgres
image with pgBackRest inside, WAL archiving to the backup target and backups from a host timer (RPO ≤ 5 min, RTO ≤ 4 h
by point-in-time restore). P0/P1 add only seams
(§8).

### 5.2 Three hosts (P2, first multi-host deployment)

| Host | Size (estimate) | Runs | Networks |
|---|---|---|---|
| **D** data | 8–16 vCPU, 64 GB, NVMe | postgres, redis (enabled by `ConnectionStrings:redis`), object store, OTel collector | LAN: 5432, 6379 (TLS), 443 S3 (TLS) to listed peer IPs only |
| **A** data plane + control | 8 vCPU, 16 GB | rag-api a/b (in-process tool hub), rag-admin a/b; Open WebUI on its own VM (decided) | `edge`, `llm`; LAN to D |
| **I** ingestion | 16 vCPU, 32 GB | rag-worker (all classes), rag-parser (2 × 2 or 4 × 2 slots), rag-clamav; rag-renderer only if a source needs JavaScript | `ingestion`, `parser`, `crawl`, `mirror`, `llm`; LAN to D |

The Postgres streaming standby required before production beyond the pilot needs a host of the primary's memory class;
its placement is decided with register D1.

### 5.3 Five hosts

| Host | Runs | Classes / roles |
|---|---|---|
| **D** | as above; standby per D1 | – |
| **A1**, **A2** | rag-api a (A1) / b (A2) ×2 each, rag-admin a (A1) / b (A2) | `Rag__Roles=knowledge,chat[,mcp]`; tool hub in-process |
| **F** fetch | worker `preview,command,sync,fetch,eval`; clamav; small parser (inspect, preview); renderer if needed | crawl egress host |
| **P** processing | worker `parse,embed`; parser 4 × 2 slots (16–24 vCPU, 48 GB) | no crawl network |

A sixth host is a second **P** (same compose, more parser), which is how ingestion scales beyond 10×.

### 5.4 Cross-host rules

| Topic | Decision |
|---|---|
| Networks | **routed LAN + host firewall + TLS**, no Docker overlay (Swarm adds an orchestrator, VXLAN is unencrypted by default; Kubernetes later brings NetworkPolicies, repo §10). Each host's compose publishes only the ports its peers need on its LAN address; `deploy/host/docker-user-firewall.sh` gains a peer list per role; Postgres `pg_hba.conf` lists each peer host per role (repo §6.4 pattern) |
| Host-local only | parser (on F and P with their own worker), renderer (if deployed), clamd: never published on the LAN |
| Compose files | `compose.data.yaml`, `compose.api.yaml`, `compose.admin.yaml`, `compose.ingestion.yaml` (exists), `compose.parser.yaml`; `init-deployment.sh --role data|api|ingestion|processing|all` (extends repo §6.4) writes that host's `.env`, certificates and **only its secrets** |
| Secrets distribution | generated once on the operator's machine (as today), copied per role with the operator's existing tooling; master key files reach more hosts (api: chat + read-only blob keys; worker hosts: blob keys), which raises the stakes of postponed decision **K1**: decide it before the first multi-host deployment (§9); long-lived keys stay escrowed offline (simplification §4) |
| Must be single | Postgres primary (+ standby), Redis, object store (single node), migrations (one-shot), lease leaders (§3.5) |
| Failure domains | D down = failover to the standby (D1), else point-in-time restore from pgBackRest (RPO ≤ 5 min, RTO ≤ 4 h); A1 or A2 down = chat continues on the other; F down = crawls pause (leases reaped in 45 s), uploads still accepted; P down = parse/embed queue up, nothing lost |
| Rolling updates | `rolling-update.sh --role` per host: extensions + migrations once (from D's operator shell) → A1 then A2 (each a/b, repo §7.3) → admin → F → P (worker stop-then-start per host; parser recreated); mixed versions are safe because migrations are expand/contract and jobs carry `MinBuild` (§3.4) |
| Kubernetes | optional later, the gateway path (`docs/kubernetes.md:112-118`: stateless replicas, PDBs); Compose roles map to Deployments per class group, `rag.jobs.age` to the autoscaler; PgBouncer becomes required there (replica counts vary) |

## 6. Scaling triggers

| Metric (measured how) | Threshold | Action |
|---|---|---|
| Σ backends or backend memory (`pg_stat_activity`) | the connection budget exceeds `max_connections` at the planned instance count, backend memory > 15 % of RAM, or Kubernetes autoscaling | PgBouncer transaction mode for api + worker (§3.1) |
| Primary CPU in office hours with retrieval share (`pg_stat_statements`) | > 70 % with retrieval > 50 % of it, or retrieval p95 > 250 ms during ingestion | route K, V, H+D and the evaluator to the standby |
| HNSW size vs RAM (`rag.db.hnsw.bytes`) | > 50 % of the DB host's RAM | binary-quantized index + re-rank first; then O1 |
| Retrieval latency (`rag.retrieval.stage.duration`) | V p95 > 50 ms or retrieval p95 > 300 ms after tuning | O1 evaluation (`Retrieval:Index=shadow`) |
| Filtered recall (scale test, shadow HNSW vs exact) | recall@100 < 0.95 for collections in the 0.05–2 % selectivity band | O1 (efficient filtered kNN) or per-collection partial indexes |
| Queue (`pg_stat_statements` on `Jobs`, NOTIFY lock waits) | > 1,500 queue statements/s sustained, or `Jobs` > 30 % of primary CPU, or lock waits on database 0 > 5 % of commits | move `fetch` (then `embed`) to a broker behind `IJobQueue` (outbox relay) |
| `Jobs` bloat (dead tuples in `IX_Jobs_Ready`, dequeue p95) | dequeue p95 rising after the nightly delete despite autovacuum (threshold set from P1 measurements) | day partitions for `Jobs` again (drop instead of delete) |
| Parse backlog (`rag.jobs.age{class=parse}`) | p95 > 12 h for a week, or OCR onboarding not done in 3 nights | second parser host (P-type), then CUDA parser image |
| Embedding (`rag.embed.queue_wait`, gateway 429s) | ingest queue wait p95 > 5 s at the global cap, or onboarding > 1 week | more BGE-M3 replicas at the gateway (gateway team); query lane if not yet |
| Query embedding p95 during ingestion | > 2 × idle | query lane (answered 8) |
| `EmbeddingCache` size | > 20 % of database size | `Chunks` lookup first (§3.2) |
| Hosts / instances | > 5 hosts, or any role needs automatic scaling, or > 3 rolling slots | Kubernetes (repo §10) |
| Redis | > 50 % of one command thread, or an availability requirement | Redis replica + Sentinel |
| Object store | > 2 TB or > 50M objects, or single-disk throughput saturated | distributed object store |
| Open WebUI | CPU > 80 % of its VM or p95 overhead > 100 ms at peak | its own Postgres + workers + Redis (§3.11) |

## 7. Load and scale tests

**R1 release test** (`perf.yml`, from P1, every release, pilot-shaped host; repo §8.3). **Scale test** `scale.yml` (P2;
manual dispatch on 3 test hosts, before the multi-host rollout and after changes to the §3 decisions; not per release):

| Component | Design |
|---|---|
| Synthetic site `tests/scale/Ume.Rag.FakeSites` | one .NET process serving N virtual hosts (default 20 municipal-like + 50 external-like) with deterministic pages from a seed (Swedish text from `tests/fixtures`, 100 links each with site chrome), sitemaps with `lastmod` on every URL (as umea.se), hosts with and without `ETag`/304 (umea.se sends none), roughly as many PDFs as pages (as umea.se) from the fixture corpus, configurable change rate (10 %), latency distribution, JS-only pages (5 %, renderer if deployed), 429 injection; a `--hosts`/`--pages` scale knob |
| FakeGateway throughput model | `Embed:TokensPerSecond` (token bucket emulating a GPU), `Embed:Replicas`, `Embed:MaxQueue` (429 + `Retry-After` beyond), `Embed:QueryLane` (separate bucket); chat latency profile as today (`FirstTokenMs` 400, `TokenIntervalMs` 25, `JitterPercent` 30, gateway `benchmarks/compare`) |
| Chunk seed `tests/scale/Ume.Rag.ScaleSeed` | binary-COPY loader for 1M / 5M / 10M chunks into one profile: texts from the fixture corpus, vectors = real BGE-M3 embeddings of ≈ 50k fixture chunks + small Gaussian noise, renormalised (random vectors give a pessimistic graph); 20 departments, collections at 0.01–20 % selectivity, the same texts in several departments' sources (per-source chunk sets) |
| Chat load | k6 from the gateway's `benchmarks/compare/k6`: 300 VUs, think time 60–90 s, bursts to 20 msg/s; replicas behind nginx as the gateway's horizontal-scaling item (`docs/performance-improvement-plan.md:83-92`) |
| Reports | Markdown as `latency_report.py`; harness check table (fake gateway, FakeSites, k6 not saturated) as the gateway does (`docs/performance.md:112-113`) |

| Decision | Proving metric | Pass |
|---|---|---|
| halfvec HNSW (§3.3) | `pg_relation_size` per variant at 1M/5M/10M; recall@10 HNSW vs exact on the evaluation sets; V p95 | ≤ 3 KB/element; recall delta ≤ 0.5 points; V p95 ≤ 25 ms at 10M warm |
| Selectivity rule (§3.3) | recall@100 and latency per selectivity band | recall ≥ 0.95, p95 ≤ 150 ms in every band |
| Connection budget (§3.1) | `rag.db.connections` per role, pool waits, Postgres backend RSS | no refusals; backend memory within the container limit |
| Queue (§3.4) | queue statements/s, NOTIFY lock waits (`pg_locks` object 0), dequeue p95 | ≥ 3× the 10× rate without lock waits > 1 % |
| Crawl budgets (§3.6) | max in-flight per host (FakeSites counter) with 3 fetch instances; `rag.crawl.host_budget.utilisation` | never > `MaxConnections`; utilisation ≈ 1 on the busiest host |
| Embedding caps (§3.9) | in-flight at FakeGateway ≤ `GlobalMaxInFlight`; query embedding p95 with and without the query lane | query p95 ≤ 2 × idle with the lane |
| Leaders (§3.5) | kill the leader's host: time to the next maintenance/releaser action | ≤ 30 s (lease), ≤ 1 tick (xact) |
| Topology (§5) | pages/h checked and changed; chat p95 TTFT idle vs during a 100k-page crawl + OCR batch | ≥ 100k checked/h with ≥ 3 hosts at budget; chat p95 ≤ 1.10 × idle (R1 rule at scale) |
| Blobs (§3.13) | `rag.blob.duration` p95 per op; contract test against the chosen server | GET ≤ 20 ms, PUT 1 MB ≤ 50 ms on the LAN |

## 8. Phasing

| Phase | Item | Why now |
|---|---|---|
| **P0** | on-prem capacity on the real gateway hardware: BGE-M3 throughput, condense and answer model latency (checklist #22, weakness review §5; register M2 if short); write-once `IBlobStore` contract (FS impl); `IJobQueue` seam (Postgres impl) with the partial unique dedup index on unpartitioned `Jobs`; pooler-safe rules + SQL scan test; `ConnectionStrings__ragdb-listen`; `Worker:Concurrency:<class>` (deployment) and `Worker:Classes` per instance; pool starting values (api 12 with `MaxConcurrentSearches` 4, admin 5, worker 10, `max_connections` 100) and the Postgres memory limit measured; pgvector image with pgBackRest inside (`ume-rag/postgres`) and a point-in-time restore drill; `DOTNET_GCHeapHardLimitPercent` on every .NET service; halfvec index expression in the first migration | seams cost a day now and a migration later; restore works before data exists |
| **P1** | `S3BlobStore` (AWSSDK.S3) + contract tests in CI against the chosen server; chunk writes in short batches + swap + orphan sweep; sorted upserts and totals-last; selectivity rule for V; incremental `lastmod` runs as the template default; capacity metrics and dashboard; FakeGateway throughput model; `perf.yml` (R1) | cheap and single-host-safe |
| **P2** | from weakness review §1: shared crawl frontier with claims, releaser and the nav-link claim cache, AIMD embedding concurrency; first multi-host deployment: 3-host topology (D, A, I) with routed LAN, compose split, `init-deployment --role`; a/b slots; `content."LeaderLeases"`; connection-budget script; `ume_rag_monitor`; batched dequeue/completion; wake-up coalescing and NOTIFY debounce; Redis (enabled by `ConnectionStrings:redis`: shared rate limits, L2 query-embedding cache, in-flight leases for crawl hosts and embedding); `MinBuild` on job payloads; object store server; Postgres streaming standby before production beyond the pilot (D1); `EmbeddingCache` via `Chunks`; scale test (`FakeSites`, `ScaleSeed`) before the rollout; renderer if a source needs it; K1 decided before going multi-host | departments 2–20 arrive (README P2); each removes a known multi-instance defect |
| **P3** | 5-host topology; PgBouncer, read routing to the standby, broker, binary-quantized index, OpenSearch (O1), CUDA parser, Kubernetes, Redis HA, distributed object store, query lane — **each only when its §6 trigger fires** | measured, not planned |

## 9. Changes to other documents

Applied to the other documents on 2026-10-30 and carried into the simplification pass rewrite; the table lists where
each decision lives.

| File | Section | Change |
|---|---|---|
| `../01-general-idea.md` | §10 Decisions | Row "Performance pass (2026-10-30)": the answered decisions of §10.1 and a pointer to this document |
| `platform/data-model.md` | §1.1 | Pools and role limits of §3.1 (pilot 35 of 100); role `ume_rag_monitor` (`pg_monitor`, limit 2) from P2; budget rule; worker limit a sum over instances |
| `platform/data-model.md` | §2.2 | `CrawlHosts`, `CrawlRuns`: `fillfactor = 50`; P2 table `LeaderLeases` (§3.5); P2 index `IX_Chunks_Profile_TextHash` with the embedding-cache lookup via `Chunks` (§3.2) |
| `platform/data-model.md` | §3.2, §12.16 | HNSW index on `("Embedding"::halfvec(1024)) halfvec_cosine_ops` (column stays `vector`) from P1, measured with evaluation sets |
| `platform/data-model.md` | §4.2, §4.4 | Per-instance defaults are `Worker:Concurrency:<class>` (deployment); `Settings.WorkerConcurrency` holds fleet-wide shares only; `Jobs` unpartitioned with the partial unique dedup index; batched dequeue (`LIMIT $5`) in P2 |
| `platform/data-model.md` | §4.3 | P2: NOTIFY debounce (≤ 1 per class per 200 ms per process, none when handled locally) with the `async.c` commit-lock reason; LISTEN on `ConnectionStrings__ragdb-listen` |
| `platform/data-model.md` | §9 | Write-once `IBlobStore` contract of §3.13; one S3 bucket per deployment and per-service access keys |
| `platform/data-model.md` | §11 | P2 keys `ume-rag:crawl:inflight:{hostMac}`, `ume-rag:embed:inflight` (sorted sets, TTL = lease) |
| `platform/repo-and-deployment.md` | §2.2 | `AWSSDK.S3` (≥ 7 days old at pin time; 4.0.104.6 checked here) in `Directory.Packages.props` for `Ume.Rag.Infrastructure` |
| `platform/repo-and-deployment.md` | §3 | `Worker:Concurrency:<class>`; P2: `Worker:Maintenance` on several instances (lease leaders), `Admin:Retention` on both slots (lock-guarded batches); `BlobStore:*`, `Embedding:GlobalMaxInFlight`, `Parser:Endpoints` (optional), `ConnectionStrings__ragdb-listen`, `ConnectionStrings__ragdb-read` (by trigger) |
| `platform/repo-and-deployment.md` | §6.1–§6.4 | P2 multi-host: routed LAN + peer firewall lists, compose split per role, `init-deployment.sh --role data|api|ingestion|processing|all`, host-local parser/renderer/clamd (§5.4); object store replaces the `rag-blobs` volume beyond one host |
| `platform/repo-and-deployment.md` | §6.2 | `DOTNET_GCHeapHardLimitPercent=0x4B` on worker, admin, migrations; `shared_preload_libraries = 'pg_stat_statements'`; 64 GB database-host settings (§3.2) as the multi-host profile; `wal_compression = lz4` if the stock image supports it |
| `platform/repo-and-deployment.md` | §6.3 | Secrets `BlobStore__S3__AccessKey`/`SecretKey` per service; `Security__CrawlKeyPepper` (worker, P2) |
| `platform/repo-and-deployment.md` | §7.3 | Per-host rolling order (§5.4, P2); one slot with a maintenance window in the pilot |
| `platform/repo-and-deployment.md` | §8 | `perf.yml` (P1); `scale.yml` (manual) and `tests/scale/` (`FakeSites`, `ScaleSeed`) in P2 |
| `platform/repo-and-deployment.md` | §9 | Capacity metrics and dashboard of §3.14; OTel `postgresql` receiver; archive lag |
| `platform/repo-and-deployment.md` | §11 | P0 items of §8 (seams, pool values, GC env, backups) |
| `platform/plan.md` | §2 | Production placement per §5 (3- and 5-host tables); R1 rule extended to the scale test |
| `ingestion-worker/design-engine.md` | §7.6 | Blob writes via write-once `IBlobStore.PutAsync` (existing key = success) |
| `ingestion-worker/design-engine.md` | §9.1, §9.3 | `Worker:Concurrency:<class>`; `IJobQueue`; P2: batched dequeue, coalesced wake-ups, `MinBuild` |
| `ingestion-worker/design-engine.md` | §2.3, §9.4 | P2: drift sweep, partition maintenance and content retention as lease leaders (§3.5) |
| `ingestion-worker/design-engine.md` | §14, §18 | Chunk sets written with `DeletedAt` set in batches of ≤ 1,000, then a short swap per document; ≥ 2 parallel writers at bulk rates; 10× capacity table pointer (§2) |
| `ingestion-worker/design-connectors.md` | §5.4 | `incrementalSchedule` default for hosts whose sitemaps carry `lastmod` |
| `ingestion-worker/design-connectors.md` | §6.2, §6.3 | Nav-link claim cache; P2: connection cap via Redis in-flight lease with ⌈cap ÷ k⌉ fallback |
| `ingestion-worker/design-connectors.md` | §8.2, §11 | Renderer (P2) and clamd host-local per fetch host; ClamAV scans a local temp file, then one `PutAsync` |
| `ingestion-worker/design-processing.md` | §11 | Global embedding cap (P2); batch 32 / 8,192 at scale; bulk share; query lane by trigger |
| `parser/design.md` | §13.1 | Optional `PARSER_TLS_CLIENT_CA` for the multi-endpoint mode |
| `knowledge-api/design.md` | §6.2, §6.4 | V on `"Embedding"::halfvec(1024)`; selectivity rule; `hnsw.max_scan_tuples` 100,000 as a role default (no per-query `SET LOCAL`); `minHnswSelectivity` in `Settings.Retrieval` |
| `knowledge-api/design.md` | §6.6 | `MaxConcurrentSearches` 4 = ⌊(pool − 3) ÷ 2⌋ with pool 12, validated at start, measured in P0; optional `ConnectionStrings__ragdb-read` |
| `knowledge-api/design.md` | §13 | O1 triggers (§3.3: HNSW memory, V/retrieval latency, filtered recall, recall/facets) |
| `chat-api/design.md` | §12.2 | Sorted keys; `AssistantDailyTotals` upsert last in the batch |
| `admin/design.md` | §2.2 | P2: `RetentionService` on both slots with lock-guarded idempotent batches |
| `postponed-decisions.md` | O1, K1, S2, D1 | O1 trigger text as §3.3; K1 "decide by" includes "before the first multi-host deployment" (§5.4); S2 = object store server; D1 = standby |
| `to-verify.md` | rows 114–126, 130, 132, 140 | §11 items (one row each), phase and method as listed there |
| `architecture.md` | §1 | Optional: a multi-host view (D, A1/A2, F, P) after review |

## 10. Questions

### 10.1 Answered (user, 2026-10-30)

1. **Scale target**: ≈ 10× the pilot on a few hosts: ~100,000 pages/hour of ingestion, ~5–10M chunks, 20+
   departments, several hundred concurrent chat users, over 3–5 hosts; beyond that only behind measured triggers.
   (Interpreted in §2 as pages checked per hour, aggregate over hosts; downstream sized for changed pages.)
2. **Scale-out = Compose per host, roles per host**: same images; each host runs a chosen set of roles via
   `Worker:Classes` and env; all hosts share one Postgres, Redis and object store; Kubernetes stays optional later.
3. **Blob storage for scale-out = S3-compatible object store** behind `IBlobStore` (MinIO on-prem; Azure Blob via its
   own provider if ever on Azure); file system stays for dev and the single-host pilot; envelope encryption unchanged.
4. **Job queue stays Postgres**, hardened (batching, partitioning, indexes) with an `IJobQueue` seam and measured
   triggers for moving hot classes (e.g. `fetch`) to a broker later.
5. **Object store server** (Q1): the municipality's existing S3-capable storage if IT offers one, otherwise
   **SeaweedFS** (Apache-2.0); never MinIO built from archived source. Decided with IT before the first multi-host
   deployment (P2); register row in `postponed-decisions.md`.
6. **Vectors** (Q2): HNSW on `("Embedding"::halfvec(1024))` from P1 (column stays `vector`); recall checked with the
   evaluation sets before the first large collection; O1 trigger restated as measured HNSW memory, latency and
   filtered recall instead of "> 2–3M chunks".
7. **Crawl rates** (Q3): the crawler rules stay (≤ 10 req/s per host, night window); higher rates for allow-listed
   municipal hosts and a daytime trickle become per-host settings, off by default, enabled only with the web team's
   written consent.
8. **Embedding contention** (Q4): propose to the gateway team now a worker-only platform key with the gateway's
   existing `MaxConcurrentRequests` cap; a separate BGE-M3 query route/replica only when the §6 trigger fires.

Later answers superseded parts of 3 (MinIO → 5), 4 (`Jobs` unpartitioned, simplification §9) and 8 (per-level keys
per deployable, simplification §6).

### 10.2 Open (user)

None.

## 11. To verify

| # | Item | Phase | How |
|---|---|---|---|
| 114 | S3 server choice passes the `IBlobStore` contract test with `AWSSDK.S3` (`WHEN_REQUIRED` checksums, path style, `UseChunkEncoding`/`DisablePayloadSigning`, `If-None-Match: *`, range GET, multipart) | P1 | test |
| 115 | AWSSDK.S3 version at pin time (7-day rule; 4.0.104.6 read here) and its `AWSSDK.Core` | P1 | source |
| 116 | Licence terms of the chosen object store for the municipality (postponed P2) | P2 | ask (legal) |
| 117 | HNSW element size on disk for `vector(1024)` vs `halfvec(1024)` vs `bit(1024)` (`pg_relation_size`) and recall delta of halfvec on the evaluation sets | P1 | measure |
| 118 | Filtered HNSW recall and latency by selectivity band at 1M/5M/10M with `relaxed_order` and the role default `max_scan_tuples` 100,000 | P2 (scale test) | measure |
| 119 | Npgsql binary COPY into `Chunks` with `Vector` and the generated `SearchVector`; HNSW insert cost at 10M (extends #21) | P1/P2 | test + measure |
| 120 | `NOTIFY` lock waits at 85 notifying commits/s with and without debounce | P2 (scale test) | measure |
| 121 | PgBouncer (1.24.1 trixie / 1.26 PGDG) with Npgsql 10 auto-prepare, `No Reset On Close`, TLS both sides, SCRAM `auth_query` | P3 (by trigger) | test |
| 122 | `wal_compression = lz4` available in the stock pgvector image | P0 | test |
| 123 | OTel Collector `postgresql` receiver metric set (connections per role, dead tuples, index sizes) | P1 | source + test |
| 124 | .NET 10 GC defaults in containers (DATAS on with Server GC; `GCHeapHardLimitPercent`) | P0 | source + test |
| 125 | BGE-M3 tokens/s per GPU class and CPU at the gateway's provider; head-of-line blocking of a query behind 64- vs 32-input batches (extends #98) | P0 (checklist #22, weakness review §5) | measure |
| 126 | uvicorn client-certificate verification for the optional multi-endpoint parser mode | P3 | test |
| 130 | Managed Postgres (Azure) with pgvector ≥ 0.8 and the §3.2 settings | P2 | source |
| 132 | pypdfium2 fast-path throughput in the parser (pages/s per slot) and the PDF route mix on umea.se | P1 | measure |
| 140 | `rag-api` pool 12 with `MaxConcurrentSearches` 4 = ⌊(pool − 3) ÷ 2⌋ (≤ 2 connections per search): pool waits at 20 msg/s | P0 | measure |
