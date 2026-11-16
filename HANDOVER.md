# Handover

State of the Ume.Rag work for the next session, rewritten each session (earlier ones: `git log -- HANDOVER.md`).
**Last release `v0.7.0`** (2026-11-16, the setup wizard; on the gateway's `v0.11.0`). `main` is 0.7.0 plus what
sessions 44 and 45 merged after it (*Unreleased* in `CHANGELOG.md`). The user's VM runs 0.5.0. Repo `~/research/rag` on this
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

## Start here (session 46): the deploy simplification (M-22)

The user asked for it on 2026-11-16: [`plans/proposals/deploy-simplification.md`](plans/proposals/deploy-simplification.md)
says where deployment is more complex than it needs to be and what to do first; the queue item is `plans/work/main.md`
**M-22**. Read the proposal, then:

1. **S1, one bootstrap for Compose and Portainer.** It has four points to decide before any code (where the files live
   in Compose, what stays in `init-deployment.sh`, one stack file or two, what Kubernetes does). Put them to the user
   first, each as four alternatives with one recommendation. **C13** of M-17 (the script loses the options whose
   values moved; `plans/proposals/deploy-configuration.md` §5) is S1's first step and needs no decision.
2. **S3 with S9, Open WebUI as a profile in Compose and easy to skip** (the user's own point: not every installation
   needs it). Off unless asked for in every form, and no trace of it when it is off: the stack example's default
   profiles, the `chat.` host, the managed client, the last step of *Get started*, the first-install guides. S9's list
   is from reading; check each point in the code first. The admin UI's texts are stream b's paths.
3. **S4a, delete the upgrade paths nobody runs:** every per-version section but the newest.

**Upgrades may break** (user, 2026-11-16: "assume no one is really using this yet"): a low-hanging simplification
need not keep an existing stack upgradable, as long as the release says so; the proposal lists what that frees
(*Allowed now that upgrades may break*). Tell the user when a change means the VM is reinstalled, not upgraded.

S2 (the host firewall and slice as optional hardening), S5 (one wizard hand-off from the gateway) and S7 (the manifest
names the owner and a first assigned key) wait for the user's decision: S2 changes the security design, S5 and S7 the
gateway.

### What else the main session can take without input

1. **A-20 (#506)** GitHub Actions pinned by commit SHA, and **A-27 (#507)** base-image CVE scans with automated digest
   bumps: stream a's queue, both on the before-go-live list (roadmap step 7).
2. **#792** (stream b's paths): `ParentMessageId` is never written, against chat design §11.4. Write it, or let the
   design drop the column; ask on the issue first.
3. **#793** (stream a's paths): `src/parser/LICENSES.md` has no LibreOffice section and its written offer names only
   GPL and LGPL. The issue holds a draft; its entries marked *from memory* must be checked against the image's
   `/usr/share/doc/*/copyright` files.
4. **#797** (stream b's paths): `deploy/keycloak/README.md`'s standalone import command uses `http://keycloak:8080`,
   and the gateway's Keycloak serves only HTTPS on 8443.

Before a stream's paths: check for open `a/` or `b/` work and say so on the issue (handbook).

### Waiting on the user

- **The VM:** 0.5.0 → 0.6.0 → 0.7.0 by `docs/portainer.md` (*Upgrade from 0.5.x to 0.6.0* **with the `v0.6.0`
  release's stack file**, then *Upgrade from 0.6.0* with the `v0.7.0` file: #795 found that the guide said only "the
  new file", and the 0.7.0 file at the first step would create three new credential keys).
  Before the first step, rag-api's and rag-admin's credential keys get `OnPrem` and `Eu`, and each published assistant
  needs a gateway key that reaches both its chat model and its embedding model. Pulls that say "denied":
  `docker logout ghcr.io`. **This is the first boot of an upgraded stack and the first use of the new stack file in
  Portainer itself: neither is tested anywhere.**
- **The support-chatbot proposal** (`plans/proposals/support-chatbot-features.md`): the user picks; the suggestion is
  F3, F4, F8 and F10.
- **The roadmap's T0 row** (the gateway with the integration, keys and models) says *open* though T1, which depends on
  it, is deployed: the user knows its state.
- **To-verify row 96:** the NVDA screen-reader pass of the admin UI, which needs Windows.
- **To-verify row 110, a question to KB** (the user's call; the earlier licence review judged it unnecessary for the
  pilot, the main session would send it before go-live): *"We intend to use KBLab/bert-base-swedish-cased-ner (revision
  f3c83af8) in a municipal production service and possibly redistribute a container image containing the weights. Can
  you confirm that the MIT licence on the model card covers the weights without restriction from the SUC 3.0
  agreement, and which variant of SUC 3.0 (licensed or scrambled/SUCX) it was trained on? Which copyright line should
  the MIT notice carry?"*
- **To-verify row 79:** whether `NerMinScore` moves for the int8 NER model. It misses 1 of 66 labelled names at 0.80
  (0.60 finds it for one false positive). Left at 0.80: the sample is small.
- **U1 (pictures in uploads)** waits for DoIT's files at T6 (decided 2026-11-15).
- **The Eneo report PDF** is only at `/mnt/c/temp/eneo-vs-ume-rag.pdf`; the user hasn't said whether to keep it in the
  repo.

### Watch

- **#781, the flaky "search unavailable" answer in `ChatApiTests`:** it failed the first attempt of the 0.7.0 release
  run. #790 gave rag-api's test hosts 30 s embedding limits and made every failed query embedding log why. The cause
  (the 2 s limit passed on a busy runner) rests on elimination and on #584's precedent, **not on a reproduction**.
  The issue stays open until 2026-11-30. If it recurs, the job's log has *The query embedding of profile … is
  unavailable (…)*, which names the cause.
- **Run `e2e.yml` before the next release.** It did not run on the 0.7.0 tag (green in dry run 43857593918 on the same
  code), and #790 and #791 came after.

## What session 45 did (2026-11-16)

- **A first install with Compose is shorter, #794:** `init-deployment.sh --gateway-secrets-dir` reads the three
  credential keys a gateway on the same host creates from Ume.Rag's manifest (now in the release bundle); an
  interactive run asks for the sign-in provider's address, which the host firewall needs for the wizard's test;
  `docs/compose-quick-start.md` replaces `docs/gateway-host-setup.md`.
- **The install and upgrade guides, the plans and the README checked against the code, #795 and #796:** six read-only
  audits, one per group of documents, every finding checked in the files before it was applied (about 80 statements).
  What mattered: Compose 2.33.1, not 2.24 (`gw_priority`); a Portainer upgrade over several releases takes each
  release's own stack file; the single-slot procedure had no stop step for *Rolling update: no*; the 0.6.0 stack pins
  the gateway's `v0.10.3`. `restore-pitr.sh` now stops the second slots too (not run: no rolling stack here). The
  release bundle carries the pages and `deploy/keycloak/` its guides link to.
- **The deploy simplification proposal** (above), from the user's question what could be simpler.
- **How it was done:** the audits ran as parallel read-only agents with a brief per document group; their reports were
  claims, and two findings were left for their owner or the user (#797, the roadmap's T0).
- **Session 44** (the setup wizard's stage 2, release 0.7.0, #790, #791): `git show 4d80080c:HANDOVER.md`.

## Known gaps, documented, not fixed

- **Not tested anywhere:** a boot of an upgraded stack (the previous release's volumes under the new images);
  Portainer itself; both wizards completed against a real provider in one deployed stack (the boot test has no
  provider and stops at each wizard's state); Entra ID and AD FS; a real untrusted TLS certificate; Kubernetes beyond
  rendering.
- **The manifest default needs Compose 2.24.7 or later:** an older one cuts a default that contains `}` at the first
  one, without an error, also when the variable is set. Portainer pins a later one from 2.21 (read, not run).
- **A stack without `UME_INTEGRATION_MANIFEST`** (0.5.x style, keys issued by hand) gets the default from 0.7.0: three
  unused credential keys unless the variable is set empty first (`docs/portainer.md`, *Upgrade from 0.6.0*).
- **The boot test shows no Open WebUI with OIDC** (no provider anywhere, so it starts without sign-in); `e2e.yml`
  shows it (accepted by the user).
- **Compose's host firewall** lets rag-admin reach only the provider of `--oidc-authority`; without it the wizard's
  test can't reach a provider. `init-deployment.sh` asks for it in an interactive run, and the wizard's message says
  so; a run with `--yes` and no authority still ends there (its closing notes warn). A provider typed only in the
  wizard would need the firewall to open rag-admin's egress, which is the user's call.
- **The Compose quick start (`docs/compose-quick-start.md`) is read, not run:** `--gateway-secrets-dir` is tested with
  placeholder files, the gateway's manifest path only in the Portainer boot test, and the same-host flow end to end
  (the gateway on `--bind 0.0.0.0`, Ume.Rag's firewall letting its networks through) nowhere.
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
- **On `main`, unreleased** (#790, #791, #794 and the guides' check): the warning for a failed query embedding;
  schedules validated with the scheduler's parser; chat's 413 as `request_too_large`; `init-deployment.sh
  --gateway-secrets-dir` and its prompt for the sign-in provider; `docs/compose-quick-start.md`; `restore-pitr.sh` stops
  the second slots; the install and upgrade guides corrected against the code. No migration, no new variable.
- **New in 0.7.0:** migrations `IdentityOidcSettings`, `SetupMode`, `OpenWebUiSignInSettings` (additive); security
  events 6008–6013 and 5001 `callback_failed`; `GET /api/settings/sign-in`, `GET/PUT /api/settings/openwebui`,
  `/bff/setup/*`, rag-api's `GET /internal/openwebui/sign-in`; `/bff/user` gains `signIn.configured` and `setup.open`;
  `GET /api/overview` gains `secretExpiry` and `deploymentNotices`; `UME_DOMAIN`, `UME_RAG_SETUP_REOPEN`,
  `Setup__Secret`; Open WebUI starts through `deploy/openwebui/start-with-sign-in.py`.
- **Open issues that matter:** #781 (watch), #792, #793, #667 (M-16 and C13), #506, #507, #333 (the exact-scan and
  recall checks on the test host).
- **Ume.Rag on this machine:** the local stack is stopped; no worktree besides the main clone; volumes
  `ume-rag-m1-postgres` (the dev data), `ume-rag-openwebui-data` and `ume-rag-clamav-db` are kept. Session 44's scratch
  directory holds about 1 GB (883 MB of Compose binaries in `compose-check/bin`) and can go.
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
