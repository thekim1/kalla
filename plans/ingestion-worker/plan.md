# Ingestion worker: plan

Background service that keeps the document store and collection indexes in step with their sources: it runs
connectors, executes each source's pipeline (a typed DAG), decides how to parse each document, embeds through the
gateway, writes chunks and runs evaluations. It is the largest service and the one where the other platforms fail
most (`../../platforms/00-comparison.md` F4–F10, F13). Review items applied: R1, R2, R3, R6, R7.

## 1. Scope

**Does:** scheduling, job queue, connectors (URL list, website crawl, file upload; SharePoint in P2), pipeline engine
and node catalogue, auto-parse and parse-profile learning, change detection, deletion guard, embedding, run records,
preview execution, evaluation runs and gates.

**Does not:** serve queries (knowledge API), own configuration (admin API writes sources and pipeline versions; the
worker reads them), parse PDFs/Office itself beyond the fast path (parser sidecar).

Runs in the **ingestion resource zone** (platform plan §2): hard CPU/memory limits, lower CPU priority than the data
plane, a small capped Postgres connection pool and its own `statement_timeout`.

## 2. Components

| Component | Responsibility |
|---|---|
| Scheduler | Turns source schedules, "sync now", uploads and (P2) SharePoint change notifications into jobs |
| Job queue | Postgres, `FOR UPDATE SKIP LOCKED`, woken by `LISTEN/NOTIFY`; retries with back-off; job classes with their own concurrency limits, fair per department; table partitioned by day |
| Connectors | Implement the capability interfaces below |
| Crawl frontier | One frontier and one politeness budget per host, shared by every source that crawls it (R2) |
| Document store writer | One document per canonical identity; fetch and parse results shared across collections (R2) |
| Pipeline engine | Executes a published pipeline version per document; caches node outputs |
| Node catalogue | `IPipelineNode` implementations with typed ports and JSON-schema parameters; registered as a manifest (R7) |
| Auto-parse | Chooses a parse strategy per document/page; records observations; learns profiles |
| Index writer | Upserts a collection's chunks and embeddings per document in batches; soft deletes |
| Evaluator | Generates and runs evaluation sets; computes gates (R6) |
| Run recorder | One run per sync, one run item per document with outcome, reason, strategy chosen, cost |

**Job classes**, in priority order: `preview` (interactive, always first), `command` (admin decisions applied by the
worker: clear a PII flag, confirm a deletion, approve or roll back a profile, edit human evaluation questions), `sync`,
`fetch`, `parse`, `embed`, `eval`. Table design: `../platform/data-model.md` §4.
`parse` concurrency equals the parser sidecar's capacity, so the sidecar is never overloaded.

## 3. Connector contract

Small capability interfaces (after Onyx), each optional:

- `IFetchConnector`: resumable fetch that yields `Document` or `Failure` items and returns a typed checkpoint.
- `IIdListingConnector`: cheap listing of ids for deletion detection.
- `IRetryFailedConnector`: re-fetch only last run's failures.
- `IHierarchyConnector`: folders/sites for the admin UI.
- `IPreflightCheck`: validates an unsaved source configuration and explains fixes.

Every fetched item gets a **canonical identity** (normalised URL without tracking parameters, SharePoint drive item
id, or file hash). If the document store already has it with the same content hash **at the same classification
level**, the fetch result is shared and nothing is re-parsed (R2); across levels documents are never shared (platform
plan §3). Every LLM call in a pipeline (enrichment, judge, evaluation generation) sends the residency ceiling of the
source's level (platform plan §6). Source settings are tagged by effect (identity, scope, behaviour, cosmetic) so an edit
produces the smallest re-sync.

**P1 connectors**

- *URL list*: exactly the listed URLs; conditional GET (ETag / Last-Modified).
- *Website crawl*: start URL or sitemap, include/exclude patterns, max depth/pages, robots.txt, identifying user
  agent, SSRF protection, crawl report with failure reasons. Sources crawling the same host share the frontier and
  its rate limit. Optional headless rendering via the `rag-renderer` container (P1, decided).
- *File upload*: files from the admin API through blob storage; size/type limits.

