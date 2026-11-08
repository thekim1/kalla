# Handover

State of the Ume.Rag work for the next session. Rewritten 2026-11-08, end of session 26 (`main` at `40ecb3a` plus this
file; tag `v0.2.0`). Earlier sessions are in the git history (`git log -- HANDOVER.md`).
Repo `~/research/rag` on this machine (`~/git/ume-rag-platform` on the session-19 machine), branch `main`, remote
`origin` = https://github.com/thekim1/ume-rag-platform (push after every commit).

## What this is

**Ume.Rag** (working name, N1) is a RAG / crawling / chatbot platform for Umeå kommun: small .NET 10 services on top
of the team's **LLM gateway** (https://github.com/thekim1/llmgateway, `~/git/llmgateway`), as an add-on to it (Decisions
"Shared sign-in with the gateway"). The user owns the gateway and wants to do better than **Eneo**, a slow "kitchen
sink" monolith. Design and code live in this repo.

## Where to read

1. `01-general-idea.md` §10 **Decisions table** (rows `current` bind).
2. **`plans/roadmap.md`**: done, what is left before go-live, the live block, waiting on people, P2/P3. One page.
3. **`plans/work/`**: the queues `stream-a.md`, `stream-b.md`, `main.md`; results in `results-a.md`/`results-b.md`.
4. The design for the area at hand (map in `plans/README.md`). History is in `plans/archive/` and does not bind.
5. **`plans/cases/support-assistant/`**: the first case plan (internal support assistant, DoIT), up to date with v0.2.0.
6. **`plans/platform/cost-attribution.md`**: who pays for each model call (new this session).

## Start here

**P1b code is done and released** as `v0.2.0` (https://github.com/thekim1/ume-rag-platform/releases/tag/v0.2.0: 8
images on ghcr.io by digest, the bundle, SBOMs, SHA256SUMS). M-3 is ticked. What is left of P1b is the **live block
(M-4)**, which waits on people (roadmap "Waiting on people").

**Proposed next, in this order** (told to the user at the end of session 26; the user had not chosen yet):

1. **to-verify #117**: recall@10 of the `halfvec` HNSW index against full-precision exact search on the evaluation
   sets. It can run now on the local `turkos` collection (about 10,000 chunks, 100-question evaluation set). It answers
   #502 (below) and is due before the full umea.se crawl.
2. **A-20** (#506): pin GitHub Actions by commit SHA. Small; the self-hosted runners hold release secrets. Good for an
   implementor agent in its own worktree, in parallel with #117.
3. **SA-15**: 2 MB body on the chat route in the generated proxy configuration (`deploy/proxy/nginx.conf.example`, the
   printed routes, `docs/production-setup.md`); the support-assistant case suggests it before the pilot.
4. **M-9**: write cost-attribution stage 1 into the designs and queue C1–C6 for the streams.
5. The rest of stream a's cheap items: **A-22** (#500 Postgres settings), **A-23** (#505 idle polling, renderer as a
   Compose profile).

**What only the user can unblock:**

- **Support-assistant proof of concept** (`plans/cases/support-assistant/07-rollout.md` §0b, steps P1–P8): needs the
  documents (Word, PDF, Markdown; which intranet sections, and whether this machine reaches them) and about twenty
  anonymised tickets. With those it is a session's work on the local stack; it goes ahead of everything else when they
  arrive. The local model mirror has no `gpt-6-luna` row: add one (it is on the interim gateway) or use
  `claude-haiku-5-5` as condense model.
- The live block's inputs: gold set with DoIT, G1/DPIA, test host, U1, pilot accounts on the gateway.
- **CI runners** (roadmap "Waiting on people", proposal only, user: "write it down, change nothing yet"): the runners
  `ume-rag` and `ume-rag2` share one machine and one Docker daemon, so jobs collide. A: one runner per Docker host; B:
  `release.yml` on GitHub-hosted runners. Once decided: a Decisions row, then drop the collision notes in
  `docs/ci-runner.md` and `release.yml`.
- **#511** (split the worker into libraries): only if it keeps growing in P2.

## Done in session 26 (2026-11-08)

