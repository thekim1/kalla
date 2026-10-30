# Chat API: plan

Data-plane service that turns assistants into OpenAI-compatible "models": a client sends a chat completion with
`model = <assistant>`, and the service retrieves knowledge (and, for agent assistants, calls tools through the tool
hub), builds the prompt, streams the answer from the gateway with citations, and stores the conversation for 90 days.
Review items applied: R1, R4, R5, R7, plus the smaller conversation-id finding. Detailed in [`design.md`](design.md)
(detail step 8).

## 1. Scope

**Does:** assistant resolution and access, the fixed query-step sequence for RAG assistants, the tool loop for agent
assistants (P2), prompt building, streaming through the gateway, residency enforcement, citations, "not found"
answers, conversation history with retention, feedback.

**Does not:** a chat UI (clients: Open WebUI, widget, Teams, apps), retrieval internals (knowledge API), tool
execution and credentials (tool hub), model routing or budgets (gateway).

## 2. API (first cut)

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/v1/models` | Assistants this caller **and client** may use, as OpenAI models; plus the virtual task model `owui-tasks` for `open-webui` clients with a task model (design §4.2) |
| `POST` | `/v1/chat/completions` | OpenAI-compatible, streaming and non-streaming; `model = owui-tasks` answers Open WebUI title/tags/follow-up tasks (never retrieved for, never stored) |
| `GET` | `/v1/conversations`, `/v1/conversations/{id}` | The caller's own history |
| `DELETE` | `/v1/conversations/{id}` | The caller deletes a conversation |
| `POST` | `/v1/feedback` | `{ message_id, rating: 1\|-1, reason?, comment? }` on an assistant message of the caller's own conversation (design §3.5) |
| `POST`, `GET`, `DELETE` | `/v1/conversations/{id}/attachments[/{aid}]` | P2: chat-scoped attachments (design §15.2); worker → API `POST /internal/attachments/{id}/chunks` (internal service key) |

Callers and clients follow `../identity/plan.md`: Entra access tokens (delegated `Chat.Use`), the signed user JWT
from Open WebUI, `ume-rag-sk-` keys for integrations, `ume-rag-pk-` keys + allowed origin + IP rate limit for the public widget.
Every request resolves to a **client** from the client registry, which sets a classification ceiling (R5).

## 3. Assistant modes

- **RAG assistant** (P1): fixed query steps, one model call. Fast and predictable; the default.
- **Agent assistant** (P2): built from skills; the model may call `search_knowledge`, `load_skill` and the read-only
  tools of its skills in a bounded loop: per user message 8 tool calls, 60 s until the final answer starts and $0.05
  model cost by default (assistants may lower; platform admins raise to ≤ 20 / 120 s / $0.25; tools design §6.5,
  answered 1). No condense in agent mode: the model writes its own `search_knowledge` queries. Tool calls go to the
  tool hub (`../tools/plan.md`, `../tools/design.md` §6).

## 4. Request path (RAG assistant)

1. Authenticate; resolve client and assistant from in-memory caches **at the current config version**; if the
   version cannot be confirmed, refuse (fail closed for keys, access and classification, R7).
2. Check access (`IAccessPolicy`: user, groups, department, client) and classification: the assistant's level must not
   exceed the client's ceiling (R5).
3. **Query steps** (fixed order, configurable per assistant):
   1. *Condense*: rewrite the latest question into a standalone query, **follow-ups only** (skipped on the first
      message), on by default, one short call to the platform's **on-prem** task model for every level, used for
      retrieval only (design §5). This is the only query rewriting: the Knowledge API uses the query as given
      (knowledge design §5.1).
   2. *Retrieve*: Knowledge API (in-process `IRetriever`) with `AssistantId` (it resolves the assistant's
      collections), `Expand = Section`, the assistant's filters and top-k (default 10, max 50; knowledge design §2.2).
   3. *Rerank*: P3.
   4. *Coverage check*: if coverage is `Low`, answer with the configured "not found" text and contact links, without
      calling the model. If coverage is `Unknown` (degraded to keyword-only because the query embedding failed,
      knowledge design §5.5), answer from the keyword results with a caveat and **never** with the not-found text;
      `Unknown` with zero results → the fixed "search unavailable" text, no model call (design §10.2).
   5. *Prompt*: system prompt + numbered sources (marked as untrusted content) + history window + question; one
      number `[n]` per document in order of its best rank, with its sections and page ranges (knowledge design
      §10.4); history = the client's messages, last 10 and ≤ 4,000 tokens (design §6.3).
4. Call the gateway (`/v1/chat/completions`, stream) with the platform service key on behalf of the assistant's team
   (`x-ume-on-behalf-of: <team id>`) and the residency ceiling of the effective level: `Eu` for `internal`, `OnPrem`
   for `sensitive` (platform plan §6; R4; gateway design §3, §5). A request carrying earlier assistant turns
   (client-sent history) runs at **max(assistant level, client ceiling)** (design §8.2). Interim: the team's linked key; after gateway
   release B only the default linked key, after release A none.
5. **Residency check before the first byte (R4)**: for `internal` and `sensitive` assistants, read the gateway's
   `x-ume-residency` response header, compared case-insensitively by order `OnPrem < Eu < External` (the gateway
   writes `Eu`); anything above the level's ceiling, or (after release A) a missing `x-ume-on-behalf-of` echo, aborts
   the request with an error and a security event. No content
   is streamed first.
6. Stream to the client. Parse `[n]` markers, **renumber them in order of first citation**, and emit citations only
   for sources actually used, per client kind (never both): Open WebUI gets one `source` event per cited document
   just before its first marker, other clients OpenAI `url_citation` annotations (design §9.2–§9.3;
   `../../platforms/open-webui.md` §9). Links are the
   document's `url`, or its `download_url` (signed grant link, knowledge design §4.6) for uploads and attachments,
   plus `#page=n` for PDFs; labels show title, heading path, pages and modified date (knowledge design §10.4). After
   the answer, call `IRetriever.MarkCited` with the cited ranks (counters only). Images and HTML are stripped and
   links to hosts outside the allow-list de-linked (exfiltration channels, design §9.4).
