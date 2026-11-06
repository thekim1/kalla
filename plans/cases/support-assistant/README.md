# Case: a support assistant for ticket handling

Status: **case plan, non-binding** (main session, 2026-11-06). It applies the platform to one use; it does not change
the platform. The Decisions table ([`../../../01-general-idea.md`](../../../01-general-idea.md) §10) and the design
documents ([`../../README.md`](../../README.md)) bind and win where this plan differs from them. Every claim about
existing behaviour names the file it was checked in; "unverified" marks what was not.

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

## Fit summary

| # | Need | Today | What it takes | Where |
|---|---|---|---|---|
| 1 | Ingest by crawl or upload | **Works.** Crawl and URL-list sources, upload sources with PDF, DOCX/PPTX/XLSX, ODF, images, TXT/**MD**/CSV, HTML (`src/Ume.Rag.AdminApi/UploadEndpoints.cs:36-39`) | Configuration. The crawler fetches intranet hosts only inside `--crawl-allow` ranges and never sends cookies or credentials (`src/Ume.Rag.Worker/Connectors/Egress/CrawlHttpClient.cs:33`), so an intranet behind a login cannot be crawled (gap SA-8). Legacy `.doc` is refused until P2 (connectors §9.2) | [`02`](02-knowledge-base.md) |
| 2 | Own frontend app | **Works for a server-side caller.** OpenAI-compatible `GET /v1/models` and `POST /v1/chat/completions` accept a client of kind `integration` with a `ume-rag-sk-` key (`src/Ume.Rag.Domain/Access/AccessPolicy.cs:203-211`) | Configuration: client, key, assistant audience, proxy route. No CORS (by design: the key stays server-side). No per-user identity: every staff member is the same key subject (gaps SA-5, SA-6) | [`05`](05-frontend-integration.md) |
| 3 | Citations link to real pages; open or download | **Works.** A crawled document's citation is its page URL; an uploaded one's is a signed download link, `#page=` added for PDFs (`src/Ume.Rag.Answering/Chat/Citations.cs:66-73`) | Configuration: `Knowledge:PublicBaseUrl`, the proxy opens `/v1/documents/*` to the staff network. Uploaded Markdown and Word download as files; there is no rendered view (gap SA-12) | [`05`](05-frontend-integration.md) |
| 4 | Top or related documents | **Missing for this client.** The chat answer carries only cited documents; `/v1/retrieve` is open only to `internal` clients (knowledge §2.3) and refuses `assistant_id` from others (`src/Ume.Rag.Api/KnowledgeEndpoints.cs:251-254`) | Code (gap SA-1) | [`06`](06-gaps.md) |
| 5 | Follow-up suggestions | **Missing.** No suggestion feature; Open WebUI's follow-up generation is switched off (chat §4.2) | Clarifying questions by instructions now (configuration); suggestions are code (gap SA-3) | [`04`](04-assistant.md), [`06`](06-gaps.md) |
| 6 | Strictness of general knowledge | **Works.** `QuerySteps.answer.generalKnowledge` `off`/`supplement`/`fallback`/`open` per assistant version (Decisions "General knowledge per assistant"; `src/Ume.Rag.Answering/Chat/AssistantChatSettings.cs`) | Configuration: `fallback` meets the requirement (general answers where the knowledge base is silent); `supplement` is the conservative option | [`04`](04-assistant.md) |
| 7 | Automatic updates | **Works for crawled pages, manual for files.** Scheduled crawls with content-hash change detection; only changed content is re-parsed and re-embedded; guarded deletions (connectors §12, §13). Uploads change only by an admin's replace in the admin UI | Configuration (daily schedule). No push trigger, scripted upload API or folder/Git sync (gaps SA-9, SA-10, SA-14) | [`03`](03-updates.md) |
| – | Ticket text as the question | **Limited.** The last user message is the retrieval query and is capped at 2,000 characters (`src/Ume.Rag.Retrieval/QueryNormalizer.cs:14`, `src/Ume.Rag.Answering/Chat/ChatRequest.cs:112`) | Frontend workaround now; code later (gap SA-4) | [`05`](05-frontend-integration.md), [`06`](06-gaps.md) |

In short: points 1, 3 and 6 work with configuration, 2 and 7 work with configuration and known limits, 4 and 5 need
code. Before a pilot with more than a handful of staff, the shared rate limit (SA-6) needs a fix or a raised
platform setting.

## Reading order

1. [`01-gateway-setup.md`](01-gateway-setup.md): models, keys, residency ceiling, personal-data policy.
2. [`02-knowledge-base.md`](02-knowledge-base.md): the knowledge base, its sources and settings.
3. [`03-updates.md`](03-updates.md): how edits, additions and deletions reach the index.
4. [`04-assistant.md`](04-assistant.md): the assistant's instructions, general-knowledge level and retrieval.
5. [`05-frontend-integration.md`](05-frontend-integration.md): the contract for the frontend team.
6. [`06-gaps.md`](06-gaps.md): what the platform lacks for this case, sized and assigned.
7. [`07-rollout.md`](07-rollout.md): the order of work from setup to go-live, and what comes later.

## Open questions for the user

1. **Which department and which support?** The knowledge base and assistant need an owning department with admins
   (Decisions "Organisation …"). Is it the pilot department (DoIT) or a second one? The roadmap puts a second
   department after gateway release 2 ([`../../roadmap.md`](../../roadmap.md), "Next: P2"); this case uses a client
   audience only, which needs neither member groups nor delegation. Does it run inside P1b or as the first P2 item?
2. **Is the intranet reachable without signing in** from the RAG host? If not, the crawl needs SA-8 or the pages
   must be exported (or come through SharePoint, P2).
3. **Classification `internal`?** `sensitive` would need on-prem models, which do not exist yet (Decisions "M2 and
   test hosting").
4. **Personal data in tickets.** Pasted tickets reach an EU-hosted model through the gateway. Does the DPIA (G1)
   cover it, and should the api's gateway key redact personal data ([`01-gateway-setup.md`](01-gateway-setup.md) §4)?
5. **General-knowledge level: `fallback` (recommended) or `supplement`?** `fallback` is the level that meets
   requirement 6 (a general answer, behind the platform's caveat, where the knowledge base has nothing);
   `supplement` is the conservative choice if the gold set or support managers find general answers misleading.
6. **Related documents and follow-up suggestions** (SA-1, SA-3) are new platform features. Build them (each needs a
   Decisions row), or start the pilot without them?
7. **Per-user identity.** Is one shared application key acceptable for the pilot, with the frontend deciding who may
   use it (SA-5)? It means Ume.Rag cannot rate-limit, audit or filter per person.
8. **Where do the Markdown files live** (a Git repository, a file share, the intranet CMS)? The no-code route is to
   publish them as intranet pages and crawl them ([`03-updates.md`](03-updates.md) §4); the alternatives are SA-9
   or SA-10 (the roadmap already lists "a Git/Markdown connector" as an open product call).
9. **Legacy Office files** (`.doc`, `.xls`, `.ppt`) in the collection? They wait for P2 (postponed item Z1) unless
   converted first.
10. **Does the frontend store conversations?** That decides the client's `StoresHistory` and retention and belongs
    in the DPIA.
11. **How long are tickets?** Over 2,000 characters the frontend must use the workaround in
    [`05-frontend-integration.md`](05-frontend-integration.md) §6 until SA-4.
