# Handover

State of the Ume.Rag work for the next session. Rewritten 2026-10-31, end of session 9.
Repo `~/research/rag`, branch `main`, remote `origin` = https://github.com/thekim1/ume-rag-platform (push after
every commit).

## What this is

**Ume.Rag** (working name, N1) is a RAG / crawling / chatbot platform for Umeå kommun: small .NET 10 services on top
of the team's **LLM gateway** (`~/git/llmgateway`, https://github.com/thekim1/llmgateway). The user owns the gateway
and wants to do better than **Eneo** (`~/git/eneo`), a slow "kitchen sink" monolith. Design and code live in this repo.

**Where we are:** the design is complete. **P0 is done and released as `v0.1.0`** (2026-10-31,
https://github.com/thekim1/ume-rag-platform/releases/tag/v0.1.0). **P1 is being coded "code first, live testing
last"** (user, 2026-10-31): every P1a and P1b code item is built on FakeGateway, fixtures and the dev Keycloak; the
live parts (real gateway, shared Keycloak, umea.se crawl, gold set, pilot member, test host) come as one block at the
end. Stream A's P1a code is nearly complete; stream B (remote) is on its P1a schema and API items.

Stakeholder overview: `docs/stakeholder-report.html`, also a private artifact
https://claude.ai/artifact/GzKzra4RwKdf7xEWFc4EAM (not updated since session 8; refresh when P1a code is done).

## Reading order

1. `01-general-idea.md` §10 **Decisions table** (later rows win). The 2026-10-31 rows matter most now: "7-day rule
   in development" (suspended until the first production deployment), "Licence" (EUPL-1.2, gateway too), "Interim
   gateway for P1a", "Low CPU during development" (base Compose caps ≤ 2), "Code first, live testing last",
   "P1b briefs", "P0 integration, stream A/B".
2. `plans/weakness-review.md` (binding), then `plans/simplification-pass.md`.
3. **`plans/p1/README.md` §1a** (what changed 2026-10-31), §3 (P1a checklist, binding done-when), §4 (hand-offs J1–J15);
   **`plans/p1b/README.md`** (32 items: 19 code, 13 live; hand-offs L1–L12; §7 settled options, **O7 open**).
4. Progress: `plans/progress.md`, `plans/p1/progress-{a,b}.md`, `plans/p1b/progress-{a,b}.md`; results
   `plans/p1/results-{a,b}.md`.
5. The detail document for the area (`plans/*/design*.md`; map in `plans/README.md`). They were brought in line with
   the code at the J2/J3 sync point (`4b0f3a6`).

## How we work (user preferences, binding)

- **Autonomy:** the user asked the main session to work autonomously on its best recommendations and to **merge PRs
  that are obviously ready** (green CI, reviewed) without asking. Decisions, gate relaxations and anything doubtful
  still go to the user (question tool, recommended option first; record answers in the Decisions table).
- **Stream A** runs through `implementor` subagents, each in its own worktree (`git worktree add ../rag-aN -b
  a/<item>-<slug> origin/main`), **up to ~4 in parallel on disjoint paths**. Brief each fully (it sees nothing
  else): goal, files to read, scope, binding done-when, proofs incl. a **negative proof**, "run the suites under
  `taskset -c 0,1`" (CI has 2 vCPUs), ownership check, report + PR body. **Implementors never run git writes**: the
  main session commits, merges `origin/main` into the branch, re-runs build + suites, pushes, opens the PR, merges
  when green, then `git worktree prune`. Send heads-ups to running agents when a merge changes an API they call.
- **Stream B** is a remote agent (GitHub `thekim1`). Talk **only through GitHub** (issues labelled `stream-b`,
  `stream-a`, `main`; PR comments). Check GitHub before each new piece of work. The main session reviews B's PRs and
  answers its issues; **B merges its own PRs**. Note: everything the main session files appears under the user's
  account (`Crotalus`).
- **Shared files** (`Directory.Packages.props`, `README.md`, `CHANGELOG.md`, `UmeRag.slnx`, `.gitignore`): own small
  PR, merged first. **Design documents** (`plans/**`, `*.md` at the root, `.github/stream-ownership.txt`) are the
  main session's: commit directly to `main` (or a plain-named branch, never `main/…`).
- Verify claims against real sources (packages downloaded into the scratchpad and read, never executed; umea.se only
  politely, `UmeaKommunBot/1.0`, robots.txt first, a handful of requests). Re-read a file before editing (an editor
  formatter realigns tables). Commit + push after each reviewed step.
- **Lessons (sessions 7–9):**
  - CI's shellcheck is older than 0.11: write `if …; then …; fi`, never `A && B || C` (SC2015 failed CI twice);
    check with `koalaman/shellcheck:v0.9.0`.
  - The pooler-safe SQL scan (IntegrationTests) treats any string literal starting with "Set " as session SQL.
  - Branch carrying an unsquashed copy of a squash-merged PR → don't merge `main` into it; recreate the branch from
    `origin/main` and apply only the new diff (`git diff <base> HEAD | git apply --3way`).
  - `results-a.md` / `progress-*.md` conflict on parallel PRs: keep both blocks (`main`'s first).
  - Parser contract tests share one container (`ParserImageTests` collection) and wait for readiness; a slot recycles
    on RSS growth (to-verify #65 open).
  - `gh pr merge --admin` is refused (branch-protection bypass); a plain squash merge works.
  - Waiting: no chained `sleep`; use an `until …; do sleep 30; done` loop in one call, or `run_in_background`.
  - The 5-hour usage limit can stop agents mid-task: resume each with SendMessage after the reset.

## Code map (what exists, on `main`)

| Area | Where | State |
|---|---|---|
| Contracts (J3, L1) | `src/Ume.Rag.Pipelines/`, `contracts/{sources,pipeline,jobs,site-profiles}/` | done (#78, #88); drift test in ContractTests |
| Parser P1 formats | `src/parser/`, `src/Ume.Rag.Worker/Parsing/` | done (#84); 200-PDF umea.se comparison is live |
| Connectors | `src/Ume.Rag.Worker/Connectors/` | done (#82, #91, #97); freshness P1b item 4 (#101); noise rules v2 (#107) |
| Processing | `src/Ume.Rag.Worker/Processing/` | done (#98) incl. inspect/NER adapters |
| Sitevision + umea.se | `Processing/Html/SitevisionPreset.cs`, `contracts/site-profiles/v1/www.umea.se.profile.json`, `tests/fixtures/umea/` | done (#104) |
| **Engine (J4)** | `src/Ume.Rag.Worker/Engine/` | **URL lists and uploads sync into live chunks** (#108); crawl wiring etc. remain (below) |
| Inventory | `scripts/inventory/` | done (#111) |
| Pilot stack | `deploy/`, `docs/p1a-local-run.md` | code part done (#94); "stack healthy" needs J8/J9 + fresh images |
| CI / release | `.github/workflows/{verify,parser,release,perf,evaluation,e2e,entra}.yml`, `benchmarks/release-test/` | release proven (`v0.1.0`); `perf.yml` dry run green on GitHub |
| Grading (L8) | `scripts/grading/` | done (#99); run in the live block (#100) |
| Schema (stream B) | `src/Ume.Rag.Infrastructure/Persistence/**` | part 1 (#76), part 2 (#93); pipeline templates seeded (#89) |

## Session 10 in progress (2026-10-31)

- Reviewed and approved B's **#112** (retrieval core, J5; B merges). Follow-ups asked: flaky plan test, `Kind = 'child'`
  in V, and the **keyword stop list** (to-verify #5: K 375–659 ms p95 at 100k chunks), now a P1b item 3 code item for B
  (Decisions "Keyword stop list before the pilot crawl", `13aabcf`).
- CHANGELOG Unreleased since 0.1.0 merged (#113).
- **#115 merged**: worker handlers for every P1 command kind (P1a item 10 part 1, P1b item 8 part 1); B asked for
  `decidedBy` and Swedish refusal texts (#116). Site-profile edits enqueue `sync` `reprocess` (`site-profile`), which
  runs once `a/9-engine-crawl` wires `reprocess`.
- Design sync of #89–#108 done (`30f629b`). Found: the parse cache key lacked the parser build (against simplification
  §2) → fix added to `a/9-engine-drift-slicing`. Open for later: #89 seeds a new built-in template version straight as
  *published* (no publish plan / `reprocess`; check against engine §13.3 when the first v2 template ships); demote URL
  form (asked on #102); `rag_count_retrievals` level overwrite (#114).
- **Merged this session:** #113 (CHANGELOG), #115 (all P1 command kinds), #117 (preview + preflight), #121 (drift
  sweep, parse slicing/resume, preview-first gate, parser build in the parse key), #122 (crawl through the engine,
  `reprocess`/`retry-failed`, site profile → canonicaliser, `jfif`). P1a item 9 is code-complete. B merged #112 (J5,
  issue #120); I approved B's #118 (schema requests from #110, pgvector on the shared data source) and #119 (AppHost keys).
- **Running (worktrees):** `../rag-e1` `a/10-eval-on-demand` (eval class: on-demand three-way incl. `reindex-fts` scratch,
  publish-report, nightly + alert); `../rag-e2` `a/p1b-7-eval-sets` (generate-set/top-up-set in
  `Evaluation/Generation/`, own `IEvalSetHandler`, reconcile names with e1 on merge); `../rag-a5` `a/p1b-6-publish`
  (publish-plan, per-document swap, 14-day sets, rollback, publish-report enqueue); `../rag-a6` `a/p1b-8-parse-rules`.
- **Stream-A follow-ups (small PR later):** after #118 merges: 5105 via `SecurityEvents.PipelineRefused`,
  `SyncScheduler` on `Sources.CreatedAt`; one site-profile host convention (with/without `:port`) across
  `SiteProfiles.AppliedAsync`, #115's follow-up query and `ISiteProfileReader`; fold `Reconciler.WantedSet` with
  `DocumentIngest.EnsureCurrentAsync`; profile `excludedTypes` → `CrawlScope`; `reprocess-document` `fromStage` needs a
  cache-key change (write-once outputs); after #119 merges: drop the "only `Gateway:Keys:Eu`" caveat in
  `docs/p1a-local-run.md` / `docs/local-development.md`.
- **Test lesson:** gate tests must use page text unique to the test (shared embedding cache) and `await using` every
  host; one leaked host failed 40 later tests through `RagDatabaseFixture.ResetAsync`.
- **CI is local only** (user, 2026-10-31: GitHub Actions credits exhausted). Never add the `ci` label, dispatch a
  workflow or run `release.yml`. Every PR is verified locally (full build + suites under `taskset -c 0,1` after merging
  `origin/main`, output in the PR body) and merged on that evidence. Anything that would need the runner goes to the user. Actions are switched off on the repo (`thekim1`), so the ownership check runs locally too: `git diff --name-only --no-renames origin/main...HEAD | python3 -I scripts/check-ownership.py <branch>`.

## Next step (start of session 10)

Start with `plans/p1/prompts/main-session.md`, then this list. **First check GitHub** (open PRs, issues, comments).

1. **Done at the end of session 9:** P1a item 1 (#111): umea.se sitemap inventory, 24,187 URLs (HTML 6,157, PDF
   8,126, legacy Office 0.1 % → Z1 stays P2); **no renderer needed** (all saved pages carry their text without
   JavaScript). The `lastmod` sample (#170) waits for U1.
2. **Engine part 2** (item 9 remainder, new agent): crawl wiring against #101 (`CrawlTraversal.StartAsync` returns a
   `CrawlRunContext`, `EvaluateAsync(context)`, `ListingDoneAsync`, `CrawlFreshness.Revalidates`, `Runs.Report`
   sections, gauge `rag.crawl.incomplete_runs`; known gap: `ProcessAsync` completes the frontier row before the engine
   commits), site-profile → `CanonicalizerOptions` mapping (#104), `reprocess` and `retry-failed` kinds, parse
   slicing/resume, the drift sweep. Then measure #21/#119/#141 where possible.
3. **Then (each after its hand-off):** P1a item 10 (commands `edit-site-profile`/`rollback-site-profile`/
   `edit-eval-question`, `eval` `on-demand` three-way, after J5 = B's `RetrievalEngine`), item 17 (`evaluation-smoke`
   job, after B's smoke set), P1b stream A items 5–8 (preview, publish/re-process/rollback, evaluation harness,
   remaining commands) in `plans/p1b/stream-a.md` order.
4. **Sync point** when stream B's next PRs land: fold their deviations and `results-b.md` rows; also fold #89, #93,
   #101, #104, #107, #108 deviations (engine §3/§9/§10/§13/§16, data-model, connectors §12) into the design.
5. **Small follow-ups:** CHANGELOG (shared PR) for everything since `0.1.0` (incl. "HTML noise rules v2: HTML
   documents re-processed once after upgrade"); unify `robots.disallow` vs `robots.disallowed`; add `jfif` to `CrawlScope.TypesByExtension` (16 on umea.se); refresh the
   stakeholder report.

**Open issues to stream B (waiting on B):** #80 (J3 questions: site-profile create route, template storage, labels),
#90 (L1), #92 (upload manifest, `Settings.Crawl` keys), #95 (item 18: empty `OpenWebUi:ClientId`, AppHost
real-gateway keys, redirect URIs), #102 (freshness schema: `CrawlRuns.Incremental`, `CrawlFrontier.RemovalCheck`,
`UrlPattern` for `demote`), #110 (J4 + requests: event 5105, `Sources.CreatedAt`, `DEFAULT 0` on run counters). Also
#34, #52 (P2/distribution), #75 (P1b briefs pick-up). Stream A's own: #48 (H8, parked), #51 (licence notices, before
distribution). For the user: #100 (L8 run steps).

**Waiting on the user (live block and decisions):** start the **gateway session**
(`plans/gateway-changes/prompt-gateway-session.md`: release 1, shared Keycloak realm `ume`, identity §16 Q1–Q4/Q6,
EUPL for the gateway, the `Eu` keys and catalogue for U-b); **O7** (go-live host); identity §16 Q5, Q7–Q9 (Open WebUI
sign-in gate, identity continuity, token callers, E1 provider); the runbook review by the operations owner; U1 with
the web editors; names and dates for A1, G1, N1, U1, W2, P2 and the gold-set owner; DPIA G1. New finding for G1/W2:
**nothing backs up Open WebUI's data directory**, and with the pilot settings no admin can export another user's chat
(to-verify #173, `docs/runbook.md`).

## Open register items (`plans/postponed-decisions.md`)

A1 (archive law; gates stored chat content), G1 (DPIA, before go-live), M2 (triggered: EU-hosted models until on-prem
hardware), W2 (Open WebUI chat copies), N1 (name), U1 (crawler about page / robots.txt), P2 (licences), D1 (standby),
R1 (self-hosted runner), S2 (object store), W3 (per-user model lists), C2 (visual builder, P3). Every phase-blocking
item needs a named person and a date.

## Environment notes

- Worktrees: none open at the end of session 9; `git worktree list` to check.
- Local images: `ume-rag.local/clamav:dev` (tests use `UME_RAG_TEST_CLAMAV_IMAGE`), `ume-rag.local/parser:ci`
  (`UME_RAG_TEST_PARSER_IMAGE`; rebuild from `src/parser` when the parser changes). Testcontainers needs a registry
  part with a dot in local image names. `172.30.0.0/16` is taken on this machine (Compose subnet overrides locally).
- Aspire CLI: `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire` (not on PATH).
- Tools in the session scratchpad (actionlint, shellcheck) don't survive; re-download when needed.
