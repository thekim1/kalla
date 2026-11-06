# The knowledge base

One knowledge base (collection) owned by the support organisation's department, fed by an intranet crawl and an upload
source. The admin journey that does this exists end to end (admin §5.4); this file says what to choose at each step
for this case.

## 1. Ownership, sharing, level

| Setting | Choice for the case | Rule |
|---|---|---|
| Owner | the support organisation's department; its admins hold `rag-department-admin` | Decisions "Organisation …": departments own content and are the admin boundary |
| Sharing | *only our department* (the default) | same row; widen later only if another department's assistant should link it |
| Contact person | the knowledge-base editor (name, e-mail) | same row; shown to departments that link it |
| Level | **`internal`** (question 3 in [`README.md`](README.md)) | Decisions "Classification", "Residency per level". `public` is wrong for internal support content; `sensitive` needs on-prem models (none yet, M2) |
| Topics | optional: path rules on crawl and URL-list sources tag chunks (template parameter `pathRules`, label "Topics", `src/Ume.Rag.MigrationService/Templates/website.v1.json`); uploads carry no topics | Decisions "Organisation …", "M-2 open design calls" (uploads need no topics) |

With level `internal`, uploads and raw blobs are envelope-encrypted (connectors §9.1; `DataProtectionPolicy.DefaultFor`
in `src/Ume.Rag.Domain/Entities/CollectionEntities.cs:106-121`).

Personal-data flags in an `internal` collection are shown to admins but block nothing (processing §9.5); `piiMode`
only matters in `public` collections. The flags still deserve a look: a knowledge-base article with a staff member's
direct number is fine, a pasted ticket with a citizen's personnummer in an article is not.

## 2. Source A: intranet pages (crawl)

**Preconditions** (operator, once per deployment):

- The intranet hosts' address ranges are in `--crawl-allow` (`init-deployment.sh`), which feeds both the worker's
  egress guard (`Crawl:AllowedPrivateRanges`, `src/Ume.Rag.Worker/Connectors/Egress/CrawlOptions.cs:16-19`) and the
  host firewall ([`../../../docs/production-setup.md`](../../../docs/production-setup.md), "2. `init-deployment.sh`";
  connectors §7). Without it every private address is refused.
- The pages are readable **without signing in**. The crawler sends no cookies, credentials or proxy
  (`src/Ume.Rag.Worker/Connectors/Egress/CrawlHttpClient.cs:11`, `:33`; `src/Ume.Rag.Worker/Connectors/Http/HttpFetcher.cs:82`).
  A login wall is gap SA-8 (question 2).
- robots.txt is always honoured; page-level `noindex`/`nofollow` are ignored on allow-listed municipal hosts
  (Decisions "Crawler (step 6b questions)"). If the intranet's robots.txt disallows bots, the web team adds an
  allowance for `UmeaKommunBot`.

**Source settings** (`contracts/sources/v1/crawl.schema.json`; the drawer shows them under "Advanced"):

| Setting | Choice | Why |
|---|---|---|
| `startUrls` | the support knowledge base's start page(s) | their hosts are the scope hosts |
| `include` | `<intranet host>/<kb path>/**` | keeps the crawl to the knowledge base, not the whole intranet |
| `exclude` | the defaults (search, sort, print, login, logout) plus the intranet's own noise (archives, news) | default list in the schema |
| `maxPages`, `maxDepth` | defaults 10,000 and 10 are ample for a support knowledge base | V25 caps |
| `noiseSelectors` | CSS selectors of the intranet template's changing parts (dates, "most read" boxes) | keeps the content hash stable, so unchanged pages are not re-processed (connectors §12.2) |
| `crawlWindow` | see [`03-updates.md`](03-updates.md) §1 | default `22:00-06:00` |
| `demote` | archive sections, if any | ranking only (knowledge §6.5) |

Files linked from the pages (PDF, DOCX, MD and the other allowed types) are fetched when their URLs are in scope
(`src/Ume.Rag.Worker/Connectors/Crawl/CrawlScope.cs:42-50`, `src/Ume.Rag.Worker/Connectors/Http/MediaTypes.cs:13`,
`:25`); images and video are excluded by default. Their citations then link to the file's intranet URL, which is
what staff expect. Links inside PDFs are not followed (P2, connectors §19).

**Site profile.** The only built-in preset is umea.se's Sitevision profile
(`contracts/site-profiles/v1/www.umea.se.profile.json`). For the intranet the first preview shows whether menus and
footers leak into the text. If they do, a platform admin creates a manual profile for the host (main root,
boilerplate selectors, breadcrumb; `POST /api/site-profiles`, `src/Ume.Rag.AdminApi/SiteProfileEndpoints.cs:73`).
Learned profiles are P2 (Decisions "Weakness review 1").