**P2: SharePoint** via Microsoft Graph (`Sites.Selected`, see identity plan): drive delta with `deleted` facets and
moves handled, 410 → full resync, change subscriptions with a renewal schedule, pre-flight check for grants. Onyx's
and Eneo's SharePoint issues (#13583, #16590; #427, #521, #522, #429, #1081) become acceptance tests.

## 4. Pipeline engine

- **Definition**: versioned JSON in `config`: nodes (`id`, `type`, `typeVersion`, `params`) and edges
  (`from: node.port`, `to: node.port`). Canvas layout is stored separately and never read by the worker.
- **Two stages**: the *document stage* (fetch → detect → parse → clean) writes to the shared document store; the
  *collection stage* (split → chunk → enrich → embed → write) writes to one collection's index. Two collections whose
  pipelines are identical in the document stage share its output (R2).
- **Port types**: `RawContent` → `ParsedDocument` (Markdown + structure + pages) → `Sections` → `Chunks` (flat or
  parent-child) → `EmbeddedChunks`. Validated server-side on save and publish.
- **Node catalogue manifest (R7)**: at startup the worker registers its node types, versions and parameter schemas in
  `content."NodeManifest"` (with live support per instance in `content."WorkerNodeSupport"`). The admin validates pipelines and generates forms from the registered manifest; pipelines
  pin `typeVersion`; publishing is refused if no running worker supports a node version (safe rolling updates).
- **Node interface**: `IPipelineNode { Type; Version; Inputs; Outputs; ParamsSchema; EstimateCost(); ExecuteAsync(ctx, inputs) }`
  returning `Success | TransientError | FatalError`.
