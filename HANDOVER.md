# Handover

State of the Ume.Rag work for the next session, rewritten each session (earlier ones: `git log -- HANDOVER.md`).
**Last release `v0.7.0`** (2026-11-16, the setup wizard; on the gateway's `v0.11.0`). `main` is 0.7.0 plus what
sessions 44 to 46 merged after it (*Unreleased* in `CHANGELOG.md`), the deploy simplification among it; session 47's
work is in open pull requests (*Start here*). The user's VM runs 0.5.0. Repo `~/research/rag` on this
machine (`~/git/ume-rag-platform` on the session-19 machine), branch `main`, remote `origin` =
https://github.com/thekim1/ume-rag-platform (push after every commit; HTTPS through `gh auth git-credential`, handbook
*Environment notes*).

## What this is

**Ume.Rag** (working name, N1) is a RAG / crawling / chatbot platform for Umeå kommun: small .NET 10 services on top
of the team's **LLM gateway** (https://github.com/thekim1/llmgateway, `~/git/llmgateway`), as an add-on to it (Decisions
"Shared sign-in with the gateway"). The user owns the gateway and wants to do better than **Eneo**, a slow "kitchen
sink" monolith. Design and code live in this repo.

## Where to read

1. **[`plans/roadmap.md`](plans/roadmap.md): the plan ahead.** Done by release, the critical path to go-live with each
   step's input and owner, work that needs no input, and the open calls. HANDOVER doesn't repeat it.
2. [`plans/decisions.md`](plans/decisions.md): the decisions in force; every entry binds. Record a decision by changing
   or adding an entry, then a dated line under *Later decisions* in `plans/archive/decisions-log.md`.
3. **[`plans/work/main-handbook.md`](plans/work/main-handbook.md): how we work (the user's preferences, binding), the
   local stack, lessons, environment notes.** Read it at the start of a session; add a lesson there when you learn one.
4. `plans/work/`: the queues `stream-a.md`, `stream-b.md`, `main.md` (open items only; finished ones are in
   `plans/archive/work/`), results in `results-a.md`, `results-b.md` and `results-main.md`.
5. The design for the area at hand (map in `plans/README.md`). History is in `plans/archive/` and doesn't bind.

## Start here (session 48)

**Everything session 47 did waits in pull requests.** `main` got a ruleset on 2026-11-16 23:29 CET ("Protect main":
a pull request with one approving review and the required checks; `ume-nichlas` cannot bypass it, and an author cannot
approve their own pull request), so the main session no longer commits to `main` or merges by itself. `plans/**` and
this file go through a `main-session/` branch too. First look at which of these are approved or merged, and in this
order:

| PR | What | State when the session ended |
|---|---|---|
| #813 | `deploy/keycloak/README.md`: the standalone import over HTTPS on 8443 (closes #797) | green |
| #814 | A-20: GitHub Actions pinned by commit SHA, checked in `doc-refs` (closes #506) | one unrelated test failed once (`IntegrationReadbackTests`, 401 from the fake gateway), re-run started |
| #819 | A-27: the image CVE scan in `audit.yml`, `dependabot.yml` (closes #507). **Stacked on #814** | merge after #814 |
| #815 | `LICENSES.md`: the LibreOffice section, the wider written offer (closes #793) | `parser-image` runs the staleness test in the image |
| #816 | The admin UI without Open WebUI (closes #807, M-22 S9's last part) | checks running |
| #817 | **M-22 S4b and S4c: upgrades in one step** (below) | dry run green; `backend` re-run started (flaky under load) |
| #820 | `chat."Messages"."ParentMessageId"` dropped (closes #792). **Stacked on #817** | merge after #817 |
| this one | The handover, connectors design §5.2's sentence for #802 | |

After a stacked pull request's base is squash-merged, merge `origin/main` into the stacked branch (no force push) and
check its diff is only its own.

### Upgrades in one step (#817; Decisions "Upgrades in one step")

- **Service fences (data-model §10):** a migration the previous version of a service can't run beside carries
  `[StopsServices(ServiceRoles.…)]`. On an upgrade `rag-migrate` revokes that role's schema `USAGE` (recorded in
  `public.ume_rag_service_fences`) and logs *Migration … stops the services of role …*; rag-api, rag-admin and
  rag-worker call `public.ume_rag_admit(<last migration compiled in>)` as their first hosted service and get the
  schemas back, an older build is refused (`UR001`) and does not start. Complete on one slot (Compose, Portainer);
  with two slots or on Kubernetes the old instances are still stopped first (the grant is per role).
