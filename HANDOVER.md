# Handover

State of the Ume.Rag work for the next session, rewritten each session (earlier ones: `git log -- HANDOVER.md`).
**Last release `v0.6.0`** (2026-11-15); the user's VM runs 0.5.0 and the user is to upgrade it to 0.6.0. `main` is well
past 0.6.0: the setup wizard's stage 1 (M-20) is on it, shown by a release dry run, unreleased. Repo `~/research/rag` on
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

## Start here (session 43): finish the gateway's WG4, then wait for the user's merges and a gateway release

The setup wizard (M-20) has two stages. **Stage 1** (Ume.Rag and Open WebUI; a deployer types 5 values and no secret)
is on `main` and shown by a release dry run. **Stage 2** (one typed value, `UME_DOMAIN`) needs the gateway's part,
which session 42 built in the gateway's repository on the user's word ("build it", 2026-11-16).

### 1. First: finish WG4 in the gateway (uncommitted work)

thekim1/llmgateway#101, the integration manifest without an edit. **Built, uncommitted, not pushed**, in the worktree
`~/git/llmgateway-101` (branch `feat/101-manifest-default-owner`, from the gateway's `origin/main`). Session 42 stopped
there because the tool's permission check failed for several calls in a row (a transient fault).

What it is (the user asked for it after an explanation, "ok do it"): an integration cannot exist without an owner
team (every credential key belongs to a team; usage is stored per team and department), so "without an owner" is a
**default owner that can be moved**:
- `integration.owner` is optional in the manifest. Without it a new integration gets a team and a department named
  after it, with a code slugged from the name ("Ume.Rag" → `ume-rag`); the migration log and the import preview say so
  (`defaultOwner` in the result, and a warning). A manifest with an owner behaves as before.
- `PUT /api/integrations/{id}` with another `ownerTeamId` no longer answers 409 while the integration has credential
  keys: its credential keys that are not revoked move with it, each audited; revoked and assigned keys stay; recorded
  usage keeps its team. **Added by the main session:** a move to an inactive team, or a team in an inactive department,
  is refused (400 on `ownerTeamId`), with a test and the docs.
- The Ume.Rag preset (`deploy/integration-presets/ume-rag.json`) no longer names an owner.

To do, in order:
1. **Re-run `Ume.LlmGateway.Gateway.Tests` a few times.** One run gave 486 of 487:
   `ClientAddressTests.Forwarded_address_is_used_only_through_a_trusted_proxy` failed once. It writes the shared
   settings row (trusted proxies) and WG4 doesn't touch that area; the implementor's two earlier runs passed. Decide:
   an existing flake (say so in the PR) or an effect of the two new Gateway tests (`HotPathTests`,
   `IntegrationGatewayTests`), which publish key invalidations.
2. Add to `CHANGELOG.md`'s entry, after "…while the new one counts from the move.": *A move to an inactive team, or to
   a team in an inactive department, is refused (400): the gateway refuses such a team's keys.* (The edit was refused by
   the failing check; `docs/integrations.md` and `docs/admin-api.md` already say it.)
3. Re-run everything: `dotnet build UmeLlmGateway.slnx -c Release --no-incremental --nologo -v:q`; each test project
   with `ASPNETCORE_URLS=http://127.0.0.1:0 dotnet test --project tests/Ume.LlmGateway.<P> -c Release --no-build`
   (last run: AdminApi 376, Domain 599, DataApi 29, IntegrationTests 73 pass); the UI gate in a scratch copy (the
   implementor's is `<scratchpad>/gw-ui-101/ui`, gone with the session: `rsync` the worktree's `src/admin-ui` to a
   scratch directory, `npm ci` there, then lint, typecheck, test, build; 268 tests at the implementor's run).
4. Read the rest of the diff (the main session read the service, the provisioner and the manifest; not yet the UI
   drawers, the tests and the docs in full), commit, push as `ume-nichlas`, open the pull request against `main` (a
   draft first; mark it ready to run CI once), closing #101. Its `CHANGELOG.md` and `docs/upgrade-guide.md` *Unreleased*
   sections will conflict with #102/#103 at merge: keep every entry.
5. Three things an admin should know about a move are in the docs; say them in the PR too: the previous team keeps the
   period's spend and the new one counts from the move; the new team's request policy and routing rules apply to the
   moved keys; an ownerless manifest lands in an existing team that already has the slugged code.

### 2. Waiting for the user (the main session may not merge in the gateway's repository)

- **Merge thekim1/llmgateway#102** (WG1 `UME_DOMAIN` for the gateway's hosts, WG2 `gateway.oidc_authority` in
  `/v1/ume/context`). Green.
