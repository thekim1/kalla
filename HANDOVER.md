# Handover

State of the Ume.Rag work for the next session. Rewritten 2026-11-05, end of session 21 (`main` after #379). Earlier
sessions are in the git history (`git log -- HANDOVER.md`).
Repo `~/research/rag` on this machine (`~/git/ume-rag-platform` on the session-19 machine), branch `main`, remote
`origin` = https://github.com/thekim1/ume-rag-platform (push after every commit).

## What this is

**Ume.Rag** (working name, N1) is a RAG / crawling / chatbot platform for Umeå kommun: small .NET 10 services on top
of the team's **LLM gateway** (https://github.com/thekim1/llmgateway, `~/git/llmgateway`). The user owns the gateway
and wants to do better than **Eneo**, a slow "kitchen sink" monolith. Design and code live in this repo.

## Where to read

1. `01-general-idea.md` §10 **Decisions table** (rows `current` bind).
2. **`plans/roadmap.md`**: done, what is left before go-live, the live block, waiting on people, P2/P3. One page.
3. **`plans/work/`**: the queues `stream-a.md`, `stream-b.md`, `main.md` (each item: do, paths, done-when, waits on,
   read). Items are ticked in the PR that does them. Start prompts in `plans/work/prompts/`.
4. The design for the area at hand (map in `plans/README.md`). History (phase plans, passes, area plans) is in
   `plans/archive/` and does not bind.

## Where we are

- **P0** released (`v0.1.0`), **P1a code** done (demo 2026-11-01), **P1b code** nearly done: organisation (sharing,
  topics, Map), general knowledge per assistant, default prompt and *Advanced*, chat-models-only picker (B-1, #379).
  All reviewed live in session 21 except #379 (merged by B after the review round; CI green).
- **Browser tests green on the runner** (#377, 9/9: both pilot journeys, mouse and keyboard, and the accessibility
  specs). `e2e` runs on dispatch and from `release.yml`, never on PRs.
- **What is left before go-live** is the roadmap's live block plus a few code items (A-1 licence notices, B-2 #374,
  B-3 records, B-4 NVDA pass, the pin review A-4/B-6). The live block waits on people: gold set with DoIT, G1, the
  gateway session (prompt updated in #378), test host, U1.
- **Search quality:** on `turkos`'s generated set, `keywordWeight` 0.25 gives recall@10 0.98, MRR 0.77 (hybrid default
  0.91/0.70). Confirm on the human gold set (B-L2) before the template default changes.

## Decided this week (user)

- **Session 21 (2026-11-05):** lean CI (PR jobs path-filtered; full set and `e2e` before a release; agents run only the
  test projects they touch; no re-dispatching `verify` on main). Plans condensed (#375). #352 merged without its
  benchmark. **Identity providers** (#378, new Decisions row): AD and Entra ID must both work, brokered through
  Keycloak and directly; Ume.Rag must be able to run standalone later.
- **Session 20:** general knowledge per assistant (`off`/`supplement`/`fallback`/`open`; `public` always `off`).
- **Session 19:** architecture-plan item 3 (presets) stays P2; `turkos` keyword weight 0.25; Open WebUI feedback
  retention 90 days; #333 quick fixes done, findings 1–2 wait.

## Next session

1. **GitHub:** `gh pr list`, `gh issue list`. Has stream B answered #376 (plans restructure)? Review #379 (B-1) live:
   the model picker shows chat models only after a restart with the new migration.
2. **Main queue** (`plans/work/main.md`): **M-2**, short recommendations with defaults for the open design calls
   (topics for uploads; `UrlCanonicalizer` into Pipelines #178; #217's fired-types list; identity §16 Q2–4, Q8 against
   the new identity-providers row; a direct connection for the HNSW rebuild behind a pooler; index health for
   multi-collection scopes; recall for a narrow topic in a large collection).
3. **Stream A** (main session runs it): **A-1** licence notices (#51) is unblocked; good for an implementor subagent.
4. **Stream B** continues from its queue: B-2 (#374), B-3, B-4.
5. **User:** the roadmap's "Waiting on people" table.

Open issues: **B** #376, #374, #363 (point 3 waits on the user), #333, #52. **A** #51, #48. **Main** #100. **User**
#215.

## Local stack (this machine)

- Start: `export Seed__Enabled=false; aspire start --apphost src/Ume.Rag.AppHost/Ume.Rag.AppHost.csproj
  --non-interactive` (Docker up: `docker info`). Stop: `aspire stop --non-interactive`. After merges: `dotnet build
  UmeRag.slnx`, then restart. **Running at `6d4b3df`: restart on the current `main`** for #379's migration.
- Aspire CLI: `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire` (not on PATH).
- Ports: dashboard `https://localhost:17470`; **admin UI `https://localhost:5174`** (user `rag-platform-admin`); api
  7401, admin API 7402, worker 7403; **Open WebUI `http://localhost:7410`**, Keycloak 7411, Postgres 7412, parser 7413,
  ClamAV 7414.
- **Data on this machine** (volume `ume-rag-m1-postgres`) is older than the session-19 machine's: department DoIT;
  collection `turkos` (public, published, sharing `department`, no `keywordWeight` saved, 1,270 documents, 1,166 live,
  4,990 chunks); assistant `turkos` v1 on `claude-opus-5-5`; archived test assistants `gk-check`, `review-39`; client
  "Open WebUI" with `PortalUrl` `http://localhost:7410` and retention 90 days. No more crawling at this stage (user).
- Feedback importer: every minute, user secret `Parameters:openwebui-admin-api-key`. Logins:
  `~/ume-rag-demo-login.txt` (mode 600; use port 7410). Draft gold sets: `~/ume-rag-gold-draft-*.csv`, `plans/gold-set/`.
- Postgres: `docker exec -e PGPASSWORD="$(docker exec <postgres-…> printenv POSTGRES_PASSWORD)" <postgres-…> psql -h
  localhost -U postgres -d ragdb` (tables in schemas `config`, `content`, `chat`).

## How we work (user preferences, binding)

- **Good and easy to use:** look at pages in the browser, not only tests; fix friction or file it.
- **Demo first; admin UI focus.** No extra negative proofs or flake hunts unless a change needs one. No tests that
  depend on timing ("wait until X, fail after 60 s" is fine).
- **Approve and merge without asking** when a PR is ready (reviewed, checked live where relevant, CI green with the
  tests actually run); tell the user afterwards. Design, scope and destructive actions go to the user.
- **Stream B** is a remote agent (GitHub `thekim1`); talk only through GitHub (labels `stream-b`, `stream-a`, `main`).
  B merges its own PRs; review its security-relevant parts. `gh` acts as `ume-nichlas`.
- **Main session** owns `plans/**`, root `*.md` and `.github/stream-ownership.txt` (commit to `main`). Stream-A work
  on `a/<id>-<slug>` branches; a PR touching another stream's paths uses a `main-session/<slug>` branch.
- **New work** goes into the owning stream's queue as an item; update the roadmap when a phase step lands.
- **Subagents:** own worktree (`../rag-aN`), full brief, no touching the live stack unless the brief says so. Their
  report is a claim: read the diff, check CI's test totals, check live.
- **Shared files** (`Directory.Packages.props`, `README.md`, `CHANGELOG.md`, `UmeRag.slnx`, `.gitignore`): small PR,
  append-only. Secrets never go into the repo, a log or a chat. Verify claims against real sources.

## Lessons

- **Admin API scripting** (scratch in the session's scratchpad): `/bff/login` → Keycloak form (`rag-platform-admin`,
  password = AppHost user secret `Parameters:dev-user-password`, file `~/.microsoft/usersecrets/ume-rag-apphost-dev/
  secrets.json` with a BOM) → the upper-case `form_post` page (parse case-insensitively) → `GET /bff/user` sets
  `RAG-XSRF-TOKEN`; writes need `X-Requested-With: XMLHttpRequest` and `X-XSRF-TOKEN`. Publish takes
  `{version, draftVersion}`; a client update needs `clientRetentionDays` for `open-webui`.
- **Browser checks:** `npm i --ignore-scripts playwright-core` in a scratch folder; `executablePath`
  `~/.cache/ms-playwright/chromium-1247/chrome-linux*/chrome` here. Wait for the data, not only `networkidle`, before a
  screenshot (the Sharing tab's "Only DoIT" loads a moment later).
- **Editing workflows by script:** match job blocks exactly. #367 matched `images:` inside `outputs:` and swapped the
  backend and images conditions (fixed in #372); a PR that changes `verify.yml` runs every job, so it can't catch that.
- **The interim gateway:** Claude models refuse `temperature`; embeddings ≤ 8,192 tokens per request; 403 for
  Python's default User-Agent; 429 on embeddings (requeued).
- Read the first real failure; don't overlap two Worker.Tests runs (MTP `--filter-class` values are OR'd). Admin UI
  tests: `npx vitest run --pool=threads`; a worktree needs `node_modules` (symlink, remove before commit).
- The auto-mode check blocks minting credentials unless the user allows it in the conversation.

## Environment notes

- `rag_key.txt` and `rag_gateway_url.txt` in the repo root hold the interim-gateway key and address (mode 600,
  excluded in `.git/info/exclude`, also in the AppHost user secrets). Never commit, print or move them.
- **Git over HTTPS:** `git -c credential.helper= -c 'credential.helper=!gh auth git-credential' fetch|pull|push …`.
- Self-hosted runners `ume-rag` and `ume-rag2` share one machine (`gh-buildagent`, set up per `docs/ci-runner.md`).
- Local images: `ume-rag.local/clamav:dev`, `ume-rag.local/parser:ci`, `ume-rag.local/postgres:ci`. `172.30.0.0/16` is
  taken on this machine.
