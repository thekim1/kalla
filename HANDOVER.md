# Handover

State of the Ume.Rag work for the next session. Rewritten 2026-11-09, end of session 27 (`main` at `c4cef5d` plus this
file; last release `v0.2.0`). Earlier sessions are in the git history (`git log -- HANDOVER.md`).
Repo `~/research/rag` on this machine (`~/git/ume-rag-platform` on the session-19 machine), branch `main`, remote
`origin` = https://github.com/thekim1/ume-rag-platform (push after every commit).

## What this is

**Ume.Rag** (working name, N1) is a RAG / crawling / chatbot platform for Umeå kommun: small .NET 10 services on top
of the team's **LLM gateway** (https://github.com/thekim1/llmgateway, `~/git/llmgateway`), as an add-on to it (Decisions
"Shared sign-in with the gateway"). The user owns the gateway and wants to do better than **Eneo**, a slow "kitchen
sink" monolith. Design and code live in this repo.

## Where to read

1. `01-general-idea.md` §10 **Decisions table** (rows `current` bind). If **#521** has merged, read
   `plans/decisions.md` (the register) instead; the old table is then `plans/archive/decisions-log.md`.
2. **`plans/roadmap.md`**: what is done, what is left before go-live, the live block, waiting on people, P2/P3. One page.
3. **`plans/work/`**: the queues `stream-a.md`, `stream-b.md` and `main.md`; results in `results-a.md` and
   `results-b.md`.
4. The design for the area at hand (map in `plans/README.md`). History is in `plans/archive/` and does not bind.
5. **`plans/cases/support-assistant/`**: the first case plan (internal support assistant, DoIT).
6. **New this session:** `plans/platform/upload-quality-review.md` (an assessment, not binding: how well uploads are
   handled, gaps U1–U7, a proposed order) and admin design **§5.5.7** (upload knowledge bases).

## Start here

**P1b code is done and released** as `v0.2.0`. What is left of P1b is the **live block (M-4)**, which waits on people
(roadmap "Waiting on people"). Since v0.2.0, `main` has gained the upload work below plus stream b's B-25–B-29, #529,
#530 and #531, so a **v0.3.0** is due before anything is deployed. A release needs the full CI set plus a release dry
run (`docs/ci-runner.md`).

**Waiting on the user:**
- **#521, the decisions register** (`main-session/decisions-register`, worktree `../rag-docs`). It was held for the
  user's review because it rewrites what binds. CI is green, and two reviewers' fixes are in `fd2d9de`. Rebase it on
  `main` before merging: since it was opened, two Decisions rows have been added to `01-general-idea.md` §10 ("Legacy
  Office and RTF uploads", "Upload knowledge bases"), and they must move into the register and the log. It also adds
  M-11 (about 15 design-vs-decision contradictions, not fixed).
- **Whether to queue the upload gaps** from `upload-quality-review.md`. Suggested: picture descriptions (U1, a P2 item
  brought forward, which needs a Decisions row) and rerank on (U4, when the gold set shows a gain), then `.msg`/`.eml`
  (U2), a contextual-header trial (U5) and a near-duplicate warning (U6).
- **LibreOffice placement** is decided for now (stays in the parser) and is decided again **before go-live**, together
  with the parser sandbox question (Decisions "Legacy Office and RTF uploads", roadmap go-live step 7).

**Proposed next**, if the user doesn't choose:
1. **SA-15**: a 2 MB body on the chat route in the generated proxy configuration (`deploy/proxy/nginx.conf.example`,
   the printed routes, `docs/production-setup.md`). The support-assistant case wants it before the pilot.
2. **M-10**: settle #499 step 2 (who keeps scope sizes and the stop list) and #501 in the designs. B-25's measurement is
   in: the api role's 60 s limit is reached near 3.3M chunks, so step 2 is needed before about 2M chunks.
3. **M-9**: write cost-attribution stage 1 into the designs and queue C1–C6.
4. Stream a's cheap items: **A-22** (#500), **A-21** (#503/#504 Compose part), **A-23** (#505), **A-24** (#509
   evaluation part, unblocked by B-28). Also drop the smoke test's snake_case fallback in
   `deploy/tests/release-smoke.sh:149` now that #529 has merged (rag-api's `/version` is camelCase).
5. **v0.3.0** (CHANGELOG, full CI set, dry run, tag).

**Stream b** has nothing left in its queue except the live items (B-L2, B-L3, B-L5). If it asks for work, M-10's
outcome or the upload gaps are candidates.

