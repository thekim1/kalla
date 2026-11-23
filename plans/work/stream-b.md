# Stream B: work queue

Stream B owns the schema, the admin API, every admin page, retrieval, knowledge and chat
(`.github/stream-ownership.txt`). Rules: [`../../AGENTS.md`](../../AGENTS.md). Context: [`../roadmap.md`](../roadmap.md).

**How to use:** take the first item that is not ticked and not waiting. One PR per item (branch `b/<id>-<slug>`), and
tick it here in that PR (`[x] … (#PR)`). Record `to-verify` rows in [`results-b.md`](results-b.md). If an item is
waiting, take the next one. Live items need the inputs named in *Waits on*; don't start them early.
An item someone has started says **Taken** with the date and branch; leave it to them.

Finished items move to [`../archive/work/`](../archive/work/) at each release (the main session); find one by its ID.

## Code

## P2 wave 1: assistants and answer quality (Decisions "P2 starts before go-live")

`v0.9.0` is released, so these can start (the roadmap's *P2 Broaden*). Each adds its
tables in one migration of its own (no backward compatibility before production). The UI texts go in `labels.ts` and
`messages.ts`; show each page in a real browser before the PR (FakeGateway stack with the demo seed).

## Support chatbot F10 and F7 (user, 2026-11-21)

After P2 wave 1, or beside it when a wave item waits. Each adds its table in a migration of its own.

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

### [ ] B-62 Content-gap report (F7)
- **Taken:** 2026-11-23, the main session on the user's machine (#1011), branch `b/B-62-content-gaps`.
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

## Topics editor (user, 2026-11-22; Decisions "Topics editor")

Beside P2 wave 1: take B-64 when a wave item waits, or after it. `proposals/topics-editor.md` is the record of why.

### [ ] B-64 Topics picked from the sections found, on the Topics tab
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

### [ ] B-65 The topic form, the match preview, and the read-only Topics field
- **Do:** admin §4.5's `POST /api/collections/{id}/topic-rules/preview`; on the Topics tab the *Add topic* / *Edit* form
  (§5.5.2: under the address, title contains, file type, source, Advanced with every rule kind, a rule the form can't
  word shown as JSON under Advanced) with "Matches N of M documents" and the first ten, the 0-match warning asked
  again at **Save**, and remove; in `PipelineCard` the `pathRules` parameter shown read-only in words with a link to
  the tab, its stored value kept when other parameters are published (§5.8).
- **Paths:** `src/Kalla.AdminApi/**`, `src/admin-ui/**` (not `src/platform/**`), `tests/**` (b's).
- **Done when:** API tests for the preview (each rule kind, an invalid regex 400, the sample cap, the roles); Vitest
  for the form's round trip of every rule kind and the 0-match flow, and for the read-only field; shown in a browser.
- **Waits on:** B-64.
- **Read:** Decisions "Topics editor"; admin §4.5, §5.5.2, §5.8; processing §7.

## Support chatbot F6 (user, 2026-11-22)

### [ ] B-66 Downvotes per document (F6)
- **Taken:** 2026-11-23, the main session on the user's machine (#1011), branch `b/B-66-document-downvotes`.
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

## P2 wave 2: assistants, the rest (user, 2026-11-22; Decisions "P2 starts before go-live")

After wave 1, or beside it when a wave-1 item waits.

### [ ] B-67 The drawn Map
- **Do:** admin §5.5.6's drawing beside the existing table (**Table** / **Drawing**): one column per stage, plain SVG
  lines next to lists that carry the same relations as text, selecting an item highlights what it feeds and what feeds
  it; from the same `GET /api/organisation/departments/{id}/map` (sources added to its rows if missing).
- **Paths:** as B-54.
- **Done when:** Vitest for the layout and the text relations; the accessibility spec over the drawing in three themes;
  shown in a browser on the demo seed.
- **Waits on:** nothing.
- **Read:** admin §5.5.6, §10.1, §10.2 (no graph library).

### [ ] B-68 Unused assistants: *Archive* and *Keep*
- **Do:** admin §5.5.1's unused-assistant attention item (published ≥ 90 days ago, no message in 90 days in
  `AssistantDailyTotals`) with **Archive** (`…/archive`) and **Keep** (`POST /api/assistants/{id}/keep`, the column
  `Assistants.UnusedKeptUntil` in a migration of its own, audited `assistant.keep`).
- **Paths:** as B-54.
- **Done when:** API tests: raised, gone after Archive, not raised while kept, raised again after 90 days; shown in a
  browser.
- **Waits on:** B-59 (the attention list).
- **Read:** admin §4.5, §5.5.1; data-model `Assistants`.

### [ ] B-69 The evaluation trend chart
- **Do:** copy the gateway's `components/usage/TimeSeriesChart.vue` (and its spec) unchanged; on Test questions and
  quality (admin §7.1) the trend of recall@10 and MRR over the latest 30 runs as a chart, the `human` layer first, with
  the table of the same runs under it as its text view.
- **Paths:** as B-54.
- **Done when:** Vitest for the chart's data mapping; the accessibility spec over the page in three themes; shown in a
  browser on the demo seed with several evaluation runs.
- **Waits on:** nothing.
- **Read:** admin §7.1, §10.1; the gateway's `TimeSeriesChart.vue`.

## Sources (user, 2026-11-22)

### [ ] B-71 The admin's next run from the shared schedule rule
- **Do:** `Kalla.AdminApi`'s `scheduleInForce` and `nextRunAt` (#935) call A-43's rule in `Kalla.Pipelines` instead of
  repeating it; `CronSchedules` keeps only the save-time validation (`IsValid`, `Invalid`), and the AdminApi's own
  Cronos reference goes if nothing else needs it. No behaviour change.
- **Paths:** `src/Kalla.AdminApi/**`, `tests/Kalla.AdminApi.Tests/**`.
- **Done when:** `Kalla.AdminApi.Tests` green, `SourceEndpointsTests` unchanged; the tests of the rule itself live in
  `Kalla.Pipelines.Tests` (A-43), not here.
- **Waits on:** A-43.
- **Read:** engine §9.6; #935.

## Chat extension for streaming clients (user, 2026-11-23; #959)

Decisions "Progress, timings and the nearest subject in the chat extension". After the wave items, or beside them when
one waits. Neither changes an answer that doesn't ask.

### [ ] B-72 Progress chunk and timings (`kalla.progress`, `kalla.timings`)
- **Taken:** 2026-11-23, the main session on the user's machine (#1011), branch `b/B-72-progress-timings`.
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

### [ ] B-73 Topics on related documents and the nearest subject (`kalla.nearest`)
- **Taken:** 2026-11-23, the main session on the user's machine (#1011), branch `b/B-73-nearest-subject`.
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

## Uploads (user, 2026-11-23)

### [ ] B-83 Replace by name: a known file's upload replaces it by default (admin §5.5.7 point 6; support case SA-11)
- **Taken:** 2026-11-23, the main session on the user's machine (#1011), branch `b/B-83-replace-by-name`.
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

## Admin UI UX pass (user, 2026-11-23; Decisions "Admin API and UI")

The rules are admin §5.12, the pages §5.2, §5.5.1, §5.5.2, §5.5.4, §5.5.7, §5.7 and §5.10;
[`proposals/admin-ui-ux-pass.md`](../proposals/admin-ui-ux-pass.md) is the record of why, with the mockups. One pull
request per step, in this order, smallest first; each step adapts to what has landed before it (B-55's *Copy*, B-56's
*Compare*, B-64/B-65's *Topics*, B-67's Map switch, B-68's *Archive* and *Keep*). New UI text in `labels.ts` and
`messages.ts`; the four shared components (`MenuButton`, `FilterChip` with `PeriodPicker`, `SaveBar`, `SummaryTile`) go
in `components/ui/` and are offered to Fors (an issue on thekim1/fors) when built.

**Every step's checks** (admin §5.12, *How a change is checked*): shown in a browser at 1,440, 390 and 320 px in the
light, dark and Lumen themes, with a keyboard-only pass and a screen-reader pass (NVDA or VoiceOver) of each changed
page; the accessibility spec and `router/*.spec.ts` updated where the step changes them; the `tests/e2e` specs that
drive a renamed tab, button or filter (`pilot.spec.ts` mostly) run locally (main handbook, *Browser tests locally*)
and fixed in the same pull request, named in its body. **Every step's hand-offs**, listed in the pull request: a
`stream-a` issue when the step renames a tab, button or menu group that `docs/` names (grep `docs/` for the old
names), and a `stream-a` issue for any change in `src/admin-ui/src/platform/**` (stream a's), built against the
design meanwhile.

### [ ] B-74 The phone's bottom menu as admin §5.2 says (UX pass step 0)
- **Do:** `mobileTabs` in `router/nav.ts` gets Assistants and Feedback beside Home and Knowledge bases (Home, Knowledge
  bases, Assistants, Feedback, Menu), each filtered by role like the side menu (Feedback: `FEEDBACK_ROLES`); *Menu*
  stays highlighted for every other page.
- **Paths:** `src/admin-ui/**` (not `src/platform/**`), `tests/**`.
- **Done when:** `router/nav.spec.ts` covers the four tabs and the role filter (no Feedback tab for an editor role);
  shown at 390 and 320 px in the three themes, with 44 px targets.
- **Waits on:** nothing.
- **Read:** admin §5.2, §5.12 rule 10.

### [ ] B-75 Field widths, grey zeros, repeated texts and the renames (UX pass step 1)
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

### [ ] B-76 One primary action and ⋯ menus in headers and rows (UX pass step 2, UX4 C)
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

### [ ] B-77 The save bar on every form (UX pass step 3, UX6 A)
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

### [ ] B-78 The assistant page: five tabs, the test beside the draft (UX pass step 4, UX3 C)
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

### [ ] B-79 The knowledge base page: six tabs and the Overview's tiles (UX pass step 5, UX2 C)
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

### [ ] B-80 Filters as chips, kept in the address (UX pass step 6, UX5 C)
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

### [ ] B-81 The side menu and Home (UX pass step 7, UX1 A and UX6 C)
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

### [ ] B-82 The Publish dialog: the diff, the warnings and the test run's cost (admin §5.5.4, §7.4)
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

## Live (the live block, see the roadmap)


### [ ] B-42 Gateway phase 2: every api and admin call as the object's key, the echo check, the FakeGateway acting (#580, cost-attribution B5)
- **Do:** in the shared client (`src/Kalla.Infrastructure/Gateway/`): each service reads `gateway.features` from its
  own `GET /v1/fors/context` with its credential key, at start and every 5 minutes with `If-None-Match`, held in memory
  (identity §4.7); the call's object key travels on the request and `ResidencyCeilingHandler` sets
  `x-fors-on-behalf-of` from it only while the features contain `act-as-key`; on every `2xx`/`304` answer the echo is
  parsed and compared as a GUID, and a missing or different one throws before use with event 5011 (`Step`,
  `AssistantId` or `CollectionId`, `GatewayKeyId`, `GatewayRequestId`) while `Gateway:RequireDelegationEcho` (default
  `true`) is on; the refusals of gateway-changes §5.7 on an acted call (`403 delegation_not_allowed`, `403
  key_disabled`, `403 key_revoked`/`key_expired`, `402 budget_exceeded` with the gateway's message) surface as one typed
  refusal the callers map (B-44, A-34), and `400 invalid_request` is logged at Error and never retried. kalla-api: every
  call of a turn as the assistant's `GatewayKeyId` (answer, condense, long-question condense and follow-ups in
  `GatewayChatClient`, the query embedding in `GatewayQueryEmbedder`, rerank in `GatewayReranker`, `ChatStreaming`),
  a Knowledge API caller without an assistant as the first searched collection's key (knowledge §5.2); kalla-admin: the
  test panel as the assistant's key, the retrieval test as the collection's (cost-attribution §4.1). The FakeGateway's
  phase 2 of repo-and-deployment §4.3: features `act-as-key` and `run-metadata`; the header resolved as the gateway does
  (`400` malformed or repeated, `403 delegation_not_allowed` for an id not assigned to the integration or sharing no
  residency with the credential key, `403 key_disabled`, `403 key_expired`), the request served under the assigned
  key's models and bounded residencies, the id echoed in the `D` form on every later answer; `/v1/models` with the
  header lists the key's models; `FakeGateway:DropOnBehalfOfEcho`; `FakeGateway:AssignedKeys:<n>:BudgetExceeded`
  (`Key`, `Team`, `Department`: `402 budget_exceeded` with the gateway's message); each served request recorded with
  the key it ran as, its `x-fors-run-id`, `x-fors-step`, tokens and cost (for B-45). `docs/security-events.md`'s 5011 row
  to the key form (it still names a team).
- **Paths:** `src/Kalla.Infrastructure/Gateway/**`, `src/Kalla.Answering/**`, `src/Kalla.Retrieval/**`,
  `src/Kalla.Api/**`, `src/Kalla.AdminApi/**` (`AssistantTestEndpoints.cs`, `TestRetrievalEndpoints.cs`, the
  gateway registration), `src/Kalla.FakeGateway/**`, `docs/security-events.md`, `tests/**` (not
  `tests/Kalla.ContractTests/**`, `tests/Kalla.Worker.Tests/**`).
- **Done when:** against the FakeGateway with `act-as-key`: every call of a chat turn (condense, query embedding,
  rerank, answer, follow-ups) and of a test-panel turn carries the assistant's key id and the FakeGateway records it
  under that key; an assistant without a key, or a FakeGateway without `act-as-key`, sends no header; with
  `DropOnBehalfOfEcho` the turn is aborted before a byte reaches the client and 5011 is logged, and passes with
  `Gateway:RequireDelegationEcho=false`; an upper-case echo passes; an unknown id (`403 delegation_not_allowed`), a
  disabled key (`403 key_disabled`), an expired one (`403 key_expired`) and a spent budget (`402`) each surface as the
  typed refusal, not retried; the handler's call-site tests cover the header at every call site.
- **Live check:** on the interim gateway on v0.10.1 (not deployed yet): one chat turn of an assistant with an assigned
  key shows under that key in the gateway's usage report, with kalla-api's credential key as the via key and the turn's
  run id and steps.
- **Code merged** (#616, #617; review follow-ups #629, #631): only the live check is left.
- **Waits on:** B-35 (the contract pinned at v0.10.0, the FakeGateway's assigned keys), B-36 (`GatewayKeyId` on
  assistants and collections and in kalla-api's snapshot). The stream cost (`x-fors-stream-cost`, the comment) and the
  usage records are B-38's: not repeated here.
- **Read:** gateway-changes §5.2, §5.3, §5.4, §5.7, §7.1; identity §4.7, §10; chat §8.1, §8.2; knowledge §5.2;
  cost-attribution §4.1; repo-and-deployment §4.3.

### [ ] B-43 Gateway phase 2: save checks as the key, publish needs a key, the acted status check (#580, cost-attribution B5, admin part)
- **Do:** once the admin's context shows `act-as-key`: V8's and V39's `GET /v1/models` as the object's key
  (`x-fors-on-behalf-of`) at the ceiling (admin §4.6): each missing model named (`gateway_key_model`), a refusal of the
  key `gateway_key_invalid`, a missing echo `gateway_unavailable` with 5011; `GET /api/gateway/models?gatewayKeyId=`
  computes `available` as that key (admin §4.5); publish and restore refuse an assistant without a key
  (`gateway_key_required`, 409), and a knowledge base without one keeps saving and indexing on the credential key with
  its page's note (cost-attribution §4.2); `GET /api/gateway/status` adds one acted `/v1/models` call's echo (the first
  usable mirrored key). The publish dialog and the key picker show the new refusals (`messages.ts`).
- **Paths:** `src/Kalla.AdminApi/**`, `contracts/admin/**`, `src/admin-ui/src/views/**`,
  `src/admin-ui/src/components/**`, `src/admin-ui/src/utils/labels.ts`, `messages.ts`, `tests/**` (not
  `tests/Kalla.ContractTests/**`, `tests/Kalla.Worker.Tests/**`).
- **Done when:** against the FakeGateway with `act-as-key`: V39 refuses a key whose `/v1/models` lacks the condense
  model, naming it, though the admin's own list has it; a disabled key gives `gateway_key_invalid`; with
  `DropOnBehalfOfEcho` the save gives `gateway_unavailable` and 5011; publishing an assistant without a key gives
  `gateway_key_required` (without `act-as-key`: the warning only); a knowledge base without a key saves; the status
  answer reports the echo; Vitest shows each refusal's text.
- **Live check:** on the interim gateway on v0.10.1: the key picker's model availability and V39 follow a real
  assigned key's models; the status page's echo is ok.
- **Code merged** (#618): only the live check is left.
- **Waits on:** B-42 (the handler and the FakeGateway's acting), B-36 (V39 and the picker).
- **Read:** admin §4.5, §4.6 V8, V39, §5.5.2, §5.5.4; gateway-changes §5.5; cost-attribution §4.2.

### [ ] B-44 Gateway phase 2: a spent budget or an unusable key in chat and the admin (#580, cost-attribution B6, api and admin part)
- **Do:** chat §3.6: at step 7 an assistant whose `GatewayKeys` row is missing, not `Active`, not `OwnerActive` or not
  `Usable` answers 503 `assistant_unavailable` before any model call; B-42's typed refusal maps to 503
  `assistant_unavailable` (the key) or 402 `budget_exceeded` with the gateway's message (a budget) before the first
  byte; the turn's outcome is `unavailable`, counted in the daily totals; a refusal after the first byte is §9.6's
  error chunk; Knowledge API callers get §5.5's embedding failure (knowledge §5.2); the test panel shows the same
  reason. kalla-admin: the `[Unavailable]` badge with its reason on the assistant's and knowledge base's pages, derived
  from the mirror (admin §5.5.4, §5.5.2), and from B-45's budgets once there; the attention-list source of admin
  §5.5.1 (the list itself comes with P2's `GET /api/overview`; until then the badge is the signal).
- **Paths:** `src/Kalla.Answering/**`, `src/Kalla.Api/**`, `src/Kalla.Retrieval/**`, `src/Kalla.AdminApi/**`,
  `contracts/admin/**`, `src/admin-ui/src/views/**`, `src/admin-ui/src/components/**`,
  `src/admin-ui/src/utils/labels.ts`, `messages.ts`, `tests/**` (not `tests/Kalla.ContractTests/**`,
  `tests/Kalla.Worker.Tests/**`).
- **Done when:** against the FakeGateway: a key with `BudgetExceeded: Department` gives 402 `budget_exceeded` with the
  department's message before the first byte and an `unavailable` turn in the totals; a disabled key in the mirror
  gives 503 `assistant_unavailable` and the FakeGateway saw no call; a key the FakeGateway refuses (`403
  delegation_not_allowed`) while the mirror still shows it active gives 503 `assistant_unavailable`; the test panel
  shows both reasons; the assistant page shows `[Unavailable]` with "The gateway key is disabled".
- **Live check:** on the interim gateway on v0.10.1: a small key budget set in the gateway stops the assistant with the
  planned message (cost-attribution §6's done-when).
- **Code merged** (#632 chat, #634 admin): only the live check is left.
- **Waits on:** B-42 (the typed refusal, the FakeGateway's modes), B-36 (the key on the assistant and its page).
- **Read:** chat §3.6, §4.1, §8.2, §9.6; knowledge §5.2, §5.5; admin §5.5.1, §5.5.2, §5.5.4; gateway-changes §5.4,
  §5.7; cost-attribution §4.2.

### [ ] B-45 Gateway phase 3: the Integration API read-back, cost and budget per key (#580, cost-attribution B7, API part)
- **Do:** `Gateway:IntegrationApiUrl` in kalla-admin (unset: no read-back, the routes answer `null` without a call); a
  client for `/integration/v1/usage` and `/budgets` with kalla-admin's credential key (`usage.read`), parameters and
  fields as gateway-changes §5.6; `GET /api/gateway/keys/{id}/budget` (admin §4.5) and
  `GET /api/gateway/keys/{id}/usage` (admin §8.6), cached 5 minutes per key and query; `401`, `403` (scope missing)
  and `429` shown on the *Gateway* page as the reason; the budgets' `exhausted` feeds B-44's badge. The FakeGateway's
  `/integration/v1` (repo-and-deployment §4.3): `usage` from the requests it served (B-42's record) with `from`, `to`,
  `granularity`, `groupBy`, `keyId`, `runId`, `400` beyond 366 days or for an unknown `groupBy`, `404` for a key not
  of the integration; `budgets` from `FakeGateway:AssignedKeys:<n>:Budgets`; `usage.read` on the admin's dev
  credential key only (`401` for a key that is not a credential key, `403` without the scope); the AppHost passes the
  dev `Gateway:IntegrationApiUrl`. The contract test checks both answers against the pinned `integrations.json`.
- **Paths:** `src/Kalla.AdminApi/**`, `src/Kalla.FakeGateway/**`, `src/Kalla.AppHost/**`, `contracts/admin/**`,
  `tests/**` (not `tests/Kalla.ContractTests/**`, `tests/Kalla.Worker.Tests/**`).
- **Done when:** against the FakeGateway, after chat turns on two keys: `GET /api/gateway/keys/{id}/usage` returns per
  step that key's tokens and cost only, by day and by month; `/budget` returns an exhausted department budget with
  `exhausted: true`; kalla-api's credential key gets `403` from the FakeGateway's `/integration/v1`; with
  `Gateway:IntegrationApiUrl` unset both routes answer `null` without a call; a second read within 5 minutes makes no
  call; the contract test fails on a renamed or missing field.
- **Live check:** on the interim gateway on v0.10.1 with `usage.read` on kalla-admin's key: an assistant key's cost for
  a day equals the gateway's usage report for that key.
- **Code merged** (#637): only the live check is left.
- **Waits on:** B-42 (the FakeGateway's record per request under the acting key), B-36 (keys on objects), B-35 (the
  pin at v0.10.0).
- **Read:** gateway-changes §5.6; admin §4.5, §8.6; repo-and-deployment §3 (`Gateway:IntegrationApiUrl`), §4.3;
  cost-attribution §6.

### [ ] B-46 Gateway phase 3 in the admin UI: budget status and the gateway's cost beside Källa's (#580, cost-attribution B7, UI part)
- **Do:** the key header's budget status on the assistant's and knowledge base's pages (admin §5.5.4: per budget its
  scope, spent of limit with the percentage, the period's end, *Used up*); the gateway's figures on B-41's *Cost* cards
  beside Källa's (admin §8.6), with the note when several objects share the key and the gateway link when there is no
  read-back. Every label in `labels.ts`, every sentence in `messages.ts`.
- **Paths:** `src/admin-ui/src/views/**`, `src/admin-ui/src/components/**`, `src/admin-ui/src/api/**`,
  `src/admin-ui/src/utils/labels.ts`, `messages.ts`.
- **Done when:** Vitest against B-45's contract: the budget status with an exhausted budget, the gateway's figures
  beside the stored ones by step, the shared-key note, `null` showing the link; no UI string outside
  `labels.ts`/`messages.ts`.
- **Live check:** on the interim gateway on v0.10.1: a real assistant's card shows its key's gateway cost and budget.
- **Code merged** (#638): only the live check is left.
- **Waits on:** B-45, B-41 (the cards).
- **Read:** admin §5.5.4, §8.6; Decisions "Admin UI in English".

### [ ] B-L2 Gold set in, thresholds out
- **Do:** enter the gold set as `human` questions; from the three-way run (with A and the main session) save
  `coverageMaxDistance` and `coverageMinKeywordScore`; decide whether `keywordWeight` 0.25 becomes the template
  default.
- **Done when:** thresholds saved on the pilot collection; #143, #168, #169 recorded.
- **Waits on:** the gold set (user + DoIT); A-L4.
- **Read:** processing §13.4; knowledge §7.

### [ ] B-L3 Freshness and index checks on the pilot collection
- **Done when:** current documents rank above outdated ones on the temporal questions without recall@10 dropping
  elsewhere (#171); HNSW recall against exact search recorded (#180).
- **Waits on:** B-L2.
- **Read:** knowledge §6.4, §6.5.

### [ ] B-L5 Feedback import live
- **Done when:** a pilot rating appears on the Feedback page; Open WebUI feedback older than 90 days is pruned (#49,
  #135).
- **Waits on:** the shared Keycloak with the feedback service account.
- **Read:** chat §13.3, §14.2.

