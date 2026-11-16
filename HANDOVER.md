# Handover

State of the Ume.Rag work for the next session, rewritten each session (earlier ones: `git log -- HANDOVER.md`).
**Last release `v0.6.0`** (2026-11-15); the user's VM runs 0.5.0 and the user is to upgrade it to 0.6.0. `main` is well
past 0.6.0: the whole setup wizard (M-20, both stages, on the gateway's `v0.11.0`) is on it, shown by a release dry
run with the browser tests, unreleased. Repo `~/research/rag` on
this machine (`~/git/ume-rag-platform` on the session-19 machine), branch `main`, remote `origin` =
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
   `plans/archive/work/`), results in `results-a.md` and `results-b.md`.
5. The design for the area at hand (map in `plans/README.md`). History is in `plans/archive/` and doesn't bind.

## Start here (session 45): the setup wizard is done; a release is the next step

**M-20 is ticked** (#786, 2026-11-16): a deployer types one value, `UME_DOMAIN`, and no secret, and sets sign-in in two
setup wizards, the gateway's first. `main` carries it on the gateway's `v0.11.0`, unreleased.

### 1. What stage 2 is, and what is not shown

- **Built in session 44:** `stack.env.example` has 1 CHANGE value; `build-stack.sh` rewrites the gateway's one
  `UME_INTEGRATION_MANIFEST` line to a default (`deploy/portainer/integration-manifest.json`, no owner; unset only);
  all five host names come from `UME_DOMAIN`; rag-admin's default address of the gateway's admin; the setup state reads
  the gateway's context again when it would suggest no address, and the wizard page reads the state again when its tab
  is shown again (identity §19); the deploy tests; the operators' docs. The three open points were decided by the user
  (Decisions "Setup wizard", the stage 2 points).
- **Shown:** release dry run [43857593918](https://github.com/thekim1/ume-rag-platform/actions/runs/43857593918) from
  the branch with `e2e=true`, all green: the boot test with no sign-in value set (the gateway's wizard first; the JSON
  default on the runner's Compose), `e2e.yml` through the wizard. The pull request's own checks were green (backend:
  3696 tests).
- **Not shown:** Portainer itself; both wizards completed in one deployed stack against a real provider (the boot test
  has none, so it stops at each wizard's state; the suggestion's re-read is covered by unit tests and specs only); a
  boot of an upgraded 0.6.0 stack; Open WebUI starting with OIDC in the Portainer stack (accepted: `e2e.yml` shows it).
- **Known:** a default with braces needs Compose 2.24.7 or later (older ones cut the value without an error);
  Portainer pins a later one from 2.21 (read, not run). A stack without `UME_INTEGRATION_MANIFEST` (0.5.x style) gets
  the default: three unused keys unless the variable is set empty first (`docs/portainer.md`, *Upgrade from 0.6.0*).
- **Next:** a release with the wizard (0.7.0) when the user wants it; `main` is ready for a tag (the dry run above was
  on the merged content). Then the user's VM: from 0.5.0 through 0.6.0 by the guides.

### 2. The gateway's pull requests (the main session may not merge in the gateway's repository)

Built in sessions 42 and 43 on the user's word ("build it", 2026-11-16), reviewed by thekim1 on 2026-11-16.

- **#102** (WG1 `UME_DOMAIN` for the gateway's hosts, WG2 `gateway.oidc_authority` in `/v1/ume/context`): **merged**
  2026-11-16.
- **#103** (WG3, setup mode in the gateway's admin): **merged** 2026-11-16 (`70122b4`). **Open with the
  reviewer:** on Kubernetes a deployment that declares `Oidc__Authority` must set `Gateway__OidcAuthority` too
  (documented), or the stored authority is cleared in code instead (not done; asked). Backend tests pass (AdminApi 440,
  Domain 599, DataApi 29, IntegrationTests 64, Gateway 491); the browser test and the UI gate were not re-run after
  the fixes. **A gateway deployment that sets `UME_OIDC_AUTHORITY` never sees the wizard and needs nothing new.**
- **#104** (WG4, closes #101: `integration.owner` optional in a manifest, with a default owner team and department
  made from the name; an integration moves to another team with its credential keys; the Ume.Rag preset names no
  owner): **merged** 2026-11-16.
- **Released as `v0.11.0`** (2026-11-16; release preparation #107, approved by the main session as `ume-nichlas`,
  merged and tagged by the user).
- **A flaky gateway test, not caused by these:** `ClientAddressTests.Forwarded_address_is_used_only_through_a_trusted_proxy`
  failed in 2 of 8 local runs of `Gateway.Tests` (it writes the shared settings row, as `AccountingTests` does). Its
  rate on the gateway's `main` was not measured. If a CI run fails on it, re-run the job.

### What session 44 did (2026-11-16)

- **#786, a draft:** Ume.Rag's side of stage 2 (above), built with four agents in parallel (Compose versions, deploy
  tests, docs, the context re-read); each diff read and its tests re-run before it went on the branch.

### What session 43 did (2026-11-16)

- **Gateway:** WG4 checked and opened as #104; the reviews of #103 and #104 answered (above); #103 retargeted.
- **#784, merged: rag-admin's sign-in paths are bounded for anonymous callers.** Ume.Rag had the flaw the gateway's
  reviewer found, and `/bff/user` read the settings on every call too. Now `/bff/login`, `/bff/user`, `/bff/logout`
  and the callback read the sign-in values at most once every 2 seconds per instance (`AdminSignIn.RefreshInterval`;
  `complete` reads at once on its own instance), a setup test's callback has the same two limits as the gateway's, and
  `complete` compares what the test's result rested on (identity design §19, *Reload* and *Anonymous callbacks of a
  setup test*). **The browser tests were not run on it: run `e2e.yml` before the next release.** A test that changes
  the sign-in values behind rag-admin's back and calls again needs a `FakeTimeProvider` moved on by that interval.
