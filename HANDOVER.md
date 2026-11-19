# Handover

State of the Ume.Rag work for the next session, rewritten each session (earlier ones: `git log -- HANDOVER.md`).
**Last release `v0.7.0`** (2026-11-16). **`v0.8.0` is prepared and merged (#856, `faa001dd`) but not tagged yet:** the
tag waits for the user's go-ahead (*Start here*). `main` after it has stream b's B-47 and B-48 (#859, #860), which go
into the next release. The user's VM runs 0.5.0. Repo `~/git/ume-rag-platform` (session 48's machine) or
`~/research/rag` (the other one), branch `main`, remote `origin` = https://github.com/thekim1/ume-rag-platform (HTTPS
through `gh auth git-credential`, handbook *Environment notes*).

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

## Start here (session 49)

**`main` is protected** ("Protect main": a pull request with one approving review and the required checks; an author
cannot approve their own, never `gh pr merge --admin`). Everything, `plans/**` and this file included, goes through a
`main-session/` branch; `thekim1` reviews the main session's pull requests. No pull request is open.
`thekim1` merged #858 to #860 with `--admin` on 2026-11-19, unreviewed; its review of #861 says the user allowed it
that for green pull requests the same day. That is not a broken rule, and it does not extend to `ume-nichlas`.

1. **Tag `v0.8.0`, once the user says go** (the tag publishes the release):
   `git tag -a v0.8.0 faa001dd -m "Ume.Rag 0.8.0"` and push the tag. On the merge commit `faa001dd`, **not** `main`'s
   head (B-47 and B-48 are after it and not in its notes). Then the pin pull request that `release.yml` opens: it
   starts no checks, so close and reopen it, get it approved, merge. Then check that the daily Audit turns green: it
   has been red since #819 because it scans the last release's images (0.7.0).
   - The CHANGELOG dates 0.8.0 **2026-11-18** (the upgrade guide's `## 0.8.0` has no date) and the tag will be
     later: change the CHANGELOG's date in the pin pull request. A fix before the tag would move the tag past B-47
     and B-48. The release page is not affected (`release.yml` takes the notes from below the heading); the tagged
     CHANGELOG keeps 11-18.
   - The dry run from the release branch is green with the browser tests:
     [44474768676](https://github.com/thekim1/ume-rag-platform/actions/runs/44474768676). What `faa001dd` changed
     after it is notes and docs only (`thekim1`'s review of #856).
2. **0.5.1's admin image is gone from ghcr.io.** The first dry run
   ([44350629158](https://github.com/thekim1/ume-rag-platform/actions/runs/44350629158)) failed pulling
   `ghcr.io/thekim1/ume-rag/admin:0.5.1@sha256:38c9e6de…` (*not found*); the same digest pulled in #817's run on
   2026-11-17, and no workflow here deletes package versions. #856 moved `release.yml`'s `UPGRADE_FROM` to **0.6.0**
   (with `--expect-fenced ume_rag_api --expect-fenced ume_rag_admin`: from 0.6.0 the upgrade crosses
   `DropMessageParentId`); the guides say 0.8.0 tests the upgrade from 0.6.0 and that the step from 0.5.1 was last
   shown by #817. **Not known:** who deleted it, and whether other 0.5.x images went too. The local `gh` token has no
   `read:packages` (`gh auth refresh -s read:packages`, or ask `thekim1`). A 0.5.x stack that has its images keeps
   working; one that must pull them again (a new or pruned host) can't. The VM runs 0.5.0 with its images local.
3. **Stream b's B-47 and B-48** (#857's F3 and F4, the support chatbot) merged on 2026-11-19 by the other machine,
   after the release preparation; their notes are in `changes/`. The design before them is **#858**: Decisions
   "Escalation hand-off" and "Source dates on citations", chat §9.9, knowledge §10.1 and §10.4, to-verify V38. It
   left three things for the main session (*What the main session can take without input*, 4).

### Waiting on the user

- **The go-ahead for the `v0.8.0` tag** (above).
- **The VM:** runs 0.5.0. With 0.8.0 released it moves in one update by `docs/portainer.md`, *Upgrade* (steps 2 to 4
  are the 0.5.x ones: the keys' residencies, the hand-made Open WebUI client, role groups); step 1 backs up the three
  files (a 0.7.x or earlier stack has no escrow set), step 15 makes the first escrow set. Pulls that say "denied":
  `docker logout ghcr.io`. Portainer's own *Update the stack* has never been run on an upgraded stack.
- **The support-chatbot proposal** (`plans/proposals/support-chatbot-features.md`): F3 and F4 are built (#857); the
  rest is the user's pick (the suggestion was F8 and F10 next).
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

### What the main session can take without input

1. **M-22:** S6 is done (#851); S8 (operator pages without project vocabulary) as each page is touched. S5 and S7 are
   proposals in the gateway's repository (thekim1/llmgateway#108, #109). The queue item can close once the user
   agrees S8 runs as a standing rule.
2. **Stream a's queue** (`plans/work/stream-a.md`): what is left is the live block (A-L1 to A-L10) and A-34
   and A-35, whose code is merged with only their live checks open. Each waits on its input; nothing is open without
   one.
3. **The dated exceptions expire:** the scan's FFmpeg CVEs in the parser's opencv wheel (eleven, until 2026-12-17,
   `scripts/audit/image-scan-exceptions.txt`, #818) and picomatch in the admin UI's build (until 2026-12-18,
   `scripts/audit/npm-production-exceptions.txt`, #853). Before then: a newer wheel or picomatch fix, a new date with
   a reason, or drop the dependency.
4. **#858's open ends** (design, the main session's):
   - `Chat:EscalationTimeout` (2 s, hard; 0 = no summary, chat §9.9) is missing from `repo-and-deployment.md`'s
     chat knob list, where `Chat:FollowUpTimeout` is.
   - `checked` is missing from knowledge §3.2's JSON example.
   - Settle #860's design deviation: for `unavailable`, `contacts` falls back to the platform's links (§9.9), but
     the unavailable text (§10.2, `FixedTexts.Unavailable`) shows only the assistant's. Align §10.2, or keep it.

### Watch

- **#781, the flaky "search unavailable" answer in `ChatApiTests`:** open until 2026-11-30; if it recurs, the job's log
  has *The query embedding of profile … is unavailable (…)*, which names the cause.
- **`IntegrationReadbackTests.A_keys_usage_per_step_…` failed once** on #814 (the fake gateway answered 401,
  `scope_missing`). Seen once; no issue filed.
- **Flaky under load** (session 47): with many pull requests' checks on the two runners, `backend` failed with tests
  the change didn't touch (`ChatApiTests`, `RenderTests.The_real_renderer_…`); run alone they pass.
- **`gh pr edit` fails** on this repository with GitHub's *Projects (classic) is being deprecated* error and changes
  nothing; edit a pull request's body with `gh api -X PATCH repos/thekim1/ume-rag-platform/pulls/<n> -F body=@file`.

## What session 48 did (2026-11-17 to 19)

On `~/git/ume-rag-platform`, as the main session and stream a. All merged after `thekim1`'s review:

- **#847 reviewed** (changes requested; it merged anyway), fixed in **#850**: a continue run reads the earlier run's
  last items in one `DISTINCT ON` pass, and a continue run whose carried seeds all fail again is unreachable.
  **#855** wrote the continue run kind into the design (connectors §2.2, §5.4, §6.5; engine §9.1, §9.2, §10.1;
  data-model `Runs`; admin §4.5).
- **#818 decided:** **#848** gives the image scan dated exceptions (`audit.py grype`), the opencv wheel's FFmpeg CVEs
  until 2026-12-17; #855 added five more.
- **#853:** **#854** gives the admin UI's production npm audit dated exceptions, only for a package the bundle doesn't
  contain (`audit.py npm-production` reads the bundle's packages from a second build's source maps).
- **M-22 S6, one escrow set (#851):** `deploy/escrow-keys.sh` writes, `--verify`s and `--restore`s one set with every
  key a restore needs (the pepper, the blob and chat keys, the backup passphrase, the database roles' passwords, the
  conversation id keys, the CA and the Data Protection certificate), chosen by `--env-file`, `--project` or
  `--secrets-dir` with `--certs-dir`; boot-tested in `compose-bootstrap.sh` and `kubernetes-secrets.sh`
  (repo-and-deployment §7.4). `thekim1` added the role passwords and the conversation id keys.
- **#506 closed** (A-20 done in #814). **Release 0.8.0 prepared (#856)**, dry run green after the move to 0.6.0
  (*Start here*).

## Known gaps, documented, not fixed

- **Not tested anywhere:** Portainer itself (an upgraded stack boots in CI since #817, with Compose; from 0.6.0 since
  0.8.0); both wizards completed against a real provider in one deployed stack (the boot test has no provider and
  stops at each wizard's state); Entra ID and AD FS; a real untrusted TLS certificate; Kubernetes beyond
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
  version, `docs/portainer.md`'s version, the ticked queue items to `plans/archive/work/to-v<version>.md`, the roadmap's
  table; `scripts/unreleased.py fold` moves `changes/` in), a dry run with `e2e=true`, an annotated tag on the merge
  commit (with the user's go-ahead), then the pin pull request that `release.yml` opens: it starts no checks, so close
  and reopen it before merging (handbook, *Release dry runs*). The upgrade boot test starts from `release.yml`'s
  `UPGRADE_FROM` (0.6.0); its images must still be on ghcr.io.
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
  tag at `7a04001a`. **`v0.8.0` prepared at `faa001dd`, not tagged** (*Start here*); its notes are `CHANGELOG.md`'s
  `## 0.8.0` and the upgrade guide's: the deploy simplification (M-22: one service file and one bootstrap for every
  form, upgrades in one step from 0.5.x, one escrow set, Open WebUI off unless asked for), continue a crawl run and
  its live progress, move to another department, the images' security updates and the daily image scan. Migrations
  `DropMessageParentId` (stops the old rag-api and rag-admin) and `ContinueRuns` (additive); *Rolling update: no*.
- **What a deployer types in Portainer:** 1 value since 0.7.0 (`UME_DOMAIN`, no secret).
- **On `main` after `faa001dd`, unreleased:** B-47 (source dates on citations) and B-48 (the escalation hand-off,
  `ume.escalation`), notes in `changes/`.
- **Open issues that matter:** #781 (watch), #667 (M-16, the other machine's), #333 (the exact-scan and recall
  checks on the test host), #511 (splitting the worker), #580 and #550 (decisions to record). No pull request open.
- **Dependabot works:** its first grouped pull requests merged (#841 to #844); `.github/dependabot.yml`'s header lists
  where a pin is repeated.
- **Ume.Rag on this machine (`~/git/ume-rag-platform`):** no extra worktrees; Docker is not in this WSL distro, so
  stack and image tests run only in CI; `shellcheck` and `actionlint` are not installed. On the other machine
  (`~/research/rag`) session 47's agent worktrees and the merged local branches were removed on 2026-11-19; what
  remains is the main clone on `main` and `~/research/rag-small` (the second checkout). Volumes
  `ume-rag-m1-postgres`, `ume-rag-openwebui-data` and `ume-rag-clamav-db` are kept there.
- **The gateway on this machine:** `gh` is signed in as `ume-nichlas` (it can push to both repositories; merges and
  releases in the gateway are the user's). Worktrees `~/git/llmgateway`, `~/git/llmgateway-100`,
  `~/git/llmgateway-100-deploy` and `~/git/llmgateway-101` are on branches whose pull requests are all merged: they can
  go, or `~/git/llmgateway` can move to `main`. Local images `ume-llm-gateway/*:setup-test` predate the release and can
  go. Ports 5000 and 5173 are held by something else: the gateway's IntegrationTests need
  `ASPNETCORE_URLS=http://127.0.0.1:0`. The gateway's own rules: draft pull requests (drafts run no CI), the checks run
  locally first, squash-merge.
- **Tools here:** the Aspire CLI is at `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire`
  (not on PATH). On the other machine `actionlint` is installed (`/snap/bin/actionlint`; it hung once without
  `-shellcheck= -pyflakes=`); `shellcheck` and `pwsh` are not; `grep` is ugrep (handbook, *Environment notes*). The
  local `gh` token can't read ghcr packages: live stack tests run on the runner. Playwright's Chromium and Firefox are
  in `~/.cache/ms-playwright`; Edge needs sudo to install.
