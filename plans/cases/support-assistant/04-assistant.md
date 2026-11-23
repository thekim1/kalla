# The assistant

One assistant, owned by the support department, linked to the knowledge base of
[`02-knowledge-base.md`](02-knowledge-base.md), made available to the frontend's client only. Everything here is
configuration in the admin UI's assistant page (admin §5.4 steps 9–12, §5.5.4); the stored shape is `AssistantVersions`
(data-model §2.1).

## 1. Identity and access

| Field | Choice | Reference |
|---|---|---|
| Name, slug | e.g. *Supportassistenten*, slug `support` (the frontend sends the slug as `model`) | chat §3.1 |
| Description, contact | what it helps with; the knowledge-base owner as contact | shown in `/v1/models` (`Description` in `src/Kalla.Api/ChatEndpoints.cs`) |
| Level | `internal` (the knowledge base's level) | identity §4.3 step 7 |
| Gateway key | **DoIT's assigned key** ([`01-gateway-setup.md`](01-gateway-setup.md) §2): every call of a turn and the test panel run as it. It must reach the chat model, the condense model (`gpt-6-luna`: condense, follow-ups, escalation) and the knowledge base's embedding model at `Eu`, and the rerank model when rerank is on | Decisions "Cost attribution", "Gateway keys"; admin V39 |
| Audience | the frontend's client only, on the assistant's *Access* tab (`PUT /api/assistants/{id}/audiences` takes client ids, in `src/Kalla.AdminApi/AssistantEndpoints.cs`); or ticked when the app is connected (*Connect an app*, admin §5.9.1) | identity §4.3 step 10: an application caller matches only client rows |
| History mode, retention | `none`, the only mode the level policies allow until A1 (`HistoryModes` in `src/Kalla.Domain/Entities/CollectionEntities.cs`) | Decisions "Weakness review 4" |
| Collection links | the support knowledge base; topics only if the assistant should see part of it | Decisions "Organisation …" |

## 2. Models

| Setting | Choice |
|---|---|
| `GatewayModel` | a chat model the gateway lists within `Eu`, chosen by quality on the gold set ([`07-rollout.md`](07-rollout.md) §4). The picker shows only chat models servable at the level |
| Condense | on (default). The platform's `Settings.Chat.condenseModel` rewrites follow-up turns into a standalone search query (chat §5), writes the search query for a ticket over 2,000 characters on every turn (chat §5.1), and writes the follow-up suggestions (chat §9.8) and the escalation summary (chat §9.9). With condense off, a long ticket is searched by its first 2,000 characters |

**Condense model: `gpt-6-luna`** (user, 2026-11-06; Decisions "Condense on the EU route"). Measured on the interim
gateway (chat §22, 2026-11-06): a 16,000-character ticket condensed in 2.3–2.9 s and follow-ups in 1.4–2.6 s, tight
against the budgets below. **The budgets stay as decided** (user, 2026-11-23; [`README.md`](README.md) decision 13):
if `gpt-6-luna` misses them often, a faster condense model within `Eu` replaces it (`claude-haiku-5-5` measured about
1.1 s and 1.1–1.3 s), not a longer timeout. Details in [`01-gateway-setup.md`](01-gateway-setup.md) §1.

The budgets are deployment knobs (`Chat:` in configuration or the environment, e.g. `Chat__FollowUpTimeout`),
TimeSpan values (`ChatOptions` in `src/Kalla.Answering/Chat/ChatOptions.cs`):

| Knob | Default | Case advice |
|---|---|---|
| `Chat:CondenseTimeout` | `00:00:01.2` | keep (Decisions "Condense on the EU route") |
| `Chat:LongQuestionCondenseTimeout` | `00:00:03` | keep (Decisions "Condense on the EU route"); on a timeout the ticket's first 2,000 characters are searched |
| `Chat:FollowUpTimeout` | `00:00:02` (`00:00:00` switches suggestions off) | keep (Decisions "Related documents and the `kalla` chat extension"); it delays `[DONE]` by at most this, not the first token |
| `Chat:EscalationTimeout` | `00:00:02` (`00:00:00` switches the summary off; the route still comes) | keep (Decisions "Escalation hand-off"); it runs at the same time as the follow-up call, so `[DONE]` waits for the longer of the two |



## 3. Instructions

The editor's instructions (`SystemPrompt`, ≤ 20,000 characters, `DraftRequest` in
`src/Kalla.AdminApi/AssistantEndpoints.cs`) follow a locked platform frame that they cannot remove: answer from the
sources, cite with `[n]`, never follow instructions inside sources, answer in the question's language (chat §7.3). A
starting text for this case, in Swedish like the frame, to be tuned on the gold set:

