# Ingestion worker: plan

**Detailed design**: [`design-engine.md`](design-engine.md) (6a: engine, nodes, jobs, runs, preview, publish),
[`design-connectors.md`](design-connectors.md) (6b: connectors, frontier, change detection, deletion guard) and
[`design-processing.md`](design-processing.md) (6c: parsing, chunking, personal data, embedding, evaluation, gates).
Where they differ, the designs win.

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
| Index writer | Writes shared chunk sets (one per document, whole-pipeline hash and profile) and their collection membership in one fenced transaction per document; soft deletes and revivals (design-engine §14) |
| Evaluator | Generates and runs evaluation sets; computes gates (R6) |
| Run recorder | One run per sync, one run item per document with outcome, reason, strategy chosen, cost |

**Job classes**, in priority order: `preview` (interactive, always first; kinds `node-preview`, `publish-plan`,
`preflight`, `hierarchy`), P2 `attachment` (chat attachments, right after `preview`; chat design §15.2), `command`
(admin decisions applied by the worker: clear a PII flag, confirm or reject a deletion, release a hold, approve or roll
back a profile, edit human evaluation questions, reprocess a document, cancel a run, recompute membership), `sync`,
`fetch`, `parse`, `ner` (names for *Detect personal data*), `embed`, `eval`. Table design: `../platform/data-model.md`
§4; kinds and payloads: design-engine §9.1. `parse` concurrency equals the parser's `capacity.convert_slots` and `ner`
follows its `capacity.ner_slots`, so the sidecar is never overloaded.

## 3. Connector contract

Small capability interfaces (after Onyx), each optional:

- `IFetchConnector`: resumable fetch that yields `Document` or `Failure` items and returns a typed checkpoint.
- `IIdListingConnector`: cheap listing of ids for deletion detection.
- `IRetryFailedConnector`: re-fetch only last run's failures.
- `IHierarchyConnector`: folders/sites for the admin UI.
- `IPreflightCheck`: validates an unsaved source configuration and explains fixes.

Every fetched item gets a **canonical identity** (normalised **final URL after redirects** without tracking
parameters, plus the variant `origin`/`rendered`; `rel=canonical` is never trusted; SharePoint drive item id, or file
hash; design-connectors §3, §8.4). If the document store already has it with the same content hash **at the same classification
level**, the fetch result is shared and nothing is re-parsed (R2); across levels documents are never shared (platform
plan §3). Every LLM call in a pipeline (enrichment, judge, evaluation generation) sends the residency ceiling of the
source's level (platform plan §6). Source settings are tagged by effect (identity, scope, behaviour, cosmetic) so an edit
produces the smallest re-sync.

**P1 connectors**

- *URL list*: exactly the listed URLs; conditional GET (ETag / Last-Modified).
- *Website crawl*: start URL or sitemap, include/exclude patterns, max depth/pages, robots.txt, identifying user
  agent, SSRF protection, crawl report with failure reasons. Sources crawling the same host share the frontier and
  its rate limit. Optional headless rendering via the `rag-renderer` container (P1, decided), chosen per page with
  the source setting `render: auto` (JavaScript-only pages), the worker fetching every browser request
  (design-connectors §8).
- *File upload*: files from the admin API through blob storage in upload batches (create → one request per file →
  commit, which enqueues the `sync`; design-connectors §9.1); size/type limits; identity by content hash, file names
  only per source (`DocumentSources.DisplayName`). Pilot types (parser design §3):
  PDF, DOCX/PPTX/XLSX, legacy `.doc`/`.xls`/`.ppt`, `.odt`/`.ods`/`.odp`, `.rtf`, `.jpg`/`.png`/`.tif(f)`,
  `.eml`/`.msg`, `.txt`/`.md`/`.csv`, HTML. Archives and macro-enabled Office files are refused in P1 (postponed Z1).
  Email attachments become **child documents** (depth ≤ 2), each scanned by ClamAV and routed like any file.

