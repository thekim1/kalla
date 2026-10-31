# Handover

State of the Ume.Rag work for the next session. Rewritten 2026-10-31, session 11 (late).
Repo `~/research/rag`, branch `main`, remote `origin` = https://github.com/thekim1/ume-rag-platform (push after
every commit).

## What this is

**Ume.Rag** (working name, N1) is a RAG / crawling / chatbot platform for Umeå kommun: small .NET 10 services on top
of the team's **LLM gateway** (`~/git/llmgateway`, https://github.com/thekim1/llmgateway). The user owns the gateway
and wants to do better than **Eneo** (`~/git/eneo`), a slow "kitchen sink" monolith. Design and code live in this repo.

**Where we are:** the design is complete. **P0 is done and released as `v0.1.0`** (2026-10-31,
https://github.com/thekim1/ume-rag-platform/releases/tag/v0.1.0). **P1 is coded "code first, live testing last"**
(user, 2026-10-31). **Stream A's P1a and P1b code items are done except** the parse-rule commands (agent running at
this checkpoint), the items that wait on stream B (P1a item 17 smoke job, P1b item 15 `e2e`, item 17's importer part)
and the end-of-P1b pin review. Stream B (remote) is on P1a items 12–14: knowledge API (#125/#127/#146/#152 approved),
gateway client (#123 approved, merging), then admin endpoints (J7), chat API (J9) and realm additions (J8).

**First live step (milestone 1)** is prepared: the user's interim gateway (address only in local user secrets and the
main session's memory, never in the repo) serves `text-embedding-3-large` from an `Eu` provider; a smoke test got
1024 values via `dimensions`. Steps: `docs/p1a-local-run.md` §4 (developer machine) and **`docs/gateway-host-setup.md`**
(on the gateway's host, written for a person or the user's infra agent "Hermes"). It waits for B's #123 and #142.

Overviews: **`docs/phase1-guide.html`** (Swedish, stakeholders; private artifact
https://claude.ai/artifact/6RMq1GXHc7siE8VVudJwL1) and the older English `docs/stakeholder-report.html`
(https://claude.ai/artifact/GzKzra4RwKdf7xEWFc4EAM, status section stale).

## Reading order

1. `01-general-idea.md` §10 **Decisions table** (later rows win). The 2026-10-31 rows matter most now: "7-day rule
   in development" (suspended until the first production deployment), "Licence" (EUPL-1.2, gateway too), "Interim
   gateway for P1a", "Low CPU during development" (base Compose caps ≤ 2), "Code first, live testing last",
   "P1b briefs", "P0 integration, stream A/B"; the session-10 row "Keyword stop list before the pilot crawl"; the
   session-11 rows **"Pilot embedding model, U-b"** (text-embedding-3-large, 1024 dims via `dimensions`, `Eu`) and
   **"CI on the self-hosted runner for every pull request"** (replaces "Actions off for now").
2. `plans/weakness-review.md` (binding), then `plans/simplification-pass.md`.
3. **`plans/p1/README.md` §1a** (what changed 2026-10-31), §3 (P1a checklist, binding done-when), §4 (hand-offs J1–J15);
   **`plans/p1b/README.md`** (32 items: 19 code, 13 live; hand-offs L1–L12; §7 settled options, **O7 open**).
4. Progress: `plans/progress.md`, `plans/p1/progress-{a,b}.md`, `plans/p1b/progress-{a,b}.md`; results
   `plans/p1/results-{a,b}.md`.
5. The detail document for the area (`plans/*/design*.md`; map in `plans/README.md`). They were brought in line with
   the code at the J2/J3 sync point (`4b0f3a6`), in session 10 for #89–#108 (`30f629b`) and in session 11 for
   #112–#141 (`d6b4657`, `7487c72`) and #143 (`884c89d`); #149–#153 and later are not folded yet.

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
- **CI** (user, 2026-10-31; Decisions "CI on the self-hosted runner for every pull request", #151): `verify.yml`
  and `parser.yml` run on the self-hosted runner `ume-rag` for every non-draft PR, no label. GitHub-hosted minutes are
  gone: never dispatch to hosted runners; scheduled workflows stay off. The main session **still verifies every PR
  locally**: merge `origin/main` into the branch, then `dotnet build UmeRag.slnx -c Release`, `Ume.Rag.Worker.Tests`
  (with `UME_RAG_TEST_CLAMAV_IMAGE=ume-rag.local/clamav:dev UME_RAG_TEST_PARSER_IMAGE=ume-rag.local/parser:ci`)
  **twice**, `Pipelines.Tests`, `ContractTests`, and `git diff --name-only origin/main...HEAD | python3 -I
  scripts/check-ownership.py <branch>`; evidence in the PR body. **Merge when both the local suites and the runner's
  run are green** (`gh pr checks <n>`; GitHub can't enforce it on this repo). The scratchpad script `verify.sh
  <worktree> <cores> <branch>` did all local checks in session 11 (recreate it; it doesn't survive). Main session on
  cores `2,3`, agents on `0,1`.
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
  - (Session 11) `gh pr merge --delete-branch` also removes the branch's local worktree; `git worktree prune` after.
  - (Session 11) A stream-B watcher: a background `until` loop on `gh api …/issues?since=` and `…/issues/comments`,
    filtering out `Crotalus` (the main session's own account), re-armed after each hit.
  - (Session 11) Production `pg_hba.conf` allows the `postgres` superuser only over the Unix socket (peer):
    `seed_url_list.py --docker ume-rag-postgres-1 --socket`.
  - (Session 11) The host firewall rejected new connections from RAG networks to the host itself, which blocks a
    gateway/Keycloak on the same machine; #150 adds `INPUT` and `--ctorigdst` exceptions for allow-listed endpoints.

## Code map (what exists, on `main` late in session 11)

| Area | Where | State |
|---|---|---|
| Contracts (J3, L1) | `src/Ume.Rag.Pipelines/`, `contracts/{sources,pipeline,jobs,site-profiles}/` | done (#78, #88); drift test in ContractTests |
| Parser P1 formats | `src/parser/`, `src/Ume.Rag.Worker/Parsing/` | done (#84); 200-PDF umea.se comparison is live |
| Connectors | `src/Ume.Rag.Worker/Connectors/` | done (#82, #91, #97, #101, #107); site profile → canonicaliser (#122) |
| Processing | `src/Ume.Rag.Worker/Processing/` | done (#98); parse slicing/resume, preview-first gate, parser build in the parse key (#121); admin parse rules (#133, index half waits on #134) |
| Sitevision + umea.se | `Processing/Html/SitevisionPreset.cs`, `contracts/site-profiles/v1/`, `tests/fixtures/umea/` | done (#104) |
| **Engine (J4)** | `src/Ume.Rag.Worker/Engine/` | **P1a item 9 code-complete**: URL list, upload and **crawl** sync into live chunks (#108, #122); `reprocess`/`retry-failed` (#122); drift sweep + `Reconciler` (#121); `ChunkSetSwap`, 14-day sets, rollback swap-back, `publish-report` enqueue (#131) |
| Commands | `src/Ume.Rag.Worker/Commands/` | every P1 kind (#115); forced `reprocess-document` (#140); parse-rule commands in progress (`a/p1b-8-parse-rule-commands`) |
| Preview | `src/Ume.Rag.Worker/Preview/` | `stage-preview`, `preflight` (#117), `publish-plan` (#131) |
| Evaluation | `src/Ume.Rag.Worker/Evaluation/` | reproducibility test, set triggers (#137); `on-demand` three-way, `publish-report`, `nightly` + alert + 03:30 schedule (#128); `generate-set`/`top-up-set` on FakeGateway (#130) |
| Retrieval (B) | `src/Ume.Rag.Retrieval/` | `RetrievalEngine` (#112, J5); knowledge role in PRs #125/#127 |
| Inventory, grading | `scripts/inventory/`, `scripts/grading/` | done (#111, #99) |
| Pilot stack | `deploy/`, `docs/p1a-local-run.md`, `docs/test-host.md`, `docs/gateway-host-setup.md` | test-host run book + Open WebUI feedback account (#135); milestone-1 seed script `scripts/dev/` (#149); same-host firewall fix, migrator `EmbeddingProfile__*` env (#150); code part done (#94); "stack healthy" needs J8/J9 |
| CI / release | `.github/workflows/*`, `benchmarks/release-test/` | release proven (`v0.1.0`); Actions off; self-hosted runner PR #124 |
| Schema (B) | `src/Ume.Rag.Infrastructure/Persistence/**` | parts 1–2 (#76, #93); #118 (5105, `Sources.CreatedAt`, run defaults, pgvector) approved, not merged |
| Blobs | `src/Ume.Rag.Infrastructure/Blobs/` | URB2 envelope, file key provider, worker encrypts `internal` blobs, `IBlobEnvelope` for rag-api (#153) |
| Engine extras (session 11) | `src/Ume.Rag.Worker/Engine/` | one `Reconciler.ReconcileAsync` decision + drift-sweep swap-back (#138); forced reprocess with salted keys (#140); settings-change effects via `Runs.SettingsHashes` (#141); 24 h ClamAV cap, embed batch cap 6000 tokens (#143); 5105, `CreatedAt` scheduler (follow-up PR in verification) |

## Session 11 (done so far)

Merged (stream A, verified locally): #135 test-host run book + Open WebUI feedback account; #136 site-profile host key
without port, `excludedTypes` per host, preview parse key with admin rules; #137 eval reproducibility + set triggers;
#138 one reconcile decision, drift-sweep swap-back; #139 P1a items 5/6 rows (#26, #43, #111, #81); #140 forced
reprocess; #141 settings-change effects; #143 24 h ClamAV cap, embed batch cap, URL-list `identity-changed`; #144
CHANGELOG; #148 README; #149 milestone-1 seed script + run steps; #150 gateway-host setup guide + firewall fix;
#153 blob envelope. Design syncs `d6b4657`, `7487c72`, `884c89d`. Docs: `docs/phase1-guide.html`.
Reviewed/approved B: #123, #146, #147, #151, #152. Filed: #142 (RequestDimensions; B takes it right after #123),
comments on #134 (B confirmed the command route; salt column for the chunk-set key), #145 (answered by #153).
Decisions: "Pilot embedding model, U-b"; "CI on the self-hosted runner for every pull request". Gateway session
prompt §1 gained the embedding-batch vs `ContextWindow` fix (`a464514`).

## Next step

1. **Finish the running work:** merge `a/9-engine-followups` (5105, `CreatedAt`, shared pgvector source, docs) and
   `a/p1b-8-parse-rule-commands` (edit/disable-parse-rule, affected-source reprocess) when local + runner are green;
   merge #154 (README CI) on a green runner run.
2. **Milestone 1 when B lands #123 and #142** (and #146/#152 for retrieval): worker side of #142 if B leaves it
   (`dimensions` in the embed stage, canary, evaluation query embedder); then run `docs/p1a-local-run.md` §4 locally
   against the interim gateway (user secrets are set: `Gateway:BaseUrl`, `Gateway:Keys:Eu`; flip
   `FakeGateway:Enabled=false`; AppHost must forward `EmbeddingProfile:*` to migrations, asked on #142). Then the user
   can hand `docs/gateway-host-setup.md` to Hermes.
3. **Stream-A code left:** P1a item 17 `evaluation-smoke` job (after B's smoke set; the runner now runs CI); P1b item
   15 `e2e` (after L7); item 17 importer settings (after L6); worker upload reads through `ForLevel` once the admin
   encrypts uploads (#145 follow-up); the chunk-set key salt with B's schema part 3 (#134); binary `COPY` in
   `IndexWriter` for to-verify #21/#119 (optional); item 19 pin review before go-live.
4. **Watch item:** one unexplained failure of `EngineCrawlTests.A_crawl_syncs_the_site_into_live_chunks_with_its_report_and_a_second_run_is_unchanged` (1 of ≈ 12 runs, message not captured). Read the message the next time it fails.
5. **Design sync** of #143–#153 deviations (each PR body) and to-verify; refresh the old stakeholder report's status
   or point it to the phase 1 guide.
6. Then the live block with the user (`plans/p1b/README.md` §5 step 4).

**Open issues to stream B:** #80, #90, #92, #95, #102, #110, #114, #116, #129, #132, #134, #142; also #34, #52, #75.
Stream A's own: #48, #51, #120 (close once #128 is accepted). For the user: #100 (L8 run steps).

**Waiting on the user:** keys on the interim gateway restricted to `Eu` and the embedding deployment's `ContextWindow`
left empty; the **gateway session** (release 1 incl. the embedding-batch fix, shared Keycloak realm `ume`, identity §16
Q1–Q4/Q6); **O7** (go-live host); identity §16 Q5, Q7–Q9; runbook review by the operations owner; U1 with the web
editors; names and dates for A1, G1, N1, U1, W2, P2 and the gold-set owner; DPIA G1.

## Open register items (`plans/postponed-decisions.md`)

A1 (archive law; gates stored chat content), G1 (DPIA, before go-live), M2 (triggered: EU-hosted models until on-prem
hardware), W2 (Open WebUI chat copies), N1 (name), U1 (crawler about page / robots.txt), P2 (licences), D1 (standby),
R1 (self-hosted runner), S2 (object store), W3 (per-user model lists), C2 (visual builder, P3). Every phase-blocking
item needs a named person and a date.

## Environment notes

- Worktrees: at this checkpoint `../rag-f118` (`a/9-engine-followups`), `../rag-pr` (`a/p1b-8-parse-rule-commands`),
  `../rag-readme2` (`a/shared-readme-ci`, PR #154); `git worktree list` to check.
- `rag_key.txt` in the repo root holds the user's interim-gateway key; it is excluded locally (`.git/info/exclude`),
  mode 600, and already copied into the AppHost user secrets. Never commit, print or move it into the repo.
- Local images: `ume-rag.local/clamav:dev` (tests use `UME_RAG_TEST_CLAMAV_IMAGE`), `ume-rag.local/parser:ci`
  (`UME_RAG_TEST_PARSER_IMAGE`; rebuild from `src/parser` when the parser changes). Testcontainers needs a registry
  part with a dot in local image names. `172.30.0.0/16` is taken on this machine (Compose subnet overrides locally).
- Aspire CLI: `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire` (not on PATH).
- Tools in the session scratchpad (actionlint, shellcheck) don't survive; re-download when needed.
