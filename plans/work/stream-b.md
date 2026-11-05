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

### [ ] B-2 Archived assistants and one document count (#374)
- **Do:** show "Archived" for archived assistants in *Used by* and leave them off the Map (or mark them); use one
  document count on the knowledge base list and its Overview (live documents), or label the difference.
- **Paths:** `src/Ume.Rag.AdminApi/**`, `src/admin-ui/**`.
- **Done when:** vitest for both pages; the Map route test leaves out an archived assistant.
- **Waits on:** –
- **Read:** admin §5.5.2, §5.5.6.

### [ ] B-3 Close the open records of items 3, 9, 10 and 12
- **Do:** record what is left of #23 (config lock wait, `rag.admin.config_lock.wait`), #20 (the writer's batch),
  #146 (generic forms for platform objects, data protection `none` only); confirm knowledge §16.1 tests and
  "evaluator order == `/v1/retrieve` order" (the evaluation smoke checks it).
- **Paths:** `plans/work/results-b.md`, tests if a check is missing.
- **Done when:** each row recorded as met, partly met with the reason, or moved with the main session's agreement.
- **Waits on:** –
- **Read:** [`../to-verify.md`](../to-verify.md).

### [ ] B-4 Accessibility statement and one NVDA pass (#96)
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

### [ ] B-6 Go-live pin review, B's part (last code item)
- **Do:** review every NuGet and npm pin younger than 7 days; the realm callback for the Open WebUI pin bump
  (`/oauth/oidc/login/callback`) matching A-4; condense stays at `Eu` until an on-prem route exists (M2).
- **Paths:** `Directory.Packages.props` (shared), `src/admin-ui/package*.json`, `deploy/keycloak/**`.
- **Done when:** no pin under 7 days old without a recorded reason; #113 recorded.
- **Waits on:** every other code item of both streams.
- **Read:** Decisions "7-day rule in development"; identity §5.3.

## Live (the live block, see the roadmap)

### [ ] B-L1 Status green against the real gateway
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

### [ ] B-L4 Gateway release 1 switch
- **Do:** the pilot team's per-level keys with `RequireMaxResidency`; every call sends its ceiling.
- **Done when:** status green against release 1; `/v1/models` at a ceiling lists only models within it (#41).
- **Waits on:** gateway release 1 deployed for the pilot.
- **Read:** gateway-changes §13.2–§13.4.

### [ ] B-L5 Feedback import live
- **Done when:** a pilot rating appears on the Feedback page; Open WebUI feedback older than 90 days is pruned (#49,
  #135).
- **Waits on:** the shared Keycloak with the feedback service account.
- **Read:** chat §13.3, §14.2.
