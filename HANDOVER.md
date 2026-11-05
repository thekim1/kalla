# Handover

State of the Ume.Rag work for the next session. Rewritten 2026-11-02, end of session 19 (`main` = `122990d`, after
#349); updated 2026-11-05, session 20 (general knowledge, P1b order, housekeeping) and session 21 (reviews of 35, 36,
39, 40; #360; lean CI). Earlier sessions are in the git history (`git log -- HANDOVER.md`).
Repo `~/research/rag` on this machine (`~/git/ume-rag-platform` on the session-19 machine), branch `main`, remote `origin` = https://github.com/thekim1/ume-rag-platform (push after
every commit).

## What this is

**Ume.Rag** (working name, N1) is a RAG / crawling / chatbot platform for Umeå kommun: small .NET 10 services on top
of the team's **LLM gateway** (`~/git/llmgateway`, https://github.com/thekim1/llmgateway). The user owns the gateway
and wants to do better than **Eneo** (`~/git/eneo`), a slow "kitchen sink" monolith. Design and code live in this repo.

## Where we are

- **The demo runs:** crawl → retrieve → cited answer in Open WebUI from the `turkos` assistant (`claude-haiku-5-5`)
  over the user's interim gateway at `Eu`; follow-ups are condensed. P0 released (`v0.1.0`); P1a code complete (its live
  checks moved to P1b item 20); **P1b in progress**.
- **Organisation (Decisions "Organisation …"): built.** Schema #346, topics in retrieval #347 and the worker #349, the
  platform Organisation form #348, the admin API #361 (item 35: sharing with V35, `view=mine|shared|all`, `/topics`,
  the Map route, contact on collections, `collectionLinks` in the draft contract) and the pages #366 (item 36: the menu,
  *Ours / Shared with us / All*, Topics and Sharing tabs, the picker with topics, *Who can use it*, the Map table).
  All reviewed live in session 21; cross-department access rests on `OrganisationSharingTests` (only DoIT exists
  locally). Their design deviations are settled in the admin design (`a014e71`, `6d4b3df`). Open: #374 (archived
  assistants look active on the Map and in *Used by*; two document counts).
