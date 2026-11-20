# Stream B: work queue

Stream B owns the schema, the admin API, every admin page, retrieval, knowledge and chat
(`.github/stream-ownership.txt`). Rules: [`../../AGENTS.md`](../../AGENTS.md). Context: [`../roadmap.md`](../roadmap.md).

**How to use:** take the first item that is not ticked and not waiting. One PR per item (branch `b/<id>-<slug>`), and
tick it here in that PR (`[x] … (#PR)`). Record `to-verify` rows in [`results-b.md`](results-b.md). If an item is
waiting, take the next one. Live items need the inputs named in *Waits on*; don't start them early.

Finished items move to [`../archive/work/`](../archive/work/) at each release (the main session); find one by its ID.

## Code

### [x] B-47 Source dates on every citation and related document (F4, #857)
- **Do:** knowledge §10.1: `Citation.Modified` loses the `FetchedAt` fallback for `url` documents (it stays the upload
  time for `upload`), and `Citation` gets `Checked` (`Documents.FetchedAt` for `web`, null for `file`; the hydrator
  already reads `FetchedAt`), in the Knowledge API's `document` JSON as `checked`. `Citations.Label` takes the turn's
  language and writes the dates as knowledge §10.4 says (`(2026-03-02, kontrollerad 2026-11-18)`,
  `(kontrollerad …)` without a stated date, `checked` in English), for both the Open WebUI `source` events and the
  `url_citation` titles. `RelatedDocument` gets `modified` and `checked` (chat §9.7; ISO 8601 UTC, omitted when
  null).
- **Paths:** `src/Ume.Rag.Domain/Retrieval/**`, `src/Ume.Rag.Retrieval/**`, `src/Ume.Rag.Answering/**`,
  `src/Ume.Rag.Api/**`, `src/Ume.Rag.AdminApi/**` (only where it builds citations), `tests/**` (not
  `tests/Ume.Rag.ContractTests/**`, `tests/Ume.Rag.Worker.Tests/**`).
- **Done when:** chat §18.9's *Source dates* tests pass: a crawled page with and without a stated date, an English
  turn and an upload, in both the label and the related-documents entry; the Knowledge API's `document` has
  `checked` for a crawled page and none for an upload; the Open WebUI recorded fixture changes only in the label.
- **Waits on:** nothing.
- **Read:** Decisions "Source dates on citations"; knowledge §10.1, §10.4; chat §9.3, §9.7, §18.9.

### [x] B-48 Escalation hand-off: `ume.escalation` (F3, #857)
- **Do:** chat §9.9: the request member `ume.escalation` (§3.8, `true`/`false`, 400 otherwise); for `not-found`,
  `answered-general` and `unavailable` the object `reason`, `contacts` (the version's `NotFound.links`, else
  `Settings.Chat`'s) and `summary`, `null` for `answered` and conversational turns; the summary call (`Escalation.cs`
  beside `FollowUps.cs`: the condense model at the condense ceiling as the assistant's key, `x-ume-step: escalation`,
  `max_tokens` 200, `Chat:EscalationTimeout` 2,000 ms, run at the same time as the follow-up call) with its input,
  language and accept rule; the second `ume` chunk carries `follow_ups` and `escalation` (§3.8 Delivery);
  `Settings.Chat.Prompts.escalation` with its locked lines (`PromptTexts`, V38 ≤ 2,000, the prompt texts endpoint's
  section `escalation`, `labels.ts`'s section and member names); `rag.chat.escalation` and the `escalation` token
  step (§17); the FakeGateway's script per step already covers `escalation`.
- **Paths:** `src/Ume.Rag.Answering/**`, `src/Ume.Rag.Api/**`, `src/Ume.Rag.AdminApi/PromptText*.cs`,
  `src/Ume.Rag.Domain/**` (b's), `src/admin-ui/src/utils/labels.ts`, `tests/**` (not
  `tests/Ume.Rag.ContractTests/**`, `tests/Ume.Rag.Worker.Tests/**`).
- **Done when:** chat §18.9's *Escalation* tests pass, with a negative proof on the accept rule's masking; the
  Settings page's Prompt texts panel shows the escalation section with its locked lines (API test and Vitest).
- **Waits on:** nothing (B-47 first only to keep the two PRs apart on `ChatStreaming.cs`).
- **Read:** Decisions "Escalation hand-off", "Chat prompt texts configurable"; chat §3.8, §9.8, §9.9, §10.1, §17,
  §18.9; admin V15, V38; cost-attribution §4.1.

### [x] B-49 The unavailable text ends with the platform's links when the assistant has none (#860's deviation)
- **Do:** chat §10.2: the "search is temporarily unavailable" text ends with the contact links by §10.1's rule (the
  version's `NotFound.links`, else `Settings.Chat`'s), as the not-found and general-knowledge texts and as
  `ume.escalation`'s `contacts` (§9.9) already do. Today `FixedTexts.Unavailable` gets only the assistant's links,
  so the text and `contacts` differ when only the platform has links.
