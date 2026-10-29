# Ingestion worker, part A: pipeline engine, nodes, jobs, runs, preview and publish

Detail step 6a of the Ume.Rag design. Details [`plan.md`](plan.md) §2, §4 and §8–§10 (binding, incl. answered items
1–8) within the limits set by [`../platform/data-model.md`](../platform/data-model.md) §2, §4–§9,
[`../platform/repo-and-deployment.md`](../platform/repo-and-deployment.md) §3, §6, §9, [`../parser/design.md`](../parser/design.md)
§7, §8, §10 and [`../gateway-changes/design.md`](../gateway-changes/design.md) §3, §5, §7.1. Nothing is implemented.

Sibling documents, **to be written**: [`design-connectors.md`](design-connectors.md) (6b: connector internals, crawl
frontier, change detection, deletion guard, child documents) and [`design-processing.md`](design-processing.md) (6c:
auto-parse heuristics, chunking, embedding profile, personal-data detection, evaluation and the publish gate). This
document defines the contracts they plug into. Changes it needs elsewhere are listed in §21; questions in §22.

**Gateway conventions copied** (`~/git/llmgateway`, read-only):

| Convention | Gateway source | Here |
|---|---|---|
| Long-running loops are `BackgroundService`s driven by `PeriodicTimer(interval, TimeProvider)`; a final flush in `StopAsync` | `src/Ume.LlmGateway.Gateway/Pipeline/AuthFailureRecorder.cs:115-145`, `UsageWriter.cs:130-143` | dispatcher, heartbeat, reaper, scheduler, finalizer (§9) |
| `TimeProvider` injected everywhere, registered with `TryAddSingleton(TimeProvider.System)` | `src/Ume.LlmGateway.Infrastructure/InfrastructureExtensions.cs:89` | all time decisions (leases, back-off, cron), `FakeTimeProvider` in tests |
| Options classes with `SectionName`, data annotations, `ValidateDataAnnotations().ValidateOnStart()` | `src/Ume.LlmGateway.AdminApi/RetentionService.cs:11-26`, `InfrastructureExtensions.cs:57-58` | `WorkerOptions` (`Worker:*`) |
| Local schedule in Europe/Stockholm via `BudgetPeriods.Stockholm` | `src/Ume.LlmGateway.Domain/Services/BudgetPeriods.cs:11`, `RetentionService.cs:44-62` | cron evaluation (§9.6) |
| One instance does a periodic job: `pg_try_advisory_xact_lock` inside an execution-strategy transaction | `src/Ume.LlmGateway.AdminApi/ModelListSync.cs:148-153` | scheduler and finalizer leadership |
| Meters from `IMeterFactory` with one telemetry name; `[LoggerMessage]` source-generated logs | `UsageWriter.cs:616-636`, `:565-583`; `src/Ume.LlmGateway.ServiceDefaults/Extensions.cs:21`, `:83-89` | meter `Ume.Rag`, `rag.*` names (repo-and-deployment §9) |

## 1. Summary of decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Engine | Small custom DAG executor in the worker on the Postgres queue (comparison §4); executes **one document** at a time, in process, topological order (§7) |
| 2 | Work model | **Reconciliation**: desired state = every live `DocumentSources` row points to the chunk set its source's published pipeline produces from the document's current content. `EnsureCurrent(document)` enqueues only what is missing; a nightly sweep catches drift (§2.3) |
| 3 | Jobs per document | Nodes declare a job class (`fetch`, `parse`, `embed` or `inline`); the engine runs a document until it reaches a node of another class, then enqueues **one continuation job** in the same transaction as its state. Typically 2 jobs per web page, 3 per PDF (§7.4). Not a queue per step (KM lesson) |
| 4 | Stage sharing | Merkle hash per node over (type, version, canonical params, upstream hashes); `DocumentStageHash` = hash at the stage boundary, `PipelineHash` = hash at *Write*. Node ids, labels, forms and layout never enter a hash (§3.3) |
| 5 | Validation | One shared library `Ume.Rag.Pipelines` (definition, validator, hashing) used by admin **and** worker, driven only by manifest data; every node rule must be expressible in JSON Schema (§4) |
| 6 | Results | `Success | TransientError | FatalError`; capacity refusals (`busy`, `not_ready`, `duplicate_in_flight`, 429, unconfirmed config) requeue **without** an attempt (§7.3) |
| 7 | Execution id | A generation per (document, content); changes only on new content or a forced reprocess. It is a **fence**: commits check it and the job lease in the same transaction, so a superseded or lease-lost job can never write (§7.5) |
| 8 | Semantics | At-least-once execution, effectively-once effects: content-addressed blobs and cache rows, dedup keys, run items and index writes only in fenced transactions (§7.6) |
| 9 | Cache | Data-model §9 key; adds `OutputHash`; three cache classes: `durable` (parser outputs, kept while the document lives), `intermediate` (30 days), `none` (§8) |
| 10 | Fairness | Per class, departments served by fewest-in-flight first; long parse jobs **yield** after a 10-minute slice so one 2,000-page PDF cannot hold the parser for hours (§9.3, §9.5) |
| 11 | Scheduler | Cron in Europe/Stockholm, deterministic per-source jitter, catch-up collapses to one run; defaults URL lists nightly, crawls weekly Saturday night, uploads on change (§9.6) |
| 12 | Runs | Run items written only at a terminal outcome; finalizer derives run counters from items; degraded when > 5 % of processed items fail (§10) |
| 13 | Preview | Same engine with a preview context: no DB access for nodes, blobs only under `previews/{jobId}/`, *Write* never runs, *Embed* estimates by default (§11) |
| 14 | Publish | Worker computes the plan (`preview` job) and the gate (`eval` job); the admin flips the published pointer; rollback = re-publish an old version, usually a pure membership change because old chunk sets are kept 14 days (§12) |
| 15 | Index writer | One transaction per (document, pipeline hash): binary COPY in 1,000-row batches, pointer swap, membership recompute (§14) |
| 16 | Config | Each execution resolves the published version at its start and carries it through its continuations; security-relevant reads fail closed at job start **and** at commit (§15) |

## 2. Engine model

### 2.1 Two stages

| Stage | Nodes (by output type) | Writes | Shared by |
|---|---|---|---|
| **Document** | root (fetch/upload) and every node whose outputs are `RawContent` or `ParsedDocument` | document store: `Documents` facts, `NodeOutputs` (+ blobs), PII counts | every source at the same level whose pipeline has the same `DocumentStageHash` (R2) |
| **Collection** | every node with an input of `Sections` or later | `ChunkSets`, `Chunks`, `DocumentSources.ChunkSetId` | every source whose `PipelineHash` and embedding profile match: one chunk set, `CollectionIds` lists all (data-model §2.2) |

The **boundary** is the single port where a `ParsedDocument` enters a node producing `Sections` (normally *Split by
headings*). The root runs at source level in `sync`/`fetch` jobs (6b) and yields one `RawContent` per document;
everything else runs at document level (the separation KM lacked, `../../platforms/kernel-memory-and-dotnet.md:214-219`).

### 2.2 Port types

| Type | Content | Produced by |
|---|---|---|
| `RawContent` | blob reference (`documents/{id}/raw/{hash}`), media type declared and sniffed, size, URL or upload facts; carries a `Facts` bag (Detect results, inspect signals) | root nodes, *Headless render*, *Detect* (adds facts) |
| `ParsedDocument` | parser `result.json` shape (blocks with `md`, level, page, layer; tables; pages with signals; parser design §7.3), plus `Facts` | parse nodes, *Clean*, *Detect personal data* |
| `Sections` | ordered sections with heading path, page span, block refs | *Split by headings* |
| `Chunks` | `flat` or `parent-child` (subtype, data-model `Chunks.Kind`), text, heading path, page, tags, document type, token count | *Chunk*, *Metadata from URL/path* |
| `EmbeddedChunks` | `Chunks` + vector + profile id | *Embed* |
| `T` | generic, bound by the incoming edge, with a constraint list | *Switch*, *Quality check* |

### 2.3 Reconciliation

The desired state for one document `D` at level `L`:

```
for every live DocumentSources row r of D:
    v   = published version of r.Source's pipeline (confirmed config snapshot)
    want = ChunkSet(D, PipelineHash(v), profile(v), D.ContentHash)
    r.ChunkSetId == want.Id  and  r.PipelineVersionId == v
```

`EnsureCurrent(D)` compares this with the database and, for every reference that is not current, enqueues the
missing work, deduplicated: a document-stage job per missing `(ContentHash, DocumentStageHash)` whose output is not in
the cache, else a collection-stage job per missing `(ContentHash, PipelineHash, profile)`. A chunk set that already
exists (another source, or a gate shadow set, §12) only needs a membership change.

Callers: the fetch path after new content (6b), the document-stage commit, a new `DocumentSources` row, a published
pipeline version (`reprocess` run), a cleared PII flag, and the **nightly drift sweep** (`Worker:Maintenance`
instance, 01:30): `DocumentSources` rows whose `PipelineVersionId` is not the published one, or whose chunk set's
`ContentHash` differs from the document's. The sweep is what makes "no lost work" independent of any single job
surviving (Onyx F6 silent stalls, `../../platforms/onyx.md:267`; Dify documents stuck "Queuing",
`../../platforms/dify.md:265`).

**Shared documents fan out at once.** When source A's sync finds new content in a document that source B (another
department, same level) also references, B's collection stage runs in the same wave, so both indexes agree. Work for
B is recorded in B's open **follow run** (`Runs.Kind = reprocess`, `Trigger = shared-document`, one open per source,
closed by the finalizer), so B's admin page shows it and B's team pays B's LLM costs. Decided (§22).

## 3. Definition format

### 3.1 JSON (`contracts/pipeline/v1/definition.schema.json`)

Stored in `config."PipelineVersions"."Definition"`; canvas positions stay in `PipelineCanvasLayouts`, which the worker
cannot read (data-model §1.2; Dify and RAGFlow mix the two, F13). Abridged example (full templates in §13):

