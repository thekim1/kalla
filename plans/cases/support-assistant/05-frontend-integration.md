# Frontend integration: the contract for the frontend team

The frontend is built by another team in its own repository. This file is what they build against: how the app
authenticates, which endpoints it calls, what comes back, and what Ume.Rag's operators set up. Everything below is
read from the code on `main` (2026-11-06); the binding descriptions are chat §3 and knowledge §3–§4.

## 1. Architecture and authentication

```text
staff browser ──OIDC (Keycloak realm ume)──▶ frontend app (its own backend)
                                              │  Authorization: Bearer ume-rag-sk-…   (server side only)
                                              ▼
                                         rag-api  /v1/models, /v1/chat/completions, /v1/feedback
staff browser ──────── citation links ──────▶ rag-api  /v1/documents/{id}/content?grant=…   (staff network)
                └───── citation links ──────▶ intranet page URLs
```

| Rule | Why | Reference |
|---|---|---|
| The app calls rag-api **from its own backend**, never from the browser | the key is a secret; rag-api has no CORS (no `AddCors`/`UseCors` in `src/Ume.Rag.Api/Program.cs`), so browser calls fail anyway | identity §6 |
| One Ume.Rag client of kind **`integration`** and one `ume-rag-sk-` key for it | `integration` accepts a secret key (`src/Ume.Rag.Domain/Access/AccessPolicy.cs:203-211`); the key is sent as `Authorization: Bearer …`, `x-api-key` or `api-key` (`src/Ume.Rag.Infrastructure/Keys/KeyStore.cs:49-67`) | identity §3 table, row "Integration with a key" (P1) |
| Staff sign in to **the app**, not to Ume.Rag | rag-api accepts no user tokens today: the OAuth token path is designed but not built (identity §7.1 "Not built yet") | identity §7 |
| The app decides who may use it | Ume.Rag sees every request as the key (`SubjectId` = the key's id, `src/Ume.Rag.Api/KnowledgeCallers.cs:90`; no header can name the end user), so audience by department or group does not apply to it (identity §4.3 step 10) | gap SA-5 |

**Sign-in for the app.** The app gets its own OIDC client in Keycloak realm `ume` (the realm shared with the gateway;
Decisions "P1 pilot sign-in", "Gateway release 1 …"). It is the app's client, not one of RAG's: the realm's
administrator creates it, and a realm role or group for support staff lets the app check who may use it. Which
accounts exist and how support staff get them is the same open point as for Open WebUI (identity §5.3, "Who may sign
in … in P1 is not settled").

## 2. What Ume.Rag's admins set up

| Step | Who | How |
|---|---|---|
| Register the client: kind `integration`, ceiling `internal`, `StoresHistory` true if the app keeps chats (then `ClientRetentionDays`), `PortalUrl` = the app's address | platform admin | `POST /api/clients` (`src/Ume.Rag.AdminApi/ClientEndpoints.cs:33`) |
| Create its key, scoped to the support assistant (`Scopes.AssistantIds`), with an expiry | platform admin | `POST /api/keys` (`src/Ume.Rag.AdminApi/KeyEndpoints.cs:41`); rotate with `POST /api/keys/{id}/rotate` |
| Hand the key over once, through a secret channel | platform admin → frontend team | never in a repository or ticket |
| Make the assistant available to the client | department admin | assistant page, *Audience* (`PUT /api/assistants/{id}/audiences`) |
| Route the app's backend host to rag-api | operator | reverse proxy and firewall: `/v1/models`, `/v1/chat/completions`, `/v1/feedback` from the app's backend only; `/v1/documents/*` from the staff network (identity §5.3; knowledge §4.6) |
| Absolute citation links | operator | `Knowledge:PublicBaseUrl` = rag-api's staff-network address (set from `UME_RAG_API_PUBLIC_URL`, `deploy/compose.prod.yaml:155`) |
| Allow the chat body through the proxy | operator | rag-api accepts 2 MB on `/v1/chat/completions` (`src/Ume.Rag.Api/ChatEndpoints.cs:19`); the printed proxy routes allow 1 MB outside uploads ([`../../../docs/production-setup.md`](../../../docs/production-setup.md), "Reverse proxy and PKI"), so raise it on this route (gap SA-15) |

The key's `AllowedIpRanges` and `RequestsPerMinute` can be saved (`src/Ume.Rag.AdminApi/KeyEndpoints.cs:242-245`), but
no enforcement was found in rag-api (`KeyChecks.Refusal` checks kind, status and owner only,
`src/Ume.Rag.Domain/Services/KeyChecks.cs:18-34`). Until gap SA-7 the network restriction is the proxy's and the
firewall's job.

## 3. Endpoints

All chat routes answer errors in the OpenAI shape and send `Cache-Control: no-store` (chat §3, §3.6).

### 3.1 `GET /v1/models`

The assistants this key may use (`src/Ume.Rag.Api/ChatEndpoints.cs:26-48`):

```json
{ "object": "list",
  "data": [ { "id": "support", "object": "model", "created": 1793952000, "owned_by": "umea-kommun",
              "name": "Supportassistenten", "description": "Hjälper supportpersonal med ärenden. Kontakt: …",
              "info": { "meta": { "description": "…" } } } ] }
```

### 3.2 `POST /v1/chat/completions`: request

```json
{ "model": "support",
  "stream": true,
  "stream_options": { "include_usage": true },
  "messages": [
    { "role": "user", "content": "Användaren får felmeddelande 809 när hen ansluter till VPN hemifrån." },
    { "role": "assistant", "content": "…previous answer…" },
    { "role": "user", "content": "Det hjälpte inte. Vad gör jag nu?" } ] }
```

| Rule | Value | Reference |
|---|---|---|
| `model` | the assistant's slug; unknown or not allowed → 404 `model_not_found` | chat §3.2 |
| `messages` | 1–1,000; the **last must be `user`**; `system`/`developer` messages are ignored (the assistant's instructions decide); text only, attachments → 400 `attachments_not_supported` | `src/Ume.Rag.Answering/Chat/ChatRequest.cs:28-113` |
| Last user message (the question) | **1–2,000 characters** after normalisation, else 400 `question_too_long`; other messages ≤ 32,000 characters | `ChatRequest.cs:112`; `src/Ume.Rag.Retrieval/QueryNormalizer.cs:14` |
| Body | ≤ 2 MB | `src/Ume.Rag.Api/ChatEndpoints.cs:19` |
| History | the app sends the whole conversation every turn; it is the authoritative history. Ume.Rag uses the last `history.maxMessages` within `history.maxTokens` | chat §11.3 |
| `max_tokens` / `max_completion_tokens` | may lower, never raise, the assistant's limit | chat §3.2 |
| `temperature`, `top_p`, `user`, `metadata`, … | ignored | chat §3.2 |
| `tools`, JSON `response_format`, `n` > 1, `logprobs` | refused with 400 | `ChatRequest.cs:44-69` |
| Header `x-rag-conversation-id` | send back the value the first answer returned; a foreign or forged id is 404 `conversation_not_found` | `src/Ume.Rag.Api/ChatEndpoints.cs:112-130`; chat §11.1 |

### 3.3 Response without streaming

```json
{ "id": "chatcmpl-0193b0d2c4f87c1e9a3f5e2d1b0a9c8d", "object": "chat.completion", "created": 1793952123,
  "model": "support",
  "choices": [ { "index": 0, "finish_reason": "stop",
    "message": { "role": "assistant",
      "content": "Fel 809 betyder att brandväggen blockerar anslutningen [1]. …",
      "annotations": [ { "type": "url_citation", "url_citation": {
          "url": "https://intranat.umea.se/it/vpn/fel-809",
          "title": "VPN – Fel 809 (2026-10-02)",
          "start_index": 52, "end_index": 55 } } ] } } ],
  "usage": { "prompt_tokens": 5120, "completion_tokens": 180, "total_tokens": 5300 } }
```

(Shape from `src/Ume.Rag.Api/ChatEndpoints.cs:160-182`; values illustrative.) `id` is `chatcmpl-` + the message id,
used for feedback (§3.6). The response header `x-rag-conversation-id` carries the conversation id. Chat §3.3 also
plans an `x-request-id` header; none was found in the code, but every error body carries `request_id`.

### 3.4 Streaming

`text/event-stream`, one `data: {…}` line per event, in this order (`src/Ume.Rag.Api/ChatStreaming.cs:19-122`):

1. A role chunk: `delta: { "role": "assistant", "content": "" }`.
2. Optionally a caveat line as its own content chunk: degraded search, or a general-knowledge answer (chat §10.2,
   §10.4). The text comes from the platform, not the model.
3. Content chunks: `delta: { "content": "…" }`. A chunk that contains a citation marker also carries
   `delta.annotations` with `url_citation` objects; `start_index`/`end_index` index into the **whole** delivered
   content, not the chunk (`ChatStreaming.cs:109-118`).
4. For a general-knowledge answer, the assistant's contact links as a Markdown list.
5. A final chunk `delta: {}` with `finish_reason` (`stop`, or the gateway's reason when the answer was cut).
6. With `stream_options.include_usage`: one chunk with `choices: []` and `usage`.
7. `data: [DONE]`.

Null members are omitted (the serializer ignores nulls, `ChatStreaming.cs:22`), so non-final chunks have no
`finish_reason` key. An error after the first byte is one chunk `{"error":{"message":"The answer was
interrupted.","type":"api_error","code":"stream_interrupted"}}` followed by `[DONE]` (`ChatStreaming.cs:82-87`).

### 3.5 Not found, unavailable, general answers

These come back as ordinary completions, with no flag that tells them apart (gap SA-2):

| Case | Content | Annotations |
|---|---|---|
| Not found (coverage `Low`, level `off` or `supplement`) | the assistant's not-found text in the question's language, then its contact links as a Markdown list; no model call, usage 0 | none |
| Search unavailable | a fixed "try again shortly" text plus contact links | none |
| General answer (`fallback`/`open`, coverage `Low`) | the caveat line, the model's answer, the contact links | none |
| Degraded search (keyword only) | the caveat line, then a normal cited answer | yes |

(chat §10; `src/Ume.Rag.Answering/Chat/ChatService.cs:86-101`.)

### 3.6 `POST /v1/feedback`

```json
{ "message_id": "chatcmpl-0193b0d2c4f87c1e9a3f5e2d1b0a9c8d", "rating": -1, "reason": "wrong_sources" }
```

`rating` 1 or −1; `reason` one of `incorrect`, `incomplete`, `wrong_sources`, `not_relevant`, `other`. 204 on success;
repeating it updates the rating. **Send no `comment`**: every assistant runs in history mode `none`, and a comment is
refused with 400 `comment_not_stored` (`src/Ume.Rag.Api/FeedbackEndpoints.cs:123-128`). Ratings feed the admin's
feedback page and daily totals (admin §8.1).

### 3.7 Conversations

`GET/DELETE /v1/conversations[/{id}]` exist (chat §3.4) but return metadata only in P1 (no text is stored). The app
keeps its own chat history if it shows one, which makes it a `StoresHistory` client (§2; question 10).

## 4. Citations: opening and downloading the page (requirement 3)

Each `url_citation` has a `url` and a `title` (the label: title, last heading, pages, modified date;
`src/Ume.Rag.Answering/Chat/Citations.cs:75-94`). Markers in the text are renumbered `[1]`, `[2]`… in order of first
citation; one document keeps one number.

| Document | `url` | Opening it |
|---|---|---|
| Crawled page or crawled file | the page's or file's own URL (intranet), plus `#page=N` for PDFs | opens the live page in the staff browser |
| Uploaded file | `<Knowledge:PublicBaseUrl>/v1/documents/{id}/content?grant=g1.…`, plus `#page=N` for PDFs (`Citations.cs:67-73`; `src/Ume.Rag.Answering/DownloadGrants.cs:120`) | PDF, PNG and JPEG open inline, other types download with their file name (`src/Ume.Rag.Answering/DocumentContentReader.cs:76-89`). Valid **7 days** (`internal`), re-checked on every use, reachable from the staff network only; an expired link shows a short "ask again" page (knowledge §4.6) |

The grant link also unlocks the document's metadata: `GET /v1/documents/{id}?grant=…` with the same grant returns
title, display name, media type, modified date, language and page count (`src/Ume.Rag.Api/KnowledgeEndpoints.cs:97-116`).
The document id is the path segment of the link. Crawled pages have no grant and no served content
(`content_not_served`; serving crawled HTML from rag-api's origin would be an XSS vector,
`src/Ume.Rag.Answering/KnowledgeService.cs:180-191`).

Suggested UI: render the answer's Markdown, turn each `[n]` (by the annotation's indices) into a link, and list the
cited documents under the answer with their labels. Do not forward grant links outside the organisation: anyone on
the staff network who has one can open it for 7 days.

## 5. Related documents and follow-up suggestions (requirements 4 and 5)

| Need | Today | Interim in the app |
|---|---|---|
| Top or related documents beyond the cited ones | not returned. The answer carries only cited documents; `/v1/retrieve` is open to `internal` clients only (`Knowledge:OpenToClientKinds`, knowledge §2.3), and `assistant_id` is refused from other clients (`src/Ume.Rag.Api/KnowledgeEndpoints.cs:251-254`); collection access for non-internal callers is denied (`AccessPolicy.cs:170-172`) | show the cited documents as "Sources"; gap SA-1 adds the rest |
| Suggested follow-up questions | not generated | none; the assistant asks clarifying questions in its text ([`04-assistant.md`](04-assistant.md) §3); gap SA-3 |

## 6. Long tickets

The question (last user message) is also the search query, capped at 2,000 characters. Until gap SA-4, the app:

1. sends a ticket of ≤ 2,000 characters as the question;
2. for a longer ticket, sends it as an **earlier** user message, followed by a short question as the last message
   ("Hur löser jag ärendet ovan?"). Condense rewrites that last message into a search query from the conversation
   (chat §5; ≤ 96 tokens, 600 ms; on timeout or failure the raw short question is searched, which retrieves poorly).
   The assistant's `history.maxTokens` should then be 8,000 ([`04-assistant.md`](04-assistant.md) §5). Unverified on
   real tickets: measure it in the pilot;
3. never cuts a ticket silently; it tells the user when it shortened the text.

## 7. Errors and retries

| Status | `code` | App behaviour |
|---|---|---|
| 400 | `invalid_request`, `question_too_long`, `attachments_not_supported`, `unsupported_parameter`, `comment_not_stored`, `context_length_exceeded` | fix the request; show a plain message |
| 401 | `missing_key`, `invalid_key`, `key_expired`, `key_revoked`, … | configuration fault: alert the operators |
| 402 | `budget_exceeded` | the gateway budget is spent: alert the operators |
| 403 | `client_disabled`, `permission_missing`, … | configuration fault |
| 404 | `model_not_found`, `conversation_not_found`, `message_not_found` | assistant unpublished or not in the client's audience; drop a stale conversation id |
| 429 | `rate_limited` | wait `Retry-After` seconds |
| 502 | `upstream_error` | retry once, then show an error |
| 503 | `config_unconfirmed`, `search_unavailable`, `upstream_unavailable` | wait `Retry-After`, retry |

(chat §3.6; `src/Ume.Rag.Api/ChatEndpoints.cs:193-233`.)

**Rate limits.** 20 messages a minute per client and subject (`Settings.Chat.messagesPerMinutePerUser`, deployment
default `Chat:MessagesPerMinutePerUser`; `src/Ume.Rag.Api/ChatStreaming.cs:170-200`). With one key, **every staff
member is the same subject**, so the whole app shares 20 messages a minute. Raising the setting raises it for every
client of the platform. Gap SA-6. Downloads: 120 a minute per address (`src/Ume.Rag.Api/Program.cs:43-54`).

## 8. Developing against Ume.Rag

- **Locally:** run Ume.Rag's AppHost with the FakeGateway ([`../../../docs/local-development.md`](../../../docs/local-development.md));
  sign in to the admin UI as a test platform admin, create an `integration` client and key, a small collection and an
  assistant. The FakeGateway answers deterministically, so the app's parsing and rendering can be tested without
  model cost.
- **API reference:** rag-api publishes an OpenAPI document and a Scalar page when `Knowledge:PublishApiReference` is
  on (default on; `src/Ume.Rag.Api/Program.cs:55-66`, `:125-131`). It is titled "Ume.Rag Knowledge API"; whether the
  chat routes appear in it is unverified. This file and chat §3 are the contract until then.
- **Test host:** the same routes as production, with the app's backend host routed by the operator.

## 9. Setup checklist

| # | Step | Who |
|---|---|---|
| 1 | App's OIDC client and a support-staff role in realm `ume` | realm administrator |
| 2 | Ume.Rag `integration` client, key scoped to the assistant | Ume.Rag platform admin |
| 3 | Assistant audience = the client | support department admin |
| 4 | Proxy and firewall: app backend → rag-api chat routes (2 MB body); staff network → `/v1/documents/*` | operator |
| 5 | `Knowledge:PublicBaseUrl` set; `Settings.Chat.allowedLinkHosts` includes the intranet host | operator, platform admin |
| 6 | Rate limit decided (SA-6) | platform admin |
| 7 | App: server-side calls, streaming parser, citation rendering, feedback, long-ticket handling, error handling | frontend team |
| 8 | Contract check on the test host: one cited answer, one not-found, one uploaded-file download, one feedback | both teams |
