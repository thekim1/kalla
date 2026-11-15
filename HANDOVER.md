# Handover

State of the Ume.Rag work for the next session, rewritten each session (earlier ones: `git log -- HANDOVER.md`).
**Last release `v0.6.0`** (2026-11-15); the user's VM runs 0.5.0 and the user is to upgrade it to 0.6.0. `main` is well past 0.6.0: the setup wizard (M-20) is on it, unreleased. Repo `~/research/rag` on this machine (`~/git/ume-rag-platform` on the
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

## Start here (session 41): a release dry run for the setup wizard (M-20), and the user's open answers

**What happened in session 40.** The design session on the setup wizard ended in an agreed proposal
(`plans/proposals/setup-wizard.md`, W1–W10 answered by the user, reviewed twice by independent agents), and the user
then asked the main session to **build it, stream b's paths included** (announced on #759; stream b hasn't answered and
worked elsewhere). Stage 1 is built and merged, all eight steps:

| Step | What | PR |
|---|---|---|
| S0 | `UME_DOMAIN` gives the three Ume.Rag host names their defaults | #760 |
| S1 | Sign-in values as settings, the client secret encrypted, effective values with their source | #761 |
| S2 | rag-admin signs in with the effective values, follows a change without a restart, starts without an authority | #762 |
| S3 | Setup mode: `Setup__Secret` and `UME_RAG_SETUP_REOPEN` in all deploy forms (#763); the setup code, session, test sign-in, complete, re-open (#767); a race in `complete` (#770) | #763, #767, #770 |
| S4 | The wizard's pages (`/setup`) | #769 |
| S5 | Open WebUI: settings, the back-channel test, rag-api's internal route, the start wrapper | #771 |
| – | The wizard's Open WebUI step; Home's reminder before a client secret expires | #773 |
| S6 | Deploy files without locking defaults, `stack.env.example` at 5 CHANGE values and no secret, the boot test through the wizard's API, the operators' docs | #772 |
| S7 | The browser test of the wizard (`tests/e2e/specs/setup.spec.ts`, a second phase of `e2e.yml`) | #774 |

The rules are Decisions "Setup wizard" and "Deploy configuration", identity §19 and admin §5.11; the queue item is
`plans/work/main.md` M-20.

**S7's done-when is met:** the manual `e2e.yml` run [43762339453](https://github.com/thekim1/ume-rag-platform/actions/runs/43762339453)
passed in both phases (9 pilot and accessibility tests in 9.7 min, the wizard's 9 in 15 s), run with the runners idle.
An earlier run failed because it ran beside the PR's `backend` job (`net::ERR_NETWORK_CHANGED`; handbook).

**First, to call stage 1 done: a release dry run.** Two things can only be shown with images built from `main` (0.6.0's
have no setup mode):
- `deploy/tests/portainer-boot.sh`'s wizard assertions (#772): the setup line in rag-admin's output, a wrong and the
  right code, `GET /bff/setup/state` with nothing locked by accident, the code masked in the log, no restart of
  rag-admin or rag-openwebui;
- the Open WebUI start wrapper's TLS call to the real rag-api (#771; it was run in the real pinned image against a
  plain-HTTP stand-in only).
Use the handbook's *Release dry runs* and the user's "fast release loops" rule: if a check fails, fix it in isolation
(a boot-only run against a tag whose images have the wizard), not with repeated full dry runs.

**Waiting on the user (asked in session 40, not answered):**
1. **CI time.** A PR takes about 9 minutes and `Ume.Rag.AdminApi.Tests` alone sets it (5 min 40 s; 302 → 360 tests in
   this work; the backend job is now 7 to 8.5 min). It also runs in full for a UI-only PR, which is how a backend
   flake blocked #769. Four alternatives were given; the recommendation: split `AdminApi.Tests` into parallel shards
   (it already has four database groups) **and** run only the test projects a PR touches. Measure where the time goes
   first (container start or tests).
2. **The gateway's part (S8).** Four issues for thekim1/llmgateway are drafted in the proposal §6 (WG1 `UME_DOMAIN`
   for the gateway's hosts, WG2 the authority in `/v1/ume/context`, WG3 the same setup mode in the gateway's admin,
   WG4 the manifest without an edit) and **not filed**: the user hasn't said whether to file them now. Stage 2 (one
   value typed in Portainer) needs them.
