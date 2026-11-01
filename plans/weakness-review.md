# Weakness review (2026-10-30)

> **Folded into the detail documents on 2026-11-01. Not binding; kept for the reasoning.** Every rule is now stated in the detail document it changes; the ledger at the end lists each item and where it went. Open risks are in `postponed-decisions.md` and `to-verify.md`. Where this text and a detail document or the Decisions table differ, the detail document or the table wins.

A full review of the design after the simplification pass. Three reviewers each read one area in full: platform and
operations; ingestion and retrieval; chat, identity and admin. The main session checked their evidence against the
documents. These are the five most serious weaknesses and their fixes, in decided form (user: "fix each of these").
This document is binding over the detail documents. The Decisions table carries one row per fix.

## 1. P1 builds a platform before anyone sees one good answer

**Weakness.** P1 carried assistant versions with diff, test-question sets, templates, copy and a department map, plus
learned site profiles with auto re-learning, a crawl frontier shared between sources, a nightly answer judge,
publish plans from effect tags, adaptive embedding concurrency and encrypted chat content. That is too much subtle
code for a small team plus agents before the first useful answer.

**Fix.**
- **P1 is split.**
  - **P1a, the walking skeleton.** URL list, umea.se crawl and file upload, each through its one fixed pipeline. The
    umea.se site profile is a **hand-checked Sitevision preset**: a manual profile JSON, versioned like any config.
    Then hybrid retrieval with citations, the chat API, and one assistant in Open WebUI for the pilot department.
    Done when a member of the pilot department asks a question in Open WebUI and gets a cited answer from umea.se
    content.
  - **P1b, pilot hardening.** Everything else that stays in P1: feedback, the go-live gate of §2, the freshness rules
    of §3, the data-protection items of §4, admin journey pages for sources and the assistant.
- **Moved from P1 to P2:**
  - assistant version **diff**, per-assistant **test-question sets**, **templates**, **copy**, the **department map**
    and unused-assistant attention items;
  - **site-profile learning** and automatic re-learning. P1 uses manual profiles with the same schema, so learning
    adds to them later;
  - the **crawl frontier shared between sources** (claims, releaser). In P1 each source has its own frontier;
  - the **nightly answer judge** (`answers` runs);
  - **publish plans from effect tags**. P1 publish re-processes every document of the collection; the per-stage
    caches make unchanged stages free. Publish still produces a report and offers keep or roll back;
  - **adaptive embedding concurrency** (AIMD) and moving-average range sizing. P1 uses fixed values in configuration.
- **Stays in P1:** assistant draft → publish, with history and restore (a cheap safety net for editors). Cross-rules
  apply only to the objects P1 still has.
- **Settled while applying:**
  - **Crawl pacing.** A P1 crawl paces its fetches through its own source `sync`, in batches per host
    (`Crawl:FetchBatchPerHost`), and still takes slots from the shared `CrawlHosts` budget. The P1 `CrawlFrontier`
    key includes `SourceId`.
  - **Effect tags.** Effect tags on source settings stay (impact counts, rule V25, re-sync). Only the publish plan
    built from them moves to P2.
  - **Range sizes.** Fixed parse range sizes are set conservatively from P1 measurements.
  - **P1a set-up.** P1a has no journey pages: the platform team sets up the walking skeleton through Scalar and the
    generic forms.

## 2. Nothing proves the answers are good, and "not found" is effectively off at launch

**Weakness.** The only quality signal was a set of LLM-generated questions per collection, each written from one
chunk. Such questions reward word overlap with the chunk. No pilot success criterion existed. The not-found gate
needs both sides weak, but the keyword floor started as `null` and the OR query matches almost any question.

**Fix.**
- **Pilot gold set (P1a, before go-live).**
  - **Answerable questions:** 100–150 real questions, written with the pilot department from its own FAQ, its
    contact-centre questions and, if W1 allows, the existing umea.se chat. Each has its expected source URL(s) and the
    facts a correct answer contains.
  - **Unanswerable questions:** 30–50 off-topic and near-miss questions whose expected outcome is "not found".
  - **Temporal questions:** at least 10 questions where an outdated document competes (§3).
  - **Storage:** stored as `human` evaluation questions, so the existing harness runs them. Owner: the pilot
    department with the platform team. It is the start of register item B1.