- **General knowledge per assistant: built and checked live** (#358, #359; #356 closed). `fallback` on an off-topic
  question gives the caveat line and `answered-general`; `off` the not-found text; V36 warns on a `public` draft,
  refuses its publish and the run time answers `off`. #356 notes a possible later rename of "assistant" ("knowledge
  scope"), not decided; new names avoid "assistant".
- **Item 39 built** (#369): a new assistant's draft starts from `Settings.Chat.defaultSystemPrompt` (a citizen-service
  text in code; paste the `turkos` prompt there if wanted); model and query settings under a collapsed *Advanced*;
  "Open in <client>" from the client's `PortalUrl`.
- **Stream B:** P1b code items done; what is left is in `plans/work/stream-b.md` (B-1 `ModelKind`, B-2 #374, records,
  NVDA pass, item 36's E2E step on the first green `e2e` run, pin review). Next for B: #363 point 1 (`GatewayModels.ModelKind`, design
  `5f8f8c1`) and #374. Item 14 (browser tests, L7) was done by the main session (#362, #364).
- **Topics, in one line** (user asked): one owner crawls umea.se once and tags it by path; each assistant links it with
  only the topics it needs; topics narrow search, they are not access control. Uploads have no `pathRules`, so no topics
  (recommended: leave it until a department has a large mixed upload knowledge base).
- **Search quality:** generated set for `turkos` (100 q): hybrid 0.91/0.70 → with **`keywordWeight` 0.25: 0.98/0.77**
  (recall@10/MRR; saved on `turkos`, user). The misses were fusion, not content: vector rank 1–2 pages absent from the
  OR-keyword list lost to pages mediocre in both lists. Of the 9 old misses: 2 near-duplicates, 1 Finnish-page question,
  2 answered by other pages, 4 fusion. Hybrid still wins on exact names. **Confirm on the human gold set** (item 21)
  before the template default changes. Nightly HNSW recall check rebuilds a degraded index (#330).
- **Benchmarks (#331, #333):** B's quick fixes A–D merged (stop-list warm-up before readiness #338, streaming −79 %
  allocation #337, candidate keys #334, adjusters #335); #338's deviations settled in knowledge §6.2. Open with the
  user: finding 1 (exact vector search ~25 ms at 10k chunks: `exactScanMaxChunks`), finding 2 (stop-list statistics
  stop loading near 1M chunks per profile). Finding 3 = #336 (chunker, stream A).
- **W3 spike done (#345, to-verify #174):** Open WebUI v0.12.3 group permissions hide models per user over our
  client-wide `/v1/models`; interleaved chat works; a direct call to a hidden model gets 400; OIDC groups sync by name.
  **Fail-closed:** with the bypass off a new assistant is hidden until granted in Open WebUI → a grant step in the
  publish journey if groups are used (register W3). AppHost switch `OpenWebUi:BypassModelAccessControl` (default true).
- **Feedback:** the importer runs locally (#340) and prunes; an `open-webui` client defaults to 90 days and can't clear
  it (#344, chat §13.3).
- **Architecture plan** (`architecture_plans.md`, a proposal): reviewed and fixed (`2e0ed77`); item 3 (presets) stays
  **P2** (user); its P1b part is item 39. Adopting items 1, 2, 4–7 as design rules is still open.
- **Platform/CI:** Aspire 13.6.4 (#339); fixed local ports (#341); MSBuild node reuse off in CI (#342: both runners
  share one machine). **Lean CI** (user, session 21, repo-and-deployment §8.2): on PRs `images` and `evaluation-smoke`
  run only when their paths change (#367, conditions fixed in #372); `e2e` only from `release.yml` or by hand (#371,
  #373), never on PRs. `e2e` reaches the browser step on the runner (#368) but Chromium lacks its system libraries:
  the runner host needs `docs/ci-runner.md` (#370). Item 15's `e2e` box waits on that.

## Decided in session 19 (user)

- Item 3 of the architecture plan (fixed assistant presets) **stays P2**; P1b item 39 instead: a new assistant's first
  draft starts from a default citizen-service prompt; model and query settings under "Advanced".
- **`turkos` keyword weight 0.25** (template default only after the gold set).
- **#333 quick fixes A–D** go ahead (done); findings 1–2 wait.
- **Open WebUI feedback retention 90 days** (default for `open-webui` clients).
- **The main session takes B's items** 33, 34, 18 and #343 (done); B keeps 35, 36, 39.
- Aspire 13.6.4; fixed ports; the gold set waits.

## Decided in session 20 (user, 2026-11-05)

- **General knowledge per assistant** with the four levels of #356 (Decisions row; replaces the same-day
  `grounding` `strict`/`balanced`/`open` of #355). `public` stays `off`.
- **Item 40 first** in stream B's queue (POC); another agent runs stream B from the new prompt.
- Housekeeping: #314, #300, #210 closed; `plans/archive/progress.md` P1b lines annotated with what is code-done.

## Decided in session 21 (user, 2026-11-05)

- **Lean CI:** path-filtered PR jobs; the full set and `e2e` before a release. Agents run locally only the test
  projects they touch (AGENTS.md "Tests"); no re-dispatching `verify` on main after merges.
- **#352 merged without the benchmark** (byte-identical output and tests were enough).
- #360's cause was the robots.txt cache, not the settings refresh (#365); `GatewayModels.ModelKind` designed for #363.
- **Plans condensed** (#375, `58bf9e5`): `plans/README.md` (three layers and the design map), `plans/roadmap.md` (done,
  now, waiting on people, P2/P3) and `plans/work/` (queues `stream-a.md`, `stream-b.md`, `main.md`; items with paths,
  done-when, waits-on, refs; ticked in the PR that does them; results files; short prompts). Phase plans, passes, area
  plans and old proposals are history in `plans/archive/`. Stream B told in #376. New work goes in as a queue item in
  the owning stream's file; keep the roadmap current when a phase step lands.

## Local stack (this machine)

- Start: `export Seed__Enabled=false; aspire start --apphost src/Ume.Rag.AppHost/Ume.Rag.AppHost.csproj
  --non-interactive` (Docker Desktop up: `docker info`). Stop with `aspire stop --non-interactive` from the directory
  of the running AppHost. After merges: `dotnet build UmeRag.slnx`, then restart (or `aspire resource <r> restart`).
  **It runs from `~/research/rag` at `6d4b3df`, all resources healthy.** The Aspire CLI here prints its "new version"
  notice; the stack is fine.
- Ports: dashboard `https://localhost:17470`; **admin UI `https://localhost:5174`** (user `rag-platform-admin`); api
  7401, admin API 7402, worker 7403; **Open WebUI `http://localhost:7410`**, Keycloak 7411, Postgres 7412, parser 7413,
  ClamAV 7414 (#341).
- **Pilot data on this machine** (volume `ume-rag-m1-postgres`) is older than the session-19 machine's: department
  DoIT; collection `turkos` (public, published, sharing `department`, no `keywordWeight` saved, 1,270 documents of
  which 1,166 live, 4,990 chunks, one sync on 2026-11-01); assistant `turkos` **v1** on `claude-opus-5-5`; archived
  test assistants `gk-check`, `review-39`; client "Open WebUI" `PortalUrl` `http://localhost:7410`, retention 90 days
  (set in session 21). `GatewayModels` holds only `claude-opus-5-5`.
- The feedback importer runs every minute with the user secret `Parameters:openwebui-admin-api-key` (an API key of
  Open WebUI's local admin `demo@umea.se`; Open WebUI restricts keys to the two feedback routes).
- The dev stack runs as the Postgres superuser and doesn't apply `deploy/postgres/app-roles.sql`; grants are tested by
  `DatabaseRoleTests`.
- **Logins:** `~/ume-rag-demo-login.txt` (mode 600; the Open WebUI URL in it is stale: use 7410). Never put passwords
  in a chat.
- **Draft gold sets:** `~/ume-rag-gold-draft-{questions,answers}.csv` (40) and `plans/gold-set/` (199 rows).
- No more crawling at this stage (user). A source's scope change re-syncs it; cancel with `POST /api/runs/{id}/cancel`.

## Reading order

1. `01-general-idea.md` §10 **Decisions table** (rows `current` bind).
2. `plans/roadmap.md` (one page: done, now, waiting on people, P2/P3) and `plans/work/` (the queues of stream A,
   stream B and the main session). Plans were condensed in session 21: phase plans, passes and area plans are in
   `plans/archive/` (history only).
3. The design for the area at hand (map in `plans/README.md`).

## How we work (user preferences, binding)

- **The platform should be good and easy to use:** look at pages in the browser, not only tests; fix friction or file
  it (session 19 found raw field names on every platform form this way, fixed in #348).
- **Demo first; admin UI focus.** No extra negative proofs or flake hunts unless a change needs one.
- **No tests that depend on timing or machine speed:** "wait until X, fail after 60 s" is fine.
- **Approve and merge without asking:** PRs judged ready (reviewed, checked live where relevant, CI green with the
  tests actually run); tell the user afterwards. Design, scope and destructive actions go to the user.
- **Stream B** is a remote agent (GitHub `thekim1`); talk only through GitHub (labels `stream-b`, `stream-a`, `main`).
  B merges its own PRs; review its security-relevant parts proportionately. `gh` acts as `ume-nichlas`.
- **Main session** owns design documents (`plans/**`, root `*.md`, `.github/stream-ownership.txt`): commit to `main`.
  Stream-A work: `a/<item>-<slug>` branches. **Any PR touching another stream's path** (B's admin API, `labels.ts`, the
  AppHost, …): claim it on the issue and use a `main-session/<slug>` branch, or CI's ownership check refuses it.
- **Subagents:** own worktree (`../rag-aN`, `../rag-mN`), full brief, Docker-bound tests named, no touching the live
  stack unless the brief says so. Their report is a claim: read the diff, check the CI log's test totals (after a
  re-run use `gh run view <id> --attempt 1 --log`), and check live.
- **Shared files** (`Directory.Packages.props`, `README.md`, `CHANGELOG.md`, `UmeRag.slnx`, `.gitignore`): small
  PR, append-only.
- Verify claims against real sources. Secrets never go into the repo, a log or a chat.

### Lessons

- **Admin API scripting** (recreate each session; scratch in `/tmp/claude-1000/…`): `/bff/login` → Keycloak form
  (`rag-platform-admin`, password = AppHost user secret `Parameters:dev-user-password`, secrets file has a BOM) →
  the `form_post` page is **upper-case HTML** (`<FORM … ACTION=…>`: parse case-insensitively) → `/signin-oidc`; then
  `GET /bff/user` sets the `RAG-XSRF-TOKEN` cookie; writes need `X-Requested-With: XMLHttpRequest` and `X-XSRF-TOKEN`.
  Test retrieval: `POST /api/collections/{id}/test-retrieval` `{question, topK, mode}`. Evaluation variants:
  `POST /api/collections/{id}/evaluation/run` `{variants: [hybrid|vector|hybrid-no-decompounder], candidateSettings}`.
- **Browser checks:** `npm i --ignore-scripts playwright-core` in a scratch folder, `executablePath`
  `~/.cache/ms-playwright/chromium-1247/chrome-linux*/chrome` on this machine (1248 on the other); sign in via `https://localhost:5174/bff/login`
  (`#username`, `#password`, `#kc-login`); platform pages are under `/plattform/<resource>`.
- **Chat through Open WebUI:** `POST /api/v1/auths/signin`, then `/api/chat/completions`. For feedback mapping, send
  it as the browser does: create the chat (`/api/v1/chats/new`) and pass `chat_id` and the assistant message `id`.
- **The interim gateway:** Claude models refuse `temperature`; embeddings ≤ 8,192 tokens per request; 403 for
  Python's default User-Agent; 429 on embeddings (requeued).
- The auto-mode check blocks minting credentials unless the user allows it in the conversation.
- Read the *first* real failure; don't overlap two Worker.Tests runs. MTP: `--filter-class` values are OR'd.
- Admin UI tests: `npx vitest run --pool=threads`; a worktree needs `node_modules` (symlink, remove before commit;
  the AppHost's installer may replace it with a real directory).

## Code map (on `main`)

| Area | Where | State |
|---|---|---|
| Contracts | `src/Ume.Rag.Pipelines/`, `contracts/` | stage-preview result carries `tags` per document (#349) |
| Worker | `src/Ume.Rag.Worker/` | P1a complete, P1b 4–8, 37 done; HNSW health (#330); evaluator on whole collections |
| Retrieval | `src/Ume.Rag.Retrieval/` | topic predicate in K/V (#347); warm-up before readiness (#338) |
| Answering / Api (B) | `src/Ume.Rag.{Answering,Api}/` | chat with condense; streaming −79 % alloc (#337); feedback importer (#340) |
| Admin API (B) | `src/Ume.Rag.AdminApi/` | item 9 done; department list with member groups + sharing (#348); client retention default (#344) |
| Admin UI | `src/admin-ui/` | English; platform forms with readable labels and working row actions (#348, A) |
| Schema / migrations | `src/Ume.Rag.Infrastructure/…/Migrations/`, `deploy/postgres/app-roles.sql` | `OrganisationSchema` (#346) |
| AppHost (B) | `src/Ume.Rag.AppHost/` | Aspire 13.6.4, fixed ports, local feedback import, W3 switch |

## Next step

1. **Check GitHub and the stack:** `gh pr list`, `gh issue list` (has B answered #376?); `aspire describe`.
2. **Main queue** (`plans/work/main.md`): M-1 run the browser tests locally; M-2 recommendations for the open design
   calls (list below); review B's next PRs live (B-1 `ModelKind`, B-2 #374) and tick nothing for B.
3. **Stream A queue** (`plans/work/stream-a.md`): A-1 licence notices (#51) is unblocked; A-2 the first green `e2e`
   run waits on the runner host (#370, `docs/ci-runner.md`) and then unblocks B-5.
4. **User decides:** the roadmap's "Waiting on people" table (gold set with DoIT, G1, gateway session, test host, #333
   findings 1–2, architecture-plan items 1, 2, 4–7, the W3 grant step, Sitevision `open.*`/`sv.*` on hosts without a
   profile, a Git/Markdown connector).

**Open issues — stream B:** #376 (plans restructure, read first), #374, #363 (points 1, 2 done in #369), #318 (item 36's E2E box), #278, #333 (findings 1–2
with the user), #52. **Stream A:** #370 (runner host), #48, #51. **Main session:** #100 (L8). **User:** #215 (J8).

**Not folded into the design yet (need a call):** topics for uploads (`files` template has no `pathRules`); #217's
fired-types list; `UrlCanonicalizer` → Pipelines (#178); identity §16 Q2–4, Q8; a connection pooler needs a direct
connection for the HNSW rebuild; multi-collection scopes aren't one index-health scope; a narrow topic in a large
collection may take HNSW with post-filter recall loss (measure with the §20 recall check).

**Waiting on the user:** a Data API key (`catalog.read`, read-only) for the org and model syncs (admin §4.10; not a
blocker: departments, teams and `GatewayModels` rows are entered by hand until then); the gateway session (release 1,
shared Keycloak realm with J8 #215, identity §16); **O7** (go-live host) and L10 (test host, backup target, key escrow);
L11 (runner, IT security); L12 (DPIA G1); runbook review; U1 with the web editors; names and dates for A1, G1, N1, U1,
W2, P2 and the gold-set owner.

## Open register items (`plans/postponed-decisions.md`)

A1 (archive law; history `none` only), G1 (DPIA, before go-live), M2 (EU-hosted models until on-prem hardware), W2
(Open WebUI chat copies), N1 (name), U1 (crawler about page), P2 (licences), D1 (standby), R1 (self-hosted runner), S2
(object store), W3 (per-user model lists: spike confirmed, grant step needed), C2 (visual builder, P3). Every
phase-blocking item needs a named person and a date.

## Environment notes

- `rag_key.txt` and `rag_gateway_url.txt` in the repo root hold the interim-gateway key and address (mode 600, excluded
  in `.git/info/exclude`, also in the AppHost user secrets). Never commit, print or move them.
- **Git over HTTPS:** `git -c credential.helper= -c 'credential.helper=!gh auth git-credential' fetch|pull|push …`.
- Aspire CLI: `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire` (not on PATH); it runs the
  13.6.4 AppHost and only suggests `aspire update`.
- Postgres: `docker exec -e PGPASSWORD="$(docker exec <postgres-…> printenv POSTGRES_PASSWORD)" <postgres-…> psql -h
  localhost -U postgres -d ragdb` (or port 7412 from the host).
- Local images: `ume-rag.local/clamav:dev`, `ume-rag.local/parser:ci`, `ume-rag.local/postgres:ci`. `172.30.0.0/16` is
  taken on this machine.
- Self-hosted runners `ume-rag` and `ume-rag2` run on one machine (`gh-buildagent`); MSB4166 was shared MSBuild nodes
  (#342). If it recurs, re-run the job and consider one runner per machine.