- **The upgrade boot test:** `deploy/tests/portainer-upgrade-boot.sh` boots an earlier release's stack asset with its
  variables (`deploy/tests/fixtures/stack-<version>.env`) and applies the new file over it. `release.yml` runs it from
  `UPGRADE_FROM` (0.5.1; 0.5.0's asset does not pin the gateway) with `--expect-fenced ume_rag_admin`. Alone, in
  minutes: `gh workflow run release.yml --ref <branch> -f portainer_boot=0.7.0 -f portainer_upgrade_from=0.5.1`.
- **Shown:** [run 43974596778](https://github.com/thekim1/ume-rag-platform/actions/runs/43974596778): 0.5.1's stack,
  then the branch's file with 0.7.0's images: every service healthy, the schema from 33 to 41 migrations, the
  embedding profile, the CA, the pepper and the Data Protection certificate kept, the gateway from 0.10.1 to 0.11.0 in
  the same step. **The first boot of an upgraded stack anywhere.** That run had no fence (0.7.0's images).
- **The fence on real containers:** release dry run
  [43984531659](https://github.com/thekim1/ume-rag-platform/actions/runs/43984531659) from #817's branch is green:
  0.5.1's stack, then the branch's file with the branch's images; `rag-migrate` stopped `ume_rag_admin` itself, the
  new rag-admin was admitted and every service is healthy. (The first dry run failed on the API's SQL contract, which
  wants positional parameters, and on a missing ghcr.io login in a dry run; both fixed.)
- **Flaky under load:** with six pull requests' checks on the two runners, `backend` failed on #817 and #819 with
  tests neither touches (`ChatApiTests.Every_call_of_a_turn_counts_…`, `ChatApiTests.A_failed_answer_call_still_writes_…`,
  `RenderTests.The_real_renderer_reports_a_refused_ready_selector_…`); the test projects took twice their usual time.
  Re-runs were started; check them.
- **Guides:** `docs/portainer.md` has one *Upgrade* section (from 0.5.x or later); the joined stack defaults
  `UME_INTEGRATION_API_URL`. **The VM** (below) can go from 0.5.0 to the next release in one update once it exists.

### M-22: what is left

S6 (one command that writes one escrow set, the same in both forms), then S8 (operator pages without project
vocabulary, as each is touched). S5 and S7 are proposals in the gateway's repository (thekim1/llmgateway#108, #109).

**Not run anywhere** (unchanged from session 46): the hardened overlay on a host and moving a running stack to it;
`compose-rolling-e2e.sh`; a boot without the `openwebui` profile after #806, and adding the profile to a running
stack; root's `chown` on an NFS backup share; the upgrade guide's carry-over of an old Compose installation;
`create-secrets.sh`'s upload to a cluster. **Stated in the guides, not verified:** `up -d` runs the exited
`rag-bootstrap` again, `docker compose create` makes the volumes before a first `up`, Docker creates a missing backup
directory at the first `up`. The design passages session 46 left as they are (`repo-and-deployment.md` §6.3, §5.4,
§6.1, §6.4, §7.3's `--role`; history rows §11 row 14, §12 rows 8, 11 and 15, §14 row 2) are still as they are.

### What the main session can take without input

1. **#818, the first image scan's findings** (stream a's paths): the .NET base image 10.0.13 → 10.0.15 in
   `Directory.Build.targets` (the gateway's pin first or together), newer digests for `pgvector/pgvector`,
   `python:3.12-slim-trixie` (3.12.16), ClamAV and Playwright. With #819 merged the scheduled Audit is red until then.
2. **Dependabot's first pull requests** after #819: check that it can read the manifests at all (central package
   management, `.npmrc` with `min-release-age`, digests with tags). A bump also moves the pin where it is repeated
   (`.github/dependabot.yml`'s header lists the places).
3. **#821** (the other machine's, for #811: platform admins move a knowledge base or an assistant to another
   department). Reviewed, not approved: a move shares a knowledge base with a department whose link did not work
   before (the review's finding 1), and four smaller points. Read their answer and approve when finding 1 is fixed.
4. **M-22 S6.**

Before a stream's paths: check for open `a/` or `b/` work and say so on the issue (handbook).

### Waiting on the user

- **Approvals** of the pull requests above (or a change to the ruleset, if the main session should merge again).
- **The VM:** runs 0.5.0. With #817 released it goes to the new release in one update by `docs/portainer.md`,
  *Upgrade* (steps 2 to 4 are the 0.5.x ones: the keys' residencies, the hand-made Open WebUI client, role groups).
  Until then the two-step path of `v0.7.0`'s guide holds (0.6.0's file first). Pulls that say "denied":
  `docker logout ghcr.io`. Portainer's own *Update the stack* has still never been run on an upgraded stack.
- **The support-chatbot proposal** (`plans/proposals/support-chatbot-features.md`): the user picks; the suggestion is
  F3, F4, F8 and F10.
- **The roadmap's T0 row** says *open* though T1, which depends on it, is deployed: the user knows its state.
- **To-verify row 96:** the NVDA screen-reader pass of the admin UI, which needs Windows.
- **To-verify row 110, a question to KB** (the user's call; the main session would send it before go-live): *"We
  intend to use KBLab/bert-base-swedish-cased-ner (revision f3c83af8) in a municipal production service and possibly
  redistribute a container image containing the weights. Can you confirm that the MIT licence on the model card covers
  the weights without restriction from the SUC 3.0 agreement, and which variant of SUC 3.0 (licensed or
  scrambled/SUCX) it was trained on? Which copyright line should the MIT notice carry?"*
- **To-verify row 79:** whether `NerMinScore` moves for the int8 NER model (left at 0.80: the sample is small).
- **U1 (pictures in uploads)** waits for DoIT's files at T6 (decided 2026-11-15).
- **The Eneo report PDF** is only at `/mnt/c/temp/eneo-vs-ume-rag.pdf`; the user hasn't said whether to keep it in the
  repo.

### Watch

- **#781, the flaky "search unavailable" answer in `ChatApiTests`:** open until 2026-11-30; if it recurs, the job's log
  has *The query embedding of profile … is unavailable (…)*, which names the cause.
- **`IntegrationReadbackTests.A_keys_usage_per_step_…` failed once** on #814 (no rows; the log has the fake gateway
  answering 401 and `scope_missing`), in a pull request that changes no code. Seen once; no issue filed.
- **Before the next release: a full dry run with `e2e=true`.** The browser tests have not run since 0.7.0's dry run;
  #816 changes one line of `pilot.spec.ts` (*Model name:*) that no run has shown.
- **#805:** `Worker.Tests.HealthEndpointTests` failed once in its teardown; stream a's path. Re-run the job if it
  shows again.
- **From the other machine, merged during session 47:** #802 (globs with a leading scheme) and #812 (a running run
  shows live counters). #801, #803 and #804 are their crawl feature issues.

## What session 47 did (2026-11-16 to 17)

- The user: "do every point one by one, keep track on GitHub, work autonomously, take recommended decisions". The four
  points were M-22's S4b and S4c with #807; A-20 and A-27; #792, #793 and #797.
- **How:** the fence (S4b), #807 and #793 were built by implementor agents in their own worktrees from written
  briefs; the main session read each diff, ran the tests again, drove #807's pages in a browser on a throwaway stack
  without Open WebUI, and wrote S4c's boot test, the workflows, the guides and the design itself. Each agent corrected
  statements of the brief (no Open WebUI section exists on the Settings page; `licences.py` reads installed
  distributions, not `uv.lock`; two COIN-OR packages are not EPL-2.0).
- **Decided by the main session, as the user asked:** Decisions "Upgrades in one step" (the fence rather than a
  documented stop or a script; skipping releases; the stack's default for `UME_INTEGRATION_API_URL`); #792: the column
  goes.
- **The upgrade boot test took five runs** to pass, each failing on the test's own setup: no `gh` on the runner
  (`scripts/ci/release-asset.sh` downloads with curl), 0.5.0's asset without gateway pins, a fixture group name that
  0.5.1 refuses (it must start with `rag-`), and `sed` reading the wrong half of a JSON log line.
- **Session 46** (the deploy simplification): `git show 2acdee77:HANDOVER.md`.

## Known gaps, documented, not fixed

- **Not tested anywhere:** Portainer itself (an upgraded stack boots in CI since #817, with Compose); both wizards completed against a real provider in one deployed stack (the boot test has no
  provider and stops at each wizard's state); Entra ID and AD FS; a real untrusted TLS certificate; Kubernetes beyond
  rendering.
- **The manifest default needs Compose 2.24.7 or later:** an older one cuts a default that contains `}` at the first
  one, without an error, also when the variable is set. Portainer pins a later one from 2.21 (read, not run).
- **A stack without `UME_INTEGRATION_MANIFEST`** (0.5.x style, keys issued by hand) gets the default from 0.7.0: three
  unused credential keys unless the variable is set empty first (`docs/portainer.md`, *Upgrade from 0.6.0*).
- **The boot test shows no Open WebUI with OIDC** (no provider anywhere, so it starts without sign-in); `e2e.yml`
  shows it (accepted by the user).
- **A hardened Compose host's firewall** lets rag-admin reach only the provider of `--oidc-authority`; without it
  the wizard's test can't reach a provider there. `init-deployment.sh --hardened` asks for it in an interactive run,
  and its closing lines say when it is missing. A host without the hardening has no such limit.
- **The Compose quick start (`docs/compose-quick-start.md`) is read, not run** end to end: the gateway's manifest path
  is exercised only in the Portainer boot test, and the same-host flow (the gateway on `--bind 0.0.0.0`, Ume.Rag
  reading its keys and CA from the gateway's directory) nowhere. The release smoke test boots `compose.rag.yaml` with
  the backup overlay against the fake gateway.
- **The gateway's Data API does not follow its wizard:** a gateway whose sign-in is set there sets
  `UME_DATA_AUTHORITY` for access tokens. On Kubernetes a gateway that declares `Oidc__Authority` must set
  `Gateway__OidcAuthority` too (documented).
- `GET /internal/openwebui/sign-in` is closed at the Portainer proxy; in Compose only the managed client's key opens
  it.
- **Open WebUI v0.12.5, upstream** (to-verify 47, 165): text-fragment links lose å, ä and ö, so Swedish highlights
  don't match; a PDF snippet link gets two `#`; a workspace override of an assistant replaces its description.
- **`tests/e2e/specs/openwebui.spec.ts` is a by-hand spec** (project `openwebui`; the stack needs
  `FakeGateway__Latency__TokenIntervalMs=80` and `FakeGateway__Latency__AnswerWords=150` for its Stop check): not in
  `e2e.yml`.
- **The crawl setting `IncrementalSchedule`** is checked only for five tokens (nothing reads it yet); sources'
  schedules and `Settings.DefaultSchedules` are parsed properly since #791.
- **CI:** what `AdminApi.Tests`' collections wait for inside one process is not found; attaching a profiler
  (`dotnet-stack`) was stopped by the permission check. The user can allow it, or run it by hand.
- **A flaky gateway test:** `ClientAddressTests.Forwarded_address_is_used_only_through_a_trusted_proxy` failed in 2 of
  8 local runs of the gateway's `Gateway.Tests`. If a gateway CI run fails on it, re-run the job.

## How to

- **A release:** the preparation as #788 did it (*Unreleased* in `CHANGELOG.md` and `docs/upgrade-guide.md` under the
  version, `docs/portainer.md`'s version, the ticked queue items to `plans/archive/work/to-v<version>.md`, the
  roadmap's table), an annotated tag on the merge commit, then the pin pull request that `release.yml` opens: it starts
  no checks, so close and reopen it before merging (handbook, *Release dry runs*).
- **Show a deploy change before a release:** a dry run from the branch (`gh workflow run release.yml --ref <branch>
  -f version=X.Y.Z-dryrun -f e2e=true`, about 30 minutes, with the runners idle). The boot-only run
  (`-f portainer_boot=X.Y.Z`) takes minutes but uses that version's images already on ghcr.io, so it only fits a change
  that needs no new image.
- **Rebuild the Portainer stack:** `sh deploy/portainer/build-stack.sh v0.11.0` (or the gateway's next release);
  `--check` is what `deploy-checks` runs.
- **Try Ume.Rag's wizard locally** (a throwaway volume each time): `export FakeGateway__Enabled=true
  Seed__Enabled=false Parser__Mode=off Render__Mode=off OpenWebUi__Enabled=false ClamAv__Mode=off
  DevelopmentVolumes__Postgres=<a throwaway name> Setup__Try=true`, `aspire start …`, `aspire wait admin-ui`; the code
  is in `aspire logs adminapi | grep 'Setup code:'`; the provider is `https://localhost:7411/realms/ume`, the clients
  `ume-rag-admin` and `open-webui-dev` (secrets: the AppHost parameters `oidc-client-secret`,
  `openwebui-client-secret`), the user `rag-platform-admin`. Afterwards `aspire stop --all`, wait, `docker volume rm`.
  Or run the spec: `tests/e2e/README.md`. On a FakeGateway stack the sign-in step shows the note that the gateway's
  sign-in is not set up yet (the fake announces the feature with no address).
- **Try the gateway's wizard locally:** in a gateway worktree, `Setup__Try=true Seed__Enabled=false
  DataApi__Enabled=false DevelopmentVolumes__Postgres=<a throwaway name>`, `aspire start --apphost
  src/Ume.LlmGateway.AppHost/Ume.LlmGateway.AppHost.csproj`, then `npx playwright test specs/setup.spec.ts` in
  `tests/e2e` (the gateway's `docs/local-development.md`, *Trying setup mode*). Port 5173 is taken here: set
  `ADMIN_UI_URL` to the port `aspire logs admin-ui` names and add that address's `/signin-oidc` to the `ume-admin`
  client in the bundled Keycloak.

## State

- **Release:** `v0.7.0` published 2026-11-16 ([run 43865015500](https://github.com/thekim1/ume-rag-platform/actions/runs/43865015500)),
  tag at `7a04001a`. `deploy/portainer/` on `main` pins its images (#789) and is the release asset's content.
- **What a deployer types in Portainer:** 9 values at 0.6.0 (two secrets) → 1 at 0.7.0 (`UME_DOMAIN`, no secret).
- **On `main`, unreleased:** #790, #791, #794 and the guides' check (the warning for a failed query embedding;
  schedules validated with the scheduler's parser; chat's 413 as `request_too_large`; `restore-pitr.sh` stops the second
  slots), and **M-22** (#798 to #800, #806, #808 to #810): one service file and one bootstrap for every form, the
  short `init-deployment.sh`, Open WebUI off unless asked for, hardening as an optional step. No migration. Portainer and Kubernetes upgrade as before; a
  Compose installation made with the old files is installed again (`docs/upgrade-guide.md`, *Unreleased*). **The next
  release needs a full dry run with `e2e=true`:** the Compose files are new and the browser tests have not run since
  0.7.0's dry run.
- **New in 0.7.0:** migrations `IdentityOidcSettings`, `SetupMode`, `OpenWebUiSignInSettings` (additive); security
  events 6008–6013 and 5001 `callback_failed`; `GET /api/settings/sign-in`, `GET/PUT /api/settings/openwebui`,
  `/bff/setup/*`, rag-api's `GET /internal/openwebui/sign-in`; `/bff/user` gains `signIn.configured` and `setup.open`;
  `GET /api/overview` gains `secretExpiry` and `deploymentNotices`; `UME_DOMAIN`, `UME_RAG_SETUP_REOPEN`,
  `Setup__Secret`; Open WebUI starts through `deploy/openwebui/start-with-sign-in.py`.
- **Open issues that matter:** #781 (watch), #805 (a flaky worker test teardown), #811 (move to another department,
  built in #821), #818 (the image scan's findings), #667 (M-16; C13 is done with #800), #333 (the exact-scan and
  recall checks on the test host); #506, #507, #792, #793, #797 and #807 close with their pull requests.
- **Ume.Rag on this machine:** the local stack is stopped. Worktrees: `~/research/rag-small` (the main session's
  second checkout, on whatever branch was last worked on) and, under `.claude/worktrees/`, `agent-a17f…` (the fence,
  cherry-picked into #817), `agent-a8d4…` (#815's branch), `agent-ab00…` (#816's branch) and session 46's two
  (`agent-a959…`, `agent-ad14…`, merged). Remove each once its pull request is merged (`git worktree remove`, then
  the local branch). The main clone is on `main-session/m22-upgrade-one-step`. Volumes `ume-rag-m1-postgres` (the dev
  data), `ume-rag-openwebui-data` and `ume-rag-clamav-db` are kept. Session 44's scratch directory holds about 1 GB
  and can go.
- **The gateway on this machine:** `gh` is signed in as `ume-nichlas` (it can push to both repositories; merges and
  releases in the gateway are the user's). Worktrees `~/git/llmgateway`, `~/git/llmgateway-100`,
  `~/git/llmgateway-100-deploy` and `~/git/llmgateway-101` are on branches whose pull requests are all merged: they can
  go, or `~/git/llmgateway` can move to `main`. Local images `ume-llm-gateway/*:setup-test` predate the release and can
  go. Ports 5000 and 5173 are held by something else: the gateway's IntegrationTests need
  `ASPNETCORE_URLS=http://127.0.0.1:0`. The gateway's own rules: draft pull requests (drafts run no CI), the checks run
  locally first, squash-merge.
- **Tools here:** the Aspire CLI is at `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire`
  (not on PATH). `actionlint` is installed (`/snap/bin/actionlint`; it hung once without `-shellcheck= -pyflakes=`);
  `shellcheck` and `pwsh` are not; `grep` is ugrep (handbook, *Environment notes*). The local `gh` token can't read
  ghcr packages: live stack tests run on the runner. Playwright's Chromium and Firefox are in `~/.cache/ms-playwright`;
  Edge needs sudo to install.
