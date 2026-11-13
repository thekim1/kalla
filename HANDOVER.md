# Handover

State of the Ume.Rag work for the next session. Rewritten 2026-11-13, end of session 32 (`main` at `a8da614` plus
this file; **last release `v0.4.0`**, published 2026-11-12 from `714a58f`; a **dry run of 0.5.0 passed** from `be37df0`).
Earlier sessions are in the git history (`git log -- HANDOVER.md`). Repo `~/research/rag` on this machine
(`~/git/ume-rag-platform` on the session-19 machine), branch `main`, remote `origin` =
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

## Start here

**Stream a's queue is empty apart from live checks and A-40.** All of gateway phases 1–3 and cost attribution are
built on both streams; what remains is live checks on the interim gateway and small follow-ups:

**1. A-40** (the worker's `rag.embed.tokens` and `rag.ingestion.cost`, engine §17): the next stream-a code item, waits
on nothing. Then point the runbook's cost section at it.

**2. GitHub:**
- **#642** (stream b): the read-back's reason in no view, the shared-key note on every key, and the missing
  `messages.ts` texts for `gateway.key_unavailable`/`delegation_not_applied` (and `budget_exhausted` blaming "the
  platform's budget"). If stream b says the reason belongs on the platform's *Gateway models* view, that's
  stream a's `platform/**`.
- **#580**: the gateway owner was asked whether the request list gets a `runId` filter (gateway-changes §7.1 now
  describes v0.10.1 as built).
- **#645**: the flaky renderer test, now filed (twice in CI, both with another job on the Docker host).
- Review stream b's new PRs (it merges its own); its queue now has B-42–B-46 waiting only on live checks.

**3. The live block, once the user has the interim gateway on v0.10.1:** A-34, A-35 (a real read-back) and every
B-35–B-46 live check. **The V39 rule** (a published assistant's key can't be cleared once `act-as-key` is on) is
built (#618 review) but the user hasn't confirmed it: ask again.

**4. Before the next release (0.5.0):** the dry run passed from `be37df0`, but much landed since (B-39–B-48, A-32–A-39,
the content retention migration). Run the dry run again and the full CI set; the upgrade guide says **rolling update:
no** and names `UME_RAG_GATEWAY_INTEGRATION_API_URL` and `usage.read`. The CHANGELOG is current through #643.

**Waiting on the user:**
- **The interim gateway (J10) on v0.10.1** (not v0.10.0: #86 makes a refusal of an assigned key always `403`), with the integration *Ume.Rag* and three credential keys (`rag-api`,
  `rag-admin` with `organisation.read`, `rag-worker` capped at `OnPrem`+`Eu`). **It is needed now:** B-35 is merged,
  so the organisation, keys and model syncs read only `/v1/ume/context` and `/v1/models`, which v0.9.x doesn't have.
  Every live check of B-35–B-46, A-31, A-34 and A-35 waits on it. Gateway setup: `docs/production-setup.md` *Gateway
  credential keys*.
- **Local user secrets** (see *Environment notes*): the AppHost refuses `Gateway:Keys:Eu`, and since #599 also
  `ClamAv:Enabled` (use `ClamAv:Mode`).
- **Upload gaps** from `upload-quality-review.md`. Suggested: picture descriptions (U1, needs a decision entry) and
  rerank on (U4, when the gold set shows a gain), then `.msg`/`.eml` (U2), a contextual-header trial (U5) and a
  near-duplicate warning (U6).
- **Ask-a-file workspaces** (P2): owned by teams and individuals, against "Organisation" (departments own all content).
  Answer before P2 (`plans/work/main.md` M-11).
- **LibreOffice placement**, decided again before go-live with the parser sandbox question (Decisions "Parser").
- **CI runners** (proposal only, user: "write it down, change nothing yet"): `ume-rag` and `ume-rag2` share one Docker
  daemon and one user home. A: one runner per Docker host; B: `release.yml` on GitHub-hosted runners.
- **Support-assistant proof of concept** (`plans/cases/support-assistant/07-rollout.md` §0b, P1–P8): needs the
  documents and about twenty anonymised tickets. It goes ahead of everything else once they exist.
- The live block's inputs: gold set with DoIT, G1/DPIA, test host, U1, pilot accounts on the gateway.
- **#511** (split the worker into libraries): only if it keeps growing in P2.
- From the gateway owner on #580: the gateway's docs say `403` for a revoked or expired assigned key, while the code
  answers `401` (Ume.Rag follows the code). Not built and not asked for: a platform-wide cap on acted requests, the
  Graph group lookup, tenant and guest refusal.

**Stream b** (remote, GitHub only) has its own main session (design PRs on `main-session/` branches, decisions in
`plans/decisions.md`). It works fast (B-35 parts 2–3 and B-36 in one evening), splits items into parts, and also takes
stream-a paths on `a/` branches when the user asks. It announces its work on the issue (#580). B-42 and B-47 are merged (B-42 unticked until
its live check); open: B-43 (#618), B-39 (#625); next B-40, B-41, B-44–B-46, B-48.

## Done in session 32 (2026-11-12 night)

- **Gateway v0.10.1** checked in the gateway's code and put in the design (`5b0526f`): refusals of the assigned key are
  `403`; a `401` is the credential key's. Nine documents and the queues' live checks updated; stream b asked on #580.
- **Reviewed #616, #617, #618** (agent, findings verified in the code): no high; two medium (one key's refusal opens
  the query-embedding circuit for all; clearing a published assistant's key bypasses publish-needs-a-key) and three
  low. Design settled in `4fba45d`; code to stream b on #580 and #618.
- **A-34 #621** (agent): every worker job's calls as the knowledge base's key; a refused key waits until the next full
  hour, then `failed` with the reason; a missing echo is fatal with 5011. Unticked until the live check.
- **A-38 #623** (agent, checked in the browser on a FakeGateway stack): the gateway's limits read-only and its version
  and features on *Gateway models*.
- **A-39 #626** (agent): `content.rag_retention_content_step`, the age rules of data-model §8 for rows without blobs,
  run nightly by the worker's maintenance. CI caught a missing line in the API's `sql-shapes.json` (see *Lessons*).
- **B-38 part 2's deviations** settled in chat §8.3 and §17 (`be37df0`); the cancelled turn kept and queued as B-48.
- **Docs and changelog:** #620 (the five migrations since 0.4.0 in the upgrade guide, rolling update no;
  `fake-no-cost`, `CachedInputTokens`), #622 (CHANGELOG through #621).
- **Issues:** #535, #604, #606 closed (the last two with a correction: the upgrade guide hadn't named their
  migrations); #619 and #624 filed for stream b.
- **Release dry run `0.5.0-dryrun`** (run 43318830850): green, smoke test with every phase including scan off.
- **Later the same night:** stream b built B-39–B-46 and B-48 and fixed every review finding (#629 v0.10.1, #631
  query embedding, #618's two); session 32 reviewed #625 and #627 (no blocking findings), merged #640 (#635's gateway
  link fallback), A-32 #641 (the cost runbook), A-35 #643 (`UME_RAG_GATEWAY_INTEGRATION_API_URL`, the firewall, the
  runbook's refusals, the `admin-console` client in production setup; unticked for its live check), CHANGELOG #644;
  queued A-40; filed #642 and #645.

## Done in session 31 (2026-11-12, evening)

- **#535, the malware scan is optional** (user's three calls: a separate `unscanned` state, health Healthy with
  "malware scan off", no re-scan when it is turned on):
  - **M-13** (`fe64205`): Decisions "Malware scan optional", the designs, B-47, A-36 and A-37 queued.
  - **B-47 part 1 #599** (main session, stream b's paths): `UploadStates.Unscanned`, `Uploads.ScannedAt` (migration
    `UploadScannedAt`), event 5106, the AppHost's `ClamAv:Mode`. README #600.
  - **A-36 #602:** the worker reads `ClamAv:Mode`; `unscanned` slots, `ScannedAt`, `malwareScan: "off"` in the run
    report. Outside Development a worker with the scan on and no `ClamAv:Host` refuses to start.
  - **A-37 #601:** the Compose profile `clamav`, `init-deployment.sh --clamav on|off`, `components/clamav`, Portainer,
    docs. An existing deployment must re-run `init-deployment.sh` (upgrade guide).
