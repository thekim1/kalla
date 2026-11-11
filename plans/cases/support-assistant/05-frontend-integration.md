# Frontend integration: the contract for the frontend team

The frontend is built by another team in its own repository. This file is what they build against: how the app
authenticates, which endpoints it calls, what comes back, and what Ume.Rag's operators set up. Everything below is
read from the code on `main` (2026-11-06; line references re-checked against `v0.2.0` on 2026-11-08); the binding descriptions are chat §3 and knowledge §3–§4.

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
| The app decides who may use it | Ume.Rag sees every request as the key (`SubjectId` = the key's id, `src/Ume.Rag.Api/KnowledgeCallers.cs:95`; no header can name the end user), so audience by department or group does not apply to it (identity §4.3 step 10) | gap SA-5 |
| The rate limit is the app key's own | for a key subject the chat limit is the key's `RequestsPerMinute` when set, else `Settings.Chat.messagesPerMinutePerUser` (`ChatRateLimiter.LimitFor`, `src/Ume.Rag.Api/ChatStreaming.cs:250-257`); the platform admin sets it on the app key (§2, §7) | chat §4.1 step 4 |
| Only the app's backend may use the key | the key's `AllowedIpRanges` are checked on every request, else 403 `address_not_allowed` (`src/Ume.Rag.Domain/Services/KeyChecks.cs:38`, `src/Ume.Rag.Api/KnowledgeCallers.cs:64-71`). Behind the reverse proxy, rag-api takes the client address from `X-Forwarded-For` only when the connection comes from one of `Settings.TrustedProxies` (`src/Ume.Rag.Api/ClientAddressMiddleware.cs`) | identity §6.3 step 4 |

**Sign-in for the app.** The app gets its own OIDC client in Keycloak realm `ume` (the realm shared with the gateway;
Decisions "P1 pilot sign-in", "Gateway release 1 …"). It is the app's client, not one of RAG's: the realm's
administrator creates it, and a realm role or group for support staff lets the app check who may use it. Which
accounts exist and how support staff get them is the same open point as for Open WebUI (identity §5.3, "Who may sign
in … in P1 is not settled").

## 2. What Ume.Rag's admins set up

| Step | Who | How |
|---|---|---|
| Register the client: kind `integration`, ceiling `internal`, `StoresHistory` true if the app keeps chats (then `ClientRetentionDays`), `PortalUrl` = the app's address | platform admin | `POST /api/clients` (`src/Ume.Rag.AdminApi/ClientEndpoints.cs:33`) |
| Create its key, scoped to the support assistant (`Scopes.AssistantIds`), with an expiry, `requestsPerMinute` for the whole app (e.g. 200 for twenty staff) and `allowedIpRanges` = the app backend's addresses | platform admin | `POST /api/keys`, edit with `PUT /api/keys/{id}` (`src/Ume.Rag.AdminApi/KeyEndpoints.cs:41`, `:66`; fields `:240-245`, `requestsPerMinute` 1–100,000); rotate with `POST /api/keys/{id}/rotate`, which copies both (`src/Ume.Rag.Domain/Services/KeyRotation.cs:43-44`) |
| Hand the key over once, through a secret channel | platform admin → frontend team | never in a repository or ticket |
| Make the assistant available to the client | department admin | assistant page, *Audience* (`PUT /api/assistants/{id}/audiences`) |
| Trusted proxies: the reverse proxy's own addresses, so rag-api sees the app backend's address and not the proxy's | operator, platform admin | `PUT /api/settings/network` `trustedProxies` (`src/Ume.Rag.AdminApi/SettingsEndpoints.cs:28-32`; a `/0` network is refused, `:224-228`). Without it every request comes from the proxy, and the key's allow-list and the download limit see only the proxy |
| Route the app's backend host to rag-api | operator | reverse proxy and firewall: `/v1/models`, `/v1/chat/completions`, `/v1/feedback` from the app's backend only; `/v1/documents/*` from the staff network (identity §5.3; knowledge §4.6) |
| Absolute citation links | operator | `Knowledge:PublicBaseUrl` = rag-api's staff-network address (set from `UME_RAG_API_PUBLIC_URL`, `deploy/compose.prod.yaml:157`) |
| Allow the chat body through the proxy | operator | rag-api accepts 2 MB on `/v1/chat/completions` (`src/Ume.Rag.Api/ChatEndpoints.cs:19`); the generated proxy configuration and the printed routes allow it since #552 ([`../../../docs/production-setup.md`](../../../docs/production-setup.md), "Reverse proxy and PKI"); a proxy configured before 0.3.1/0.4.0 needs `client_max_body_size 2m` on the api host |

The proxy and the firewall still restrict the routes; the key's allow-list is the second line, so a leaked key is
useless outside the app's backend.

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
    { "role": "user", "content": "Det hjälpte inte. Vad gör jag nu?" } ],
  "ume": { "related_documents": 5, "follow_ups": 3 } }
