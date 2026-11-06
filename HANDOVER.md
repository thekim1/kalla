# Handover

State of the Ume.Rag work for the next session. Rewritten 2026-11-06, end of session 23 (`main` at `ec03525`). Earlier
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
4. The design for the area at hand (map in `plans/README.md`). History is in `plans/archive/` and does not bind.
5. **`plans/cases/support-assistant/`**: the first case plan (non-binding): an internal support assistant for ticket
   handling with a frontend built by another team. README has the user's decisions; `05-frontend-integration.md` is
   the contract for that team.

## Where we are

- **P0** released (`v0.1.0`), **P1a/P1b code** done apart from M-3 (code complete) and the pin reviews (A-4, B-6).
  **Stream B is inactive** (user, 2026-11-06): the main session builds B's paths on `main-session/<slug>` branches.
- **Gateway v0.9.0 and v0.9.1 are released** (thekim1/llmgateway): residency ceiling, per-input embedding check,
  `/v1/rerank`, Keycloak project, EUPL (0.9.0); **rerankers under the Foundry provider and a residency per model**
  (0.9.1, gateway #72/#73, from our issue). The interim gateway runs ≥ 0.9.0 (it answers a bad
  `x-ume-max-residency` with 400).
- **Merged in session 23** (all CI green):
  - A-5 #429: no token cap per embedding request (live: 64 inputs, 59,584 tokens, one request).
  - #423/#426: role groups must be `rag-` names or Entra ids (identity §1.4/§2.5); member-groups hint.
  - Support-assistant gaps, designed (Decisions rows "Related documents and the `ume` chat extension", "Follow-up
    suggestions", "Long questions condensed into the search query", "Chat rate limit per key"; chat §3.8, §5.1,
    §9.7, §9.8) and built: **M-5** #430 per-key chat limit (`Keys.RequestsPerMinute`), **M-6** #431 questions up to
    16,000 characters (long ones condensed into the search query, full text with line breaks to the answer model),
    **M-7** #433 the `ume` extension (`outcome`, `degraded`, `related_documents`), **M-8** #434 follow-up
    suggestions (second `ume` chunk; a task-model call never fails the turn).
  - #432 (closes #424): key `AllowedIpRanges` enforced (identity §6.3 step 4); rag-api takes the client address from
    `X-Forwarded-For` only via `Settings.TrustedProxies` (gateway `ClientAddressMiddleware`).
  - Architecture review (external, assessed by a subagent): #437 V17 accepts exactly what the runtime reads
    (integer `QuerySteps`, `contextTokens` 4,096–131,072, boolean `condense.enabled`); #436 preview/production
    parity guard; **#435 filed** (chunk dates: HTTP `Last-Modified` and reliable `lastmod` never reach production);
    the rest (typed-settings generator, lifecycle services, assistant evaluation, presets) is P2.
- **`ume` checked with the OpenAI SDKs** (Python 3.46.0, JS 7.53.0; chat §22): send via `extra_body` / request
  field, read `model_extra["ume"]` / `.ume`. .NET SDK not checked.
- **Measured on the interim gateway** (chat §22): long condense of a 16,000-character ticket `gpt-6-luna` 2.3–2.9 s,
  `claude-haiku-5-5` ~1.1 s; follow-ups luna 1.4–2.6 s, haiku 1.1–1.3 s.

## Decided this session (user, 2026-11-06)

- **Support-assistant case:** DoIT owns it; level `internal`; intranet readable without login; `fallback` general
  knowledge; one shared app key; frontend keeps history; Markdown uploaded by hand now, Git connector (SA-10) after the
  pilot; build SA-1, SA-3, SA-4, SA-6 before the pilot (done); personal data by instructions + DPIA, no redaction.
