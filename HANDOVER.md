# Handover

State of the Ume.Rag work for the next session, rewritten each session (earlier ones: `git log -- HANDOVER.md`).
**Last release `v0.8.0`** (2026-11-19, the deploy simplification; tag on #856's merge commit `faa001dd`, on the
gateway's `v0.11.0`). `main` after it has a release's worth of work (*State*). The user's VM runs 0.5.0. Two
machines work on it: `~/research/rag` (the user's, where session 49 ran) and `~/git/ume-rag-platform` (`thekim1`'s,
stream b and often stream a). Branch `main`, remote `origin` = https://github.com/thekim1/ume-rag-platform (HTTPS
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

## Start here (session 50)

**The rules of the repository:**
- **`main` is protected** ("Protect main": a pull request with one approving review and the required checks; an
  author cannot approve their own, never `gh pr merge --admin` as `ume-nichlas`). Everything, `plans/**` and this
  file included, goes through a `main-session/` branch; `thekim1` reviews the main session's pull requests.
  `thekim1` merges its own green pull requests with `--admin`, which its review of #861 says the user allowed on
  2026-11-19; #877 was merged before the main session's review was read, and the review was then answered in a
  follow-up (#879).
- **Release tags are protected** ("Protect release tags", `refs/tags/v*`: creation, update and deletion):
  `ume-nichlas` cannot push one (GH013), and no workflow creates one. `thekim1` pushed `v0.8.0`; the next tag is
  `thekim1`'s or the user's, on the release preparation's merge commit.
- **On the user's machine the session cannot approve a pull request:** `gh pr review --approve` on #882 was stopped
  by Claude Code's permission check. Review as a comment and say so; the approval is the user's or `thekim1`'s.

**Nothing is open:** no pull request, and no item of the main session waits on anything but the user. In order:

1. **The daily Audit has not run since the release.** Its last run (2026-11-19 13:18 UTC) failed on 0.7.0's images,
   as every run since #819. The schedule says 04:17 UTC, the runs come around 13:00. The first run after
   2026-11-19 20:55 UTC scans 0.8.0's images and should be green; if it is not, its findings are the work (#818's
   way: update, or a dated exception in `scripts/audit/image-scan-exceptions.txt`).
2. **The next release (0.9.0) has its content** (*State*): the support chatbot's F3 and F4, deletes, the demo seed,
   the host-only Compose fix. It needs the preparation, a dry run with `e2e=true` (the browser tests ran on
   `thekim1`'s machine after the demo seed, #882, not in CI), and the user's go-ahead for the tag.
3. **Open queue items without an input:** stream b's **B-52** (`ume.topics`, F8) and **B-53** (the purge of deleted
   sources' and knowledge bases' rows); stream a's **A-37** (the 14-day purge of documents removed from a living
   source, designed and never built) and **A-38** (evaluation questions whose expected documents are gone, which
   starts with what the design says). `thekim1`'s machine has been taking both streams' items; say on the issue
   before starting one.
4. **#874**, the quick start's three smaller findings (stream a's paths): the bootstrap's wizard address has no
   port, the setup script's bare error without root, the quick start does not say the cost read-back is off.
5. **The gateway's repository:** thekim1/llmgateway#112 (`realm-import` cannot read its trust store after
   `init-keycloak.sh`, and `up --wait` reports success) and #113 (the release bundle has no `docs/`), both from the
   quick start run; the wildcard DNS advice of Decisions "Host names" is not in the gateway's guides yet (a pull
   request there; merges are the user's).

### Waiting on the user

- **The VM:** runs 0.5.0. It moves to 0.8.0 in one update by `docs/portainer.md`, *Upgrade* (steps 2 to 4 are the
  0.5.x ones: the keys' residencies, the hand-made Open WebUI client, role groups); step 1 backs up the three files
  (a 0.7.x or earlier stack has no escrow set), step 15 makes the first escrow set. Pulls that say "denied":
  `docker logout ghcr.io`. Portainer's own *Update the stack* has never been run on an upgraded stack, and the
  one-step upgrade only in CI, so this is the step with the most to learn. **0.5.1's admin image is gone from
  ghcr.io** (found 2026-11-18; who deleted it and whether other 0.5.x images went is not known; the local `gh`
  tokens have no `read:packages`): the VM has its 0.5.0 images locally, a host that must pull them again cannot.
- **The live block** (the critical path to go-live, `plans/roadmap.md`): every item waits on an input, A-L1 (the
  umea.se inventory) first.
- **The support-chatbot proposal** (`plans/proposals/support-chatbot-features.md`): F3 and F4 are built, F8 is
  designed and queued (the user picked F8 only, 2026-11-20). The rest is the user's pick when F8 is in.
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
  a reason, or drop the dependency.
- **#781, the flaky "search unavailable" answer in `ChatApiTests`:** open until 2026-11-30; if it recurs, the job's log
  has *The query embedding of profile … is unavailable (…)*, which names the cause.
- **`IntegrationReadbackTests.A_keys_usage_per_step_…` failed once** on #814 (the fake gateway answered 401,
  `scope_missing`). Seen once; no issue filed.