7. After the response: conversation, messages, used sources, tool-call records and gateway usage headers are written
   by a background writer, off the request path.

**Open WebUI task requests** (title, tags, follow-ups) go to `owui-tasks`, recognised by model id and the
`X-Rag-Owui-Task` header: no assistant policy, retrieval, condense or storage; answered by the client's on-prem
`TaskModel` with `x-ume-max-residency: OnPrem` (design §4.2).

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
  retention within the level's limits; content
  envelope-encrypted with a per-conversation data key before the background writer stores it. Retention destroys the
  data key (crypto-shredding), which also covers backups. Copies in clients are governed by the client's
  data-handling profile (R5).
- Storage goes through the tier resolver, so `sensitive` conversations can move to a separate data tier later
  without code changes.
- Public widget conversations are stored for quality review without user identity; the widget tells visitors not to
  enter personal data.
- Reading other people's conversations happens only in the admin UI, by `rag-reviewer`, with a stated reason, audited
  (admin design §8).

## 6. Open WebUI pilot setup (R5)

Registered as a client with ceiling **`internal`**; `sensitive` assistants never appear in its model list.

Full settings: design §4.3 (Open WebUI v0.12.5).

- **One connection** only: `OPENAI_API_BASE_URLS` pointing at this API (no gateway connection, so the user JWT goes
  only to us), `ENABLE_PERSISTENT_CONFIG=false`, `BYPASS_MODEL_ACCESS_CONTROL=true` (our `/v1/models` filters),
  `ENABLE_FORWARD_USER_INFO_HEADERS=true` with `FORWARD_USER_INFO_HEADER_JWT_SECRET`.
