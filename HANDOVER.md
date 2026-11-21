# Handover

State of the Källa work for the next session, rewritten each session (earlier ones: `git log -- HANDOVER.md`).
**Last release `v0.9.1`** (2026-11-21, tag at `e9ec0597`, on Fors `v0.12.2`), the same day as `v0.9.0`, the first
under the names Källa and Fors: 0.9.1 makes the upgrade need no SQL by hand. Nothing is unreleased on `main`. The
user's VM runs 0.5.0. Two machines work on it: `~/research/rag` (the user's, where sessions 49 to 52 ran) and
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

## Start here (session 53)

**The rules of the repository:**
- **`main` is protected** ("Protect main": a pull request with one approving review and the required checks; an
  author cannot approve their own, never `gh pr merge --admin` as `ume-nichlas`). Everything, `plans/**` and this
  file included, goes through a `main-session/` branch. `thekim1` merges green pull requests with `--admin` (allowed
  by the user on 2026-11-19), the main session's too.
- **Release tags are protected** (`refs/tags/v*`): `ume-nichlas` cannot push one (GH013), no workflow creates one.
  A tag is `thekim1`'s or the user's to push, on the release preparation's merge commit.
- **The session's GitHub account is `ume-nichlas`** (user, 2026-11-20). `gh` here also holds `Crotalus` (the user's
  own), and the active account has switched to `Crotalus` by itself before. **Run `gh auth status` before every push
  or post**, and `gh auth switch -u ume-nichlas` when it shows `Crotalus`.
- **The session approves nothing on this machine.** `gh pr review --approve` as `ume-nichlas` is stopped by Claude
  Code's permission check, and in session 52 so was approving as `Crotalus`, with the user's decision on record and
  their go-ahead in the conversation. Review as a comment, say that it is not the approval, and give the user the
  line to run (handbook, *How we work*). What then merges a pull request is the user's approval, `thekim1`'s
  approval, or `thekim1`'s `--admin`.
- **Look at the open pull requests before each step** (`gh pr list`): the other machine works at the same hour.
  Say on its pull request what you are about to write.
- **A decision for the user is asked as four alternatives with one recommendation** (the question tool).

**Open when this was written** (2026-11-21 17:15 UTC): this file's pull request (#915, the plan clean-up) and
Dependabot's four (#911 to #914). Nothing of the main session's is open in Fors. **The user
ended the session here and continues with the clean-up next time.** In order:

1. **#915, the plan clean-up:** its checks are green; it waits for an approval (the user's or `thekim1`'s). When it
   is merged, remove the worktree `~/research/rag-docs` and the branch. It closed M-16 in the queue; M-16's issue
   #667 is `thekim1`'s to close, asked in #915's description.
2. **Continue the clean-up with what #915 did not read:**
   - **The design documents** (about 15,000 lines). Four agents were sent over them and stopped by the usage limit
     before they reported (it resets 2026-11-26 19:00, Europe/Stockholm). Their shares: admin, `decisions.md`,
     cost-attribution and `architecture.md`; chat, knowledge and tools; the three ingestion designs and the parser;
     identity, data-model, repo-and-deployment and gateway-changes (with `adr-drafts.md` and
     `prompt-gateway-session.md`: do they still have a purpose?). What to look for: statements that the code has
     overtaken ("not built yet", "P2", an open pull request), names from before the rename that are not about the
     old name on purpose, history that belongs in `plans/archive/design-history/`, and passages that repeat another
     document. Known already: admin §5.8 to §5.10 still read as before and after M-16; `plans/tools/design.md` says
     `rag-tools` once where the rest says `kalla-tools`.
   - **Not read either:** `plans/to-verify.md`, the open items of the two stream queues, `postponed-decisions.md`
     beyond its header, `plans/cases/support-assistant/` (written for 0.5.0), `plans/gold-set/README.md`, and
     `docs/` (stream a's; only the accessibility statement's old working name was fixed).
   - A design is binding: change what is plainly out of date, and bring anything that changes a rule to the user.
3. **Dependabot's #911 to #914** are reviewed as comments (2026-11-21). **#912** (admin UI) and **#914**
   (AngleSharp) are green and fine. **#911** (uv 0.13.9) is green but not ready: `parser.yml:77`, `audit.yml:101`
   and the comment at `src/parser/Dockerfile:7` still say 0.13.8. **#913** (the parser's minor group) has
   `parser-image` red: `src/parser/LICENSES.md` must be regenerated in the image with `scripts/licences.py`. Nothing
   was pushed to their branches. The user was offered that this session makes the two fixes and had not answered;
   look at what `thekim1`'s machine did first.
4. **P2 wave 1 runs on `thekim1`'s machine** (Decisions "P2 starts before go-live"): B-60 is merged (#910), B-54 to
   B-59 are open in stream b's queue. The user was offered implementor agents on this machine for some of them and
   chose the plan clean-up instead; say on the issue before starting one.
