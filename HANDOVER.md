# Handover

State of the Ume.Rag work for the next session. Rewritten 2026-11-13, end of session 32 (`main` at `b26d877` plus
this file; **last release `v0.4.0`**, published 2026-11-12 from `714a58f`; **a dry run of 0.5.0 passed from
`a49c848`**, run 43387199025). Earlier sessions are in the git history (`git log -- HANDOVER.md`). Repo
`~/research/rag` on this machine (`~/git/ume-rag-platform` on the session-19 machine), branch `main`, remote `origin` =
https://github.com/thekim1/ume-rag-platform (push after every commit).

## What this is

**Ume.Rag** (working name, N1) is a RAG / crawling / chatbot platform for Umeå kommun: small .NET 10 services on top
of the team's **LLM gateway** (https://github.com/thekim1/llmgateway, `~/git/llmgateway`), as an add-on to it (Decisions
"Shared sign-in with the gateway"). The user owns the gateway and wants to do better than **Eneo**, a slow "kitchen
sink" monolith. Design and code live in this repo.

## Where to read

1. [`plans/decisions.md`](plans/decisions.md): the decisions in force, one entry per topic, every entry binds. The old
   `01-general-idea.md` §10 table is `plans/archive/decisions-log.md` (verbatim, doesn't bind). Record a decision by
   changing or adding an entry, then a dated line under the log's *Later decisions*.
2. **`plans/roadmap.md`**: done, what is left before go-live, the live block, waiting on people, P2/P3. One page.
3. **`plans/work/`**: the queues `stream-a.md`, `stream-b.md`, `main.md`; results in `results-a.md`/`results-b.md`.
4. The design for the area at hand (map in `plans/README.md`). History is in `plans/archive/` and does not bind.
5. **`plans/cases/support-assistant/`**: the first case plan (internal support assistant, DoIT).
6. Not binding: `plans/platform/upload-quality-review.md` (upload gaps U1–U7, decisions still to take).

## Start here: a look at CI (user, 2026-11-13)

The user wants the next session to **take a quick look at the GitHub Actions workflows and see whether they can run
faster or less often**. It's a review first: propose, and change only what the user agrees to. The binding rules today
are repo-and-deployment §8.2 (lean pull requests) and the *Lean CI* preference below; `docs/ci-runner.md` has the
runner notes.

**What runs now** (`.github/workflows/`, all on the two self-hosted runners `ume-rag` and `ume-rag2`, which share one
Docker daemon and one user home):

| Workflow | When | Jobs | Typical time |
|---|---|---|---|
| `verify.yml` | every non-draft PR; dispatch; called by release | `changes`, `ownership`, `doc-refs`; then by path: `backend` (builds the Postgres and ClamAV images, `dotnet build` + `dotnet test --solution` without Flaky and EvaluationSmoke), `evaluation-smoke`, `admin-ui`, `deploy-checks`, `images`, `renderer` | median 7 min per PR, max 34; `backend` 6–7 min on every code PR |
| `parser.yml` | PRs (its own path filter), dispatch, called by release | `changes`, `parser`, `parser-image`, `contract` | usually skipped (0.2 min), 3.4 when it runs |
| `release.yml` | tag push; dispatch = dry run | `verify` + `parser` (called), `build` (images, SBOM, bundle, smoke test in a local registry), `e2e` (optional), `publish` | about 17 min |
| `e2e.yml` | dispatch or called by release when ticked | the browser tests (9 tests, ~10 min) | by hand only |
| `audit.yml` | nightly 04:17 and dispatch | `dotnet`, `admin-ui`, `parser` vulnerability audits | — |
| `evaluation.yml`, `perf.yml`, `entra.yml` | dispatch (entra also on its paths) | evaluation on a real gateway, capacity runs, Entra plan/apply | rarely |

**Observations from session 32, to start from (not yet verified as the best fix):**
- **`backend` runs on docs-only and deploy-only PRs.** The `changes` filter's `code` output is "anything besides
  `plans/`, `platforms/` and root `*.md`", so a PR that changes only `docs/**` (#641: 6 min) or only `deploy/**` +
  `docs/**` (#643) runs the full 6–7 minute backend build and test. `docs/**` and most of `deploy/**` build nothing
  the backend tests read. Check what does read them first (the doc-refs check, `deploy/postgres/*.sql` in the
  integration tests, the release bundle) before narrowing the filter.
- **`backend` builds the Postgres and ClamAV images on every run** before the tests, even when `images/` is
  unchanged. Look at whether the tests need fresh images or could reuse a tagged one (layer cache, or build only on
  change).
- **`dotnet test --solution` runs every test project** for any code change: a worker-only PR also runs the API,
  AdminApi, Answering, integration and contract tests. Per-project selection by path is possible but risky (shared
  Infrastructure: the API's `sql-shapes.json` covers Infrastructure statements, see *Lessons*); weigh it against the
  6–7 minutes.
- **Both runners share one Docker daemon**: a PR's backend run and another's container tests compete, and the flaky
  renderer test (#645) failed both times with another job on the host. The open proposal (*Waiting on the user*:
  one runner per Docker host, or release on GitHub-hosted runners) belongs to this review.
- `evaluation-smoke` (about 1 min) runs whenever the worker, retrieval, answering, API, infrastructure, pipelines or
  domain change, which is most code PRs.
- Stream b's PRs are the bulk of the runs (it splits items into parts and pushes several fixes per PR). Draft PRs
  skip everything; pushing as draft until ready is one lever that costs no workflow change.
- How to measure: `gh run list --workflow verify.yml --limit 30 --json …` and `gh run view <id> --json jobs` give job
  start and end times; session 32's numbers above came from the last 30 runs.

## Then

**1. GitHub:**
- **#642** (stream b): the read-back's reason is in no view, the shared-key note shows on every key, and `messages.ts`
  lacks texts for `gateway.key_unavailable`/`gateway.delegation_not_applied` (and `gateway.budget_exhausted` still
  blames "the platform's budget"). If stream b says the reason belongs on the platform's *Gateway models* view, that
  is stream a's `platform/**`.
- **#580**: the gateway owner was asked whether the gateway's request list gets a `runId` filter (gateway-changes
  §7.1 describes v0.10.1 as built). Keep #580 open until the live checks pass.
- **#645**: the flaky renderer test (twice in CI, both with another job on the Docker host).
- Review stream b's new PRs (it merges its own); its open queue is B-42–B-46's live checks.

**2. Stream a's queue is empty** apart from live checks: A-34 (worker as the knowledge base's key) and A-35 (a real
read-back) both wait on the interim gateway.

**3. The V39 rule needs the user's confirmation:** once the gateway acts as keys, a published assistant's key can't be
cleared (#618 review, built). Ask.

**4. Before tagging 0.5.0:** the dry run passed from `a49c848`; only A-40 (#646, worker metrics) landed after it. The
upgrade guide says **rolling update: no** (`DailyTotalsWithoutCost`), and names `UME_RAG_GATEWAY_INTEGRATION_API_URL`
and the `usage.read` scope. The CHANGELOG is current through #643 (#646 needs a line).

**Waiting on the user:**
- **The interim gateway (J10) on v0.10.1** (#86: a refusal of an assigned key is always `403`), with the integration
  *Ume.Rag* and three credential keys (`rag-api`, `rag-admin` with `organisation.read` and `usage.read`, `rag-worker`
  capped at `OnPrem`+`Eu`). Every live check of B-35–B-46, A-31, A-34 and A-35 waits on it. Gateway setup:
  `docs/production-setup.md` *Gateway credential keys* and *Cost and budget read-back*.
- **The V39 rule** above.
- **Local user secrets** (see *Environment notes*): the AppHost refuses `Gateway:Keys:Eu` and `ClamAv:Enabled`.
- **CI runners** (proposal, "write it down, change nothing yet"): `ume-rag` and `ume-rag2` share one Docker daemon and
  one user home. A: one runner per Docker host; B: `release.yml` on GitHub-hosted runners. Part of the CI review.
- **Upload gaps** from `upload-quality-review.md`. Suggested: picture descriptions (U1, needs a decision entry) and
  rerank on (U4, when the gold set shows a gain), then `.msg`/`.eml` (U2), a contextual-header trial (U5) and a
  near-duplicate warning (U6).
- **Ask-a-file workspaces** (P2): owned by teams and individuals, against "Organisation" (departments own all content).
  Answer before P2 (`plans/work/main.md` M-11).
- **LibreOffice placement**, decided again before go-live with the parser sandbox question (Decisions "Parser").
- **Support-assistant proof of concept** (`plans/cases/support-assistant/07-rollout.md` §0b, P1–P8): needs the
  documents and about twenty anonymised tickets. It goes ahead of everything else once they exist.
- The live block's inputs: gold set with DoIT, G1/DPIA, test host, U1, pilot accounts on the gateway.
- **#511** (split the worker into libraries): only if it keeps growing in P2.

**Stream b** (remote, GitHub only) has its own main session (design PRs on `main-session/` branches, decisions in
`plans/decisions.md`). It works fast (B-39–B-46 and B-48 in one night), splits items into parts, answers review
findings with PRs rather than comments, and takes stream-a paths on `a/` branches when the user asks. It announces its
work on #580. All of B-35–B-48 is merged; B-42–B-46 are unticked until their live checks.

## Done in session 32 (2026-11-12 evening to 2026-11-13 morning)

- **Gateway v0.10.1** checked in the gateway's code and put in the design (`5b0526f`): a refusal of the assigned key
  is `403`; a `401` is the credential key's. Stream b fixed the code (#629).
- **Reviews of stream b** (agents, findings verified in the code): #616/#617/#618 (two medium, three low; design
  settled in `4fba45d`; stream b fixed all in #618, #631), #625 and #627 (no blocking findings).
- **Stream a, merged:** A-34 #621 (the worker as the knowledge base's key), A-38 #623 (the gateway's limits and
  version in the platform UI, checked in the browser), A-39 #626 (`content.rag_retention_content_step`), #640 (the
  gateway links' fallback, #635), A-32 #641 (the cost runbook), A-35 #643 (`UME_RAG_GATEWAY_INTEGRATION_API_URL`, the
  firewall, the runbook's refusals, `admin-console` in production setup; #630), A-40 #646 (`rag.embed.tokens`,
  `rag.ingestion.cost`). A-34 and A-35 stay unticked for their live checks.
- **Design:** B-38's deviations settled (`be37df0`), B-48 queued and built by stream b; V39 refuses clearing a
  published assistant's key; the query-embedding cache per acting key; gateway-changes §7.1 as built.
- **Docs and changelog:** #620 (upgrade guide's migrations, rolling update no), #622 and #644 (CHANGELOG through #643).
- **Issues:** closed #535, #604, #606, #630, #635 (stream b closed #619, #624); filed #619, #624, #642, #645.
- **Two release dry runs**, both green, the second from `a49c848` with the content retention migration and A-35.

## Open

- **#513** stays open until its items close; **#550** until stream b acknowledges; **#580** until the live checks of
  phases 2–3 pass.
- **#645**, the flaky renderer test.
- The live block (M-4), in the roadmap's order.

## Local stack (this machine)

- **State at handover:** the dev AppHost is **stopped**. No ClamAV container exists any more (the "persistent" one
  was gone before session 31 started; its signatures are still in the volume `ume-rag-clamav-db`). Migrations through
  `AnswerLengthFromModel` are applied on the dev database; the next start applies the rest (`KeywordStatistics`,
  `UploadScannedAt`, `GatewayKeys`, `GatewayModelLimitsSource`, `DailyUsage`, `ObjectGatewayKeys`,
  `DailyTotalsWithoutCost`). After `UploadScannedAt` every existing upload row has `ScannedAt` null: run the
  upgrade guide's one-line `UPDATE` so scanned files don't read as unscanned. It won't start until the user secrets
  change (*Environment notes*). `Settings.Chat` is `{}`. The local parser image is older than #526 (no LibreOffice):
  **rebuild it before demoing `.doc`/`.xls`/`.ppt`/`.rtf`**.
- **A stack on the FakeGateway starts without touching the user secrets or the dev data** (done in session 31 to check
  #599): `export FakeGateway__Enabled=true Seed__Enabled=false Parser__Mode=off Render__Mode=off
  OpenWebUi__Enabled=false DevelopmentVolumes__Postgres=<a throwaway volume name>` (add `ClamAv__Mode=off` to skip
  ClamAV), then `aspire start …`; afterwards `aspire stop --all` and `docker volume rm` the volume.
- Start: `export Seed__Enabled=false; aspire start --apphost src/Ume.Rag.AppHost/Ume.Rag.AppHost.csproj
  --non-interactive` (Docker up: `docker info`). Stop: **`aspire stop --all --non-interactive`**. Restart one service:
  `aspire resource worker restart --apphost … --non-interactive`.
- **E2E locally without pwsh:** `aspire stop --all`; `docker volume rm ume-rag-e2e-postgres`; export
  `DevelopmentVolumes__Postgres=ume-rag-e2e-postgres Seed__Enabled=true FakeGateway__Enabled=true E2E__Enabled=true`;
  `aspire start --apphost src/Ume.Rag.AppHost/Ume.Rag.AppHost.csproj --non-interactive`; `aspire wait admin-ui
  --apphost …`; in `tests/e2e`: `npm ci`, `npx playwright install chromium`, `npx playwright test` (9 tests, ~10 min).
- Aspire CLI: `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire` (not on PATH).
  `aspire logs worker --non-interactive`; `aspire describe --apphost …` shows resource states.
- Ports: dashboard `https://localhost:17470`; **admin UI `https://localhost:5174`** (user `rag-platform-admin`); api
  7401, admin API 7402, worker 7403; **Open WebUI `http://localhost:7410`**, Keycloak 7411, Postgres 7412, parser 7413,
  ClamAV 7414, **renderer 7417**. Platform objects are under `/plattform/<resource>` (e.g. `/plattform/settings`).
- **Parser** goes cold after 15 idle minutes; `PARSER_IDLE_STOP_S=0` / `NER_IDLE_STOP_S=0` keep it warm.
- **Data:** department DoIT.
  - **Collections:**
    - `turkos`: 1,166 live documents, about 10,000 chunks, a 100-question evaluation set;
    - `renderer-demo`: quotes.toscrape.com, `browser` and `http`;
    - `a13-klimatkollen`: 25 pages, browser;
    - `a19-e-umea`: 137 pages, browser, no `readySelector`;
    - **`Personalhandbok`** (new, internal): one *Files* source with `Handbok/HR/…`, `Handbok/IT/…`,
      `Handbok/kontorstider.txt` and `IT/VPN/vpn-guide.md` indexed, plus `IT/VPN/tom-rubrik.md` refused
      (`parse.empty`). Made as test data; keep it as a demo or delete it.
  - **Assistant:** `turkos` v2 on `claude-haiku-5-5`.
  - **Crawl settings:** `hostOverrides` only `www.umea.se` (2 req/s, 2 connections).
  - **Gateway models:** manual mirror rows `claude-haiku-5-5`, `claude-opus-5-5` and `Cohere-rerank-v5-fast`; model
    sync is off locally, so the embedding profile's model shows "Not among the gateway's models" (expected).
  - **Embedding profile:** `pilot-embedding`, in use, unchanged (a **Test** call was made, no save).
- **Admin API scripting:** `~/ume-rag-tools/` (outside the repo, no secrets in it): `admin.py` (BFF login from the
  AppHost user secret, XSRF; `python3 -I admin.py GET /api/assistants`, or `import admin; admin.call(m, path, body)`;
  raw uploads through `admin.req` with `X-XSRF-TOKEN` and `X-File-Name`), `pf.py '<preflight JSON>'`, `runwait.py
  <sourceId> [start]`, `chat.py <n> <label> <out.json> [gap s]`. Run them from that folder.
- Feedback importer: every minute, user secret `Parameters:openwebui-admin-api-key`. Logins:
  `~/ume-rag-demo-login.txt` (mode 600; port 7410). Draft gold sets: `~/ume-rag-gold-draft-*.csv`, `plans/gold-set/`.
- Postgres: `docker exec -e PGPASSWORD="$(docker exec <postgres-…> printenv POSTGRES_PASSWORD)" <postgres-…> psql -h
  localhost -U postgres -d ragdb` (schemas `config`, `content`, `chat`).
- Local images: `ume-rag/renderer:ci` (also `ume-rag.local/renderer:ci`; the container test uses
  `UME_RAG_TEST_RENDERER_IMAGE=ume-rag.local/renderer:ci`), `ume-rag.local/clamav:dev`, `ume-rag.local/parser:ci`
  (pre-#526), `ume-rag.local/postgres:ci`.
- `~/git/llmgateway`'s working tree is still at v0.9.0; `origin` is fetched through **v0.10.0** (tag). Read the
  gateway with `git show v0.10.0:<path>`, or pull first. Where its docs and its code differ, follow the code.
- Worktrees: none besides the main clone (the agents' worktrees and the `../rag-*` ones were removed).
- The 13.6.1 CLI above starts the 13.6.5 AppHost fine (checked this session).

## How we work (user preferences, binding)

- **Good and easy to use:** look at pages in the browser, not only tests; fix friction or file it.
- **Demo first; admin UI focus.** No extra negative proofs or flake hunts unless a change needs one. No tests that
  depend on timing ("wait until X, fail after 60 s" is fine).
- **Approve and merge without asking** when a PR is ready (reviewed, checked live where relevant, CI green with the
  tests actually run); tell the user afterwards. Design, scope and destructive actions go to the user.
- **Handle GitHub on your own:** answer new or updated issues and PRs; close verified-done issues with an evidence
  comment.
- **Stream B** is a remote agent (GitHub `thekim1`) with its own coordinating main session on another machine; talk
  only through GitHub (labels `stream-b`, `stream-a`, `main`). B merges its own PRs; review its security-relevant parts.
  Before taking B's paths, check for open `b/…` work and announce it in an issue (as #528 did). `gh` acts as
  `ume-nichlas`. Stream b's agent sometimes does user-requested stream-a work on `a/` branches (#526).
- **Main session** owns `plans/**`, root `*.md` and `.github/stream-ownership.txt` (commit to `main`). Stream-A work
  on `a/<id>-<slug>` branches; a PR touching another stream's paths uses a `main-session/<slug>` branch. The user may
  give the main session a whole feature to build (upload knowledge bases, 2026-11-09).
- **Lean CI:** PR jobs path-filtered; full set before a release, **plus a release dry run**; `e2e` alone on the
  runners; agents run only the test projects they touch; no re-dispatching `verify` on main.
- **Subagents:** own worktree (`../rag-<id>`), full brief, no touching the live stack unless the brief says so. Their
  report is a claim: read the diff, re-run the tests, check CI's test totals, check live, spot-check line numbers.
  Splitting a feature into an API agent and a UI agent against a contract written first in the design worked well
  (#532); integrate on one branch and check both sides agree.
- **Shared files** (`Directory.Packages.props`, `README.md`, `CHANGELOG.md`, `UmeRag.slnx`, `.gitignore`): small PR,
  append-only. Secrets never go into the repo, a log or a chat. Verify claims against real sources.
- **No backward compatibility before production:** no migration data steps for old rows (fix development data by
  hand, and put the SQL in the PR), no shims.

## Lessons

Development and CI gotchas are in [`docs/local-development.md`](docs/local-development.md) (*Working notes*) and
[`docs/ci-runner.md`](docs/ci-runner.md) (*Working with the workflows*), where stream agents read them too. What only the
main session needs:

- **Stacked PRs:** merging the base PR with `--delete-branch` *closed* the stacked PR (#579) instead of retargeting
  it. Retarget first (`gh pr edit <n> --base main`), then merge the base. To recover, push the old base sha back to its
  branch, reopen, retarget.
- **After a GitHub outage** runs can sit `queued` with jobs that have a start time but no runner. Cancel and re-run
  them (the user allows cancelling stuck CI).
- **Check a watcher's exit code before merging:** `gh run watch … --exit-status; echo exit=$?` printed `exit=1` and
  I merged anyway (#582 was docs only; the failure was the flaky test of #584). Read the result first.
- An `a/` branch may not touch `plans/**` (`ownership`, main's path): put design lines in a direct `main` commit.
- The auto-mode classifier blocks `commit --amend` + force-push even on your own branch: push a follow-up commit.
- New worker background services open connections on the worker role: count them against data-model §1.1's budget
  (pool 10 + LISTEN + 1); `DispatcherTests.Under_load_…_role_cap` catches an extra session.
- **`scripts/check-doc-refs.py`** resolves a bare `name.md §N` by file name: a new file with a common name (`plan.md`)
  captures old references. Give plan files distinctive names.
- The `implementor` agent refuses git writes (commit, rebase): review its diff, commit and rebase yourself.
- **`gh pr merge --delete-branch` also removes the branch's local worktree.** Stop any stack running from that
  worktree first (`aspire stop --all`), and merge without `--delete-branch` while an agent still reads it.
- **The auto-mode classifier** blocks force-pushing another session's branch and approving a PR that contains your
  own commit. To bring stream b's PR up to date, push a plain merge commit (or `gh pr update-branch <n>`); then the
  merge needs the user's go-ahead or stream b.
- A new GitHub advisory turns every backend job red through `dotnet list package --vulnerable` (NU1903) with no code
  change: check the audit step first when an unrelated PR fails.
- Squash-merged branches look unmerged to git: check the PR state (`gh pr list --state all`) and `git rev-list
  --count main..<branch>` before `git branch -D`.
- When editing a stream's queue on `main`, `pull --rebase` first: stream B edits queues too.
- **Browser checks with the Playwright MCP:**
  - The admin UI's dev certificate isn't trusted. Open a new context with `ignoreHTTPSErrors: true` inside
    `browser_run_code_unsafe`.
  - State doesn't survive between calls, so save `storageState` to a scratch file after logging in and reuse it.
  - The code can't read files. To log in without the password entering the transcript, serve it once from a
    short-lived `python3 -m http.server --bind 127.0.0.1` in a mode-700 scratch folder and read it with
    `page.request.get`. Then stop the server and delete the file.
  - Don't `pkill -f` a pattern that also matches your own shell command.
- **Release dry runs:**
  - The runners share one user's `~/.docker/config.json`; a tag release's ghcr login used to stay there and, once
    expired, broke a later dry run's pulls ("failed to fetch oauth token: denied"). #572 gives the build job its own
    `DOCKER_CONFIG`. The stale entry is still in that home: other workflows that pull from ghcr may hit it.
  - When a run fails at an artifact upload with `Unexpected token '<'`, check https://www.githubstatus.com first.
  - The smoke test can't re-run `init-deployment.sh` on the runner (not root; the files belong to the containers'
    users after the first run); verify.yml's deploy-checks test the re-run.
  - Measure before optimising: v0.3.0's smoke test took 1 min 51 s; the time was in image builds and pushes.
- **A PR that changes `deploy/postgres/*.sql`** must also regenerate `deploy/portainer/compose.portainer.yaml`
  (`deploy/portainer/build-stack.sh <gateway compose file>`), or the Portainer stack lacks the change (#566 caught
  B-30's grants missing).
- **xunit v3 test output** (`TestOutputHelper.WriteLine`) shows only in a report: `--report-xunit-trx --results-directory
  DIR`, then the TRX's `<Message>` elements.
- **Playwright sign-in on the admin UI:** click `locator('a,button').filter({ hasText: 'Sign in' }).last()` and wait for
  the Keycloak URL together with the click.
- **Agents as stream-a implementors** work well with `isolation: worktree`: review the diff, commit on an `a/` branch,
  rebase on `origin/main`, open the PR, watch CI, remove the worktree, then merge with `--delete-branch`. Check that a
  short CI job really ran its steps (a 25 s `deploy-checks` did).
- **Gateway docs vs code:** the gateway's `docs/integrations.md` and OpenAPI said 403 where the code answers 401. Check
  behaviour Ume.Rag depends on in the code at the tag.
- **Agent worktrees start from local `main`, not from the branch that is checked out.** An agent told "your branch
  contains X" didn't have X (A-36 on B-47 part 1): merge the prerequisite first, or tell the agent to check
  `git log -1` and name the commit it needs. Both agents coped (one applied the missing files unchanged).
- **Bringing an agent's work to a PR:** commit it in the agent's worktree, `git worktree add ../rag-<id> -b <branch>
  origin/main`, `git cherry-pick -n <agent branch>`, resolve, build, re-run the tests there, commit, push. Afterwards
  `git worktree unlock` and `remove --force` the agent's worktree and delete its `worktree-agent-…` branch.
- **Two open PRs with an EF migration each:** the second to merge regenerates its own. Keep a copy of the migration's
  `.cs`, remove it and its designer, restore `RagDbContextModelSnapshot.cs` from `main`, `dotnet ef migrations add
  <Name> --project src/Ume.Rag.Infrastructure --startup-project src/Ume.Rag.Infrastructure --output-dir
  Persistence/Migrations`, compare the new `.cs` with the copy (it should differ only by the header and hand-written
  SQL) and put the copy back under the new timestamp. The file needs `// <auto-generated />` as its first line or the
  build fails on style rules. A new worktree needs `dotnet restore` first. Tell the other stream on its PR.
- **`src/admin-ui/src/utils/labels.ts` and `messages.ts` are stream b's paths**, so a stream-a UI item that needs a
  text goes on a `main-session/` branch (A-33, #609), or `ownership` fails.
- **Regenerating the Portainer stack:** `git -C ~/git/llmgateway show v0.10.0:deploy/compose.portainer.yaml >
  <scratch file>`, then `deploy/portainer/build-stack.sh <scratch file>`. Any PR that changes
  `deploy/postgres/app-roles.sql` leaves the stack stale until someone does this (#605 and #608 both did; #611 fixed
  it). An agent's sandbox may refuse `git -C` on the gateway clone; it then fetches the file with `gh api … ?ref=v0.10.0`.
- **Waiting on a PR's checks in a loop:** test for an empty `conclusion`, not for `status != COMPLETED`; right after a
  push the rollup lists checks whose status is already final-looking while the conclusion is still empty.
- **A `cd` into an agent's worktree makes it the session's working directory.** Use `git -C <path>` for other
  worktrees, and start each command with `cd /home/crotalus/research/rag`.
- **`rm` with a shell variable in the path is stopped by a safety check.** Use literal paths, or `git rm` for tracked
  files.
- **SQL in `src/Ume.Rag.Infrastructure/` is in the API's SQL contract** (`tests/Ume.Rag.Api.Tests/sql-shapes.json`),
  not only the worker's: an agent that adds a statement there must also run `tests/Ume.Rag.Api.Tests` (regenerate with
  `UME_RAG_UPDATE_CONTRACTS=1`). A-39's agent ran only the worker tests and CI failed.
- **A browser check without the password in the transcript:** a Node script under the scratchpad that requires
  `tests/e2e/node_modules/playwright`, reads `Parameters:dev-user-password` from the AppHost user secrets itself and
  writes screenshots (session 32, A-38). Simpler than the Playwright MCP route.
- The Edit tool fails on a file not Read in this session. A commit made in the same step then goes out without the
  change, so check `git show --stat` after a combined edit-and-commit.

## Environment notes

- `rag_key.txt` in the repo root holds the interim-gateway key (mode 600, excluded in `.git/info/exclude`); the
  address and the same key are in the AppHost user secrets (`Gateway:BaseUrl`, `Gateway:Keys:Eu`). Never commit,
  print or move them.
- **`ClamAv:Enabled` is gone** (#599): the AppHost stops on it. Use `ClamAv:Mode` = `on` | `off`.
- **The AppHost won't start with these user secrets:** since B-37 it refuses a `Gateway:Keys` section ("give each
  service its credential key as `Gateway:Services:<service>:Key`"). The user moves the key, or gives the three
  credential keys once the interim gateway runs v0.10.0. `Parameters:gateway-key-{eu,onprem,external}` and
  `Parameters:data-api-key` are stale too (`docs/local-development.md` lists the current parameters).
- **Git over HTTPS:** `git -c credential.helper= -c 'credential.helper=!gh auth git-credential' fetch|pull|push …`.
- `172.30.0.0/16` is taken on this machine; test networks use 10.21x.0.0/24.
