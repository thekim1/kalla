# chat-api: history moved out of the design

The process sections of [`../../chat-api/design.md`](../../chat-api/design.md) (summary of decisions, milestones, changes to other documents, questions and their answers, deferred and removed items), moved here unchanged on 2026-11-13 (M-14) under their original numbers, so the citations of them still resolve. History: it does not bind; `plans/decisions.md` and the design do.

## 1. Summary of decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Placement | Chat role of `Ume.Rag.Api` (HTTP, SSE, writer, importer) over the answer core in `Ume.Rag.Answering` (shared with the admin test panel, admin design §2.1); `IRetriever` in process; gateway calls through the shared `IGatewayClient` with a raw `HttpClient` + copied `SseReader` (headers must be read before the body); P2 agent loop behind `IAnswerStrategy` (§2) |
| 2 | Contract | OpenAI-compatible `/v1/models`, `/v1/chat/completions` (stream and not); unknown fields ignored, a fixed list refused; text content parts only (§3, answered 6) |
| 3 | History | **The messages the client sends are authoritative for the prompt**; our store is a record for the user, review, feedback and retention. 0 SQL on the hot path (§11.3). **P1 pilot: history mode `none`** until A1 is answered: metadata and ratings only (§11.2) |
| 4 | Open WebUI tasks | One connection only (ours); Open WebUI title, tag and follow-up generation **switched off**; no task model of ours (§4.2, §4.3) |
| 5 | Condense | On by default, **follow-ups only**, skipped when the question is self-contained (length, no anaphora); one call ≤ 96 output tokens to `Settings.Chat.CondenseModel` (a small instruct model, reasoning off: `Eu` until register M2, then on-prem), hard **1,200 ms** timeout on the EU route, falls back to the raw question (§5, answered 1) |
| 6 | History window | Last **10 messages, ≤ 4,000 tokens**, oldest dropped first, old markers stripped, sources never resent; per assistant (§6.3, answered 2) |
| 7 | Prompt budget | Character estimate (÷ 3, Swedish-safe) against the assistant's `ContextTokens`; fixed priority: frame + question + answer reserve, then sources, then history (§6.3) |
| 8 | Sources | In the final user message as `<source n="…">` blocks marked untrusted; numbered per document by best rank (knowledge §10.4); `</source` neutralised (§7) |
| 9 | Residency | Explicit ceiling on **every** call (`External`/`Eu`/`OnPrem`), enforced by a `DelegatingHandler` that also sets the assistant's gateway key (`x-ume-on-behalf-of`) and refuses a ceiling above the service's maximum; `x-ume-residency` and the key's echo checked **before our first byte**; history-keeping clients run every turn at max(assistant level, client ceiling) (§8) |
| 10 | Citations | Streamed `[n]` markers parsed across chunks and **renumbered in order of first citation**; Open WebUI gets one `source` event per cited document just before the marker, other clients `url_citation` annotations; uncited sources never shown (§9) |
| 11 | Output filter | Markdown/HTML images removed; links to hosts outside the allow-list de-linked; bounded look-ahead transducer (§9.4) |
| 12 | Not found | Coverage `Low` (vector and keyword both weak) → with `generalKnowledge` `off` (default) or `supplement` the assistant's fixed Swedish or English text + contact links, no model call; with `fallback` or `open` a model answer without sources behind a fixed caveat line (`answered-general`, §10.4); conversational messages skip retrieval and the gate; a failed condense never yields the not-found text; `Unknown` → model answer from keyword results with a caveat, or a "search unavailable" text when nothing was found (§6.2, §10, answered 3) |
| 13 | Conversation ids | Open WebUI: (client, `sub`, chat id, assistant); others: issued **signed** ids `c1.…` verified without SQL; temporary Open WebUI chats stored as `none` (§11) |
| 14 | Writer | Bounded channel (bytes and items), batches of ≤ 200 turns / 250 ms, encryption in the writer (built when A1 allows stored content; P1 writes metadata only), one transaction per batch, no journal (no content outside the encrypted tables); loss bound stated (§12) |
| 15 | Feedback | `POST /v1/feedback` + a 15-minute import of Open WebUI ratings via its admin API, mapped by chat id and the forwarded message id; comments encrypted under the message's bucket key (P1 pilot in mode `none`: ratings only, comments stay in Open WebUI); department roles see ratings in P1, comments only `rag-reviewer` and `rag-department-admin` (§14, answered 4, 7; admin design §3) |
| 16 | Attachments C1 | P1: refused with `attachments_not_supported`, uploads off in Open WebUI. P2: chat-scoped storage in the `chat` schema under the chat bucket keys, parsed by the worker, written back through the API, searched only within that conversation (§15, answered 5–6) |
| 17 | Latency | Chat overhead < 10 ms p95 excluding condense and retrieval; 0 Postgres statements and 0 Redis commands of our own per message (P1 has no Redis); measured by benchmarks, a `benchmarks/compare`-style run and the R1 test (§16) |
| 18 | `ume` extension | Clients other than Open WebUI opt in per request with a body member `ume`; the answer carries `outcome`, `degraded` and, when asked, related documents and follow-up suggestions, after the answer text (§3.8) |
| 19 | Related documents | Up to 10 retrieved documents, one per document by best rank, with link, collection, rank and `cited`, from the turn's own retrieval; empty on coverage `Low` (§9.7) |
| 20 | Follow-up suggestions | Up to 5, one call to the condense model after the answer, `Chat:FollowUpTimeout` 2 s, never stored (§9.8) |
| 21 | Long questions | Question up to 16,000 characters; over 2,000 the condense model writes the search query on every turn (`Chat:LongQuestionCondenseTimeout` 3 s), else the first 2,000 characters; the answer model gets the full text (§5.1) |
| 22 | Rate limit per key | A key that is itself the subject is limited by `Keys.RequestsPerMinute`, else the per-user value (§4.1 step 4) |

