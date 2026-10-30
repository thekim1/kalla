# Weakness review (2026-10-30)

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

## Lower-ranked findings (recorded, not changed here)

- Editors see no question text and no list of unanswered questions. Revisit when A1 allows stored content.
- The pypdfium2 fast path is unproven on two-column pages and on tables without ruling lines. At P1 start, compare it
  page by page with Docling on 200 sampled umea.se PDFs, and set the routing threshold from that.
- A single e-mail address outside `PiiOwnDomains` holds a whole `public` document. Do a personal-data dry run on the
  full umea.se crawl before the pilot.
- The umea.se collection will exceed `exactScanMaxChunks`, so the pilot runs HNSW over `halfvec`. Measure its recall
  against exact search on the pilot collection.