- **Flaky under load** (session 47): with many pull requests' checks on the two runners, `backend` failed with tests
  the change didn't touch (`ChatApiTests`, `RenderTests.The_real_renderer_…`); run alone they pass.
- **`gh pr edit` fails** on this repository with GitHub's *Projects (classic) is being deprecated* error and changes
  nothing; edit a pull request's body with `gh api -X PATCH repos/thekim1/ume-rag-platform/pulls/<n> -F body=@file`.
- **The demo seed's slug in a test:** `openwebui.spec.ts` leaves out the assistant named `demo-assistent` (#882); a
  change of `DemoSeeder`'s slug brings its old failure back.

## What session 49 did (2026-11-19 to 20)

On `~/research/rag`, as the main session, beside `thekim1`'s machine building stream b's and stream a's items.

- **`v0.8.0` released** (2026-11-19): #861's handover reviewed and corrected, the tag pushed by `thekim1` after the
  ruleset refused `ume-nichlas`, the pin #863; #864 brought the README, the roadmap and two guides' stale lines up
  to the release.
- **#858's open ends** (#862) with the user's decision that the unavailable text ends with the assistant's links,
  else the platform's (B-49, #866).
- **The Compose quick start run end to end** (gateway 0.11.0 and Ume.Rag 0.8.0 as two Compose projects on one host,
  no proxy, the gateway bundle's Keycloak as provider). Passed: the gateway's install and wizard, the manifest's
  three keys, Ume.Rag's install and wizard (the provider suggested from the gateway), sign-in, *Models*, the backup
  stanza, `escrow-keys.sh` with `--verify`. Not run: a provider and models, so nothing from the embedding profile
  on; `deploy/host/install.sh` and the hardening (no root). Not the guide as written: Ume.Rag's images were built
  from the `v0.8.0` tag (no ghcr login; the bundle's `RAG_*_IMAGE` variables took them), root-only steps ran in
  containers, both projects got a host entry and Ume.Rag's networks explicit subnets. Found: **#865** (rag-admin did
  not start without a domain or a gateway admin address; fixed in #867, tested in #872), **#874**, and the
  gateway's **#112** and **#113**.
- **How easy the setup is** (asked by the user; read in both repositories): Portainer is close to one typed value;
  what is left is sign-in (two hand-made clients, two wizards, two codes from logs), DNS and TLS before any wizard,
  and hand work after "healthy" that no page asks for (the gateway's provider and models, moving the integration,
  assigned keys per department: the gateway's #108 and #109 would take most of it).
- **Decisions "Host names"** (#875): each service keeps a host name of its own; one wildcard record and certificate
  for a sub-domain of the installation's own; one name with ports for a test host; paths under one name not built
  (the roadmap's open calls).
- **Decisions "Deleting knowledge bases, sources and assistants"** (#880, for #878; six questions to the user):
  built the same day on `thekim1`'s machine as B-50 (#883), A-36 (#884, #886) and B-51 (#885). Reviewed #884: the
  nightly step now also takes sources that were already deleted. A deleted source was never searchable (the API's
  snapshot leaves it out); what was missing was the cleanup.
- **Decisions "Topics per request"** (#881): F8's design, B-52.
- **M-22 closed** (user, 2026-11-20): its S8 is a standing rule in `plans/work/main.md`.
- **#873, the dev seed's demo content:** built on `thekim1`'s machine as designed (#877, #879) while the user was
  being asked which way to go; reviewed, and the browser tests then run there (#882).
- **Housekeeping on the user's machine:** seven agent worktrees and 29 merged local branches removed.

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
- **The Compose quick start (`docs/compose-quick-start.md`) was run once, by hand, not as written** (*What session
  49 did*): with locally built images, without root, and without a provider, so its steps 5.2 to 5.4, the host timers
  and the hardening are still only read. No test runs the two products' Compose projects together; the release
  smoke test boots `compose.rag.yaml` with the backup overlay against the fake gateway (since #872 also in the
  host-only form).
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

- **Release:** `v0.8.0` published 2026-11-19 ([run 44609831965](https://github.com/thekim1/ume-rag-platform/actions/runs/44609831965)),
  tag at `faa001dd`; `deploy/portainer/` on `main` pins its images (#863). Migrations `DropMessageParentId` (stops
  the old rag-api and rag-admin) and `ContinueRuns` (additive); *Rolling update: no*.
- **What a deployer types in Portainer:** 1 value since 0.7.0 (`UME_DOMAIN`, no secret).
- **On `main` after `faa001dd`, unreleased** (notes in `changes/`, 13 files): source dates on citations (B-47) and
  the escalation hand-off `ume.escalation` (B-48), with the unavailable text's links (B-49); delete a knowledge
  base, a source (B-50, A-36: the worker's `delete-source` and the nightly purge) and an assistant (B-51), names
  free at once; the dev seed's demo knowledge base and *Demo-assistent* (#877); rag-admin starting without a domain
  (#865); the Geist fonts (#871); the wildcard guidance. Also the website (GitHub Pages, #868, #870). **Migrations
  since the release are not listed here:** B-50 and B-51 change the slug indexes and add `Assistants.DeletedAt`;
  read them at the release preparation for the *Rolling update* line.
- **Queues:** stream a: A-37, A-38, then the live block (A-L1 to A-L10) and A-34 and A-35 with only their live
  checks open. Stream b: B-52, B-53, then B-42 to B-46 (the gateway phases) and its live items. Main: M-4 (the
  live block), M-16, M-17.
- **Open issues that matter:** #878 (deletes: the three leftovers are A-37, A-38 and B-53), #874, #781 (watch),
  #667 (M-16, `thekim1`'s machine), #333 (the exact-scan and recall checks on the test host), #511 (splitting the
  worker), #580 and #550 (decisions to record). No pull request is open.
- **Dependabot works:** its first grouped pull requests merged (#841 to #844); `.github/dependabot.yml`'s header lists
  where a pin is repeated.
- **The user's machine (`~/research/rag`):** the main clone on `main` and `~/research/rag-small` (the second
  checkout, on whatever branch was last worked on); no other worktree. Docker works without sudo (the `docker`
  group); there is no sudo. Volumes `ume-rag-m1-postgres`, `ume-rag-openwebui-data` and `ume-rag-clamav-db` are the
  dev data and are kept. The quick start's test installation is torn down; the gateway's 0.11.0 images stay in the
  local Docker. **Docker's default address pools are full there** (about 25 unused networks of other projects): a
  new Compose project needs explicit subnets, or a `docker network prune` by the user. The machine's name is upper
  case and does not resolve inside containers. `gh` has two accounts, `ume-nichlas` (active) and `Crotalus` (the
  user's own; not to be switched to without being asked); neither token has `read:packages`, so Ume.Rag's images
  cannot be pulled there. `actionlint` is installed (`/snap/bin/actionlint`; it hung once without `-shellcheck=
  -pyflakes=`); `shellcheck` and `pwsh` are not; `grep` is ugrep (handbook, *Environment notes*). Playwright's
  Chromium and Firefox are in `~/.cache/ms-playwright`; Edge needs sudo to install. The Aspire CLI is at
  `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire` (not on PATH).
- **`thekim1`'s machine (`~/git/ume-rag-platform`):** no extra worktrees; Docker is not in its WSL distro, so stack
  and image tests run only in CI there; `shellcheck` and `actionlint` are not installed.
- **The gateway on the user's machine:** `gh` as `ume-nichlas` can push to both repositories; merges and releases
  in the gateway are the user's. Worktrees `~/git/llmgateway` (on `feat/98-99-domain-and-context-authority`),
  `~/git/llmgateway-100`, `~/git/llmgateway-100-deploy` and `~/git/llmgateway-101` are on branches whose pull
  requests are all merged: they can go, or `~/git/llmgateway` can move to `main` (read `origin/main` with `git
  show` until then). Local images `ume-llm-gateway/*:setup-test` predate the release and can go. Ports 5000 and
  5173 are held by something else: the gateway's IntegrationTests need `ASPNETCORE_URLS=http://127.0.0.1:0`. The
  gateway's own rules: draft pull requests (drafts run no CI), the checks run locally first, squash-merge. Its pull
  request #110 (a benchmark against Bifrost) is open.