## 19. Milestones

| Phase | Deliverable |
|---|---|
| P1 | `/v1/models`; `/v1/chat/completions` stream and JSON; conversational check; condense on follow-ups with skip heuristic and 600 ms timeout; retrieval with coverage handling; token budget; prompt frame; per-level keys, `ResidencyCeilingHandler`, residency and echo checks; marker renumbering, Open WebUI `source` events and `url_citation`; output filter; not-found and degraded texts; signed conversation ids; writer with daily totals (history mode `none`: metadata and ratings only); conversation endpoints; user delete; `/v1/feedback` (ratings; comments refused in mode `none`); Open WebUI feedback import (ratings) and feedback pruning over its API; Open WebUI configuration incl. the `WEBUI_BANNERS` input warning; P1 refusal of attachments; metrics, traces, events 5003/5011; benchmarks, audit, overhead run, R1 |
| When A1 allows stored content | history modes `stored`/`session`; bucket keys and encryption in the writer (§12.3); crypto-shredding behind its own A1 setting; feedback comments; conversation review in the admin |
| P2 | answer-level evaluation and assistant tests (§18.8); agent loop for assistants with tool-bearing skills (`AgentAnswerStrategy`, tool hub, skills; detail in `../tools/design.md` §6, milestones §12); chat attachments (§15.2); public widget (`pk` keys, sessions, server-side history); gateway-served rerank per assistant (knowledge §6.7); cost reconciliation from the gateway usage feed; condense tuning with the advisory evaluation; feedback review workflow beyond P1 |
| P3 | Teams client; OBO tools; write tools with confirmation |

## 20. Changes to other documents