5. **This machine's development stack still has its old names** (*State*): nothing was moved at the rename. The
   user was told and asked whether to copy the secrets and volumes over; not answered. Do not move them unasked.
6. **Repository variables:** since the rename the workflows read `vars.KALLA_TEST_HOST_RUNNER`,
   `vars.KALLA_TEST_HOST_DEPLOY_ENV`, `vars.KALLA_R1_*` and `secrets.KALLA_R1_API_KEY`. `gh variable list` shows
   none to `ume-nichlas` (both accounts here have write access, not admin); whoever set the `UME_RAG_*` ones creates
   them again (asked in #905, not answered; the user was told on 2026-11-21).
7. **Fors is on `v0.12.2`** (2026-11-21): its stack renames the gateway's database roles itself, and `thekim1`
   merged #119 to #123 there (the release bundle's `docs/`, the Keycloak trust store, the setup callback limit, the
   drain, the role rename), which the main session has not read. Whether #112 and #113 are closed by them is not
   checked. **thekim1/fors#118, the main session's note for the upgrade guide, is closed:** 0.12.2 removed the dead
   end it warned about, and its 0.12.2 section says how a stack that also holds Källa upgrades.

### Waiting on the user

- **The VM:** runs 0.5.0 (Ume.Rag) with its gateway. **It can move to `v0.9.1` now** (offered to the user as the
  next thing to do, 2026-11-21; they chose the plan clean-up first): one update by `docs/portainer.md`, *Upgrade*:
  back up, the 0.5.x steps (the keys' residencies, the hand-made Open WebUI client, role groups), a look through the
  variables, then **rename the stack's variables, paste 0.9.1's file and update once**. The roles are renamed by
  the stack's own one-shots (`kalla-roles`, and Fors 0.12.2's `roles`): no console and no SQL, which 0.9.0 still
  needed. Read the guide's steps before the user starts: the main session has not read 0.9.1's version of them.
  Never Fors 0.12.0 (thekim1/fors#116). Portainer's own *Update the stack* has never been run on an upgraded stack,
  so this is the step with the most to learn. **0.5.1's admin image is gone from ghcr.io** (found 2026-11-18): the
  VM has its 0.5.0 images locally, a host that must pull them again cannot.
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

## What session 52 did (2026-11-21)

On `~/research/rag`, as the main session. Session 51's work (the rename's leftovers #905, the image updates #907,
the reviews of #906) is in this file's history: `git log -- HANDOVER.md`.

- **#906 checked and `v0.9.0` released.** The head `3ef38e8e` was read against the runs: its own checks green, the
  release steps (smoke test, Portainer boot, the upgrade from 0.6.0 with the roles renamed first) in dry run
  [44839739980](https://github.com/thekim1/kalla/actions/runs/44839739980), the browser tests in
  [44846329548](https://github.com/thekim1/kalla/actions/runs/44846329548). No single dry run was green from end to
  end, and the comment on #906 says so.
- **The approval did not go as decided.** The session could not approve as `Crotalus` (the permission check). The
  user approved twice themselves, and GitHub dismissed both within seconds with *The merge-base changed after
  approval* (handbook, *Lessons*). **The user then decided** (four alternatives) that `thekim1` merges with
  `--admin`: merged as `1ed5bdd8`, whose tree is `3ef38e8e`'s, tagged, and published at 10:28 UTC.
- **The pin (#908)** compared with the release's own stack file, closed and reopened for its checks; `thekim1` merged
  it. **The handover (#904)** approved by `thekim1` and merged by the session.
- **thekim1/fors#118:** a line for Fors's upgrade guide, marked ready on the user's word and closed the same day,
  when Fors 0.12.2 made it wrong.
- **`v0.9.1` came out while the clean-up was open** (`thekim1`'s: #916 to #919, on Fors 0.12.2): the role renames
  run in the stack. The main session did not review it; this file and the roadmap were brought up to it.
- **After the release:** the first Audit run on 0.9.0's images is green (*State*); Dependabot's #911 to #914 read
  and answered as comments (*Start here* 3); the worktrees `rag-small` and `rag-902` removed.
- **The plan clean-up** (the user's choice of what to do next): the roadmap without its finished and duplicated
  parts; M-16 and M-17 closed and archived, with the lines in the admin design and Decisions that still called them
  open; the handbook sorted by subject and without what no longer holds; the README's CI section and document list;
  the proposals' status lines; this file.

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
  request that `release.yml` opens: it starts no checks, so close and reopen it before merging (handbook, *CI and
  releases*). The upgrade boot test starts from `release.yml`'s `UPGRADE_FROM` (0.6.0); its images must still be on
  ghcr.io.
- **Show a deploy change before a release:** a dry run from the branch (`gh workflow run release.yml --ref <branch>
  -f version=X.Y.Z-dryrun -f e2e=true`, about 30 minutes, with the runners idle). The boot-only run
  (`-f portainer_boot=X.Y.Z`) takes minutes but uses that version's images already on ghcr.io, so it only fits a change
  that needs no new image.
- **Rebuild the Portainer stack:** `sh deploy/portainer/build-stack.sh v0.12.2` (Fors's release; or its next one);
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

- **Release:** `v0.9.1` published 2026-11-21 ([run 44923888580](https://github.com/thekim1/kalla/actions/runs/44923888580)),
  tag at `e9ec0597`, on Fors `v0.12.2`; `v0.9.0` the same morning (tag at `1ed5bdd8`, on Fors `v0.12.1`). Both are
  `ghcr.io/thekim1/kalla/*`, public, Apache-2.0. `v0.8.0` and earlier are Ume.Rag (`ghcr.io/thekim1/ume-rag/*`,
  EUPL-1.2).
- **Audit:** green on 2026-11-21 ([run 44905272366](https://github.com/thekim1/kalla/actions/runs/44905272366)),
  the first run on 0.9.0's images and the first green one since the image scan was added (#819). 0.9.1's images
  are scanned from the next run. The dated exceptions are in *Watch*.
- **What a deployer types in Portainer:** 1 value since 0.7.0 (`FORS_DOMAIN` since the rename, no secret).
- **On `main`, unreleased:** nothing (`changes/` holds only its README); the compose files default to 0.9.1's
  images (#919).
- **Migrations in 0.9.0:** `CollectionSlugUniqueAmongLive`, `AssistantsDeletedAt`,
  `PurgeDeletedSourcesAndCollections`; *Rolling update: no* (the rename).
- **Queues:** stream a: the live block (A-L1 to A-L10), A-34 and A-35 with only their live checks open. Stream b:
  B-54 to B-59 (P2 wave 1), B-42 to B-46 (the gateway phases' live checks) and its live items. Main: M-4.
- **Open issues that matter:** #781 (watch), #667 (M-16's issue: the work is done, the issue is `thekim1`'s to
  close), #333, #511, #580 and #550. In Fors: #112 and #113. Open pull requests: this file's and Dependabot's #911
  to #914; none in Fors.
- **Dependabot works:** grouped pull requests; `.github/dependabot.yml`'s header lists where a pin is repeated.
  Playwright is bumped by hand, in the package list, the renderer image and its lockfile together (#907).
- **The user's machine (`~/research/rag`):** the main clone on `main` and one more checkout, `~/research/rag-docs`
  (this file's branch, #915; it goes when the pull request is merged). Docker works without sudo; there is no sudo.
  - **The development stack has not run since the rename, and its data is still under the old names**
    (checked 2026-11-21; this file said otherwise before): the AppHost's user secrets are under the old id
    `ume-rag-apphost-dev` and the new id `kalla-apphost-dev` has none; the volumes are `ume-rag-m1-postgres` (the
    development data; the old secret `DevelopmentVolumes:Postgres` names it), `ume-rag-openwebui-data` and
    `ume-rag-clamav-db`, while the AppHost now mounts `kalla-openwebui-data` and `kalla-clamav-db`; the helper
    scripts are in `~/ume-rag-tools/`. A start from `main` today gets an empty database and generated secrets.
    Moving the secrets and volumes over is the user's call.
  - **Local images:** `kalla.local/renderer:ci` (Playwright 1.65.1; the worker's render tests take it through
    `KALLA_TEST_RENDERER_IMAGE`). `kalla.local/*:bump09`, every `ume-rag*/*` image and
    `ume-llm-gateway/*:setup-test` are left from earlier sessions and can go.
  - **Docker's default address pools are full** (about 25 unused networks of other projects): a new Compose project
    needs explicit subnets, or a `docker network prune` by the user. The machine's name is upper case and does not
    resolve inside containers. Neither `gh` token has `read:packages`; Källa's images are public since 0.9.0.
  - **Tools:** `kubectl` is installed (`kubectl kustomize` renders the overlays); `actionlint` is at
    `/snap/bin/actionlint`; `shellcheck` and `pwsh` are not installed; `grep` is ugrep. Playwright's Chromium and
    Firefox are in `~/.cache/ms-playwright`. The Aspire CLI is at
    `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire` (not on PATH).
- **`thekim1`'s machine:** a fresh clone after the rename; its development data starts empty under `~/.kalla`.
- **Fors on the user's machine:** `~/git/llmgateway` (and the worktrees `-100`, `-100-deploy`, `-101`) are clones of
  `thekim1/fors` on merged branches; fetched through `v0.12.2` (read a release with `git show v0.12.2:<path>`). They
  can go, or move to `main`. The remote is still named `thekim1/llmgateway` there (GitHub redirects); push with
  the `gh` credential helper. Merges and releases in Fors are the user's; its rules: draft pull requests (drafts run
  no CI), checks run locally first, squash-merge. Ports 5000 and 5173 are taken here
  (`ASPNETCORE_URLS=http://127.0.0.1:0` for its IntegrationTests).