- **The e2e failure was the runners colliding, not code.** Run 42469437132 ran beside `verify.yml` on the shared
  machine; the same code passed 9/9 locally and 9/9 on CI alone (42471519711). #494: `release.yml`'s `e2e` needs
  `verify` and `parser`; `docs/ci-runner.md`: dispatch e2e alone.
- **v0.2.0 released.** CHANGELOG 0.2.0 (#495). Release dry runs found five problems, fixed in #496: the SBOM tool is
  net8-only (`DOTNET_ROLL_FORWARD: Major`); the dry-run registry was never removed; no sudo (`--chown-with-docker`);
  **a bundle bug**, `rag-blobs-init` lacked FOWNER so the worker never started (`cap_add: [CHOWN, FOWNER]`); rag-api's
  `/version` is snake_case (the smoke test reads both; real fix #497). The tag run's **publish** failed with `gh: command
  not found`; the release was created by hand from that run's artifact with the workflow's notes script, and **#498**
  makes publish download a pinned gh 2.103.3 (first real test: the next tag).
- **M-3 ticked**; roadmap "P1b code done, released as `v0.2.0`".
- **Support-assistant case plan brought up to date** (`bf6d0ab`): gateway v0.9.1 deployed and Foundry rerankers native
  (llmgateway#72), JavaScript rendering, politeness profiles, topics on uploads, file restore; SA-15 against the
  generated proxy config; a proof-of-concept section (§0b); 23 moved line references corrected (none changed
  behaviour).
- **Cost attribution planned** (user, `080f10a`): Decisions row "Cost attribution", `plans/platform/cost-attribution.md`,
  roadmap entries, main item **M-9**. Long-term goal: the gateway's delegated attribution (release 2, feature A,
  `x-ume-on-behalf-of` = the owner team of the assistant or knowledge base); first, Ume.Rag meters tokens and cost per
  assistant, knowledge base, step and client (stage 1, C1–C6). Not a gateway key per assistant. Stage 1 was recorded as
  decided because the user accepted the recommendation; the user may still drop it. Open questions Q1–Q4 in the plan
  (Q2: who keeps a delegating key's team list current).
- **Review #513 triaged** (`57d4e38`, comment on #513): stream b's five approved by the user and queued as **B-25–B-29**
  (#499 step 1 measure first, #503, #504 csproj, #509 statement, #512); stream a queued as **A-20–A-28**; **M-10**
  settles #499 step 2 and #501 in the designs (waits on B-25's measurement). **#502 answered** (comment, roadmap
  `40ecb3a`): to-verify #117 first; `halfvec` storage in P2 if the recall delta is ≤ 0.5 points, else keep data-model
  decision 16.
- **Closed** #490, #480, #468 (done, evidence comments). Filed **#497** for stream b.
- All 64 stale local branches deleted (their work was on `main`).

## Open

- **Stream b** (inactive since the morning of 2026-11-08): B-25–B-29 queued; #488 parts 1 and 3 (validate
  `readySelector` on save, a Cancel run action); #497 (`/version` one JSON shape; then stream a drops the smoke test's
  fallback in `deploy/tests/release-smoke.sh`).
- **Stream a**: A-20–A-28 (order in #513's comment and the queue); main items M-9, M-10.
- **#502** stays open until to-verify #117 has a result. **#513** stays open until its items close.
- The live block (M-4), in the roadmap's order.

## Local stack (this machine)

- **State at handover:** the dev AppHost and the e2e stack are **stopped**; no Ume.Rag containers run (the next
  `aspire start` starts the persistent ones). The e2e volume `ume-rag-e2e-postgres` is removed. User secrets are at
  defaults. The working tree was last built on `0dbfa87` (code since then changed only in `deploy/` and
  `.github/`).
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
- **Data:** department DoIT; collection `turkos` (1,166 live documents, about 10,000 chunks, a 100-question evaluation
  set), assistant `turkos` **v2 on `claude-haiku-5-5`**; collection `renderer-demo` (quotes.toscrape.com, `browser` and
  `http`); collection **`a13-klimatkollen`** (25 pages, browser); collection **`a19-e-umea`** (137 pages, browser, no
  `readySelector`). Crawl settings: `hostOverrides` only `www.umea.se` (2 req/s, 2 connections). Gateway models: manual
  mirror rows `claude-haiku-5-5`, `claude-opus-5-5`, `Cohere-rerank-v5-fast` (model sync is off locally).
- **Admin API scripting:** `~/ume-rag-tools/` (outside the repo, no secrets in it): `admin.py` (BFF login from the
  AppHost user secret, XSRF; `python3 -I admin.py GET /api/assistants`, or `import admin; admin.call(m, path, body)`),
  `pf.py '<preflight JSON>'`, `runwait.py <sourceId> [start]`, `chat.py <n> <label> <out.json> [gap s]`. Run them from
  that folder.
- Feedback importer: every minute, user secret `Parameters:openwebui-admin-api-key`. Logins:
  `~/ume-rag-demo-login.txt` (mode 600; port 7410). Draft gold sets: `~/ume-rag-gold-draft-*.csv`, `plans/gold-set/`.
- Postgres: `docker exec -e PGPASSWORD="$(docker exec <postgres-…> printenv POSTGRES_PASSWORD)" <postgres-…> psql -h
  localhost -U postgres -d ragdb` (schemas `config`, `content`, `chat`).
- Local images: `ume-rag/renderer:ci` (also `ume-rag.local/renderer:ci`; the container test uses
  `UME_RAG_TEST_RENDERER_IMAGE=ume-rag.local/renderer:ci`), `ume-rag.local/clamav:dev`, `ume-rag.local/parser:ci`,
  `ume-rag.local/postgres:ci`.
- `~/git/llmgateway`'s working tree is at v0.9.0; `origin/main` is fetched at v0.9.2 (`df6bf5e`). Read the current
  gateway with `git show origin/main:<path>` or pull first.

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
  Before taking B's paths, check for open `b/…` work. `gh` acts as `ume-nichlas`.
- **Main session** owns `plans/**`, root `*.md` and `.github/stream-ownership.txt` (commit to `main`). Stream-A work
  on `a/<id>-<slug>` branches; a PR touching another stream's paths uses a `main-session/<slug>` branch.
- **Lean CI:** PR jobs path-filtered; full set before a release, **plus a release dry run**; `e2e` alone on the
  runners; agents run only the test projects they touch; no re-dispatching `verify` on main.
- **Subagents:** own worktree (`../rag-<id>`), full brief, no touching the live stack unless the brief says so. Their
  report is a claim: read the diff, re-run the tests, check CI's test totals, check live, spot-check line numbers.
- **Shared files** (`Directory.Packages.props`, `README.md`, `CHANGELOG.md`, `UmeRag.slnx`, `.gitignore`): small PR,
  append-only. Secrets never go into the repo, a log or a chat. Verify claims against real sources.
- **No backward compatibility before production:** no migration data steps for old rows (fix development data by
  hand), no shims.

## Lessons

- **Before a release, run a release dry run** (`gh workflow run release.yml -f version=X.Y.Z-dryrun -f e2e=false`,
  ~30 min) and fix what it finds; it is the only run of the bundle's smoke test. It never reaches the publish job.
  Dispatch `e2e.yml` only when no other workflow runs (`gh run list --status in_progress`).
- **The runner host was rebuilt since v0.1.0:** no sudo, no .NET 8, no `gh`. A workflow that needs a tool downloads a
  pinned, checksummed copy (as Syft and gh) or rolls forward.
- A failed `gh run view --log-failed` of the smoke test is long: grep `ok:|error:` for the script's own lines; an
  `up --wait` "not healthy within 900s" after a few seconds means a one-shot container failed (read its log lines).
- **Hardened Compose one-shots:** with `cap_drop: [ALL]`, root needs FOWNER to chmod what it has chowned.
- **rag-api's JSON is snake_case globally** (OpenAI routes), so every endpoint it serves is too, including `/version`.
- **`scripts/check-doc-refs.py`** resolves a bare `name.md §N` by file name: a new file with a common name (`plan.md`)
  captures old references. Give plan files distinctive names.
- **Admin API scripting:** `/bff/login` → Keycloak form (`#username`, `#password`, `#kc-login`; the AppHost secret
  `Parameters:dev-user-password` from `~/.microsoft/usersecrets/ume-rag-apphost-dev/secrets.json`, BOM) → the
  `form_post` page (upper-case `FORM`/`INPUT` attributes: match case-insensitively) → `GET /bff/user`. Writes need
  `X-Requested-With: XMLHttpRequest` and `X-XSRF-TOKEN` (cookie `RAG-XSRF-TOKEN`). The OpenAPI document is
  `/openapi/v1.json` through the BFF. A draft made by API needs `collectionLinks`, or it searches nothing.
- **Browser checks:** the Playwright MCP browser is not installed; use `playwright-core` 1.64.1 in a scratch folder
  with `executablePath` `~/.cache/ms-playwright/chromium-1247/chrome-linux64/chrome`, `ignoreHTTPSErrors`. Drawers are
  `dialog[open]`. Wait for the data, not only `networkidle`.
- **The interim gateway:** Claude models refuse `temperature`; 403 for Python's default User-Agent; 429 on
  embeddings (requeued); its front proxy answers intermittent `error code: 502`, so chat-model rerank runs carry many
  fallbacks (the Foundry reranker had none).
- **AppHost containers:** DCP copies files into a container before it starts, so `--read-only` breaks an AppHost
  container; Compose keeps the read-only root.
- **Worktrees:** running the AppHost from a worktree makes it `npm install` into `src/admin-ui/node_modules`, replacing
  a symlink with a real directory (git-ignored, but `git worktree remove` then needs `--force`). Never `git add -A` in
  a worktree. Merge a PR without `--delete-branch` while another agent still reads its worktree.
- The `implementor` agent refuses git writes (commit, rebase): review its diff, commit and rebase yourself.
- Squash-merged branches look unmerged to git: check the PR state (`gh pr list --state all`) and `git rev-list
  --count main..<branch>` before `git branch -D`.
- Read the first real failure; don't overlap two Worker.Tests runs. Admin UI checks in a worktree: symlink
  `node_modules`, `npx vitest run --pool=threads`, `npx eslint --max-warnings 0 …`, `npx vue-tsc -p tsconfig.app.json
  --noEmit --composite false --incremental false`; remove the symlink.
- A flaky test can be a real bug (#471): read the failed attempt's full log (`gh run view <id> --attempt 1
  --log-failed`) before calling it a race.
- Tests that listen to process-wide `ActivitySource`s or meters must filter to their own scope (#431).
- When editing a stream's queue on `main`, `pull --rebase` first: stream B edits queues too.
- `Chat:*Timeout` knobs are TimeSpans (`00:00:03`); a bare `3` means three days.
- **`pkill -f <pattern>` kills its own shell** when the pattern appears in the command line: use
  `pgrep -f '<pat>[.]sh' | xargs -r kill`, or kill by PID.
- **Knobs live in two services:** `Render:MaxConcurrent` is the worker's render cap *and* the admin API's ceiling for
  `renderPerHost` (V37). A local test value needs both user secrets (`ume-rag-worker`, `ume-rag-adminapi`).
- **The admin test endpoint is rate limited** (20 calls a minute: 429s); pace chat-latency samples (one per 10 s).
- `gh pr checks --watch` right after a push returns at once with "no checks reported": wait ~30 s first.

## Environment notes

- `rag_key.txt` in the repo root holds the interim-gateway key (mode 600, excluded in `.git/info/exclude`); the
  address and the same key are in the AppHost user secrets (`Gateway:BaseUrl`, `Gateway:Keys:Eu`). Never commit,
  print or move them.
- **Git over HTTPS:** `git -c credential.helper= -c 'credential.helper=!gh auth git-credential' fetch|pull|push …`.
- Self-hosted runners `ume-rag` and `ume-rag2` share one machine and one Docker daemon (`docs/ci-runner.md`); the
  renderer tests use fixed subnets 10.214.0.0/24 and 10.215.0.0/24, so two renderer jobs at once could collide.
- `172.30.0.0/16` is taken on this machine; test networks use 10.21x.0.0/24.
