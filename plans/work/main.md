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

### [x] M-14 Restructure the plan files (user, 2026-11-13; plan review of session 33)
- **Do, in order:** (1) the roadmap as the one place for the plan ahead; (2) finished queue items to
  `archive/work/`; (3) HANDOVER to state only, the rest to `main-handbook.md`; (4) non-binding papers to
  `plans/proposals/`, the goal statement to `plans/goal.md`; (5) per design document: the process sections (summary of
  decisions, milestones, changes to other documents, answered questions, deferred lists) to the archive, each keeping
  its number and heading with a pointer; (6) the V-rule tables of admin and data-model as one heading per rule. Later,
  as each area is next worked on: stable IDs over §-numbers in new citations, triage of `to-verify.md`, the planning
  section of `decisions.md` into the roadmap.
- **Rule:** no section is renumbered (§-numbers are cited about 2,800 times in code); `scripts/check-doc-refs.py` green.
- **Done when:** (1)–(6) merged; the later ones are noted in the handbook.
- **Status (2026-11-13):** (1)–(4) merged in #655 (`01-general-idea.md` stays in the root: code and the citation check
  read it by path); (5)–(6) in #656 (the citation check follows a moved section's "Moved to" line). The later ones are in
  the handbook's *Lessons* (stable IDs) and *Plan files*.

### [x] M-15 The test chatbot (user, 2026-11-13; roadmap *Now*, T0–T6)
- **Withdrawn (user, 2026-11-14):** the user's team sets up the test chatbot themselves; the agents only fix what they
  report. The text below is kept for reference.
- **Do:** support the user's manual deploy (T0–T2: a checklist from `docs/production-setup.md`'s read-backs, and answers
  when something fails); with DoIT, set up the knowledge base and the assistant (T3–T4, the case plan's settings) and
  check them in the test panel; collect the findings (T6) into the case plan, the upload gaps and the stream queues.
- **Waits on:** T0–T1 (the user's deploy) and DoIT's files.
- **Done when:** the test group has used it, and the findings are written back (T6).

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
  deployment* once M-17's C4 lands; `Settings.Identity.RoleGroups` as a Settings section once C10 lands; #677 (Processing's
  concurrency is not read).

### [ ] M-17 Deploy configuration: settings, automatic setup and the gateway (user, 2026-11-13; #662)
- **Do:** the steps C0–C13 of [`../proposals/deploy-configuration.md`](../proposals/deploy-configuration.md) §5 in their
  order, one PR per step, each step's paths, done-when and stream as listed there. Decided: Decisions "Deploy
  configuration", "Residency per level", "Role groups as a setting". C0–C6, C8–C10 and C13 need nothing more; C7 waits
  on llmgateway#87 (G1), C11 on #90 (G5), C12 on #89 (G4), each released and pinned.
- **Mind:** M-16 (another session) builds the Apps and Settings pages: C4 removes the wizard's Open WebUI type and makes
  its client "Managed by the deployment"; C5's create form goes on the Models page and C10's role groups on the
  Settings page (§5.10). Main updates identity §4.7 (C9), §5.2 (C4) and repo-and-deployment §6 before the step that
  changes them.
- **Done when:** C0–C13 merged (C7, C11, C12 once their gateway release exists); a fresh Portainer stack boots with the
  proposal's §2 "Net effect" (six values, no pasted keys once G1 is pinned), checked by C0's release boot test.
- **Waits on:** nothing for C0; the gateway releases for C7, C11, C12.
- **Read:** the proposal; Decisions above; docs/portainer.md; deploy/portainer/.
- **Status (2026-11-14):** C0 merged (#674; the pin pull request runs first at the next tag release and needs the
  repository setting *Allow GitHub Actions to create and approve pull requests*), C1 (#683), C2 and C3 (#682, by the
  M-16 session), C4 (#684; the upgrade note for hand-made Open WebUI clients is in `docs/upgrade-guide.md`'s
  *Unreleased*). The M-16 session continues with the next steps in order and announces each on #667.

### [x] M-18 Remove `Settings.WorkerConcurrency` until P2 (user, 2026-11-14; #677)
- **Do:** drop the member from `PlatformSettings`, the EF model (a migration dropping the column; no data step), the
  admin's `PUT/GET /api/settings/worker`, the Settings page's Processing section, and their tests and fixtures.
  Per-instance concurrency stays the deployment's `Worker:Concurrency:<class>`.
- **Paths:** `src/Ume.Rag.Domain/Entities/CollectionEntities.cs`, `src/Ume.Rag.Persistence/**` (model, migration),
  `src/Ume.Rag.AdminApi/SettingsEndpoints.cs`, `src/admin-ui/src/platform/**`, their tests (crosses streams: a
  `main-session/` branch).
- **Done when:** `grep -rn WorkerConcurrency src tests` finds only the dropping migration; the Settings page's
  Processing section has no concurrency; backend and admin-ui tests green.
- **Read:** data-model §2.1 (`Settings`), engine §9.1.
