# Handover

State of the Ume.Rag work for the next session. Rewritten 2026-11-12, end of session 29 (`main` at `81bb827` plus this
file; **last release `v0.4.0`**, published 2026-11-12 from `714a58f`). Earlier sessions are in the git history (`git log
-- HANDOVER.md`). Repo `~/research/rag` on this machine (`~/git/ume-rag-platform` on the session-19 machine), branch
`main`, remote `origin` = https://github.com/thekim1/ume-rag-platform (push after every commit).

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

**The next session starts the next queued work. In order:**

**1. M-9, cost attribution stage 1** (`plans/work/main.md` M-9; the first unticked item that isn't waiting on people;
stream a has nothing ready: A-20 is postponed to go-live, A-27 waits on it, A-30 waits on B-37, the A-L items wait on
the live block). Write stage 1 of `plans/platform/cost-attribution.md` §5 into the designs it names (chat §8.3, §17
and the `AssistantDailyTotals` columns or a per-step table, admin §4.9 and a cost subsection under §8, processing
§11/§14, engine §10): each call's cost is the gateway's (`x-ume-cost-usd` now; the stream comment from gateway phase 2;
`usage.read` from phase 3), estimated from the mirror's prices only where none is given, split per step on
`x-ume-run-id`/`x-ume-step`. Then queue C1–C6 in `stream-a.md`/`stream-b.md` with paths and done-whens, and bring
**Q3** (cost-attribution §7) to the user. A design pass of this size can go to an implementor agent in a worktree;
review its diff, fix paths, commit on `main` (it owns `plans/**`).

**Then, as they arrive:** review stream b's PRs for **B-34** (#510 SQL contract), **B-35**, **B-36**, **B-37** (gateway
phase 1, below); take **A-30** once B-37 is
merged.

**2. #580, the gateway integration (user decision, 2026-11-11).** Every assistant and knowledge base runs on an
*assigned* gateway key of its department chosen by id; each service has one credential key and sends
`x-ume-on-behalf-of`; configuration from `/v1/ume/context` and `/v1/models`. Designs: `a457474`, `b0740ab`.
- **Gateway phase 1 merged** (thekim1/llmgateway#81, `6a8f0a5`); designs checked against it; **queued** as B-35 (sync,
  `GatewayKeys`, FakeGateway contract with pinned examples in `contracts/gateway/`), B-36 (key picker, V39), B-37
  (credential key per service) and A-30 (its deployment) (`abd452b`). Review them as they come.
- **Point 1 decided by the user (2026-11-12):** acted requests are bounded by the credential key too
  (`RequireMaxResidency` when either key requires it; residencies intersected). #82 (phase 2, open) has the first
  half; the intersection is asked for on #82. Queue phase 2 (M-12) only once a release with it exists.
- **Open:** point 8 (every active department and team in the context, member groups with feature C); until then the
  organisation sync stays on the Data API. No OpenAPI document from the gateway yet.

**3. After M-9:** M-12 phase 2 once a gateway release has #82 with the residency intersection (queue its items from
M-12's list); the upload gaps or the support-assistant proof of concept when the user picks them (below).

**Waiting on the user:**
- **#580 point 8** (as gateway owner), the residency intersection in #82, and the gateway's phase PRs.
- **Upload gaps** from `upload-quality-review.md`. Suggested: picture descriptions (U1, needs a decision entry) and
  rerank on (U4, when the gold set shows a gain), then `.msg`/`.eml` (U2), a contextual-header trial (U5) and a
  near-duplicate warning (U6).
- **Ask-a-file workspaces** (P2): owned by teams and individuals, against "Organisation" (departments own all content).
  Answer before P2 (`plans/work/main.md` M-11).
- **LibreOffice placement**, decided again before go-live with the parser sandbox question (Decisions "Parser").
- **CI runners** (proposal only, user: "write it down, change nothing yet"): `ume-rag` and `ume-rag2` share one Docker
  daemon and one user home. A: one runner per Docker host; B: `release.yml` on GitHub-hosted runners.
