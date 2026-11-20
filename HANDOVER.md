# Handover

State of the Källa work for the next session, rewritten each session (earlier ones: `git log -- HANDOVER.md`).
**Last release `v0.8.0`** (2026-11-19, the deploy simplification, as Ume.Rag; tag on #856's merge commit
`faa001dd`, on the gateway's `v0.11.0`). Since then `main` has a release's worth of work **and the rename to Källa
and Fors** (#902, 2026-11-20): the next release, `v0.9.0`, is the first under the new names, on Fors `v0.12.1`. The
user's VM runs 0.5.0. Two machines work on it: `~/research/rag` (the user's, where sessions 49 and 50 ran) and
`thekim1`'s (stream b and often stream a). Branch `main`, remote `origin` = https://github.com/thekim1/kalla (the
repository was renamed from `thekim1/ume-rag-platform`; old links redirect; HTTPS through `gh auth git-credential`,
handbook *Environment notes*).

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

## Start here (session 51)

**The rules of the repository:**
- **`main` is protected** ("Protect main": a pull request with one approving review and the required checks; an
  author cannot approve their own, never `gh pr merge --admin` as `ume-nichlas`). Everything, `plans/**` and this
  file included, goes through a `main-session/` branch. `thekim1` merges its own green pull requests with `--admin`
  (allowed by the user on 2026-11-19) and did so for #897 (the main session's) too.
- **Release tags are protected** (`refs/tags/v*`): `ume-nichlas` cannot push one (GH013), no workflow creates one.
  The `v0.9.0` tag is `thekim1`'s or the user's, on the release preparation's merge commit.
- **The session's GitHub account is `ume-nichlas`** (user, 2026-11-20). `gh` here also holds `Crotalus` (the user's
  own), and **the active account switched to `Crotalus` by itself twice on 2026-11-20** (not by the session): the
  review of #902 and thekim1/fors#116 went out under `Crotalus`. **Run `gh auth status` before every push or post**,
  and `gh auth switch -u ume-nichlas` when it shows `Crotalus`.
