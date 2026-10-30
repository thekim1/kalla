# Chat API: plan

Data-plane service that turns assistants into OpenAI-compatible "models": a client sends a chat completion with
`model = <assistant>`, and the service retrieves knowledge (and, for assistants with tool-bearing skills, calls tools
through the tool hub), builds the prompt, streams the answer from the gateway with citations, and stores the conversation's metadata for 90 days (its content only once A1 allows, weakness review §4).
Review items applied: R1, R4, R5, R7, plus the smaller conversation-id finding. Detailed in [`design.md`](design.md)
(detail step 8).

## 1. Scope

**Does:** assistant resolution (the published version) and access, the fixed query-step sequence, the tool loop for
assistants with tool-bearing skills (P2), prompt building, streaming through the gateway, residency enforcement, citations, "not found"
answers, conversation history with retention, feedback.

**Does not:** a chat UI (clients: Open WebUI, widget, Teams, apps), retrieval internals (knowledge API), tool
execution and credentials (tool hub), model routing or budgets (gateway).

## 2. API (first cut)

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/v1/models` | Assistants this caller **and client** may use, as OpenAI models |
| `POST` | `/v1/chat/completions` | OpenAI-compatible, streaming and non-streaming |
| `GET` | `/v1/conversations`, `/v1/conversations/{id}` | The caller's own history |
| `DELETE` | `/v1/conversations/{id}` | The caller deletes a conversation (its rows are deleted) |
| `POST` | `/v1/feedback` | `{ message_id, rating: 1\|-1, reason?, comment? }` on an assistant message of the caller's own conversation (design §3.5) |
| `POST`, `GET`, `DELETE` | `/v1/conversations/{id}/attachments[/{aid}]` | P2: chat-scoped attachments (design §15.2); worker → API `POST /internal/attachments/{id}/chunks` (internal service key) |

Callers and clients follow `../identity/plan.md`: Entra access tokens (delegated `Chat.Use`), the signed user JWT
from Open WebUI, `ume-rag-sk-` keys for integrations, `ume-rag-pk-` keys + allowed origin + IP rate limit for the public widget.
Every request resolves to a **client** from the client registry, which sets a classification ceiling (R5).

## 3. Assistant modes

One assistant type (simplification pass §15): instructions, the collections it links directly (its only knowledge)
and, from P2, skills (know-how and tools, never collections). Chat serves the assistant's **published version**; edits
are drafts tested in the admin test panel until published (data-model §2.1). The mode is derived, not chosen:

- **Fixed RAG path** (P1; any assistant without tool-bearing skills): fixed query steps, one model call. Fast and
  predictable.
- **Agent loop** (P2; an assistant that links a skill with tools, shown as "Använder verktyg"): the model may call
  `search_knowledge`, `load_skill` and the read-only tools of its skills in a bounded loop: per user message 8 tool
  calls, 60 s until the final answer starts and $0.05 model cost by default (assistants may lower; platform admins raise to ≤ 20 / 120 s / $0.25; tools design §6.5,
  answered 1). No condense in agent mode: the model writes its own `search_knowledge` queries. Tool calls go to the
  tool hub (`../tools/plan.md`, `../tools/design.md` §6).

## 4. Request path (RAG assistant)

1. Authenticate; resolve client and assistant (its published version) from in-memory caches **at the current config
   version**; if the version has not been confirmed for longer than the grace window (`Config:MaxUnconfirmedSeconds`,
   default 90 s), refuse (fail closed for keys, access and classification, R7).
2. Check access (`IAccessPolicy`: user, groups, department, client) and classification: the assistant's level must not
   exceed the client's ceiling (R5).
3. **Query steps** (fixed order, configurable per assistant). A short conversational message (no content words:
   greetings, thanks) skips steps 1–4 and goes to the model with a "no sources" frame (design §6.2).
   1. *Condense*: rewrite the latest question into a standalone query, **follow-ups only** and only when the question
      is not self-contained (heuristic: length and no anaphora), on by default, one short call to
      `Settings.Chat.CondenseModel` (**on-prem**) with a hard 600 ms timeout, falling back to the raw question; used
      for retrieval only (design §5). This is the only query rewriting: the Knowledge API uses the query as given
      (knowledge design §5.1).
   2. *Retrieve*: Knowledge API (in-process `IRetriever`) with `AssistantId` (it resolves the published version's
      direct collections), `Expand = Section`, the assistant's filters and top-k (default 10, max 50; knowledge design §2.2).
   3. *Rerank*: `IReranker` seam in P1 (off); gateway-served rerank per assistant in P2 when the evaluation shows a
      gain (knowledge design §6.7).
   4. *Coverage check*: coverage is `Low` only when the vector **and** the keyword side are both weak (knowledge
      design §7; until the keyword floor is calibrated from the pilot gold set, the vector side alone, weakness review
      §2); then answer with the configured "not found" text and contact links, without calling the model. A
      condense failure or timeout never produces the not-found text: retrieval runs on the raw question and the model
      answers. If coverage is `Unknown` (degraded to keyword-only because the query embedding failed, knowledge design
      §5.5), answer from the keyword results with a caveat and **never** with the not-found text; `Unknown` with zero
      results → the fixed "search unavailable" text, no model call (design §10.2).
   5. *Prompt*: system prompt + numbered sources (marked as untrusted content) + history window + question; one
      number `[n]` per document in order of its best rank, with its sections and page ranges (knowledge design
      §10.4); history = the client's messages, last 10 and ≤ 4,000 tokens (design §6.3).
4. Call the gateway (`/v1/chat/completions`, stream) with the effective level's delegating key on behalf of the
   assistant's team (`x-ume-on-behalf-of: <team id>`) and an **explicit** residency ceiling on every call: `External`
   for `public`, `Eu` for `internal`, `OnPrem` for `sensitive` (platform plan §6; R4; gateway design §3, §5). A
   `DelegatingHandler` in the gateway client refuses any request without the ceiling and checks that the key matches
   the level. For clients that keep history (Open WebUI) every turn, from the first message, runs at **max(assistant
   level, client ceiling)**, so model and residency never change between turns (design §8.2). Gateway `503` with
   `Retry-After` (in-ceiling targets drained) is passed on as retryable; `403 max_residency_not_met` is a
   configuration fault (design §3.6). **Until gateway release 2 (P1)** the keys are the pilot department's team's own
   keys per level and no `x-ume-on-behalf-of` is sent; delegation and its echo check arrive with release 2 in P2
   (weakness review §5, gateway design §13.4).
5. **Residency check before the first byte (R4)**: read the gateway's `x-ume-residency` response header, compared
   case-insensitively by order `OnPrem < Eu < External` (the gateway writes `Eu`); anything above the call's ceiling,
   or (from release 2) a missing `x-ume-on-behalf-of` echo when `Gateway:RequireDelegationEcho` is set, aborts the request with an
   error and a security event. No content is streamed first.
6. Stream to the client. Parse `[n]` markers, **renumber them in order of first citation**, and emit citations only
   for sources actually used, per client kind (never both): Open WebUI gets one `source` event per cited document
   just before its first marker, other clients OpenAI `url_citation` annotations (design §9.2–§9.3;
   `../../platforms/open-webui.md` §9). Links are the
   document's `url`, or its `download_url` (signed grant link, knowledge design §4.6) for uploads and attachments,
   plus `#page=n` for PDFs; labels show title, heading path, pages and modified date (knowledge design §10.4). After
   the answer, call `IRetriever.MarkCited` with only the ranks actually cited (counters only). Images and HTML are stripped and
   links to hosts outside the allow-list de-linked (exfiltration channels, design §9.4).