- **The gateway** (`~/git/llmgateway` `origin/main`) still pins Aspire 13.6.4 (GHSA-f7vh-h5g3-7jrh; AppHost only).
- **Support-assistant proof of concept** (`plans/cases/support-assistant/07-rollout.md` §0b, P1–P8): needs the
  documents and about twenty anonymised tickets. It goes ahead of everything else once they exist.
- The live block's inputs: gold set with DoIT, G1/DPIA, test host, U1, pilot accounts on the gateway.
- **#511** (split the worker into libraries): only if it keeps growing in P2.

**Stream b** (remote, GitHub only) has its own main session (design PRs on `main-session/` branches, decisions in
`plans/decisions.md`). It works fast and also takes stream-a items on `a/` branches. B-30–B-33 are done; its queue has
the live items, and **#584** (a flaky Api test, its path) is filed for it.

## Done in session 29 (2026-11-11/12)

- **v0.4.0 released** (tag on `714a58f`, release run #43167650776 all green, 11 assets, published 06:41 UTC).
- **Release:** dry run 3 failed in `parser.yml` (`docker create ghcr.io/astral-sh/uv` → "denied": the expired ghcr
  login in the runners' shared Docker config); `82f027d` gives every job that pulls ghcr its own `DOCKER_CONFIG`.
  Dry run 4 passed (verify, parser, build; smoke 55 s, renderer on and off; build job 8 min). #572 merged. A-21, A-23
  ticked with results rows (#581); #503, #504 (with a correction: B-27 adopted nothing), #505 closed. Release notes
  #582, #586.
- **Reviewed and merged:** A-29 #578/#579 (one note: a collection with no lexeme at 10 % stores no rows), B-33 #583
  (stream b merged it), #577 and #576 after browser checks (notes: disabled inputs look editable; three fields named
  *Instructions*), #585. #499, #560, #565 closed.
- **#586 (mine):** the worker checks every collection's keyword statistics at start (an upgrade otherwise ran without
  stop lists until the night), and `KeywordStatistics` moved from its own unpooled session onto the worker's pool: CI's
  `DispatcherTests` saw 12 worker connections, over the role budget (data-model §1.1).
- **#580:** reviewed the gateway contract (7 points + point 8 on the department list) and wrote the design pass
  (`a457474`, an implementor agent's work, reviewed; one wrong path fixed).
- **#584** filed: `KnowledgeServiceTests.A_failed_embedding_…` times out on a busy runner (embedding 2 s total limit).

## Open

- **#513** stays open until its items close; **#501** until A-25; **#535** (ClamAV `ClamAv:Mode = on | off`, user
  decision) has no PR yet; **#550** until stream b acknowledges; **#580** (above); **#584** (stream b).
- The live block (M-4), in the roadmap's order.

## Local stack (this machine)

- **State at handover:** the dev AppHost is **stopped** (only the persistent ClamAV container runs). It ran this session
  on `main` plus #576/#577 for the browser checks; migrations through `AnswerLengthFromModel` are applied locally (not
  yet `KeywordStatistics`; the next start applies it). `Settings.Chat` is back to `{}`. User secrets are at defaults.
  The local parser image is older than #526 (no LibreOffice): **rebuild it before demoing `.doc`/`.xls`/`.ppt`/`.rtf`**.
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
- `~/git/llmgateway`'s working tree is at v0.9.0; `origin/main` is fetched at v0.9.2 (`df6bf5e`). Read the current
  gateway with `git show origin/main:<path>` or pull first.
- Worktrees: none besides the main clone.
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
- The Edit tool fails on a file not Read in this session. A commit made in the same step then goes out without the
  change, so check `git show --stat` after a combined edit-and-commit.

## Environment notes

- `rag_key.txt` in the repo root holds the interim-gateway key (mode 600, excluded in `.git/info/exclude`); the
  address and the same key are in the AppHost user secrets (`Gateway:BaseUrl`, `Gateway:Keys:Eu`). Never commit,
  print or move them.
- **Git over HTTPS:** `git -c credential.helper= -c 'credential.helper=!gh auth git-credential' fetch|pull|push …`.
- `172.30.0.0/16` is taken on this machine; test networks use 10.21x.0.0/24.