```

| Rule | Value | Reference |
|---|---|---|
| `model` | the assistant's slug; unknown or not allowed → 404 `model_not_found` | chat §3.2 |
| `messages` | 1–1,000; the **last must be `user`**; `system`/`developer` messages are ignored (the assistant's instructions decide); text only, attachments → 400 `attachments_not_supported` | `src/Ume.Rag.Answering/Chat/ChatRequest.cs:68-160` |
| Last user message (the question) | **1–16,000 characters** after normalisation, else 400 `question_too_long`; other messages ≤ 32,000 characters. Paste the whole ticket: over 2,000 characters it is condensed into the search query, and the answer model reads it in full with its line breaks (§6) | `ChatRequest.cs:57-60`, `:140-142`; chat §3.2, §5.1 |
| `ume` | optional: `related_documents` 0–10 (documents to list, §5) and `follow_ups` 0–5 (suggestions, §5); `{}` gives only `outcome` and `degraded` (§3.5). Not an object or a count out of range → 400 `invalid_request`; an unknown member → 400 `unsupported_parameter`, `param` `ume.<name>`. Absent: no `ume` in the answer | `ChatRequest.cs:198-230`; chat §3.8 |
| Body | ≤ 2 MB | `src/Ume.Rag.Api/ChatEndpoints.cs:19` |
| History | the app sends the whole conversation every turn; it is the authoritative history. Ume.Rag uses the last `history.maxMessages` within `history.maxTokens` | chat §11.3 |
| `max_tokens` / `max_completion_tokens` | may lower, never raise, the assistant's limit | chat §3.2 |
| `temperature`, `top_p`, `user`, `metadata`, … | ignored | chat §3.2 |
| `tools`, JSON `response_format`, `n` > 1, `logprobs` | refused with 400 | `ChatRequest.cs:68-101` |
| Header `x-rag-conversation-id` | send back the value the first answer returned; a foreign or forged id is 404 `conversation_not_found` | `src/Ume.Rag.Api/ChatEndpoints.cs:130-152`; chat §11.1 |

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
  "usage": { "prompt_tokens": 5120, "completion_tokens": 180, "total_tokens": 5300 },
  "ume": { "outcome": "answered", "degraded": false,
    "related_documents": [
      { "document_id": "0193b0d2-c4f8-7c1e-9a3f-5e2d1b0a9c8d", "title": "VPN – Fel 809",
        "url": "https://intranat.umea.se/it/vpn/fel-809", "collection": "it-support", "rank": 1, "cited": true, "number": 1 },
      { "document_id": "0193a7e1-5b2c-7d40-8e11-2f6a9c0b4d7e", "title": "Felsökning av VPN-klienten",
        "url": "https://rag-api.umea.local/v1/documents/0193a7e1-5b2c-7d40-8e11-2f6a9c0b4d7e/content?grant=g1.…#page=3",
        "collection": "it-support", "rank": 2, "cited": false } ],
    "follow_ups": [ "Hur installerar jag om VPN-klienten?", "Vilka portar måste vara öppna för VPN?" ] } }
```