- **Approvals on this machine:** `gh pr review --approve` as `ume-nichlas` is stopped by Claude Code's permission
  check (#882, #896): review as a comment. When the user says so explicitly, the session approves the main
  session's pull requests **as `Crotalus`** (`gh auth switch -u Crotalus`, approve, switch back at once): done for
  #889 to #892 and #895 on 2026-11-20, each time on the user's word for those pull requests only.

**Nothing of the main session is open.** In order:

1. **Fix what #902 left before the 0.9.0 dry run** (the main session's review on #902, findings 3 to 16; 1, 2 and 5
   were fixed before the merge, by `StoredFormatNames` and the rerun test). One `main-session/` pull request, since
   they cross both streams' paths. Checked on `main` at `b1212c92`:
   - **The release run fails without these:** `deploy/tests/release-smoke.sh:62-66` expects a `ume-rag-X.Y.Z`
     bundle and unsets only `^UME_` (add `^KALLA_`, `^FORS_`); `deploy/tests/portainer-boot.sh:205/207` match
     `*-rag-migrate-1` and `*-rag-openwebui-setup-1` (now `kalla-*`); `tests/e2e/specs/setup.spec.ts:265` expects
     `secret-expiry-rag-admin` (the UI renders `secret-expiry-kalla-admin`).
   - **An upgraded database:** `SetupTargets.RagAdmin` became `"kalla-admin"` and `CK_SetupTests_Target` was edited
     inside the applied migrations `20261115180053_SetupMode` and `20261115185539_OpenWebUiSignInSettings`, with no
     new migration: a 0.8 database keeps `IN ('rag-admin','open-webui')`, so the wizard's test sign-in fails (23514).
     Either keep the stored value `rag-admin` (and put the two migrations back), or a new migration that rewrites
     the constraint and the rows; never edit an applied migration.
   - **Kubernetes:** the example's namespace (`ume-rag` → `kalla`), the blob PVC (`rag-blobs` → `kalla-blobs`) and the
     StatefulSet's selector labels changed; the upgrade note's "apply the new manifests and delete the old objects"
     gives an empty database and blob store or a refused apply. Keep the PVC names and selectors, or write how to
     move them.
   - **Docs:** `docs/portainer.md:142,353,407` and `docs/compose-quick-start.md:96` name the credential keys
     `kalla-*` (they stay `rag-api`, `rag-admin`, `rag-worker`, Fors's `kalla.json`), the integration is `Kalla` not
     "`Källa`"; `docs/upgrade-guide.md`'s unversioned sections (preamble, *Upgrade procedure*, *Image pins*,
     *Superuser steps*, *Change types*, *Certificate and secret rotation*) still use `RAG_*_IMAGE`, `rag-postgres`,
     `ume_rag_*`; `docs/runbook.md:494` defaults to `ume-rag_rag-upload-copy`; `site/build.mjs:229` and
     `site/content.mjs:73` show the `ume` member; `src/parser/LICENSES.md:4` (and `scripts/licences.py:31`) say
     EUPL-1.2.
   - **The upgrade note** (`changes/rename-kalla.upgrade.md`): the host state directory `/var/lib/ume-rag` →
     `/var/lib/kalla` (textfile collector, `backup-last-run`), the security log category `Ume.Rag.Security` →
     `Kalla.Security` (an operator's SIEM filter), the repository variables and secrets the workflows now read as
     `KALLA_*` (`vars.KALLA_TEST_HOST_*`, `KALLA_R1_*`, `secrets.KALLA_R1_API_KEY`).
   - **Smaller:** the Portainer copy writes `secrets/.pepper` first and treats it as "done", so a copy that fails
     after the secrets is never resumed (copy the pepper last); `upgrade-to-0.9.sh` takes `s3://EP/BUCKET/` (a
     trailing slash) as having a path.
2. **The image bumps the Audit asks for** (its first run on 0.8.0's images, 2026-11-20 12:59 UTC, [run
   44740311868](https://github.com/thekim1/kalla/actions/runs/44740311868), red): **parser** 180 findings, LibreOffice
   deb13u9 → deb13u12 (move `DEBIAN_SNAPSHOT`, 20261109, in `src/parser/Dockerfile`); **worker** 7, Node 24.22.1 in
   Playwright's driver (`Microsoft.Playwright` 1.64.1 → 1.65.1, `Directory.Packages.props`); **postgres** 1, zlib1g
   (a new base digest). The images of 0.9.0 must carry them; none is an exception.
3. **Release `v0.9.0`:** the preparation (*How to*), with `UPGRADE_FROM` and the `--rename-0.9` upgrade boot test
   already in `release.yml`; the Portainer stack is on Fors `v0.12.1` (#903); the image defaults in the compose
   files are still 0.8.0's `ghcr.io/thekim1/ume-rag/*` until the preparation pins 0.9.0's (they do not run with
   the renamed files: nothing should start from `main`'s defaults until then). A dry run with `e2e=true`, then the
   user's go-ahead for the tag. Read the migrations since 0.8.0 for the *Rolling update* line (B-50, B-51, B-53 and
   the rename: *no*).
4. **Then P2 wave 1** (Decisions "P2 starts before go-live"): B-54 to B-59 in stream b's queue, assistants and
   answer quality; B-60 (#893) after the rename, which is now.
5. **Close #878** (every leftover is merged: A-37 #891, A-38 #892, B-53 #896; the admin part is B-60) and **#894**
   (its table is done) with an evidence comment.

### Waiting on the user

- **The VM:** runs 0.5.0 (Ume.Rag) with its gateway. **Wait for `v0.9.0`** (on Fors 0.12.1; never Fors 0.12.0, whose
  upgrade lost the provider credentials, thekim1/fors#116), then one update by `docs/portainer.md`, *Upgrade*, plus
  0.9.0's rename steps (rename the variables, update with *Prune services*, rename the roles in both Postgres,
  deploy again). The 0.5.x steps still apply (the keys' residencies, the hand-made Open WebUI client, role groups;
  step 1 backs up the three files, the last step makes the first escrow set). Pulls that say "denied": `docker
  logout ghcr.io`. Portainer's own *Update the stack* has never been run on an upgraded stack, so this is the step
  with the most to learn. **0.5.1's admin image is gone from ghcr.io** (found 2026-11-18; who deleted it is not
  known): the VM has its 0.5.0 images locally, a host that must pull them again cannot.
- **The live block** (the critical path to go-live, `plans/roadmap.md`): every item waits on an input, A-L1 (the
  umea.se inventory) first.
- **The support-chatbot proposal** (`plans/proposals/support-chatbot-features.md`): F3, F4 and F8 are built (F8 as
  B-52, #898). The rest is the user's pick.
- **P2's later waves** (the roadmap's *P2 Broaden*): the user picks the next theme when wave 1 is nearly done (tools
  and MCP, ingestion breadth, the rest of assistants).
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
  a reason, or drop the dependency. **opencv-python-headless 5.1.0.95** came out 2026-11-17 (the pin is 4.14.0.94,
  the newest 4.x); the parser's uv cutoff (`exclude-newer = "7 days"`) lets it in from about 2026-11-25: try it
  (Docling needs only `cv2.resize`, `cv2.dilate` and border constants) and see whether its wheel bundles a fixed
  FFmpeg. picomatch had no fix on 2026-11-20 (4.0.7 is the newest).
- **#781, the flaky "search unavailable" answer in `ChatApiTests`:** open until 2026-11-30; if it recurs, the job's log
  has *The query embedding of profile … is unavailable (…)*, which names the cause.
- **`IntegrationReadbackTests.A_keys_usage_per_step_…` failed once** on #814 (the fake gateway answered 401,
  `scope_missing`). Seen once; no issue filed.
- **Flaky under load** (session 47): with many pull requests' checks on the two runners, `backend` failed with tests
  the change didn't touch (`ChatApiTests`, `RenderTests.The_real_renderer_…`); run alone they pass.
- **`gh pr edit` fails** on this repository with GitHub's *Projects (classic) is being deprecated* error and changes
  nothing; edit a pull request's body with `gh api -X PATCH repos/thekim1/kalla/pulls/<n> -F body=@file`.
- **Tests that share the job queue:** the nightly purge re-queues `delete-source` for every deleted source with live
  content (#884), so a test that queues a command for a deleted source can lose its lease (#897 fixed A-38's test
  by soft-deleting directly). The same for a new test of that kind.
- **The demo seed's slug in a test:** `openwebui.spec.ts` leaves out the assistant named `demo-assistent` (#882); a
  change of `DemoSeeder`'s slug brings its old failure back.

## What session 50 did (2026-11-20)

On `~/research/rag`, as the main session (and stream a), beside `thekim1`'s machine.

- **The user's decisions:** the account is `ume-nichlas`; **P2 starts before go-live**, once 0.9.0 is out, with
  wave 1 *assistants and answer quality* (#889: Decisions "P2 starts before go-live", the roadmap's *P2 Broaden*,
  the phase review in `postponed-decisions.md`, B-54 to B-59 queued; three seeded assistant templates and the
  100-question cap of the nightly answer run written into the designs); **Decisions "Product names"**: Källa
  (`Kalla`) and Fors, the wire names too (`kalla.*`, `x-fors-*`, `/v1/fors/context`, `fors.*`; #895), **rename
  first, then 0.9.0**; #893 after the rename (B-60).
- **A-38's design question** answered in processing §13.3 (#889): a question whose expected document is gone is left
  out of every run and counted as `documentGone`; a `human` one turns `stale`.
- **Built (agents in worktrees, reviewed, committed by the session):** #874's three points (#890); **A-37** (#891:
  one nightly `DocumentPurge` for every soft-deleted document past its 14 days; the raw blob tombstoned only when no
  other document of the level has the content); **A-38** (#892, plus an edit of a stale question making it active;
  its test raced the queue on CI, fixed in #897); #893 filed for the admin's part.
- **Reviewed:** #896 (B-53, good; as a comment); **#902, the rename** (with a second agent's sweep): eleven findings,
  of which `thekim1` fixed the three that lost data (Data Protection's names and the blob wrap, kept as
  `StoredFormatNames`) and the red `deploy-checks` before merging; the rest is *Start here* 1. Found the same
  Data Protection fault in **Fors 0.12.0** (thekim1/fors#116): fixed in Fors 0.12.1 (#117), the stack rebuilt on it
  (#903).
- **The Audit's first run on 0.8.0's images** (red: *Start here* 2).
- **Housekeeping:** the agent worktrees and their branches removed; the local remote points at `thekim1/kalla`.

## Known gaps, documented, not fixed

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
  tag at `faa001dd`, as Ume.Rag (`ghcr.io/thekim1/ume-rag/*`, EUPL-1.2).
- **What a deployer types in Portainer:** 1 value since 0.7.0 (`FORS_DOMAIN` since the rename, no secret).
- **On `main` after `faa001dd`, unreleased** (notes in `changes/`): source dates on citations (B-47), the escalation
  hand-off (B-48, B-49), topics per request (B-52), deletes of knowledge bases, sources and assistants with their
  purges (B-50, B-51, A-36, A-37, B-53), evaluation questions whose documents are gone (A-38), the demo seed (#877),
  kalla-admin without a domain (#865), #874's fixes, the website, **and the rename** (#902: `Kalla.*`, `KALLA_*`,
  `kalla-*` services and volumes, `kalla_*` roles, `ghcr.io/thekim1/kalla/*`, Apache-2.0 from 0.9.0; old
  `ume-rag-sk-` keys stay valid; `rag-*` host names, metrics, role groups and credential keys stay; upgrade paths
  in `deploy/upgrade/` and the bootstrap) on **Fors 0.12.1** (#903).
- **Queues:** stream a: the live block (A-L1 to A-L10), A-34 and A-35 with only their live checks open. Stream b:
  B-54 to B-60 (P2 wave 1 and #893), B-42 to B-46 (the gateway phases' live checks) and its live items. Main: M-4,
  M-16, M-17.
- **Open issues that matter:** #878 and #894 (to close, *Start here* 5), #893 (B-60), #781 (watch), #667 (M-16),
  #333, #511, #580 and #550. In Fors: #112 and #113 (from the quick start run). No open pull request.
- **Dependabot works:** grouped pull requests (#841 to #844); `.github/dependabot.yml`'s header lists where a pin is
  repeated.
- **The user's machine (`~/research/rag`):** the main clone on `main` (remote `thekim1/kalla`) and
  `~/research/rag-small` (a second checkout, on the old `main-session/handover-50`). Docker works without sudo;
  there is no sudo. Volumes `kalla-m1-postgres` (named before the rename; development data, kept),
  `kalla-openwebui-data` and `kalla-clamav-db`. **Docker's default address pools are full** (about 25 unused
  networks of other projects): a new Compose project needs explicit subnets, or a `docker network prune` by the
  user. The machine's name is upper case and does not resolve inside containers. Neither `gh` token has
  `read:packages`. `actionlint` is at `/snap/bin/actionlint` (it hung once without `-shellcheck= -pyflakes=`);
  `shellcheck` and `pwsh` are not installed; `grep` is ugrep. Playwright's Chromium and Firefox are in
  `~/.cache/ms-playwright`. The Aspire CLI is at
  `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire` (not on PATH).
- **`thekim1`'s machine:** a fresh clone after the rename; its development data starts empty under `~/.kalla`.
- **Fors on the user's machine:** `~/git/llmgateway` (and the worktrees `-100`, `-100-deploy`, `-101`) are clones of
  the renamed `thekim1/fors` on merged branches; fetched through `v0.12.0`. They can go, or move to `main`. Merges and
  releases in Fors are the user's; its rules: draft pull requests (drafts run no CI), checks run locally first,
  squash-merge. Ports 5000 and 5173 are taken here (`ASPNETCORE_URLS=http://127.0.0.1:0` for its IntegrationTests).
