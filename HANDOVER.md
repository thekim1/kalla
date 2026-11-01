# Handover

State of the Ume.Rag work for the next session. Rewritten 2026-11-01, end of session 13; sessions 14–16 added below.
**Direction since session 16: a complete demo first, details after it (`plans/demo-path.md`).**
Repo `~/git/ume-rag-platform` (this computer; it was `~/research/rag` on the other one), branch `main`, remote `origin` =
https://github.com/thekim1/ume-rag-platform (push after every commit).

## What this is

**Ume.Rag** (working name, N1) is a RAG / crawling / chatbot platform for Umeå kommun: small .NET 10 services on top
of the team's **LLM gateway** (`~/git/llmgateway`, https://github.com/thekim1/llmgateway). The user owns the gateway
and wants to do better than **Eneo** (`~/git/eneo`), a slow "kitchen sink" monolith. Design and code live in this repo.

## Where we are (end of session 13; sessions 14–17 below, session 17 is the latest)

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

1. `01-general-idea.md` §10 **Decisions table** (rows with status `current` bind; two layers: Decisions table > detail
   document). Recent rows that matter: "Code first, live testing
   last", "7-day rule in development" (suspended until the first production deployment), "Interim gateway for P1a",
   "Pilot embedding model, U-b", "CI on the self-hosted runner for every pull request", "Merge on local evidence, then
   check main", and **"Personal data on the municipality's own published site"** (2026-11-01: `piiMode` report).
2. The passes (`plans/weakness-review.md`, `plans/simplification-pass.md`, `plans/performance-pass.md`,
   `plans/consistency-pass.md`) are folded into the detail documents (2026-11-01) and don't bind; read them only for the
   reasoning. Each ends with a ledger: every rule, where it went.
3. `plans/p1/README.md` §1a, §3 (P1a checklist, binding done-when), §4 (hand-offs J1–J15), "Milestone 1 result";
   `plans/p1b/README.md` (items, hand-offs L1–L12, §7 settled options, **O7 open**).
4. Progress: `plans/progress.md`, `plans/p1/progress-{a,b}.md`, `plans/p1b/progress-{a,b}.md`; results
   `plans/p1/results-{a,b}.md`.
