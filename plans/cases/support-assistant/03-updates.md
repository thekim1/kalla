# Keeping the knowledge base current

Requirement 7: when a document is added, edited or removed, the index follows by itself. For crawled pages it does,
on a schedule. For uploaded files it follows an admin's action. Nothing reacts to a change the moment it happens.

## 1. What happens on a change (all sources)

| Step | Behaviour | Reference |
|---|---|---|
| Change detection | stage 1: conditional GET or a sitemap `lastmod` skip; stage 2: SHA-256 of the noise-normalised content. Only a new hash starts processing | connectors §12.1 |
| Re-processing | the document gets a new execution: parse, chunk, embed. Parse output and embeddings are cached by content and text hash, so an edit re-embeds only the chunks whose text changed | engine §8; processing §11 |
| Swap | the new chunk set replaces the old one in one transaction; retrieval never sees half a document | engine §14 |
| Identity | a crawled document is its final URL, an uploaded one its upload slot (connectors §3.4); both survive edits, so the document id, its evaluation questions, feedback and citation history stay attached | connectors §3.4, §9.3 |
| Old version | kept 14 days for rollback, then purged | connectors §9.3, §13.4 |

## 2. Crawled pages

**Schedule.** `Sources.Schedule` (cron, Europe/Stockholm) or the platform default by type: crawl weekly, Saturday
01:00; URL list nightly, 02:00; both with a per-source jitter of up to 90 minutes
(`src/Ume.Rag.Worker/Engine/EngineSettings.cs:20-27`; `src/Ume.Rag.Worker/Engine/SyncScheduler.cs:21`). Missed
occurrences collapse into one run.

**Crawl window.** A traversal fetches only inside `crawlWindow` (default `22:00-06:00`); equal start and end mean
always open (`src/Ume.Rag.Worker/Connectors/Crawl/CrawlWindow.cs:5-9`). Manual "sync now" ignores it
(connectors §6.5).

**Incremental runs** (only changed sitemap entries) need the host's site profile to mark `lastmod` reliable, which
takes a 500-URL sample over at least 7 days; the live fetch runner for that sample is not built (connectors §5.4;
to-verify #170). A new intranet host therefore runs **full traversals**: every page is requested, but unchanged pages
cost one request and no parsing.

**Recommendation for this case:** a daily full traversal in the night window (`Schedule` `0 2 * * *`). A support
knowledge base of a few thousand pages takes minutes at the municipal host rate (connectors §6.5). If editors need
changes live within hours, a schedule every few hours with `crawlWindow` widened (for example `06:00-06:00`, always
open) works, but needs the intranet web team's consent: daytime crawling is an exception (connectors §6.5). Since
#461 that consent can also be recorded on the host itself: a politeness profile with `crawlWindow` `any`
([`02-knowledge-base.md`](02-knowledge-base.md) §2).

**Urgent edits** today, without code:

- *Sync now* on the source (`POST /api/sources/{id}/sync`, `src/Ume.Rag.AdminApi/SourceEndpoints.cs:158`) runs the whole
  source at once.
- *Reprocess* one document from stage `fetch` re-fetches that page alone (`POST /api/documents/{id}/reprocess`,
  `src/Ume.Rag.AdminApi/DocumentEndpoints.cs:144`; payload `fromStage`,
  `contracts/jobs/v1/command.reprocess-document.payload.schema.json`). It needs the `Manage` policy and the document
  must already be known; a new page arrives with the next run.

**Deletions** (connectors §5.4, §13):

- A page answering 404 or 410 is `Gone` and a deletion candidate in any run.
- A page no longer reached is a candidate only after a **complete** traversal (≥ 99 % of seeds resolved).
- The guard holds the deletions for an admin's decision when they exceed 20 % (and at least 5) or 500 documents, or
  when the source came back empty; otherwise they apply at once. A held decision waits indefinitely and shows on the
  Overview page.
- A deleted document leaves retrieval at once, stays revivable for 14 days, then is purged.

## 3. Uploaded files

| Action | How | Effect |
|---|---|---|
| Add | upload in the source's file list | a new slot and document |
| Edit | **Replace** on the file's row: picks the new file and sends it with `X-Upload-Id` (`src/admin-ui/src/components/sources/SourceFiles.vue:117-130`; `src/Ume.Rag.AdminApi/UploadEndpoints.cs:188-194`) | same document id, new version; byte-identical bytes are `unchanged` (connectors §9.3) |
| Delete | delete entries in a batch | soft delete, exempt from the guard; a platform admin restores it from the file list within 14 days (B-24, #489; connectors §9.3) |

**Pitfall:** uploading an edited file as a new file (not *Replace*) creates a **second** document. Only byte-identical
duplicates are skipped (`upload.duplicate`, connectors §9.3). The old version stays searchable until someone deletes
it, and the assistant may cite either. The editors' routine must be "Replace", or gap SA-11 (replace by name) closes
it.

Upload sources have no schedule (`src/Ume.Rag.Worker/Engine/SyncScheduler.cs:120`): nothing re-reads a file that was
edited elsewhere.

## 4. Markdown in Git or on a file share

No connector watches a repository or a folder (gap SA-10; the roadmap lists "a Git/Markdown connector" as an open
product call), and the admin API accepts only a signed-in admin's session (cookie and antiforgery,
`src/Ume.Rag.AdminApi/AdminAuthentication.cs:104-115`), so a script cannot upload either (gap SA-9).

Without code: publish the files as an intranet site and crawl it ([`02-knowledge-base.md`](02-knowledge-base.md) §4,
route 1). The repository's CI rebuilds the site on merge; the next crawl picks the change up; a removed file becomes
a 404 and leaves the index through the guard. This also gives citations a real page to open.

## 5. What citations do after a change

| Citation kind | After an edit | After a deletion |
|---|---|---|
| Crawled page (link = page URL) | the link shows the live page, which may already differ from the cited text until the next crawl | the link may 404 at the source; the document leaves retrieval when the run deletes it |
| Uploaded file (link = signed grant) | the grant names the document, not a version: it serves the current version | the grant re-checks visibility on every use and answers 404 (knowledge §4.6) |
| Any grant | valid 7 days (`internal`); an expired link in a browser gets a short "ask again" page | – |

## 6. Gaps

| Gap | Size | Blocks |
|---|---|---|
| SA-9 scripted upload / sync API | M | no |
| SA-10 Git/Markdown or file-share connector | M–L | no (crawl route) |
| SA-11 replace by file name | S | no |
| SA-14 push-triggered refresh (webhook or API "sync now") | M | no |
| SA-8 authenticated intranet crawl | L | yes, if the intranet needs a login |

Details in [`06-gaps.md`](06-gaps.md).
