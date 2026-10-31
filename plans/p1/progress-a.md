# Progress: stream A (the worker side, P1a)

Owner: stream A. Brief: [`stream-a.md`](stream-a.md). Rules: [`../../AGENTS.md`](../../AGENTS.md). Checklist and binding
done-when criteria: [`README.md`](README.md) §3.

**How to use:** work top to bottom; item 2 (contracts) early, because stream B waits on it. Take the first unticked
step whose hand-off has landed; if blocked, take the next item and come back. Tick each box in the PR that does the
work and put the PR number after it (`[x] … (#12)`). Record every `to-verify` row in `results-a.md` before ticking it here.

## Waiting on (tick when it lands)
- [x] **J1** `v0.1.0` tagged, P1 ownership rules and these briefs merged (2026-10-31, `v0.1.0`)
- [ ] **J2** B's schema part 1 merged (needed for item 7)
- [ ] **J5** B's `RetrievalEngine` merged (needed for item 9)
- [ ] **J7** B's admin set-up endpoints in Scalar (needed for item 12)
- [ ] **J8** B's realm-additions file; **J9** B's chat API (needed for item 10)
- [ ] **J10** gateway session: gateway reachable with EU embedding, condense and answer routes and the `Eu` key (needed for items 11–13)
- [ ] **J12** user: U1 done (about page, robots.txt) (needed for the `lastmod` sample and item 12)
- [ ] **J13** user and pilot department: gold set written (needed for item 13)
- [x] **J14** user: P1a is shown locally (decided 2026-10-30)

