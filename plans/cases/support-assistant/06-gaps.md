# Gaps

What the platform lacks for this case. Each gap names what is missing (with the code that shows it), why the case
needs it, a solution sketch, a size (S ≈ days, M ≈ a week or two, L ≈ several weeks), the owner by
`.github/stream-ownership.txt` (stream **a**: worker, pipelines, contracts, deploy, docs; stream **b**: `src/**`
otherwise, admin UI, Keycloak files; **main**: designs and Decisions), and whether it blocks the case.

The sketches are proposals. Each one that changes a contract or a rule needs the main session to settle it in the
design (and, where marked, a Decisions row) before a stream builds it. Under "No backward compatibility before
production" (Decisions) none needs a compatibility step.

## Summary

| ID | Gap | Size | Owner | Blocks the case? |
|---|---|---|---|---|
| SA-1 | Related (retrieved, uncited) documents in the chat answer | M | b + main (Decisions row) | blocks requirement 4 only |
| SA-2 | An outcome signal (answered, not found, general, unavailable, degraded) in the answer | S | b + main | no |
| SA-3 | Suggested follow-up questions | M | b + main (Decisions row) | no (requirement 5 is "maybe") |
| SA-4 | Tickets longer than 2,000 characters as the question | M | b + main | no (workaround), yes for long tickets in practice |
| SA-5 | Per-user identity for a non-Open WebUI client (delegated tokens) | L | b + main | no, if a shared key is accepted (question 7) |
| SA-6 | A rate limit that fits a shared application key | S | b | **yes** beyond a handful of users, unless the platform setting is raised |
| SA-7 | Key IP allow-list (and per-key rate) not enforced in rag-api; filed as #424 | S | b | no (firewall), fix before go-live |
| SA-8 | Crawling an intranet that needs a login | L | a + main | **yes**, if the intranet needs a login (question 2) |
| SA-9 | Scripted upload API (service credential on the upload routes) | M | b | no |
| SA-10 | Git/Markdown or file-share connector | M–L | a + main | no (crawl route) |
| SA-11 | Replace an upload by file name | S | b | no |
| SA-12 | A readable view of uploaded non-PDF documents | M | b + main | no |
| SA-13 | `.markdown` extension refused on upload | S | b | no |
| SA-14 | Push-triggered refresh (webhook or API "sync now") | M | b + a | no |
| SA-15 | Proxy body-size guidance (1 MB) below the chat route's 2 MB | S | a | no |

## SA-1 Related documents in the chat answer

- **Missing.** For clients other than Open WebUI the answer carries only `url_citation` annotations for cited
  documents (`src/Ume.Rag.Api/ChatStreaming.cs:91-122`, `src/Ume.Rag.Api/ChatEndpoints.cs:160-182`). The retrieval
  result the answer was built from (`ChatPlan.Retrieval`, `src/Ume.Rag.Answering/Chat/ChatPlan.cs`) never leaves the
  service. `/v1/retrieve` is closed to non-internal clients (knowledge §2.3; `src/Ume.Rag.Api/KnowledgeEndpoints.cs:251-254`;
  `src/Ume.Rag.Domain/Access/AccessPolicy.cs:170-172`).
