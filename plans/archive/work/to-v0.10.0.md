# Finished queue items up to v0.10.0

The ticked items of the work queues, moved here unchanged when `v0.10.0` was released (2026-11-23).
History: it does not bind. An item is found by its ID (`B-60`); the queues in `plans/work/` hold only open and waiting
items.

## Stream A (`stream-a.md`)

### [x] A-42 One path-rule matcher in `Kalla.Pipelines`, for the Topics tab (Decisions "Topics editor") (#948)
- **Do:** move the reading and matching of `pathRules` (`PathRule`, `ReadRules`, the `path` glob with its pasted-address,
  host-first and trailing-`/` forms, `path-regex`, `host`, `title-regex`, `mediaType`, the tag format) from
  `Kalla.Worker/Processing/Chunking/Metadata.cs` into `Kalla.Pipelines` (processing §7 *One matcher*), as a public
  matcher that takes a rule and a document's URL or display name, title and media type and answers whether it matches
  (and with which tags, `documentType` and date groups). The worker's `Metadata.Apply` keeps its behaviour and calls
  it. No behaviour change: the existing worker tests pass unchanged; the matching tests move to
  `Kalla.Pipelines.Tests`.
- **Paths:** `src/Kalla.Pipelines/**`, `src/Kalla.Worker/**`, `tests/Kalla.Pipelines.Tests/**`,
  `tests/Kalla.Worker.Tests/**`.
- **Done when:** `Kalla.Worker.Tests` (`StageRuleTests`, `UploadEngineTests`, `ChunkDateEngineTests`, `PreviewTests`,
  `GoldenTests`) and `Kalla.Pipelines.Tests` green; the golden chunk output unchanged.
- **Waits on:** nothing. B-64 and B-65 use it.
- **Read:** processing §7; admin §4.5 (`topic-suggestions`, `topic-rules/preview`); Decisions "Topics editor".

### [x] A-43 One schedule rule in `Kalla.Pipelines`, for the worker and the admin's next run (#949)
- **Do:** move the rule that decides when a source is next fetched into `Kalla.Pipelines` (with the Cronos reference):
  the schedule in force (the source's own, else `Settings.DefaultSchedules` when it names at least one type,
  else the built-in defaults: `url-list` 02:00 nightly, `crawl` Saturday 01:00; never for uploads), the next occurrence
  after the last `ScheduledFor` (else `CreatedAt`) in Europe/Stockholm with missed occurrences collapsed into one, and
  the jitter (`SHA256(sourceId)` first 8 bytes mod `Settings.ScheduleJitterMinutes`). The worker's `SyncScheduler.Due`
  and `Jitter` and `EngineSettings`' defaults call it. No behaviour change: the existing worker tests pass unchanged;
  the rule's tests move to `Kalla.Pipelines.Tests`. Today `Kalla.AdminApi/CronSchedules.cs` (#935) repeats the rule;
  B-71 switches it over.
- **Paths:** `src/Kalla.Pipelines/**`, `src/Kalla.Worker/**`, `tests/Kalla.Pipelines.Tests/**`,
  `tests/Kalla.Worker.Tests/**`.
- **Done when:** `Kalla.Worker.Tests` (the scheduler tests) and `Kalla.Pipelines.Tests` green, with the jitter pinned
  to the value #935's `CronSchedulesTests` pins (GUID `0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b`, 90 minutes: 8).
- **Waits on:** #935. B-71 uses it.
- **Read:** engine §9.6; #935's review comment.

## Stream B (`stream-b.md`)

### [x] B-54 Assistant templates: three seeded starting points (#950)
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

### [x] B-55 Copy an assistant (#952)
- **Do:** admin §4.5 `POST /api/assistants/{id}/copy` `{ name, slug, departmentId, ownerTeamId }`: a new assistant
  whose only version is a draft equal to the source's published version (its draft when never published); audiences,
  keys, versions and statistics not copied; V11/V12 for the target department as warnings; audited `assistant.copy`;
  **Copy** on the assistant page. Copy its test questions too once B-57 is in (whichever item lands second does it).
- **Paths:** as B-54.
- **Done when:** API tests: a copy into another department warns on a collection not shared with it, the copy has no
  key and no audience, the source is unchanged; the button shown in a browser.
- **Waits on:** `v0.9.0`.
- **Read:** admin §3 (`Edit` row), §4.5, the wireframe under §5.5 (*Copy*), §9 (audit actions).

### [x] B-56 Version diff (#954)
- **Do:** admin §4.5 `GET /api/assistants/{id}/versions/{n}/diff?against={m|draft}`: changed settings by name,
  collections and skills added or removed, and a line diff of instructions and not-found texts (an in-house Myers
  diff over at most 20,000 characters, no package); **Compare** in Versions; the diff in the 409 `draft_base_changed`
  answer of publish and in its dialog.
- **Paths:** as B-54.
- **Done when:** admin §11.1's version-diff tests (settings, links, a line diff with Swedish characters, the size
  limit); the Compare view shown in a browser.
- **Waits on:** `v0.9.0`.
- **Read:** admin §4.5 (versions, publish), §11.1.

