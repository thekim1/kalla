# Handover

State of the Ume.Rag work for the next session. Rewritten 2026-11-08, end of session 25 (`main` at `e80c42c`). Earlier
sessions are in the git history (`git log -- HANDOVER.md`).
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
5. **`plans/cases/support-assistant/`**: the first case plan (internal support assistant, DoIT).

## Start here: the e2e failure blocks M-3

**Every code item of both queues is ticked.** M-3 (`plans/work/main.md`) is done except its last step, "run the full
CI set once". On `main` at `e80c42c` (2026-11-08, dispatched by hand):

- `verify.yml` (run 42469432178): **all jobs green** (backend, admin-ui, renderer, evaluation-smoke, images,
  deploy-checks, doc-refs).
- `parser.yml` (run 42469434705): **all green**.
- `e2e.yml` (run 42469437132): **failed**. Both pilot journeys show the admin UI's `offline` message, "The server cannot
  be reached. Check your connection and try again.":
  - **mouse** (`tests/e2e/specs/pilot.spec.ts:64`): step 5, after Preview, the "Result of the preview" region shows
    the alert and never "Before / After". The preflight before it worked ("About 10 pages"), and the worker logged
    `Preview …: 1 documents in 1590 ms`, so the job ran; a later request failed.
  - **keyboard** (`:143`): step 10, the test panel's answer never shows "Answered"; the same alert.
- That message is `ApiError(0, …, 'network')` from `src/admin-ui/src/api/client.ts`: **`fetch` itself threw**
  (network error, a CSP-blocked request, or a redirect to another origin), not an HTTP error status. The server logs
  in the run (step "Server logs of a failed run": last 300 lines of adminapi, admin-ui, api and worker) show no
  `fail:` or exception. The admin UI is Vite 8.3.3's dev server proxying to the admin API.
- **Last green e2e:** run 42142370096 on 2026-11-06 (branch `b/B-11-open-webui-link`, `bbc1c9e`, not on `main`; the
  nearest main commit is `f7baab9`). About 20 PRs of both streams merged since. Admin API files changed since
  `f7baab9`: `AssistantEndpoints.cs` (B-18/B-19/B-20/B-22), `SettingsEndpoints.cs`, `UploadEndpoints.cs` (B-24),
  `GatewayEndpoints.cs`, `PipelineEndpoints.cs`, `Program.cs` (A-14, B-20). Admin UI: A-12, A-14, A-17, B-18..B-24.
- **Next step:** reproduce locally. No `pwsh` here, so mirror `scripts/Start-E2E.ps1` in bash: `aspire stop --all`,
  `docker volume rm ume-rag-e2e-postgres`, export `DevelopmentVolumes__Postgres=ume-rag-e2e-postgres
  Seed__Enabled=true FakeGateway__Enabled=true E2E__Enabled=true`, `aspire start --apphost … --non-interactive`,
  `aspire wait admin-ui …`; then in `tests/e2e`: `npm ci`, `npx playwright install chromium` (the spec pins
  `@playwright/test` 1.63.0), `npx playwright test specs/pilot.spec.ts --grep mouse --trace on`. The trace's network
  tab names the request that fails. (A first attempt was stopped before it started, session 25.) Alternative: bisect
  with `gh workflow run e2e.yml --ref <commit>` on a few main commits between `f7baab9` and `e80c42c`.
- **When e2e is green:** tick M-3, say "P1b code done" in the roadmap, then the **v0.2.0 release** (user approved:
  "after M-3 closes"; images and tag are outward-facing, the user said yes already).

## Done in session 25 (2026-11-07/08; all merged, CI green with the tests actually run)

