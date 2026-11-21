# Handover

State of the Källa work for the next session, rewritten each session (earlier ones: `git log -- HANDOVER.md`).
**Last release `v0.8.0`** (2026-11-19, as Ume.Rag, on the gateway's `v0.11.0`). **`v0.9.0` is one step away:** its
content is on `main`, its release preparation is `thekim1`'s pull request **#906**, and a dry run of it was going when
session 51 ended (*Start here* 1). It is the first release under the names Källa and Fors, on Fors `v0.12.1`. The
user's VM runs 0.5.0. Two machines work on it: `~/research/rag` (the user's, where sessions 49 to 51 ran) and
`thekim1`'s (stream b, often stream a, and this release's preparation). Branch `main`, remote `origin` =
https://github.com/thekim1/kalla (HTTPS through `gh auth git-credential`, handbook *Environment notes*).

## What this is

**Källa** (`Kalla` in identifiers; Decisions "Product names", which closed N1) is a RAG / crawling / chatbot platform
for Umeå kommun: small .NET 10 services on top of **Fors**, the team's LLM gateway (https://github.com/thekim1/fors,
the local clone still `~/git/llmgateway`), as an add-on to it (Decisions
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

## Start here (session 52)

**The rules of the repository:**
- **`main` is protected** ("Protect main": a pull request with one approving review and the required checks; an
  author cannot approve their own, never `gh pr merge --admin` as `ume-nichlas`). Everything, `plans/**` and this
  file included, goes through a `main-session/` branch. `thekim1` merges green pull requests with `--admin` (allowed
  by the user on 2026-11-19), the main session's too: #905 and #907 were merged that way within the half hour.
- **Release tags are protected** (`refs/tags/v*`): `ume-nichlas` cannot push one (GH013), no workflow creates one.
  The `v0.9.0` tag is `thekim1`'s to push, on #906's merge commit.
- **The session's GitHub account is `ume-nichlas`** (user, 2026-11-20). `gh` here also holds `Crotalus` (the user's
  own), and the active account has switched to `Crotalus` by itself before. **Run `gh auth status` before every push
  or post**, and `gh auth switch -u ume-nichlas` when it shows `Crotalus`.
- **Approvals on this machine:** `gh pr review --approve` as `ume-nichlas` is stopped by Claude Code's permission
  check: review as a comment. On the user's explicit word for a named pull request the session approves **as
  `Crotalus`** (`gh auth switch -u Crotalus` in a command of its own, approve, switch back at once). **Read the pull
  request's `state` first:** the approvals of #905 and #907 landed twenty minutes after `thekim1` had merged them.
- **Look at the open pull requests before each step** (`gh pr list`): the other machine works at the same hour and
  opened the release preparation while this one was doing the items before it. Say on its pull request what you
  are about to write.
- **A decision for the user is asked as four alternatives with one recommendation** (the question tool).

**Open when the session ended:** #906 (the release preparation, `thekim1`'s) and this file's pull request, #904.
In order:

