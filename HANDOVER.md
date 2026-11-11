# Handover

State of the Ume.Rag work for the next session. Rewritten 2026-11-11, end of session 28 (`main` at `fc859ab` plus this
file; last release `v0.3.0`, **v0.4.0 is next**). Earlier sessions are in the git history (`git log -- HANDOVER.md`).
Repo `~/research/rag` on this machine (`~/git/ume-rag-platform` on the session-19 machine), branch `main`, remote
`origin` = https://github.com/thekim1/ume-rag-platform (push after every commit).

## What this is

**Ume.Rag** (working name, N1) is a RAG / crawling / chatbot platform for Umeå kommun: small .NET 10 services on top
of the team's **LLM gateway** (https://github.com/thekim1/llmgateway, `~/git/llmgateway`), as an add-on to it (Decisions
"Shared sign-in with the gateway"). The user owns the gateway and wants to do better than **Eneo**, a slow "kitchen
sink" monolith. Design and code live in this repo.

## Where to read

1. [`plans/decisions.md`](plans/decisions.md): the decisions in force, one entry per topic, every entry binds (#521,
   merged this session). The old `01-general-idea.md` §10 table is `plans/archive/decisions-log.md` (verbatim, doesn't
   bind). Record a decision by changing or adding an entry, then a dated line under the log's *Later decisions*.
2. **`plans/roadmap.md`**: done, what is left before go-live, the live block, waiting on people, P2/P3. One page.
3. **`plans/work/`**: the queues `stream-a.md`, `stream-b.md`, `main.md`; results in `results-a.md`/`results-b.md`.
4. The design for the area at hand (map in `plans/README.md`). History is in `plans/archive/` and does not bind.
5. **`plans/cases/support-assistant/`**: the first case plan (internal support assistant, DoIT).
6. Not binding, for decisions still to take: `plans/platform/upload-quality-review.md` (upload gaps U1–U7) and
   `plans/platform/assistant-keys-and-budgets-options.md` (keys and budgets per assistant).

## Start here

**1. Finish the v0.4.0 release.** `main`'s Unreleased section is large: B-30 (editable embedding profiles), B-31
(answer length from the model, platform ceiling), B-32 part 1 (prompt texts as settings), A-21–A-24, SA-15, the
organisation name, Aspire 13.6.5. Steps:
- **#572 (mine, open, CI green)**: release build cache per image, one smoke boot covering the renderer on and off, a
  per-job `DOCKER_CONFIG`, no build-record uploads. Held until a release dry run from its branch passes.
  - Dry run 1 (`main`, #43083741760) failed: a stale ghcr login (fixed in #572).
  - Dry run 2 (`a/release-faster`, #43091903032) built every image and **passed the smoke test** (1 min 56 s,
    renderer on then off), then failed on GitHub's artifact outage (fixed: no build-record uploads; the final artifact
    upload needs GitHub up).
  - **Dry run 3**: a session watcher was to dispatch it from `a/release-faster` once GitHub was healthy and the
    runners idle. Check `gh run list --workflow release.yml`; if none newer than #43091903032, run
    `gh workflow run release.yml --ref a/release-faster -f version=0.4.0-dryrun -f e2e=false` (runners idle first).
    In that run the parser and renderer steps should take seconds (cache hits, filled by dry run 2).
- When it passes: merge #572; **tick A-21 and A-23** (their done-when is that smoke test) with a results row; close
  #503, #504, #505 with evidence.
- Then the release PR: `CHANGELOG.md` `## 0.4.0 (date)` from Unreleased; `docs/upgrade-guide.md` 0.4.0 section. That
  section needs **rolling update: yes or no**, and the new migrations are `ProfileIndexReset` (B-30, new functions and
  grants in `app-roles.sql`) and `AnswerLengthFromModel` (B-31: drops `answer.maxOutputTokens`/`contextTokens` from
  every `QuerySteps`), plus A-29's once merged. The Unreleased notes already there (proxy 2 MB, renderer profile, idle
  lanes, telemetry) move into it. **Ask the user before pushing the tag**: it publishes images and the release.

**2. Review stream b's open PRs:**
- **#578, #579** (A-29: `content.KeywordStatistics` and the worker's refresh, from M-10). Stream b took this stream-a
  item. Review the migration, the grants (`ume_rag_api` S only), the `ts_stat` on the maintenance connection, and the
  10 % / 7-day refresh rule (knowledge §6.2). B-33 (rag-api reads it) follows.
