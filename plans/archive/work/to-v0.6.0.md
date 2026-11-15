# Finished queue items up to v0.6.0

The ticked items of the three work queues, moved here unchanged when `v0.6.0` was released (2026-11-15, session 38).
History: it does not bind. An item is found by its ID (`A-35`, `B-42`, `M-12`); the queues in `plans/work/` hold only
open and waiting items.

## Stream A (`stream-a.md`)

### [x] A-41 The Integration API read-back on the platform's Gateway view; the cost runbook after #642 (#654)
- **Do:** the platform's *Gateway models* view (A-38) shows the read-back from `GET /api/organisation/sync`'s
  `integration` (`IntegrationReadback` in `src/admin-ui/src/api/types.ts`): not configured, working, or the last
  refusal's reason (`401`, `403` scope missing, `429`, a failure), as admin §4.5 and §8.6 have the *Gateway* page do;
  the texts in `labels.ts`/`messages.ts` (stream b's paths: a `main-session/` branch, or ask stream b). The cost
  runbook's "as built for now" lines (A-32, #641) follow #653: the shared-key note shows only when more than one
  assistant or knowledge base holds the key, and the read-back's reason is on the Gateway view.
- **Paths:** `src/admin-ui/src/platform/**`, `docs/runbook.md`; `src/admin-ui/src/utils/labels.ts`, `messages.ts`.
- **Done when:** the three states show on the view (a Vitest spec each), checked in the browser on the local stack;
  the runbook matches; `verify.yml` green.
- **Waits on:** nothing (#653 is merged).
- **Read:** admin §4.5, §8.6; #654; #653's body.

## Main (`main.md`)

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

### [x] M-19 Admin UI review: Home, lists, audit log, assistant page (user, 2026-11-15; `proposals/admin-ui-review.md`)
- **Do**, four parts on `main-session/` branches (stream b's paths, announced on #667):
  - **M-19a** assistant-page wording (UI4 B) and the review's small points (Settings placeholders where the value in
    force is known, Feedback and Costs filter layout, Models table heading and "Set here", Organisation wording, the
    source page's personal-data table and failed-run link, the knowledge base Overview's *Add contact* and linked
    *Used by*);
  - **M-19b** list columns and rows as links (UI2 B);
  - **M-19c** Home: Live cards, health, folded checklists (UI1 B);
  - **M-19d** audit log: names, plain-word events, pickers (UI3 A).
- **Done when:** each part's PR is merged with its tests and a check in the browser on the dev data's copy.
- **Read:** admin §5.3, §5.5.1, §5.5.2, §5.5.4, §9.1; Decisions "Admin API and UI".
- **Status (2026-11-15):** all four parts merged in #724 (built in parallel, merged on one branch); deviations settled in
  the design. Not done: Settings placeholders (the API doesn't return the value in force) and document links in the
  personal-data table (no document page). Cosmetic, open: a Live card's numbers show in link colour, and *Hide* wraps
  to its own line on a phone.
