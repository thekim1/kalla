# Stream B: work queue

Stream B owns the schema, the admin API, every admin page, retrieval, knowledge and chat
(`.github/stream-ownership.txt`). Rules: [`../../AGENTS.md`](../../AGENTS.md). Context: [`../roadmap.md`](../roadmap.md).

**How to use:** take the first item that is not ticked and not waiting. One PR per item (branch `b/<id>-<slug>`), and
tick it here in that PR (`[x] … (#PR)`). Record `to-verify` rows in [`results-b.md`](results-b.md). If an item is
waiting, take the next one. Live items need the inputs named in *Waits on*; don't start them early.

## Code

### [x] B-1 Chat models only in the model picker (#363 point 1; #379)
- **Do:** add `GatewayModels.ModelKind` (`chat`/`embedding`) filled by `GatewayModelSync` from the catalogue's `Kind`
  (manual route rows `chat`); return `modelKind` from `GET /api/gateway/models`; the assistant's model picker lists
  chat models only; V34 and V32 refuse an embedding model. No compatibility step: the next sync fills the column.
- **Paths:** `src/Ume.Rag.Domain/**`, `src/Ume.Rag.Infrastructure/**` (migration), `src/Ume.Rag.AdminApi/**`,
  `src/admin-ui/**`.
- **Done when:** a sync test maps `Chat`/`Embedding`; a rules test refuses an embedding model on publish; vitest: the
  picker hides `bge-m3`.
- **Waits on:** –
- **Read:** data-model §2.1 (`GatewayModels`); admin §4.6 (V32, V34), §4.10.

### [x] B-2 Archived assistants and one document count (#374; #385)
- **Do:** show "Archived" for archived assistants in *Used by* and leave them off the Map (or mark them); use one
  document count on the knowledge base list and its Overview (live documents), or label the difference.
- **Paths:** `src/Ume.Rag.AdminApi/**`, `src/admin-ui/**`.
- **Done when:** vitest for both pages; the Map route test leaves out an archived assistant.
- **Waits on:** –
- **Read:** admin §5.5.2, §5.5.6.

### [x] B-3 Close the open records of items 3, 9, 10 and 12 (#393)
- **Do:** record what is left of #23 (config lock wait, `rag.admin.config_lock.wait`), #20 (the writer's batch),
  #146 (generic forms for platform objects, data protection `none` only); confirm knowledge §16.1 tests and
  "evaluator order == `/v1/retrieve` order" (the evaluation smoke checks it).
- **Paths:** `plans/work/results-b.md`, tests if a check is missing.
- **Done when:** each row recorded as met, partly met with the reason, or moved with the main session's agreement.
- **Waits on:** –
- **Read:** [`../to-verify.md`](../to-verify.md).

### [x] B-4 Accessibility statement and one NVDA pass (#96) (#427; approved by the user 2026-11-06 without the NVDA pass, which follows soon)
- **Do:** finish `docs/accessibility-statement.md` once the DOS-lagen scope is known; one screen-reader pass through
  the pilot journey with NVDA.
- **Paths:** `docs/accessibility-statement.md`, `src/admin-ui/**` for fixes.
- **Done when:** NVDA findings fixed or filed; statement final; #96 recorded.
- **Waits on:** the user (DOS-lagen scope) and a person with NVDA.
- **Read:** admin §10.1.

### [x] B-5 Item 36's browser-test step (#377; https://github.com/thekim1/ume-rag-platform/actions/runs/42090732503)
- **Done when:** the first green `e2e` run (A-2) passes step 14 of `tests/e2e/specs/pilot.spec.ts` (share with the
  municipality, link with a topic from another department, by keyboard).
- **Waits on:** A-2.

### [x] B-7 HNSW rebuild on its own connection string (M-2) (#383)
- **Do:** `ConnectionStrings:ragdb-maintenance` (falls back to `ragdb`) for `HnswIndexHealth.RebuildAsync`; set
  `statement_timeout = 0` with `SET` on the session, not as a startup option (PgBouncer drops `options`); AppHost
  passes it like `ragdb-listen`.
- **Paths:** `src/Ume.Rag.Worker/Engine/**`, `src/Ume.Rag.Worker/Jobs/**`, AppHost wiring.
- **Done when:** a Worker test shows the rebuild uses the maintenance string and sets the timeout by `SET`.
- **Waits on:** –
- **Read:** Decisions "M-2 open design calls"; knowledge-api §6.4; data-model §1 (`ragdb-listen`).

### [x] B-8 Fired PII flag types stored and shown (#217, M-2) (#404)
- **Do:** the PII stage stores the fired types next to `PiiCounts`; the PII report and document page show them.
- **Paths:** `src/Ume.Rag.Worker/Processing/Pii/**`, `src/Ume.Rag.AdminApi/PiiReportEndpoints.cs`,
  `DocumentEndpoints.cs`, migration, admin UI.
- **Done when:** a document with fired rules shows their types in the report (test and a look in the browser).
- **Waits on:** –
- **Read:** Decisions "M-2 open design calls"; processing §9.4–§9.5.

### [x] B-9 Index health for multi-collection scopes (M-2) (#388)
- **Do:** sample every HNSW-searched scope an assistant link uses, including multi-collection ones; report the worst
  recall per profile and log the failing scope.
- **Paths:** `src/Ume.Rag.Worker/Engine/HnswIndexHealth.cs`.
- **Done when:** a test with several small collections that together pass the HNSW threshold samples that scope.
- **Waits on:** –
- **Read:** Decisions "M-2 open design calls"; knowledge-api §6.4.

### [x] B-10 Scope size counts topic-filtered chunks (M-2) (#389)
- **Do:** `ScopeSizeCache` counts the chunks of a (collection, tag) link, so a narrow topic in a large collection gets
  exact search.
- **Paths:** `src/Ume.Rag.Retrieval/**`.
- **Done when:** a retrieval test: a topic of a few hundred chunks in a collection over the limit is searched exactly.
- **Waits on:** –
- **Read:** Decisions "M-2 open design calls"; knowledge-api §6.4; to-verify #180.

### [x] B-11 "Open in Open WebUI" link on the assistant page (#363 point 2) (#395)
- **Do:** the assistant page links to the client's `PortalUrl` for a published assistant on a client with one.
- **Paths:** `src/admin-ui/**`, `src/Ume.Rag.AdminApi/AssistantEndpoints.cs`.
- **Done when:** the link shows on a published `turkos` and opens Open WebUI (look in the browser); the pilot-journey
  spec's step 13 follows it.
- **Waits on:** –
- **Read:** admin §5.4 step 13.