- **#577**: the fix for #565 (a used profile's fixed fields read-only, a visible switch label). Check it in the browser.
- **#576**: B-32 part 2, the Prompt texts panel. Check it in the browser.

**3. Then, if the user doesn't choose:** A-25 (write each chunk once, engine §14 rule 2a, #501; give it to an
implementor agent in a worktree and respect the profile guard of processing §11 inside the swap transaction), then
M-9 once the user picks a keys-and-budgets option.

**Waiting on the user:**
- **Tag v0.4.0** (after dry run 3 and the release PR).
- **Upload gaps** from `upload-quality-review.md`. Suggested: picture descriptions (U1, needs a decision entry) and
  rerank on (U4, when the gold set shows a gain), then `.msg`/`.eml` (U2), a contextual-header trial (U5) and a
  near-duplicate warning (U6).
- **Keys and budgets per assistant**: options paper `af7e30f`, not chosen; M-9 depends on it.
- **Ask-a-file workspaces** (P2): owned by teams and individuals, against "Organisation" (departments own all content).
  Answer before P2 (`plans/work/main.md` M-11).
- **LibreOffice placement**, decided again before go-live with the parser sandbox question (Decisions "Parser").
- **CI runners** (proposal only, user: "write it down, change nothing yet"): `ume-rag` and `ume-rag2` share one Docker
  daemon and one user home. A: one runner per Docker host; B: `release.yml` on GitHub-hosted runners. More capacity is
  the remaining lever for the ~8.5-minute verify gate of a release.
- **The gateway** (`~/git/llmgateway` `origin/main`) still pins Aspire 13.6.4 (GHSA-f7vh-h5g3-7jrh; AppHost only, so
  development tooling); nothing filed there.
- **Support-assistant proof of concept** (`plans/cases/support-assistant/07-rollout.md` §0b, P1–P8): needs the
  documents (which intranet sections, Word/PDF/Markdown) and about twenty anonymised tickets. It goes ahead of
  everything else once they exist, and is the first real measurement of upload quality. The local model mirror has no
  `gpt-6-luna` row: add one or use `claude-haiku-5-5` as condense model.
- The live block's inputs: gold set with DoIT, G1/DPIA, test host, U1, pilot accounts on the gateway.
- **#511** (split the worker into libraries): only if it keeps growing in P2.