```json
{
  "schema": "pipeline/v1",
  "template": { "pipelineId": "0193…", "number": 3 },
  "nodes": [
    { "id": "route", "type": "switch", "typeVersion": 1,
      "params": { "on": "mediaType", "cases": { "html": ["text/html"], "document": ["application/pdf"] } } },
    { "id": "parse", "type": "auto-parse", "typeVersion": 1, "params": { "ocr": "auto", "tables": "accurate" } },
    { "id": "pii", "type": "detect-personal-data", "typeVersion": 1, "params": { "ner": true }, "locked": true },
    { "id": "chunk", "type": "chunk", "typeVersion": 1, "params": { "maxTokens": 400, "overlapPercent": 15 } }
  ],
  "edges": [ { "from": "route.document", "to": "parse.in" }, { "from": "parse.parsed", "to": "clean.in" } ],
  "form": [ { "field": "chunkTokens", "node": "chunk", "param": "maxTokens",
              "label": { "sv": "Chunkstorlek (token)", "en": "Chunk size (tokens)" } } ]
}
```

- `locked: true` freezes a node (cannot be removed, disconnected or re-parameterised) for everyone but
  `rag-platform-admin`; `lockedParams: ["…"]` freezes single parameters. `form` lists the parameters a template exposes
  (Dify `rag_pipeline_variables`, `../../platforms/dify.md:288-291`).
- Node ids: `^[a-z][a-z0-9-]{0,39}$`; ports are named by the manifest; edges are `node.port` strings.
- `template` records which template version a copy came from (§13.3).

### 3.2 Manifest entry (`content."NodeManifest"`, one row per type and version)

```json
{ "nodeType": "switch", "typeVersion": 1,
  "ports": { "inputs":  [ { "name": "in", "type": "T", "constraint": ["RawContent", "ParsedDocument", "Chunks"] } ],
             "outputs": { "dynamic": { "fromParam": "/cases", "type": "T", "plus": ["default"], "optional": true } } },
  "paramsSchema": { "$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object", "…": "…" },
  "metadata": { "jobClass": "inline", "cacheClass": "none", "category": "route", "deterministic": true,
                "displayName": { "sv": "Växel", "en": "Switch" }, "deprecated": false } }
```

`metadata` is a new `jsonb` column (§21). Port and schema of an existing (type, version) are immutable: a worker that
starts with a different definition for a registered pair **fails startup** (a change needs a version bump).

### 3.3 Hashes ("identical" stages)

For each node `n` in a validated definition, with defaults from its schema applied and params serialised as RFC 8785
canonical JSON:

```
paramsHash(n) = SHA-256(canonical(params))
h(root)       = SHA-256("root")                               (fetch, crawl and upload roots hash alike)
h(n)          = SHA-256( type ‖ typeVersion ‖ paramsHash(n) ‖
                         sort_by_port( inputPort ‖ fromPort ‖ h(upstream) ) for every incoming edge )
DocumentStageHash  = h(node feeding the boundary port)          (stored in PipelineVersions)
PipelineHash       = h(write)                                   (stored as CollectionStageHash, §21)
```

Two pipelines are identical in a stage when their hashes are equal: node ids, edge order, labels, `locked`, `form` and
layout do not count, and isomorphic graphs hash alike. The root does not count either: what it fetched is
identified by the content hash, so a URL list and a crawl of the same site share the document stage. `PipelineHash` covers the whole graph, because a chunk set
depends on the document stage too: two sources with equal collection stages but different parsers must not share
chunks (the data-model name `CollectionStageHash` stays; its meaning is fixed here). *Write* has no
collection-specific parameter, so sets can be shared; the target collections come from `DocumentSources`. The embed
profile enters the hash as the literal `platform-default` and the chunk-set key separately as the resolved id.

## 4. Validation

Run by `PipelineValidator` (`Ume.Rag.Pipelines`) in the admin on save (warnings allowed) and publish (errors block),
and **again by the worker** when it loads a published version (fail closed: an invalid published version is not run;
event 5105, §17).

| # | Rule | On |
|---|---|---|
| 1 | Definition matches `definition.schema.json`; ≤ 40 nodes, ≤ 80 edges, params ≤ 16 KB per node | save |
| 2 | Every (`type`, `typeVersion`) exists in `NodeManifest`; `deprecated` versions warn | save |
| 3 | `params` validate against the version's `paramsSchema` (JSON Schema 2020-12; library *to verify*, §23); node-specific rules live in the schema (`if`/`then`, `enum`, ranges), never in worker-only code, so the admin can check them (R7) | save |
| 4 | Edges join an existing output port to an existing input port; types equal, or a generic binds to one type within its constraint, consistently for all its ports | save |
| 5 | Acyclic; exactly one root (a node with `metadata.category = fetch` and no inputs); every node reachable from it; every required input connected | save |
| 6 | A port with several incoming edges is an **exclusive merge**: for each pair of edges, their nearest common branching ancestor (*Switch*, *Auto-parse*, *Quality check*) reaches them through different output ports | save |
| 7 | Exactly one boundary port (`ParsedDocument` → `Sections`); no edge from collection stage to document stage (follows from types, checked anyway) | save |
| 8 | Exactly one *Write*; every path from the root ends in *Write* or in an unconnected optional output (*Switch* `default`, which yields outcome `unsupported` or `skipped`) | save |
| 9 | Required locked nodes: *Detect personal data* on every path before the boundary, *Embed* with `profile = platform-default` (platform admins may set another, R3), *Write*; locked nodes and params unchanged from the template unless the editor is `rag-platform-admin` | save |
| 10 | Locked nodes at a version ≥ the template's `minVersion` for that node (§13.3) | publish |
| 11 | Every (`type`, `typeVersion`) is supported by a live worker instance (`WorkerNodeSupport` row, heartbeat < 60 s) that also runs the node's job class (`WorkerInstances.Classes`, §21) | publish |
| 12 | LLM-calling nodes (P2) on a source above `public`: model allowed at the level's residency (platform plan §6) | publish |

Errors carry `nodeId`, `port` and a code (`port_type_mismatch`, `cycle`, `merge_not_exclusive`, `locked_node_missing`,
`node_version_unsupported`, …) so the canvas and the form can point at the fault.

## 5. Node interface and manifest

### 5.1 C# sketch (`Ume.Rag.Worker/Pipelines`)

```csharp
public interface IPipelineNode
{
    NodeDescriptor Descriptor { get; }                       // type, version, ports, params schema, metadata
    NodeCost EstimateCost(NodeCostContext context);           // pure; no I/O
    ValueTask<NodeResult> ExecuteAsync(NodeContext context, NodeInputs inputs, CancellationToken ct);
}

public abstract class PipelineNode<TParams> : IPipelineNode where TParams : class   // params deserialised once per paramsHash
{ /* ExecuteAsync(NodeContext, TParams, NodeInputs, CancellationToken) */ }

public abstract record NodeResult
{
    public sealed record Success(NodeOutputs Outputs, NodeFacts Facts, NodeUsage Usage) : NodeResult;   // only active ports set
    public sealed record TransientError(string Code, TimeSpan? RetryAfter = null, bool CountsAttempt = true) : NodeResult;
    public sealed record FatalError(string Code, RunItemOutcome Outcome) : NodeResult;
}

public sealed record NodeFacts(IReadOnlyDictionary<string, JsonElement> Values, IReadOnlyList<string> Degraded); // strategy, pages, …
public sealed record NodeUsage(decimal CostUsd, long LlmTokensIn, long LlmTokensOut, long EmbeddingTokens, int ParserPages);
public sealed record NodeCost(long LlmTokens, long EmbeddingTokens, int ParserPages, TimeSpan Time);

public sealed class NodeContext   // built by the engine; nodes get no DbContext
{
    // Document (id, level, content hash, facts), ExecutionId, RunId, NodeId, Mode (Run | Preview), Deadline,
    // Blobs (run: documents/{id}/nodes/…; preview: previews/{jobId}/), Gateway (headers prefilled, §7.7),
    // Parser (parse-class nodes only), Time (TimeProvider), Logger
}
```

- **Rules**: a node is a pure function of (inputs, params, its version) apart from declared external calls (parser,
  gateway, renderer, fetch); `deterministic: false` in metadata (LLM nodes) still caches, because a re-run would pay
  again (RAGFlow GraphRAG cost lesson, F17), but preview re-runs it. Nodes never write the database: the engine
  persists. Any change in a node's output for the same input requires a `typeVersion` bump, enforced by golden tests
  (§19); old versions stay in the binary for at least one release after their successor (rolling updates).
- **Cost estimation**: `EstimateCost` receives document facts (media type, page count, text length, current chunk
  count) and params; returns tokens, parser pages and time. The admin API adds USD when it shows a preview or plan,
  from the gateway catalogue price of the model the node names (it already holds `catalog.read`), else "unknown"
  (never 0). Used by preview, the publish plan and the per-run cost forecast.

### 5.2 Registration and support

At startup, before the dispatcher dequeues anything:

1. Upsert `WorkerInstances` (`InstanceId` = new v7 id, `BuildVersion`, `StartedAt`, `Classes` from `Worker:Classes`).
2. For each node in DI: insert `NodeManifest` if absent; if present with different `Ports`/`ParamsSchema`, stop with an
   error naming the pair. Insert `WorkerNodeSupport` rows for this instance.
3. Log a warning for every **published** pipeline version that uses a pair this instance lacks.

Heartbeat every 15 s (`WorkerInstances.LastHeartbeatAt`); clean stop deletes the instance (support cascades); any
instance deletes instances silent for 5 minutes. A job whose pipeline needs a pair this instance does not support is
released (`AvailableAt + 30 s`, no attempt); after 10 minutes without any supporting instance it fails with outcome
`failed`, reason `node.version_unsupported`.

## 6. P1 node catalogue