- **Reranker for the POC: `Cohere-rerank-v5-fast` on Azure AI Foundry (EU)** instead of Berget; models and their
  location are configuration in the gateway admin UI, never a code change (gateway #72).
- **Condense model stays `gpt-6-luna`**; for the support case set `Chat:LongQuestionCondenseTimeout` 4 s and
  `Chat:FollowUpTimeout` 3 s (case `04-assistant.md` §2). The defaults in code are still 3 s and 2 s; no Decisions
  row changed (the user approved only the case-plan recommendation).
- **The `ume` name and placement** kept (namespaced extension object, opt-in, top level).

## Next session

1. **GitHub:** `gh pr list`, `gh issue list`. Stream B is inactive; check whether it resumed.
2. **#421 live rerank run** (B-12, B-16) waits on the **user**: add `Cohere-rerank-v5-fast` under the Foundry provider
   on the interim gateway (v0.9.1: kind Rerank, publisher `cohere`, residency Eu) and allow it on Ume.Rag's key.
   Last probe still gave 503/403. Then: model sync, `Settings.Retrieval.gatewayRerankModel`, evaluation
   `{"variants":["hybrid","hybrid-rerank","hybrid-rerank-gateway"]}` on `turkos`, report on #421, tick B-12/B-16.
3. **#435 (stream A):** carry `Last-Modified`/reliable `lastmod` into production `SegmentInput`s and take out the shared
   stage-input builder for production, preview and `DocumentProcessor`. Needed before any host gets
   `lastmodReliable` (to-verify #170); not a pilot blocker.
4. **Optional, ask first:** whether to raise the code defaults of the two budgets for `gpt-6-luna` (Decisions row);
   the v0.2.0 release (images and tag are outward-facing; turn the browser tests on); M-3 code complete.
5. **User:** the roadmap's "Waiting on people" (test host, gold set with DoIT, G1, U1); allow `claude-opus-5-5` on the
   interim key again or publish `turkos` on another model; confirm #397 (browser tests optional on releases).
6. **Stale worktrees** from earlier sessions: `../rag-a1`, `../rag-a3`, `../rag-a4`, `../rag-main`, `../rag-review`.
   Check for unpushed work before removing them.

## Local stack (this machine)

- Start: `export Seed__Enabled=false; aspire start --apphost src/Ume.Rag.AppHost/Ume.Rag.AppHost.csproj
  --non-interactive` (Docker up: `docker info`). Stop: **`aspire stop --all --non-interactive`** (after a worktree
  run, a stale AppHost can survive a plain stop). After merges: `dotnet build UmeRag.slnx`, then restart.
- Aspire CLI: `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire` (not on PATH).
  `aspire logs worker --non-interactive --search <text>` reads a resource's log.
- Ports: dashboard `https://localhost:17470`; **admin UI `https://localhost:5174`** (user `rag-platform-admin`); api
  7401, admin API 7402, worker 7403; **Open WebUI `http://localhost:7410`**, Keycloak 7411, Postgres 7412, parser 7413,
  ClamAV 7414.
- **Parser** goes cold after 15 idle minutes (convert and NER); the first request after that waits a few seconds.
  `PARSER_IDLE_STOP_S=0` / `NER_IDLE_STOP_S=0` keep it warm.
- **Data:** department DoIT; collection `turkos` (1,166 live documents); assistant `turkos` v1 published on
  `claude-opus-5-5`. **The interim gateway key no longer allows Opus** (403 `model_not_allowed`; it lists only
  `claude-haiku-5-5` and `gpt-6-luna`), so turkos fails in Open WebUI until the key allows Opus again or the user
  approves publishing on another model. Settings: `rerankModel`, `evaluationModel`, `evaluationJudgeModel` =
  `claude-haiku-5-5`; mirror row `claude-haiku-5-5` (manual, `Eu`, chat, no prices). **A 100-question generated
  evaluation set** for `turkos`.
- **Rerank comparison here:** `POST /api/collections/{turkos}/evaluation/run` with
  `{"variants":["hybrid","hybrid-rerank"]}` (or `candidateSettings`); result in `content."Jobs"."Result"`.
  `scripts/evaluation/rerank.sh` expects a Compose deployment, not Aspire.
- Feedback importer: every minute, user secret `Parameters:openwebui-admin-api-key`. Logins:
  `~/ume-rag-demo-login.txt` (mode 600; use port 7410). Draft gold sets: `~/ume-rag-gold-draft-*.csv`, `plans/gold-set/`.
- Postgres: `docker exec -e PGPASSWORD="$(docker exec <postgres-…> printenv POSTGRES_PASSWORD)" <postgres-…> psql -h
  localhost -U postgres -d ragdb` (schemas `config`, `content`, `chat`).

## How we work (user preferences, binding)

- **Good and easy to use:** look at pages in the browser, not only tests; fix friction or file it.
- **Demo first; admin UI focus.** No extra negative proofs or flake hunts unless a change needs one. No tests that
  depend on timing ("wait until X, fail after 60 s" is fine).
- **Approve and merge without asking** when a PR is ready (reviewed, checked live where relevant, CI green with the
  tests actually run); tell the user afterwards. Design, scope and destructive actions go to the user.
- **Stream B** (currently inactive; the main session takes its items on `main-session/` branches) is a remote agent (GitHub `thekim1`), with its own coordinating main session on the other machine;
  talk only through GitHub (labels `stream-b`, `stream-a`, `main`). B merges its own PRs; review its
  security-relevant parts. `gh` acts as `ume-nichlas`.
- **Main session** owns `plans/**`, root `*.md` and `.github/stream-ownership.txt` (commit to `main`). Stream-A work
  on `a/<id>-<slug>` branches; a PR touching another stream's paths uses a `main-session/<slug>` branch (the
  ownership check lets any non-`a/`/`b/` branch through).
- **Lean CI:** PR jobs path-filtered; full set and `e2e` before a release; agents run only the test projects they
  touch; no re-dispatching `verify` on main.
- **Subagents:** own worktree (`../rag-aN`), full brief, no touching the live stack unless the brief says so. Their
  report is a claim: read the diff, check CI's test totals, check live. The `implementor` agent type may refuse git
  writes; then commit its patches yourself and diff the result against its final tree.
- **Shared files** (`Directory.Packages.props`, `README.md`, `CHANGELOG.md`, `UmeRag.slnx`, `.gitignore`): small PR,
  append-only. Secrets never go into the repo, a log or a chat. Verify claims against real sources.

## Lessons

- **Admin API scripting:** a working client is easy to rebuild (`/bff/login` → Keycloak form with
  `rag-platform-admin` and the AppHost secret `Parameters:dev-user-password` from
  `~/.microsoft/usersecrets/ume-rag-apphost-dev/secrets.json` (BOM) → the `form_post` page → `GET /bff/user`, check
  `isAuthenticated`, sets `RAG-XSRF-TOKEN`). Writes need `X-Requested-With: XMLHttpRequest` and `X-XSRF-TOKEN`.
  A draft made by API needs `collectionLinks`, or it searches nothing.
- **The interim gateway:** Claude models refuse `temperature`; embeddings ≤ 8,192 tokens per request; 403 for
  Python's default User-Agent; 429 on embeddings (requeued); **its front proxy answers intermittent plain
  `error code: 502`** (up to half the calls under an evaluation run), so live rerank numbers carry many fallbacks.
- **Browser checks:** `npm i --ignore-scripts playwright-core` in a scratch folder; `executablePath`
  `~/.cache/ms-playwright/chromium-1247/chrome-linux*/chrome`. Wait for the data, not only `networkidle`.
- **Editing workflows by script:** match job blocks exactly (#367/#372).
- Read the first real failure; don't overlap two Worker.Tests runs. Admin UI tests: `npx vitest run --pool=threads`;
  a worktree needs `node_modules` (symlink, remove before commit).
- When editing a stream's queue on `main`, `pull --rebase` first: stream B edits queues too.
- The `implementor` agent refuses git writes: review its diff, commit yourself, and never `git add -A` in a worktree
  (the `src/admin-ui/node_modules` symlink is not ignored by `node_modules/`). Remove the symlink before
  `git worktree remove`.
- Tests that listen to process-wide `ActivitySource`s or meters must filter to their own trace or host scope; tests
  run in parallel (#431).
- Live latency checks: build the exact request body in a scratch script and `curl` the interim gateway with the
  key from `rag_key.txt` and the base URL from the AppHost secrets; never print either.
- `Chat:*Timeout` knobs are TimeSpans (`00:00:03`); a bare `3` means three days.

## Environment notes

- `rag_key.txt` in the repo root holds the interim-gateway key (mode 600, excluded in `.git/info/exclude`); the
  address and the same key are in the AppHost user secrets (`Gateway:BaseUrl`, `Gateway:Keys:Eu`). Never commit,
  print or move them.
- **Git over HTTPS:** `git -c credential.helper= -c 'credential.helper=!gh auth git-credential' fetch|pull|push …`.
- Self-hosted runners `ume-rag` and `ume-rag2` share one machine (`gh-buildagent`, per `docs/ci-runner.md`).
- Local images: `ume-rag.local/clamav:dev`, `ume-rag.local/parser:ci`, `ume-rag.local/postgres:ci`. `172.30.0.0/16` is
  taken on this machine.
