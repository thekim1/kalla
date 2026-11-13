# ingestion-engine: history moved out of the design

The process sections of [`../../ingestion-worker/design-engine.md`](../../ingestion-worker/design-engine.md) (summary of decisions, milestones, changes to other documents, questions and their answers, deferred and removed items), moved here unchanged on 2026-11-13 (M-14) under their original numbers, so the citations of them still resolve. History: it does not bind; `plans/decisions.md` and the design do.

## 1. Summary of decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Engine | One **fixed linear pipeline per source type**: fetch → scan → detect → parse → clean → chunk → personal data → embed → index. A pipeline version is (template, template version, parameter set). Stages are `IPipelineNode` implementations in code; no graph, no user-added stages (§2, §3) |
| 2 | Ownership | A **document belongs to one source** (source, identity); a **chunk set** belongs to one document and is keyed by (document, pipeline version, site-profile version, embedding profile, content hash, parse-rule salt). Reuse across sources is by content-addressed caches only (§2.1, §8) |
| 3 | Work model | **Reconciliation**: desired state = every live document's live chunk set matches its source's published pipeline version, its host's site-profile version and its current content. `EnsureCurrent(document)` enqueues only what is missing; a nightly sweep catches drift (§2.3) |
| 4 | Jobs per document | Stages declare a job class (`fetch`, `parse`, `embed` or `inline`); a job runs stages until one of another class, then enqueues **one continuation job** in the same transaction. 2 jobs per web page, 3 per PDF (§7.4) |
| 5 | Cache keys | Per stage: `SHA-256(inputHash ‖ stage ‖ stageVersion ‖ settingsHash)`; parser output by (content hash, parser settings hash, parser build). No whole-graph hashes (§3.3, §8) |
| 6 | Results | `Success | TransientError | FatalError`; capacity refusals (`busy`, `not_ready`, `duplicate_in_flight`, 429, unconfirmed config) requeue **without** an attempt (§7.3) |
| 7 | Execution id | A generation per (document, content); changes only on new content or a forced reprocess. It is a **fence**: commits check it and the job lease in the same transaction (§7.5) |
| 8 | Semantics | At-least-once execution, effectively-once effects: content-addressed blobs and cache rows, dedup keys, fenced swaps (§7.6) |
| 9 | Queue | `Jobs` unpartitioned; dedup by a partial unique index on `DedupKey`; a reclaim after an instance died is not an attempt, 3 reclaims → dead-letter (§9.4). Dispatch by class, priority and age, with per-department fairness from P0 (a `GROUP BY` department list, §9.3); the loose index scan, batched dequeue and coalesced `NOTIFY` are P2 |
| 10 | Scheduler | Cron in Europe/Stockholm, deterministic per-source jitter, catch-up collapses to one run (§9.6) |
| 11 | Runs | Run items written only at a terminal outcome; the finalizer derives counters from items; degraded when > 5 % of processed items fail (§10) |
| 12 | Preview | Same engine with a preview context: no DB access for stages, blobs only under `previews/{jobId}/`, *Index* never runs, *Embed* estimates by default; produces personal-data state like any execution (§11) |
| 13 | Publish | Plan → re-process (new sets written next to the live ones, swapped per document) → evaluation report → keep or roll back; previous sets kept 14 days for one-click rollback (§12) |
| 14 | Index writer | A set of ≤ 1,000 chunks is inserted **inside the swap transaction**, visible at commit (written once, #501); a larger set is written **invisible** (`Chunks.DeletedAt` set, state `writing`) in batches of ≤ 1,000 chunks in their own short transactions, then **one short swap transaction** per document (fence, `LiveChunkSetId`, clear `DeletedAt` on the new set's chunks, set it on the old set's, old set `superseded`); an orphan sweep removes sets never swapped (§14) |
| 15 | Config | Each execution resolves the published version at its start and carries it through its continuations; security-relevant reads use the config grace window at job start **and** at the swap (§15) |

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
| `archive/area-plans/admin.md` §4, `admin/design.md` §6 | Pipelines page: template parameter form (no canvas, no JSON node editor, no `gating` state); publish = plan → confirm → re-process → report → keep or roll back; run views per §10.5 |

## 22. Answered questions (2026-10-29)

1. **Shared documents across departments**: answered 2026-10-29 with follow runs; revised 2026-10-30 (simplification
   pass §2): each department's source has its own documents and chunk sets; sharing is by the frontier request, the
   raw blob and the parse cache only.

Decided here without asking (reasons in the sections): degraded threshold 5 % (§10.3); crawl default Saturday 01:00
(§9.6); preview cost cap 0.50 USD (§11); template updates notify pipelines instead of changing them (§13.3); no
office-hours throttling in P1 until the R1 data asks for it (§20); preview may fetch live URLs for new sources through
the normal egress guard (§11).

## 24. Deferred or removed (simplification pass)

See [`../archive/passes/simplification-pass.md`](../passes/simplification-pass.md).

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

Per [weakness review §1](../passes/weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer):

| Item | Where it went |
|---|---|
| Re-processing plan by first affected stage from effect tags (§12.2) | P2; P1 publish re-processes every live document of the affected sources, unchanged stages free from the per-stage caches, still with report and keep or roll back |
| Adaptive embed concurrency (AIMD, §18) | P2; P1 uses the fixed `Worker:Concurrency:embed` from configuration |
| Shared crawl frontier and site-profile learning (6b) | P2 (6b §24) |

Per [weakness review §5](../passes/weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control):

| Item | Where it went |
|---|---|
| `x-ume-on-behalf-of` on worker calls and the `gateway.delegation_not_applied` echo check in P1 (§7.7) | from the gateway's integration phase 2, as the knowledge base's key; until phase 1 the pilot team's `OnPrem` and `Eu` keys |
