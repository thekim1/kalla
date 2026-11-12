# Handover

State of the Ume.Rag work for the next session. Rewritten 2026-11-12, end of session 30 (`main` at `beeb86c` plus this
file; **last release `v0.4.0`**, published 2026-11-12 from `714a58f`). Earlier sessions are in the git history (`git log
-- HANDOVER.md`). Repo `~/research/rag` on this machine (`~/git/ume-rag-platform` on the session-19 machine), branch
`main`, remote `origin` = https://github.com/thekim1/ume-rag-platform (push after every commit).

## What this is

**Ume.Rag** (working name, N1) is a RAG / crawling / chatbot platform for Umeå kommun: small .NET 10 services on top
of the team's **LLM gateway** (https://github.com/thekim1/llmgateway, `~/git/llmgateway`), as an add-on to it (Decisions
"Shared sign-in with the gateway"). The user owns the gateway and wants to do better than **Eneo**, a slow "kitchen
sink" monolith. Design and code live in this repo.

## Where to read

1. [`plans/decisions.md`](plans/decisions.md): the decisions in force, one entry per topic, every entry binds. The old
   `01-general-idea.md` §10 table is `plans/archive/decisions-log.md` (verbatim, doesn't bind). Record a decision by
   changing or adding an entry, then a dated line under the log's *Later decisions*.
2. **`plans/roadmap.md`**: done, what is left before go-live, the live block, waiting on people, P2/P3. One page.
3. **`plans/work/`**: the queues `stream-a.md`, `stream-b.md`, `main.md`; results in `results-a.md`/`results-b.md`.
4. The design for the area at hand (map in `plans/README.md`). History is in `plans/archive/` and does not bind.
5. **`plans/cases/support-assistant/`**: the first case plan (internal support assistant, DoIT).
6. Not binding: `plans/platform/upload-quality-review.md` (upload gaps U1–U7, decisions still to take).

## Start here

**Nothing is ready for the main session itself.** Both main items are ticked (M-9, M-12). M-4, the live block, waits
on people, and stream a's items wait on stream b. The next session:

**1. Review stream b's PRs as they come** (stream b merges its own; review the security-relevant parts). The gateway
work runs B-35 → B-36 → B-42:
- **B-35 parts 2–3:** the `GatewayKeys` mirror and the context sync, then the model sync.
  - Part 2 must pin `contracts/gateway/integrations.json` at `v0.10.0`, not `7b678a1`.
  - The sync takes departments, teams and **member groups** from `/v1/ume/context` with `organisation.read`.
  - It removes `PUT /api/organisation/departments/{id}/member-groups` and every Data API call.
- **B-36:** the key picker and V39.
- **B-38:** cost per step, the usage tables, and the FakeGateway sending cost.
- **Then B-39–B-46:** cost report and UI; gateway phases 2–3.

**2. Take stream a's items as they unblock** (implementor agent in a worktree, review the diff, `a/` branch, PR):
- **A-30 part 2** and **A-33** once B-35 part 2 is merged:
  - A-30 part 2 drops `--org-sync-key-file` / `OrgSync__DataApiKey` and the Data API notes.
  - A-33 makes the member groups read-only in the platform UI.
- **A-31** (the worker's cost per knowledge base) once B-38 is merged.
- **A-34** (the worker acts as the knowledge base's key) after B-42, B-36 and A-31.
- **A-35** (the deployment's `Gateway__IntegrationApiUrl` and runbook) after B-45.
- A-32 (cost runbook) after B-40.

**3. Before the next release:** run the release smoke test. A-30 part 1 (#597) changed it to boot with one credential
key per service, and nobody has booted it since. `CHANGELOG.md` is up to date through #597 (#598).

**Waiting on the user:**
- **The interim gateway (J10) on v0.10.0**, with the integration *Ume.Rag* and three credential keys (`rag-api`,
  `rag-admin` with `organisation.read`, `rag-worker` capped at `OnPrem`+`Eu`). Best done around when B-35 part 2
  lands: from then on the organisation sync reads only `/v1/ume/context`, which v0.9.x doesn't have. Every live check
  of B-35–B-46, A-34 and A-35 waits on it. Gateway setup: `docs/production-setup.md` *Gateway credential keys*.
- **Local user secrets** (see *Environment notes*): the AppHost refuses `Gateway:Keys:Eu` since B-37.
- **Upload gaps** from `upload-quality-review.md`. Suggested: picture descriptions (U1, needs a decision entry) and
  rerank on (U4, when the gold set shows a gain), then `.msg`/`.eml` (U2), a contextual-header trial (U5) and a
  near-duplicate warning (U6).
- **Ask-a-file workspaces** (P2): owned by teams and individuals, against "Organisation" (departments own all content).
  Answer before P2 (`plans/work/main.md` M-11).
- **LibreOffice placement**, decided again before go-live with the parser sandbox question (Decisions "Parser").
- **CI runners** (proposal only, user: "write it down, change nothing yet"): `ume-rag` and `ume-rag2` share one Docker
  daemon and one user home. A: one runner per Docker host; B: `release.yml` on GitHub-hosted runners.
- **Support-assistant proof of concept** (`plans/cases/support-assistant/07-rollout.md` §0b, P1–P8): needs the
  documents and about twenty anonymised tickets. It goes ahead of everything else once they exist.
- The live block's inputs: gold set with DoIT, G1/DPIA, test host, U1, pilot accounts on the gateway.
- **#511** (split the worker into libraries): only if it keeps growing in P2.
- From the gateway owner on #580: the gateway's docs say `403` for a revoked or expired assigned key, while the code
  answers `401` (Ume.Rag follows the code). Not built and not asked for: a platform-wide cap on acted requests, the
  Graph group lookup, tenant and guest refusal.

**Stream b** (remote, GitHub only) has its own main session (design PRs on `main-session/` branches, decisions in
`plans/decisions.md`). It works fast, splits items into parts, and also takes stream-a paths on `a/` branches when the
user asks (B-37 part 2, #594). It announces its work on the issue (#580).

## Done in session 30 (2026-11-12)

- **#580, gateway integration:**
  - **Gateway v0.10.0 released** (phases 1–3, the credential residency bound, `organisation.read`, member groups).
  - **User decision:** member groups wait for the gateway rather than come from another source. The gateway built
    them, so they now come from the context (`959c4ae`, `7a15ca3`).
  - **M-12 done** (`beeb86c`): designs checked against the gateway's code at `v0.10.0` (401 for a revoked or expired
    assigned key; echo as a GUID on 2xx/304; 402 scope only in the message; the stream cost comment before `[DONE]`;
    the Integration API's real shape). Phases 2–3 queued as B-42–B-46, A-34, A-35.
  - Decisions "Gateway keys": each service reads `features` from its own context.
- **M-9 done** (`1699b51`): cost attribution stage 1 in the designs.
  - Per-step tables `chat."AssistantDailyUsage"` and `content."CollectionDailyUsage"`, with gateway cost and
    estimates apart; the cost report (admin §8.6).
  - B-38–B-41, A-31, A-32 queued.
  - **User: Q3, no hard budget in the pilot.**
- **Merged:**
  - B-34 #592 (schema contract for stream b's SQL; #510 closed);
  - B-37 #593 (stream b), #594, #596 (one credential key per service; `Gateway:Keys` gone);
  - B-35 part 1 #595 (FakeGateway context and model facts, contract pinned at #84's `7b678a1`);
  - **A-30 part 1 #597** (mine, via an agent): `init-deployment.sh --gateway-credential-key-file SERVICE=FILE`,
    Portainer/Kubernetes/docs, deploy-checks. The Portainer stack was regenerated from the gateway's `main` compose
    file, which credential keys need.
- **Ownership:** `deploy/kubernetes/base/app-roles.sql` belongs to stream b with its source (`.github/stream-ownership.txt`).

## Open

- **#513** stays open until its items close; **#550** until stream b acknowledges; **#580** until phases 1–3 are
  built.
- **#535** (malware scan optional, Decisions "Malware scan optional", M-13) is built except B-47 part 2: B-47 part 1
  (#599, by the main session), A-36 (#602, worker) and A-37 (#601, deployment and docs) are merged. Stream b has the
  admin API's `scannedAt` and the admin UI's notes left. The release smoke test's scan-off phase has not run yet.
  The main session asked stream b on #580 whether B-35 part 2 is started and whether B-38 is free (the user allows
  the main session to take a stream b item to unblock stream a); don't start either before it answers.
- The live block (M-4), in the roadmap's order.

## Local stack (this machine)

- **State at handover:** the dev AppHost is **stopped** and wasn't started in session 30 (only the persistent ClamAV
  container runs). Migrations through `AnswerLengthFromModel` are applied locally; the next start applies the rest.
  It won't start until the gateway user secrets change (*Environment notes*). `Settings.Chat` is `{}`. The local parser
  image is older than #526 (no LibreOffice): **rebuild it before demoing `.doc`/`.xls`/`.ppt`/`.rtf`**.
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
- `~/git/llmgateway`'s working tree is still at v0.9.0; `origin` is fetched through **v0.10.0** (tag). Read the
  gateway with `git show v0.10.0:<path>`, or pull first. Where its docs and its code differ, follow the code.
- Worktrees: none besides the main clone (agent worktrees under `.claude/worktrees/` were removed).
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

- **Stacked PRs:** merging the base PR with `--delete-branch` *closed* the stacked PR (#579) instead of retargeting
  it. Retarget first (`gh pr edit <n> --base main`), then merge the base. To recover, push the old base sha back to its
  branch, reopen, retarget.
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
- **A PR that changes `deploy/postgres/*.sql`** must also regenerate `deploy/portainer/compose.portainer.yaml`
  (`deploy/portainer/build-stack.sh <gateway compose file>`), or the Portainer stack lacks the change (#566 caught
  B-30's grants missing).
- **xunit v3 test output** (`TestOutputHelper.WriteLine`) shows only in a report: `--report-xunit-trx --results-directory
  DIR`, then the TRX's `<Message>` elements.
- **Playwright sign-in on the admin UI:** click `locator('a,button').filter({ hasText: 'Sign in' }).last()` and wait for
  the Keycloak URL together with the click.
- **Agents as stream-a implementors** work well with `isolation: worktree`: review the diff, commit on an `a/` branch,
  rebase on `origin/main`, open the PR, watch CI, remove the worktree, then merge with `--delete-branch`. Check that a
  short CI job really ran its steps (a 25 s `deploy-checks` did).
- **Gateway docs vs code:** the gateway's `docs/integrations.md` and OpenAPI said 403 where the code answers 401. Check
  behaviour Ume.Rag depends on in the code at the tag.
- The Edit tool fails on a file not Read in this session. A commit made in the same step then goes out without the
  change, so check `git show --stat` after a combined edit-and-commit.

## Environment notes

- `rag_key.txt` in the repo root holds the interim-gateway key (mode 600, excluded in `.git/info/exclude`); the
  address and the same key are in the AppHost user secrets (`Gateway:BaseUrl`, `Gateway:Keys:Eu`). Never commit,
  print or move them.
- **The AppHost won't start with these user secrets:** since B-37 it refuses a `Gateway:Keys` section ("give each
  service its credential key as `Gateway:Services:<service>:Key`"). The user moves the key, or gives the three
  credential keys once the interim gateway runs v0.10.0. `Parameters:gateway-key-{eu,onprem,external}` and
  `Parameters:data-api-key` are stale too (`docs/local-development.md` lists the current parameters).
- **Git over HTTPS:** `git -c credential.helper= -c 'credential.helper=!gh auth git-credential' fetch|pull|push …`.
- `172.30.0.0/16` is taken on this machine; test networks use 10.21x.0.0/24.
