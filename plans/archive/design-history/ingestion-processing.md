# ingestion-processing: history moved out of the design

The process sections of [`../../ingestion-worker/design-processing.md`](../../ingestion-worker/design-processing.md) (summary of decisions, milestones, changes to other documents, questions and their answers, deferred and removed items), moved here unchanged on 2026-11-13 (M-14) under their original numbers, so the citations of them still resolve. History: it does not bind; `plans/decisions.md` and the design do.

## 1. Summary of decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Routing | Per document by detected media type: HTML, text/Markdown and CSV in the worker; PDF, Office, ODF and images to the parser. The worker parses **no untrusted binary format** (§3) |
| 2 | Per-page strategy | Six ordered rules on the `/v1/inspect` signals; clean text-layer pages use the parser's **text-layer fast path** (pypdfium2); post-conversion checks re-run garbled pages with forced OCR and empty scanned pages once at scale 3.0 (§3.1) |
| 3 | Parse rules | Built-in rules (`default`, `regulations`, `protokoll`) plus platform-admin parse rules, versioned; `ParseObservations` recorded; no auto-apply learning of parse strategies (§3.5) |
| 4 | HTML | Extraction set by the host's **site profile** (main root, boilerplate, breadcrumb, date pattern, metadata, link lists for discovery only), versioned by 6b (manual in P1, learned from P2, weakness review §1); built-in presets (Sitevision first) when no profile exists (§3.7) |
| 5 | Single-page failures | A page that fails alone after halving is a **missing page** (`degraded`) while ≤ 10 % of pages; above that the document fails (§3.3) |
| 6 | Headings | Layout headings re-levelled by Swedish numbering patterns (`1.2`, `Kapitel 3`, `3 kap.`, `§ 4`, `Bilaga 2`); deeper than `maxLevel` becomes bold body text (§5) |
| 7 | Tokenizer | `Microsoft.ML.Tokenizers` 2.0.1 `SentencePieceTokenizer` over BGE-M3's own SentencePiece model, embedded in the worker; counts must equal the Hugging Face tokenizer on a fixture corpus (§6.1) |
| 8 | Chunk budget | 400 tokens **including** the prefix (breadcrumb › heading path); overlap 60 tokens (15 %) of whole trailing sentences, within a section only; tables as `Kolumn: värde; …` row groups without overlap (§6) |
| 9 | `TextHash`, `SearchText` | `TextHash` = SHA-256 of the exact embedded text (prefix + body, NFC); `SearchText` = text plus the parts of its compounds from the .NET `SwedishDecompounder` (§6.5) |
| 10 | Personal data | Runs on the chunk set after chunking; patterns in .NET for every set; names (NER, `POST /v1/ner`) **only when they can change the flag**; counts and flag live **on the chunk set** (§9) |
| 11 | NER down | Only sets that need the name check wait: `pending`, held out of the `public` collection until checked (§9.6) |
| 12 | Re-flag | A cleared set's clearance carries to its successor unless a flagging type's count rises above its count at clearance; values are never stored (§9.5) |
| 13 | Embed | Platform profile only; ≤ 64 inputs per request, no token cap per request (the gateway checks each input, v0.9.0); cache by (level, profile, `TextHash`); a canary embedding, checked on demand, detects a silently changed gateway model (§11) |
| 14 | Noise-only changes | **No new chunk set**: when the `chunk` output's hash equals the live set's, the set is re-keyed in one row update (§12) |
| 15 | Evaluation | **Advisory harness**: questions point at **documents**; recall@10 and MRR@10 with counts on the full collection after each publish and nightly, compared with the previous run; never blocks (§13, §14). Reports lead with the `human` gold-set results; generated questions are a regression signal only (weakness review §2, §13.4) |
| 16 | Retrieval in evaluation | A shared `Ume.Rag.Retrieval` library runs the Knowledge API's statements, fusion and diversity for both the API and the evaluator (§13.5) |

## 18. Milestones