All version 1. Class: job class (§7.4); cache: §8. Details column: which document specifies the internals.

| Type | Ports (in → out) | Key params | Class | Cache | Details |
|---|---|---|---|---|---|
| `http-fetch` (URL list root) | – → `out: RawContent` | `conditionalGet` (true), `timeoutSeconds` | fetch | none (raw blob) | 6b |
| `crawl` (crawl root) | – → `out: RawContent` | `render` (`never`/`if-enabled`/`always`) | fetch | none | 6b |
| `upload` (upload root) | – → `out: RawContent` | – (limits from source) | fetch | none | 6b |
| `headless-render` | `in: RawContent` → `out: RawContent` | `timeoutSeconds` (30), `waitFor` (`networkidle`); passes input through with fact `render.not_enabled` when the source does not allow rendering | fetch | intermediate | 6b |
| `detect` | `in: RawContent` → `out: RawContent` (+facts) | `inspect` (true: call parser `/v1/inspect` for PDF/TIFF) | inline | intermediate | 6c (signals: parser §6) |
| `switch` | `in: T` → one port per case + `default` | `on` (`mediaType`/`language`/`scanned`/`fact:<name>`), `cases` | inline | none | 6a |
| `auto-parse` | `in: RawContent` → `parsed: ParsedDocument`, `fallback: RawContent` (optional) | `ocr` (`auto`/`force`/`off`), `tables` (`fast`/`accurate`), `profiles` (true), `maxPages` (2,000) | parse | **durable** | 6c (ranges, stitching: parser §8) |
| `html-main-content` | `in: RawContent` → `out: ParsedDocument` | `cleanupRules` (`none`/`learned`), `keepTables` | inline | durable | 6c |
| `pdf-fast-path` | `in: RawContent` → `out: ParsedDocument` | `minTextLayerQuality` | parse | durable | 6c |
| `docling-parse` | `in: RawContent` → `out: ParsedDocument` | `ocr`, `ocrEngine`, `tables`, `imageScale` (parser §7.2) | parse | durable | 6c / parser §7 |
| `plain-text` | `in: RawContent` → `out: ParsedDocument` | `markdown` (auto) | inline | durable | 6c |
| `csv-table` | `in: RawContent` → `out: ParsedDocument` | `delimiter` (auto), `headerRows` (1) | inline | durable | 6c |
| `clean` | `in: ParsedDocument` → `out: ParsedDocument` | `dropFurniture` (true), `minBlockChars`, `dehyphenate` | inline | intermediate | 6c |
| `quality-check` | `in: T ∈ {ParsedDocument, Chunks}` → `pass: T`, `fail: T` | `minChars`, `maxGarble`, `minOcrConfidence`, `maxEmptyChunkRatio` | inline | none | 6c |
| `detect-personal-data` (locked) | `in: ParsedDocument` → `out: ParsedDocument` | `types` (personnummer, phone, email, name), `ner` (true) | inline | intermediate | 6c (answered 6) |
| `split-headings` | `in: ParsedDocument` → `out: Sections` | `maxLevel` (4), `relevelLayoutHeadings` (true) | inline | intermediate | 6c |
| `chunk` | `in: Sections` → `out: Chunks` | `mode` (`flat`; `parent-child` P2), `maxTokens` (400), `overlapPercent` (15), `prependHeadingPath` (true), `tableRowGroups` (true) | inline | intermediate | 6c (answered 8) |
| `metadata-from-path` | `in: Chunks` → `out: Chunks` | `rules` (pattern → tags, document type, date) | inline | none | 6c |
| `embed` (locked profile) | `in: Chunks` → `out: EmbeddedChunks` | `profile` (`platform-default`), `batchSize` (64) | embed | none (embedding cache) | 6c |
| `write` (locked) | `in: EmbeddedChunks` → – | – | embed | none | 6a (§14) |

*Detect personal data* sets the document's PII counts and flag in the document-stage commit (§7.6); its output is its
input unchanged. Email attachments returned by the parser become child documents (6b creates and scans them), each
running the pipeline itself.
P2 nodes (plan §4): SharePoint, OCR-forced shortcut, table extract, PII mask, LLM summary/keywords, date/type
extraction, describe pictures.

## 7. Execution

### 7.1 Per document

1. Load the execution's pipeline version (cached per version id; validated, §4) and its topological order (Kahn,
   ties by node id, so logs and tests are stable).
2. Node states: `pending` → `running` → `done` | `skipped` | `failed`. A node runs when each connected input port has
   an active value; it is `skipped` when all edges into some required port come from skipped or inactive outputs.
3. Before running a cacheable node, look up its cache key (§8); a hit marks it `done` with the cached output.
4. After each node: check the cancellation token (supersession, lease loss, shutdown; §7.5) and the deadline.
5. The document ends at *Write* (success), at an unconnected optional output (`unsupported` from *Switch*
   `default`, otherwise `skipped`), or at the first `FatalError`.

### 7.2 Branching and fallback

- *Switch* sets exactly one output: the first case whose list matches the bound fact, else `default`.
- *Auto-parse* sets `parsed`, or `fallback` for content it declines (6c), so a pipeline can send it elsewhere.
- *Quality check* sets `pass` or `fail`; the `fail` branch is the fallback (e.g. HTML with too little text →
  *Headless render* → *HTML main content* → merge into *Clean*, §13). A fallback cannot loop back: the graph is a DAG,
  so every fallback is bounded by its length. Facts from the failed check are kept and shown in the run item.