**URL list instead.** If the knowledge base is a small, fixed set of pages, a URL-list source fetches exactly those
URLs, nightly by default and outside the crawl window (`contracts/sources/v1/url-list.schema.json`;
`src/Ume.Rag.Worker/Engine/EngineSettings.cs:24-27`). It never follows links, so new pages need an edit of the list.

## 3. Source B: Word, PDF and Markdown files (upload)

| Topic | Today | Reference |
|---|---|---|
| Allowed types | `.pdf`, `.docx`, `.pptx`, `.xlsx`, `.odt`, `.ods`, `.odp`, `.jpg`/`.jpeg`, `.png`, `.tif`/`.tiff`, `.txt`, **`.md`**, `.csv`, `.html`/`.htm` | `src/Ume.Rag.AdminApi/UploadEndpoints.cs:36-39` |
| Refused until P2 | `.doc`, `.xls`, `.ppt`, `.rtf`, `.eml`, `.msg` (`upload.format_deferred`); macro-enabled Office and archives | connectors §9.2; postponed item Z1 |
| `.markdown` extension | refused by the admin's extension pre-check (only `.md` is listed), although the worker's sniffer knows it (`src/Ume.Rag.Worker/Connectors/Uploads/FileTypeSniffer.cs:265`). Rename to `.md` (gap SA-13) | – |
| Limits | ≤ 50 MB per file (source setting `maxFileSizeMb`), ≤ 100 files and ≤ 500 MB per batch | connectors §9.1 |
| Scanning | every upload passes ClamAV, fail-closed | connectors §11 |
| Backup | uploads are copied to the backup target within 15 minutes | connectors §9.1; [`../../../docs/runbook.md`](../../../docs/runbook.md) "Upload copy" |
| Citation | the file name is the citation's display name; the title comes from the parsed document | connectors §9.4 |

**Parsing.** PDF and Office files go to the Docling parser (parser design). Markdown and plain text are parsed in the
worker with Markdig: headings become sections, lists keep their depth, pipe tables become tables, fenced code stays
code (`src/Ume.Rag.Worker/Processing/Text/TextParsers.cs:13-26`). YAML front matter is not handled specially (the
pipeline enables only pipe tables, `TextParsers.cs:21`), so it would be indexed as text (unverified in a run): strip
it, or keep it out of the files.

**Chunking** (processing §6.2): 400-token chunks with a 60-token overlap inside a section, prefixed by the breadcrumb
and heading path. Articles with one heading per problem ("Outlook starts in safe mode", "VPN: error 809") retrieve
best, because each chunk then carries its problem in its prefix. That is advice for the editors, not a setting.

## 4. Markdown files kept elsewhere

Markdown files that live in a Git repository or on a file share have no connector (gap SA-10). Three routes, in the
order this plan recommends them:

1. **Publish them as intranet pages and crawl them.** A static-site build (any Markdown-to-HTML tool) on an intranet
   web server, with a sitemap, makes them a normal crawl source: citations link to real pages, edits arrive with the
   next crawl, deletions follow the deletion guard. No platform change.
2. **Serve the raw `.md` files** from an intranet web server and list them in a URL-list source (or crawl an index
   page that links them). Citations then open the raw file.
3. **Upload them** through the admin UI. Every later edit is a manual replace ([`03-updates.md`](03-updates.md) §2).

## 5. Steps

1. A platform admin checks the department exists in the organisation sync and grants the support admins
   `rag-department-admin` in Keycloak realm `ume` (admin §3.1; identity §11.2).
2. The department admin creates the knowledge base: name, contact, level `internal` (admin §5.4 step 2).
3. Adds the crawl source; preflight checks reachability, robots and sitemap (admin §5.4 steps 3–4).
4. Previews up to five pages and files and checks the extracted text, sections and chunks (admin §5.4 step 5); adds
   `noiseSelectors` or asks for a site profile if boilerplate shows.
5. Saves; the first sync runs; the run page shows failures by reason (admin §5.4 step 6).
6. Adds the upload source and uploads the Word, PDF and Markdown files.
7. Checks the personal-data report and held documents, if any (`GET /api/sources/{id}/pii-report`, `/held`).
8. Generates the evaluation set (admin §5.4 step 7; processing §13) and reads the report.
9. **Publishes the knowledge base.** Until then no API caller can search it (knowledge §4.5).