### [x] B-12 Rerank per assistant: `ChatModelReranker` now, `GatewayReranker` later (live run #421)
- **Do:** `ChatModelReranker` behind `IReranker`: one chat call through the gateway (`/v1/chat/completions`, the
  assistant's residency ceiling) with the question and the top 20 fused candidates, asking for JSON scores per
  passage; invalid output or the timeout degrades to the fused order (`rerank_unavailable`). The model is a platform
  setting (`Settings.Retrieval.RerankModel`, a chat model from the mirror); no `temperature` for Claude models.
  An assistant setting (Advanced) turns rerank on, off by default. The evaluation harness runs a set with and
  without rerank and reports latency and cost per query. FakeGateway answers the rerank prompt deterministically.
  `GatewayReranker` follows on the gateway's rerank endpoint when it exists.
- **Paths:** `src/Ume.Rag.Retrieval/**`, `src/Ume.Rag.FakeGateway/**`, assistant and platform settings in the admin
  API and UI, `scripts/evaluation/**`.
- **Done when:** retrieval tests for scoring, malformed output, timeout and degrade; a live evaluation run on
  `turkos`'s generated set over the interim gateway reports recall@10/MRR, p50/p95 latency and cost with and without
  rerank (link it on the PR).
- **Waits on:** – (the live run: the other main session, #403).
- **Read:** Decisions "Rerank pulled forward"; knowledge-api §6.7.

### [x] B-13 Config lock wait histogram (#391, to-verify #23; #396)
- **Do:** emit `rag.admin.config_lock.wait` (histogram, seconds) around the `config."ConfigVersion"` lock wait in the
  admin's config writes, on the admin's meter; the measurement at the real write rate is A-L6's.
- **Paths:** `src/Ume.Rag.Infrastructure/Config/**`, `src/Ume.Rag.AdminApi/**`, their tests.
- **Done when:** a test records one measurement per config write, and a longer one while another write holds the lock.
- **Waits on:** –
- **Read:** [`../to-verify.md`](../to-verify.md) #23; results-b #23 (B-3, #393).

### [x] B-14 FakeGateway matches gateway release 1 and rerank (#390) (#406)
- **Do:** align `Ume.Rag.FakeGateway` with #390's Feature B points 1–4 and 8 (no circuit-open 503: failing in-ceiling
  targets end in `502`/`504`; 403 vs 503 + `Retry-After` only on the ceiling 503; `400 invalid_request` for a
  malformed, empty, repeated or required-but-missing header; `/v1/models` filtered by the ceiling) and serve
  `/v1/rerank` in the Cohere shape (documents as strings or `{text}`, `results[]` with `index` and
  `relevance_score`, `usage`), with a **no-rerank-model mode** that answers 404/400 as the gateway does. List any
  remaining difference on #390. Ume.Rag's gateway clients retry 502/504 with the same ceiling, never a wider one.
- **Paths:** `src/Ume.Rag.FakeGateway/**`, its tests; the retry policy where the gateway clients live.
- **Done when:** FakeGateway tests cover each point and the rerank shape; #390 has the comparison comment.
- **Waits on:** –
- **Read:** Decisions "Gateway release 1 and shared Keycloak as built"; gateway-changes §3.3, §7.4.

### [x] B-15 `deploy/keycloak` for the shared realm (#390, #215) (#409)
- **Do:** `rag-additions.json` gets `"realm": "ume"` and `$(env:NAME)` placeholders (`$(env:NAME:-default)` for
  defaults); drop `rag-realm-settings.json` and the OTP flow for the shared realm (keep a standalone variant only if
  cheap); `apply-additions.py` only for a standalone realm; the Keycloak test pin moves to 26.8.1 by the digest in
  #390; local redirect URIs stay out of the production file.
- **Paths:** `deploy/keycloak/**`, its tests.
- **Done when:** the additions import with keycloak-config-cli after a base realm in a 26.8.1 test container, and the
  `ume-rag-admin` token carries only `rag-*` roles plus `departmentCodes`; #215 closed.
- **Waits on:** –
- **Read:** Decisions "Gateway release 1 and shared Keycloak as built"; identity §11.2.

### [x] B-16 `GatewayReranker` on `/v1/rerank` (live run #421; timeout #438)
- **Do:** `GatewayReranker` behind `IReranker` calling `/v1/rerank` (Berget AI `bge-reranker-v2-m3`, `Eu` ceiling
  until on-prem), selectable per assistant next to `ChatModelReranker`. **Rerank is optional everywhere** (user,
  2026-11-06): with no rerank model set, no rerank call is made, retrieval is plain fused, and there is no
  `rerank_unavailable`, no degraded health and nothing that looks broken in the admin UI; the assistant's rerank
  switch is disabled with a hint while the platform has none. A 404/400 or timeout from the gateway falls back to
  fused order, logged once, never a failed answer. Timeouts: 500 ms for `GatewayReranker` (1.5 s since #438); `ChatModelReranker` gets
  its own longer default (3 s, then set from B-12's live p95). The evaluation compares both rerankers.
- **Paths:** `src/Ume.Rag.Retrieval/**`, assistant and platform settings in the admin API and UI.
- **Done when:** retrieval tests against the FakeGateway rerank (B-14); the chat path, the test panel and the
  evaluation are tested with rerank fully unconfigured; a live evaluation run once the endpoint exists.
- **Waits on:** the live run only (the other main session): gateway v0.9.0 (2026-11-06) with Berget AI
  `BAAI/bge-reranker-v2-m3` as a Rerank model, `Settings.Retrieval.gatewayRerankModel` set, then the evaluation's
  `hybrid-rerank-gateway` variant (#416).
- **Read:** Decisions "Gateway release 1 and shared Keycloak as built", "Rerank pulled forward"; gateway-changes §7.4.

### [x] B-17 Chat: one retry on a connection failure (chat-api §8.3) (#422)
- **Do:** the chat's gateway call retries once when the connection fails with no response, within 300 ms, same
  request, same ceiling and key; never after the first byte. Found by the hot-path tidy (#417): today only 502/504
  are retried (`GatewayRetryHandler`). Decide whether the handler gets the case for every client or only the chat
  client; the query embedder keeps its own loop (knowledge §5.3).
- **Paths:** `src/Ume.Rag.Infrastructure/Gateway/**`, `src/Ume.Rag.Answering/Chat/**`.
- **Done when:** a test with a refused connection then an answer shows one retry, and none after the first byte; the
  chat golden files (`tests/Ume.Rag.Api.Tests/Golden/chat/`) unchanged.
- **Waits on:** –
- **Read:** chat-api §8.3; Decisions "Gateway release 1 and shared Keycloak as built".

### [x] B-6 Go-live pin review, B's part (last code item)
- **Do:** review every NuGet and npm pin younger than 7 days; the realm callback for the Open WebUI pin bump
  (`/oauth/oidc/login/callback`) matching A-4; condense stays at `Eu` until an on-prem route exists (M2).
- **Paths:** `Directory.Packages.props` (shared), `src/admin-ui/package*.json`, `deploy/keycloak/**`.
- **Done when:** no pin under 7 days old without a recorded reason; #113 recorded.
- **Waits on:** every other code item of both streams.
- **Read:** Decisions "7-day rule in development"; identity §5.3.

### [x] B-18 General knowledge on a public assistant: warn and confirm (#445)
- **Do:** V36 turns from a refusal into a confirmed warning: draft save returns `general_knowledge_public_unconfirmed`
  as a warning; publish, restore and `PUT /api/assistants/{id}` with a level change to `public` answer 409 with that
  code until the body carries `confirmGeneralKnowledge: true`, and the audit event records `generalKnowledgeConfirmed`.
  The Utkast tab keeps all four choices enabled for `public` and shows the warning under the group; *Publicera
  utkastet*, restore and the level change (B-19) ask one extra confirmation and resend with the flag. Remove the
  run-time override in `ChatService` (`Classification.Public` → `GeneralKnowledge.Off`): the published level runs.
  Texts only in `labels.ts`/`messages.ts`.
- **Paths:** `src/Ume.Rag.AdminApi/**`, `src/Ume.Rag.Answering/**`, `src/admin-ui/**`, their tests.
- **Done when:** rules tests: warning on save, 409 without the flag and success with it on publish, restore and level
  change, audit field set; a chat test runs `fallback` on a `public` assistant with the §10.4 caveat line; vitest: the
  radios are enabled and the confirmation is asked.
- **Waits on:** –
- **Read:** Decisions "General knowledge on a public assistant"; admin §4.5 (publish, restore, assistants), §4.6 V36,
  §5.5.4; chat §7.3, §10.4.

### [x] B-19 Level change in the edit drawer (#446)
- **Do:** add the level to the collection's *Edit* drawer (in the `⋯` menu; create the drawer with name, contact and
  level if the page has none) and to the assistant's *Ändra* drawer, `edit` right, saved with the other fields through
  the existing `PUT`s. Show the API's refusals (`collection_level_conflict`, `assistant_below_collection`, V3/V7/V8/V9)
  in the drawer with `affected[]` by name; a change to `public` with general knowledge on asks B-18's confirmation.
- **Paths:** `src/admin-ui/**` (`CollectionView.vue`, `AssistantView.vue`, drawers, labels), the admin API only if a
  field is missing.
- **Done when:** vitest: the level field saves, a refusal lists the affected objects; a browser check of both drawers
  (screenshot in the PR).
- **Waits on:** B-18 for the confirmation (the rest can go first).
- **Read:** Decisions "Level change in the edit drawer"; admin §4.5, §5.5.2, §5.5.4.

### [x] B-20 Model pickers show what the key may use (#447)
- **Do:** `GET /api/gateway/models?level=` adds `available` (`true`/`false`/`null`) per row from the gateway's
  `/v1/models` called with the platform's key at the level's ceiling (no `level`: the platform ceiling), cached 5
  minutes per ceiling; `null` when the gateway can't be asked. Every model picker (assistant model; Settings:
  evaluation, judge, condense, skill gate, `rerankModel`, `gatewayRerankModel`) greys out `available: false` with "Not
  available to this platform's key" and keeps every model choosable with "Availability unknown" on `null`. V32 also
  calls `/v1/models` on save (`model_not_listed` 409, `gateway_unavailable` 503), as V8 does on publish.
- **Paths:** `src/Ume.Rag.AdminApi/**`, `src/admin-ui/**` (the assistant picker; the Settings forms' model fields,
  coordinating with stream A where the generic forms live), FakeGateway if its `/v1/models` needs a key filter.
- **Done when:** API tests for `available` true/false/null and the cache; a V32 test refuses an unlisted model; vitest:
  a greyed-out model can't be chosen; checked live on the real gateway (15 mirror models, 5 available).
- **Waits on:** –
- **Read:** Decisions "Model pickers show what the key may use"; admin §4.5 (`/api/gateway/models`), §4.6 V8/V32,
  §4.10.

### [x] B-21 One connection pool in rag-api (M-3, #127) (#485)
- **Do:** rag-api registers a second keyed `NpgsqlDataSource` (`"retrieval"`, `Answering/KnowledgeExtensions.cs:40`,
  `Retrieval/Postgres/RetrievalDataSource.cs`) next to `ragdb` on the same connection string, which can double the
  connections per instance and breaks `MaxConcurrentSearches = ⌊(pool − 3) ÷ 2⌋` (knowledge §6.6, data-model §1.1:
  one pool per process). Move retrieval onto `ragdb` (it maps pgvector since #118); remove the keyed pool.
- **Paths:** `src/Ume.Rag.Answering/**`, `src/Ume.Rag.Retrieval/**`, `src/Ume.Rag.Api/**`, their tests.
- **Done when:** one `NpgsqlDataSource` per process (a DI test); retrieval and integration tests pass.
- **Waits on:** –
- **Read:** knowledge §6.6; data-model §1.1.

### [x] B-22 Publish checks the worker supports the template version (M-3, #298) (#486)
- **Do:** pipeline publish calls `PipelineValidator.ValidateForPublish` (rules 1–4, with the live workers' supported
  template versions) instead of `Validate` (`PipelineEndpoints.cs:227`), so engine §4 rule 4 / admin V24 hold during a
  rolling update.
- **Paths:** `src/Ume.Rag.AdminApi/**`, its tests.
- **Done when:** an admin API test refuses a publish whose template version no live worker supports.
- **Waits on:** –
- **Read:** engine §4; admin §4.6 V24.

### [x] B-23 Small admin gaps from the deviation review (M-3) (#487)
- **Do:** (1) the feedback import alert after 4 failed runs (chat §14.3; today only `LastOutcome`,
  `FeedbackEndpoints.cs:99-107`); (2) the collection page's sources table shows documents and the last run (the API
  returns both, `CollectionView.vue:92-96`, admin §5.5.2); (3) the deletion card's "at most 20 % (at least 5)" comes from
  the configured deletion guard (`labels.ts:1032`, `DeletionGuardSettings`); (4) `deploy/keycloak/rag-additions.json`'s
  `open-webui` description no longer says "legacy callback path".
- **Paths:** `src/Ume.Rag.Api/**`, `src/Ume.Rag.AdminApi/**`, `src/admin-ui/**`, `deploy/keycloak/**`, their tests.
- **Done when:** a test per point (vitest for 2 and 3; an API or importer test for 1; the realm-additions test for 4).
- **Waits on:** –
- **Read:** chat §14.3; admin §5.5.2, §5.5.3.

### [x] B-24 Restore a deleted uploaded file (M-3, #309) (#489)
- **Do:** `POST /api/sources/{id}/files/restore` within the upload grace (connectors §9.3, admin §4.5 files row): the
  UI promises "Within 14 days the platform team can bring them back" (`labels.ts:580`, `:1044`) and no route exists.
  Build the route (platform admin) and a *Restore* action on the files list for deleted slots, or change the texts if
  the design decides otherwise (ask main first).
- **Paths:** `src/Ume.Rag.AdminApi/**`, `src/Ume.Rag.Worker/**` (if the restore needs a job), `src/admin-ui/**`.
- **Done when:** a deleted file restored within the grace is searchable again after its sync; tests on both sides.
- **Waits on:** –
- **Read:** connectors §9.3, §13.4; admin §4.5.

### [x] B-25 Search statistics within the role's timeout (#499, step 1) (#516)
- **Do:** measure `RetrievalStatements.CountChunks` and `FrequentLexemes` on the scale test data (A-18's probe or the
  largest local collection) as `ume_rag_api` with its 5 s `statement_timeout`. If either can exceed it, run both inside a
  transaction with `SET LOCAL statement_timeout` equal to their client timeouts (30 s, 60 s), and log a failed refresh
  at Warning with the statement's name. List it under "Design deviations" (knowledge §6.4, §20). The single-writer
  redesign of #499 is step 2 (M-10), not this item.
- **Paths:** `src/Ume.Rag.Retrieval/**`, `tests/Ume.Rag.Retrieval.Tests/**`.
- **Done when:** the measured times are in `results-b.md`; a test shows a refresh slower than the role's timeout
  completes instead of being cancelled.
- **Waits on:** –
- **Read:** #499; knowledge §6.2, §6.4, §20.

### [x] B-26 Less telemetry overhead (#503, code part) (#518)
- **Do:** `"Microsoft.EntityFrameworkCore.Database.Command": "Warning"` in every service's `appsettings.json`;
  register tracing only when an OTLP endpoint is configured; leave sampling to the standard `OTEL_TRACES_SAMPLER`
  variables (the Compose knob is A-21).
- **Paths:** `src/*/appsettings*.json`, `src/Ume.Rag.ServiceDefaults/**`.
- **Done when:** a local run logs no EF SQL text at Information; traces still reach the Aspire dashboard locally.
- **Waits on:** –
- **Read:** #503; chat §17.

### [x] B-27 Smaller .NET footprint (#504, csproj part) (#520; measured, no gain: no csproj change)
- **Do:** measure idle and loaded RSS per .NET container first (local Compose with the release images, or the
  AppHost), then `<ServerGarbageCollection>false</ServerGarbageCollection>` for AdminApi, Worker and MigrationService
  (rag-api keeps Server GC). ReadyToRun only with a measured start-up gain: report it, main changes
  `Directory.Build.*`. The health-check interval is A-21.
- **Paths:** `src/Ume.Rag.AdminApi/*.csproj`, `src/Ume.Rag.Worker/*.csproj`, `src/Ume.Rag.MigrationService/*.csproj`.
- **Done when:** a before/after RSS table in `results-b.md`.
- **Waits on:** –
- **Read:** #504.

### [x] B-28 One statement K: the search-vector expression as a parameter (#509, statement part) (#523)
- **Do:** `RetrievalStatements.Keyword` takes the search-vector expression (the stored `SearchVector` by default,
  `to_tsvector('swedish', "Text")` for the no-decompounder variant), cached per variant like the other statements.
  The worker's variant switches to it in A-24.
- **Paths:** `src/Ume.Rag.Retrieval/**`, `tests/Ume.Rag.Retrieval.Tests/**`.
- **Done when:** both forms are built from one statement and tested.
- **Waits on:** –
- **Read:** #509; knowledge §6.2.

### [x] B-29 Open WebUI user JWT through a library (#512) (#525)
- **Do:** validate the forwarded user JWT with `Microsoft.IdentityModel.JsonWebTokens` (HS256 only, issuer
  `open-webui`, 30 s clock skew, the existing maximum lifetime), keeping every refusal code.
- **Paths:** `src/Ume.Rag.Api/**`, `tests/Ume.Rag.Api.Tests/**`, `Directory.Packages.props` (shared: its own small PR).
- **Done when:** the existing refusal tests pass unchanged; a token signed with another algorithm is refused.
- **Waits on:** –
- **Read:** #512; identity §5.

### [x] B-30 Editable embedding profiles before use (#554, #559, #558)
- **Do:**
  - The three endpoints `GET /api/embedding-profiles[/{id}]`, `PUT /api/embedding-profiles/{id}` and
    `POST /api/embedding-profiles/{id}/test`, as admin design §4.5 lists them: the `inUse` rule, the test before each
    save, the canary stored on save, and the index rebuilt on a dimension change.
  - In the admin UI: the profile on Platform → Gateway models (generic form, §5.8), with a **Test** button whose outcome
    names the fix on the gateway side.
  - The seeder's "exists with another model" warning points to the admin UI.
  - The seeder's code defaults follow the Decisions row "Pilot embedding model, U-b" (`RequestDimensions` true,
    `MaxInputTokens` 8191), as the deployment defaults have done since #546.
- **Paths:** `src/Ume.Rag.AdminApi/**`, `src/admin-ui/**`, `src/Ume.Rag.MigrationService/EmbeddingProfileSeeder.cs`,
  `tests/Ume.Rag.AdminApi.Tests/**`, `tests/Ume.Rag.IntegrationTests/**`.
- **Done when:** tests show all of these:
  - An unused profile's model, dimensions and `RequestDimensions` change, and its canary is recorded again.
  - A save whose test fails is refused with 422 `profile_test_failed`, and the profile is unchanged.
  - A profile with a chunk set refuses every field but `MaxResidency` with 409 `profile_in_use`.
  - **Test** against FakeGateway reports `model_not_allowed`, `max_residency_not_met` and a wrong dimension count in
    plain words.
  - After a dimension change the profile has exactly one HNSW index, at the new size.
- **Waits on:** –
- **Read:** Decisions "Editable embedding profiles before use"; admin §4.5, §5.8; data-model §2.1; processing §11.

### [x] B-31 Answer length from the model (#561, #564, #569, #571, #573, #574)
- **Do:** add `GatewayModels.MaxOutputTokens` (migration, check constraint) and let `PUT /api/gateway/models/{name}`
  set `contextWindow` and `maxOutputTokens` on any row, kept by the sync; make `answer.maxOutputTokens` and
  `answer.contextTokens` optional (`AssistantChatSettings` with nullable values; new drafts write neither), resolve
  them against the version's model row as chat §6.3 (`C`, `R`, `F` = 1,024, `max_tokens` = override, else
  `min(O, C − P)`, else left out in `GatewayChatClient`); V17's new bounds and the run-time lowering of an override
  above a lowered model limit; the Utkast tab's *Use the model's limit* checkboxes with the model's limit shown; the
  Gateway models page edits both limits; the test panel returns and shows `maxTokens`, `maxTokensSource` and
  `finishReason` ("The answer stopped at the length limit"); the optional platform ceiling
  `Settings.Chat.MaxAnswerTokens` (`X` in §6.3: caps `R`, override included, fills an unknown `R`; on
  `PUT /api/settings/chat` with its field on the Settings page; `maxTokensSource` `ceiling`; the Utkast tab shows it
  when lower). No compatibility step: the migration removes
  `answer.maxOutputTokens` and `answer.contextTokens` from every development `QuerySteps`.
- **Paths:** `src/Ume.Rag.Domain/**`, `src/Ume.Rag.Infrastructure/**` (migration), `src/Ume.Rag.Answering/**`,
  `src/Ume.Rag.AdminApi/**`, `src/Ume.Rag.Api/**` if the client's `max_tokens` cap moves, `src/Ume.Rag.FakeGateway/**`
  (echo the `max_tokens` it got, `finish_reason: length` on request), `src/admin-ui/**`, their tests.
- **Done when:** unit tests for the §6.3 table (override; model limit known; unknown → no `max_tokens`; override above
  the model limit lowered; `C − P` below `O`; the ceiling lowering an override, capping the model's limit and filling an
  unknown one); a FakeGateway test sees no `max_tokens` for a model without limits;
  rules tests for V17's bounds; vitest for the checkboxes and the test panel's length notice.
- **Waits on:** –
- **Read:** Decisions "Answer length from the model"; chat §6.3, §8.1; admin §4.9, §4.10, V17, §5.5.4 (Utkast tab);
  data-model `GatewayModels`, `AssistantVersions`.

### [x] B-32 Chat prompt texts in settings (#575, #576)
- **Do:** `Settings.Chat.Prompts` (chat §7.3 table) read through the config snapshot; `PromptBuilder`, `Condenser`
  (both prompts), the follow-up prompt and the two caveats take each text from it with today's text as the built-in
  default, the locked lines staying in code (the follow-up prompt's locked lines after its editable block, chat §9.8);
  `{assistant}` and `{N}` filled; V38 on `PUT /api/settings/chat`; the Settings page's **Prompt texts** panel (the
  whole frame in order, editable fields with **Reset to default**, locked lines read-only, the "changes every
  assistant at once" note). Labels and messages in `labels.ts` and `messages.ts`.
- **Paths:** `src/Ume.Rag.Answering/**`, `src/Ume.Rag.Api/**`, `src/Ume.Rag.AdminApi/**`, `src/Ume.Rag.Domain/**`,
  `src/admin-ui/**`, their tests.
- **Done when:** a golden test shows the default frame, condense prompts, follow-up prompt and caveats unchanged in
  wording (the follow-up lines in their new order); a test with every member set shows the locked lines still
  present; V38 refuses a `<sources>` tag, an unknown placeholder and an unknown member; the test panel answers with an
  edited identity line; vitest for the panel and its reset.
- **Waits on:** – (independent of B-31; touches `PromptBuilder` too, so merge one, rebase the other)
- **Read:** Decisions "Chat prompt texts configurable"; chat §5, §5.1, §7.2–§7.4, §9.8, §10.2, §10.4; admin §4.5
  (`chat` section), V38; data-model `Settings`.

### [x] B-33 rag-api reads the keyword statistics (#499 step 2, api part)
- **Do:** `ScopeSizeCache` builds the stop list from `content."KeywordStatistics"` (knowledge §6.2: share = `Ndoc` /
  the row's `ChunkCount`, the same floors and the multi-collection lower bound) instead of `FrequentLexemes`;
  `RetrievalStatements.FrequentLexemes` and its `SET LOCAL statement_timeout` leave rag-api (the worker's A-29 owns the
  statement). `CountChunks` stays.
- **Paths:** `src/Ume.Rag.Retrieval/**`, `src/Ume.Rag.Api/**` if needed, their tests.
- **Done when:** rag-api runs no `ts_stat` (`pg_stat_statements` on a test database, or a test that the api role
  issues none); the stop list on the evaluation collection equals today's.
- **Waits on:** A-29 (the table).
- **Read:** #499; knowledge §6.2.

## Live (the live block, see the roadmap)


### [x] B-34 Schema contract for stream b's positional SQL readers (#510, follow-up of A-28)
- **Do:** add the raw-SQL statements of stream b's stores to A-28's schema contract (`SqlSchemaContractTests`,
  `sql-shapes.json`, #588): the retrieval statements and hydrator (`src/Ume.Rag.Retrieval/Postgres/**`), the admin
  API's and rag-api's raw readers, the column names and types each reader expects, with `CommandBehavior.SchemaOnly`.
  Move the harness to `tests/Ume.Rag.ContractTests/**` if both streams' statements fit there better.
- **Paths:** `tests/**` (the harness and shapes), `src/Ume.Rag.Retrieval/**`, `src/Ume.Rag.AdminApi/**`,
  `src/Ume.Rag.Api/**` (only where a statement must be exposed as a constant).
- **Done when:** a renamed column in a retrieval or admin statement fails the test.
- **Waits on:** –
- **Read:** #510, #588.
### [x] B-35 Gateway phase 1: context and model sync with the organisation, the keys mirror, the FakeGateway contract (#580, cost-attribution B3)
- **Do:** the admin's sync of admin §4.10 from the gateway's discovery with the admin's key, `If-None-Match`/`ETag`
  and nothing written on `304`: `GET /v1/ume/context` → `gateway.features`, `version`, `admin_url` in memory and on the
  Gateway page; `departments` and `teams` → `Departments`/`Teams`, taken only when `features` contains `organisation`
  and `key.integration_scopes` contains `organisation.read` (otherwise left as they are, `degraded` with the reason);
  member groups from each department's `member_groups` when `features` contains `member-groups` (identity §2.1), and
  the hand-entry route `PUT /api/organisation/departments/{id}/member-groups` goes (stream a removes its platform UI
  edit, A-33); `assigned_keys` →
  the `GatewayKeys` mirror with `Usable` (data-model §2.1; migration and grants, rag-api `S`); `GET /v1/models` →
  `GatewayModels` from the `ume` fields (`kind`, `residency`/`residencies`, `context_window`, `max_output_tokens`,
  `features`, `pricing`), the manual limit fields kept only while the gateway sends `null`. Drop the Data API path:
  `OrgSync:DataApiKey`, the catalogue client and the FakeGateway's `/v1/catalog/*`. The FakeGateway answers as
  the gateway's contract `docs/openapi/integrations.json` describes (`key.integration_scopes`, `key.allowed_residencies`,
  `usable`, the features `credential-residencies` and `organisation`; the admin's dev credential key holds
  `organisation.read` and gets every department and team, an inactive one included). Pin the document in
  `contracts/gateway/` with the gateway release it was taken from (`v0.10.0`; part 1 pinned #84's `7b678a1`, which
  lacks `member_groups` and the feature `member-groups`), and a contract test checks the FakeGateway's `/v1/ume/context` and `/v1/models`
  answers against its schemas: an undescribed field or a missing required one fails, as in the gateway's own tests.
- **Paths:** `src/Ume.Rag.AdminApi/**` (`OrganisationSync.cs`, `GatewayModelSync.cs`, `GatewayEndpoints.cs`),
  `src/Ume.Rag.Domain/**`, `src/Ume.Rag.Infrastructure/Persistence/**`, `deploy/postgres/app-roles.sql` (+ its
  copy `deploy/kubernetes/base/app-roles.sql`; stream a regenerates the Portainer stack, issue labelled `stream-a`), `src/Ume.Rag.FakeGateway/**`, `src/Ume.Rag.AppHost/**` (the Data API key
  goes), `contracts/gateway/**`, `src/admin-ui/src/**` outside `platform/` (the Gateway page's features and version),
  `tests/**`.
- **Done when:** against the FakeGateway: the sync fills `GatewayKeys` from `assigned_keys` (a revoked key's row goes,
  a disabled one keeps its `Status`, `usable: false` is stored); departments and teams come from the context, an
  inactive one turns `IsActive = false`, a department's `member_groups` replace its stored ones and the PUT route is
  gone; without `organisation.read` the
  sync leaves departments and teams unchanged and reports `degraded`; a second run with an unchanged `ETag` writes
  nothing; a model's limits come from `ume.context_window`/`max_output_tokens` and the manual fields don't show for
  it; no `OrgSync:DataApiKey` or `/v1/catalog/` call is left; the contract test fails when the FakeGateway renames a
  field or drops a required one.
- **Waits on:** –. The live run needs the gateway on v0.10.1 (released 2026-11-12).
  A-30 removes the Data API key from the deployment.
- **Read:** Decisions "Gateway keys"; admin §4.10; identity §2, §2.1; data-model `GatewayKeys`; gateway-changes §5,
  §5.5; repo-and-deployment §4.3; the gateway's `docs/openapi/integrations.json` and `docs/integrations.md` at `7b678a1`;
  #580 (the gateway owner's comment of 2026-11-12).

### [x] B-36 Gateway phase 1: a gateway key per assistant and knowledge base, the key picker, V39 (#580, cost-attribution B4)
- **Do:** `GatewayKeyId` on `Assistants` and `Collections` (data-model §2.1); `GET /api/gateway/keys?departmentId=`
  (admin §4.5); the key picker on the assistant's and knowledge base's pages with the key's settings read-only and a
  link to the gateway (`admin_url`), a warning when the key has a system prompt (admin §5.5.4); V39 on save (admin §4.6:
  an `Active` and `usable` key of the object's department, owner active, a residency within the object's level, every
  model the object uses allowed); a key the gateway marks `usable: false` shows in the picker as not selectable, with
  the reason (it shares no residency with Ume.Rag's credential key). No call is made as the key yet, and publishing doesn't need one until the gateway's `features`
  contain `act-as-key` (phase 2).
- **Paths:** `src/Ume.Rag.AdminApi/**`, `src/Ume.Rag.Domain/**`, `src/Ume.Rag.Infrastructure/Persistence/**`,
  `src/admin-ui/src/views/**`, `src/admin-ui/src/utils/labels.ts`, `messages.ts`, `contracts/admin/**`, `tests/**`.
- **Done when:** the picker lists only the department's keys, unusable ones marked with their reason; V39 refuses
  another department's key, a disabled key, a key with `usable: false`, a key whose residencies are all above the object's level and a key that
  lacks one of the object's models (the embedding profile's and rerank models included), each with its code; a saved
  key shows its settings read-only.
- **Waits on:** B-35 (the mirror).
- **Read:** admin §4.5, §4.6 V39, §5.5.2, §5.5.4; data-model §2.1; gateway-changes §5.3, §5.5; cost-attribution §4.

### [x] B-37 Gateway phase 1: one credential key per service (#580, cost-attribution B2, code part)
- **Do:** `Gateway:Key` (secret file `Gateway__Key`) in place of `Gateway:Keys:OnPrem/Eu/External` in the gateway
  client of api, admin and worker; `ResidencyCeilingHandler` refuses a call without a ceiling or with one above the
  service's maximum (the worker `Eu`; api and admin the top of their `Gateway:MaxResidency` ladder), unit-tested at
  every call site as before (identity §4.7); the FakeGateway accepts a credential key; the AppHost passes one dev key per
  service. Don't send `x-ume-on-behalf-of` yet (phase 2).
- **Paths:** `src/Ume.Rag.Infrastructure/Gateway/**`, `src/Ume.Rag.AppHost/**`, `src/Ume.Rag.FakeGateway/**`, the
  services' gateway registration, `tests/**`.
- **Done when:** no `Gateway:Keys:*` is read anywhere; a call above the service's maximum is refused before sending and
  logged; the call-site tests pass with one key per service; the local stack answers a chat on the FakeGateway.
- **Waits on:** –. A-30 (deploy) follows with the same key names.
- **Read:** identity §4.7; repo-and-deployment §6.3, §7.1; gateway-changes §5.1.

### [x] B-38 (#608, #615, by the main session) Cost per step: every chat model call's tokens and cost, the usage tables, the FakeGateway's cost (cost-attribution C1)
- **Do:** every model call of a chat turn (condense, long-question condense, query embedding, rerank, answer,
  follow-ups) yields a usage record of chat §8.3: its `x-ume-step`, input, cached input
  (`usage.prompt_tokens_details.cached_tokens`) and output tokens, and its cost from `x-ume-cost-usd`, else the stream
  comment `: ume-cost-usd=<usd>` (every streamed answer sends `x-ume-stream-cost: 1`; the stream reader takes the
  comment and never forwards it), else an estimate from `GatewayModels`' prices (cached input at its own price), kept
  apart. The reading and the estimate live once in `src/Ume.Rag.Infrastructure/Gateway/` so the worker (A-31) uses the
  same code; `IReranker` and the query embedder return their call's usage. `ChatTurn` carries the turn's usage records;
  `ConversationWriter` upserts `chat."AssistantDailyUsage"` per (day, assistant, client, level, step) last in the
  batch (chat §12.2), and a turn that fails after a model call is enqueued usage-only. Migration: the new
  `chat."AssistantDailyUsage"` and `content."CollectionDailyUsage"` (data-model §2.2, §2.3; the worker's table here so
  A-31 needs no stream-b path), `InputTokens`/`OutputTokens`/`CostUsd` dropped from `AssistantDailyTotals` (and from
  the totals inserts of `FeedbackEndpoints` and `FeedbackImporter`), `rag-admin` `I U` on `AssistantDailyUsage`
  (data-model §1.2), retention of both (data-model §8). Metrics `rag.chat.tokens` (`cached-input`, steps `rerank`,
  `query-embed`) and `rag.chat.cost{step, source}` (chat §17). The FakeGateway sends `x-ume-cost-usd` on every JSON
  answer (chat, embeddings, rerank) from its catalogue prices, writes the stream comment just before `data: [DONE]` when the
  request has `x-ume-stream-cost: 1` (gateway-changes §5.4), reports `cached_tokens` when scripted, and a model `fake-no-cost` that sends
  neither.
- **Paths:** `src/Ume.Rag.Answering/**`, `src/Ume.Rag.Retrieval/**`, `src/Ume.Rag.Api/**`,
  `src/Ume.Rag.AdminApi/FeedbackEndpoints.cs`, `src/Ume.Rag.Infrastructure/Gateway/**`,
  `src/Ume.Rag.Infrastructure/Persistence/**`, `src/Ume.Rag.Domain/**`, `deploy/postgres/app-roles.sql` (+ its
  copy `deploy/kubernetes/base/app-roles.sql`; stream a regenerates the Portainer stack, issue labelled `stream-a`), `src/Ume.Rag.FakeGateway/**`, `tests/**`.
- **Done when:** against the FakeGateway (chat §18.2): a turn with condense, query embedding, rerank, answer and
  follow-ups writes one `AssistantDailyUsage` row per step whose tokens and `CostUsd` equal the FakeGateway's; the
  streamed answer's cost comes from the comment and the comment is absent from the client's stream; on `fake-no-cost`
  the cost is in `EstimatedCostUsd` (cached input priced at `CachedInputPerMillionUsd`), `EstimatedCalls` counts it
  and `CostUsd` stays 0; a failed answer call still writes condense's and the embedding's usage; a cached query
  embedding writes nothing; `AssistantDailyTotals` has no token or cost column left.
- **Waits on:** –. Gateway phase 2 (the stream comment) is merged. **Taken by the main session** (stream b released it
  on #580, 2026-11-12). **Part 1 is done** (#608): the two usage tables with grants and the chat retention step, the
  shared reader and estimate (`src/Ume.Rag.Infrastructure/Gateway/GatewayUsage.cs`), usage from the rerankers and the
  query embedder, the FakeGateway's cost. **Part 2 is done** (#615): the chat turn's wiring, the totals' columns
  dropped, the metrics, the §18.2 tests. B-39 can pass `chat.Usage()` from the test panel (hand-off in #615).
- **Read:** cost-attribution §5; chat §8.3, §12.1, §12.2, §12.4, §17, §18.2; data-model §1.2, §2.2, §2.3, §8;
  gateway-changes §5.4, §7.1.

### [x] B-39 Cost per step: the test panel's calls under `admin-console` (cost-attribution C2) (#625)
- **Do:** the test panel (admin §4.9) collects the usage records of its turn as chat does (B-38) and `rag-admin`
  upserts them into `AssistantDailyUsage` under the assistant and the `admin-console` client row (its id from the
  snapshot by name and kind `internal`; without the row the usage is not written and a warning is logged), never into
  `AssistantDailyTotals`; `AssistantTestEndpoints` uses that row instead of a random client id. The panel's response
  carries `gateway.costUsd` and `costEstimated`, shown on the panel ("0,0012 USD", "estimated"). The dev seed adds the
  `admin-console` row (identity §4.1).
- **Paths:** `src/Ume.Rag.AdminApi/**`, `src/Ume.Rag.MigrationService/DevSeeder.cs`, `src/admin-ui/src/views/**`,
  `src/admin-ui/src/components/**`, `src/admin-ui/src/utils/labels.ts`, `messages.ts`, `contracts/admin/**`,
  `tests/**`.
- **Done when:** a test-panel turn on the FakeGateway adds its steps to `AssistantDailyUsage` under `admin-console`
  with the FakeGateway's costs and leaves `AssistantDailyTotals` unchanged; the response's `costUsd` is the sum of its
  calls and `costEstimated` is true on `fake-no-cost`; a write failure leaves the answer intact.
- **Waits on:** B-38.
- **Read:** admin §4.9, §11.2; chat §8.3, §12.2; identity §4.1; data-model §1.2, §2.3.

### [x] B-40 Cost report API: per assistant, knowledge base and department, by day or month, CSV (cost-attribution C4) (#627)
- **Do:** admin §8.6's routes `GET /api/assistants/{id}/costs`, `GET /api/collections/{id}/costs` (Read; the
  collection's own department only) and `GET /api/costs` with `format=csv` (Manage: department admins their
  departments, platform admins all) over `AssistantDailyUsage` and `CollectionDailyUsage`: per period (Europe/Stockholm
  day or calendar month), step, client (assistants) and object, with calls, tokens, `costUsd`, `estimatedCostUsd`,
  `estimatedCalls`; scope by the row's `DepartmentId`; names from the snapshot, deleted objects as id + "(deleted)";
  limits 400 days by day and 36 months by month (400 `period_too_long`); the CSV with `;` and the headers of §8.6;
  `PermissionMatrix` entries (§3.4).
- **Paths:** `src/Ume.Rag.AdminApi/**`, `src/Ume.Rag.Domain/**` (`PermissionMatrix`), `contracts/admin/**`,
  `tests/**`.
- **Done when:** on seeded usage rows the routes return the expected sums by day and by month with gateway and
  estimated cost apart; the generated role matrix passes (a department admin gets 404 for another department's
  assistant and no foreign rows from `/api/costs`; a platform admin sees all; another department gets no cost for a
  shared knowledge base); the CSV has the documented headers; a too-long period gives `period_too_long`.
- **Waits on:** B-38 (the tables). The knowledge-base figures are empty until A-31 fills them; the routes and their
  tests don't wait on it.
- **Read:** admin §3.3, §3.4, §4.8, §8.6, §11.2; data-model §2.2, §2.3; cost-attribution §4.1, §5.

### [x] B-41 Cost in the admin UI: assistant and knowledge base cards, the Costs page (cost-attribution C5) (#628)
- **Do:** admin §8.6's UI: the *Cost* card on the assistant's Statistics tab (period picker, total with gateway and
  estimated parts, by step and client), the *Cost* card on the knowledge base's Overview (indexing and evaluation, last
  30 days, owning department only), and the *Costs* page under FOLLOW-UP for department and platform admins
  (department filter, period, granularity, one row per assistant and knowledge base with its cost, expandable by step,
  totals, **Download CSV**, the two notes on estimates and the gateway as the record). Route guard as admin §3.3. Every
  label in `labels.ts` (the step names too), every sentence in `messages.ts`.
- **Paths:** `src/admin-ui/src/views/**`, `src/admin-ui/src/components/**`, `src/admin-ui/src/router/**`,
  `src/admin-ui/src/api/**`, `src/admin-ui/src/utils/labels.ts`, `messages.ts`, `tests/e2e/**` if a browser test is
  added.
- **Done when:** Vitest covers the cards and the page against the B-40 contract (sums, estimated parts, empty states);
  a viewer has no Costs entry and the route sends them to `/forbidden`; the CSV link carries the filters; no UI string
  outside `labels.ts`/`messages.ts`.
- **Waits on:** B-40.
- **Read:** admin §3.3, §5.2, §5.3, §5.5.2, §5.5.4, §5.7, §8.6; Decisions "Admin UI in English".

### [ ] B-42 Gateway phase 2: every api and admin call as the object's key, the echo check, the FakeGateway acting (#580, cost-attribution B5)
- **Do:** in the shared client (`src/Ume.Rag.Infrastructure/Gateway/`): each service reads `gateway.features` from its
  own `GET /v1/ume/context` with its credential key, at start and every 5 minutes with `If-None-Match`, held in memory
  (identity §4.7); the call's object key travels on the request and `ResidencyCeilingHandler` sets
  `x-ume-on-behalf-of` from it only while the features contain `act-as-key`; on every `2xx`/`304` answer the echo is
  parsed and compared as a GUID, and a missing or different one throws before use with event 5011 (`Step`,
  `AssistantId` or `CollectionId`, `GatewayKeyId`, `GatewayRequestId`) while `Gateway:RequireDelegationEcho` (default
  `true`) is on; the refusals of gateway-changes §5.7 on an acted call (`403 delegation_not_allowed`, `403
  key_disabled`, `403 key_revoked`/`key_expired`, `402 budget_exceeded` with the gateway's message) surface as one typed
  refusal the callers map (B-44, A-34), and `400 invalid_request` is logged at Error and never retried. rag-api: every
  call of a turn as the assistant's `GatewayKeyId` (answer, condense, long-question condense and follow-ups in
  `GatewayChatClient`, the query embedding in `GatewayQueryEmbedder`, rerank in `GatewayReranker`, `ChatStreaming`),
  a Knowledge API caller without an assistant as the first searched collection's key (knowledge §5.2); rag-admin: the
  test panel as the assistant's key, the retrieval test as the collection's (cost-attribution §4.1). The FakeGateway's
  phase 2 of repo-and-deployment §4.3: features `act-as-key` and `run-metadata`; the header resolved as the gateway does
  (`400` malformed or repeated, `403 delegation_not_allowed` for an id not assigned to the integration or sharing no
  residency with the credential key, `403 key_disabled`, `403 key_expired`), the request served under the assigned
  key's models and bounded residencies, the id echoed in the `D` form on every later answer; `/v1/models` with the
  header lists the key's models; `FakeGateway:DropOnBehalfOfEcho`; `FakeGateway:AssignedKeys:<n>:BudgetExceeded`
  (`Key`, `Team`, `Department`: `402 budget_exceeded` with the gateway's message); each served request recorded with
  the key it ran as, its `x-ume-run-id`, `x-ume-step`, tokens and cost (for B-45). `docs/security-events.md`'s 5011 row
  to the key form (it still names a team).
- **Paths:** `src/Ume.Rag.Infrastructure/Gateway/**`, `src/Ume.Rag.Answering/**`, `src/Ume.Rag.Retrieval/**`,
  `src/Ume.Rag.Api/**`, `src/Ume.Rag.AdminApi/**` (`AssistantTestEndpoints.cs`, `TestRetrievalEndpoints.cs`, the
  gateway registration), `src/Ume.Rag.FakeGateway/**`, `docs/security-events.md`, `tests/**` (not
  `tests/Ume.Rag.ContractTests/**`, `tests/Ume.Rag.Worker.Tests/**`).
- **Done when:** against the FakeGateway with `act-as-key`: every call of a chat turn (condense, query embedding,
  rerank, answer, follow-ups) and of a test-panel turn carries the assistant's key id and the FakeGateway records it
  under that key; an assistant without a key, or a FakeGateway without `act-as-key`, sends no header; with
  `DropOnBehalfOfEcho` the turn is aborted before a byte reaches the client and 5011 is logged, and passes with
  `Gateway:RequireDelegationEcho=false`; an upper-case echo passes; an unknown id (`403 delegation_not_allowed`), a
  disabled key (`403 key_disabled`), an expired one (`403 key_expired`) and a spent budget (`402`) each surface as the
  typed refusal, not retried; the handler's call-site tests cover the header at every call site.
- **Live check:** on the interim gateway on v0.10.1 (not deployed yet): one chat turn of an assistant with an assigned
  key shows under that key in the gateway's usage report, with rag-api's credential key as the via key and the turn's
  run id and steps.
- **Waits on:** B-35 (the contract pinned at v0.10.0, the FakeGateway's assigned keys), B-36 (`GatewayKeyId` on
  assistants and collections and in rag-api's snapshot). The stream cost (`x-ume-stream-cost`, the comment) and the
  usage records are B-38's: not repeated here.
- **Read:** gateway-changes §5.2, §5.3, §5.4, §5.7, §7.1; identity §4.7, §10; chat §8.1, §8.2; knowledge §5.2;
  cost-attribution §4.1; repo-and-deployment §4.3.

### [ ] B-43 Gateway phase 2: save checks as the key, publish needs a key, the acted status check (#580, cost-attribution B5, admin part)
- **Do:** once the admin's context shows `act-as-key`: V8's and V39's `GET /v1/models` as the object's key
  (`x-ume-on-behalf-of`) at the ceiling (admin §4.6): each missing model named (`gateway_key_model`), a refusal of the
  key `gateway_key_invalid`, a missing echo `gateway_unavailable` with 5011; `GET /api/gateway/models?gatewayKeyId=`
  computes `available` as that key (admin §4.5); publish and restore refuse an assistant without a key
  (`gateway_key_required`, 409), and a knowledge base without one keeps saving and indexing on the credential key with
  its page's note (cost-attribution §4.2); `GET /api/gateway/status` adds one acted `/v1/models` call's echo (the first
  usable mirrored key). The publish dialog and the key picker show the new refusals (`messages.ts`).
- **Paths:** `src/Ume.Rag.AdminApi/**`, `contracts/admin/**`, `src/admin-ui/src/views/**`,
  `src/admin-ui/src/components/**`, `src/admin-ui/src/utils/labels.ts`, `messages.ts`, `tests/**` (not
  `tests/Ume.Rag.ContractTests/**`, `tests/Ume.Rag.Worker.Tests/**`).
- **Done when:** against the FakeGateway with `act-as-key`: V39 refuses a key whose `/v1/models` lacks the condense
  model, naming it, though the admin's own list has it; a disabled key gives `gateway_key_invalid`; with
  `DropOnBehalfOfEcho` the save gives `gateway_unavailable` and 5011; publishing an assistant without a key gives
  `gateway_key_required` (without `act-as-key`: the warning only); a knowledge base without a key saves; the status
  answer reports the echo; Vitest shows each refusal's text.
- **Live check:** on the interim gateway on v0.10.1: the key picker's model availability and V39 follow a real
  assigned key's models; the status page's echo is ok.
- **Waits on:** B-42 (the handler and the FakeGateway's acting), B-36 (V39 and the picker).
- **Read:** admin §4.5, §4.6 V8, V39, §5.5.2, §5.5.4; gateway-changes §5.5; cost-attribution §4.2.

### [ ] B-44 Gateway phase 2: a spent budget or an unusable key in chat and the admin (#580, cost-attribution B6, api and admin part)
- **Do:** chat §3.6: at step 7 an assistant whose `GatewayKeys` row is missing, not `Active`, not `OwnerActive` or not
  `Usable` answers 503 `assistant_unavailable` before any model call; B-42's typed refusal maps to 503
  `assistant_unavailable` (the key) or 402 `budget_exceeded` with the gateway's message (a budget) before the first
  byte; the turn's outcome is `unavailable`, counted in the daily totals; a refusal after the first byte is §9.6's
  error chunk; Knowledge API callers get §5.5's embedding failure (knowledge §5.2); the test panel shows the same
  reason. rag-admin: the `[Unavailable]` badge with its reason on the assistant's and knowledge base's pages, derived
  from the mirror (admin §5.5.4, §5.5.2), and from B-45's budgets once there; the attention-list source of admin
  §5.5.1 (the list itself comes with P2's `GET /api/overview`; until then the badge is the signal).
- **Paths:** `src/Ume.Rag.Answering/**`, `src/Ume.Rag.Api/**`, `src/Ume.Rag.Retrieval/**`, `src/Ume.Rag.AdminApi/**`,
  `contracts/admin/**`, `src/admin-ui/src/views/**`, `src/admin-ui/src/components/**`,
  `src/admin-ui/src/utils/labels.ts`, `messages.ts`, `tests/**` (not `tests/Ume.Rag.ContractTests/**`,
  `tests/Ume.Rag.Worker.Tests/**`).
- **Done when:** against the FakeGateway: a key with `BudgetExceeded: Department` gives 402 `budget_exceeded` with the
  department's message before the first byte and an `unavailable` turn in the totals; a disabled key in the mirror
  gives 503 `assistant_unavailable` and the FakeGateway saw no call; a key the FakeGateway refuses (`403
  delegation_not_allowed`) while the mirror still shows it active gives 503 `assistant_unavailable`; the test panel
  shows both reasons; the assistant page shows `[Unavailable]` with "The gateway key is disabled".
- **Live check:** on the interim gateway on v0.10.1: a small key budget set in the gateway stops the assistant with the
  planned message (cost-attribution §6's done-when).
- **Waits on:** B-42 (the typed refusal, the FakeGateway's modes), B-36 (the key on the assistant and its page).
- **Read:** chat §3.6, §4.1, §8.2, §9.6; knowledge §5.2, §5.5; admin §5.5.1, §5.5.2, §5.5.4; gateway-changes §5.4,
  §5.7; cost-attribution §4.2.

### [ ] B-45 Gateway phase 3: the Integration API read-back, cost and budget per key (#580, cost-attribution B7, API part)
- **Do:** `Gateway:IntegrationApiUrl` in rag-admin (unset: no read-back, the routes answer `null` without a call); a
  client for `/integration/v1/usage` and `/budgets` with rag-admin's credential key (`usage.read`), parameters and
  fields as gateway-changes §5.6; `GET /api/gateway/keys/{id}/budget` (admin §4.5) and
  `GET /api/gateway/keys/{id}/usage` (admin §8.6), cached 5 minutes per key and query; `401`, `403` (scope missing)
  and `429` shown on the *Gateway* page as the reason; the budgets' `exhausted` feeds B-44's badge. The FakeGateway's
  `/integration/v1` (repo-and-deployment §4.3): `usage` from the requests it served (B-42's record) with `from`, `to`,
  `granularity`, `groupBy`, `keyId`, `runId`, `400` beyond 366 days or for an unknown `groupBy`, `404` for a key not
  of the integration; `budgets` from `FakeGateway:AssignedKeys:<n>:Budgets`; `usage.read` on the admin's dev
  credential key only (`401` for a key that is not a credential key, `403` without the scope); the AppHost passes the
  dev `Gateway:IntegrationApiUrl`. The contract test checks both answers against the pinned `integrations.json`.
- **Paths:** `src/Ume.Rag.AdminApi/**`, `src/Ume.Rag.FakeGateway/**`, `src/Ume.Rag.AppHost/**`, `contracts/admin/**`,
  `tests/**` (not `tests/Ume.Rag.ContractTests/**`, `tests/Ume.Rag.Worker.Tests/**`).
- **Done when:** against the FakeGateway, after chat turns on two keys: `GET /api/gateway/keys/{id}/usage` returns per
  step that key's tokens and cost only, by day and by month; `/budget` returns an exhausted department budget with
  `exhausted: true`; rag-api's credential key gets `403` from the FakeGateway's `/integration/v1`; with
  `Gateway:IntegrationApiUrl` unset both routes answer `null` without a call; a second read within 5 minutes makes no
  call; the contract test fails on a renamed or missing field.
- **Live check:** on the interim gateway on v0.10.1 with `usage.read` on rag-admin's key: an assistant key's cost for
  a day equals the gateway's usage report for that key.
- **Waits on:** B-42 (the FakeGateway's record per request under the acting key), B-36 (keys on objects), B-35 (the
  pin at v0.10.0).
- **Read:** gateway-changes §5.6; admin §4.5, §8.6; repo-and-deployment §3 (`Gateway:IntegrationApiUrl`), §4.3;
  cost-attribution §6.

### [ ] B-46 Gateway phase 3 in the admin UI: budget status and the gateway's cost beside Ume.Rag's (#580, cost-attribution B7, UI part)
- **Do:** the key header's budget status on the assistant's and knowledge base's pages (admin §5.5.4: per budget its
  scope, spent of limit with the percentage, the period's end, *Used up*); the gateway's figures on B-41's *Cost* cards
  beside Ume.Rag's (admin §8.6), with the note when several objects share the key and the gateway link when there is no
  read-back. Every label in `labels.ts`, every sentence in `messages.ts`.
- **Paths:** `src/admin-ui/src/views/**`, `src/admin-ui/src/components/**`, `src/admin-ui/src/api/**`,
  `src/admin-ui/src/utils/labels.ts`, `messages.ts`.
- **Done when:** Vitest against B-45's contract: the budget status with an exhausted budget, the gateway's figures
  beside the stored ones by step, the shared-key note, `null` showing the link; no UI string outside
  `labels.ts`/`messages.ts`.
- **Live check:** on the interim gateway on v0.10.1: a real assistant's card shows its key's gateway cost and budget.
- **Waits on:** B-45, B-41 (the cards).
- **Read:** admin §5.5.4, §8.6; Decisions "Admin UI in English".

### [x] B-L1 Status green against the real gateway
- **Done when:** `GET /api/gateway/status` green with the pilot keys; #95, #102, #162 rechecked.
- **Waits on:** the gateway session (EU routes, pilot keys).

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

### [x] B-L4 Gateway release 1 switch
- **Do:** the pilot team's per-level keys with `RequireMaxResidency`; every call sends its ceiling.
  Remove the worker's embedding batch cap below the context window (the gateway checks per input from release 1);
  `502`/`504` retry with the same ceiling, never wider.
- **Done when:** status green against release 1; `/v1/models` at a ceiling lists only models within it (#41).
- **Waits on:** gateway release 1 (tagged v0.9.0, 2026-11-06) deployed for the pilot.
- **Read:** gateway-changes §13.2–§13.4.

### [ ] B-L5 Feedback import live
- **Done when:** a pilot rating appears on the Feedback page; Open WebUI feedback older than 90 days is pruned (#49,
  #135).
- **Waits on:** the shared Keycloak with the feedback service account.
- **Read:** chat §13.3, §14.2.

### [x] B-47 Malware scan optional: the `unscanned` state, `ScannedAt`, event 5106, the AppHost's `ClamAv:Mode`, the admin's note (#535)
- **Do:**
  - **State and column:** `UploadStates.Unscanned` (`unscanned`) in the `State` check and `Uploads.ScannedAt
    timestamptz NULL`, in one migration (connectors §9.1). No data step: the PR gives the SQL that sets `ScannedAt` on
    development rows that were scanned.
  - **Event:** 5106 `rag.malware.scan_off` (Warning) in `SecurityEvents`, with its row in `docs/security-events.md`.
  - **AppHost:** `ClamAv:Mode` = `on` (default) | `off` replaces `ClamAv:Enabled`. `off` starts no `clamav` resource
    and passes `ClamAv__Mode=off` to the worker.
  - **Admin API:** `unscanned` counts as *processing*; each file carries `scannedAt` (admin §4.5); the contract follows.
  - **Admin UI:** *Not scanned for malware* on a file's row and details when it is `unscanned`, or `indexed` with no
    `scannedAt`; *Files in this run were not scanned for malware* in a run's summary when its report says
    `malwareScan: "off"` (admin §5.5.7). Texts in `labels.ts` and `messages.ts` only.
- **Paths:** `src/Ume.Rag.Domain/**`, `src/Ume.Rag.Infrastructure/Persistence/**`,
  `src/Ume.Rag.Infrastructure/Security/**`, `src/Ume.Rag.AppHost/**`, `src/Ume.Rag.AdminApi/**`,
  `src/admin-ui/src/views/**`, `src/admin-ui/src/utils/labels.ts`, `messages.ts`, `contracts/admin/**`,
  `docs/security-events.md`, `tests/**`.
- **Done when:** the migration applies and the schema contract shapes are regenerated; admin API tests: an `unscanned`
  slot is among *processing*, and an `indexed` slot without `ScannedAt` answers `scannedAt: null`; Vitest: the note
  shows on those files and not on a scanned one, and the run line shows for `malwareScan: "off"`; the AppHost with
  `ClamAv:Mode=off` has no `clamav` resource and the worker's environment has `ClamAv__Mode=off`.
- **Waits on:** –. **Part 1 is done** (#599, by the main session with the user's go-ahead, 2026-11-12): the state,
  the column and its migration `UploadScannedAt`, event 5106 and the AppHost's `ClamAv:Mode`. **Left for stream b:**
  the admin API (`unscanned` among *processing*, `scannedAt` on each file, the contract) and the admin UI's two notes.
  The worker writes `unscanned`, `ScannedAt` and `Runs.Report.malwareScan` since A-36 (#602).
- **Read:** Decisions "Malware scan optional"; connectors §9.1, §11; admin §4.5, §5.5.7; identity §10;
  repo-and-deployment §4.1.

### [x] B-48 A cancelled turn is recorded: the user's message, the partial answer, its usage, the `Cancelled` count (chat §8.3) (#636)
- **Do:** when the client disconnects or the idle or total timeout ends a streamed answer (chat §8.3), the writer gets
  the turn as any other (§12): the user's message, the partial answer as delivered so far with `Outcome = cancelled`,
  the turn's usage records (the aborted answer at its prompt estimate, as B-38 records it now, plus the calls before
  it), and `AssistantDailyTotals.Cancelled` counted in the same transaction. Today such a turn writes usage only:
  no message row, and `ConversationWriter` inserts `Errors` and `Cancelled` as constant `0`. Count `Errors` the same
  way for a turn that ends `error` after the user's message was accepted, if it doesn't already.
- **Paths:** `src/Ume.Rag.Api/**`, `src/Ume.Rag.Answering/**`, their tests.
- **Done when:** a test disconnects at the first byte and mid-stream (FakeGateway `fake-slow` or a stalled stream):
  one user and one assistant message are stored, the second with `Outcome = cancelled` and the delivered text, the
  `chat-answer` usage row is estimated, and the day's `Cancelled` is 1; an idle timeout does the same; chat §18.4's
  "partial turn `cancelled`" case passes.
- **Waits on:** nothing (B-38 part 2, #615, records the usage).
- **Read:** chat §8.3 (client disconnect, usage and cost), §12, §18.4; data-model §2.3 `Messages`, `AssistantDailyTotals`.
