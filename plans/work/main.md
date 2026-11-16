# Main session: work queue

The main session owns the design documents, the roadmap and these queues; it reviews and merges stream PRs and
settles their design deviations. Start each session from [`../../HANDOVER.md`](../../HANDOVER.md) and [`main-handbook.md`](main-handbook.md).

## Standing

- Review every stream PR (live in the browser where it touches a page), settle its "Design deviations" in the design,
  merge when ready. Keep [`../roadmap.md`](../roadmap.md) and the stream queues current.
- New work is written as a queue item (goal, paths, done-when, waits on, read) in the owning stream's file.

## Queue

Finished items move to [`../archive/work/`](../archive/work/) at each release; find one by its ID.

### [ ] M-4 The live block
- **Do:** run it in the roadmap's order with the streams and the user; the three-way gold-set run and the hand
  grading use `scripts/grading/` (README there).
- **Done when:** the go-live gate is met, G1 is finished and the pilot is live; the roadmap moves P1b to Done.

### [ ] M-16 Connect an app in one go; the platform menu; the Settings page (user, 2026-11-13)
- **Owner:** the session on stream b's machine (GitHub `thekim1`), by the user; it crosses both streams' paths, so
  each step is a PR on a `main-session/<slug>` branch, announced in the issue first (handbook, *How we work*).
- **Do, in order** (each one PR or less):
  1. **API** (admin §5.9.1): `assistantIds` on `POST /api/clients`, `PUT /api/clients/{id}/assistants`, `slug` and
     `classification` on `ClientDto.assistants`, `GET /api/clients/connection` (`Admin:ApiPublicUrl`), `lastUsedAt` on
     `KeyDto` and `ClientSummary`. Paths: `src/Ume.Rag.AdminApi/**`, `tests/Ume.Rag.AdminApi.Tests/**`,
     `src/admin-ui/src/api/**`.
  2. **Deploy:** rag-admin gets `Admin__ApiPublicUrl` from `UME_RAG_API_PUBLIC_URL` in the Compose, Kubernetes and
     Portainer templates. Paths: `deploy/**`.
  3. **Add app and the drawer:** the step *What it may use*, the result *How to connect*, the drawer's checklist and
     *How to connect*, last use, **Connect a new app** on the assistant's *Who can use it*. Paths:
     `src/admin-ui/src/platform/**`, `src/admin-ui/src/views/AssistantView.vue`, `src/admin-ui/src/utils/labels.ts`.
  4. **Keys:** the short key form with *Restrictions*, scope lists from the app's audience, the **All keys** tab; the
     generic `keys` resource goes; the page is named *Apps and keys*; `docs/portainer.md` step 7 follows. Paths:
     `src/admin-ui/src/platform/**`, `labels.ts`, `docs/portainer.md`.
  5. **Menu** (admin §5.10): the Platform group's five items, the index page gone, the generic `organisation`
     resource's facts on the Organisation page, search entries. Paths: `src/admin-ui/src/router/**`,
     `src/admin-ui/src/platform/**`, `src/admin-ui/src/views/OrganisationView.vue`, `labels.ts`, `tests/e2e/**` where a
     spec opens `/plattform`.
  6. **Settings, the shell:** the page with its section list and today's forms on the page; site profiles and data
     protection as sections. Paths: `src/admin-ui/src/platform/**`, `labels.ts`.
  7. **Settings, typed members:** Chat, Search, Processing, Personal data, one PR each. Paths as 6.
  8. **Generic forms:** an id field as a select of the object's rows. Paths: `src/admin-ui/src/platform/**`.
- **Done when:** a platform admin connects an integration without leaving the wizard, never types or looks up a GUID,
  and a `curl` copied from *How to connect* answers through the picked assistant; the app's row shows the key's last
  use; Apps and keys, Models and Settings open from the side menu in one click; no settings member of steps 6–7 needs
  JSON; checked in the browser and by the Vitest and API tests of each step.
- **Mind:** the open deploy-configuration proposal (#662, on its branch): its C4 makes Open WebUI's client deployment-managed (the wizard's Open WebUI block goes then), its C1
  gives `UME_RAG_API_PUBLIC_URL` a default (step 2 follows it), its C5 and C10 add to the Models and Settings pages.
- **Read:** admin §5.9, §5.9.1, §5.10, §5.8, §4.6 (V7–V9); identity §6.4; data-model §2.1.
- **Status (2026-11-14):** steps 1–8 built: #668, #669, #671, #672, #673 (step 6, before 5), #675, #676 (step 7 for Chat,
  Search and Processing's schedules) and step 8 in the PR that says so, with #670 (rag-api records a key's last use, which
  nothing wrote before), and Personal data's flag rules as a table (the PR that says so). Left: Open WebUI's block and *Managed by the
  deployment* once M-17's C4 lands. The Role groups section came with C10 (#693); #677 was M-18 (#686).

