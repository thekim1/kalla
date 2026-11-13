# knowledge-api: history moved out of the design

The process sections of [`../../knowledge-api/design.md`](../../knowledge-api/design.md) (summary of decisions, milestones, changes to other documents, questions and their answers, deferred and removed items), moved here unchanged on 2026-11-13 (M-14) under their original numbers, so the citations of them still resolve. History: it does not bind; `plans/decisions.md` and the design do.

## 1. Summary of decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Placement | Search core (candidates, fusion, dedup, diversity, coverage) in the shared library **`Ume.Rag.Retrieval`**, used unchanged by the API, the evaluator (worker) and the admin test panel; access, embedding, hydration, sections and citations in `KnowledgeService` (library `Ume.Rag.Answering`, admin design §2.1), hosted with HTTP by the **knowledge role** of `Ume.Rag.Api`, behind the `IRetriever` port the Chat API calls in process (§2) |
| 2 | HTTP surface in P1 | `/v1/retrieve`, `/v1/collections`, `/v1/documents/{id}` mapped but open only to `internal` clients (`rag-internal` when chat and knowledge split); `/v1/documents/{id}/content` open to signed **download grants** issued with citations; integrations and MCP in P2 (§2.3, §4.6) |
| 3 | Errors | RFC 9457 problem details as the gateway's Data API (`ApiFaultException` + `code` extension); the OpenAI error shape stays for the chat role (§3.6) |
| 4 | `top_k` | Default **10**, max **50** (answered 1); candidates 100 per list |
| 5 | Query handling | Query used as given: no rewriting, no instruction prefix; NFC, whitespace collapsed, 1–2,000 characters (answered 3) |
| 6 | Statements | Three short statements instead of one: **keyword** candidates run **in parallel with the query embedding**, then **vector** candidates, then one batched **hydrate** (chunks, sections, documents); fusion in .NET (§6). Hybrid and the decompounder stay only if they beat vector-only on the pilot gold set, run three ways in P1a (§20; reasoning: weakness review §2) |
| 7 | Exact vs HNSW | Scopes of ≤ 10,000 chunks are searched **exactly** (B-tree on the scalar `CollectionId` + sort), larger ones through HNSW with `relaxed_order` iterative scan unless the scope is too selective; HNSW settings are role defaults, no per-query `SET`; same rule in the evaluator (§6.4) |
| 8 | Fusion | RRF k = 60, weights from `RetrievalSettings` (default 1.0 / 1.0); multi-collection queries use the platform defaults unless all collections agree; after fusion, per-collection **age decay** (`RetrievalSettings.recency`) and source **`demote`** patterns multiply the score; deterministic tie-breaks (§6.5) |
| 9 | Embedding failure | `hybrid` degrades to keyword-only with `degraded: "embedding_unavailable"` and coverage `unknown`; `vector` answers 503; the evaluator never degrades (§5.5) |
| 10 | Embedding cache | In-process cache for every level (P1 has no Redis); from the first multi-host deployment (P2) a shared Redis L2 only for `public`/`internal` behind `ISharedCache`; key is an **HMAC** of the normalised query, 5 min (§5.4) |
| 11 | Coverage | `low` only when the vector side is weak (best cosine distance above every searched collection's `coverageMaxDistance`, default 0.50) **and** the keyword side is weak (no candidate at or above `coverageMinKeywordScore`); until that floor is calibrated (`null`), `low` is decided by the vector side alone; both thresholds are set from the pilot gold set's answerable and unanswerable questions together; keyword-only → `unknown`; outcomes and near-threshold margins are metrics for calibration, never query text (§7) |
| 12 | Diversity | Dedup `(DocumentId, TextHash)`; documents of different sources with the same identity (URL) collapse to one; **max 3 chunks per document** (configurable 1–10) (§8) |
| 13 | Section expansion | From **neighbouring chunks in Postgres** (`ChunkSetId`, `Ordinal`, heading path), not from blobs; overlap stripped by a new `Chunks.OverlapChars`; ≤ 1,500 tokens per section, ≤ 8,000 per response; hits in one section merged (§9) |
| 14 | Citations | Title, page range, heading path and modified date always; public `url` or an access-checked **download link**; upload titles from the document's `DisplayName` (§10) |
| 15 | Ranking | Relevance, then per-collection **age decay** on `SourceModifiedAt` (`RetrievalSettings.recency`, off by default; the `website` template turns it on for PDFs: half-life 730 days, weight 0.15) and source **`demote`** URL patterns (default factor 0.5) for archive sections; the `modified_after` filter stays (§6.5; reasoning: weakness review §3) |
| 16 | Latency | p95 retrieval ≤ 90 ms on an embedding miss, ≤ 40 ms on a hit, at 100k chunks; alert at 250 ms (§11) |
| 17 | Result cache | None; config snapshot and embeddings are cached, results are not (§12) |
| 18 | OpenSearch seam | `ISearchIndex` replaces candidate generation only; hydration, access defence and citations stay in Postgres, which re-checks every hit (§13) |
| 19 | Rerank | **Optional**, off by default, per assistant (`QuerySteps.retrieval.rerank`: `chat` or `gateway`, absent or `off` = none), behind `IReranker` (§6.7): `ChatModelReranker` (interim, a chat model) and `GatewayReranker` (`/v1/rerank`, Berget `bge-reranker-v2-m3` at `Eu` until on-prem); enabled when the evaluation shows a gain (Decisions "Rerank pulled forward", "Gateway release 1 and shared Keycloak as built") |

## 17. Milestones

| Phase | Deliverable |
|---|---|
| P0 | `Ume.Rag.Retrieval` skeleton; stock pgvector image (0.8.7); role defaults for `hnsw.*`; `SwedishDecompounder` unit tests and one Postgres match test |
| P1 | In-process `IRetriever` for the chat role; K (lexeme OR query) / V / H+D statements with the exact/HNSW selectivity rule on the halfvec index (recall vs exact measured with the evaluation sets); RRF, age decay and `demote` multipliers (P1b), dedup, identity-group collapse, cap 3; coverage (vector and keyword; vector side alone until the keyword floor is calibrated) with per-collection thresholds calibrated from the pilot gold set and calibration metrics; `IReranker` seam (no-op, FakeGateway implementation); section expansion with `OverlapChars`; citations incl. upload names and attachment parents; download grants and `/content` streaming with envelope decryption; query embedding with L1/L2 cache, circuit and degrade; `/v1/retrieve`, `/v1/collections`, `/v1/documents/{id}` for `internal` clients; problem details, OpenAPI/Scalar; counters; metrics, traces; contract test; evaluation smoke; interaction audit; retrieval load test |
| P2 | `/mcp` early with `search_knowledge` and `list_collections` (ceiling `public`); HTTP access for integrations and tokens (`CollectionAudiences`, key scopes, rate limits); gateway-served rerank (§6.7) per assistant when the evaluation shows a gain; coverage calibration with control questions; tuning per collection with the advisory evaluation report; range requests if a PDF viewer needs them; optional Entra sign-in for downloads (§4.6) |
| P3 | MCP `get_document`, `document://` resources; multi-query/HyDE behind evaluation (answered 3); cross-profile fusion during an embedding-profile migration; OpenSearch behind `ISearchIndex` if a trigger fires |

## 18. Changes to other documents

| Document | Change |
|---|---|
| `platform/data-model.md` §2.2 `Chunks` | scalar `CollectionId uuid NOT NULL` with B-tree `IX_Chunks_CollectionId` (replaces `CollectionIds` and its GIN index); `SearchText` (text + decompounder parts) with the generated `SearchVector` over built-in `swedish`; `OverlapChars smallint NOT NULL DEFAULT 0`; **U (`ChunkSetId`, `Ordinal`)** `IX_Chunks_ChunkSetId_Ordinal`; a chunk is searchable iff `"DeletedAt" IS NULL` (new sets written with `DeletedAt` set and flipped at the swap, engine design §14); `Kind`/`ParentId` stay with every P1 row `child` |
| `platform/data-model.md` §2.2 `Documents` | retrieval reads `SourceId` (document = (source, identity)) and `DisplayName` (upload file name) and `LiveChunkSetId` on the document; no `ParentDocumentId` in P1; no `Variant`, no `DocumentSources` lookup in D (§6.3) |
| `platform/data-model.md` §3.3 | the statements of §6.2–§6.3; function `content.rag_or_query(tsvector) → tsquery` and aggregate `content.rag_tsquery_or_agg(tsquery)` over the built-in `tsquery_or`; fusion, dedup and `LIMIT` in `Ume.Rag.Retrieval`; no `SET LOCAL` |
| `platform/data-model.md` §1.1 and `app-roles.sql` | `ALTER ROLE ume_rag_api, ume_rag_worker, ume_rag_admin IN DATABASE ragdb SET hnsw.ef_search = 100; … SET hnsw.iterative_scan = relaxed_order; … SET hnsw.max_scan_tuples = 100000` |
| `platform/data-model.md` §1.2 grants | api: S on `content."Documents"` (incl. `SourceId`, `DisplayName`), `ChunkSets`, `Chunks`, S on `config."Sources"` (columns `Id`, `CollectionId`, `Type`, `Classification`, `State`, `DeletedAt`) |
| `platform/data-model.md` §2.2 `RetrievalStats` | key (`Day`, `DocumentId`, `TextHash`, `CollectionId`) + `Classification`, not `ChunkId`; `content.rag_count_retrievals` takes those arrays |
| `platform/data-model.md` §2.1 | `Collections.RetrievalSettings` schema of §12.1 (no `efSearch`; adds `coverageMinKeywordScore` and `recency`); `Sources.DemoteRules` generated from `Settings -> 'demote'`, readable by the api role; `Settings.Retrieval` defaults and platform-only values of §12.1 (P2: `rerankCandidates`, `coverageMinRerankScore`); `Assistants.QuerySteps.retrieval.rerank` (P2); P2 table `CollectionAudiences` (`CollectionId`, `Kind` `client`/`department`/`group`, `SubjectId`, `GroupValue`, same checks as `AssistantAudiences`) |
| `platform/data-model.md` §11, §12.14, §12.15 | P2 Redis key `ume-rag:qemb:{profileId:N}:{gatewayKeyId:N or -}:{queryMac}` with HMAC (`Security:CacheKeyPepper`) and the value format; in process for all levels (P1: the only cache); §12.15 resolved by §7 here |
| `ingestion-worker/design-processing.md` §6.5 | the chunker writes `OverlapChars` (0 for the first chunk of a section, tables, code) |
| `ingestion-worker/design-processing.md` §13.5 | the evaluator calls the library with a `Collections` scope over the full collection (no `ChunkSets` scope); same dedup, identity-group collapse and per-document cap (k = 10); P2: compares with and without `IReranker` |
| `identity/design.md` §3 | `CredentialKind.DownloadGrant`; caller row "Browser following a citation link — grant — subject none — client from the grant — P1" |
| `identity/design.md` §4.3, §4.4 | knowledge routes answer problem details (Data API style) with `code`; the OpenAI shape stays for chat routes; deny reason `client_not_allowed` (§2.3 gate, 403) |
| `identity/design.md` §5.3 | the proxy exposes `GET /v1/documents/*` and `/content` to the staff network; the rest of `rag-api` stays restricted to the Open WebUI host |
| `identity/design.md` §7.2, §10 | 5001 reasons `grant_invalid`, `grant_expired` and `Credential` value `grant` |
| `platform/repo-and-deployment.md` §1, §2.2 | test project `tests/Ume.Rag.Retrieval.Tests`; `tests/Ume.Rag.Benchmarks` (`RetrievalBenchmarks`, `InteractionAudit` copy); `ModelContextProtocol`/`.AspNetCore` **2.3.0** (checked); `PGVECTOR_VERSION` **0.8.7** (stock image tag) |
| `platform/repo-and-deployment.md` §3, §6.3, §7.1 | `rag-api` mounts the blob container **read-only** and reads the `rag-blob-<level>` master key files of non-`public` levels; secrets `Security__DownloadGrantKeys__<kid>`, `Security__CacheKeyPepper` made by `init-deployment.sh`; settings `Knowledge:*` (`OpenToClientKinds`, `PublicBaseUrl`, `DownloadGrantLifetime`, `DownloadsPerMinutePerAddress`, `PublishApiReference`) and `Retrieval:*` (`EmbeddingTimeout`, `MaxConcurrentSearches` 4, `ExpandNeighbourChunks`); data source pool 12 per instance |
| `platform/repo-and-deployment.md` §4.3 FakeGateway, §9 | a fake rerank endpoint (P1, for the `IReranker` seam); metrics of §15 and dashboard panels |
| `platform/repo-and-deployment.md` §4.4 / gateway setup notes | rag-api's credential key has `RequireMaxResidency` and `PiiPolicy` `Off` or `Allow`; an assigned key used for chat or search needs `PiiPolicy` `Off` or `Allow` for embeddings (§5.2) |
| `archive/area-plans/gateway-changes.md`, `design.md` | P2 item: a rerank endpoint serving `bge-reranker-v2-m3` on-prem (contract to define), honouring `x-ume-max-residency` |
| (capacity) | retrieval concurrency 4 on a pool of 12 per instance (⌊(pool − 3) ÷ 2⌋); `hnsw.max_scan_tuples` is a role default (100,000), no per-query `SET LOCAL` from selectivity; `max_connections = 100` (`deploy/postgres/postgresql.conf`) |
| `archive/area-plans/tools.md` §4, `tools/design.md` §7 | `search_knowledge` schema and output shaping of §14; `/mcp` early in P2 with ceiling `public`; `get_document` and `document://` P3; MCP download grants 1 hour |
| `archive/area-plans/admin.md` | test panel runs the knowledge core in the admin process (`KnowledgeService` in `Ume.Rag.Answering` over `Ume.Rag.Retrieval`, incl. unpublished collections), so the admin host needs `Gateway:*` for query embeddings; collection page: `RetrievalSettings` form (§12.1), coverage-threshold proposal from the evaluation set (§7) |

## 19. Questions

### 19.1 Answered (user, 2026-10-30)

1. **`top_k`**: default **10**, max **50**; the same k as the evaluator's recall@10, so chat and evaluation see
   identical lists (§3.1, §8).
2. **Citations without a public URL** (uploads, later SharePoint): link to an access-checked download
   `GET /v1/documents/{id}/content`, same `IAccessPolicy` and client ceiling, 404 otherwise; title, page and heading
   path always shown; upload titles from the `DisplayName` of a source the caller may see (§3.5, §4.6, §10).
3. **No query rewriting** in the knowledge API in P1: the query is used as given; condense stays a chat-API step;
   multi-query/HyDE only in P3 behind evaluation (§5.1, §17).
4. **No recency boost**: relevance only; callers may filter by `modified_after`; citations carry the modified date;
   revisit with evaluation (§3.1, §10.1). **Replaced** (reasoning: [weakness review §3](../passes/weakness-review.md#3-outdated-or-removed-content-keeps-being-answered)):
   per-collection age decay (`RetrievalSettings.recency`, off by default, on for PDFs in the `website` template) and
   source `demote` patterns after fusion (§6.5); the `modified_after` filter and the modified date stay.

5. **Download links for Open WebUI users** (§4.6): a signed grant link valid **7 days** (`sensitive`: 1 hour),
   reachable only from the staff network, access re-checked at every download; an expired link shows a "ask again"
   page; Entra sign-in on the download path stays a possible P2 addition.

### 19.2 Open (user)

None.

## 21. Deferred or removed (simplification pass)

Removed or deferred, with where each went:

| Item | Where it went |
|---|---|
| `CollectionIds` arrays, GIN filter `&& $scope`, shared chunk sets across collections, `collections[]` in results | scalar `CollectionId` (B-tree), one collection per chunk, `collection` in results (§3.2, §6.2) |
| `DocumentSources` visibility join, upload names chosen among visible sources | `Documents.SourceId` / `DisplayName` (§4.4, §6.3, §10.2) |
| Variant collapse (`origin`/`rendered`) | identity-group collapse of the same URL in several sources (§8) |
| Swedish Hunspell config `content.rag_swedish`, connection warm-up, `' & '` → `' | '` rewrite of `plainto_tsquery` | built-in `swedish` over `SearchText`, .NET decompounder on the query side, OR query from lexeme arrays (§5.1, §6.2) |
| Tier fan-out (`ITierResolver`), per-tier runs and cross-tier merge, tier dimension in counters and logs | one database, plain `Classification` filter (§4.3); `sensitive` isolation would be a separate deployment over `Retrieval:Mode=Http` |
| Per-query `SET LOCAL` batch (per-collection `efSearch`, `max_scan_tuples` from selectivity) | role defaults (§6.4); `efSearch` dropped from `RetrievalSettings` |
| Evaluator `ChunkSets` scope, gate sub-corpora always exact, "gated" settings, `building` collections | evaluation is advisory on the full collection; unpublished collections are not searched (§2.1, §4.5, §12.1) |
| Coverage from vector distance alone | vector **and** keyword weak (§7); calibration metrics without query text |
| Rerank in P3 between diversity and hydration | `IReranker` seam P1, gateway rerank P2 (§6.7) |
| OpenSearch index content, rebuild, outbox, `shadow` switch | bare seam note (§13); designed when a trigger fires |
| MCP `get_document`, `document://`, per-skill endpoints in P2; ceiling raisable to `internal` | P3; P2 ceiling `public` (§14) |
| Pool 25, minimum 8 | pool 12, 4 searches by the same formula (§6.6) |
| Strict 10 s fail-closed on an unconfirmed snapshot | grace window `Config:MaxUnconfirmedSeconds` (§4.1) |
| Interim team linked key for query embeddings | rag-api's credential key, acting as the object's gateway key, with explicit ceiling (§5.2) |
| Attachments as child documents (`ParentDocumentId`, `attachment` identity kind, `parent` citation field, "Bilaga *name* i *subject*", separate caps) | P2 with email (Z1, §10.3) |
| `DocumentKeys` per-document blob keys | data key in the blob header (data-model §7, §3.5) |
| Redis L2 query-embedding cache and Redis download rate limit in P1 | P2, first multi-host deployment; P1 uses the in-memory `ISharedCache` and, for downloads, the in-process rate limiter (§5.4, §3.5) |
| Skill collections in the retrieval scope (P2 RAG assistants: every linked skill's; agent: loaded skills' via `SkillVersionIds`), the per-skill MCP endpoint's collection scope (P3) | removed: the scope is the published version's direct collections (§4.2); skills carry no collections, so `SkillVersionIds` is gone |

## 22. Deferred or removed (weakness review)

Removed (reasoning: [weakness review §5](../passes/weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control)):

| Item | Where it went |
|---|---|
| `x-ume-on-behalf-of` on query embeddings and its echo check in P1 (§5.2) | from the gateway's integration phase 2; until phase 1 the pilot team's key of the profile's ceiling |
