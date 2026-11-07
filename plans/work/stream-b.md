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

### [ ] B-21 One connection pool in rag-api (M-3, #127)
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

### [ ] B-23 Small admin gaps from the deviation review (M-3)
- **Do:** (1) the feedback import alert after 4 failed runs (chat §14.3; today only `LastOutcome`,
  `FeedbackEndpoints.cs:99-107`); (2) the collection page's sources table shows documents and the last run (the API
  returns both, `CollectionView.vue:92-96`, admin §5.5.2); (3) the deletion card's "at most 20 % (at least 5)" comes from
  the configured deletion guard (`labels.ts:1032`, `DeletionGuardSettings`); (4) `deploy/keycloak/rag-additions.json`'s
  `open-webui` description no longer says "legacy callback path".
- **Paths:** `src/Ume.Rag.Api/**`, `src/Ume.Rag.AdminApi/**`, `src/admin-ui/**`, `deploy/keycloak/**`, their tests.
- **Done when:** a test per point (vitest for 2 and 3; an API or importer test for 1; the realm-additions test for 4).
- **Waits on:** –
- **Read:** chat §14.3; admin §5.5.2, §5.5.3.

### [ ] B-24 Restore a deleted uploaded file (M-3, #309)
- **Do:** `POST /api/sources/{id}/files/restore` within the upload grace (connectors §9.3, admin §4.5 files row): the
  UI promises "Within 14 days the platform team can bring them back" (`labels.ts:580`, `:1044`) and no route exists.
  Build the route (platform admin) and a *Restore* action on the files list for deleted slots, or change the texts if
  the design decides otherwise (ask main first).
- **Paths:** `src/Ume.Rag.AdminApi/**`, `src/Ume.Rag.Worker/**` (if the restore needs a job), `src/admin-ui/**`.
- **Done when:** a deleted file restored within the grace is searchable again after its sync; tests on both sides.
- **Waits on:** –
- **Read:** connectors §9.3, §13.4; admin §4.5.

## Live (the live block, see the roadmap)

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