- **Headline metric.** Reports lead with the gold-set results. Generated questions remain a regression signal only.
- **Go-live gate (people, not automatic).** Department reviewers grade the gold-set answers by hand. The pilot goes
  live when every starting threshold below is met. The department confirms the thresholds when the set is written.
  Publish stays advisory as before; this gate applies once, at go-live.

  | Measure | Starting threshold |
  |---|---|
  | Correct answers on answerable questions | ≥ 80 % |
  | Citation precision | ≥ 90 % |
  | False not-found | ≤ 10 % |
  | Off-topic questions answered "not found" | ≥ 80 % |

- **Pilot success** = the gate is met at go-live and still met at the end of the pilot (re-run monthly), and the
  department decides to continue.
- **Not-found calibration.** Until `coverageMinKeywordScore` is calibrated, `low` is decided by the **vector side
  alone**. Both thresholds are set from the gold set's answerable and unanswerable questions together: the value that
  separates them best, not a percentile of answerable questions only.
- **Hybrid has to earn its place.** In P1a the gold set is run three ways: vector-only, hybrid, and hybrid without
  the decompounder. The result is recorded. Hybrid and the decompounder stay only if they beat vector-only.
- **Settled while applying:**
  - **Three-way comparison.** "Without the decompounder" means on both sides: a scratch copy of the collection whose
    `SearchText` is recomputed by `reindex-fts` without compound parts, plus queries without them.
  - **Hand grading.** A script sends each gold-set question through the chat API as the pilot assistant and writes
    question, answer, citations and coverage to a CSV. Reviewers grade it in a spreadsheet, and the graded file is
    attached to the go-live decision. P1 has no grading UI.
  - **P1a scope.** Retrieval evaluation runs over the `human` layer move into P1a. Generated sets stay in P1b.
  - **Threshold proposal.** The threshold proposal is shown as numbers in P1. The histogram stays in P2.
  - **Data model.** `EvaluationRuns.Variant` and per-question coverage fields are added. A `human` question with no
    expected documents means "not found".

## 3. Outdated or removed content keeps being answered

**Weakness.** About 8,000 of umea.se's URLs are PDFs (fee schedules, minutes, older guidance), and ranking had no
recency at all. Documents missing from a crawl were deleted only after a traversal with "no seed failed", so one
failing URL among 14,000 blocked every such deletion. The `lastmod` skip was trusted on evidence from a single page.

**Fix.**
- **Age decay, per collection.** `RetrievalSettings.recency` = `{ halfLifeDays, weight, kinds }`. It is off by
  default. The `website` template turns it on for PDFs (`halfLifeDays` 730, `weight` 0.15). It is applied after
  fusion as a multiplier `1 − weight · (1 − 2^(−age/halfLife))` on `SourceModifiedAt`. The temporal questions of §2
  check it. This replaces "no recency boost" (knowledge plan answer 7); the `modified_after` filter stays.
- **Demote rules.** Next to `include` and `exclude`, URL patterns can carry `demote` (a rank multiplier, default 0.5)
  for archive sections such as old minutes. They are set on the source and shown on the source page.
- **Completeness by ratio.** A traversal is **complete** when ≥ 99 % of seeds are resolved, no host was paused, no
  admitted claim is pending and the page limit was not hit. A seed counts as resolved when it was fetched, returned
  304, was skipped by `lastmod`, or is `Gone`. Documents whose URL is a failed seed of the run are never deletion
  candidates in that run. The deletion guard (mass-deletion stop) stays.
- **Removal check.** A URL that leaves the sitemap is re-requested (HEAD, then GET if HEAD is refused) in the next
  incremental run. A 404 or 410 makes it `Gone` and deletes it in any run. A 200 keeps it until the next complete
  traversal.
- **`lastmod` is measured before it is trusted.** The site profile's `lastmod reliable` flag is set only after a
  sample of 500 random URLs has been fetched and their content hash compared with `lastmod` (`to-verify.md`). Until
  then the umea.se source runs full traversals without the `lastmod` skip.
- **Settled while applying:**
  - **Sample threshold.** The 500-URL sample passes when at most 2 % of the sampled URLs, re-fetched at least 7 days
    apart, changed their main content without a newer `lastmod`.
  - **Unreachable robots.txt.** A host whose robots.txt is unreachable counts as paused.
  - **Demote rules.** Retrieval reads demote rules from the generated column `Sources.DemoteRules`. When several
    patterns match, the lowest factor applies.
  - **Template default.** The `website` template sets `recency` on the collection when its first `website` source is
    added, unless the collection already has a value.
  - **Removal check in full traversals.** The removal check also runs in full traversals, for sources without
    incremental runs.
  - **Phasing.** Decay, demote, completeness by ratio and the removal check are P1b. P1a keeps the stricter deletion
    rule.