5. The detail document for the area (`plans/*/design*.md`; map in `plans/README.md`). Written in the present tense,
   passes folded (2026-11-01); the deviations up to #226 and #228–#243 are in. **Not yet in:** stream B's merged P1b
   items 9–10 (#254–#262) and the later PRs (design sync, Next step 3).

## How we work (user preferences, binding)

- **Demo first, details after (user, 2026-11-01):** work toward the complete demo of `plans/demo-path.md` before any deep dive.
  Don't over-engineer tests: no extra negative proofs, probes or flake hunts unless a change needs one; merge on green CI.
  Adversarial review of stream B's security-relevant parts stays (it found real bugs), but keep it proportionate.
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

## Session 17 (2026-11-01: the demo runs)

- **Demo done** (`plans/demo-path.md` "Result"): `turkos` answers in Open WebUI with citations to crawled umea.se pages,
  over the interim gateway at `Eu`. Model **`gpt-6.1-sol`**, because the gateway refuses `temperature` for
  `claude-opus-5-5`/`claude-haiku-5-5` and rag-api always sends it (first follow-up, see the deferred list).
- **Local state (AppHost running, Open WebUI at the `open-webui` resource's http port):** department DoIT (SQL seed),
  collection `turkos` (public, published) with crawl source "umea.se Kommun och politik" (`maxPages` 200), site profile
  www.umea.se (sitevision), `Settings.Crawl`, `PiiOwnDomains`, clients "Open WebUI" (`open-webui`) and "Pilot retrieval
  (checks)" (`internal`) with one key each, assistant `turkos` v2 published to the Open WebUI client. User secrets now
  also hold `OpenWebUi:ClientId` and `Parameters:openwebui-api-key`. Open WebUI's first account (admin) is
  `demo@umea.se`; it and the keys were in the session scratchpad only (gone: reveal or re-issue).
- **`adm.py`** (scratchpad, gone next session): `/bff/login` follows to Keycloak's form, posts `rag-platform-admin` and
  the `Parameters:dev-user-password` user secret, then posts the `form_post` page (uppercase `<FORM`) to `/signin-oidc`;
  writes send `X-Requested-With` and `X-XSRF-TOKEN` (the `RAG-XSRF-TOKEN` cookie from `/bff/user`); send the body as
  UTF-8 bytes (`requests` sends a `str` body as Latin-1, which breaks `å`). `/openapi/v1.json` needs the session.
- **Stream B:** #264/#266 fixed on B's branch (667139c), waiting on my re-review; nudged #265.

## Session 16 (2026-11-01, new computer; ended: demo-first plan, AppHost left running)

- **User direction:** "try not overcomplicate every test etc.; adjust the plans so we can come to a complete demo / test
  of the platform with RAG / chat / crawling; after that dive deeper". Written down in **`plans/demo-path.md`** (the
  five steps, and what is deliberately deferred). Merge on green CI; no extra negative proofs unless a change needs one.
- **New computer set-up done:** `rag_key.txt` and `rag_gateway_url.txt` in the repo root (mode 600, in
  `.git/info/exclude`); AppHost user secrets hold `Gateway:BaseUrl`, `Gateway:Keys:Eu`, `FakeGateway:Enabled=false`, the
  `EmbeddingProfile:*` values (`text-embedding-3-large`, 1024, `RequestDimensions`, 8191, `Eu`) and
  `DevelopmentVolumes:Postgres=ume-rag-pilot-postgres`. The gateway answers `GET /v1/models` 200 with the key. Local test
  images `ume-rag.local/{postgres:ci,clamav:dev,parser:ci}` built. **Git over HTTPS needs a workaround here:** git's
  credential manager opens a dialog nobody answers; use `git -c credential.helper= -c 'credential.helper=!gh auth
  git-credential' fetch|push …` (no config change). `gh` works. `gh` acts as `ume-nichlas`, not `Crotalus`.
- **AppHost is RUNNING** (`aspire start`, `Seed__Enabled=false`, started 2026-11-01 ~18:16, dashboard
  `https://localhost:17470`): migrations finished, worker, api, adminapi, keycloak, parser, clamav, postgres up; **empty
  database**, no pilot data, no `open-webui` container (it starts when `OpenWebUi:ClientId` and
  `Parameters:openwebui-api-key` are set, #236 is done). Next: step 2 of `plans/demo-path.md`.
- **Doc restructure done** (`plans/doc-restructure.md`, 9 commits): the four passes are folded into the detail
  documents (present tense, no PR history) and marked not binding, each with a rule **ledger** at its end; precedence is
  **two layers: Decisions table (status column, rows `current` bind) > detail document** (AGENTS.md, plans/README.md,
  prompts); `to-verify.md` has a status column; P0 retired in place; `scripts/check-doc-refs.py` checks every `§`
  citation and link and runs in CI (job `doc-refs`). Follow-ups at the end of `plans/doc-restructure.md`. Design sync
  still to do for stream B's P1b items 9–10 (#254–#263) and later.
- **Merged by me/with the user's approval:** #244 (seed suffix), #245 (doc-refs CI), #268 (`CrawlFrontier.FetchUrl`, the
  crawl keeps and fetches the path form; #228 closed). `main` = `a588317` + later; CI green incl. `doc-refs` (run
  41333673980).
- **Open PRs of mine (green locally, runner jobs were finishing; merge needs the user's explicit OK, the auto-mode check
  blocks my `gh pr merge` otherwise):** #269 faster host-slot livelock test (it flaked ~1 in 12: monitor used `now()`;
  now 4 req/s, 2.6 s instead of 9 s, `clock_timestamp()`), #270 evaluator applies source demote (L3), #271 removes
  compatibility-only worker code. Worktrees `../rag-a2`, `../rag-a3`, `../rag-a4`.