### [x] B-57 Test questions per assistant (#956)
- **Do:** admin §7.4: `config."AssistantTestQuestions"` (data-model), `GET/POST/PUT/DELETE
  /api/assistants/{id}/test-questions[/{qid}]` with V33, `AssistantTestRunner` in kalla-admin (each question through
  `Kalla.Answering` as the test panel runs it, the expected document, phrase and behaviour checks, the answer
  judge `Settings.EvaluationJudgeModel` at the assistant's level, `x-fors-step: assistant-test`), the
  `EvaluationRuns`/`EvaluationResults` rows of kind `assistant-tests` and `AssistantVersions.TestRunId`, the run after
  every publish and restore and **Run test questions** on demand (one run per assistant at a time), the cost shown
  before, the report card (§7.2) with **Keep** and **Restore the previous version**, **Save as test question** in the
  test panel, the last 20 runs kept (data-model §8: a step added to `content.rag_retention_content_step` in this item's
  migration; the worker already runs that function nightly, so no worker change), `rag.admin.assistant_tests{outcome}`.
  The attention item (a run worse than the previous version's, admin §5.5.1) goes into B-59's list when that has
  landed, else in B-59. The FakeGateway answers the
  judge (its script per step).
- **Paths:** as B-54, and `src/Kalla.Answering/**`, `src/Kalla.FakeGateway/**` where the judge step needs it.
- **Done when:** admin §11.2's assistant-test cases: a publish starts a run, a question with each check passes and
  fails as specified, a run compares with the previous version's, results are readable only within the assistant's
  scope; the tab and the report shown in a browser.
- **Waits on:** `v0.9.0`.
- **Read:** admin §4.5 (publish, restore), §4.9, §7.2, §7.4, V33; data-model §2.2 (`EvaluationRuns`), §8;
  processing §14.8 (the judge prompt family).

### [x] B-58 The nightly answer run (#977)
- **Do:** processing §14.8 *answers*: `AnswerEvaluationService` in kalla-admin, nightly per published assistant: the
  evaluation sets of its collections answered through `Kalla.Answering` with the published version's settings, the
  judge (`Settings.EvaluationJudgeModel` at the assistant's ceiling, `x-fors-step: eval-answer`) rating faithfulness
  and citation precision; run kind `answers` with results per question; the assistant's Statistics tab shows the
  latest run's counts beside the live `uncited_answer` rate; `rag.evaluation.llm_tokens{step=answer}`; the night's
  cost in the run (§14.8). **Whether and how much** (Decisions "Nightly answer run"): the column
  `Settings.EvaluationAnswerQuestions` (0–100, default 100, with its range check) in `GET/PUT /api/settings/evaluation`,
  and on the Settings page's *Evaluation* section the switch and the count of admin §5.10; 0 starts no run. **The run
  row** as processing §14.8: one per assistant and night, no `SetId`, `SubjectId` the assistant, `SubjectVersion` the
  published version's number, compared with the previous finished run; a refused key or a spent budget leaves the row
  unfinished and the next night starts a new one. The last 60 runs per assistant kept (data-model §8: a step in
  `content.rag_retention_content_step`, as B-57's). The attention item (the 28-day median, admin §5.5.1) goes into
  B-59's list when that has landed, else in B-59.
- **Paths:** as B-57.
- **Done when:** a test runs the nightly job on FakeGateway for two assistants and stores per-question verdicts;
  an assistant with no published version or no questions gets no run; with the setting at 0 no assistant gets a run,
  and at 5 a run has at most 5 questions; a FakeGateway key with `BudgetExceeded` leaves that assistant's run
  unfinished while the other assistant's finishes; the Statistics tab and the Settings switch shown in a browser.
- **Waits on:** B-57 (the judge call and the run rows).
- **Read:** Decisions "Nightly answer run"; processing §14.8, §15; admin §2.2 (`AnswerEvaluationService`), §5.5.4
  (the Statistics tab), §5.10, §7.2; data-model §2.1 (`Settings`), §2.2 (`EvaluationRuns`), §8; chat design
  (`uncited_answer`).

### [x] B-59 Home: *Needs attention* (#978)
- **Do:** admin §5.5.1: `GET /api/overview`'s attention list for the caller's scope (a kind, the object, the reason, a
  link) with the twelve items that §5.5.1's table marks *the list* (V32's Overview warning among them), the *Needs
  attention* card at the top of Home and the badge count; one query per kind, each from data the admin already has.
  Three more rows are added by the item that builds their data when this item has landed first, and by this item
  otherwise: the assistant-test item (B-57), the answer-run item (B-58) and the downvoted document (B-66). The unused
  assistant is B-68's (it waits on this item); site-profile proposals, tool drift and approvals come with later waves.
- **Paths:** as B-54.
- **Done when:** an API test per kind (raised, and gone when its cause is); a department admin sees only the items
  of their departments; the card shown in a browser with the demo seed plus one forced item of each kind.
- **Waits on:** nothing (B-57's, B-58's and B-66's items as *Do* says).
- **Read:** admin §5.3 (Home row), §5.5.1, V32; processing §14.8; cost-attribution §4.2.


### [x] B-63 Clear a personal-data hold in the admin UI, with the highlights (#925 step 1) (#963)
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

### [x] B-70 Pause and resume a source from its page (#1005)
- **Do:** admin §5.5.3's **Pause** in the source page's header beside **Fetch now**, **Resume** while the source is
  paused (`POST /api/sources/{id}/pause` and `/resume` with the source's `version`, both there since P1, `Edit`); a
  paused source says so on its page and in the sources list, and its *Next scheduled fetch* reads *None: the source is
  paused* (#935). A 409 version conflict reloads the source and says so; texts in `labels.ts` and `messages.ts`.
- **Paths:** `src/admin-ui/**` (not `src/platform/**`), `tests/**`; `src/Kalla.AdminApi/**` only if the list lacks the
  state.
- **Done when:** Vitest: the button by state and role (hidden without `Edit`), the call with the version, the page
  after it; pause, then resume, shown in a browser on the demo seed, the next scheduled fetch changing with it.
- **Waits on:** #935 (the next-run row).
- **Read:** admin §3 (`Edit` row), §4 (the sources table: pause/resume), §5.5.3.