- An exclusive merge (§4 rule 6) takes the one active edge. The run item's `Strategy` records the path taken
  (`route=document;parse=auto[pdfpig:1-40,docling-ocr:41-44]`), so a silent "configured but did nothing" step
  (RAGFlow #14376, `../../platforms/ragflow.md:222`) shows up per document.
- No error ports in P1: a node's own error is the document's error. Per-page fallback inside parsing is *Auto-parse*'s
  job (6c).

### 7.3 Results, retries and back-off

Queue mechanics are data-model §4.2: `TransientError` → `AvailableAt = now + least(10 s · 2^Attempts, 1 h) ± 20 %`;
`FatalError` → terminal at once; attempts exhausted → `dead`. Requeues that do not count an attempt set
`Attempts = Attempts − 1` in the same statement (dequeue had incremented it).

| Cause | Result | Attempt counted | Next |
|---|---|---|---|
| Parser `busy` / `not_ready` / `duplicate_in_flight` (parser §7.5) | transient | no | `Retry-After` (default 5 s) |
| Parser `timeout`, `worker_crashed`, `resource_limit` / `ocr_failed`, `conversion_failed` | half the range inside the job (6c), single page → fatal / transient once, then fatal | – / yes | – / back-off |
| Parser 400 / 413 / 415 / 422 others | fatal: `failed`, `too-large`, `unsupported`, `encrypted`, `corrupt`; `limit_exceeded` also event 5104 | – | – |
| Gateway 429 or 503 with `Retry-After` / other 5xx, timeouts | transient | no / yes | `Retry-After` / back-off |
| Gateway 402 (budget used up) | transient | yes | `AvailableAt` = next full hour; after `MaxAttempts` `failed`, reason `gateway.budget_exhausted` |
| Gateway 403 `max_residency_not_met`, `delegation_not_allowed`; 400 | fatal, reason `gateway.<code>` | – | – |
| Gateway answer above the residency ceiling (`x-ume-residency`) | fatal, result discarded, event 5003 | – | – |
| Config not confirmed (§15) | transient | no | 2 s, then 5 s |
| Node version not supported here (§5.2) | transient | no | 30 s |
| Unhandled exception in a node | transient (`node.exception`; type name only, never the message) | yes | back-off |

Bounded redelivery with back-off and an explicit lease follows RAGFlow's queue (`../../platforms/ragflow.md:57-58`).
A job that crashes the whole process (e.g. OOM) is reclaimed by the reaper with its attempt counted, so it dies after
`MaxAttempts` instead of looping (§9.4).

### 7.4 Job segments and continuations

Each node's `jobClass` is `fetch`, `parse`, `embed` or `inline`. The engine runs nodes inside the current job while
they are `inline` or of the job's class. At the first node of another class it stops, and in **one transaction**
writes the document-stage results reached so far (if any), enqueues the continuation job (class of that node,
payload: execution id, pipeline version, node id, cache key of its input) with a dedup key, and completes the current
job (fenced, §7.5). This is the KM pointer pattern made atomic (KM saves state and enqueues separately and repairs
with rollback logic, `../../platforms/kernel-memory-and-dotnet.md:61-65`, `:161`), with at most a few hops per
document instead of a queue per step.

| Document | Jobs |
|---|---|
| Web page | `fetch` (fetch, detect, html, clean, PII, split, chunk) → `embed` (embed, write) |
| Unchanged web page (304 or same hash) | `fetch` only |
| PDF / Office | `fetch` (fetch, detect) → `parse` (auto-parse … chunk) → `embed` |
| Shared document, second pipeline | `embed` only, reading the cached boundary output |

Per-class concurrency then holds by construction: parser slots are only used by `parse` jobs, gateway embedding
load only by `embed` jobs, politeness by `fetch` (Eneo and Dify run everything through one worker, F4,
`../../platforms/eneo.md:228`, `../../platforms/dify.md:234`).

### 7.5 Execution id, supersession, cancellation

- `Documents.CurrentExecutionId` (new, §21) is set when the document's content changes, or by a `reprocess` with
  `force` (cache bypass from a node). It is **not** changed by a new pipeline version or a new reference: those only
  add collection-stage work through `EnsureCurrent`, so a 2-hour OCR job is never killed because another department
  added the same file.
- Every job of an execution carries its id. **Fence** in each committing transaction:

```sql
SELECT 1 FROM content."Documents"
 WHERE "Id" = @doc AND "Classification" = @level AND "CurrentExecutionId" = @exec AND "ContentHash" = @hash
 FOR SHARE;                                              -- blocks a concurrent supersession until commit
UPDATE content."Jobs" SET "State" = 'succeeded', "CompletedAt" = now(), "Result" = @summary
 WHERE "EnqueuedAt" = @enq AND "Id" = @job AND "State" = 'running' AND "LeaseOwner" = @me;
-- either returns no row → ROLLBACK, the job is superseded or lost its lease
```

- **Cancellation of running work**: a per-instance *supersession watcher* polls every 5 s
  `SELECT "Id", "CurrentExecutionId" FROM content."Documents" WHERE "Id" = ANY(@running)` for documents with running
  jobs, and cancels the token of any job whose execution is outdated. Closing the parser connection makes the parser
  kill the slot (parser §10.4); gateway calls are aborted. The job ends `cancelled`, outcome `superseded`, without
  a retry. Queued jobs of an old execution are not touched: they see the mismatch at start and end `cancelled` in
  one statement (cheaper than searching `Payload`).
- Newer run cancels older (KM `ExecutionId`, `../../platforms/kernel-memory-and-dotnet.md:145-146`), but only for
  new content.

### 7.6 Transactions, idempotency, at-least-once

| Effect | Where | Why it is safe to repeat |
|---|---|---|
| Node output blobs | written **before** the DB commit, at content-addressed paths (`…/{cacheKey}…`), write-to-temp then rename/put-if-absent | same key → same bytes; orphans (no `NodeOutputs` row after 24 h) removed by the sweeper |
| `NodeOutputs` rows | document-stage commit, `INSERT … ON CONFLICT ("CacheKey") DO NOTHING` | content-addressed |
| `Documents` facts (media type, language, page count, strategy, parse outcome and cache key, PII counts and flag) | document-stage commit, fenced | last fenced writer wins; fence rejects stale executions |
| Continuation jobs | same transaction, `DedupKey` (`parse:{doc}:{exec}:{node}`, `embed:{doc}:{hash}:{pipelineHash}`) | enqueue is idempotent (data-model §4.2 advisory lock) |
| `EmbeddingCache` | own short transaction, `ON CONFLICT DO NOTHING` | cache |
| Chunk sets, chunks, `DocumentSources` pointers | index writer transaction, fenced (§14) | unique key on `ChunkSets`; pointer swap is a set operation |
| Run items | in the fenced transaction that reaches the terminal outcome | written once per (run, document) by construction (§10) |
| Gateway cost | charged by the gateway per call; a retried call is charged again | bounded by caches (parse, embedding) and attempts |

Jobs hold a connection only for these short statements, never across an HTTP call, so the pool (10 + 1 LISTEN,
data-model §1.1) serves ~20 concurrent jobs.

### 7.7 Gateway calls

Every call from a node goes through `IGatewayClient` with headers set by the engine, never by the node:

| Header | Value |
|---|---|
| `x-ume-on-behalf-of` | owning team id: collection stage → the collection's `OwnerTeamId`; document stage → the triggering source's collection owner team (gateway design §5.2); interim: per-team key from `GatewayKeyLinks` instead |
| `x-ume-max-residency` | `OnPrem` for embeddings always (R3); otherwise by the document level: `public` none, `internal` `Eu`, `sensitive` `OnPrem` (platform plan §6) |
| `x-ume-run-id`, `x-ume-step` | `RunId` (GUID) and node **type** (`embed`, `llm-summary`), never names (gateway design §7.1) |
| `traceparent` | current activity |

The response is checked before anything is stored: `x-ume-residency` within the ceiling (else fatal + 5003); after
gateway release A, when `x-ume-on-behalf-of` was sent, its echo must match (`Gateway:RequireDelegationEcho`), else
fatal `gateway.delegation_not_applied`. `x-ume-cost-usd` is added to the node's `NodeUsage`.

## 8. Output cache

- **Key** (data-model §9): `SHA-256(inputHash ‖ nodeType ‖ nodeVersion ‖ paramsHash)`; `inputHash` = SHA-256 over
  the sorted `(port, outputHash)` of the active inputs; for the first node after the root, the raw content hash.
  `NodeOutputs` gains `OutputHash` (SHA-256 of the serialised output) so the next key can be computed **without
  loading the blob**: resuming or re-planning walks the path from the root with one primary-key lookup per node.
- **Classes** (manifest `metadata.cacheClass`):

| Class | Nodes | Kept |
|---|---|---|
| `durable` | parse nodes (`auto-parse`, `docling-parse`, `pdf-fast-path`, `html-main-content`, `plain-text`, `csv-table`) | while the document lives (`ExpiresAt` null, `IsParseOutput = true`); when superseded by new content, `ExpiresAt = now + 30 days` |
| `intermediate` | `detect`, `clean`, `detect-personal-data`, `split-headings`, `chunk`, `headless-render` | 30 days (`Settings`) from last use; a hit extends `ExpiresAt` at most once a day |
| `none` | `switch`, `quality-check`, `metadata-from-path`, `embed` (own `EmbeddingCache`), `write`, roots (raw blob is the document) | – |

- The parse output is multi-blob (parser §7.6): `NodeOutputs.BlobPath` is the prefix
  `documents/{id}/nodes/{nodeType}/{cacheKey}/`, holding `result.json`, `docling/{from}-{to}.json.gz` and
  `ranges/{from}-{to}.result.json` (intermediate, 30 days, written per completed range so a crashed or yielded parse
  job resumes at the next range). Non-parse nodes use one blob `…/{cacheKey}.json.gz`.
- **Invalidation is by key**: a new node version, changed params or changed upstream output gives a new key; nothing
  is deleted eagerly. Explicit bypass: a `reprocess` with `force` and `fromNode` skips lookups from that node on (new
  execution id). A parser upgrade does not re-parse (parser §7.6).
- **Sharing**: same document, same path prefix → same keys → one parse for all departments (R2). Different documents
  with identical bytes do not share (data-model §12.13).
- **Retention**: the worker's content retention deletes expired rows in batches of 1,000 and then their blob
  prefixes; orphan blobs older than 24 h without a row are removed by the same job.

## 9. Jobs and scheduler

### 9.1 Classes

Data-model §4.2 holds order, leases and attempts. Concurrency defaults below are per instance and may be overridden in
`config."Settings"."WorkerConcurrency"` (repo-and-deployment §3), read at the confirmed config version.

| Class | Concurrency | Payload (ids and parameters only, ≤ 4 KB) | Runs |
|---|---|---|---|
| `preview` | 2 | `kind` (`node-preview`/`publish-plan`), `pipelineVersionId`, `documentIds` or `urls`, `untilNodeId`, `basePreviewJobId` | §11, §12 |
| `command` | 2 | `kind` + ids: `clear-pii-flag`, `confirm-deletion`, `reject-deletion`, `approve-profile`, `rollback-profile`, `edit-eval-question`, `recompute-membership`, `reprocess-document`, `cancel-run` | 6a/6b/6c each own their kinds |
| `sync` | 2 | `sourceId`, `runId`, `kind` (`sync`/`retry-failed`/`reprocess`), `scheduledFor`, `force`, `fromNode`, connector checkpoint | 6b (listing), §9.2 |
| `fetch` | 8 (frontier limits per host) | `sourceId`, `runId`, identity or frontier entry; `executionId` once known | 6b, then §7 |
| `parse` | `min(Settings, /v1/info capacity.convert_slots)` | `documentId`, `level`, `executionId`, `pipelineVersionId`, `nodeId`, `inputKey`, `slice` | §7, §9.5 |
| `embed` | 4, adaptive (§18) | `documentId`, `level`, `executionId`, `pipelineHash`, served `(sourceId, runId)` pairs, `inputKey` | §7, §14 |
| `eval` | 1 | 6c | 6c |

`Worker:Classes` lists the classes an instance runs (`crawl` is not a class: discovery is part of `sync`/`fetch`, §21).

### 9.2 How a sync fans out

> **Refined by 6b** (`design-connectors.md` §2, §6, §13): URL-list and crawl items become frontier **claims**
> released per host by the frontier releaser instead of eager `fetch` jobs; the `sync` payload carries a `phase`
> (`list`/`fetch`/`reconcile`) and a checkpoint reference; deletions run in a **reconcile phase** after all
> fetches, enqueued by the finalizer; new `preview` kinds `preflight` and `hierarchy`. Where this section differs,
> 6b wins.

1. `sync` job: resolve source and pipeline version at the confirmed snapshot; open or reuse the `Runs` row; run the
   connector's listing and change detection (6b), checkpointed, yielding after 1,000 items or 2 minutes (re-enqueued
   as a continuation, no attempt).
2. Per changed or new item: enqueue `fetch` jobs in batches of 500 (`INSERT … SELECT unnest(…)`, one `pg_notify`
   per batch), `DedupKey = fetch:{level}:{identityHash}` (the shared frontier, 6b, decides which source's request
   wins; the other records outcome `shared`, §10.2).
3. Per unchanged item: `EnsureCurrent` (reads; enqueues only if a reference is not current) and a run item `unchanged` written in batches.
4. Per deletion candidate: the deletion guard (6b) and soft deletes through the index writer (§14).
5. The sync job ends when listing is done; the run stays `running` until the finalizer sees no open jobs (§10.4).

`retry-failed` lists only the documents whose last run item failed (`IRetryFailedConnector`); `reprocess` (publish,
§12) iterates the source's live `DocumentSources` and calls `EnsureCurrent`, never fetching.

### 9.3 Dispatcher and fairness

One dispatcher loop per instance (woken by `LISTEN rag_jobs`, polling every 2 s; data-model §4.3) fills free slots in
class order (`preview`, `command`, `sync`, `fetch`, `parse`, `embed`, `eval`). Within a class:

- the list of departments with ready jobs is read every 5 s with a recursive-CTE loose index scan over
  `IX_Jobs_Ready` (Postgres 17 has no skip scan), cached in memory;
- departments are tried in ascending order of their in-flight count in that class on this instance, ties by longest
  wait, using the data-model §4.2 dequeue query; the first hit wins;