- **Paths:** `src/Ume.Rag.Answering/**`, `tests/**`.
- **Done when:** a test shows an unavailable turn of an assistant without links ending with the platform's links,
  and its `contacts` equal to the links in the text; one of an assistant with its own links shows those.
- **Waits on:** nothing.
- **Read:** chat §9.9, §10.1, §10.2; decided by the user, 2026-11-19 (decisions log).

### [x] B-50 Delete a knowledge base and a source in the admin UI; their names free at once (#878)
- **Do:** Decisions "Deleting knowledge bases, sources and assistants"; admin §4.5 (the collections and sources rows)
  and *Delete in the UI* (§5.5): *Delete* in the ⋯ menu of a knowledge base and of a source (`Manage`), each with the
  dialog (the counts, the name typed; for a knowledge base in use the `collection_in_use` list and no button). The
  unique index on `config."Collections"."Slug"` becomes partial (`WHERE "DeletedAt" IS NULL`), and `slug_taken`
  looks only at knowledge bases that are not deleted. `collection_in_use` ignores deleted assistants once B-51 is in.
- **Paths:** `src/admin-ui/**` (b's), `src/Ume.Rag.AdminApi/**`, `src/Ume.Rag.Infrastructure/**` (the index and its
  migration), `tests/**`.
- **Done when:** tests show a knowledge base created with the name of a deleted one, the delete of one in use
  refused with its list, and both dialogs (Vitest); a browser check by hand of both deletes, named in the PR.
- **Waits on:** nothing. (That a deleted source's documents leave the index is A-36.)
- **Read:** Decisions "Deleting knowledge bases, sources and assistants"; admin §4.5, §5.5; data-model §2.1, §8.

### [ ] B-51 Delete an assistant (#878)
- **Do:** the same decision; admin §4.5 (the assistants row): `config."Assistants"."DeletedAt"` with the partial
  unique index on `Slug`; `DELETE /api/assistants/{id}` (`Manage`, from any state, the draft discarded, audit
  `assistant.delete`); a deleted assistant is in no list, answers 404 on every route, is served to no client
  (identity §4.3 step 5; `PublishedAssistantVersions` and the worker's column grant, data-model §2.2) and does not
  block a knowledge base's delete; *Delete* in the assistant's ⋯ menu with its dialog (the apps, the questions of
  the last 7 days, the name typed); statistics and feedback pages read *Deleted assistant* where the row is gone
  (`labels.ts`); `config.rag_retention_config_step` purges the row 30 days after `DeletedAt` once no conversation
  names it (data-model §8).
- **Paths:** `src/Ume.Rag.AdminApi/**`, `src/Ume.Rag.Api/**`, `src/Ume.Rag.Answering/**`, `src/Ume.Rag.Domain/**`
  (b's), `src/Ume.Rag.Infrastructure/**`, `src/Ume.Rag.MigrationService/**` (b's), `src/admin-ui/**` (b's),
  `tests/**`.
- **Done when:** tests show: a deleted assistant gone from `GET /v1/models` and refused in chat with 404 for a
  client that had it; a new assistant with its name; its knowledge base deletable; its conversations and daily
  totals still there; the retention step purging it only after the 30 days and its last conversation.
- **Waits on:** nothing (B-50 first only to keep the two migrations apart).
- **Read:** Decisions "Deleting knowledge bases, sources and assistants"; admin §4.5, §5.5; identity §4.3;
  data-model §2.1, §2.2, §8.

### [ ] B-52 Narrow a chat turn by topic: `ume.topics` (F8)
- **Do:** chat §3.8: the request member `ume.topics` (an array of 1–20 tag-format strings; 400 `invalid_request`
  with `param` `ume.topics` otherwise; absent, `null` and `[]` change nothing; ignored for Open WebUI) and the
  answer's `ume.topics` (the asked topics that a link searched); the narrowing of the published version's links
  (a link with topics: the common ones, out of the turn when none; a link without: the asked ones), passed as
  `RetrievalRequest.Topics` so the topic links, the coverage counts and the floor see the narrowed links
  (knowledge §4.2, §6.2); a turn with no link left ends `not-found` without a search.
- **Paths:** `src/Ume.Rag.Api/**`, `src/Ume.Rag.Answering/**`, `src/Ume.Rag.Domain/**` (b's), `tests/**`; the
  OpenAPI document and `docs/` where the `ume` members are listed.
- **Done when:** chat §18.9's *Topics per request* tests pass, with the negative proof that a request cannot
  reach a tag its link does not have; the Open WebUI recorded fixture is unchanged.
- **Waits on:** nothing.
- **Read:** Decisions "Topics per request"; chat §3.8, §18.9; knowledge §4.2, §6.2; processing §7 (the tag
  format); admin V11.

## Live (the live block, see the roadmap)


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
- **Code merged** (#616, #617; review follow-ups #629, #631): only the live check is left.
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
- **Code merged** (#618): only the live check is left.
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
- **Code merged** (#632 chat, #634 admin): only the live check is left.
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
- **Code merged** (#637): only the live check is left.
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

