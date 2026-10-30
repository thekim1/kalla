# Stream A brief: the worker side (P1a)

You build the ingestion side of the walking skeleton: contracts, parser formats, connectors, processing stages, the
Sitevision preset, the engine and index writer, evaluation runs, the pilot Compose and CI. Stream B builds the
serving side and owns the schema, the admin API and every admin page. You start at J1 (`README.md` §4).

## Read first

1. [`../../AGENTS.md`](../../AGENTS.md): working rules.
2. [`README.md`](README.md): what changed since the design (§1, EU-hosted models), ownership, the checklist (§3, binding
   done-when), hand-offs, sync points, rules.
3. [`../weakness-review.md`](../weakness-review.md) §1–§3 and [`../simplification-pass.md`](../simplification-pass.md)
   §1, §2, §13.
4. [`../ingestion-worker/plan.md`](../ingestion-worker/plan.md) §10 (P1a row) and §12–§13, then the three designs
   (`design-engine.md`, `design-connectors.md`, `design-processing.md`) and [`../parser/design.md`](../parser/design.md).
   Each ends with "Deferred or removed": nothing listed there is P1a.

## Items, in order

| Order | §3 # | Item (your part) | Read | `to-verify.md` rows |
|---|---|---|---|---|
| 1 | 1 | Pilot-start checks: sitemap inventory; the 500-URL `lastmod` sample's first pass after **J12** | connectors §5.4, §8.1 | 139, 170 (started) |
| 2 | 4 | **First, because stream B waits on it (J3).** Contracts: source settings schemas, pipeline definition schema and the three templates, job payloads and results, site-profile schema; validator; `Ume.Rag.Pipelines` | engine §3, §4, §9.1, §13.1; connectors §2.1, §12.3; admin §4.7 | 17 |
| 3 | 5 | Parser P1 formats, `/v1/inspect`, ranges, `/v1/ner`, OCR acceptance, text-layer vs Docling on 200 umea.se PDFs | parser §3, §6–§10, §16 | 79, 80, 110, 131, 132 |
| 4 | 8 | Sitevision preset and the hand-checked umea.se profile; regression corpus; acceptance page | connectors §5.6; processing §17; simplification pass §13 | 31, 138, 158 |
| 5 | 6 | Connectors: canonicaliser, egress guard, robots and sitemaps, URL list, crawl (one frontier per source, full traversals, stricter deletion rule), uploads, ClamAV client, change detection, deletion guard | connectors §3–§7, §9, §11–§13 | 10, 27, 28, 81, 112 |
| 6 | 7 | Processing stages through `embed` (profile's model, dimensions and `Eu` ceiling; canary) | processing §2–§11; README §1 | 26, 43, 111 |
| 7 | 9 | Engine, runs, scheduler, index writer, blob-tombstone sweep, #41 (**after J2**) → **J4** | engine §2–§10, §14, §16, §19; data-model §9 | 21, 119, 141 |
| 8 | 17 | `evaluation-smoke` job in `verify.yml` (B writes the smoke set) | repo-and-deployment §8.2 | – |
| 9 | 10 | Commands (`edit-site-profile`, `rollback-site-profile`, `edit-eval-question`) and `eval` `on-demand` in three variants (**after J5**) → **J6** | processing §13.4–§13.6; data-model §3.2 | – |
| 10 | 18 | Pilot stack: Compose and Open WebUI wiring, shared Keycloak, gateway `Eu` key, demo-host run book (**after J8, J9**; host after J14) | repo-and-deployment §6, §7.1; chat §4.3 | 92 |
| 11 | 19 | EU-route capacity and the umea.se initial-load estimate (**after J10**) | performance pass §3.9 | – |
| 12 | 20 | umea.se set-up and full crawl with the user (**after J7, J10, J12**) | admin §12; connectors §5.5 | 94 |
| 13 | 21 | Three-way run of the gold set (**after J13**) with stream B and the user | weakness review §2 | 168 |
| 14 | 22 | P1a done, with stream B and the user | README §3 | – |

If an item is blocked on a hand-off, start the next one and come back later.

**Not in P1a** (P1b or later; README §6): preview, publish and its report, completeness by ratio, the removal check,
`lastmod` skips and incremental runs (until the sample passes), generated evaluation sets, nightly runs, `perf.yml`,
`evaluation.yml`, the test-host deploy and restore drill, site-profile learning, the shared frontier, AIMD.

## You provide, you consume

- **Provide:** J3 (contracts, before any other code), J4 (a source syncs into live chunks), J6 (commands and
  `on-demand` runs applied). Announce each with an issue labelled `stream-b`.
- **Consume:** J2 (schema; table or column needs go to B as issues naming the data-model section), J5 (`RetrievalEngine`
  for the evaluator), J7 (Scalar set-up), J8 and J9 (realm additions, chat API) for the stack, J10–J12 and J14 from
  the user and the gateway session.
- **Paths:** `Ume.Rag.Retrieval` (decompounder, retrieval engine) is B's: use it, request changes by issue. Put your
  database tests in `Ume.Rag.Worker.Tests` or `Ume.Rag.Pipelines.Tests` on `TestPostgres`.

## Finished when

Every row above meets its §3 done-when with evidence in its PR, and every listed `to-verify.md` row is recorded in
`results-a.md`. The umea.se crawl report is reviewed, the three-way result is recorded, and a pilot member's cited
answer has been shown locally, with the pilot member present.
