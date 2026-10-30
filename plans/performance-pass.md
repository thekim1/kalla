# Performance and scalability pass

Cross-cutting pass over the Ume.Rag design (after the consistency pass, [`consistency-pass.md`](consistency-pass.md)):
good parallelism now, and the ability to scale the crawler, parser, embedding and data plane out over several hosts
later. Binding inputs: the Decisions table in [`../01-general-idea.md`](../01-general-idea.md) §10, the user's four
answers of 2026-10-30 (§10.1 here), resource zones and the R1 release test of
[`platform/plan.md`](platform/plan.md) §2, roles, caps, queue, outbox, blobs and Redis of
[`platform/data-model.md`](platform/data-model.md) (§1, §3, §4, §5, §9, §11), settings rule, compose, zones, R1 test,
observability and the Kubernetes outline of [`platform/repo-and-deployment.md`](platform/repo-and-deployment.md)
(§3, §6, §7.3, §8.3, §9, §10, §11), execution, jobs, fairness, scheduler, index writer and capacity of
[`ingestion-worker/design-engine.md`](ingestion-worker/design-engine.md) (§7–§9, §14, §18), frontier, releaser, host
budgets, renderer, ClamAV and change detection of
[`ingestion-worker/design-connectors.md`](ingestion-worker/design-connectors.md) (§5–§8, §11, §12, §17), NER and embed
of [`ingestion-worker/design-processing.md`](ingestion-worker/design-processing.md) (§10, §11, §16), the parser
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

## 1. Summary of decisions

Q1–Q4 mentioned in the body were open when this pass was written; the user answered them on 2026-10-30
(§10.1 answers 5–8: municipal S3 else SeaweedFS; halfvec HNSW from P1; crawl rules stay with consent-gated per-host
overrides; worker-only gateway key now, query lane by trigger). The other documents carry the decided form.

| # | Topic | Decision |
|---|---|---|
| 1 | Scale target | ≈ 10× the pilot on 3–5 hosts (answered 1): ~100,000 pages/hour **checked**, downstream sized for 10–20k pages/hour **changed** sustained and bulk loads throttled to the embedding budget; 5–10M chunks; 20+ departments; several hundred concurrent chat users (≈ 20 messages/s peak). Beyond that only behind §6 triggers |
| 2 | Binding resources | Per-host politeness binds crawling; the gateway's on-prem **BGE-M3** binds embedding (bulk); the **NER lane** binds PII detection (CPU); Postgres binds only through HNSW memory and vacuum; chat is bound by the gateway's answer models, not by us (§2) |
| 3 | Scale-out shape | Compose per host, roles per host (answered 2): same images, `Worker:Classes` + deployment settings per instance, one Postgres, one Redis, one object store; routed LAN with TLS and host firewalls, no overlay network (§5) |
| 4 | Per-process vs fleet settings | Per-instance concurrency moves to **`Worker:Concurrency:<class>`** (deployment, per the settings rule); `Settings.WorkerConcurrency` keeps only fleet-wide shares and caps (§3.4, fixes a contradiction in data-model §4.2 / repo §3) |
| 5 | Connections | Explicit budget per role × instances (§3.1); **no PgBouncer** up to 5 hosts (fits in 200 connections), but code is **pooler-safe from P0** (no session state; LISTEN on its own connection string); PgBouncer ≥ 1.21 in transaction mode behind a trigger |
| 6 | Vectors | **HNSW on `("Embedding"::halfvec(1024))`** (column stays `vector`) keeps Postgres viable to ≈ 10M chunks on a 64 GB database host; the O1 trigger is restated as measured HNSW memory, latency and filtered recall instead of "> 2–3M chunks" (open question Q2, §3.3) |
| 7 | Job queue | Stays Postgres (answered 4) behind an **`IJobQueue`** seam; batched dequeue and completion, coalesced wake-ups, debounced `NOTIFY` (it serialises commits, `async.c:905-922`); broker only by §6 trigger |
| 8 | Leaders | Tick leaders keep `pg_try_advisory_xact_lock` (failover = next tick); config singletons (`Worker:Maintenance`, `Admin:Retention`) become **lease leaders** (`content."LeaderLeases"`, failover ≤ 30 s) or idempotent lock-guarded batches (§3.5) |
| 9 | Crawl connections | The per-instance connection cap (k × cap, connectors §6.3) is replaced by a **Redis in-flight lease per host** (gateway pattern, `RedisStores.cs:36-38`), with ⌈cap ÷ k⌉ as the Redis-down fallback (§3.6) |
| 10 | Parser scale-out | Default: a `parse,ner` worker **co-located** with each parser host on its internal `parser` network (no cross-host parser traffic, no new auth); `NER_LANES` > 1; multi-endpoint `Parser:Endpoints` only as an option (§3.8) |
| 11 | Embedding | Fleet-wide in-flight cap (Redis lease, `Embedding:GlobalMaxInFlight`) on top of per-instance AIMD; batches ≤ 32 inputs / 8,192 tokens at scale; separate query lane at the gateway proposed (Q4, §3.9) |
| 12 | Blobs | `IBlobStore` contract made S3-safe in P0 (atomic put, promote, prefix delete, no rename or listing on hot paths); **`AWSSDK.S3`** client with `ForcePathStyle` and checksums `WHEN_REQUIRED`; envelope encryption unchanged, no SSE (answered 3, §3.13) |
| 13 | Object store server | MinIO's community edition is **archived and source-only** (no maintained images): the S3 server: municipal S3 service if IT offers one, else SeaweedFS (answered 5; register S2) (was: municipal S3 service, SeaweedFS Apache-2.0, or a licensed MinIO product) |
| 14 | Chat path | Stateless instances, no sticky sessions; fix `Retrieval:MaxConcurrentSearches` (16 × 2 > pool 25); hot-row upserts sorted and placed last; read replica only by trigger (§3.11) |
| 15 | Tests | R1 stays; new **scale test** (`scale.yml`): synthetic multi-host site, 5M/10M-chunk seed, FakeGateway with a GPU throughput model, 300-VU chat; each decision has a proving metric (§7) |

## 2. Capacity model

Workload assumptions (all *estimates* unless cited): a typical web page ≈ 600 words ≈ 8 chunks ≈ 3,600 embedding
tokens (engine §18); a document (PDF/Office) ≈ 40 chunks; 10M chunks ≈ 375k web pages + 175k documents; weekly
traversals; 10 % of checked pages changed per run (pilot data will replace this); chat: 300 active users, one message
per 60–90 s each, bursts to **20 messages/s**, ≈ 8 s per answer stream.

### 2.1 Ingestion per stage

| Stage | Cost per unit (source) | Pilot: 10k pages/h checked, all changed (R1) | 10×: 100k/h checked, 10k/h changed (steady) | 10×: bulk, 100k/h changed | Binds |
|---|---|---|---|---|---|
| List / sync, claims | 1 claims upsert of ≈ 100 links + ≈ 3 statements per page (connectors §17) | ≈ 11 statements/s | ≈ 110 statements/s, ≈ 2,800 claim-row probes/s (−60 % with the nav-link cache, §3.6) | same | Postgres (cheap) |
| Fetch | 1 politeness slot, ~200 ms latency; AngleSharp + hash 10–15 ms CPU (connectors §17) | 2.8 req/s | **27.8 req/s aggregate**; ≈ 0.4 worker cores | same | **politeness** (§2.3) |
| Render | 1–3 s per page, 2 pages per 1-CPU renderer, 1 per host (connectors §8.2, §17) | outside R1 | ~1 page/s per JS-only host whatever the renderer count | – | per-host lease |
| ClamAV | files only, PDF 50–200 ms (connectors §17) | negligible | ≈ 10 % files → ≈ 0.3 cores | ≈ 0.3 cores | CPU, local |
| Worker CPU (extract, clean, PII regex, tokenizer, chunk, JSON, AES) | ≈ 80 ms per changed page (engine §18, processing §16) + 15 ms per checked page | 0.25 cores | ≈ 0.65 cores | ≈ 2.6 cores | CPU (cheap) |
| Parse, text layer (parser) | p50 1.0 s × 4 threads ≈ 4 core-s per page (parser §14) | small (PdfPig takes most pages) | 2–4 cores | 10–20 cores | CPU |
| Parse, OCR | p50 4 s × 4 threads ≈ 16 core-s per page (parser §14, Tesseract) | 3,000 pages ≈ 3.3 h (parser §14) | onboarding 30k pages ≈ 133 core-h (one night on ≈ 16–20 cores) | – | **CPU** (GPU optional, trigger) |
| NER (names) | 0.3–1.0 s of a 2-thread lane ≈ 0.6–2 core-s per changed page (processing §16) | 1.7–5.6 cores | 1.7–5.6 cores | **17–56 cores** fp32 (≈ ⅓–½ with int8, *to measure*) | **CPU** |
| Chunk / tokenizer | 5–10 ms per page (processing §16) | < 0.1 core | < 0.1 core | 0.3 cores | CPU |
| Embed | ≈ 3,600 tokens per changed page (engine §18) | **10k tokens/s** | ≈ 10k tokens/s | **≈ 100k tokens/s** | **gateway BGE-M3** (§3.9) |
| Index write | 8 chunk inserts + HNSW insert 1–3 ms each in RAM (engine §18); WAL ≈ 10–15 KB per chunk (estimate) | 22 inserts/s | 22 inserts/s, ≈ 0.3 MB/s WAL | 222 inserts/s (≥ 2 concurrent writers), ≈ 3 MB/s WAL | Postgres I/O, HNSW memory |
| Jobs | ≈ 3 jobs per changed page (fetch, ner, embed; parse for documents), ≈ 4 queue statements each | ≈ 35 statements/s | ≈ 140 statements/s | ≈ 330 statements/s | Postgres (cheap) |
| Evaluation | gates ≈ 200 retrievals + ≤ 200k on-prem LLM tokens (processing §14.7) | rare | 100 collections × weekly sets ≈ 10k retrievals/week | – | on-prem LLM |

