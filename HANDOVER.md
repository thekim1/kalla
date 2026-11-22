# Handover

State of the Källa work for the next session, rewritten each session (earlier ones: `git log -- HANDOVER.md`).
**Last release `v0.9.2`** (2026-11-21, tag at `c7d5de47`, on Fors `v0.12.2`): JavaScript rendering works again and topic
rules take a pasted address. Nothing unreleased on `main` but plan files. The user's VM runs 0.5.0. Two machines work
on it: `~/research/rag` (the user's, where sessions 49 to 53 ran) and `thekim1`'s (stream b, often stream a, the
releases, and a coordinating session that writes proposals). Branch `main`, remote `origin` =
https://github.com/thekim1/kalla (HTTPS through `gh auth git-credential`, handbook *Environment notes*).

## What this is

**Källa** (`Kalla` in identifiers; Decisions "Product names") is a RAG / crawling / chatbot platform for Umeå kommun:
small .NET 10 services on top of **Fors**, the team's LLM gateway (https://github.com/thekim1/fors, the local clone
still `~/git/llmgateway`), as an add-on to it (Decisions "Shared sign-in with the gateway"). The user owns the gateway
and wants to do better than **Eneo**, a slow "kitchen sink" monolith. Design and code live in this repo.

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

## Start here (session 54)

**The rules of the repository:**
- **`main` is protected** ("Protect main": a pull request with one approving review and the required checks; an
  author cannot approve their own, never `gh pr merge --admin` as `ume-nichlas`). Everything, `plans/**` and this
  file included, goes through a `main-session/` branch. `thekim1` merges green pull requests with `--admin` (allowed
  by the user on 2026-11-19), its own included.
- **Release tags are protected** (`refs/tags/v*`): a tag is `thekim1`'s or the user's to push, on the release
  preparation's merge commit, with the user's go-ahead.
- **The session's GitHub account is `ume-nichlas`.** `gh` here also holds `Crotalus` (the user's own). **Run
  `gh auth status` before every push or post.**
- **Approvals on this machine.** Approving as `Crotalus` goes through on the user's direct word in the conversation
  for named pull requests ("approve #936, #937, #938"): read each pull request's state and checks, switch the account
  in a command of its own, approve, switch back at once, then merge the green ones. Without that word: review as a
  comment, say that it is not the approval, and give the user the line to run (handbook, *How we work*). **Stacked or
  overlapping pull requests:** after the first is squash-merged the next conflicts; a rebase and push dismisses its
  approval, so it needs the word again (session 53: the same word for the same named pull requests was accepted).
- **Look at the open pull requests before each step** (`gh pr list`): the other machine works at the same hour. Say on
  its pull request what you are about to write; review its pull requests as comments (claims checked against the code).
- **A decision for the user is asked as four alternatives with one recommendation** (the question tool).

**Open when this was written** (2026-11-22, 10:00 UTC): `thekim1`'s #935 (a schedule per source in the admin UI; one
commit, unchanged since the review) and #932 (the topics-editor proposal; no answer to the main session's comment),
and this file's pull request. No issue moved overnight; #905 and #920 have no reply. In order:

1. **#932, the topics editor** (#929 steps 2 to 4; the user's preference: pick topics from the sections found). The
   main session's view and its answers to the proposal's four questions are on the pull request (keep `EditPipelines`;
   depth 1 with the next level on demand; the path segment as the name; one topic across sources; levels respected in
   the suggestions; the re-chunk count from the publication plan). **The user decides** whether to accept those
   answers and whether to write the Decisions entry and the designs (admin §5.5.2, processing §7) now or after P2 wave 1;
   the main session recommended now. Ask with four alternatives if they haven't said.
2. **#935, a source's schedule** (stream b): reviewed as a comment. The rule matches the worker's `SyncScheduler`;
   asked for a browser check and noted that the rule could live once in `Kalla.Pipelines` (both the admin and the worker
   reference it). `thekim1` merges it.