3. **A public client that declares everything** still gets the wizard once (a declared deployment is an authority
   **and** a client secret from the deployment, since the client id has a default). The user was told and may want
   another rule.

**Known gaps, documented, not fixed:**
- **Compose's host firewall** lets rag-admin reach only the provider of `--oidc-authority`; without it the wizard's
  test can't reach a provider (docs and the script's notes say to keep the option). An allow-list would be the fix.
- **Not tested anywhere:** Entra ID (the test sign-in and the Open WebUI client check), a real untrusted TLS
  certificate, Kubernetes (no Open WebUI there; the wizard's files render with `kubectl kustomize` only).
- **Accepted in the security review:** a setup-code holder can learn whether an internal https address answers (no
  content comes back); the audit names whoever the chosen provider says signed in.
- `GET /internal/openwebui/sign-in` is closed at the Portainer proxy; in Compose only the managed client's key opens
  it (Open WebUI calls rag-api by its public address there).

**How to try the wizard locally** (done three times in session 40, each on a throwaway volume):
`export FakeGateway__Enabled=true Seed__Enabled=false Parser__Mode=off Render__Mode=off OpenWebUi__Enabled=false
ClamAv__Mode=off DevelopmentVolumes__Postgres=<a throwaway name> Setup__Try=true`, `aspire start …`, `aspire wait
admin-ui`; the code is in `aspire logs adminapi | grep 'Setup code:'`; the provider is
`https://localhost:7411/realms/ume`, the clients `ume-rag-admin` and `open-webui-dev` (secrets: the AppHost parameters
`oidc-client-secret`, `openwebui-client-secret`), the user `rag-platform-admin`. Afterwards `aspire stop --all`, wait,
`docker volume rm`. Or run the spec: `tests/e2e/README.md`.

**Also open (not this work):**
- **The user's upgrade to 0.6.0** (`docs/portainer.md`, *Upgrade from 0.5.x to 0.6.0*). Before it, rag-api's and
  rag-admin's credential keys get `OnPrem` and `Eu`, and each published assistant needs a gateway key that reaches both
  its chat model and its embedding model. Pulls that say "denied": `docker logout ghcr.io`.
- **The support-chatbot proposal** (`plans/proposals/support-chatbot-features.md`): the user picks; the suggestion is
  F3, F4, F8 and F10.
- **0.6.1:** C13 (the M-16 session, #667); the release files are free again.
- **The next release run checks for the first time:** `:latest` moves in `publish` (#756); verify and parser run in
  full on a tag (#755); the run takes about 17 min (0.6.0: 27); and now the wizard's boot-test path (above).
- **For the user:** *Allow GitHub Actions to create and approve pull requests* is still off (Settings → Actions →
  General), so `release.yml` couldn't open the pin PR (#754 was opened by hand).
- **Checks before go-live:** the 16 *Before go-live* rows of `plans/to-verify.md` need no input.
- Stream b merged #765 and #766 (search slots, #764) during the session; no overlap.

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

## Session 40 (2026-11-15)

- **Design:** the setup wizard's proposal, agreed; a survey of 14 products' first-run and SSO settings; the first
  review (14 findings, all worked in: an explicit setup state, a derived code, 9 not 11 values).
- **Build:** S0–S7 and the UI follow-up merged (table above). Each step by an implementor agent in a
  worktree; the main session read the diffs, re-ran the tests and opened the PRs.
- **The security review of S3's code** (an independent agent) found what the main session's own reading had missed: a
  code holder could have had the stored or the deployment's client secret sent to a provider of their choice. Fixed
  before the merge, with: a throttle instead of a wrong-code limit that locked the deployer out, a cookie that binds a
  test to one browser, a missing settings row staying closed.
- **Browser runs by the main session** (Playwright against the local Keycloak) found what unit tests couldn't: the
  Open WebUI step had no default provider on a first run (the admin's provider is tested but not saved at that
  point); now it follows the session's passed test.
- **The Open WebUI wrapper** was run in the real pinned image against a stand-in for rag-api.
