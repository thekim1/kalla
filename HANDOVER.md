# Handover

State of the Ume.Rag work for the next session. Rewritten 2026-11-07, end of session 24 (`main` at `57b2a5c`). Earlier
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
3. **`plans/work/`**: the queues `stream-a.md`, `stream-b.md`, `main.md` (each item: do, paths, done-when, waits on,
   read). Items are ticked in the PR that does them. Start prompts in `plans/work/prompts/`.
4. The design for the area at hand (map in `plans/README.md`). History is in `plans/archive/` and does not bind.
5. **`plans/cases/support-assistant/`**: the first case plan (internal support assistant, DoIT).

## Where we are

- **P0** released (`v0.1.0`). **P1a/P1b code is done apart from the M-3 follow-ups** (below). Every other code item of
  both queues is ticked; the rest of the queues is the live block, waiting on people.
- **Stream B is active again** (remote, GitHub `thekim1`). It built B-20 (#447 model pickers, API side), B-L1, B-L4
  (gateway release 1 verified live), B-6, #468's convention changes and operator guide, and #476.
- **Gateway** v0.9.1 is deployed and release 1 verified live. The interim key lists `Cohere-rerank-v5-fast`,
  `claude-haiku-5-5`, `gpt-6-luna`, `text-embedding-3-large`; **not `claude-opus-5-5`**, so `turkos` (published on
  Opus) fails in Open WebUI until the user decides (below).

### Built and merged in session 24 (all CI green, tests re-run by the main session)

- **Rerank live run (#421):** `Cohere-rerank-v5-fast` on Foundry: MRR 0.825 vs 0.786 hybrid, 0 fallbacks, p50 651 ms.
  `Retrieval:GatewayRerankTimeout` default 500 ms → **1.5 s** (#439). B-12, B-16 ticked.
- **#435 (#443):** chunk dates from HTTP `Last-Modified` and reliable `lastmod` reach production; one shared
  stage-input builder for production, preview and `DocumentProcessor`.
- **JavaScript rendering (connectors §8; A-9 to A-12):** spike (#459, results-a row 29); `images/renderer`
  (Playwright 1.64.1 `launch-server`, Chromium with its sandbox via `images/renderer/seccomp.json`, secret WebSocket
  path, WebRTC off, 1.48 GB) on the internal `render` network 172.30.18.0/24 (#462); the worker drives it and fetches
  every browser request through its egress guard, `PageFetcher` as the one fetch path, render slots, failures keep the
  last good content and links, `Documents.FetchMode` (#463); source settings `fetchMode`/`readySelector`/`scrollToLoad`,
  preflight raw-vs-rendered word counts with the "needs JavaScript" hint, render card, fetch mode in the preview
  (#472). CI's `renderer` job runs the real-container test (#479). **A-13** partly done on a demo site (below).
- **Politeness profiles per host (A-14, #461):** presets `polite`/`municipal`/`fast`/`custom` in
  `Settings.Crawl.hostOverrides`; rate, connections, `Crawl-delay`, latency term, window, back-off, breaker,
  **robots.txt ignore per host with a reason**, **free User-Agent per host**; ceilings `Crawl:MaxHostRate` 50 and
  `Crawl:MaxHostConnections` 16; V37; host table in the admin UI.
- **Crawlee review → HTTP gaps (A-15, #460):** meta refresh as a redirect hop, `redirect.cookie_required`, sniffed
  `octet-stream`, soft-404 fingerprint (`http.soft_404`), challenge pages (`http.challenge`, never evaded), sitemap
  retries and text sitemaps; **a URL that failed at any depth in a run is never `not-seen`** (was seeds only).
- **#471 (#475):** the "flaky" test was a production bug: identity rules matched with a 50 ms timeout, so under load a
  page got two identities; now `NonBacktracking` with no timeout. #476 (stream B) refuses constructs that engine lacks.
- **#466 (#473):** Settings model fields are pickers that grey out models the key may not use.
- **A-16 (#481):** the `files` template takes `pathRules` (a rule matches an upload's display name).
- **M-3 (in progress):** every design deviation of the 309 PRs since v0.1.0 checked and settled in the design
  (`fd46ad3`); results-a/b folded into `to-verify.md`; CHANGELOG (#478); the roadmap's gateway row updated.

## Decided this session (user, 2026-11-07; all in the Decisions table)

- **Gateway rerank time limit 1.5 s.**
- **General knowledge on a public assistant** allowed with a warning and a confirmation on publish (#445; B-18).
- **Level change in the edit drawer**, `edit` right (#446; B-19).
- **Model pickers show what the key may use**, greyed out (#447; done).
- **Parser telemetry is logs only in P1**; OTel in P2 (#453).
- **JavaScript rendering:** a browser-only container driven by the worker (Option A; Chromium in the worker, a crawler
  in the browser image and an egress proxy were rejected); per-source `fetchMode`, no automatic detection yet, render
  every page of a `browser` source on every traversal; replay check, path rules, auto-detection and Lightpanda later.
- **Configurable politeness per host** (robots.txt ignorable with a reason; User-Agent free; platform admin only).
- **Crawlee review:** ideas adopted, Crawlee itself not (own queue, no SSRF guard, anti-bot machinery).
- **Shared sign-in with the gateway** (#468): Keycloak groups carry role mappings for both systems, one client per
  system, Entra app roles per product, two Compose projects; no code in either product refers to the other's roles.
- **M-3 settlements:** the test panel is designed as built (its missing parts P2); Overview attention items and editors'
  site-profile read access P2; the interaction-audit benchmark dropped; to-verify #102 (retrieval over budget at
  100k chunks in one scope) accepted for the pilot, re-measured in R1.

## Next session

1. **GitHub:** `gh pr list`, `gh issue list`; review stream B's PRs (security-relevant parts), answer `main` issues.
2. **M-3 closes** when stream B's **B-21..B-24** are merged (#480: one connection pool in rag-api; publish calls
   `ValidateForPublish`; feedback-import alert, sources-table columns, deletion-card text from the configured guard,
   stale `rag-additions.json` description; restore a deleted uploaded file). Then run the full CI set once **including
   `e2e.yml`** (workflow_dispatch; M-3's done-when asks for it), tick M-3, and say "P1b code done" in the roadmap.
   B-18, B-19 (#445, #446) are stream B's too.
3. **Ask the user, then:** the v0.2.0 release (images and tag are outward-facing).
4. **A-13** needs the user's two or three real JavaScript sites (§8.8 scenarios: mixed crawl, a change behind an
   unchanged shell, chat p95 during the crawl).
5. **User:** `turkos`'s model (allow Opus on the interim key, or republish on `claude-haiku-5-5`/`gpt-6-luna`); the
   roadmap's "Waiting on people" (gold set with DoIT, G1/DPIA, test host, U1, N1/W1); three leftover branches of merged
   PRs (`a/228-fetch-url`, `a/l3-eval-demote`, `a/compat-sweep`, local and remote) that the permission check would not
   let the session delete.

## Local stack (this machine)

- Start: `export Seed__Enabled=false; aspire start --apphost src/Ume.Rag.AppHost/Ume.Rag.AppHost.csproj
  --non-interactive` (Docker up: `docker info`). Stop: **`aspire stop --all --non-interactive`**. After merges:
  `dotnet build UmeRag.slnx`, then restart. It runs `main` at `57b2a5c` now, renderer included.
- Aspire CLI: `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire` (not on PATH).
  `aspire logs worker --non-interactive --search <text>`; `aspire describe --apphost …` shows resource states.
- Ports: dashboard `https://localhost:17470`; **admin UI `https://localhost:5174`** (user `rag-platform-admin`); api
  7401, admin API 7402, worker 7403; **Open WebUI `http://localhost:7410`**, Keycloak 7411, Postgres 7412, parser 7413,
  ClamAV 7414, **renderer 7417** (`Render:Mode` `container` default, `off` skips it). Platform objects are under
  `/plattform/<resource>` (e.g. `/plattform/settings`).
- **Parser** goes cold after 15 idle minutes; `PARSER_IDLE_STOP_S=0` / `NER_IDLE_STOP_S=0` keep it warm.
- **Data:** department DoIT; collection `turkos` (1,166 live documents, a 100-question generated evaluation set),
  assistant `turkos` v1 on `claude-opus-5-5` (refused by the key). Collection **`renderer-demo`** with two crawl sources
  of `quotes.toscrape.com` (`/js/` as `browser`, `/` as `http`; 5 pages each). Settings: `rerankModel` and the
  evaluation models `claude-haiku-5-5`, `gatewayRerankModel` `Cohere-rerank-v5-fast`; manual mirror rows
  `claude-haiku-5-5`, `claude-opus-5-5`, `Cohere-rerank-v5-fast` (rerank) — model sync is not configured locally
  (`sync_off`), so new models need a manual `config."GatewayModels"` row.
- **Rerank comparison:** `POST /api/collections/{turkos}/evaluation/run` with
  `{"variants":["hybrid","hybrid-rerank","hybrid-rerank-gateway"]}`; result in `content."Jobs"."Result"`.
- Feedback importer: every minute, user secret `Parameters:openwebui-admin-api-key`. Logins:
  `~/ume-rag-demo-login.txt` (mode 600; port 7410). Draft gold sets: `~/ume-rag-gold-draft-*.csv`, `plans/gold-set/`.
- Postgres: `docker exec -e PGPASSWORD="$(docker exec <postgres-…> printenv POSTGRES_PASSWORD)" <postgres-…> psql -h
  localhost -U postgres -d ragdb` (schemas `config`, `content`, `chat`).
- Local images: `ume-rag/renderer:ci` (also tagged `ume-rag.local/renderer:ci`; the container test uses
  `UME_RAG_TEST_RENDERER_IMAGE=ume-rag.local/renderer:ci`), `ume-rag.local/clamav:dev`, `ume-rag.local/parser:ci`,
  `ume-rag.local/postgres:ci`.

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

## Environment notes

- `rag_key.txt` in the repo root holds the interim-gateway key (mode 600, excluded in `.git/info/exclude`); the
  address and the same key are in the AppHost user secrets (`Gateway:BaseUrl`, `Gateway:Keys:Eu`). Never commit,
  print or move them.
- **Git over HTTPS:** `git -c credential.helper= -c 'credential.helper=!gh auth git-credential' fetch|pull|push …`.
- Self-hosted runners `ume-rag` and `ume-rag2` share one machine and one Docker daemon (`docs/ci-runner.md`); the
  renderer tests use fixed subnets 10.214.0.0/24 and 10.215.0.0/24, so two renderer jobs at once could collide.
- `172.30.0.0/16` is taken on this machine; test networks use 10.21x.0.0/24.
