# Gaps

What the platform lacks for this case. Each gap names what is missing (with the code that shows it), why the case
needs it, a solution sketch, a size (S ≈ days, M ≈ a week or two, L ≈ several weeks), the owner by
`.github/stream-ownership.txt` (stream **a**: worker, pipelines, contracts, deploy, docs; stream **b**: `src/**`
otherwise, admin UI, Keycloak files; **main**: designs and Decisions), and whether it blocks the case.

The sketches are proposals. Each one that changes a contract or a rule needs the main session to settle it in the
design (and, where marked, a Decisions row) before a stream builds it. Under "No backward compatibility before
production" (Decisions) none needs a compatibility step.

SA-1, SA-2, SA-3, SA-4, SA-6 and SA-7 are **done** on `main` (2026-11-06): each has a **Done** line below naming its PR
and the code, and the design sections it names are the contract
([`../../archive/work/to-v0.5.0.md`](../../archive/work/to-v0.5.0.md) M-5 to M-8; issue #424). SA-15 is done too (#552).
SA-5 and SA-8 to SA-14 are in no queue: each waits for a reason the case gives, or for the user to queue it. Their
sections keep only what was asked and what was built.

## Summary

| ID | Gap | Size | Owner | Blocks the case? |
|---|---|---|---|---|
| SA-1 | Related (retrieved, uncited) documents in the chat answer | M | main session, M-7 | **done**, #433 |
| SA-2 | An outcome signal (answered, not found, general, unavailable, degraded) in the answer | S | main session, M-7 | **done**, #433 |
| SA-3 | Suggested follow-up questions | M | main session, M-8 | **done**, #434 |
| SA-4 | Tickets longer than 2,000 characters as the question | M | main session, M-6 | **done**, #431 |
| SA-5 | Per-user identity for a non-Open WebUI client (delegated tokens) | L | b + main | no: a shared key is accepted for the pilot (user, 2026-11-06) |
| SA-6 | A rate limit that fits a shared application key | S | main session, M-5 | **done**, #430 |
| SA-7 | Key IP allow-list not enforced in kalla-api (#424) | S | main session, #424 | **done**, #432 |
| SA-8 | Crawling an intranet that needs a login | L | a + main | no: the intranet is readable without a login (user, 2026-11-06) |
| SA-9 | Scripted upload API (service credential on the upload routes) | M | b | no |
| SA-10 | Git/Markdown or file-share connector | M–L | a + main | no: **after the pilot**, when the Markdown moves to a Git repository (user, 2026-11-06) |
| SA-11 | Replace an upload by file name | S | b | no, but the **main risk for an upload-only pilot** (below); queued as B-83 |
| SA-12 | A readable view of uploaded non-PDF documents | M | b + main | no |
| SA-13 | `.markdown` extension refused on upload | S | b | no |
| SA-14 | Push-triggered refresh (webhook or API "sync now") | M | b + a | no |
| SA-15 | Proxy body-size guidance (1 MB) below the chat route's 2 MB: **done** (#552) | S | a | no |

## SA-1 Related documents in the chat answer

- **Asked.** Requirement 4: the next-best articles next to the answer, for a client other than Open WebUI, which got
  only the cited documents while `/v1/retrieve` stays closed to it (knowledge §2.3).
- **Done** in #433 (M-7): `kalla.related_documents` (request 0–10), from the answer's own retrieval, one entry per
  document with `document_id`, `title`, `url`, `collection`, `rank`, `cited` and `number`; `[]` unless the outcome is
  `answered` (chat §3.8, §9.7; `src/Kalla.Answering/Chat/RelatedDocuments.cs`). How the app reads it:
  [`05-frontend-integration.md`](05-frontend-integration.md) §5.

## SA-2 Outcome signal

- **Asked.** Not-found, unavailable and general-knowledge answers were ordinary completions; the app should style them.
- **Done** in #433 with SA-1: `kalla.outcome` (`answered`, `answered-general`, `not-found`, `unavailable`) and
  `kalla.degraded` in every answer that sent `kalla` (chat §3.8; `KallaAnswer` in
  `src/Kalla.Answering/Chat/RelatedDocuments.cs`, names in `ChatOutcomes.Name` in
  `src/Kalla.Answering/Chat/ChatPlan.cs`). No header.

## SA-3 Suggested follow-up questions

- **Asked.** Requirement 5: one-click next questions when a ticket is vague.
- **Done** in #434 (M-8): `kalla.follow_ups` (request 0–5), one call on `Settings.Chat.condenseModel` after an
  `answered` or `answered-general` answer, budget `Chat:FollowUpTimeout` 2 s, `[]` on failure, never stored; in a second
  streamed `kalla` chunk, so time to first token and the related documents do not wait (chat §9.8, §3.8;
  `src/Kalla.Answering/Chat/FollowUps.cs`, `ChatSse.ExecuteAsync` in `src/Kalla.Api/ChatStreaming.cs`). Measured 1.1–1.3
  s on `claude-haiku-5-5`, 1.4–2.6 s on `gpt-6-luna` (chat §22; [`04-assistant.md`](04-assistant.md) §2).

## SA-4 Long tickets as the question

- **Asked.** Pasted tickets with mail threads and logs exceeded the 2,000-character question.
- **Done** in #431 (M-6): the question may be 1–16,000 characters (`ChatRequestParser.MaxQuestionChars` in
  `src/Kalla.Answering/Chat/ChatRequest.cs`); over 2,000 the condense model writes the search query
  (`x-fors-step: condense-long`, `Chat:LongQuestionCondenseTimeout` 3 s), with a fallback to the first 2,000 characters cut at a word
  boundary (`Condenser.Fallback` in `src/Kalla.Answering/Chat/Condenser.cs`); the answer model reads the full text with
  its line breaks (chat §3.2, §5.1). Measured on a 16,000-character ticket: `gpt-6-luna` 2.3–2.9 s, `claude-haiku-5-5`
  about 1.1 s (chat §22).

## SA-5 Per-user identity for the app

- **Missing.** kalla-api authenticates keys and Open WebUI's user JWT only (`KnowledgeCallers.AuthenticateAsync` in
  `src/Kalla.Api/KnowledgeCallers.cs`, `ChatCallers.AuthenticateAsync` in `src/Kalla.Api/ChatCallers.cs`); the OAuth
  token path (identity §7, `own-client` delegated tokens) is not built (identity §7.1). An `integration` key is one
  subject for all users.
