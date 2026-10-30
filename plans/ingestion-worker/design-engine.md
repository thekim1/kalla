# Ingestion worker, part A: pipeline engine, stages, jobs, runs, preview and publish

Detail step 6a of the Ume.Rag design. Details [`plan.md`](plan.md) §2, §4 and §8–§10 (binding, incl. answered items
1–8) within the limits set by [`../platform/data-model.md`](../platform/data-model.md) §2, §4–§9,
[`../platform/repo-and-deployment.md`](../platform/repo-and-deployment.md) §3, §6, §9, [`../parser/design.md`](../parser/design.md)
§7, §8, §10 and [`../gateway-changes/design.md`](../gateway-changes/design.md) §3, §5, §7.1. Rewritten to the
decided form of [`../simplification-pass.md`](../simplification-pass.md) (2026-10-30): fixed linear pipelines,
documents and chunk sets per source, per-stage cache keys, short chunk-write transactions, advisory evaluation. Items moved to P2 by the [weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer) are listed in §25. Nothing
is implemented.

Sibling documents (where they refine this one, they win): [`design-connectors.md`](design-connectors.md) (6b: connector
internals, crawl frontier, site-profile learning, change detection, deletion guard) and
[`design-processing.md`](design-processing.md) (6c: parse routing, HTML extraction with site profiles, chunking,
personal data, embedding, evaluation). This document defines the contracts they plug into. Changes it needs elsewhere
are listed in §21; questions in §22; removed mechanisms in §24.

**Gateway conventions copied** (`~/git/llmgateway`, read-only):

| Convention | Gateway source | Here |
|---|---|---|
| Long-running loops are `BackgroundService`s driven by `PeriodicTimer(interval, TimeProvider)`; a final flush in `StopAsync` | `src/Ume.LlmGateway.Gateway/Pipeline/AuthFailureRecorder.cs:115-145`, `UsageWriter.cs:130-143` | dispatcher, heartbeat, reaper, scheduler, finalizer (§9) |
| `TimeProvider` injected everywhere, registered with `TryAddSingleton(TimeProvider.System)` | `src/Ume.LlmGateway.Infrastructure/InfrastructureExtensions.cs:89` | all time decisions (leases, back-off, cron), `FakeTimeProvider` in tests |
| Options classes with `SectionName`, data annotations, `ValidateDataAnnotations().ValidateOnStart()` | `src/Ume.LlmGateway.AdminApi/RetentionService.cs:11-26`, `InfrastructureExtensions.cs:57-58` | `WorkerOptions` (`Worker:*`) |
| Local schedule in Europe/Stockholm via `BudgetPeriods.Stockholm` | `src/Ume.LlmGateway.Domain/Services/BudgetPeriods.cs:11`, `RetentionService.cs:44-62` | cron evaluation (§9.6) |
| One instance does a periodic job: `pg_try_advisory_xact_lock` inside an execution-strategy transaction | `src/Ume.LlmGateway.AdminApi/ModelListSync.cs:148-153` | reaper, scheduler, finalizer and maintenance leadership |
| Meters from `IMeterFactory` with one telemetry name; `[LoggerMessage]` source-generated logs | `UsageWriter.cs:616-636`, `:565-583`; `src/Ume.LlmGateway.ServiceDefaults/Extensions.cs:21`, `:83-89` | meter `Ume.Rag`, `rag.*` names (repo-and-deployment §9) |

## 1. Summary of decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Engine | One **fixed linear pipeline per source type**: fetch → scan → detect → parse → clean → chunk → personal data → embed → index. A pipeline version is (template, template version, parameter set). Stages are `IPipelineNode` implementations in code; no graph, no user-added stages (§2, §3) |
| 2 | Ownership | A **document belongs to one source** (source, identity); a **chunk set** belongs to one document and is keyed by (document, pipeline version, site-profile version, embedding profile, content hash). Reuse across sources is by content-addressed caches only (§2.1, §8) |
| 3 | Work model | **Reconciliation**: desired state = every live document's live chunk set matches its source's published pipeline version, its host's site-profile version and its current content. `EnsureCurrent(document)` enqueues only what is missing; a nightly sweep catches drift (§2.3) |
| 4 | Jobs per document | Stages declare a job class (`fetch`, `parse`, `embed` or `inline`); a job runs stages until one of another class, then enqueues **one continuation job** in the same transaction. 2 jobs per web page, 3 per PDF (§7.4) |
| 5 | Cache keys | Per stage: `SHA-256(inputHash ‖ stage ‖ stageVersion ‖ settingsHash)`; parser output by (content hash, parser settings hash, parser build). No whole-graph hashes (§3.3, §8) |
| 6 | Results | `Success | TransientError | FatalError`; capacity refusals (`busy`, `not_ready`, `duplicate_in_flight`, 429, unconfirmed config) requeue **without** an attempt (§7.3) |
| 7 | Execution id | A generation per (document, content); changes only on new content or a forced reprocess. It is a **fence**: commits check it and the job lease in the same transaction (§7.5) |
| 8 | Semantics | At-least-once execution, effectively-once effects: content-addressed blobs and cache rows, dedup keys, fenced swaps (§7.6) |
| 9 | Queue | `Jobs` unpartitioned; dedup by a partial unique index on `DedupKey`; a reclaim after an instance died is not an attempt, 3 reclaims → dead-letter (§9.4). P1 dispatch by class, priority and age; per-department fairness, batched dequeue and coalesced `NOTIFY` are P2 (§9.3) |
| 10 | Scheduler | Cron in Europe/Stockholm, deterministic per-source jitter, catch-up collapses to one run (§9.6) |
| 11 | Runs | Run items written only at a terminal outcome; the finalizer derives counters from items; degraded when > 5 % of processed items fail (§10) |
| 12 | Preview | Same engine with a preview context: no DB access for stages, blobs only under `previews/{jobId}/`, *Index* never runs, *Embed* estimates by default; produces personal-data state like any execution (§11) |
| 13 | Publish | Plan → re-process (new sets written next to the live ones, swapped per document) → evaluation report → keep or roll back; previous sets kept 14 days for one-click rollback (§12) |
| 14 | Index writer | New set written **invisible** (`Chunks.DeletedAt` set, state `writing`) in batches of ≤ 1,000 chunks in their own short transactions, then **one short swap transaction** per document (fence, `LiveChunkSetId`, clear `DeletedAt` on the new set's chunks, set it on the old set's, old set `superseded`); an orphan sweep removes sets never swapped (§14) |
| 15 | Config | Each execution resolves the published version at its start and carries it through its continuations; security-relevant reads use the config grace window at job start **and** at the swap (§15) |

## 2. Engine model

### 2.1 Stages

| Stage | Does | Writes | Reused across sources by |
|---|---|---|---|
| `fetch` | fetch or upload (6b); raw blob by content hash | `Documents` facts, raw blob | crawl frontier (P1 per source; from P2 shared, one request per URL per freshness window) and raw blob key |
| `scan` | ClamAV for files (6b §11) | quarantine on a finding | – |
| `detect` | sniff media type and language; `/v1/inspect` for parser-bound types (6c §2) | facts | cache (intermediate) |
| `parse` | routes per document: HTML extraction with the host's site profile, text/Markdown, CSV in the worker; PDF, Office, ODF, images to the parser (6c §3) | `NodeOutputs` (+ blobs) | parse cache: (content hash, parser settings hash, parser build), or the worker stage key |
| `clean` | normalisation and furniture (6c §4) | `NodeOutputs` | cache (intermediate) |
| `chunk` | split by headings, chunk, metadata from path and profile (6c §5–§7) | `NodeOutputs` | cache (intermediate) |
| `personal-data` | patterns, gated NER, counts and flag for this set (6c §9) | `NodeOutputs`; state on the chunk set | cache (intermediate) |
| `embed` | vectors (6c §11) | `EmbeddingCache` | embedding cache: (level, profile, text hash) |
| `index` | invisible chunk batches, then the swap (§14) | `ChunkSets`, `Chunks`, the document's live pointer | – |

The root (`fetch`) runs at source level in `sync`/`fetch` jobs (6b) and yields one raw content per document; every
other stage runs at document level (the separation KM lacked, `../../platforms/kernel-memory-and-dotnet.md:214-219`).
Sources of two departments crawling the same site share the raw blob and the parse cache (and from P2 the frontier
request), but
each has its own documents and chunk sets: nothing couples departments.

### 2.2 Stage values

| Value | Content | Produced by |
|---|---|---|
| `RawContent` | blob reference (`raw/{level}/{hash}`, data-model §9), media type declared and sniffed, size, URL or upload facts; carries a `Facts` bag (detect results, inspect signals) | `fetch`, `detect` (adds facts) |
| `ParsedDocument` | parser `result.json` shape (blocks with `md`, level, page, layer; tables; pages with signals; parser design §7.3), plus `Facts` (breadcrumb, description, keywords, visible date, site-profile version) | `parse`, `clean` |
| `Chunks` | flat chunks (written as `Chunks.Kind = child`; parent-child only if evaluation shows query-time section expansion insufficient, 6c §5.3): text, heading path, page span, overlap characters, tags, document type, token count, `SearchText` | `chunk` (personal data adds facts only) |
| `EmbeddedChunks` | `Chunks` + vector + profile id | `embed` |

The value types are internal C# types checked by the compiler; there are no ports or edges to validate.

### 2.3 Reconciliation

The desired state for one document `D` of source `S`:

```
v    = published version of S's pipeline (confirmed config snapshot)
p    = current site-profile version of D's host (HTML documents only, else null; 6b)
want = ChunkSet(D, v, p, profile(S.Collection), D.ContentHash)
D.LiveChunkSetId == want.Id   (state live or held, §14)
```

`EnsureCurrent(D)` compares this with the database and, if the live set is not current, enqueues the missing work
from the first stage whose cache key has no output (§8), deduplicated by `DedupKey`. If a set with the wanted key
already exists (kept 14 days for rollback, §12.3) it is only swapped back; if the current set differs only by label
(the plan found no stage affected, or a noise-only change), it is **re-keyed** in one row (§14).

