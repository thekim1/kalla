# Chat API: plan

Data-plane service that turns assistants into OpenAI-compatible "models": a client sends a chat completion with
`model = <assistant>`, and the service retrieves knowledge (and, for agent assistants, calls tools through the tool
hub), builds the prompt, streams the answer from the gateway with citations, and stores the conversation for 90 days.
Review items applied: R1, R4, R5, R7, plus the smaller conversation-id finding.

## 1. Scope

**Does:** assistant resolution and access, the fixed query-step sequence for RAG assistants, the tool loop for agent
assistants (P2), prompt building, streaming through the gateway, residency enforcement, citations, "not found"
answers, conversation history with retention, feedback.

**Does not:** a chat UI (clients: Open WebUI, widget, Teams, apps), retrieval internals (knowledge API), tool
execution and credentials (tool hub), model routing or budgets (gateway).

## 2. API (first cut)

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/v1/models` | Assistants this caller **and client** may use, as OpenAI models |
| `POST` | `/v1/chat/completions` | OpenAI-compatible, streaming and non-streaming |
| `GET` | `/v1/conversations`, `/v1/conversations/{id}` | The caller's own history |
| `DELETE` | `/v1/conversations/{id}` | The caller deletes a conversation |
| `POST` | `/v1/feedback` | Thumbs up/down + comment on a message |

Callers and clients follow `../identity/plan.md`: Entra access tokens (delegated `Chat.Use`), the signed user JWT
from Open WebUI, `ume-rag-sk-` keys for integrations, `ume-rag-pk-` keys + allowed origin + IP rate limit for the public widget.
Every request resolves to a **client** from the client registry, which sets a classification ceiling (R5).

## 3. Assistant modes

- **RAG assistant** (P1): fixed query steps, one model call. Fast and predictable; the default.
- **Agent assistant** (P2): built from skills; the model may call `search_knowledge`, `load_skill` and the read-only
  tools of its skills in a bounded loop (max steps, time and cost per message). Tool calls go to the tool hub
  (`../tools/plan.md`).

## 4. Request path (RAG assistant)

1. Authenticate; resolve client and assistant from in-memory caches **at the current config version**; if the
   version cannot be confirmed, refuse (fail closed for keys, access and classification, R7).
2. Check access (`IAccessPolicy`: user, groups, department, client) and classification: the assistant's level must not
   exceed the client's ceiling (R5).
3. **Query steps** (fixed order, configurable per assistant):
   1. *Condense*: rewrite the latest question into a standalone query (optional; on-prem model for `sensitive`).
      This is the only query rewriting: the Knowledge API uses the query as given (knowledge design §5.1).
   2. *Retrieve*: Knowledge API (in-process `IRetriever`) with `AssistantId` (it resolves the assistant's
      collections), `Expand = Section`, the assistant's filters and top-k (default 10, max 50; knowledge design §2.2).
   3. *Rerank*: P3.
   4. *Coverage check*: if coverage is `Low`, answer with the configured "not found" text and contact links, without
      calling the model. If coverage is `Unknown` (degraded to keyword-only because the query embedding failed,
      knowledge design §5.5), answer from the keyword results with a caveat and **never** with the not-found text.
   5. *Prompt*: system prompt + numbered sources (marked as untrusted content) + history window + question; one
      number `[n]` per document in order of its best rank, with its sections and page ranges (knowledge design
      §10.4).
4. Call the gateway (`/v1/chat/completions`, stream) with the platform service key on behalf of the assistant's team
   (`x-ume-on-behalf-of: <team id>`) and the residency ceiling of the assistant's level: `Eu` for `internal`, `OnPrem`
   for `sensitive` (platform plan §6; R4; gateway design §3, §5). Interim: the team's linked key; after gateway
   release B only the default linked key, after release A none.
5. **Residency check before the first byte (R4)**: for `internal` and `sensitive` assistants, read the gateway's
   `x-ume-residency` response header, compared case-insensitively by order `OnPrem < Eu < External` (the gateway
   writes `Eu`); anything above the level's ceiling, or (after release A) a missing `x-ume-on-behalf-of` echo, aborts
   the request with an error and a security event. No content
   is streamed first.
6. Stream to the client. Parse `[n]` markers and emit citations only for sources actually used: OpenAI
   `url_citation` annotations and Open WebUI `source` events (`../../platforms/open-webui.md` §9). Links are the
   document's `url`, or its `download_url` (signed grant link, knowledge design §4.6) for uploads and attachments,
   plus `#page=n` for PDFs; labels show title, heading path, pages and modified date (knowledge design §10.4). After
   the answer, call `IRetriever.MarkCited` with the cited ranks (counters only). External image
   URLs in answers are stripped (exfiltration channel).