- **Stream B (it merges its own PRs, often before my review lands):** merged #247 (gateway-address scrubbing), #254–#263
  (P1b item 9 admin API parts, item 10 parts a–b). **My reviews:** #263 and #264 changes requested; **#264 still open**
  (stacked, B's #267 on it): its required fix is that concurrent ratings of one message corrupt the thumbs totals
  (probe: 2 parallel ratings → both thumbs counted in 9 of 10). **Issues filed for B:** #265 (rag-admin's `org-sync`
  HttpClient keeps the standard resilience handler: Polly logs the gateway host at Warning/Error), #266 (issued
  conversation ids are not bound to the assistant; the writer's `ON CONFLICT ("Id")` doesn't check the owner; proven by
  probes), #246 (restructure notice). Decision taken on #264: API feedback reasons keep their own `ReasonCategory`
  (no CHECK on the column; chat §14.1 / admin §8.1 updated). New since the handover and not read: #249, #252, #256.
- **To do for stream A, small:** add `Security:ConversationIdKeys` (≥ 32 bytes per key, hand-off L6) to the Compose files
  and `init-deployment.sh`: `rag-api` refuses to start in Production without it. Not blocking the demo (Development gets a
  per-process key).
- **Lessons this session:** an implementor/agent report is a claim: check headings (never change heading text), run the
  ref check, spot-check as-built claims against the code. Resilience handlers (Polly) log exception text; a test that
  removes the handler from its client hides it. Timing assertions need `clock_timestamp()`, not `now()`, when sampling
  from the database. Squash-merged B PRs differ from the copies a branch carries: recreate the branch from
  `origin/main` and cherry-pick the stream-A commit instead of merging.
- **Scratchpad helpers do not survive:** `verify.sh <worktree> <cores> <branch|main> [full]` (build, Worker.Tests ×2,
  Pipelines, Contract, ownership; `full` adds the other projects and the smoke test), `watch.sh` (GitHub watcher),
  `adm.py` (to write: OIDC sign-in, XSRF header), `cells.py`. Recreate only what the demo needs (`adm.py`).

## Session 15 (2026-11-01, ended: moving to another computer)

- **Start:** `main` @ `1900916` green on the runner (run 41299302356) and locally (all suites; Worker.Tests 1116 ×2);
  session 14's last run on `9d3c7ae` green. AppHost still running with the Turkos data; worker, api and adminapi rebuilt
  and restarted on `1900916`. Scratchpad `verify.sh`, `watch.sh` (ignores Crotalus's PRs), `adm.py` (OIDC sign-in;
  Keycloak's form_post page is upper-case HTML; the post-login redirect to `/` 404s, ignore it), `ret.sh`
  (`/v1/retrieve` body is snake_case: `query`, `collections` slugs, `top_k`) re-created; the Open WebUI and retrieval keys
  revealed into the scratchpad `secrets/`.
- **User decisions:** gold set drafted by the main session, DoIT reviews (`13c32f4`); owner/date later; Open WebUI
  rehearsal waits for #236; **no backward compatibility before production** (`0d73352`: Decisions row, AGENTS.md,
  implementor-common.md; B told in #243); **don't nudge stream B** (it may idle and start later).
- **Merged:** #242 scheduled dependency audit (`audit.yml`, daily 04:17 UTC on the self-hosted runner; `scripts/audit/`
  shared with verify.yml); its runner run and a dispatched `audit.yml` run (41301857132) green.
- **Reviewed:** B's #241 (source demote) approved with two nits (doc summary misplaced; `(long, Dictionary)?` cache fields
  can tear on the singleton).
- **Gold set draft (item 21):** 199 rows (138 answerable, 21 temporal, 40 unanswerable) from 1,166 exported Turkos pages,
  ids checked; `~/ume-rag-pilot/gold/Turkos-guldstandard-utkast.xlsx` (Swedish review sheet for DoIT), `draft.csv`,
  `README.md` (known weaknesses). Spot check of 30 answerable questions on live Turkos: 29/30 found@10, 23 first, MRR
  0.83 (likely easier than real questions: DoIT should add real ones). With the user to hand to DoIT.