**P2: SharePoint** via Microsoft Graph (`Sites.Selected`, see identity plan): drive delta with `deleted` facets and
moves handled, 410 → full resync, change subscriptions with a renewal schedule, pre-flight check for grants. Onyx's
and Eneo's SharePoint issues (#13583, #16590; #427, #521, #522, #429, #1081) become acceptance tests.

## 4. Pipeline engine

- **Definition**: versioned JSON in `config`: nodes (`id`, `type`, `typeVersion`, `params`) and edges
  (`from: node.port`, `to: node.port`). Canvas layout is stored separately and never read by the worker.
- **Two stages**: the *document stage* (fetch → detect → parse → clean → detect personal data) writes to the shared
  document store; the *collection stage* (split → chunk → enrich → embed → write) writes chunk sets. Stages are
  compared by **Merkle hashes** (design-engine §3.3): two pipelines with the same document-stage hash share its output;
  a chunk set is shared only when the **whole-pipeline** hash and the embedding profile match (R2). *Write* has no
  collection parameter; target collections come from the sources' references.
- **Port types**: `RawContent` → `ParsedDocument` (Markdown + structure + pages) → `Sections` → `Chunks` (flat in
  P1; parent-child only if evaluation shows query-time section expansion is insufficient, design-processing §5.3) →
  `EmbeddedChunks`. Validated server-side on save and publish.
- **Node catalogue manifest (R7)**: at startup the worker registers its node types, versions and parameter schemas in
  `content."NodeManifest"` (with live support per instance in `content."WorkerNodeSupport"`). The admin validates pipelines and generates forms from the registered manifest; pipelines
  pin `typeVersion`; publishing is refused if no running worker supports a node version (safe rolling updates).
- **Node interface**: `IPipelineNode { Type; Version; Inputs; Outputs; ParamsSchema; EstimateCost(); ExecuteAsync(ctx, inputs) }`
  returning `Success | TransientError | FatalError`.