7. After the response: conversation, messages, used sources, tool-call records and gateway usage headers are written
   by a background writer, off the request path.

**Performance budget**: overhead before the first gateway byte, excluding retrieval embedding and SQL, under 10 ms at
p95; no N+1 loading (Eneo: 78 SQL statements, 54–68 ms per message). Measured with the gateway's benchmark harness
against a fake gateway, idle and during the R1 ingestion load test.

## 5. Conversations and identity

- **Conversation id**: required from the client (Open WebUI chat id, widget session id) or issued by us on the first
  response (`x-rag-conversation-id`, signed `c1.…`, verified without SQL) for clients to send back. No derivation from
  history hashes. One conversation **per assistant**: a model switch inside an Open WebUI chat starts a new one
  (design §11.1).
- **The client's messages are authoritative for the prompt**; our store is a record for the user, review, feedback
  and retention, never read on the hot path (design §11.3). Server-side history is a P2 option for the widget.
- Stored per subject (`SubjectId`: Entra `oid`, Open WebUI user id, or anonymous widget session; identity design §3).
- **History mode, retention and encryption follow the assistant's level policy** (`../platform/plan.md` §8a):
  `stored`, `session` (24 h) or `none` (metadata only; also forced for Open WebUI temporary and channel chats);
  retention within the level's limits; content envelope-encrypted before the background writer stores it, under the
  data key of its (level, expiry month) **bucket**. Retention and a user's delete remove rows; destroying bucket keys
  (crypto-shredding, which reaches backups) stays switched off until the registrar answers A1. Copies in clients are
  governed by the client's data-handling profile (R5).
