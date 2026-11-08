# OpenSearch for the Umeå knowledge platform

> Background research from 2026-10-29, kept for its evidence. It does not bind: the Decisions table and the design
> documents decide. Facts are as of the commits named below and are not kept up to date.

Evaluated 2026-10-29 against the draft `../01-general-idea.md` (sections 2, 6, 9). Sources: OpenSearch docs repo
(`opensearch-project/documentation-website`, commit 975c110, docs version 3.9.0), local clones in
`/home/crotalus/git/rag-platforms/` (onyx a4e97fc, ragflow a609e62, dify), CommunityToolkit/AI (e0a1ec3), NuGet API,
and vendor pages. **[V]** = verified in source/docs/registry today, **[U]** = unverified, recalled from memory or from a
single secondary source, or estimated.

## 1. Summary and verdict

- OpenSearch 3.x is a capable RAG backend: HNSW (Faiss/Lucene), quantization down to 1-bit, hybrid query with min-max,
  z-score or **RRF** fusion done in the engine, neural sparse, rerank processors, aggregations and highlighting, and
  analyzers that can **split Swedish compound words**. Current release: **3.9.0 (29 Sep 2026)** [V].
- For our case its real advantages over Postgres are (a) Swedish decompounding and richer analysis, (b) facets and
  highlighting at scale, (c) staying fast past a few million chunks. Its in-engine ML features (ingest chunking,
  embedding connectors) mostly overlap with things we deliberately do ourselves (Docling sidecar, gateway).
- Its cost is a second stateful JVM system for a small ops team, about 4 GB RAM minimum in practice, a security plugin
  to configure, no first-party Azure managed service, and **no Microsoft.Extensions.VectorData connector** [V].
- **Verdict: keep Postgres-only for the MVP, with the seam made explicit (an `IChunkSearch` port, not only MEVD). Treat
  OpenSearch as an optional backend we add only when a trigger in section 8 fires.** Run the Swedish benchmark
  (section 9) early. It is cheap, and it is the trigger most likely to fire.
- Postgres can do much of what decompounding gives: a Hunspell `sv_SE` ispell dictionary with compound support,
  prefix queries and `pg_trgm`. Measure that before concluding we need OpenSearch.

## 2. Capabilities relevant to RAG

**Vector search (k-NN plugin)** [V unless marked]
- Engines: `faiss` (the default for trained models and `on_disk` mode) and `lucene`. `nmslib` is marked deprecated in
  the 3.x docs (`_vector-search/api/knn.md`).
- HNSW with `m`, `ef_construction`, `ef_search`. IVF/PQ on Faiss.
- Quantization: Lucene scalar quantization (7-bit, 4-bit, 1-bit since 3.6), Faiss SQ fp16, binary quantization, PQ.
  3.9 adds `half_float` vectors, a `bf16` encoder and 2/4-bit SQ (`_about/version-history.md`).
- Disk-based search (`mode: on_disk`, 2.17+), 32x compression by default, rescoring from full-precision vectors on
  disk. Memory-optimized search (`index.knn.memory_optimized_search`, 3.1+) lets Faiss indexes be read through Lucene
  instead of being loaded fully into native memory.
- Memory formula from the docs: `1.1 * (dim * bits/8 + 8 * m)` bytes per vector
  (`_vector-search/optimizing-storage/lucene-scalar-quantization.md:188`). Worked out for dim 1024, m 16 [estimate]:
  float32 ≈ 4.6 KB/vector → **100k ≈ 0.46 GB, 3M ≈ 14 GB**. fp16 ≈ 7 GB at 3M, 1-bit ≈ 0.85 GB at 3M.
- Filtering: efficient pre-filtering during k-NN (Lucene and Faiss), post-filtering, nested k-NN, radial search, MMR
  (`_vector-search/filter-search-knn/`, `specialized-operations/`).

**Hybrid search (neural-search plugin + search pipelines)** [V]
- A `hybrid` compound query runs lexical and k-NN sub-queries in parallel. A search pipeline then combines them:
  `normalization-processor` (min_max, l2, z_score; arithmetic/geometric/harmonic mean with weights) or
  `score-ranker-processor` (**RRF**, `rank_constant` default 60, per-clause weights, since 2.19)
  (`_search-plugins/search-pipelines/score-ranker-processor.md`).