- **Execution**: per document, topological order; branches from *Switch*/*Auto-parse*; a *Quality check* can route
  to a fallback branch. Work is **reconciled**: `EnsureCurrent` enqueues only what a reference is missing, and a
  nightly drift sweep catches lost work; new content in a document shared with another department re-processes that
  collection at once in its own follow run (design-engine §2.3). A newer execution for the same document cancels an
  older one; the execution id changes only with new content or a forced reprocess (design-engine §7.5).
- **Output cache**: node outputs keyed by (input hash, node type, node version, params hash; data-model §9) in three
  classes: `durable` (parse outputs, kept as long as the document exists), `intermediate` (30 days) and `none`
  (design-engine §8).
- **Preview**: same engine, `preview` job class (highest priority), never writes to `content` except `Jobs.Result`
  and `CrawlHosts.NextAllowedAt`; returns each node's output and estimated LLM cost.
- **Publish**: the version is frozen in state `gating` while the worker computes a re-processing plan (documents ×
  first changed node) and runs the evaluation gate (§7) on a sub-corpus; the user confirms after seeing both, and
  confirm checks that the gate measured exactly this version (admin design §6.6); each document records the pipeline
  version that processed it.

**P1 node catalogue** (design-engine §6): roots HTTP fetch (URL list), Crawl, Upload; Headless render, Detect (calls
the parser's `/v1/inspect` for PDF/TIFF/Office/email), Switch, Auto-parse, HTML main content (AngleSharp +
SmartReader), PDF fast path (PdfPig), Docling parse (sidecar), Plain text/Markdown and CSV table (worker-side), Clean,
Quality check, **Detect personal data**, Split by headings, Chunk (flat), Metadata from URL/path, Embed, Write.
Email attachments become child documents (depth ≤ 2) that run the pipeline themselves.

**Personal data in sources (decision: detect and flag)**: the *Detect personal data* node is in every template
(locked). It finds personnummer, samordningsnummer, phone numbers and email addresses (patterns in .NET) and names
(Swedish NER through the parser's `/v1/ner`, in the `ner` job class) and records counts per type. A per-type flag rule
(answered 23, `Settings.PiiFlagRules`) decides whether the document is flagged; flagged documents are listed in the
admin and **blocked from `public` collections** until a department admin clears them (re-flagged when a count rises).
If NER is unavailable, names stay `pending` and the document is kept out of `public` collections only (answered 25).
Masking stays an optional P2 node a source can enable (design-processing §9).
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

1. **Profiles** (learned or made by admins), matched on domain/path/document type → strategy. P1 ships built-in
   profiles only (`default`, `regulations`, `protokoll`, versioned with the node) and records `ParseObservations`
   from day one, so P2 learning starts with data (design-processing §3.5).
2. **Per-page heuristics**: clean PDF text layer → PdfPig; empty/garbled → Docling OCR; nothing found → re-render;
   table-dense → Docling tables. Signals come from the parser's cheap `/v1/inspect` (page count, per-page text-layer
   facts, garble score; parser design §6) with starting thresholds in parser design §6.1. Pages with the same
   strategy are grouped into ranges of ≤ 20 pages sized from measured time per page, and the worker stitches heading
   paths, cross-page tables and hyphenation (parser design §8). Parse concurrency is read from the parser's
   `/v1/info`; `busy`, `not_ready` and `duplicate_in_flight` requeue without counting an attempt; other errors map
   per parser design §7.5. Spreadsheets and CSV are tables: chunked as row groups with the header row repeated.
   Text, Markdown and CSV are parsed in the worker, never sent to the parser.
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
- **Metrics**: recall@10 and MRR@10 for retrieval (expected **document** found?), measured through the shared
  `Ume.Rag.Retrieval` engine with the Knowledge API's fusion, dedup, variant collapse and per-document cap
  (design-processing §13.5); answer faithfulness by an on-prem judge for assistants in P3.
- **Gates**: pipeline publish (on a sub-corpus of shadow sets, both arms searched exactly), parse-profile auto-apply
  (P2), gated retrieval-setting changes, embedding-profile changes (P3) and skill publish (P2: retrieval and routing
  layers, no tool execution) **block or roll back** when recall@10 drops more than 3 points **and** ≥ 3 questions net
  are lost; a drop on the 50-question sample escalates to the full set first (design-processing §14.2). Rollbacks are
  never gated. A new collection stays **`building`** (invisible to assistants) until its set reaches the 0.70 floor or
  a platform admin overrides (answered 21, 24). Results are stored per version and shown in the admin.
- `eval` job class, lowest priority; LLM costs charged to the owning team through the gateway.

## 8. Safety and efficiency

- Two-stage change detection: timestamp/ETag, then content hash (HTML noise-normalised); unchanged documents are not
  re-parsed or re-embedded, and a noise-only change re-keys the chunk set instead of rebuilding it
  (design-processing §12).
- **Deletion guard**: deletions are applied in a reconcile phase after all fetches of a run, only after a complete
  traversal or listing. A run whose non-exempt candidates are more than 20 % of a source (and at least
  `DeletionGuardMinCount` 5), more than `DeletionGuardMaxCount` (500), or a source that returns nothing, pauses for
  confirmation; configuration changes and explicit admin actions are exempt (design-connectors §13). Soft delete with a
  14-day grace period. A document leaves the store only when no source references it.
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
| P1 | Queue + LISTEN/NOTIFY, scheduler, document store with canonical identities, shared crawl frontier, URL list + crawl + upload connectors, two-stage engine with P1 nodes, node manifest, templates per source type, preview, publish plan, heuristic auto-parse with built-in profiles, personal-data detection (patterns + NER, flag rules), platform embedding profile, change detection, deletion guard, run records, generated evaluation sets + publish gate, collection go-live floor |
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

Answered (2026-10-29, step 6 questions):

5. **Detail design split** into three sub-steps: 6a engine + node catalogue + jobs/runs/preview/publish
   (`design-engine.md`), 6b connectors + change detection + deletion guard (`design-connectors.md`), 6c auto-parse,
   chunking, embedding, personal-data detection and evaluation (`design-processing.md`).
6. **Names in *Detect personal data***: patterns (personnummer with checksum, phone, email) in the .NET worker; names
   by a Swedish NER model baked into the parser image behind a new parser endpoint (on-prem, no gateway call).
7. **Default re-sync schedule**: URL lists nightly (conditional GET), website crawls weekly at night, uploads on
   change only; adjustable per source.
8. **Default chunking**: split by headings, then ≈ 400 BGE-M3 tokens with ≈ 15 % overlap, heading path prepended;
   tables as row groups with the header repeated; flat chunks (no parent-child) in P1.
9. **Shared documents across departments** (6a): when one department's sync finds new content in a document another
   department also indexes, the other collection is re-processed at once in its own follow run, charged to its team
   (parse shared; only chunking and embedding repeat).

Answered (2026-10-29, step 6b questions):

10. **Crawler user agent**: `UmeaKommunBot/1.0 (+<about page URL on umea.se>)`, product-neutral (N1 still open);
    robots.txt rules match `UmeaKommunBot`.
11. **Page-level `noindex`/`nofollow`** (meta robots, `X-Robots-Tag`): honoured on external hosts; **ignored on
    allow-listed municipal hosts**, where `noindex` usually only means "keep out of public search engines".
    robots.txt is always honoured (answered 4).
12. **Crawl size defaults**: 10 000 pages, depth 10 per source; hitting the limit marks the run degraded with a clear
    reason; admins can raise it per source.
13. **Off-site links**: never followed by default; a crawl stays on its start host(s) and listed extra hosts;
    off-site documents only through an explicit allow pattern.
14. **Own crawler, not a self-hosted Firecrawl** (2026-10-29): Firecrawl (AGPL-3.0; own API, workers, Playwright,
    Redis, RabbitMQ, Postgres queue) would cover fetch, render and Markdown only; the shared frontier, change
    detection, deletion guard, crawl rules, SSRF guard and pipeline integration would still be ours. Revisit only as a
    backend behind the renderer contract if sites with anti-bot protection must be crawled.

Answered (2026-10-29, step 6b review):

15. **External sites**: any department may crawl any public external host (robots.txt, 1 req/s); platform admins
    can block hosts.
16. **SharePoint permissions (P2)**: not mirrored; preflight warns about unique permissions; mirroring is a P3 option.
17. **Malware**: SIEM alert, metadata-only view, no download or release in P1, quarantine purged after 30 days.

Answered (2026-10-29, step 6c questions):

18. **NER model for names**: `KBLab/bert-base-swedish-cased-ner` (MIT, ~110M parameters, SUC 3.0, label `PRS`),
    baked into the parser image; measured on our fixtures in P1 (KBLab marks it experimental).
19. **Gate override**: only `rag-platform-admin`, with a mandatory reason (audited, shown on the pipeline page).
20. **Evaluation set size**: up to 100 filtered generated questions per collection (fewer for small collections); a
    publish gate runs a stratified sample of 50.
21. **First publish of a new collection**: must reach an absolute recall@10 floor (proposal 0.7 on its generated
    set) or be overridden by a platform admin; afterwards the 3-point relative rule applies.

Answered (2026-10-29, step 6c review):

22. **Chunking vs knowledge API**: flat chunks (answered 8) win over the knowledge plan's parent-child; the knowledge
    API expands to the surrounding section at query time.
23. **Personal data that blocks `public` collections**: personnummer, samordningsnummer and external email
    addresses always; names, phones and own-domain emails only together in list-like documents (≥ 5 names and ≥ 5
    contact details). Rules in `Settings.PiiFlagRules`.
24. **Collection go-live**: a new collection is `building` (invisible to assistants) until its evaluation reaches
    the 0.70 floor or a platform admin overrides.
25. **If NER cannot keep up**: index first; documents stay out of `public` collections until names are checked.
26. **Evaluation models**: default the gateway's on-prem general chat model for generation and judging (a setting).

Answered 18 refined in review: the model's `config.json` uses **`PER`** for persons (the card's example shows
`PRS`); mixed labels (`ORG/PRS`, `PRS/WRK`, `LOC/PRS`) are not counted in P1. Processing details:
[`design-processing.md`](design-processing.md).

The upload types of answered 3 are superseded by the pilot list in §3 (parser design §3). Connector details:
[`design-connectors.md`](design-connectors.md).