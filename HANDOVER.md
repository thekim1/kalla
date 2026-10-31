# Handover

State of the Ume.Rag work for the next session. Rewritten 2026-10-31, end of session 10.
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
end. **Stream A's P1a code is complete except the live parts, and its P1b code items 2 and 4–8 are merged** (item 8's index half waits on B's schema). Stream B (remote) is on P1a items 12–13 (knowledge role, gateway client).

Stakeholder overview: `docs/stakeholder-report.html`, also a private artifact
https://claude.ai/artifact/GzKzra4RwKdf7xEWFc4EAM (not updated since session 8; refresh when P1a code is done).

## Reading order

1. `01-general-idea.md` §10 **Decisions table** (later rows win). The 2026-10-31 rows matter most now: "7-day rule
   in development" (suspended until the first production deployment), "Licence" (EUPL-1.2, gateway too), "Interim
   gateway for P1a", "Low CPU during development" (base Compose caps ≤ 2), "Code first, live testing last",
   "P1b briefs", "P0 integration, stream A/B"; then the session-10 rows "Keyword stop list before the pilot crawl" and "CI on a self-hosted runner, Actions off for now".
2. `plans/weakness-review.md` (binding), then `plans/simplification-pass.md`.
3. **`plans/p1/README.md` §1a** (what changed 2026-10-31), §3 (P1a checklist, binding done-when), §4 (hand-offs J1–J15);
   **`plans/p1b/README.md`** (32 items: 19 code, 13 live; hand-offs L1–L12; §7 settled options, **O7 open**).
4. Progress: `plans/progress.md`, `plans/p1/progress-{a,b}.md`, `plans/p1b/progress-{a,b}.md`; results
   `plans/p1/results-{a,b}.md`.
5. The detail document for the area (`plans/*/design*.md`; map in `plans/README.md`). They were brought in line with
   the code at the J2/J3 sync point (`4b0f3a6`) and again in session 10 for #89–#108 (`30f629b`); #112–#133 are not folded yet.

## How we work (user preferences, binding)

- **Autonomy:** the user asked the main session to work autonomously on its best recommendations and to **merge PRs
  that are obviously ready** (verified locally, reviewed) without asking. Decisions, gate relaxations and anything doubtful
  still go to the user (question tool, recommended option first; record answers in the Decisions table).
- **Stream A** runs through `implementor` subagents, each in its own worktree (`git worktree add ../rag-aN -b
  a/<item>-<slug> origin/main`), **up to ~4 in parallel on disjoint paths**. Brief each fully (it sees nothing
  else): point it at **`plans/p1/prompts/implementor-common.md`** (the binding common rules), then goal, files to read,
  scope and the files other running agents own, binding done-when, proofs incl. a **negative proof**, report + PR body. **Implementors never run git writes**: the
  main session commits, merges `origin/main` into the branch, re-runs build + suites, pushes, opens the PR, merges
  when green, then `git worktree prune`. Send heads-ups to running agents when a merge changes an API they call.
- **CI is local only** (user, 2026-10-31; Decisions "CI on a self-hosted runner, Actions off for now"). Actions are
  switched off on the repo; #124 moves every workflow to the self-hosted runner `ume-rag` for later. Never add the
  `ci` label or dispatch a workflow. Every PR: merge `origin/main` into the branch, then `dotnet build UmeRag.slnx -c
  Release`, `Ume.Rag.Worker.Tests` (with `UME_RAG_TEST_CLAMAV_IMAGE=ume-rag.local/clamav:dev
  UME_RAG_TEST_PARSER_IMAGE=ume-rag.local/parser:ci`) **twice**, `Pipelines.Tests`, `ContractTests`, and
  `git diff --name-only origin/main...HEAD | python3 -I scripts/check-ownership.py <branch>`; evidence in the PR body.
  The main session runs its checks on cores `2,3` (agents use `0,1`).
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
- **Lessons (sessions 7–10):**
  - A merged `main` often surfaces order-dependent test failures that each branch alone doesn't show. Run the worker
    suite twice after every merge and read the *first* real failure (one timed-out test leaking its host made 40 later
    tests fail in `ResetAsync`). Find it by instrumenting `ResetAsync` (dump `pg_stat_activity` for `ume_rag_worker`).
  - `dotnet test` MTP filters: `--filter-class` values are OR'd; mixing `--filter-namespace` and `--filter-class` ANDs
    them (0 tests). Wildcards work: `--filter-class 'Ume.Rag.Worker.Tests.Preview.*'`.
  - A wait loop using `pgrep -f '<pattern>'` matches its own command line and never ends; wait on a file or a PID.
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