**Stream b** (remote, GitHub only) has its own main session that writes design PRs on `main-session/` branches
(#547, #548, #568) and records decisions in `plans/decisions.md` (#550 told it how). It works fast: B-30, B-31 and
B-32 part 1 all landed this session, and it now also takes stream-a items (A-29) on `a/` branches. Its queue: B-32
part 2 (#576), B-33 (after A-29), the live items.

## Done in session 28 (2026-11-11)

- **Security: Aspire 13.6.5 (#549)** for GHSA-f7vh-h5g3-7jrh (published 2026-11-10 23:24; it turned every backend job
  red through the package audit). EF Core and the 10.0.x package family to 10.0.14 with it. CHANGELOG #551.
- **Stream b reviewed and merged:** #542/#543 (Portainer Data API network), #544 (neutral wording,
  `Admin:OrganisationName`), #545 (`Crawl:UserAgent`), #546 (deploy templates send `dimensions`).
- **Decisions register #521 merged** (rebased twice; the five newer decisions written as entries); design PRs #547,
  #548, #568 (platform answer ceiling, my suggestion) merged.
- **B-30 (editable embedding profiles)**: reviewed #554/#559 twice. Found the in-use race (admin save vs a worker's
  first chunk set) and then a cache hole (a worker caching old-model vectors after the admin's reset); both fixed by
  stream b with a row lock and `content.rag_lock_profile_if_unchanged` on the set insert and on cache writes. Checked
  #558 in the browser (list, edit drawer, **Test** against the real gateway, 409 on a used profile); filed #565.
- **SA-15 (#552)**: 2 MB request bodies on the api host and Open WebUI.
- **M-11 (`319aef7`)**: 21 design-vs-decision points fixed (an agent, reviewed; admin design now in English).
- **M-10 (`0141a1a`)**: keyword statistics move to the worker (`KeywordStatistics`, knowledge §6.2), scope sizes stay
  in rag-api; sets ≤ 1,000 chunks written in the swap transaction (engine §14 rule 2a). Queued A-29, B-33; A-25
  unblocked. #560 told stream b.
- **A-22 (#553)**: Postgres `jit = off`, `random_page_cost = 1.1`, `effective_io_concurrency = 200`; unchanged plans at
  55,000 chunks (`results-a.md`). #500 closed.
- **A-21 (#555)**: trace sampling knobs, 30 s .NET health probe with a 5 s start interval. Unticked until dry run 3.
- **A-24 (#557)**: the no-decompounder variant runs statement K; identical on 494 rows. #509 closed.
- **A-23 (#563)**: idle job lanes wait for `rag_jobs` or the next due job (`Worker:IdleWaitSeconds` 30; 16 probes a
  minute instead of 240), requeues notify, `rag-renderer` behind the `render` profile (`init-deployment.sh --render`).
  An implementor agent's work, reviewed and re-tested. Designs updated (`1b2f3f0`). Unticked until dry run 3.
- **Designs** (`4e7f42c`): B-30 as built (admin §4.5, processing §11 *Profile guard*), `Admin:OrganisationName`.
- **#566**: `UME_RAG_ORGANISATION_NAME` knob; found and fixed the Portainer stack missing B-30's grants.
- **#567**: the release smoke test covers the renderer profile. **#572** (open, above) makes the release faster.
- **#575** (B-32 part 1) reviewed: approved, with a non-blocking note that invisible Unicode format characters (Cf) pass
  V38. CHANGELOG lines: #556, #570. #502 closed. The keys-and-budgets options paper committed.

## Open

- **#572** (above). **#513** stays open until its items close; **#499** until A-29 and B-33; **#501** until A-25;
  **#503**, **#504**, **#505** until dry run 3; **#535** (ClamAV `ClamAv:Mode = on | off`, user decision) has no PR
  yet; **#550**, **#560** until stream b acknowledges; **#565** until #577.
- The live block (M-4), in the roadmap's order.

## Local stack (this machine)

- **State at handover:** the dev AppHost is **stopped** (only the persistent ClamAV container runs; the next
  `aspire start` starts the rest). It ran this session on `main` with B-30 for the browser check; the migrations through
  `AnswerLengthFromModel` are applied locally. User secrets are at defaults. The local parser image is older than #526
  (no LibreOffice): **rebuild it before demoing `.doc`/`.xls`/`.ppt`/`.rtf`**.
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
    sync is off locally, so the embedding profile's model shows "Not among the gateway's models" (expected).
  - **Embedding profile:** `pilot-embedding`, in use, unchanged (a **Test** call was made, no save).
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
- The 13.6.1 CLI above starts the 13.6.5 AppHost fine (checked this session).

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
- **Release dry runs:**
  - The runners share one user's `~/.docker/config.json`; a tag release's ghcr login used to stay there and, once
    expired, broke a later dry run's pulls ("failed to fetch oauth token: denied"). #572 gives the build job its own
    `DOCKER_CONFIG`. The stale entry is still in that home: other workflows that pull from ghcr may hit it.
  - When a run fails at an artifact upload with `Unexpected token '<'`, check https://www.githubstatus.com first.
  - The smoke test can't re-run `init-deployment.sh` on the runner (not root; the files belong to the containers'
    users after the first run); verify.yml's deploy-checks test the re-run.
  - Measure before optimising: v0.3.0's smoke test took 1 min 51 s; the time was in image builds and pushes.
- **A PR that changes `deploy/postgres/*.sql`** must also regenerate `deploy/portainer/compose.portainer.yaml`
  (`deploy/portainer/build-stack.sh <gateway compose file>`), or the Portainer stack lacks the change (#566 caught
  B-30's grants missing).
- **xunit v3 test output** (`TestOutputHelper.WriteLine`) shows only in a report: `--report-xunit-trx --results-directory
  DIR`, then the TRX's `<Message>` elements.
- **Playwright sign-in on the admin UI:** click `locator('a,button').filter({ hasText: 'Sign in' }).last()` and wait for
  the Keycloak URL together with the click.
- The Edit tool fails on a file not Read in this session. A commit made in the same step then goes out without the
  change, so check `git show --stat` after a combined edit-and-commit.

## Environment notes

- `rag_key.txt` in the repo root holds the interim-gateway key (mode 600, excluded in `.git/info/exclude`); the
  address and the same key are in the AppHost user secrets (`Gateway:BaseUrl`, `Gateway:Keys:Eu`). Never commit,
  print or move them.
- **Git over HTTPS:** `git -c credential.helper= -c 'credential.helper=!gh auth git-credential' fetch|pull|push …`.
- `172.30.0.0/16` is taken on this machine; test networks use 10.21x.0.0/24.
