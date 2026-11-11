# Handover

State of the Ume.Rag work for the next session. Rewritten 2026-11-11, end of session 28 (`main` after #551 plus this file;
last release `v0.3.0`). Earlier sessions are in the git history (`git log -- HANDOVER.md`).
Repo `~/research/rag` on this machine (`~/git/ume-rag-platform` on the session-19 machine), branch `main`, remote
`origin` = https://github.com/thekim1/ume-rag-platform (push after every commit).

## What this is

**Ume.Rag** (working name, N1) is a RAG / crawling / chatbot platform for Umeå kommun: small .NET 10 services on top
of the team's **LLM gateway** (https://github.com/thekim1/llmgateway, `~/git/llmgateway`), as an add-on to it (Decisions
"Shared sign-in with the gateway"). The user owns the gateway and wants to do better than **Eneo**, a slow "kitchen
sink" monolith. Design and code live in this repo.

## Where to read

1. [`plans/decisions.md`](plans/decisions.md): the decisions in force (every entry binds). **New since #521**: the old
   `01-general-idea.md` §10 table is `plans/archive/decisions-log.md` (verbatim, doesn't bind). Record a decision by
   changing or adding an entry, then a dated line under the log's *Later decisions* (header of `decisions.md`).
2. **`plans/roadmap.md`**: done, what is left before go-live, the live block, waiting on people, P2/P3. One page.
3. **`plans/work/`**: the queues `stream-a.md`, `stream-b.md`, `main.md`; results in `results-a.md`/`results-b.md`.
4. The design for the area at hand (map in `plans/README.md`). History is in `plans/archive/` and does not bind.
5. **`plans/cases/support-assistant/`**: the first case plan (internal support assistant, DoIT).
6. Not binding, for decisions still to take: `plans/platform/upload-quality-review.md` (upload gaps U1–U7) and
   `plans/platform/assistant-keys-and-budgets-options.md` (keys and budgets per assistant; reopens cost-attribution
   option A).

## Start here

**v0.3.0 is released** (2026-11-10, from stream b's machine: Portainer stack #536, rolling updates #537, Kubernetes
manifests and SIEM collector #538, upgrade guide #539). P1b's remaining work is the **live block (M-4)**, which waits on
people (roadmap "Waiting on people"). A Portainer test stack runs on the other machine; #542, #546 and #547 came from
setting it up.

**Since v0.3.0 on `main`** (Unreleased in the CHANGELOG): the Portainer Data API network fix (#542), neutral admin
wording and `Admin:OrganisationName` (#544), `Crawl:UserAgent` (#545), embedding `dimensions` on by default in the
deploy templates (#546), Aspire 13.6.5 for a security advisory (#549). A **v0.3.1 or v0.4.0** carrying #549 is worth
cutting before the next deployment (full CI set plus a release dry run, `docs/ci-runner.md`).

**Waiting on the user:**
- **Whether to queue the upload gaps** from `upload-quality-review.md`. Suggested: picture descriptions (U1, a P2 item
  brought forward, needs a decision entry) and rerank on (U4, when the gold set shows a gain), then `.msg`/`.eml` (U2), a
  contextual-header trial (U5) and a near-duplicate warning (U6).
- **Keys and budgets per assistant**: the options paper is committed (`af7e30f`); the user hasn't chosen.
- **LibreOffice placement** is decided again **before go-live**, with the parser sandbox question (Decisions "Parser").

**Proposed next**, if the user doesn't choose:
1. **SA-15**: the proxy's chat route still has `client_max_body_size 1m` (`deploy/proxy/nginx.conf.example:34`, `:67`);
   the chat route accepts 2 MB. Also the printed routes and `docs/production-setup.md`.
2. **M-11**: about 15 design-vs-decision contradictions found in the register rewrite (`plans/work/main.md`).
3. **M-10**: settle #499 step 2 (who keeps scope sizes and the stop list) and #501 in the designs. The api role's 60 s
   limit is reached near 3.3M chunks, so step 2 is needed before about 2M chunks.
4. **M-9**: write cost-attribution stage 1 into the designs and queue C1–C6 (depends on the keys/budgets choice).
5. Stream a's cheap items: **A-22** (#500), **A-21** (#503/#504 Compose part), **A-23** (#505), **A-24** (#509).
   Also drop the smoke test's snake_case fallback in `deploy/tests/release-smoke.sh:149` (#529 made `/version`
   camelCase everywhere).
6. Design hand-off from #544: `Admin:OrganisationName` isn't in repo-and-deployment's Admin settings or the operator
   docs yet.

**Stream b** has **B-30** (editable embedding profiles), **B-31** (answer length from the model) and **B-32** (chat
prompt texts) queued, plus the live items B-L2, B-L3, B-L5. Review notes left for them: B-30 must lock the profile row
against a concurrent first chunk-set build (#547); B-31 should weigh an optional platform-wide answer ceiling now that
the 4,096 cap is gone (#548). **Stream b's machine also runs a main session** that writes design PRs on
`main-session/` branches (#547, #548); #550 told it where decisions go now.

**Only the user can unblock:**
- **Support-assistant proof of concept** (`plans/cases/support-assistant/07-rollout.md` §0b, steps P1–P8). It needs the
  documents (Word, PDF, Markdown: which intranet sections, and whether this machine reaches them) and about twenty
  anonymised tickets. With those it is a session's work on the local stack, and it goes ahead of everything else. It is
  also the first real measurement of upload quality. The local model mirror has no `gpt-6-luna` row: add one or use
  `claude-haiku-5-5` as condense model.
- The live block's inputs: gold set with DoIT, G1/DPIA, test host, U1, pilot accounts on the gateway.
- **The gateway** (`~/git/llmgateway` `origin/main`) still pins Aspire 13.6.4, affected by GHSA-f7vh-h5g3-7jrh; nothing
  filed there.
- **CI runners** (proposal only, user: "write it down, change nothing yet"): `ume-rag` and `ume-rag2` share one Docker
  daemon, so jobs collide. A: one runner per Docker host; B: `release.yml` on GitHub-hosted runners.
- **#511** (split the worker into libraries): only if it keeps growing in P2.

## Done in session 28 (2026-11-11)

- **Security: Aspire 13.6.5 (#549).** GHSA-f7vh-h5g3-7jrh (CVE-2026-120006, high) was published 2026-11-10 23:24 for
  `Aspire.Hosting(.AppHost)` < 13.6.5 and turned every backend job red through the package audit. All `Aspire.*` pins and
  the AppHost SDK went to 13.6.5; `Aspire.Npgsql.EntityFrameworkCore.PostgreSQL` 13.6.5 needs EF Core ≥ 10.0.14, so the
  10.0.13 package family went to 10.0.14 (the runtime base image is unchanged). Local full suite 3212 tests, 1 load flake
  (`SettingsEffectsTests.Lowering_max_depth_or_max_pages_deletes_nothing`, 3/3 green alone). CHANGELOG line: #551 (merged).
- **Reviewed and merged stream b's work:** #542/#543 (Portainer: `dataapi` joins the internal `rag-dataapi` network shared
  only with rag-admin), #544 (neutral wording, password-manager opt-outs, `Admin:OrganisationName`), #545
  (`Crawl:UserAgent`; robots.txt, meta robots and `X-Robots-Tag` match its product token), #546 (deploy templates send
  `dimensions`, `MaxInputTokens` 8191).
- **Design PRs from the other main session merged** (user: use the main session's recommendation): #547 (embedding
  profiles editable until a chunk set uses them; B-30) and #548 (prompt texts as `Settings.Chat.Prompts` with locked
  safety lines; answer length from the model's limits; B-31, B-32).
- **#521, the decisions register, merged** after a rebase. The five decisions added since it was opened are entries
  ("Parser" updated for legacy Office/RTF, "Upload knowledge bases", "Editable embedding profiles before use", "Chat
  prompt texts configurable", "Answer length from the model") with dated log lines. **#550** told stream b.
- **#502 closed** (halfvec answered by to-verify #117). The keys-and-budgets options paper committed.

## Open

- **#513** stays open until its items close; **#499** until M-10; **#503**/**#504** until A-21; **#509** until A-24.
  **#535** (ClamAV `ClamAv:Mode = on | off`, user decision) has no PR yet. **#550** until stream b acknowledges.
- The live block (M-4), in the roadmap's order.

## Local stack (this machine)

- **State at handover:** the dev AppHost is **stopped** and no Ume.Rag containers run (the next `aspire start` starts
  the persistent ones). User secrets are at defaults. The local parser image is older than #526, so it has no
  LibreOffice: **rebuild it before demoing `.doc`/`.xls`/`.ppt`/`.rtf`**. Word and PDF uploads go through the parser
  and were not tried this session; Markdown and text were.
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
    sync is off locally.
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
- The AppHost is on Aspire 13.6.5 since #549; the local stack wasn't started this session, so the CLI path above
  (13.6.1) is untested against it. Install the 13.6.5 CLI if `aspire start` complains.

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
- The Edit tool fails on a file not Read in this session. A commit made in the same step then goes out without the
  change, so check `git show --stat` after a combined edit-and-commit.

## Environment notes

- `rag_key.txt` in the repo root holds the interim-gateway key (mode 600, excluded in `.git/info/exclude`); the
  address and the same key are in the AppHost user secrets (`Gateway:BaseUrl`, `Gateway:Keys:Eu`). Never commit,
  print or move them.
- **Git over HTTPS:** `git -c credential.helper= -c 'credential.helper=!gh auth git-credential' fetch|pull|push …`.
- `172.30.0.0/16` is taken on this machine; test networks use 10.21x.0.0/24.