- **Cost attribution stage 1, code:**
  - **B-38** (taken from stream b with its agreement on #580): part 1 #608 (the usage tables, the shared reader and
    estimate in `src/Ume.Rag.Infrastructure/Gateway/GatewayUsage.cs`, usage from the rerankers and the query
    embedder, the FakeGateway's cost), part 2 #615 (the chat turn's usage per step, `AssistantDailyUsage` written
    with the turn, the totals without tokens and cost, the metrics).
  - **A-31 #613:** every worker model call's tokens and cost per knowledge base, `RunItems.CostUsd`, `x-ume-run-id`.
    Its deviations are in engine §10.6 and §11 (`6d326a8`).
- **Gateway phase 1, stream a's side:** A-30 part 2 #611 (the Data API is gone from the deployment and docs; the
  Portainer stack regenerated at gateway `v0.10.0` with the grants it lacked), A-33 #609 (member groups read-only in
  the platform UI, with a link to the gateway).
- **Stream b merged:** B-35 parts 2 and 3 (#605, #607), B-36 (#610, #612).
- **CHANGELOG:** #598 (through #597), #603 (the malware scan).

## Open

- **#513** stays open until its items close; **#550** until stream b acknowledges; **#580** until phases 2–3 are
  built (phase 1 is done on both streams).
- **A flaky test to watch:** `RenderTests.The_real_renderer_waits_for_the_quiet_rule_after_the_ready_selector_holds_text`
  failed once in CI (#613's first backend run: `Transient` instead of `Content`), with another PR's job on the same
  runner; it passed on the re-run and locally. File it if it comes back.
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