- **Mark thekim1/llmgateway#103 ready and merge it** (WG3, setup mode in the gateway's admin). A draft stacked on
  #102's branch: no CI has run on it. Six commits: stored sign-in values (table `SignInSettings`, the client secret
  encrypted), setup mode in the admin API, the security review's fixes, the wizard's pages, its browser test
  (`tests/e2e/specs/setup.spec.ts`), every deploy form with the operators' docs and the upgrade guide. Checked locally:
  Release build clean, 1,618 backend tests, the UI gate (316 tests), the wizard end to end in a browser against the
  bundled Keycloak (5 of 5, with axe on every step), and a boot of the Portainer stack with only `UME_DOMAIN` (code
  printed, a wrong one refused, the printed one accepted, setup closed once the authority is declared).
  **A gateway deployment that sets `UME_OIDC_AUTHORITY` never sees the wizard and needs nothing new.**
- **Then WG4's pull request** (above), and **a gateway release** with all of it.

### 3. After the gateway release: Ume.Rag's side of stage 2 (M-20, S8)

- **Done already (#779):** the wizard offers the gateway's provider address (`gateway.oidc_authority`, feature
  `oidc-authority`) for rag-admin's authority, as a suggestion in `GET /bff/setup/state`
  (`gateway.suggestedAuthority`, never a source of the effective values), and links to the gateway's wizard when the
  gateway has none.
- **To do:**
  - `contracts/gateway/integrations.json` is the gateway's **unreleased** file (llmgateway `7c945d1`): replace it with
    the release's copy.
  - The combined stack on the release (`deploy/portainer/build-stack.sh <version>`); `stack.env.example` loses the
    gateway's four CHANGE values (`UME_OIDC_AUTHORITY`, `UME_GATEWAY_HOST`, `UME_GATEWAY_PUBLIC_URL`,
    `UME_ADMIN_HOST`) and has one, `UME_DOMAIN`.
  - With WG4: the manifest as a default in the stack file, without an owner, and the variable out of
    `stack.env.example`. **Mind upgraders:** a 0.6.0 stack has `UME_INTEGRATION_MANIFEST` with an owner (the variable
    wins, nothing changes); a 0.5.x-style stack with hand-made keys and no manifest would get three new keys from a
    default. Use `${UME_INTEGRATION_MANIFEST-default}` (unset only), so an explicit empty value switches it off, and
    say so in the upgrade steps.
  - The boot test (`deploy/tests/portainer-boot.sh`: the gateway's wizard first, then Ume.Rag's with the suggested
    address), a new fixture for `deploy/tests/portainer-upgrade.sh`, and `docs/portainer.md` (*First deploy* and
    *Upgrade from 0.6.0* gain the gateway's steps; the gateway's own `docs/upgrade-guide.md` has them per deploy form).
  - M-20's done-when: `stack.env.example` has 1 CHANGE value, the release's boot test passes with no sign-in value
    set, `e2e.yml` passes through the wizard. Then tick M-20.

### What session 42 did (2026-11-16)

- **Gateway, in `~/git/llmgateway` as `ume-nichlas`:** #102, #103 and the WG4 work above. Each step by an implementor
  agent in a worktree; the main session read the diffs, re-ran the tests, committed (the agents may not commit) and
  ran the wizard in a browser.
- **An independent security review of the gateway's setup mode** found no way to a session without the code or to a
  secret, and three things that were the same in Ume.Rag, fixed in both (#778 here): the throttle came before the
  code's comparison, so a flood of wrong codes kept the right one out; a made-up callback ended a running test;
  completion trusted that the deployment's values were those of the test. Accepted and documented: the setup code is an
  admin credential for its hour; a code holder can make the admin API call an `https` address and learn whether it
  answered; a restored or emptied database can open setup again on a deployment that doesn't declare its authority.
- **Merged here:** #775 (CI: `AdminApi.Tests` as five processes, backend 7 min 40 s → about 4 min 30 s; a UI-only PR
  skips `backend`), #776 (no `:?` inside a default in the Portainer stack: some Compose versions evaluate it even when
  the outer variable is set, which refused a stack with only the host names), #777 (*Upgrade from 0.6.0* step by step
  for Portainer and Compose, with `deploy/tests/portainer-upgrade.sh` rendering the new stack with 0.6.0's variables),
  #778, #779. #768 (stream b's session, #764 item 3) is approved with two small notes; its author merges it.
