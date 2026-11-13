# ingestion-connectors: history moved out of the design

The process sections of [`../../ingestion-worker/design-connectors.md`](../../ingestion-worker/design-connectors.md) (summary of decisions, milestones, changes to other documents, questions and their answers, deferred and removed items), moved here unchanged on 2026-11-13 (M-14) under their original numbers, so the citations of them still resolve. History: it does not bind; `plans/decisions.md` and the design do.

## 1. Summary of decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Contracts | Onyx-style capability interfaces; listing in the `sync` job, fetching in the `fetch` job; a typed checkpoint is committed with the work it covers (§2) |
| 2 | Identity | Own URL normaliser (RFC 3986 + fixed tracking/session parameter list + the site profile's session parameters); identity = **final URL after redirects**, mapped by the site profile's identity rule only when verified; `rel=canonical` folds an alias **only when verified** by equal content hash (§3) |
| 3 | Politeness | `CrawlHosts.NextAllowedAt` is the per-host budget shared by every source; each `fetch` takes its slot before the request. P1: each source has its own frontier (§6.0). **P2 (weakness review §1)**: per-URL `fetch` jobs are **released by a frontier releaser** at each host's rate, so the queue never holds more than ~2 s of work per host (§6) |
| 4 | Sharing | Documents belong to one source; raw blobs are content-addressed, parse output and embeddings cached by content (P1). **P2 (weakness review §1)**: one frontier row per (level, URL) with one **claim** per source, one request serves every claim, and a fetch younger than 12 h is reused by scheduled runs (§6.2) |
| 5 | Crawl runs | Every scheduled crawl is a full **traversal** with stored outlinks, skipping unchanged pages by conditional GET or reliable sitemap `lastmod`; `not-seen` deletions only after a **complete** traversal, complete when ≥ 99 % of seeds are resolved (weakness review §3); URLs that leave the sitemap get a removal check; an alert after 3 runs without a complete traversal (§5.4) |
| 6 | Egress | `IEgressGuard` resolves once and connects only to the checked addresses (`ConnectCallback`), max 5 redirects re-checked per hop; host firewall as second layer plus a startup canary; cookies never stored or sent (§7) |
| 7 | Renderer | **Now** (user, 2026-11-07): a `browser` source (`fetchMode`) is rendered by headless Chromium in a browser-only container driven by the worker, every browser request fetched by the worker's guarded client (§8) |
| 8 | Render coexistence | No document variants; the fetch mode is a document attribute (`Documents.FetchMode`, §8.1) |
| 9 | Uploads | Identity = (source, **upload slot**); the content hash is the version, so a replace keeps the document id; the file name is the document's `DisplayName`, never `Title` (§9) |
| 10 | Attachments | P2 with email (Z1); none in P1 (§10) |
| 11 | ClamAV | Own `INSTREAM` client; fail closed (no file enters parsing unscanned) while the scan is on; a deployment may turn it off with `ClamAv:Mode`, and its files are then marked as not scanned; quarantine blob kept 30 days, metadata only in the UI (§11) |
| 12 | Change detection | ETag/Last-Modified where sent, sitemap `lastmod` where reliable (measured on a 500-URL sample first, weakness review §3), then a hash over **noise-normalised** HTML (raw bytes for other types); a metric measures the remaining noise (§12) |
| 13 | Effect tags | Declared per setting in the source JSON schemas (`x-effect`); the worker plans from per-tag settings hashes, the admin shows the impact before save (§12.3) |
| 14 | Deletion guard | Proposals collected per run, applied in a **reconcile phase** after all fetches; pause above 20 % (min 5) or 500, or on an empty source; held documents after a rejection (§13) |
| 15 | SharePoint (P2) | Stored `deltaLink` per drive, tombstones and moves processed, 410 → enumeration through the guard, subscriptions as hints only; ACLs not mirrored (§14) |
| 16 | Site profiles | A **versioned profile per host**; P1: a manual profile JSON (umea.se: the hand-checked Sitevision preset); **P2 (weakness review §1)**: **learned**, (CMS preset such as Sitevision, extraction selectors, verified identity rule, session parameters, excluded types, `lastmod` reliability, JavaScript need), applied automatically with guardrails, audited, one-click rollback (§5.6) |

## 19. Milestones

| Phase | Deliverable |
|---|---|
| P1 | (items moved by weakness review §1 in §24) connector interfaces and runner with checkpoints; canonicaliser incl. verified identity rules and canonical folding; URL list; crawl with robots, sitemaps (`lastmod`), scope, `demote` patterns, traps, traversal with `lastmod` skip, incremental runs, completeness by ratio and the removal check (weakness review §3), report, incomplete-traversal alert; the 500-URL `lastmod` sample for umea.se (full traversals until it passes, weakness review §3); site profiles (manual profile JSON, the hand-checked Sitevision preset for umea.se, edit and rollback) with the umea.se acceptance test; one frontier per source (§6.0), host budgets (in-process connection cap), back-off, breaker, night window; egress guard + canary; uploads (slots, batches, sniffing, replace/delete, backup copy); ClamAV + quarantine; change detection with noise rules v1; effect tags; deletion guard with reconcile phase and commands; preflight for URL list and crawl; P1-start check whether pilot sites need JavaScript; tests of §18 |
| P2 | for external single-page apps a per-page burst of the page's own subrequests (A-13: ≈ 5.7 s per page at 1 req/s against ≈ 0.6 s in the browser) and a renderer asset cache kept across runs with conditional revalidation (the first pages of each run render cold; user 2026-11-07); site-profile learning, guardrails, proposals and re-learning (§5.6); shared frontier with claims and releaser (§6.1–§6.3); SharePoint (§14, S1–S10) with `IHierarchyConnector`; Redis per-host in-flight leases (first multi-host deployment); renderer behind `IRenderer` only if the P1-start check found a pilot site that needs JavaScript (§8); email with attachments as child documents and legacy Office/RTF (Z1); presets for WordPress and Episerver/Optimizely; PDF link extraction for crawls; quarantine release workflow if needed |
| P3 | more connectors on the same contracts; an alternative renderer backend behind `IRenderer` if anti-bot sites must be crawled (answered 14) |

## 20. Changes to other documents

Applied in the step 6b review commit (2026-10-29) and the consistency pass (step 11). Restated here in their
simplification-pass form; items marked **(SP)** are new with this rewrite and still to be applied by the owners.

| Document | Change |
|---|---|
| `platform/data-model.md` §2.2 `Documents` | `CanonicalHint text NULL`; `ContentHashVersion smallint NOT NULL`; `Title` never from an upload file name. **(SP, applied in data-model)** one row per (source, identity) with the former `DocumentSources` columns; `IdentityKind` `upload`; `SiteProfileVersionId`; no variants or attachment columns. **(SP, still needed)** uploads' `CanonicalIdentity` = `upload:<uploadId>` |
| `platform/data-model.md` §2.2 crawl and connector tables | `CrawlHosts` columns of §6.1; `CrawlFrontier` reshaped (§6.1); new `CrawlClaims`, `CrawlRuns`, `ConnectorCheckpoints`, `Uploads`, `DeletionProposals`, `QuarantinedFiles` (L); grants: worker S I U D, admin S. **(SP, applied)** no `RenderLease*`; claims without `Variant`; `SiteProfiles`/`SiteProfileVersions`. **(SP, still needed)** `CrawlFrontier` adds `ETag`, `LastModified`, `ContentHash`, `SitemapLastMod` (validators per URL, since one request serves every source; `Documents.ETag`/`LastModified` then redundant); `Uploads` one row per slot (§9.1 columns); `QuarantinedFiles.Origin` without `attachment` |
| `platform/data-model.md` §2.1, §4.2, §9 | `Runs.Phase`, `Report`, `SettingsHashes`; `Trigger` adds `config-change`, `notification` (P2); `Settings`: `DeletionGuardMinCount` 5, `DeletionGuardMaxCount` 500, `Crawl jsonb` (§6.3 budgets, overrides, blocked hosts, ports, freshness 12 h, window, limits, caps, redirects, identity rules version), `QuarantineRetentionDays` 30; dedup keys `fetch:{level}:{identityHash}`, `sync-upload:{batchId}`. **(SP)** `Settings.Crawl.IncompleteRunsAlert` 3; raw blobs content-addressed `raw/{level}/{contentHash}` (+ `.links.json.gz`; `internal`: wrapped key in the blob header, no per-upload or per-document keys); the upload blob is the document's raw content; no `:r` suffix, no child raw blobs |
| `ingestion-worker/design-engine.md` §9–§10 | `sync` phases and checkpoint reference; URL-list and crawl items become claims released by the releaser; reconcile phase and `awaiting-deletion-confirmation`. **(SP)** fetch stages `http-fetch`, `crawl`, `upload` only; `preview` kinds `preflight`, `site-profile`; `command` kinds `approve-site-profile`, `reject-site-profile`, `edit-site-profile`, `rollback-site-profile`; connectors give each source's document its real outcome (fact `fetch.shared`, no `shared` outcome needed) |
| `ingestion-worker/design-processing.md` **(SP)** | HTML extraction applies the host's site profile (main root, boilerplate, link blocks, breadcrumb, date pattern) in place of `cleanupRules: learned`; breadcrumb into the chunk prefix; email and child documents (§3.2) to P2 |
| `archive/area-plans/ingestion-worker.md` §3, §8 | final-URL identity; deletion guard min/max counts and exempt reasons. **(SP)** identity per source and upload slot; verified canonical folding and identity rules; site profiles; renderer P2 conditional; upload types per §9.2 |
| `platform/repo-and-deployment.md` | ClamAV freshness via `zVERSION`; `UME_RAG_CRAWL_ALLOW` feeds `Crawl__AllowedPrivateRanges`; egress canary; metrics of §16; trace filter removing `url.full`/`server.address`. **(SP)** no `rag-renderer` in P1; Redis crawl lease P2; upload backup copy within 15 minutes |
| `identity/design.md` §10 | attributes of 5101 (§7) and 5102 (§11); SIEM alert on any 5102. **(SP)** the 5101 `Purpose = Render` alert moves to P2 |
| `archive/area-plans/platform.md` §8 | quarantine 30 days (confirm); upload blobs purged 14 days after deletion or replacement |
| `archive/area-plans/admin.md` §4 | preflight results, impact count before scope/identity changes, crawl report (§5.5), upload list, quarantine list, deletion confirmation (§13.5), held documents; municipal hosts, host overrides, blocked hosts. **(SP)** no crawl hierarchy tree or two-variant warning; site profile preview in the source form, profile versions, proposals and rollback; upload replace; incomplete-traversal alert |
| `knowledge-api` | upload citations use the document's `DisplayName` **(SP: no "Bilaga … i …" in P1)**; consider de-duplicating chunks by `TextHash` within a collection (§3.1) |
| `postponed-decisions.md` | the crawler about page (answered 10) needs an owner and URL; SharePoint ACL mirroring if §21 Q2 is answered "later". **(SP)** Z1 joined by legacy Office, RTF and email with child documents |
| `archive/decisions-log.md` | "Connectors (step 6b)" row: final-URL identity, canonical trusted only when verified, claims + releaser, site profiles, guard with reconcile phase |

## 21. Answered questions (2026-10-29)

1. **External sites**: any department may crawl any public external host under our user agent and robots.txt
   (1 req/s); the preflight shows the host's robots status; platform admins can block hosts.
2. **SharePoint permissions (P2)**: not mirrored; content is visible to the collection's audience; the preflight
   warns about items with unique permissions. ACL mirroring is a possible P3 item.
3. **Malware findings**: SIEM alert, metadata-only admin view, no download or release in P1, quarantine purged after
   30 days.

Decided here without asking (reasons in the sections): final-URL identity (§3.2); canonical trusted only when verified
by content hash (§3.3, replaces "never trusted" after the umea.se findings); trailing slash and `index.html` kept
(§3.1); releaser instead of eager enqueue (§6.3); 12 h fetch freshness (§6.2); night window for all traversals (§6.5);
5 redirects (§7); worker-side browser fetching for a P2 renderer (§8.3); `lastmod` skip only for hosts whose profile
marks it reliable, with a full pass every 4th traversal (§5.4); crawled HTML not virus-scanned (§11); guard min 5 /
max 500 (§13.2); holds after rejection (§13.3).

## 23. Deferred or removed (simplification pass)

Per [`../archive/passes/simplification-pass.md`](../passes/simplification-pass.md):

| Item | Where it went |
|---|---|
| Renderer (`rag-renderer`, Playwright, render leases `CrawlHosts.RenderLease*`, JavaScript-only score, `render` source setting, `headless-render` fallback node, render metrics and health, `EgressPurpose.Render`) | Back in scope (user, 2026-11-07) as §8 describes it: per-source `fetchMode`, no render leases, no JavaScript-only score, no fallback node |
| Document variants `origin`/`rendered` (`Documents.Variant`, `RenderRecommendedAt`, `:r` dedup suffix, `variant-changed` exempt reason) | Removed: fetch mode will be a document attribute (§1) |
| `degraded` `render.not_enabled` for short pages | Removed: info note in the crawl report (§1) |
| Shared documents across sources (`DocumentSources` references, mirrored references, one pipeline per URL plus `EnsureCurrent` for the others, `shared` outcome) | Removed: documents per source, reuse by frontier and content-addressed caches (§2) |
| Upload identity `sha256:<hex>` shared across sources; replace = delete + add | Removed: identity (source, upload slot), content hash as version (§2) |
| Email attachments as child documents (§10, attachment intake, `attachment.depth`, EICAR attachment test) | P2 with email (Z1, §1) |
| Legacy `.doc`/`.xls`/`.ppt`, `.rtf`, `.eml`/`.msg` as allowed upload types | P2 (Z1), refused as `format_deferred` in P1 unless the pilot inventory shows them common (§1) |
| Redis per-host crawl in-flight lease (`ume-rag:crawl:inflight:{hostMac}`, `Security__CrawlKeyPepper`, Redis-down fallback ⌈`MaxConnections` ÷ k⌉) | P2, first multi-host deployment (§1); P1 uses an in-process semaphore |
| Crawl hierarchy (`IHierarchyConnector` for crawl, `preview` kind `hierarchy`) | Removed from P1 with the admin tree (simplification pass §10); the interface stays for SharePoint (P2) |
| "`rel=canonical` never trusted" | Replaced by "trusted only when verified" (§13.1) |
| Per-document upload encryption for `public` sources | Removed: `public` blobs are not envelope-encrypted (§4) |
| Blob moves (quarantine, raw per document) | Replaced by write-once keys, content-addressed raw blobs, tombstone + delayed purge (§2, §4) |

## 24. Deferred or removed (weakness review)

Per [weakness review §1](../passes/weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer):

| Item | Where it went |
|---|---|
| Site-profile learning (`preview` kind `site-profile`, sampling, field derivation), automatic application with guardrails, `proposed` versions with approve/reject, re-learning and its thresholds, `NextLearnAfter` | P2; P1 uses manual profiles of the same schema (umea.se: a hand-checked Sitevision preset) (§5.6) |
| Crawl frontier shared between sources: `CrawlClaims`, one request serving every claim, freshness-window reuse across sources, `fetch.shared`, `FrontierReleaser`, nav-link claim cache | P2; P1 has one frontier per source with the shared host budget (§6.0) |
| Re-processing plan from changed pipeline parameters | P2; P1 re-processes every document of the collection (§12.3) |