7. After the response: conversation, messages, used sources, tool-call records and gateway usage headers are written
   by a background writer, off the request path.

**Performance budget**: overhead before the first gateway byte, excluding retrieval embedding and SQL, under 10 ms at
p95; no N+1 loading (Eneo: 78 SQL statements, 54–68 ms per message). Measured with the gateway's benchmark harness
against a fake gateway, idle and during the R1 ingestion load test.

## 5. Conversations and identity

- **Conversation id**: required from the client (Open WebUI chat id, widget session id) or issued by us on the first
  response (`x-rag-conversation-id`) for clients to send back. No derivation from history hashes.
- Stored per subject (`SubjectId`: Entra `oid`, Open WebUI user id, or anonymous widget session; identity design §3).
- **History mode, retention and encryption follow the assistant's level policy** (`../platform/plan.md` §8a):
  `stored`, `session` (24 h) or `none` (metadata only); retention within the level's limits; content
  envelope-encrypted with a per-conversation data key before the background writer stores it. Retention destroys the
  data key (crypto-shredding), which also covers backups. Copies in clients are governed by the client's
  data-handling profile (R5).
- Storage goes through the tier resolver, so `sensitive` conversations can move to a separate data tier later
  without code changes.
- Public widget conversations are stored for quality review without user identity; the widget tells visitors not to
  enter personal data.
- Reading other people's conversations happens only in the admin UI, with a stated reason, audited.

## 6. Open WebUI pilot setup (R5)

Registered as a client with ceiling **`internal`**; `sensitive` assistants never appear in its model list.

- `OPENAI_API_BASE_URLS` pointing at this API, `ENABLE_PERSISTENT_CONFIG=false`, `BYPASS_MODEL_ACCESS_CONTROL=true`
  (our `/v1/models` filters), `ENABLE_FORWARD_USER_INFO_HEADERS=true` with `FORWARD_USER_INFO_HEADER_JWT_SECRET`.
- Its own RAG, web search, tools and code execution off.
- Title, tag and follow-up generation on a task model **served on-prem** through the gateway (`TASK_MODEL_EXTERNAL`).
- Its own chat database pruned to 90 days by a scheduled job (until it supports retention natively).
- Login to Open WebUI itself with Entra (its own app registration). Branding stays (licence clause).

## 7. Milestones

| Phase | Deliverable |
|---|---|
| P1 | `/v1/models` filtered by caller and client, streaming RAG completions, citations (both formats), not-found answers, residency check, issued conversation ids, history + retention, Open WebUI pilot with its data-handling profile |
| P2 | Agent assistants with skills and read-only tools via the tool hub, public widget (`ume-rag-pk-` keys, origins, rate limits), feedback review, condense tuning behind the evaluation gate |
| P3 | Rerank, query-side canvas (if evaluation shows it helps), Teams client, user-delegated tools (OBO), write tools with confirmation |

## 8. Open questions

1. ~~Does Open WebUI forward a stable chat id?~~ **Answered**: yes, `X-OpenWebUI-Chat-Id` when user-info forwarding
   is on (`open-webui/backend/open_webui/utils/headers.py:198`). It is not inside the signed JWT, so a conversation is
   keyed by (client, Open WebUI user id from the JWT `sub`, chat id) (identity design §3, §5): a guessed id can never
   reach another user's conversation.
2. ~~File attachments in P1?~~ **Answered**: not in P1. Ad-hoc files arrive in P2 through our own upload endpoint
   (temporary per-conversation collection, level-based retention).
3. ~~Language?~~ **Answered**: answer in the user's language (sources are mostly Swedish); an assistant setting can
   force Swedish. Citations keep the source's original title.
