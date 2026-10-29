# Microsoft Kernel Memory + the .NET building blocks for Umeå's platform

Research for `01-general-idea.md` (sections 5, 5a, 6, 9). Date: 2026-10-29.
Source: shallow clone `~/git/rag-platforms/kernel-memory` (HEAD `8fc4e16`, 2026-06-08), `gh`, NuGet API, Microsoft Learn.

Legend: **[V]** = verified by reading code / NuGet / GitHub during this research. **[U]** = not verified, inference
or secondary source.

---

## 1. Kernel Memory status & summary

**Status: dead as a product. Use it only as a reference for ideas.**

- [V] The README starts with *"This is an archived research project. The code serves as a learning resource, not
  production software… No support is provided."* It also says KM is "not an officially supported Microsoft offering".
- [V] The last commit is `Research project archive (#1118)` by Devis Lucato, 2026-06-08. Before that, commits in
  Dec 2025 were for "KM²" (`archived/v2/`), a rewrite that was also archived. KM² ended up as a local CLI (`km put`,
  `km search`) on SQLite FTS. It has nothing useful for a server platform.
- [V] `gh repo view`: `isArchived: false`, but PRs are disabled ("Pull requests are disabled for this repository").
  In practice it is frozen.
- [V] The last NuGet release is `Microsoft.KernelMemory.Core` **0.98.250508.3** (May 2025). It never reached 1.0.
  Orchestrators and several handlers are marked `[Experimental("KMEXP0x")]`.
- [U] The repo does not name a successor (a grep for Agent Framework / Extensions.AI / DataIngestion finds nothing).
  Microsoft's current .NET RAG story is **Microsoft.Extensions.AI** (model abstractions),
  **Microsoft.Extensions.VectorData** (vector store abstraction; connectors now in `CommunityToolkit.VectorData.*`),
  **Microsoft.Extensions.DataIngestion** (preview ETL building blocks) and **Microsoft Agent Framework** (successor to
  Semantic Kernel). This is the de facto replacement, but Microsoft does not say so in the KM repo.

**What KM was.** A document-ingestion and RAG service in .NET with three parts:
1. A **pipeline of named steps per document** (`extract → partition → gen_embeddings → save_records`), run either
   in process ("serverless") or through queues by separate handler workers ("service").
2. Pluggable **content decoders** (PDF, Office, HTML, images through OCR), **chunkers**, embedding generators, and
   **memory DBs** (Postgres, Qdrant, Azure AI Search, Elasticsearch, Redis, SQL Server, MongoDB…).
3. A **Search/Ask API** that returns an LLM answer plus `RelevantSources` (citations), with tag filters for security
   trimming.

---

## 2. Pipeline/handler model (file paths relative to `service/`)

### How it works [V]

- **Step contract:** `Abstractions/Pipeline/IPipelineStepHandler.cs` has `string StepName` and
  `Task<(ReturnType, DataPipeline)> InvokeAsync(DataPipeline, CancellationToken)`. `ReturnType` is
  `Success | TransientError | FatalError` (`Abstractions/Pipeline/ReturnType.cs`).
- **Pipeline state = one JSON document per document:** `Abstractions/Pipeline/DataPipeline.cs` holds `Index`,
  `DocumentId`, `ExecutionId`, `Steps`, `RemainingSteps`, `CompletedSteps`, `Tags`, `ContextArguments` and `Files`.
  Each file has `GeneratedFiles` (extracted text, partitions, embeddings), each with `ArtifactType`, `ParentId`,
  `ContentSHA256` and a `ProcessedBy` list. The state is saved as `__pipeline_status.json` in *document storage*
  (`Abstractions/Constants.cs:112`), next to every intermediate artifact (`file.extract.txt`, `file.partition.N.txt`, …).
- **Pipeline definition per document:** a flat **list of step names**. The defaults are in
  `Abstractions/Constants.cs:156-184` (`DefaultPipeline`, plus variants with `summarize`). Callers can override them
  per upload (`steps:` on `IKernelMemory.ImportDocumentAsync`, `Abstractions/IKernelMemory.cs:18`). Per-document
  parameters go in `ContextArguments` (e.g. chunk size and overlap override in `TextPartitioningHandler`).
