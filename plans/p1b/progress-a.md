# Progress: stream A (the worker side, P1b)

Owner: stream A. Brief: [`stream-a.md`](stream-a.md). Rules: [`../../AGENTS.md`](../../AGENTS.md). Checklist and binding
done-when criteria: [`README.md`](README.md) §3.

**How to use:** start once your P1a code items are done or blocked on a live hand-off. Work top to bottom; item 2
(contracts) first, because stream B waits on it. Take the first unticked step whose hand-off has landed; if blocked,
take the next item and come back. Code items (§3.1) all come before the live block (§3.2). Tick each box in the PR
that does the work and put the PR number after it (`[x] … (#12)`). Record every `to-verify` row in `results-a.md`
before ticking it here.

## Waiting on (tick when it lands)
- [ ] P1b ownership rules (README §2) and these briefs merged
- [ ] **L2** B's schema part 3 and freshness columns (needed for item 4's database tests)
- [ ] **L3** B's `RetrievalEngine` with decay and `demote` (needed for item 7's temporal checks and item 25)
- [ ] **L4** B's P1b admin endpoints (then the jobs run end to end from the admin)
- [ ] **L6** B's chat writer and importer with their configuration names (needed for item 17)
- [ ] **L7** B's pilot-journey E2E specs (needed for item 15's `e2e` job)
- [ ] **L10** user: test host, backup target, key-escrow location (needed for item 21)
- [ ] **L11** IT security: self-hosted runner, or the manual stopgap accepted (needed for items 22, 23)
- [ ] **L12** user: G1 finished (needed for items 29, 32)
- [ ] P1a's J10–J13 (live block, `../p1/progress-a.md`)

## Code items