| Document | Change |
|---|---|
| `platform/data-model.md` §2.1 `Assistants` | `NotFoundText` → `NotFound jsonb` (`sv`, `en`, `links[]{title,url}`, §10.1); `QuerySteps jsonb` schema: `{ condense: { enabled: true, model?: null }, retrieval: { topK: 10, filters, rerank: false (P2) }, history: { maxMessages: 10, maxTokens: 4000 }, answer: { contextTokens: 16384, maxOutputTokens: 1024, temperature?: null, language: "auto" }, attachments: { enabled: false } (P2) }` |
| `platform/data-model.md` §2.1 `Settings` | `Chat jsonb`: `CondenseModel` (on-prem alias, the only task model), `MaxContextTokens`, `MaxAnswerTokens`, default not-found texts sv/en, `AllowedLinkHosts`, `MessagesPerMinutePerUser` 20 |
| `platform/data-model.md` §2.1 `Clients` | remove `TaskModel`, `TaskModelResidency` and `CK_Clients_TaskModel` (§4.2) |
| `platform/data-model.md` §2.3 `Conversations` | unique index (`ClientId`, `SubjectId`, `ExternalChatId`, `AssistantId`) (§11.1); no tombstone (`DeletedByUserAt` removed; user delete deletes the row, §13.2); plain `Classification`, no composite keys; title from the first question |
| `platform/data-model.md` §2.3, §7 `DataKeys` | bucket keys per (level, expiry month), unique per bucket (§12.3); content rows reference their bucket; no `Epoch`, no per-conversation keys, no conversation FK cascade |
| `platform/data-model.md` §2.3 `Messages` | add `ExternalMessageId varchar(100) NULL` with partial U (`ConversationId`, `ExternalMessageId`) where not null, `ParentMessageId uuid NULL`, `ExpiresAt` (message time + retention), `Coverage varchar(10)` (incl. `skipped`), `Degraded varchar(40)`, `Language char(2)`, `CondenseUsed bool`; `Outcome` values `answered`/`not-found`/`unavailable`/`refused`/`error`/`cancelled`; token, cost and TTFT columns nullable |
| `platform/data-model.md` §2.3 `MessageSources` | add `Number smallint NULL` (display number, §9.2), `Page`, `PageEnd`; `Used` = cited |
| `platform/data-model.md` §2.3 `Feedback` | add `ClientId`, `Source varchar(20)` (`api`/`owui-import`), `ExternalId varchar(100)` with U (`Source`, `ClientId`, `ExternalId`), `Score smallint NULL` (1–10), `UpdatedAt`; `CommentCiphertext` holds `{reason, comment}`; U (`MessageId`, `Source`) for `api` |
| `platform/data-model.md` §2.3 new | `FeedbackImportState` (§14.2); P2 `Attachments`, `AttachmentChunks` (§15.2) |
| `platform/data-model.md` §2.3 `AssistantDailyTotals` | add `Unavailable`, `Degraded`, `Errors`, `Cancelled`, `UncitedAnswers` (§18.8) |
| `platform/data-model.md` §8 | chat retention deletes rows (messages by their own expiry, then empty expired conversations); bucket-key destruction behind a setting switched on only after A1 (§13) |
| `archive/area-plans/platform.md` §6 (residency matrix) | `public` sends `External`; history-keeping clients run every turn at max(assistant level, client ceiling) (§8.2) |
| `identity/design.md` §3 client registry, §5.1, §5.3 | no `TaskModel`/`TaskModelResidency`; Open WebUI has one connection (ours) and no task model; custom connection headers `X-Rag-Owui-Message-Id`, `X-Rag-Owui-User-Message-Id` are unsigned and only used inside the caller's own conversation |
| `identity/design.md` §3, §10 | P2 caller "worker → API internal attachment route, internal service key"; event 5011 `rag.gateway.delegation_missing`; 5002 reason `conversation_not_owned`; secret `Security__ConversationIdKeys__<kid>` |
| `gateway-changes/design.md` / `../archive/area-plans/chat-api.md` | optional later item: `contextWindow` on `/v1/catalog/models` so the admin can prefill `contextTokens`; P2 rerank endpoint (knowledge §18) |
| `ingestion-worker/design-engine.md` job classes | P2 class `attachment` (high priority, concurrency 2; scan → parse → chunk → PII → embed → post to API) |
| `ingestion-worker/design-processing.md` §14.8 | answer-level evaluation (§18.8) runs in `rag-admin` (run kind `answers`), judge `Settings.EvaluationJudgeModel`; the worker references neither `Ume.Rag.Answering` nor `Ume.Rag.Tools` |
| `platform/repo-and-deployment.md` §2.2, §3 | `Microsoft.Extensions.AI.OpenAI` not needed by the chat role; settings `Chat:*` (`PublishApiReference`, `CondenseTimeout` 600 ms, `GatewayHeadersTimeout` 15 s, `StreamIdleTimeout`, `MaxAnswerDuration`, `WriterEnqueueTimeout`, `CharsPerToken`), `Gateway:RequireDelegationEcho`, `OpenWebUi:BaseUrl`, `OpenWebUi:AdminApiKey`; `Gateway:AttributionMode` and `Gateway:SendResidencyCeiling` removed (always on) |
| `platform/repo-and-deployment.md` §6.1, §6.3, §7.1 | egress `rag-api` → Open WebUI host (feedback import and pruning); secrets: the credential key of `rag-api` (and of `rag-admin` for the test panel and §18.8; until the gateway's integration phase 1 the `OnPrem`, `Eu` and open keys of each), `OpenWebUi__AdminApiKey`, `Security__ConversationIdKeys__<kid>` (made by `init-deployment.sh`, except the Open WebUI key which an operator creates) |
| `platform/repo-and-deployment.md` §1, §4.1, §12.8 | `tests/Ume.Rag.Benchmarks/ChatTurnBenchmarks.cs`; overhead run under `benchmarks/release-test`; `compose.openwebui.yaml` env of §4.3; no pruning sidecar |
| `platform/repo-and-deployment.md` §4.3 FakeGateway | streams with configurable usage chunk, broken stream, stall, missing `[DONE]`; echoes `x-ume-session`; refuses requests without `x-ume-max-residency`; modes `503` + `Retry-After` and `403 max_residency_not_met` |
| `platform/repo-and-deployment.md` §9 | metrics of §17 |
| `archive/area-plans/admin.md` §4–§5 | publish/audience validation: an assistant offered to a history-keeping client needs a gateway model served at max(level, client ceiling) (§8.2); Assistants form: condense toggle, history window, context and output tokens, temperature, language mode, not-found texts sv/en + contact links; answer-level evaluation on the assistant page (§18.8); test panel shows raw vs filtered answer; **Feedback page in P1** (§14.4); Clients page: warning when Open WebUI headers are missing (no task model) |
| `archive/area-plans/tools.md` §3 | agent loop behind `IAnswerStrategy` with `AnswerEvent`s (§2.2) |
| `postponed-decisions.md` A1, C1 | A1 also gates bucket-key destruction; C1 struck (decided 2026-10-30, §15.2) |

## 21. Questions

### 21.1 Answered (user, 2026-10-30)

1. **Condense**: on by default, **follow-ups only**, one short call on an on-prem model (`OnPrem` for every level);
   assistants can turn it off (§5). Simplification pass: skipped for self-contained questions, 600 ms timeout.
2. **History window**: last **10 messages, ≤ 4,000 tokens**, trimmed oldest-first; earlier turns' sources are not
   resent; per assistant (§6.3).
3. **Not found** (coverage `Low`): the assistant's fixed not-found text + contact links, no model call; Swedish and
   English variants by detected language. `Unknown` answers from keyword results with a caveat, never with the
   not-found text (§10).
4. **Feedback in P1**: `/v1/feedback` plus a scheduled import of Open WebUI ratings through its API, mapped by chat id
   and message id; comments encrypted under the chat bucket keys, same retention; editors see ratings in P1 (§14).
   In the P1 pilot (history mode `none`) only ratings are stored; comments stay in Open WebUI until A1 allows stored
   content (§11.2).
5. **C1 chat attachments (P2)**: chat-scoped storage with conversation retention, parsed and chunked by the worker,
   encrypted under the chat bucket keys, never in the shared document store or a collection, searched only within
   the conversation (§15.2). It needs the bucket keys, so it also waits for A1.
6. **P1 file uploads in Open WebUI**: disabled in Open WebUI's user permissions; our API refuses file and image
   content parts with an OpenAI-shaped error (§15.1).

7. **Feedback comments for editors** (§14.4): on the P1 Feedback page a comment is shown after a one-click,
   pre-filled reason ("Granskning av återkoppling"), audited as a conversation access, for `public` and `internal`;
   `sensitive` comments only through the full conversation-review flow with a second approver. Only the comment is
   shown, never the conversation. *Who* was settled in step 10 (admin design §14.1 answer 3): `rag-reviewer` and
   `rag-department-admin`, not platform admins implicitly (§14.4). Built when A1
   allows stored content, not in P1.

### 21.2 Open (user)

1. **Open WebUI's own chat copies after 90 days** (§13.3): v0.12.5 has no retention setting, its API reaches other
   users' chats only with `ENABLE_ADMIN_CHAT_ACCESS=true` (pin v0.12.3; same behaviour confirmed for `ENABLE_ADMIN_CHAT_ACCESS`/`ENABLE_ADMIN_EXPORT`, to-verify #173), and direct SQL is ruled out. Options: (a) turn admin chat
   access on for the service account's API use (the flag is global, so Open WebUI admins could then read chats in its
   UI, acceptable only if no person holds that role), (b) contribute or wait for native retention upstream and accept
   unpruned Open WebUI copies in the pilot, stated in the privacy notice. Recommendation: (b) for the pilot together
   with A1, unless the registrar requires the 90 days in Open WebUI too, then (a). Registered as postponed item **W2**
   "Open WebUI chat-copy retention", decided together with A1 before the pilot; stopgap: unpruned copies in the pilot,
   stated in Open WebUI's data-handling profile. Feedback pruning through the API stays (§13.3). W2 is decided within the DPIA (G1) before go-live, as part of assessing Open WebUI's store as the
   primary copy.

