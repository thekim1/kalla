# Finished queue items up to v0.12.0

The ticked items of the work queues, moved here unchanged when `v0.12.0` was released (2026-11-28).
History: it does not bind. An item is found by its ID (`B-65`); the queues in `plans/work/` hold only open and waiting
items.

## Stream B (`stream-b.md`)

### [x] B-65 The topic form, the match preview, and the read-only Topics field (#1068, #1071)
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

### [x] B-67 The drawn Map (#1074)
- **Do:** admin §5.5.6's drawing beside the existing table (**Table** / **Drawing**): one column per stage, plain SVG
  lines next to lists that carry the same relations as text, selecting an item highlights what it feeds and what feeds
  it; from the same `GET /api/organisation/departments/{id}/map` (sources added to its rows if missing).
- **Paths:** as B-54.
- **Done when:** Vitest for the layout and the text relations; the accessibility spec over the drawing in three themes;
  shown in a browser on the demo seed.
- **Waits on:** nothing.
- **Read:** admin §5.5.6, §10.1, §10.2 (no graph library).

### [x] B-68 Unused assistants: *Archive* and *Keep* (#1078)
- **Do:** admin §5.5.1's unused-assistant attention item (published ≥ 90 days ago, no message in 90 days in
  `AssistantDailyTotals`) with **Archive** (`…/archive`) and **Keep** (`POST /api/assistants/{id}/keep`, the column
  `Assistants.UnusedKeptUntil` in a migration of its own, audited `assistant.keep`).
- **Paths:** as B-54.
- **Done when:** API tests: raised, gone after Archive, not raised while kept, raised again after 90 days; shown in a
  browser.
- **Waits on:** B-59 (the attention list).
- **Read:** admin §4.5, §5.5.1; data-model `Assistants`.

### [x] B-69 The evaluation trend chart (#1085)
- **Do:** copy the gateway's `components/usage/TimeSeriesChart.vue` (and its spec) unchanged; on Test questions and
  quality (admin §7.1) the trend of recall@10 and MRR over the latest 30 runs as a chart, the `human` layer first, with
  the table of the same runs under it as its text view.
- **Paths:** as B-54.
- **Done when:** Vitest for the chart's data mapping; the accessibility spec over the page in three themes; shown in a
  browser on the demo seed with several evaluation runs.
- **Waits on:** nothing.
- **Read:** admin §7.1, §10.1; the gateway's `TimeSeriesChart.vue`.

### [x] B-71 The admin's next run from the shared schedule rule (#1088)
- **Do:** `Kalla.AdminApi`'s `scheduleInForce` and `nextRunAt` (#935) call A-43's rule in `Kalla.Pipelines` instead of
  repeating it; `CronSchedules` keeps only the save-time validation (`IsValid`, `Invalid`), and the AdminApi's own
  Cronos reference goes if nothing else needs it. No behaviour change.
- **Paths:** `src/Kalla.AdminApi/**`, `tests/Kalla.AdminApi.Tests/**`.
- **Done when:** `Kalla.AdminApi.Tests` green, `SourceEndpointsTests` unchanged; the tests of the rule itself live in
  `Kalla.Pipelines.Tests` (A-43), not here.
- **Waits on:** A-43.
- **Read:** engine §9.6; #935.