## Code map (what exists, on `main` @ `1b0c7b7`)

| Area | Where | State |
|---|---|---|
| Contracts (J3, L1) | `src/Ume.Rag.Pipelines/`, `contracts/{sources,pipeline,jobs,site-profiles}/` | done (#78, #88); drift test in ContractTests |
| Parser P1 formats | `src/parser/`, `src/Ume.Rag.Worker/Parsing/` | done (#84); 200-PDF umea.se comparison is live |
| Connectors | `src/Ume.Rag.Worker/Connectors/` | done (#82, #91, #97, #101, #107); site profile → canonicaliser (#122) |
| Processing | `src/Ume.Rag.Worker/Processing/` | done (#98); parse slicing/resume, preview-first gate, parser build in the parse key (#121); admin parse rules (#133, index half waits on #134) |
| Sitevision + umea.se | `Processing/Html/SitevisionPreset.cs`, `contracts/site-profiles/v1/`, `tests/fixtures/umea/` | done (#104) |
| **Engine (J4)** | `src/Ume.Rag.Worker/Engine/` | **P1a item 9 code-complete**: URL list, upload and **crawl** sync into live chunks (#108, #122); `reprocess`/`retry-failed` (#122); drift sweep + `Reconciler` (#121); `ChunkSetSwap`, 14-day sets, rollback swap-back, `publish-report` enqueue (#131) |
| Commands | `src/Ume.Rag.Worker/Commands/` | every P1 kind (#115) |
| Preview | `src/Ume.Rag.Worker/Preview/` | `stage-preview`, `preflight` (#117), `publish-plan` (#131) |
| Evaluation | `src/Ume.Rag.Worker/Evaluation/` | `on-demand` three-way, `publish-report`, `nightly` + alert + 03:30 schedule (#128); `generate-set`/`top-up-set` on FakeGateway (#130) |
| Retrieval (B) | `src/Ume.Rag.Retrieval/` | `RetrievalEngine` (#112, J5); knowledge role in PRs #125/#127 |
| Inventory, grading | `scripts/inventory/`, `scripts/grading/` | done (#111, #99) |
| Pilot stack | `deploy/`, `docs/p1a-local-run.md` | code part done (#94); "stack healthy" needs J8/J9 |
| CI / release | `.github/workflows/*`, `benchmarks/release-test/` | release proven (`v0.1.0`); Actions off; self-hosted runner PR #124 |
| Schema (B) | `src/Ume.Rag.Infrastructure/Persistence/**` | parts 1–2 (#76, #93); #118 (5105, `Sources.CreatedAt`, run defaults, pgvector) approved, not merged |

## Session 10 (done)

Merged (stream A, all verified locally): #113 CHANGELOG, #115 commands, #117 preview/preflight, #121 drift sweep +
slicing + parser-build key, #122 crawl + `reprocess`/`retry-failed`, #128 eval class, #130 generated sets, #131
publish/rollback, #133 admin parse rules. Design sync of #89–#108 (`30f629b`). Decisions: keyword stop list for B
(P1b item 3), CI local only + self-hosted runner. Approved B's #112 (merged), #118, #119, #124, #125, #126, #127
(B merges). Hand-off issues filed: #116, #129 (J6), #132 (L5), #134 (parse-rule schema), #114.

## Session 11 (in progress, checkpoint)

Merged (stream A, verified locally): #135 P1b item 17 part 1 (test-host run book, Open WebUI feedback service account
+ `deploy/openwebui-feedback-key.sh`, public-records routes, `deploy/tests/pilot-stack.sh` in verify.yml), #136
site-profile host key without port + `excludedTypes` per host + preview parse key with admin rules, #137 eval
reproducibility test + set triggers (first syncs, weekly Sun 04:30, D +25 %), #138 one `Reconciler.ReconcileAsync`
decision, drift sweep swaps back. Design sync of #112–#133 on `main` (`d6b4657`). Approved B's #123 (2 minor notes).
Running: `a/9-reprocess-force` (force/fromStage bypass), `a/9-settings-effects` (source settings-change effects),
`a/5-6-done-rows` (P1a items 5/6 done rows #26, #43, #111). Watch item: one unexplained failure of
`EngineCrawlTests.A_crawl_syncs_the_site_into_live_chunks_with_its_report_and_a_second_run_is_unchanged` (1 of ≈ 10
runs, message not captured). Note: `gh pr merge --delete-branch` also removes the branch's local worktree.

## Next step (start of session 11)

**First check GitHub** (open PRs, issues, comments from stream B). No worktrees or agents are running.

1. **Review B's open PRs**: **#123** (P1a item 13, gateway client; not reviewed yet), and whatever is new. B merges
   its own; #118, #119, #124–#127 are approved and waiting for B. Never re-enable Actions.
2. **Stream-A follow-ups (one small PR, `a/9-engine-followups`):**
   - after #118 merges: event 5105 via `SecurityEvents.PipelineRefused` in `ContentJobHandlers`; `SyncScheduler` on
     `Sources.CreatedAt`; drop #128's own `UseVector()` data-source replacement in `AddRagEvaluation` (B's
     `AddRagDatabase` maps pgvector then);
   - one site-profile host convention (with or without `:port`) across `SiteProfiles.AppliedAsync`, #115's
     `SiteProfileFollowUp` query and `ISiteProfileReader`;
   - fold `Reconciler.WantedSet` (#121) and `DocumentIngest.EnsureCurrentAsync` (#122/#131) into one check, and let
     the drift sweep swap back directly like `reprocess` does;
   - the site profile's `excludedTypes` → `CrawlScope`; the preview passes admin parse-rule candidates to `ParseKey`;
   - after #119 merges: drop the "only `Gateway:Keys:Eu`" caveat in `docs/p1a-local-run.md` / `docs/local-development.md`.
3. **When B answers #134:** the parse-rule commands (`edit-parse-rule`, `disable-parse-rule`: contracts + handlers +
   reprocess of affected sources) and the engine's chunk-set key with `ParseRuleKey`.
4. **Remaining stream-A code items** (`plans/p1b/stream-a.md` order): P1a item 17 (`evaluation-smoke` job, after B's
   smoke set; local only while Actions are off); P1b item 15's `e2e` job (after L7); item 17 pilot stack for P1b
   (after L6); item 19 code complete + pin review. Not built: `reprocess-document` `fromStage` cache bypass (needs a key
   change, write-once outputs), the generated-set triggers (D grew 25 %, weekly top-up schedule).
5. **Sync point** (main session): fold #112–#133 deviations (each PR body's "Design deviations") and `results-a.md`
   row #10 into the design and `to-verify.md`. Open design notes: #89 seeds a new built-in template version straight
   as *published* (check against engine §13.3 when the first v2 template ships); demote URL form (asked on #102);
   simplification §13 / to-verify #137 say the id-only URL gives "the same content hash" (only `OutputHash` matches on
   umea.se).
6. **Then** the stakeholder report refresh (P1a code done) and, once both streams' code items are done, the live
   block with the user (`plans/p1b/README.md` §5 step 4).

**Open issues to stream B (waiting on B):** #80, #90, #92, #95, #102, #110, #114, #116, #129, #132, #134; also #34,
#52, #75. Stream A's own: #48 (H8, parked), #51 (licence notices, before distribution), #120 (J5, can be closed once
#128 is accepted). For the user: #100 (L8 run steps).

**Waiting on the user (live block and decisions):** start the **gateway session**
(`plans/gateway-changes/prompt-gateway-session.md`: release 1, shared Keycloak realm `ume`, identity §16 Q1–Q4/Q6,
EUPL for the gateway, the `Eu` keys and catalogue for U-b); **O7** (go-live host); whether to re-enable Actions on the
self-hosted runner; identity §16 Q5, Q7–Q9; the runbook review by the operations owner; U1 with the web editors; names
and dates for A1, G1, N1, U1, W2, P2 and the gold-set owner; DPIA G1. For G1/W2: **nothing backs up Open WebUI's data
directory**, and with the pilot settings no admin can export another user's chat (to-verify #173).

## Open register items (`plans/postponed-decisions.md`)

A1 (archive law; gates stored chat content), G1 (DPIA, before go-live), M2 (triggered: EU-hosted models until on-prem
hardware), W2 (Open WebUI chat copies), N1 (name), U1 (crawler about page / robots.txt), P2 (licences), D1 (standby),
R1 (self-hosted runner), S2 (object store), W3 (per-user model lists), C2 (visual builder, P3). Every phase-blocking
item needs a named person and a date.

## Environment notes

- Worktrees: none open at the end of session 10; `git worktree list` to check.
- Local images: `ume-rag.local/clamav:dev` (tests use `UME_RAG_TEST_CLAMAV_IMAGE`), `ume-rag.local/parser:ci`
  (`UME_RAG_TEST_PARSER_IMAGE`; rebuild from `src/parser` when the parser changes). Testcontainers needs a registry
  part with a dot in local image names. `172.30.0.0/16` is taken on this machine (Compose subnet overrides locally).
- Aspire CLI: `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire` (not on PATH).
- Tools in the session scratchpad (actionlint, shellcheck) don't survive; re-download when needed.