1. **Release `v0.9.0`: finish #906.** The user decided on 2026-11-21, for #906: *when its checks and a dry run are
   green, the main session approves it as `Crotalus`, merges it, and the go-ahead for the tag is given with that;
   `thekim1` pushes the tag.* Where it stood at 2026-11-21 00:35 UTC:
   - #906's head is `5ffe3506`. The dry run on `24078efe`,
     [44839739980](https://github.com/thekim1/kalla/actions/runs/44839739980), was **green in everything but the
     browser tests**: *Smoke test the bundle*, *Boot the Portainer stack*, **the upgrade from 0.6.0's stack with the
     roles renamed first** (which failed in the run before,
     [44835972377](https://github.com/thekim1/kalla/actions/runs/44835972377)), `verify` and the parser. In `e2e` the
     pilot and accessibility suites passed and the setup wizard's stopped at `setup.spec.ts:44`, which waited for
     the realm `ume` (it is `fors`); its six later tests did not run.
   - `thekim1` fixed that line (`5ffe3506`) and a recall pinned at 1.0 in `HnswIndexHealthTests` (`ccb452fe`), and
     started `e2e.yml` alone on the branch:
     [44844546882](https://github.com/thekim1/kalla/actions/runs/44844546882). **Read that run first**, then #906's
     own checks on its head.
   - **What counts as green** (the main session's reading of the user's condition, by the user's rule of 2026-11-15
     to fix a failing release check in isolation instead of another 30-minute dry run): #906's checks green on its
     head, and an `e2e.yml` run green on that head or a later one, with the release steps shown by 44839739980. Say
     so when approving. If the user or `thekim1` wants a full dry run again instead, that is theirs to ask for.
   - If it is red: diagnose it and say so on #906. `thekim1` asked that nobody else push to its branch, and the user
     chose that `thekim1` makes the fixes there.
   - If it is green and #906 is still open: approve as `Crotalus`, merge (squash, not `--admin`), and write on #906
     that the tag has the user's go-ahead. If `thekim1` has merged or tagged already, read what it did.
   - After the tag: the pin pull request that `release.yml` opens starts no checks; close and reopen it, wait for
     `deploy-checks`, merge (*How to*). Then the README, the roadmap and this file name the release.
2. **The first Audit run after the release** (the schedule says 04:17 UTC, the runs come around 13:00) scans
   0.9.0's images and should be green: #907 has the three updates the scan of 0.8.0 asked for. Until 0.9.0 is out
   the daily run stays red on 0.8.0's images. If it is red after: update, or a dated exception (#818's way).
3. **Then P2 wave 1** (Decisions "P2 starts before go-live"): B-54 to B-59 in stream b's queue, and B-60 (#893).
4. **Repository variables:** since the rename the workflows read `vars.KALLA_TEST_HOST_RUNNER`,
   `vars.KALLA_TEST_HOST_DEPLOY_ENV`, `vars.KALLA_R1_*` and `secrets.KALLA_R1_API_KEY`. `gh variable list` shows
   none to `ume-nichlas`; whoever set the `UME_RAG_*` ones creates them again (asked in #905, not answered).
5. **Fors's guide** (a pull request there; merges are the user's): its *Portainer* upgrade steps (update, then
   rename the roles) are right for a gateway alone and lead to a dead end in the stack that also holds Källa. A line
   pointing to Källa's guide for that stack would help. Its #112 and #113 (from the quick start run) are open.

### Waiting on the user

- **The VM:** runs 0.5.0 (Ume.Rag) with its gateway. **Wait for `v0.9.0`**, then one update by `docs/portainer.md`,
  *Upgrade*, as #906 writes it: the 0.5.x steps (the keys' residencies, the hand-made Open WebUI client, role
  groups), the integration renamed from `Ume.Rag` to `Kalla`, then **the services stopped and the roles renamed in
  consoles on the old `rag-postgres` and `postgres`**, the stack stopped, its variables renamed, and one update
  with *Prune services*. After the role rename the old stack does not start again: the way back is the backup of
  step 1. Never Fors 0.12.0 (thekim1/fors#116). Pulls that say "denied": `docker logout ghcr.io`. Portainer's own
  *Update the stack* has never been run on an upgraded stack, so this is the step with the most to learn. **0.5.1's
  admin image is gone from ghcr.io** (found 2026-11-18): the VM has its 0.5.0 images locally, a host that must pull
  them again cannot.
- **The live block** (the critical path to go-live, `plans/roadmap.md`): every item waits on an input, A-L1 (the
  umea.se inventory) first.
- **The support-chatbot proposal** (`plans/proposals/support-chatbot-features.md`): F3, F4 and F8 are built. The
  rest is the user's pick.
- **P2's later waves** (the roadmap's *P2 Broaden*): the user picks the next theme when wave 1 is nearly done.
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

- **The dated exceptions expire:** the scan's FFmpeg CVEs in the parser's opencv wheel (eleven, until 2026-12-17,
  `scripts/audit/image-scan-exceptions.txt`, #818) and picomatch in the admin UI's build (until 2026-12-18,
  `scripts/audit/npm-production-exceptions.txt`, #853). Before then: a newer wheel or picomatch fix, a new date with
  a reason, or drop the dependency. **opencv-python-headless 5.1.0.95** came out 2026-11-17 (the pin is 4.14.0.94);
  the parser's uv cutoff lets it in from about 2026-11-25: try it (Docling needs only `cv2.resize`, `cv2.dilate` and
  border constants) and see whether its wheel bundles a fixed FFmpeg. picomatch had no fix on 2026-11-20.
- **The Postgres image's `zlib1g` line** (`images/postgres/Dockerfile`, #907) goes when `pgvector/pgvector` has a
  build newer than 2026-11-12: the tag `0.8.7-pg17-trixie` had not been rebuilt on 2026-11-20, so the fix is one
  pinned package in our own Dockerfile. The AppHost and the TestKit pin the same base, unchanged.
- **#781, the flaky "search unavailable" answer in `ChatApiTests`:** open until 2026-11-30; if it recurs, the job's log
  has *The query embedding of profile … is unavailable (…)*, which names the cause.
- **`IntegrationReadbackTests.A_keys_usage_per_step_…` failed once** on #814 (the fake gateway answered 401,
  `scope_missing`). Seen once; no issue filed.
- **Flaky under load** (session 47): with many pull requests' checks on the two runners, `backend` failed with tests
  the change didn't touch (`ChatApiTests`, `RenderTests.The_real_renderer_…`); run alone they pass.
- **`gh pr edit` fails** on this repository with GitHub's *Projects (classic) is being deprecated* error and changes
  nothing; edit a pull request's body with `gh api -X PATCH repos/thekim1/kalla/pulls/<n> -F body=@file`.
- **Tests that share the job queue:** the nightly purge re-queues `delete-source` for every deleted source with live
  content (#884), so a test that queues a command for a deleted source can lose its lease (#897).
- **The demo seed's slug in a test:** `openwebui.spec.ts` leaves out the assistant named `demo-assistent` (#882); a
  change of `DemoSeeder`'s slug brings its old failure back.

## What session 51 did (2026-11-20 to 21)

On `~/research/rag`, as the main session, beside `thekim1`'s machine, which did the release preparation.

- **What the rename left (#905, merged):** the review of #902's findings 3, 4 and 6 to 16.
  - **Stored names kept** (the user's choice; Decisions "Product names"): the setup test's target is `rag-admin`
    again and the two applied migrations have their 0.8.0 text; the Kubernetes blob claim keeps its name
    `rag-blobs`. The Postgres StatefulSet's labels follow the rename, so the upgrade note has the operator delete
    the StatefulSet object first (its claims stay).
  - **The release tests:** the bundle's name in `release-smoke.sh`, the one-shots' names in `portainer-boot.sh`.
  - **Three faults the review had missed:** `portainer-boot.sh` expected the gateway's old setup cookie;
    `upgrade-to-0.9.sh` compared the backup target with its quotes on and so never kept the path `/ume-rag`; the same
    script took a half-copied volume as done.
  - **The volume copies resume:** the Portainer copy writes the pepper last and the database's `PG_VERSION` last.
  - **Guides and the note:** the integration is `Kalla` with the keys `rag-api`, `rag-admin` and `rag-worker`; the
    upgrade guide's unversioned sections; the note's Kubernetes steps, host state directory and SIEM category.
- **The Audit's image updates (#907, merged):** the parser on a Debian snapshot of 2026-11-19 (LibreOffice
  `deb13u12`), Playwright 1.65.1 in the worker and the renderer (Node 26.13.1 in the driver), `zlib1g` pinned in the
  Postgres image. Each image built and tested here before the pull request.
- **Closed #878 and #894** with an evidence comment.
- **The release preparation (#906, `thekim1`'s), reviewed four times.** It was opened at the same hour as #905 and
  folded the upgrade note as it was before #905; it was rebuilt on `main` after both merges. Its first dry run that
  could reach the upgrade test failed there (the next one, with the new order, passed it and failed only in the
  setup wizard's browser test, on a realm name that the main session's sweep of #905 had missed), and the fault was
  in the upgrade as documented: in the joined stack
  `kalla-bootstrap` waits for the gateway's `migrations`, which fail until the gateway's roles are renamed, so the
  volumes were never copied and `kalla-postgres`, where the guide said to rename the roles, never started.
- **The user's decisions (2026-11-21):** the Portainer upgrade renames the roles first, on the old databases with
  the services stopped, and then updates once; `thekim1` makes that change in #906 (done, `24078efe`, read); the
  go-ahead for #906 and the tag as in *Start here* 1.
- **Handbook:** six lessons (the open pull requests, the account switch, quoted values in `deploy/.env`, a "done"
  marker written last, the joined stack's start order, a base image with no newer build).

## Known gaps, documented, not fixed

- **The Kubernetes upgrade to 0.9.0** (the upgrade guide's six steps) is read through, not run: no cluster, and
  Kubernetes is rendered only. The Compose upgrade script `upgrade-to-0.9.sh` has no test of its own either: its
  variable step was run by hand (#905), its volume copy only read.
- **Not tested anywhere:** Portainer itself (an upgraded stack boots in CI since #817, with Compose; from 0.6.0 since
  0.8.0); both wizards completed against a real provider in one deployed stack (the boot test has no provider and
  stops at each wizard's state); Entra ID and AD FS; a real untrusted TLS certificate; Kubernetes beyond
  rendering.
- **The manifest default needs Compose 2.24.7 or later:** an older one cuts a default that contains `}` at the first
  one, without an error, also when the variable is set. Portainer pins a later one from 2.21 (read, not run).
- **A stack without `FORS_INTEGRATION_MANIFEST`** (0.5.x style, keys issued by hand) gets the default from 0.7.0: three
  unused credential keys unless the variable is set empty first (`docs/portainer.md`, *Upgrade from 0.6.0*).
- **The boot test shows no Open WebUI with OIDC** (no provider anywhere, so it starts without sign-in); `e2e.yml`
  shows it (accepted by the user).
- **A hardened Compose host's firewall** lets kalla-admin reach only the provider of `--oidc-authority`; without it
  the wizard's test can't reach a provider there. `init-deployment.sh --hardened` asks for it in an interactive run,
  and its closing lines say when it is missing. A host without the hardening has no such limit.
- **The Compose quick start (`docs/compose-quick-start.md`) was run once, by hand, not as written** (*What session
  49 did*): with locally built images, without root, and without a provider, so its steps 5.2 to 5.4, the host timers
  and the hardening are still only read. No test runs the two products' Compose projects together; the release
  smoke test boots `compose.rag.yaml` with the backup overlay against the fake gateway (since #872 also in the
  host-only form).
- **The gateway's Data API does not follow its wizard:** a gateway whose sign-in is set there sets
  `FORS_DATA_AUTHORITY` for access tokens. On Kubernetes a gateway that declares `Oidc__Authority` must set
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
  roadmap's table; `scripts/unreleased.py fold` moves `changes/` in), a dry run with `e2e=true`, an annotated tag on
  the merge commit (with the user's go-ahead; pushed by `thekim1` or the user: release tags are protected, *Start
  here*), then the pin pull
  request that `release.yml` opens: it starts no checks, so close and reopen it before merging (handbook, *Release dry
  runs*). The upgrade boot test starts from `release.yml`'s `UPGRADE_FROM` (0.6.0); its images must still be on
  ghcr.io.
- **Show a deploy change before a release:** a dry run from the branch (`gh workflow run release.yml --ref <branch>
  -f version=X.Y.Z-dryrun -f e2e=true`, about 30 minutes, with the runners idle). The boot-only run
  (`-f portainer_boot=X.Y.Z`) takes minutes but uses that version's images already on ghcr.io, so it only fits a change
  that needs no new image.
- **Rebuild the Portainer stack:** `sh deploy/portainer/build-stack.sh v0.12.1` (Fors's release; or its next one);
  `--check` is what `deploy-checks` runs.
- **Try Källa's wizard locally** (a throwaway volume each time): `export FakeGateway__Enabled=true
  Seed__Enabled=false Parser__Mode=off Render__Mode=off OpenWebUi__Enabled=false ClamAv__Mode=off
  DevelopmentVolumes__Postgres=<a throwaway name> Setup__Try=true`, `aspire start …`, `aspire wait admin-ui`; the code
  is in `aspire logs adminapi | grep 'Setup code:'`; the provider is `https://localhost:7411/realms/fors`, the clients
  `kalla-admin` and `open-webui-dev` (secrets: the AppHost parameters `oidc-client-secret`,
  `openwebui-client-secret`), the user `rag-platform-admin`. Afterwards `aspire stop --all`, wait, `docker volume rm`.
  Or run the spec: `tests/e2e/README.md`. On a FakeGateway stack the sign-in step shows the note that the gateway's
  sign-in is not set up yet (the fake announces the feature with no address).
- **Try the gateway's wizard locally:** in a gateway worktree, `Setup__Try=true Seed__Enabled=false
  DataApi__Enabled=false DevelopmentVolumes__Postgres=<a throwaway name>`, `aspire start --apphost
  src/Fors.AppHost/Fors.AppHost.csproj`, then `npx playwright test specs/setup.spec.ts` in
  `tests/e2e` (the gateway's `docs/local-development.md`, *Trying setup mode*). Port 5173 is taken here: set
  `ADMIN_UI_URL` to the port `aspire logs admin-ui` names and add that address's `/signin-oidc` to the `fors-admin`
  client in the bundled Keycloak.

## State

- **Release:** `v0.8.0` published 2026-11-19 ([run 44609831965](https://github.com/thekim1/kalla/actions/runs/44609831965)),
  tag at `faa001dd`, as Ume.Rag (`ghcr.io/thekim1/ume-rag/*`, EUPL-1.2). `v0.9.0`: *Start here* 1.
- **What a deployer types in Portainer:** 1 value since 0.7.0 (`FORS_DOMAIN` since the rename, no secret).
- **On `main` (`898e89f6`), unreleased:** everything 0.9.0 holds. Source dates, the escalation hand-off and topics in
  the chat API (B-47 to B-49, B-52); deletes of knowledge bases, sources and assistants with their purges (B-50,
  B-51, B-53, A-36, A-37); evaluation questions whose documents are gone (A-38); the demo seed; the rename (#902)
  with its leftovers (#905) on Fors 0.12.1 (#903); the image updates (#907). The notes are in `changes/` (22
  files) until #906 folds them. **The image defaults in the compose files are still 0.8.0's
  `ghcr.io/thekim1/ume-rag/*`** until the release pins 0.9.0's: nothing should start from `main`'s defaults.
- **Migrations since 0.8.0:** `CollectionSlugUniqueAmongLive`, `AssistantsDeletedAt`,
  `PurgeDeletedSourcesAndCollections`; *Rolling update: no* (the rename).
- **Queues** (as #906 leaves them): stream a: the live block (A-L1 to A-L10), A-34 and A-35 with only their live
  checks open. Stream b: B-54 to B-60 (P2 wave 1 and #893), B-42 to B-46 (the gateway phases' live checks) and its
  live items. Main: M-4, M-16, M-17.
- **Open issues that matter:** #893 (B-60), #781 (watch), #667 (M-16), #333, #511, #580 and #550. In Fors: #112 and
  #113. Open pull requests: #906 and this file's (#904).
- **Dependabot works:** grouped pull requests; `.github/dependabot.yml`'s header lists where a pin is repeated.
  Playwright is bumped by hand, in the package list, the renderer image and its lockfile together (#907).
- **The user's machine (`~/research/rag`):** the main clone on `main`, and three more checkouts: `~/research/rag-main`
  (on `main-session/handover-51`, this file's branch), `~/research/rag-small` (the old `main-session/handover-50`)
  and `~/research/rag-902` (detached, from the review of #902); the last two can go. Docker works without sudo;
  there is no sudo. Volumes `kalla-m1-postgres` (development data, kept), `kalla-openwebui-data` and
  `kalla-clamav-db`. Local images from this session: `kalla.local/renderer:ci` (now Playwright 1.65.1; the worker's
  render tests take it through `KALLA_TEST_RENDERER_IMAGE`), `kalla.local/parser-test-env:bump09`,
  `kalla.local/parser-test:bump09` and `kalla.local/postgres:bump09` (they can go). **Docker's default address pools
  are full** (about 25 unused networks of other projects): a new Compose project needs explicit subnets, or a
  `docker network prune` by the user. The machine's name is upper case and does not resolve inside containers.
  Neither `gh` token has `read:packages`. `kubectl` is installed (`kubectl kustomize` renders the overlays);
  `actionlint` is at `/snap/bin/actionlint`; `shellcheck` and `pwsh` are not installed; `grep` is ugrep.
  Playwright's Chromium and Firefox are in `~/.cache/ms-playwright`. The Aspire CLI is at
  `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire` (not on PATH).
- **`thekim1`'s machine:** a fresh clone after the rename; its development data starts empty under `~/.kalla`.
- **Fors on the user's machine:** `~/git/llmgateway` (and the worktrees `-100`, `-100-deploy`, `-101`) are clones of
  `thekim1/fors` on merged branches; fetched through `v0.12.1` (read a release with `git show v0.12.1:<path>`). They
  can go, or move to `main`. Merges and releases in Fors are the user's; its rules: draft pull requests (drafts run
  no CI), checks run locally first, squash-merge. Ports 5000 and 5173 are taken here
  (`ASPNETCORE_URLS=http://127.0.0.1:0` for its IntegrationTests).