- **Stream-A branches ready, waiting for B to merge #239 and #241** (each carries B's unmerged commit, so no PR yet):
  - `a/228-fetch-url` (`../rag-a1`, `c6019e6`): crawl stores and fetches `FetchUrl ?? Url`; scope re-checks
    (`CrawlScope.CheckStored`), retry-failed, demote counts on the path form; a 304 or lastmod skip refreshes
    `Documents.Url`; `CrawlRig` drains by time (cold-start flake fixed). Local evidence green (Worker.Tests 1124 ×2,
    Pipelines, Contract); ownership fails only on #239's files. PR body in the implementor's report (see deviations 1–8).
  - `a/l3-eval-demote` (`../rag-a3`, `a605031`, on #241): evaluator applies `SearchScope.Demote`; contract case with
    recency + demote; Worker.Tests 1117 ×2.
  - `a/compat-sweep` (`../rag-a4`, `4246dff`, on `a/228-fetch-url`): removes the `-1` removal-check sentinel, the
    `Report.mode` incremental fallback and the pre-#130 eval-round fallback; new test pins "removal checks first".
    Worker.Tests 1124 ×2. PR after `a/228-fetch-url` merges. For B later (wording only, not filed):
    `Domain/Entities/IngestionEntities.cs:169` (`Depth = -1` history), `MigrationService/MigrationWorker.cs:10`
    ("expand/contract, backwards compatible").
  - Order: B merges #239/#241 → recreate each branch's PR on `origin/main` (merge main; B's commits drop out) →
    FetchUrl, then eval-demote, then compat-sweep → check main → restart the worker → re-crawl Turkos once.

**At the restart (session 16, on the user's other computer):**
- **Pull** `main` and fetch the three pushed stream-A branches (`git fetch origin`; `git worktree add ../rag-a1
  a/228-fetch-url`, `../rag-a3 a/l3-eval-demote`, `../rag-a4 a/compat-sweep`). They are pushed but have **no PR**: each
  carries B's unmerged #239 (and `a/l3-eval-demote` B's #241). When B has merged #239/#241: merge `origin/main` into each
  branch (B's commits drop out of the diff), run the evidence, open the PRs in the order above. PR bodies: write them
  from the bullets above (done-when, tests, negative proofs, deviations); FetchUrl's deviations: (1) the latest path form
  found in a new run wins, an admission without one keeps it; (2) a path form over 2,048 chars is stored NULL; (3)
  redirect targets keep `FetchUrl` NULL; (4) demote counts over `Documents.Url` only; (5) `scope-narrowed` prefers the
  frontier's `FetchUrl` over `Documents.Url`; (6) a 304's final URL replaces `Documents.Url`; (7) a row that redirected
  writes no `FetchUrl` on a lastmod skip; (8) the removal check's 2xx `HEAD` in an incremental run also writes the kept
  path form.
- **The gold-set draft is in the repo:** `plans/p1/gold-set/` (`Turkos-guldstandard-utkast.xlsx` for DoIT, `draft.csv`,
  `README.md`). Its `dokument_id`s are document ids in **this computer's** pilot database only. On a new database they
  won't match: map them by URL/title after the new crawl (`dokument_url` is the node-id form today; after `a/228-fetch-url`
  the crawl stores path forms, so match on the node id `4.<id>`/`5.<id>` inside the URL).
- **No pilot data on the new computer.** The Turkos crawl, the assistant `turkos`, the Open WebUI and retrieval keys,
  DoIT, the `GatewayModels` row and the `Settings.Crawl`/`PiiOwnDomains` values live in this computer's AppHost Postgres
  volume. Under "No backward compatibility before production" rebuild them there (session 14's list, in J7's order,
  through the admin API with a recreated `adm.py`), then crawl Turkos once **after** `a/228-fetch-url` is merged (one
  crawl gives readable citations; no re-crawl needed).
- **Needs the user's hands on the new computer:** the interim-gateway key (`rag_key.txt`, mode 600, excluded in
  `.git/info/exclude`) and the AppHost user secrets (`Gateway:BaseUrl`, `Gateway:Keys:Eu`, the `EmbeddingProfile:*`
  values, `DevelopmentVolumes:Postgres`, the `Parameters:*` secrets: `dotnet user-secrets list` here shows the key names,
  never copy values into the repo or a chat); local images `ume-rag.local/clamav:dev` and `ume-rag.local/parser:ci`
  (build from `src/parser`); the Aspire CLI 13.6.1 (Environment notes). Claude's memory folder is per machine: the binding
  preferences are in "How we work" and the Decisions table.
- **This computer:** the AppHost is still running with the pilot data (`aspire stop` keeps the volume); worktrees
  `../rag-a1`, `../rag-a3`, `../rag-a4`, `../rag-main`, `../rag-review` (all pushed or disposable). No agent running.
- **Open with B:** #239, #241 (approved, B merges); #232 (span exception events), #236, #237, #243 (no replies yet).
  Don't nudge B.

## Session 14 (2026-11-01)

- **Start:** `main` @ `366b4fa` green (runner + local). No stream-B activity since session 13; #223 still waits on B.
- **Pilot set-up (P1a item 20), through the admin API on the local AppHost** (`aspire start`, interim-gateway user
  secrets), as the dev `rag-platform-admin` (scripted OIDC sign-in, cookie jar in the scratchpad, `adm.py`; recreate it:
  `/bff/login` → Keycloak form → form_post to `/signin-oidc`; writes need `X-Requested-With: XMLHttpRequest` and
  `X-XSRF-TOKEN` from the `RAG-XSRF-TOKEN` cookie). In J7's order: public data-protection row; Open WebUI client + key;
  department **DoIT seeded locally** (no Data API key for the org sync: `cost centre dev-seed-doit`); collection
  **`Turkos`** (slug `turkos`, public, published); crawl source **"umea.se Kommun och politik"** (`startUrls`
  `/kommunochpolitik`, include `www.umea.se/kommunochpolitik` + `/**`, `maxPages` 2000, `piiMode: report`); umea.se
  site profile v1; `Settings.Crawl`: `municipalHosts` `umea.se`, `*.umea.se`, override `www.umea.se` 2 req/s, 2
  connections; `PiiOwnDomains` `umea.se`, `vakin.se`, `umeaenergi.se`, `visitumea.se`; an `internal` client
  "Pilot retrieval (checks)" + a key scoped to Turkos. Keys are in the scratchpad's `secrets/` only (gone next session:
  reveal or re-issue through the admin API). Decisions rows "Pilot set-up inputs" and "PiiOwnDomains".