3. **#925's steps 2 to 4** (personal-data verdicts, *reject*, the false-positive summary): the user answered the four
   questions (decisions log, 2026-11-21); the Decisions entry and the design changes are left to `thekim1`'s session
   (said on #925). Step 1 is stream b's B-63.
4. **The design clean-up is done** (#926, #936 to #938). Share 1 was read line by line; shares 2 to 4 were scanned for
   the kinds of staleness share 1 found and each hit's section read, so a stale passage that matches no such pattern
   can remain. A full read of those three shares is possible once agents can run again (the usage limit resets
   2026-11-26 19:00, Europe/Stockholm). Left open: admin §4.5's row on manual model rows ("`SupportsTools` until the
   gateway sends `features`; not built yet") says unclearly what isn't built (check `GatewayEndpoints.cs`);
   repo-and-deployment §14 and data-model §14 repeat `plans/to-verify.md`; the FakeGateway's per-level dev keys,
   "kept until Källa moves to credential keys", are still in the code although it has moved. Not read at all:
   `plans/to-verify.md`, the open queue items, `postponed-decisions.md`, `plans/cases/support-assistant/`,
   `plans/gold-set/README.md`, `docs/` (stream a's).
5. **The VM's upgrade is `thekim1`'s** (#920): it can now go from 0.5.0 straight to 0.9.2 (`docs/portainer.md`,
   *Upgrade*). Read what is reported; a step that did not go as the guide says is a guide fix, and the roadmap's T1 row
   follows the result. No report yet.
6. **Repository variables:** the workflows read `vars.KALLA_TEST_HOST_RUNNER`, `vars.KALLA_TEST_HOST_DEPLOY_ENV`,
   `vars.KALLA_R1_*` and `secrets.KALLA_R1_API_KEY`, and none exist (`gh variable list` empty on 2026-11-22). On the
   user's decision `thekim1` was asked on #905 (2026-11-21) with the full list; no answer yet.
7. **P2 wave 1 runs on `thekim1`'s machine:** B-54 to B-59 and B-63 open in stream b's queue, then B-61 and B-62
   (support-chatbot F10 and F7). Say on the issue before starting one here.

### Waiting on the user

- **The umea.se inventory (A-L1):** the first input of the live block (`plans/roadmap.md`); every live item waits on an
  input, A-L1 first.
- **#932's questions** (above, item 1).
- **The support-chatbot proposal** (`plans/proposals/support-chatbot-features.md`): F3, F4 and F8 built; F10 and F7
  queued (B-61, B-62); the rest is the user's pick.
- **P2's later waves** (the roadmap's *P2 Broaden*): the user picks the next theme when wave 1 is nearly done.
- **To-verify row 96:** the NVDA screen-reader pass of the admin UI, which needs Windows.
- **To-verify row 79:** whether `NerMinScore` moves for the int8 NER model (left at 0.80: the sample is small).
- **U1 (pictures in uploads)** waits for DoIT's files at T6 (decided 2026-11-15).
- **The old development data on this machine:** the `ume-rag-*` volumes and the `ume-rag-apphost-dev` secrets are kept
  beside their copies until the user lets them go; three older `ume.rag.apphost-*-postgres-data` volumes and the
  ignored build output under `src/Ume.Rag.*` can go too. Don't remove them unasked.

### Watch

- **The CI runners' disk filled up once** (2026-11-21, the 0.9.2 dry run's parser image job; `thekim1` re-ran it).
  If a long job is cancelled with no step result again, look at the runner machine's disk first.
- **The dated exceptions expire:** the scan's FFmpeg CVEs in the parser's opencv wheel (eleven, until 2026-12-17,
  `scripts/audit/image-scan-exceptions.txt`, #818) and picomatch in the admin UI's build (until 2026-12-18,
  `scripts/audit/npm-production-exceptions.txt`, #853). Before then: a newer wheel or picomatch fix, a new date with
  a reason, or drop the dependency. **opencv-python-headless 5.1.0.95** came out 2026-11-17 (the pin is 4.14.0.94);
  the parser's uv cutoff lets it in from about 2026-11-25: try it (Docling needs only `cv2.resize`, `cv2.dilate` and
  border constants) and see whether its wheel bundles a fixed FFmpeg. picomatch had no fix on 2026-11-20.
- **The Postgres image's `zlib1g` line** (`images/postgres/Dockerfile`, #907) goes when `pgvector/pgvector` has a
  build newer than 2026-11-12.
- **#781, the flaky "search unavailable" answer in `ChatApiTests`:** open until 2026-11-30; if it recurs, the job's log
  has *The query embedding of profile … is unavailable (…)*, which names the cause. **#921**, a flaky render-timing
  test, is `thekim1`'s.
- **Flaky under load** (session 47): with many pull requests' checks on the two runners, `backend` failed with tests
  the change didn't touch; run alone they pass.
- **`gh pr edit` fails** on this repository (*Projects (classic) is being deprecated*) and changes nothing; edit a pull
  request with `gh api -X PATCH repos/thekim1/kalla/pulls/<n> -f title=… -F body=@file`.
- **Tests that share the job queue:** the nightly purge re-queues `delete-source` for every deleted source with live
  content (#884), so a test that queues a command for a deleted source can lose its lease (#897).
- **The demo seed's slug in a test:** `openwebui.spec.ts` leaves out the assistant named `demo-assistent` (#882).

## What session 53 did (2026-11-21 and 22)

On `~/research/rag`, as the main session. Session 52's work is in this file's history.

- **Twelve of the user's decisions** (four alternatives each; decisions log): the design clean-up one share at a time;
  no question to KB (the NER model's MIT licence is taken as covering the weights, to-verify 110 closed); the Eneo PDF
  stays outside the repository; T0 done with 0.5.0; support-chatbot F10 and F7 next; `thekim1` asked for the
  repository variables; the development stack copied to the new names; and four answers on #925 (personal-data
  review: *reject* a state of its own, false positives need the types, deciders as today, step 1 queued as B-63).
- **Recorded and designed:** #923 (the answers, B-63), #924 (F10 cost per outcome and F7 content gaps: Decisions
  entries, chat §12, data-model `AssistantDailyOutcomes` and `AssistantDailyGaps`, admin §8.6 and §8.7, B-61 and B-62).
- **The design clean-up** in four pull requests: #926 (repo-and-deployment, identity, data-model, gateway-changes, read
  in full; `adr-drafts.md` archived; `ume-mcp-*` and `rag-tools` renamed in every plan; Decisions "CI on the
  self-hosted runner" states the protected `main`), #936 (chat, knowledge, tools), #937 (ingestion, parser), #938
  (admin §5.8 to §5.10 as built, Decisions' interim-gateway pointer, architecture's rerank).
- **This machine:** the dev secrets and volumes copied to the new names (the old ones kept) and a start on the copy
  checked (migrations up, five knowledge bases); the old images and Fors worktrees removed, `~/git/llmgateway` on
  `main`.
- **GitHub:** reviews as comments on #925, #932, #933 and #935; the cancelled 0.9.2 dry run reported on #933.
  `thekim1` released 0.9.2 (#933, pin #934) and closed #927. All of the main session's pull requests (#923, #924, #926, #936 to #939)
  were approved as `Crotalus` on the user's word and merged; #939 was the first version of this file. Fors #112 and #113 are closed.

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

- **Release:** `v0.9.2` published 2026-11-21 23:27 UTC (tag at `c7d5de47`, on Fors `v0.12.2`); `v0.9.1` and `v0.9.0`
  the same day. All are `ghcr.io/thekim1/kalla/*`, public, Apache-2.0. `v0.8.0` and earlier are Ume.Rag
  (`ghcr.io/thekim1/ume-rag/*`, EUPL-1.2).
- **Audit:** last green on 2026-11-21 ([run 44905272366](https://github.com/thekim1/kalla/actions/runs/44905272366));
  0.9.2's images are scanned from the next run. The dated exceptions are in *Watch*.
- **What a deployer types in Portainer:** 1 value since 0.7.0 (`FORS_DOMAIN`, no secret).
- **On `main`, unreleased:** plan files only; `changes/` holds only its README.
- **Queues:** stream a: the live block (A-L1 to A-L10), A-34 and A-35 with only their live checks open. Stream b:
  B-54 to B-59 and B-63 (P2 wave 1), B-61 and B-62 (F10, F7), B-42 to B-46 (the gateway phases' live checks) and its
  live items. Main: M-4.
- **Open issues that matter:** #781 and #921 (watch), #920 (the VM's upgrade, `thekim1`'s), #929 (topics; steps 2 to 4
  are #932), #667 (M-16's issue: the work is done, `thekim1`'s to close), #333, #511, #580 and #550. In Fors: proposals
  #108 and #109, #99, #91, and a flaky test #124; no open pull requests.
- **Dependabot works:** grouped pull requests; `.github/dependabot.yml`'s header lists where a pin is repeated.
  Playwright is bumped by hand, in the package list, the renderer image and its lockfile together (#907).
- **The user's machine (`~/research/rag`):** the main clone on `main`, and a worktree per open `main-session/` pull
  request. Docker works without sudo; there is no sudo.
  - **The development stack is under the new names** (session 53): secrets in `kalla-apphost-dev`, data in
    `kalla-dev-postgres`, `kalla-openwebui-data` and `kalla-clamav-db`. A start against a real gateway needs
    `Gateway:Services:<service>:Key` first; until then start with `FakeGateway__Enabled=true` (handbook, *Local stack*).
    Stop it with `aspire stop --apphost src/Kalla.AppHost/Kalla.AppHost.csproj` (the permission check stops `--all`).
  - **Local images:** `kalla.local/renderer:ci` (Playwright 1.65.1; the worker's render tests take it through
    `KALLA_TEST_RENDERER_IMAGE`).
  - **Docker's default address pools are full** (about 25 unused networks of other projects): a new Compose project
    needs explicit subnets, or a `docker network prune` by the user. The machine's name is upper case and does not
    resolve inside containers. Neither `gh` token has `read:packages`; Källa's images are public.
  - **Tools:** `kubectl` is installed (`kubectl kustomize` renders the overlays); `actionlint` is at
    `/snap/bin/actionlint`; `shellcheck` and `pwsh` are not installed; `grep` is ugrep. Playwright's Chromium and
    Firefox are in `~/.cache/ms-playwright`. The Aspire CLI is at
    `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire` (not on PATH).
- **`thekim1`'s machine:** a fresh clone after the rename; its development data starts empty under `~/.kalla`.
- **Fors on the user's machine:** `~/git/llmgateway` is a clone of `thekim1/fors` on `main` at `v0.12.2`; read a
  release with `git show v0.12.2:<path>`. The remote is still named `thekim1/llmgateway` there (GitHub redirects); push
  with the `gh` credential helper. Merges and releases in Fors are the user's; its rules: draft pull requests (drafts
  run no CI), checks run locally first, squash-merge. Ports 5000 and 5173 are taken here
  (`ASPNETCORE_URLS=http://127.0.0.1:0` for its IntegrationTests).