- **A-13 renderer live check (#482):** Klimatkollen (a client-only Vite/React app) as the JavaScript site, umea.se
  `turkos` as the HTTP baseline, both mixed, chat p95 during the crawl (unaffected). Two defects fixed: (1) a rendered
  page's whole time fed the host's `LatencyEwmaMs`, which on an external host *is* the pacing interval, so renders
  slowed each other into timeouts (`PageLatency.Of`); (2) `Render:QuietPeriod` 100 ms → **500 ms** (at 100 ms pages were
  stored empty). Results: `results-a.md` "item A-13 (live sites)".
- **A-17 (#483):** preflight `render.empty` warn; the run report's `render.empty` count and run-page row.
- **A-18 (#484):** crawl concurrency probe (`tests/Ume.Rag.Worker.Tests/Scale/`, trait `Category=Scale`, runs only with
  `UME_RAG_SCALE=1`): with many hosts the fetch lanes bound first; polite hosts filled them with in-process slot waits.
- **#491:** `Worker:Concurrency:fetch` 8 → **32** (user); fetched bodies spool to `/data/blobs/spool` on disk
  (`TMPDIR`), not the `/tmp` tmpfs that counts against the worker's 1.5 GB limit; `rag-blobs-init` creates it, release
  smoke checks it, the worker deletes stale spool files at start.
- **#492:** `Render:MaxConcurrent` 2 → **4** (user, after measuring: renderer peak 294 MiB with one open page, 453 MiB
  with four, 2 GB limit); `renderPerHost` capped at the host's `maxConnections` (a render holds a connection).
- **A-19 (#493, #488 part 2, #490):** ready rule with a `readySelector` = holds text **and** quiet (user); permanent
  `render.bad_selector` and `render.selector_not_found`; `render.mostly_empty` degrades a run. Checked live on
  e.umea.se: `<h1>` → bad selector in 4 s; `h2` now gives full pages (stream b's `h2` run had stored 130 of 131 empty).
- **e.umea.se live check (#490 item 1, user approved):** no `readySelector`, 137 pages added, 0 empty, 0 failed;
  answered on #490.
- **Stream B** merged B-21..B-24 (#485–#489); their deviations are settled in the design (`e80c42c`).
- **Small fixes:** `turkos` republished as v2 on `claude-haiku-5-5` (works in Open WebUI and the test panel); old dev
  `Runs.SettingsHashes` without `fetch` cleared by hand (they failed every scheduler pass); three leftover branches
  deleted.

## Decided this session (user, 2026-11-07/08)

- Keep strict 1 req/s pacing for an external single-page app's own subrequests; a per-page burst and a renderer asset
  cache kept across runs are **P2** (roadmap, connectors P2 table).
- `Worker:Concurrency:fetch` 32; `Render:MaxConcurrent` 4 after the memory measurement.
- Ready rule: selector **and** quiet.
- Testing on external JavaScript sites is fine; e.umea.se crawl approved.

## Open, not blocking M-3

- **#488 parts 1 and 3** are stream b's (validate `readySelector` on save; a Cancel run action in the admin UI).
- **Waiting on people** (roadmap): gold set with DoIT, G1/DPIA, test host (also: Chromium's sandbox on a Linux Docker
  Engine host, to-verify #29), U1, N1/W1.
- The live block (M-4) after the release.

## Local stack (this machine)

- **State at handover:** the dev AppHost is **stopped** (`aspire stop --all`). Its persistent containers (postgres,
  keycloak, parser, clamav, renderer) still run. The e2e volume `ume-rag-e2e-postgres` does not exist. User secrets
  are back to defaults (worker: none; admin API: no `Render:*`). The working tree's last build is of A-19's commit
  (`c2d01eb`); run `dotnet build UmeRag.slnx` on `main` before starting.
- Start: `export Seed__Enabled=false; aspire start --apphost src/Ume.Rag.AppHost/Ume.Rag.AppHost.csproj
  --non-interactive` (Docker up: `docker info`). Stop: **`aspire stop --all --non-interactive`**. Restart one service:
  `aspire resource worker restart --apphost … --non-interactive`.
- Aspire CLI: `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire` (not on PATH).
  `aspire logs worker --non-interactive`; `aspire describe --apphost …` shows resource states.
- Ports: dashboard `https://localhost:17470`; **admin UI `https://localhost:5174`** (user `rag-platform-admin`); api
  7401, admin API 7402, worker 7403; **Open WebUI `http://localhost:7410`**, Keycloak 7411, Postgres 7412, parser 7413,
  ClamAV 7414, **renderer 7417**. Platform objects are under `/plattform/<resource>` (e.g. `/plattform/settings`).
- **Parser** goes cold after 15 idle minutes; `PARSER_IDLE_STOP_S=0` / `NER_IDLE_STOP_S=0` keep it warm.
- **Data:** department DoIT; collection `turkos` (1,166 live documents, a 100-question evaluation set), assistant
  `turkos` **v2 on `claude-haiku-5-5`**; collection `renderer-demo` (quotes.toscrape.com, `browser` and `http`);
  collection **`a13-klimatkollen`** (source *Klimatkollen companies (browser)*, 25 pages); collection **`a19-e-umea`**
  (source *e.umea.se (browser, no readySelector)*, 137 pages). Crawl settings: `hostOverrides` only `www.umea.se`
  (2 req/s, 2 connections). Gateway models: manual mirror rows `claude-haiku-5-5`, `claude-opus-5-5`,
  `Cohere-rerank-v5-fast` (model sync is off locally).
- **Admin API scripting:** `~/ume-rag-tools/` (outside the repo, no secrets in it): `admin.py` (BFF login from the
  AppHost user secret, XSRF; `python3 -I admin.py GET /api/assistants`, or `import admin; admin.call(m, path, body)`),
  `pf.py '<preflight JSON>'` (runs a preflight, prints its render check), `runwait.py <sourceId> [start]` (sync and
  wait), `chat.py <n> <label> <out.json> [gap s]` (`turkos` test-panel latency). Run them from that folder.
- Feedback importer: every minute, user secret `Parameters:openwebui-admin-api-key`. Logins:
  `~/ume-rag-demo-login.txt` (mode 600; port 7410). Draft gold sets: `~/ume-rag-gold-draft-*.csv`, `plans/gold-set/`.
- Postgres: `docker exec -e PGPASSWORD="$(docker exec <postgres-…> printenv POSTGRES_PASSWORD)" <postgres-…> psql -h
  localhost -U postgres -d ragdb` (schemas `config`, `content`, `chat`).
- Local images: `ume-rag/renderer:ci` (also `ume-rag.local/renderer:ci`; the container test uses
  `UME_RAG_TEST_RENDERER_IMAGE=ume-rag.local/renderer:ci`), `ume-rag.local/clamav:dev`, `ume-rag.local/parser:ci`,
  `ume-rag.local/postgres:ci`. Playwright scratch use: `playwright-core` 1.64.1 with `executablePath`
  `~/.cache/ms-playwright/chromium-1247/chrome-linux64/chrome`.

## How we work (user preferences, binding)

- **Good and easy to use:** look at pages in the browser, not only tests; fix friction or file it.
- **Demo first; admin UI focus.** No extra negative proofs or flake hunts unless a change needs one. No tests that
  depend on timing ("wait until X, fail after 60 s" is fine).
- **Approve and merge without asking** when a PR is ready (reviewed, checked live where relevant, CI green with the
  tests actually run); tell the user afterwards. Design, scope and destructive actions go to the user.
- **Stream B** is a remote agent (GitHub `thekim1`) with its own coordinating main session on another machine; talk
  only through GitHub (labels `stream-b`, `stream-a`, `main`). B merges its own PRs; review its security-relevant parts.
  Before taking B's paths, check for open `b/…` work. `gh` acts as `ume-nichlas`.
- **Main session** owns `plans/**`, root `*.md` and `.github/stream-ownership.txt` (commit to `main`). Stream-A work
  on `a/<id>-<slug>` branches; a PR touching another stream's paths (`tests/**`, `src/**` outside the worker, the
  AppHost, admin API/UI) uses a `main-session/<slug>` branch.
- **Lean CI:** PR jobs path-filtered; full set and `e2e` before a release; agents run only the test projects they
  touch; no re-dispatching `verify` on main.
- **Subagents:** own worktree (`../rag-<id>`), full brief, no touching the live stack unless the brief says so. Their
  report is a claim: read the diff, re-run the tests, check CI's test totals, check live. Parallel agents work well on
  disjoint items with an agreed contract (A-10/A-11 shared the renderer image contract).
- **Shared files** (`Directory.Packages.props`, `README.md`, `CHANGELOG.md`, `UmeRag.slnx`, `.gitignore`): small PR,
  append-only. Secrets never go into the repo, a log or a chat. Verify claims against real sources.
- **No backward compatibility before production:** no migration data steps for old rows (fix development data by
  hand), no shims.

## Lessons

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
  container ("container rootfs is marked read-only"); Compose keeps the read-only root.
- **Worktrees:** running the AppHost from a worktree makes it `npm install` into `src/admin-ui/node_modules`, replacing
  a symlink with a real directory (git-ignored, but `git worktree remove` then needs `--force`). Never `git add -A` in
  a worktree. Merge a PR without `--delete-branch` while another agent still reads its worktree; delete the remote
  branch with `git push origin --delete <branch>` afterwards.
- The `implementor` agent refuses git writes (commit, rebase): review its diff, commit and rebase yourself.
- The permission classifier refuses bulk branch deletion combined with `--force`; ask the user for those.
- Read the first real failure; don't overlap two Worker.Tests runs. Admin UI checks in a worktree: symlink
  `node_modules`, `npx vitest run --pool=threads`, `npx eslint --max-warnings 0 …`, `npx vue-tsc -p tsconfig.app.json
  --noEmit --composite false --incremental false`; remove the symlink.
- A flaky test can be a real bug (#471): read the failed attempt's full log (`gh run view <id> --attempt 1
  --log-failed`) before calling it a race.
- Tests that listen to process-wide `ActivitySource`s or meters must filter to their own scope (#431).
- When editing a stream's queue on `main`, `pull --rebase` first: stream B edits queues too.
- `Chat:*Timeout` knobs are TimeSpans (`00:00:03`); a bare `3` means three days.
- **`pkill -f <pattern>` kills its own shell** when the pattern appears in the command line (it happened twice):
  use `pgrep -f '<pat>[.]sh' | xargs -r kill`, or kill by PID.
- **Knobs live in two services:** `Render:MaxConcurrent` is the worker's render cap *and* the admin API's ceiling for
  `renderPerHost` (V37). A local test value needs both user secrets (`ume-rag-worker`, `ume-rag-adminapi`).
- **A rendered page holds one host connection** for its whole render, so `renderPerHost` above `maxConnections` does
  nothing (now capped, #492). An external host has 1 connection.
- **Measuring Chromium pages in the renderer:** `ps -eo args | grep -c -- '--type=renderer'` counts 2 at idle (the
  spare process and the grep itself).
- **The admin test endpoint is rate limited** (20 calls a minute: 429s); pace chat-latency samples (one per 10 s).
- `gh pr checks --watch` right after a force-push returns at once with "no checks reported": wait ~30 s first.

## Environment notes

- `rag_key.txt` in the repo root holds the interim-gateway key (mode 600, excluded in `.git/info/exclude`); the
  address and the same key are in the AppHost user secrets (`Gateway:BaseUrl`, `Gateway:Keys:Eu`). Never commit,
  print or move them.
- **Git over HTTPS:** `git -c credential.helper= -c 'credential.helper=!gh auth git-credential' fetch|pull|push …`.
- Self-hosted runners `ume-rag` and `ume-rag2` share one machine and one Docker daemon (`docs/ci-runner.md`); the
  renderer tests use fixed subnets 10.214.0.0/24 and 10.215.0.0/24, so two renderer jobs at once could collide.
- `172.30.0.0/16` is taken on this machine; test networks use 10.21x.0.0/24.
