# Main session: work queue

The main session owns the design documents, the roadmap and these queues; it reviews and merges stream PRs and
settles their design deviations. Start each session from [`../../HANDOVER.md`](../../HANDOVER.md).

## Standing

- Review every stream PR (live in the browser where it touches a page), settle its "Design deviations" in the design,
  merge when ready. Keep [`../roadmap.md`](../roadmap.md) and the stream queues current.
- New work is written as a queue item (goal, paths, done-when, waits on, read) in the owning stream's file.

## Queue

### [x] M-1 Run the browser tests locally (covered by the runner's green run of #377: https://github.com/thekim1/ume-rag-platform/actions/runs/42090732503)
- **Do:** `scripts/Start-E2E.ps1` (stops the dev stack; own test database), then `npx playwright test` in
  `tests/e2e`.
- **Done when:** the pilot journey (admin §5.4 steps 1–13, mouse and keyboard) and the accessibility spec pass
  locally, or their failures are fixed or filed. This is the local evidence for the old items 11, 12 and 14; B-5 and
  A-2 still need the runner.

### [x] M-2 Recommendations for the open design calls
- **Do:** one short proposal each, with a default: topics for uploads; `UrlCanonicalizer` into Pipelines (#178);
  #217's fired-types list; identity §16 Q2–4, Q8; a direct connection for the HNSW rebuild behind a pooler; index
  health for multi-collection scopes; recall for a narrow topic in a large collection.
- **Done when:** the user has answered; the answers are in the design or the Decisions table.

### Support-assistant items before the pilot (user, 2026-11-06)

Stream B, which owns these paths, is inactive, so M-5 to M-8 are built on `main-session/<id>-<slug>` branches (the
ownership check restricts only `a/*` and `b/*` branches), by the main session or a stream A agent, one PR each,
reviewed and merged by the main session. Case plan: [`../cases/support-assistant/06-gaps.md`](../cases/support-assistant/06-gaps.md).

### [x] M-5 Chat rate limit per key (SA-6, #424)
- **Do:** when the key itself is the subject (`SubjectKind.Application`), `ChatRateLimiter` uses the key's
  `Keys.RequestsPerMinute`, else `Settings.Chat.messagesPerMinutePerUser`; Open WebUI users keep the per-user value.
  Carry the value on the principal's `KeyEntry` (set in `KnowledgeCallers`). Add the admin key form's hint
  (`PLATFORM.fieldHints.requestsPerMinute`). Answer #424's `RequestsPerMinute` question in the PR (enforced, kept);
  the `AllowedIpRanges` half of #424 stays open (SA-7).
- **Paths:** `src/Ume.Rag.Api/ChatStreaming.cs` (`ChatRateLimiter`), `src/Ume.Rag.Api/ChatEndpoints.cs`,
  `src/Ume.Rag.Api/KnowledgeCallers.cs`, `src/Ume.Rag.Domain/Access/AccessModel.cs`,
  `src/admin-ui/src/utils/labels.ts`, `tests/Ume.Rag.Api.Tests/**`.
- **Done when:** the "Rate limit per key" tests of chat §18.9 pass (`dotnet test tests/Ume.Rag.Api.Tests`); the key
  form shows the hint (admin UI unit test or screenshot).
- **Waits on:** –
- **Read:** Decisions "Chat rate limit per key"; chat §4.1 step 4; identity §3, §6.3; data-model `Keys`; admin §5.8.

### [x] M-6 Long questions condensed into the search query (SA-4)
- **Do:** accept a question up to 16,000 characters (`ChatRequestParser.MaxQuestionChars`); keep its prompt form
  (line breaks) for the answer and its query form for kind, language, condense and retrieval; over 2,000 characters
  run the long-question condense (`x-ume-step: condense-long`, `Chat:LongQuestionCondenseTimeout` 3 s) on every turn,
  with the word-boundary fallback, counting and logging; a fallback counts as a failed condense for coverage. Give
  FakeGateway a default `condense-long` answer.
- **Paths:** `src/Ume.Rag.Answering/Chat/ChatRequest.cs`, `Condenser.cs`, `ChatService.cs`, `ChatOptions.cs`,
  `PromptBuilder.cs`; `src/Ume.Rag.AdminApi/AssistantTestEndpoints.cs` (only if `ParsedChat` changes shape);
  `src/Ume.Rag.FakeGateway/FakeChatScripts.cs`; `tests/Ume.Rag.Api.Tests/**`.
- **Done when:** the "Long questions" tests of chat §18.9 and the 16,001-character row of §18.6 pass; a golden prompt
  file shows a multi-line ticket reaching the answer prompt unchanged; the long-condense latency on the EU model is
  measured once against the dev gateway and recorded in the PR (chat §22).
- **Waits on:** –
- **Read:** Decisions "Long questions condensed into the search query", "Condense on the EU route"; chat §3.2, §5,
  §5.1, §6.2, §6.3, §7.1, §16.1, §17.

### [x] M-7 The `ume` extension: outcome and related documents (SA-1, SA-2)
- **Do:** parse and validate `ume` (§3.8; ignored for `open-webui`); build `related_documents` from the turn's
  retrieval (§9.7); write the `ume` member in the JSON answer and the `ume` chunk in the stream (after the final
  chunk, before usage and `[DONE]`), with `outcome` and `degraded`; metrics `rag.chat.ume` and
  `rag.chat.related_documents`.
- **Paths:** `src/Ume.Rag.Answering/Chat/ChatRequest.cs`, `ChatPlan.cs`, a new `RelatedDocuments.cs` in
  `src/Ume.Rag.Answering/Chat/`; `src/Ume.Rag.Api/ChatEndpoints.cs`, `src/Ume.Rag.Api/ChatStreaming.cs`;
  `tests/Ume.Rag.Api.Tests/**`.
- **Done when:** the "`ume` contract", "Related documents" and "Open WebUI" tests of chat §18.9 and the `ume` rows
  of §18.6 pass; a request without `ume` gives a byte-identical answer to today's (golden JSON and SSE files).
- **Waits on:** –
- **Read:** Decisions "Related documents and the `ume` chat extension"; chat §3.2, §3.3, §3.6, §3.8, §6.2, §9.2,
  §9.3, §9.7, §17; knowledge §4.6, §10.

### [x] M-8 Follow-up suggestions (SA-3)
- **Do:** after a model answer with `ume.follow_ups`, one call to the condense model at the condense ceiling
  (`x-ume-step: follow-ups`, `Chat:FollowUpTimeout` 2 s, 0 = off), the prompt and accept rule of chat §9.8, delivered
  in the second `ume` chunk or the JSON `ume`; never stored or logged; metrics `rag.chat.follow_ups` and
  `rag.chat.tokens{step=follow-ups}`. Give FakeGateway a default `follow-ups` answer.
- **Paths:** a new `FollowUps.cs` in `src/Ume.Rag.Answering/Chat/`, `ChatService.cs`, `ChatOptions.cs`;
  `src/Ume.Rag.Api/ChatEndpoints.cs`, `src/Ume.Rag.Api/ChatStreaming.cs`;
  `src/Ume.Rag.FakeGateway/FakeChatScripts.cs`; `tests/Ume.Rag.Api.Tests/**`.
- **Done when:** the "Follow-ups" tests of chat §18.9 and the follow-up row of §18.6 pass, including the telemetry
  scan; the follow-up latency on the EU model is measured once against the dev gateway and recorded in the PR.
- **Waits on:** M-7 (the `ume` parsing and chunk writer).
- **Read:** Decisions "Follow-up suggestions"; chat §3.8, §5, §7.2, §7.4, §8.2, §9.5, §9.8, §10.3, §16.1, §17.

### [x] M-3 Code complete (2026-11-08, `e80c42c`: verify 42469432178, parser 42469434705, e2e 42471519711 run alone, #494)
- **Do:** when every code item of both queues is ticked: settle every remaining design deviation, fold the results
  files into `to-verify.md`, run the full CI set once.
- **Done when:** nothing open in the code sections of the queues; the roadmap says P1b code done.

### [ ] M-4 The live block
- **Do:** run it in the roadmap's order with the streams and the user; the three-way gold-set run and the hand
  grading use `scripts/grading/` (README there).
- **Done when:** the go-live gate is met, G1 is finished and the pilot is live; the roadmap moves P1b to Done.

### [x] M-9 Cost attribution stage 1: settle the design and queue the streams
- **Do:** write stage 1 of [`../platform/cost-attribution.md`](../platform/cost-attribution.md) §5 into the designs it names
  (chat §8.3, §17 and the `AssistantDailyTotals` columns or a per-step table, admin §4.9 and a cost subsection under
  §8, processing §11/§14, engine §10): the cost of each call is the gateway's (`x-ume-cost-usd`, the stream comment
  from gateway phase 2, the Integration API's usage by run id from phase 3), estimated from the mirror's prices only
  where none is given, and split per step on `x-ume-run-id`/`x-ume-step`. Then queue C1–C6 in `stream-a.md` and
  `stream-b.md` with paths and done-whens. Bring Q3 of the plan's §7 to the user (Q1, Q2 and Q4 are answered by #580).
- **Done when:** the design sections state stage 1's rules, the queue items exist, and the user has answered Q3 (or
  confirmed the default).