Callers: the fetch path after new content (6b), a published pipeline version (`reprocess` run), a new site-profile
version for a host (6b), a cleared PII flag, a collection made `public` (§15), and the **nightly drift sweep** (01:30,
run by the maintenance instance, §9.4): documents whose live set's `PipelineVersionId`, site-profile version or
`ContentHash` differs from the wanted one, or whose personal-data state is `pending` (6c §9.6). The sweep is what makes
"no lost work" independent of any single job surviving (Onyx F6 silent stalls, `../../platforms/onyx.md:267`; Dify
documents stuck "Queuing", `../../platforms/dify.md:265`).

## 3. Definition format

### 3.1 Pipeline version (`contracts/pipeline/v2/definition.schema.json`)

Stored in `config."PipelineVersions"."Definition"`:

```json
{ "schema": "pipeline/v2",
  "template": "website", "templateVersion": 1,
  "params": { "ocr": "auto", "tables": "accurate", "chunkTokens": 400, "overlapPercent": 15, "pathRules": [] } }
```

`params` holds only values that differ from the template defaults or that the editor set; defaults are applied before
hashing and validation. A pipeline version is immutable once published.

### 3.2 Templates and the stage manifest

A **template** (`src/Ume.Rag.MigrationService/Templates/{name}.v{n}.json`, seeded insert-if-absent; a changed
template is a new version) names its stages with their code versions and declares its parameters:

```json
{ "template": "website", "version": 1,
  "stages": { "fetch": 1, "detect": 1, "parse": 1, "clean": 1, "chunk": 1, "personal-data": 1, "embed": 1, "index": 1 },
  "parameters": {
    "ocr":            { "stage": "parse", "appliesTo": ["parser"], "schema": { "enum": ["auto", "force", "off"] }, "default": "auto" },
    "tables":         { "stage": "parse", "appliesTo": ["parser"], "schema": { "enum": ["fast", "accurate"] }, "default": "accurate" },
    "chunkTokens":    { "stage": "chunk", "schema": { "type": "integer", "minimum": 200, "maximum": 800 }, "default": 400 },
    "overlapPercent": { "stage": "chunk", "schema": { "type": "integer", "minimum": 0, "maximum": 30 }, "default": 15 },
    "pathRules":      { "stage": "chunk", "schema": { "$ref": "#/defs/pathRules" }, "default": [] } },
  "labels": { "ocr": { "sv": "Textigenkänning (OCR)", "en": "OCR" } } }
```

- `stage` is the parameter's **effect tag**: the first stage its change affects. `appliesTo` narrows it to a route
  (`html`, `parser`, `text`, `csv`), so a changed OCR mode does not re-process HTML pages. The publish plan is computed
  from these tags (§12.2) from P2 (weakness review §1); P1 publish re-processes every document.
- The embedding profile is not a parameter: it comes from the collection (`Collections.EmbeddingProfileId`, set by
  platform admins only, R3). Personal-data detection has no switch: every template contains the stage.
- **Stage manifest (R7)**: at startup the worker registers each stage type and code version it contains in
  `content."NodeManifest"` (with live support per instance in `content."WorkerNodeSupport"`); stage parameter schemas
  come from the templates. Publishing is refused if no running worker supports every stage version of the template
  version (safe rolling updates). A stage's output change for the same input requires a stage version bump, shipped in
  a new template version, enforced by golden tests (§19).

### 3.3 Stage keys

```
settingsHash(s) = SHA-256(canonical(settings of stage s))         RFC 8785 canonical JSON of the template parameters
                                                                   tagged for s, plus inputs the stage reads (parse
                                                                   rule id@version, site-profile version for HTML)
key(s)          = SHA-256(inputHash ‖ s ‖ stageVersion(s) ‖ settingsHash(s))
inputHash       = OutputHash of the previous stage's output; for detect and parse the raw content hash
key(parse)      = SHA-256(contentHash ‖ "parser" ‖ parserBuild ‖ parserSettingsHash)     parser-bound documents
```

Keys are per stage only; the chain is linear, so a changed parameter changes the keys of its stage and every stage
after it, and nothing before it. The live chunk set records the keys and `OutputHash`es of the stage outputs it was
built from, so re-processing from a later stage reads exactly those outputs: a parser upgrade (new build in the key)
does not force a re-parse when only chunking changes.

## 4. Validation

Run by `PipelineValidator` (`Ume.Rag.Pipelines`) in the admin on save and publish, and **again by the worker** when it
loads a published version (fail closed: an invalid published version is not run; event 5105, §17).

| # | Rule | On |
|---|---|---|
| 1 | Definition matches `definition.schema.json`; params ≤ 16 KB | save |
| 2 | (`template`, `templateVersion`) exists; `deprecated` template versions warn | save |
| 3 | Every param is declared by the template and validates against its schema (JSON Schema 2020-12; library *to verify*, §23); cross-parameter rules live in the schema (`if`/`then`), never in worker-only code, so the admin can check them (R7) | save |
| 4 | Every stage version of the template version is supported by a live worker instance (`WorkerNodeSupport` row, heartbeat < 60 s) that also runs the stage's job class (`WorkerInstances.Classes`) | publish |
| 5 | LLM-calling stages (P2) on a source above `public`: model allowed at the level's residency (platform plan §6) | publish |

Errors carry the param name and a code (`param_invalid`, `template_unknown`, `stage_version_unsupported`, …) so the
form can point at the fault.

## 5. Stage interface and manifest

### 5.1 C# sketch (`Ume.Rag.Worker/Pipelines`)

```csharp
public interface IPipelineNode                                // one stage; the name stays for continuity
{
    NodeDescriptor Descriptor { get; }                         // stage, version, job class, cache class
    NodeCost EstimateCost(NodeCostContext context);             // pure; no I/O
    ValueTask<NodeResult> ExecuteAsync(NodeContext context, StageValue input, CancellationToken ct);
}

public abstract record NodeResult
{
    public sealed record Success(StageValue Output, NodeFacts Facts, NodeUsage Usage) : NodeResult;
    public sealed record TransientError(string Code, TimeSpan? RetryAfter = null, bool CountsAttempt = true) : NodeResult;
    public sealed record FatalError(string Code, RunItemOutcome Outcome) : NodeResult;
}

public sealed record NodeFacts(IReadOnlyDictionary<string, JsonElement> Values, IReadOnlyList<string> Degraded);
public sealed record NodeUsage(decimal CostUsd, long LlmTokensIn, long LlmTokensOut, long EmbeddingTokens, int ParserPages);
public sealed record NodeCost(long LlmTokens, long EmbeddingTokens, int ParserPages, TimeSpan Time);

public sealed class NodeContext   // built by the engine; stages get no DbContext
{
    // Document (id, source, collection, level, content hash, facts), ExecutionId, RunId, Stage, Settings,
    // Mode (Run | Preview), Deadline, Blobs (run: documents/{id}/nodes/…; preview: previews/{jobId}/),
    // Gateway (headers prefilled, §7.7), Parser (parse and personal-data stages), SiteProfile (HTML), Time, Logger
}
```

- **Rules**: a stage is a pure function of (input, settings, its version) apart from declared external calls (parser,
  gateway, fetch). Stages never write the database: the engine persists. Old stage versions stay in the binary for at
  least one release after their successor (rolling updates).
- **Cost estimation**: `EstimateCost` receives document facts (media type, page count, text length, current chunk
  count) and settings; returns tokens, parser pages and time. The admin API adds USD from the gateway catalogue price
  when it shows a preview or plan, else "unknown" (never 0).

### 5.2 Registration and support

At startup, before the dispatcher dequeues anything:

1. Upsert `WorkerInstances` (`InstanceId` = new v7 id, `BuildVersion`, `StartedAt`, `Classes` from `Worker:Classes`).
2. For each stage in DI: insert `NodeManifest` if absent; if present with a different descriptor, stop with an error
   naming the pair. Insert `WorkerNodeSupport` rows for this instance.
3. Log a warning for every **published** pipeline version whose template version uses a stage version this instance
   lacks.

Heartbeat every 15 s (`WorkerInstances.LastHeartbeatAt`); clean stop deletes the instance (support cascades); any
instance deletes instances silent for 5 minutes. A job that needs a stage version this instance does not support is
released (`AvailableAt + 30 s`, no attempt); after 10 minutes without any supporting instance it fails with outcome
`failed`, reason `node.version_unsupported`.

## 6. P1 stages

All version 1. Class: job class (§7.4); cache: §8. Details column: which document specifies the internals.

| Stage | Class | Cache | Template parameters | Details |
|---|---|---|---|---|
| `fetch` (URL list, crawl, upload) | fetch | none (raw blob is the document) | – (source settings, 6b) | 6b |
| `scan` | fetch | none | – (uploads and fetched files; ClamAV, fail-closed) | 6b §11 |
| `detect` | inline | intermediate | – (`/v1/inspect` for PDF, TIFF, Office, ODF) | 6c §2 |
| `parse` | inline for HTML, text, Markdown, CSV; **parse** for parser-bound types | **durable** | `ocr`, `tables` (parser route); parse rules and the site profile are inputs, not parameters | 6c §3 |
| `clean` | inline | intermediate | – | 6c §4 |
| `chunk` | inline | intermediate | `chunkTokens`, `overlapPercent`, `pathRules` | 6c §5–§7 |
| `personal-data` | inline (the gated `/v1/ner` call runs inside the current job) | intermediate | – | 6c §9 |
| `embed` | embed | none (embedding cache) | – (profile from the collection) | 6c §11 |
| `index` | embed | none | – | §14 |

Unsupported media types end at `parse` with outcome `unsupported` (6c §3.2); the P2 formats (legacy Office, RTF, email) with reason `format_deferred`. P2 stages (plan §4): SharePoint fetch,
table extract, PII mask, LLM summary/keywords, describe pictures, legacy Office, RTF and email with child documents
(simplification pass §1).