## 1. Pilot-start checks (#1)
Refs: [connectors §5.4, §8.1](../ingestion-worker/design-connectors.md), [weakness review §3](../weakness-review.md)
- [x] Sitemap inventory script in `scripts/`: formats, legacy share (Z1), JavaScript need; to-verify #139 recorded
- [ ] 500-URL `lastmod` sample: first pass stored (after J12); re-fetch date noted (#170, result in P1b)

## 2. Worker contracts (#4)
Refs: [engine §3, §4, §9.1, §13.1](../ingestion-worker/design-engine.md), [connectors §2.1, §12.3](../ingestion-worker/design-connectors.md), [admin §4.7](../admin/design.md)
- [x] `contracts/sources/v1/{url-list,crawl,upload}.schema.json` with `x-effect` (#78)
- [x] `contracts/pipeline/v2/definition.schema.json`; templates `website`, `url-list`, `files` in `src/Ume.Rag.MigrationService/Templates/` (#78)
- [x] `contracts/jobs/v1/`: payloads and results of `sync`, `fetch`, `parse`, `embed`, `command` (P1a kinds), `eval` `on-demand` (#78)
- [x] `contracts/site-profiles/v1/` profile schema (#78)
- [x] JSON Schema 2020-12 validator chosen (to-verify #17); `Ume.Rag.Pipelines` validator and stage keys with tests (#78)
- [ ] **Hand-off J3:** issue to stream B

## 3. Parser P1 formats (#5)
Refs: [parser §3, §6–§10, §15, §16](../parser/design.md)
- [x] `/v1/inspect`; per-page signals and garble detector
- [x] All P1 formats; legacy Office, RTF, e-mail → `format_deferred`; OCR and table modes; page ranges with `partial`
- [x] `/v1/ner` with its two schemas; limits, RSS watchdog, recycling
- [ ] OCR acceptance (#131); text-layer vs Docling on 200 umea.se PDFs (#132); #79, #80 (latency), #110 recorded
  (code part done: `ocr_check.py` passes the synthetic sample, `compare_paths.py` tested on fixtures, #79 and #80
  recorded; the real Umeå sample, the 200 umea.se PDFs and #110 are in the live block)

## 4. Sitevision preset and the umea.se profile (#8)
Refs: [connectors §5.6](../ingestion-worker/design-connectors.md), [processing §17](../ingestion-worker/design-processing.md), [simplification pass §13](../simplification-pass.md)
- [x] Preset in code; umea.se manual profile JSON, hand-checked (PR: a/8-sitevision)
- [x] ≈ 20 saved umea.se pages in `tests/fixtures/umea/` (PR: a/8-sitevision: 20 HTML + 1 PDF)
- [x] **Done:** acceptance page extracts exactly (main text, breadcrumb, date 2026-01-15); #31, #138, #158 recorded
  (PR: a/8-sitevision; open parts: SmartReader fallback (#31), other Sitevision sites (#158, live block))

## 5. Connectors (#6)
Refs: [connectors §3–§7, §9, §11–§13, §18](../ingestion-worker/design-connectors.md)
- [x] Canonicaliser; egress guard (`ConnectCallback`, redirects re-checked); robots and sitemaps
- [x] URL list; crawl with one frontier per source and `CrawlHosts`; full traversals; stricter deletion rule
- [x] Uploads by slot; ClamAV `INSTREAM` client, fail closed, quarantine (#82 ClamAV client; #91 slots, sniffing, quarantine copy; #97 Postgres stores; #108 engine fan-out, `QuarantinedFiles` row, 5102; end to end through the engine PR: a/5-6-done-rows)
- [x] Change detection with noise normalisation; deletion guard
- [x] Settings-change effects (connectors §12.3–§12.4): `Runs.SettingsHashes`, `config-change` syncs from the scheduler, `scope-narrowed` without a request, `identity-changed`, lowered limits delete nothing, URL-list change claims only added URLs, site-profile `excludedTypes`/identity follow-up (PR: a/9-settings-effects)
- [x] **Done:** §18 tests for these parts (incl. C7); #10, #27, #28, #81, #112 recorded (PR: a/5-6-done-rows: §18 mapping in the PR, `UploadEngineTests`; the 24 h `failed` `clamav.unavailable` (#81, `ClamAvWaitEngineTests`) PR: a/9-small-followups; open, deployment, not worker: the upload backup copy within 15 minutes)

## 6. Processing stages (#7)
Refs: [processing §2–§11, §17](../ingestion-worker/design-processing.md), [README §1](README.md)
- [x] `detect`, parse routing with built-in rules and fixed ranges, worker-side parsers, `clean`, split (PR: a/7-processing; inspect adapter after item 5)
- [x] `chunk` with breadcrumb prefix and `SearchText` (`SwedishDecompounder`); metadata; quality facts (PR: a/7-processing)
- [x] `personal-data`: patterns, gated NER, flag rules, `held` (PR: a/7-processing; `/v1/ner` adapter after item 5, database swap with item 9)
- [x] `embed`: cache, canary, fixed concurrency, the profile's model, dimensions and ceiling (`Eu` in P1a) (PR: a/7-processing; SQL stores with item 9)
- [x] **Done:** goldens, PII targets, flag lifecycle green; #26, #43, #111 recorded (PR: a/5-6-done-rows; open parts: #43 provider limits (gateway team), SCB gazetteer of #111 (P2))

## 7. Engine and index writer (#9, after J2)
Refs: [engine §2–§10, §14, §16, §19](../ingestion-worker/design-engine.md), [data-model §9](../platform/data-model.md)
- [x] Stage manifest and support rows; execution with segments, fence, output cache; sync fan-out (URL list and upload, PR: a/9-engine; crawl, `reprocess` and `retry-failed`, PR: a/9-engine-crawl)
- [x] Runs, run items, finalizer; scheduler (Cronos) (PR: a/9-engine)
- [x] Index writer: invisible batches, swap, re-key, `held`, orphan sweep (PR: a/9-engine)
- [x] Blob-tombstone purge sweep (#34); blob root ownership (#41) (PR: a/9-engine)
- [x] Nightly drift sweep (`EnsureCurrent` outside a fetch, `rag.reconcile.drift`); parse slicing and resume from stored ranges; preview-first parser slot gate; parse cache keyed by parser build (PR: a/9-engine-drift-slicing)
- [x] **Done:** integration and crash-recovery tests; a URL-list source syncs into live chunks; #21, #119, #141 recorded (PR: a/9-engine; #141 test part only, measurement open); a crawl source syncs into live chunks, crash between fetch and commit loses no work, #10 measured (PR: a/9-engine-crawl)
- [x] **Hand-off J4:** issue to stream B (#110)

## 8. Evaluation smoke job (#17, your part)
- [ ] `evaluation-smoke` job in `verify.yml`, green on a PR

## 9. Commands and evaluation runs (#10, after J5)
Refs: [processing §13.4–§13.6](../ingestion-worker/design-processing.md), [engine §9.1](../ingestion-worker/design-engine.md)
- [x] `edit-site-profile`, `rollback-site-profile`, `edit-eval-question` (PR: a/10-commands)
- [x] `eval` `on-demand` over the `human` layer: vector, hybrid, hybrid without the decompounder (`reindex-fts` scratch copy) (PR: a/10-eval-on-demand)
- [x] Per-question coverage and the threshold proposal as numbers (PR: a/10-eval-on-demand)
- [x] **Done:** reproducibility test; evaluator == `/v1/retrieve` order (PR: a/10-eval-on-demand)
- [ ] **Hand-off J6:** issue to stream B

## 10. Pilot stack (#18, after J8, J9)
Refs: [repo-and-deployment §6, §7.1](../platform/repo-and-deployment.md), [chat §4.3](../chat-api/design.md)
- [ ] Compose: `rag-admin` and Open WebUI OIDC to the shared Keycloak (dev Keycloak until J11); Open WebUI → `rag-api` key and JWT secret; gateway URL and `Eu` key
- [ ] Run book for the local P1a run; #92 recorded
- [ ] **Done:** stack healthy locally

## 11. EU-route capacity (#19, after J10)
- [ ] `scripts/capacity/` against the EU embedding and chat routes; umea.se initial-load time and cost told to the user

## 12. umea.se set-up and crawl (#20, after J7, J10, J12)
- [ ] Pilot collection, umea.se crawl source with the manual profile, pilot URL list, one upload, set up through Scalar
- [ ] Full traversal with EU embeddings; crawl report reviewed; #94 recorded

## 13. Gold set three-way run (#21, after J13)
- [ ] Three-way run recorded (#168); proposal handed to stream B for the thresholds

## 14. P1a done (#22)
- [ ] A pilot member's cited answer from umea.se content shown in Open WebUI
