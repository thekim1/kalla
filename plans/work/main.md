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

### [ ] M-5 Chat rate limit per key (SA-6, #424)
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

### [ ] M-6 Long questions condensed into the search query (SA-4)
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

### [ ] M-7 The `ume` extension: outcome and related documents (SA-1, SA-2)
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

### [ ] M-8 Follow-up suggestions (SA-3)
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

### [ ] M-3 Code complete
- **Do:** when every code item of both queues is ticked: settle every remaining design deviation, fold the results
  files into `to-verify.md`, run the full CI set once.
- **Done when:** nothing open in the code sections of the queues; the roadmap says P1b code done.

### [ ] M-4 The live block
- **Do:** run it in the roadmap's order with the streams and the user; the three-way gold-set run and the hand
  grading use `scripts/grading/` (README there).
- **Done when:** the go-live gate is met, G1 is finished and the pilot is live; the roadmap moves P1b to Done.
