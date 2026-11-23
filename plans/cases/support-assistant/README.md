# Case: a support assistant for ticket handling

Status: **case plan, non-binding** (main session, 2026-11-06; brought up to date with `v0.9.2` on 2026-11-23). It
applies the platform to one use; it does not change the platform. The decisions
([`../../decisions.md`](../../decisions.md)) and the design documents ([`../../README.md`](../../README.md)) bind and
win where this plan differs from them. Every claim about existing behaviour names the file it was checked in;
"unverified" marks what was not.

## The case

Internal municipal support staff get help with incoming tickets from a chat in an app of their own:

1. A collection of documents, uploaded or crawled, becomes a new knowledge base.
2. A purpose-built frontend, **built by a separate team in their own repository**, where staff paste or type the
   ticket text and chat about it.
3. Answers cite the real knowledge-base pages; the whole page can be opened or downloaded.
4. The top matching or related documents are listed next to the answer.
5. Follow-up questions are suggested when something is unclear (wanted, not required).
6. Configurable strictness: the model may fill in with general knowledge where the knowledge base is silent.
7. When a document is added or edited, the index (parsing, chunks, embeddings) follows automatically.

User's answers (2026-11-06): the sources are intranet/web pages, Word and PDF files, and Markdown files; the frontend
is the other team's; tickets come in by chat only (staff paste the text); ticket-system integration is "later".

## Scope

| In | Out |
|---|---|
| Gateway setup for the case's models and keys | Building the frontend (the other team) |
| One department-owned knowledge base: intranet crawl, uploaded Word/PDF/Markdown | Ticket-system integration (read a ticket, write back): [`07-rollout.md`](07-rollout.md) "Later" |
| One assistant tuned for ticket solving | SharePoint as a source (P2, connectors §14) |
| The contract the frontend team builds against | Public or citizen use |
| Keeping the knowledge base current | Changes to the binding designs (they go to the main session as gaps) |
| Evaluation, pilot and go-live | |

## Where it stands (2026-11-23)