- Hybrid queries support aggregations, sorting, collapse, pagination, search_after, inner hits, explain and
  pre/post-filtering (`_vector-search/ai-search/hybrid-search/*.md`). Post-filter is the documented way to get facets
  that ignore the active filter.

**Sparse and neural sparse** [V]
- `sparse_vector`/`rank_features` fields, `neural_sparse` query, ANN for sparse (`neural-sparse-ann.md`), two-phase
  processor. Pretrained OpenSearch sparse models include `opensearch-neural-sparse-encoding-multilingual-v1` and a
  matching tokenizer (`_ml-commons-plugin/pretrained-models.md:73-75`).
- Swedish quality of the multilingual sparse model is unknown [U]. Running it in-cluster also means running a model
  in the JVM node, which goes against principle 3 (every model call goes through the gateway).

**Rerank** [V]
- `rerank` response processor: `ml_opensearch` (cross-encoder via ML Commons, local or remote connector) and `by_field`
  (`_search-plugins/search-pipelines/rerank-processor.md`). A remote rerank connector could point at the gateway if it
  ever serves a reranker [U: needs a connector request/response mapping for the gateway's API].

**Ingest pipelines** [V]
- `text_chunking` processor (`fixed_token_length` default 384 tokens on the standard tokenizer,
  `fixed_char_length`, `delimiter`), `text_embedding`, `ml_inference` (`_ingest-pipelines/processors/`).
- ML Commons **connectors can call an external OpenAI-compatible endpoint** (blueprints exist for OpenAI). They are
  restricted by `plugins.ml_commons.trusted_connector_endpoints_regex`. A gateway on a private IP also needs
  `plugins.ml_commons.connector.private_ip_enabled=true`, optionally narrowed with
  `trusted_connector_private_endpoints_regex` (`_ml-commons-plugin/cluster-settings.md:84-90`).
- **For us:** chunking is better done after Docling with structure-aware rules, and embeddings should be called from
  the .NET worker. That keeps one code path per backend and puts gateway keys per team in our code, not in cluster
  connector config. Use OpenSearch as a store and query engine, not as the pipeline.

**Other** [V]: aggregations (terms, date histogram, nested) for facets; highlighting (unified/fvh with offsets);
synonyms (`synonym_graph`, which can be reloaded via search analyzers); `collapse` for one hit per document; snapshots.

## 3. Swedish language handling

**OpenSearch** [V]
- Built-in `swedish` analyzer: standard tokenizer → lowercase → Swedish stop → keyword_marker → Swedish (Snowball)
  stemmer. Supports `stem_exclusion` (`_analyzers/language-analyzers/swedish.md`). `light_swedish` stemmer also exists
  (`_analyzers/token-filters/stemmer.md:47`).
- **Decompounding:** `dictionary_decompounder` (word list) and `hyphenation_decompounder` (hyphenation patterns XML +
  word list, `min_subword_size`, `only_longest_match`). Both emit sub-tokens alongside the original
  (`_analyzers/token-filters/*-decompounder.md`). Patterns come from OFFO (Swedish `sv.xml` exists there [U: check
  licence]). The word list must be built: Hunspell `sv_SE` (LibreOffice dictionaries) plus a municipal glossary
  (bygglov, försörjningsstöd, detaljplan …) [U: dictionary licences].
- Also `hunspell` token filter (dictionary-based stemming), `synonym_graph`, `asciifolding`/ICU (ICU needs the
  analysis-icu plugin [U: bundled in the official image?]), `word_delimiter_graph` and `pattern_capture` for
  identifiers.
- Example: `bygglovsansökan` → `bygglovsansökan, bygglov, ansökan` (with the linking `s` handled by the hyphenation
  route). A query for `ansökan om bygglov` then matches it lexically [expected behaviour, not tested].

**Postgres** [V]
- The built-in `swedish` text search config is Snowball stemming only, with no decompounding.
- But Postgres **ispell dictionaries support compound splitting** ("Ispell dictionaries support splitting compound
  words"; Hunspell compound support is "basic"), PostgreSQL 18 docs, textsearch-dictionaries. A config
  `swedish_hunspell` = hunspell `sv_SE` (with `compoundwords controlled`) → swedish_stem fallback is possible on our
  self-hosted `postgres:17` container (the gateway runs `docker.io/library/postgres:17-alpine`,
  `/home/crotalus/git/llmgateway/deploy/compose.prod.yaml:29`). It is not possible on Azure Database for PostgreSQL,
  where dictionary files can't be installed [U].
- Prefix queries (`to_tsquery('swedish','bygglov:*')`) match head-first compounds. They do not match the tail
  (`ansökan` ↛ `bygglovsansökan`). `pg_trgm` gives fuzzy substring matching at a cost in precision and index size.
- **Seam caveat [V]:** the MEVD PgVector connector's hybrid search uses `plainto_tsquery(language, $1)` and
  `ts_rank_cd`, fused with RRF, and the language is configurable via `WithFullTextSearchLanguage`
  (`CommunityToolkit/AI MEVD/src/PgVector/PostgresSqlBuilder.cs:730-760`, `PostgresPropertyExtensions.cs`).
  `plainto_tsquery` ANDs all terms, which hurts recall on long natural-language questions. We will likely want our own
  SQL (OR-ed `websearch_to_tsquery`/custom tsquery, weighted title field, identifier column) instead of MEVD's
  `HybridSearchAsync`.

**Identifiers (diarienummer, form numbers, e.g. `KS 2024/00123`, `Blankett 1234`)**: both engines split these with
their standard tokenizers. Either way, extract them at ingestion into a normalised keyword field or column
(`ks2024-00123`) and boost exact hits. This needs no engine change.

**Expectation [U]:** decompounding should raise lexical recall noticeably on Swedish municipal text, but dense
embeddings already bridge many compound cases. The net gain after RRF is what matters, and only a benchmark settles it.

## 4. Ops and footprint (Azure + on-prem)

**Minimum realistic Docker node** [V for settings, U for sizing]
- Single node: `discovery.type=single-node`, `OPENSEARCH_INITIAL_ADMIN_PASSWORD` (strength-checked), host
  `vm.max_map_count=262144`, memlock ulimits, nofile 65536. The default heap is 1 GB. Docs examples use 512m (dev) and
  2g. The guidance is heap ≈ 50% of RAM, the rest for page cache and native k-NN memory
  (`_install-and-configure/install-opensearch/docker.md`, `index.md:137-139`).
- Realistic: **4 GB RAM / 2 vCPU for ≤ 500k chunks; 8–16 GB for 1.5–3M chunks** [estimate]. Data point [V]: Onyx's
  benchmark of **1.57M chunks, 384-dim**, ran at ~7.3 GiB container RAM (float32), hybrid median 170 ms, p95 269 ms, ~21
  QPS at 4 concurrent requests. 7-bit/1-bit quantization cost about 1 point of recall@50 and saved little RAM
  (`onyx/backend/onyx/document_index/opensearch/QUANTIZATION_BENCHMARK.md`). Onyx runs `opensearch:3.6.0` with
  `-Xms2g -Xmx2g` (`onyx/deployment/docker_compose/docker-compose.yml:415-447`).
- `vm.max_map_count` is a host sysctl. On Docker Desktop/WSL and some locked-down municipal VMs that is an extra ops
  request [V: docker.md:73-83].

**Security plugin**: the demo config is not for production. Production needs its own TLS certificates (transport TLS
is mandatory with security enabled), `internal_users.yml` / roles mounted as volumes, and `securityadmin.sh` or the
REST API to apply changes. OIDC is possible for Dashboards. 3.7 added scoped API keys [V: version history]. Effort:
about 1–3 days the first time, plus certificate rotation [U]. Disabling security (`DISABLE_SECURITY_PLUGIN=true`) on a
private Docker network is common (Onyx keeps it on with an admin password, RAGFlow keeps transport TLS on), but it does
not suit a store of personal data under our audit requirements.

**Upgrades** [U]: rolling upgrades within a major. Indexes are readable one major back, so a 2.x→4.x jump needs a
reindex. k-NN and plugins ship with the image. Analyzer or mapping changes need a reindex (alias swap). Onyx built a
whole "reindex port" subsystem for this (`backend/onyx/document_index/opensearch/port_copy.py`).

**Backups** [V]: snapshot API to filesystem, S3 or **Azure Blob** (`repository-azure`, keystore credentials),
Snapshot Management policies (`_tuning-your-cluster/availability-and-recovery/snapshots/`). Since Postgres stays the
source of truth for documents and chunks, OpenSearch could be treated as a **rebuildable index**: snapshots are only a
shortcut to avoid re-embedding.

**Managed on Azure**
- No first-party Azure OpenSearch service [V by absence; Microsoft's managed search is Azure AI Search].
- Instaclustr (NetApp) managed OpenSearch on Azure, including RIYOA via Azure Marketplace, and Aiven for OpenSearch on
  Azure regions [U: secondary sources, check Sweden Central availability and DPA].
- Elastic offers "Elastic Cloud (Elasticsearch) – An Azure Native ISV Service" (learn.microsoft.com
  partner-solutions/elastic) [V]. Elastic is a different product and API: the clients are not interchangeable with
  OpenSearch 3.x [U for wire compatibility details].

**Licence** [V]: OpenSearch is Apache 2.0 (Linux Foundation project since 2024 [U]). Elasticsearch: SSPL/ELv2 since
2021, with AGPLv3 added as an option in Aug 2024. Elastic's clients are Apache 2.0. For a municipality, both are
usable self-hosted. OpenSearch has the simpler licence story.

## 5. .NET support

- **OpenSearch.Client / OpenSearch.Net 2.3.0**, published 2026-10-28 [V NuGet], ~32M/35M downloads, targets
  netstandard2.0/2.1. It is the NEST-derived high-level client. It is maintained (monthly releases since 2.0.0 in Jul
  2026), but its typed coverage of newer plugin features (hybrid query, search pipelines, ML Commons) lags. Expect to
  send some requests as raw JSON via the low-level client [U: verify hybrid/`search_pipeline` typing in 2.3.0].
- **Testcontainers.OpenSearch 4.16.0** exists [V], which helps integration tests.
- **No Microsoft.Extensions.VectorData connector for OpenSearch** [V]: none in CommunityToolkit/AI `MEVD/src`
  (AzureAISearch, AzureCosmosDB, AzureDocumentDB, Chroma, InMemory, Milvus, PgVector, Qdrant, Redis, SqlServer,
  SqliteVec, Weaviate), and none on NuGet search. For Elasticsearch, Elastic's
  `Elastic.SemanticKernel.Connectors.Elasticsearch` 0.3.1 (Oct 2025, on `Elastic.Clients.Elasticsearch` 8.19, MEVD
  9.7) exists but looks stale, and it is not OpenSearch-compatible [V for metadata].
- Note [V]: the MEVD connectors moved from semantic-kernel to `CommunityToolkit/AI` (semantic-kernel
  `dotnet/src/VectorData/PgVector/README.md`). Hybrid (`IKeywordHybridSearchable`) is implemented for PgVector,
  Qdrant, Weaviate, AzureAISearch, Cosmos, SqlServer, Milvus, Chroma.
- **Implication:** MEVD's common denominator (vector + simple keyword hybrid + LINQ filter) cannot express analyzers,
  facets, highlighting or pipelines. Make the seam our own `IChunkIndex`/`IChunkSearch` port (upsert chunks, delete by
  document, hybrid search with filters, optional facets and highlights). Use MEVD inside the Postgres adapter if
  convenient, and write an OpenSearch adapter later against OpenSearch.Client (~1–2 weeks [U]).

## 6. How other platforms use search engines

**Onyx: has moved from Vespa to OpenSearch** [V]
- `backend/onyx/document_index/` contains only `opensearch/` (plus `disabled.py`). `factory.py` builds
  `OpenSearchDocumentIndex` only. The default compose has no Vespa service, and `opensearch:3.6.0` "is the search
  backend and is enabled by default" (`deployment/docker_compose/docker-compose.yml:415-418`).
- Migration trail: `backend/alembic/versions/cbc03e08d0f3_add_opensearch_migration_tables.py` then
  `3067343245d1_drop_opensearch_migration_tables.py`. Remaining `vespa` mentions are legacy names (e.g. chunk-count
  columns).
- Design: Lucene engine, `cosinesimil`, `m=32`, `ef_construction=256`, optional SQ encoder (`schema.py:404-426`,
  `constants.py:27-56`). Hybrid uses the **normalization-processor (min_max or z_score) with weights**, not RRF
  (`search.py:75-130`). Text analyzer default `english`, configurable with `OPENSEARCH_TEXT_ANALYZER`
  (`configs/app_configs.py:547`). Heap 2 GB. Resource-health probes for disk, heap and k-NN circuit breaker
  (`opensearch/README.md`, `resource_health.py`).
- Their README notes a hybrid pitfall: a doc matched only by one sub-query gets 0 for the other, with no re-scoring
  pass. This is one reason RRF is simpler.

**RAGFlow** [V]
- `DOC_ENGINE` options: `elasticsearch` (default), `infinity`, `oceanbase`, `opensearch`, `seekdb`, `gaussdb`,
  `vastbase` (`docker/.env:27-36`). Compose profile `opensearch01` runs `opensearch:2.19.1` with security on and HTTP
  TLS off (`docker/docker-compose-base.yml:41-61`), with mapping `conf/os_mapping.json` (knn_vector, cosinesimil).
- But RAGFlow is mid-rewrite to Go (2,924 `.go` vs 166 `.py` files): `internal/engine/` has elasticsearch, infinity,
  oceanbase, vastbase, serenedb, and **no OpenSearch**, and `internal/server/config/environments.go:165-166` returns
  `not implemented: opensearch`. OpenSearch is a second-class backend there.

**Dify** [V]
- Pluggable vector stores via `api/core/rag/datasource/vdb/vector_type.py` (35 types incl. `opensearch`,
  `elasticsearch`, `pgvector`, `qdrant`). Implementations moved to `api/providers/vdb/vdb-*`. Default
  `VECTOR_STORE=weaviate` (`docker/.env.example:166`).
- The OpenSearch provider (`api/providers/vdb/vdb-opensearch/src/dify_vdb_opensearch/opensearch_vector.py`) is
  minimal: Faiss `l2` knn_vector, plain `match` full-text with the default analyzer (no language config), and fusion
  done in Dify's app layer (lines 210-291). This is the lowest-common-denominator pattern we want to avoid for Swedish.

**Takeaway:** the platform with the most serious retrieval engineering (Onyx) settled on OpenSearch as its single
engine. Platforms that are pluggable treat OpenSearch as one adapter among many, with little use of analyzers. None of
them configures Swedish analysis or decompounding.

## 7. Comparison table

| | Postgres + pgvector + FTS (draft) | Postgres + pg_search (ParadeDB) | OpenSearch 3.9 | Qdrant | Azure AI Search |
|---|---|---|---|---|---|
| Already operated by team | **Yes** | Same Postgres, new extension | No (JVM, new) | No | Managed (Azure only) |
| Runs on-prem Docker | Yes | Yes (self-built image) | Yes | Yes | **No** |
| Lexical ranking | ts_rank (not BM25) | **BM25** (Tantivy) | **BM25** | BM25 on text index (1.20 [U]) | BM25 |
| Swedish stemming | Snowball | Yes (stemmer=swedish) [V] | Snowball, light, hunspell | Snowball [U] | Lucene `sv.lucene` / `sv.microsoft` |
| Swedish decompounding | Hunspell ispell, basic [V] | Not documented [V] | **dictionary + hyphenation** | No [U] | **Yes, `sv.microsoft`** [V] |
| Hybrid fusion | Own SQL RRF (MEVD has RRF) | Own SQL RRF | **In-engine RRF / normalization** | In-engine RRF (Query API) | In-engine RRF |
| Facets / highlighting | GROUP BY / ts_headline (slow at scale) | Facets, highlighting [U] | **Mature** | Facets (limited) [U] | Mature |
| Vectors at 3M × 1024 | HNSW/halfvec OK, RAM-heavy [U] | same (pgvector) | Good, quantization, on_disk | **Very good** | Good, costs scale with tier |
| Transactions with metadata/ACL | **Same DB, joins** | Same DB | Separate, eventual sync | Separate | Separate |
| MEVD connector | **Yes** (hybrid) | No (use SQL) | **No** | Yes (hybrid) | Yes (hybrid) |
| Licence | PostgreSQL / PostgreSQL | **AGPLv3** / commercial [V] | Apache 2.0 | Apache 2.0 | Proprietary SaaS |
| Extra footprint | ~0 | ~0 (+ image build) | 4–16 GB node | 1–8 GB [U] | Per-SU pricing |

pgvectorscale (StreamingDiskANN, PostgreSQL licence) and Azure's `pg_diskann` lower vector RAM inside Postgres.
Neither pg_search nor pgvectorscale is offered on Azure Database for PostgreSQL (PG17/18 list has `vector` 0.8.2 and
`pg_diskann` 0.6.5) [V]. That does not matter while we run our own Postgres container. **Vespa** is the most capable
engine here (Onyx used it for years before moving), but it is heavier to run and less known locally, so it is not a
fit for a small team. **Azure AI Search** has the best out-of-the-box Swedish (Microsoft analyzer with decompounding),
but it fails the on-prem requirement and adds per-tier cost.

## 8. Recommendation and switch criteria

**Now (MVP):** Postgres-only.
1. Own port `IChunkSearch` (upsert, delete by document/collection, hybrid search with ACL/collection/metadata filters,
   optional `Facets`/`Highlights` capability flags). The Postgres adapter is hand-written SQL: pgvector HNSW (`halfvec`
   if dim > 2000), a `tsvector` generated column with weights (title A, headings B, body C), OR-ed tsquery, RRF in SQL,
   and an `identifiers text[]` column with a GIN index for diarienummer/form numbers. MEVD remains optional inside the
   adapter.
2. Treat any search index as **derived**: Postgres holds chunks, embeddings and ACL. A reindex job can rebuild any
   backend from Postgres without re-embedding. This makes OpenSearch an add-on later, not a migration.
3. Ship the Swedish config as a migration (`swedish` now; `swedish_hunspell` if the benchmark shows it helps).
4. Keep an evaluation set per assistant (open question 5) so a backend switch is measured, not argued.

**Add OpenSearch as a pluggable backend when any of these fire** (each measured on our eval set):

| Trigger | Threshold |
|---|---|
| Swedish lexical recall | OpenSearch+decompounding beats best Postgres config by **≥ 5 pts hybrid recall@10** (or ≥ 10 pts lexical-only), and Hunspell-in-Postgres cannot close the gap |
| Scale | **> 2–3M chunks**, or the HNSW index no longer fits in the RAM the Postgres host can spare next to the gateway |
| Latency | Retrieval **p95 > 300 ms** at expected peak QPS (public widget) after tuning (`ef_search`, partial indexes, iterative scan) |
| Facets / UX | A product need for search-UI facets, highlighting or "did you mean" across collections (a search page, not only chat) |
| Write contention | Ingestion bursts measurably hurt gateway/chat Postgres latency, and a separate Postgres instance is not the easier fix |
| Ops readiness | The ops team accepts a JVM service with TLS/security config, snapshots and monitoring (k-NN breaker, heap, disk watermarks) |

If only scale fires and Swedish is fine, consider **Qdrant** (vector only, simpler ops) or a separate Postgres with
`pgvectorscale` first. If on-prem were ever dropped, **Azure AI Search** would beat running OpenSearch ourselves.

## 9. Proposed benchmark (≈ 3–5 days)

**Corpus:** 5–20k chunks of real Umeå content: umea.se pages, a few hundred PDFs (taxor, detaljplaner, blanketter),
and protokoll with diarienummer. Same Docling output and chunking for all arms.

**Questions:** 150–300 real questions (from the website search log, the kontaktcenter, and colleagues), each with 1–3
gold chunks/docs. Tag each question: *compound* (query term is part of a compound in the text or vice versa, e.g.
"ansökan bygglov" vs "bygglovsansökan"), *identifier* (diarienummer/form number), *paraphrase*, *English query*.

**Arms (same embeddings from the gateway, RRF k = 60, top 50 per leg):**
1. Vector only (pgvector).
2. PG `swedish` FTS + vector (draft plan), with MEVD-style `plainto_tsquery` **and** an OR-ed tsquery.
3. PG `swedish_hunspell` (sv_SE ispell with compounds → swedish_stem) + vector.
4. OpenSearch `swedish` analyzer + vector (RRF via `score-ranker-processor`).
5. OpenSearch custom analyzer: lowercase → `hyphenation_decompounder` (OFFO sv + word list from sv_SE + glossary)
   → stop → `light_swedish`, as a sub-field queried alongside the plain field, + vector.
6. (Optional) arm 5 + gateway reranker on the top 50.

**Metrics:** recall@10, recall@50, MRR@10, nDCG@10 overall and **per tag**; exact-hit rate for identifiers; p50/p95
latency at 1 and 8 concurrent requests; index size and RAM. Use `analyze`/`ts_debug` spot checks on 50 compounds to
sanity-check the tokenisation.

**Decision rule:** adopt OpenSearch only if arm 5 beats the best of arms 2–3 by the section 8 thresholds on the
*compound* slice **and** does not lose overall. Otherwise keep Postgres and, if arm 3 wins, ship the Hunspell config.
Keep the harness (a .NET console or Python script plus a docker compose with both stores) as the regression test for
future backend or embedding changes.
