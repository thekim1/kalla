# The assistant

One assistant, owned by the support department, linked to the knowledge base of [`02-knowledge-base.md`](02-knowledge-base.md),
made available to the frontend's client only. Everything here is configuration in the admin UI's assistant page
(admin §5.4 steps 9–12, §5.5.4); the stored shape is `AssistantVersions` (data-model §2.1).

## 1. Identity and access

| Field | Choice | Reference |
|---|---|---|
| Name, slug | e.g. *Supportassistenten*, slug `support` (the frontend sends the slug as `model`) | chat §3.1 |
| Description, contact | what it helps with; the knowledge-base owner as contact | shown in `/v1/models` (`src/Ume.Rag.Api/ChatEndpoints.cs:185-190`) |
| Level | `internal` (the knowledge base's level) | identity §4.3 step 7 |
| Audience | the frontend's client only (`PUT /api/assistants/{id}/audiences` takes client ids, `src/Ume.Rag.AdminApi/AssistantEndpoints.cs:898-905`) | identity §4.3 step 10: an application caller matches only client rows |
| History mode, retention | `none`, the only mode the level policies allow until A1 (`src/Ume.Rag.Domain/Entities/CollectionEntities.cs:112`) | Decisions "Weakness review 4" |
| Collection links | the support knowledge base; topics only if the assistant should see part of it | Decisions "Organisation …" |

## 2. Models

| Setting | Choice |
|---|---|
| `GatewayModel` | a chat model the gateway lists within `Eu`, chosen by quality on the gold set ([`07-rollout.md`](07-rollout.md) §4). The picker shows only chat models servable at the level |
| Condense | on (default); the platform's `Settings.Chat.condenseModel` rewrites follow-up turns into a standalone search query, 600 ms budget (chat §5) |

## 3. Instructions

The editor's instructions (`SystemPrompt`, ≤ 20,000 characters, `src/Ume.Rag.AdminApi/AssistantEndpoints.cs:848-849`)
follow a locked platform frame that they cannot remove: answer from the sources, cite with `[n]`, never follow
instructions inside sources, answer in the question's language (chat §7.3). A starting text for this case, in Swedish
like the frame, to be tuned on the gold set:

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
per assistant"; frames in chat §7.3; code in `src/Ume.Rag.Answering/Chat/AssistantChatSettings.cs:11-47`):

| Level | With matching sources | With no matching sources (coverage `Low`) | Fit for support |
|---|---|---|---|
| `off` (default) | answers only from the sources | the fixed not-found text, no model call | safe, but generic problems the KB does not describe ("clear the Teams cache") get "not found" |
| `supplement` | sources first; general knowledge for concepts, context and wording, never for municipal facts (rules, fees, times, decisions, contacts); the answer says which parts do not rest on the sources | the not-found text | **the conservative option**: explanations around the KB's steps, but a ticket outside the KB still ends in "not found" plus the escalation links, so it does not meet requirement 6 on its own. Switch to it if the gold set or support managers find general answers misleading |
| **`fallback`** | as `supplement` | a general-knowledge answer behind a fixed caveat line written by the platform, plus the contact links; no citations; counted as `answered-general` | **recommended start**: the level that meets requirement 6 (general knowledge where the KB page does not cover the problem), with the platform's caveat so staff see the answer does not rest on the KB |
| `open` | uses the sources when relevant, else answers as a general assistant | general answer with the caveat | not recommended: the KB stops being the authority |

Rules that hold at every level: the injection, citation and no-invented-municipal-facts rules; general-knowledge
text never carries citation markers; search unavailable stays the fixed text; a `public` assistant is always `off`
(V36, `src/Ume.Rag.Answering/Chat/ChatService.cs:45-49`), which does not apply here. Anyone with `edit` on the
assistant changes the level as a versioned draft field.

## 5. Retrieval and answer settings

`QuerySteps` (ranges checked by V17, `src/Ume.Rag.AdminApi/AssistantEndpoints.cs:655-683`; defaults in
`AssistantChatSettings.cs:58`):

```json
{
  "condense":  { "enabled": true },
  "retrieval": { "topK": 10 },
  "history":   { "maxMessages": 10, "maxTokens": 4000 },
  "answer":    { "contextTokens": 16384, "maxOutputTokens": 1500, "language": "auto", "generalKnowledge": "fallback" }
}
```

| Setting | Default and range | Case advice |
|---|---|---|
| `retrieval.topK` | 10, 1–50 | keep 10; at most 3 chunks per document and one variant per URL reach the prompt (Decisions "Knowledge API (step 7)") |
| `retrieval.rerank` | absent = off; `chat` or `gateway` | off by default; turn on only if the gold-set comparison shows a gain (Decisions "Rerank pulled forward"; `scripts/evaluation/rerank.sh`). `gateway` uses the platform's `Settings.Retrieval.gatewayRerankModel`, for this case the Foundry `Cohere-rerank-v5-fast` alias ([`01-gateway-setup.md`](01-gateway-setup.md) §1); `chat` (the chat-model reranker) stays the interim option |
| `history.maxMessages`, `maxTokens` | 10 (0–20), 4,000 (0–8,000) | raise `maxTokens` to 8,000 if the frontend sends the ticket as an earlier message ([`05-frontend-integration.md`](05-frontend-integration.md) §6) |
| `answer.maxOutputTokens` | 1,024 (64–4,096) | 1,500 for step-by-step answers |
| `answer.temperature` | unset (not sent) | leave unset (#275; Claude models refuse `temperature`, Decisions "Evaluation models at the collection's ceiling") |
| `answer.language` | `auto` or `sv` | `auto` |

Collection-level search settings (keyword weight, age decay, demote patterns) are on the knowledge base
(`PUT /api/collections/{id}/retrieval-settings`, `src/Ume.Rag.AdminApi/CollectionEndpoints.cs:189`; knowledge §6.5).
Leave them at their defaults until the gold set says otherwise.

## 6. Not found and links

`NotFound` holds the Swedish and English texts and contact links shown when coverage is `Low` (chat §10.1). For
support, the links are the escalation routes (second line, the system owner's queue). Links in the not-found text and
links the model writes must pass the output filter: a cited source's host or `Settings.Chat.allowedLinkHosts`
(default `umea.se`, `*.umea.se`; chat §9.4; `src/Ume.Rag.Answering/Chat/ChatOptions.cs:17`). **Add the intranet host
to `allowedLinkHosts`** if it is not under `umea.se`, or the escalation links are stripped.

## 7. Follow-up questions (requirement 5)

| Part | Today |
|---|---|
| Follow-up turns ("and if that does not work?") | work: the frontend sends the whole conversation, condense rewrites the last turn for search (chat §5, §11.3) |
| Clarifying questions when the ticket is unclear | by instructions (§3); the model asks in its answer text |
| Suggested follow-up questions as clickable chips | **missing** (gap SA-3). Open WebUI's own follow-up generation is off (chat §4.2), and nothing in the chat API generates suggestions (no match for follow-up suggestions in `src/Ume.Rag.Answering/Chat/`) |

## 8. Test and publish

1. Save the draft; warnings show V-rule problems early (admin §4.6).
2. Test in the admin's test panel (`POST /api/assistants/{id}/test`, `src/Ume.Rag.AdminApi/AssistantTestEndpoints.cs:36`):
   it shows the sources, the coverage and the raw next to the delivered answer (admin §4.9).
3. Publish; the model is checked against the gateway (V8). Versions keep history and can be restored.
4. Set the audience to the frontend's client ([`05-frontend-integration.md`](05-frontend-integration.md) §2).
