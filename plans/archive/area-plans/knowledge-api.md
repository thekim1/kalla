# Knowledge API: plan

Data-plane service that answers "which pieces of our knowledge are relevant to this question?" with ranked,
citable chunks. Used by the Chat API, by the MCP server (`search_knowledge` tool), and directly by applications that
do their own prompting. Review items applied: R1, R2, R3, R5, R7. Detailed design: [`../../knowledge-api/design.md`](../../knowledge-api/design.md) (detail
step 7).

## 1. Scope

**Does:** hybrid retrieval over collections, metadata filters, section expansion, citation metadata, access and
classification checks, query embedding through the gateway.

**Does not:** ingest or change content (worker), generate answers (chat API), manage configuration (admin).

## 2. API (first cut)

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/v1/retrieve` | Search one or more collections |
| `GET` | `/v1/collections` | Collections the caller may search |
| `GET` | `/v1/documents/{id}` | Document metadata and link (for showing a citation) |
| `GET` | `/v1/documents/{id}/content` | Access-checked download of a document without a public URL (uploads; SharePoint in P2) |

P1: these routes are open over HTTP only to `internal` clients (`rag-internal` when chat and knowledge are split) and,
for `/v1/documents/{id}` and `/content`, to signed **download grants** issued with citations (7 days, `sensitive` 1
hour; re-checked at every download); any other client gets 403 `client_not_allowed`. Integrations, tokens and MCP in
P2 (design §2.3, §4.6).

`POST /v1/retrieve`:

```json
{
  "query": "Hur ansöker jag om bygglov för altan?",
  "collections": ["bygg-och-miljo"],
  "top_k": 10,                   // default 10, max 50
  "mode": "hybrid",              // hybrid | vector | keyword
  "filters": { "document_types": ["page", "pdf"], "modified_after": "2025-01-01", "tags": ["bygglov"] },
  "expand": "section"            // none | section
}
```

Response: results with `chunk_id`, `text`, `score`, `scores` (vector, keyword, fused), and `document` (`id`, `title`,
`url` or `download_url`, `source_type`, `page`, `heading_path`, `modified`), the collections, `sections` (with
`expand: section`), `coverage` (`ok` / `low` / `unknown`) and `degraded`. Errors are RFC 9457 problem details with a
`code` extension, as the gateway's Data API (design §3.6). OpenAPI and Scalar as in the gateway.

Authentication and authorisation follow `identity.md`: Entra access tokens (delegated `Knowledge.Read` or
app role `Knowledge.Read.All`), `ume-rag-sk-` keys scoped to collections, or the in-process call from the Chat API carrying
the original caller. The client's classification ceiling (R5) and the caller's access are checked, at the current
config version, before any query runs (R7). For chat the scope is the collections the assistant's **published
version** links directly, and only those: skills carry no collections (design §4.2).

## 3. Retrieval design

- **Search interface** `ISearchIndex` with a Postgres implementation; Postgres is the master copy, so a future
  OpenSearch index can be rebuilt from it (`../../../platforms/opensearch.md`).
- **Three short statements** (design §6): **keyword** candidates (`SearchVector` over `SearchText` with the built-in
  `swedish` configuration, GIN index; the query is decompounded in .NET by `SwedishDecompounder` and turned into an OR
  query built from its lexeme array) run **in parallel with the query embedding**, then **vector** candidates
  (pgvector, cosine; exact scan for scopes ≤ 10,000 chunks, HNSW above), then one batched **hydrate** statement;
  reciprocal rank fusion, then per-collection age decay and source `demote` patterns (answer 7),
  dedup and diversity in .NET (`Ume.Rag.Retrieval`); filters (scalar `CollectionId` via
  B-tree, tags via GIN, type, dates) in both candidate statements.
- **Embedding profiles (R3)**: in the normal case every collection uses the one platform profile (the pilot's is
  `text-embedding-3-large` at 1024 dimensions from an `Eu` provider; on-prem BGE-M3 once the hardware exists, register
  M2), so a query
  is embedded **once** and one HNSW index serves all collections. Chunks store `embedding_profile_id`; there is one
  partial HNSW index per profile. If an assistant spans collections in different profiles (exception), the API
  searches each profile separately and fuses the result lists with RRF, which is rank-based and so does not need
  comparable scores.
- **One citation per page**: documents belong to one source, so a page crawled for two searched collections is two
  documents; results collapse them by identity (URL) and dedup by document and chunk text hash, at most 3 chunks per
  document (design §8).
- **Section expansion**: with `expand=section` the hit's section is rebuilt from neighbouring chunks (≤ 1,500 tokens,
  returned once per section; design §9).
- **Query embedding** through the gateway with the profile's model and residency ceiling (attribution: the assistant's or caller's team);
  cached by (profile, HMAC of the normalised query) for a few minutes in process memory (P1 has no Redis); from the
  first multi-host deployment (P2) also in Redis for `public`/`internal` queries, never for `sensitive` (embeddings
  partly reveal text; design §5.4).
- **No-answer signal**: `coverage: low` only when the best hit's **cosine distance** is above a per-collection
  threshold **and** the keyword list is empty or below its floor (RRF scores are rank-only, so they cannot signal
  coverage), so callers can say "I don't know" without guessing; outcomes and near-threshold distances are metrics
  (never query text) for calibration (design §7). Until the keyword floor is calibrated, `low` is decided by the
  vector side alone; both thresholds are set from the pilot gold set's answerable and unanswerable questions together,
  at the value that separates them best.
- **Hybrid has to earn its place**: in P1a the pilot gold set is run three ways
  (vector-only, hybrid, hybrid without the decompounder) and the result recorded; hybrid and the decompounder stay
  only if they beat vector-only.
- Exact SQL of the statements: design §6; indexes: `../../platform/data-model.md` §3.
- **Rerank**: `IReranker` seam in P1 (FakeGateway implementation, off for every assistant); a gateway rerank endpoint
  for `bge-reranker-v2-m3` on-prem in P2, enabled per assistant when the evaluation shows a gain; coverage then uses
  the reranker score (design §6.7).

## 4. Performance and isolation

- p95 under 100 ms for the database part at 100k chunks, excluding the query embedding call (to confirm).
- Configuration from an in-memory cache tagged with the config version; zero config queries on the hot path.
- Three short database round trips per retrieve call (keyword, vector, hydrate; the keyword one overlaps the query
  embedding), on the data-plane connection pool with its own `statement_timeout`; the worker cannot exhaust it (R1).
  Retrieval latency is part of the R1 release test.
- Retrieval statistics (returned and cited counts per document and chunk text hash) are counted asynchronously from
  P1 and feed evaluation; query text is never stored (answered 2, design §4.7).

## 5. Milestones

| Phase | Deliverable |
|---|---|
| P0 | `Ume.Rag.Retrieval` skeleton on the stock pgvector image; `SwedishDecompounder` compound tests in .NET plus one Postgres match test |
| P1 | `/v1/retrieve` hybrid + filters + section expansion (HTTP for `internal` clients and download grants only), access + ceiling checks, single-profile query embedding + cache, coverage signal (vector and keyword; vector side alone until the keyword floor is calibrated), document dedup, `IReranker` seam, retrieval counters, evaluation set run in CI; P1a: three-way gold-set run (vector-only, hybrid, hybrid without decompounder) and thresholds calibrated from the gold set; P1b: age decay (`RetrievalSettings.recency`) and `demote` multipliers after fusion |
| P2 | Gateway-served rerank per assistant (advisory evaluation shows the gain); tuning per collection (weights, thresholds) with the advisory evaluation report; `search_knowledge` and `list_collections` through `/mcp` |
| P3 | Cross-profile fusion if any exception profile exists, OpenSearch implementation if a trigger fires |

## 6. Open questions

Answered (2026-10-29):

1. **External access**: Chat API only during the pilot; `/v1/retrieve` opens to registered integrations in P2,
   together with the MCP server.
2. **Query logs**: counters only. Retrieved and cited chunk/document counts are stored; query text is never stored.
3. ~~**Default chunking**: parent-child~~ **Superseded 2026-10-29** (ingestion answered 8, 22): P1 stores **flat**
   ~400-token chunks (split by headings, heading path prepended); `expand=parent` returns the surrounding **section**
   (up to ~1,500 tokens) at query time from the chunk's heading path and the stored parse output, giving the same
   context without a second chunk kind (design §9: rebuilt from neighbouring chunks, not the parse output; the
   parameter value is `section`). Retrieval runs through the shared `Ume.Rag.Retrieval` library (also used by
   the evaluator), excludes unpublished collections (except the admin test panel), and embeds queries without an
   instruction prefix (processing design §13.5).

Answered (2026-10-30; detail in [`../../knowledge-api/design.md`](../../knowledge-api/design.md) §19.1):

4. **`top_k`**: default 10, max 50; the same k as the evaluator's recall@10.
5. **Citations without a public URL**: link to an access-checked download `GET /v1/documents/{id}/content` (same
   policy and ceiling, 404 otherwise); title, page and heading path always shown; upload titles from the
   `DisplayName` of a source the caller may see.
6. **No query rewriting** in P1: the query is used as given; condense stays a chat-API step; multi-query/HyDE only in
   P3 behind evaluation.
7. **No recency boost**: relevance only; `modified_after` filter; citations carry the modified date. **Replaced**
   by per-collection age decay `RetrievalSettings.recency` = `{ halfLifeDays, weight, kinds }`, off by default, on for PDFs in the
   `website` template (730 days, weight 0.15), a multiplier `1 − weight · (1 − 2^(−age/halfLife))` on
   `SourceModifiedAt` after fusion; source `demote` URL patterns (default factor 0.5) for archive sections; the
   `modified_after` filter stays (design §6.5; reasoning: [weakness review §3](../passes/weakness-review.md#3-outdated-or-removed-content-keeps-being-answered)).
8. **Download links for Open WebUI users**: signed grant link valid 7 days (`sensitive`: 1 hour), reachable only from
   the staff network, access re-checked at every download; Entra sign-in on the download path a possible P2 addition.

## 7. Deferred or removed (simplification pass)

Removed or deferred, with where each went:

| Item | Where it went |
|---|---|
| Swedish Hunspell configuration in Postgres, `plainto_tsquery` with `' & '` → `' | '` | .NET `SwedishDecompounder` + built-in `swedish`; OR query from lexeme arrays (§3) |
| `CollectionIds` arrays with GIN filter, shared documents across collections (R2 sharing by row) | scalar `CollectionId` (B-tree); documents per source, collapsed by URL at query time (§3) |
| Coverage from vector distance alone | vector **and** keyword weak (§3) |
| Rerank in P3 | `IReranker` seam P1, gateway rerank P2 (§3, §5) |
| `building` collections, tuning "behind the evaluation gate" | removed; unpublished collections are not searched, evaluation is advisory |
| Redis query-embedding cache in P1 | P2, first multi-host deployment; P1 caches in process (§3) |
| Collections of linked skills in an assistant's scope (P2) | removed; the published version's direct collections only (§2) |