**Onboarding time** = chunks × 450 tokens ÷ measured embedding tokens/s: 5M chunks at 20k tokens/s ≈ 31 h (several
nights); the scheduler throttles bulk loads to the budget instead of sizing GPUs for the peak (§3.9).

### 2.2 Chat, knowledge, admin

| Stage | Per message (source) | 10× (20 msg/s peak) | Binds |
|---|---|---|---|
| Authentication, policy, rate limit | memory + 1 Redis command (chat §16.1) | 20 Redis cmd/s | – |
| Chat overhead, re-streaming ≈ 200 events | < 10 ms budget (chat §16.1); the gateway spends ≈ 6.5–7 ms CPU per realistic stream (`docs/performance.md:211-213`) | ≈ 0.3 cores across instances; ≈ 160 open streams | – |
| Query embedding | ≈ 30 tokens; ≤ 60 ms miss (knowledge §11) | 600 tokens/s, but **latency** behind ingestion batches | BGE-M3 queueing (§3.9) |
| K, V, H+D (3 round trips, knowledge §11) | K ≤ 30 ms at 100k chunks; frequent OR terms at 10M *to measure* (to-verify #5); V ≤ 15 ms in RAM | 60 statements/s, ≈ 0.5–2 Postgres cores | Postgres CPU, HNSW memory |
| Condense + answer model | on-prem/EU models through the gateway (chat §5, §8) | ≈ 200k prompt tokens/s prefill (≤ 8,000 source tokens each), ≈ 6k output tokens/s | **gateway providers** (not ours) |
| Writer | 1 transaction per ≤ 200 turns / 250 ms per instance (chat §12) | ≈ 8–16 transactions/s | – |
| Admin | BFF pages, preview, test panel (admin §10.2) | negligible | – |

### 2.3 Politeness: what actually caps crawling

| Fact | Number |
|---|---|
| Municipal host budget (answered 4, connectors §6.3) | ≤ 10 req/s, 4 connections → 36,000 requests/h, 288,000 per 8-h night window |
| External host | 1 req/s, 1 connection → 3,600/h (a 10,000-page external crawl ≈ 2.8 h, connectors §6.5) |
| Little's law at 200 ms latency | the interval rule `max(1/rate, LatencyEwma ÷ 4)` already keeps ≤ 4 requests in flight, so a municipal host tops out at **min(10, 4 ÷ latency) req/s** = 10 at 200 ms, 5 at 800 ms |
| 100k pages/h checked | needs ≥ 27.8 req/s aggregate: **≥ 3 municipal hosts crawled at full rate at once**, or umea.se above 10 req/s (needs a decision, Q3) |
| umea.se alone | at 10 req/s a 50,000-page site (estimate) traverses in ≈ 1.4 h: politeness is not the limit for weekly traversals of the real corpus, only for a single-host "pages per hour" figure |

So the 10× target is an **aggregate** over many hosts and departments, not one site. Levers, none of which breaks the
crawler decisions (user agent, robots always, night window, external 1 req/s, per-host shared budgets):

| Lever | Exists? | Effect | Change |
|---|---|---|---|
| Sitemap `lastmod` incremental runs (connectors §5.4) | yes, optional per source | a daily run fetches only changed + new URLs (≈ 1–5 % of requests) | make `incrementalSchedule` the **template default** for municipal hosts whose sitemaps carry `lastmod` (measured in the crawl report: share of `lastmod` hits that really changed) |
| Conditional GETs, stage-2 hash (connectors §12) | yes | saves bytes, CPU, parse and embed; **not** requests | none |
| Shared frontier + freshness window 12 h (connectors §6.2) | yes | 20 departments crawling umea.se cost one request per page | none |
| Change detection skipping parse/embed, noise re-key (connectors §12, processing §12) | yes | removes ≈ 90 % of downstream cost on re-crawls | none |
| `revalidateEvery` 4 (connectors §5.4) | yes | one unconditional traversal in four | none |
| Allow-listed municipal host override (`Settings.Crawl.HostOverrides`) | yes (admins) | umea.se at e.g. 20 req/s / 8 connections with the web team's consent | above 10 req/s contradicts answered 4 → **Q3** |
| Day-time trickle for municipal hosts (e.g. 2 req/s outside the window) | no | spreads traversals; keeps freshness during the day | changes the default `crawlWindow` → **Q3** |
| CMS change feed connector (Sitevision/Episerver export) | no | zero-request change detection | P3 idea, needs the web team |

## 3. Bottlenecks and decisions

### 3.1 Postgres connections, pooler, timeouts

**Budget rule**: for each role, Σ over instances (`Maximum Pool Size` + 1 LISTEN) ≤ role `CONNECTION LIMIT`; Σ limits +
3 (`superuser_reserved_connections`) + 2 (monitoring, new role `ume_rag_monitor` with `pg_monitor`) ≤
`max_connections`. Checked by a deploy-time script (`deploy/postgres/connection-budget.sh`: reads each host's `.env`
instance list and fails the rollout when the sum exceeds a limit).

| Role | Pool per instance (+ LISTEN) | Pilot (1 host) | 3 hosts | 5 hosts | Limit at 5 hosts |
|---|---|---|---|---|---|
| `ume_rag_api` | 25 (+1); see `MaxConcurrentSearches` below | 2 → 52 | 2 → 52 | 4 (a/b on two hosts) → 104 | 110 |
| `ume_rag_admin` | 6 (+1) | 2 → 14 | 2 → 14 | 2 → 14 | 15 |
| `ume_rag_tools` (P2) | 5 (+1) | 2 → 12 | 2 → 12 | 2 → 12 | 12 |
| `ume_rag_worker` | by classes: fetch host 10 (+1), processing 10 (+1), parser-host `parse,ner` 6 (+1) | 1 → 11 | 1 → 11 | 2 → 22 (+1 per extra P host: 7) | 32 |
| `ume_rag_migrator`, `ume_rag_monitor`, reserved | 2, 2, 3 | 7 | 7 | 7 | – |
| **Sum** / `max_connections` | | 96 / 150 | 96 / 150 | **159 / 200** | – |

- **No pooler up to 5 hosts.** The counts fit; the real cost is memory per backend, dominated by the Hunspell
  dictionaries loaded per backend (data-model §3.1, H1): ≈ 170 backends × (≈ 10 MB + dictionary, tens of MB) ≈ 2–8 GB,
  acceptable on a 64 GB database host, not on the pilot's 5 GB container (H1 measures it in P0).
- **Pooler-safe from P0** (cheap, code rules + one test): no session-level `SET` (role defaults and `SET LOCAL` only,
  as today), no session advisory locks (only `pg_try_advisory_xact_lock`), no `WITH HOLD` cursors, no non-`ON COMMIT
  DROP` temp tables, no SQL-level `PREPARE`; LISTEN uses its own connection string `ConnectionStrings__ragdb-listen`
  (same server today). A unit test scans SQL constants for the forbidden forms.
- **PgBouncer, when the trigger fires** (§6): ≥ 1.21 (Debian trixie ships 1.24.1, PGDG 1.26.0) in **transaction
  mode** for `ume_rag_api` and `ume_rag_worker`, with `max_prepared_statements` > 0 (default 200 on master,
  `doc/config.md`), because Npgsql's auto-prepare uses protocol-level prepared statements, which transaction pooling
  supports only then (feature map: "Protocol-level prepared plans: Yes²"; `SET/RESET`, `LISTEN`, `WITH HOLD`,
  `PREPARE`, session advisory locks: "Never"). `connect_query` warms Hunspell (`doc/config.md:1387`); `server_lifetime`
  raised so warm backends live long; Npgsql `No Reset On Close=true` on the pooled strings (its `DISCARD ALL` would
  clear PgBouncer's tracked statements). Direct (not pooled): LISTEN, migrations, admin (few connections, EF), COPY
  works through it inside a transaction. Known caveat after column changes: "cached plan must not change result type"
  until `RECONNECT` (`doc/config.md`, prepared-statement section); expand/contract migrations avoid it.
- **`MaxConcurrentSearches` vs pool** (knowledge §6.6): 16 searches × ≤ 2 connections = 32 > pool 25, plus the writer
  and the stats flush. Fix: `Retrieval:MaxConcurrentSearches` = ⌊(pool − 3) ÷ 2⌋ = 11 by default, validated at start
  against the pool size; more chat throughput comes from more instances, not a larger pool.
- **Timeouts** stay (data-model §1.1: worker `statement_timeout` 60 s per COPY batch of 1,000 rows,
  `idle_in_transaction_session_timeout` 60 s); a 20,000-chunk write is ≈ 20 COPY batches whose
  HNSW inserts at 10M chunks (3–10 ms each, *estimate*) may hold the transaction 60–200 s: allowed (not idle), but it
  pins the vacuum horizon, so sets above 5,000 chunks are written in a **dedicated short-lived connection** and
  measured (to-verify #21 extended).
- **Read replica**: not before the trigger (§6). When added: streaming standby, `hot_standby_feedback = on`,
  `max_standby_streaming_delay = 30s`; `ConnectionStrings__ragdb-read` used by K, V, H+D and the evaluator only (lag of
  seconds is acceptable: a publish swap becomes visible a few seconds later); writes (stats flush, writer) stay on the
  primary. It doubles as the warm standby for host failure (§5.4).

### 3.2 Postgres write path, vacuum, partitions

| Hot spot | At 10× | Decision |
|---|---|---|
| Chunk insert amplification: heap + TOAST (4 KB vector) + GIN `SearchVector`, `CollectionIds`, `Tags` + 2 B-trees + HNSW | ≈ 10–15 KB WAL per chunk (estimate) | binary `COPY` (engine §14) stays; `wal_compression = lz4` (PG 15+, our image's build *to verify*), `max_wal_size` 16 GB, `checkpoint_timeout` 15 min on the 64 GB host |
| `CollectionIds` change rewrites every chunk + HNSW entry (data-model §2.2 known cost) | adding umea.se (≈ 1M chunks) to another department's collection ≈ 1M new tuples, ≈ 10 GB WAL, hours of HNSW inserts | stays (batches of 1,000, nights); **trigger** for the `ChunkSetCollections` mapping alternative: > 2M membership rewrites per week (§6) |
| HNSW vacuum ("can take a while", pgvector `README.md:742-748`) | soft-deleted sets hard-deleted after 14 days; re-chunks and membership rewrites leave dead tuples | hard deletes in weekly batches; `autovacuum_work_mem` 2 GB; per-table `autovacuum_vacuum_scale_factor = 0.05` on `Chunks`; metric: vacuum duration; trigger: > 6 h → monthly `REINDEX INDEX CONCURRENTLY` (needs 2× index space) |
| GIN pending list on `SearchVector` (fastupdate) | K latency spikes when the list is long | keep defaults; trigger: K p95 spikes correlated with insert bursts → `fastupdate = off` on that index |
| `EmbeddingCache` duplicates vectors (processing §11) | a 10M-chunk onboarding stores ≈ 10M × 4 KB = 40 GB again (30-day retention) | P2: look up `Chunks` first (`IX_Chunks_Profile_TextHash` on (`EmbeddingProfileId`, `TextHash`) `WHERE "Kind" = 'child'`, ≈ 0.5 GB), cache only texts not in a live set; store cache vectors as `halfvec` |
| `Jobs`/`RunItems` day partitions (data-model §4.4) | ≈ 2.4M job rows per busy night, ≈ 4 versions each | unchanged design; per-partition autovacuum (scale 0.02) suffices; dequeue cost watched via dead tuples in `IX_Jobs_Ready` |
| Single-row hot spots: `CrawlHosts."NextAllowedAt"` (one per request), `CrawlRuns."Admitted"` (per claim batch) | ≤ 30 updates/s per row | non-indexed columns → HOT updates; `fillfactor = 50` on `CrawlHosts` and `CrawlRuns`, aggressive autovacuum on both |
| Multi-row upserts from several instances: `RetrievalStats` flush (knowledge §4.7), `AssistantDailyTotals` (chat §12.2) | deadlocks between instances flushing overlapping key sets; one totals row per (day, assistant, client, level) locked by every batch | **sort keys** before every multi-row upsert (one rule in `Ume.Rag.Infrastructure`); totals upsert as the **last** statement of the writer batch so its row lock is held for one statement |

**Postgres host at 10×** (estimate, confirmed by the scale test): 8–16 vCPU, **64 GB RAM** (`shared_buffers` 16 GB,
`effective_cache_size` 48 GB), NVMe, ≈ 150 GB data at 10M chunks with the halfvec index (§3.3), `random_page_cost`
1.1, `effective_io_concurrency` 200, `max_parallel_maintenance_workers` 4, `autovacuum_max_workers` 5.

### 3.3 Vectors: HNSW at 5–10M × 1024 dimensions

Sizes per element (estimate from pgvector's layout: an element tuple holds the vector, a neighbour tuple ≈ 2m × 6 B =
192 B at level 0; pages are 8 KB; measured with `pg_relation_size` in the scale test):

| Variant | Element + neighbours | Per element on disk | 5M | 10M | Fits a 64 GB host's cache? |
|---|---|---|---|---|---|
| `vector(1024)` HNSW (today) | ≈ 4.1 KB + 0.2 KB | ≈ 4.3 KB, up to **8 KB** when only one element fits a page | 22–40 GB | 43–80 GB | no at 10M (the O1 "> 2–3M chunks / not in RAM" figure is this case on a 32 GB host) |
| `halfvec(1024)` expression index | ≈ 2.1 KB + 0.2 KB | ≈ 2.7 KB (three per page) | ≈ 14 GB | ≈ 27 GB | **yes** |
| binary quantization `bit(1024)` + re-rank | ≈ 0.13 KB + 0.2 KB | ≈ 0.35–0.5 KB | ≈ 2.5 GB | ≈ 5 GB | yes, recall needs re-rank (pgvector `README.md:589-606`) |

**Decision (Q2 confirms):** the profile's HNSW index is created on the half-precision expression from the first
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
  collection, which brings data-model §12.16 ("evaluate halfvec in P2") forward.
- The **O1 trigger is restated** (knowledge §13): not a chunk count but (a) HNSW index size > 50 % of the database
  host's RAM, or (b) V p95 > 50 ms / retrieval p95 > 300 ms after tuning, or (c) filtered recall: HNSW recall@100
  against exact < 0.95 on mid-size collections (below), or (d) the existing recall and facet triggers. With halfvec a
  64 GB host keeps 10M chunks below (a).
- **Partitioning `Chunks`** by profile or tier does not shrink a profile's index (chunks are shared across
  departments, so no department partitioning); only a second profile (P3 migration) gets its own partial index, built
  **after** the shadow load with `CREATE INDEX CONCURRENTLY` (faster than incremental inserts, `README.md:289-319`),
  replacing the "index before any row" rule for large migrations.
- **Builds and rebuilds** need the graph in `maintenance_work_mem` to be fast (`README.md:291-305`): ≈ 27 GB at 10M is
  too much on a live 64 GB host, so rebuilds run in a maintenance window with 16 GB and parallel workers, or switch to
  the binary-quantized index (fast builds, `README.md:319`) — that switch is the §6 fallback before OpenSearch.

**Filtered search, the weak spot at scale.** HNSW filters after the walk; with `relaxed_order` it stops at
`hnsw.max_scan_tuples` 20,000 (knowledge §6.4). A collection of selectivity *s* (its share of the profile's index)
needs ≈ 100 ÷ *s* visited tuples for 100 hits, so with the default cap only *s* ≥ 0.5 % is safe: at 10M chunks that
is ≥ 50k chunks. Collections between 10k (exact today) and 50k chunks either lose recall under HNSW or pay an exact
scan of up to 50k vectors (≈ 50–150 ms, *estimate*). Decision: the V variant is chosen by **selectivity**, not size:
exact when scope ≤ `exactScanMaxChunks`; else HNSW when *s* ≥ `Settings.Retrieval.minHnswSelectivity` (0.2 %), with
`SET LOCAL hnsw.max_scan_tuples = least(100000, ceil(200 / s))` in the existing read-only batch (knowledge §6.4);
else exact, counted as `rag.retrieval.exact_large`. The scale test measures recall and latency per band; a failing
band is trigger (c) for O1.

### 3.4 Job queue

| Issue | At 10× | Decision |
|---|---|---|
| Seam for a later broker (answered 4) | – | **`IJobQueue`** in `Ume.Rag.Infrastructure` (sketch below); the Postgres implementation is today's SQL; enqueue takes the caller's transaction, so job creation stays atomic with domain writes (a broker would sit behind an outbox relay) |
| Dequeue round trips | one statement per job and department probe | **batched dequeue**: `LIMIT @n` = free slots the department may take (fairness share, engine §9.3), one statement for several jobs |
| Completion round trips | one `UPDATE` per job | completions that are not part of a fenced effect transaction (e.g. fetch `unchanged`, `shared`) are buffered ≤ 100 ms and written with one `UPDATE … FROM unnest(…)`; fenced completions stay in their transaction (engine §7.6) |
| `NOTIFY` serialises committing transactions on a database-wide lock (`async.c:905-922`, REL_17) | ≈ 85 notifying commits/s, each holding the lock through commit | **debounce**: a process sends `rag_jobs` for a class at most once per 200 ms, and not at all when it runs that class itself and has a free slot (it wakes locally); polling (2 s) remains the safety net |
| LISTEN fan-out to many instances | 85 notifications/s × 6 instances = 500 wake-ups/s | wake-ups are **coalesced** per class (≤ 1 probe round per 50 ms) and ignored when the instance has no free slot for the class (local check, no SQL) |
| Fairness across instances | per instance, approximate (engine §9.3) | stays; at 20+ departments the loose-index-scan department list (every 5 s) is shared per instance; per-department cap `max(1, ⌈slots ÷ departments⌉)` per instance |
| Per-instance concurrency is a `config."Settings"` value (data-model §4.2, engine §9.1) but the settings rule makes per-process concurrency a deployment knob (repo §3) | heterogeneous hosts need different values per instance | **`Worker:Concurrency:<class>`** (deployment, defaults = today's table); `Settings.WorkerConcurrency` keeps run-time fleet values only (e.g. `departmentShare`); the fleet-wide embedding cap is infrastructure, so a deployment value (`Embedding:GlobalMaxInFlight`, §3.9) |
| Mixed versions during a rolling update | an old worker may dequeue a job enqueued by new code | payloads are additive (tolerant reader); a job that needs a newer build carries `MinBuild` in its payload and older instances requeue it without an attempt (`rag.jobs.requeued{reason=build}`) |
| Broker | – | not before the §6 trigger; `fetch` is the first candidate, but its rate is capped by politeness (≤ Σ host rates), so the queue is unlikely to bind first |

```csharp
// Ume.Rag.Infrastructure/Jobs
public interface IJobQueue
{
    Task EnqueueAsync(IReadOnlyList<JobRequest> jobs, NpgsqlTransaction tx, CancellationToken ct);   // dedup, wake-up
    Task<IReadOnlyList<LeasedJob>> DequeueAsync(DequeueRequest request, CancellationToken ct);        // class, dept, n
    Task<IReadOnlySet<JobId>> HeartbeatAsync(IReadOnlyList<JobId> running, CancellationToken ct);     // lost = missing
    Task CompleteAsync(IReadOnlyList<JobCompletion> done, NpgsqlTransaction? tx, CancellationToken ct);
    Task ReleaseAsync(IReadOnlyList<JobId> jobs, RequeueReason reason, CancellationToken ct);         // no attempt
    IAsyncEnumerable<string> WakeUpsAsync(CancellationToken ct);                                       // LISTEN or broker
}
```

```sql
-- Batched dequeue (data-model §4.2 with LIMIT @n)
WITH next AS (
    SELECT "EnqueuedAt", "Id" FROM content."Jobs"
    WHERE "State" = 'queued' AND "Class" = $1 AND "DepartmentId" = $2 AND "AvailableAt" <= now()
    ORDER BY "Priority" DESC, "AvailableAt"
    LIMIT $5
    FOR UPDATE SKIP LOCKED)
UPDATE content."Jobs" j
SET "State" = 'running', "LeaseOwner" = $3, "LeaseUntil" = now() + $4::interval, "Attempts" = j."Attempts" + 1
FROM next WHERE j."EnqueuedAt" = next."EnqueuedAt" AND j."Id" = next."Id"
RETURNING j.*;
```

### 3.5 Leaders and background services across hosts

| Service (source) | Today | Failover | Decision |
|---|---|---|---|
| Frontier releaser per tier (connectors §6.3), scheduler (engine §9.6), reaper (engine §9.4), admin `OrganisationSync`, `GatewayModelSync`, P2 `DriftWatcher` (admin §2.2) | `pg_try_advisory_xact_lock` per tick (gateway `ModelListSync.cs:152`) | next tick (1 s – 5 min); no state held between ticks | **keep**; rule: a tick leader keeps no in-memory state that the next leader needs (the releaser counts `released` rows from `CrawlFrontier`, not memory) |
| Partition maintenance, content retention, nightly drift sweep (`Worker:Maintenance`, "exactly one instance") | config singleton | none: if that host is down nothing runs (7 days of partitions give a buffer) | **lease leader**: `Worker:Maintenance=true` allowed on ≥ 2 instances; each task takes a row in `content."LeaderLeases"` (below), renewed every 10 s, lease 30 s → failover ≤ 30 s; a task resumes from its batch checkpoint |
| Admin retention steps (`Admin:Retention`, slot a only) | config singleton | none | each step batch is idempotent; run on **both slots** under `pg_try_advisory_xact_lock(<step>)` per batch, so the slots share or skip work; `Admin:Retention` stays as an off switch |
| `ConfigVersionMonitor` | every instance | – | unchanged; config lag per instance is a metric (repo §9) |

```sql
CREATE TABLE content."LeaderLeases" (            -- main tier; worker S I U
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

- **Shared state is already in Postgres** (frontier, claims, `CrawlHosts.NextAllowedAt` slot pacing, render lease):
  rates are exact across instances (connectors §6.3).
- **k × connection cap** (connectors §6.3 "enforced in memory per instance … accepted until the ingestion zone moves")
  is fixed now, before more than one `fetch` instance exists. A Redis sorted-set lease per host, copied from the
  gateway's in-flight limiter (`src/Ume.LlmGateway.Infrastructure/Stores/RedisStores.cs:36-38` acquire, `:78-79`
  release); lease 60 s (> fetch timeout), released in `finally`:

```lua
-- KEYS[1] = ume-rag:crawl:inflight:{hostMac}; ARGV: now ms, max, expiry ms, lease id, key TTL ms
redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', ARGV[1])
if redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[2]) then return 0 end
redis.call('ZADD', KEYS[1], ARGV[3], ARGV[4])
redis.call('PEXPIRE', KEYS[1], ARGV[5])
return 1
```

  Refused → the job requeues at `now + interval` without an attempt (`rate_limited`). `hostMac` = HMAC of the host name
  with a new worker secret `Security__CrawlKeyPepper` (no host names of possibly `sensitive` sources in Redis).
  **Redis down**: fall back to ⌈`MaxConnections` ÷ k⌉ per instance, k = live `WorkerInstances` running `fetch`
  (heartbeat < 45 s); slot pacing still bounds in-flight requests by Little's law.
- **Nav-link claim cache**: per run and instance, an LRU (100k entries) of URL hashes already admitted in this run;
  site-chrome links (header, footer, menus: most of a page's ≈ 100 links) skip the claims upsert after the first page.
  Exactness is kept because the upsert remains the authority for the first sighting on each instance.
- **Releaser** stays one leader per tier (1 s tick); its query cost grows with hosts with pending entries, measured as
  `rag.crawl.releaser.duration`; trigger to shard by host hash across two leaders: p95 > 500 ms.
- **Crawl egress** (connectors §7): every host that runs `fetch` gets the same `crawl` network, firewall script and
  `UME_RAG_CRAWL_ALLOW`; the egress canary runs per instance.

### 3.7 Renderer and ClamAV

- **Renderer**: the worker fulfils every browser request itself (connectors §8.3), so a renderer must be **host-local
  to its fetch worker** (chatty WebSocket + per-request fulfilment). Scale = one renderer per fetch host, `cpus` 1–2,
  `Renderer:MaxPages` = 2 per CPU. The per-host render lease (1, shared across instances via `CrawlHosts`) means one
  JavaScript-only site renders at ≈ 1 page/s regardless of renderers; more renderers help only for many such sites.
- **ClamAV**: clamd has no TLS or authentication on TCP 3310, so it stays **host-local** on the `ingestion` network of
  every host that runs `sync`/`fetch` (uploads are scanned by the `sync-upload` path). Memory 1.2–3 GB each
  (signatures, doubled during `ConcurrentDatabaseReload`, repo §5.5); each instance updates from database.clamav.net
  through its host's `mirror` network (decision: no mirror). Load is small (files only, §2.1).

### 3.8 Parser pool, OCR and the NER lane

| Topic | Decision |
|---|---|
| Placement | A parser host runs `rag-parser` **and** a worker with `Worker:Classes=parse,ner` on the host's internal `parser` network: the security property "parser has no other network" (repo §6.1) holds unchanged, no parser mTLS or discovery is needed, and blobs come from the object store. The ingestion-zone host keeps a small parser for `preview` and `/v1/inspect` |
| Capacity | each worker still reads `capacity.convert_slots` and `ner_slots` from its local `/v1/info` (parser §10.1); `busy` requeues without an attempt (engine §7.3), so two worker instances can never wedge a parser |
| Optional multi-endpoint mode | `Parser:Endpoints` (deployment list) for Kubernetes or a shared parser pool: the client polls each `/v1/info` every 15 s, tracks its own in-flight per endpoint, picks the endpoint with most free slots, marks an endpoint full for 2 s on `busy`; cross-host parser traffic then needs mTLS (`PARSER_TLS_CLIENT_CA`, uvicorn client-certificate verification, *to verify*). Not used in the 3- and 5-host topologies |
| Slot layout | parser hosts use 2 × 2 or 4 × 2 slots (throughput, parser §14 expectation); `PARSER_WORKERS × PARSER_THREADS` = the container's `cpus` |
| NER | new `NER_LANES` (default 1; parser hosts 2–6) with `NER_THREADS` 2 each; `/v1/info` `capacity.ner_slots` = lanes; NER is fail-closed per document (`ner_pending` blocks `public` collections, processing §9.6), so a lagging lane delays, never leaks. int8 (`NER_QUANTIZE=1`) becomes the default once measured equal on the fixtures (processing §10.2) |
| OCR CPU vs GPU | CPU (Tesseract, Docling layout on CPU) is enough for onboarding in nights (§2.1); a CUDA parser image (Docling models, EasyOCR, NER on GPU) is a §6 trigger, not a P1/P2 item: image size and driver coupling are costly |
| Preview priority | the in-process preview gate (engine §9.5) applies per worker; previews run on the ingestion-zone host's parser, never queued behind a parser host's bulk OCR |

### 3.9 Embedding throughput through the gateway

The embedding model is the binding constraint for bulk ingestion and the main R1 risk (chat query embeddings share it).
Estimated BGE-M3 throughput (≈ 568M parameters ≈ 1.1 GFLOP per token; *estimates*, to-verify #98 measures them):

| Hardware at the gateway's provider | Tokens/s | Pages/h (3,600 tokens) |
|---|---|---|
| 8 CPU cores (ONNX/Ollama) | 0.5–1.5k | 0.5–1.5k |
| One L4/A10-class GPU | 8–20k | 8–20k |
| One A100/H100-class GPU | 30–80k | 30–80k |

| Topic | Decision |
|---|---|
| Fleet-wide cap | per-instance AIMD (engine §18) stays; on top, a Redis lease set `ume-rag:embed:inflight` (same Lua as §3.6) limits in-flight embedding requests of **all** worker instances to `Embedding:GlobalMaxInFlight` (deployment; default 2 × provider replicas); Redis down → per-instance AIMD only |
| Gateway-side cap (defence) | the gateway already limits in-flight requests per key across its instances (`VirtualKeys.MaxConcurrentRequests`, `src/Ume.LlmGateway.Domain/Entities/Entities.cs:108-110`, enforced with a 429 + `Retry-After` in `Pipeline/GatewayRequestHandler.Stages.cs:82-114`); a **worker-only platform key** with that limit would protect chat even from a misconfigured worker fleet → Q4 (today one service key is shared by api, worker and admin, repo §7.1) |
| Query latency | an ingestion batch of 64 × 450 tokens is ≈ 29k tokens, ≈ 1–3 s on an L4: a chat query queued behind a few batches misses its 60 ms budget although AIMD reacts to p95. Proposed: a **query lane**, i.e. a separate gateway route/replica of the same weights for `x-ume-step: query` (or CPU replica: a 30-token query is cheap) → Q4; the canary (processing §11) checks both routes against `EmbeddingProfiles.CanaryEmbedding` |
| Batch size at scale | `batchSize` 32 and ≤ 8,192 tokens per request when `Embedding:GlobalMaxInFlight` > 4 (shorter head-of-line blocking at equal throughput, *to measure*); 64 / 16,384 stays the single-host default |
| Back-pressure | 429 and capacity refusals requeue without an attempt (engine §7.3) and halve AIMD; `Retry-After` honoured |
| Cache hits | per-document `EmbeddingCache` lookup (processing §11) and shared chunk sets (one embed for 20 departments); P2 lookup via `Chunks` (§3.2) |
| Bulk loads | onboarding and pipeline publishes that re-chunk large shared corpora run at `Priority` −5 inside the night window and are forecast in the admin from measured tokens/s (processing §14.7 forecast); a "bulk" run never takes more than `Settings.Ingestion.bulkEmbedShare` (0.75) of the global cap |

### 3.10 Index writer, evaluation

- **Index writer contention**: one transaction per document (engine §14); contention is per document only
  (`DocumentSources`, `ChunkSets` unique key). Run counters are derived (engine §10.4), so no hot run row. At bulk rates
  ≥ 2 writers run in parallel (separate `embed` jobs); HNSW concurrent inserts scale to a few writers (page locks),
  measured as `rag.index.write.duration` per chunk.
- **Grouped writes**: an `embed` job serving several small documents commits each document separately (fence per
  document) but reuses one connection and one `NpgsqlBatch` for the fence + set + pointer statements; COPY per set.
- **Evaluation isolation**: `eval` runs on at most two instances (`Worker:Classes`), concurrency 1 each; scheduled
  weekly evaluations spread with the scheduler's jitter; gate retrieval uses exact scans on sub-corpora (cheap) and
  reads the replica when one exists; judge and generation calls go to on-prem models whose capacity is the gateway's.

### 3.11 Chat, knowledge and admin path

| Topic | Decision |
|---|---|
| Instances | 2 per data-plane host (a/b slots, rolling overlay unchanged), 2 hosts at 5-host scale; the municipal proxy load-balances all four with `proxy_buffering off`; CPU need is < 1 core at 20 msg/s (§2.2), so instances are for availability |
| Sticky sessions | **none needed**: config snapshot per instance (fail-closed ≤ 10 s, data-model §5), rate limits and admin sessions in Redis, Data Protection keys in `config."DataProtectionKeys"`, signed conversation ids and download grants (chat §11.1, knowledge §4.6), MCP stateless (tools §7.1). Per-instance only: L1 query-embedding cache (L2 Redis shares hits), the writer queue (bounded loss, chat §12.4) |
| Connection pools | §3.1 (`MaxConcurrentSearches` fix); `Minimum Pool Size` 8 keeps warm Hunspell backends (knowledge §6.6) |
| Writer across instances | `Seq` under the conversation row lock (chat §12.2) is correct across instances; totals upsert sorted and last (§3.2) |
| Read replica | §3.1, only by trigger |
| Tool hub (P2) | stateless, data-plane zone (tools §2.2); one per data-plane host behind the same internal name list; pooled MCP clients are per instance |
| Admin | two slots; background work per §3.5; previews and test panel use the same engines, bounded by their own rate limits |
| Open WebUI (P2: own VM) | defaults to SQLite and one worker (`backend/open_webui/env.py:271`, `:462`); several hundred concurrent users may saturate it: §6 trigger → its own Postgres (never `ragdb`), `UVICORN_WORKERS` > 1 and `WEBSOCKET_MANAGER=redis` (`env.py:476`) with its own Redis |

### 3.12 Redis

One instance stays enough at 10×: ≈ 2–3 commands per chat message (≈ 60/s), crawl and embed leases (≈ 60–100/s),
sessions; the gateway's own Redis used about half a command thread at 17,888 msg/s (`docs/performance.md:129-131`).
Memory: query embeddings 4 KB × entries of 5 min (≈ 6,000 at 20 msg/s ≈ 24 MB) + small keys → `mem_limit` 256 MB
stays. New keys (data-model §11): `ume-rag:crawl:inflight:{hostMac}` and `ume-rag:embed:inflight` (sorted sets, TTL =
lease). No persistence (as today): a restart loses rate-limit windows, L2 cache, leases (fallbacks above) and admin
sessions (users sign in again). Sentinel/replica only by trigger (availability requirement or > 50 % of one thread).

### 3.13 Object store and `IBlobStore`

**Contract changes (P0, file system implementation):** the engine assumes "write-to-temp then rename/put-if-absent"
(engine §7.6) and ClamAV "renamed into place" (connectors §11); S3 has no rename. New contract, S3-safe on both
implementations:

```csharp
public interface IBlobStore
{
    // Atomic: the object is visible only when complete (FS: temp + rename; S3: PUT or completed multipart upload).
    Task<BlobWriteResult> PutAsync(BlobKey key, Stream content, long? length, BlobPutMode mode, CancellationToken ct);
    Task<Stream?> OpenReadAsync(BlobKey key, BlobRange? range, CancellationToken ct);            // null = absent
    Task<bool> ExistsAsync(BlobKey key, CancellationToken ct);
    Task PromoteAsync(BlobKey from, BlobKey to, CancellationToken ct);   // FS rename; S3 server-side copy + delete
    Task DeletePrefixAsync(BlobKey prefix, CancellationToken ct);         // document purge, preview and tmp sweeps
    IAsyncEnumerable<BlobInfo> ListAsync(BlobKey prefix, CancellationToken ct); // tmp/ sweeps, weekly orphan audit only
}
public enum BlobPutMode { Overwrite, IfAbsent }  // IfAbsent: S3 If-None-Match "*"; 412 = already there = success
```

- **Content-addressed writes need no conditional PUT**: two writers of one cache key write the same plaintext under
  the same document key (different nonces), either ciphertext is valid, so `Overwrite` is correct and `IfAbsent` is
  only an optimisation (`PutObjectRequest.IfNoneMatch`, AWSSDK.S3 XML docs: "Uploads the object only if the object key
  name does not already exist … Otherwise … 412"; server support *to verify*).
- **ClamAV path**: the worker streams the encrypted body to a **local** temp file (`/tmp` tmpfs or a scratch volume,
  ≤ 60 MB), scans it, then `PutAsync` to the final key; no `tmp/` object and no copy on S3. Uploads arrive at the admin
  and are already blobs (`uploads/…`), scanned by the worker reading them.
- **Multipart**: uploads (50 MB) and fetched files have a known encrypted length (header + 64 KiB segments + 16 B tag
  each, data-model §7), so a single PUT suffices (≤ 5 GB); multipart (8 MiB parts) only for unknown lengths > 64 MiB.
- **Listing**: no hot-path listing; orphan detection is DB-driven (`NodeOutputs.ExpiresAt`, purges) plus a weekly
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

- **Settings**: `BlobStore:Kind` (`FileSystem` | `S3`), `BlobStore:S3:ServiceUrl`, `BlobStore:S3:Bucket:<tier>`
  (`rag-main`, `rag-sensitive`), secrets `BlobStore__S3__AccessKey`/`SecretKey` **per service** with bucket policies
  mirroring today's mounts: worker read/write, admin write on `uploads/` and read, api read-only (P2: write on
  `chat-tmp/`).
- **Encryption**: envelope encryption unchanged (answered 3); no SSE/SSE-C (no KMS dependency; disks of the object
  store host are LUKS-encrypted like `postgres-data`). Backups: bucket replication or `rclone`/server tooling instead of
  `backup-blobs.sh`'s file copy (repo §11 #14).
- **Latency**: a GET on the LAN costs ≈ 5–20 ms (*estimate*), negligible next to parse (≥ 1 s) and preview; download
  grants stream through the api with range requests. No decrypted caches on disk; within a job outputs are passed in
  memory.
- **Server**: MinIO's repository is **archived** ("THIS REPOSITORY IS NO LONGER MAINTAINED", community edition
  "distributed as source code only", last release `RELEASE.2025-10-15T17-29-55Z`, `minio/minio` `README.md`; anonymous
  pulls of `minio/minio` refused by the registry on 2026-10-30), AGPL-3.0, and its successor "AIStor Free" has its own
  licence. Running unmodified AGPL software internally creates no source obligation, but an **unmaintained** storage
  server holding every document is the real risk. Candidates: the municipality's existing S3-capable storage (if IT
  runs one), **SeaweedFS** (Apache-2.0, active, image `chrislusf/seaweedfs` 4.51), Garage (AGPL-3.0), Ceph RGW (heavy).
  → **Q1**. Single node with a separate encrypted disk at 3–5 hosts; distributed only by trigger.

### 3.14 Observability for capacity and autoscaling signals

New metrics (meter `Ume.Rag`, low-cardinality; repo §9 conventions):

| Metric | Type | Dimensions | Saturation signal for |
|---|---|---|---|
| `rag.jobs.age` p95 (existing) | histogram | class | **the** queue signal: per-class lag; later the KEDA/autoscaling input |
| `rag.jobs.slots.busy`, `.slots.total` | gauge | class, instance role | per-class saturation per instance |
| `rag.crawl.host_budget.utilisation` | gauge | host kind (`municipal`/`external`) | used vs allowed slots; politeness-bound when ≈ 1 |
| `rag.crawl.inflight_refusals`, `rag.embed.global_refusals` | counter | – | Redis lease saturation |
| `rag.embed.tokens_per_second`, `rag.embed.queue_wait` | gauge, histogram | lane (`ingest`/`query`) | BGE-M3 capacity, head-of-line blocking |
| `rag.parser.slots_busy` / `slots_ready`, `rag.parser.ner.queue` | gauge | host | parser and NER saturation |
| `rag.db.connections` per role vs limit, `rag.db.pool.wait` | gauge, histogram | role | budget (§3.1) |
| `rag.db.hnsw.bytes`, `rag.db.hnsw.cache_hit` | gauge | profile | O1 trigger (a) |
| `rag.db.vacuum.duration`, `rag.db.dead_tuples` | histogram, gauge | table | §3.2 triggers |
| `rag.db.wal.bytes_per_second`, `rag.db.replication.lag` | gauge | – | write load, replica |
| `rag.blob.duration` | histogram | op, store kind | object store latency |
| `rag.leader.changes` | counter | task | §3.5 failovers |

Postgres-side metrics come from the OpenTelemetry Collector's `postgresql` receiver with the `ume_rag_monitor` role
(collector already in `deploy/otel/`; receiver metric set *to verify*), plus `pg_stat_statements` (needs
`shared_preload_libraries`, today empty: repo §6.2 change). Dashboards: a *Capacity* board (per-stage saturation, queue
age per class, host budget utilisation, embedding tokens/s vs cap, HNSW memory, connections vs budget).

## 4. In-process parallelism

| Area | Rule |
|---|---|
| I/O | async end to end; no `.Result`/`.Wait()`; one `CancellationToken` per job linked to the lease (engine §9.4) |
| Dispatcher | one loop per instance; per class a `SemaphoreSlim(Worker:Concurrency:<class>)`; jobs run as tasks, not threads; wake-ups via a coalescing `Channel<string>` (capacity 1 per class, `DropWrite`) |
| CPU-bound work | AngleSharp/SmartReader, PdfPig, tokenizer, SHA-256 (`IncrementalHash`), AES-GCM segments, JSON of large outputs run in a **CPU lane**: a bounded `Channel<CpuWork>` with `ProcessorCount` consumers, so long CPU work never occupies all thread-pool threads that I/O continuations need; each item checks cancellation between pages/segments |
| Thread safety | `AesGcm` instances per lane consumer (not shared); `Tokenizer` shared only if verified thread-safe (to-verify #26), else one per consumer; compiled regexes shared |
| Pipelines within a job | fetch → hash → links → store as a streaming pipeline (no full-body buffers beyond 60 MB; `ArrayPool` as the gateway, `src/Ume.LlmGateway.Infrastructure/Providers/ProviderJson.cs:264-286`) |
| Batching | claims upsert per page (`unnest`), run items in batches, embeddings ≤ 64 (32 at scale) inputs, completions buffered (§3.4), stats flush 30 s |
| Npgsql | one `NpgsqlDataSource` per tier with `UseVector()`; `NpgsqlBatch` for multi-statement round trips (to-verify #19, #20); **binary COPY** for chunks (below); `Max Auto Prepare` 64; `Multiplexing` not used (complexity; revisit only with a trigger) |
| HTTP | `IHttpClientFactory` with one `SocketsHttpHandler` per downstream: gateway (`PooledConnectionLifetime` 5 min as `ProviderHttpClient.cs:50-56`, `MaxConnectionsPerServer` = `Embedding:GlobalMaxInFlight` share + query lane), parser (`MaxConnectionsPerServer` = slots + 2, long timeouts), crawl (egress-guard `ConnectCallback`, `MaxConnectionsPerServer` 4) |
| GC | Server GC (Web SDK default) with DATAS (default since .NET 9, *to verify* for .NET 10); `DOTNET_GCHeapHardLimitPercent=75` on **every** .NET container (today only api, repo §6.2); the gateway's memory study (`docs/performance-improvement-plan.md:72-81`) decides further knobs for both repos |

```csharp
// Index writer: chunks with binary COPY; Pgvector 0.3.2 maps Vector → "vector" and HalfVector → "halfvec" by default
// (VectorTypeInfoResolverFactory.cs:22-29), so no NpgsqlDbType is needed; pattern as the gateway's
// UsageRecordCopy.cs:39 and pgvector-dotnet examples/Loading/Program.cs:37-51. SearchVector is generated, not copied.
await using var copy = await conn.BeginBinaryImportAsync(
    """COPY content."Chunks" ("Id","ChunkSetId","DocumentId","Classification","Kind","Ordinal","HeadingPath","Text",
       "OverlapChars","TextHash","TokenCount","Page","Tags","DocumentType","SourceModifiedAt","CollectionIds",
       "EmbeddingProfileId","Embedding") FROM STDIN (FORMAT BINARY)""", ct);
foreach (var c in batch)                                   // ≤ 1,000 rows per COPY statement (engine §14)
{
    await copy.StartRowAsync(ct);
    await copy.WriteAsync(c.Id, NpgsqlDbType.Uuid, ct);
    // … remaining columns …
    await copy.WriteAsync(new Vector(c.Embedding), ct);   // ReadOnlyMemory<float>; column stays vector (§3.3)
}
await copy.CompleteAsync(ct);
```

## 5. Topologies

### 5.1 One host (pilot, unchanged)

repo §6.2: 8 vCPU / 24 GB, zones by `cpu_shares` and the ingestion slice, file-system blobs, no pooler, one worker
with all classes. P0/P1 adds only seams (§8).

### 5.2 Three hosts

| Host | Size (estimate) | Runs | Networks |
|---|---|---|---|
| **D** data | 8–16 vCPU, 64 GB, NVMe | postgres, redis, object store, OTel collector | LAN: 5432, 6379 (TLS), 443 S3 (TLS) to listed peer IPs only |
| **A** data plane + control | 8 vCPU, 16 GB | rag-api a/b, rag-admin a/b, P2 rag-tools; Open WebUI on its own VM (decided) | `edge`, `llm`; LAN to D |
| **I** ingestion | 16 vCPU, 32 GB | rag-worker (all classes), rag-parser (2 × 2 or 4 × 2 slots, `NER_LANES` 2), rag-renderer, rag-clamav | `ingestion`, `parser`, `crawl`, `mirror`, `llm`; LAN to D |

### 5.3 Five hosts

| Host | Runs | Classes / roles |
|---|---|---|
| **D** | as above (+ read replica on **A2** by trigger) | – |
| **A1**, **A2** | rag-api a (A1) / b (A2) ×2 each, rag-admin a (A1) / b (A2), rag-tools one each (P2) | `Rag__Roles=knowledge,chat[,mcp]` |
| **F** fetch | worker `preview,command,sync,fetch,eval`; renderer ×2; clamav; small parser (inspect, preview) | crawl egress host |
| **P** processing | worker `parse,ner,embed`; parser 4 × 2 slots + `NER_LANES` 4 (16–24 vCPU, 48 GB) | no crawl network |

A sixth host is a second **P** (same compose, more parser + NER), which is how ingestion scales beyond 10×.

### 5.4 Cross-host rules

| Topic | Decision |
|---|---|
| Networks | **routed LAN + host firewall + TLS**, no Docker overlay (Swarm adds an orchestrator, VXLAN is unencrypted by default; Kubernetes later brings NetworkPolicies, repo §10). Each host's compose publishes only the ports its peers need on its LAN address; `deploy/host/docker-user-firewall.sh` gains a peer list per role; Postgres `pg_hba.conf` lists each peer host per role (repo §6.4 pattern) |
| Host-local only | parser (on F and P with their own worker), renderer, clamd: never published on the LAN |
| Compose files | `compose.data.yaml`, `compose.api.yaml`, `compose.admin.yaml`, `compose.ingestion.yaml` (exists), `compose.parser.yaml`; `init-deployment.sh --role data|api|ingestion|processing|all` (extends repo §6.4) writes that host's `.env`, certificates and **only its secrets** |
| Secrets distribution | generated once on the operator's machine (as today), copied per role with the operator's existing tooling; master key files reach more hosts (api: chat + read-only blob keys; worker hosts: blob keys), which raises the stakes of postponed decision **K1**: decide it before the first multi-host deployment (§9) |
| Must be single | Postgres primary, Redis, object store (single node), migrations (one-shot), lease leaders (§3.5) |
| Failure domains | D down = platform down (backups, optional standby on A2 by trigger); A1 or A2 down = chat continues on the other; F down = crawls pause (leases reaped in 45 s), uploads still accepted; P down = parse/NER/embed queue up, nothing lost |
| Rolling updates | `rolling-update.sh --role` per host: extensions + migrations once (from D's operator shell) → A1 then A2 (each a/b as today, repo §7.3) → admin → F → P (worker stop-then-start per host; parser recreated); mixed versions are safe because migrations are expand/contract and jobs carry `MinBuild` (§3.4) |
| Kubernetes | optional later, the gateway path (`docs/kubernetes.md:112-118`: stateless replicas, PDBs); Compose roles map to Deployments per class group, `rag.jobs.age` to the autoscaler; PgBouncer becomes required there (replica counts vary) |

## 6. Scaling triggers

| Metric (measured how) | Threshold | Action |
|---|---|---|
| Σ backends or Hunspell memory (`pg_stat_activity`, H1 test) | > 250 backends, or backend memory > 15 % of RAM, or Kubernetes autoscaling | PgBouncer transaction mode for api + worker (§3.1) |
| Primary CPU in office hours with retrieval share (`pg_stat_statements`) | > 70 % with retrieval > 50 % of it, or retrieval p95 > 250 ms during ingestion | read replica for K, V, H+D and the evaluator |
| HNSW size vs RAM (`rag.db.hnsw.bytes`) | > 50 % of the DB host's RAM | binary-quantized index + re-rank first; then O1 |
| Retrieval latency (`rag.retrieval.stage.duration`) | V p95 > 50 ms or retrieval p95 > 300 ms after tuning | O1 evaluation (`Retrieval:Index=shadow`) |
| Filtered recall (scale test, shadow HNSW vs exact) | recall@100 < 0.95 for collections in the 0.05–2 % selectivity band | O1 (efficient filtered kNN) or per-collection partial indexes |
| Queue (`pg_stat_statements` on `Jobs`, NOTIFY lock waits) | > 1,500 queue statements/s sustained, or `Jobs` > 30 % of primary CPU, or lock waits on database 0 > 5 % of commits | move `fetch` (then `embed`) to a broker behind `IJobQueue` (outbox relay) |
| Parse backlog (`rag.jobs.age{class=parse}`) | p95 > 12 h for a week, or OCR onboarding not done in 3 nights | second parser host (P-type), then CUDA parser image |
| NER backlog (`rag.jobs.age{class=ner}`) | p95 > 6 h | more `NER_LANES`/P hosts; then NER on GPU |
| Embedding (`rag.embed.queue_wait`, gateway 429s) | ingest queue wait p95 > 5 s at the global cap, or onboarding > 1 week | more BGE-M3 replicas at the gateway (gateway team); query lane if not yet |
| Query embedding p95 during ingestion | > 2 × idle | query lane (Q4) |
| Membership rewrites (`rag.index.chunks_written{kind=membership}`) | > 2M per week | `ChunkSetCollections` mapping (data-model §2.2) |
| `EmbeddingCache` size | > 20 % of database size | `Chunks` lookup first (§3.2) |
| Hosts / instances | > 5 hosts, or any role needs automatic scaling, or > 3 rolling slots | Kubernetes (repo §10) |
| Redis | > 50 % of one command thread, or an availability requirement | Redis replica + Sentinel |
| Object store | > 2 TB or > 50M objects, or single-disk throughput saturated | distributed object store |
| Open WebUI | CPU > 80 % of its VM or p95 overhead > 100 ms at peak | its own Postgres + workers + Redis (§3.11) |

## 7. Load and scale tests

**R1 release test stays** (repo §8.3, every release, pilot-shaped host). New **scale test** `scale.yml` (manual
dispatch on 3 test hosts, before P2 multi-host rollout and after changes to the §3 decisions; not per release):

| Component | Design |
|---|---|
| Synthetic site `tests/scale/Ume.Rag.FakeSites` | one .NET process serving N virtual hosts (default 20 municipal-like + 50 external-like) with deterministic pages from a seed (Swedish text from `tests/fixtures`, 100 links each with site chrome), sitemaps with `lastmod`, ETag/304, configurable change rate (10 %), latency distribution, JS-only pages (5 %), 429 injection, PDFs from the fixture corpus; a `--hosts`/`--pages` scale knob |
| FakeGateway throughput model | `Embed:TokensPerSecond` (token bucket emulating a GPU), `Embed:Replicas`, `Embed:MaxQueue` (429 + `Retry-After` beyond), `Embed:QueryLane` (separate bucket); chat latency profile as today (`FirstTokenMs` 400, `TokenIntervalMs` 25, `JitterPercent` 30, gateway `benchmarks/compare`) |
| Chunk seed `tests/scale/Ume.Rag.ScaleSeed` | binary-COPY loader for 1M / 5M / 10M chunks into one profile: texts from the fixture corpus, vectors = real BGE-M3 embeddings of ≈ 50k fixture chunks + small Gaussian noise, renormalised (random vectors give a pessimistic graph); 20 departments, collections at 0.01–20 % selectivity, shared sets |
| Chat load | k6 from the gateway's `benchmarks/compare/k6`: 300 VUs, think time 60–90 s, bursts to 20 msg/s; replicas behind nginx as the gateway's horizontal-scaling item (`docs/performance-improvement-plan.md:83-92`) |
| Reports | Markdown as `latency_report.py`; harness check table (fake gateway, FakeSites, k6 not saturated) as the gateway does (`docs/performance.md:112-113`) |

| Decision | Proving metric | Pass |
|---|---|---|
| halfvec HNSW (§3.3) | `pg_relation_size` per variant at 1M/5M/10M; recall@10 HNSW vs exact on the evaluation sets; V p95 | ≤ 3 KB/element; recall delta ≤ 0.5 points; V p95 ≤ 25 ms at 10M warm |
| Selectivity rule (§3.3) | recall@100 and latency per selectivity band | recall ≥ 0.95, p95 ≤ 150 ms in every band |
| Connection budget (§3.1) | `rag.db.connections` per role, pool waits, Postgres backend RSS | no refusals; Hunspell memory within budget |
| Queue (§3.4) | queue statements/s, NOTIFY lock waits (`pg_locks` object 0), dequeue p95 | ≥ 3× the 10× rate without lock waits > 1 % |
| Crawl budgets (§3.6) | max in-flight per host (FakeSites counter) with 3 fetch instances; `rag.crawl.host_budget.utilisation` | never > `MaxConnections`; utilisation ≈ 1 on the busiest host |
| Embedding caps (§3.9) | in-flight at FakeGateway ≤ `GlobalMaxInFlight`; query embedding p95 with and without the query lane | query p95 ≤ 2 × idle with the lane |
| Leaders (§3.5) | kill the leader's host: time to the next maintenance/releaser action | ≤ 30 s (lease), ≤ 1 tick (xact) |
| Topology (§5) | pages/h checked and changed; chat p95 TTFT idle vs during a 100k-page crawl + OCR batch | ≥ 100k checked/h with ≥ 3 hosts at budget; chat p95 ≤ 1.10 × idle (R1 rule at scale) |
| Blobs (§3.13) | `rag.blob.duration` p95 per op; contract test against the chosen server | GET ≤ 20 ms, PUT 1 MB ≤ 50 ms on the LAN |

## 8. Phasing

| Phase | Item | Why now |
|---|---|---|
| **P0** | `IBlobStore` S3-safe contract (FS impl); `IJobQueue` seam (Postgres impl); pooler-safe rules + SQL scan test; `ConnectionStrings__ragdb-listen`; `Worker:Concurrency:<class>` (deployment) and `Worker:Classes` per instance; `content."LeaderLeases"`; connection-budget script; `ume_rag_monitor` role; `DOTNET_GCHeapHardLimitPercent` on every .NET service; halfvec index expression in the first migration (if Q2 agrees) | seams cost a day now and a migration later |
| **P1** | `S3BlobStore` (AWSSDK.S3) + contract tests in CI against the chosen server (Q1); Redis in-flight leases (crawl hosts, embedding); batched dequeue/completion; wake-up coalescing and NOTIFY debounce; sorted upserts and totals-last; `MaxConcurrentSearches` from pool; nav-link claim cache; selectivity rule for V; capacity metrics and dashboard; FakeGateway throughput model; `MinBuild` on job payloads | cheap, single-host-safe, and each removes a known multi-instance defect |
| **P2** | 3-host topology (D, A, I) with routed LAN, compose split, `init-deployment --role`; object store server; `NER_LANES`; `EmbeddingCache` via `Chunks`; scale test (`FakeSites`, `ScaleSeed`) before the rollout; query lane if Q4 agrees; K1 decided before going multi-host | departments 2–20 arrive (README P2) |
| **P3** | 5-host topology; PgBouncer, read replica, broker, binary-quantized index, OpenSearch (O1), CUDA parser, Kubernetes, Redis HA, distributed object store, `ChunkSetCollections` — **each only when its §6 trigger fires** | measured, not planned |

## 9. Changes to other documents

| File | Section | Change |
|---|---|---|
| `../01-general-idea.md` | §10 Decisions | Add row "Performance pass (2026-10-30)": the four answered decisions of §10.1 and a pointer to this document |
| `platform/data-model.md` | §1.1 | Add role `ume_rag_monitor` (`pg_monitor`, limit 2); `max_connections` 200 when the 5-host budget applies; budget rule and table of §3.1 (pilot values unchanged); worker limit becomes a sum over instances (32 at 5 hosts) |
| `platform/data-model.md` | §2.2 | `CrawlHosts`, `CrawlRuns`: `fillfactor = 50`; new main-tier table `LeaderLeases` (§3.5); P2 index `IX_Chunks_Profile_TextHash` with the embedding-cache lookup via `Chunks` (behind its trigger, §3.2) |
| `platform/data-model.md` | §3.2, §12.16 | HNSW index on `("Embedding"::halfvec(1024)) halfvec_cosine_ops` (column stays `vector`) if Q2 agrees; §12.16 "halfvec in P2" → "halfvec index from P1, column stays vector, measured with evaluation sets" |
| `platform/data-model.md` | §4.2 | "overridable in `Settings.WorkerConcurrency`" → per-instance defaults are `Worker:Concurrency:<class>` (deployment); `Settings.WorkerConcurrency` holds fleet-wide shares only; batched dequeue (`LIMIT $5`) |
| `platform/data-model.md` | §4.3 | NOTIFY debounce (≤ 1 per class per 200 ms per process, none when handled locally) with the `async.c` commit-lock reason; LISTEN on `ConnectionStrings__ragdb-listen` |
| `platform/data-model.md` | §9 | `IBlobStore` contract of §3.13 (atomic put, promote, prefix delete, listing only for sweeps/audits); S3 buckets per tier and per-service access keys |
| `platform/data-model.md` | §11 | Keys `ume-rag:crawl:inflight:{hostMac}`, `ume-rag:embed:inflight` (sorted sets, TTL = lease) |
| `platform/repo-and-deployment.md` | §2.2 | Add `AWSSDK.S3` (≥ 7 days old at pin time; 4.0.104.6 checked here) to `Directory.Packages.props` for `Ume.Rag.Infrastructure` |
| `platform/repo-and-deployment.md` | §3 | `Worker:Concurrency:<class>`; `Worker:Maintenance` may be `true` on several instances (lease leaders); `Admin:Retention` on both slots (lock-guarded batches); `BlobStore:*`, `Embedding:GlobalMaxInFlight`, `Parser:Endpoints` (optional), `ConnectionStrings__ragdb-listen`, `ConnectionStrings__ragdb-read` (by trigger); fix "concurrency per class stays in `config."Settings"`" |
| `platform/repo-and-deployment.md` | §6.1–§6.4 | Multi-host: routed LAN + peer firewall lists, compose split per role, `init-deployment.sh --role data|api|ingestion|processing|all`, host-local parser/renderer/clamd (§5.4); object store replaces the `rag-blobs` volume beyond one host |
| `platform/repo-and-deployment.md` | §6.2 | `DOTNET_GCHeapHardLimitPercent=75` on worker, admin, tools, migrations; `shared_preload_libraries = 'pg_stat_statements'`; 64 GB database-host settings (§3.2) as the multi-host profile; `wal_compression = lz4` if the image supports it |
| `platform/repo-and-deployment.md` | §6.3 | Secrets `BlobStore__S3__AccessKey`/`SecretKey` per service; `Security__CrawlKeyPepper` (worker) |
| `platform/repo-and-deployment.md` | §7.3 | Per-host rolling order (§5.4) |
| `platform/repo-and-deployment.md` | §8 | New `scale.yml` (manual) and `tests/scale/` (`FakeSites`, `ScaleSeed`) |
| `platform/repo-and-deployment.md` | §9 | Capacity metrics and dashboard of §3.14; OTel `postgresql` receiver |
| `platform/repo-and-deployment.md` | §11 | P0 items of §8 (seams, budget script, monitor role, GC env) |
| `platform/plan.md` | §2 | Production placement per §5 (3- and 5-host tables); R1 rule extended to the scale test |
| `ingestion-worker/design-engine.md` | §7.6 | "write-to-temp then rename/put-if-absent" → `IBlobStore.PutAsync` atomic, `Overwrite` correct for content-addressed keys |
| `ingestion-worker/design-engine.md` | §9.1, §9.3 | `Worker:Concurrency:<class>`; batched dequeue; coalesced wake-ups; `MinBuild`; `IJobQueue` |
| `ingestion-worker/design-engine.md` | §2.3, §9.4 | Drift sweep, partition maintenance and content retention as lease leaders (§3.5) |
| `ingestion-worker/design-engine.md` | §14, §18 | Sets > 5,000 chunks on a dedicated short-lived connection; ≥ 2 parallel writers at bulk rates; 10× capacity table pointer (§2) |
| `ingestion-worker/design-connectors.md` | §5.4 | `incrementalSchedule` default in the municipal `website` template when sitemaps carry `lastmod` |
| `ingestion-worker/design-connectors.md` | §6.2, §6.3 | Nav-link claim cache; connection cap via Redis in-flight lease with ⌈cap ÷ k⌉ fallback (replaces "k × the cap … accepted") |
| `ingestion-worker/design-connectors.md` | §8.2, §11 | Renderer and clamd host-local per fetch host; ClamAV scans a local temp file, then one `PutAsync` (no `tmp/` rename) |
| `ingestion-worker/design-processing.md` | §11 | Global embedding cap; batch 32 / 8,192 at scale; bulk share; query lane (if Q4) |
| `ingestion-worker/design-processing.md` | §10.4 | `NER_LANES` |
| `parser/design.md` | §10.1, §13.1 | `NER_LANES` (default 1) and `capacity.ner_slots` = lanes; optional `PARSER_TLS_CLIENT_CA` for the multi-endpoint mode |
| `knowledge-api/design.md` | §6.2, §6.4 | V on `"Embedding"::halfvec(1024)` (if Q2); selectivity rule with `SET LOCAL hnsw.max_scan_tuples`; `minHnswSelectivity` in `Settings.Retrieval` |
| `knowledge-api/design.md` | §6.6 | `MaxConcurrentSearches` default ⌊(pool − 3) ÷ 2⌋ = 11, validated at start; optional `ConnectionStrings__ragdb-read` |
| `knowledge-api/design.md` | §13 | O1 triggers restated (§3.3: HNSW memory, V/retrieval latency, filtered recall, recall/facets) |
| `chat-api/design.md` | §12.2 | Sorted keys; `AssistantDailyTotals` upsert last in the batch |
| `admin/design.md` | §2.2 | `RetentionService` on both slots with lock-guarded idempotent batches |
| `postponed-decisions.md` | O1, K1 | O1 trigger text as §3.3; K1 "decide by" adds "before the first multi-host deployment" (§5.4); new row S3 = object store server (Q1) |
| `to-verify.md` | new rows 114–126 | §11 items (one row each), phase and method as listed there |
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

### 10.2 Open (user)

None.

## 11. To verify

| # | Item | Phase | How |
|---|---|---|---|
| 114 | S3 server choice (Q1) passes the `IBlobStore` contract test with `AWSSDK.S3` (`WHEN_REQUIRED` checksums, path style, `UseChunkEncoding`/`DisablePayloadSigning`, `If-None-Match: *`, range GET, multipart) | P1 | test |
| 115 | AWSSDK.S3 version at pin time (7-day rule; 4.0.104.6 read here) and its `AWSSDK.Core` | P1 | source |
| 116 | Licence terms of the chosen object store (AGPL-3.0 for Garage/MinIO, AIStor Free licence) for the municipality (postponed P2) | P2 | ask (legal) |
| 117 | HNSW element size on disk for `vector(1024)` vs `halfvec(1024)` vs `bit(1024)` (`pg_relation_size`) and recall delta of halfvec on the evaluation sets | P1 | measure |
| 118 | Filtered HNSW recall and latency by selectivity band at 1M/5M/10M with `relaxed_order` and per-query `max_scan_tuples` | P2 (scale test) | measure |
| 119 | Npgsql binary COPY into `Chunks` with `Vector` and the generated `SearchVector`; HNSW insert cost at 10M (extends #21) | P1/P2 | test + measure |
| 120 | `NOTIFY` lock waits at 85 notifying commits/s with and without debounce | P2 (scale test) | measure |
| 121 | PgBouncer (1.24.1 trixie / 1.26 PGDG) with Npgsql 10 auto-prepare, `No Reset On Close`, `connect_query` warm-up, TLS both sides, SCRAM `auth_query` | P3 (by trigger) | test |
| 122 | `wal_compression = lz4` available in our Postgres image | P0 | test (`images/postgres/test.sh`) |
| 123 | OTel Collector `postgresql` receiver metric set (connections per role, dead tuples, index sizes) | P1 | source + test |
| 124 | .NET 10 GC defaults in containers (DATAS on with Server GC; `GCHeapHardLimitPercent`) | P0 | source + test |
| 125 | BGE-M3 tokens/s per GPU class and CPU at the gateway's provider; head-of-line blocking of a query behind 64- vs 32-input batches (extends #98) | P1 (R1) | measure |
| 126 | uvicorn client-certificate verification for the optional multi-endpoint parser mode | P3 | test |
