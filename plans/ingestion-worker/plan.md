# Ingestion worker: plan

**Detailed design**: [`design-engine.md`](design-engine.md) (6a: engine, stages, jobs, runs, preview, publish),
[`design-connectors.md`](design-connectors.md) (6b: connectors, frontier, site profiles, change detection, deletion
guard) and [`design-processing.md`](design-processing.md) (6c: parsing, HTML extraction, chunking, personal data,
embedding, evaluation). Where they differ, the designs win. The plan states the decided form: fixed linear pipelines,
P1 scope as in §10 (reasoning: [`../simplification-pass.md`](../simplification-pass.md), [weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer); deferred items in §12 and §13).

Background service that keeps the document store and collection indexes in step with their sources: it runs
connectors, executes each source's fixed linear pipeline, decides how to parse each document, embeds through the
gateway, writes chunks and runs advisory evaluations. It is the largest service and the one where the other platforms
fail most (`../../platforms/00-comparison.md` F4–F10, F13). Review items applied: R1, R2, R3, R6, R7.

## 1. Scope

**Does:** scheduling, job queue, connectors (URL list, website crawl, file upload; SharePoint in P2), fixed pipelines
per source type, routing per document, parse rules, site profiles for HTML, change detection, deletion guard,
embedding, run records, preview execution, evaluation runs.

**Does not:** serve queries (knowledge API), own configuration (admin API writes sources and pipeline versions; the
worker reads them), parse untrusted binary formats (every PDF, Office file and image goes to the parser sidecar; the
worker parses HTML, text, Markdown and CSV only).

Runs in the **ingestion resource zone** (platform plan §2): hard CPU/memory limits, lower CPU priority than the data
plane, a small capped Postgres connection pool and its own `statement_timeout`.

## 2. Components

| Component | Responsibility |
|---|---|
| Scheduler | Turns source schedules, "sync now", uploads and (P2) SharePoint change notifications into jobs |
| Job queue | Postgres, `FOR UPDATE SKIP LOCKED`, woken by `LISTEN/NOTIFY`; retries with back-off; job classes with their own concurrency limits; unpartitioned, dedup by a partial unique index; per-department fairness in P2 |
| Connectors | Implement the capability interfaces below |
| Crawl frontier | P1: one frontier per source, with one politeness budget per host shared by every source (`CrawlHosts`). P2 (weakness review §1): one frontier shared by every source that crawls a host, with claims and a releaser (R2) |
| Site profiles | Per host and versioned: main content, boilerplate, breadcrumb, date pattern, identity rule (6b). P1: manual profile JSON (umea.se: a hand-checked Sitevision preset); learned from a sample in P2 (weakness review §1) |
| Document store writer | One document per (source, identity); raw content, parse output and embeddings reused across sources through content-addressed caches (R2) |
| Pipeline engine | Executes the fixed pipeline of a published version per document; caches stage outputs by per-stage keys |
| Stage manifest | `IPipelineNode` stages with versions, registered at startup (R7) |
| Auto-parse | Chooses a parse strategy per document/page from `/v1/inspect` and parse rules; records observations |
| Index writer | Writes a new chunk set invisible (chunks carry `DeletedAt`) in batches, then swaps it in one short transaction per document (`Documents.LiveChunkSetId`, `DeletedAt` cleared on the new set's chunks and set on the old set's); keeps the previous set 14 days for rollback (design-engine §14) |
| Evaluator | Generates and runs evaluation sets; advisory reports (R6) |
| Run recorder | One run per sync, one run item per document with outcome, reason, strategy chosen, cost |

**Job classes**, in priority order: `preview` (interactive, always first; kinds `stage-preview`, `publish-plan`,
`preflight`, `site-profile`), P2 `attachment` (chat attachments, right after `preview`; chat design §15.2), `command`
(admin decisions applied by the worker: clear a PII flag, confirm or reject a deletion, release a hold, approve,
reject, edit or roll back a site profile, edit human evaluation questions, reprocess a document, cancel a run), `sync`, `fetch`, `parse`, `embed`,
`eval`. Table design: `../platform/data-model.md` §4; kinds and payloads: design-engine §9.1. `parse` concurrency
equals the parser's `capacity.convert_slots`, so the sidecar is never overloaded.

## 3. Connector contract

Small capability interfaces (after Onyx), each optional:

- `IFetchConnector`: resumable fetch that yields `Document` or `Failure` items and returns a typed checkpoint.
- `IIdListingConnector`: cheap listing of ids for deletion detection.
- `IRetryFailedConnector`: re-fetch only last run's failures.
- `IHierarchyConnector`: folders/sites for the admin UI.
- `IPreflightCheck`: validates an unsaved source configuration and explains fixes.

A document belongs to **one source**: its identity within the source is the normalised **final URL after redirects**
without tracking parameters (a site profile's verified identity rule may fold aliases; `rel=canonical` is trusted only
when verified), the SharePoint drive item id, or the **upload slot** (the content hash is the version, so replacing a
file keeps the document id; design-connectors §3, §9). Two sources of different departments that see the same URL
share the raw blob and the parse output through content-addressed caches (and from P2 the frontier request), but keep their own
documents and chunk sets (simplification pass §2); across classification levels nothing is shared (platform plan §3).
Every LLM call in a pipeline (evaluation generation and judging; P2 enrichment) sends an explicit residency ceiling
(platform plan §6; the worker holds `OnPrem` and `Eu` keys, never the open key). Source settings are tagged by effect
(identity, scope, behaviour, cosmetic) so an edit produces the smallest re-sync.

**P1 connectors**

- *URL list*: exactly the listed URLs; conditional GET (ETag / Last-Modified) where the host supports it.
- *Website crawl*: start URL or sitemap, include/exclude patterns, max depth/pages, robots.txt, identifying user
  agent, SSRF protection, crawl report with failure reasons. Sources crawling the same host share its rate limit (each source has its own frontier in P1; a shared frontier is
  P2, weakness review §1). Incremental runs by sitemap `lastmod` where the host's site profile marks it reliable (umea.se:
  24,184 URLs, all with `lastmod`; design-connectors); the flag is set only after a sample of 500 random URLs has been
  fetched and their content hashes compared with `lastmod`, and until then the umea.se source runs full traversals
  without the `lastmod` skip ([weakness review §3](../weakness-review.md#3-outdated-or-removed-content-keeps-being-answered)).
  `demote` URL patterns beside include/exclude lower the rank of archive sections such as old minutes (factor, default
  0.5). Images and video are excluded by default. No headless rendering in P1: umea.se is server-rendered; a renderer (`IRenderer` seam) comes in P2 only if a P1-start check
  shows pilot sites need JavaScript. Short pages are an info note in the crawl report, never `degraded`.
- *File upload*: files from the admin API through blob storage in upload batches (create → one request per file →
  commit, which enqueues the `sync`; design-connectors §9.1); size/type limits; file names per source. P1 types:
  PDF (text layer and OCR), `.docx`/`.xlsx`/`.pptx`, `.odt`/`.ods`/`.odp`, `.jpg`/`.png`/`.tif(f)`,
  `.txt`/`.md`/`.csv`, HTML. Legacy `.doc`/`.xls`/`.ppt`, `.rtf` and email `.eml`/`.msg` are refused as
  `format_deferred` in P1 and come in P2 (earlier only if the pilot source inventory shows them common); archives
  and macro-enabled Office files are refused (postponed Z1). Everything is virus-scanned (ClamAV) before parsing.

**P2: SharePoint** via Microsoft Graph (`Sites.Selected`, see identity plan): drive delta with `deleted` facets and
moves handled, 410 → full resync, change subscriptions with a renewal schedule, pre-flight check for grants. Onyx's
and Eneo's SharePoint issues (#13583, #16590; #427, #521, #522, #429, #1081) become acceptance tests.

## 4. Pipeline engine

- **Fixed linear pipelines**: each source type has one template: fetch → scan (ClamAV, files) → detect → parse →
  clean → chunk → personal data → embed → index. A pipeline version is (template, template version, parameter set)
  as versioned JSON in `config`; editors set parameters in a form, they never see a graph. `IPipelineNode` is the
  internal way to add a stage in code (design-engine §3, §5).
- **Dynamic routing per document** inside the fixed pipeline: HTML is extracted in the worker with the host's site
  profile; PDFs go to the parser, where `/v1/inspect` decides per page between the pypdfium2 text-layer fast path,
  Docling layout and OCR; Office, ODF and images go to the parser whole (design-processing §3).
- **Per-stage cache keys** = hash(input hash, stage settings, stage code version); parse output by (content hash,
  parser settings hash, parser build); embeddings by (text hash, profile). No whole-graph hashes. A chunk set belongs
  to one document and is keyed by (document, pipeline version, site-profile version, embedding profile, content
  hash); chunks carry a scalar `CollectionId` (design-engine §3.3, §14).
- **Stage manifest (R7)**: at startup the worker registers its stage types and versions; publishing is refused if no
  running worker supports a template version's stages (safe rolling updates).
- **Execution**: per document in stage order. Work is **reconciled**: `EnsureCurrent` enqueues only what a document
  is missing, and a nightly drift sweep catches lost work (design-engine §2.3). A newer execution for the same
  document cancels an older one; the execution id changes only with new content or a forced reprocess (design-engine
  §7.5).
- **Output cache classes**: `durable` (parse outputs, kept while a document uses them), `intermediate` (30 days) and
  `none` (design-engine §8).
- **Preview**: same engine, `preview` job class (highest priority), never writes to `content` except `Jobs.Result`
  and `CrawlHosts.NextAllowedAt`; returns each stage's output, the personal-data state and estimated LLM cost.
- **Publish** = plan → re-process → report → keep or roll back. In P1 the plan is **every document of the collection**;
  the per-stage caches make unchanged stages free. From P2 (weakness review §1) the worker computes a re-processing plan
  (documents × first affected stage, from the parameters that changed and their effect tags). After confirmation new chunk sets
  are written next to the live ones and swapped per document; when the run finishes, the evaluation report (§7) is
  shown; the user keeps the version or rolls back in one click (previous sets kept 14 days). Each chunk set records
  the pipeline version that produced it (design-engine §12).

**P1 stages** (design-engine §6): fetch roots (URL list, crawl, upload), scan, detect (`/v1/inspect` for PDF, TIFF,
Office, ODF), parse (HTML extraction with site profiles and presets, plain text/Markdown and CSV in the worker,
everything else in the parser), clean, chunk (split by headings, flat chunks, metadata from path and page), personal
data, embed, index.

**Personal data in sources (decision: detect and flag)**: the personal-data stage is in every template. It finds
personnummer, samordningsnummer, phone numbers and email addresses (patterns in .NET) for every chunk set, and names
(Swedish NER through the parser's `/v1/ner`, called inside the processing job) **only when they can change the
flag**: the source's collection is `public`, no other rule already flags the set, and it has ≥ 5 contact values. A
per-type flag rule (answered 23, `Settings.PiiFlagRules`) decides whether the set is flagged. Counts and flag live on
the chunk set; a flagged set in a `public` collection is **held** (not served) until a department admin clears the
document (the clearance is a decision on the document); it carries to later sets unless a count rises. If NER is
unavailable, a set that needs it stays `pending` and held (answered 25). Masking stays an optional P2 stage (design-processing §9).
**P2**: SharePoint, PII mask, LLM summary/keywords, table extract, describe pictures, legacy Office, RTF, email with
child documents.

## 5. Embedding (R3)

- One **platform embedding profile**, used for every collection and classification. The pilot runs EU-hosted models
  through the gateway: `text-embedding-3-large` at 1024 dimensions, requested with the `dimensions` parameter
  (Decisions "Pilot embedding model, U-b", "M2 and test hosting"). The profile moves to on-prem BGE-M3 (1024
  dimensions, no `dimensions` parameter) when the on-prem hardware exists (register M2); that move re-embeds every
  chunk.
- Calls go through the gateway with the worker's delegating key for the profile's residency ceiling, sent as the
  explicit `x-ume-max-residency` header: at most the platform ceiling, `Eu` until M2. Until gateway release 2 (P1)
  the key is the pilot department's team's own per-level key and no `x-ume-on-behalf-of` is sent.
- On-prem embedding throughput is measured when the hardware exists (H8, no P1b gate); until then the fixed
  concurrency is set from the EU provider's measured rate.
- Embedding cache by (profile, text hash): re-chunking only embeds new text; batches sized for the gateway. Embedding
  concurrency is a fixed value from configuration in P1; adaptive concurrency (AIMD) is P2.
- Other profiles are an explicit exception set by platform admins. A profile change (P3) re-embeds every affected
  chunk in the background into new, invisible sets, shows the evaluation comparison, then swaps.

## 6. Auto-parse and site profiles

1. **Parse rules**: built-in rules (`default`, `regulations`, `protokoll`, versioned with the stage) plus rules made by
   platform admins, matched on domain/path/document type → strategy overrides and structure hints. `ParseObservations`
   are recorded from day one; from P2 they feed rule **proposals** that a platform admin must approve. Parse
   strategies are never learned or applied automatically (design-processing §3.5).
2. **Per-page heuristics** (all in the parser, chosen by the worker): clean PDF text layer → pypdfium2 text-layer fast
   path; empty/garbled → Docling OCR (Tesseract `swe`); nothing found → re-render; table-dense → Docling tables.
   Signals come from the parser's cheap `/v1/inspect` (page count, per-page text-layer facts, garble score; parser
   design §6) with starting thresholds in parser design §6.1. Pages with the same strategy are grouped into ranges of
   ≤ 20 pages (P1: a fixed size per strategy from configuration; sized from measured time per page in P2, weakness review §1), and range outputs are joined in order (parser design §8.2). Parse concurrency is read from the parser's `/v1/info`; `busy`, `not_ready` and
   `duplicate_in_flight` requeue without counting an attempt; other errors map per parser design §7.5. Spreadsheets
   and CSV are tables: chunked as row groups with the header row repeated.
3. **Site profiles for HTML** (simplification pass §13): per host. **P1** (weakness review §1): a manual profile JSON with
   the fields below; for umea.se a hand-checked Sitevision preset, versioned like any config, with one-click rollback.
   **P2**: learned deterministically from a sample of 30–50
   sitemap pages on a crawl source's first run (design-connectors): CMS preset (Sitevision first), main-content root,
   boilerplate selectors and phrases, breadcrumb selector, date pattern, verified identity rule, URL normalisation,
   excluded URL types, `lastmod` reliability, link lists kept for discovery only. A new source's profile is applied and
   shown in the source-form preview; a re-learned profile for a host in use is applied automatically only when its
   sample check passes (no page loses > 10 % of its main-text words, none ends up empty, boilerplate share does not
   rise), otherwise it is a proposal for a platform admin. Every application is versioned, audited and can be rolled
   back in one click; each document records the profile version that extracted it. The breadcrumb goes in front of
   the heading path in the chunk prefix; the visible date sets `SourceModifiedAt` (fallback `lastmod`); description
   and keywords are stored as metadata (design-processing §3.7, §6.2, §7).

## 7. Evaluation (R6)

- **Evaluation set per collection**, built in layers:
  1. *Generated* (P1b; built and tested on FakeGateway, not run on the pilot until an on-prem model exists, O1): sample chunks, have an on-prem model write a question the chunk answers plus the expected answer,
     keyed to the chunk's document; filter out trivial or ambiguous questions with a judge pass. Regenerated as
     content changes.
  2. *Human*: questions from editors, plus questions derived from chat feedback. Feedback never enters a set
     directly: an editor turns a flagged chat into a **rewritten test question without personal data**, and only the
     rewritten question is kept, with the collection's classification (decision 2026-10-29). The original chat still
     expires under its own retention. The postponed Swedish benchmark becomes this curated layer.
     **Pilot gold set** (P1a, before go-live; reasoning: [weakness review §2](../weakness-review.md#2-nothing-proves-the-answers-are-good-and-not-found-is-effectively-off-at-launch)): 100–150 answerable real questions
     written with the pilot department (its FAQ, contact-centre questions and, if W1 allows, the existing umea.se
     chat), each with expected source URL(s) and the facts a correct answer contains; 30–50 unanswerable off-topic and
     near-miss questions whose expected outcome is "not found"; at least 10 temporal questions where an outdated
     document competes. Stored as `human` questions, so this harness runs them; owned by the pilot department with the
     platform team; the start of register item B1.
  3. *Skill tests*: questions shipped with skills, run by the skill gate (`../tools/plan.md`).
- **Metrics**: recall@10 and MRR@10 for retrieval (expected **document** found?) with counts, measured through the
  shared `Ume.Rag.Retrieval` engine with the Knowledge API's fusion, dedup, variant collapse and per-document cap
  (design-processing §13.5), compared with the previous run (questions won and lost). **Reports lead with the
  gold-set (`human`) results; generated questions are a regression signal only** (weakness review §2).
- **Three-way run** (P1a): the gold set is run vector-only, hybrid, and hybrid without the decompounder (on both
  sides: the copy without compound parts is computed inside the keyword statement from `Chunks.Text`, processing
  §13.4, and queries without them); the result is recorded, and hybrid and the decompounder stay only if they beat vector-only. The same
  run supplies the coverage-threshold proposal from answerable and unanswerable questions together
  (`../knowledge-api/design.md` §7).
- **Advisory, not a gate**: runs on the **full collection** after each publish and nightly; the result is shown on the
  publish screen and the evaluation page and never blocks. A collection goes live when its owner publishes it. A gate
  is reconsidered only when measured run-to-run variance shows it can tell versions apart (design-processing §14).
  The pilot's **go-live gate** (weakness review §2) is a separate, one-time check by people: department reviewers
  grade the gold-set answers by hand (`../README.md`, P1b); publish stays advisory. The answers come from a script
  that sends each gold-set question through the chat API as the pilot assistant (the real path) and writes question,
  answer, citations and coverage to a CSV; reviewers grade it in a spreadsheet, and the graded file and its totals are
  attached to the go-live decision (no grading UI in P1).
- **Answer-level evaluation** (P2, weakness review §1): a nightly on-prem judge over the evaluation sets through
  `Ume.Rag.Answering` measuring faithfulness and citation precision per assistant, run by `rag-admin` (run kind
  `answers`; design-processing §14.8). The worker runs retrieval evaluation only and references neither
  `Ume.Rag.Answering` nor `Ume.Rag.Tools`; the skill gate (P2) also runs in `rag-admin`.
- `eval` job class, lowest priority; LLM costs charged to the owning team through the gateway.

## 8. Safety and efficiency

- Two-stage change detection: sitemap `lastmod` (where reliable) or timestamp/ETag, then content hash (HTML
  noise-normalised); unchanged documents are not re-parsed or re-embedded, and a noise-only change re-keys the chunk
  set instead of rebuilding it (design-processing §12).
- **Deletion guard**: deletions are applied in a reconcile phase after all fetches of a run, only after a complete
  traversal or listing (a traversal is complete when ≥ 99 % of seeds are resolved, no host was paused, no admitted
  claim is pending and the page limit was not hit; a failed seed's document is never a candidate in that run; a URL
  that leaves the sitemap gets a removal check, 404/410 → `Gone`; weakness review §3). A run whose non-exempt
  candidates are more than 20 % of a source (and at least `DeletionGuardMinCount` 5), more than
  `DeletionGuardMaxCount` (500), or a source that returns nothing, pauses for confirmation; configuration changes
  and explicit admin actions are exempt (design-connectors §13). Soft delete with a 14-day grace period. An alert fires when a source has had 3 runs in a row without a complete traversal
  (deletions never ran, content may be stale).
- **Short chunk-write transactions**: a new chunk set is written invisible in batches of ≤ 1,000 chunks, then one
  short swap transaction per document; an orphan sweep removes sets never swapped (design-engine §14).
- **Poison documents**: a reclaim after an instance died does not count as an attempt; a job reclaimed 3 times is
  dead-lettered (design-engine §9.4).
- Failures are data: per-document outcome and reason; a sync with failures above a threshold is marked degraded.
- Every LLM or embedding call carries the owning team's attribution, so gateway budgets stop runaway costs.
- Config is read at the current config version (platform plan §4) with the grace window of the config library; a
  source or pipeline change takes effect within seconds.

## 9. Targets (to confirm)

- Ingest 10k typical web pages per hour on the ingestion zone without moving chat p95 by more than 10% (R1 release
  test, `perf.yml` in P1).
- A re-crawl of an unchanged site does no parsing and no embedding.
- Two departments adding the same site cause one parse (from P2, with the shared frontier, also one crawl request per URL); chunking and embedding run per
  department, embedding mostly from the cache.
- Worker restart loses no work (jobs are leased, not lost) and costs no attempts.
- The umea.se example page extracts to exactly its main text with the right breadcrumb and date (design-processing
  §17).

## 10. Milestones

| Phase | Deliverable |
|---|---|
| P0 | worker dispatcher skeleton: `Jobs` (unpartitioned), leases, heartbeat, reaper, one `command` kind end to end |
| P1a (weakness review §1) | Queue + LISTEN/NOTIFY, scheduler, document store per source, one crawl frontier per source, URL list + crawl (incremental by `lastmod` once the host's flag is set; umea.se starts with full traversals, weakness review §3) + upload connectors, the umea.se site profile as a hand-checked Sitevision preset (manual profile JSON), fixed pipelines with templates per source type, stage manifest, auto-parse rules with built-in rules, personal-data detection (patterns + gated NER, flag rules, held sets), platform embedding profile with fixed concurrency and range sizes from configuration, change detection, deletion guard, run records; human evaluation set (the pilot gold set) and retrieval evaluation runs with the three-way comparison (weakness review §2) |
| P1b | freshness rules of weakness review §3 (completeness by ratio, removal check, `demote` patterns, the 500-URL `lastmod` sample for umea.se, the `website` template's age decay for PDFs), preview, publish (every document of the collection re-processed) + swap + report + rollback, admin parse rules, site-profile edit and rollback, generated evaluation sets, advisory reports (led by the gold set) and nightly runs, `perf.yml` |
| P2 | from weakness review §1: site-profile learning and re-learning, shared crawl frontier (claims, releaser), publish plans from effect tags, adaptive embedding concurrency (AIMD) and moving-average range sizing, nightly answer judge; SharePoint, renderer if needed, parse-rule proposals, per-department fairness and the multi-host queue features, P2 stages, legacy Office/RTF/email, retrieval-signal feedback loop, skill test questions in evaluation |
| P3 | More connectors, embedding-profile migration tooling, OpenSearch indexing if adopted |

## 11. Open questions

Answered (2026-10-29):

1. **Auto-apply defaults**: answered with confidence ≥ 0.9, trial runs and automatic rollback; revised 2026-10-30
   (simplification pass §2): no auto-apply of parse strategies; P2 rule proposals need approval. Site profiles for
   HTML are applied automatically under the guardrails of §6 item 3 (P2 with learning; manual in P1, weakness review §1).
2. **Headless rendering**: answered as needed in P1; revised 2026-10-30 (simplification pass §1, §13): P2, and only if
   a P1-start check shows pilot sites need JavaScript (umea.se is server-rendered).
3. **Uploads**: 50 MB per file; types as the P1 list in §3. Everything is virus-scanned (ClamAV) before parsing.
4. **Crawl politeness**: municipal (allow-listed) hosts up to 10 requests/s, external sites 1 request/s with one
   connection; robots.txt always respected; full crawls of large sites scheduled at night. Per-host limits are shared
   by all sources through the host budget (`CrawlHosts`).

Answered (2026-10-29, step 6 questions):

5. **Detail design split** into three sub-steps: 6a engine + stages + jobs/runs/preview/publish (`design-engine.md`),
   6b connectors + change detection + deletion guard (`design-connectors.md`), 6c auto-parse, chunking, embedding,
   personal-data detection and evaluation (`design-processing.md`).
6. **Names in personal-data detection**: patterns (personnummer with checksum, phone, email) in the .NET worker; names
   by a Swedish NER model baked into the parser image behind a parser endpoint (on-prem, no gateway call), called only
   when it can change the flag (revised 2026-10-30).
7. **Default re-sync schedule**: URL lists nightly (conditional GET), website crawls weekly at night (nightly
   incremental by `lastmod` where reliable; reliable only after the 500-URL sample, weakness review §3), uploads on
   change only; adjustable per source.
8. **Default chunking**: split by headings, then ≈ 400 BGE-M3 tokens with ≈ 15 % overlap, breadcrumb and heading path
   prepended; tables as row groups with the header repeated; flat chunks (no parent-child) in P1.
9. **Shared documents across departments** (6a): answered with follow runs; revised 2026-10-30 (simplification pass
   §2): each source has its own documents and chunk sets; reuse is by content-addressed caches (frontier request from P2, raw
   blob, parse output, embeddings).

Answered (2026-10-29, step 6b questions):

10. **Crawler user agent**: `UmeaKommunBot/1.0 (+<about page URL on umea.se>)`, product-neutral (N1 still open);
    robots.txt rules match `UmeaKommunBot`.
11. **Page-level `noindex`/`nofollow`** (meta robots, `X-Robots-Tag`): honoured on external hosts; **ignored on
    allow-listed municipal hosts**, where `noindex` usually only means "keep out of public search engines".
    robots.txt is always honoured (answered 4).
12. **Crawl size defaults**: 10 000 pages, depth 10 per source; hitting the limit marks the run degraded with a clear
    reason; admins can raise it per source. The preflight warns when the sitemap lists more URLs than the limit; the
    umea.se source needs ≥ 20,000 (≈ 14,100 HTML and PDF URLs).
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

18. **NER model for names**: `KBLab/bert-base-swedish-cased-ner` (MIT, ~110M parameters, SUC 3.0), baked into the
    parser image; measured on our fixtures in P1 (KBLab marks it experimental). The model's `config.json` uses **`PER`**
    for persons (the card's example shows `PRS`); mixed labels (`ORG/PRS`, `PRS/WRK`, `LOC/PRS`) are not counted in P1.
19. **Gate override**: answered as platform admin with a reason; revised 2026-10-30 (simplification pass §8): there is
    no blocking gate, so no override.
20. **Evaluation set size**: up to 100 filtered generated questions per collection (fewer for small collections); every
    run uses the full set (the 50-question gate sample is gone with the gate).
21. **First publish of a new collection**: answered with a 0.70 floor; revised 2026-10-30: no floor; the first
    evaluation result is shown, advisory.

Answered (2026-10-29, step 6c review):

22. **Chunking vs knowledge API**: flat chunks (answered 8) win over the knowledge plan's parent-child; the knowledge
    API expands to the surrounding section at query time.
23. **Personal data that blocks `public` collections**: personnummer, samordningsnummer and external email
    addresses always; names, phones and own-domain emails only together in list-like documents (≥ 5 names and ≥ 5
    contact details). Rules in `Settings.PiiFlagRules`.
24. **Collection go-live**: answered as `building` until the floor; revised 2026-10-30: a collection goes live when its
    owner publishes it.
25. **If NER cannot keep up**: index first; sets that need the name check stay out of their `public` collection until
    names are checked (revised 2026-10-30: only those sets need NER at all).
26. **Evaluation models**: default the gateway's on-prem general chat model for generation and judging (a setting).

Processing details: [`design-processing.md`](design-processing.md). Connector details:
[`design-connectors.md`](design-connectors.md).

## 12. Deferred or removed (simplification pass)

See [`../simplification-pass.md`](../simplification-pass.md).

| Mechanism | Where it went |
|---|---|
| Typed DAG, node catalogue with ports, user-added and locked nodes, canvas | fixed linear pipelines = template + parameters (§4); the visual builder is a P3 item behind a trigger (register C2) |
| Two-stage Merkle hashes, shared chunk sets, collection membership arrays, follow runs | dropped; documents and sets per source, per-stage cache keys (§3, §4) |
| `ner` job class and NER lane sizing; NER for every document | dropped; gated NER inside the processing job (§4) |
| PdfPig fast path in the worker | dropped; all PDFs to the parser, pypdfium2 text-layer path there (§6.2) |
| Parse-profile learning with auto-apply, trial runs, evaluation-driven rollback, kill switch; try-and-score | dropped; parse rules, P2 proposals with approval (§6.1) |
| Publish gate, shadow sub-corpus, escalation, statistics, `building` state and 0.70 floor, override | dropped; advisory harness (§7) |
| Headless rendering (`rag-renderer`) and `origin`/`rendered` variants | P2 if needed (§3) |
| Legacy Office, RTF, email with child documents | P2 (§3) |
| Per-department fairness, batched dequeue, coalesced `NOTIFY`, lease leaders, fleet-wide embedding cap | P2, first multi-host deployment (§2) |
| Interim per-team gateway keys | dropped; one combined gateway release before the pilot (§5). Reversed for the pilot team by weakness review §5 (§13) |

## 13. Deferred or removed (weakness review)

Per [weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer):

| Mechanism | Where it went |
|---|---|
| Site-profile learning, automatic application with guardrails, proposals, re-learning | P2; P1 uses manual profiles of the same schema (umea.se: a hand-checked Sitevision preset) (§6) |
| Crawl frontier shared between sources (claims, releaser) | P2; P1 has one frontier per source, host budgets still shared (§3) |
| Publish plans from effect tags | P2; P1 publish re-processes every document of the collection, still with report and keep or roll back (§4) |
| Adaptive embedding concurrency (AIMD), parse ranges sized by moving average | P2; fixed values from configuration in P1 (§5, §6) |
| Nightly answer-level judge (`answers` runs) | P2 (§7) |

Per [weakness review §5](../weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control):

| Item | Where it went |
|---|---|
| Delegated attribution of worker calls (`x-ume-on-behalf-of` = collection owner team) in P1 | P2 with gateway release 2; P1 uses the pilot team's own per-level keys (§5) |
| BGE-M3 throughput measured in P1 (R1) | on-prem capacity run H8 when the hardware exists (register M2; `to-verify.md` #98, #125) |