### [ ] M-17 Deploy configuration: settings, automatic setup and the gateway (user, 2026-11-13; #662)
- **Do:** the steps C0–C13 of [`../proposals/deploy-configuration.md`](../proposals/deploy-configuration.md) §5 in their
  order, one PR per step, each step's paths, done-when and stream as listed there. Decided: Decisions "Deploy
  configuration", "Residency per level", "Role groups as a setting". C0–C6, C8–C10 and C13 need nothing more; C7 waits
  on llmgateway#87 (G1, accepted with changes: create-only, its own volume), C11 on #90's replacement (the context's
  `integration_api_url`; G5 itself declined), C12 on #89 (G4, accepted), each released and pinned; the gateway's order
  is #89, #90's discovery part, #87, #88 (#580, 2026-11-14).
- **Mind:** M-16 (another session) builds the Apps and Settings pages: C4 removes the wizard's Open WebUI type and makes
  its client "Managed by the deployment"; C5's create form goes on the Models page and C10's role groups on the
  Settings page (§5.10). Main updates identity §4.7 (C9), §5.2 (C4) and repo-and-deployment §6 before the step that
  changes them.
- **Done when:** C0–C13 merged (C7, C11, C12 once their gateway release exists); a fresh Portainer stack boots with the
  proposal's §2 "Net effect" (six values, no pasted keys once G1 is pinned), checked by C0's release boot test.
- **Waits on:** nothing. The gateway released all three in **v0.10.2** (2026-11-14): #87 (with #94, ADR 022, *Provisioning
  from a manifest* in its `docs/integrations.md`), #89 and #90's replacement (#92), and #88's preset (#94). Pin v0.10.2
  first.
- **Read:** the proposal; Decisions above; docs/portainer.md; deploy/portainer/.
- **Status (2026-11-15):** C0 merged (#674; the pin pull request runs first at the next tag release and needs the
  repository setting *Allow GitHub Actions to create and approve pull requests*), C1 (#683), C2 and C3 (#682, by the
  M-16 session), C4 (#684; the upgrade note for hand-made Open WebUI clients is in `docs/upgrade-guide.md`'s
  *Unreleased*), C5 (the PR that says so: `POST /api/embedding-profiles`, *Create profile* on *Models*, the Home card;
  the seed and its five variables gone; review nits #688), C6 (the PR that says so: the one-shot `rag-openwebui-setup`
  mints the feedback key with a session token signed with Open WebUI's secret, as the spike on #667 found;
  `UME_RAG_OPENWEBUI_ADMIN_API_KEY`, `_PASSWORD_AUTH` and `openwebui-feedback-key.sh` gone; #689), C8 (#690: profile
  `proxy`, `rag-proxy` nginx with the operator's certificate from `UME_RAG_PROXY_CERTS_DIR`, a host directory by the
  user's choice; upstream re-resolution #692), C9 (#691: each service's ceiling is its credential key's residencies from
  the context, `OnPrem` before the first one; `Gateway:MaxResidency` gone), C10 (#693: `Settings.Identity.RoleGroups`,
  *Settings → Role groups*; four group variables gone, the platform-admin one stays as the break-glass), C7 (#727, gateway
  v0.10.2: the gateway's migrations create the three credential keys from `UME_INTEGRATION_MANIFEST`, rag-bootstrap
  copies them; preset hand-off thekim1/llmgateway#95), C12 (#731: `organisation_name` from the context). In review: C11
  (#740, replaces #732: `integration_api_url` from the context, key sent only over `https`). Left: C13. The M-16 session
  continues and announces each step on #667.

### [ ] M-20 Setup wizard: sign-in and the last deploy values set in the browser (user, 2026-11-15)
- **Do:** the steps S0–S8 of [`../proposals/setup-wizard.md`](../proposals/setup-wizard.md) §8 in their order, one PR
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
  thekim1/llmgateway#102 and #103, waiting for the user's merge and a gateway release. The context's `oidc_authority` as the wizard's suggestion is merged (#779). **Open:** Ume.Rag's side of the rest when the gateway releases it ( `UME_DOMAIN` for all five names, `stack.env.example` at 1 CHANGE value); the Compose host
  firewall reaches only the provider of `--oidc-authority` (documented; an allow-list is a follow-up); S8 (the
  gateway, WG1–WG4, on the user's word). Stream b
  was told on #759.

