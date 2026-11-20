# Main session handbook

How the main session works, the local stack, and the lessons of earlier sessions. Stable: it changes when something is
learned, not every session (the session's state is in [`../../HANDOVER.md`](../../HANDOVER.md), the plan ahead in
[`../roadmap.md`](../roadmap.md)). Moved out of HANDOVER on 2026-11-13 (session 33). Stream agents' gotchas are in
[`../../docs/local-development.md`](../../docs/local-development.md) (*Working notes*) and
[`../../docs/ci-runner.md`](../../docs/ci-runner.md).

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
- **Shared files** (`Directory.Packages.props`, `README.md`, `Kalla.slnx`, `.gitignore`): small PR, append-only.
- **Release notes are files, not edits** (user, 2026-11-17): a pull request adds `changes/<branch>.md` (CHANGELOG) and,
  when an operator has something to do, `changes/<branch>.upgrade.md` (upgrade guide) instead of appending to
  *Unreleased*, where any two pull requests conflicted (#817 and #827). `scripts/unreleased.py` checks and folds them. Secrets never go into the repo, a log or a chat. Verify claims against real sources.
- **No backward compatibility before production:** no migration data steps for old rows (fix development data by
  hand, and put the SQL in the PR), no shims.

## Local stack (this machine)

- **State at handover:** the dev AppHost is **stopped**. No ClamAV container exists any more (the "persistent" one
  was gone before session 31 started; its signatures are still in the volume `kalla-clamav-db`). Migrations through
  `AnswerLengthFromModel` are applied on the dev database; the next start applies the rest (`KeywordStatistics`,
  `UploadScannedAt`, `GatewayKeys`, `GatewayModelLimitsSource`, `DailyUsage`, `ObjectGatewayKeys`,
  `DailyTotalsWithoutCost`). After `UploadScannedAt` every existing upload row has `ScannedAt` null: run the
  upgrade guide's one-line `UPDATE` so scanned files don't read as unscanned. It won't start until the user secrets
  change (*Environment notes*). `Settings.Chat` is `{}`. The local parser image is older than #526 (no LibreOffice):
  **rebuild it before demoing `.doc`/`.xls`/`.ppt`/`.rtf`**.
- **A stack on the FakeGateway starts without touching the user secrets or the dev data** (done in session 31 to check
  #599): `export FakeGateway__Enabled=true Seed__Enabled=false Parser__Mode=off Render__Mode=off
  OpenWebUi__Enabled=false DevelopmentVolumes__Postgres=<a throwaway volume name>` (add `ClamAv__Mode=off` to skip
  ClamAV), then `aspire start …`; afterwards `aspire stop --all` and `docker volume rm` the volume.
- Start: `export Seed__Enabled=false; aspire start --apphost src/Kalla.AppHost/Kalla.AppHost.csproj
  --non-interactive` (Docker up: `docker info`). Stop: **`aspire stop --all --non-interactive`**. Restart one service:
  `aspire resource worker restart --apphost … --non-interactive`.
- **E2E locally without pwsh:** `aspire stop --all`; `docker volume rm kalla-e2e-postgres`; export
  `DevelopmentVolumes__Postgres=kalla-e2e-postgres Seed__Enabled=true FakeGateway__Enabled=true E2E__Enabled=true`;
  `aspire start --apphost src/Kalla.AppHost/Kalla.AppHost.csproj --non-interactive`; `aspire wait admin-ui
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
- **Scripting uploads through the admin API:** upload slots are numbered from 1 (`PUT /api/upload-batches/{id}/files/1`), and the commit takes the slot numbers (`{"files": [1, 2, …]}`); an empty commit answers `upload_batch_too_large` (1 to 100 files). A published assistant needs a description, a contact and a gateway key (V39) whose residencies reach the collection's embedding model too. `aspire stop --all` returns before the persistent containers have stopped: wait a few seconds before `docker volume rm`. `dotnet dev-certs https --export-path x.pem --format Pem --no-password` also writes `x.key` (the dev certificate's private key): delete it afterwards.
- **Admin API scripting:** `~/kalla-tools/` (outside the repo, no secrets in it): `admin.py` (BFF login from the
  AppHost user secret, XSRF; `python3 -I admin.py GET /api/assistants`, or `import admin; admin.call(m, path, body)`;
  raw uploads through `admin.req` with `X-XSRF-TOKEN` and `X-File-Name`), `pf.py '<preflight JSON>'`, `runwait.py
  <sourceId> [start]`, `chat.py <n> <label> <out.json> [gap s]`. Run them from that folder.
- Feedback importer: every minute, user secret `Parameters:openwebui-admin-api-key`. Logins:
  `~/kalla-demo-login.txt` (mode 600; port 7410). Draft gold sets: `~/kalla-gold-draft-*.csv`, `plans/gold-set/`.
- Postgres: `docker exec -e PGPASSWORD="$(docker exec <postgres-…> printenv POSTGRES_PASSWORD)" <postgres-…> psql -h
  localhost -U postgres -d ragdb` (schemas `config`, `content`, `chat`).
- Local images: `kalla/renderer:ci` (also `kalla.local/renderer:ci`; the container test uses
  `KALLA_TEST_RENDERER_IMAGE=kalla.local/renderer:ci`), `kalla.local/clamav:dev`, `kalla.local/parser:ci`
  (pre-#526), `kalla.local/postgres:ci`.
- `~/git/llmgateway`'s working tree is still at v0.9.0; `origin` is fetched through **v0.10.0** (tag). Read the
  gateway with `git show v0.10.0:<path>`, or pull first. Where its docs and its code differ, follow the code.
- Worktrees: none besides the main clone (the agents' worktrees and the `../rag-*` ones were removed).
- The 13.6.1 CLI above starts the 13.6.5 AppHost fine (checked this session).

## Lessons

Development and CI gotchas are in [`docs/local-development.md`](../../docs/local-development.md) (*Working notes*) and
[`docs/ci-runner.md`](../../docs/ci-runner.md) (*Working with the workflows*), where stream agents read them too. What only the
main session needs:

- **Stacked PRs:** merging the base PR with `--delete-branch` *closes* the stacked PR instead of retargeting it (#579,
  again #732 in session 38). **Before every merge, run `gh pr list --base <branch-being-merged>`**; retarget each hit
  first (`gh pr edit <n> --base main`). To recover, push the old base sha back to its branch, reopen, retarget, or open
  a replacement PR from the same branch (#740). Better: branch dependent work from the other branch but target `main`,
  and rebase it after the first one is squash-merged (#712 on #738).
- **After a GitHub outage** runs can sit `queued` with jobs that have a start time but no runner. Cancel and re-run
  them (the user allows cancelling stuck CI).
- **Check a watcher's exit code before merging:** `gh run watch … --exit-status; echo exit=$?` printed `exit=1` and
  I merged anyway (#582 was docs only; the failure was the flaky test of #584). Read the result first.
- An `a/` branch may not touch `plans/**` (`ownership`, main's path): put design lines in a direct `main` commit.
- The auto-mode classifier blocks `commit --amend` + force-push even on your own branch: push a follow-up commit.
- New worker background services open connections on the worker role: count them against data-model §1.1's budget
  (pool 10 + LISTEN + 1); `DispatcherTests.Under_load_…_role_cap` catches an extra session.
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
  - Until the release-speed change (session 39), a dry run with `e2e=true` skipped `build` when e2e failed, so neither
    smoke test ran (0.6.0: the Portainer boot first ran at the tag and failed, #749). Now `build` runs beside verify and
    parser, and e2e after all three.
  - The shared `~/.docker/config.json`'s stale ghcr.io login also denied the e2e stack's Open WebUI pull (#748): every
    workflow that pulls from ghcr.io sets its own `DOCKER_CONFIG`.
  - **Run `e2e.yml` at least once between releases.** It hadn't run from 2026-11-08 to 11-15 and seven UI changes had
    drifted from the pilot spec, which took nine CI rounds to clear; the accessibility spec found real target-size bugs.
  - An inline (`content:`) config can't go into a `read_only` service: copy the file into `kalla-config` from the
    bootstrap instead (as `kalla-proxy`, `kalla-openwebui-setup`).
  - **Test a stack-file or boot-test fix with the boot-only run, not a dry run** (#752, session 39):
    `gh workflow run release.yml --ref <branch> -f portainer_boot=X.Y.Z` boots `<branch>`'s stack against X.Y.Z's
    images on ghcr.io in about 2 minutes (a failed tag run has already pushed its images). 0.6.0's `--wait` fix went
    green this way on the first try, instead of a 30-minute dry run.
  - **The pin pull request that `release.yml` opens starts no checks** (a pull request opened with the workflow's token
    triggers no workflow): `gh pr close <n>` and `gh pr reopen <n>`, wait for `deploy-checks`, merge (#789, 0.7.0).
  - **The boot-only run needs that version's Källa images on ghcr.io.** For a change that needs new images (the
    wizard's stage 2 against 0.6.0's images), only a dry run from the branch shows the boot test (session 44).
  - `docker compose up --wait` fails on a one-shot that exits 0 unless a service waits for it
    (`service_completed_successfully`). The boot test waits only on the long-running services (#751).
- **Playwright sign-in on the admin UI:** click `locator('a,button').filter({ hasText: 'Sign in' }).last()` and wait for
  the Keycloak URL together with the click.
- **Agents as stream-a implementors** work well with `isolation: worktree`: review the diff, commit on an `a/` branch,
  rebase on `origin/main`, open the PR, watch CI, remove the worktree, then merge with `--delete-branch`. Check that a
  short CI job really ran its steps (a 25 s `deploy-checks` did).
- **Gateway docs vs code:** the gateway's `docs/integrations.md` and OpenAPI said 403 where the code answers 401. Check
  behaviour Källa depends on in the code at the tag.
- **Agent worktrees start from local `main`, not from the branch that is checked out.** An agent told "your branch
  contains X" didn't have X (A-36 on B-47 part 1): merge the prerequisite first, or tell the agent to check
  `git log -1` and name the commit it needs. Both agents coped (one applied the missing files unchanged).
- **Bringing an agent's work to a PR:** commit it in the agent's worktree, `git worktree add ../rag-<id> -b <branch>
  origin/main`, `git cherry-pick -n <agent branch>`, resolve, build, re-run the tests there, commit, push. Afterwards
  `git worktree unlock` and `remove --force` the agent's worktree and delete its `worktree-agent-…` branch.
- **`src/admin-ui/src/utils/labels.ts` and `messages.ts` are stream b's paths**, so a stream-a UI item that needs a
  text goes on a `main-session/` branch (A-33, #609), or `ownership` fails.
- **Regenerating the Portainer stack:** `deploy/portainer/build-stack.sh` (the gateway's latest release asset, which
  pins the gateway's images; or a tag, or a local file). Any PR that changes `deploy/postgres/app-roles.sql` or the
  template leaves the stack stale until someone runs it (#605, #608; #611 fixed it). Källa's image pins in the
  template follow each release (#657 set 0.5.0). **The Open WebUI profile (#657) was merged without a boot test**
  (user, 2026-11-13): the first VM deploy is its test; suspect the entrypoint wrapper, the CA bundle and the data
  volume's owner first. **Booted:** the user deployed 0.5.0 with Portainer, `openwebui` and `clamav` on (2026-11-13).
- **Waiting on a PR's checks in a loop:** test for an empty `conclusion`, not for `status != COMPLETED`; right after a
  push the rollup lists checks whose status is already final-looking while the conclusion is still empty.
- **A `cd` into an agent's worktree makes it the session's working directory.** Use `git -C <path>` for other
  worktrees, and start each command with `cd /home/crotalus/research/rag`.
- **`rm` with a shell variable in the path is stopped by a safety check.** Use literal paths, or `git rm` for tracked
  files.
- **A browser check without the password in the transcript:** a Node script under the scratchpad that requires
  `tests/e2e/node_modules/playwright`, reads `Parameters:dev-user-password` from the AppHost user secrets itself and
  writes screenshots (session 32, A-38). Simpler than the Playwright MCP route.
- The Edit tool fails on a file not Read in this session. A commit made in the same step then goes out without the
  change, so check `git show --stat` after a combined edit-and-commit.

- **Design sections are never renumbered:** code and plans cite §-numbers (about 2,800 times in `src/` and
  `tests/`). A section whose content moves keeps its number and heading with a one-line pointer; a new section takes
  the next free number. Cite stable IDs (V39, a rule's name) where one exists.
- **AdminApi.Tests database classes** go into one of `AdminDatabaseGroup1`..`4`, the shortest by measured time
  (#648); the per-project test times are in the `backend` job log's *Test run summary*.
- **UI checks on realistic data** (session 37): copy the dev volume (`kalla-m1-postgres`) to a throwaway one with
  `docker run --rm -v kalla-m1-postgres:/from:ro -v <copy>:/to alpine cp -a /from/. /to/`, start the FakeGateway stack
  on it (*Local stack*), and remove it afterwards. The Playwright MCP browser rejects the dev certificate: drive a Node
  script with `tests/e2e/node_modules/playwright` and `ignoreHTTPSErrors: true`, reading the login password from the
  user secret inside the script, never on a command line.
- **Parallel implementor agents** (session 37): some refuse to commit (their rules say the user commits), so commit
  their work yourself after reading the diff. Four agents testing at once push the load to ~37 and vitest then fails
  at random in untouched specs: rerun alone before calling a failure real. `uv run` in `src/parser` leaves a `.venv`
  that breaks `check-doc-refs.py`: delete it after use.
- **Run `e2e.yml` only with the runners idle** (session 40): a PR's `backend` job on the other runner creates and
  removes Docker networks, and Chromium then loses its page with `net::ERR_NETWORK_CHANGED`. Wait for the PR's checks,
  then dispatch.
- **A workflow step's `if:` without a status function implies `success()`:** a step meant to run after an earlier
  failure needs `!cancelled() &&` (or `always()`), also when it tests another step's `outcome` (#774).
- **An independent review agent for security-critical code** (session 40, S3): give it the code and the threat model,
  not your conclusions; it found a secret-exfiltration path the main session's own reading missed. Check each finding
  in the files before acting.
- **Drive a new page in a real browser before the PR** (session 40): a Node script with `tests/e2e/node_modules/playwright`
  on a throwaway stack found a usability gap that 400 unit tests didn't. For the setup wizard: `Setup__Try=true`
  (HANDOVER has the recipe).
- **Briefing implementors:** say which facts are assumptions. Two of the main session's own brief statements were wrong
  in session 40 ("a new database has no settings row"; the first "declared deployment" rule) and came back as built.

- **A slow test project: compare one process with two before adding collections** (session 41, #775).
  `Kalla.AdminApi.Tests` took the same 4 minutes with four and with eight database collections; a class took 29 s
  alone and 66 s beside three others; two processes side by side took 148 s. The collections of one process wait for
  one another (not CPU, thread pool, `fsync` or GC), so CI runs the project as five processes
  (`scripts/ci/backend-tests.sh`). Measure with the test executable itself: `-xml <file>` gives every test's time,
  `-class`/`-class-` pick the classes; `KALLA_TEST_POSTGRES_TMPFS=1` doesn't start with the `:ci` Postgres image.

- **Shell tests that pass here and fail on the runner** (session 46, twice): `join` and `sort` pair names by the
  locale's collation (one that ignores punctuation took `secrets/.openwebui-jwt-secret` and
  `secrets/openwebui/jwt-secret` for the same file): compare by exact name in `awk`, or set `LC_ALL=C`. And
  `producer | grep -m1` under `pipefail` fails when grep closes the pipe before the producer is done: read the output
  into a variable first.
- **A stack-file refactor is shown by rendering, not by reading** (session 46, #800): render the committed and the
  regenerated `compose.portainer.yaml` with `docker compose config --format json` (the example's variables, every
  profile) and compare the JSON; only the embedded bootstrap script may differ.
- **A second agent's reading finds what the first one built wrong** (session 46): the guides agent, told to verify
  every command against the scripts, reported that #800's `--gateway-ca-dir` mounted the gateway's key directory.
  Ask a documenting agent for "wrong messages and side effects you noticed" in its report.
- **A `cd` into an agent's worktree moves the session there for good** (the harness then refuses a `cd` back): work
  on `main` with `git -C ~/research/rag` and absolute paths, and reuse the worktree for the next branch
  (`git checkout -B <branch> origin/main`).

- **`main` is protected since 2026-11-16** (ruleset "Protect main"): every change is a pull request with one
  approving review; the main session can't approve its own or bypass. Never `gh pr merge --admin`. Design and handover
  changes go on a `main-session/` branch; a design line that belongs to a code change goes into that pull request.
- **SQL in `src/Kalla.Infrastructure/` uses positional parameters** (`$1`), also in new non-EF code: the API's SQL
  contract prepares every string constant there and reads `@name` as a column (session 47; run
  `tests/Kalla.Api.Tests` `SqlSchemaContractTests`, regenerate with `KALLA_UPDATE_CONTRACTS=1`).
- **The runners have no `gh`:** a workflow step downloads a release asset with `scripts/ci/release-asset.sh`.
- **A boot test's fixture is the earlier release's own example file with real-looking values:** 0.5.1's kalla-admin
  refuses a platform-admin group that doesn't start with `rag-` and restarts for ever (session 47).
- **Six pull requests at once fill both runners for an hour** (session 47): open the small ones first, and dispatch a
  dry run only after the pull request's own checks are done.

## Plan files (how they are kept, M-14)

- **The plan ahead is only in `plans/roadmap.md`.** HANDOVER links to it; when a step lands or an input arrives, update
  the roadmap's critical path first.
- **At each release** move the queues' ticked items to `plans/archive/work/to-v<version>.md` (unchanged, links fixed
  for the depth), fold the release notes (`python3 scripts/unreleased.py fold`), and move the CHANGELOG's and upgrade
  guide's *Unreleased* to the version.
- **Designs hold the specification.** History (answered questions, deferred items, milestones) goes to
  `plans/archive/design-history/<area>.md`; a moved section keeps its number and heading with a `Moved to [...]` line,
  which `scripts/check-doc-refs.py` follows. Non-binding papers go to `plans/proposals/` with a status line.
- **`to-verify.md` is sorted by when a check can run** (2026-11-15): the live block under the roadmap step whose input
  it needs, before go-live (no input needed), and P2 and later. When a step's input arrives, its section is that
  step's checklist; a new row goes where it can run.
- **Left for when each area is next worked on:** cite stable IDs (V39, a rule's name, a heading anchor) in new
  citations rather than bare §-numbers; move the *Phases and planning* section of
  `decisions.md` into the roadmap, keeping only binding rules there.

## Environment notes

- `rag_key.txt` in the repo root holds the interim-gateway key (mode 600, excluded in `.git/info/exclude`); the
  address and the same key are in the AppHost user secrets (`Gateway:BaseUrl`, `Gateway:Keys:Eu`). Never commit,
  print or move them.
- **`ClamAv:Enabled` is gone** (#599): the AppHost stops on it. Use `ClamAv:Mode` = `on` | `off`.
- **The AppHost won't start with these user secrets:** since B-37 it refuses a `Gateway:Keys` section ("give each
  service its credential key as `Gateway:Services:<service>:Key`"). The user moves the key, or gives the three
  credential keys once the interim gateway runs v0.10.0. `Parameters:gateway-key-{eu,onprem,external}` and
  `Parameters:data-api-key` are stale too (`docs/local-development.md` lists the current parameters).
- **Git over HTTPS:** `git -c credential.helper= -c 'credential.helper=!gh auth git-credential' fetch|pull|push …`.
- `172.30.0.0/16` is taken on this machine; test networks use 10.21x.0.0/24.
- **`actionlint`** is installed (`/snap/bin/actionlint`): run it on every workflow change. `shellcheck` is not.
- **`grep` here is ugrep**, which reads `$` mid-pattern as an anchor (`grep '${A:-'` doesn't match `${A:-x}`). Use
  `grep -F` for literal `${...}`; the runner has GNU grep.
