# Handover

State of the Ume.Rag work for the next session. Rewritten 2026-11-08, end of session 26 (`main` at `f61af31` + the M-3 tick, tag `v0.2.0`). Earlier
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

## Start here: P1b code is done and released; the live block is next

**M-3 is ticked and `v0.2.0` is released** (2026-11-08): https://github.com/thekim1/ume-rag-platform/releases/tag/v0.2.0 (8 images on ghcr.io by digest,
  bundle, SBOMs, SHA256SUMS). Every code item of both queues is ticked; what
is left of P1b is the **live block (M-4)**, which waits on people (roadmap "Waiting on people"). Code work that can go
on meanwhile: stream b's #488 parts 1 and 3 and #497; anything new the user files.

**The user has an open decision (roadmap "Waiting on people", CI runners):** the two runners share one machine and one
Docker daemon, so jobs collide. Proposal A: one runner per Docker host (remove `ume-rag2`, or give it its own machine
or VM); B: `release.yml` on GitHub-hosted runners (paid minutes). The user said: write it down, change nothing yet.
Once decided: a Decisions row, then drop the collision notes in `docs/ci-runner.md` and `release.yml`.

## Done in session 26 (2026-11-08)

- **The e2e failure was the runners colliding, not code.** Run 42469437132 ran beside `verify.yml` on the shared
  machine: the mouse journey failed while `verify`'s backend job ran, the keyboard journey during its renderer job.
  The same code passed 9/9 locally (fresh `Start-E2E` stack, bash mirror of the script) and 9/9 on CI **alone**
  (42471519711). #494: `release.yml`'s `e2e` needs `verify` and `parser`; `docs/ci-runner.md`: dispatch e2e alone.
- **CHANGELOG 0.2.0 (#495):** the P1b entries that were missing (#144–#458 and the late items), grouped by area.
- **Release dry runs found five problems (#496)**; `release.yml` had not run since v0.1.0 and the runner host was
  rebuilt since: (1) `Microsoft.Sbom.DotNetTool` 4.1.13 is net8-only, the host has .NET 10 only →
  `DOTNET_ROLL_FORWARD: Major`; (2) the dry-run registry container was never removed → `ume-rag-dryrun-registry`,
  removed `if: always()`; (3) no sudo → smoke test `--chown-with-docker`; (4) **a bundle bug:** `rag-blobs-init` had
  CHOWN but not FOWNER, so its chmod failed and the worker never started → `cap_add: [CHOWN, FOWNER]`; (5) rag-api's
  `/version` is snake_case (`informational_version`, its global JSON policy) → the smoke test reads both; the real fix
  is #497 for stream b. Dry run 42479183095 green, then the tag.
- **The tag run (42480775293)** passed verify, parser and build (images pushed), then **publish** failed: `gh: command
  not found` on the runner. The release was created by hand from that run's `release` artifact with the workflow's own
  notes script (checksums verified after download). **#498** (merged): the publish job downloads a
  pinned gh 2.103.3 with its sha256. A dry run never reaches publish, so the next tag is its first real test.
- **M-3 ticked**, roadmap "P1b code done, released as `v0.2.0`".

## Open

- **#488 parts 1 and 3** are stream b's (validate `readySelector` on save; a Cancel run action in the admin UI).
- **Waiting on people** (roadmap): gold set with DoIT, G1/DPIA, test host (also: Chromium's sandbox on a Linux Docker
  Engine host, to-verify #29), U1, N1/W1.
- **#497** (stream b): `/version` in one JSON shape in every service; then drop the smoke test's fallback.
- The live block (M-4), in the roadmap's order.

## Local stack (this machine)

- **State at handover:** the dev AppHost and the e2e stack are **stopped** (`aspire stop --all`); no Ume.Rag
  containers run (the persistent ones are stopped too; the next `aspire start` starts them). The e2e volume
  `ume-rag-e2e-postgres` is removed. User secrets are at defaults. The working tree was last built on `0dbfa87` (code
  unchanged since, except `deploy/`).
- **E2E locally without pwsh:** `aspire stop --all`; `docker volume rm ume-rag-e2e-postgres`; export
  `DevelopmentVolumes__Postgres=ume-rag-e2e-postgres Seed__Enabled=true FakeGateway__Enabled=true E2E__Enabled=true`;
  `aspire start --apphost src/Ume.Rag.AppHost/Ume.Rag.AppHost.csproj --non-interactive`; `aspire wait admin-ui
  --apphost …`; in `tests/e2e`: `npm ci`, `npx playwright install chromium`, `npx playwright test` (9 tests, ~10 min).
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

- **Before a release, run a release dry run** (`gh workflow run release.yml -f version=X.Y.Z-dryrun -f e2e=false`,
  ~30 min) and fix what it finds; it is the only run of the bundle's smoke test. Dispatch `e2e.yml` only when no other
  workflow runs (`gh run list --status in_progress`).
- A failed `gh run view --log-failed` of the smoke test is long: grep `ok:|error:` for the script's own lines; an
  `up --wait` "not healthy within 900s" after a few seconds means a one-shot container failed (read its log lines).
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
