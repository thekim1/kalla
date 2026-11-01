# Handover

State of the Ume.Rag work for the next session. Rewritten 2026-11-01, end of session 13.
Repo `~/research/rag`, branch `main`, remote `origin` = https://github.com/thekim1/ume-rag-platform (push after
every commit).

## What this is

**Ume.Rag** (working name, N1) is a RAG / crawling / chatbot platform for Umeå kommun: small .NET 10 services on top
of the team's **LLM gateway** (`~/git/llmgateway`, https://github.com/thekim1/llmgateway). The user owns the gateway
and wants to do better than **Eneo** (`~/git/eneo`), a slow "kitchen sink" monolith. Design and code live in this repo.

## Where we are (end of session 13)

- **Design** complete. **P0** done and released as `v0.1.0` (2026-10-31). **P1** is built "code first, live testing
  last" (user, 2026-10-31).
- **Milestone 1 (first live run) passed** 2026-11-01 against the user's interim gateway, on this machine (WSL, Aspire
  CLI): 5 umea.se pages, 17 chunks at 1024 dims (`text-embedding-3-large` via `dimensions`, `Eu`), canary recorded,
  `/v1/retrieve` right chunk first. Run book and findings: `docs/p1a-local-run.md` §4/§4.7; `plans/p1/README.md`
  "Milestone 1 result".
