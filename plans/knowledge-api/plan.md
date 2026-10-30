# Knowledge API: plan

Data-plane service that answers "which pieces of our knowledge are relevant to this question?" with ranked,
citable chunks. Used by the Chat API, by the MCP server (`search_knowledge` tool), and directly by applications that
do their own prompting. Review items applied: R1, R2, R3, R5, R7. Detailed design: [`design.md`](design.md) (detail
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
| `GET` | `/v1/documents/{id}/content` | Access-checked download of a document without a public URL (uploads, attachments; SharePoint in P2) |

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

Authentication and authorisation follow `../identity/plan.md`: Entra access tokens (delegated `Knowledge.Read` or
app role `Knowledge.Read.All`), `ume-rag-sk-` keys scoped to collections, or the in-process call from the Chat API carrying
the original caller. The client's classification ceiling (R5) and the caller's access are checked, at the current
config version, before any query runs (R7).

## 3. Retrieval design

- **Search interface** `ISearchIndex` with a Postgres implementation; Postgres is the master copy, so a future
  OpenSearch index can be rebuilt from it (`../../platforms/opensearch.md`).
- **Three short statements** (design §6): **keyword** candidates (`tsvector` with a Swedish Hunspell configuration,
  GIN index, `plainto_tsquery` with OR semantics) run **in parallel with the query embedding**, then **vector**
  candidates (pgvector, cosine; exact scan for scopes ≤ 10,000 chunks, HNSW above), then one batched **hydrate**
  statement; reciprocal rank fusion, dedup and diversity in .NET (`Ume.Rag.Retrieval`); filters (collection, tags via
  GIN, type, dates) in both candidate statements.
- **Embedding profiles (R3)**: in the normal case every collection uses the one on-prem platform profile, so a query
  is embedded **once** and one HNSW index serves all collections. Chunks store `embedding_profile_id`; there is one
  partial HNSW index per profile. If an assistant spans collections in different profiles (exception), the API
  searches each profile separately and fuses the result lists with RRF, which is rank-based and so does not need
  comparable scores.
- **Shared documents (R2)**: citations point at the document store's canonical document, so the same page appears
  once in results even if it is indexed in several of the searched collections (deduplicated by document and chunk
  text hash; one variant per URL, at most 3 chunks per document).
- **Section expansion**: with `expand=section` the hit's section is rebuilt from neighbouring chunks (≤ 1,500 tokens,
  returned once per section; design §9).
- **Query embedding** through the gateway on the on-prem profile (attribution: the assistant's or caller's team);
  cached by (profile, normalised query hash) for a few minutes: in Redis for `public`/`internal` queries, in process
  memory only for `sensitive` (embeddings partly reveal text).
- **No-answer signal**: `coverage: low` when the best hit's **cosine distance** is above a per-collection threshold
  (RRF scores are rank-only, so they cannot signal coverage), so callers can say "I don't know" without guessing.
- Exact SQL of the statements: design §6; indexes: `../platform/data-model.md` §3.
- **Rerank**: interface only in P1; implemented in P3 if the gateway serves a reranker and evaluation shows a gain.

## 4. Performance and isolation

- p95 under 100 ms for the database part at 100k chunks, excluding the query embedding call (to confirm).
- Configuration from an in-memory cache tagged with the config version; zero config queries on the hot path.
- Three short database round trips per retrieve call (keyword, vector, hydrate; the keyword one overlaps the query
  embedding), on the data-plane connection pool with its own `statement_timeout`; the worker cannot exhaust it (R1).
  Retrieval latency is part of the R1 release test.
- Retrieval statistics (returned and cited counts per document and chunk text hash) are counted asynchronously from
  P1 and feed the parse learner and evaluation; query text is never stored (answered 2, design §4.7).

## 5. Milestones

| Phase | Deliverable |
|---|---|
| P0 | Postgres image, Swedish Hunspell config, compound-splitting test |
| P1 | `/v1/retrieve` hybrid + filters + section expansion (HTTP for `internal` clients and download grants only), access + ceiling checks, single-profile query embedding + cache, coverage signal, document dedup, retrieval counters, evaluation set run in CI |
| P2 | Tuning per collection (weights, thresholds) behind the evaluation gate; `search_knowledge` exposed through the MCP server |
| P3 | Rerank, cross-profile fusion if any exception profile exists, OpenSearch implementation if a trigger fires |

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
   the evaluator), excludes `building` collections (except the admin test panel), and embeds queries without an
   instruction prefix (processing design §13.5, §14.3).

Answered (2026-10-30; detail in [`design.md`](design.md) §19.1):

4. **`top_k`**: default 10, max 50; the same k as the evaluator's recall@10.
5. **Citations without a public URL**: link to an access-checked download `GET /v1/documents/{id}/content` (same
   policy and ceiling, 404 otherwise); title, page and heading path always shown; upload titles from the
   `DisplayName` of a source the caller may see.
6. **No query rewriting** in P1: the query is used as given; condense stays a chat-API step; multi-query/HyDE only in
   P3 behind evaluation.
7. **No recency boost**: relevance only; `modified_after` filter; citations carry the modified date.
8. **Download links for Open WebUI users**: signed grant link valid 7 days (`sensitive`: 1 hour), reachable only from
   the staff network, access re-checked at every download; Entra sign-in on the download path a possible P2 addition.