**Only the user can unblock:**
- **Support-assistant proof of concept** (`plans/cases/support-assistant/07-rollout.md` §0b, steps P1–P8). It needs the
  documents (Word, PDF, Markdown: which intranet sections, and whether this machine reaches them) and about twenty
  anonymised tickets. With those it is a session's work on the local stack, and it goes ahead of everything else. It is
  also the first real measurement of upload quality. The local model mirror has no `gpt-6-luna` row: add one or use
  `claude-haiku-5-5` as condense model.
- The live block's inputs: gold set with DoIT, G1/DPIA, test host, U1, pilot accounts on the gateway.
- **CI runners** (proposal only, user: "write it down, change nothing yet"): `ume-rag` and `ume-rag2` share one Docker
  daemon, so jobs collide. A: one runner per Docker host; B: `release.yml` on GitHub-hosted runners.
- **#511** (split the worker into libraries): only if it keeps growing in P2.

## Done in session 27 (2026-11-09)

- **Stream b's B-29 reviewed** (security: the Open WebUI JWT through `Microsoft.IdentityModel.JsonWebTokens`). Nothing
  wrong: the library's 32-byte minimum key size can't cause an outage, because `OpenWebUiUserJwt.cs:64` already
  requires `OpenWebUi:JwtSecret` ≥ 32 characters. **#512 closed.**
- **Legacy Office and RTF uploads** (user request for the 2026-11-10 demo; stream b's agent wrote both PRs):
  - **#526**: the parser converts `.doc`, `.xls`, `.ppt` and `.rtf` with LibreOffice 25.2 (forced import filter, a
    profile per job, a process-group kill), then reads the OOXML result.
  - **Linked-file reads tested**, with the exact command, offline: a linked Writer section, an RTF `INCLUDETEXT` field
    and a Calc external reference all kept their cached text (Calc shows `Err:540`). So the defaults read no linked
    local file.
  - **#527**: drag-and-drop and folder upload, and the four types allowed in the admin API.
  - **Decision** (user approved stream b's lean): LibreOffice stays in the parser, and parser vs separate converter
    container is decided before go-live (`e2baf57`).
- **Upload knowledge bases** (user: the main session owns this feature, in stream b's paths):
  - **Design `1264ad3`**: admin §5.5.7, the §4.5 `files` contract, Decisions row "Upload knowledge bases". **#528**
    told stream b; closed after the merge.
  - **#532 merged**: *Create knowledge base* → "Files I upload" creates the knowledge base and its upload source in one
    step. A **Files** tab carries the drop zone, live progress (3 s polling while anything is processing), search, state
    chips, sort, folder grouping, *Retry failed* and a details drawer with passages.
  - **Folder paths kept**: `X-File-Name` carries a relative path, which is checked and becomes the display name. A
    path rule `HR/**` (or `/HR/**`) tags a folder.
  - Checked in the browser on the local stack; three problems found there were fixed (visible checkbox labels, raw
    uploader id, "1 kB" for an empty file).
  - Known gaps: no download link in the details (the admin API has no route for an upload's content), and no uploader
    name (only the actor id is stored).
- **#533 / #534**: the worker now records an upload slot's outcome in the run item's commit. It writes `indexed` with
  `DocumentId` when the live set was built from the slot's bytes, else `refused` or `malware` with the reason, guarded by
  `Sha256`, so a replace is never marked by old bytes. The admin API reads the row; connectors §9.1 is updated.
  Checked live. **Development data:** slots uploaded before #534 stay `clean`; the SQL fix is in #534's body. It was
  run on this machine.
- **`plans/platform/upload-quality-review.md`** (user asked for it): text-heavy uploads are handled to best practice
  (structure-aware parsing, heading-path chunks, tables row by row, hybrid search with RRF, built-in evaluation). The
  gaps are pictures (U1), e-mail and archives (U2), spreadsheet calculations (U3), rerank off (U4), thin context
  without headings (U5), near-duplicates (U6) and OCR languages (U7). Upload answer quality is unmeasured until the
  proof of concept.
- Stream b merged #529 (`/version` one shape, #497), #530 (refuse a non-CSS `readySelector` on save, #488 part 1) and
  #531 (Cancel run, #488 part 3); #530's AngleSharp reference was already pinned centrally.

## Open

- **#521** (above). **#513** stays open until its items close; **#499** until M-10; **#503**/**#504** until A-21;
  **#509** until A-24. **#502** is answered (halfvec storage in P2, to-verify #117 found no recall cost) and can be
  closed with an evidence comment.
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
- Worktrees: only `../rag-docs` (#521) besides the main clone.

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
