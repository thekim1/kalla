# Handover

State of the Ume.Rag work for the next session, rewritten each session (earlier ones: `git log -- HANDOVER.md`).
**Last release `v0.6.0`** (2026-11-15); the user's VM runs 0.5.0 and the user is to upgrade it to 0.6.0. `main` is well past 0.6.0: the setup wizard's stage 1 (M-20) is on it, shown by a release dry run, unreleased. Repo `~/research/rag` on this machine (`~/git/ume-rag-platform` on the
session-19 machine), branch `main`, remote `origin` = https://github.com/thekim1/ume-rag-platform (push after every
commit; HTTPS through `gh auth git-credential`, handbook *Environment notes*).

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

## Start here (session 42): the setup wizard's stage 1 is done; the gateway's part of stage 2 is built and waits for a merge and a release

**What happened in session 41.** The user said to go on until the wizard is done, with the main session's
recommendations for the open answers.

- **Stage 1 is shown.** The release dry run from `main` is green
  ([43763822654](https://github.com/thekim1/ume-rag-platform/actions/runs/43763822654), `0.7.0-dryrun`, 15 min). Its
  Portainer boot test, on images with the wizard: setup mode open in rag-admin's output with the code not printed, a
  wrong code refused, the right code giving a setup session, `GET /bff/setup/state` as a first run with only the
  authority from the deployment, no container restarted; Open WebUI's start wrapper started it with OIDC sign-in
  fetched from the real rag-api over TLS. With S7's `e2e.yml` run (43762339453) that is M-20's stage-1 done-when.
- **The gateway's issues are filed** (S8): thekim1/llmgateway#98 (WG1 `UME_DOMAIN` for its host names), #99 (WG2
  `gateway.oidc_authority` in `/v1/ume/context`), #100 (WG3 the same setup mode in its admin), #101 (WG4 the manifest
  without an edit, optional). Stream b was told on #759.
- **CI time** (#775): `backend` 7 min 40 s → 4 min 53 s. Measured first: `Ume.Rag.AdminApi.Tests`' collections wait
  for one another inside one process (handbook, *Lessons*), so `scripts/ci/backend-tests.sh` runs the project as five
  processes; a pull request with only `src/admin-ui/` or `tests/e2e/` changes skips `backend`. **Not done:** running
  only the test projects a PR touches (several tests read other projects' sources; it would save at most the 3
  minutes the tests take now).
- **A public client that declares everything** keeps today's rule (it gets the wizard once); nothing changed.

**The gateway's part is built by this session** (user, 2026-11-16: "build it"; nobody had picked the issues up), in
`~/git/llmgateway` as `ume-nichlas` (which can push there; `Crotalus` cannot). **Both pull requests wait for the user's
merge** (this session's merge was stopped by the permission check), then a gateway release:
- **WG1 and WG2:** thekim1/llmgateway#102, green. `gateway.oidc_authority` is `Gateway:OidcAuthority` (set from
  `UME_OIDC_AUTHORITY` in the deploy files), else the authority saved in the gateway's wizard; feature `oidc-authority`.
- **WG3:** thekim1/llmgateway#103, a **draft stacked on #102** (no CI has run on it; mark it ready once #102 is merged
  and it retargets to `main`). Six commits: stored sign-in values, setup mode in the admin API, the security review's
  fixes, the wizard's pages, its browser test, every deploy form with the operators' docs and the upgrade guide. Checked
  locally: Release build clean, 1,618 backend tests, the UI gate (316 tests), the wizard end to end in a browser against
  the bundled Keycloak (5 of 5, with axe), and a boot of the Portainer stack with only `UME_DOMAIN` (code printed,
  accepted, setup closed once the authority is declared). **A declared gateway deployment (one that sets the authority)
  never sees the wizard.** Its upgrade guide has numbered steps for Portainer, Compose and Kubernetes.