- **The crawl** (run `01a19671-…`): complete, 1,168/1,168 seeds, 1,166 added (43 personal-data reports, none with a
  personnummer), 104 hub pages `parse.empty` (counted Unchanged by design, engine §10.2), 0 failed, 4,990 chunks;
  acceptance page live (date 2026-01-15); `/v1/retrieve` right page first for three questions (0.2 s warm). The
  gateway rate-limits embeddings (429 `gateway.rate_limited`, requeued without an attempt; drained fine). `costUsd`
  stays 0 (the interim gateway reports no cost?).
- **Bugs found live and fixed (stream A):** #229 scope patterns ran on the identity-rule form (`/4.<id>.html`), so a
  path include never matched on umea.se, and reconcile would have deleted node-id documents as `scope-narrowed`; #231
  host-slot livelock (every requeued fetch reserved a new slot; 10 jobs on a 1 req/s host never sent a request); #233
  the worker logged the gateway URL (HttpClient at Information; **the address appeared in the AppHost log and in the
  session-14 transcript**). Also merged: #227 (eval-set tests on FakeGateway ChatScripts), #230 (dev seed `piiMode
  report`). Filed for B: **#228** `CrawlFrontier.FetchUrl` (path-form URL: citations, demote, scope re-checks), **#232**
  Api/AdminApi log the gateway URL; #102 comment: demote matches `Documents.Url`.
- **Design sync** of #159–#226 folded in (`75e9d5f`, 9 design docs) plus connectors §5.2/§6.3 for #229/#231
  (`44fa1b2`). Not folded (need a call): #217 fired-types list in the PII report; `UrlCanonicalizer` → Pipelines
  (#178); identity §16 Q2–4, Q8 (gateway session); admin design says `GET /api/departments`/`/api/teams`, code has
  `/api/organisation/departments` with teams nested.

- **Later in session 14:** assistant **`turkos`** published (v1) on `claude-opus-5-5` (row seeded in the local
  `GatewayModels` mirror, `Source manual`), classified **`internal`** (a public assistant gets ceiling `External`, and
  only `Eu` keys exist: #237), contact `turkos@umea.se`, audience = the Open WebUI client; `/v1/models` with the Open
  WebUI key lists `turkos`; `/v1/chat/completions` needs the Open WebUI user JWT (401 `user_jwt_missing`, by design).
  The AppHost's `open-webui` runs only with the dev seed (#236 asks B for a configured client id + key). The user raised
  the interim gateway key's rate limit from 60/min (the crawl's 429s).
- **Merged later:** #240 PII patterns refuse a match inside an alphanumeric token (from a `PiiModeEngineTests` flake:
  a GUID's digits read as a phone number); B's #223 (chat schema) and #235 (age decay, approved). **Reviewed:** #238
  (gateway address out of logs/telemetry): B merged it 17 s after my change request, so the leak is on `main` — a
  failed request's span keeps an `exception` event whose message/stacktrace hold host:port (probe); **#232 still open** (comment with the evidence); #239 (`CrawlFrontier.FetchUrl`, chat ciphertext CHECKs) approved.
  Closed #234 (L2). `main` = `9d3c7ae` green locally (Worker.Tests 1116 ×2); its runner run was queued at the end.

**At the restart (session 14):** the AppHost was left **running** with the pilot data (Postgres dev volume from the
user secrets' `DevelopmentVolumes:Postgres`; blobs under `~/.ume-rag/blobs/rag`); `aspire stop` keeps both. Its worker
runs code up to #233 (`97c4adb`; #240 not deployed: restart it after pulling: build `src/Ume.Rag.Worker`, `aspire resource worker
restart`). Worktrees: `../rag-main` only. No agent running. Scratchpad keys (Open WebUI, retrieval) are gone: re-issue
or reveal through the admin API (`POST /api/keys/{id}/reveal`, audited).

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
- Worktrees: none. No agent running, no stream-A PR open.
- Open from B: #223 (chat schema), approved with a condition: the chat metadata retention lands in the same PR as the
  conversation writer (P1b item 10).

## Next step

**The demo runs (session 17). Next: the admin UI (user: "more focus on the admin UI"; `plans/demo-path.md` "Next focus",
stream B), the "Found by the demo" items (#275, #276), the draft gold set over the demo slice, then the deferred list in
the user's order.** The steps below are how
the demo was reached.

1. **Check the stack and GitHub** (5 minutes): `aspire describe` (if the AppHost is down, `export Seed__Enabled=false; aspire start
   --apphost src/Ume.Rag.AppHost/Ume.Rag.AppHost.csproj --non-interactive`; `aspire` is
   `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire`); open PRs and CI; ask the user to merge
   #269–#271 (or merge them if allowed).
2. **Set-up through the admin API** (recreate `adm.py`: `/bff/login` → Keycloak form with the dev `rag-platform-admin` →
   form_post to `/signin-oidc`; writes need `X-Requested-With: XMLHttpRequest` and `X-XSRF-TOKEN` from the `RAG-XSRF-TOKEN`
   cookie). Order in session 14 below (J7). Crawl slice: `maxPages` ≈ 200.
3. **Crawl once**, check `/v1/retrieve` (body is snake_case: `query`, `collections` slugs, `top_k`), then **Open WebUI**
   (secrets `OpenWebUi:ClientId`, `Parameters:openwebui-api-key`; restart the AppHost) and ask a question.
4. **Record the result** (progress.md, this file). Then, and only then, the deferred list of `plans/demo-path.md`.

**After the demo, in this order (user decides):** gold set → DoIT, three-way evaluation, thresholds (item 21); EU-route capacity
(item 19); stream-A leftovers (P1b item 15 `e2e`, item 17 importer settings, pin review, Aspire 13.6.4, `ConversationIdKeys`
in Compose); design sync of stream B's merged items 9–10; review B's fixes for #264/#265/#266 when they come (don't nudge);
the rest of umea.se when wanted.

**Open issues to stream B:** #80, #90, #102, #129, #132, #75, #52, #228, #232, #236, #237, #243 (#134 closed by B). Stream A's own: #48, #51. For the user: #100
(L8 run steps), #215 (J8, realm additions for the gateway session). Hand-offs received: #193 (J7), #210 (J9).

**Waiting on the user:** the pilot URL list and upload (Next step 2); a Data API key (`catalog.read`) for the org and model syncs (DoIT is a local seed until then); the gateway session (release 1 incl. the embedding-batch
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