- **#783, merged: deployment values across releases (M-21, ticked).** The user asked what happens when deployment
  variables come and go. Decisions "Deployment values across releases" and `AGENTS.md`, *Conventions*: a new variable
  has a default, is generated, or is a setting; a required one without a default needs the user's approval; a variable
  that is no longer read goes on `rag-bootstrap`'s retired list (`retired_hint` in `deploy/portainer/bootstrap.sh`, its
  name in `UME_RAG_RETIRED_SET` in the stack template). `rag-bootstrap` names a retired variable a stack still sets on
  its checklist, and Home shows platform admins a deployment notice (`GET /api/overview`, `deploymentNotices`). The
  notice was not looked at in a browser.
- **Session 42** (same day): the gateway's WG1–WG3 as pull requests and WG4 built; an independent security review of
  setup mode with its fixes in both repositories (#778); the upgrade guides step by step (#777); the wizard's
  suggestion from the gateway (#779); #775, #776. Details: `git log -- HANDOVER.md`.

### Known gaps, documented, not fixed

- **Not tested in either repository: a boot of an upgraded stack** (the previous release's volumes under the new
  images). The guides are backed by rendering the stack files and by first-run boots.
- **Not tested anywhere:** Entra ID and AD FS (the test sign-in, the Open WebUI client check), a real untrusted TLS
  certificate, Portainer itself, Kubernetes beyond rendering.
- **Compose's host firewall** (Ume.Rag) lets rag-admin reach only the provider of `--oidc-authority`; without it the
  wizard's test can't reach a provider (the docs say to keep the option). An allow-list would be the fix. The gateway's
  deploy files pin no provider host; an operator's own egress firewall must allow the provider.
- **The gateway's Data API does not follow the wizard:** a gateway whose sign-in is set in the wizard sets
  `UME_DATA_AUTHORITY` for access tokens.
- `GET /internal/openwebui/sign-in` is closed at the Portainer proxy; in Compose only the managed client's key opens
  it (Open WebUI calls rag-api by its public address there).
- **CI:** what `AdminApi.Tests`' collections wait for inside one process is not found; attaching a profiler
  (`dotnet-stack`) was stopped by the permission check. The user can allow it, or run it by hand.

### How to try the wizards locally (each time on a throwaway volume)

- **Ume.Rag:** `export FakeGateway__Enabled=true Seed__Enabled=false Parser__Mode=off Render__Mode=off
  OpenWebUi__Enabled=false ClamAv__Mode=off DevelopmentVolumes__Postgres=<a throwaway name> Setup__Try=true`, `aspire
  start …`, `aspire wait admin-ui`; the code is in `aspire logs adminapi | grep 'Setup code:'`; the provider is
  `https://localhost:7411/realms/ume`, the clients `ume-rag-admin` and `open-webui-dev` (secrets: the AppHost
  parameters `oidc-client-secret`, `openwebui-client-secret`), the user `rag-platform-admin`. Afterwards `aspire stop
  --all`, wait, `docker volume rm`. Or run the spec: `tests/e2e/README.md`. On a FakeGateway stack the sign-in step now
  shows the note that the gateway's sign-in is not set up yet (the fake announces the feature with no address).
- **Gateway:** in the worktree, `Setup__Try=true Seed__Enabled=false DataApi__Enabled=false
  DevelopmentVolumes__Postgres=<a throwaway name>`, `aspire start --apphost
  src/Ume.LlmGateway.AppHost/Ume.LlmGateway.AppHost.csproj`, then `npx playwright test specs/setup.spec.ts` in
  `tests/e2e` (the gateway's `docs/local-development.md`, *Trying setup mode*). On this machine port 5173 is taken: set
  `ADMIN_UI_URL` to the port `aspire logs admin-ui` names and add that address's `/signin-oidc` to the `ume-admin`
  client in the bundled Keycloak (`kcadm.sh` in the container, with the container's own bootstrap credentials).

### Also open (not this work)

- **A release with the wizard** (0.7.0): both stages are on `main`; C13 (the M-16 session, #667) was planned for
  0.6.1.
- **The user's upgrade to 0.6.0** (`docs/portainer.md`, *Upgrade from 0.5.x to 0.6.0*). Before it, rag-api's and
  rag-admin's credential keys get `OnPrem` and `Eu`, and each published assistant needs a gateway key that reaches both
  its chat model and its embedding model. Pulls that say "denied": `docker logout ghcr.io`.
- **The support-chatbot proposal** (`plans/proposals/support-chatbot-features.md`): the user picks; the suggestion is
  F3, F4, F8 and F10.
- **The next tag run checks for the first time:** `:latest` moves in `publish` (#756); verify and parser run in
  full on a tag (#755).
- **For the user:** *Allow GitHub Actions to create and approve pull requests* is still off (Settings → Actions →
  General), so `release.yml` couldn't open the pin PR (#754 was opened by hand).
- **Checks before go-live:** the 16 *Before go-live* rows of `plans/to-verify.md` need no input.

## State

- **Release:** `v0.6.0` published 2026-11-15 ([run 43720085132](https://github.com/thekim1/ume-rag-platform/actions/runs/43720085132)),
  tag at `d5029e29`; `deploy/portainer/` on `main` is ahead of it (the wizard's stack file: don't hand it to a 0.6.0
  deployment; the `v0.6.0` release asset is the file for 0.6.0).
- **What a deployer types in Portainer:** 9 values at 0.6.0 (two secrets) → 1 on `main` (`UME_DOMAIN`, no secret).
- **New on `main`, unreleased:** migrations `IdentityOidcSettings`, `SetupMode`, `OpenWebUiSignInSettings` (additive);
  security events 6008–6013 and 5001 `callback_failed`; `GET /api/settings/sign-in`, `GET/PUT
  /api/settings/openwebui`, `/bff/setup/*` (its state now with `gateway`), rag-api's `GET
  /internal/openwebui/sign-in`; `/bff/user` gains `signIn.configured` and `setup.open`; `GET /api/overview` gains
  `secretExpiry`; `UME_DOMAIN`, `UME_RAG_SETUP_REOPEN`, `Setup__Secret`; `UME_RAG_OIDC_REFUSE_GUESTS` reaches rag-admin
  as a file (an empty boolean stops it at start); Open WebUI starts through `deploy/openwebui/start-with-sign-in.py`;
  `scripts/ci/backend-tests.sh`; `deploy/tests/portainer-upgrade.sh`.
- **Gateway, on this machine:** `gh` is signed in as `ume-nichlas` (it can push to both repositories; `Crotalus` is
  read-only on the gateway). Worktrees: `~/git/llmgateway` (the branch of #102, merged), `~/git/llmgateway-100` (the branch of
  #103, with `node_modules` in `src/admin-ui` and `tests/e2e`), `~/git/llmgateway-100-deploy` (merged into #103's
  branch; can go), `~/git/llmgateway-101` (WG4, the branch of #104; behind its remote by thekim1's merge of `main`). Local images `ume-llm-gateway/*:setup-test`.
  Something else holds ports 5000 and 5173 here: the gateway's IntegrationTests need
  `ASPNETCORE_URLS=http://127.0.0.1:0`. The Aspire CLI is at
  `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire` (not on PATH). The gateway's own rules:
  draft pull requests (drafts run no CI; Actions minutes are limited), the checks run locally first, squash-merge.
- **Ume.Rag local stack:** stopped; no worktree besides the main clone. The scratch directory of session 44 holds
  883 MB of Compose binaries (`compose-check/bin`), which can go.
- **Open proposals for the user:** `plans/proposals/support-chatbot-features.md`.
- **U1 (pictures in uploads)** waits for DoIT's files at T6 (decided 2026-11-15).
- **The Eneo report PDF** is only at `/mnt/c/temp/eneo-vs-ume-rag.pdf`; the user hasn't said whether to keep it in the
  repo.
- **Tools here:** `actionlint` is installed (`/snap/bin/actionlint`; it hung once without `-shellcheck= -pyflakes=`);
  `shellcheck` and `pwsh` are not; `grep` is ugrep (handbook, *Environment notes*). The local `gh` token can't read
  ghcr packages: live stack tests run on the runner.

## Sessions 41 to 43 (2026-11-15 and 16)

- **41:** the release dry run for the wizard, green on the first run; the four gateway issues (#98–#101); CI time:
  five measurements of `Ume.Rag.AdminApi.Tests` (collections, thread pool, `fsync`, GC, two processes), then #775.
- **42:** the gateway's WG1–WG3 as pull requests and WG4 built; the security review and its fixes in both
  repositories; the upgrade guides; the wizard's suggestion from the gateway; #776–#779.
- **43:** the gateway's #104 opened and both reviews answered; M-21 (#783); rag-admin's sign-in bounds (#784).