- **WG4** (optional) is not started.
- **Found on the way, fixed in both repositories:** a `:?` inside a default in a stack file is evaluated by some Compose
  versions even when the outer variable is set (#776); the setup code's throttle came before the comparison, so a flood
  of wrong codes kept the right one out, and two smaller findings of the gateway's independent security review (#778).
- **Upgrade guides** (user, 2026-11-16: someone who already runs Portainer or Compose must be able to upgrade from the
  guides): `docs/portainer.md` *Upgrade from 0.6.0* and the Compose steps in `docs/upgrade-guide.md` (#777), with
  `deploy/tests/portainer-upgrade.sh` rendering the new stack with 0.6.0's variables. **Not tested: a boot of an upgraded
  stack** (0.6.0's volumes under the new images), in either repository.
- **Left on this machine:** the gateway worktrees `~/git/llmgateway-100` (the branch of #103) and
  `~/git/llmgateway-100-deploy` (merged into it; can go), the local images `ume-llm-gateway/*:setup-test`. Something
  else holds ports 5000 and 5173 here: the gateway's IntegrationTests need `ASPNETCORE_URLS=http://127.0.0.1:0`, and
  its admin UI is on the port `aspire logs admin-ui` names.

**Ume.Rag's side of stage 2:**
- **Done (#779):** the wizard offers the gateway's provider address (`gateway.oidc_authority`, feature `oidc-authority`)
  for rag-admin's authority, as a suggestion in `GET /bff/setup/state` (`gateway.suggestedAuthority`), and links to the
  gateway's wizard when the gateway has none. `contracts/gateway/integrations.json` is the gateway's **unreleased** file
  (llmgateway `7c945d1`): replace it with the release's copy.
- **Left, when the gateway releases #102 and #103:** the combined stack on that release (`deploy/portainer/build-stack.sh
  <version>`); `stack.env.example` loses the gateway's four CHANGE values and has one, `UME_DOMAIN`; the boot test
  (`portainer-boot.sh`: the gateway's wizard first, then Ume.Rag's with the suggested address), `portainer-upgrade.sh`'s
  fixture for the new release, and `docs/portainer.md` (first deploy and *Upgrade from 0.6.0* gain the gateway's steps,
  which its own upgrade guide has). WG4 (the manifest as a default) only if the gateway builds #101.

**Known gaps, documented, not fixed:**
- **Compose's host firewall** lets rag-admin reach only the provider of `--oidc-authority`; without it the wizard's
  test can't reach a provider (docs and the script's notes say to keep the option). An allow-list would be the fix.
- **Not tested anywhere:** Entra ID (the test sign-in and the Open WebUI client check), a real untrusted TLS
  certificate, Kubernetes (no Open WebUI there; the wizard's files render with `kubectl kustomize` only).
- **Accepted in the security review:** a setup-code holder can learn whether an internal https address answers (no
  content comes back); the audit names whoever the chosen provider says signed in.
- `GET /internal/openwebui/sign-in` is closed at the Portainer proxy; in Compose only the managed client's key opens
  it (Open WebUI calls rag-api by its public address there).
- **CI:** what the test collections of one process wait for is not found; attaching a profiler (`dotnet-stack`) was
  stopped by the session's permission check. The user can allow it, or run it by hand.

**How to try the wizard locally** (each time on a throwaway volume):
`export FakeGateway__Enabled=true Seed__Enabled=false Parser__Mode=off Render__Mode=off OpenWebUi__Enabled=false
ClamAv__Mode=off DevelopmentVolumes__Postgres=<a throwaway name> Setup__Try=true`, `aspire start …`, `aspire wait
admin-ui`; the code is in `aspire logs adminapi | grep 'Setup code:'`; the provider is
`https://localhost:7411/realms/ume`, the clients `ume-rag-admin` and `open-webui-dev` (secrets: the AppHost parameters
`oidc-client-secret`, `openwebui-client-secret`), the user `rag-platform-admin`. Afterwards `aspire stop --all`, wait,
`docker volume rm`. Or run the spec: `tests/e2e/README.md`.

**Also open (not this work):**
- **A release with the wizard** (0.7.0): the dry run is green, so `main` can be tagged when the user wants it; C13
  (the M-16 session, #667) was planned for 0.6.1.
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
- **Open PR:** #768 (#764 item 3, by the stream b session): approved by the main session with two small notes; its author merges it. Engine design §9.3 has its deviation.

## State

- **Release:** `v0.6.0` published 2026-11-15 ([run 43720085132](https://github.com/thekim1/ume-rag-platform/actions/runs/43720085132)),
  tag at `d5029e29`; `deploy/portainer/` on `main` is ahead of it (the wizard's stack file: don't hand it to a 0.6.0
  deployment; the `v0.6.0` release asset is the file for 0.6.0).
- **What a deployer types in Portainer:** 9 values at 0.6.0 (two secrets) → 5 on `main` (no secret:
  `UME_OIDC_AUTHORITY`, `UME_GATEWAY_HOST`, `UME_GATEWAY_PUBLIC_URL`, `UME_ADMIN_HOST`, `UME_DOMAIN`) → 1 after the
  gateway's part.
- **New on `main`, unreleased:** migrations `IdentityOidcSettings`, `SetupMode`, `OpenWebUiSignInSettings` (additive);
  security events 6008–6013 and 5001 `callback_failed`; `GET /api/settings/sign-in`, `GET/PUT
  /api/settings/openwebui`, `/bff/setup/*`, rag-api's `GET /internal/openwebui/sign-in`; `/bff/user` gains
  `signIn.configured` and `setup.open`; `GET /api/overview` gains `secretExpiry`; `UME_DOMAIN`,
  `UME_RAG_SETUP_REOPEN`, `Setup__Secret`; `UME_RAG_OIDC_REFUSE_GUESTS` reaches rag-admin as a file (an empty boolean
  stops it at start); Open WebUI starts through `deploy/openwebui/start-with-sign-in.py`.
- **Local stack:** stopped; every throwaway volume of the session is removed; no worktree besides the main clone.
- **Open proposals for the user:** `plans/proposals/support-chatbot-features.md`.
- **U1 (pictures in uploads)** waits for DoIT's files at T6 (decided 2026-11-15).
- **The Eneo report PDF** is only at `/mnt/c/temp/eneo-vs-ume-rag.pdf`; the user hasn't said whether to keep it in the
  repo.
- **Tools here:** `actionlint` is installed (`/snap/bin/actionlint`; it hung once without `-shellcheck= -pyflakes=`);
  `shellcheck` and `pwsh` are not; `grep` is ugrep (handbook, *Environment notes*). The local `gh` token can't read
  ghcr packages: live stack tests run on the runner.

## Session 41 (2026-11-15)

- The release dry run for the wizard, green on the first run; the four gateway issues; #759 answered.
- CI time: five measurements of `Ume.Rag.AdminApi.Tests` (collections, thread pool, `fsync`, GC, two processes),
  then #775.