## 23. Deferred or removed (simplification pass)

Removed or deferred, with where each went:

| Item | Where it went |
|---|---|
| Virtual task model `owui-tasks`, `OwuiTaskHandler`, `Clients.TaskModel`, `X-Rag-Owui-Task`, `TASK_MODEL_EXTERNAL`, `### Task:` heuristic, `rag.chat.tasks` | removed; Open WebUI title, tag and follow-up generation switched off (§4.2, §4.3); `Settings.Chat.CondenseModel` is the only task model |
| Nightly SQL pruning sidecar against Open WebUI's database | removed; feedback pruned through its HTTP API; chat pruning open (§13.3, §21.2) |
| Per-conversation data keys, 7-day epochs, epoch race index, tombstones (`DeletedByUserAt`), in-process conversation key cache | bucket keys per (level, expiry month) (§12.3); user delete deletes rows (§13.2) |
| Crypto-shredding on retention and user delete | switched off until A1; retention deletes rows (§13) |
| Tier resolver, per-tier writer batches, tier-local tables, composite `(Id, Classification)` conflict targets | one database, plain `Classification` (§12.2) |
| Keys per gateway release (linked keys, B → C → A), no ceiling for `public` | one credential key per service with the assistant's gateway key, `ResidencyCeilingHandler`, `External` for `public` (§8.1) |
| Effective level raised only when the request carries earlier assistant turns | max(assistant level, client ceiling) on every turn for history-keeping clients (§8.2) |
| `Low` coverage from vector distance alone; not-found after a failed condense | vector and keyword both weak; conversational skip; condense failure never yields the not-found text (§6.2) |
| Condense on every follow-up with a 1.2 s budget | skip heuristic and 600 ms (§5) |
| Rerank in P3 | seam P1, gateway rerank per assistant P2 (§19; knowledge §6.7) |
| `MarkCited` with every chunk of every cited document | only the cited ranks (§9.5) |
| Gateway headers timeout 60 s | 15 s, proposed (§8.3) |
| Strict fail-closed on an unconfirmed config version | grace window (§4.1) |
| `Assistants.Mode` as an editable field; knowledge from linked skills' collections; chat serving the current assistant row | mode derived from tool-bearing skills; direct collections only (knowledge §4.2); chat serves the published version (§2.2) |
| Redis rate limit per user in P1 ("not applied" when Redis is down) | P2, first multi-host deployment; P1 limits in process behind `IRateLimitStore`, P2 falls back to per-instance limits (§4.1 step 4) |