- **Idempotency inside a step:** handlers loop over files and skip any marked `AlreadyProcessedBy(this)`, so a
  retried step does not redo finished files (`Core/Handlers/TextExtractionHandler.cs`, `TextPartitioningHandler.cs`).
- **In-process orchestrator:** `Core/Pipeline/InProcessPipelineOrchestrator.cs` runs a `while (!pipeline.Complete)`
  loop. It looks up the handler by step name, invokes it, calls `MoveToNextStep()` and saves state after every step.
  Errors are thrown (`OrchestrationException(isTransient)`).
- **Distributed orchestrator:** `Core/Pipeline/DistributedPipelineOrchestrator.cs` creates **one queue per step
  name**. After a step succeeds it (1) saves state, then (2) enqueues a small `DataPipelinePointer` (index, doc id,
  execution id, steps) on the next step's queue. The comments explain how it recovers when the order breaks: if the
  saved state is "ahead" of the queue message, it calls `RollbackToPreviousStep()`. If `ExecutionId` differs because
  the document was re-uploaded mid-run, the old message is dropped. Queues: `Core/Pipeline/Queue/DevTools/SimpleQueues.cs`
  (file system), `extensions/RabbitMQ` (poison queue after `MaxRetriesBeforePoisonQueue = 20`, 500 ms delay),
  Azure Queues, Redis.
- **Hosting:** `Core/Handlers/HandlerAsAHostedService.cs` wraps each handler as an `IHostedService` that subscribes to
  its queue. `Service/Program.cs` adds them when `Service.RunHandlers` is set, so the same binary runs as API only,
  as worker only, or as both. `Core/Handlers/HandlerTypeLoader.cs` loads custom handlers by assembly and type name.

### Strengths

- **The step state machine is simple and debuggable.** Persisted per-document state, `ExecutionId` to supersede stale
  runs, and per-file `ProcessedBy` markers make retries cheap and safe. All of this is worth copying.
- **Every intermediate artifact is stored**, so you can inspect what extraction and chunking produced for a document.
  This is the same idea as Umeå's "preview each node's output".
- **Transient vs fatal errors** are part of the handler contract.
- **One binary, three roles** (API / worker / both) fits Aspire and Compose well.

### Flaws (for Umeå's needs)

- **Linear only.** A pipeline is a `List<string>`. There are no branches, switches, fallbacks or fan-out per chunk,
  so the *Detect → Switch → Auto-parse → Quality → fallback* graph in §5a cannot be expressed.
- **Untyped.** Steps talk through convention: file names and `ArtifactType` enums in a mutable bag. Nothing checks
  that a step's input exists. For example, `TextPartitioningHandler` only partitions `ExtractedText` with MIME
  `text/plain` or `text/markdown` and silently skips everything else.
- **Step names are global singletons.** There is one handler per step name per orchestrator and one queue per step.
  You cannot have two differently configured "chunk" nodes in one pipeline unless you register a new step name in code.
- **State in blob storage, not a DB.** You cannot query "all failed documents in source X". The JSON can grow large
  for big documents, and save-then-enqueue is not atomic (they work around it with rollback logic instead of a
  transactional outbox).
- **No source-level concepts.** There is no source, sync run, change detection, deletion of removed documents or
  schedule. KM only knows "a document was uploaded".
- **Queue-per-step** multiplies infrastructure, and per-step hops add latency for small documents.

---

## 3. Ingestion, chunking, storage, retrieval in KM

**Decoders** (`Core/DataFormats/`, interface `Abstractions/DataFormats/IContentDecoder.cs`: `SupportsMimeType`,
`DecodeAsync(stream)` → `FileContent` with a list of `Chunk` sections) [V]:
- PDF: `Pdf/PdfDecoder.cs` uses **PdfPig** `ContentOrderTextExtractor`, one section per page with the page number.
  No tables, no layout, no OCR fallback.
- HTML: `WebPages/HtmlDecoder.cs` uses **HtmlAgilityPack** `DocumentNode.InnerText`. There is **no boilerplate
  removal**: nav, footers and possibly script/style text end up in the index. `WebScraper.cs` does a plain HTTP GET.