- a department may hold at most `max(1, ⌈concurrency / departments with ready jobs⌉)` slots of a class while others
  wait (per-tenant slots as Eneo, `../../platforms/eneo.md:210`, and Dify's tenant queues,
  `../../platforms/dify.md:219-221`).

Fairness is per instance; with several instances it is approximate, which is enough.

### 9.4 Leases, heartbeats, reaper, shutdown

| Mechanism | Rule |
|---|---|
| Lease | set at dequeue (data-model §4.2 durations) |
| Heartbeat | every `lease / 3`, one statement for all of the instance's running jobs: `UPDATE … SET "LeaseUntil" = now() + lease WHERE "LeaseOwner" = @me AND "State" = 'running' AND "Id" = ANY(@ids) RETURNING "Id"`; a job missing from the result has lost its lease → its token is cancelled and its commit will fail the fence |
| Reaper (every 30 s, any instance, `pg_try_advisory_xact_lock`) | `running` jobs with `LeaseUntil < now()`, **or** whose `LeaseOwner` instance has no heartbeat for 45 s, go back to `queued` with the attempt kept; at `MaxAttempts` → `dead`, run item `failed`, reason `worker.lost` |
| Graceful stop (`stop_grace_period` 120 s) | stop dequeuing; give running jobs 90 s; then cancel the rest and release their leases (`queued`, attempt not counted, `AvailableAt = now()`); delete the instance row |
| Crash | reaper returns the jobs within ~45 s; resumed work replays from the cache (§8), parse jobs from the last completed range |

Early reclaim keeps a killed worker from idling the parser for a 15-minute `parse` lease. With the drift sweep (§2.3)
this is the "worker restart loses no work" target.

### 9.5 Parse concurrency and slicing

- Capacity = `capacity.convert_slots` from the parser's `/v1/info`, read at start and every minute (parser §10.1); the
  `parse` class never runs more jobs than that per instance. Several worker instances sharing one parser may
  oversubscribe it; `busy` then requeues without an attempt (§7.3).
- **Preview first**: an in-process priority gate in front of the parser client lets a waiting preview take the next
  free slot before any `parse` job starts its next range. With the pilot's single slot a preview waits at most one
  range (≤ 50 % of the 120 s deadline, parser §8.1).
- **Slicing**: a `parse` job yields after 10 minutes at a range boundary: it writes the completed ranges, re-enqueues
  itself with `slice + 1` at the back of its department's queue (no attempt), and completes. One 2,000-page OCR PDF
  (≈ 2 hours, parser §14) then shares the parser with other departments instead of holding it.
- `/v1/inspect` (inside *Detect*) uses the parser's separate inspect lane and is not counted against slots.

### 9.6 Scheduler

A `BackgroundService` on every instance; the instance holding `pg_try_advisory_xact_lock(<scheduler key>)` in its
1-minute tick does the work (gateway `ModelListSync.cs:148-153` pattern).

- **Cron** (`Sources.Schedule`, 5 fields, Cronos *to verify*) evaluated in Europe/Stockholm; DST gaps run once at
  the next valid minute, overlaps once.
- **Defaults** (answered 7), set by the admin when a source is created: URL list `0 2 * * *` (nightly 02:00),
  crawl `0 1 * * 6` (Saturday 01:00, weekly at night), upload none (the upload itself enqueues a `sync` with
  `Trigger = upload`). Adjustable per source.
- **Jitter**: deterministic per source, `hash(sourceId) mod JitterMinutes` (`Settings`, default 90), so sources with
  the same cron do not start together and a source keeps its slot from week to week.
