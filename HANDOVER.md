# Handover

State of the Ume.Rag work for the next session. Rewritten 2026-11-06, end of session 22 (`main` after #412). Earlier
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

## Where we are

- **P0** released (`v0.1.0`), **P1a code** done, **P1b code** almost done. Open code items: **B-4** (accessibility,
  NVDA), **B-12** (tick pending; live run recorded on #403), **B-16** (`GatewayReranker`, in progress; its evaluation
  hand-off to stream A is **#414**), **#413** (group-name collision in the shared realm, not a pilot blocker),
  **#410 items 3–4** (hot-path: one answer stream, one 502/504 retry; after B-16), then the pin reviews **A-4/B-6**,
  then **M-3** (code complete). **A-5** waits on gateway release 1.
- **Gateway release 1 is merged on the gateway's `main` but not tagged** (latest tag v0.8.9): residency ceiling
  (#66), embeddings per input (#65), FakeLlm (#67), rerank endpoint (#68), Keycloak project (#70), EUPL (#69).
  Reviewed from Ume.Rag's side in session 22: header names match, `x-ume-residency` is set on every routed answer
  (streaming and realtime too), rerank goes through the same pipeline. Draft #71 adds LDAP/sign-in tests.
- **Rerank (measured, session 22, `turkos`, 100 generated questions, Haiku as `ChatModelReranker`):** paired per
  question 4 better, 0 worse, 96 same; MRR 0.786 → 0.808; +1.1 s and ~3k input tokens per question. 57 of 100 fell
  back, mostly the interim proxy's 502s. Hybrid is already strong on generated questions; the gold set and B-16
  (Berget `bge-reranker-v2-m3`) decide. Rerank is optional everywhere and off by default.
- **Keyword weight 0.25 on this machine's set:** recall 0.91 → 0.91, MRR 0.786 → 0.796. The session-19 machine's
  0.98 came from its easier set. Don't save 0.25 on `turkos` until the gold set.

## Decided this session (user, 2026-11-05/06)

- **M-2 answered** (Decisions "M-2 open design calls"): uploads need no topics; `UrlCanonicalizer` stays in the
  Worker; fired PII types stored (B-8 done); HNSW rebuild on its own connection (B-7 done); index health for
  multi-collection scopes (B-9, #401 done); scope size counts topic-filtered chunks (B-10 done).
- **Sitevision `open.*`/`sv.*` dropped on every host** (A-3, #382).
- **Rerank pulled forward**, interim `ChatModelReranker` then `GatewayReranker`; **rerank optional everywhere**.
- **Gateway release 1 and shared Keycloak as built** (#390): 502/504 retried with the same ceiling; keycloak-config-cli;
  realm-wide settings in the gateway's base file; **no admin OTP for the pilot** (record in G1); Keycloak 26.8.1.
- **Hot-path tidy** (#410): items 1, 2, 5, 6 done in #411 (no behaviour change).
- From the other machine's main session (#397): browser tests optional on releases (`RELEASE_E2E` unset). **Ask the
  user to confirm** if not yet done.

## Next session

1. **GitHub:** `gh pr list`, `gh issue list`. Review B-16's PR (rerank optional: no call, no `rerank_unavailable`,
   switch disabled without a model) and do **#414** (stream A: evaluation compares both rerankers). Close #403 when
   B-12 is ticked.
2. **#410 items 3–4** once B-16 has landed (main-session branch, implementor; check the chat live afterwards).
3. **User:** tag gateway release 1 (then B-L4, A-5); the roadmap's "Waiting on people" (test host, gold set with
   DoIT, G1, U1); allow `claude-opus-5-5` on Ume.Rag's interim gateway key again (see Local stack).

## Local stack (this machine)

- Start: `export Seed__Enabled=false; aspire start --apphost src/Ume.Rag.AppHost/Ume.Rag.AppHost.csproj
  --non-interactive` (Docker up: `docker info`). Stop: **`aspire stop --all --non-interactive`** (after a worktree
  run, a stale AppHost can survive a plain stop). After merges: `dotnet build UmeRag.slnx`, then restart.
- Aspire CLI: `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire` (not on PATH).
  `aspire logs worker --non-interactive --search <text>` reads a resource's log.
- Ports: dashboard `https://localhost:17470`; **admin UI `https://localhost:5174`** (user `rag-platform-admin`); api
  7401, admin API 7402, worker 7403; **Open WebUI `http://localhost:7410`**, Keycloak 7411, Postgres 7412, parser 7413,
  ClamAV 7414.
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
- **Stream B** is a remote agent (GitHub `thekim1`), with its own coordinating main session on the other machine;
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

## Environment notes

- `rag_key.txt` in the repo root holds the interim-gateway key (mode 600, excluded in `.git/info/exclude`); the
  address and the same key are in the AppHost user secrets (`Gateway:BaseUrl`, `Gateway:Keys:Eu`). Never commit,
  print or move them.
- **Git over HTTPS:** `git -c credential.helper= -c 'credential.helper=!gh auth git-credential' fetch|pull|push …`.
- Self-hosted runners `ume-rag` and `ume-rag2` share one machine (`gh-buildagent`, per `docs/ci-runner.md`).
- Local images: `ume-rag.local/clamav:dev`, `ume-rag.local/parser:ci`, `ume-rag.local/postgres:ci`. `172.30.0.0/16` is
  taken on this machine.