## 24. Deferred or removed (weakness review)

Reasoning: [weakness review §1](../passes/weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer):

| Item | Where it went |
|---|---|
| Nightly answer-level evaluation (run kind `answers`, §18.8) | P2 |
| Assistant tests (run kind `assistant-tests`, §18.8) | P2, with the assistant version diff, templates and copy (admin design §17) |

Reasoning: [weakness review §4](../passes/weakness-review.md#4-data-protection-guards-the-secondary-copy-while-the-primary-stays-unprotected):

| Item | Where it went |
|---|---|
| History modes `stored`/`session`, bucket keys (§12.3), content encryption in the writer, crypto-shredding | When A1 allows stored content; P1 pilot runs history mode `none` (§11.2) |
| Feedback comments in our store (`/v1/feedback`, import), comment reveal, conversation review | Same; comments are refused (`comment_not_stored`) or dropped on import and stay in Open WebUI (§14) |
| Chat attachments (§15.2), widget conversations stored for review, server-side history (P2) | Also wait for A1 to allow stored content |

Reasoning: [weakness review §5](../passes/weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control):

| Item | Where it went |
|---|---|
| `x-ume-on-behalf-of`, the delegation echo check (§8.2 step 3) and event 5011 in P1 | the assistant's gateway key from the gateway's integration phase 2; until phase 1 the pilot team's per-level keys (§8.1) |
