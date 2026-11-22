# Stream B: work queue

Stream B owns the schema, the admin API, every admin page, retrieval, knowledge and chat
(`.github/stream-ownership.txt`). Rules: [`../../AGENTS.md`](../../AGENTS.md). Context: [`../roadmap.md`](../roadmap.md).

**How to use:** take the first item that is not ticked and not waiting. One PR per item (branch `b/<id>-<slug>`), and
tick it here in that PR (`[x] … (#PR)`). Record `to-verify` rows in [`results-b.md`](results-b.md). If an item is
waiting, take the next one. Live items need the inputs named in *Waits on*; don't start them early.

Finished items move to [`../archive/work/`](../archive/work/) at each release (the main session); find one by its ID.

## Code

## P2 wave 1: assistants and answer quality (Decisions "P2 starts before go-live")

Start these once `v0.9.0` is tagged (the roadmap's *P2 Broaden*); B-52 and B-53 above go first. Each adds its
tables in one migration of its own (no backward compatibility before production). The UI texts go in `labels.ts` and
`messages.ts`; show each page in a real browser before the PR (FakeGateway stack with the demo seed).

### [ ] B-54 Assistant templates: three seeded starting points
- **Do:** admin §4.5 `GET/POST/PUT/DELETE /api/assistant-templates[/{id}]` (Read / Admin, audited
  `assistanttemplate.*`), the table `config."AssistantTemplates"` (data-model), the seeder `AssistantTemplateSeeder`
  with the three presets of admin §4.5 (into an empty table only), the platform page through the generic form (§5.8),
  and the template field in the create-assistant drawer (§5 step 9: *Allmän rådgivning* preselected; its
  instructions, not-found texts with links and query steps copied into the first draft, never linked; the field
  emptied keeps P1's start from `Settings.Chat.defaultSystemPrompt`). Write the two new presets' instructions in
  Swedish in the style of the default prompt and show them in the PR.
- **Paths:** `src/Kalla.AdminApi/**`, `src/Kalla.Infrastructure/**` (b's), `src/Kalla.Domain/**` (b's),
  `src/Kalla.MigrationService/` (not `Templates/**`), `src/admin-ui/**` (not `src/platform/**`), `tests/**`.
- **Done when:** API tests for the endpoints and roles; a seeded database has the three templates and a second start
  doesn't add or restore one; an assistant created from *Intern policy* has its instructions in the first draft and
  keeps them when the template changes later; the drawer shown in a browser.
- **Waits on:** `v0.9.0`.
- **Read:** admin §3 (`Admin` row), §4.5, §5 step 9, §5.8; data-model `AssistantTemplates`; architecture plan §2
  item 3 and §3 *Presets make setup easier*.

### [ ] B-55 Copy an assistant
- **Do:** admin §4.5 `POST /api/assistants/{id}/copy` `{ name, slug, departmentId, ownerTeamId }`: a new assistant
  whose only version is a draft equal to the source's published version (its draft when never published); audiences,
  keys, versions and statistics not copied; V11/V12 for the target department as warnings; audited `assistant.copy`;
  **Copy** on the assistant page. Copy its test questions too once B-57 is in (whichever item lands second does it).
- **Paths:** as B-54.
- **Done when:** API tests: a copy into another department warns on a collection not shared with it, the copy has no
  key and no audience, the source is unchanged; the button shown in a browser.
- **Waits on:** `v0.9.0`.
- **Read:** admin §3 (`Edit` row), §4.5, the wireframe under §5.5 (*Copy*), §9 (audit actions).

### [ ] B-56 Version diff
- **Do:** admin §4.5 `GET /api/assistants/{id}/versions/{n}/diff?against={m|draft}`: changed settings by name,
  collections and skills added or removed, and a line diff of instructions and not-found texts (an in-house Myers
  diff over at most 20,000 characters, no package); **Compare** in Versions; the diff in the 409 `draft_base_changed`
  answer of publish and in its dialog.
- **Paths:** as B-54.
- **Done when:** admin §11.1's version-diff tests (settings, links, a line diff with Swedish characters, the size
  limit); the Compare view shown in a browser.
- **Waits on:** `v0.9.0`.
- **Read:** admin §4.5 (versions, publish), §11.1.

### [ ] B-57 Test questions per assistant
- **Do:** admin §7.4: `config."AssistantTestQuestions"` (data-model), `GET/POST/PUT/DELETE
  /api/assistants/{id}/test-questions[/{qid}]` with V33, `AssistantTestRunner` in kalla-admin (each question through
  `Kalla.Answering` as the test panel runs it, the expected document, phrase and behaviour checks, the answer
  judge `Settings.EvaluationJudgeModel` at the assistant's level, `x-fors-step: assistant-test`), the
  `EvaluationRuns`/`EvaluationResults` rows of kind `assistant-tests` and `AssistantVersions.TestRunId`, the run after
  every publish and restore and **Run test questions** on demand (one run per assistant at a time), the cost shown
  before, the report card (§7.2) with **Keep** and **Restore the previous version**, **Save as test question** in the
  test panel, the last 20 runs kept (data-model §8), `rag.admin.assistant_tests{outcome}`. The FakeGateway answers the
  judge (its script per step).
- **Paths:** as B-54, and `src/Kalla.Answering/**`, `src/Kalla.FakeGateway/**` where the judge step needs it.
- **Done when:** admin §11.2's assistant-test cases: a publish starts a run, a question with each check passes and
  fails as specified, a run compares with the previous version's, results are readable only within the assistant's
  scope; the tab and the report shown in a browser.
- **Waits on:** `v0.9.0`.
- **Read:** admin §4.5 (publish, restore), §4.9, §7.2, §7.4, V33; data-model §2.2 (`EvaluationRuns`), §8;
  processing §14.8 (the judge prompt family).

### [ ] B-58 The nightly answer run
- **Do:** processing §14.8 *answers*: `AnswerEvaluationService` in kalla-admin, nightly per published assistant: the
  evaluation sets of its collections answered through `Kalla.Answering` with the published version's settings, the
  judge (`Settings.EvaluationJudgeModel` at the assistant's ceiling, `x-fors-step: eval-answer`) rating faithfulness
  and citation precision; run kind `answers` with results per question; the assistant's Statistics tab shows the
  latest run's counts beside the live `uncited_answer` rate; `rag.evaluation.llm_tokens{step=answer}`; at most 100
  questions per assistant and night, and the night's cost in the run (§14.8).
- **Paths:** as B-57.
- **Done when:** a test runs the nightly job on FakeGateway for two assistants and stores per-question verdicts;
  an assistant with no published version or no questions gets no run; the Statistics tab shown in a browser.
- **Waits on:** B-57 (the judge call and the run rows).
- **Read:** processing §14.8, §15; admin §2.2 (`AnswerEvaluationService`), §7.2; chat design (`uncited_answer`).

### [ ] B-59 Home: *Needs attention*
- **Do:** admin §5.5.1: `GET /api/overview`'s attention list for the caller's scope with every source listed under
  *Attention items are P2* (a kind, the object, the reason, a link), the *Needs attention* card at the top of Home and
  the badge count, V32's Overview warning; one query per kind, each from data the admin already has.
- **Paths:** as B-54.
- **Done when:** an API test per kind (raised, and gone when its cause is); a department admin sees only the items
  of their departments; the card shown in a browser with the demo seed plus one forced item of each kind.
- **Waits on:** B-57 and B-58 for their two kinds (the others first, those two added when they land).
- **Read:** admin §5.3 (Home row), §5.5.1, V32; processing §14.8; cost-attribution §4.2.


### [ ] B-63 Clear a personal-data hold in the admin UI, with the highlights (#925 step 1)
- **Do:** admin §4.5's `POST /api/documents/{id}/pii-clear` (reason ≥ 10, V26) and `POST /api/documents/{id}/pii-preview`
  (audited `pii.view`) in the admin UI, both built in the API and unused by the UI today: on a flagged, not cleared
  document in the knowledge base's document list (§5.5.2) and in the source's personal-data report (§5.5.3), for
  Manage holders of the source's department, a *Show highlights* action (the spans of the preview job, with the
  wait while it runs) and a *Clear* action with the reason dialog that says what clearing does (the held set is
  released and later sets are covered while no count rises, processing §9.5). The badges and the report follow the
  cleared state.
- **Paths:** `src/admin-ui/**` (not `src/platform/**`), `tests/**`; `src/Kalla.AdminApi/**` only if a field the UI
  needs is missing.
- **Done when:** Vitest for both actions and the reason rule; an API-backed test that a cleared document's held set is
  served; both shown in a browser on the demo seed with a document flagged by a test personnummer.
- **Waits on:** nothing. Steps 2–4 of `proposals/pii-review.md` (verdicts, reject, the summary) come with their
  Decisions entry (user's answers on #925, 2026-11-21).
- **Read:** admin §4.5, §4.7, V26, §5.5.2, §5.5.3; processing §9.5; `proposals/pii-review.md`.

## Support chatbot F10 and F7 (user, 2026-11-21)

After P2 wave 1, or beside it when a wave item waits. Each adds its table in a migration of its own.

### [ ] B-61 Cost per outcome (F10)
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
  elsewhere (#171); HNSW recall against exact search recorded (#117).
- **Waits on:** B-L2.
- **Read:** knowledge §6.4, §6.5.

### [ ] B-L5 Feedback import live
- **Done when:** a pilot rating appears on the Feedback page; Open WebUI feedback older than 90 days is pruned (#49,
  #135).
- **Waits on:** the shared Keycloak with the feedback service account.
- **Read:** chat §13.3, §14.2.

