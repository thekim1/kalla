# Hot-path benchmarks

BenchmarkDotNet benchmarks of the code that runs per chat turn, per search, per streamed token and per crawled page.
They find what is already cheap and what is worth optimising, and they catch a regression in those paths before it
ships. The setup is modelled on the gateway's `benchmarks/Ume.LlmGateway.Benchmarks`.

**Local only.** The project is not in `UmeRag.slnx`, so no workflow builds or runs it (`verify.yml` builds and tests the
solution). Run it by hand:

- when asked to;
- before merging a change to one of the paths below, comparing against the baseline in `results/`.

## What is measured

| Class | Path | Code |
|---|---|---|
| `RetrievalBenchmarks` | the .NET side of one search, with 100 and 200 candidates per list | `QueryNormalizer`, `SwedishDecompounder` (query), `Candidate`, `RrfFusion`, `RecencyAdjuster`, `DemoteAdjuster`, `ResultDiversifier`, `CoverageEvaluator`, `RetrievalEngine` over an in-memory index |
| `PostgresRetrievalBenchmarks` | the database half of a search at 10,000 and 50,000 chunks of `vector(1024)` | statements K and V (exact and HNSW), `PostgresHydrator`, `RetrievalEngine` on Postgres (no embedding call) |
| `ChatStreamBenchmarks` | per streamed token, for an answer of ≈ 400 deltas | `ChatService` reading the gateway's SSE, `AnswerTransducer`, `ChatStreaming` writing our SSE. Each `Current` benchmark copies the production code; its `Candidate` sizes a cheaper alternative |
| `ChatTurnBenchmarks` | once per turn, around retrieval | `ChatRequestParser`, `LanguageGuess`, `PromptBuilder`, `Citations` |
| `AccessBenchmarks` | per request, with 10 and 1,000 assistants | `KeyHasher`, `AccessPolicy.Decide`, `AccessPolicy.Filter` (`/v1/models`) |
| `IngestionBenchmarks` | per crawled page, on three umea.se fixture pages | `DocumentProcessor` (detect → extract → clean → chunk → personal data), and `Chunker`, `PiiPatterns`, `Bgem3TokenCounter`, `SwedishDecompounder`, `LanguageDetector` alone |

Not covered: the embedding and chat calls (the gateway; see `benchmarks/release-test` for the end-to-end chat load
test), job dequeue and the crawl (database and network bound; the planned P2 scale test, performance pass §7).

## Running

```bash
# Everything (≈ 35 minutes; the Postgres class needs Docker and seeds 60,000 chunks first)
dotnet run -c Release --project tests/Ume.Rag.Benchmarks -- --filter '*'

# One class, or one benchmark
dotnet run -c Release --project tests/Ume.Rag.Benchmarks -- --filter '*RetrievalBenchmarks*'
dotnet run -c Release --project tests/Ume.Rag.Benchmarks -- --filter '*ChatStream*Transducer*'

# A quick check that every benchmark still runs (one iteration each, numbers meaningless)
dotnet run -c Release --project tests/Ume.Rag.Benchmarks -- --filter '*' --job dry
```

Results go to `BenchmarkDotNet.Artifacts/results/` under the working directory: one Markdown table per class.

Run on an otherwise idle machine, on AC power, and close anything that uses CPU (another `dotnet test`, a browser
build). Compare **ratios and allocations** with the baseline rather than absolute times: they move less between
machines. A change of more than 10–15 % in a hot path, or any new allocation per token or per candidate, is worth a
look.

## Baseline

`results/` holds the last full run, committed with the change that produced it, and `results/README.md` names the
machine. Replace the baseline when a change moves the numbers on purpose.