- **P1a code is complete on both streams** except what waits for the live block:
  - item 14 admin set-up surface (B; J7 #193; done check #194);
  - item 15 chat API a–d with Open WebUI user-JWT trust and the AppHost `open-webui` resource (B; J9 #210; live check
    through Open WebUI #209);
  - item 16 realm additions for the shared Keycloak (B #207/#211; J8 #215 → the gateway session);
  - item 17 evaluation smoke: B's smoke set #208 + stream A's `evaluation-smoke` job and evaluator == API contract test
    #220 (green on the self-hosted runner).
- **P1b schema part 3** is merged (#219) and stream A switched to it (#224–#226). B's chat schema #223 is approved (with
  a condition, below) and not merged yet.
- **`main` = `893e5be`**, green on the runner and locally (all suites, Worker.Tests twice) at `a1a372f`.
- Overviews for people: `docs/phase1-guide.html` (Swedish, private artifact https://claude.ai/artifact/6RMq1GXHc7siE8VVudJwL1),
  `docs/stakeholder-report.html` (English, status section stale).

## Reading order

1. `01-general-idea.md` §10 **Decisions table** (later rows win). Recent rows that matter: "Code first, live testing
   last", "7-day rule in development" (suspended until the first production deployment), "Interim gateway for P1a",
   "Pilot embedding model, U-b", "CI on the self-hosted runner for every pull request", "Merge on local evidence, then
   check main", and **"Personal data on the municipality's own published site"** (2026-11-01: `piiMode` report).
2. `plans/weakness-review.md` (binding), then `plans/simplification-pass.md`.
3. `plans/p1/README.md` §1a, §3 (P1a checklist, binding done-when), §4 (hand-offs J1–J15), "Milestone 1 result";
   `plans/p1b/README.md` (items, hand-offs L1–L12, §7 settled options, **O7 open**).
4. Progress: `plans/progress.md`, `plans/p1/progress-{a,b}.md`, `plans/p1b/progress-{a,b}.md`; results
   `plans/p1/results-{a,b}.md`.
5. The detail document for the area (`plans/*/design*.md`; map in `plans/README.md`). Deviations up to #156 are folded
   in; **#159 and later are not** (see Next step 3).

## How we work (user preferences, binding)

- **Autonomy:** work on your best recommendations; merge stream-A PRs that are obviously ready (verified locally,
  reviewed) without asking. **Handle GitHub without being asked** (user, 2026-11-01): review new and updated stream-B
  PRs (approve / request changes with local evidence), answer B's issues, take the decisions that are the main
  session's, close issues verified as done with an evidence comment. Real decisions still go to the user (question
  tool, recommended option first; record answers in the Decisions table).
- **Stream A** runs through `implementor` subagents in their own worktrees (`git worktree add ../rag-aN -b
  a/<item>-<slug> origin/main`), up to ~3–4 in parallel on disjoint paths. Brief each fully: point at
  `plans/p1/prompts/implementor-common.md`, then goal, files to read, the files other running agents own, binding
  done-when, proofs incl. a **negative proof**, report + PR body. Tell them: Worker.Tests DB classes use
  `[Collection(RagDatabaseGroup1..4.Name)]` (#174). **Implementors never run git writes**: the main session commits,
  merges `origin/main` into the branch, runs the evidence, pushes, opens the PR, merges, removes the worktree.
- **Local evidence before a merge** (Decision "Merge on local evidence, then check main"): after merging `origin/main`,
  `dotnet build UmeRag.slnx -c Release`, `Ume.Rag.Worker.Tests` **twice** (with `UME_RAG_TEST_CLAMAV_IMAGE=
  ume-rag.local/clamav:dev UME_RAG_TEST_PARSER_IMAGE=ume-rag.local/parser:ci`), `Pipelines.Tests`, `ContractTests`,
  `git diff --name-only origin/main...HEAD | python3 -I scripts/check-ownership.py <branch>`; output in the PR body.
  After a batch: `gh workflow run verify.yml --ref main` (self-hosted runners `ume-rag`, `ume-rag2`; never GitHub-hosted)
  plus the local suites on `main` (worktree `../rag-main`). Fix a red `main` first. Exception: a done-when that says
  "green on a PR" waits for that PR's runner job (#220).
- **The scratchpad helper** `verify.sh <worktree> <cores> <branch|main> [full]` (recreate it each session): runs the
  evidence above with `NO_COLOR=1`, strips ANSI, keeps failing test names and raw per-suite logs under the scratchpad's
  `logs/`. Main session on cores `2,3`, agents on `0,1`.
- **Stream B** is a remote agent (GitHub `thekim1`). Talk only through GitHub (labels `stream-b`, `stream-a`, `main`;
  PR comments). **B merges its own PRs.** Everything the main session files appears as `Crotalus`.
  - Review B's PRs on the PR merged with `main` (or its head if it conflicts), the affected suites twice, and read the
    security-relevant parts adversarially: session 13 found an unscoped-key gap (#185), a collection-scope bypass
    through chat (#198), 7 answer-filter bypasses proven with a throwaway probe (#203), a crash on old dev volumes
    (#172) and an unsafe repeated commit (#180). Throwaway probes go in the scratch worktree and are never committed.
  - **Watcher:** a background loop every 10 min on the whole open-PR list (`gh pr list --state open --json
    number,headRefOid`), non-`Crotalus` issue comments since T, and new issues/PRs since T. Watch the PR list, not
    known PR numbers (a PR created just before T was missed once). Stay well under the 5,000/h API limit (hit once
    with 60–90 s polling).
- **Shared files** (`Directory.Packages.props`, `README.md`, `CHANGELOG.md`, `UmeRag.slnx`, `.gitignore`): own small
  PR, append-only. **Design documents** (`plans/**`, root `*.md`, `.github/stream-ownership.txt`) are the main
  session's: commit directly to `main`. `.github/**` is stream A's.
- Verify claims against real sources (packages read in the scratchpad, never executed; umea.se only politely,
  `UmeaKommunBot/1.0`, robots.txt first). Secrets (gateway address and key, dev API keys, pepper) never go into the repo,
  a log or a chat: read them into variables (`dotnet user-secrets list`, `docker exec … printenv`), write keys to a
  mode-600 scratchpad file.

### Lessons

- A merged `main` surfaces order-dependent failures; read the *first* real failure (one leaked host makes every later
  test of its collection fail `ResetAsync` after 10 s).
- **Don't overlap more than two full Worker.Tests runs** (each starts four Postgres containers): three implementors plus
  the main session caused `ResetAsync` cascades that looked like real failures. Judge on a quiet run; an isolated
  failure that doesn't reproduce in 8 runs on the branch and on `main` is load.
- Flakes are often real: session 13's `SettingsEffectsTests` flake was a crawl race that silently missed deletions
  (#182); a `UrlPattern` flake was an `exclude` pattern missing under load (#197); an `EvalSetTriggerTests` "same
  millisecond" flake was the hour after Sunday 04:30 Stockholm (#202). Trace before widening a bound.
- MTP: `--filter-class` values are OR'd; repeated `--filter-not-trait` excludes any match; MTP colours `failed`, so grep
  with `NO_COLOR=1`. A solution-wide `--filter-trait` leaves projects with zero tests, which fails.
- CI renders Compose in several steps with their own `env`: after changing a required Compose variable, check every
  `compose config`/bundle step in `verify.yml`/`release.yml` (#213 broke `deploy-checks`, fixed in #218).
- CI's shellcheck is 0.9.0: `if …; then …; fi`, never `A && B || C`. actionlint before pushing a workflow change.
- A branch carrying an unsquashed copy of a squash-merged PR: recreate it from `origin/main` and apply only the new diff.
- `psql -c "A; B"` is one transaction. `gh pr merge --admin` is refused (plain squash works). `pgrep -f` matches its own
  command line; don't `pkill -f` a pattern your command contains.
- A merge of two branches that both add a column to the same SELECT: read new columns by name (`GetOrdinal`), not index.
- Production `pg_hba.conf`: `postgres` only over the socket (`seed_url_list.py --docker … --socket`). The host firewall
  needs the same-host exceptions of #150 for a gateway/Keycloak on the same machine.
- Run the AppHost here with the Aspire CLI (`aspire start/describe/logs/resource <r> start/stop/ps`); WSL mirrored
  networking lets the Windows browser reach every `localhost` port (another app on 5174 = browser cache: private window).
- The 5-hour usage limit can stop agents mid-task: resume each with SendMessage after the reset.

## Code map (on `main`)

| Area | Where | State |
|---|---|---|
| Contracts (J3, L1) | `src/Ume.Rag.Pipelines/`, `contracts/{sources,pipeline,jobs,site-profiles}/` | done; drift test in ContractTests; `piiMode` (#173), `EmbedPayload.ParseRuleKey` (#225); `UrlPattern` now `Ume.Rag.Pipelines.Sources` (#224) |
| Parser | `src/parser/`, `src/Ume.Rag.Worker/Parsing/` | done (#84); contract tests retry `not_ready` (#177) |
| Connectors | `src/Ume.Rag.Worker/Connectors/` | done; crawl `RemovalCheck` flag + `CrawlRuns.Incremental` (#224, old `-1` rows still read); URL patterns without a match timeout (#197); uploads read through the envelope at the source level (#186) |
| Processing | `src/Ume.Rag.Worker/Processing/` | done; `ContentKind`/`LargeTable` per chunk, `Chunker.Version` 2 (#226); `piiMode` report/hold (#173); `PiiFlaggedAt` (#226) |
| Engine (J4) | `src/Ume.Rag.Worker/Engine/` | P1a item 9 complete; `ChunkSets.ParseRuleKey` salt (rule + forced reprocess, #225); crawl fetch-phase race fixed (#182) |
| Commands / Preview | `src/Ume.Rag.Worker/{Commands,Preview}/` | every P1 kind; parse-rule commands (#156) with a 10 s settle (#225) |
| Evaluation | `src/Ume.Rag.Worker/Evaluation/` | on-demand/three-way, publish-report, nightly, generate/top-up on FakeGateway; kind stratification, `Round`, `Candidate`/`ThresholdProposal` (#226); `evaluation-smoke` + contract test (#220) |
| Jobs | `src/Ume.Rag.Worker/Jobs/` | job log names type chain + throw site, never the message (#179) |
| Blobs | `src/Ume.Rag.Infrastructure/Blobs/` | URB2 envelope; `internal` uploads encrypted by the admin (#180) and read by the worker (#186) |
| Admin API (B) | `src/Ume.Rag.AdminApi/` | item 14 complete (#167, #176, #178, #180, #185, #188–#190, #199); `piiMode` report endpoint + `PiiOwnDomains` seed (#217) |
| Knowledge + chat (B) | `src/Ume.Rag.{Retrieval,Answering,Api}/` | knowledge routes; chat a–d (#198, #203–#205), escape-by-default answer filter; data-protection defaults for unsaved levels (#209) |
| AppHost (B) | `src/Ume.Rag.AppHost/` | dev run fixed (#172: `vector`, PGDATA in `pgdata/`, blob root); `open-webui` resource (#206) |
| Deploy | `deploy/` | Compose `Knowledge__PublicBaseUrl` (#213); Keycloak realm additions `deploy/keycloak/` (#207, local redirects opt-in #211) |
| Tests / CI | `tests/`, `.github/workflows/` | Worker.Tests in four parallel collections, finalizer 0.2 s (#174: 4 min → ~45 s, Verify ≈ 3–4 min); `evaluation-smoke` job (#220) |

## Session 13 (done)

- **Milestone 1** (above). Findings: #168 (AppHost dev run; fixed by B's #172); the gateway answers a PII policy block
  with 503 (the worker retries it as an outage; for the gateway session: 4xx with a reason code); umea.se pages held for
  e-mail only, which led to the user's `piiMode` decision.
- **Merged (stream A, 30 PRs):** docs #170 #171 #183 #184 #192; `piiMode` #173; CI speed #174; parser contract retry
  #177; job log #179; crawl race #182; upload envelope read #186; URL patterns #197; eval-trigger test clock #202;
  Compose `PublicBaseUrl` #213 + its CI env #218; item 17 #220; schema-part-3 follow-ups #224 #225 #226.
- **Reviewed every stream-B PR** (#165–#223); the security findings above were fixed before merge. **Answered/decided:**
  #129 (FakeGateway chat-script shape; chunk columns), #163 (smoke loader), #164, #181 (encrypt `internal` uploads now,
  worker side first), B's #178 deviation 1 (canonicaliser stays in the worker). **Filed:** #168, #175, #187.
- **Plans:** Decisions row "Personal data on the municipality's own published site" (`d748909`); progress and P1 README
  milestone result (`691cdad`).

**At the restart:**
- The milestone-1 AppHost is **stopped** (and its leftover containers removed). Its user secrets still hold the
  milestone switches (`docs/p1a-local-run.md` §4.6 removes them; keep them for the pilot set-up). After #172 the first
  start begins with an empty database.
- Worktrees: `../rag-main` (detached) only. No agent running, no stream-A PR open.
- Open from B: #223 (chat schema), approved with a condition: the chat metadata retention lands in the same PR as the
  conversation writer (P1b item 10).

## Next step

1. **Check `main` and GitHub** at the start (recreate `verify.sh`; dispatch `verify.yml` on `main`; arm the watcher).
2. **With the user: the pilot set-up through Scalar (P1a item 20)**, in J7 #193's order: data-protection row, Open
   WebUI client + key, collection (+ publish), sources (umea.se crawl with `piiMode: report`), the umea.se site profile
   (`contracts/site-profiles/v1/www.umea.se.profile.json`), sync, gold set, assistant + audience. Needs from the user:
   a sign-in, the pilot collection's name and department, the crawl's start URLs, and the municipal companies' e-mail
   domains for `Settings.Engine.PiiOwnDomains` (seeded with `umea.se`). Start the AppHost first (`aspire start`).
3. **Design sync** of deviations from #159 on: `piiMode` (processing §9.3–§9.6, connectors §12.3, 6a §14 rule 3);
   #169 (knowledge §9/§4.7); #174 (`Worker:FinalizerSeconds` double ≥ 0.1); #176/#178 (admin §4.5/§4.6: V16
   `candidates` 20–200, delete refused with live sources, V25 exact duplicates); #197/#224 (connectors §5.2 no regex
   timeout, matcher location; §5.4/§6.0 `RemovalCheck`, `CrawlRuns.Incremental`); #198/#209 (identity §4.3 step 6
   collection scopes reach the assistant's collections; step 9 data-protection defaults); #200 (identity §6.3 cached
   key after a key change); #225 (engine §2.3/§8/§14, processing §3.5, data-model §2.2 `ParseRuleKey`); #226 (processing
   §13.1/§13.6, `PiiFlaggedAt` kept on re-flag); data-model §2.1 `EmbeddingProfiles.RequestDimensions`. Then the
   restructure (`plans/doc-restructure.md`).
4. **Stream-A code left:**
   - `EvalSetTestKit` → B's `FakeGateway:ChatScripts` (#221);
   - drop the `-1` removal-check fallback once B migrates before its `Depth >= 0` CHECK (#102);
   - P1b item 15 `e2e` (after L7); item 17 importer settings (after L6); item 19 pin review before go-live (incl. the
     condense step `Eu` → `OnPrem`, B's #204 note);
   - small: Aspire 13.6.1 → 13.6.4; `seed-url-list.sql` with `piiMode: report`; the `parse.empty` run counter shows
     `Unchanged` (check intent); a package audit in a weekly/release workflow.
5. **Then the live block** (`plans/p1b/README.md` §5 step 4): the umea.se pilot crawl (personal-data dry run), the gold
   set with the department (item 21), capacity on the EU routes; the user hands `docs/gateway-host-setup.md` to Hermes.

**Open issues to stream B:** #80, #90, #102, #129, #132, #134, #75, #52. Stream A's own: #48, #51. For the user: #100
(L8 run steps), #215 (J8, realm additions for the gateway session). Hand-offs received: #193 (J7), #210 (J9).

**Waiting on the user:** the pilot set-up inputs (Next step 2); the gateway session (release 1 incl. the embedding-batch
fix, a 4xx for policy blocks, the shared Keycloak realm `ume` with J8 #215, identity §16 Q1–Q4/Q6); handing
`docs/gateway-host-setup.md` to Hermes; **O7** (go-live host); identity §16 Q5, Q7–Q9; runbook review by the operations
owner; U1 with the web editors; names and dates for A1, G1, N1, U1, W2, P2 and the gold-set owner; DPIA G1.

## Open register items (`plans/postponed-decisions.md`)

A1 (archive law; gates stored chat content: the chat schema allows history `none` only), G1 (DPIA, before go-live), M2
(triggered: EU-hosted models until on-prem hardware), W2 (Open WebUI chat copies), N1 (name), U1 (crawler about page /
robots.txt), P2 (licences), D1 (standby), R1 (self-hosted runner), S2 (object store), W3 (per-user model lists), C2
(visual builder, P3). Every phase-blocking item needs a named person and a date.

## Environment notes

- `rag_key.txt` in the repo root holds the user's interim-gateway key; excluded locally (`.git/info/exclude`), mode 600,
  copied into the AppHost user secrets. Never commit, print or move it into the repo.
- Local images: `ume-rag.local/clamav:dev` (`UME_RAG_TEST_CLAMAV_IMAGE`), `ume-rag.local/parser:ci`
  (`UME_RAG_TEST_PARSER_IMAGE`; rebuild from `src/parser` when the parser changes). Testcontainers needs a dotted
  registry part in local image names. `172.30.0.0/16` is taken on this machine.
- Aspire CLI: `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire` (not on PATH).
- Self-hosted runners `ume-rag` and `ume-rag2` (the user's other machine, `/home/kim/actions-runner{,2}`).
- Scratchpad tools (actionlint, shellcheck, `verify.sh`) don't survive a session; re-create them.