### 2. Contracts part 2 (first)
Refs: [engine §9.1, §11, §12](../ingestion-worker/design-engine.md), [admin §4.7](../admin/design.md), [connectors §5.4](../ingestion-worker/design-connectors.md), [processing §13, §14](../ingestion-worker/design-processing.md)
- [x] `contracts/jobs/v1/`: `preview` (`stage-preview`, `preflight`, `publish-plan`), `sync` `reprocess`/`retry-failed`, `eval` `publish-report`/`nightly`/`generate-set`/`top-up-set`, `on-demand` with `CandidateSettings` (#88)
- [x] Commands `confirm-deletion`, `reject-deletion`, `release-hold`, `clear-pii-flag`, `reprocess-document` (those not in J3) (#88)
- [x] `contracts/sources/v1/`: `demote` patterns, `incrementalSchedule`, `revalidateEvery` with `x-effect`; `website` template's `recency` default (#88)
- [x] Drift test; `Ume.Rag.Pipelines.Tests` for the new keys (#88)
- [ ] **Hand-off L1:** issue to stream B

### 4. Freshness in the crawler
Refs: [weakness review §3](../weakness-review.md), [connectors §5.4–§5.6, §18](../ingestion-worker/design-connectors.md)
- [x] Completeness by ratio (≥ 99 % resolved; failed seeds protected; unreachable robots.txt = paused); C7 updated (#101)
- [x] Removal check (HEAD, then GET; 404/410 → `Gone`; 200 kept; also in full traversals) (#101)
- [x] `demote` patterns on the source; incomplete-traversal alert; report sections (#101)
- [x] `lastmod` skip and incremental runs gated on `lastmodReliable`; `revalidateEvery` (#101)
- [x] `lastmod` sample comparison (≤ 2 % threshold, ≥ 7 days apart) proposing the flag through `edit-site-profile` (#101)
- [x] **Done:** connectors §18 tests for these parts green; fixture-site removal test (#101)

### 5. Preview and preflight
Refs: [engine §11](../ingestion-worker/design-engine.md), [admin §4.7, §6.4](../admin/design.md)
- [ ] `preview` class, `stage-preview` with isolation, `untilStage`, `basePreviewJobId`, limits, 24 h blobs; site-profile before/after
- [ ] `preflight` for URL lists (≤ 20 URLs) and crawls
- [ ] **Done:** engine §19 preview-isolation test green

### 6. Publish, re-process, rollback
Refs: [engine §12, §14](../ingestion-worker/design-engine.md)
- [ ] `publish-plan` (every live document, forecast); `reprocess` with per-document swap
- [ ] Previous sets kept 14 days; rollback swaps back; site-profile versions via the same logic
- [ ] **Done:** engine §19 publish and rollback tests green

### 7. Evaluation harness
Refs: [processing §13, §14](../ingestion-worker/design-processing.md), [ingestion plan §7](../ingestion-worker/plan.md)
- [ ] `publish-report`; `nightly` with the recall@10 alert and metric; `on-demand` with `CandidateSettings`
- [ ] `generate-set`/`top-up-set` with filtering on FakeGateway (model and ceiling per README §7 O1)
- [ ] Reports lead with the `human` layer
- [ ] **Done:** reproducibility test green
- [ ] **Hand-off L5** (with items 5, 6, 8): issue to stream B

### 8. Remaining commands and admin parse rules
Refs: [admin §4.7](../admin/design.md), [processing §3.5, §9.5](../ingestion-worker/design-processing.md), [connectors §13](../ingestion-worker/design-connectors.md)
- [x] Deletion decisions, release hold, PII clearance, reprocess one document (PR: a/10-commands)
- [ ] Admin-made parse rules matched and re-keying only matching documents

### 16. Gold-set grading script
Refs: [weakness review §2](../weakness-review.md), [ingestion plan §7](../ingestion-worker/plan.md)
- [ ] Script in `scripts/`: questions through the chat API as the pilot assistant → CSV (question, answer, citations, coverage)
  (code part done: `scripts/grading/grading.py answer` tested against a fake chat API, streamed and not, #?; the run
  against the AppHost with FakeGateway on `tests/evaluation/smoke` waits for stream B's chat API (J9) and smoke set)
- [x] Totals for the four gate measures from a graded file (#101)
- [ ] **Hand-off L8:** issue to the user with the CSV format

### 15. CI and harnesses
Refs: [repo-and-deployment §8.1–§8.3](../platform/repo-and-deployment.md), [chat §16.2](../chat-api/design.md)
- [x] `benchmarks/release-test/` (k6 idle and during crawl + OCR; overhead run) and `perf.yml`; local dry run writes the report (#?)
- [x] `evaluation.yml` (nightly, dispatch) (#?)
- [ ] `e2e` job, nightly + release (after L7) — `e2e.yml` wired (nightly, dispatch, called by `release.yml`); skips with a notice until `tests/e2e` exists (#?)

### 17. Pilot stack for P1b
Refs: [chat §4.3, §13.3, §14.2](../chat-api/design.md), [repo-and-deployment §6.2, §7](../platform/repo-and-deployment.md)
- [ ] Open WebUI API keys restricted to the feedback routes; service account; `OpenWebUi__AdminApiKey` secret; importer settings
- [ ] Test-host Compose with the pilot caps; test-host run book
- [ ] Runbook public-records section lists the three candidate routes
- [ ] **Done:** `docker compose config` green; local import and pruning work; #49 recorded

### 19. Code complete (your part)
- [ ] Pin review: images, PyPI and Compose pins under 7 days old reviewed; Open WebUI pin per README §7 O8

## Live block

### 20. P1a's live items
- [ ] As `../p1/progress-a.md` (items 11–14 there; item 1's `lastmod` first pass started early per README §7 O5)

### 21. Test-host deploy and restore drill (after L10)
- [ ] Current release behind the municipal proxy; health green through the proxy
- [ ] Restore drill with realistic size: RPO ≤ 5 min, RTO ≤ 4 h; escrowed keys read back; #90, #123, #129 recorded

### 22. R1 on the test host (after L11)
- [ ] `perf.yml` (or the script) passes; report attached; #100, #101, #106 recorded
- [ ] Pilot CPU caps re-set in `compose.host-24g.yaml`

### 23. Nightly evaluation
- [ ] A week of nightly runs on the test deployment; variance noted; #44 recorded

### 24. `lastmod` sample result
- [ ] Re-fetch ≥ 7 days after the first pass; #170 recorded; flag set or full traversals kept

### 25. Freshness checks (with B)
- [ ] Evaluator runs on the temporal questions handed to B for #171

### 26. Personal-data dry run
- [ ] Full umea.se crawl's flags reviewed with the platform team

### 28. Feedback import live (with B)
- [ ] Service account in the shared realm; Compose secrets on the test host

### 29. Banner and public-records route (after L12)
- [ ] Runbook section names the G1 route and was tried once; #172, #173 recorded

### 31–32. Go-live gate and pilot live
- [ ] Graded gold-set answers meet every threshold (with B and the user)
- [ ] Pilot live