- Office: OpenXML for Word and PowerPoint, ClosedXML for Excel. Images go to `IOcrEngine` (Azure Document
  Intelligence extension).
- Text and Markdown pass through.

**Chunking** (`extensions/Chunkers/Chunkers/PlainTextChunker.cs`, `MarkDownChunker.cs`) [V]: token-based with
overlap and an optional `ChunkHeader` (a prefix string per chunk). The Markdown chunker splits on separators in
priority order. **Flaw:** `TextPartitioningHandler` sets `sectionNumber = 0 // TODO: use this to store the page number`.
The extracted `.txt` is one concatenated string, so **page numbers are lost before chunking** and citations cannot
point to a page.

**Postgres connector** (`extensions/Postgres/Postgres/Internals/PostgresDbClient.cs`) [V]:
- **One table per index** (`km-<index>`) with columns `id TEXT PK, embedding vector(n), tags TEXT[] (GIN),
  content TEXT, payload JSONB`. It is created with `pg_advisory_xact_lock`, and you can supply custom `CreateTableSql`.
- Search: `ORDER BY embedding <=> @embedding` with a distance threshold and tag filters as SQL. There is **no ANN
  index by default** (no HNSW/IVFFlat in the default DDL) and **no full-text or hybrid search**. Upsert uses
  `ON CONFLICT`.
- Tags-as-`TEXT[]` (`"key:value"` strings) is a simple, fast filter model for ACL/collection trimming.

**Retrieval / Ask** (`Core/Search/SearchClient.cs`, `AnswerGenerator.cs`, `Core/Prompts/answer-with-facts.txt`) [V]:
- It embeds the query, streams matches from the memory DB, renders each one with `FactTemplate`
  (`==== [File:{source};Relevance:{relevance}]:\n{content}`), and packs facts until a token budget is used up
  (`MaxAskPromptSize - AnswerTokens - prompt`). Duplicate chunks are dropped from the prompt but kept in citations.
- The prompt says: *"Given only the facts above… If you don't have sufficient information, reply with
  '{notFound}'"*. If the answer equals `EmptyAnswer`, it returns `NoFactsResult`. This is a good "I don't know" pattern.
- **Citations:** `Abstractions/Models/Citation.cs` holds `Link`, `SourceName`, `SourceUrl`, `DocumentId`, `FileId`,
  and a list of `Partitions` (text, relevance, partition number, section number, last update). **Flaw:** citations
  are *all retrieved sources that were fed to the prompt*, not the sources the answer actually used. The model is
  told "You don't know where the knowledge comes from", so there are no inline `[1]` markers.

**Service vs serverless** [V]: `MemoryServerless` (in-process orchestrator, library inside your app) or
`MemoryService` / `MemoryWebClient` (HTTP API + queue-backed handlers, Docker image `kernelmemory/service`).
Both use the same handlers.

---

## 4. What to copy / avoid for the municipality's ingestion worker

**Copy**
1. **Per-document run record with `ExecutionId` supersession.** A newer sync of the same document cancels the
   older in-flight run. Store it in **Postgres tables** (`pipeline_run`, `node_run`), not a JSON blob.
2. **Per-node idempotency markers and stored intermediate outputs** (content-addressed by SHA-256). These are the
   basis for §5a "preview before publish" and "re-process only what the change affects". If the input hash and node
   config hash are unchanged, reuse the cached output.
3. **`Success | Transient | Fatal` result contract** on every node, with retries and back-off only for transient
   errors, and a dead-letter/review state for fatal ones.
4. **Same binary, different roles** (API / worker), toggled by config.
5. **Tag-array filters** (`TEXT[]` + GIN) for collection, visibility and ACL trimming, next to typed columns.
6. **Token-budgeted fact packing with an explicit "not found" sentinel** in the Chat API.

**Avoid**
1. A linear `List<string>` pipeline with global step names. Use a **versioned DAG of node instances** (node id,
   type, config, typed ports).
2. A queue per step. Use **one durable job queue** of "run pipeline P for document D". Execute the DAG in process
   inside the worker and checkpoint node outputs. Fan out at document level, not node level.
