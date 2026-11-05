# Stream A brief: the worker side (P1b)

You harden the ingestion side for the pilot: the P1b job contracts, the freshness rules in the crawler, preview,
publish and rollback, the advisory evaluation harness, the remaining commands, the CI workflows and harnesses
(`perf.yml`, `evaluation.yml`, `e2e`), the gold-set grading script, the pilot stack and the test host. Stream B owns
the schema, the admin API and every admin page. You start P1b once your P1a **code** items are done or blocked on a
live hand-off (Decisions table, "Code first, live testing last"); the live items wait for the end block
(`README.md` §5 step 4).

## Read first

1. [`../../../AGENTS.md`](../../../AGENTS.md): working rules.
2. [`README.md`](README.md): what P1b is (§1, code vs live), ownership, the checklist (§3, binding done-when),
   hand-offs, sync points, open questions (§7).
3. [`../passes/weakness-review.md`](../passes/weakness-review.md) §1–§3 and §5, and [`../passes/simplification-pass.md`](../passes/simplification-pass.md)
   §1, §2, §8.
4. [`../area-plans/ingestion-worker.md`](../area-plans/ingestion-worker.md) §7, §10 (P1b row), §12–§13, then
   [`design-engine.md`](../../ingestion-worker/design-engine.md) §9.1, §11, §12, §14, §19;
   [`design-connectors.md`](../../ingestion-worker/design-connectors.md) §5.4–§5.6, §13, §18–§19;
   [`design-processing.md`](../../ingestion-worker/design-processing.md) §3.5, §13, §14, §17.
5. [`../../platform/repo-and-deployment.md`](../../platform/repo-and-deployment.md) §6.2, §7, §8 for CI, Compose and the
   test host. Each design ends with "Deferred or removed": nothing listed there is P1b.

## Items, in order

| Order | §3 # | Kind | Item (your part) | Read | `to-verify.md` rows |
|---|---|---|---|---|---|
| 1 | 2 | code | **First, because stream B waits on it (L1).** Contracts part 2: P1b job kinds, source settings additions, the `website` template's `recency` default | engine §9.1, §11, §12; admin §4.7; connectors §5.4; processing §13, §14 | – |
| 2 | 4 | code | Freshness in the crawler: completeness by ratio, removal check, `demote`, incomplete-traversal alert, gated `lastmod` skip and incremental runs, `revalidateEvery`, report sections, the sample's comparison (database tests **after L2**) | weakness review §3; connectors §5.4–§5.6, §18 | – |
| 3 | 5 | code | Preview and preflight | engine §11; admin §4.7, §6.4 | – |
| 4 | 6 | code | Publish, re-process, rollback | engine §12, §14 | – |
| 5 | 7 | code | Evaluation harness: `publish-report`, `nightly` with the alert, `on-demand` with `CandidateSettings`, generated sets (README §7 O1) → **L5** with items 3, 4, 6 | processing §13, §14 | – |
| 6 | 8 | code | Remaining commands and admin parse rules (worker side) | admin §4.7; processing §3.5, §9.5; connectors §13 | – |
| 7 | 16 | code | Gold-set grading script → **L8** | weakness review §2; ingestion plan §7 | – |
| 8 | 15 | code | `perf.yml` and `benchmarks/release-test/`, `evaluation.yml`, the `e2e` job (**after L7**) | repo-and-deployment §8.1–§8.3; chat §16.2 | – |
| 9 | 17 | code | Pilot stack for P1b: feedback service account and key restrictions, importer settings (**after L6**), test-host Compose and run book, runbook's public-records routes | chat §4.3, §13.3, §14.2; repo-and-deployment §6.2, §7 | 49 |
| 10 | 37 | code | Topics in the worker (**after L13, L14**) | processing §7, §13; engine §11 | – |
| 11 | 38 | code | Platform forms for organisation (`platform/**`) | admin §5.8 | – |
| 12 | 19 | code | Code complete; your part of the go-live pin review (images, PyPI, Compose; Open WebUI pin per README §7 O8) | Decisions "7-day rule in development" | – |
| 13 | 20 | live | P1a's live items, with stream B and the user | `../p1/README.md` §3 | as listed there |
| 14 | 21 | live | Test-host deploy and restore drill (**after L10**) | P0 progress-a §5, §12; repo-and-deployment §7.4 | 90, 123, 129 |
| 15 | 22 | live | R1 on the test host; pilot CPU caps re-set (**after L11** or its stopgap) | repo-and-deployment §6.2, §8.3 | 100, 101, 106 |
| 16 | 23 | live | Nightly evaluation on the test deployment | processing §14.8 | 44 |
| 17 | 24 | live | `lastmod` sample result | weakness review §3 | 170 |
| 18 | 25 | live | With stream B: decay and `demote` on the temporal questions (you run the evaluator) | weakness review §3 | (171 in B's results) |
| 19 | 26 | live | Personal-data dry run on the full umea.se crawl | weakness review "Lower-ranked findings" | – |
| 20 | 28 | live | With stream B: feedback import live (Compose and the service account) | chat §14.2 | (49, 135 in B's results) |
| 21 | 29 | live | Banner and public-records route; runbook section final (**after L12**) | weakness review §4; chat §13.3 | 172, 173 |
| 22 | 31, 32 | live | Go-live gate and pilot live, with stream B and the user | README §1.1 | – |

If an item is blocked on a hand-off, start the next one and come back later. Start the `lastmod` sample's first pass
as soon as U1 is done (README §7 O5).

**Not in P1b** (P2 or later; weakness review §1, ingestion plan §13): site-profile learning and re-learning, the shared
crawl frontier, publish plans from effect tags (P1b publish re-processes every document), AIMD and moving-average range
sizing, the nightly answer judge (`answers` runs), feedback-derived questions, re-embedding onto on-prem BGE-M3 (no
hardware), the H8 capacity run (waits for hardware), `scale.yml`, `flake-hunt.yml`, parser fuzzing, `deploy-e2e.yml`.

## You provide, you consume

- **Provide:** L1 (contracts, before any other P1b code), L5 (preview, publish, evaluation runs and commands applied),
  L8 (grading script, to the user). Announce each with an issue labelled `stream-b` (L8: to the user).
- **Consume:** L2 (chat schema and freshness columns; column needs go to B as issues naming the data-model section),
  L3 (`RetrievalEngine` with decay and `demote`, for the evaluator), L4 (admin endpoints enqueue your kinds), L6 (chat
  importer settings), L7 (E2E specs for the `e2e` job); L10 and L11 from the user and IT security; J10–J13 from P1a.
- **Paths:** `Ume.Rag.Retrieval` is B's: use it, request changes by issue. Your new paths are
  `benchmarks/release-test/**` (README §2); the static page copy for R1 lives under `tests/fixtures/` or
  `benchmarks/release-test/`. Database tests stay in `Ume.Rag.Worker.Tests` or `Ume.Rag.Pipelines.Tests` on
  `TestPostgres`.

## Finished when

Every row above meets its §3 done-when with evidence in its PR, and every listed `to-verify.md` row is recorded in
`../../work/results-a.md`. R1 passed on the test host, the restore drill met RPO and RTO there, the `lastmod` sample has a
result, and the graded gold-set answers met the go-live gate.
