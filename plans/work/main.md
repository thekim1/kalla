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

### [ ] M-22 Deploy simplification (user, 2026-11-16)
- **Do:** [`../proposals/deploy-simplification.md`](../proposals/deploy-simplification.md), starting with S1 (one
  bootstrap for Compose and Portainer; decided 2026-11-16, Decisions "One deployment bootstrap"; the proposal's S1
  lists its five steps, one pull request each), S3 with S9 (Open WebUI as a profile in Compose, off unless asked for
  and invisible when off) and S4a (delete the upgrade paths older than the VM's version). Upgrades may break, but each
  breaking change gets an upgrade path where one is feasible (user, 2026-11-16). The setup wizard works the same in
  every form. M-17's C13 is absorbed by S1's step 3. S2, S5 and S7 wait for the user's decision.
- **Paths:** `deploy/**`, `docs/**`, `.github/workflows/**` (stream a's: `main-session/<slug>` branches, no open `a/`
  work at the start); the admin UI's texts for S9 are stream b's (announce on an issue first).
- **Done when:** a first install with Compose needs the same few values as the Portainer stack and no bootstrap logic
  exists twice; an installation without Open WebUI shows no trace of it; each step shown by the deploy tests
  (`deploy-checks`, the boot test) and, for a stack-file change, a boot.

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