(Shape from `src/Ume.Rag.Api/ChatEndpoints.cs:182-208`, golden file `tests/Ume.Rag.Api.Tests/Golden/chat/json-ume-cited.txt`;
values illustrative, `ume` from chat §3.8.) `ume` is present only when the request sent one; it is written when the
follow-up call has returned or its 2-second budget has run out, so asking for follow-ups delays a JSON answer by at
most that. `id` is `chatcmpl-` + the message id,
used for feedback (§3.6). The response header `x-rag-conversation-id` carries the conversation id. Chat §3.3 also
plans an `x-request-id` header; none was found in the code, but every error body carries `request_id`.

### 3.4 Streaming

`text/event-stream`, one `data: {…}` line per event, in this order (`src/Ume.Rag.Api/ChatStreaming.cs:22-114`):

1. A role chunk: `delta: { "role": "assistant", "content": "" }`.
2. Optionally a caveat line as its own content chunk: degraded search, or a general-knowledge answer (chat §10.2,
   §10.4). The text comes from the platform, not the model.
3. Content chunks: `delta: { "content": "…" }`. A chunk that contains a citation marker also carries
   `delta.annotations` with `url_citation` objects; `start_index`/`end_index` index into the **whole** delivered
   content, not the chunk (`ChatStreaming.cs:109-118`).