- **Due**: `next = cron.Next(lastScheduledFor ?? source.CreatedAt)`; when `next + jitter ≤ now` and no `sync` for
  the source is `queued`/`running`, enqueue one with `ScheduledFor = next` (new `Runs.ScheduledFor`, §21). After an
  outage the missed occurrences collapse into one run. The schedule is computed from `ScheduledFor`, never from an
  "updated" timestamp that other jobs touch (Onyx's stall, F6).
- **Sync now**: the admin inserts a `sync` job (`Trigger = manual`, `Priority` 10); if one is already queued or
  running the admin answers "already running" with its run id (dedup `sync:{sourceId}`).
- Paused or deleted sources are skipped; `rag.scheduler.lag` (now − due) is a metric.
- The scheduler also enqueues scheduled `eval` runs and the nightly drift sweep; their rules are 6c's and §2.3.

## 10. Runs and run items

### 10.1 Run states

`running` → `succeeded` | `degraded` | `failed` | `awaiting-deletion-confirmation` (6b) | **`cancelled`** (new: source
paused or deleted, or `cancel-run` command). Kinds `sync`, `retry-failed`, `reprocess` (publish, rollback, follow
runs); `Trigger`: `schedule`, `manual`, `upload`, `publish`, `rollback`, `shared-document`, `retry`.

### 10.2 Outcomes

| Outcome | Meaning | Run counter |
|---|---|---|
| `added` | new document indexed for this source | Added |
| `updated` | changed content or new pipeline version indexed | Updated |
| `unchanged` | conditional GET 304 or same hash, chunk set current | Unchanged |
| `shared` | processed by another run or source in the same window (reason names the run) | Unchanged |
| `skipped` | excluded on purpose: robots, include/exclude rule, *Switch* `default` with nothing to index, empty after cleaning | Unchanged |
| `degraded` | indexed with gaps: missing pages after retry, low OCR confidence, render not enabled; `Reason` lists them | Updated/Added |
| `deleted` | reference soft-deleted (6b) | Deleted |
| `unsupported` | format refused (`unsupported_format`, *Switch* `default` for binary types) | Failed |
| `too-large` | above upload, parser or engine limits (§16) | Failed |
| `encrypted` | password-protected | Failed |
| `malware` | ClamAV finding, quarantined (6b) | Failed |
| `blocked` | egress guard or robots refused a redirect target (6b) | Failed |
| `failed` | other fatal error, or attempts exhausted (`dead`) | Failed |
| `superseded` | newer content arrived; the newer execution records the real outcome | not counted |

`Reason` is a code plus short fixed text, `<area>.<code>` (`parser.timeout`, `http.404`, `gateway.budget_exhausted`,
`pii.flagged`), never library messages or document text (parser §7.5 rule). `Strategy` holds the path (§7.2);
`CostUsd` the gateway cost of this document in this run; `DurationMs` wall time across its jobs; `ParserBuildHash` as
parser §13.1.

### 10.3 Degraded threshold

After finalization: `failed` when the sync job itself failed or every processed item failed; `degraded` when
`Failed / (processed − unchanged − shared) > Settings.DegradedFailureRatio` (default 5 %) or any `malware`; otherwise
`succeeded`, still showing its failure count. Runs never show green while documents fail silently (Onyx F6, comparison
F6).

### 10.4 Finalizer and cost

Every 30 s (advisory-lock leader): for `running` runs whose `sync` job is terminal and that no `queued`/`running` job
serves (new `Jobs.RunIds uuid[]`: every run a job serves, since one shared `embed` job can serve the triggering run
and follow runs; partial GIN index on open jobs, §21), aggregate `RunItems` into the counters and
`CostUsd`, set `State` and `FinishedAt`. Counters are derived, never incremented by jobs, so retries cannot double-count
and no hot row is updated per document.

### 10.5 What the admin shows (consumed by the admin plan §4)

| View | Contents |
|---|---|
| Source → runs | state, trigger, pipeline version, counters by outcome, cost, duration, queue position of the next waiting job and age of the oldest (RAGFlow's "parsing stuck" is mostly invisible waiting, `../../platforms/ragflow.md:210-212`) |
| Run detail | failures grouped by reason with counts; degraded items with their gaps; per item: identity, outcome, strategy, duration, cost, parser build; links to the document |
| Document / Overview | run items across sources (90 days), chunk sets and pipeline version per source, cache entries, PII state / degraded or failed runs per department, dead jobs, scheduler lag, follow runs |

## 11. Preview

- **Class** `preview`, highest priority, `MaxAttempts` 1, lease 2 minutes. Started by the admin for a saved draft
  version (canvas autosave) or a template form.
- **Inputs**: up to 5 existing documents of the source (raw blob and cached outputs are read), or up to 5 URLs/upload
  ids for a source that has none yet (fetched live through the same egress guard and host politeness; the one
  `content` write a preview makes is `CrawlHosts.NextAllowedAt`, which is bookkeeping, not content, §21).
- **Isolation**: `ExecutionMode.Preview` gives nodes a blob store limited to `previews/{jobId}/` (encrypted with a
  per-job data key wrapped by `rag-blob-<level>`, the wrapped key in `Jobs.Result`), no cache writes, a
  `NullIndexWriter` that throws if called, and no `Documents` updates; nodes have no database access anyway (§5.1).
  An integration test asserts that no `content` row except `Jobs.Result`/`CrawlHosts` changes (§19).
- **Execution**: up to `untilNodeId` (default *Chunk*). *Embed* reports chunk count and tokens and does not call the
  gateway unless `embed: true`; *Write* never runs ("would write N chunks to M collections"). LLM nodes (P2) run for
  real within a cost cap of `Settings.PreviewMaxUsd` (default 0.50) computed from `EstimateCost` before start;
  above it the admin must confirm.
- **Single-node re-run** (Dify's per-node preview, comparison §3): `basePreviewJobId` reuses that preview's upstream
  outputs from `previews/{baseJobId}/` and runs only the changed node and what follows.
- **Limits**: 5 documents, 50 pages per document (first pages, through the normal range planning), 2 MB of output per
  node shown (truncated with a marker), 2-minute deadline; blobs expire after 24 h (data-model §9).
- **Result** (`Jobs.Result`, small): per document and node: state, duration, cache hit, facts, active output port,
  estimated and actual cost, error code; blob paths for the outputs the admin streams on demand (parsed Markdown with
  page markers, sections, chunks with heading paths and token counts).

## 12. Publish and rollback

### 12.1 Flow

| Step | Who | What |
|---|---|---|
| 1 | admin | validate the draft (§4 incl. live support); version stays `draft` |
| 2 | worker (`preview`, `kind = publish-plan`) | the **re-processing plan** (§12.2) and cost/time forecast into `Jobs.Result` |
| 3 | worker (`eval`) | the **gate hook**: `IPublishGate.RunAsync(candidateVersion, sample)`; the sample's chunk sets are built for real as **shadow sets** (`CollectionIds = '{}'`, `DeletedAt = now()`, invisible to retrieval), so the work is reused on publish. Gate logic, sample size and thresholds: 6c (plan §7) |
| 4 | user | sees plan, forecast and gate result; confirms (a failed gate blocks; override rules are 6c's) |
| 5 | admin | one config transaction: version → `published` (`PublishedAt`, `GateRunId`), previous → `superseded`, `Pipelines.PublishedVersionId`; audit 6001; enqueues a `reprocess` `sync` per source using the pipeline |
| 6 | worker | `EnsureCurrent` per live reference; the run items say `updated`, or `unchanged` when the set was already current |

The admin owns `config` and the worker only reads it, so publish states stay in `PipelineVersions.State`; the plan and
the gate are job results the admin shows (`Jobs.Result`, `EvaluationRuns`). Dify publishes without re-processing and
leaves an index mixing versions (`../../platforms/dify.md:241-242`); here every document records the version that
processed it (`DocumentSources.PipelineVersionId`, `RunItems.PipelineVersionId`, §21).

### 12.2 Re-processing plan

For each live `DocumentSources` row of the affected sources:

1. evaluate the routing of both versions on the stored facts (media type, language, scanned, page count) to get each
   version's path; *Quality check* fallbacks are assumed as recorded in the last run item's `Strategy`;
2. **first changed node** = first node on the new path whose Merkle hash (§3.3) does not occur on the old path;
3. group documents by first changed node; per group, estimate the work from that node on with `EstimateCost`
   (parser pages, embedding tokens from current chunk counts, LLM tokens) and time from measured per-class
   throughput.

Example result: "1,240 documents; 1,180 from *Chunk* (re-chunk + re-embed, ~0 parser pages, ~2.1M embedding tokens,
~25 min), 60 from *Auto-parse* (~900 parser pages, ~1 h); estimated cost 0.00 USD (on-prem)".

### 12.3 Rollback

Rollback re-publishes an older version of the same pipeline (no new number; `superseded` → `published`). The gate
runs and is shown but never blocks a rollback (it is the recovery path). Because chunk sets of the previous version
are kept 14 days after they leave every collection (data-model §2.2), the writer **revives** them by unique key
(`DeletedAt = null`, membership recomputed): a rollback within 14 days is a membership change per document, without
parsing or embedding. Older rollbacks re-run from the durable parse output.

## 13. Templates

### 13.1 P1 templates

Platform templates (`Pipelines.DepartmentId = null`, `IsTemplate`), seeded by the migration service from
`src/Ume.Rag.MigrationService/Templates/*.json` (insert-if-absent by name and number; a changed template is a new
version). Hashes are computed at seed time.

**`website`** (crawl). **`url-list`** is identical except the root `{ "id": "fetch", "type": "http-fetch",
"typeVersion": 1, "params": { "conditionalGet": true } }`:

```json
{ "schema": "pipeline/v1",
  "nodes": [
    { "id": "fetch",   "type": "crawl",             "typeVersion": 1, "params": { "render": "if-enabled" } },
    { "id": "detect",  "type": "detect",            "typeVersion": 1, "params": { "inspect": true } },
    { "id": "route",   "type": "switch",            "typeVersion": 1, "params": { "on": "mediaType", "cases": {
        "html": ["text/html", "application/xhtml+xml"],
        "document": ["application/pdf", "image/jpeg", "image/png", "image/tiff", "application/rtf",
          "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "application/msword",
          "application/vnd.openxmlformats-officedocument.presentationml.presentation", "application/vnd.ms-powerpoint",
          "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "application/vnd.ms-excel",
          "application/vnd.oasis.opendocument.text", "application/vnd.oasis.opendocument.spreadsheet",
          "application/vnd.oasis.opendocument.presentation"],
        "text": ["text/plain", "text/markdown"], "csv": ["text/csv"] } } },
    { "id": "html",    "type": "html-main-content", "typeVersion": 1, "params": { "cleanupRules": "learned" } },
    { "id": "enough",  "type": "quality-check",     "typeVersion": 1, "params": { "minChars": 200 } },
    { "id": "render",  "type": "headless-render",   "typeVersion": 1, "params": { "timeoutSeconds": 30 } },
    { "id": "html2",   "type": "html-main-content", "typeVersion": 1, "params": { "cleanupRules": "learned" } },
    { "id": "parse",   "type": "auto-parse",        "typeVersion": 1, "params": { "ocr": "auto", "tables": "accurate" } },
    { "id": "text",    "type": "plain-text",        "typeVersion": 1, "params": {} },
    { "id": "csv",     "type": "csv-table",         "typeVersion": 1, "params": { "headerRows": 1 } },
    { "id": "clean",   "type": "clean",             "typeVersion": 1, "params": { "dropFurniture": true, "dehyphenate": true } },
    { "id": "pii",     "type": "detect-personal-data", "typeVersion": 1, "locked": true,
      "params": { "types": ["personnummer", "phone", "email", "name"], "ner": true } },
    { "id": "split",   "type": "split-headings",    "typeVersion": 1, "params": { "maxLevel": 4, "relevelLayoutHeadings": true } },
    { "id": "chunk",   "type": "chunk",             "typeVersion": 1,
      "params": { "mode": "flat", "maxTokens": 400, "overlapPercent": 15, "prependHeadingPath": true, "tableRowGroups": true } },
    { "id": "meta",    "type": "metadata-from-path", "typeVersion": 1, "params": { "rules": [] } },
    { "id": "embed",   "type": "embed",             "typeVersion": 1, "locked": true,
      "params": { "profile": "platform-default", "batchSize": 64 } },
    { "id": "write",   "type": "write",             "typeVersion": 1, "locked": true, "params": {} } ],
  "edges": [
    { "from": "fetch.out", "to": "detect.in" },   { "from": "detect.out", "to": "route.in" },
    { "from": "route.html", "to": "html.in" },    { "from": "html.out", "to": "enough.in" },
    { "from": "enough.pass", "to": "clean.in" },  { "from": "enough.fail", "to": "render.in" },
    { "from": "render.out", "to": "html2.in" },   { "from": "html2.out", "to": "clean.in" },
    { "from": "route.document", "to": "parse.in" }, { "from": "parse.parsed", "to": "clean.in" },
    { "from": "route.text", "to": "text.in" },    { "from": "text.out", "to": "clean.in" },
    { "from": "route.csv", "to": "csv.in" },      { "from": "csv.out", "to": "clean.in" },
    { "from": "clean.out", "to": "pii.in" },      { "from": "pii.out", "to": "split.in" },
    { "from": "split.out", "to": "chunk.in" },    { "from": "chunk.out", "to": "meta.in" },
    { "from": "meta.out", "to": "embed.in" },     { "from": "embed.out", "to": "write.in" } ],
  "form": [
    { "field": "render",      "node": "fetch", "param": "render" },
    { "field": "ocr",         "node": "parse", "param": "ocr" },
    { "field": "chunkTokens", "node": "chunk", "param": "maxTokens" },
    { "field": "pathRules",   "node": "meta",  "param": "rules" } ] }
```

The `render` branch: when the source does not enable rendering, *Headless render* passes its input through with the
fact `render.not_enabled`; `html2` then re-runs on the same bytes (a cache miss, because the added fact changes the
input's `OutputHash`; cheap, in-process) and the document is indexed as `degraded`. Whether the crawl root renders
up front instead (JavaScript-only pages, plan answered 2) and how rendered and non-rendered raw content of one URL
coexist is decided in 6b (`design-connectors.md` §8.4: `Documents.Variant` `origin`/`rendered` in the document
key); this branch stays as the fallback and sets `RenderRecommendedAt`. Unsupported media types leave through `route.default` (unconnected) → `unsupported`.

**`files`** (upload): root `upload`; same `route` cases; `route.html` goes straight to `html` → `clean` (no render
branch: an uploaded file is never re-fetched); otherwise as `website`. Email (`.eml`/`.msg`) is listed under
`document` (`message/rfc822`, `application/vnd.ms-outlook`): *Auto-parse* sends it to the parser, and its
attachments become child documents in 6b. Its form: `ocr`, `chunkTokens`.

### 13.2 Who edits what

- `rag-department-admin`: picks a template for a source and sets its `form` fields (stored as a department copy
  differing only in those params).
- `rag-pipeline-editor`: copies a template into a department pipeline; edits any unlocked param in forms generated
  from the manifest; adds or removes unlocked catalogue nodes in a JSON editor (P1), on the canvas from P2.
- `rag-platform-admin`: templates, locked nodes and params, `deprecated` flags, embed profile exceptions.

### 13.3 Copy-and-edit rules

- A copy records `Pipelines.CopiedFromId` and `Definition.template` (template version). Locks are copied and stay.
- A template update does not change copies. Copies see "template updated" with a definition diff and can re-base by
  creating a new draft. Exception: the platform may raise a locked node's `minVersion` in the template (e.g. a fixed
  *Detect personal data*): publishing a copy below it is refused (§4 rule 10), and a platform admin can bulk-create
  and publish upgraded drafts through the normal flow (plan, gate).
- A department copy is department-scoped (`Pipelines.DepartmentId`); a source may use only its department's
  pipelines or templates.

## 14. Index writer contract

```csharp
public interface IIndexWriter
{
    // One transaction: fence, chunk set (new, reused or revived), pointers, membership, run items, job completion.
    Task<IndexWriteResult> WriteAsync(ChunkSetWrite write, ExecutionFence fence, CancellationToken ct);
    // Soft-deletes references (6b decides which); recomputes membership; documents without live references get DeletedAt/PurgeAfter.
    Task<int> SoftDeleteReferencesAsync(IReadOnlyList<DocumentSourceKey> refs, Guid runId, CancellationToken ct);
    // Recomputes CollectionIds of every set of a document (PII cleared, collection level changed); batches of 1,000 chunks.
    Task RecomputeMembershipAsync(Guid documentId, Classification level, CancellationToken ct);
}

public sealed record ChunkSetWrite(Guid DocumentId, Classification Level, byte[] ContentHash, byte[] PipelineHash,
    Guid EmbeddingProfileId, IReadOnlyList<ServedReference> Serves,   // (SourceId, PipelineVersionId, RunId)
    IAsyncEnumerable<ChunkRow> Chunks, int ChunkCount, bool Shadow);
```

Transactional rules (schema: data-model §2.2):

1. **Fence** first (§7.5). Then look up `ChunkSets` by its unique key (`DocumentId`, `CollectionStageHash` =
   `PipelineHash`, `EmbeddingProfileId`, `ContentHash`), deleted rows included.
2. **New set**: insert `ChunkSets`, then the chunks with binary `COPY` in batches of 1,000 rows (each statement well
   under the 60 s `statement_timeout`), `CollectionIds` already final, so chunks are inserted once and never updated
   for visibility (a `CollectionIds` update rewrites every chunk and its HNSW entry, data-model §2.2).
   **Existing set**: no chunk insert; **deleted set**: revive (`DeletedAt = null`).
3. **Swap**: `DocumentSources` of the served sources get `ChunkSetId`, `PipelineVersionId`, `LastSeenAt`, `LastRunId`,
   but only for sources whose published version (confirmed snapshot) is still the write's; for the others the set is
   kept as a shadow set and `EnsureCurrent` runs, so a slow write of an old version never overtakes a newer one.
4. **Membership**: for the new set and every set the served references pointed to before,
   `CollectionIds = distinct collections of live references − public collections while the PII flag is not cleared`
   (data-model shared-chunk rule); copy to `Chunks.CollectionIds` where it changed; a set left with `{}` gets
   `DeletedAt = now()` (14-day grace). Shadow writes (§12.1) skip steps 3–4 and create the set already deleted.
5. **Run items** for each served `(source, run)`, then the job completion; commit.

A retrieval statement sees either the old or the new set (one snapshot), never both or neither. Limits: 20,000
chunks per set (above: outcome `too-large`, §16). Large membership changes (shared sets above 10,000 chunks) are
applied in `command` jobs outside office hours, as data-model §2.2 states.

## 15. Config and mid-run changes

- The worker reads its config tables (data-model §1.2) through the config-version library (data-model §5); the
  security-relevant ones (`Sources`, `Collections`, `Teams`, `Departments`, `DataTiers`, `DataProtectionPolicies`,
  `GatewayKeyLinks`) **fail closed**: a job starts and a commit proceeds only when
  `snapshot.Version == lastConfirmedVersion` and it was confirmed ≤ 10 s ago; otherwise the job is requeued without
  an attempt and event 5004 is logged (once a minute per instance with `Count`).
- The commit check matters for `CollectionIds`: a collection that became `public` while a PII-flagged document was
  being embedded must not receive its chunks.

| Change while work is in flight | Effect |
|---|---|
| New pipeline version published | running executions finish on the version they started with (pinned in their payloads, so no document mixes versions); the publish's `reprocess` run brings them up to date via `EnsureCurrent` |
| Rollback | same as publish |
| Source paused or deleted | queued jobs of its runs end `cancelled` at start (`source.paused`); running jobs finish their current node and do not commit a write for that source; run → `cancelled` |
| Source level changed (identity change) | full re-sync into the new level (6b); jobs at the old level fail the commit check (`source.reclassified`) and end `cancelled` |
| Collection level changed | `recompute-membership` command for its documents |
| Source settings: `cosmetic` / `behaviour` / `scope` / `identity` | nothing / next sync / sync with the smallest plan / full re-sync (6b) |
| Template updated | nothing until a copy is re-based (§13.3) |
| Node version deprecated or removed from a release | publish refuses it; running published versions keep working while a supporting instance lives (§5.2) |

## 16. Failure modes and recovery

| Failure | Detection | Recovery |
|---|---|---|
| Worker process crash or OOM | missing heartbeat 45 s | reaper requeues; cache and range blobs make the retry cheap; `MaxAttempts` stops a poison document (`dead`, run item `failed`, reason `worker.lost`) |
| Lease lost (GC pause, DB stall) | heartbeat returns no row | token cancelled; the fence rejects the commit; the new owner redoes the work |
| Postgres unavailable | Npgsql errors | dispatcher backs off (1 s → 30 s); jobs in flight fail their commit and are re-leased later; nothing partial is visible (per-document transactions) |
| Parser down or warming | `not_ready`, connection refused | requeue without attempt; `rag.parser.slots_ready` and queue age alert after 10 minutes |
| Gateway or embedding provider down | 5xx, timeouts | transient back-off up to 1 h; runs end `degraded` if attempts run out; query embeddings for chat are unaffected by our retries thanks to the adaptive limit (§18) |
| Budget used up | 402 | hourly retries; `failed` with `gateway.budget_exhausted`; the run page says which team's budget |
| Config monitor broken | unconfirmed > 10 s | all security-relevant work pauses (fail closed), 5004 |
| A job or a whole run silently lost | nightly drift sweep (§2.3), finalizer | re-enqueued; `rag.reconcile.drift` metric (should be 0) |
| Huge document | limits: 2,000 pages (parser), parsed output 50 MB, 20,000 chunks per set | outcome `too-large` with the limit's name |
| A node bug producing bad output | golden tests; preview; evaluation gate (6c) | fix with a version bump; publish plan re-runs from that node |

## 17. Observability

Meter `Ume.Rag` and activity source `Ume.Rag` (repo-and-deployment §9). New metrics (low-cardinality dimensions only;
never source names, URLs or titles):

| Metric | Type | Dimensions |
|---|---|---|
| `rag.jobs.queued`, `rag.jobs.age` (existing); `rag.jobs.duration` (s) | gauge; histogram | class; class, outcome |
| `rag.jobs.requeued` | counter | class, reason (`busy`, `not_ready`, `rate_limited`, `config_unconfirmed`, `unsupported_node`, `slice`, `shutdown`) |
| `rag.jobs.lease_lost`, `.dead`, `.reclaimed`; `rag.engine.superseded` | counter | class |
| `rag.engine.node.duration` (s); `rag.engine.cache` | histogram; counter | node type, outcome; node type, `hit`/`miss` |
| `rag.runs.items`; `rag.runs.finished` | counter | outcome, source type; state, kind |
| `rag.index.write.duration`, `rag.index.chunks_written` | histogram, counter | new/reused/revived |
| `rag.embed.tokens`, `rag.embed.concurrency_limit`, `rag.ingestion.cost` (USD) | counter, gauge, counter | –, –, class |
| `rag.scheduler.lag` (s), `rag.reconcile.drift`, `rag.preview.duration` (s) | histogram, counter, histogram | –, –, kind |

- **Traces**: one span per job (`rag.job {class}`) with child spans per node (`rag.node {type}`) and the HTTP client
  spans to parser and gateway, which continue the trace there. A continuation job stores the `traceparent` of its
  predecessor in its payload and starts with a **link** to it, so fetch → parse → embed of one document is one
  navigable chain without one trace lasting hours. Attributes: job class, node type, document id, level, cache hit,
  outcome code; never URLs of non-`public` sources, titles or text (repo-and-deployment §9 "no content").
- **Log scopes** (repo-and-deployment §9) plus `ExecutionId` and `NodeId`: `ConfigVersion`, `RunId`, `JobId`,
  `JobClass`, `DocumentId`, `SourceId`, `Classification`, `Tier`.
- **Security events**: 5003, 5004 (worker), 5101–5103 (6b/6c), 5104 (parser limits), and new **5105
  `rag.pipeline.refused`** (Warning): the worker refused to run a published version that fails validation (missing
  locked node, unknown node version, schema failure). Attributes `PipelineId`, `PipelineVersionId`, `Reason`.
  Missing a locked *Detect personal data* is a data-protection bypass, so the SIEM alerts on any 5105.

## 18. Performance and capacity

**R1 target**: 10,000 typical web pages per hour on the ingestion zone without moving chat p95 TTFT by more than
10 %. That is 2.8 pages/s. Planning estimates per page (*to measure* in the R1 test):

| Resource | Per page | At 2.8 pages/s | Budget | Notes |
|---|---|---|---|---|
| Worker CPU (fetch, AngleSharp + SmartReader, clean, regex PII, tokenizer, JSON) | ≈ 80 ms | ≈ 0.25 cores | 1.5 cores | `fetch` concurrency 8 covers ~200 ms fetch latency |
| Politeness | – | 2.8 req/s on umea.se | 10 req/s municipal (answered 4) | external hosts at 1 req/s cap a single external site at 3,600 pages/h |
| Parser NER lane (names) | ≈ 100 ms | ≈ 0.3 cores | parser 4 cores | must not queue behind conversions (§21, 6c) |
| Embedding (≈ 8 chunks × 450 tokens) | ≈ 3,600 tokens | ≈ 10,000 tokens/s | gateway's on-prem BGE-M3 | **the bottleneck to verify** (§23) |
| Postgres: chunk inserts with HNSW, run items, jobs | ≈ 8 rows + 2 jobs | ≈ 22 vector inserts/s, ≈ 20 statements/s | 3 cores, pool 10 | HNSW insert ~1–3 ms each |
| WAL | ≈ 50 KB | ≈ 140 KB/s | – | – |

Protection of chat:

- Zones, `cpu_shares`, the ingestion slice and the worker's connection cap (repo-and-deployment §6.2, data-model
  §1.1); jobs hold connections only for short statements (§7.6).
- **Adaptive embed concurrency** (AIMD): the worker tracks p95 latency of its embedding calls per minute; above
  2× the idle baseline it halves `embed` concurrency (min 1), below it adds one per minute up to the configured
  value. Chat's query embeddings use the same on-prem model, so this keeps ingestion from queuing ahead of them;
  `rag.embed.concurrency_limit` shows it working.
- Batches of ≤ 64 inputs per embedding request; the embedding cache skips unchanged text (re-chunking embeds only new
  text, plan §5).
- An unchanged re-crawl costs one conditional GET per page and a batched `unchanged` run item: no parse, no embed
  (plan §9; Eneo re-embeds everything, `../../platforms/eneo.md:234`).
- Two departments with the same site: one fetch (frontier, 6b), one parse (cache key), one chunk set when pipelines
  match (`PipelineHash`).

`perf.yml` (repo-and-deployment §8.3) measures pages per hour, chat p95 TTFT idle vs. during crawl + OCR batch, Postgres
CPU and pool waits, and the embed limit's behaviour.

## 19. Tests

| Level | Project | What |
|---|---|---|
| Unit | `Ume.Rag.Pipelines.Tests` | schema validation; every §4 rule with a failing and a passing graph; generic port binding; exclusive-merge detection; Merkle hashes: invariant under node renames, edge order, labels, form, layout; change in one param changes exactly the downstream hashes; `PipelineHash` differs when only the document stage differs; templates validate and have the documented hashes |
| Unit | `Ume.Rag.Worker.Tests` | topological order and skip propagation; *Switch*, *Auto-parse* `fallback` and *Quality check* branches incl. the render pass-through cache hit; result mapping table §7.3 (each parser and gateway code); cache keys (data-model §9 vector); `OutputHash` chain; segment cutting and continuation payloads; cost estimates; scheduler with `FakeTimeProvider` (DST change days, jitter stability, catch-up collapse); fairness order; AIMD limiter; preview context refuses DB and non-preview blob paths |
| Golden | `Ume.Rag.Worker.Tests` | per node and version: fixture input → expected output (`tests/fixtures/`); a changed output without a version bump fails |
| Integration (Testcontainers, our Postgres image) | `Ume.Rag.IntegrationTests` | dequeue under `SKIP LOCKED` with 8 parallel consumers (no double lease); fairness across 3 departments; dedup under concurrency; heartbeat and lease expiry; reaper incl. early reclaim of a dead instance; requeue without attempt; fence: superseded and lease-lost commits roll back; index writer: new, reused, revived and shadow sets, membership with PII flag and a `public` collection, snapshot test (a concurrent reader sees old or new set only); finalizer counters; partition move of queued jobs while running ones are skipped; manifest immutability and support check; preview leaves `content` unchanged (row counts and `xmin` of every table except `Jobs`, `CrawlHosts`) |
| Crash recovery | `Ume.Rag.IntegrationTests` (worker in-process, killed by cancellation without `StopAsync`, and as a container with `docker kill`) | kill during fetch, during a parse range, between embed and write, inside the write transaction; restart; assert every document ends current, no duplicate chunk sets, run counters exact: **"worker restart loses no work"** |
| Contract | `Ume.Rag.ContractTests` | parser client vs parser image error fixtures (parser §15.2) mapped per §7.3; FakeGateway residency and 402/429 answers |
| Tier matrix | `Ume.Rag.IntegrationTests` | the engine suite with `sensitive` in a separate database (data-model §6) |
| Performance | `perf.yml`, BenchmarkDotNet | R1 test (§18); benchmarks for hashing, validation and the dequeue query plan |

## 20. Milestones

| Phase | Deliverable |
|---|---|
| P0 | `Jobs` table and partitions (P0 checklist #6); dispatcher skeleton with leases, heartbeat, reaper; `WorkerInstances`; one `command` kind end to end |
| P1 | `Ume.Rag.Pipelines` (format, validator, hashes); manifest registration and support check; engine with segments, fence, cache; all P1 nodes (internals 6b/6c); fairness, slicing, parser capacity; scheduler with defaults and jitter; runs, outcomes, finalizer, follow runs; preview; publish plan, gate hook, rollback; templates `website`, `url-list`, `files`; index writer; drift sweep; metrics, 5105; the tests of §19; R1 test passed |
| P2 | canvas editing (admin); try-and-score (Auto-parse fan-out, 6c); error ports on nodes; parent-child chunks; P2 nodes; office-hours concurrency profiles if the R1 data asks for them; per-department caps in `Settings` |
| P3 | more connectors; per-class worker deployments next to a separate parser host (`Worker:Classes`) |

## 21. Changes to other documents

Applied in the step 6a review commit (2026-10-29), except the admin plan items, which detail step 10 takes up.

| Document | Change |
|---|---|
| `platform/data-model.md` §2.2 | `Documents.CurrentExecutionId uuid NULL` (fence, §7.5). `NodeOutputs.OutputHash bytea NOT NULL`. `NodeManifest.Metadata jsonb` (job class, cache class, category, deterministic, display names, deprecated). `WorkerInstances.Classes varchar(20)[]`. `Runs.ScheduledFor timestamptz NULL`; `Runs.State` adds `cancelled`; `Runs.Trigger` values (§10.1). `RunItems.PipelineVersionId uuid`; `RunItems.Outcome` values of §10.2. `DocumentSources.LastRunId uuid`. `ChunkSets.CollectionStageHash` holds `PipelineHash` (whole-graph Merkle hash, §3.3); deleted sets are revived by the writer. `PipelineVersions.CollectionStageHash` likewise |
| `platform/data-model.md` §4 | §4.1: `"RunIds" uuid[]` (all runs a job serves; `RunId` stays the primary one) with a partial GIN index `WHERE "State" IN ('queued','running')`. §4.2: requeue without attempt (`Attempts − 1`) for capacity refusals (§7.3); reaper also reclaims jobs of instances silent for 45 s; preview may write `CrawlHosts.NextAllowedAt`; concurrency "per instance, defaults; overrides in `Settings.WorkerConcurrency`"; `parse` = `min(setting, convert_slots)` |
| `platform/data-model.md` §2.1, §8, §9 | `Settings`: `WorkerConcurrency jsonb`, `DegradedFailureRatio` (0.05), `ScheduleJitterMinutes` (90), `PreviewMaxUsd` (0.50), default schedules. §9: preview blobs encrypted with a per-job key wrapped in `Jobs.Result`; parse-output prefix per §8; orphan-blob sweep in the worker's content retention |
| `platform/repo-and-deployment.md` §1, §3 | new project `src/Ume.Rag.Pipelines` (+ `tests/Ume.Rag.Pipelines.Tests`) referenced by Worker and AdminApi; `contracts/pipeline/v1/definition.schema.json`; `Worker:Classes` without `crawl`; templates under `src/Ume.Rag.MigrationService/Templates/`; package: JSON Schema validator (*to verify*, §23) |
| `platform/repo-and-deployment.md` §9 | metrics of §17; log scopes `ExecutionId`, `NodeId`; event 5105 `rag.pipeline.refused` |
| `identity/design.md` §10 | add 5105 to the table and the SIEM alert list |
| `ingestion-worker/plan.md` §4 | stage sharing by Merkle hashes (document-stage hash; chunk sets by whole-pipeline hash); *Write* has no collection parameter; node list adds *Plain text*, *CSV table*, *PDF fast path* as separate nodes; output cache classes; execution id changes only with content; reconciliation and drift sweep; follow runs for shared documents |
| `parser/design.md` / 6c | the NER endpoint for names needs its own lane like `/v1/inspect`, not a conversion slot (§18) |
| `admin/plan.md` §4 | Pipelines page: publish = plan → gate → confirm; rollback never blocked by the gate; run views per §10.5; P1 editing via template forms and generated parameter forms, canvas in P2 (already P2 there) |
| `01-general-idea.md` §10 | after review, an "Ingestion engine (step 6a)" row: reconciliation, segments by job class, Merkle stage hashes, fence, follow runs, preview isolation, publish plan + shadow gate sets |

## 22. Answered questions (2026-10-29)

1. **Shared documents across departments**: re-process the other department's collection at once, recorded in its
   follow run and charged to its team (§2.3).

Decided here without asking (reasons in the sections): degraded threshold 5 % (§10.3); crawl default Saturday
01:00 (§9.6); preview cost cap 0.50 USD (§11); template updates notify copies instead of changing them (§13.3); no
office-hours throttling in P1 until the R1 data asks for it (§20); preview may fetch live URLs for new sources through
the normal egress guard (§11).

## 23. To verify

- **Embedding throughput** of the gateway's on-prem BGE-M3 provider (tokens/s, batch size, effect on chat query
  embeddings): decides whether 10,000 pages/hour is reachable (§18).
- A JSON Schema 2020-12 validator for .NET used by admin and worker (candidate `JsonSchema.Net`; licence, maintenance,
  `if`/`then` support, performance); and RFC 8785 canonicalisation (same package family or ~100 lines of our own).
- `Cronos`: version, 5-field syntax, `TimeZoneInfo` DST behaviour (repo-and-deployment §2.2 marks it *to verify*).
- Npgsql binary `COPY` of `vector` columns via `Pgvector` inside a transaction; HNSW insert cost per row on the
  pilot host; transaction length for a 20,000-chunk document under `idle_in_transaction_session_timeout = 60s`.
- Recursive-CTE loose index scan over `IX_Jobs_Ready` on a partitioned table (plan shape, cost at 100k queued jobs).
- `pg_advisory_xact_lock` dedup under 500-row batch enqueues (lock count per transaction, `max_locks_per_transaction`).
- Parser NER lane capacity and latency for Swedish names (6c / parser).
- Planning numbers of §18 (CPU per page, statements per page), measured in the R1 test.
