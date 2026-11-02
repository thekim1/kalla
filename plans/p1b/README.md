# P1b work split

P1b is pilot hardening ([weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer):
"feedback, the go-live gate of §2, the freshness rules of §3, the data-protection items of §4, admin journey pages for
sources and the assistant"; phases table P1b row in [`../README.md`](../README.md)). **Done when the go-live gate is met
on hand-graded gold-set answers and the pilot is live** ([`../progress.md`](../progress.md) P1b list, weakness review
§2). This file is the single source of the ordered P1b checklist (§3) with every item's done-when; the briefs only
assign and order it.

**Written now, not after P1a** (Decisions table, "Code first, live testing last", 2026-10-31; overrides
[`../p1/README.md`](../p1/README.md) "P1b's briefs are written when P1a is done"). Both streams finish their P1a
**code** items, then continue straight into P1b's code items without pausing at the P1a demo. Everything that needs
the real gateway, the shared Keycloak, umea.se, the gold set, the pilot member or the test host is the **live block**
at the end, run in one go with P1a's live items.

Two streams on P0's seam ([`../p0/README.md`](../p0/README.md) §6, [`../p1/README.md`](../p1/README.md) §2): stream B
owns the schema, the admin API and **every admin page**, retrieval, knowledge and chat; stream A owns the worker,
parser, connectors, pipelines, evaluation runs, Compose, CI and scripts, and delivers job kinds against written
contracts in `contracts/`.

| Brief | For |
|---|---|
| [`stream-a.md`](stream-a.md) | Stream A: P1b job contracts, freshness in the crawler, preview, publish and rollback, evaluation harness, commands, CI (`perf.yml`, `evaluation.yml`, `e2e`), grading script, stack and test host |
| [`stream-b.md`](stream-b.md) | Stream B: chat schema, age decay and `demote`, admin API and journey pages, assistant versions, test panel, feedback (writer, import, page), accessibility, W3 spike, release-1 switch |

Progress: [`progress-a.md`](progress-a.md), [`progress-b.md`](progress-b.md); phases: [`../progress.md`](../progress.md).
Results: `results-a.md`, `results-b.md` in this folder (created by each stream's first PR that records a `to-verify`
row, same shape as P0's).

## 1. What P1b is

### 1.1 Done when

1. The **go-live gate** is met: department reviewers grade the gold-set answers by hand and every starting threshold
   holds (correct ≥ 80 %, citation precision ≥ 90 %, false not-found ≤ 10 %, off-topic answered "not found" ≥ 80 %);
   the graded file and its totals are attached to the go-live decision (weakness review §2; ingestion plan §7).
2. The **DPIA (G1) is finished**, with W2 decided inside it (weakness review §4; register G1, W2).
3. The **pilot is live**: one pilot department uses its assistant in Open WebUI (phases table, P1b goal).

After P1b, outside its done-when: the gate is re-run monthly and **pilot success** is decided at the end of the pilot
with the department (weakness review §2; [`../progress.md`](../progress.md) last P1b box).

### 1.2 Code items and live items

| Kind | Meaning | When |
|---|---|---|
| **code** | buildable now against FakeGateway (1024 dims), fixtures (`tests/fixtures/`, `tests/evaluation/smoke`, the ≈ 20 saved umea.se pages), the AppHost's dev Keycloak and a local Open WebUI | right after the stream's P1a code items, in §3 order |
| **live** | needs the real gateway (J10/L9), the shared Keycloak (J11), umea.se (J12/U1), the gold set (J13), the pilot member, the test host (L10) or a decision by the user (L12) | the live block (§5 step 4), after every code item of both streams |

The live block also runs P1a's live items (P1a §3 items 19–22, the 200-PDF comparison of item 5, the `lastmod`
sample's first pass of item 1); their done-when stays binding in [`../p1/README.md`](../p1/README.md) §3.

### 1.3 Carried from P1a and the Decisions table

| Decision | Effect on P1b |
|---|---|
| **M2 triggered** ("M2 and test hosting"): every model call EU-hosted through the gateway | Pilot embedding, condense and answer stay EU (`p1/README.md` §1). Re-embedding onto on-prem BGE-M3 (P1a §8 O3) is **not** a P1b item while no hardware is in sight; the H8 capacity run waits for hardware (Open O6) |
| **Gateway release 1 in P1b, before go-live** ("P1a plan answers"; P1a J15) | Live item 27 switches to release 1 (hand-off L9) |
| **Test host and restore drill before go-live** ("M2 and test hosting"; P0 #12/#16, P0 progress-a §5) | Live item 21 |
| **7-day rule applies again before go-live** ("7-day rule in development") | Item 19: every pin younger than 7 days is reviewed |
| **Pilot CPU sizing re-set before go-live with the R1 test** ("Low CPU during development"; repo-and-deployment §6.2, §8.3) | Live item 22 |
| **G1 postponed, still before go-live**, must list the EU processor agreement (PUB-avtal) (`p1/README.md` §1) | User item U-i; if G1 refuses EU processing the pilot waits (accepted risk, `p1/README.md` §8) |
| **History mode `none`** for every P1 assistant (weakness review §4) | No chat bucket keys, content encryption, crypto-shredding, comment reveal or conversation review in P1b; the schema keeps their columns |

## 2. Ownership additions

`.github/stream-ownership.txt` stays the authority; the main session adds these lines before P1b work starts (each
before the broader rule it narrows):

| Path | Owner | Why |
|---|---|---|
| `plans/p1b/results-a.md`, `plans/p1b/progress-a.md` | a | as P0 and P1a |
| `plans/p1b/results-b.md`, `plans/p1b/progress-b.md` | b | as P0 and P1a |
| `benchmarks/release-test/**` | a | the R1 test harness (k6 chat streams during crawl + OCR, the chat overhead run) of repo-and-deployment §1 and §8.3, run by `perf.yml`; no rule matches `benchmarks/` today, so the check would fail |
| `docs/accessibility-statement.md` | b | the admin UI's tillgänglighetsredogörelse "in the gateway's format" (admin design §10.1, §12 P1b (3)); `docs/**` is A's |

Unchanged and worth repeating: B is the single schema owner; `tests/e2e/**` (pilot journey specs and their static
fixture site) is B's under `tests/**`; `.github/workflows/**`, `scripts/**`, `deploy/compose.*.yaml`, `docs/runbook.md`
and `tests/fixtures/**` are A's; `deploy/keycloak/**` is B's.

## 3. P1b checklist (ordered)

Order follows [`../README.md`](../README.md) "Order within P1", P1b part: admin journey pages → evaluation harness and
`perf.yml` → spikes on E1 and the W3 workaround → Open WebUI pilot (G1 finished before it). Items marked *(after Kn)*
wait on a hand-off (§4). Each item's done-when is binding; the refs say where its detail is.

### 3.1 Code items

| # | Owner | Kind | Item | Done when | Refs | to-verify |
|---|---|---|---|---|---|---|
| 1 | B | code | **Schema part 3** (first, A's database tests wait): the `chat` tables of data-model §2.3 except the P2 `Attachments`/`AttachmentChunks` (`Conversations`, `Messages`, `MessageSources`, `Feedback`, `FeedbackImportState`, `AssistantDailyTotals`, `DataKeys` and `ToolCalls` created but unused in P1); any weakness review §3 column P1a's item 2 did not create (`Sources.DemoteRules` generated column, `CrawlRuns.SeedsFailed`/`Complete`); grants → **L2** | migrations apply on `ume-rag/postgres`; `DatabaseRoleTests` cover the new grants; no P1 code path writes a ciphertext column or a `DataKeys` row | data-model §2.3, §3, §8; weakness review §3 "Demote rules", §4 | – |
| 2 | A | code | **Contracts part 2** → **L1**: job payloads and results for the P1b kinds not in J3: `preview` (`stage-preview`, `preflight`, `publish-plan`), `sync` `reprocess` and `retry-failed`, `eval` `publish-report`, `nightly`, `generate-set`, `top-up-set` and `on-demand` with `CandidateSettings`, the commands `confirm-deletion`, `reject-deletion`, `release-hold`, `clear-pii-flag`, `reprocess-document`; source settings additions (`demote` patterns, `incrementalSchedule`, `revalidateEvery`) with `x-effect`; the `website` template's `recency` default | schemas checked in and exported by a test that fails on drift; additive to J3 (tolerant reader); `Ume.Rag.Pipelines.Tests` cover the new keys | engine §9.1, §11, §12; admin §4.7; connectors §5.4; processing §13, §14; weakness review §3 | – |
| 3 | B | code | **Age decay and `demote`** in `Ume.Rag.Retrieval` → **L3**: `RetrievalSettings.recency` multiplier `1 − weight · (1 − 2^(−age/halfLife))` on `SourceModifiedAt` after fusion (off by default); `demote` factor from `Sources.DemoteRules`, lowest factor when several match; the write path sets `recency` (730 days, 0.15, PDFs) when a collection gets its first `website` source unless already set; **the keyword stop list** (terms in > 30 % of the scope's chunks dropped from K's query, Decisions "Keyword stop list before the pilot crawl") | knowledge §16.1 tests for both multipliers green (uploads never demoted); the evaluator and `/v1/retrieve` still return the same ids in the same order; K ≤ 30 ms p95 on the 100k-chunk synthetic scope of #5 | weakness review §3; knowledge §1 (rows 8, 15), §6.2, §6.5, §11, §16.1, plan §5 (P1b); admin §5.4 step 3 | 5 (re-measure), (171 live) |
| 4 | A | code | **Freshness in the crawler** *(after L2 for DB tests)*: completeness by ratio (≥ 99 % of seeds resolved, no paused host, no pending claim, limit not hit; failed seeds never `not-seen` candidates; unreachable robots.txt = paused); removal check (HEAD then GET, 404/410 → `Gone`, 200 keeps; also in full traversals); `demote` patterns on the source; incomplete-traversal alert; `lastmod` skip and incremental runs, used only when the host's `lastmodReliable` is set; `revalidateEvery`; the crawl report's completeness and removal sections; the `lastmod` sample's comparison (≤ 2 % changed without a newer `lastmod` over ≥ 7 days → propose `lastmodReliable` through `edit-site-profile`) | connectors §18 tests for these parts green (C7 with the ratio rule); a fixture site shows a 404 after leaving the sitemap deleted and a 200 kept; an incremental run never marks a traversal complete | weakness review §3; connectors §5.4, §5.5, §5.6, §19 (P1 row); ingestion plan §10 (P1b) | – (170 live) |
| 5 | A | code | **Preview and preflight** (worker): class `preview`, `stage-preview` with `ExecutionMode.Preview` isolation, `untilStage`, `basePreviewJobId`, limits and 24 h blobs; the site profile's before/after for crawl sources (manual profile or Sitevision preset); `preflight` for URL lists (≤ 20 URLs) and crawls (reachability, robots, sitemaps, estimated pages vs `maxPages`, egress, CMS recognised) | engine §19 preview-isolation test green (no `content` row except `Jobs.Result`/`CrawlHosts` changes) | engine §11; connectors §2.2, §19; admin §4.7, §5.4 steps 4–5, §6.4 | – |
| 6 | A | code | **Publish, re-process, rollback** (worker): `publish-plan` (every live document of the affected sources, forecast), `reprocess` syncs with per-document swap, previous sets kept 14 days, rollback swaps back without parsing or embedding; site-profile versions use the same logic for the host's HTML documents; `publish-report` enqueued when the `reprocess` run finishes | engine §19 tests: live answers use the old sets until each swap; a rollback within 14 days parses and embeds nothing | engine §12, §14; weakness review §1 ("P1 publish re-processes …"); admin §6.6, §6.7 | – |
| 7 | A | code | **Evaluation harness** (worker) → with items 5, 6, 8: **L5**: `publish-report` vs the previous run; `nightly` per published collection with the recall@10 alert (5 points below the 4-week median) and `rag.evaluation.recall_at_10`; `on-demand` with `CandidateSettings`; `generate-set`/`top-up-set` with filtering (model and ceiling per Open O1); reports lead with the `human` (gold-set) layer | processing §17 reproducibility test green; generation and filtering tested on FakeGateway; the nightly schedule registered | processing §13.1–§13.3, §14.1–§14.8; ingestion plan §7, §10 (P1b); weakness review §2 | – (44 live) |
| 8 | A | code | **Remaining commands and admin parse rules** (worker side): `confirm-deletion`, `reject-deletion`, `release-hold`, `clear-pii-flag`, `reprocess-document` (those P1a did not build); admin-made parse rules (`ParseProfiles.Origin = admin`) matched like built-in ones, re-keying only matching documents | each command applied end to end from a job row in `Ume.Rag.Worker.Tests`; a rule change re-keys only the documents it matches | admin §4.7; processing §3.5, §9.5; connectors §13; ingestion plan §10 (P1b "admin parse rules") | – |
| 9 | B | code | **Admin API for P1b** → **L4**: sources (preflight, preview, impact, run history, deletion decisions, documents with PII view/clear, release hold, template parameters, `demote`), pipeline publish (plan → confirm → report → keep / roll back), evaluation set and reports (gold set first, threshold proposal as numbers), retrieval settings with the on-demand comparison, assistants (draft → publish, versions with restore, archive, description, contact), test panel (`Ume.Rag.Answering` in `rag-admin`, same release as `rag-api`), audiences, retention, impact lines, audit list, explain access; cross-rules for these objects only | every write audited and bumps the config version; `PermissionMatrix` covers each endpoint; confirm refused (409) when the draft changed since the plan | admin §4.1–§4.9, §6.6, §6.7, §12 P1b (1)–(2); admin plan §5; identity §4.9; weakness review §1 "Stays in P1" | 23 |
| 10 | B | code | **Chat: history `none`, feedback** → **L6**: issued conversation ids (`x-rag-conversation-id`), `ConversationWriter` (metadata only, `AssistantDailyTotals`), conversation endpoints and user delete, 90-day metadata retention, `/v1/feedback` (ratings; comment refused `comment_not_stored`), `FeedbackImporter` (ratings and reason categories only, watermark, advisory lock) and Open WebUI feedback pruning over its HTTP API | chat §18 tests for these parts green; a mode-`none` turn writes no text; the importer maps a recorded Open WebUI feedback list fixture to the right message and drops comments | chat §11, §12.1–§12.2, §13, §14; chat plan §7 (P1b); weakness review §4 "Feedback" | 20, 51, 135 |
| 11 | B | code | **Journey pages, part 1** *(after L1; real results after L5)*: Kunskapsbanker (list, Översikt, Källor, Dokument with personuppgifter), Källa (URL list, crawl, upload forms with template parameters, preflight, preview with Före / Efter, impact, runs, crawl report, files, quarantine, deletions, held, Nedprioritera), Publicering (plan, progress, report, Behåll / Återställ) | the department admin's journey (admin §5.4 steps 1–8) works on the local stack with FakeGateway; Swedish text only in `labels.ts`/`messages.ts` | admin §5.3, §5.4, §5.5, §6.4, §6.6, §6.7, §12 P1b (1); admin plan §5 | – |
| 12 | B | code | **Journey pages, part 2**: Testfrågor och kvalitet and the evaluation report, Sökinställningar, Assistenter (draft form with description and contact, Testa on the draft, Publicera, Versioner with restore, Målgrupp, retention, archive), Översikt setup checklist, generic forms for platform objects (settings, data protection with `none` only, clients, keys, templates, site profiles, embedding profiles, gateway models, organisation, parse rules), Granskningslogg, explain access | admin §5.4 steps 9–13 work locally; restore makes a new version; generic forms render and round-trip `jsonb` | admin §5.3, §5.8, §7, §12 P1b (2); simplification pass §15 item 3 (later change) | 146 |
| 13 | B | code | **Feedback page** (Återkoppling): ratings, reason categories, assistant, time, cited documents, outcome, import health; no content | page tests green on imported fixture feedback; department scoping enforced | admin §8.1, §12 P1b (3); chat §14.4 | – |
| 14 | B | code | **Accessibility and pilot journey E2E** → **L7**: axe, target-size and reflow specs, a keyboard-only Playwright run of the pilot journey against a static fixture site (nginx, Swedish pages and PDFs), one NVDA pass; the tillgänglighetsredogörelse | the pilot journey E2E green locally; zero CSP violations; statement in `docs/accessibility-statement.md` | admin §10.1, §11.4, §12 P1b (3); repo-and-deployment §8.2 (`e2e`) | 96 |
| 15 | A | code | **CI and harnesses** *(the `e2e` job after L7)*: `perf.yml` and `benchmarks/release-test/` (k6 chat at 5 req/s idle, then during a full crawl of a static page copy plus 50 scanned PDFs; pass rule; Markdown report; the chat overhead run); `evaluation.yml` (nightly, dispatch, against the test deployment); the `e2e` job (nightly + release) | both workflows dispatchable; a local dry run of the R1 harness on FakeGateway's latency profile writes the report; `e2e` green on a dispatch | repo-and-deployment §8.1–§8.3; chat §16.2; simplification pass §1 (workflows) | – (100, 101, 106 live) |
| 16 | A | code | **Gold-set grading script** → **L8**: sends each `human` question through the chat API as the pilot assistant and writes question, answer, citations and coverage to a CSV; totals for the four gate measures from the graded file | runs against the AppHost with FakeGateway on `tests/evaluation/smoke` and writes the CSV; totals computed from a hand-made graded sample | weakness review §2 "Hand grading"; ingestion plan §7 | – |
| 17 | A | code | **Pilot stack for P1b** *(after L6)*: Open WebUI API keys restricted to the feedback routes, the feedback service account and `OpenWebUi__AdminApiKey` secret; the importer's settings in Compose; test-host Compose with the pilot caps (`compose.host-24g.yaml`) and a test-host run book; the runbook's public-records section lists the three candidate routes until G1 picks one | `docker compose config` green; locally the importer reads feedback from Open WebUI and prunes it | chat §4.3, §13.3, §14.2; repo-and-deployment §6.2, §7; weakness review §4 "Export route" | 49 |
| 18 | B | code | **W3 spike** on the dev Keycloak and a local Open WebUI: our `/v1/models` lists every assistant offered to one client, and Open WebUI group permissions (groups from the OIDC claim) hide models per user without breaking chat | result recorded with a two-user interleaved test | weakness review §5; postponed W3; workspaces plan §3; identity plan §5 | 174 |
| 19 | A, B | code | **Code complete and go-live pin review** (last code item): every pin younger than 7 days reviewed (NuGet, npm, PyPI, images), the Open WebUI pin (Open O8), the `sv_SE` list check | no pin under 7 days old without a recorded reason; the main session has settled every P1b "Design deviations" | Decisions table "7-day rule in development", "P0 session 7"; register P2 (obligations) | 113 |

### 3.2 Live items (the end block)

Run after every code item of both streams, together with P1a's live items (§5 step 4).

| # | Owner | Kind | Item | Done when | Refs | to-verify |
|---|---|---|---|---|---|---|
| 20 | all, user | live | **P1a's live items**: J10–J13, P1a items 19–22, item 5's 200-PDF comparison, item 1's `lastmod` first pass (if not started early, Open O5) | as `p1/README.md` §3 | `p1/README.md` §3, §4 | as listed there |
| 21 | A, user | live | **Test-host deploy and restore drill** *(after L10)*: the current release deployed behind the municipal proxy; point-in-time restore drill with a realistic size; escrowed keys read back | health green through the proxy; RPO ≤ 5 min and RTO ≤ 4 h achieved and documented | P0 progress-a §5, §12; repo-and-deployment §7.4; Decisions "M2 and test hosting" | 90, 123, 129 |
| 22 | A | live | **R1 on the test host** *(after L11 or its stopgap)*: `perf.yml` (or the same harness by script); pilot CPU caps re-set from the result | chat p95 TTFT in phase 2 ≤ 1.10 × phase 1, no errors, no connection refusals; report attached; caps recorded in `compose.host-24g.yaml` | repo-and-deployment §6.2, §8.3; register R1; Decisions "Low CPU during development" | 100, 101, 106 |
| 23 | A | live | **Nightly evaluation** on the test deployment (`evaluation.yml`) and nightly runs on the pilot collection | a week of nightly runs recorded; run-to-run variance noted | repo-and-deployment §8.1; processing §14.8; Open O4 | 44 |
| 24 | A | live | **`lastmod` sample result** for umea.se: re-fetch ≥ 7 days after the first pass | result recorded; `lastmodReliable` set (incremental runs on) or full traversals kept | weakness review §3; connectors §5.4 | 170 |
| 25 | B, A | live | **Freshness and index checks on the pilot collection**: age decay and `demote` on the gold set's temporal questions; HNSW recall against exact search | current documents rank above outdated ones without recall@10 dropping on the other questions (values adjusted otherwise); recall delta recorded | weakness review §3, "Lower-ranked findings"; knowledge §6.4, §6.5 | 117, 171 |
| 26 | A | live | **Personal-data dry run** on the full umea.se crawl | flags reviewed with the platform team; `PiiOwnDomains` adjusted if a single address holds `public` documents | weakness review "Lower-ranked findings"; processing §9 | – |
| 27 | B | live | **Gateway release 1 switch** *(after L9)*: the pilot team's per-level keys with `RequireMaxResidency`; every call sends its ceiling; V8 against release 1 | `GET /api/gateway/status` green against release 1; `/v1/models` at a ceiling lists only models within it | gateway-changes §13.2–§13.4; `p1/README.md` J15; weakness review §5 | 41 (release-1 part) |
| 28 | B, A | live | **Feedback import live**: the service account in the shared realm (J11), import and pruning against the pilot Open WebUI | a pilot rating appears on the Feedback page; Open WebUI feedback older than 90 days is pruned | chat §14.2, §13.3 | 49, 135 |
| 29 | A, user | live | **Banner and public-records route** *(after L12)*: banner adequacy and the export route as decided in G1; runbook section final | runbook section names one route and was tried once on the pilot Open WebUI | weakness review §4; chat §13.3; docs/runbook.md | 172, 173 |
| 30 | B | live | **E1 spike** per Open O3 | result recorded, or moved to P2 by the user | weakness review §5; identity §9.4, §16 Q9 | 175 |
| 31 | all, user | live | **Go-live gate** *(after L8, J13)*: the grading script's CSV graded by department reviewers | every threshold of §1.1 met; graded file and totals attached to the go-live decision | weakness review §2 | – |
| 32 | all, user | live | **Pilot live** *(after L12, item 31)*: G1 finished (W2 inside), N1 named, W1's relation decided, pilot accounts in the shared Keycloak; the pilot department uses the assistant in Open WebUI | the main session ticks P1b in `../progress.md` | phases table P1b; weakness review §4, §5 | – |

## 4. Hand-offs

L-numbers are P1b hand-offs (renamed from K, which the register uses for the key service, `postponed-decisions.md`). P1a's J10–J13 stay the inputs
of the live block (`p1/README.md` §4).

| # | From → to | What | Until it lands |
|---|---|---|---|
| L1 | A → B | Contracts part 2 merged (item 2) | B's endpoints use the admin §4.7, engine §9.1, §11, §12 field lists; schema validation added when L1 lands |
| L2 | B → A | Schema part 3 and the freshness columns merged (item 1) | A codes item 4 against data-model §2.2; its database tests wait |
| L3 | B → A | `RetrievalEngine` with decay and `demote` merged (item 3) | the evaluator runs without them; the temporal-question checks wait |
| L4 | B → A | P1b admin endpoints enqueue the L1 kinds (item 9) | A's tests enqueue through `IJobQueue` directly |
| L5 | A → B | Preview, publish and rollback, evaluation runs and commands applied (items 5–8) | B's pages and tests read recorded `Jobs.Result` and `EvaluationRuns` fixtures |
| L6 | B → A | Chat writer, feedback endpoint and importer merged, with their configuration names (item 10) | A wires Compose with chat §14.2's names (`OpenWebUi:BaseUrl`, `OpenWebUi__AdminApiKey`) |
| L7 | B → A | Pilot-journey E2E specs and their fixture site in `tests/e2e` (item 14) | A's `e2e` job runs the P0 specs |
| L8 | A → user | Grading script and CSV format (item 16) | the gold set's answers wait |
| L9 | gateway session → B | Gateway release 1 deployed for the pilot (was P1a's J15) | the interim gateway with Eu-only keys and the `x-ume-residency` check (`p1/README.md` §6, §8) |
| L10 | user → A | Test host, backup target and key-escrow location (P0 H6, to-verify #145) | items 21, 22 wait |
| L11 | IT security → A | Self-hosted runner on the test host (register R1) | stopgap of R1: run the R1 test and nightly evaluation manually on the test host by script |
| L12 | user + data protection officer → all | G1 finished: EU processor agreement listed, W2 decided, the public-records route chosen, banner residual risk accepted or not | items 29, 32 wait; if G1 refuses EU processing, the pilot waits (`p1/README.md` §8) |

## 5. Sync points

1. **Start**: each stream takes its first P1b code item once its own P1a code items are done or blocked on a live
   hand-off (Decisions table, "Code first, live testing last"); B on item 1 first, A on item 2 first.
2. **After L1 and L2**: the main session checks the contracts against admin §4.7 and engine §9.1, and the chat schema
   against data-model §2.3.
3. **Code complete, local** (item 19): on FakeGateway, a fixture collection is set up through the journey pages,
   published with a report and rolled back; an assistant draft is tested, published and restored; a rating in the
   local Open WebUI reaches the Feedback page; the pilot journey E2E is green; the R1 harness dry run writes its
   report. The main session settles every deviation in the design.
4. **Live block, in one go** (Decisions table, "Code first, live testing last"), in this order where hand-offs allow:
   J10 (interim gateway) and J11 → U1/J12 → P1a items 19, 20 → P1a item 21 with J13 → P1a item 22 (P1a done) →
   items 21, 22, 23 on the test host → items 24, 25, 26 on the crawled collection → item 27 (L9) → item 28 → items 29,
   30 → item 31 → item 32.
5. **P1b done** (item 32): the main session reviews every deviation and result, ticks P1b in `../progress.md`.

## 6. Owned by the user, outside the streams

Letters continue P1a's U-a…U-h (`p1/README.md` §7), which still hold.

| # | Item | Needed by |
|---|---|---|
| U-i | **G1 (DPIA)** finished with the data protection officer before go-live: Open WebUI's store as the primary copy, the EU processor agreement for embedding, condense and answer, the banner's residual risk (#172 partly refuted: it can be closed until reload), the public-records route (#173), user input | L12, items 29, 32 |
| U-j | **W2** (Open WebUI chat-copy retention) decided within G1 | L12 |
| U-k | **Gold set** (U-f): owner and date; the department confirms the four thresholds and names the reviewers who grade by hand | J13, item 31 |
| U-l | Test host (cheapest possible), backup target, key-escrow location (P0 H6, #145) | L10 |
| U-m | R1: IT security's approval of a self-hosted runner, or acceptance of the manual stopgap | L11 |
| U-n | Gateway session: release 1 deployed with the pilot team's per-level keys (`RequireMaxResidency`); identity §16 questions 1–8 answered before J11 | L9, J11 |
| U-o | **N1**: the user-facing product name, before the pilot | item 32 |
| U-p | The relation to `chat.umea.se` with Team Turkos (U-h, W1) | item 32 |
| U-q | **Owners and dates** for every item blocking go-live (phase-start rule, weakness review §5): G1, W2, N1, U1, R1, A1 (named owner; it no longer blocks the pilot) | before the live block |
| U-r | DOS-lagen scope of the admin app and its tillgänglighetsredogörelse (to-verify #96) | item 14 |
| U-s | Where the pilot runs at go-live (Open O7) | items 21, 32 |

## 7. Settled by the main session (2026-10-31)

Sources conflicted or left these open. The main session took the recommended option for each (the user asked it to
proceed on its best recommendations, 2026-10-31); **O7 stays open for the user** and blocks nothing until go-live. The
user may overturn any of them.

| # | Question | Sources | Recommended |
|---|---|---|---|
| O1 | **Generated evaluation sets need an on-prem model**: `Settings.EvaluationModel` must be servable `OnPrem` with ceiling `OnPrem`, but M2 means no on-prem model exists | processing §13.1; ingestion plan §10 P1b "generated evaluation sets"; Decisions "M2 and test hosting" | **Settled (user, 2026-11-02):** generation and judging run at the collection's ceiling, `Eu` under M2, with no `temperature` (Decisions "Evaluation models at the collection's ceiling"); the gold set stays the headline metric (weakness review §2) |
| O2 | **Share lists in P1b?** Admin design §12 P1b (2) and §5.3 (Delning tab) put share lists in P1; the phases table (P2) and simplification pass §10 put sharing with named departments in P2 | admin design §5.3, §12; `../README.md` P2 row; simplification pass §10 | P2: the pilot has one department; leave the Delning tab out of item 12 |
| O3 | **E1 spike**: to-verify #175 needs a real Entra token ("test (tenant)"), but Entra moved to P2 ("P1 pilot sign-in"); identity §16 Q9 asks whether it runs against the shared Keycloak | weakness review §5; identity §9.4, §16 Q9; postponed E1 | do W3 now (item 18); move E1 to P2 with Entra, since it decides only OBO and group audiences, neither of which the one-department pilot uses |
| O4 | **`evaluation.yml` says "real BGE-M3"**, but the pilot profile is an EU model (M2) | repo-and-deployment §8.1; `p1/README.md` §1 | run it against the test deployment's own profile (the EU model); cost is ≤ 100 queries a night |
| O5 | **The `lastmod` sample needs ≥ 7 days between fetches**, which a single end block cannot contain | weakness review §3 "Sample threshold"; Decisions "Code first, live testing last" (items 1 and 8 may run early) | start the first pass as soon as U1 is done, ahead of the live block, so item 24 can close inside it |
| O6 | **H8 capacity run**: `../progress.md` says P0's open boxes moved to P1b incl. H8; the Decisions table says H8 waits for hardware | `../progress.md` P0 stream A; Decisions "M2 and test hosting" | not a P1b item or gate; run it when hardware exists (with P1a O3's re-embedding) |
| O7 | **Pilot host at go-live**: the decisions name a cheap test host for testing and a pilot-shaped host (8 vCPU / 16 GB) for R1 and the pilot caps, but not whether go-live runs on the test host | Decisions "M2 and test hosting"; repo-and-deployment §6.2, §8.3 | go live on the host where R1 passed, sized as §6.2; the user decides whether that is the test host |
| O8 | **Open WebUI pin before go-live**: v0.12.3 was pinned "until a later release passes the 7-day rule"; the next bump moves the callback to `/oauth/oidc/login/callback`; to-verify #172/#173 read v0.12.5 | Decisions "P0 session 7", "P0 integration, stream A"; identity §5.3 | bump once in item 19, with the callback change in the realm additions (B) and Compose (A) in matching PRs, then re-check #135, #172, #173 at the new pin |
| O9 | **Preview kind name**: admin §4.7 says `node-preview`, engine §9.1 says `stage-preview` | admin design §4.7; engine §9.1 | `stage-preview` (the contract owner's document); B lists the rename as a deviation |
