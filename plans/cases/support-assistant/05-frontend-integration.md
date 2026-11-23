# Frontend integration: the contract for the frontend team

The frontend is built by another team in its own repository. This file is what they build against: how the app
authenticates, which endpoints it calls, what comes back, and what Källa's operators set up. Everything below is
read from the code on `main` (2026-11-06; brought up to date on 2026-11-23, code cited by file and symbol); the binding
descriptions are chat §3 and knowledge §3–§4.

## 1. Architecture and authentication

```text
staff browser ──OIDC (Keycloak realm fors)─▶ frontend app (its own backend)
                                              │  Authorization: Bearer kalla-sk-…   (server side only)
                                              ▼
                                         kalla-api  /v1/models, /v1/chat/completions, /v1/feedback
staff browser ──────── citation links ──────▶ kalla-api  /v1/documents/{id}/content?grant=…   (staff network)
                └───── citation links ──────▶ intranet page URLs
```

| Rule | Why | Reference |
|---|---|---|
| The app calls kalla-api **from its own backend**, never from the browser | the key is a secret; kalla-api has no CORS (no `AddCors`/`UseCors` in `src/Kalla.Api/Program.cs`), so browser calls fail anyway | identity §6 |
| One Källa client of kind **`integration`** and one `kalla-sk-` key for it | `integration` accepts a secret key (`CredentialAllowed` in `src/Kalla.Domain/Access/AccessPolicy.cs`); the key is sent as `Authorization: Bearer …`, `x-api-key` or `api-key` (`KeyStore.ExtractKey` in `src/Kalla.Infrastructure/Keys/KeyStore.cs`) | identity §3 table, row "Integration with a key" (P1) |
| Staff sign in to **the app**, not to Källa | kalla-api accepts no user tokens today: the OAuth token path is designed but not built (identity §7.1 "Not built yet") | identity §7 |
| The app decides who may use it | Källa sees every request as the key (`SubjectId` = the key's id, `KnowledgeCallers.AuthenticateAsync` in `src/Kalla.Api/KnowledgeCallers.cs`; no header can name the end user), so audience by department or group does not apply to it (identity §4.3 step 10) | gap SA-5 |
| The rate limit is the app key's own | for a key subject the chat limit is the key's `RequestsPerMinute` when set, else `Settings.Chat.messagesPerMinutePerUser` (`ChatRateLimiter.LimitFor` in `src/Kalla.Api/ChatStreaming.cs`); the platform admin sets it on the app key (§2, §7) | chat §4.1 step 4 |
| Only the app's backend may use the key | the key's `AllowedIpRanges` are checked on every request, else 403 `address_not_allowed` (`KeyChecks.Refusal` in `src/Kalla.Domain/Services/KeyChecks.cs`, called by `KnowledgeCallers.AuthenticateAsync`). Behind the reverse proxy, kalla-api takes the client address from `X-Forwarded-For` only when the connection comes from one of `Settings.TrustedProxies` (`src/Kalla.Api/ClientAddressMiddleware.cs`) | identity §6.3 step 4 |

**Sign-in for the app.** The app gets its own OIDC client in Keycloak realm `fors` (the realm shared with the gateway;
Decisions "P1 pilot sign-in", "Gateway release 1 …"). It is the app's client, not one of RAG's: the realm's
administrator creates it, and a realm role or group for support staff lets the app check who may use it. Which
accounts exist and how support staff get them is the same open point as for Open WebUI (identity §5.3, "Who may sign
in … in P1 is not settled").

## 2. What Källa's admins set up

| Step | Who | How |
|---|---|---|
| Connect the app in one go: *Apps and keys* → **Add app** registers the client (kind `integration`, ceiling `internal`, `StoresHistory` true if the app keeps chats, then `ClientRetentionDays`; `PortalUrl` = the app's address), ticks the support assistant as what it may use (its audience), and creates the first key, in one transaction | platform admin | *Connect an app in one go* (admin §5.9.1); `POST /api/clients` in `src/Kalla.AdminApi/ClientEndpoints.cs` |
| The key: scoped to the support assistant (`Scopes.AssistantIds`), with an expiry, `requestsPerMinute` for the whole app (e.g. 200 for twenty staff) and `allowedIpRanges` = the app backend's addresses | platform admin | in the app's first key, or `POST /api/keys`, edited with `PUT /api/keys/{id}` (`src/Kalla.AdminApi/KeyEndpoints.cs`; `requestsPerMinute` 1–100,000); rotate with `POST /api/keys/{id}/rotate`, which copies both (`KeyRotation.Rotate` in `src/Kalla.Domain/Services/KeyRotation.cs`) |
| Hand the key over once, through a secret channel | platform admin → frontend team | never in a repository or ticket |
| The assistant's audience: the client, when not ticked above | department admin | the assistant's *Access* tab (`PUT /api/assistants/{id}/audiences`) |
| Trusted proxies: the reverse proxy's own addresses, so kalla-api sees the app backend's address and not the proxy's | operator, platform admin | `PUT /api/settings/network` `trustedProxies` (`src/Kalla.AdminApi/SettingsEndpoints.cs`; a `/0` network is refused, `NetworkRanges.IsEverything`). Without it every request comes from the proxy, and the key's allow-list and the download limit see only the proxy |
| Route the app's backend host to kalla-api | operator | reverse proxy and firewall: `/v1/models`, `/v1/chat/completions`, `/v1/feedback` from the app's backend only; `/v1/documents/*` from the staff network (identity §5.3; knowledge §4.6) |
| Absolute citation links | operator | `Knowledge:PublicBaseUrl` = kalla-api's staff-network address (set from `KALLA_API_PUBLIC_URL` on the `kalla-api` service in `deploy/compose.rag.yaml`) |
| Allow the chat body through the proxy | operator | kalla-api accepts 2 MB on `/v1/chat/completions` (`ChatEndpoints.BodyLimit` in `src/Kalla.Api/ChatEndpoints.cs`); the generated proxy configuration and the printed routes allow it since #552 ([`../../../docs/production-setup.md`](../../../docs/production-setup.md), "Reverse proxy and PKI"); a proxy configured before 0.3.1/0.4.0 needs `client_max_body_size 2m` on the api host |

The proxy and the firewall still restrict the routes; the key's allow-list is the second line, so a leaked key is
useless outside the app's backend.

## 3. Endpoints

All chat routes answer errors in the OpenAI shape and send `Cache-Control: no-store` (chat §3, §3.6).

### 3.1 `GET /v1/models`

The assistants this key may use (`GET /models` in `src/Kalla.Api/ChatEndpoints.cs`):

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
    "kalla": { "related_documents": 5, "follow_ups": 3, "escalation": true } }
```

| Rule | Value | Reference |
|---|---|---|
| `model` | the assistant's slug; unknown or not allowed → 404 `model_not_found` | chat §3.2 |
| `messages` | 1–1,000; the **last must be `user`**; `system`/`developer` messages are ignored (the assistant's instructions decide); text only, attachments → 400 `attachments_not_supported` | `ChatRequestParser.Parse` in `src/Kalla.Answering/Chat/ChatRequest.cs` |
| Last user message (the question) | **1–16,000 characters** after normalisation, else 400 `question_too_long`; other messages ≤ 32,000 characters. Paste the whole ticket: over 2,000 characters it is condensed into the search query, and the answer model reads it in full with its line breaks (§6) | `ChatRequestParser.MaxQuestionChars`, `MaxMessageChars`; chat §3.2, §5.1 |
| `kalla` | optional: `related_documents` 0–10 (documents to list, §5), `follow_ups` 0–5 (suggestions, §5), `escalation` `true`/`false` (the escalation hand-off, §5), `topics` (1–20 topic tags: the turn searches only chunks with one of them, inside the assistant's links, so it narrows and never widens; Decisions "Topics per request") and `general_knowledge` `off`/`supplement`/`fallback`/`open` (a stricter general-knowledge level for this turn, [`04-assistant.md`](04-assistant.md) §4: the turn runs at the stricter of it and the assistant's own, so it never loosens the assistant; absent or `null`: the assistant's level); `{}` gives only `outcome` and `degraded` (§3.5). Not an object, a count out of range, a value of the wrong type or another level value → 400 `invalid_request`, `param` `kalla.<name>`; an unknown member → 400 `unsupported_parameter`, `param` `kalla.<name>`. Absent: no `kalla` in the answer. Designed and queued, not yet accepted: `progress`, `timings` and `nearest` (chat §3.8, §9.10, §9.11; B-72, B-73) | `ChatRequestParser.Kalla`; chat §3.8 |
| Body | ≤ 2 MB | `ChatEndpoints.BodyLimit` |
| History | the app sends the whole conversation every turn; it is the authoritative history. Källa uses the last `history.maxMessages` within `history.maxTokens` | chat §11.3 |
| `max_tokens` / `max_completion_tokens` | may lower, never raise, the assistant's limit | chat §3.2 |
| `temperature`, `top_p`, `user`, `metadata`, … | ignored | chat §3.2 |
| `tools`, JSON `response_format`, `n` > 1, `logprobs` | refused with 400 | `ChatRequestParser.Parse` |
| Header `x-rag-conversation-id` | send back the value the first answer returned; a foreign or forged id is 404 `conversation_not_found` | `src/Kalla.Api/ChatEndpoints.cs`; chat §11.1 |

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
  "kalla": { "outcome": "answered", "degraded": false,
    "related_documents": [
      { "document_id": "0193b0d2-c4f8-7c1e-9a3f-5e2d1b0a9c8d", "title": "VPN – Fel 809",
                "url": "https://intranat.umea.se/it/vpn/fel-809", "collection": "it-support", "rank": 1, "cited": true, "number": 1,
        "modified": "2026-10-02T00:00:00+00:00", "checked": "2026-11-18T04:30:00+00:00" },
      { "document_id": "0193a7e1-5b2c-7d40-8e11-2f6a9c0b4d7e", "title": "Felsökning av VPN-klienten",
        "url": "https://kalla-api.umea.local/v1/documents/0193a7e1-5b2c-7d40-8e11-2f6a9c0b4d7e/content?grant=g1.…#page=3",
        "collection": "it-support", "rank": 2, "cited": false, "modified": "2026-09-14T08:00:00+00:00" } ],
        "follow_ups": [ "Hur installerar jag om VPN-klienten?", "Vilka portar måste vara öppna för VPN?" ],
    "escalation": null } }
```

(Shape from `src/Kalla.Api/ChatEndpoints.cs`, golden files `tests/Kalla.Api.Tests/Golden/chat/json-kalla-cited.txt` and
`json-kalla-follow-ups.txt`; values illustrative, `kalla` from chat §3.8.) `kalla` is present only when the request sent
one; it is written when the follow-up and escalation calls have returned or their 2-second budgets
(`Chat:FollowUpTimeout`, `Chat:EscalationTimeout`; they run at the same time) have run out, so asking for them delays a
JSON answer by at most that. `id` is `chatcmpl-` + the message id,
used for feedback (§3.6). The response header `x-rag-conversation-id` carries the conversation id. Chat §3.3 also
plans an `x-request-id` header; none was found in the code, but every error body carries `request_id`.

### 3.4 Streaming

`text/event-stream`, one `data: {…}` line per event, in this order (`ChatSse.ExecuteAsync` in
`src/Kalla.Api/ChatStreaming.cs`):

1. A role chunk: `delta: { "role": "assistant", "content": "" }`.
2. Optionally a caveat line as its own content chunk: degraded search, or a general-knowledge answer (chat §10.2,
   §10.4). The text comes from the platform, not the model.
3. Content chunks: `delta: { "content": "…" }`. A chunk that contains a citation marker also carries
   `delta.annotations` with `url_citation` objects; `start_index`/`end_index` index into the **whole** delivered
   content, not the chunk (`ChatSse.PiecesAsync`).
4. For a general-knowledge answer, the assistant's contact links as a Markdown list.
5. A final chunk `delta: {}` with `finish_reason` (`stop`, or the gateway's reason when the answer was cut).
6. With `kalla`: a chunk with `choices: []` and `kalla` holding `outcome`, `degraded`, `related_documents` and `topics`,
   written at once (no model call).
7. With `kalla.follow_ups` ≥ 1 or `kalla.escalation`: a second chunk with `choices: []` and
   `kalla: { "follow_ups": […], "escalation": {…} }`, when the follow-up and escalation calls return or their budgets
   (2 s each, run at the same time) run out. A member whose call is skipped before it starts (a fixed text for
   follow-ups, chat §9.8; `escalation: null` for `answered`, or a summary switched off, chat §9.9) is in the first
   `kalla` chunk, and when no call is left there is no second one.
8. With `stream_options.include_usage`: one chunk with `choices: []` and `usage`.
9. `data: [DONE]`.

The tail of a streamed answer that asked for both (golden file
`tests/Kalla.Api.Tests/Golden/chat/sse-kalla-follow-ups.txt`, shortened):

```text
data: {"id":"chatcmpl-0193…","object":"chat.completion.chunk","created":1793952123,"model":"support","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

data: {"id":"chatcmpl-0193…","object":"chat.completion.chunk","created":1793952123,"model":"support","choices":[],"kalla":{"outcome":"answered","degraded":false,"related_documents":[…]}}

data: {"id":"chatcmpl-0193…","object":"chat.completion.chunk","created":1793952123,"model":"support","choices":[],"kalla":{"follow_ups":["…","…"]}}

data: {"id":"chatcmpl-0193…","object":"chat.completion.chunk","created":1793952123,"model":"support","choices":[],"usage":{"prompt_tokens":5120,"completion_tokens":180,"total_tokens":5300}}

data: [DONE]
```

Each `kalla` member appears in exactly one chunk; the app merges them. Null members are omitted (the serializer ignores
nulls, `ChatSse`'s `Json` options), so non-final chunks have no `finish_reason` key. An error after the first byte is
one chunk `{"error":{"message":"The answer was interrupted.","type":"api_error","code":"stream_interrupted"}}` followed
by `[DONE]`, and no `kalla` chunk (`ChatSse.ExecuteAsync`; golden file `sse-kalla-mid-stream-error.txt`).

### 3.5 Not found, unavailable, general answers

These come back as ordinary completions; `kalla.outcome` and `kalla.degraded` tell them apart (send at least
`"kalla": {}`):

| Case | `kalla.outcome` | `kalla.degraded` | Content | Annotations | `related_documents` |
|---|---|---|---|---|---|
| Cited answer | `answered` | `false` | the model's answer | yes | listed |
| Degraded search (keyword only) | `answered` | `true` | the caveat line, then a normal cited answer | yes | listed |
| Not found (coverage `Low`, level `off` or `supplement`) | `not-found` | `false` | the assistant's not-found text in the question's language, then its contact links as a Markdown list; no model call, usage 0 | none | `[]` |
| General answer (`fallback`/`open`, coverage `Low`) | `answered-general` | `false` | the caveat line, the model's answer, the contact links | none | `[]` |
| Search unavailable | `unavailable` | `false` | a fixed "try again shortly" text plus contact links | none | `[]` |

(chat §3.8, §10; `src/Kalla.Answering/Chat/ChatService.cs`; names in `ChatOutcomes.Name` in
`src/Kalla.Answering/Chat/ChatPlan.cs`; golden files `json-kalla-not-found.txt`, `sse-kalla-degraded.txt`.) Suggested
UI: offer the escalation route on `not-found` (`kalla.escalation`, §5), mark an `answered-general` answer as not resting
on the knowledge base. A refusal is an HTTP error (§7) and has no outcome.

### 3.6 `POST /v1/feedback`

```json
{ "message_id": "chatcmpl-0193b0d2c4f87c1e9a3f5e2d1b0a9c8d", "rating": -1, "reason": "wrong_sources" }
```

`rating` 1 or −1; `reason` one of `incorrect`, `incomplete`, `wrong_sources`, `not_relevant`, `other`. 204 on success;
repeating it updates the rating. **Send no `comment`**: every assistant runs in history mode `none`, and a comment is
refused with 400 `comment_not_stored` (`src/Kalla.Api/FeedbackEndpoints.cs`). Ratings feed the admin's
feedback page and daily totals (admin §8.1).

### 3.7 Conversations

`GET/DELETE /v1/conversations[/{id}]` exist (chat §3.4) but return metadata only in P1 (no text is stored). The app
keeps its own chat history if it shows one, which makes it a `StoresHistory` client (§2; question 10).

## 4. Citations: opening and downloading the page (requirement 3)

Each `url_citation` has a `url` and a `title` (the label: title, last heading, pages, and the `modified` and `checked`
dates, "2026-03-02, kontrollerad 2026-11-18"; Decisions "Source dates on citations"; `Citations.Label` in
`src/Kalla.Answering/Chat/Citations.cs`). Markers in the text are renumbered `[1]`, `[2]`… in order of first citation;
one document keeps one number.

| Document | `url` | Opening it |
|---|---|---|
| Crawled page or crawled file | the page's or file's own URL (intranet), plus `#page=N` for PDFs | opens the live page in the staff browser |
| Uploaded file | `<Knowledge:PublicBaseUrl>/v1/documents/{id}/content?grant=g1.…`, plus `#page=N` for PDFs (`Citations.Link`; `src/Kalla.Answering/DownloadGrants.cs`) | PDF, PNG and JPEG open inline, other types download with their file name (`DocumentContentReader.ContentDisposition` in `src/Kalla.Answering/DocumentContentReader.cs`). Valid **7 days** (`internal`), re-checked on every use, reachable from the staff network only; an expired link shows a short "ask again" page (knowledge §4.6) |

The grant link also unlocks the document's metadata: `GET /v1/documents/{id}?grant=…` with the same grant returns title,
display name, media type, modified date, language and page count (`DocumentAsync` in
`src/Kalla.Api/KnowledgeEndpoints.cs`). The document id is the path segment of the link. Crawled pages have no grant and
no served content (`content_not_served`; serving crawled HTML from kalla-api's origin would be an XSS vector,
`src/Kalla.Answering/KnowledgeService.cs`).

Suggested UI: render the answer's Markdown, turn each `[n]` (by the annotation's indices) into a link, and list the
cited documents under the answer with their labels. Do not forward grant links outside the organisation: anyone on
the staff network who has one can open it for 7 days.

## 5. Related documents and follow-up suggestions (requirements 4 and 5)

Both come in `kalla` when the request asks for them (§3.2):

| Member | What it holds | Rule |
|---|---|---|
| `related_documents` | up to N documents from the search the answer was built from, one entry per document, best first: `document_id`, `title`, `url` (as citations: the page's URL, or a grant link for uploads, §4), `collection`, `rank`, `cited`, `number` (its `[n]` in the answer) when cited, `modified` (the source's own date) and `checked` (when a crawl last confirmed a crawled page; none for uploads) when known | only for `answered`; `[]` otherwise. No second search; the list holds only what this key's search through the assistant found (chat §9.7; Decisions "Source dates on citations"; `src/Kalla.Answering/Chat/RelatedDocuments.cs`) |
| `follow_ups` | up to N short questions in the turn's language | for `answered` and `answered-general`; `[]` for fixed texts or when the call fails or takes over 2 s. Never stored (chat §9.8; `src/Kalla.Answering/Chat/FollowUps.cs`) |
| `escalation` | `{ "reason", "contacts": [ { "title", "url" } ], "summary" }`: the turn's outcome as the reason, the assistant's not-found contact links, and a short hand-off summary of the user's problem for the second-line ticket, or `null` when the summary was skipped, failed or took over 2 s | for `not-found`, `answered-general` and `unavailable`; `null` for `answered`. Links removed and personal data masked in the summary; never stored or logged (chat §9.9; Decisions "Escalation hand-off"; `src/Kalla.Answering/Chat/Escalation.cs`) |
| `topics` | the asked topics that at least one of the assistant's links searched, in the request's order | only when `kalla.topics` was sent; a topic missing here matched no link (a typo or a removed topic) (chat §3.8) |

Suggested UI: show the entries with `cited: true` as "Sources" (with their `[n]`) and the rest as "Related"; show the
follow-ups as chips that send their text as the next user message. A clicked suggestion is an ordinary question.
Show the dates next to each source, and on an `escalation` the contacts with a *Copy summary* button for the
second-line ticket.

### 5.1 Using the OpenAI SDKs

The OpenAI SDKs read the `kalla` member and skip the `kalla` chunks as they skip the usage chunk. Checked by replaying
the recorded golden answers to `openai` 3.46.0 (Python) and 7.53.0 (JS) (chat §22); a mid-stream error raises `APIError`
in both.

Python: send with `extra_body`, read with `model_extra`:

```python
stream = client.chat.completions.create(
    model="support", messages=messages, stream=True, stream_options={"include_usage": True},
    extra_body={"kalla": {"related_documents": 5, "follow_ups": 3}})
kalla = {}
for chunk in stream:
    if chunk.choices:
        render(chunk.choices[0].delta)
    if (extra := (chunk.model_extra or {}).get("kalla")):
        kalla.update(extra)

response = client.chat.completions.create(model="support", messages=messages, extra_body={"kalla": {}})
outcome = response.model_extra["kalla"]["outcome"]
```

JS: send `kalla` as a request field, read `chunk.kalla` and `response.kalla`:

```js
const stream = await client.chat.completions.create({
  model: "support", messages, stream: true, stream_options: { include_usage: true },
    kalla: { related_documents: 5, follow_ups: 3 },
});
const kalla = {};
for await (const chunk of stream) {
  if (chunk.choices.length) render(chunk.choices[0].delta);
  if (chunk.kalla) Object.assign(kalla, chunk.kalla);
}
```

In TypeScript the `kalla` field is not in the SDK's types; cast the request and the chunk. The .NET SDK and the app's
own parser are not checked yet (chat §22).

## 6. Long tickets

Staff paste the whole ticket as the question, up to 16,000 characters (chat §3.2). Over 2,000 characters the condense
model writes the search query (chat §5.1); when it fails or takes over its budget, the first 2,000 characters, cut at a
word boundary, are searched instead (`Condenser.Fallback` in `src/Kalla.Answering/Chat/Condenser.cs`). The answer model
reads the full text with its line breaks (`ChatRequestParser.PromptForm`). A longer ticket gets 400 `question_too_long`:
the app checks the length before sending and tells the user, never cutting the text silently.

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

(chat §3.6; `ChatEndpoints.OpenAiAsync` and `Error` in `src/Kalla.Api/ChatEndpoints.cs`; `address_not_allowed` from
`KeyChecks.Refusal`.)

**Rate limits.** With one key, every staff member is the same subject, so the whole app shares one window per minute.
Its size is the key's `RequestsPerMinute` (§2; e.g. 200), or, when unset, the platform's
`Settings.Chat.messagesPerMinutePerUser` (20; `ChatRateLimiter` in `src/Kalla.Api/ChatStreaming.cs`; chat §4.1 step 4).
A new value applies at once. Over it: 429 `rate_limited` with `Retry-After`. Downloads: 120 a minute per client address
(`AddRateLimiter` with `KnowledgeEndpoints.DownloadsPolicy` in `src/Kalla.Api/Program.cs`), the staff member's own
address once the trusted proxies are set (§2).

## 8. Developing against Källa

- **Locally:** run Källa's AppHost with the FakeGateway
  ([`../../../docs/local-development.md`](../../../docs/local-development.md)); sign in to the admin UI as a test
  platform admin, create an `integration` client and key, a small collection and an assistant. The FakeGateway answers
  deterministically, so the app's parsing and rendering can be tested without model cost.
- **API reference:** kalla-api publishes an OpenAPI document and a Scalar page when `Knowledge:PublishApiReference` is
  on (default on; `src/Kalla.Api/Program.cs`). It is titled "Källa Knowledge API"; whether the
  chat routes appear in it is unverified. This file and chat §3 are the contract until then.
- **Test host:** the same routes as production, with the app's backend host routed by the operator.

## 9. Setup checklist

| # | Step | Who |
|---|---|---|
| 1 | App's OIDC client and a support-staff role in realm `fors` | realm administrator |
| 2 | Källa `integration` client, its audience and its key in one go (*Connect an app*, admin §5.9.1): the key scoped to the assistant, with `requestsPerMinute` (e.g. 200) and `allowedIpRanges` = the app backend's addresses | Källa platform admin |
| 3 | Assistant audience = the client, if not ticked in step 2 (the assistant's *Access* tab) | support department admin |
| 4 | Proxy and firewall: app backend → kalla-api chat routes (2 MB body); staff network → `/v1/documents/*`; the proxy's addresses in `Settings.TrustedProxies` | operator, platform admin |
| 5 | `Knowledge:PublicBaseUrl` set; `Settings.Chat.allowedLinkHosts` includes the intranet host | operator, platform admin |
| 6 | App: server-side calls, streaming parser with the `kalla` chunks, citation rendering with dates, related documents, follow-up chips, the escalation route and summary, outcome styling, feedback, the 16,000-character check, error handling | frontend team |
| 7 | Contract check on the test host: one cited answer with related documents and follow-ups (stream and JSON), one not-found with its escalation, one long ticket, one uploaded-file download, one feedback, one call from an address outside the allow-list (403) | both teams |