- **Waits on:** nothing for the `x-ume-cost-usd` part; the stream cost and the read-back follow M-12's phases.
- **Written (2026-11-12):** stage 1 is in the designs (cost-attribution §5 lists the sections): a per-step table per
  owner schema, `chat."AssistantDailyUsage"` and `content."CollectionDailyUsage"` (data-model §2.3 says why, not
  columns on the totals row), gateway and estimated cost apart; the stream cost needs no wait (gateway phase 2 is
  merged), and the Integration API read-back is stage 2's B7. Queued: B-38–B-41 (stream b), A-31, A-32 (stream a).
  Q6 marked answered. Q3 answered by the user (2026-11-12): no hard budget in the pilot.

### [x] M-10 Settle #499 step 2 and #501 in the designs
- **Do:** #499 step 2: who maintains scope sizes and the keyword stop list (the worker at the chunk-set swap, a
  statistics table rag-api reads) in knowledge §6.4, §20 and data-model; #501: chunk sets written visible inside the
  swap transaction in engine §12 and data-model §3.2. Then queue the code items (A-25 waits on this).
- **Done when:** the design sections say it and the items are queued.
- **Waits on:** B-25's measurement (for #499).
- **Settled (2026-11-11):** scope sizes stay in rag-api (cheap); the stop list's statistics move to the worker
  (`KeywordStatistics`, knowledge §6.2); sets ≤ 1,000 chunks are written in the swap transaction (engine §14 rule 2a).
  Queued: A-29, B-33, A-25.

