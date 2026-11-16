# Finished queue items up to v0.7.0

The ticked items of the work queues, moved here unchanged when `v0.7.0` was released (2026-11-16, session 44).
History: it does not bind. An item is found by its ID (`M-20`); the queues in `plans/work/` hold only open and waiting
items.

## Main session (`main.md`)

### [x] M-20 Setup wizard: sign-in and the last deploy values set in the browser (user, 2026-11-15)
- **Do:** the steps S0–S8 of [`../proposals/setup-wizard.md`](../../proposals/setup-wizard.md) §8 in their order, one PR
  per step, on `main-session/m20-<step>` branches: the main session builds all of it, stream b's paths included (user,
  2026-11-15; announced on an issue). The rules are Decisions "Setup wizard", identity §19 and admin §5.11.
- **Decided** (user, 2026-11-15, W1–W10): the whole stack with Ume.Rag first; Portainer, Compose and Kubernetes; a
  setup code from rag-admin's log; a test sign-in before saving, re-opened only with host access; the deployment's
  value wins; wizard values in the database, secrets encrypted; host names under one `UME_DOMAIN`; Open WebUI's wrapper
  fetches its sign-in settings; the crawler's ranges stay deploy-time; reload in place.
- **Waits on:** stage 2 (S8's code) waits on the gateway's releases of thekim1/llmgateway#98–#100; stage 1 on nothing.
- **Done when:** `stack.env.example` has no secret and 5 CHANGE values (stage 1), then 1 (stage 2); the release's
  boot test passes with no sign-in value set and `e2e.yml` passes through the wizard.
- **Reviewed** 2026-11-15 by an independent agent; the findings are worked in (proposal §10).
- **Progress:** S0 (#760: `UME_DOMAIN`; the boot-only run green with only the domain set) and S1 (#761: the settings,
  the encrypted secret, effective values with their source, `GET /api/settings/sign-in`) merged 2026-11-15. S2 (#762: sign-in from the effective
  values, reload by fingerprint, start without an authority, a failed callback no longer a 500) merged. S3 merged: the deploy side (#763: `Setup__Secret`,
  `UME_RAG_SETUP_REOPEN`, the AppHost's `Setup:Try`) and the backend (#767: setup state, code, session, the test sign-in,
  complete, re-open, events 6008–6012; an independent security review's findings fixed before the merge, proposal §10).
  S4 merged (#769: the wizard's pages; run end to end in a browser against the local Keycloak; #770 fixed a
  race in `complete` that made a test flaky). S5 merged (#771: Open WebUI's settings, the back-channel test, rag-api's internal route, the start wrapper,
  run in the real pinned image). S6 merged (#772: no defaults that lock fields, `stack.env.example` at 5 CHANGE
  values and no secret, the boot test through the wizard's API, the operators' docs). The UI follow-up merged (#773:
  the wizard's Open WebUI step, run end to end in a browser, and Home's reminder before a secret expires). S7 merged
  (#774: the browser test, green in both phases of a manual `e2e.yml` run, 43762339453). **Stage 1 is built and shown** (session 41): the release dry run
  from `main` is green ([43763822654](https://github.com/thekim1/ume-rag-platform/actions/runs/43763822654)), with
  S6's boot-test assertions and the wrapper starting Open WebUI with sign-in fetched from the real rag-api over TLS.
  **S8, the gateway's issues, are filed** (user, 2026-11-15): thekim1/llmgateway#98 (WG1), #99 (WG2), #100 (WG3),
  #101 (WG4, optional). **The gateway's WG1–WG3 are built by the main session** (user, 2026-11-16):
  thekim1/llmgateway#102 and #103, waiting for the user's merge and a gateway release. The
  context's `oidc_authority` as the wizard's suggestion is merged (#779). **Open:** Ume.Rag's side of the rest when the
  gateway releases it (`UME_DOMAIN` for all five names, `stack.env.example` at 1 CHANGE value); the Compose host
  firewall reaches only the provider of `--oidc-authority` (documented; an allow-list is a follow-up). Stream b was
  told on #759. **Ume.Rag's side of stage 2 is built** (session 44, draft #786: 1 CHANGE value, the
  manifest as the stack file's default, the gateway's wizard first in the boot test, the docs); it is finished on the
  gateway's release (HANDOVER has the steps).
- **Done** 2026-11-16 (#786, on the gateway's `v0.11.0`): `stack.env.example` has 1 CHANGE value and no secret
  (asserted by `deploy/tests/portainer-hosts.sh`); the release dry run from the branch with `e2e=true` is green
  ([43857593918](https://github.com/thekim1/ume-rag-platform/actions/runs/43857593918)): the boot test passes with no
  sign-in value set, the gateway's wizard first, and `e2e.yml` passes through the wizard (`setup.spec.ts`, 9 passed).

### [x] M-21 Deployment values across releases: retired variables named, a notice on Home (user, 2026-11-16)
- **Do:** Decisions "Deployment values across releases" and admin §5.5.1 (*Deployment notices*), on a
  `main-session/m21-<slug>` branch (stream a's and b's paths; announced on an issue). `rag-bootstrap` gets the retired
  list (every Portainer variable retired since 0.3.0, with what replaced it), names the ones a stack still sets on its
  checklist and writes them to `admin/Deployment__RetiredVariables`; `GET /api/overview` returns `deploymentNotices`
  for a platform admin; Home shows the notice; `docs/portainer.md`, the upgrade guide and the changelog say so.
- **Waits on:** nothing.
- **Done when:** `deploy/tests/portainer-rerun.sh` shows a retired variable on the checklist and in the file, and none
  without one; the admin API's and the admin UI's tests cover the notice; `verify.yml` green.
- **Done** 2026-11-16 (#783): the re-run test, the admin API's test and the admin UI's specs cover it; `verify.yml` green.