- **Execution**: per document, topological order; branches from *Switch*/*Auto-parse*; a *Quality check* can route
  to a fallback branch. A newer run for the same document cancels an older one (execution id).
- **Output cache**: node outputs stored by (content hash, node id, node version, params hash). The parse output is
  kept as long as the document exists; other outputs 30 days.
- **Preview**: same engine, `preview` job class (highest priority), never writes to `content`; returns each node's
  output and estimated LLM cost.
- **Publish**: computes a re-processing plan (documents × first changed node), runs the evaluation gate (§7) on a
  sample, and shows both before it runs; each document records the pipeline version that processed it.

**P1 node catalogue**: HTTP fetch, Sitemap/crawl, Upload, Detect, Switch, Auto-parse, HTML main content
(AngleSharp + SmartReader), PDF fast path (PdfPig), Docling parse (sidecar), Clean, Split by headings, Chunk
(flat / parent-child), Metadata from URL/path, Quality check, **Detect personal data**, Embed, Write to collection,
Headless render.

**Personal data in sources (decision: detect and flag)**: the *Detect personal data* node is in every template. It
finds personnummer, phone numbers, email addresses and names (Swedish NER) and flags the document with counts per
type. Flagged documents are listed in the admin, and they are **blocked from `public` collections** until an editor
clears them. Masking stays an optional P2 node a source can enable.
**P2**: SharePoint, OCR-forced, Table extract, PII mask, LLM summary/keywords, Date/type extraction.

## 5. Embedding (R3)

- One **platform embedding profile**: a multilingual model served on-prem through the gateway, used for every
  collection and classification. Content is never sent off-premises to be indexed.
- Calls go through the gateway with the platform service key on behalf of the collection's owning team (or the team
  key in the interim), with the on-prem residency ceiling header.
- Embedding cache by (profile, text hash): re-chunking only embeds new text; batches sized for the gateway.
- Other profiles are an explicit exception set by platform admins. A profile change re-embeds every affected chunk in
  the background into the new profile, runs the evaluation gate, then swaps atomically.

## 6. Auto-parse and learning

1. **Profiles** (learned or made by admins), matched on domain/path/document type → strategy.
2. **Per-page heuristics**: clean PDF text layer → PdfPig; empty/garbled → Docling OCR; nothing found → re-render;
   table-dense → Docling tables. Garbled-text detectors (after RAGFlow) also feed the Quality check.
3. **Try and score** (P2): two or three strategies on uncertain documents, scored by the Quality check (text yield,
   empty chunks, OCR confidence, duplicates) and optionally a small LLM judge via the gateway (on-prem).
4. **Learning** (P2): observations per pattern; above the confidence threshold a profile is **auto-applied** after a
   trial run alongside the current strategy, with a minimum sample count, department scope, notification, audit entry
   and kill switch. **Rollback is driven by the evaluation gate** (§7): if recall on the affected collections drops
   beyond the threshold, the profile is rolled back automatically. Below the confidence threshold it is proposed for
   approval. Learns per-site cleanup rules the same way. Profiles live on the document store, so all departments
   benefit from one learned profile for a shared site.

## 7. Evaluation (R6)

- **Evaluation set per collection**, built in layers:
  1. *Generated*: sample chunks, have an on-prem model write a question the chunk answers plus the expected answer,
     keyed to the chunk's document; filter out trivial or ambiguous questions with a second pass. Regenerated as
     content changes.
  2. *Skill tests*: test questions shipped with skills (`../tools/plan.md`).
  3. *Human*: questions from editors, plus questions derived from chat feedback. Feedback never enters a set
     directly: an editor turns a flagged chat into a **rewritten test question without personal data**, and only the
     rewritten question is kept, with the collection's classification (decision 2026-10-29). The original chat
     still expires under its own retention. The postponed Swedish benchmark becomes this curated layer.
- **Metrics**: recall@k and MRR for retrieval (expected document found?), plus an optional answer-faithfulness check
  by an on-prem judge model for assistants.
- **Gates**: pipeline publish, parse-profile auto-apply, retrieval-setting changes and embedding-profile changes run the
  affected sets (sampled) and **block or roll back** when recall@k drops more than a threshold (proposal: 3 points).
  Results are stored per version and shown in the admin.
- `eval` job class, lowest priority; LLM costs charged to the owning team through the gateway.

## 8. Safety and efficiency

- Two-stage change detection: timestamp/ETag, then content hash; unchanged documents are not re-parsed or re-embedded.
- **Deletion guard**: a sync that would delete more than 20% of a source, or a source that returns nothing, pauses
  for confirmation. Soft delete with a 14-day grace period. A document leaves the store only when no source references it.
- Failures are data: per-document outcome and reason; a sync with failures above a threshold is marked degraded.
- Every LLM or embedding call carries the owning team's attribution, so gateway budgets stop runaway costs.
- Config is read at the current config version (platform plan §4); a source or pipeline change takes effect within
  seconds.

## 9. Targets (to confirm)

- Ingest 10k typical web pages per hour on the ingestion zone without moving chat p95 by more than 10% (R1 release test).
- A re-crawl of an unchanged site does no parsing and no embedding.
- Two departments adding the same site cause one crawl and one parse.
- Worker restart loses no work (jobs are leased, not lost).

## 10. Milestones

| Phase | Deliverable |
|---|---|
| P1 | Queue + LISTEN/NOTIFY, scheduler, document store with canonical identities, shared crawl frontier, URL list + crawl + upload connectors, two-stage engine with P1 nodes, node manifest, templates per source type, preview, publish plan, heuristic auto-parse, platform embedding profile, change detection, deletion guard, run records, generated evaluation sets + publish gate |
| P2 | SharePoint, try-and-score, learned profiles with auto-apply and evaluation-driven rollback, P2 nodes, retrieval-signal feedback loop, skill test questions in evaluation |
| P3 | More connectors, embedding-profile migration tooling, OpenSearch indexing if adopted, answer-faithfulness judge |

## 11. Open questions

Answered (2026-10-29):

1. **Auto-apply defaults**: confidence ≥ 0.9 over ≥ 20 documents, trial run on 10% of matching documents first;
   automatic rollback if recall@10 drops more than 3 points. Adjustable in settings.
2. **Headless rendering**: needed in P1. A `rag-renderer` container (Playwright with Chromium) in the ingestion zone,
   with the same SSRF egress rules as the crawler, a page timeout and a per-host concurrency of 1; used only by
   sources that enable it, or for pages the crawl flags as JavaScript-only.
3. **Uploads**: 50 MB per file; PDF, DOCX, PPTX, XLSX, ODT/ODS/ODP, HTML, TXT, MD, CSV. Everything is virus-scanned
   (ClamAV) before parsing.

4. **Crawl politeness**: municipal (allow-listed) hosts up to 10 requests/s, external sites 1 request/s with one
   connection; robots.txt always respected; full crawls of large sites scheduled at night. Per-host limits are shared
   by all sources through the crawl frontier.