### [x] M-11 Design documents that contradict the decisions (found in the rewrite, 2026-11-08)
- **Do:** align each design with [`../decisions.md`](../decisions.md), or bring the call to the user where the decision
  itself looks wrong:
  - engine §13.1 says `files` has no `pathRules` (it has them, "Organisation"); connectors §5.1 says robots.txt is
    always honoured ("Configurable politeness per host").
  - repo-and-deployment §6.2 still counts six containers; §8.1 states the deployer's `Group.Create` as settled (H7 is
    open); §8 names one runner (there are two); §8.2 says the backend tests run on the pinned pgvector image (they
    build `ume-rag/postgres`); the daily `audit.yml` schedule is in no decision.
  - identity: the provider-per-phase table gives admins OTP in P1 ("P1 pilot sign-in": no OTP); the brokered AD and
    Entra ID requirement of "Identity providers" is missing; identity §4.7 retries a gateway 503, chat §8.3 does not.
  - gateway-changes §1 and §13.1 call rerank a separate P2 release ("Rerank pulled forward").
  - `MaxInputTokens`: data-model §2.1 and `compose.prod.yaml` say 8192, "Pilot embedding model, U-b" 8191 (the model's
    limit is 8191: check and align).
  - admin: the judge model is "on-prem, V32" ("Evaluation models at the collection's ceiling": the collection's
    ceiling); Swedish labels and wireframes remain ("Admin UI in English"); Edit is a header button, "Level change in
    the edit drawer" puts it in the ⋯ menu.
  - roadmap and postponed B1 give the gold set as 100–150 answerable (decision: 120–150); processing §14.8 has an
    Overview alert, which "M-3 settlements" makes P2; the roadmap's P2 rerank bullet reads as if rerank were still P2.
  - Seven design headers (admin, chat, gateway-changes, identity, knowledge, parser, tools) call their archived area
    plan "binding, incl. its answered questions"; `AGENTS.md` says the archive does not bind. Fold what still holds
    into the design and drop the word.
  - "Ask-a-file workspaces" (P2) are owned by teams and individuals, "Organisation" by departments only (user call
    before P2).
  - repo-and-deployment §8.4 and §12.10 call SHA pins for actions "proposed", and postponed decision S1 is open
    ("CI/CD": postponed until before go-live, A-20, A-27); connectors §8.3 names `Render:MaxPerHost`, which the code
    does not have (it is the host's `renderPerHost`, capped by `Render:MaxConcurrent`).
- **Done when:** each point is fixed in its design or answered by the user, and `check-doc-refs.py` passes.
- **Waits on:** the decisions register (this PR).
- **Left for the user:** who owns "Ask-a-file workspaces" (teams and individuals) against "Organisation" (departments
  only); answer before P2.

### [ ] M-12 Gateway integration (#580): queue the stream items when the gateway's phase PRs exist
- **Do:** when a gateway integration phase is merged in `~/git/llmgateway` (phase 1 is draft PR thekim1/llmgateway#81),
  pin its OpenAPI document in `contracts/gateway/`, check the designs against what was built (gateway-changes §5 is
  Ume.Rag's view, the gateway's `docs/integrations.md` wins), and queue that phase's items in `stream-a.md` (worker)
  and `stream-b.md` (api, admin, FakeGateway) with done-whens from cost-attribution §6:
  - **Phase 1** (B2–B4), **queued 2026-11-12** as B-35, B-36, B-37 and A-30 (designs checked against `6a8f0a5`: they match);
    **amended 2026-11-12** after the gateway owner's decisions on #580 (#84): B-35 takes the organisation from the
    context with `organisation.read` and drops the Data API key, and its contract test checks against the gateway's
    `docs/openapi/integrations.json`; B-36 handles `usable: false`; A-30 removes the Data API key: one credential key per service (`Gateway:Key`) in place of the per-level keys, and the
    residency handler's service maximum (`src/Ume.Rag.Infrastructure/Gateway/GatewayOptions.cs`,
    `ResidencyCeilingHandler.cs`, `GatewayApi.cs`; `deploy/init-deployment.sh`; `docs/production-setup.md`); the
    context and model sync with `ETag` and the `GatewayKeys` mirror, the manual model limit fields replaced by the
    gateway's (`src/Ume.Rag.AdminApi/OrganisationSync.cs`, `GatewayModelSync.cs`, `GatewayEndpoints.cs`;
    `src/Ume.Rag.Domain/Entities/`, a migration in `src/Ume.Rag.Infrastructure/Persistence/Migrations/`,
    `deploy/postgres/app-roles.sql`); `GatewayKeyId` on assistants and collections with the key picker and V39
    (`AssistantEndpoints.cs`, `CollectionEndpoints.cs`, `src/admin-ui/src/views/AssistantView.vue`,
    `CollectionView.vue`, `labels.ts`, `messages.ts`); the FakeGateway's `/v1/ume/context`, `ume` model fields and
    contract test (`src/Ume.Rag.FakeGateway/**`, `tests/Ume.Rag.ContractTests/**`).
  - **Phase 2** (B5–B6): `x-ume-on-behalf-of` from the object's key with the echo check on every call
    (`src/Ume.Rag.Infrastructure/Gateway/**`, `src/Ume.Rag.Answering/Chat/GatewayChatClient.cs`,
    `GatewayStreamLine.cs`, `src/Ume.Rag.Answering/GatewayQueryEmbedder.cs`, `src/Ume.Rag.Retrieval/GatewayReranker.cs`,
    `src/Ume.Rag.Api/ChatStreaming.cs`, `src/Ume.Rag.AdminApi/AssistantTestEndpoints.cs`,
    `src/Ume.Rag.Worker/Processing/Embedding/EmbeddingClient.cs`, `src/Ume.Rag.Worker/Evaluation/Generation/EvalModelClient.cs`);
    run headers stored; the stream cost read and never forwarded; `assistant_unavailable`, the worker's
    `gateway.key_unavailable` and the attention item; save checks as the key; publish needs a key; the FakeGateway's
    acting, echo, drop-echo and budget modes and the stream cost comment.
  - **Phase 3** (B7): the Integration API read-back (`usage.read` on `rag-admin`'s key, `Gateway:IntegrationApiUrl`):
    cost and budget status per assistant and knowledge base (`src/Ume.Rag.AdminApi/**`, the two views); the
    FakeGateway's `/integration/v1`.
- **Done when:** each merged phase has its stream items queued and the designs match what the gateway built.
- **Waits on:** the gateway's phase PRs (user). Phase 2 waits on a gateway release with #84 (feature
  `credential-residencies`); phase 3 on #83 merged. Department member groups wait for the gateway (feature C, not
  scheduled; user, 2026-11-12: wait, no other source in Ume.Rag); until then they are entered by hand (identity §2).
- **Read:** Decisions "Cost attribution", "Gateway keys", "Gateway releases"; gateway-changes §5, §13.4;
  cost-attribution §4, §6; identity §4.7; admin §4.6 V39, §4.10, §5.5.4; data-model `GatewayKeys`; chat §8;
  repo-and-deployment §4.3.