## 7. Execution

### 7.1 Per document

1. Load the execution's pipeline version and its template version (cached per id; validated, §4).
2. Run the stages in order. Before a cacheable stage, look up its key (§8); a hit uses the cached output.
3. After each stage: check the cancellation token (supersession, lease loss, shutdown; §7.5) and the deadline.
4. The document ends at `index` (success), at a `FatalError`, or at `parse` with `unsupported`/`skipped` (empty after
   extraction).

### 7.2 Routing inside stages

There are no branches between stages. `parse` routes per document by the detected media type (HTML, text, CSV in the
worker; parser-bound types to the parser with per-page strategies from `/v1/inspect`, 6c §3). The run item's
`Strategy` records what happened (`parse=html(profile=umea.se@3)`,
`parse=auto[text-layer:1-40,docling-ocr:41-44]`), so a silent "configured but did nothing" step (RAGFlow #14376,
`../../platforms/ragflow.md:222`) shows up per document. Short pages are an info note (`quality.short`), never
`degraded` because rendering is off (6c §8).

### 7.3 Results, retries and back-off

Queue mechanics are data-model §4.2: `TransientError` → `AvailableAt = now + least(10 s · 2^Attempts, 1 h) ± 20 %`;
`FatalError` → terminal at once; attempts exhausted → `dead`. Requeues that do not count an attempt set
`Attempts = Attempts − 1` in the same statement (dequeue had incremented it).

| Cause | Result | Attempt counted | Next |
|---|---|---|---|
| Parser `busy` / `not_ready` / `duplicate_in_flight` (parser §7.5), also on `/v1/ner` | transient | no | `Retry-After` (default 5 s) |
| Parser `timeout`, `worker_crashed`, `resource_limit` / `ocr_failed`, `conversion_failed` | half the range inside the job; a single page that still fails is a **missing page** (`degraded` while ≤ 10 % and ≤ 20 pages, else fatal; 6c §3.3) / transient once, then as before | – / yes | – / back-off |
| Parser 400 / 413 / 415 / 422 others | fatal: `failed`, `too-large`, `unsupported`, `encrypted`, `corrupt`; `limit_exceeded` also event 5104 | – | – |
| Gateway 429, or 503 with `Retry-After` / other 5xx, timeouts | transient | no / yes | `Retry-After` / back-off |
| Gateway 402 (budget used up) | transient | yes | `AvailableAt` = next full hour; after `MaxAttempts` `failed`, reason `gateway.budget_exhausted` |
| Gateway 403 `max_residency_not_met`, `delegation_not_allowed`; 400 | fatal, reason `gateway.<code>` | – | – |
| Gateway answer above the residency ceiling (`x-ume-residency`) | fatal, result discarded, event 5003 | – | – |
| Config not confirmed within the grace window (§15) | transient | no | 2 s, then 5 s |
| Stage version not supported here (§5.2) | transient | no | 30 s |
| Unhandled exception in a stage | transient (`node.exception`; type name only, never the message) | yes | back-off |

Bounded redelivery with back-off and an explicit lease follows RAGFlow's queue (`../../platforms/ragflow.md:57-58`).
A job whose instance died is reclaimed **without** an attempt; a job reclaimed 3 times is dead-lettered as a poison
suspect (§9.4).

### 7.4 Job segments and continuations

Each stage's job class is `fetch`, `parse`, `embed` or `inline`. The engine runs stages inside the current job while
they are `inline` or of the job's class. At the first stage of another class it stops, and in **one transaction**
writes the stage outputs reached so far, enqueues the continuation job (class of that stage, payload: execution id,
pipeline version, stage, cache key of its input) with a dedup key, and completes the current job (fenced, §7.5). This
is the KM pointer pattern made atomic (KM saves state and enqueues separately and repairs with rollback logic,
`../../platforms/kernel-memory-and-dotnet.md:61-65`, `:161`), with at most two hops per document.

| Document | Jobs |
|---|---|
| Web page | `fetch` (fetch, detect, parse, clean, chunk, personal data) → `embed` (embed, index) |
| Unchanged web page (same hash) | `fetch` only |
| PDF / Office / image | `fetch` (fetch, scan, detect) → `parse` (parse, clean, chunk, personal data) → `embed` |

Per-class concurrency then holds by construction: parser conversion slots are used only by `parse` jobs, gateway
embedding load only by `embed` jobs, politeness by `fetch` (Eneo and Dify run everything through one worker, F4,
`../../platforms/eneo.md:228`, `../../platforms/dify.md:234`). The rare gated NER call (6c §9.6) runs in whichever job
reaches `personal-data`; a `busy` answer requeues that job without an attempt and it resumes from the cached chunk
output.

### 7.5 Execution id, supersession, cancellation

- `Documents.CurrentExecutionId` is set when the document's content changes, or by a `reprocess` with `force` (cache
  bypass from a stage). It is **not** changed by a new pipeline version or site-profile version: those only add work
  through `EnsureCurrent`.
- Every job of an execution carries its id. **Fence** in each committing transaction:

```sql
SELECT 1 FROM content."Documents"
 WHERE "Id" = @doc AND "Classification" = @level AND "CurrentExecutionId" = @exec AND "ContentHash" = @hash
 FOR SHARE;                                              -- blocks a concurrent supersession until commit
UPDATE content."Jobs" SET "State" = 'succeeded', "CompletedAt" = now(), "Result" = @summary
 WHERE "Id" = @job AND "State" = 'running' AND "LeaseOwner" = @me;
-- either returns no row → ROLLBACK, the job is superseded or lost its lease
```

- **Cancellation of running work**: a per-instance *supersession watcher* polls every 5 s
  `SELECT "Id", "CurrentExecutionId" FROM content."Documents" WHERE "Id" = ANY(@running)` for documents with running
  jobs, and cancels the token of any job whose execution is outdated. Closing the parser connection makes the parser
  kill the slot (parser §10.4); gateway calls are aborted. The job ends `cancelled`, outcome `superseded`, without a
  retry. Queued jobs of an old execution see the mismatch at start and end `cancelled` in one statement.
- Newer run cancels older (KM `ExecutionId`, `../../platforms/kernel-memory-and-dotnet.md:145-146`), but only for new
  content.

### 7.6 Transactions, idempotency, at-least-once

| Effect | Where | Why it is safe to repeat |
|---|---|---|
| Stage output blobs | written **before** the DB commit, at content-addressed paths (`…/{cacheKey}…`), one atomic `IBlobStore.PutAsync` (file system: temp + rename; S3: one PUT or completed multipart upload; [`../performance-pass.md`](../performance-pass.md) §3.13); blobs are write-once, a new key for new content | same key → same plaintext; orphans (no `NodeOutputs` row after 24 h) removed by the sweeper |
| `NodeOutputs` rows | segment commit, `INSERT … ON CONFLICT ("CacheKey") DO NOTHING` | content-addressed |
| `Documents` facts (media type, language, page count, strategy, parse cache key, site-profile version) | segment commit, fenced | last fenced writer wins; the fence rejects stale executions |
| Continuation jobs | same transaction, `DedupKey` (`parse:{doc}:{exec}`, `embed:{doc}:{exec}:{pipelineVersion}`) | `INSERT … ON CONFLICT DO NOTHING` on the partial unique `DedupKey` index (data-model §4.2) |
| `EmbeddingCache` | own short transaction, `ON CONFLICT DO NOTHING` | cache |
| Invisible chunk batches | own short transactions (§14) | the chunks carry `DeletedAt` until the swap; a retried execution writes a new set, the orphan sweep removes the old one |
| Swap, run item, job completion | one short fenced transaction (§14) | unique key on `ChunkSets`; the swap sets the live pointer and set states and flips `DeletedAt` on the two sets' chunks |
| Gateway cost | charged by the gateway per call; a retried call is charged again | bounded by caches (parse, embedding) and attempts |

Jobs hold a connection only for these short statements, never across an HTTP call, so the pool (10 + 1 LISTEN,
data-model §1.1) serves ~20 concurrent jobs.

### 7.7 Gateway calls

Every call from a stage goes through `IGatewayClient` with headers set by the engine, never by the stage. The worker
holds the per-level delegating keys `OnPrem` and `Eu` (never the open key, simplification pass §6); the client's
`DelegatingHandler` refuses a call without an explicit ceiling or with a key that does not match it. **Until gateway
release 2** (P1, weakness review §5) these are the pilot department's team's own `OnPrem` and `Eu` keys, every call
is charged to that team, and no `x-ume-on-behalf-of` is sent (so no echo is checked).

| Header | Value |
|---|---|
| `x-ume-on-behalf-of` | from gateway release 2 (P2): the owning team of the document's collection (`Collections.OwnerTeamId`; gateway design §5.2) |
| `x-ume-max-residency` | `OnPrem` for embeddings and evaluation always (R3); other LLM calls (P2 stages) by the document level: `sensitive` `OnPrem`, `internal` and `public` `Eu` (the worker has no `External` key) |
| `x-ume-run-id`, `x-ume-step` | `RunId` (GUID) and stage **type** (`embed`, `eval-judge`), never names (gateway design §7.1) |
| `traceparent` | current activity |

The response is checked before anything is stored: `x-ume-residency` within the ceiling (else fatal + 5003); when
`x-ume-on-behalf-of` was sent, its echo must match (`Gateway:RequireDelegationEcho`), else fatal
`gateway.delegation_not_applied`. `x-ume-cost-usd` is added to the stage's `NodeUsage`.

## 8. Output cache

- **Key**: §3.3. `NodeOutputs` keeps `OutputHash` (SHA-256 of the serialised output) so the next key is computed
  **without loading the blob**. The `OutputHash` of a `ParsedDocument` covers content only (blocks, tables, pages with
  signals, title, language, breadcrumb) and **excludes** volatile facts (fetch time, timings, parser build), so a
  noise-only change can re-key a chunk set (6c §12).
- **Classes**:

| Class | Stages | Kept |
|---|---|---|
| `durable` | `parse` | while a document referencing it lives (`ExpiresAt` null, `IsParseOutput = true`); when no longer referenced, `ExpiresAt = now + 30 days` |
| `intermediate` | `detect`, `clean`, `chunk`, `personal-data` | 30 days (`Settings`) from last use; a hit extends `ExpiresAt` at most once a day |
| `none` | `fetch`, `scan` (raw blob is the document), `embed` (own `EmbeddingCache`), `index` | – |

- The parse output is multi-blob (parser §7.6): `NodeOutputs.BlobPath` is a per-level prefix keyed by the cache key,
  not the document id (layout in data-model §9), holding `result.json`, `docling/{from}-{to}.json.gz` and `ranges/{from}-{to}.result.json`
  (intermediate, 30 days, written per completed range so a crashed or yielded parse job resumes at the next range).
  Other stages use one blob `…/{cacheKey}.json.gz`.
- **Sharing by content**: the parse key has no document id, so two sources (any department, same level) with the same
  bytes and parser settings share one parse; different levels never share (blobs are encrypted per level). Worker
  stages after `parse` are cheap and run per document.
- **Invalidation is by key**: a new stage version, changed settings or changed upstream output gives a new key; nothing
  is deleted eagerly. Explicit bypass: a `reprocess` with `force` and `fromStage` skips lookups from that stage on (new
  execution id).
- **Retention**: the worker's content retention deletes expired rows in batches of 1,000 and then their blob
  prefixes; orphan blobs older than 24 h without a row are removed by the same job.

## 9. Jobs and scheduler

### 9.1 Classes

Data-model §4.2 holds order, leases and attempts. Concurrency defaults below are per instance and are overridden by
the deployment value `Worker:Concurrency:<class>` (repo-and-deployment §3 settings rule);
`config."Settings"."WorkerConcurrency"` holds only fleet-wide shares and caps (P2).

| Class | Concurrency | Payload (ids and parameters only, ≤ 4 KB) | Runs |
|---|---|---|---|
| `preview` | 2 | `kind` (`stage-preview`/`publish-plan`; 6b: `preflight`/`site-profile`), `pipelineVersionId`, `documentIds` or `urls` (a `preflight` of a URL list samples ≤ 20 URLs, admin design §4.7), `untilStage`, `basePreviewJobId` | §11, §12 |
| `command` | 2 | `kind` + ids: `clear-pii-flag`, `confirm-deletion`, `reject-deletion`, `edit-eval-question`, `reprocess-document`, `cancel-run`, `release-hold`, `approve-site-profile`, `reject-site-profile`, `edit-site-profile`, `rollback-site-profile` (admin design §4.7) | 6a/6b/6c each own their kinds |
| `sync` | 2 | `sourceId`, `runId`, `kind` (`sync`/`retry-failed`/`reprocess`), `phase` (`list`/`fetch`/`reconcile`), `scheduledFor`, `force`, `fromStage`, checkpoint reference (`ConnectorCheckpoints`, 6b §2.3); uploads `batchId` | 6b (listing, reconcile), §9.2 |
| `fetch` | 8 (frontier limits per host) | `sourceId`, `runId`, identity or frontier entry; `executionId` once known | 6b, then §7 |
| `parse` | `min(Worker:Concurrency:parse, /v1/info capacity.convert_slots)` | `documentId`, `level`, `executionId`, `pipelineVersionId`, `stage`, `inputKey`, `slice` | §7, §9.5 |
| `embed` | 4, adaptive (§18) | `documentId`, `level`, `executionId`, `pipelineVersionId`, `runId`, `inputKey` | §7, §14 |
| `eval` | 1 | `kind`: `generate-set`, `top-up-set`, `publish-report`, `nightly`, `on-demand` (retrieval evaluation only; the answer-level `answers` run and the P2 skill gate run in `rag-admin`, 6c §14.8) | 6c §13–§14 |
| `attachment` (P2) | 2 | `attachmentId`, `conversationId`, `level` (chat design §15.2) | scan (ClamAV, fail-closed) → sniff/type check → parse → chunk → PII (recorded, not blocking) → embed on-prem → post chunks to the API's internal route; dispatched right after `preview`; never writes `chat` and holds no chat keys |

`Worker:Classes` lists the classes an instance runs (`crawl` is not a class: discovery is part of `sync`/`fetch`).

**Mixed versions during a rolling update**: payloads are additive (tolerant reader); a job that needs a newer build
carries `MinBuild` in its payload, and an older instance requeues it without an attempt
(`rag.jobs.requeued{reason=build}`) ([`../performance-pass.md`](../performance-pass.md) §3.4).

### 9.2 How a sync fans out

6b (`design-connectors.md` §2, §6, §13) owns the details: URL-list and crawl items become frontier **claims**
released per host; the `sync` payload carries a `phase` (`list`/`fetch`/`reconcile`) and a checkpoint reference;
deletions run in a **reconcile phase** after all fetches, enqueued by the finalizer. The engine side:

1. `sync` job: resolve source and pipeline version at the confirmed snapshot; open or reuse the `Runs` row; run the
   connector's listing and change detection (6b), checkpointed, yielding after 1,000 items or 2 minutes (re-enqueued
   as a continuation, no attempt).
2. Per changed or new item: a frontier claim or a `fetch` job, `DedupKey = fetch:{sourceId}:{identityHash}`.
3. Per unchanged item: `EnsureCurrent` (reads; enqueues only if the live set is not current) and a run item
   `unchanged`, written in batches.
4. The sync job ends when listing is done; the run stays `running` until the finalizer sees no open jobs (§10.4).

`retry-failed` lists only the documents whose last run item failed (`IRetryFailedConnector`); `reprocess` (publish,
site-profile change, §12) iterates the source's live documents and calls `EnsureCurrent`, never fetching.

### 9.3 Dispatcher

One dispatcher loop per instance (woken by `LISTEN rag_jobs`, polling every 2 s; data-model §4.3) fills free slots in
class order (`preview`, P2 `attachment`, `command`, `sync`, `fetch`, `parse`, `embed`, `eval`), within a class by
`Priority` and age, one job per dequeue statement (data-model §4.2 query).

Queue access goes through the **`IJobQueue`** seam in `Ume.Rag.Infrastructure` (Postgres implementation = the
data-model §4.2 SQL; enqueue takes the caller's transaction; a broker only behind the performance pass's §6 trigger).
Batched completion of non-fenced outcomes (fetch `unchanged`) is buffered ≤ 100 ms and written with one
`UPDATE … FROM unnest(…)`. The P1 pilot is one department, so per-department fairness waits for P2 together with
batched dequeue and coalesced `NOTIFY` (§24).

### 9.4 Leases, heartbeats, reaper, shutdown