- **Why.** Per-person rate limits, audit, feedback ownership, department or group audiences, and later stored
  conversations per person.
- **Sketch.** Build identity §7 as designed (JWT bearer on realm `fors`, audience `kalla-api`, client by `azp`, scope
  `Chat.Use`); the app exchanges or forwards the user's token (OBO later, identity §9). Realm additions for the app's
  client scopes.
- **Size L.** Designed for P2; pulling it forward is a roadmap call, not a new decision.

## SA-6 Rate limit for a shared application key

- **Asked.** The chat limit was per (client, subject), and for an integration key the subject is the key, so the
  whole app shared `Settings.Chat.messagesPerMinutePerUser` (20); twenty staff at peak exceed it.
- **Done** in #430 (M-5): for a key subject the limit is the key's `Keys.RequestsPerMinute` when set, else
  `Settings.Chat.messagesPerMinutePerUser`; Open WebUI users keep the per-user value (chat §4.1 step 4;
  `ChatRateLimiter.LimitFor` in `src/Kalla.Api/ChatStreaming.cs`). The platform admin sets the app key's
  value (e.g. 200; [`05-frontend-integration.md`](05-frontend-integration.md) §2).

## SA-7 Key IP allow-list not enforced (#424)

- **Asked.** A platform bug against identity §6.3 step 4: `Keys.AllowedIpRanges` was saved but not checked, so a
  leaked app key worked from anywhere.
- **Done** in #432: `KeyChecks.Refusal` refuses an address outside the ranges with 403 `address_not_allowed`
  (`src/Kalla.Domain/Services/KeyChecks.cs`, called by `KnowledgeCallers.AuthenticateAsync`), and kalla-api takes the
  client address from `X-Forwarded-For` when the connection comes from `Settings.TrustedProxies`
  (`src/Kalla.Api/ClientAddressMiddleware.cs`). The operator sets the trusted proxies
  ([`05-frontend-integration.md`](05-frontend-integration.md) §2).

## SA-8 Authenticated intranet crawl

- **Missing.** The crawler sends no cookies or credentials (`CrawlHttpClient` in
  `src/Kalla.Worker/Connectors/Egress/CrawlHttpClient.cs`; `HttpFetcher` in
  `src/Kalla.Worker/Connectors/Http/HttpFetcher.cs`).
- **Why.** Requirement 1, if the intranet requires a login.
- **Sketch.** A per-source credential reference (a secret file named in deployment config, never in the settings
  JSON), sent only to the source's scope hosts through the egress guard (basic, bearer or NTLM per host), audited;
  preflight checks it. If the intranet is SharePoint, the P2 SharePoint connector (connectors §14) is the better route.
- **Size L.** Security design by main first.

## SA-9 Scripted upload API

- **Missing.** Upload routes need an admin's browser session with antiforgery (`AddAdminAuthentication` in
  `src/Kalla.AdminApi/AdminAuthentication.cs`); no machine credential exists on the admin API.