- **The user's rule for guides** (2026-11-16): someone who already runs Portainer or Compose must be able to upgrade to
  the next release from the guides alone. Both repositories' guides have numbered steps per deploy form.

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
- **The gateway's test callback is not rate limited per address** (one indexed update per request, only while setup is
  open).
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

- **A release with the wizard** (0.7.0): stage 1's dry run is green, so `main` can be tagged when the user wants it
  (before or after stage 2); C13 (the M-16 session, #667) was planned for 0.6.1.
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
- **What a deployer types in Portainer:** 9 values at 0.6.0 (two secrets) → 5 on `main` (no secret:
  `UME_OIDC_AUTHORITY`, `UME_GATEWAY_HOST`, `UME_GATEWAY_PUBLIC_URL`, `UME_ADMIN_HOST`, `UME_DOMAIN`) → 1 after the
  gateway's release and Ume.Rag's stack on it.
- **New on `main`, unreleased:** migrations `IdentityOidcSettings`, `SetupMode`, `OpenWebUiSignInSettings` (additive);
  security events 6008–6013 and 5001 `callback_failed`; `GET /api/settings/sign-in`, `GET/PUT
  /api/settings/openwebui`, `/bff/setup/*` (its state now with `gateway`), rag-api's `GET
  /internal/openwebui/sign-in`; `/bff/user` gains `signIn.configured` and `setup.open`; `GET /api/overview` gains
  `secretExpiry`; `UME_DOMAIN`, `UME_RAG_SETUP_REOPEN`, `Setup__Secret`; `UME_RAG_OIDC_REFUSE_GUESTS` reaches rag-admin
  as a file (an empty boolean stops it at start); Open WebUI starts through `deploy/openwebui/start-with-sign-in.py`;
  `scripts/ci/backend-tests.sh`; `deploy/tests/portainer-upgrade.sh`.
- **Gateway, on this machine:** `gh` is signed in as `ume-nichlas` (it can push to both repositories; `Crotalus` is
  read-only on the gateway). Worktrees: `~/git/llmgateway` (the branch of #102), `~/git/llmgateway-100` (the branch of
  #103, with `node_modules` in `src/admin-ui` and `tests/e2e`), `~/git/llmgateway-100-deploy` (merged into #103's
  branch; can go), `~/git/llmgateway-101` (WG4, **uncommitted**). Local images `ume-llm-gateway/*:setup-test`.
  Something else holds ports 5000 and 5173 here: the gateway's IntegrationTests need
  `ASPNETCORE_URLS=http://127.0.0.1:0`. The Aspire CLI is at
  `~/.nuget/packages/aspire.cli.linux-x64/13.6.1/tools/net10.0/linux-x64/aspire` (not on PATH). The gateway's own rules:
  draft pull requests (drafts run no CI; Actions minutes are limited), the checks run locally first, squash-merge.
- **Ume.Rag local stack:** stopped; every throwaway volume of the session is removed; no worktree besides the main
  clone.
- **Open proposals for the user:** `plans/proposals/support-chatbot-features.md`.
- **U1 (pictures in uploads)** waits for DoIT's files at T6 (decided 2026-11-15).
- **The Eneo report PDF** is only at `/mnt/c/temp/eneo-vs-ume-rag.pdf`; the user hasn't said whether to keep it in the
  repo.
- **Tools here:** `actionlint` is installed (`/snap/bin/actionlint`; it hung once without `-shellcheck= -pyflakes=`);
  `shellcheck` and `pwsh` are not; `grep` is ugrep (handbook, *Environment notes*). The local `gh` token can't read
  ghcr packages: live stack tests run on the runner.

## Sessions 41 and 42 (2026-11-15 and 16)

- **41:** the release dry run for the wizard, green on the first run; the four gateway issues (#98–#101); CI time:
  five measurements of `Ume.Rag.AdminApi.Tests` (collections, thread pool, `fsync`, GC, two processes), then #775.
- **42:** the gateway's WG1–WG3 as pull requests and WG4 built; the security review and its fixes in both
  repositories; the upgrade guides; the wizard's suggestion from the gateway; #776–#779.