| Mechanism | Rule |
|---|---|
| Lease | set at dequeue (data-model §4.2 durations) |
| Heartbeat | every `lease / 3`, one statement for all of the instance's running jobs: `UPDATE … SET "LeaseUntil" = now() + lease WHERE "LeaseOwner" = @me AND "State" = 'running' AND "Id" = ANY(@ids) RETURNING "Id"`; a job missing from the result has lost its lease → its token is cancelled and its commit will fail the fence |
| Reaper (every 10 s, any instance, `pg_try_advisory_xact_lock`) | `running` jobs with `LeaseUntil < now()`, **or** whose `LeaseOwner` instance has no heartbeat for 30 s (two missed heartbeats; worst case 40 s after a kill, P0 #20), go back to `queued` **without** counting an attempt and with `Reclaims + 1`; at 3 reclaims → `dead`, run item `failed`, reason `worker.poison_suspect` |
| Graceful stop (`stop_grace_period` 120 s) | stop dequeuing; give running jobs 90 s; then cancel the rest and release their leases (`queued`, attempt not counted, `AvailableAt = now()`); delete the instance row |
| Crash | reaper returns the jobs within 40 s; resumed work replays from the cache (§8), parse jobs from the last completed range |

A document that kills its process (OOM in a stage) is reclaimed at most 3 times, so it dies instead of looping, while
an ordinary deploy or host restart costs no attempts. Early reclaim keeps a killed worker from idling the parser for a
15-minute `parse` lease. With the drift sweep (§2.3) this is the "worker restart loses no work" target.

**Maintenance** (drift sweep, content retention, nightly deletion of terminal jobs older than 7 days in batches, orphan
sweep of never-swapped sets, §14) runs on the instance with `Worker:Maintenance=true` (exactly one in P1), each task
under `pg_try_advisory_xact_lock` per batch and resuming from its batch checkpoint. Lease leaders in
`content."LeaderLeases"` come with the first multi-host deployment (P2, §24).

### 9.5 Parse concurrency and slicing

- Capacity = `capacity.convert_slots` from the parser's `/v1/info`, read at start and every minute (parser §10.1); the
  `parse` class never runs more jobs than that per instance. `busy` requeues without an attempt (§7.3).
- **Preview first**: an in-process priority gate in front of the parser client lets a waiting preview take the next
  free slot before any `parse` job starts its next range. With the pilot's single slot a preview waits at most one
  range (≤ 50 % of the 120 s deadline, parser §8.1).
- **Slicing**: a `parse` job yields after 10 minutes at a range boundary: it writes the completed ranges, re-enqueues
  itself with `slice + 1` (no attempt), and completes. One 2,000-page OCR PDF (≈ 2 hours, parser §14) then shares the
  parser instead of holding it.
- `/v1/inspect` (in `detect`) uses the parser's separate inspect lane and is not counted against slots; the gated
  `/v1/ner` call uses the parser's NER process (parser §7.8) and is not counted either.

### 9.6 Scheduler

A `BackgroundService` on every instance; the instance holding `pg_try_advisory_xact_lock(<scheduler key>)` in its
1-minute tick does the work (gateway `ModelListSync.cs:148-153` pattern).

- **Cron** (`Sources.Schedule`, 5 fields, Cronos *to verify*) evaluated in Europe/Stockholm; DST gaps run once at the
  next valid minute, overlaps once.
- **Defaults** (answered 7), set by the admin when a source is created: URL list `0 2 * * *` (nightly 02:00), crawl
  `0 1 * * 6` (Saturday 01:00; a host whose site profile marks `lastmod` reliable runs incrementally every night, 6b;
  the flag is set only after a 500-URL sample, so umea.se starts with weekly full traversals, weakness review §3),
  upload none (the upload itself enqueues a `sync` with `Trigger = upload`). Adjustable per source.
- **Jitter**: deterministic per source, `hash(sourceId) mod JitterMinutes` (`Settings`, default 90).
- **Due**: `next = cron.Next(lastScheduledFor ?? source.CreatedAt)`; when `next + jitter ≤ now` and no `sync` for the
  source is `queued`/`running`, enqueue one with `ScheduledFor = next`. After an outage the missed occurrences
  collapse into one run. The schedule is computed from `ScheduledFor`, never from an "updated" timestamp that other
  jobs touch (Onyx's stall, F6).
- **Sync now**: the admin inserts a `sync` job (`Trigger = manual`, `Priority` 10); if one is already queued or
  running the admin answers "already running" with its run id (dedup `sync:{sourceId}`).
- Paused or deleted sources are skipped; `rag.scheduler.lag` (now − due) is a metric.
- The scheduler also enqueues the nightly evaluation runs (6c §14.8) and the nightly drift sweep (§2.3).

## 10. Runs and run items

### 10.1 Run states

`running` → `succeeded` | `degraded` | `failed` | `awaiting-deletion-confirmation` (6b §13.2, set by the finalizer
when the guard left `DeletionProposals` waiting) | `cancelled` (source paused or deleted, or `cancel-run` command).
Kinds `sync`, `retry-failed`, `reprocess` (publish, rollback, site-profile change); `Trigger`: `schedule`, `manual`,
`upload`, `publish`, `rollback`, `site-profile`, `retry`, `config-change` (scope or identity edits, 6b §12.4), P2
`notification` (SharePoint). `Runs.Phase` (`list` → `fetch` → `reconcile` → `done`) tracks a sync run.

### 10.2 Outcomes

| Outcome | Meaning | Run counter |
|---|---|---|
| `added` | new document indexed | Added |
| `updated` | changed content, new pipeline version or new site-profile version indexed | Updated |
| `unchanged` | same hash, or live set current (incl. re-keyed) | Unchanged |
| `skipped` | excluded on purpose: robots, include/exclude rule, empty after extraction | Unchanged |
| `degraded` | indexed with gaps: missing pages after retry, low OCR confidence; `Reason` lists them | Updated/Added |
| `deleted` | document soft-deleted (6b) | Deleted |
| `unsupported` | format refused (`unsupported_format`; P2 formats `format_deferred`) | Failed |
| `too-large` | above upload, parser or engine limits (§16) | Failed |
| `encrypted` | password-protected | Failed |
| `malware` | ClamAV finding, quarantined (6b) | Failed |
| `blocked` | egress guard or robots refused a redirect target (6b) | Failed |
| `failed` | other fatal error, attempts exhausted, or poison suspect (`dead`) | Failed |
| `superseded` | newer content arrived; the newer execution records the real outcome | not counted |

`Reason` is a code plus short fixed text, `<area>.<code>` (`parser.timeout`, `http.404`, `gateway.budget_exhausted`,
`pii.flagged`), never library messages or document text (parser §7.5 rule). A document served by another source's
frontier request gets its real outcome and the fact `fetch.shared` (6b §6.2). `Strategy` holds the route (§7.2);
`CostUsd` the gateway cost of this document in this run; `DurationMs` wall time across its jobs; `ParserBuildHash` as
parser §13.1.

### 10.3 Degraded threshold

After finalization: `failed` when the sync job itself failed or every processed item failed; `degraded` when
`Failed / (processed − unchanged) > Settings.DegradedFailureRatio` (default 5 %) or any `malware`; otherwise
`succeeded`, still showing its failure count. Runs never show green while documents fail silently (Onyx F6,
comparison F6). Short pages and runs without a complete traversal are notes in the run report, not `degraded`; three
runs in a row without a complete traversal raise an alert (6b).

### 10.4 Finalizer and cost

Every 30 s (advisory-lock leader): for `running` runs whose `sync` job is terminal and that no `queued`/`running` job
serves (jobs carry one `RunId`; partial index on `RunId` for open jobs, data-model §4.1): if a `sync`-kind run's
fetches are done and it has not reconciled yet, enqueue its `sync` job with `phase = reconcile` (deletions and the
guard, 6b §13.2) and wait for it; then aggregate `RunItems` into the counters and `CostUsd`, set `State` and
`FinishedAt`. When a `reprocess` run of a publish or rollback finishes, the finalizer enqueues the `eval` job
`publish-report` for the source's collection (§12.1). Counters are derived, never incremented by jobs, so retries
cannot double-count and no hot row is updated per document.

### 10.5 What the admin shows (consumed by the admin plan §4)

| View | Contents |
|---|---|
| Source → runs | state, trigger, pipeline version, site-profile version, counters by outcome, cost, duration, queue position of the next waiting job and age of the oldest (RAGFlow's "parsing stuck" is mostly invisible waiting, `../../platforms/ragflow.md:210-212`) |
| Run detail | failures grouped by reason with counts; degraded items with their gaps; notes (short pages, incomplete traversal); per item: identity, outcome, strategy, duration, cost, parser build; links to the document |
| Document / Overview | run items (90 days), live, held and superseded chunk sets with pipeline and site-profile version, cache entries, PII state of the live set / degraded or failed runs per department, dead jobs, scheduler lag |

## 11. Preview

- **Class** `preview`, highest priority, `MaxAttempts` 1, lease 2 minutes. Started by the admin for a saved draft
  version (template parameters) or a new source's form.
- **Inputs**: up to 5 existing documents of the source (raw blob and cached outputs are read), or up to 5 URLs/upload
  ids for a source that has none yet (fetched live through the same egress guard and host politeness; the one
  `content` write a preview makes is `CrawlHosts.NextAllowedAt`, which is bookkeeping, not content).
- **Isolation**: `ExecutionMode.Preview` gives stages a blob store limited to `previews/{jobId}/` (`internal`: each
  blob's data key wrapped by `rag-blob-<level>` in its header, data-model §7; `public` previews unencrypted, as
  `public` blobs, simplification pass §4), no cache writes, a `NullIndexWriter` that throws if called, and no
  `Documents` updates; stages have no database access anyway (§5.1). An integration test asserts that no `content`
  row except `Jobs.Result`/`CrawlHosts` changes (§19).
- **Execution**: up to `untilStage` (default `personal-data`), so the preview shows the personal-data state the set
  would get (6c §9). *Embed* reports chunk count and tokens and does not call the gateway unless `embed: true`;
  *Index* never runs ("would write N chunks"). LLM stages (P2) run for real within `Settings.PreviewMaxUsd` (0.50)
  computed from `EstimateCost` before start; above it the admin must confirm.
- **Re-run from a stage**: `basePreviewJobId` reuses that preview's outputs from `previews/{baseJobId}/` and runs only
  from the first stage whose settings changed.
- **Limits**: 5 documents, 50 pages per document (first pages, through the normal range planning), 2 MB of output per
  stage shown (truncated with a marker), 2-minute deadline; blobs expire after 24 h (data-model §9).
- **Result** (`Jobs.Result`, small): per document and stage: state, duration, cache hit, facts, estimated and actual
  cost, error code; blob paths for the outputs the admin streams on demand (extracted text with breadcrumb and page
  markers, sections, chunks with prefixes and token counts, PII counts).

## 12. Publish and rollback

### 12.1 Flow

| Step | Who | What |
|---|---|---|
| 1 | admin | validate the draft (§4 incl. live support); enqueue the plan (`preview`, `kind = publish-plan`), storing `PlanJobId` and the definition's hash |
| 2 | worker | the **re-processing plan** (§12.2) and cost/time forecast into `Jobs.Result`; in P1 the plan is every live document of the affected sources (§12.2) |
| 3 | user | sees plan and forecast; confirms or cancels. Confirm is refused if the draft changed since the plan |
| 4 | admin | one config transaction: version → `published` (`PublishedAt`), previous → `superseded`, `Pipelines.PublishedVersionId`; audit 6001; enqueues a `reprocess` `sync` per source using the pipeline |
| 5 | worker | `EnsureCurrent` per live document: new sets are written not live next to the live ones and **swapped per document** (§14); unaffected documents are re-keyed (`unchanged`) |
| 6 | worker | when the `reprocess` run finishes, the `publish-report` evaluation runs on the full collection (6c §14.1) |
| 7 | user | sees the report (recall@10 and MRR with counts, compared with the previous run) next to the run; keeps the version or **rolls back** in one click (§12.3) |

The admin owns `config` and the worker only reads it, so publish states stay in `PipelineVersions.State` (`draft`,
`published`, `superseded`); the plan and the report are job results the admin shows (`Jobs.Result`,
`EvaluationRuns`). The report advises, it never blocks (6c §14.2). Dify publishes without re-processing and leaves an
index mixing versions (`../../platforms/dify.md:241-242`); here every chunk set records the pipeline and site-profile
versions that produced it, and the run shows progress until every document is current.

### 12.2 Re-processing plan

**P1 ([weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer)):** publish re-processes **every live document** of the affected sources. `EnsureCurrent`
still recomputes each stage key, so stages whose key did not change are served from the per-stage caches (§8) and cost
nothing; the forecast is the document count with a conservative time and cost estimate for the full pipeline from
`parse`, and the run reports how many documents were in fact re-keyed only. The report and keep or roll back (§12.3) are
unchanged. The plan by effect tags below is **P2**.

For each live document of the affected sources (P2):

1. take its route (`html`, `parser`, `text`, `csv`) from the stored facts;
2. **first affected stage** = the earliest `stage` tag (§3.2) among the parameters that differ between the published
   and the candidate version and apply to that route, and the earliest stage whose code version differs between the
   two template versions; none → the document is re-keyed only;
3. group documents by first affected stage; per group, estimate the work from that stage on with `EstimateCost`
   (parser pages, embedding tokens from current chunk counts, LLM tokens) and time from measured per-class
   throughput.

Example result: "1,240 documents; 1,180 from *Chunk* (re-chunk + re-embed, ~0 parser pages, ~2.1M embedding tokens,
~25 min), 60 from *Parse* (~900 parser pages, ~1 h); 0 re-keyed; estimated cost 0.00 USD (on-prem)".

A new site-profile version for a host (6b) uses the same plan logic for that host's HTML documents, with `parse` as
the affected stage (in P1 every HTML document of the host, from `parse`); its auto-apply check and proposal flow are
6b's (P2).

### 12.3 Rollback

Rollback re-publishes the previous version of the same pipeline (no new number; `superseded` → `published`), with the
same plan and report; it is never blocked. Chunk sets of the previous version are kept **14 days** after they were
superseded (§14), so the `reprocess` run finds them by key and **swaps them back** per document without parsing or
embedding. Older rollbacks re-run from the cached stage outputs. A site-profile rollback (one click, 6b) works the same
way for the host's HTML documents.

## 13. Templates

### 13.1 P1 templates

Platform templates (`Pipelines.DepartmentId = null`, `IsTemplate`), seeded by the migration service from
`src/Ume.Rag.MigrationService/Templates/*.json` (§3.2).

| Template | Source type | Stages | Parameters (defaults) |
|---|---|---|---|
| `website` | crawl | fetch → detect → parse → clean → chunk → personal data → embed → index | `ocr` (`auto`), `tables` (`accurate`), `chunkTokens` (400), `overlapPercent` (15), `pathRules` (`[]`) |
| `url-list` | URL list | as `website` | as `website` |
| `files` | upload | fetch → **scan** → detect → parse → … → index | `ocr`, `tables`, `chunkTokens`, `overlapPercent` |

**Collection default** ([weakness review §3](../weakness-review.md#3-outdated-or-removed-content-keeps-being-answered)):
the `website` template also carries a default for its collection's `RetrievalSettings.recency`, age decay for PDFs
(`{ "halfLifeDays": 730, "weight": 0.15, "kinds": ["pdf"] }`, knowledge design §6.5, §12.1). The admin sets it on the
collection when the collection's first `website` source is added and `recency` was never set; it is shown in the source
drawer and edited on the collection. Other templates leave `recency` off.

Fetched PDFs and Office files are scanned in `website` and `url-list` too (6b §11); `scan` is listed for `files` only
because every upload is a file. HTML extraction settings come from the host's site profile, not from the template;
uploaded HTML uses the built-in presets only (no host). Supported media types per route: 6c §3.2; everything else ends
`unsupported`.

### 13.2 Who edits what

- `rag-department-admin`: picks a template for a source and sets its parameters (stored as a department pipeline
  differing only in those params).
- `rag-pipeline-editor` (the "edit template parameters" right): edits any parameter of a department pipeline in the
  form generated from the template's schema and labels; publishes through §12.
- `rag-platform-admin`: templates, `deprecated` flags, embedding-profile exceptions on collections, site-profile
  proposals and rollbacks (6b), parse rules (6c §3.5).

### 13.3 Template versions

- A department pipeline records `Pipelines.CopiedFromId` and `Definition.templateVersion`.
- A new template version (new stage versions, new parameters or defaults) does not change existing pipelines. They
  see "template updated" with a parameter diff and adopt it by creating a new draft (params carried over, new ones at
  their defaults) and publishing it through §12. A platform admin can bulk-create and publish upgraded drafts through
  the normal flow.
- A department pipeline is department-scoped (`Pipelines.DepartmentId`); a source may use only its department's
  pipelines or templates.

## 14. Index writer contract

```csharp
public interface IIndexWriter
{
    // Writes the chunks of a new set with DeletedAt set (invisible), in batches of ≤ 1,000 rows, each batch its own
    // short transaction.
    Task<Guid> WriteInvisibleAsync(ChunkSetWrite write, CancellationToken ct);
    // One short transaction: fence, swap (new set live or held, old set superseded, DeletedAt flipped on both sets'
    // chunks), live pointer, run item, job completion.
    Task<IndexWriteResult> SwapAsync(Guid chunkSetId, ExecutionFence fence, CancellationToken ct);
    // One-row updates without new chunks: noise-only content change or a version change that affects no stage.
    Task<IndexWriteResult> ReKeyAsync(Guid chunkSetId, ChunkSetKey newKey, ExecutionFence fence, CancellationToken ct);
    // Soft-deletes documents (6b decides which); their live sets' chunks get DeletedAt. A document seen again within the
    // 14-day grace is revived (6b §13.4).
    Task<int> SoftDeleteDocumentsAsync(IReadOnlyList<Guid> documentIds, Guid runId, CancellationToken ct);
}

public sealed record ChunkSetKey(Guid DocumentId, Guid PipelineVersionId, Guid? SiteProfileVersionId,
    Guid EmbeddingProfileId, byte[] ContentHash);
public sealed record ChunkSetWrite(ChunkSetKey Key, Guid CollectionId, Classification Level, byte[] InputHash,
    PiiState Pii, IAsyncEnumerable<ChunkRow> Chunks, int ChunkCount);
```

Rules (schema: data-model §2.2). A chunk is searchable iff `Chunks."DeletedAt" IS NULL`; set states are `writing`,
`live`, `held` and `superseded`; the live pointer is `Documents."LiveChunkSetId"`.

1. **Lookup**: by the unique key (`DocumentId`, `PipelineVersionId`, `SiteProfileVersionId`, `EmbeddingProfileId`,
   `ContentHash`); a `superseded` set with the wanted key (rollback within 14 days) is swapped back directly (step 3).
2. **Write invisible**: insert `ChunkSets` with `State = writing`, then the chunks with `DeletedAt` set, by binary
   `COPY` in batches of ≤ 1,000 rows, each batch a short transaction of its own (well under the 60 s
   `statement_timeout`). Each chunk carries the scalar `CollectionId` of its source's collection and the `SearchText`
   computed by the worker (6c §6.5). Keyword and vector candidates never include chunks of a `writing` set.
3. **Swap** (one short transaction per document): fence (§7.5); confirm at the swap that the source's published
   version (config snapshot within the grace window, §15) is still the write's, else the set stays `writing` for the
   orphan sweep and `EnsureCurrent` runs, so a slow write of an old version never overtakes a newer one;
   `Documents.LiveChunkSetId` → new set; the new set → `live` with `DeletedAt` cleared on its chunks, or **`held`**
   (chunks stay deleted) when it is PII-flagged or NER-pending, not covered by the document's clearance, and its
   collection is `public` (6c §9.5); the previous set → `superseded` (`SupersededAt`, kept 14 days for rollback) with
   `DeletedAt` set on its chunks (tens to hundreds of rows); run item; job completion; commit. Rollback is the same
   swap back.
4. **Re-key** (noise-only change, 6c §12; or a publish with no affected stage, §12.2): in the fenced segment commit,
   the live set's key columns (`ContentHash`, `PipelineVersionId`, `SiteProfileVersionId`) are updated in one row when
   the set's `InputHash` equals the new chunk-stage input hash; no new set, run item `unchanged`, reason
   `change.noise` or `version.unaffected`. If a `superseded` set already holds the new key, it is swapped back instead.
5. **Orphan sweep** (maintenance, nightly): sets in `writing` older than 24 h, or whose execution is superseded, are
   deleted with their chunks in batches; `superseded` sets past 14 days likewise.

Each retrieval statement sees one snapshot, so the old or the new set of a document, never both or neither; the
keyword and vector statements of one search may straddle a swap, and hydration re-checks `LiveChunkSetId` as a safety
net (knowledge design §6.3). Limits: 20,000 chunks per set (above: outcome `too-large`, §16). Changing a collection's
level or clearing a document (6c §9.5) runs the same per-document swap of states and `DeletedAt`.

## 15. Config and mid-run changes

- The worker reads its config tables (data-model §1.2) through the config-version library (data-model §5); for the
  security-relevant ones (`Sources`, `Collections`, `Teams`, `Departments`, `DataProtectionPolicies`) a job starts and
  a swap proceeds only on the last confirmed snapshot, within the grace window `Config:MaxUnconfirmedSeconds`
  (default 90 s, simplification pass §9); beyond it the job is requeued without an attempt and event 5004 is logged
  (once a minute per instance with `Count`). Notifications apply changes at once when they arrive.
- The swap check matters for `public` collections: a collection that became `public` while a set without a name check
  was being embedded must not make it live there.

| Change while work is in flight | Effect |
|---|---|
| New pipeline version published | running executions finish on the version they started with (pinned in their payloads); the publish's `reprocess` run brings documents up to date via `EnsureCurrent` |
| Rollback | same as publish |
| New site-profile version for a host | `reprocess` of the host's HTML documents in each source crawling it (6b) |
| Source paused or deleted | queued jobs of its runs end `cancelled` at start (`source.paused`); running jobs finish their current stage and do not swap; run → `cancelled` |
| Source level changed (identity change) | full re-sync into the new level (6b); jobs at the old level fail the swap check (`source.reclassified`) and end `cancelled` |
| Collection made `public` | its live sets are re-run from `personal-data` (the name check may now be required, 6c §9.6); a flagged set not covered by its document's clearance becomes `held` at once (per document: state and `DeletedAt` on its chunks, batched per collection) |
| Collection made non-`public` | its `held` sets become `live` (per document: state, `DeletedAt` cleared on its chunks) |
| Source settings: `cosmetic` / `behaviour` / `scope` / `identity` | nothing / next sync / sync with the smallest plan / full re-sync (6b) |
| Template updated | nothing until a pipeline adopts the new version (§13.3) |
| Stage version removed from a release | publish refuses it; running published versions keep working while a supporting instance lives (§5.2) |

## 16. Failure modes and recovery

| Failure | Detection | Recovery |
|---|---|---|
| Worker process crash or OOM | missing heartbeat 30 s | reaper requeues without an attempt; cache and range blobs make the retry cheap; 3 reclaims stop a poison document (`dead`, `worker.poison_suspect`) |
| Lease lost (GC pause, DB stall) | heartbeat returns no row | token cancelled; the fence rejects the swap; the new owner redoes the work |
| Postgres unavailable | Npgsql errors | dispatcher backs off (1 s → 30 s); jobs in flight fail their commit and are re-leased later; nothing partial is visible (sets become visible only at the swap) |
| Crash between chunk batches and swap | set left `writing` | invisible; the retried execution writes a new set; the orphan sweep removes the old one |
| Parser down or warming | `not_ready`, connection refused | requeue without attempt; `rag.parser.slots_ready` and queue age alert after 10 minutes |
| Gateway or embedding provider down | 5xx, timeouts | transient back-off up to 1 h; runs end `degraded` if attempts run out; query embeddings for chat are protected by the adaptive limit (§18) |
| Budget used up | 402 | hourly retries; `failed` with `gateway.budget_exhausted`; the run page says which team's budget |
| Config monitor broken | unconfirmed beyond the grace window | all security-relevant work pauses, 5004 |
| A job or a whole run silently lost | nightly drift sweep (§2.3), finalizer | re-enqueued; `rag.reconcile.drift` metric (should be 0) |
| Huge document | limits: 2,000 pages (parser), parsed output 50 MB, 20,000 chunks per set | outcome `too-large` with the limit's name |
| A stage bug producing bad output | golden tests; preview; publish report and nightly evaluation (6c §14) | fix with a stage version bump in a new template version; the plan re-runs from that stage |

## 17. Observability

Meter `Ume.Rag` and activity source `Ume.Rag` (repo-and-deployment §9). New metrics (low-cardinality dimensions only;
never source names, URLs or titles):

| Metric | Type | Dimensions |
|---|---|---|
| `rag.jobs.queued`, `rag.jobs.age` (existing); `rag.jobs.duration` (s) | gauge; histogram | class; class, outcome |
| `rag.jobs.requeued` | counter | class, reason (`busy`, `not_ready`, `rate_limited`, `config_unconfirmed`, `unsupported_node`, `slice`, `shutdown`, `build`) |
| `rag.jobs.lease_lost`, `.dead`, `.reclaimed`; `rag.engine.superseded` | counter | class |
| `rag.engine.node.duration` (s); `rag.engine.cache` | histogram; counter | stage, outcome; stage, `hit`/`miss` |
| `rag.runs.items`; `rag.runs.finished` | counter | outcome, source type; state, kind |
| `rag.index.write.duration`, `rag.index.chunks_written`, `rag.index.swaps` | histogram, counter, counter | –, –, `new`/`swapped-back`/`rekeyed`/`held` |
| `rag.index.orphans_swept` | counter | – |
| `rag.embed.tokens`, `rag.embed.concurrency_limit`, `rag.ingestion.cost` (USD) | counter, gauge, counter | –, –, class |
| `rag.scheduler.lag` (s), `rag.reconcile.drift`, `rag.preview.duration` (s) | histogram, counter, histogram | –, –, kind |

Connector metrics (`rag.connector.*`, `rag.crawl.*`, `rag.egress.blocked`, `rag.clamav.*`, `rag.deletion.*`, site
profiles) are 6b §16; processing metrics (`rag.parse.*`, `rag.pii.*`, `rag.embed.cache`, `rag.chunkset.rekeyed`,
`rag.evaluation.*`) are 6c §15.

- **Traces**: one span per job (`rag.job {class}`) with child spans per stage (`rag.node {stage}`) and the HTTP client
  spans to parser and gateway, which continue the trace there. A continuation job stores the `traceparent` of its
  predecessor in its payload and starts with a **link** to it, so fetch → parse → embed of one document is one
  navigable chain. Attributes: job class, stage, document id, level, cache hit, outcome code; never URLs of
  non-`public` sources, titles or text (repo-and-deployment §9 "no content").
- **Log scopes** (repo-and-deployment §9) plus `ExecutionId` and `Stage`: `ConfigVersion`, `RunId`, `JobId`,
  `JobClass`, `DocumentId`, `SourceId`, `Classification`.
- **Security events**: 5003, 5004 (worker), 5101–5103 (6b/6c), 5104 (parser limits), and **5105
  `rag.pipeline.refused`** (Warning): the worker refused to run a published version that fails validation (unknown
  template or stage version, schema failure). Attributes `PipelineId`, `PipelineVersionId`, `Reason`.

## 18. Performance and capacity

**R1 target**: 10,000 typical web pages per hour on the ingestion zone without moving chat p95 TTFT by more than
10 %. That is 2.8 pages/s. Planning estimates per page (*to measure* in the R1 test):

| Resource | Per page | At 2.8 pages/s | Budget | Notes |
|---|---|---|---|---|
| Worker CPU (fetch, AngleSharp + SmartReader, clean, regex PII, tokenizer, decompounder, JSON) | ≈ 80 ms | ≈ 0.25 cores | 1.5 cores | `fetch` concurrency 8 covers ~200 ms fetch latency |
| Politeness | – | 2.8 req/s on umea.se | 10 req/s municipal (answered 4) | external hosts at 1 req/s cap a single external site at 3,600 pages/h; incremental runs by `lastmod` fetch only changed pages (6b) |
| Parser NER (names) | only list-like pages of `public` collections (6c §9.6) | a small fraction of pages | parser NER process | no longer a bottleneck by construction; measured in R1 |
| Embedding (≈ 8 chunks × 450 tokens) | ≈ 3,600 tokens | ≈ 10,000 tokens/s | gateway's on-prem BGE-M3 | **the bottleneck to verify** (§23); measured on the real gateway hardware in P0 (checklist #22, weakness review §5; a shortfall goes to register M2) |
| Postgres: chunk inserts with HNSW, run items, jobs | ≈ 8 rows + 2 jobs | ≈ 22 vector inserts/s, ≈ 20 statements/s | 3 cores, pool 10 | HNSW insert ~1–3 ms each; batches ≤ 1,000 rows keep transactions short |
| WAL | ≈ 50 KB | ≈ 140 KB/s | – | – |

Protection of chat:

- Zones, `cpu_shares`, the ingestion slice and the worker's connection cap (repo-and-deployment §6.2, data-model
  §1.1); jobs hold connections only for short statements (§7.6).
- **Adaptive embed concurrency** (AIMD; **P2**, weakness review §1; in P1 `embed` concurrency is the fixed
  `Worker:Concurrency:embed` from configuration, set from the P0 throughput measurement): the worker tracks p95 latency of its embedding calls per minute; above 2× the
  idle baseline it halves `embed` concurrency (min 1), below it adds one per minute up to the configured value.
  Chat's query embeddings use the same on-prem model, so this keeps ingestion from queuing ahead of them;
  `rag.embed.concurrency_limit` shows it working.
- Batches of ≤ 64 inputs per embedding request; the embedding cache skips unchanged text (re-chunking embeds only new
  text, plan §5).
- An unchanged re-crawl costs at most one GET per page (none for pages whose `lastmod` did not change, 6b) and a
  batched `unchanged` run item: no parse, no embed (plan §9; Eneo re-embeds everything, `../../platforms/eneo.md:234`).
- Two departments with the same site: one fetch (shared frontier, 6b; P2, two fetches in P1), one parse (parse cache by content hash); chunking and
  embedding run per department, embedding mostly from the cache.

`perf.yml` (repo-and-deployment §8.3, P1) measures pages per hour, chat p95 TTFT idle vs. during crawl + OCR batch,
Postgres CPU and pool waits, and the embed limit's behaviour (P2; in P1 the fixed limit's effect on chat p95).

**Beyond the pilot** (≈ 10×: 100k pages/h checked, 10–20k changed, 5–10M chunks on 3–5 hosts): the per-stage capacity
table, binding resources (politeness, the gateway's BGE-M3, HNSW memory), the fleet-wide embedding cap (P2) and the
scale test are in [`../performance-pass.md`](../performance-pass.md) §2, §3.9, §3.10 and §7.

## 19. Tests

| Level | Project | What |
|---|---|---|
| Unit | `Ume.Rag.Pipelines.Tests` | schema validation; every §4 rule with a failing and a passing definition; stage keys: a changed parameter changes exactly its stage's key and the later ones; effect tags and routes give the documented plans; templates validate |
| Unit | `Ume.Rag.Worker.Tests` | stage order; routing inside `parse`; result mapping table §7.3 (each parser and gateway code); cache keys; `OutputHash` chain; segment cutting and continuation payloads; cost estimates; scheduler with `FakeTimeProvider` (DST change days, jitter stability, catch-up collapse); AIMD limiter; preview context refuses DB and non-preview blob paths |
| Golden | `Ume.Rag.Worker.Tests` | per stage and version: fixture input → expected output (`tests/fixtures/`); a changed output without a version bump fails |
| Integration (Testcontainers, stock pgvector image) | `Ume.Rag.IntegrationTests` | dequeue under `SKIP LOCKED` with 8 parallel consumers (no double lease); dedup under concurrency with the partial unique index; heartbeat and lease expiry; reaper incl. early reclaim of a dead instance without an attempt, dead-letter after 3 reclaims; requeue without attempt; fence: superseded and lease-lost swaps roll back; index writer: invisible batches not returned by K or V, swap, swap-back within 14 days, re-key, `held` for a flagged set in a `public` collection, orphan sweep; snapshot test (a concurrent reader sees old or new set only); finalizer counters; manifest immutability and support check; preview leaves `content` unchanged (row counts and `xmin` of every table except `Jobs`, `CrawlHosts`) |
| Crash recovery | `Ume.Rag.IntegrationTests` (worker in-process, killed by cancellation without `StopAsync`, and as a container with `docker kill`) | kill during fetch, during a parse range, between chunk batches, between embed and swap; restart; assert every document ends current, no duplicate live sets, no attempts consumed by the kills, run counters exact: **"worker restart loses no work"** |
| Contract | `Ume.Rag.ContractTests` | parser client vs parser image error fixtures (parser §15.2) mapped per §7.3; FakeGateway residency and 402/429 answers; the gateway client refuses a call without a ceiling |
| Performance | `perf.yml`, BenchmarkDotNet | R1 test (§18); benchmarks for key computation, validation and the dequeue query plan |

## 20. Milestones

| Phase | Deliverable |
|---|---|
| P0 | `Jobs` table (unpartitioned, partial unique `DedupKey`); dispatcher skeleton with leases, heartbeat, reaper; `WorkerInstances`; one `command` kind end to end |
| P1 | `Ume.Rag.Pipelines` (format, validator, keys, plan); stage manifest and support check; engine with segments, fence, cache; all P1 stages (internals 6b/6c); slicing, parser capacity; scheduler with defaults and jitter; runs, outcomes, finalizer; preview; publish (every document re-processed, weakness review §1), swap per document, publish report, rollback; templates `website`, `url-list`, `files`; index writer with invisible batches, swap, re-key, orphan sweep; drift sweep; metrics, 5105; the tests of §19; R1 test passed |
| P2 | from weakness review §1: re-processing plan from effect tags (§12.2), adaptive embed concurrency (§18); per-department fairness (loose index scan), batched dequeue, coalesced `NOTIFY`, lease leaders, fleet-wide embedding cap (first multi-host deployment); `attachment` class (chat design §15.2); P2 stages; parent-child chunks only if evaluation shows section expansion insufficient (6c §5.3); office-hours concurrency profiles if the R1 data asks for them; per-department caps in `Settings` |
| P3 | more connectors; per-class worker deployments next to a separate parser host (`Worker:Classes`) |

## 21. Changes to other documents

Simplification pass (2026-10-30); owners of these documents apply them.

| Document | Change |
|---|---|
| `platform/data-model.md` §2.2 | `Documents`: one row per (source, identity) with `SourceId`, `CollectionId`, live pointer `LiveChunkSetId`, `SiteProfileVersionId`, `CurrentExecutionId`; the `DocumentSources` columns (display name, hold, failures, first/last seen, `LastRunId`, deletion) fold into it. `ChunkSets`: unique key (`DocumentId`, `PipelineVersionId`, `SiteProfileVersionId`, `EmbeddingProfileId`, `ContentHash`), scalar `CollectionId`, `State` (`writing`/`live`/`held`/`superseded`), `SupersededAt` (14 days), `InputHash`, stage keys of its inputs, PII state (6c §19); `CollectionStageHash`, `CollectionIds` and revival across sources removed. `Chunks`: scalar `CollectionId` (B-tree), `SearchText`; a chunk is searchable iff `DeletedAt IS NULL` (written deleted, flipped at the swap, §14). `PipelineVersions`: `Definition` per §3.1, `State` without `gating`, no stage hashes, no `GateRunId`. `NodeManifest` holds stage descriptors; `PipelineCanvasLayouts` removed |
| `platform/data-model.md` §4 | `Jobs` unpartitioned; partial **unique** index on `DedupKey WHERE State IN ('queued','running')` replaces the advisory-lock dedup; `Reclaims smallint` (reclaim not an attempt, 3 → `dead`); `RunIds` and its GIN index removed, partial index on `RunId` for open jobs; class `ner` removed; terminal jobs deleted nightly after 7 days |
| `platform/data-model.md` §9 | parse-output blobs under a per-level prefix keyed by the parse cache key (shared by every document with the same bytes and parser settings), not under `documents/{id}/` |
| `platform/data-model.md` §2.1 | `Settings`: `WorkerConcurrency` (P2 shares), `DegradedFailureRatio` (0.05), `ScheduleJitterMinutes` (90), `PreviewMaxUsd` (0.50) |
| `platform/repo-and-deployment.md` §1, §3 | `src/Ume.Rag.Pipelines` holds definition, validator, stage keys and the plan (no Merkle hashes, no canvas); `contracts/pipeline/v2/definition.schema.json`; templates under `src/Ume.Rag.MigrationService/Templates/` |
| `platform/repo-and-deployment.md` §9 | metrics of §17 (`rag.index.swaps`, `rag.index.orphans_swept`); log scope `Stage` instead of `NodeId`; no `Tier` scope |
| `identity/design.md` §10 | 5105 reason codes per §17; it is no longer a data-protection bypass signal (personal-data detection cannot be removed), so the SIEM alert on any 5105 can go |
| `admin/plan.md` §4, `admin/design.md` §6 | Pipelines page: template parameter form (no canvas, no JSON node editor, no `gating` state); publish = plan → confirm → re-process → report → keep or roll back; run views per §10.5 |

## 22. Answered questions (2026-10-29)

1. **Shared documents across departments**: answered 2026-10-29 with follow runs; revised 2026-10-30 (simplification
   pass §2): each department's source has its own documents and chunk sets; sharing is by the frontier request, the
   raw blob and the parse cache only.

Decided here without asking (reasons in the sections): degraded threshold 5 % (§10.3); crawl default Saturday 01:00
(§9.6); preview cost cap 0.50 USD (§11); template updates notify pipelines instead of changing them (§13.3); no
office-hours throttling in P1 until the R1 data asks for it (§20); preview may fetch live URLs for new sources through
the normal egress guard (§11).

## 23. To verify

- **Embedding throughput** of the gateway's on-prem BGE-M3 provider (tokens/s, batch size, effect on chat query
  embeddings): decides whether 10,000 pages/hour is reachable (§18).
- A JSON Schema 2020-12 validator for .NET used by admin and worker (candidate `JsonSchema.Net`; licence, maintenance,
  `if`/`then` support, performance); and RFC 8785 canonicalisation (same package family or ~100 lines of our own).
- `Cronos`: version, 5-field syntax, `TimeZoneInfo` DST behaviour (repo-and-deployment §2.2 marks it *to verify*).
- Npgsql binary `COPY` of `vector` columns via `Pgvector` in ≤ 1,000-row batches; HNSW insert cost per row on the pilot
  host.
- `INSERT … ON CONFLICT DO NOTHING` against the partial unique `DedupKey` index (index inference with the `WHERE`
  predicate) under 500-row batch enqueues.
- Swap duration and WAL for the largest sets (20,000 chunks: `DeletedAt` flipped on both sets' chunks in one
  transaction) and the effect of the flips on the partial GIN and HNSW indexes (data-model §3.2, knowledge design §6).
- Planning numbers of §18 (CPU per page, statements per page), measured in the R1 test.

## 24. Deferred or removed (simplification pass)

See [`../simplification-pass.md`](../simplification-pass.md).

| Mechanism | Where it went |
|---|---|
| DAG executor, typed ports, edges, *Switch*, *Quality check* routing, exclusive merges, user-added nodes, locked nodes, `form` lists, the Vue Flow canvas and `PipelineCanvasLayouts` | dropped; fixed linear pipelines = template + parameters (§1 #1, §3); canvas reconsidered only if editors demonstrably need custom flows |
| Two-stage model, Merkle `DocumentStageHash`/`PipelineHash` | dropped; per-stage cache keys (§3.3) |
| Shared chunk sets, `CollectionIds` arrays, membership recompute, follow runs, `RunIds`, cross-department set revival, `recompute-membership` command | dropped; documents and sets per source, scalar `CollectionId` (§2.1, §14) |
| Job class `ner` and its NER-lane concurrency | dropped; gated `/v1/ner` call inside the job that runs `personal-data` (§7.4, 6c §9.6) |
| Publish gate, `gating` state, shadow sets and sub-corpus builds, gate override, `GateRunId` | dropped; advisory publish report (§12, 6c §14) |
| One long index-writer transaction per document; dedicated connection for large sets; set-level visibility with a batched follow-up marking superseded chunks deleted | replaced by invisible batches + one short swap per document that flips `DeletedAt` (§14) |
| `Jobs` day partitions and advisory-lock dedup | dropped; unpartitioned table, partial unique index (§9.4, data-model §4) |
| Reaper counting a reclaim as an attempt | replaced by `Reclaims`, dead-letter after 3 (§9.4) |
| Per-department fairness (loose index scan), batched dequeue, coalesced `NOTIFY`, `content."LeaderLeases"`, fleet-wide embedding cap | P2, first multi-host deployment (§9.3, §9.4) |
| *Headless render* node and the render fallback branch; `origin`/`rendered` variants | renderer P2 if a P1-start check shows JavaScript-only pilot pages (6b) |
| *PDF fast path* (PdfPig) node | dropped; every PDF goes to the parser, whose text-layer fast path uses pypdfium2 (parser design; 6c §3) |
| Email child documents, legacy Office, RTF in the P1 templates | P2 (simplification pass §1) |
| `approve-profile`/`rollback-profile` commands (parse-profile auto-apply) | dropped; parse rules without auto-apply (6c §3.5); site-profile rollback is `rollback-site-profile` (6b) |
| `GatewayKeyLinks` interim keys, `DataTiers` in the fail-closed config set, tier-matrix test, `Tier` log scope | dropped (simplification pass §5, §7) |

## 25. Deferred or removed (weakness review)

Per [weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer):

| Item | Where it went |
|---|---|
| Re-processing plan by first affected stage from effect tags (§12.2) | P2; P1 publish re-processes every live document of the affected sources, unchanged stages free from the per-stage caches, still with report and keep or roll back |
| Adaptive embed concurrency (AIMD, §18) | P2; P1 uses the fixed `Worker:Concurrency:embed` from configuration |
| Shared crawl frontier and site-profile learning (6b) | P2 (6b §24) |

Per [weakness review §5](../weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control):

| Item | Where it went |
|---|---|
| `x-ume-on-behalf-of` on worker calls and the `gateway.delegation_not_applied` echo check in P1 (§7.7) | P2 with gateway release 2; P1 uses the pilot team's `OnPrem` and `Eu` keys |