- **Why.** Requirement 7 for files kept outside the intranet: a script or CI job could push changed files.
- **Sketch.** A service token (Keycloak client credentials) with a department-scoped `rag-*` role on the upload-batch
  routes, plus SA-11's replace-by-name, so a script can "put" a file by name idempotently.
- **Size M.**

## SA-10 Git/Markdown or file-share connector

- **Missing.** Source types are `url-list`, `crawl`, `upload` (and P2 `sharepoint`) (`contracts/sources/v1/`; connectors
  §2.1). The roadmap lists "a Git/Markdown connector" under product calls ([`../../roadmap.md`](../../roadmap.md)).
- **Why.** Requirement 7 for Markdown kept in a repository.
- **Sketch.** A `git` source: read-only HTTPS clone through the egress guard, identity `git:<repo>:<path>`, change
  detection from the commit diff, deletions through the guard, citations to the repository's web view or a published
  site. Until then: publish and crawl ([`03-updates.md`](03-updates.md) §4).
- **Size M–L.** Product call first.

## SA-11 Replace an upload by file name

- **Missing.** An upload without `X-Upload-Id` is a new slot, whatever its name; only byte-identical files are skipped
  (`UploadEndpoints.UploadIdHeader` in `src/Kalla.AdminApi/UploadEndpoints.cs`; `upload.duplicate` in
  `src/Kalla.Worker/Connectors/Uploads/UploadSlots.cs`; connectors §9.3). Only the row's **Replace** in the file list
  keeps the document (`FileRows.vue`, `FileUpload.vue` in `src/admin-ui/src/components/files/`).
- **Why.** An edited file uploaded as new leaves the old version searchable ([`03-updates.md`](03-updates.md) §3).
  **The main risk for an upload-only pilot** (main session, 2026-11-23): every edit of a Word, PDF or Markdown file
  goes through an editor's upload, and one upload by habit instead of *Replace* puts two versions of the same
  instruction into the answers, the old one still citable, with no warning. Until SA-11 is built, the editors'
  routine is *Replace*, and the file list is checked for duplicate names after each round of edits.
- **Queued** (user, 2026-11-23): B-83 in stream b's queue, admin §5.5.7 point 6. The API's `replace-by-name` entry for
  scripts stays with SA-9.
- **Size S.**

## SA-12 Readable view of uploaded non-PDF documents

- **Missing.** Uploaded Word and Markdown files download as attachments; only PDF, PNG and JPEG open inline
  (`DocumentContentReader.ContentDisposition` in `src/Kalla.Answering/DocumentContentReader.cs`).
- **Why.** Requirement 3, "the whole page can be opened": a Markdown article should read as a page.
- **Sketch.** A rendered HTML view from the stored parse output (sections and text, not the raw file), served with
  the existing sandboxing CSP and grant check; or convert Markdown to PDF at upload. Needs a security review (the
  reason crawled HTML is never served, `content_not_served` in `src/Kalla.Answering/KnowledgeService.cs`).
- **Size M.**

## SA-13 `.markdown` extension

- **Missing.** The admin's allowed set has `.md` only (`UploadEndpoints.Allowed` in
  `src/Kalla.AdminApi/UploadEndpoints.cs`); the worker's sniffer maps `.markdown` too (`FileTypeSniffer` in
  `src/Kalla.Worker/Connectors/Uploads/FileTypeSniffer.cs`).
- **Sketch.** Add `.markdown` to the allowed set. **Size S.**

## SA-14 Push-triggered refresh

- **Missing.** Syncs start by schedule or by an admin in the admin UI (`src/Kalla.Worker/Engine/SyncScheduler.cs`;
  `POST /api/sources/{id}/sync`, `POST /api/documents/{id}/reprocess`); nothing outside can trigger them.
- **Why.** Requirement 7 with minutes instead of a night's delay.
- **Sketch.** The same service credential as SA-9 on "sync source" and "refetch URL" (the latter a `reprocess` from
  stage `fetch`, or a new-URL fetch for a crawl source), rate-limited per source; a CMS publish hook calls it.
- **Size M.**

## SA-15 Proxy body size

**Done** (#552, 2026-11-11): the nginx example, the Kubernetes ingress, the printed routes and the production setup
allow 2 MB on the api host and on Open WebUI's (`ChatEndpoints.BodyLimit` in `src/Kalla.Api/ChatEndpoints.cs` is 2 MB);
the upgrade guide tells operators with a generated `nginx.conf` to raise it.



## Not gaps

- **General-knowledge strictness** (requirement 6) exists: [`04-assistant.md`](04-assistant.md) §4.
- **Citations to real pages and downloads** exist: [`05-frontend-integration.md`](05-frontend-integration.md) §4.
- **Re-embedding on change** exists and reuses unchanged chunks: [`03-updates.md`](03-updates.md) §1.
- **CORS** is absent on purpose: the key belongs on the app's server.