## 4. Data protection guards the secondary copy while the primary stays unprotected

**Weakness.** Our chat store was encrypted under bucket keys with reviewer roles and access logs. Meanwhile Open
WebUI keeps every chat and feedback snapshot in plaintext, unpruned. The archive-law question (A1) has no owner.
There is no data-protection impact assessment (DPIA) and no process for public-records requests. User input to an
`internal` assistant may go to EU providers whatever it contains.

**Fix.**
- **History mode `none` in the pilot until A1 is answered.** The pilot assistant stores metadata and ratings only:
  no message content in our store. As a consequence:
  - the **chat bucket keys, content encryption and crypto-shredding** are not built in P1; the data model keeps
    their columns and they are built when A1 allows stored content;
  - **conversation review** by `rag-reviewer` moves with it;
  - blob encryption for `internal` uploads is unchanged.
- **DPIA (new register item G1).** Owner: the user, with the municipality's data protection officer
  (dataskyddsombud). It starts in P0 and is finished before go-live. It covers Open WebUI's own store (its chats and
  feedback snapshots, its volume, who holds its admin role) as the primary copy, decides W2, and records the residual
  risk of user input below.
- **Public-records requests.** The DPIA and the A1 owner decide where a request for a conversation is answered. In
  the pilot that is Open WebUI's copy, because ours holds no content. The runbook gets a section on finding and
  exporting one conversation for the registrar.
- **User input.**
  - Open WebUI shows a fixed banner: "Skriv inte in sekretessbelagda uppgifter eller känsliga personuppgifter."
  - The pilot client's ceiling stays `internal` only for EU providers covered by a processor agreement (PUB-avtal),
    listed in the DPIA. If the DPIA requires more, the pilot client's ceiling drops to `onprem`.