3. State in blob storage and save-then-enqueue without a transaction. Put state and outbox in Postgres in one
   transaction.
4. `InnerText` HTML extraction (use AngleSharp + readability-style main-content extraction) and page-less chunking
   (carry `page`, `heading_path`, `anchor` through every chunk).
5. Retrieval-only citations. Number the facts in the prompt, ask for `[n]` markers, and return only cited sources
   (keep the full retrieved set for debugging).
6. Vector-only Postgres search without an index. Use HNSW + Swedish `tsvector` + RRF (§6 of the design).
7. Depending on KM packages at all: archived, pre-1.0, no support.

---

## 5. .NET ecosystem building blocks

Versions are from the NuGet API on 2026-10-29 [V] unless marked otherwise.

| Need | Package | Version / status | Recommendation |
|---|---|---|---|
| Chat + embeddings client to the gateway | `Microsoft.Extensions.AI` + `Microsoft.Extensions.AI.OpenAI` (on `OpenAI` SDK) | 10.11.0 stable [V]; part of `dotnet/extensions` (MIT) | **Use.** `IChatClient` / `IEmbeddingGenerator` with `OpenAIClient` pointed at the gateway base URL. Middleware (logging, OTel, caching, retries) for free. |
| Vector store abstraction | `Microsoft.Extensions.VectorData.Abstractions` | 10.11.0 stable [V] | **Use at the boundary only** (see Postgres row). |
| pgvector connector | `CommunityToolkit.VectorData.PgVector` (renamed from `Microsoft.SemanticKernel.Connectors.PgVector`, last 1.74.0-preview) | 1.0.2 stable, .NET Foundation, repo `CommunityToolkit/AI` [V] | **Usable.** [V] Implements `IKeywordHybridSearchable` with **RRF** over `plainto_tsquery(<language>, …)` and a GIN `to_tsvector` index with configurable language (`PostgresSqlBuilder.cs`), so `swedish` should work. Still, for §6 (metadata filters, RRF weights, `websearch_to_tsquery`, a stored tsvector column, ACL joins) **plan on hand-written SQL via Npgsql/EF Core + `Pgvector`**. Keep MEVD as the swap seam to Qdrant. |
| pgvector types for EF/Npgsql | `Pgvector` 0.3.2, `Pgvector.EntityFrameworkCore` 0.3.0 | stable, widely used (11M downloads) [V] | **Use** for the chunks table under EF Core. |
| Ingestion building blocks | `Microsoft.Extensions.DataIngestion` (+ `.Abstractions`, `.Markdig`, `.MarkItDown`) | **10.11.0-preview.1** only, no stable [V] | **Borrow types, do not build on it yet.** It has `IngestionDocument` (Markdown-centric), `IngestionDocumentReader`, `HeaderChunker`, section and semantic chunkers (on `Microsoft.ML.Tokenizers`), enrichers (`SummaryEnricher`, `KeywordEnricher`, `ClassificationEnricher`, `ImageAlternativeTextEnricher`), `VectorStoreWriter<T>` and a **linear** `IngestionPipeline<T>` (reader → processors → chunker → chunk processors → writer) with partial success through `IAsyncEnumerable<IngestionResult>` [V, Learn docs]. Good candidates to *wrap as nodes* (e.g. HeaderChunker) once stable. Not a DAG engine. |
| Tokenizer for chunk sizing | `Microsoft.ML.Tokenizers` | 2.0.1 stable, 3.0 preview [V] | Use for token counting. The on-prem embedding model's tokenizer may differ; size chunks with a margin. |
| Agent / orchestration SDK | `Semantic Kernel` (`Microsoft.SemanticKernel`) | 1.81.0, still shipping [V]; [U] Microsoft calls Agent Framework its successor; SK gets fixes for ≥1 year after AF GA | **Do not adopt** for new code. |
| Agent Framework | `Microsoft.Agents.AI`, `.Workflows` | 1.26.0 stable [V]; [U] GA 1.0 April 2026 | Only if the Chat API needs tool-calling agents later. Not needed for RAG chat. |
| HTML parsing | `AngleSharp` | 1.8.6 stable (MIT, active) [V] | **Use.** Add `SmartReader` 0.11.1 (Apache-2.0, Readability port, built on AngleSharp [U on the dependency]) for main content, plus site-specific CSS-selector rules per source. |
| PDF text (fast path) | `PdfPig` (UglyToad) | 0.1.16 stable, still 0.x, Apache-2.0, active [V] | **Use for the fast path and Detect** (text layer present, chars per page, page count). Heavy layout, tables and OCR go to Docling. |
| SharePoint delta | `Microsoft.Graph` | 6.9.0 stable [V] | **Use.** [U] `drives/{id}/root/delta` gives a delta link per drive; store the deltaLink per source; webhooks (`subscriptions`) as trigger, with delta as the source of truth. Permissions through `Sites.Selected`. |
| Durable jobs (Postgres) | `WolverineFx` + `WolverineFx.Postgresql` | 6.57.0 stable, MIT, very active [V] | **Good fit if you want a framework**: durable local queues + transactional outbox in Postgres, retries/back-off/dead letters, scheduled messages, EF Core integration. Opinionated (its own handler conventions and codegen). |
| | `Hangfire.Core` + `Hangfire.PostgreSql` | 1.8.25 / 1.21.1 stable [V]; **LGPL-3.0** core [V: `COPYING.LESSER`], paid Pro for batches | OK for cron-style "sync source X". Weaker for high-volume per-document work. Has a dashboard. |
| | `TickerQ` | 11.5.1 [V] — **switched to commercial source-available licence from 10.5.0 (agreement dated 2026-10-13)** [V `LICENSING.md`] | **Avoid** (licence risk for a municipality). |
| | Plain Postgres queue (`SELECT … FOR UPDATE SKIP LOCKED` + `BackgroundService`) | n/a | **Recommended baseline:** ~300 lines. The run/node tables are needed anyway for the UI, so the job table *is* the domain model. Use `Cronos` or similar for schedules [U on the package choice]. |
| Workflow engines | `Elsa` 3.9.0 (MIT, active), `Microsoft.Agents.AI.Workflows` 1.26.0 | stable [V] | See §6. |
| Local orchestration | `Aspire.Hosting.PostgreSQL` | 13.6.4 [V] | Same as the gateway. |