- Custom connection headers `X-Rag-Owui-Task`, `X-Rag-Owui-Message-Id`, `X-Rag-Owui-User-Message-Id` (task
  detection, feedback mapping, regeneration dedup; unsigned, used only inside the caller's own conversation).
- Its own RAG, web search, tools, code execution, query/autocomplete generation and context compaction off; user
  system prompts and parameters off; **file and web uploads off** (P1).
- Title, tag and follow-up generation through `TASK_MODEL_EXTERNAL=owui-tasks`, our virtual task model answered by
  the client's on-prem `TaskModel` (design §4.2).
- `ENABLE_ADMIN_CHAT_ACCESS=false`, `ENABLE_ADMIN_EXPORT=false`: review happens only in our admin UI with a reason.
- API keys restricted to `/api/v1/evaluations/feedbacks/list` for the service account of our **feedback import**
  (every 15 minutes, design §14.2); users get no API keys.
- Its own chat database (chats and feedback) pruned to 90 days by a nightly sidecar job (until it supports retention
  natively; design §13.3).
- Login to Open WebUI itself with Entra (its own app registration). Branding stays (licence clause).

## 7. Milestones

| Phase | Deliverable |
|---|---|
| P1 | `/v1/models` filtered by caller and client, streaming RAG completions, citations (both formats), not-found answers, residency check, issued conversation ids, history + retention, Open WebUI pilot with its data-handling profile; `/v1/feedback`, **Open WebUI feedback import** and the **Feedback page** in the admin UI (design §14; admin design §8) |
| P2 | Agent assistants with skills and read-only tools via the tool hub, public widget (`ume-rag-pk-` keys, origins, rate limits), chat attachments (design §15.2), feedback review beyond P1, condense tuning behind the evaluation gate |
| P3 | Rerank, query-side canvas (if evaluation shows it helps), Teams client, user-delegated tools (OBO), write tools with confirmation |

## 8. Open questions

1. ~~Does Open WebUI forward a stable chat id?~~ **Answered**: yes, `X-OpenWebUI-Chat-Id` when user-info forwarding
   is on (`open-webui/backend/open_webui/utils/headers.py:198`). It is not inside the signed JWT, so a conversation is
   keyed by (client, Open WebUI user id from the JWT `sub`, chat id, assistant) (identity design §3, §5; design §11.1): a guessed id can never
   reach another user's conversation.
2. ~~File attachments in P1?~~ **Answered**: not in P1. Ad-hoc files arrive in P2 through our own upload endpoint
   (level-based retention). *Updated 2026-10-30 (answered 8)*: chat-scoped storage in the `chat` schema under the
   conversation's data keys, **not** a temporary collection (design §15.2).
3. ~~Language?~~ **Answered**: answer in the user's language (sources are mostly Swedish); an assistant setting can
   force Swedish. Citations keep the source's original title.

Answered (user, 2026-10-30; details in [`design.md`](design.md) §21.1):

4. ~~Condense?~~ **Answered**: on by default, follow-ups only, one short call on an on-prem task model; per assistant
   it can be turned off.
5. ~~History window?~~ **Answered**: last 10 messages, ≤ 4,000 tokens, oldest dropped first; earlier sources not
   resent; per assistant.
6. ~~Not found?~~ **Answered**: coverage `Low` → the assistant's fixed sv/en text + contact links, no model call;
   `Unknown` answers from keyword results with a caveat, never the not-found text.
7. ~~Feedback in P1?~~ **Answered**: yes: `/v1/feedback` plus a scheduled import of Open WebUI ratings, comments
   encrypted with the conversation key; editors see ratings in P1.
8. ~~C1 chat attachments (P2)?~~ **Answered**: chat-scoped storage with conversation retention, parsed by the worker,
   encrypted with the conversation's data key, searched only within the conversation.
9. ~~P1 uploads in Open WebUI?~~ **Answered**: disabled in Open WebUI's permissions; file and image content parts
   refused with `attachments_not_supported`.
10. ~~Feedback comments for editors?~~ **Answered**: `public`/`internal`: shown after a one-click, pre-filled reason,
    audited as a conversation access, only the comment; `sensitive`: the full review flow with a second approver.
    Readers settled in step 10: `rag-reviewer` and `rag-department-admin`, not platform admins (admin answered 6).