4. For a general-knowledge answer, the assistant's contact links as a Markdown list.
5. A final chunk `delta: {}` with `finish_reason` (`stop`, or the gateway's reason when the answer was cut).
6. With `ume`: a chunk with `choices: []` and `ume` holding `outcome`, `degraded` and `related_documents`, written at
   once (no model call).
7. With `ume.follow_ups` ≥ 1: a second chunk with `choices: []` and `ume: { "follow_ups": […] }`, when the follow-up
   call returns or its budget (2 s) runs out. When follow-ups are skipped before any call (a fixed text, chat §9.8),
   `"follow_ups": []` is in the first `ume` chunk and there is no second one.
8. With `stream_options.include_usage`: one chunk with `choices: []` and `usage`.
9. `data: [DONE]`.

The tail of a streamed answer that asked for both (golden file
`tests/Ume.Rag.Api.Tests/Golden/chat/sse-ume-follow-ups.txt`, shortened):

```text
data: {"id":"chatcmpl-0193…","object":"chat.completion.chunk","created":1793952123,"model":"support","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

data: {"id":"chatcmpl-0193…","object":"chat.completion.chunk","created":1793952123,"model":"support","choices":[],"ume":{"outcome":"answered","degraded":false,"related_documents":[…]}}

data: {"id":"chatcmpl-0193…","object":"chat.completion.chunk","created":1793952123,"model":"support","choices":[],"ume":{"follow_ups":["…","…"]}}

data: {"id":"chatcmpl-0193…","object":"chat.completion.chunk","created":1793952123,"model":"support","choices":[],"usage":{"prompt_tokens":5120,"completion_tokens":180,"total_tokens":5300}}

data: [DONE]
```

Each `ume` member appears in exactly one chunk; the app merges them. Null members are omitted (the serializer ignores
nulls, `ChatStreaming.cs:26`), so non-final chunks have no `finish_reason` key. An error after the first byte is one
chunk `{"error":{"message":"The answer was interrupted.","type":"api_error","code":"stream_interrupted"}}` followed by
`[DONE]`, and no `ume` chunk (`ChatStreaming.cs:107-112`; golden file `sse-ume-mid-stream-error.txt`).

### 3.5 Not found, unavailable, general answers

These come back as ordinary completions; `ume.outcome` and `ume.degraded` tell them apart (send at least `"ume": {}`):

| Case | `ume.outcome` | `ume.degraded` | Content | Annotations | `related_documents` |
|---|---|---|---|---|---|
| Cited answer | `answered` | `false` | the model's answer | yes | listed |
| Degraded search (keyword only) | `answered` | `true` | the caveat line, then a normal cited answer | yes | listed |
| Not found (coverage `Low`, level `off` or `supplement`) | `not-found` | `false` | the assistant's not-found text in the question's language, then its contact links as a Markdown list; no model call, usage 0 | none | `[]` |
| General answer (`fallback`/`open`, coverage `Low`) | `answered-general` | `false` | the caveat line, the model's answer, the contact links | none | `[]` |
| Search unavailable | `unavailable` | `false` | a fixed "try again shortly" text plus contact links | none | `[]` |

(chat §3.8, §10; `src/Ume.Rag.Answering/Chat/ChatService.cs:103-121`; names in `src/Ume.Rag.Answering/Chat/ChatPlan.cs:22-28`;
golden files `json-ume-not-found.txt`, `sse-ume-degraded.txt`.) Suggested UI: offer the escalation route on
`not-found`, mark an `answered-general` answer as not resting on the knowledge base. A refusal is an HTTP error (§7)
and has no outcome.

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

Both come in `ume` when the request asks for them (§3.2):

| Member | What it holds | Rule |
|---|---|---|
| `related_documents` | up to N documents from the search the answer was built from, one entry per document, best first: `document_id`, `title`, `url` (as citations: the page's URL, or a grant link for uploads, §4), `collection`, `rank`, `cited`, and `number` (its `[n]` in the answer) when cited | only for `answered`; `[]` otherwise. No second search; the list holds only what this key's search through the assistant found (chat §9.7; `src/Ume.Rag.Answering/Chat/RelatedDocuments.cs:59-62`) |
| `follow_ups` | up to N short questions in the turn's language | for `answered` and `answered-general`; `[]` for fixed texts or when the call fails or takes over 2 s. Never stored (chat §9.8; `src/Ume.Rag.Answering/Chat/FollowUps.cs`) |

Suggested UI: show the entries with `cited: true` as "Sources" (with their `[n]`) and the rest as "Related"; show the
follow-ups as chips that send their text as the next user message. A clicked suggestion is an ordinary question.

### 5.1 Using the OpenAI SDKs

The OpenAI SDKs read the `ume` member and skip the `ume` chunks as they skip the usage chunk. Checked by replaying the
recorded golden answers to `openai` 3.46.0 (Python) and 7.53.0 (JS) (chat §22); a mid-stream error raises `APIError`
in both.

Python: send with `extra_body`, read with `model_extra`:

```python
stream = client.chat.completions.create(
    model="support", messages=messages, stream=True, stream_options={"include_usage": True},
    extra_body={"ume": {"related_documents": 5, "follow_ups": 3}})
ume = {}
for chunk in stream:
    if chunk.choices:
        render(chunk.choices[0].delta)
    if (extra := (chunk.model_extra or {}).get("ume")):
        ume.update(extra)

response = client.chat.completions.create(model="support", messages=messages, extra_body={"ume": {}})
outcome = response.model_extra["ume"]["outcome"]
```

JS: send `ume` as a request field, read `chunk.ume` and `response.ume`:

```js
const stream = await client.chat.completions.create({
  model: "support", messages, stream: true, stream_options: { include_usage: true },
  ume: { related_documents: 5, follow_ups: 3 },
});
const ume = {};
for await (const chunk of stream) {
  if (chunk.choices.length) render(chunk.choices[0].delta);
  if (chunk.ume) Object.assign(ume, chunk.ume);
}
```

In TypeScript the `ume` field is not in the SDK's types; cast the request and the chunk. The .NET SDK and the app's
own parser are not checked yet (chat §22).

## 6. Long tickets

Staff paste the whole ticket as the question, up to 16,000 characters (chat §3.2). Over 2,000 characters the condense
model writes the search query (chat §5.1); when it fails or takes over its budget, the first 2,000 characters, cut at
a word boundary, are searched instead (`src/Ume.Rag.Answering/Chat/Condenser.cs:150-171`). The answer model reads the
full text with its line breaks (`ChatRequest.cs:164-167`). A longer ticket gets 400 `question_too_long`: the app
checks the length before sending and tells the user, never cutting the text silently.

## 7. Errors and retries

| Status | `code` | App behaviour |
|---|---|---|
| 400 | `invalid_request`, `question_too_long`, `attachments_not_supported`, `unsupported_parameter`, `comment_not_stored`, `context_length_exceeded` | fix the request; show a plain message |
| 401 | `missing_key`, `invalid_key`, `key_expired`, `key_revoked`, … | configuration fault: alert the operators |
| 402 | `budget_exceeded` | the gateway budget is spent: alert the operators |
| 403 | `address_not_allowed` | the request did not come from an address in the key's `AllowedIpRanges` (as seen through the trusted proxies, §2): alert the operators |
| 403 | `client_disabled`, `permission_missing`, … | configuration fault |
| 404 | `model_not_found`, `conversation_not_found`, `message_not_found` | assistant unpublished or not in the client's audience; drop a stale conversation id |
| 429 | `rate_limited` | wait `Retry-After` seconds |
| 502 | `upstream_error` | retry once, then show an error |
| 503 | `config_unconfirmed`, `search_unavailable`, `upstream_unavailable` | wait `Retry-After`, retry |

(chat §3.6; `src/Ume.Rag.Api/ChatEndpoints.cs:224-260`; `address_not_allowed` from `src/Ume.Rag.Api/KnowledgeCallers.cs:64-71`.)

**Rate limits.** With one key, every staff member is the same subject, so the whole app shares one window per minute.
Its size is the key's `RequestsPerMinute` (§2; e.g. 200), or, when unset, the platform's
`Settings.Chat.messagesPerMinutePerUser` (20; `src/Ume.Rag.Api/ChatStreaming.cs:202-257`; chat §4.1 step 4). A new
value applies at once. Over it: 429 `rate_limited` with `Retry-After`. Downloads: 120 a minute per client address
(`src/Ume.Rag.Api/Program.cs:48-59`), the staff member's own address once the trusted proxies are set (§2).

## 8. Developing against Ume.Rag

- **Locally:** run Ume.Rag's AppHost with the FakeGateway ([`../../../docs/local-development.md`](../../../docs/local-development.md));
  sign in to the admin UI as a test platform admin, create an `integration` client and key, a small collection and an
  assistant. The FakeGateway answers deterministically, so the app's parsing and rendering can be tested without
  model cost.
- **API reference:** rag-api publishes an OpenAPI document and a Scalar page when `Knowledge:PublishApiReference` is
  on (default on; `src/Ume.Rag.Api/Program.cs:60-71`, `:138-144`). It is titled "Ume.Rag Knowledge API"; whether the
  chat routes appear in it is unverified. This file and chat §3 are the contract until then.
- **Test host:** the same routes as production, with the app's backend host routed by the operator.

## 9. Setup checklist

| # | Step | Who |
|---|---|---|
| 1 | App's OIDC client and a support-staff role in realm `ume` | realm administrator |
| 2 | Ume.Rag `integration` client; key scoped to the assistant, with `requestsPerMinute` (e.g. 200) and `allowedIpRanges` = the app backend's addresses | Ume.Rag platform admin |
| 3 | Assistant audience = the client | support department admin |
| 4 | Proxy and firewall: app backend → rag-api chat routes (2 MB body); staff network → `/v1/documents/*`; the proxy's addresses in `Settings.TrustedProxies` | operator, platform admin |
| 5 | `Knowledge:PublicBaseUrl` set; `Settings.Chat.allowedLinkHosts` includes the intranet host | operator, platform admin |
| 6 | App: server-side calls, streaming parser with the `ume` chunks, citation rendering, related documents, follow-up chips, outcome styling, feedback, the 16,000-character check, error handling | frontend team |
| 7 | Contract check on the test host: one cited answer with related documents and follow-ups (stream and JSON), one not-found, one long ticket, one uploaded-file download, one feedback, one call from an address outside the allow-list (403) | both teams |