| Phase | Deliverable |
|---|---|
| P1 | *Detect* with inspect; parse routing, per-page rules, ranges (fixed sizes from configuration), joining, re-render; built-in and admin parse rules, observations; HTML extraction with site profiles and presets (Sitevision first), breadcrumb, date, description and keywords; text and CSV parsers; *Clean*; split with re-levelling; *Chunk* with the BGE-M3 tokenizer, breadcrumb prefix, row groups, `SearchText`; metadata; quality facts; *Personal data* with patterns, gated `/v1/ner`, flag rules, `held` sets, clear command, carry-over and re-flag, 5103; *Embed* with cache and canary; noise re-key; generated sets with filtering; human questions (P1a: the pilot gold set, its three-way run and the coverage-threshold proposal, weakness review §2); `Ume.Rag.Retrieval`; publish reports, nightly runs, on-demand settings comparison; umea.se acceptance corpus; tests of §17 |
| P2 | from weakness review §1: answer-level nightly run (in `rag-admin`), range sizing by EWMA (§3.3), AIMD embed concurrency (§11); parse-rule proposals from observations (approval required); `pii-mask`; feedback-derived questions; parent-child chunks only if §5.3's condition is met; `halfvec` evaluation (data-model §12.16); legacy Office, RTF and email (with child documents); describe pictures; WordPress and Episerver/Optimizely presets |
| P3 | embedding-profile migration tooling; keyed PII fingerprints if count-based re-flagging proves too weak |

## 19. Changes to other documents

Simplification pass (2026-10-30); owners of these documents apply them. Rows of the step 6c review that still hold were
applied then and are not repeated.

| Document | Change |
|---|---|
| `parser/design.md` §6.1, §8 | the clean-text-layer strategy is the parser's `text-layer` fast path (pypdfium2), requested per range like the Docling strategies; ranges are all parser outputs; the worker no longer merges PdfPig pages |
| `parser/design.md` §7.8, §10.1, §13.1 | `/v1/ner` unchanged as a contract; `NER_LANES` sizing and `capacity.ner_slots`-driven worker concurrency removed (one NER process; calls are rare); `ner_ready` stays in `/v1/info` |
| `platform/data-model.md` §2.2 `ChunkSets` | `PiiCounts jsonb`, `PiiFlagged`/`PiiFlaggedAt` move from `Documents` to `ChunkSets`; the clearance (`PiiClearedAt`, `PiiClearedBy`, `PiiClearedCounts`) stays on `Documents` (a decision on the document); `State` `writing`/`live`/`held`/`superseded`; `InputHash` = the `chunk` output's `OutputHash` (§12); key column `SiteProfileVersionId uuid NULL` |
| `platform/data-model.md` §2.2 `Chunks`, `Documents` | `Chunks.SearchText text` (worker-computed, §6.5) and `SearchVector` over it with built-in `swedish`; `Documents.Description text NULL`, `Documents.Keywords text[] NULL`, `Documents.SiteProfileVersionId` (the last extraction) |
| `platform/data-model.md` §2.2 profiles, evaluation | `ParseProfiles.Origin` `builtin`/`admin` with versions; `EvaluationRuns`: `Kind` (`publish-report`/`nightly`/`on-demand`/`answers`), `QuestionCount`, `Found`, `Wins`, `Losses`, `ComparedRunId`, `Faithfulness`, `CitationPrecision`; no statistics, decision or override columns: drop `McNemarP`, CI columns, `Decision`, override columns, `Scope = sub-corpus`; `Collections.PublishedAt` stays as the owner's publish, `PublishGateRunId` and `Settings.GateFloor`/`GateSampleSize` removed |
| `platform/data-model.md` §4.1 | `Jobs.Class` CHECK without `ner` |
| `platform/data-model.md` §2.2 evaluation (weakness review §2) | `EvaluationResults` records per question the coverage status, best distance and best keyword score (§13.6); `EvaluationRuns` records the retrieval variant of an `on-demand` run (`hybrid`, `vector`, `hybrid-no-decompounder`) for the three-way run (§13.4) |
| `platform/repo-and-deployment.md` §1, §2 | `SwedishDecompounder` (in `Ume.Rag.Retrieval` or `Ume.Rag.Text`) used by the worker; the worker references neither `Ume.Rag.Answering` nor `Ume.Rag.Tools` (answer-level run and skill gate in `rag-admin`); `tests/fixtures/umea/` |
| `ingestion-worker/design-connectors.md` | site profiles: storage, versions, learning, auto-apply check (uses 6c §3.7 extraction in preview mode), commands `approve-site-profile`, `reject-site-profile`, `edit-site-profile`, `rollback-site-profile`, re-learning trigger from `quality.boilerplateShare`/`emptyRoot` (§8); reliability of `lastmod` feeds §7's date order |
| `knowledge-api/*` | scope by scalar `CollectionId` and live sets only; query side runs `SwedishDecompounder`; citations may show `Documents.Description`; no `building` collections |
| `archive/area-plans/admin.md` (step 10) | PII list per chunk set with highlight preview and clear (reason); publish screen with the advisory report (counts, wins/losses, lost questions) and one-click rollback; evaluation page: layers, stale questions, generation cost, answer-level results; parse rules in the generic platform-admin form |
| `identity/design.md` §10 | 5103 adds `ChunkSetId`; audit action `gate.override` removed |
| `archive/area-plans/platform.md` §9 | evaluation advisory; collections go live when published |