- **Why.** Requirement 4: staff want the next-best articles even when the answer cites one.
- **Sketch.** An opt-in extension: the request carries a member such as `"rag": { "include_sources": true }`
  (unknown members are already ignored, chat §3.2); the answer adds a `rag` object (JSON: top level; stream: one
  extra chunk before the final one) with `sources: [{ document_id, title, label, url, source_type, media_type,
  modified, cited, display_number, rank }]`, grouped by document from the retrieval already done, links built as for
  citations (grants for uploads). No extra search or model call. Open WebUI keeps its `source` events and ignores the
  member. The alternative, opening `/v1/retrieve` to integration keys (knowledge §2.3's P2 row), costs a second
  embedding and search per turn and can disagree with what the answer used.
- **Size M** (contract, both answer paths, tests, chat design §3.3/§9.3 update). **Decisions row**: extends the
  OpenAI-compatible contract with an own member.

## SA-2 Outcome signal

- **Missing.** Not-found, unavailable and general-knowledge answers are ordinary completions
  (`src/Ume.Rag.Answering/Chat/ChatService.cs:86-101`); the outcome is recorded (`rag.chat.requests{outcome}`,
  `ChatEndpoints.cs:241-260`) but not returned.
- **Why.** The app should style "not found" (offer escalation) and "general knowledge" (warn) differently.
- **Sketch.** `outcome` and `coverage` in SA-1's `rag` object, or an `x-rag-outcome` response header: the plan is
  known before the first byte (`ChatEndpoints.cs:94-97`), so a header works for streams too.
- **Size S**, best built with SA-1.

## SA-3 Suggested follow-up questions

- **Missing.** No code generates suggestions (nothing under `src/Ume.Rag.Answering/Chat/` does); Open WebUI's
  follow-up generation is off and there is no task model (chat §4.2; Decisions "Simplification pass": "Open WebUI
  title/tag/follow-up generation off, no `owui-tasks`").
- **Why.** Requirement 5 ("maybe"): one-click next questions when a ticket is vague.
- **Sketch.** `QuerySteps.answer.suggestFollowUps` (off by default, versioned like `generalKnowledge`); after a
  sourced answer, one call on the platform condense model at the turn's ceiling with the question, the answer and the
  source titles; ≤ 3 questions as JSON, ~1 s budget, skipped on fixed texts and on failure; delivered in SA-1's `rag`
  object after the text, so time to first token is unchanged; never stored (history mode `none`). Clarifying questions
  inside the answer need no code ([`04-assistant.md`](04-assistant.md) §3).
- **Size M.** **Decisions row**: a platform task call per turn, which the simplification pass removed for Open WebUI.

## SA-4 Long tickets as the question

- **Missing.** The last user message must be 1–2,000 characters, because it is also the retrieval query
  (`src/Ume.Rag.Answering/Chat/ChatRequest.cs:112`, `src/Ume.Rag.Retrieval/QueryNormalizer.cs:14`; chat §3.2).
- **Why.** Pasted tickets with mail threads and logs exceed it.
- **Sketch.** Separate the question from the query: accept a question up to chat §3.2's 8,000 characters; above
  2,000, a query-extraction call on the condense model ("state the problem as a search query", ≤ 96 tokens, the
  condense timeout) produces the query, falling back to the first 2,000 characters; the full ticket stays in the
  prompt. Interim workaround: [`05-frontend-integration.md`](05-frontend-integration.md) §6.
- **Size M** (chat §3.2, §5 change; prompt budget check, chat §6.3).

## SA-5 Per-user identity for the app

- **Missing.** rag-api authenticates keys and Open WebUI's user JWT only (`src/Ume.Rag.Api/KnowledgeCallers.cs:41-95`,
  `src/Ume.Rag.Api/ChatCallers.cs:16-48`); the OAuth token path (identity §7, `own-client` delegated tokens) is not
  built (identity §7.1). An `integration` key is one subject for all users.
- **Why.** Per-person rate limits, audit, feedback ownership, department or group audiences, and later stored
  conversations per person.
- **Sketch.** Build identity §7 as designed (JWT bearer on realm `ume`, audience `ume-rag-api`, client by `azp`, scope
  `Chat.Use`); the app exchanges or forwards the user's token (OBO later, identity §9). Realm additions for the app's
  client scopes.
- **Size L.** Designed for P2; pulling it forward is a roadmap call, not a new decision.

## SA-6 Rate limit for a shared application key

- **Missing.** The chat limit is per (client, subject) per minute (`src/Ume.Rag.Api/ChatStreaming.cs:170-200`,
  window key `{ClientId}|{SubjectId}`). For an integration key the subject is **the key**: `SubjectId` is the key's
  id, set in `src/Ume.Rag.Api/KnowledgeCallers.cs:90` (bare id; identity §3 writes it `key:{keyId}`), and
  `ChatCallers` returns that principal unchanged for every client kind but `open-webui`
  (`src/Ume.Rag.Api/ChatCallers.cs:19-23`). No header lets a caller set it: the only end-user channel is Open WebUI's
  signed `X-OpenWebUI-User-Jwt`, accepted only for the `open-webui` client named by `OpenWebUi:ClientId`
  (`ChatCallers.cs:25-45`), and the OpenAI `user` field is ignored (chat §3.2). A supported way for a trusted backend
  to pass the end user is **missing**; it is SA-5 (delegated tokens). So every staff member is the same subject and
  the app gets 20 messages a minute in total. The only knob,
  `Settings.Chat.messagesPerMinutePerUser`, is platform-wide. `Keys.RequestsPerMinute` is saved
  (`src/Ume.Rag.AdminApi/KeyEndpoints.cs:245`) but not read by rag-api.
- **Why.** Twenty support staff at peak exceed 20 messages a minute.
- **Sketch.** For `Application` subjects, use the key's `RequestsPerMinute` (default: the platform value × N) instead
  of the per-user value; keep the per-user limit for user subjects.
- **Size S.** Interim: raise the platform setting, accepting it for every client.

## SA-7 Key IP allow-list not enforced (#424)

A platform bug against identity §6.3 step 4 (`address_not_allowed`), not a case-specific gap; filed as Ume.Rag
issue #424 (stream b), which also asks about `Keys.RequestsPerMinute` (saved, never read; see SA-6).

- **Missing.** `Keys.AllowedIpRanges` is saved and normalised (`src/Ume.Rag.AdminApi/KeyEndpoints.cs:205-208`), but no
  check in rag-api reads it (`src/Ume.Rag.Domain/Services/KeyChecks.cs:18-34` checks kind, status and owner;
  no other reference under `src/Ume.Rag.Api` or `src/Ume.Rag.Infrastructure/Keys`). Identity §5.3 assumes the gateway
  pattern (`address_not_allowed`).
- **Why.** A leaked app key should be useless outside the app's backend host.
- **Sketch.** Copy the gateway's address check into `KeyChecks` with event 5001 reason `address_not_allowed`.
- **Size S.** Not blocking while the proxy and firewall restrict the routes; fix before go-live.

## SA-8 Authenticated intranet crawl

- **Missing.** The crawler sends no cookies or credentials (`src/Ume.Rag.Worker/Connectors/Egress/CrawlHttpClient.cs:11`,
  `:33`; `src/Ume.Rag.Worker/Connectors/Http/HttpFetcher.cs:82`).
- **Why.** Requirement 1, if the intranet requires a login.
- **Sketch.** A per-source credential reference (a secret file named in deployment config, never in the settings
  JSON), sent only to the source's scope hosts through the egress guard (basic, bearer or NTLM per host), audited;
  preflight checks it. If the intranet is SharePoint, the P2 SharePoint connector (connectors §14) is the better route.
- **Size L.** Security design by main first.

## SA-9 Scripted upload API

- **Missing.** Upload routes need an admin's browser session with antiforgery (`src/Ume.Rag.AdminApi/AdminAuthentication.cs:112-115`);
  no machine credential exists on the admin API.
- **Why.** Requirement 7 for files kept outside the intranet: a script or CI job could push changed files.
- **Sketch.** A service token (Keycloak client credentials) with a department-scoped `rag-*` role on the upload-batch
  routes, plus SA-11's replace-by-name, so a script can "put" a file by name idempotently.
- **Size M.**

## SA-10 Git/Markdown or file-share connector

- **Missing.** Source types are `url-list`, `crawl`, `upload` (and P2 `sharepoint`) (`contracts/sources/v1/`;
  connectors §2.1). The roadmap lists "a Git/Markdown connector" under product calls ([`../../roadmap.md`](../../roadmap.md)).
- **Why.** Requirement 7 for Markdown kept in a repository.
- **Sketch.** A `git` source: read-only HTTPS clone through the egress guard, identity `git:<repo>:<path>`, change
  detection from the commit diff, deletions through the guard, citations to the repository's web view or a published
  site. Until then: publish and crawl ([`03-updates.md`](03-updates.md) §4).
- **Size M–L.** Product call first.

## SA-11 Replace an upload by file name

- **Missing.** An upload without `X-Upload-Id` is a new slot; only byte-identical files are skipped
  (`src/Ume.Rag.AdminApi/UploadEndpoints.cs:118-124`; connectors §9.3).
- **Why.** An edited file uploaded as new leaves the old version searchable ([`03-updates.md`](03-updates.md) §3).
- **Sketch.** When an added file's name matches a live slot, the admin UI proposes *Replace*; the API offers an
  explicit `replace-by-name` entry for scripts (SA-9).
- **Size S.**

## SA-12 Readable view of uploaded non-PDF documents

- **Missing.** Uploaded Word and Markdown files download as attachments; only PDF, PNG and JPEG open inline
  (`src/Ume.Rag.Answering/DocumentContentReader.cs:76-89`).
- **Why.** Requirement 3, "the whole page can be opened": a Markdown article should read as a page.
- **Sketch.** A rendered HTML view from the stored parse output (sections and text, not the raw file), served with
  the existing sandboxing CSP and grant check; or convert Markdown to PDF at upload. Needs a security review (the
  reason crawled HTML is never served, `src/Ume.Rag.Answering/KnowledgeService.cs:180-182`).
- **Size M.**

## SA-13 `.markdown` extension

- **Missing.** The admin's allowed set has `.md` only (`src/Ume.Rag.AdminApi/UploadEndpoints.cs:36-39`); the worker's
  sniffer maps `.markdown` too (`src/Ume.Rag.Worker/Connectors/Uploads/FileTypeSniffer.cs:265`).
- **Sketch.** Add `.markdown` to the allowed set. **Size S.**

## SA-14 Push-triggered refresh

- **Missing.** Syncs start by schedule or by an admin in the admin UI (`src/Ume.Rag.Worker/Engine/SyncScheduler.cs`;
  `POST /api/sources/{id}/sync`, `POST /api/documents/{id}/reprocess`); nothing outside can trigger them.
- **Why.** Requirement 7 with minutes instead of a night's delay.
- **Sketch.** The same service credential as SA-9 on "sync source" and "refetch URL" (the latter a `reprocess` from
  stage `fetch`, or a new-URL fetch for a crawl source), rate-limited per source; a CMS publish hook calls it.
- **Size M.**

## SA-15 Proxy body size

- **Missing.** `init-deployment.sh` prints "the gateway's 1 MB elsewhere" for the proxy (`deploy/init-deployment.sh:545-549`;
  [`../../../docs/production-setup.md`](../../../docs/production-setup.md), "Reverse proxy and PKI"), but
  `/v1/chat/completions` takes up to 2 MB (`src/Ume.Rag.Api/ChatEndpoints.cs:19`). Long conversations hit the proxy
  first (Open WebUI too).
- **Sketch.** Print and document 2 MB for `POST /v1/chat/completions` on the api host. **Size S.**

## Not gaps

- **General-knowledge strictness** (requirement 6) exists: [`04-assistant.md`](04-assistant.md) §4.
- **Citations to real pages and downloads** exist: [`05-frontend-integration.md`](05-frontend-integration.md) §4.
- **Re-embedding on change** exists and reuses unchanged chunks: [`03-updates.md`](03-updates.md) §1.
- **CORS** is absent on purpose: the key belongs on the app's server.
