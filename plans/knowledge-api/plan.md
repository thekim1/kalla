# Knowledge API: plan

Data-plane service that answers "which pieces of our knowledge are relevant to this question?" with ranked,
citable chunks. Used by the Chat API, by the MCP server (`search_knowledge` tool), and directly by applications that
do their own prompting. Review items applied: R1, R2, R3, R5, R7.

## 1. Scope

**Does:** hybrid retrieval over collections, metadata filters, parent-child expansion, citation metadata, access and
classification checks, query embedding through the gateway.

**Does not:** ingest or change content (worker), generate answers (chat API), manage configuration (admin).

## 2. API (first cut)

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/v1/retrieve` | Search one or more collections |
| `GET` | `/v1/collections` | Collections the caller may search |
| `GET` | `/v1/documents/{id}` | Document metadata and link (for showing a citation) |

`POST /v1/retrieve`:

```json
{
  "query": "Hur ansöker jag om bygglov för altan?",
  "collections": ["bygg-och-miljo"],
  "top_k": 8,
  "mode": "hybrid",              // hybrid | vector | keyword
  "filters": { "document_type": ["page", "pdf"], "modified_after": "2025-01-01", "tags": ["bygglov"] },
  "expand": "parent"             // none | parent
}
```

Response: results with `chunk_id`, `text`, `score`, `scores` (vector, keyword, fused), and `document` (`id`, `title`,
`url` or SharePoint link, `source_type`, `page`, `heading_path`, `modified`), the collection, and `coverage`
(`ok` / `low`). OpenAPI and Scalar as in the gateway.

Authentication and authorisation follow `../identity/plan.md`: Entra access tokens (delegated `Knowledge.Read` or
app role `Knowledge.Read.All`), `sk_` keys scoped to collections, or the in-process call from the Chat API carrying
the original caller. The client's classification ceiling (R5) and the caller's access are checked, at the current
config version, before any query runs (R7).

## 3. Retrieval design

- **Search interface** `ISearchIndex` with a Postgres implementation; Postgres is the master copy, so a future
  OpenSearch index can be rebuilt from it (`../../platforms/opensearch.md`).
- **One SQL query** over the collection index: CTE for vector candidates (pgvector HNSW, cosine), CTE for keyword
  candidates (`tsvector` with a Swedish Hunspell configuration, GIN index, `websearch_to_tsquery` with OR
  semantics), fused with reciprocal rank fusion; filters (collection, tags via GIN, type, dates) inside both CTEs.
- **Embedding profiles (R3)**: in the normal case every collection uses the one on-prem platform profile, so a query
  is embedded **once** and one HNSW index serves all collections. Chunks store `embedding_profile_id`; there is one
  partial HNSW index per profile. If an assistant spans collections in different profiles (exception), the API
  searches each profile separately and fuses the result lists with RRF, which is rank-based and so does not need
  comparable scores.
- **Shared documents (R2)**: citations point at the document store's canonical document, so the same page appears
  once in results even if it is indexed in several of the searched collections (deduplicated by document and chunk
  text hash).
- **Parent-child**: children are searched; with `expand=parent` the parent section text is returned once per parent.
- **Query embedding** through the gateway on the on-prem profile (attribution: the assistant's or caller's team);
  cached in Redis by (profile, normalised query hash) for a few minutes.
- **No-answer signal**: `coverage: low` when the best fused score is under a per-collection threshold, so callers can
  say "I don't know" without guessing.
- **Rerank**: interface only in P1; implemented in P3 if the gateway serves a reranker and evaluation shows a gain.

## 4. Performance and isolation

- p95 under 100 ms for the database part at 100k chunks, excluding the query embedding call (to confirm).
- Configuration from an in-memory cache tagged with the config version; zero config queries on the hot path.
- One database round trip per retrieve call, on the data-plane connection pool with its own `statement_timeout`;
  the worker cannot exhaust it (R1). Retrieval latency is part of the R1 release test.
- Retrieval statistics (which chunks are returned and cited) are counted asynchronously and feed the parse learner and
  evaluation; no query text is stored unless open question 2 decides otherwise.

## 5. Milestones

| Phase | Deliverable |
|---|---|
| P0 | Postgres image, Swedish Hunspell config, compound-splitting test |
| P1 | `/v1/retrieve` hybrid + filters + parent expansion, access + ceiling checks, single-profile query embedding + cache, coverage signal, document dedup, evaluation set run in CI |
| P2 | Tuning per collection (weights, thresholds) behind the evaluation gate; retrieval statistics; `search_knowledge` exposed through the MCP server |
| P3 | Rerank, cross-profile fusion if any exception profile exists, OpenSearch implementation if a trigger fires |

## 6. Open questions

Answered (2026-10-29):

1. **External access**: Chat API only during the pilot; `/v1/retrieve` opens to registered integrations in P2,
   together with the MCP server.
2. **Query logs**: counters only. Retrieved and cited chunk/document counts are stored; query text is never stored.
3. **Default chunking**: parent-child. Split by headings; ~400-token child chunks for search; the surrounding section
   (up to ~1,500 tokens) returned as context. Tuned later with the evaluation sets.