## 20. Answered questions (2026-10-29)

1. **Flag rules**: option (a): personnummer, samordningsnummer and external email always; names, phones and own-domain
   emails only together in list-like documents (§9.4).
2. **NER capacity**: option (a), revised 2026-10-30: names are checked only when they can change the flag (§9.3);
   such sets stay out of the `public` collection until checked (§9.6).
3. **Evaluation models**: default the gateway's on-prem general chat model for generation and judging (settings).
4. **Collection go-live**: revised 2026-10-30: a collection goes live when its owner publishes it; no floor (§14.3).
5. **Chunking conflict** with knowledge answered 3 (raised in review): flat chunks with section expansion at query time
   in the knowledge API.

## 22. Deferred or removed (simplification pass)

See [`../archive/passes/simplification-pass.md`](../passes/simplification-pass.md).

| Mechanism | Where it went |
|---|---|
| `pdf-fast-path` (PdfPig) and PdfPig/Docling page merging | dropped; all PDFs go to the parser, `text-layer` strategy (§3.1, §4.1) |
| HTML fallback to the parser's HTML backend; render branch; `degraded` for short pages | dropped; renderer P2 (6b); short pages are an info note (§8) |
| Legacy Office, RTF, email and child documents | P2 (§3.2) |
| *Quality check* as a routing node | dropped; quality facts only (§8) |
| Parse-profile learning with auto-apply, trial runs, confidence threshold, automatic rollback, kill switch; try-and-score | dropped; built-in and admin parse rules, P2 proposals with approval (§3.5) |
| `ner` job class, NER-lane sizing (`NER_LANES`, `capacity.ner_slots` concurrency), NER result LRU, NER for every document | dropped; gated NER inside the processing job (§9.3, §9.6) |
| Personal-data state on `Documents`, per-type maximum across pipelines, membership recompute on flag | replaced by state on the chunk set and `held` sets (§9.4, §9.5) |
| Publish gate (`IPublishGate`), sub-corpus shadow sets, escalation 50 → 100, Wilson/bootstrap/McNemar, decision rule, `building` state and 0.70 floor, platform-admin override | dropped; advisory harness on the full collection (§13.6, §14) |
| Skill gate details (retrieval and routing layers) | tools design (simplification pass §10) |
| Fleet-wide Redis embedding cap, scale batching, bulk embed share | P2, first multi-host deployment ([`../archive/passes/performance-pass.md`](../passes/performance-pass.md) §3.9) |
| Answer faithfulness as a P3 assistant layer | moved earlier: nightly answer-level run in P2 (§14.8, §23) |
| `ChunkSets` evaluation scope with exact search | dropped with the gate; collection scope only (§13.5) |

## 23. Deferred or removed (weakness review)

Per [weakness review §1](../passes/weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer):

| Mechanism | Where it went |
|---|---|
| Nightly answer-level judge (`answers` runs, §14.8) | P2 |
| Range sizing from measured time per page (EWMA, §3.3) | P2; fixed range sizes per strategy from configuration in P1 |
| AIMD embedding concurrency (§11) | P2; fixed `Worker:Concurrency:embed` in P1 |
| Site-profile learning and its check (§3.7) | P2 (6b §24); P1 extraction uses the manual profile |
