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

## Decisions (user, 2026-11-06)

The open questions of the first draft, answered:

| # | Question | Answer | Consequence |
|---|---|---|---|
| 1 | Owning department | **DoIT**, the pilot department | Runs inside P1b; no second department, no gateway release 2 needed |
| 2 | Intranet readable without a login | **Yes** | The crawl works as is; SA-8 is not needed |
| 3 | Level | **`internal`** | EU-hosted models; no on-prem requirement |
| 4 | Personal data in pasted tickets | **Instructions and DPIA**: staff are told not to paste personal data; the DPIA (G1) covers the EU-hosted model; no redaction on the api's gateway key | [`01-gateway-setup.md`](01-gateway-setup.md) §4 stays without redaction; the frontend shows the instruction |
| 5 | General-knowledge level | **`fallback`** | [`04-assistant.md`](04-assistant.md) §4 |
| 6 | Related documents and follow-up suggestions | **Both built before the pilot** (SA-1, SA-3) | Each needs a Decisions row and a design section first (main session), then stream B |
| 7 | Per-user identity | **One shared app key** for the pilot | SA-5 waits; SA-6 (a limit that fits a shared key) is built before the pilot |
| 8 | Where the Markdown files live | **Local now, a Git repository later** | Upload by hand for the pilot; SA-10 (Git connector) **after the pilot** |
| 10 | Conversation history | **The frontend keeps it** and sends it each turn; Ume.Rag stores none (`none`, as today) | No A1 dependency; the frontend's store belongs in the DPIA |
| 11 | Ticket length | **Varies; fix the hard 2,000-character limit before the pilot** (SA-4) by **condensing a long ticket into a search query**: long messages are accepted (up to the chat request's own limit), a task model writes the search query, and the answer model gets the full text | SA-4 needs a design section (chat §5, condense) and a Decisions row |

Before the pilot, therefore: **SA-1, SA-3, SA-4, SA-6** (and SA-7, #424, before go-live).

## Still open

- **Legacy Office files** (`.doc`, `.xls`, `.ppt`) in the collection? They wait for P2 (postponed item Z1); until
  then they are converted to `.docx`/`.xlsx`/`.pptx` before upload.
