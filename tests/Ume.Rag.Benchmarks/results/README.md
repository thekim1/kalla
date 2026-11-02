# Baseline, 2026-11-02

The tables next to this file are the first full run, on commit `29eb280` plus this project.

**Machine:** Intel Core i7-13700H (10 cores, 20 threads), 16 GB, Windows 11 with WSL2 Ubuntu 26.04, .NET 10.0.12, Docker
Desktop with `pgvector/pgvector:0.8.7-pg17-trixie` (TestKit's pin) and the migrated schema. One run, idle machine, on
AC power. Compare ratios and allocations with this baseline; absolute times move between machines.

**Data:**
- Search: synthetic Swedish text (a 50-word municipal vocabulary, 200 topics, 2,000 mid-frequency words) and
  topic-clustered `vector(1024)` embeddings in one collection. The profile holds only that collection, so HNSW search
  is unfiltered, which is the optimistic case.
- Ingestion: three real umea.se pages from `tests/fixtures/umea/pages`.
- Chat: a 250-word answer streamed in about 400 one-token deltas, with markers and links.

## Per chat turn: where the server's time goes (model and embedding call excluded)

| Stage | 10,000 chunks | 50,000 chunks | Notes |
|---|---|---|---|
| Key hash + access decision | < 1 µs | < 1 µs | `AccessBenchmarks` |
| Parse request, prompt, citations | ≈ 0.12 ms | ≈ 0.12 ms | `ChatTurnBenchmarks` |
| Retrieval engine, .NET side | ≈ 0.09 ms | ≈ 0.09 ms | `RetrievalBenchmarks`, 100 candidates per list |
| Retrieval, database (K ∥ V, fused) | **25.4 ms** (exact V) | **3.6 ms** (HNSW) | `PostgresRetrievalBenchmarks`, engine |
| Hydrate 10 hits ± 1 neighbour | 1.0 ms | 0.7 ms | |
| Streaming, per answer | ≈ 1.9 ms CPU, 3.7 MB allocated | same | `ChatStreamBenchmarks`: upstream + transducer + downstream |

## Findings, most important first

1. **Exact vector search is the slowest step of a search.** V exact takes 25 ms at 10,000 chunks and 77 ms at 50,000.
   HNSW takes 2.1–2.3 ms at both sizes. The planner rule (`ExactScanMaxChunks` 10,000, knowledge §6.4) therefore
   makes a collection just under the limit about 7× slower to search than a larger one.
   - The `exact_large` case (a large scope under 0.2 % of its profile) pays the full linear cost.
   - The cost is reading 4 KB full-precision vectors from TOAST.
   - Options: a lower `ExactScanMaxChunks`, checked against recall with the evaluation sets; an exact scan on a stored
     `halfvec` (half the bytes); `STORAGE MAIN`/`EXTERNAL` on the column.
2. **The keyword stop list is what keeps statement K fast, and it can go missing.**
   - A common-word query takes 1.9 ms with the stop list and 454 ms without it at 50,000 chunks (82 ms at 10,000), a
     factor of 240. The cost is linear in matching rows, so the 2 s statement limit (`search_unavailable`) is reached
     near 200,000 chunks.
   - The statistics behind the list (`ts_stat` over every search vector) take 39 ms at 10,000 chunks and 250 ms at
     50,000, also linear. They are read every 10 minutes, and a cold cache's first search waits for them.
   - The api role's server-side `statement_timeout` of 5 s caps that read whatever the 60 s client limit says. At about
     1M chunks the statistics never load, and with them the stop list.
   - Options: load the statistics before readiness and keep the last ones across restarts (or have the worker store
     them); sample `ts_stat` (`TABLESAMPLE`) instead of reading every vector.
3. **Chunking re-tokenises the whole chunk for every sentence it adds** (`Chunker.Packer.Place` → `Body` → `Count`).
   - On the long regulation page, chunking is 47 ms of 59 ms (79 %) and allocates 29 MB.
   - `TokenCuts.SuffixStart` is quadratic in the same way.
   - Option: count tokens per unit once and sum them, with one exact recount near the budget (SentencePiece counts
     are not quite additive at the joins).
4. **Streaming allocates about 9 KB per delta**, 3.7 MB per answer, of which 90 % is avoidable.
   - Upstream: a linked `CancellationTokenSource` with its timer per SSE line, and a `JsonNode` DOM per line. A single
     re-armed source and a `Utf8JsonReader` are 8× faster with 1 % of the allocation.
   - Downstream: an anonymous object serialised to a string per chunk. A reused `Utf8JsonWriter` is 4× faster with no
     allocation.
   - Latency is not the issue (≈ 2 ms per answer). GC pressure under concurrent streams is.
5. **Each `Candidate` allocates about 1 KB** (198 KB for 200 candidates). Its two key strings (`IdentityKey`,
   `TextKey`, hex of two hashes) are built eagerly in the constructor. Option: build them lazily, or key on the
   bytes.
6. **Decay and demote multiply fusion time by 2.3** (20 → 48 µs at 100 per list), from a NonBacktracking regex per
   candidate and URL pattern plus a substring per match. It is small in absolute terms.

## What is already good

- The access decision takes 60 ns and 96 B, flat from 10 to 1,000 assistants. `/v1/models` over 1,000 assistants
  takes 63 µs. A key hash takes 0.9 µs.
- HNSW (halfvec, relaxed order) is flat at 2.1–2.3 ms from 10,000 to 50,000 chunks.
- Hydration takes under 1 ms. A rare-term K takes 2 ms at 10,000 chunks and 7 ms at 50,000.
- The prompt build takes 91 µs. Request parsing takes 11 µs. Language guess, PII patterns (no allocation) and the
  token counter are cheap.
- The engine's own .NET overhead is 0.09 ms at the default 100 candidates and 0.16 ms at 200: noise next to the
  database.