**No platform code is missing for the pilot.** Every gap chosen before the pilot is built and released since `v0.2.0`:
related documents and the outcome (#433), follow-up suggestions (#434), long tickets (#431), the key's own rate limit
(#430) and IP allow-list (#432). Since the first draft the platform also gained what this case can use: JavaScript
rendering for intranet pages built in the browser (connectors §8), politeness profiles per host (#461), topics on
uploads (#481), restoring a deleted file (#489), legacy Office uploads (connectors §9.2), test questions per assistant
(B-57, #956), and three of the support chatbot features
([`../../proposals/support-chatbot-features.md`](../../proposals/support-chatbot-features.md)), released in `v0.9.0`:
the escalation hand-off `kalla.escalation` (F3), the dates on citations and related documents (F4) and narrowing by
topic per request `kalla.topics` (F8). Queued for stream b: cost per outcome (F10, B-61), the content-gap report (F7,
B-62), downvotes per document (F6, B-66), and `kalla.progress`, `kalla.timings` and `kalla.nearest` (B-72, B-73).

Every assistant and knowledge base runs on an **assigned gateway key** of its department (Decisions "Cost
attribution", "Gateway keys"): this case runs on DoIT's ([`01-gateway-setup.md`](01-gateway-setup.md) §2).

What is left is configuration and people ([`07-rollout.md`](07-rollout.md)): the source inventory and the documents,
the test host, the DPIA (G1), the named people and the gold set from real tickets, and the frontend team's app. A
**proof of concept on the local stack** can start before all of that, with only the documents and a few anonymised
tickets ([`07-rollout.md`](07-rollout.md) §0b).

## Fit summary

| # | Need | Today | What it takes | Where |
|---|---|---|---|---|
| 1 | Ingest by crawl or upload | **Works.** Crawl and URL-list sources, upload sources with PDF, DOCX/PPTX/XLSX, legacy `.doc`/`.xls`/`.ppt`/`.rtf` (converted by LibreOffice), ODF, images, TXT/**MD**/CSV, HTML (`UploadEndpoints.Allowed` in `src/Kalla.AdminApi/UploadEndpoints.cs`) | Configuration. The crawler fetches intranet hosts only inside the `KALLA_CRAWL_ALLOW` ranges and never sends cookies or credentials (`UseCookies = false` in `src/Kalla.Worker/Connectors/Egress/CrawlHttpClient.cs`), so an intranet behind a login cannot be crawled (gap SA-8). Crawled legacy Office files and uploaded `.eml`/`.msg` wait for P2 (connectors §9.2). Pages built in JavaScript: `fetchMode` `browser` (connectors §8) | [`02`](02-knowledge-base.md) |
| 2 | Own frontend app | **Works for a server-side caller.** OpenAI-compatible `GET /v1/models` and `POST /v1/chat/completions` accept a client of kind `integration` with a `kalla-sk-` key (`CredentialAllowed` in `src/Kalla.Domain/Access/AccessPolicy.cs`) | Configuration: client, key with its own rate limit and IP allow-list (*Connect an app*, admin §5.9.1), trusted proxies, proxy route. No CORS (by design: the key stays server-side). No per-user identity: every staff member is the same key subject (gap SA-5) | [`05`](05-frontend-integration.md) |
| 3 | Citations link to real pages; open or download | **Works.** A crawled document's citation is its page URL; an uploaded one's is a signed download link, `#page=` added for PDFs (`Citations.Link` in `src/Kalla.Answering/Chat/Citations.cs`); each carries its `modified` and `checked` dates (Decisions "Source dates on citations") | Configuration: `Knowledge:PublicBaseUrl`, the proxy opens `/v1/documents/*` to the staff network. Uploaded Markdown and Word download as files; there is no rendered view (gap SA-12) | [`05`](05-frontend-integration.md) |
| 4 | Top or related documents | **Built** (#433, SA-1). `kalla.related_documents` lists the documents the answer's own search found, cited and uncited, with links as for citations (chat §9.7; `src/Kalla.Answering/Chat/RelatedDocuments.cs`) | The frontend asks for them (`"kalla": {"related_documents": 5}`) | [`05`](05-frontend-integration.md) |
| 5 | Follow-up suggestions | **Built** (#434, SA-3). `kalla.follow_ups` from one call on the condense model after the answer (chat §9.8; `src/Kalla.Answering/Chat/FollowUps.cs`); clarifying questions stay in the instructions | The frontend asks for them (`"kalla": {"follow_ups": 3}`); the condense model is `gpt-6-luna` (Decisions "Condense on the EU route") | [`04`](04-assistant.md), [`05`](05-frontend-integration.md) |
| 6 | Strictness of general knowledge | **Works.** `QuerySteps.answer.generalKnowledge` `off`/`supplement`/`fallback`/`open` per assistant version (Decisions "General knowledge per assistant"; `src/Kalla.Answering/Chat/AssistantChatSettings.cs`) | Configuration: `fallback` meets the requirement (general answers where the knowledge base is silent); `supplement` is the conservative option | [`04`](04-assistant.md) |
| 7 | Automatic updates | **Works for crawled pages, manual for files.** Scheduled crawls with content-hash change detection; only changed content is re-parsed and re-embedded; guarded deletions (connectors §12, §13). Uploads change only by an admin's replace in the admin UI; a deleted file can be restored for 14 days (#489) | Configuration (daily schedule). No push trigger, scripted upload API or folder/Git sync (gaps SA-9, SA-10, SA-14) | [`03`](03-updates.md) |
| – | Ticket text as the question | **Built** (#431, SA-4). The question may be 16,000 characters (`ChatRequestParser.MaxQuestionChars` in `src/Kalla.Answering/Chat/ChatRequest.cs`); over 2,000 the condense model writes the search query, and the answer model reads the whole ticket (chat §5.1) | None: staff paste the ticket | [`05`](05-frontend-integration.md) §6 |

In short: points 1, 3 and 6 work with configuration, 2 and 7 work with configuration and known limits, 4 and 5 are built
and the frontend asks for them with `kalla`, and long tickets are accepted as they are. The app's shared Källa key gets
its own rate limit (SA-6, #430) and IP allow-list (SA-7, #432). The rest is configuration and people.

## Reading order

1. [`01-gateway-setup.md`](01-gateway-setup.md): models, keys, residency ceiling, personal-data policy.
2. [`02-knowledge-base.md`](02-knowledge-base.md): the knowledge base, its sources and settings.
3. [`03-updates.md`](03-updates.md): how edits, additions and deletions reach the index.
4. [`04-assistant.md`](04-assistant.md): the assistant's instructions, general-knowledge level and retrieval.
5. [`05-frontend-integration.md`](05-frontend-integration.md): the contract for the frontend team.
6. [`06-gaps.md`](06-gaps.md): what the platform lacks for this case, sized and assigned.
7. [`07-rollout.md`](07-rollout.md): the order of work from setup to go-live, and what comes later.

## Decisions (user, 2026-11-06)

The open questions of the first draft, answered; rows 4, 12 and 13 as the user changed or added them on 2026-11-23:

| # | Question | Answer | Consequence |
|---|---|---|---|
| 1 | Owning department | **DoIT**, the pilot department | Runs inside P1b; no second department, no gateway release 2 needed |
| 2 | Intranet readable without a login | **Yes** | The crawl works as is; SA-8 is not needed |
| 3 | Level | **`internal`** | EU-hosted models; no on-prem requirement |
| 4 | Personal data in pasted tickets | **Instructions and DPIA**: staff are told not to paste personal data; the DPIA (G1) covers the EU-hosted model. **Detect and log, no redaction** on DoIT's assigned gateway key (PII policy `Allow`), reviewed after the test chatbot's first weeks (user, 2026-11-23) | [`01-gateway-setup.md`](01-gateway-setup.md) §4; the frontend shows the instruction |
| 5 | General-knowledge level | **`fallback`** | [`04-assistant.md`](04-assistant.md) §4 |
| 6 | Related documents and follow-up suggestions | **Both built before the pilot** (SA-1, SA-3) | Chat design §3.8, §9.7, §9.8; built in #433 (M-7, with the outcome of SA-2) and #434 (M-8) |
| 7 | Per-user identity | **One shared app key** for the pilot | SA-5 waits; the per-key limit `Keys.RequestsPerMinute` (SA-6) is built in #430 (M-5), the key's IP allow-list (SA-7, #424) in #432 |
| 8 | Where the Markdown files live | **Local now, a Git repository later** | Upload by hand for the pilot; SA-10 (Git connector) **after the pilot** |
| 10 | Conversation history | **The frontend keeps it** and sends it each turn; Källa stores none (`none`, as today) | No A1 dependency; the frontend's store belongs in the DPIA |
| 11 | Ticket length | **Varies; fix the hard 2,000-character limit before the pilot** (SA-4) by **condensing a long ticket into a search query**: long messages are accepted (up to the chat request's own limit), a task model writes the search query, and the answer model gets the full text | Chat design §3.2, §5.1; built in #431 (M-6) |
| 12 | Rerank model | **`Cohere-rerank-v5-fast`** on Azure AI Foundry (EU), this deployment's setting (user, 2026-11-06, #421). Decisions "Rerank pulled forward" names no model: which rerank model the gateway serves at `Eu` is the instance's choice (user, 2026-11-23) | [`01-gateway-setup.md`](01-gateway-setup.md) §1; rerank stays off until the gold set shows a gain |
| 13 | Condense budgets with `gpt-6-luna` | **The decided budgets stay**: the long-ticket condense within 3 s (Decisions "Condense on the EU route"), follow-ups and the escalation summary within 2 s each (Decisions "Related documents and the `kalla` chat extension", "Escalation hand-off"). If `gpt-6-luna` is too slow for them, a faster condense model within `Eu` (such as `claude-haiku-5-5`) replaces it, not a longer timeout (user, 2026-11-23) | [`01-gateway-setup.md`](01-gateway-setup.md) §1; [`04-assistant.md`](04-assistant.md) §2 |

## Before the pilot

The gaps chosen before the pilot are built. What remains is configuration and people
([`07-rollout.md`](07-rollout.md)):

- **Configuration:** DoIT's assigned gateway key on the knowledge base and the assistant, reaching the chat, embedding
  and condense models at `Eu` (V39; [`01-gateway-setup.md`](01-gateway-setup.md) §2); the app's `integration` client
  and key with `requestsPerMinute` (e.g. 200) and `allowedIpRanges` = the app backend's addresses;
  `Settings.TrustedProxies` = the reverse proxy's addresses; the proxy route with a 2 MB chat body (in the generated
  configuration since #552); `Settings.Chat.condenseModel` `gpt-6-luna` with the default budgets
  (`Chat:LongQuestionCondenseTimeout` 3 s, `Chat:FollowUpTimeout` and `Chat:EscalationTimeout` 2 s;
  [`04-assistant.md`](04-assistant.md) §2); the knowledge base, the assistant and its audience.
- **People:** the DPIA (G1) for pasted tickets and the app's store; the named editor, department admin, frontend
  contact and reviewers; the gold set from real tickets; the frontend team's build against
  [`05-frontend-integration.md`](05-frontend-integration.md).

## Still open

- **The source inventory** (rollout 0.5): which intranet sections, which folders of Word, PDF and Markdown files, and
  whether the intranet is reachable from where the proof of concept runs.
- **Suggestions before the pilot** (main session, 2026-11-08): SA-13 (`.markdown` uploads) only if the Markdown files
  use that extension. SA-15 is done (#552).
- **Legacy Office files** (`.doc`, `.xls`, `.ppt`, `.rtf`) in the collection? Uploads take them (converted by the
  parser's LibreOffice, connectors §9.2); crawled ones end `http.format_deferred` until P2 (postponed item Z1), so
  legacy files linked from the intranet are uploaded instead.