---

## 6. Workflow/DAG engine: Elsa / Agent Framework / Wolverine / custom?

**Recommendation: a small custom DAG executor inside the ingestion worker, on a plain Postgres job queue (or
Wolverine for the queue/outbox only). Do not use Elsa or Agent Framework Workflows for the ingestion graph.**

What §5a needs: typed ports (bytes → document → sections → chunks → vectors), Switch/fallback routing, per-node
output caching and preview, versioned JSON graphs edited in **Vue Flow**, per-document runs (thousands per source),
partial re-processing by config hash, and auditability. The node catalogue is closed (`IPipelineNode` in code, no
user code).

| Option | Fit | Why not (or why) |
|---|---|---|
| **Elsa 3** (MIT, 3.9.0, EF Core Postgres persistence, Blazor designer "Elsa Studio") | Medium-low | A general workflow/BPM engine: activities, bookmarks, long-running human tasks, its own JSON schema, its own designer (Blazor, not Vue Flow). You would map your typed port system onto Elsa's untyped variables, and fight its persistence model (workflow instances, journals) for high-volume per-document runs. Heavy for a closed catalogue of ~20 nodes. Reconsider only if you later want general business workflows (approvals). [U: runtime performance at 10⁴–10⁵ docs/sync not measured.] |
| **Microsoft Agent Framework Workflows** (`Microsoft.Agents.AI.Workflows` 1.26.0) | Low | [V] Graph of executors and edges (direct, fan-out, fan-in) with checkpointing (`CheckpointManager`, `FileSystemJsonCheckpointStore`). Designed for **agent orchestration** (messages between agents, human-in-the-loop). Graphs are built in code, or declaratively in YAML aimed at Foundry agents. No typed-port data-pipeline semantics, no Postgres checkpoint store in the box [U]. Couples ingestion to a fast-moving agent SDK. |
| **Wolverine** | As the *queue*, not as the DAG | A message bus with durable Postgres inbox/outbox and sagas. You *could* model each node as a message hop (KM's distributed design), but that recreates KM's queue-per-step flaw. Use it, if at all, to carry "run document D through pipeline version V" jobs with retries. |
| **Custom executor** | **High** | The graph is small and acyclic. Topological execution per document, in memory, with outputs checkpointed per node (`node_run`: input hash, config hash, output ref, status, duration, error). Switch = edge predicates over typed properties from Detect. Try-and-score = fan-out to N parse nodes + Quality node that picks one. Port types are checked at publish time (the same validator powers the canvas's "only offer fitting nodes"). Roughly 1–2k lines including validation, which is less than integrating Elsa. Fully under your control for auditing and preview (dry-run = the same executor with writes disabled). |

Sketch: `IPipelineNode<TIn, TOut>` with `NodeDescriptor` (type id, port types, JSON-schema config for the UI form,
`IsDeterministic` for caching) → `PipelineDefinition` (versioned JSON: nodes, edges, template id) → `PipelineValidator`
(DAG, port compatibility, one Index sink) → `PipelineExecutor` (per document; checkpoint after each node; honours
`Transient/Fatal`; cancellation on superseding `ExecutionId`). Fetch/discover nodes run at *source* level and emit
document work items. Everything from Detect onward runs at *document* level. Keep these two levels separate in the
model. KM conflated them, and n8n-style tools blur them too.

---

## 7. Risks

1. **Preview packages.** `Microsoft.Extensions.DataIngestion` is still preview (10.11.0-preview.1). APIs may change.
   Wrap behind your own node interfaces if you use any of it.
2. **Connector churn.** The pgvector connector already moved package (SK → CommunityToolkit, now under .NET
   Foundation, not a Microsoft product team). Hybrid SQL is generated for you and may not match your Swedish FTS
   needs. Own the retrieval SQL.
3. **Licence drift.** TickerQ went commercial in Oct 2026. Hangfire core is LGPL with paid Pro features. JasperFx
   (Wolverine) sells commercial add-ons [U on scope]. Prefer MIT/Apache components plus a plain Postgres queue for
   core paths.
4. **Custom DAG executor = owned code.** It needs good tests (golden-document fixtures per node, executor property
   tests) and a scope guard (§5a) to avoid growing into a general workflow engine.
5. **Swedish text.** Postgres `swedish` FTS stems poorly with compounds. The Auto-parse "learning" loop and
   embeddings model quality are unmeasured [U]. Budget for an evaluation set (open question 5).
6. **PdfPig is still 0.x** and has limited layout/table fidelity. It is fine as the fast path and detector; Docling
   carries the quality.
7. **Graph delta specifics** (delta link expiry, permission changes not showing up as content changes, `Sites.Selected`
   consent per site) need a spike [U].
8. **Do not reuse KM code directly.** It is archived, pre-1.0 and `[Experimental]`. Its patterns are worth copying,
   its packages are not.

### Sources
- KM repo: README, `archived/v2/README.md`, `service/**`, `extensions/Postgres/**`, `extensions/Chunkers/**` (local clone).
- NuGet v3 API (versions), `CommunityToolkit/AI` repo (`MEVD/src/PgVector/PostgresSqlBuilder.cs`, `PostgresCollection.cs`).
- [Data ingestion – .NET (Microsoft Learn)](https://learn.microsoft.com/dotnet/ai/conceptual/data-ingestion);
  [Introducing data ingestion building blocks (devblogs, Dec 2025)](https://devblogs.microsoft.com/dotnet/introducing-data-ingestion-building-blocks-preview/).
- [CommunityToolkit.VectorData.PgVector on NuGet](https://www.nuget.org/packages/CommunityToolkit.VectorData.PgVector).
- [Semantic Kernel and Microsoft Agent Framework (devblogs)](https://devblogs.microsoft.com/agent-framework/semantic-kernel-and-microsoft-agent-framework/) [U: not opened directly; secondary summaries].
- `microsoft/agent-framework` repo tree (Workflows/Checkpointing), `Arcenox-co/TickerQ/LICENSING.md`, `HangfireIO/Hangfire` (COPYING.LESSER).