- **A1 gets a named owner before P0 ends** (phase-start rule of §5).
- **Settled while applying:**
  - **A1 no longer blocks the pilot.** It now gates stored chat content: history modes `stored` and `session`, chat
    bucket keys, content encryption, crypto-shredding, conversation review, and the P2 features that store content.
    In P1 every assistant runs `none`.
  - **Feedback.** Comments stay in Open WebUI. Our import keeps ratings and reason categories. 90-day retention
    applies to metadata rows.
  - **Keys.** Chat master keys are neither created nor escrowed in P0 or P1. Blob keys are unchanged.
  - **`rag-reviewer`.** The role stays defined (P0 ships it) but has nothing to read in P1.
  - **The on-prem fallback** is a model choice, not a registry ceiling. The client keeps ceiling `internal`. Every
    assistant offered to it uses on-prem answer and condense models without EU fallbacks.
  - **Export route.** The route for a public-records request is decided within G1 (`to-verify.md` #173): the user's
    own export, a temporary logged admin-access window, or another route. Direct SQL stays ruled out.
  - **Banner.** Open WebUI shows the banner only when a new chat is opened (`to-verify.md` #172).

## 5. The pilot's critical path runs through things the team does not control

**Weakness.** The pilot waited for one combined gateway release (delegation, residency ceiling, member groups, key
flags) in a production gateway that other teams use. On-prem model capacity was only estimated. Open WebUI matches
users by client only, and its process-wide model list (W3) blocks a second department. Several approvals had no
owner.

**Fix.**
- **Gateway release split.**
  - **Release 1, before the pilot:** feature B (residency ceiling, `RequireMaxResidency`, the 503/403 split,
    `/v1/models` filtered by the ceiling, ADR 018) and the FakeLlm `dimensions` addition.
  - **Release 2, in P2:** feature C (member groups from claims, ADR 019) and feature A (delegation, ADR 020), before
    the first second-department or group audience.
  - **Pilot attribution** uses the per-level gateway keys of the pilot department's team. This reverses the "no
    interim per-team keys" rule of simplification pass §7 for the one pilot team. The gateway is unchanged by this.
- **On-prem capacity is measured in P0.**
  - **New checklist item #22:** BGE-M3 embedding throughput, and the latency of the condense and answer models, on
    the real gateway hardware. The `to-verify.md` rows on embedding and model throughput move to P0.
  - **New register item M2:** if capacity falls short, decide before P1 between more on-prem capacity and a longer
    initial-load window. The embedding profile stays on-prem BGE-M3.
- **Open WebUI beyond one department.** The fallback is decided now: **one Open WebUI instance per department**,
  each its own client with its own ceiling and audience, until W3 and E1 are solved. Spikes on E1 and on the W3
  workaround (Open WebUI group permissions) move from P2 to late P1.
- **Owners and dates.** Phase-start rule: every item in `postponed-decisions.md` that blocks the next phase has a
  named person and a date before that phase starts. P0 ends only when A1, G1, W1, W2, N1, U1 and P2 have both.
- **Settled while applying:**
  - **Pilot department scope.** In P1 the pilot department's scope comes from member groups entered by hand on our
    own department record, matched against the `groups` claim. With release 2 the org sync fills the same field.
  - **Release-1 migration.** `RequireMaxResidency` gets its own release-1 gateway migration (`KeyResidencyCeiling`),
    separate from the release-2 delegation migration.
  - **Pilot-team keys.** They are ordinary team keys. The pilot team keeps no request policy. Every P1 gateway call is
    charged to that team. With release 2 we switch to delegating keys and revoke the pilot-team keys.
  - **Resolver.** The member-group resolver is still built in P0 against the FakeGateway.
  - **Capacity measurements (#22).** Embedding throughput, query-embedding p95 under ingestion load, and answer-model
    TTFT p99 for a 16k-token prompt.

## Lower-ranked findings (recorded, not changed here)

- Editors see no question text and no list of unanswered questions. Revisit when A1 allows stored content.
- The pypdfium2 fast path is unproven on two-column pages and on tables without ruling lines. At P1 start, compare it
  page by page with Docling on 200 sampled umea.se PDFs, and set the routing threshold from that.
- A single e-mail address outside `PiiOwnDomains` holds a whole `public` document. Do a personal-data dry run on the
  full umea.se crawl before the pilot.
- The umea.se collection will exceed `exactScanMaxChunks`, so the pilot runs HNSW over `halfvec`. Measure its recall
  against exact search on the pilot collection.


## Ledger (2026-11-01)

Every item of this document, in document order, and where it went. Kinds: *rule* (stated in the detail document named), *risk* (kept in a register), *rationale* (binds nothing), *reference* (stays here). Statuses: *folded* (written into the target), *already there* (the target already stated it), *obsolete* (superseded; the Decisions row or "as built" is named), *not folded* (reason given).

| ID | Pass § | Item | Kind | Status | Target |
|---|---|---|---|---|---|
| W-1 | intro ¶1 | How the review was done: three reviewers, main session checked evidence; five weaknesses in decided form | rationale | rationale: binds nothing | – |
| W-2 | intro ¶1 | "This document is binding over the detail documents; the Decisions table carries one row per fix" | rationale | rationale: binds nothing | – |
| W-3 | §1 Weakness | P1 carried too much subtle code (versions/diff, test sets, learned profiles, shared frontier, judge, effect-tag plans, AIMD, encrypted chat) | rationale | rationale: binds nothing | – |
| W-4 | §1 Fix ¶1 | P1 is split into P1a (walking skeleton) and P1b (pilot hardening) | rule | folded | platform-plan §14 |
| W-5 | §1 Fix P1a | P1a = URL list, umea.se crawl, file upload (one fixed pipeline each), hybrid retrieval with citations, chat API, one assistant in Open WebU… | rule | folded | admin plan.md (phase table) |
| W-6 | §1 Fix P1a | umea.se site profile = hand-checked Sitevision preset, a manual profile JSON versioned like config | rule | already there | connectors §5.6 |
| W-7 | §1 Fix P1b | P1b = feedback, go-live gate (§2), freshness rules (§3), data-protection items (§4), admin journey pages for sources and assistant | rule | already there | admin plan.md |
| W-8 | §1 Moved to P2 (1) | Assistant version diff, per-assistant test-question sets, templates, copy, department map, unused-assistant attention items → P2 | rule | already there (platform-plan §9, §14, admin §17, admin plan.md §8); folded (data-model §2.1) | admin plan.md §8, admin §17, data-model §2.1, platform-plan §9, §14 |
| W-9 | §1 Moved to P2 (2) | Site-profile learning and automatic re-learning → P2; P1 uses manual profiles with the same schema | rule | already there | connectors §5.6, plan §13 |
| W-10 | §1 Moved to P2 (3) | Crawl frontier shared between sources (claims, releaser) → P2; P1 one frontier per source | rule | already there (connectors §6.0); folded (data-model §2.2) | connectors §6.0, data-model §2.2 |
| W-11 | §1 Moved to P2 (4) | Nightly answer judge (`answers` runs) → P2 | rule | already there | chat §18.8, platform-plan §9, processing §14.8 |
| W-12 | §1 Moved to P2 (5) | Publish plans from effect tags → P2; P1 publish re-processes every document, per-stage caches make unchanged stages free, report + keep/rol… | rule | folded | engine §12.2, processing §14.1 |
| W-13 | §1 Moved to P2 (6) | Adaptive embedding concurrency (AIMD) and moving-average range sizing → P2; P1 uses fixed config values | rule | folded (engine §18); already there (processing §3.3) | engine §18, processing §3.3 |
| W-14 | §1 Stays in P1 | Assistant draft → publish with history and restore stays in P1; cross-rules apply only to objects P1 still has | rule | folded (data-model §2.1); already there (admin §17 (and the assistant section)) | admin §17 (and the assistant section), data-model §2.1 |
| W-15 | §1 Settled: crawl pacing | P1 crawl paces fetches through its own source `sync` in per-host batches (`Crawl:FetchBatchPerHost`), still taking slots from shared `Crawl… | rule | already there | connectors §6.0 |
| W-16 | §1 Settled: crawl pacing | P1 `CrawlFrontier` key includes `SourceId` | rule | already there (connectors §6.0); folded (data-model §2.2) | connectors §6.0, data-model §2.2 |
| W-17 | §1 Settled: effect tags | Effect tags on source settings stay (impact counts, rule V25, re-sync); only the publish plan built from them moves to P2 | rule | folded (engine §3.2, engine §12.2); already there (connectors §12.3) | connectors §12.3, engine §12.2, engine §3.2 |
| W-18 | §1 Settled: range sizes | Fixed parse range sizes are set conservatively from P1 measurements | rule | folded | processing §3.3 |
| W-19 | §1 Settled: P1a set-up | P1a has no journey pages; the platform team sets up through Scalar and the generic forms | rule | folded | admin plan.md, admin §17 |
| W-20 | §2 Weakness | Only LLM-generated one-chunk questions; no pilot success criterion; keyword floor `null` makes not-found effectively off | rationale | rationale: binds nothing | – |
| W-21 | §2 Fix: gold set (answerable) | 100–150 real answerable questions from the department's FAQ, contact-centre questions and (if W1 allows) the umea.se chat; each with expect… | rule | folded | platform-plan §9, processing §13.4 |
| W-22 | §2 Fix: gold set (unanswerable) | 30–50 off-topic and near-miss questions, expected outcome "not found" | rule | already there | processing §13.4 |
| W-23 | §2 Fix: gold set (temporal) | ≥ 10 temporal questions where an outdated document competes | rule | already there | processing §13.4 |
| W-24 | §2 Fix: gold set (storage, owner) | Stored as `human` evaluation questions run by the existing harness; owner pilot department with platform team; start of register B1 | rule | folded | processing §13.4 |
| W-25 | §2 Fix: headline metric | Reports lead with gold-set results; generated questions are a regression signal only | rule | already there | knowledge §16.4, platform-plan §9, processing §13.4 |
| W-26 | §2 Fix: go-live gate | Go-live gate by people: department reviewers hand-grade gold-set answers; department confirms thresholds when the set is written; applies o… | rule | already there | platform-plan §9 |
| W-27 | §2 Fix: thresholds | Starting thresholds: correct ≥ 80 %, citation precision ≥ 90 %, false not-found ≤ 10 %, off-topic not-found ≥ 80 % | rule | already there | platform-plan §9 |
| W-28 | §2 Fix: pilot success | Pilot success = gate met at go-live and at end of pilot (re-run monthly) and department decides to continue | rule | already there | platform-plan §9 |
| W-29 | §2 Fix: not-found calibration | Until `coverageMinKeywordScore` is calibrated, `low` is decided by the vector side alone; both thresholds set at best separation of answera… | rule | folded | chat §6.2, knowledge §7 |
| W-30 | §2 Fix: hybrid earns its place | P1a gold set run three ways (vector-only, hybrid, hybrid without decompounder); recorded; hybrid and decompounder stay only if they beat ve… | rule | already there (processing §13.4); folded (knowledge plan.md §3) | knowledge plan.md §3, processing §13.4 |
| W-31 | §2 Settled: three-way | "Without the decompounder" on both sides: scratch copy with `SearchText` recomputed by `reindex-fts` without compound parts, plus queries w… | rule | obsolete: as built | processing §13.4 |
| W-32 | §2 Settled: hand grading | A script sends each gold-set question through the chat API as the pilot assistant and writes question, answer, citations, coverage to CSV;… | rule | folded | platform-plan §9, processing §14 |
| W-33 | §2 Settled: P1a scope | Retrieval evaluation over the `human` layer moves into P1a; generated sets stay in P1b | rule | already there (plan §7, processing §14); folded (processing §13.1) | plan §7, processing §13.1, processing §14 |
| W-34 | §2 Settled: threshold proposal | Threshold proposal shown as numbers in P1; histogram stays P2 | rule | already there | admin plan.md, admin §7.1, §16 |
| W-35 | §2 Settled: data model | `EvaluationRuns.Variant` and per-question coverage fields added; a `human` question with no expected documents means "not found" | rule | already there | data-model §2.2 |
| W-36 | §3 Weakness | ~8,000 PDFs, no recency in ranking; "no seed failed" blocked deletions; `lastmod` trusted on one page | rationale | rationale: binds nothing | – |
| W-37 | §3 Fix: age decay | `RetrievalSettings.recency` = `{halfLifeDays, weight, kinds}`, off by default; after fusion multiplier `1 − weight·(1 − 2^(−age/halfLife))`… | rule | folded | data-model §2.1, knowledge §12.1, knowledge §6.5 |
| W-38 | §3 Fix: age decay (template) | `website` template turns recency on for PDFs: `halfLifeDays` 730, `weight` 0.15 | rule | already there | engine §13.1 |
| W-39 | §3 Fix: demote rules | URL patterns may carry `demote` (rank multiplier, default 0.5) next to include/exclude; set on the source and shown on the source page | rule | already there | admin §5.4 (source page), connectors §5.2 |
| W-40 | §3 Fix: completeness by ratio | Traversal complete when ≥ 99 % seeds resolved, no host paused, no admitted claim pending, page limit not hit; resolved = fetched, 304, `las… | rule | already there (connectors §5.4); folded (data-model §2.2) | connectors §5.4, data-model §2.2 |
| W-41 | §3 Fix: failed seeds; guard | Documents whose URL is a failed seed of the run are never deletion candidates in that run; the mass-deletion guard stays | rule | already there | connectors §13.2, connectors §5.4 |
| W-42 | §3 Fix: removal check | URL leaving the sitemap is re-requested (HEAD, then GET if refused) in the next incremental run; 404/410 → `Gone`, deleted in any run; 200… | rule | already there | connectors §5.4 |
| W-43 | §3 Fix: `lastmod` measured | Site profile's `lastmod reliable` flag set only after 500 random URLs fetched and content hash compared with `lastmod`; until then umea.se… | rule | already there | connectors §5.4, connectors §5.6 |
| W-44 | §3 Settled: sample threshold | Sample passes when ≤ 2 % of URLs, re-fetched ≥ 7 days apart, changed main content without a newer `lastmod` | rule | folded | connectors §5.4 |
| W-45 | §3 Settled: unreachable robots.txt | A host whose robots.txt is unreachable counts as paused | rule | already there (connectors §5.1); folded (connectors §5.4) | connectors §5.1, connectors §5.4 |
| W-46 | §3 Settled: demote rules | Retrieval reads demote rules from generated column `Sources.DemoteRules`; when several patterns match, the lowest factor applies | rule | folded | data-model §2.1, knowledge §6.5 |
| W-47 | §3 Settled: template default | `website` template sets `recency` on the collection when its first `website` source is added, unless it already has a value | rule | already there (engine §13.1); folded (knowledge §6.5) | engine §13.1, knowledge §6.5 |
| W-48 | §3 Settled: removal check in full traversals | Removal check also runs in full traversals, for sources without incremental runs | rule | already there | connectors §5.4 |
| W-49 | §3 Settled: phasing | Decay, demote, completeness by ratio and removal check are P1b; P1a keeps the stricter deletion rule | rule | folded | connectors §5.4 |
| W-50 | §4 Weakness | Our store encrypted while Open WebUI keeps plaintext; A1 has no owner; no DPIA; no public-records process; internal input to EU providers | rationale | rationale: binds nothing | – |
| W-51 | §4 Fix: history mode `none` | Pilot assistant runs history mode `none` until A1 is answered: metadata and ratings only, no message content | rule | already there (platform-plan §8a, chat §3.5, data-model §2.1); folded (chat §11.2) | chat §11.2, chat §3.5, data-model §2.1, platform-plan §8a |
| W-52 | §4 Fix (a) | Chat bucket keys, content encryption and crypto-shredding not built in P1; data model keeps their columns | rule | already there | data-model §7, platform-plan §8a |
| W-53 | §4 Fix (b) | Conversation review by `rag-reviewer` moves with it | rule | already there | admin §3.1, §8, platform-plan §8a |
| W-54 | §4 Fix (c) | Blob encryption for `internal` uploads unchanged | rule | already there | platform-plan §8a |
| W-55 | §4 Fix: DPIA | New register item G1: owner the user with the DPO; starts P0, finished before go-live; covers Open WebUI's store as primary copy, decides W… | risk | n/a (risk) | platform-plan §8 |
| W-56 | §4 Fix: public-records requests | DPIA and A1 owner decide where a request is answered; in the pilot Open WebUI's copy; runbook gets a section on finding and exporting one c… | rule | already there | chat §13.3, platform-plan §8, repo-and-deployment §11 (#19) |
| W-57 | §4 Fix: user input banner | Open WebUI shows a fixed banner "Skriv inte in sekretessbelagda uppgifter eller känsliga personuppgifter." | rule | already there | chat §4.3, platform-plan §8a |
| W-58 | §4 Fix: pilot ceiling | Pilot client ceiling stays `internal` only for EU providers under a PUB-avtal listed in the DPIA; else it drops to `onprem` | rule | already there (platform-plan §8a); folded (identity §4.1) | identity §4.1, platform-plan §8a |
| W-59 | §4 Fix: A1 owner | A1 gets a named owner before P0 ends | risk | registered: `postponed-decisions.md` A1 and "Not met" note | – |
| W-60 | §4 Settled: A1 scope | A1 no longer blocks the pilot; it gates stored content (modes `stored`/`session`, bucket keys, encryption, shredding, review, P2 content fe… | rule | already there | platform-plan §8, §8a |
| W-61 | §4 Settled: feedback | Comments stay in Open WebUI; our import keeps ratings and reason categories; 90-day retention on metadata rows | rule | already there | admin §8.1, chat §3.5, platform-plan §8 |
| W-62 | §4 Settled: keys | Chat master keys neither created nor escrowed in P0/P1; blob keys unchanged | rule | already there | data-model §7, platform-plan §8a, repo-and-deployment §6.3 |
| W-63 | §4 Settled: `rag-reviewer` | Role stays defined (P0 ships it) but has nothing to read in P1 | rule | already there | admin §3.1, identity §1.3 |
| W-64 | §4 Settled: on-prem fallback | On-prem fallback is a model choice, not a registry ceiling: client keeps `internal`, assistants use on-prem answer/condense models without… | rule | already there (platform-plan §8a); folded (identity §4.1) | identity §4.1, platform-plan §8a |
| W-65 | §4 Settled: export route (rule part) | Direct SQL against Open WebUI's database stays ruled out | rule | already there | chat §13.3 |
| W-66 | §4 Settled: export route (open part) | Route for a public-records request decided within G1: user's own export, temporary logged admin-access window, or another route | risk | registered: to-verify #173 and postponed W2/G1. | – |
| W-67 | §4 Settled: banner | Open WebUI shows the banner only when a new chat is opened | rule | folded | chat §4.3 |
| W-68 | §5 Weakness | Pilot waited on one combined gateway release; on-prem capacity estimated; Open WebUI matches by client only (W3); approvals without owners | rationale | rationale: binds nothing | – |
| W-69 | §5 Fix: release 1 | Gateway release 1 before the pilot: feature B (ceiling, `RequireMaxResidency`, 503/403 split, `/v1/models` filtered, ADR 018) and FakeLlm `… | rule | already there (platform-plan §7, repo-and-deployment §11 (#18)); folded (gateway §1, §3, §13) | gateway §1, §3, §13, platform-plan §7, repo-and-deployment §11 (#18) |
| W-70 | §5 Fix: release 2 | Release 2 in P2: feature C (member groups, ADR 019) and feature A (delegation, ADR 020), before the first second-department or group audien… | rule | already there (platform-plan §7); folded (gateway §1, §13.1) | gateway §1, §13.1, platform-plan §7 |
| W-71 | §5 Fix: pilot attribution | Pilot attribution uses the pilot department team's per-level gateway keys; reverses simplification §7 "no interim per-team keys" for that t… | rule | folded (platform-plan §7); already there (gateway §13.4) | gateway §13.4, platform-plan §7 |
| W-72 | §5 Fix: capacity in P0 | On-prem capacity measured in P0: checklist #22 (BGE-M3 throughput, condense/answer latency on real hardware); to-verify embedding/model thr… | rule | obsolete: M2 and test hosting | repo-and-deployment §11 (#22) |
| W-73 | §5 Fix: register M2 | If capacity falls short, decide before P1 between more on-prem capacity and a longer initial-load window | risk | registered: postponed-decisions M2 (marked triggered). | – |
| W-74 | §5 Fix: embedding profile | The embedding profile stays on-prem BGE-M3 | rule | folded | data-model §2.1, platform-plan §12 |
| W-75 | §5 Fix: Open WebUI per department | Fallback: one Open WebUI instance per department, each its own client with ceiling and audience, until W3 and E1 are solved | rule | already there | chat §4, identity §5.2, §9.4 |
| W-76 | §5 Fix: spikes | Spikes on E1 and the W3 workaround (Open WebUI group permissions) move from P2 to late P1 | risk | registered: postponed W3, E1 (E1 moved to P2, Decisions "P1b briefs"), `to-verify.md` #174, #175 | – |
| W-77 | §5 Fix: phase-start rule | Every postponed item that blocks the next phase has a named person and date before that phase starts; P0 ends only when A1, G1, W1, W2, N1,… | rule | already there | repo-and-deployment §11 |
| W-78 | §5 Fix: phase-start rule (state) | Owners and dates for A1, N1, U1, W2 still missing although P0 is finished | risk | registered: `postponed-decisions.md` "Not met" note (A1, N1, U1, W2 need persons and dates) | – |
| W-79 | §5 Settled: pilot department scope | In P1 pilot department scope comes from member groups entered by hand on our department record, matched against `groups`; with release 2 th… | rule | folded | identity §2 |
| W-80 | §5 Settled: release-1 migration | `RequireMaxResidency` gets its own release-1 gateway migration `KeyResidencyCeiling`, separate from the release-2 delegation migration | rule | already there | gateway §8.1, §13.2 |
| W-81 | §5 Settled: pilot-team keys | Ordinary team keys, no request policy; every P1 gateway call charged to that team; with release 2 switch to delegating keys and revoke them | rule | already there | gateway §13.4, platform-plan §7 |
| W-82 | §5 Settled: resolver | Member-group resolver still built in P0 against the FakeGateway | rule | already there | identity §2 |
| W-83 | §5 Settled: capacity measurements | #22 measures embedding throughput, query-embedding p95 under ingestion load, answer-model TTFT p99 for a 16k-token prompt | rule | folded | repo-and-deployment §11 (#22) |
| W-84 | Lower-ranked (1) | Editors see no question text and no list of unanswered questions; revisit when A1 allows stored content | risk | registered: `postponed-decisions.md` A1 (editors see no question text until stored content is allowed) | – |
| W-85 | Lower-ranked (2) | pypdfium2 fast path unproven on two-column pages and tables without ruling lines; compare page by page with Docling on 200 sampled umea.se… | risk | n/a (risk) | processing §3.1 |
| W-86 | Lower-ranked (3) | A single e-mail outside `PiiOwnDomains` holds a whole `public` document; personal-data dry run on the full umea.se crawl before the pilot | risk | n/a (risk) | processing §9.5 |
| W-87 | Lower-ranked (4) | umea.se collection exceeds `exactScanMaxChunks`, so the pilot runs HNSW over `halfvec`; measure its recall against exact search on the pilo… | risk | n/a (risk) | knowledge §6.4 |