- **Pilot: history mode `none` until A1 is answered** ([weakness review §4](../weakness-review.md#4-data-protection-guards-the-secondary-copy-while-the-primary-stays-unprotected)). The pilot assistant stores
  metadata and ratings only, no message content. The bucket keys, content encryption and crypto-shredding above are
  **not built in P1**; the data model keeps their columns and they are built when A1 allows stored content. Feedback
  comments are refused (`comment_not_stored`) or dropped on import, so they stay only in Open WebUI.
- Public widget conversations (P2) are stored for quality review without user identity once A1 allows stored content; the widget tells visitors not to
  enter personal data.
- Reading other people's conversations happens only in the admin UI, by `rag-reviewer`, with a stated reason, audited
  (admin design §8); built when A1 allows stored content (weakness review §4).

## 6. Open WebUI pilot setup (R5)

Registered as a client with ceiling **`internal`**; `sensitive` assistants never appear in its model list. The ceiling
stays `internal` only for EU providers covered by a processor agreement (PUB-avtal) listed in the DPIA (register G1);
if the DPIA requires more, the client is limited to on-prem models (weakness review §4).

Full settings: design §4.3 (Open WebUI v0.12.5).

- **One connection** only: `OPENAI_API_BASE_URLS` pointing at this API (no gateway connection, so the user JWT goes
  only to us), `ENABLE_PERSISTENT_CONFIG=false`, `BYPASS_MODEL_ACCESS_CONTROL=true` (our `/v1/models` filters),
  `ENABLE_FORWARD_USER_INFO_HEADERS=true` with `FORWARD_USER_INFO_HEADER_JWT_SECRET`.
- Custom connection headers `X-Rag-Owui-Message-Id`, `X-Rag-Owui-User-Message-Id` (feedback mapping, regeneration
  dedup; unsigned, used only inside the caller's own conversation).
- Its own RAG, web search, tools, code execution, query/autocomplete generation and context compaction off; **title,
  tag and follow-up generation off** (no task model); user system prompts and parameters off; **file and web uploads
  off** (P1).
- `ENABLE_ADMIN_CHAT_ACCESS=false`, `ENABLE_ADMIN_EXPORT=false`: review happens only in our admin UI with a reason
  (none in P1, weakness review §4).
- A fixed, non-dismissible banner (`WEBUI_BANNERS`): "Skriv inte in sekretessbelagda uppgifter eller känsliga
  personuppgifter." (weakness review §4; design §4.3).
- API keys restricted to the feedback endpoints for the service account of our **feedback import** (every 15
  minutes, design §14.2) and feedback pruning; users get no API keys. No direct SQL against Open WebUI's database.
- Its own chat copies: v0.12.5 has no age-based retention setting, and its API deletes other users' chats only with
  admin chat access on; how the 90 days are enforced is open register item **W2**, decided within the DPIA (**G1**)
  before go-live (stopgap: unpruned copies, stated in Open WebUI's data-handling profile; design §13.3, §21.2). In the
  pilot this store is the **primary copy** of every conversation and feedback comment; a public-records request for a
  conversation is answered from it (runbook section, weakness review §4).
- Login to Open WebUI itself with Entra (its own app registration). Branding stays (licence clause).

## 7. Milestones

| Phase | Deliverable |
|---|---|
| P1a (weakness review §1) | `/v1/models` filtered by caller and client, streaming RAG completions, citations (both formats), not-found answers, residency check; one assistant in Open WebUI for the pilot department |
| P1b | issued conversation ids, metadata history (mode `none`, weakness review §4) + retention, Open WebUI pilot with its data-handling profile and input banner; `/v1/feedback`, **Open WebUI feedback import** and the **Feedback page** in the admin UI (design §14; admin design §8) |
| When A1 allows stored content (weakness review §4) | history modes `stored`/`session`, chat bucket keys and content encryption in the writer, feedback comments, conversation review in the admin (design §11.2, §12.3, §14) |
| P2 (moved from late P1, weakness review §1) | Nightly answer-level evaluation in `rag-admin`: an on-prem judge over the evaluation sets through `Ume.Rag.Answering` (faithfulness, citation precision); `uncited_answer` rate per assistant (design §18.8) |
| P2 | Skills with read-only tools via the tool hub (an assistant linking one runs the agent loop), public widget (`ume-rag-pk-` keys, origins, rate limits), chat attachments (design §15.2), gateway-served rerank per assistant, feedback review beyond P1, condense tuning with the advisory evaluation |
| P3 | Teams client, user-delegated tools (OBO), write tools with confirmation |

## 8. Open questions

1. ~~Does Open WebUI forward a stable chat id?~~ **Answered**: yes, `X-OpenWebUI-Chat-Id` when user-info forwarding
   is on (`open-webui/backend/open_webui/utils/headers.py:198`). It is not inside the signed JWT, so a conversation is
   keyed by (client, Open WebUI user id from the JWT `sub`, chat id, assistant) (identity design §3, §5; design §11.1): a guessed id can never
   reach another user's conversation.
2. ~~File attachments in P1?~~ **Answered**: not in P1. Ad-hoc files arrive in P2 through our own upload endpoint
   (level-based retention). *Updated 2026-10-30 (answered 8)*: chat-scoped storage in the `chat` schema under the
   chat bucket keys, **not** a temporary collection (design §15.2).
3. ~~Language?~~ **Answered**: answer in the user's language (sources are mostly Swedish); an assistant setting can
   force Swedish. Citations keep the source's original title.

Answered (user, 2026-10-30; details in [`design.md`](design.md) §21.1):

4. ~~Condense?~~ **Answered**: on by default, follow-ups only (skipped when self-contained), one short call on an
   on-prem model with a 600 ms timeout; per assistant it can be turned off.
5. ~~History window?~~ **Answered**: last 10 messages, ≤ 4,000 tokens, oldest dropped first; earlier sources not
   resent; per assistant.
6. ~~Not found?~~ **Answered**: coverage `Low` (vector and keyword both weak) → the assistant's fixed sv/en text +
   contact links, no model call; `Unknown` answers from keyword results with a caveat, never the not-found text.
7. ~~Feedback in P1?~~ **Answered**: yes: `/v1/feedback` plus a scheduled import of Open WebUI ratings, comments
   encrypted under the chat bucket keys; editors see ratings in P1. *Later change (weakness review §4):* the P1 pilot
   runs history mode `none`, so only ratings are stored; comments stay in Open WebUI.
8. ~~C1 chat attachments (P2)?~~ **Answered**: chat-scoped storage with conversation retention, parsed by the worker,
   encrypted under the chat bucket keys, searched only within the conversation. *Later change (weakness review §4):*
   needs the bucket keys, so it waits for A1 to allow stored content too.
9. ~~P1 uploads in Open WebUI?~~ **Answered**: disabled in Open WebUI's permissions; file and image content parts
   refused with `attachments_not_supported`.
10. ~~Feedback comments for editors?~~ **Answered**: `public`/`internal`: shown after a one-click, pre-filled reason,
    audited as a conversation access, only the comment; `sensitive`: the full review flow with a second approver.
    Readers settled in step 10: `rag-reviewer` and `rag-department-admin`, not platform admins (admin answered 6).
    *Later change (weakness review §4):* built when A1 allows stored content, not in P1.

## 9. Deferred or removed (simplification pass)

Per [`../simplification-pass.md`](../simplification-pass.md) §1, §5, §6, §8, §9, §15:

| Item | Where it went |
|---|---|
| Virtual task model `owui-tasks`, `Clients.TaskModel`, task heuristic, `X-Rag-Owui-Task` | removed; Open WebUI title, tag and follow-up generation switched off; `Settings.Chat.CondenseModel` is the only task model (§2, §4, §6) |
| Per-conversation data keys, 7-day epochs, epoch races, tombstones, conversation key caches | chat content keys per (level, expiry month) bucket; user delete deletes rows (§5) |
| Crypto-shredding on retention | switched off until A1; retention deletes rows (§5) |
| Tier resolver for conversations | removed; `sensitive` isolation would be a separate deployment (§5) |
| Interim linked keys, releases B → A, no ceiling for `public` | per-level delegating keys, explicit ceiling on every call (`External` for `public`), one combined gateway release (§4) |
| Effective level raised only when earlier assistant turns are present | max(assistant level, client ceiling) from the first message for history-keeping clients (§4) |
| Coverage `Low` from vector distance alone; 1.2 s condense on every follow-up | vector and keyword both weak, conversational skip, condense never yields `Low`; skip heuristic + 600 ms (§4) |
| Rerank in P3 | seam P1, gateway rerank P2 (§4, §7) |
| Nightly SQL pruning sidecar against Open WebUI's database | removed; feedback pruning via its HTTP API, chat retention open (§6) |
| Strict fail-closed on an unconfirmed config version | grace window (§4) |
| RAG and agent as two assistant kinds chosen by the editor; knowledge from skills' collections | one assistant type, mode derived from tool-bearing skills; direct collections only; the published version is served (§3) |

## 10. Deferred or removed (weakness review)

Per [weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer):

| Item | Where it went |
|---|---|
| Nightly answer-level judge (`answers` runs) in `rag-admin` | P2 (§7; design §18.8) |
| Per-assistant test questions (`assistant-tests` runs) | P2 (design §18.8) |

Per [weakness review §4](../weakness-review.md#4-data-protection-guards-the-secondary-copy-while-the-primary-stays-unprotected):

| Item | Where it went |
|---|---|
| History modes `stored`/`session`, chat bucket keys, content encryption in the writer, crypto-shredding | When A1 allows stored content; the P1 pilot runs history mode `none` (metadata and ratings) (§5) |
| Feedback comments in our store, comment reveal, conversation review by `rag-reviewer` | Same; comments stay in Open WebUI (§5, answered 7, 10) |
| P2 features that store chat content (chat attachments, widget conversations for review, server-side history) | Need A1 to allow stored content as well |

Per [weakness review §5](../weakness-review.md#5-the-pilots-critical-path-runs-through-things-the-team-does-not-control):

| Item | Where it went |
|---|---|
| `x-ume-on-behalf-of`, the delegation echo check and event 5011 in P1 | P2 with gateway release 2; P1 calls with the pilot team's per-level keys (§4) |
