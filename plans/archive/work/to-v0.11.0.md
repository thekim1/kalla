# Finished queue items up to v0.11.0

The ticked items of the work queues, moved here unchanged when `v0.11.0` was released (2026-11-24).
History: it does not bind. An item is found by its ID (`B-64`); the queues in `plans/work/` hold only open and waiting
items.

## Stream B (`stream-b.md`)

### [x] B-61 Cost per outcome (F10) (#1018)
- **Do:** the table `chat."AssistantDailyOutcomes"` (data-model §2.3, with the admin role's grant and §8's
  retention); the writer's upsert per (day, assistant, client, level, outcome) with the turns and their summed
  gateway and estimated cost, usage-only turns as `unavailable` or `error` (chat §12.2); the test panel's upsert
  (admin §4.9); `outcomes` in `GET /api/assistants/{id}/costs` and the table by outcome on the *Cost* card
  (admin §8.6).
- **Paths:** `src/Kalla.Api/**`, `src/Kalla.AdminApi/**`, `src/Kalla.Infrastructure/**` (b's),
  `src/Kalla.Domain/**` (b's), `src/Kalla.MigrationService/` (not `Templates/**`), `src/admin-ui/**` (not
  `src/platform/**`), `tests/**`.
- **Done when:** chat §18.9's *Cost per outcome* test passes, with the rows' cost summed equal to the day's
  `AssistantDailyUsage`; an API test for `outcomes` and the department scope; the card shown in a browser on the demo
  seed.
- **Waits on:** nothing.
- **Read:** Decisions "Cost per outcome"; chat §12.1, §12.2, §18.9; data-model §2.3, §8; admin §4.9, §8.6.

### [x] B-62 Content-gap report (F7) (#1022)
- **Do:** the table `chat."AssistantDailyGaps"` (data-model §2.3, §8's retention); the nearest hit on `ChatTurn`
  (the first retrieval result's collection and document for a `not-found` or `answered-general` turn, *no hit*
  otherwise) and the writer's upsert (chat §12.1, §12.2), never for the test panel; `GET /api/assistants/{id}/gaps`
  with its CSV and the *Content gaps* card on the Statistics tab (admin §8.7).
- **Paths:** as B-61.
- **Done when:** chat §18.9's *Content gaps* test passes, with the negative proof that no row or column holds the
  question; API tests for the ordering, *no hit*, a deleted document, the period limit and the department scope; the
  card shown in a browser on the demo seed with a few not-found turns.
- **Waits on:** nothing (B-61 first if both are taken at once: the same writer step).
- **Read:** Decisions "Content-gap report"; chat §6.2, §12.1, §12.2, §18.9; data-model §2.3, §8; admin §8.6,
  §8.7.

### [x] B-64 Topics picked from the sections found, on the Topics tab (#1059, #1060)
- **Do:** admin §4.5's `GET /api/collections/{id}/topic-suggestions` (first level, `under=` for one section's next
  level, sections under 5 documents left out, `coveredBy` through A-42's matcher, owning department only, nothing
  logged) and the `pending` flag on `GET …/topics`; on the Topics tab (§5.5.2) the list grouped by tag with the rule in
  words and its source, the *Suggested from {source}* blocks with *show the sections under it*, the editable proposed
  tag, and **Add N topics** saving through the publication plan of each affected processing as one confirm (the
  plan's own count; copy-on-write as §6.6). Controls for `EditPipelines` of the owning department only; read-only
  for `Edit` and `Read`; a shared department sees tags and counts only.
- **Paths:** `src/Kalla.AdminApi/**`, `src/admin-ui/**` (not `src/platform/**`), `tests/**` (b's).
- **Done when:** API tests for the suggestions (both levels, the cut-off, an upload source's folders, `coveredBy`,
  404 for a shared department, the roles) and for `pending`; Vitest for the tab (picking, the tag check, two sections
  on one tag, the read-only view); shown in a browser on the demo seed: two sections picked, saved, *pending*, then
  counted after the re-chunk.
- **Waits on:** A-42.
- **Read:** Decisions "Topics editor"; admin §4.5, §5.5.2, §6.6, §4.8; processing §7.
- **Taken** 2026-11-24, `b/64-topic-suggestions-api` (part 1: the suggestions route, `pending`, the client; part 2: the
  Topics tab).

### [x] B-66 Downvotes per document (F6) (#1023)
- **Do:** Decisions "Downvotes per document": the table `chat."DocumentDailyFeedback"` (data-model §2.3) in a migration
  of its own; its delta in `POST /v1/feedback` and the Open WebUI importer (chat §14.1, §14.2: one count per cited
  document, in the rating's transaction, never below 0); admin §4.5's `GET /api/collections/{id}/feedback` (+ CSV);
  the Feedback page's `documentId` filter (§8.1); the *Feedback on documents* card on the knowledge base's Overview
  (§8.8, §5.5.2). The attention kind (§5.5.1) goes into B-59's list when that has landed, else in B-59.
- **Paths:** `src/Kalla.Api/**`, `src/Kalla.AdminApi/**`, `src/Kalla.Infrastructure/**` (b's), `src/Kalla.Domain/**`
  (b's), `src/Kalla.MigrationService/` (not `Templates/**`), `src/admin-ui/**` (not `src/platform/**`), `tests/**`.
- **Done when:** API tests: a downvote on an answer citing two documents counts each once (also with two chunks of one);
  a changed rating moves one count; an imported rating counts like an API one; the endpoint's scope (owning department,
  not a shared one) and CSV; Vitest for the card and the filter; shown in a browser on the demo seed with ratings sent
  through `POST /v1/feedback`.
- **Waits on:** nothing (B-59 only for the attention kind).
- **Read:** Decisions "Downvotes per document"; chat §14; data-model §2.3; admin §4.5, §5.5.1, §5.5.2, §8.1, §8.8.

### [x] B-72 Progress chunk and timings (`kalla.progress`, `kalla.timings`) (#1021)
- **Do:** chat §3.8's two request members (a value that is not a boolean: 400 `invalid_request` with its `param`) and
  §9.10: on a streamed turn that gets a model answer, one `kalla` chunk `{"progress":"writing"}` as the first byte,
  written after step 13's checks have passed (§4.1) and before the role chunk, with the related documents (no `cited`,
  no `number`) when those are asked for too; none for a fixed text or a non-streamed request. `timings` in the answer's
  `kalla` from the stage times already measured (§17), and the `Server-Timing` header on a non-streamed answer.
- **Paths:** `src/Kalla.Answering/**`, `src/Kalla.Api/**`, `tests/**` (b's).
- **Done when:** chat §18.9's *Progress chunk* and *Timings* tests pass, with the two negative proofs: a request
  without the members is byte-identical to today's stream, and a refused answer call (FakeGateway `BudgetExceeded`)
  is HTTP 402 with no byte of a stream.
- **Waits on:** nothing.
- **Read:** Decisions "Progress, timings and the nearest subject in the chat extension"; chat §3.8, §4.1 (step 13),
  §9.1, §9.6, §9.10, §17, §18.9; #959.

### [x] B-73 Topics on related documents and the nearest subject (`kalla.nearest`) (#1026)
- **Do:** chat §9.7's `topics` on every related-document entry (the best-ranked chunk's tags as stored; carried on the
  retrieval result if it isn't there yet) and §9.11: the request member `kalla.nearest` and the answer's `nearest`
  (`{ collection, topics }` of the turn's nearest hit for `not-found` and `answered-general`, `null` otherwise), from
  the value the turn hands the conversation writer (B-62), following the links as `kalla.topics` narrowed them.
- **Paths:** `src/Kalla.Answering/**`, `src/Kalla.Retrieval/**`, `src/Kalla.Api/**`, `src/Kalla.Domain/**` (b's),
  `tests/**` (b's).
- **Done when:** chat §18.9's *Topics and the nearest subject* tests pass, with the negative proof that `nearest`
  holds no title, address or document id.
- **Waits on:** B-62 (the nearest hit on the turn); the `topics` on related documents can go first.
- **Read:** Decisions "Progress, timings and the nearest subject in the chat extension", "Content-gap report", "Topics
  per request"; chat §3.8, §9.7, §9.11, §12.1, §18.9; processing §7; #959.

### [x] B-83 Replace by name: a known file's upload replaces it by default (admin §5.5.7 point 6; support case SA-11) (#1017)
- **Do:** in the upload's file list (drop zone and picker), match each added file's relative path against the source's
  live files before sending; when any match, one dialog lists them and asks *Replace the existing files* (default) or
  *Keep both*. *Replace* sends each match as that file's replacement, the way the row's *Replace* does (the same
  `X-Upload-Id`), so the document keeps its id; the rest upload as new. Byte-identical files stay skipped
  (`upload.duplicate`). Text in `labels.ts` and `messages.ts`.
- **Paths:** `src/admin-ui/**` (not `src/platform/**`), `src/Kalla.AdminApi/**` if a lookup by name is needed for a
  source with more files than the list has loaded, `tests/**`.
- **Done when:** Vitest for the dialog (a match proposes *Replace* by default, *Keep both* uploads as new, no match
  shows no dialog, a folder's relative path is part of the match); an AdminApi test if an endpoint is added; shown in a
  browser at 1,440 and 390 px; the local e2e upload spec passes.
- **Waits on:** nothing. Before DoIT's editors start uploading for the test chatbot (roadmap T4).
- **Read:** admin §5.5.7; connectors §9.3; `cases/support-assistant/06-gaps.md` SA-11 and `03-updates.md` §3.

### [x] B-74 The phone's bottom menu as admin §5.2 says (UX pass step 0) (#1025)
- **Do:** `mobileTabs` in `router/nav.ts` gets Assistants and Feedback beside Home and Knowledge bases (Home, Knowledge
  bases, Assistants, Feedback, Menu), each filtered by role like the side menu (Feedback: `FEEDBACK_ROLES`); *Menu*
  stays highlighted for every other page.
- **Paths:** `src/admin-ui/**` (not `src/platform/**`), `tests/**`.
- **Done when:** `router/nav.spec.ts` covers the four tabs and the role filter (no Feedback tab for an editor role);
  shown at 390 and 320 px in the three themes, with 44 px targets.
- **Waits on:** nothing.
- **Read:** admin §5.2, §5.12 rule 10.

### [x] B-75 Field widths, grey zeros, repeated texts and the renames (UX pass step 1) (#1027, #1029)
- **Do:** admin §5.12 rules 3, 5 and 8 on every page: fields sized to their answer (numbers 8–12 characters, names and
  selects 320–400 px, form columns at most 680 px) in every form and drawer; zeros, "none", "never" and "not yet" as
  grey text; texts that repeat on each row said once (the file list's *Not scanned for malware* in the summary,
  §5.5.7); radio cards selected with the accent border and soft background; section titles inside their cards; the
  breadcrumb on detail pages; and the names of admin §5.7 *Names on the pages* that need no new component (search
  placeholder, greeting, *Demo data*, the list columns, *Held and quarantined*, Organisation and Apps and keys, the
  Settings groups of §5.10 and *Advanced: edit as JSON*, the Models page's *Models* and *Embedding* tabs with *Show
  test models*).
- **Paths:** `src/admin-ui/**` (not `src/platform/**`), `tests/**`. The Settings groups (`platform/settingsPage.ts`),
  the Models tabs and the Apps and keys view are in `src/admin-ui/src/platform/**`: a `stream-a` issue with the change.
- **Done when:** Vitest for the changed views' texts and the grey zeros; every page of the menu shown at the three
  widths and themes; the local e2e specs touched pass.
- **Waits on:** the code-quality fixes #3 and #5 of `proposals/code-quality-top7.md` (#5 is #981), which change the same
  views.
- **Read:** admin §5.7, §5.10, §5.12 (rules 3, 5, 8, *Every page*); `proposals/admin-ui-ux-pass.md` *Page by page*.

### [x] B-76 One primary action and ⋯ menus in headers and rows (UX pass step 2, UX4 C) (#1032)
- **Do:** `MenuButton` (APG menu button; a bottom sheet on a phone, admin §5.12's accessibility table); the headers of
  the knowledge base (§5.5.2: *Publish* or *Add content*; *Edit name and contact*, *Change department*, *Delete* in ⋯),
  the assistant (§5.5.4: *Publish draft* and *Compare with published*; *Edit name and contact*, *Copy*, *Change
  department*, *Archive*, *Delete* in ⋯) and the source (§5.5.3: *Fetch now* for a crawl or address list, *Upload
  files* for an upload source, which loses *Fetch now*; its section order); the row ⋯ menus (file rows: *Replace*,
  *Download*, *Delete*; Versions: *Restore*); the bulk bar only while rows are selected.
- **Paths:** `src/admin-ui/**` (not `src/platform/**`), `tests/**`.
- **Done when:** Vitest for `MenuButton` (keyboard, Esc returns focus, the accessible name) and for each header's
  primary action by state and role; the accessibility spec covers the open menu and the sheet; shown at the three
  widths and themes; the local e2e specs touched pass.
- **Waits on:** B-75; the code-quality fixes #3 and #5.
- **Read:** admin §5.5.2, §5.5.3, §5.5.4, §5.5.7, §5.12 (rule 1, the accessibility table).

### [x] B-77 The save bar on every form (UX pass step 3, UX6 A) (#1035)
- **Do:** `SaveBar` (admin §5.12 *Saving*: *n unsaved changes · Discard · Save changes*, the consequence when there
  is one, sticky, fixed above the bottom menu on a phone, `scroll-padding-bottom`) and a dirty check per form, on the
  assistant's draft, search settings, sharing, audience, storage and each Settings section; the guard that asks
  before leaving a page or tab with unsaved changes; the per-form save names leave `labels.ts`.
- **Paths:** `src/admin-ui/**` (not `src/platform/**`), `tests/**`. The Settings sections' forms
  (`SettingsSectionForm.vue` and the dedicated ones) are in `src/admin-ui/src/platform/**`: a `stream-a` issue to put
  the bar on them.
- **Done when:** Vitest for `SaveBar` (count, *Discard* asks once, the announcement) and the leave guard (route and
  tab change); the accessibility spec covers the bar; shown at the three widths and themes, a focused field never
  under the bar; the local e2e specs touched pass.
- **Waits on:** B-76; the code-quality fixes #3 and #5.
- **Read:** admin §5.5.4 (*Wording*), §5.7, §5.12 (rule 7, *Saving*).

### [x] B-78 The assistant page: five tabs, the test beside the draft (UX pass step 4, UX3 C) (#1041, #1043)
- **Do:** admin §5.5.4: the tabs *Build*, *Quality*, *Versions*, *Access*, *Usage* (`?tab=` values renamed, every
  link to the old ones changed); the test panel beside the draft, sticky, folded under the form below 1,280 px and a
  full-height sheet from the save bar on a phone, keeping all of §4.9; *General knowledge* as one radio scale with the
  chosen level's help; knowledge bases as rows with *Limit to topics* and a row ⋯; *Access* as one story (audience,
  apps, storage, model name with copy, *Check access* folded); B-57's test questions, B-58's answer evaluation and
  B-62's content gaps on *Quality*; the cost card (with B-61's outcomes), questions, not found and ratings on *Usage*;
  the *Create assistant* drawer's *Start from* cards (§5.12 *Drawers*).
- **Paths:** `src/admin-ui/**` (not `src/platform/**`), `tests/**`; `src/Kalla.AdminApi/**` only if *Usage* needs a
  field the API lacks.
- **Done when:** Vitest for the tabs, the scale (V36's extra confirmation kept) and the test panel's placement; the
  accessibility spec over each tab and the sheet; shown at the three widths and themes; the local e2e specs touched
  pass. Hand-offs: `docs/runbook.md` names *Assistant → Statistics → Cost* (a `stream-a` issue); the NVDA checklist
  (`plans/work/nvda-pass.md`, the main session's) is rewritten once this and B-79 have landed (an issue labelled
  `main`).
- **Waits on:** B-77; B-58, B-61, B-62 (their cards move here); the code-quality fixes #3 and #5.
- **Read:** admin §4.9, §5.4, §5.5.4, §7.4, §8.6, §8.7, §5.12; V36.

### [x] B-79 The knowledge base page: six tabs and the Overview's tiles (UX pass step 5, UX2 C) (#1044, #1045)
- **Do:** admin §5.5.2: the tabs *Overview*, *Content*, *Documents*, *Topics*, *Quality*, *Sharing* (`?tab=` values
  renamed and the setup checklist's links with them); `SummaryTile` and the Overview's four tiles, then *Get started*,
  the sources, the *Gateway key*, *Cost* and *Feedback on documents* cards; *Content* with the sources and the file
  list (§5.5.7), opening on the file list when an upload is the only source, and *Create knowledge base* with *Files*
  opening there; *Quality* with test questions, the evaluation and *Tune the search*; the dot on a tab that needs
  action; the *Create knowledge base* and *Add source* drawers as §5.12 *Drawers*.
- **Paths:** `src/admin-ui/**` (not `src/platform/**`), `tests/**`.
- **Done when:** Vitest for the tabs (a shared knowledge base still sees Overview and Topics only), the upload-only
  case opening on the file list, the tiles and the tab dot's text; the accessibility spec over each tab; shown at the
  three widths and themes on the demo seed (one upload-only knowledge base, one with a crawl source); the local e2e
  specs touched pass. Hand-offs: the NVDA checklist as B-78.
- **Waits on:** B-78; B-66 (its Overview card); the code-quality fixes #3 and #5.
- **Read:** admin §5.4, §5.5.2, §5.5.7, §7.1, §8.6, §8.8, §5.12.

### [x] B-80 Filters as chips, kept in the address (UX pass step 6, UX5 C) (#1047, #1053)
- **Do:** admin §5.12 *Filters*: `FilterChip` and `PeriodPicker`, and one composable that reads and writes the
  filters in the address, on Feedback, Costs (*Export* with *By day* and *By month* in the header), the Audit log
  (*Who* and *Type of object* as chips, the rest under *More filters*), the Map (with B-67's switch on the row if it
  has landed) and the lists' search; *Clear filters*; the counts in a polite live region.
- **Paths:** `src/admin-ui/**` (not `src/platform/**`), `tests/**`.
- **Done when:** Vitest for the chips' names, the period presets and custom range, and the address round trip (a
  reload and back keep the filter, a change moves no focus); the accessibility spec covers the open picker; shown at
  the three widths and themes; the local e2e specs touched pass.
- **Waits on:** B-79; the code-quality fixes #3 and #5.
- **Read:** admin §5.5.5, §5.5.6, §8.1, §8.6, §9.1, §5.12.

### [x] B-81 The side menu and Home (UX pass step 7, UX1 A and UX6 C) (#1056)
- **Do:** admin §5.2: Home alone at the top with its badge, the groups *Build*, *Follow-up* (Map moved in) and
  *Administration* (`NAV_GROUP`), Models with the icon `memory`; admin §5.5.1: Home's order (greeting with *Your
  access*, the notices, *Needs attention*, *Get started*, *Assistants in use* as a table, *Health* as one line), the
  *Your access* card gone. The theme switch stays where it is (the user menu is proposed to Fors: an issue on
  thekim1/fors, listed in the pull request).
- **Paths:** `src/admin-ui/**` (not `src/platform/**`), `tests/**`. Models' icon is in `platform/nav.ts`: a `stream-a`
  issue.
- **Done when:** `router/nav.spec.ts` for the groups and their order by role; Vitest for Home's sections (a new
  installation shows the notices, *Get started* and *Health*; zeros grey); shown at the three widths and themes; the
  local e2e specs touched pass.
- **Waits on:** B-59 (*Needs attention*); B-80; the code-quality fixes #3 and #5.
- **Read:** admin §5.2, §5.5.1, §5.10, §5.12.

### [x] B-82 The Publish dialog: the diff, the warnings and the test run's cost (admin §5.5.4, §7.4) (#1033)
- **Do:** **Publish draft** opens a dialog before it publishes: the structured diff against the published version
  (B-56's `GET …/versions/{n}/diff?against=draft`, the view of *Compare*), the draft's remaining warnings (a refusal
  blocks the button) and the test run's estimated model calls when the assistant has test questions (B-57). V36's
  confirmation joins this dialog instead of being a dialog of its own; the 409 `draft_base_changed` dialog stays. A
  first publish (nothing published yet) shows the warnings and the cost only.
- **Paths:** `src/admin-ui/**` (not `src/platform/**`), `tests/**`.
- **Done when:** Vitest for the dialog (a diff shown, a refusal disables *Publish*, V36's line in the same dialog, a
  first publish without a diff); shown at the three widths and themes; the local e2e specs that publish pass.
- **Waits on:** B-76 (the header's primary action). Before B-78, which moves the dialog with the header.
- **Read:** admin §5.5.4 (*Publish draft*), §7.4 (*Cost*), §4.5 (the diff); Decisions "Admin API and UI".