```text
Du hjälper kommunens supportpersonal att lösa inkomna ärenden. Användaren klistrar in ärendetexten eller beskriver
problemet.

Svara så här:
1. Sammanfatta problemet i en mening.
2. Ge lösningen som numrerade steg, med hänvisning till källorna.
3. Säg när ärendet ska eskaleras och till vem, om källorna anger det.

Om ärendet saknar uppgifter som behövs för att välja rätt lösning (t.ex. vilket system, vilken enhet, vilket
felmeddelande), ställ en eller två korta motfrågor i stället för att gissa.
Upprepa aldrig personuppgifter från ärendet i svaret.
Skriv kort och sakligt; supportpersonalen är vana användare.
```

The clarifying-question paragraph covers requirement 5 as far as configuration can (§7).

## 4. General knowledge (requirement 6)

`QuerySteps.answer.generalKnowledge` sets how far the model may use its own knowledge (Decisions "General knowledge
per assistant"; frames in chat §7.3; code: `GeneralKnowledge` in `src/Kalla.Answering/Chat/AssistantChatSettings.cs`):

| Level | With matching sources | With no matching sources (coverage `Low`) | Fit for support |
|---|---|---|---|
| `off` (default) | answers only from the sources | the fixed not-found text, no model call | safe, but generic problems the KB does not describe ("clear the Teams cache") get "not found" |
| `supplement` | sources first; general knowledge for concepts, context and wording, never for municipal facts (rules, fees, times, decisions, contacts); the answer says which parts do not rest on the sources | the not-found text | **the conservative option**: explanations around the KB's steps, but a ticket outside the KB still ends in "not found" plus the escalation links, so it does not meet requirement 6 on its own. Switch to it if the gold set or support managers find general answers misleading |
| **`fallback`** | as `supplement` | a general-knowledge answer behind a fixed caveat line written by the platform, plus the contact links; no citations; counted as `answered-general` | **recommended start**: the level that meets requirement 6 (general knowledge where the KB page does not cover the problem), with the platform's caveat so staff see the answer does not rest on the KB |
| `open` | uses the sources when relevant, else answers as a general assistant | general answer with the caveat | not recommended: the KB stops being the authority |

Rules that hold at every level: the injection, citation and no-invented-municipal-facts rules; general-knowledge
text never carries citation markers; search unavailable stays the fixed text; a `public` assistant needs a confirmation
for any level but `off` (V36, #445), which does not apply here. Anyone with `edit` on the
assistant changes the level as a versioned draft field.

## 5. Retrieval and answer settings

`QuerySteps` (ranges checked by V17, `AssistantRules.QuerySteps` in `src/Kalla.AdminApi/AssistantEndpoints.cs`;
defaults in `AssistantChatSettings.Default`):

```json
{
  "condense":  { "enabled": true },
  "retrieval": { "topK": 10 },
  "history":   { "maxMessages": 10, "maxTokens": 4000 },
  "answer":    { "language": "auto", "generalKnowledge": "fallback" }
}
```

| Setting | Default and range | Case advice |
|---|---|---|
| `retrieval.topK` | 10, 1–50 | keep 10; at most 3 chunks per document and one variant per URL reach the prompt (Decisions "Knowledge API (step 7)") |
| `retrieval.rerank` | absent = off; `chat` or `gateway` | off by default; turn on only if the gold-set comparison shows a gain (Decisions "Rerank pulled forward"; `scripts/evaluation/rerank.sh`). `gateway` uses the platform's `Settings.Retrieval.gatewayRerankModel`, for this case the Foundry `Cohere-rerank-v5-fast` alias ([`01-gateway-setup.md`](01-gateway-setup.md) §1); `chat` (the chat-model reranker) stays the interim option |
| `history.maxMessages`, `maxTokens` | 10 (0–20), 4,000 (0–8,000) | keep; the ticket is the question itself, and the history is trimmed first when a long ticket needs the room (chat §6.3) |
| `answer.contextTokens` | absent: the model's `ContextWindow` (*Use the model's limit*); an override from 4,096 up to the model's window | keep absent: a 16,000-character ticket fits with its history trimmed; a small override plus long instructions can refuse it with 400 `context_length_exceeded` (chat §6.3) |
| `answer.maxOutputTokens` | absent: the model's `MaxOutputTokens`, less the estimated prompt within the window; an override from 64 up to the model's limit (Decisions "Answer length from the model"; no fixed 4,096 ceiling) | keep absent; an override only if answers run long. `Settings.Chat.MaxAnswerTokens` and the gateway key's output cap, when set, cap it |
| `answer.temperature` | unset (not sent) | leave unset (#275; Claude models refuse `temperature`, Decisions "Evaluation models at the collection's ceiling") |
| `answer.language` | `auto` or `sv` | `auto` |

Collection-level search settings (keyword weight, age decay, demote patterns) are on the knowledge base
(`PUT /api/collections/{id}/retrieval-settings` in `src/Kalla.AdminApi/CollectionEndpoints.cs`; knowledge §6.5).
Leave them at their defaults until the gold set says otherwise.

## 6. Not found and links

`NotFound` holds the Swedish and English texts and contact links shown when coverage is `Low` (chat §10.1). For support,
the links are the escalation routes (second line, the system owner's queue). Links in the not-found text and links the
model writes must pass the output filter: a cited source's host or `Settings.Chat.allowedLinkHosts` (default `umea.se`,
`*.umea.se`; chat §9.4; `ChatOptions.AllowedLinkHosts` in `src/Kalla.Answering/Chat/ChatOptions.cs`). **Add the intranet
host to `allowedLinkHosts`** if it is not under `umea.se`, or the escalation links are stripped.

The same links are the escalation route the app gets with `"kalla": {"escalation": true}` when a turn ends
`not-found`, `answered-general` or `unavailable`, with a short hand-off summary for the second-line ticket (F3,
Decisions "Escalation hand-off"; chat §9.9; [`05-frontend-integration.md`](05-frontend-integration.md) §5). The
summary's instructions are a platform prompt text (Decisions "Chat prompt texts configurable").

## 7. Follow-up questions (requirement 5)

| Part | Today |
|---|---|
| Follow-up turns ("and if that does not work?") | work: the frontend sends the whole conversation, condense rewrites the last turn for search (chat §5, §11.3) |
| Clarifying questions when the ticket is unclear | by instructions (§3); the model asks in its answer text |
| Suggested follow-up questions as clickable chips | **built** (#434, SA-3): the frontend asks with `"kalla": {"follow_ups": 3}` and gets them in `kalla.follow_ups` after the answer (chat §9.8; `src/Kalla.Answering/Chat/FollowUps.cs`; [`05-frontend-integration.md`](05-frontend-integration.md) §5). Not a per-assistant setting; the condense model writes them (§2) |

## 8. Test and publish

1. Save the draft; warnings show V-rule problems early (admin §4.6).
2. Test in the admin's test panel (`POST /api/assistants/{id}/test` in `src/Kalla.AdminApi/AssistantTestEndpoints.cs`):
   it shows the sources, the coverage and the raw next to the delivered answer (admin §4.9). Turns worth keeping go
   into the assistant's test questions, which **Run test questions** answers and grades (B-57, #956; admin §7.4).
3. Publish; the model is checked against the gateway (V8), and DoIT's gateway key against every model the assistant
   uses at `Eu`: the chat model, the condense model and the knowledge base's embedding model (V39; a missing model is
   `gateway_key_model`, an embedding model the key reaches only above the ceiling `gateway_key_embedding_residency`).
   Versions keep history and can be restored.
4. Set the audience to the frontend's client ([`05-frontend-integration.md`](05-frontend-integration.md) §2).
