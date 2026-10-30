# Ingestion worker, part B: connectors, crawl frontier, change detection, deletion guard

Detail step 6b of the Ume.Rag design. Details [`plan.md`](plan.md) §3 and §8 (binding, incl. answered items 1–14)
and consumes the contracts of [`design-engine.md`](design-engine.md) (6a): root nodes `http-fetch`, `crawl`, `upload`
and `headless-render`, the `sync`/`fetch` jobs and their fan-out (§9.2), dedup keys, `EnsureCurrent` and the drift
sweep (§2.3), follow runs, run outcomes (§10.2), the index writer (§14), config rules (§15) and the execution-id fence
(§7.5). Limits come from [`../platform/data-model.md`](../platform/data-model.md) §2, §4, §7–§9,
[`../platform/repo-and-deployment.md`](../platform/repo-and-deployment.md) §5.4, §5.5, §6, §9 and
[`../parser/design.md`](../parser/design.md) §3, §5, §11. Nothing is implemented. Changes needed elsewhere: §20;
questions: §21.

Citations: `onyx/…` = `~/git/rag-platforms/onyx/backend/onyx/…`, `owui/…` =
`~/git/rag-platforms/open-webui/backend/open_webui/…`, `eneo/…` = `~/git/eneo/backend/src/intric/…`;
`../../platforms/*.md` are our analyses.

## 1. Summary of decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Contracts | Onyx-style small capability interfaces; listing runs in the `sync` job, fetching in the root node of the `fetch` job; a typed checkpoint is a listing event and is committed with the work it covers (§2) |
| 2 | Identity | Own URL normaliser (RFC 3986 + fixed tracking/session parameter list); identity = **final URL after redirects**; `rel=canonical` is recorded and used for discovery, **never** for identity (§3) |
| 3 | Politeness | Per-URL `fetch` jobs are **released by a frontier releaser** at each host's rate, so the queue never holds more than ~2 s of work per host; `CrawlHosts.NextAllowedAt` is the shared budget (§6) |
| 4 | Sharing | One frontier row per (level, URL) with one **claim** per source; one fetch serves every claim; a fetch younger than 12 h is reused by scheduled runs (§6.2) |
| 5 | Crawl runs | Every scheduled crawl is a full **traversal** with conditional GETs and stored outlinks (a 304 page still yields its links); deletions only after a **complete** traversal (Eneo deletes after partial crawls) (§5.4) |
| 6 | Egress | `IEgressGuard` resolves once and connects only to the checked addresses (`ConnectCallback`), max 5 redirects re-checked per hop; host firewall as second layer plus a startup canary (§7) |
| 7 | Renderer | The worker **fetches every browser request itself** and fulfils it, so Chromium makes no connection of its own; the renderer leaves the `crawl` network (§8.3) |
| 8 | Render coexistence | **Variant in the document key**: `Documents.Variant` = `origin` / `rendered`; a source references one variant per URL; variants never share content hash, cache or chunk sets (§8.4) |
| 9 | Uploads | Identity `sha256:<hex>` shared across sources of a level; file names live on the reference (`DocumentSources.DisplayName`), never in `Documents.Title` (§9) |
| 10 | Attachments | Created in the parent's fenced parse commit; scanned in their own `fetch` job; references mirror the parent's; removed by identity on re-parse (§10) |
| 11 | ClamAV | Own `INSTREAM` client; fail closed (no file enters parsing unscanned); quarantine blob kept 30 days, metadata only in the UI (§11) |
| 12 | Change detection | ETag/Last-Modified, then a hash over **noise-normalised** HTML (raw bytes for other types); a metric measures the remaining noise (§12) |
| 13 | Effect tags | Declared per setting in the source JSON schemas (`x-effect`); the worker plans from per-tag settings hashes, the admin shows the impact before save (§12.3) |
| 14 | Deletion guard | Proposals collected per run, applied in a **reconcile phase** after all fetches; pause above 20 % (min 5) or 500, or on an empty source; held references after a rejection (§13) |
| 15 | SharePoint (P2) | Stored `deltaLink` per drive, tombstones and moves processed, 410 → enumeration through the guard, subscriptions as hints only; ACLs not mirrored (§14) |

## 2. Connector contracts

### 2.1 Interfaces (`Ume.Rag.Worker/Connectors`)

```csharp
public interface IConnector
{
    string SourceType { get; }                     // url-list | crawl | upload | sharepoint (Sources.Type)
    Type SettingsType { get; }                     // bound from Sources.Settings; schema contracts/sources/v1/{type}.schema.json
}

public interface IFetchConnector<TCheckpoint> : IConnector where TCheckpoint : ConnectorCheckpoint, new()
{
    // sync job: yields work and failures; a Checkpoint event is a safe resume point; Done states completeness.
    IAsyncEnumerable<ListingEvent<TCheckpoint>> ListAsync(ListingContext context, TCheckpoint from, CancellationToken ct);
    // fetch job, root node: one work item → content, 304, gone, skipped, refused or transient.
    ValueTask<FetchOutcome> FetchAsync(FetchContext context, WorkItem item, CancellationToken ct);
}

public abstract record ConnectorCheckpoint { public int SchemaVersion { get; init; } }

public abstract record ListingEvent<TCheckpoint>
{
    public sealed record Item(WorkItem Work) : ListingEvent<TCheckpoint>;
    public sealed record Failure(string Identity, string Reason) : ListingEvent<TCheckpoint>;
    public sealed record Checkpoint(TCheckpoint State) : ListingEvent<TCheckpoint>;
    public sealed record Done(Completeness Completeness) : ListingEvent<TCheckpoint>;   // Complete | Partial(reason)
}

public sealed record WorkItem(IdentityKind Kind, string CanonicalIdentity, DocumentVariant Variant,
    Uri? Url, short Depth, WorkHints Hints);                                // hints: sitemap lastmod, render score, upload id

public abstract record FetchOutcome
{
    public sealed record Content(RawContentRef Raw, Validators Validators, IReadOnlyList<Link> Links, FetchFacts Facts) : FetchOutcome;
    public sealed record NotModified(Validators Validators) : FetchOutcome;   // links come from the stored outlinks (§5.4)
    public sealed record Gone(string Reason) : FetchOutcome;                  // http.404, http.410, http.moved_out_of_scope
    public sealed record Skipped(string Reason) : FetchOutcome;               // robots.*, trap.*, redirect.out_of_scope
    public sealed record Refused(RunItemOutcome Outcome, string Reason) : FetchOutcome; // blocked, unsupported, too-large, malware
    public sealed record Transient(string Reason, TimeSpan? RetryAfter, bool CountsAttempt) : FetchOutcome;
}

public interface IIdListingConnector : IConnector        // cheap "what exists now" for deletion detection
{ IAsyncEnumerable<string> ListIdentitiesAsync(ListingContext context, CancellationToken ct); }

public interface IRetryFailedConnector : IConnector      // rebuild a work item from a failed run item
{ WorkItem FromIdentity(string canonicalIdentity, FailedItem last); }

public interface IHierarchyConnector : IConnector        // tree for the admin (crawl: host → path prefixes; SharePoint: site → drive → folder)
{ IAsyncEnumerable<HierarchyNode> GetChildrenAsync(HierarchyContext context, string? parentId, CancellationToken ct); }

public interface IPreflightCheck : IConnector            // runs on an unsaved configuration
{ IAsyncEnumerable<PreflightResult> CheckAsync(PreflightContext context, JsonElement settings, CancellationToken ct); }

public sealed record PreflightResult(string Check, PreflightState State /* pass | warn | fail */, string Code,
    LocalizedText Remediation, JsonElement? Facts);
```

C# async iterators cannot return a value, so the checkpoint is an event (Onyx returns it from the generator and its
runner enforces "exactly one, at the end", `onyx/connectors/connector_runner.py:58-95`); our runner enforces "the last
event before `Done` is a `Checkpoint`". Connectors get no `DbContext` (as nodes, 6a §5.1): frontier, documents and
run items are written by the runner.

| Connector | Fetch | Id listing | Retry failed | Hierarchy | Preflight |
|---|---|---|---|---|---|
| `UrlListConnector` | yes | yes (the configured list) | yes | – | yes |
| `CrawlConnector` | yes | no: deletions come from a complete traversal (§5.4) | yes | yes (frontier path tree) | yes |
| `UploadConnector` | yes | yes (`Uploads` not deleted) | yes | – | – |
| `SharePointConnector` (P2) | yes | yes (delta state / enumeration) | yes | yes | yes (grants) |

### 2.2 Mapping onto 6a jobs and roots

| Step | Job (6a §9.1) | What runs |
|---|---|---|
| Listing | `sync`, phase `list` | `ListAsync`; items become frontier **claims** (URL list, crawl) or direct `fetch` jobs (uploads, attachments, SharePoint); yields after 1,000 items or 2 min (6a §9.2) |
| Fetch | `fetch` | root node `http-fetch` / `crawl` / `upload` calls `FetchAsync`; on `Content` with a new content hash it commits the document (new `CurrentExecutionId`, 6a §7.5) and continues the pipeline in-process; on `NotModified` or same hash it calls `EnsureCurrent` (6a §2.3) |
| Discovery | `fetch` | `Content.Links` / stored outlinks become new claims for every source claiming the URL (§6.2) |
| Reconcile | `sync`, phase `reconcile` | deletion candidates, guard, soft deletes (§13); enqueued by the finalizer when a run has no open jobs |
| Retry | `sync` (`kind = retry-failed`) | `FromIdentity` for each failed item of the last run; no reconcile deletions |
| Preflight, hierarchy | `preview` (new kinds `preflight`, `hierarchy`) | interactive, highest priority; results in `Jobs.Result`; a URL-list preflight samples ≤ 20 URLs (admin design §4.7) |

Email attachments are not listed by any connector: the parent's parse commit creates them (§10) and their `fetch`
job runs the **attachment intake** instead of a root (roots hash alike, 6a §3.3, so the child's document stage
matches the parent's pipeline).

### 2.3 Checkpoints and resume

- Stored in `content."ConnectorCheckpoints"` (tier-local: may hold paths), not in the job payload (4 KB cap, a
  SharePoint delta link alone is ~500 bytes per drive): `SourceId`, `Classification`, `Kind` (`run` = position of
  the current listing; `state` = durable cursor such as delta links), `SchemaVersion`, `Checkpoint jsonb`,
  `RunId`, `UpdatedAt`. Serialised with System.Text.Json source generation.
- A `Checkpoint` event is committed **in the same transaction** as the claims, `fetch` jobs and run items produced
  since the previous one. A crash or lease loss (6a §9.4) loses only uncommitted work; resuming re-lists from the
  last checkpoint, and dedup keys plus upserts absorb repeated items. A yield re-enqueues the `sync` job with the
  checkpoint reference (no attempt, 6a §9.2).
- Unknown `SchemaVersion` (rolled-back worker) → listing restarts from the beginning, which is always safe because
  listing is idempotent. A `state` checkpoint is replaced only when its listing reaches `Done`.
- Crawl checkpoints are tiny (seed phase and sitemap cursor) because the traversal state lives in the frontier.

## 3. Canonical identity

### 3.1 URL normalisation (`UrlCanonicalizer`, `rules v1`)

Applied to every URL before it becomes a claim, an identity or a comparison key. Parsing uses `System.Uri` only to
split components; the rules below are our own, so .NET's escaping quirks never change an identity (*to verify*, §22).

| # | Rule | Example |
|---|---|---|
| 1 | Only `http`/`https`; scheme lower-case; http and https stay **different** identities (a redirect unifies them) | `HTTPS://` → `https://` |
| 2 | Host lower-case, trailing dot removed, IDN → A-label (UTS 46 non-transitional, `IdnMapping`, STD3 rules) | `Umeå.SE.` → `xn--ume-loa.se` |
| 3 | Default port removed (80/443); userinfo refused (`url.userinfo`) | `:443/` → `/` |
| 4 | Empty path → `/`; dot segments removed (RFC 3986 §5.2.4); duplicate slashes kept (servers differ) | `/a/./b/../c` → `/a/c` |
| 5 | Percent-encoding: decode unreserved (`A–Z a–z 0–9 - . _ ~`), upper-case remaining hex, encode non-ASCII as UTF-8; reserved escapes (`%2F`, `%3F`) kept | `%7e` → `~`; `ö` → `%C3%B6` |
| 6 | Fragment removed (also `#!`); SPA hash routing is not supported in P1 | `/a#top` → `/a` |
| 7 | Query: drop the parameters in the list below and the source's `ignoreQueryParams`; sort the rest by name (stable for repeated names); drop an empty `?` | `?b=2&utm_source=x&a=1` → `?a=1&b=2` |
| 8 | Session tokens in the path removed: `;jsessionid=…`, ASP.NET `/(S(…))/` | `/p;jsessionid=AB` → `/p` |
| 9 | Trailing slash **not** changed; `index.html` **not** removed (both can be different resources); the source setting `directoryIndexNames` opts in | – |

Dropped query parameters (case-insensitive): `utm_*`, `gclid`, `gbraid`, `wbraid`, `dclid`, `fbclid`, `msclkid`,
`yclid`, `twclid`, `igshid`, `mc_cid`, `mc_eid`, `_ga`, `_gl`, `_hsenc`, `_hsmi`, `hsCtaTracking`, `mkt_tok`,
`jsessionid`, `phpsessid`, `aspsessionid*`, `sessionid`, `cfid`, `cftoken`. The list and rules carry a version
(`Settings.Crawl.IdentityRulesVersion`); changing it is a release-notes item
with a one-time identity re-sync of crawl and URL-list sources (identity tag, §12.3).

`IdentityHash` = SHA-256 of the canonical identity (data-model §2.2). Duplicates the rules leave (trailing slash,
`index.html`, mirror hosts) are **reported**, not guessed: the crawl report lists groups of URLs with equal content
hash and suggests a setting or an exclude pattern (§5.5).

### 3.2 Redirects

- Followed manually, at most **5 hops**, each hop re-checked by the egress guard, the source scope and the target
  host's robots.txt (§7). Loops and hop 6 → `failed` `redirect.limit`; https → http → `blocked` `redirect.downgrade`.
- **Identity = final URL**, whatever the redirect type, so `/` and `/start` that both land on `/start` are one
  document. The run item records the chain; the frontier row of the first URL stores `RedirectTo`.
- Target outside the source scope: permanent (301/308) on a known reference → deletion candidate
  `http.moved_out_of_scope`; temporary (302/303/307, typically a sign-in wall) → `skipped` `redirect.out_of_scope`,
  never a deletion. Target blocked by robots → `blocked` (6a §10.2).

### 3.3 `rel=canonical`

Not trusted for identity. Misconfigured canonicals (every page pointing at the start page, at a staging host, at
http) are common on CMS sites, and acting on one drops content silently, the worst failure class here (comparison
F6/F7). The declared canonical is stored (`Documents.CanonicalHint`), its URL is added as a discovered link (same
scope rules), and the crawl report lists pages whose canonical points elsewhere so an admin can add an exclude.

### 3.4 Uploads and attachments

| Kind | `IdentityKind` | `CanonicalIdentity` | Notes |
|---|---|---|---|
| URL | `url` | canonical final URL | + `Variant` (§8.4) |
| Upload | `file` | `sha256:<hex of bytes>` | shared by every source of the level that uploads the same bytes (R2); file name on the reference (§9.4) |
| Attachment | `attachment` | `<parent identity>#att/<ordinal>/<sha256>` | parser design §5.4; identical attachments in two emails are two documents (data-model §12.13) |
| SharePoint item (P2) | `sharepoint` | `sharepoint:<driveId>:<itemId>` | stable across moves and renames (§14) |

## 4. URL list

- Exactly the listed URLs (≤ 5,000 per source), normalised on save by the admin with the shared canonicaliser (so
  the form shows duplicates), never following links. Listing yields claims at depth 0; politeness and sharing are
  the frontier's (§6).
- **Conditional GET**: `If-None-Match` with the stored ETag (weak ETags sent as received) and `If-Modified-Since`
  with the stored `Last-Modified` string (never our own clock). Validators are stored only from 200 responses.
  A forced run (`force`, or every `revalidateEvery`-th run, default 4) sends no validators, catching servers whose
  304s are wrong.

| Response | Result | Outcome / reason | Deletion |
|---|---|---|---|
| 200 | content; stage 2 change detection (§12) | `added` / `updated` / `unchanged` | – |
| 304 | `NotModified`; `EnsureCurrent` | `unchanged` | – |
| 301/302/303/307/308 | follow (§3.2) | – | §3.2 |
| 404, 410 | `Gone` | known reference: `deleted` after reconcile; never indexed: `failed` `http.404` (a listed URL is broken) | candidate |
| 401, 403 | failure | `failed` `http.403` | after 4 consecutive runs and 14 days (§13.1) |
| 429, 503 with `Retry-After` | `Transient`, no attempt; host back-off (§6.4) | – | – |
| other 5xx, timeout, connection reset, DNS failure | `Transient` with attempt (5, data-model §4.2), then `failed` `http.5xx` / `net.timeout` / `net.dns` | – | as 401 |
| TLS failure | `failed` `tls.invalid_certificate`; no "ignore certificate" option (internal hosts use the internal CA bundle, repo-and-deployment §6.3) | – | as 401 |

**Limits**: HTML/XHTML ≤ 10 MB, other types ≤ 50 MB (= upload limit; parser body 60 MB, parser §11.1), counted
after decompression (a gzip bomb hits the cap); response headers within 15 s; whole body within 30 s (HTML) or 120 s
(files). Accepted content types are the template's `route` cases (6a §13.1) plus `application/xml`/`text/xml` for
sitemaps; anything else stops before download (`unsupported` `http.content_type`, only the headers are read).

## 5. Website crawl

### 5.1 Seeds: robots.txt and sitemaps

- **Seeds** = start URLs + URLs from sitemaps (depth 0). Sitemaps come from the source's `sitemaps`, every
  `Sitemap:` line of the scope hosts' robots.txt, and `/sitemap.xml` if it exists.
- **robots.txt** (RFC 9309, own parser of ~250 lines: the group matching `UmeaKommunBot` (case-insensitive) else
  `*`; longest match wins, `Allow` beats `Disallow` on ties; `*` and `$`; 500 KiB parse cap). Cached per host in
  `CrawlHosts` + blob `crawl/{host}/robots.txt` (data-model §9) for 24 h. Fetch outcomes: 2xx parse; 4xx →
  **allow all** (RFC 9309 "unavailable"); 5xx or unreachable → **disallow all** for the run (`RobotsStatus =
  unreachable`; the run's seeds on that host fail, so the traversal is incomplete and nothing is deleted).
  robots.txt is always honoured (answered 4, 11), also on municipal hosts; it is re-checked per redirect hop.
- `Crawl-delay` is honoured only when it **lowers** our rate, capped at 60 s.
- **Sitemaps**: `XmlReader` with `DtdProcessing.Prohibit` and no resolver; gzip allowed; sitemap index depth ≤ 2;
  ≤ 50,000 URLs and ≤ 50 MB uncompressed per file (sitemaps.org limits) and ≤ `maxPages` in total; out-of-scope
  entries dropped and counted. `lastmod` is used only by incremental runs (§5.4).

### 5.2 Scope

| Rule | Definition |
|---|---|
| Scope hosts | hosts of the start URLs + `extraHosts` (answered 13) |
| Patterns | `include` / `exclude` lists over the canonical URL without scheme (`host/path?query`). Glob: `*` = any characters except `/`, `**` = any characters, everything else literal (also `?` and `.`); host part case-insensitive, path case-sensitive. Advanced: `re:<regex>` (.NET `RegexOptions.NonBacktracking`, 50 ms timeout). Exclude wins; empty `include` = everything on the scope hosts. Eneo passes the start URL itself as a regex (`LinkExtractor(allow=url)`, `eneo/crawler/spiders/crawl_spider.py:25-34`), so `.` and `?` match anything |
| Off-site | a link to a non-scope host is admitted **only** if an `include` pattern names that host explicitly; it is fetched as a **leaf** (its links are never followed). Otherwise counted in the report, not fetched (answered 13) |
| Depth | link distance from a seed; default `maxDepth` 10 (answered 12); a redirect does not add depth |
| Pages | default `maxPages` 10,000 (answered 12), counted as **admitted claims** per run (`CrawlRuns.Admitted`), seeds included. Reaching it: no more admissions, run `degraded` `crawl.limit_pages`, traversal partial → no deletions |
| Municipal host | host name matches `Settings.Crawl.MunicipalHosts` (platform-admin list, e.g. `umea.se`, `*.umea.se`; audited). Decides rate (§6.3) and `noindex` handling. Separate from the **address** allow-list `UME_RAG_CRAWL_ALLOW` (§7), which only lifts the private-range block: an internal municipal host on 10.x must be in both |
| `noindex` / `nofollow` | meta robots and `X-Robots-Tag` (also the `UmeaKommunBot:`-prefixed form; `none` = both). **External host**: `noindex` → `skipped` `robots.noindex` (links still followed unless `nofollow`); `nofollow` → no links taken from that page. **Municipal host**: both ignored, recorded as facts (answered 11). `rel="nofollow"` on single links is a ranking hint and ignored everywhere |
| Content types | links to types outside the template's cases are not admitted (`.zip`, `.exe` never fetched); archives and macro-enabled files that are fetched anyway end `unsupported` (Z1) |

### 5.3 URL-space traps

Detected per run in the fetch path; each detection is a `skipped` `trap.<kind>` for admitted URLs and a counter
with an example pattern in the report.

| Trap | Detection | Action |
|---|---|---|
| Endless paths | URL > 1,024 chars, > 15 path segments, or a segment sequence repeated ≥ 3 times (`/a/b/a/b/a/b`) | not admitted |
| Faceted search, sorting | > 50 distinct query strings admitted for one (host, path) in a run, or > 6 query parameters | further variants not admitted (`trap.query_variants`) |
| Calendars | a path or query value parses as a date/year outside [now − 5 y, now + 2 y], or > 100 date-like URLs under one path prefix | not admitted (`trap.calendar`) |
| Session ids | removed by normalisation (§3.1 rule 7–8); > 20 URLs whose only difference is one unknown parameter → that parameter is reported as a candidate for `ignoreQueryParams` | report |
| Soft-404 / duplicate fan-out | ≥ 20 URLs in a run with the same content hash | their links are expanded once per content hash (a page already expanded adds nothing new) |
| Default excludes in the `website` template form | `**/sok?*`, `**/search?*`, `**?*sort=*`, `**?*print=*`, `**/login*`, `**/logout*` | editable |

### 5.4 Traversal, incremental runs and deletions

- **Traversal** (every scheduled crawl, default weekly Saturday 01:00 with jitter, 6a §9.6; answered 7): BFS from the
  seeds through the frontier. Every reached URL gets a conditional GET; a 304 or an unchanged hash expands links from
  the **stored outlinks** of that content (`documents/{id}/raw/{contentHash}.links.json.gz`, written with the raw
  blob), so an unchanged site costs one request per page and no parsing (plan §9 target).
- **Complete** traversal = no admitted claim of the run is left pending, no seed failed, no host was paused for the
  run (§6.4) and the page limit was not hit (`CrawlRuns.Complete`). Only then are references not reached in this run
  deletion candidates (`not-seen`). Eneo salvages a timed-out crawl and then deletes every page it did not reach
  (`eneo/worker/crawl_tasks.py:1008-1014`, `:1206-1232`); acceptance test C7 (§18) covers that case.
- Explicit `Gone` (404/410, permanent redirect out of scope, robots now disallowing) is a candidate in any run,
  complete or not.
- **Incremental** run (optional `incrementalSchedule`, e.g. daily for a news site with a sitemap): fetches seeds
  whose sitemap `lastmod` is newer than the stored `FetchedAt` plus sitemap URLs not yet known, expands links only
  from changed pages, never marks a traversal complete, so it deletes only on `Gone`.
- `revalidateEvery` (default 4): every 4th traversal sends no validators (§4).

### 5.5 Crawl report

Built by the finalizer from `RunItems` and `CrawlRuns` into `Runs.Report` (jsonb) and shown on the source page
(admin plan §4). URLs appear only in the admin (tier-local data), never in telemetry (§16).

| Section | Contents |
|---|---|
| Summary | traversal complete or why not (limit, seeds failed, host paused, cancelled); pages admitted / fetched / not modified / indexed; duration; requests per host and effective rate |
| Failures by reason | count per `Reason` code with ≤ 10 example URLs each; broken links (`http.404` of never-indexed URLs) separately; Eneo's `failure_counts` idea (`eneo/worker/crawl_tasks.py:956-958`) |
| Exclusions | robots-disallowed, out of scope, off-site, excluded by pattern, `noindex`: counts + top path prefixes |
| Traps | kind, pattern, count, example |
| Duplicates | groups of URLs with equal content hash; pages with a canonical elsewhere; suggested setting |
| Rendering | pages rendered, JavaScript-only detections, render failures, `render.not_enabled` pages |
| Noise | pages whose hash changed but whose parsed output did not (§12.2), by host |
| Deletions | proposed / applied / held, by reason (§13) |

## 6. Shared crawl frontier and politeness (R2)

### 6.1 Tables

| Table | Tier | Columns (new in **bold**) |
|---|---|---|
| `CrawlHosts` | M | `Host` PK, `RequestsPerSecond`, `MaxConnections`, `NextAllowedAt`, `RobotsBlobPath`, `RobotsFetchedAt`, **`Kind`** (`municipal`/`external`, derived from settings), **`RobotsStatus`**, **`CrawlDelaySeconds`**, **`BackoffFactor numeric(6,2)`**, **`PausedUntil`**, **`LatencyEwmaMs`**, **`RenderLeaseOwner`**, **`RenderLeaseUntil`**, **`BlockedAt`** (platform admin block) |
| `CrawlFrontier` | L | PK (`Classification`, `UrlHash`), `Url`, `Host`, `State` (**`idle`/`pending`/`released`**), `NextFetchAt`, **`LastFetchedAt`**, **`LastStatus smallint`**, **`RedirectTo`**; `SourceIds` and `Depth` move to claims |
| **`CrawlClaims`** | L | PK (`Classification`, `UrlHash`, `SourceId`), `RunId`, `Depth smallint`, `Variant`, `Leaf bool`, `ClaimedAt`; IX (`RunId`) |
| **`CrawlRuns`** | L | `RunId` PK, `Classification`, `SourceId`, `Admitted int`, `MaxPages`, `MaxDepth`, `LimitHit`, `SeedsFailed`, `PausedHosts jsonb`, `TrapStats jsonb`, `Complete bool` |

### 6.2 Claims: one fetch for every source

1. A source admits a URL by upserting its claim; the statement returns only claims new to this run, so a URL is
   admitted once per run and the page counter stays exact:

```sql
INSERT INTO content."CrawlClaims" ("Classification", "UrlHash", "SourceId", "RunId", "Depth", "Variant", "Leaf", "ClaimedAt")
SELECT @level, u.h, @source, @run, @depth, u.v, u.leaf, now() FROM unnest(@hashes, @variants, @leaf) AS u(h, v, leaf)
ON CONFLICT ("Classification", "UrlHash", "SourceId") DO UPDATE
   SET "RunId" = EXCLUDED."RunId", "Depth" = EXCLUDED."Depth", "Variant" = EXCLUDED."Variant", "ClaimedAt" = now()
 WHERE content."CrawlClaims"."RunId" IS DISTINCT FROM EXCLUDED."RunId"
RETURNING "UrlHash";
```

2. The frontier row (insert if absent) goes to `pending`, unless it was fetched within the **freshness window**
   (`Settings.Crawl.FreshnessHours`, 12 h; 0 for manual and forced runs): then the claim is served at once from the
   stored document and outlinks, without a request.
3. The **releaser** (§6.3) enqueues one `fetch` job per (URL, variant) with `DedupKey =
   fetch:{level}:{identityHash}` (`:r` appended for the rendered variant, §8.4).
4. The `fetch` job reads all claims of open runs for its URL at commit and serves each `(source, run)`: reference
   upsert (`DocumentSources.LastSeenAt`, `LastRunId`), run item, and new claims from the links under **that**
   source's scope and depth. The pipeline continues for the first claim's pipeline; the others get `EnsureCurrent`
   (6a §2.3).
5. Outcome per served run: the real one (`added`, `updated`, `unchanged`, …) when that run's reference changed or its
   collection stage ran in this run; `shared` (6a §10.2, reason names the run) when another run had already brought
   it current.

Two departments crawling umea.se on the default Saturday schedule therefore cause one request per page (plan §9).

### 6.3 Releaser and host budgets

`FrontierReleaser`: `BackgroundService`, `PeriodicTimer` 1 s, leader by `pg_try_advisory_xact_lock` (6a §9.6
pattern), per tier. For each host with `pending` entries and `NextAllowedAt < now() + 2 s`, not paused or blocked:
release `max(0, ⌈rate × 2 s⌉ − released-but-unfinished)` entries, ordered by manual runs first, then depth, then
`ClaimedAt` (BFS), as `fetch` jobs with `AvailableAt` spaced by the host interval. The queue then never holds more
than ~2 s of work per host, and a 10,000-page external crawl does not churn 10,000 waiting jobs.

The fetch job still takes its slot atomically before the request (the releaser can be early by a tick, and previews
fetch without the releaser, 6a §11):

```sql
UPDATE content."CrawlHosts"
   SET "NextAllowedAt" = greatest("NextAllowedAt", now()) + make_interval(secs => @interval)
 WHERE "Host" = @host AND "BlockedAt" IS NULL AND ("PausedUntil" IS NULL OR "PausedUntil" < now())
RETURNING "NextAllowedAt" - make_interval(secs => @interval) AS "Slot";
-- Slot > now() + 1 s → requeue without attempt at Slot (6a §7.3); no row → host blocked or paused
```

| Budget | Municipal | External |
|---|---|---|
| Rate | ≤ 10 req/s (answered 4) | 1 req/s (answered 4) |
| Connections | 4 | 1 (answered 4) |
| Interval | `max(1/rate, Crawl-delay, LatencyEwma / 4) × BackoffFactor` | `max(1/rate, Crawl-delay, LatencyEwma) × BackoffFactor` |

Rates are per host and shared by every source, preview and render of every level (the table is main-only). The
connection cap is enforced in memory per instance; with k worker instances it is k × the cap while the rate stays
exact (accepted until the ingestion zone moves to its own host, repo-and-deployment §6.4). Platform admins may
override a host's budget (`Settings.Crawl.HostOverrides`) and block hosts.

### 6.4 Back-off and circuit breaker

- **429 or 503**: `NextAllowedAt = now + Retry-After` (seconds or HTTP date; default 60 s; capped at 1 h),
  `BackoffFactor × 2` (max 16), the job requeues without an attempt (`rate_limited`). Each success multiplies the
  factor by 0.95 down to 1. A `Retry-After` above 1 h pauses the host for the run (`PausedUntil`), the traversal is
  incomplete and the run says why.
- **Circuit breaker**: ≥ 50 of the last 100 responses from a host are 5xx, timeouts or 404 → host paused for 30
  minutes and recorded in `CrawlRuns.PausedHosts`; three pauses in a run end the host's part of the run (incomplete).
  This stops a broken CMS from being hammered and keeps a 404 storm from becoming deletions (§13).
- Slow servers lower our rate through `LatencyEwmaMs` (Scrapy's autothrottle idea).

### 6.5 Night window

Traversals release claims only inside the source's `crawlWindow` (default `22:00–06:00` Europe/Stockholm, every
day; answered 4). A traversal not finished at 06:00 pauses and continues the next night; the run stays `running`
and shows "waiting for crawl window". At 10 req/s a 10,000-page municipal site takes ~17 minutes; at 1 req/s an
external one ~2.8 h, both inside one window. Manual "sync now" and URL lists (nightly by default) ignore the window.

## 7. Egress / SSRF guard

```csharp
public interface IEgressGuard
{
    EgressDecision CheckUrl(Uri url, EgressPurpose purpose);   // scheme, port, userinfo, literal IPs, blocked hosts
    // Resolves once; throws EgressBlockedException (→ outcome blocked, event 5101) if any address is not allowed.
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string asciiHost, EgressPurpose purpose, CancellationToken ct);
}
public enum EgressPurpose { Fetch, Robots, Sitemap, Render, Preflight, Preview }
```

| Topic | Rule |
|---|---|
| DNS pinning | One `SocketsHttpHandler` for all crawling with `ConnectCallback`: resolve via the guard, then connect **only** to the returned addresses (no second lookup, no rebinding window). TLS still uses the URL host for SNI and certificate checks. `PooledConnectionLifetime` 2 min, `UseProxy = false`, `AllowAutoRedirect = false`, `UseCookies = false`. Same as Open WebUI's pinned connect (`owui/retrieval/web/utils.py:187-215`) and Onyx's pinned request (`onyx/utils/url.py:417-435`) |
| Any-address rule | if **any** resolved address is blocked, the host is refused (round-robin rebinding cannot win by luck) |
| Blocked IPv4 | `0.0.0.0/8`, `10/8`, `100.64/10`, `127/8`, `169.254/16`, `172.16/12` (covers our compose subnets), `192.0.0/24`, `192.0.2/24`, `192.88.99/24`, `192.168/16`, `198.18/15`, `198.51.100/24`, `203.0.113/24`, `224/4`, `240/4` |
| Blocked IPv6 | `::/128`, `::1`, `fc00::/7`, `fe80::/10`, `ff00::/8`, `2001:db8::/32`, `100::/64`; addresses that **embed** IPv4 (`::ffff:0:0/96`, `::/96`, `64:ff9b::/96`, `64:ff9b:1::/48`, 6to4 `2002::/16`, Teredo `2001::/32`) are judged by the embedded address too (Open WebUI `_embedded_ipv4`, `owui/retrieval/web/utils.py:73-93`). IPv4 is tried first; the `crawl` network may have no IPv6 at all (*to verify*) |
| Allowed exceptions | CIDRs in `Crawl:AllowedPrivateRanges`, set from the same `UME_RAG_CRAWL_ALLOW` that feeds the host firewall (`init-deployment.sh --crawl-allow`, repo-and-deployment §7.1); nothing else can lift the block |
| URL rules | `http`/`https`; ports 80 and 443 (`Settings.Crawl.AllowedPorts` may add e.g. 8443); no userinfo; IP-literal hosts only if global or allow-listed; IDN hosts checked in A-label form after UTS 46 mapping, so homograph spellings resolve to the same rules |
| Redirects | manual; ≤ 5 hops; each hop through `CheckUrl` + `ResolveAsync` + scope + robots (§3.2) |
| Firewall | the `crawl` network drops RFC 1918, link-local, loopback and the host's addresses except the allow-list (repo-and-deployment §6.1); the guard is the first layer and decides the outcome text |
| Canary | at startup in Production the worker opens a raw socket (bypassing the guard) to `169.254.169.254:80` and to a private address outside the allow-list; a successful connect means the firewall is missing: critical log, health `degraded`, crawling disabled until fixed |
| Event 5101 `rag.egress.blocked` | Warning; at most once a minute per (source, reason) with `Count`. Attributes: `Reason` (`private_address`, `embedded_address`, `scheme`, `port`, `userinfo`, `blocked_host`, `redirect_downgrade`), `AddressClass` (`loopback`, `rfc1918`, `link_local`, `cgnat`, `ula`, `multicast`, `reserved`), `Purpose`, `SourceId`, `RunId`, `Classification`, `HostHash` (HMAC); the host name itself only for `public` sources |

## 8. Renderer

### 8.1 When to render

Source setting `render`: `off` (default) / `auto` / `always`; the `crawl` root's param `render` (6a §6) keeps its
meaning: `never` = never at the root, `if-enabled` = per the source setting, `always` = every HTML page if the
source allows rendering at all.

| Source `render` | Root renders a page when | Fallback node `headless-render` (6a §13.1) |
|---|---|---|
| `off` | never | passes through, fact `render.not_enabled`, outcome `degraded` |
| `auto` | the origin HTML scores as JavaScript-only (below), or `Documents.RenderRecommendedAt` is set for the URL | renders; if the rendered text is ≥ 2× the origin's, sets `RenderRecommendedAt`, so the next run renders at the root |
| `always` | every HTML page | renders (rarely reached) |

**JavaScript-only score** (cheap, on the origin DOM the root already parses for links): visible body text < 200
characters while the HTML is > 5 KB (+2); an empty mount element `#root`, `#app`, `#__next`, `#__nuxt`, `[ng-app]`
(+2); `<noscript>` text asking to enable JavaScript (`JavaScript`, `aktivera`, `enable`) (+2); ≥ 1 script bundle
> 100 KB (+1); embedded server data with text (`__NEXT_DATA__`, `__NUXT__`, `application/ld+json` with content)
(−3). Score ≥ 4 → render. The score and its parts are facts on the run item, so wrong guesses are visible.

### 8.2 Contract (`IRenderer`, backend-agnostic per answered 14)

```csharp
public interface IRenderer
{
    ValueTask<RenderResult> RenderAsync(RenderRequest request, IRenderFetcher fetcher, CancellationToken ct);
}
public sealed record RenderRequest(Uri Url, FetchedResponse MainDocument, TimeSpan Timeout /* 30 s */,
    RenderWait WaitFor /* NetworkIdle | Load | Selector */, string? Selector, long MaxDomBytes /* 10 MB */,
    int MaxSubrequests /* 200 */, long MaxTransferBytes /* 20 MB */);
public sealed record RenderResult(Uri FinalUrl, string Html, string? Title, IReadOnlyList<Link> Links,
    RenderStats Stats /* subrequests, blocked by reason, bytes, console errors, durations */, bool Truncated);
```

| Rule | Value |
|---|---|
| Browser | Playwright `BrowserType.ConnectAsync` to the `rag-renderer` server (repo-and-deployment §5.4); **one fresh context per page**, closed afterwards |
| Context | `JavaScriptEnabled`, `AcceptDownloads = false`, `ServiceWorkers = Block`, no permissions, no geolocation, locale `sv-SE`, viewport 1280×2000, our user agent (answered 10), no stored cookies, no consent clicks |
| Blocked resource types | `image`, `media`, `font`, `websocket`, `eventsource`, `manifest`, `other`; scripts, stylesheets, XHR/fetch allowed |
| Popups, dialogs, downloads | new pages closed at once, dialogs dismissed, a download aborts the page (`render.download_attempt`) |
| Wait | `networkidle` (default) or a selector, then `document.documentElement.outerHTML`; DOM > 10 MB → truncated + `degraded` |
| Concurrency | per host **1** (answered 2) via `CrawlHosts.RenderLeaseUntil` (45 s lease, shared across instances); globally `Renderer:MaxPages` = 2 (1 CPU container) |
| Timeouts | page 30 s; whole render job 45 s; on timeout the origin content is indexed, outcome `degraded` `render.timeout` |
| Subresource politeness | ≤ 4 in flight per page; assets cached in worker memory per run (LRU 200 MB, by URL + validators), so a site's bundles are fetched once per run, not per page |

### 8.3 Egress: the worker fetches for the browser

A route handler on `**/*` that only *checks* URLs is not enough: Chromium resolves the host again after the check
(rebinding), and Playwright route handlers do not see redirect hops (`onyx/utils/playwright_fetch.py:407`). Open
WebUI therefore fetches every intercepted request itself and fulfils it (`owui/retrieval/web/utils.py:850-900`). We
do the same: `Page.RouteAsync("**/*")` → blocked type → `AbortAsync`; otherwise the worker fetches the request with
the guarded client (`IRenderFetcher`: method, allowed headers, body; redirects followed and re-checked; size caps)
and calls `FulfillAsync` with the final response. The main document is fulfilled from the bytes the root already
fetched. Consequence: **Chromium never opens a network connection**, so `rag-renderer` leaves the `crawl` network
and stays on the internal `ingestion` network only (change, §20); the browser's egress rules are the crawler's by
construction (answered 2). WebSockets are blocked (no interception in our fetcher).

### 8.4 Rendered and non-rendered content of one URL (6a §13.1 open item)

**Decision: the variant is part of the document key.** `Documents.Variant` = `origin` (HTTP response as served) or
`rendered` (serialised DOM after rendering), unique key (`Classification`, `IdentityKind`, `IdentityHash`, `Variant`).

| Alternative | Why not |
|---|---|
| One document, both raw blobs | one `ContentHash` per document: two sources (one rendering) would overwrite each other's hash and execution id on every fetch, so the document would ping-pong between "changed" states and never be unchanged |
| Rendering only as a node on the origin document | change detection would see only the origin bytes; for a JavaScript-only page the HTML shell never changes while the content (loaded by XHR) does, so updates would be missed |

Consequences:

- A source references **one** variant per URL, chosen at the root (§8.1). When the choice flips, the reference
  moves: the new variant's reference is added and the old one removed in the same run with reason
  `variant-changed`, exempt from the deletion guard (it is a replacement).
- **Content hash**: origin = §12 hash of the HTTP body; rendered = §12 hash of the serialised DOM. Change detection
  for the rendered variant: conditional GET of the main document first; a 304 still re-renders only when the page
  is JavaScript-only (its data may have changed), otherwise the stored rendered content is kept.
- **Sharing**: same-level sources share a variant only if they make the same choice. Fetch, cache, parse and chunk
  sets are per variant. The fetch dedup key gets `:r` for the rendered variant; both variants respect the same host
  budget.
- **Retrieval**: two variants of one URL in one collection (two sources with different settings) give two documents
  with the same `Url`; the admin source form warns when this would happen. Citations show the URL either way.
- The fallback node (6a §13.1) stays: it renders inside the origin document's stage (output cached on the origin
  hash) and feeds §8.1's `RenderRecommendedAt`.

### 8.5 Alternatives considered: a self-hosted crawler service

A self-hosted Firecrawl was considered instead of our own crawler and rejected (plan answered 14, 2026-10-29). It
covers fetch, render and HTML → Markdown, but the hard parts here (shared frontier and claims, per-host budgets across
sources, conditional GETs and noise-normalised change detection, the deletion guard and reconcile phase, the crawl
rules of answered 11–13, the egress guard, variants and pipeline/run integration) would still be ours. It would add a
second queue stack (its own API and workers, Playwright, Redis, RabbitMQ and a Postgres queue) and an AGPL-3.0
component, and its Markdown output duplicates our HTML extraction while we keep raw HTML for re-parsing. Its main
strength, scraping sites with anti-bot protection, is not a pilot need. If that need appears, such a service can sit
behind `IRenderer` (§8.2) without changing the rest of this design.

## 9. Uploads

### 9.1 Flow

| Step | Who | What |
|---|---|---|
| 1 | admin API | batch create (`POST /api/sources/{id}/upload-batches` → `batchId`), then **one request per file** (raw body, per-file progress and retry; admin design §4.5); streams each file: per file ≤ `maxFileSizeMb` (default 50, platform cap 50; answered 3), ≤ 100 files and ≤ 500 MB per batch; extension pre-check gives an immediate 415 for refused types (archives, macro-enabled, executables); SHA-256 while streaming |
| 2 | admin API | writes each file to `uploads/{sourceId}/{uploadId}/{sha256}` encrypted with a per-upload data key wrapped by `rag-blob-<level>` (data-model §7); at **commit** (`POST /api/upload-batches/{batchId}/commit`) writes the batch manifest `uploads/{sourceId}/batches/{batchId}.json` (names, declared types, sizes, hashes, wrapped keys); inserts one `sync` job, `Trigger = upload`, payload `{sourceId, batchId}`, `DedupKey = sync-upload:{batchId}` (uploads never wait for a running crawl-style sync); uncommitted batches and their blobs are swept after 24 h |
| 3 | worker, `sync` | reads the manifest, upserts `Uploads` rows, opens the run, enqueues one `fetch` job per file (`DedupKey = fetch:{level}:{identityHash}`) and applies the batch's deletes |
| 4 | worker, `fetch` (root `upload`) | ClamAV scan (§11) → sniff → type check → document upsert by `sha256:<hex>` → raw blob `documents/{id}/raw/{contentHash}` (key moved to `DocumentKeys`) → pipeline continues |

`content."Uploads"` (L, written by the worker): `Id`, `SourceId`, `Classification`, `BatchId`, `FileName`,
`DeclaredMediaType`, `SniffedMediaType`, `Bytes`, `Sha256`, `State` (`received`/`clean`/`malware`/`refused`/
`indexed`/`deleted`), `Reason`, `DocumentId`, `UploadedBy`, `UploadedAt`, `DeletedAt`. The admin reads it for the
source's file list.

### 9.2 Types

- Allowed (pilot list, plan §3 / parser design §3; answered 3 is the earlier subset): PDF, DOCX/PPTX/XLSX, `.doc`/
  `.xls`/`.ppt`, `.odt`/`.ods`/`.odp`, `.rtf`, JPG/PNG/TIFF, `.eml`/`.msg`, TXT/MD/CSV, HTML.
- **Sniffed type wins** (magic bytes; OOXML `[Content_Types].xml` incl. `macroEnabled` content types; OLE root
  CLSIDs for legacy Office and `.msg`; text heuristics). Declared type and extension are compared: a mismatch is a
  fact; a declared allowed type whose sniffed type is not allowed → `unsupported` `upload.type_mismatch`.
- Refused (Z1, postponed): zip/7z/rar/tar/gz that are not OOXML/ODF → `upload.archive`; `.docm`/`.xlsm`/`.pptm` →
  `upload.macro_enabled`; executables and unknown → `upload.type_unknown`. The parser refuses them again (parser §3).
- Uploaded HTML is never rendered and its links are never followed (`files` template, 6a §13.1).

### 9.3 Replace and delete

- **Delete**: the admin adds a delete entry to a batch; the sync soft-deletes the reference (`upload.deleted`,
  exempt from the guard: an explicit admin action); `Uploads.DeletedAt` set; the upload blob is purged after the
  14-day grace, so an accidental delete can be undone within it (admin "restore" adds the entry back and the
  reference is revived, §13.4).
- **Replace** = delete + add in one batch: identity is the hash, so the new bytes are a new document; the old
  reference goes in the same run. Two uploads of the same bytes in one source are one reference: the second is
  `unchanged` `upload.duplicate`, its name added to the reference's `DisplayName` history in `Uploads`.

### 9.4 Identity and names

Identity is the file hash, so two departments uploading the same PDF at the same level share fetch, parse and (if
their pipelines match) chunks (R2). The **file name stays per source** (`DocumentSources.DisplayName`) because a
name can reveal another department's context ("Uppsägning_….pdf"); `Documents.Title` comes from the parsed
document (metadata title or first heading), never from a file name.

## 10. Email attachments as child documents

1. The parser returns `attachments[]` with binary parts (parser §5.2). Inside the parent's `parse` job, before its
   fenced commit (6a §7.5), the worker writes each part to `documents/{childId}/raw/{sha256}` (content-addressed,
   child key in `DocumentKeys`); parts with `skipped` and parts that would be at depth 3 get no document (run item
   `skipped` `attachment.depth` / `attachment.reference`).
2. In the parent's **fenced document-stage commit**: upsert child `Documents` (`IdentityKind = attachment`,
   identity `<parent>#att/<ordinal>/<sha256>`, `ParentDocumentId`, `AttachmentOrdinal`, `Depth = parent + 1`,
   same `Classification` by the composite FK); mirror every live `DocumentSources` row of the parent (same
   `SourceId`s), writing the attachment's sanitised file name (parser §5.2 `name`) into `DisplayName` of every mirrored
   row (knowledge design §10.2; like uploads, never into `Documents.Title`); soft-delete child references whose identity is no longer among the parts; enqueue one `fetch` job
   per new or changed child (`DedupKey` as uploads). A superseded parent execution therefore never creates children.
3. The child's `fetch` job runs the **attachment intake**: ClamAV scan of the part (§11), sniff, type check, depth
   check, then the pipeline of each referencing source from the first node after the root. A malware child is
   `malware` (run degraded, 5102); the parent stays indexed.
4. Later changes: a new reference to the parent (another source) mirrors to the children in the same transaction;
   `SoftDeleteReferencesAsync` on a parent reference also removes the children's references for that source; a
   parent purge cascades (FK). Children are excluded from deletion-guard counts (they follow their parent).
5. Citations: "Bilaga *name* i *subject*" through `ParentDocumentId` (knowledge design §10.3; parser §5.4): *name* is
   the child's `DisplayName`, *subject* the parent's `Documents.Title`, which for an email document is its subject
   (parser §5.2 `email.subject`; empty subject → first heading or "Dokument utan titel"). The parent part is shown only
   when the parent is visible to the caller.

## 11. ClamAV

| Topic | Rule |
|---|---|
| Client | own `IMalwareScanner` over TCP 3310 (`ingestion` network): `zINSTREAM\0`, chunks of 64 KiB as `<uint32 BE length><bytes>`, zero-length terminator; reply `stream: OK`, `stream: <signature> FOUND`, or `… size limit exceeded. ERROR`. `zPING` for health, `zVERSION` for signatures. ~150 lines, no package |
| What is scanned | every upload, every attachment, every crawled or listed non-HTML/non-text file (PDF, Office, images, email) before it is stored as document content or sent to the parser. Fetched HTML, text and CSV, parsed in-process, are not scanned |
| Streaming | the body is written to an encrypted temp blob `tmp/{jobId}/…` while hashing, then streamed to clamd and renamed into place if clean; no 50 MB buffers in memory; orphaned temp blobs swept after 24 h |
| Limits | `StreamMaxLength 60M`, `MaxFileSize 60M`, `MaxScanSize 200M` (repo-and-deployment §5.5); a size-limit reply → `too-large` `clamav.limit` (never "clean") |
| Timeouts | connect 5 s, scan 120 s; on timeout transient with attempt |
| Signature freshness | `zVERSION` → `ClamAV <engine>/<db version>/<db date>`, read every 10 minutes: > 48 h old → health `degraded`, > 7 days → `unhealthy` and **no new files** (repo-and-deployment §5.5); `rag.clamav.signature_age_hours` gauge |
| Down or stale (fail closed) | file jobs requeue without attempt (`clamav.unavailable`, 30 s); after 24 h waiting the item ends `failed` `clamav.unavailable` and the run degrades. HTML continues. Nothing unscanned reaches the parser |
| Malware | outcome `malware`; blob moved to `quarantine/{sourceId}/{sha256}` (data-model §9) with its wrapped key; `content."QuarantinedFiles"` (L: `Id`, `SourceId`, `RunId`, `Classification`, `Sha256`, `Signature`, `Origin` (`upload`/`crawl`/`attachment`), `Name` or `Url`, `DetectedAt`, `PurgeAfter`); event **5102**; run `degraded` (6a §10.3). A crawled page that was clean before keeps its last clean version (not a deletion) |
| Who sees it | department admins: name/URL, signature, time; platform admins also the hash. **No download or release in P1** (§21 Q3); a false positive is resolved by IT security adding an ignore signature and re-uploading. Purged after 30 days (platform plan §8, proposed) |
| Event 5102 `rag.malware.detected` | Warning; attributes `Signature`, `Sha256`, `Origin`, `SourceId`, `RunId`, `DocumentId` (if any), `Classification`; SIEM alert on any |

## 12. Change detection

### 12.1 Two stages

| Stage | Check | Unchanged → |
|---|---|---|
| 1 | conditional GET (§4); SharePoint (P2): delta reports no change | `NotModified`: validators and `FetchedAt` updated, reference touched, `EnsureCurrent`, run item `unchanged`; no blob read |
| 2 | `ContentHash` = SHA-256 over the **normalised** content: HTML per §12.2 (rules version in `Documents.ContentHashVersion`), everything else the raw bytes | no new blob, no new execution id; as above |

Only a new hash writes `raw/{contentHash}`, sets `CurrentExecutionId` (6a §7.5) and runs the pipeline. The first
node's input hash is this `ContentHash` (6a §8), so the whole engine agrees on what "changed" means. Onyx gates the
same way but only after parsing (`../../platforms/onyx.md:122-123`); Eneo stores the hash and never compares it
(`../../platforms/eneo.md:78-82`).

### 12.2 HTML noise normalisation (rules v1)

On the AngleSharp DOM the root already builds for links: drop comments; drop `<script>` except
`application/json` and `application/ld+json` (SPA data stays significant); drop `<style>`, `<template>`; drop the
attributes `nonce`, `integrity`, `data-csrf*`; blank `<input type=hidden>` values and `<meta>` named `csrf-token`,
`csrf-param`, `__RequestVerificationToken`, `__VIEWSTATE`, `__EVENTVALIDATION`; strip query strings from `src`/`href`
of `script`, `link`, `img` (cache busters); replace text equal to the fetch day's date (`2026-10-29`, `29 oktober
2026`, `idag`) outside `<main>`/`<article>` with a placeholder; remove elements matching the source's
`noiseSelectors` (e.g. `#cookie-banner`, `.senast-uppdaterad`); collapse whitespace; serialise.

Rules are deliberately conservative: hiding a real change is worse than one extra re-process. Remaining noise is
measured: when a new hash produces a parsed output whose `OutputHash` equals the previous execution's (6a §8), the
worker counts `rag.connector.change.noise` and lists the page under "Noise" in the crawl report (§5.5) so an admin
can add a selector. A rules-version bump re-processes HTML documents once (embedding cache keeps it cheap;
release-notes item).

### 12.3 Source-setting effect tags

Declared in `contracts/sources/v1/{type}.schema.json` as `"x-effect": "identity" | "scope" | "behaviour" |
"cosmetic"` (Onyx `FieldClass` and scope descriptors, `onyx/connectors/field_policy.py:20-28`, `:40-93`); the shared
library (`Ume.Rag.Pipelines`, 6a §4) computes per-tag hashes of a settings object. The admin shows the impact on
save; the run stores `Runs.SettingsHashes` (`{identity, scope, behaviour}`) and the worker compares the current hashes
with the last completed run's at every sync, so a lost "apply" job cannot leave an edit unapplied.

```json
{ "startUrls": ["https://www.umea.se/"], "sitemaps": [], "extraHosts": [],
  "include": ["www.umea.se/**"], "exclude": ["**/sok?*", "**?*print=*"], "maxDepth": 10, "maxPages": 10000,
  "render": "auto", "ignoreQueryParams": [], "directoryIndexNames": [], "noiseSelectors": [],
  "conditionalGet": true, "revalidateEvery": 4, "crawlWindow": "22:00-06:00", "incrementalSchedule": null }
```

| Setting | Source types | Tag | Smallest re-sync |
|---|---|---|---|
| `name`, `description` | all | cosmetic | none |
| `schedule`, `incrementalSchedule`, `crawlWindow` | url-list, crawl, sharepoint | cosmetic | none (the scheduler reads them) |
| `classification` | all | identity (level) | full re-sync into the new level; old-level references removed at reconcile (exempt); jobs at the old level fail the commit check (6a §15) |
| `pipelineId` | all | behaviour | `reprocess` run via `EnsureCurrent`, no fetching (6a §12) |
| `urls` | url-list | scope | added: claims for the added URLs only; removed: references soft-deleted at once (`removed-from-list`, exempt) |
| `startUrls`, `sitemaps`, `extraHosts`, `include`, `exclude` | crawl | scope | widening: a traversal now (unchanged pages cost a 304); narrowing: references whose stored URL fails the new scope removed without fetching (`scope-narrowed`, exempt, count shown on save) |
| `maxDepth`, `maxPages` | crawl | scope | raise: traversal now; lower: next traversal, never deletes (a limit makes it partial) |
| `ignoreQueryParams`, `directoryIndexNames` | url-list, crawl | identity | full re-sync: every URL re-canonicalised and fetched; old identities not re-found removed at reconcile (exempt, count shown on save) |
| `render` | url-list, crawl | identity (variant) | variant re-chosen per URL on the next fetch; moved references `variant-changed` (exempt) |
| `noiseSelectors` | url-list, crawl | behaviour | next sync; pages whose hash changes re-process once |
| `conditionalGet`, `revalidateEvery`, `timeoutSeconds` | url-list, crawl | behaviour | next sync |
| `maxFileSizeMb` | upload | behaviour | new uploads only |
| `sites`, `drives`, `folders`, `includeSitePages`, `excludedPaths` (P2) | sharepoint | scope | widening: enumerate the new scope; narrowing: from stored paths, exempt |
| `certificate`, `tenantId` (P2) | sharepoint | behaviour | preflight, then next sync |

A source never moves between collections (a new source is created), so `collectionId` is not editable.

### 12.4 Smallest re-sync per tag

| Tag | Run | Fetches | Deletions |
|---|---|---|---|
| cosmetic | none | – | – |
| behaviour | none (next scheduled) | per normal rules | normal guard |
| scope | immediate `sync`, `Trigger = config-change` | only newly in-scope items (crawl: traversal with conditional GET) | narrowing removals exempt (admin confirmed the shown count) |
| identity | immediate full `sync`, `force` | everything | identity removals exempt; unexpected losses still go through the guard |

> **Decided in 6c** (`design-processing.md` §12): a noise-only change (new `ContentHash`, same parsed output)
> creates no new chunk set; the set is re-keyed and the run item is `unchanged`, reason `change.noise`.

## 13. Deletion guard

### 13.1 Candidates

Collected during the run in `content."DeletionProposals"` (L: `RunId`, `DocumentId`, `SourceId`, `Classification`,
`Reason`, `Exempt bool`, `Decision`, `DecidedBy`, `DecidedAt`; PK (`RunId`, `DocumentId`)):

| Reason | Source | Exempt |
|---|---|---|
| `not-seen` | complete traversal or id listing without the reference | no |
| `http.404`, `http.410`, `http.moved_out_of_scope` | fetch | no |
| `robots.disallow` | robots.txt now disallows a known URL | no (a staging robots.txt deployed to production must not empty the index) |
| `persistent-failure` | 401/403/5xx/TLS for ≥ 4 consecutive runs **and** ≥ 14 days since `LastSeenAt` (`DocumentSources.ConsecutiveFailures`) | no |
| `sharepoint.deleted`, `sharepoint.moved_out_of_scope` (P2) | delta | no |
| `removed-from-list`, `scope-narrowed`, `identity-changed`, `level-changed`, `variant-changed`, `upload.deleted` | configuration or explicit admin action | yes |

### 13.2 The guard

Runs in the `reconcile` phase, after every `fetch` job of the run is terminal (the finalizer enqueues it, 6a §10.4
changed in §20). Let `n` = non-exempt candidates, `N` = live references of the source (children and held references
excluded):

- **ratio**: `n ≥ DeletionGuardMinCount` (5) and `n / N > 0.20` (plan §8, `Settings`);
- **absolute**: `n > DeletionGuardMaxCount` (500): 20 % of a 50,000-document source is too many to delete unasked;
- **empty source**: the listing or traversal completed but yielded no indexable item while `N > 0` (Onyx deleted
  189,833 documents after an empty listing and recorded success, `../../platforms/onyx.md:264`,
  `onyx/background/celery/tasks/pruning/tasks.py:816`);
- **404 storm**: covered twice: the circuit breaker (§6.4) makes the traversal partial (so `not-seen` cannot fire),
  and explicit 404s still count in `n`.

Not tripped → exempt and non-exempt candidates are applied with `SoftDeleteReferencesAsync` (6a §14) in batches of
1,000, run items `deleted`. Tripped → exempt ones are applied, the rest wait; `Runs.ProposedDeletions = n`; the run
finishes all other work and the finalizer sets **`awaiting-deletion-confirmation`**.

### 13.3 Confirm, reject, supersede

| Event | Effect |
|---|---|
| `command` `confirm-deletion {runId}` (`Manage` policy of the source's department: `rag-department-admin` or `rag-platform-admin`, admin design V28) | re-validates each proposal (a reference seen again by a later run is dropped), soft-deletes the rest, run items `deleted`; the finalizer recomputes the run state; audit 6001 |
| `command` `reject-deletion {runId}` | nothing deleted; the references get `DocumentSources.HeldAt/HeldBy`, excluded from later guards and listed as "kept by decision" on the source page until they are seen again (hold cleared) or an admin releases the hold (`command` `release-hold {sourceId}`, admin design §4.7: holds cleared, references become candidates at the next reconcile); run → `degraded` `deletion.rejected` |
| a newer run of the source reaches reconcile while a proposal waits | the newer run computes afresh; the older run → `degraded` `deletion.superseded`; one waiting proposal per source at a time |
| no decision | waits indefinitely, never auto-confirmed; shown on the Overview page (admin plan §4) and as a daily reminder metric `rag.deletion.awaiting` |

### 13.4 Grace and store removal

`SoftDeleteReferencesAsync` sets `DocumentSources.DeletedAt` and `PurgeAfter = +14 days` and recomputes membership,
so chunks leave the collection at once. A reference seen again within the grace is **revived** (`DeletedAt = null`,
membership recomputed, no re-processing; chunk sets are kept 14 days, data-model §2.2). A document gets `DeletedAt`/
`PurgeAfter` only when no live reference remains (plan §8), and the worker's content retention purges it (with
children, keys, outputs and blobs) after `PurgeAfter`.

### 13.5 What the admin sees

The run page in state `awaiting-deletion-confirmation`: `n`, `N` and the ratio, the rule that tripped, a breakdown by
reason, traversal facts (pages reached this run vs. the previous run, paused hosts, seed failures), 50 example URLs
or names with `LastSeenAt` and last status, a CSV of all proposals, and **Confirm** / **Reject** buttons that insert
the `command` jobs. The source page shows held references and the exempt removals of config changes.

## 14. SharePoint (P2 outline)

| Topic | Design |
|---|---|
| Auth | app-only certificate credential of `rag-ingestion` with `Sites.Selected`; per-site `read` grants made by admins (identity plan §3 answered 2); Graph through the egress guard on an allow-listed host set (`graph.microsoft.com`, `login.microsoftonline.com`) |
| Preflight | token; `GET /sites/{id}`; list drives; download one file; `GET /drives/{id}/root/delta?token=latest`; create and delete a test subscription; each failure with remediation text and the PowerShell/Graph call for the grant (Onyx capability checks, `../../platforms/onyx.md:112-115`); the first sync is held until it passes |
| Identity | `sharepoint:<driveId>:<itemId>`; title, path, URL and `lastModifiedDateTime` as facts |
| Delta | per drive, the stored **`@odata.deltaLink`** in `ConnectorCheckpoints` (`state`), `nextLink` checkpointed per page. No timestamp tokens or `lastModified` windows: Onyx's timestamp token (`onyx/connectors/microsoft_utils/drive_delta.py:210-221`) missed copied files (#13583) |
| Deletes | `deleted` facet → candidate `sharepoint.deleted` at once (Onyx skips tombstones, `onyx/connectors/microsoft_utils/drive_items.py:686`, so deletes wait for a weekly prune) |
| Moves | item id stable → same document; the new `parentReference.path` is re-evaluated against the scope: out → `sharepoint.moved_out_of_scope` (Eneo #522 orphans), in → added (Onyx #16590). A moved or renamed **folder** triggers a BFS of its subtree, because delta may not repeat every child. Paths compared decoded and case-insensitively by segment (Eneo #429) |
| Duplicates in one delta | last occurrence of an item wins |
| 410 `resyncRequired` | full enumeration from a fresh delta; references not seen → `not-seen` candidates **through the guard** (Onyx's 410 restart, `drive_delta.py:275-312`; Eneo `clients/sharepoint_content_client.py:563`, `:637`) |
| Throttling | 429/503 `Retry-After` honoured through a pseudo-host budget `graph:{tenantId}` in `CrawlHosts` (Eneo #427) |
| Subscriptions | one per drive (`changeType = updated`), renewed at half their lifetime and at least 48 h before expiry (Eneo renews every 12 h, `eneo/integration/infrastructure/sharepoint_subscription_worker.py:125`); random `clientState` per subscription **always** checked (Eneo checks only if configured, `sharepoint_webhook_service.py:84-85`); endpoint on the admin API behind the reverse proxy, validation handshake, notifications enqueue a `sync` (`Trigger = notification`, dedup per source); lifecycle notifications (`missed`, `reauthorizationRequired`) → delta run. Notifications are **hints**: delta also runs every 6 h |
| Health | per source: last delta, subscription expiry, last notification, last error (Eneo #521 silent death, #1081 sync health); `rag.sharepoint.subscription.expires_in` alerts below 24 h |
| Permissions | **not mirrored** (§21 Q2): access stays per collection like every other source; the preflight lists libraries/folders with unique permissions below the selected scope (*to verify* which Graph call shows this under `Sites.Selected`) so the department picks content matching the collection's audience. Mirroring ACLs needs per-chunk ACLs, group expansion and broader grants (Onyx needs `Sites.FullControl` + `GroupMember.Read.All` for it, `../../platforms/onyx.md:139-142`), against least privilege; Eneo has none either (`../../platforms/eneo.md:93-94`) |

**Acceptance tests** (comparison F8):

| Id | Issue | Test |
|---|---|---|
| S1 | Onyx #13583 | a file copied into a library with an old `lastModifiedDateTime` is indexed on the next delta |
| S2 | Onyx #16590 | a file moved into a folder-scoped source is indexed; moved out is a deletion candidate |
| S3 | Onyx tombstones (`drive_items.py:686`) | a deleted file leaves the collection within one delta run (+ guard) |
| S4 | Onyx #16221 | the listing returns 0 items for a populated source → pause, nothing deleted |
| S5 | Eneo #427 | Graph 429 with `Retry-After` slows the source, run not failed |
| S6 | Eneo #521 | an expired or deleted subscription is recreated and shown in health; delta still runs every 6 h |
| S7 | Eneo #522 | a folder moved out of scope removes its documents (through the guard), no orphaned blobs |
| S8 | Eneo #429 | scope paths with spaces, `%20`, case differences and trailing slashes match |
| S9 | Eneo #1081 | a failing source shows the failure, last success and next retry on the source page |
| S10 | 410 | an expired delta link triggers enumeration; unseen items go through the guard |

## 15. Failure handling and outcomes

Connector results mapped onto 6a §10.2 (reasons `<area>.<code>`):

| Situation | Outcome | Reason examples | Attempt |
|---|---|---|---|
| 200 new / changed / same hash; 304 | `added` / `updated` / `unchanged` | – | – |
| served by another run's fetch | `shared` | `shared.run:<id>` | – |
| robots, `noindex` (external), trap, duplicate upload, depth, broken discovered link | `skipped` | `robots.disallow`, `robots.noindex`, `trap.calendar`, `upload.duplicate`, `attachment.depth`, `http.404` (never indexed, found by a link) | – |
| render failed or not enabled | `degraded` | `render.timeout`, `render.not_enabled`, `render.truncated` | – |
| applied deletion | `deleted` | `http.410`, `not-seen`, `upload.deleted` | – |
| refused type | `unsupported` | `http.content_type`, `upload.archive`, `upload.macro_enabled` | – |
| size cap | `too-large` | `http.body_limit`, `clamav.limit` | – |
| ClamAV finding | `malware` | `clamav.<signature family>` | – |
| egress guard, robots on a redirect target, downgrade | `blocked` | `egress.private_address`, `redirect.downgrade` | – |
| listed URL 404, 401/403, TLS, 5xx after retries, ClamAV down 24 h | `failed` | `http.404`, `http.403`, `tls.invalid_certificate`, `http.5xx`, `clamav.unavailable` | 5 for transient |
| 429/503, host slot not yet due, ClamAV down, render lease busy | requeue | `rate_limited`, `politeness`, `clamav.unavailable`, `render.busy` | no |

Run-level: seeds unreachable → run `failed` `crawl.seeds_unreachable` (no deletions); page limit → `degraded`
`crawl.limit_pages`; host paused → `degraded` `crawl.host_paused`; malware → `degraded` (6a §10.3).

## 16. Observability

New metrics (meter `Ume.Rag`; dimensions low-cardinality; **never** host names, URLs, file names or titles):

| Metric | Type | Dimensions |
|---|---|---|
| `rag.connector.fetch.duration` (s), `rag.connector.fetch.bytes` | histogram | source type, status class (`2xx`, `304`, `3xx`, `4xx`, `5xx`, `error`), host kind |
| `rag.connector.items` | counter | source type, result (`new`, `changed`, `not_modified`, `same_hash`, `gone`, `skipped`) |
| `rag.connector.change.noise` | counter | host kind |
| `rag.crawl.frontier.pending`, `rag.crawl.released` | gauge, counter | host kind |
| `rag.crawl.politeness.wait` (s), `rag.crawl.backoff`, `rag.crawl.host_paused` | histogram, counter, counter | host kind; reason (`429`, `503`, `breaker`) |
| `rag.crawl.traps` | counter | kind |
| `rag.render.duration` (s), `rag.render.subrequests_blocked` | histogram, counter | outcome; reason |
| `rag.egress.blocked` | counter | reason, purpose |
| `rag.clamav.scan.duration` (s), `rag.clamav.detections` (existing), `rag.clamav.signature_age_hours` | histogram, counter, gauge | origin |
| `rag.deletion.proposed`, `.applied`, `.guard_tripped`, `.awaiting` | counter, counter, counter, gauge | reason / rule |
| `rag.sharepoint.subscription.expires_in` (P2) | gauge (min over sources) | – |

- **Traces**: spans `rag.connector.list`, `rag.connector.fetch`, `rag.render`, `rag.clamav.scan` under the job
  span (6a §17); HTTP client spans have `url.full` and `server.address` **removed** for every source (a filter in
  the worker's OTel setup; repo-and-deployment §9 already strips query strings). Attributes: source type, host kind,
  status class, outcome.
- **Logs**: URLs only at Debug and only for `public` sources; otherwise `UrlHash`.
- **Events**: 5101 (§7), 5102 (§11). SIEM alerts to add: any 5102; any 5101 with `Purpose = Render` and an address
  class in `rfc1918`/`loopback`/`link_local` (a page probing the internal network); the canary failure (§7).
- **Health**: ClamAV (ping, signature age), renderer (connect), egress canary, releaser lag (oldest due `pending`
  entry), feeding the *Ingestion* dashboard.

## 17. Performance (R1: 10,000 pages/hour)

| Connector cost per typical page | Estimate (*to measure*) | At 2.8 pages/s |
|---|---|---|
| One conditional GET (municipal host) | 1 politeness slot, ~200 ms latency | 2.8 req/s of 10 allowed; `fetch` concurrency 8 covers latency |
| AngleSharp parse for links + noise hash | ≈ 10–15 ms CPU | ≈ 0.04 cores (on top of 6a §18's 80 ms per page) |
| Frontier statements: claims upsert (1 per page, ~100 links), slot update, document upsert, run items batch | ≈ 4 statements | ≈ 11 statements/s |
| Releaser | 1 query/s per tier | – |
| ClamAV | files only (PDF ≈ 50–200 ms) | negligible for HTML-heavy sites |

An unchanged re-crawl costs one request + one parse for links per page and no blob write, parse or embedding. Rendering
is **outside** the R1 target: at ~1–3 s per page and 2 concurrent pages a rendered site runs at ~1 page/s, which is
why rendering is per page (§8.1) and not per source by default. One external host is capped at 3,600 pages/hour by
politeness (answered 4), so the R1 test uses a municipal test host.

## 18. Tests

| Level | What |
|---|---|
| Unit (`Ume.Rag.Worker.Tests`) | canonicaliser: every §3.1 rule with RFC 3986 normalisation vectors and IDN/punycode cases; tracking list; glob/regex patterns (Eneo's unescaped-regex case); robots parser against the RFC 9309 examples and Google's robotstxt test cases (*to verify* licence); sitemap parser (index, gzip, XXE payload refused, oversize); JavaScript-only score fixtures; noise normaliser (CSRF tokens, nonces, cache busters, dates: equal hashes; a changed paragraph: different hash); trap detectors; guard arithmetic incl. exempt reasons and min/max counts; effect-tag hashes per §12.3 row; ClamAV client against recorded replies (`OK`, `FOUND`, size limit, garbage); outcome mapping table §15 |
| SSRF fixtures | guard with a fake resolver: every blocked range, embedded IPv4 in IPv6 (mapped, NAT64, 6to4, Teredo), mixed public/private answers (refused), DNS answer changing between calls (pinning: connect goes to the checked address), decimal/octal/hex IP literals, IDN homographs, userinfo, ports, redirect to `169.254.169.254`, https → http, 6 hops |
| Integration (Testcontainers + an in-process Kestrel test site) | crawl of a generated site: robots allow/disallow/5xx/404, `Crawl-delay`, sitemap index, redirects (loops, chains, off-site 302 sign-in wall, 301 out of scope), calendar and faceted traps, session ids, `noindex`/`nofollow` on a "municipal" vs "external" host name, 429 with `Retry-After`, slow responses, gzip bomb, oversize body, wrong `Content-Type`; two sources on one host (one request per page, `shared` outcomes, rate never above budget); preview fetch with the releaser running; URL list ETag/Last-Modified/304 and a server with changing ETags |
| Deletion guard (integration) | 25 % removed → pause; confirm, reject (hold), supersede; empty listing (S4); 404 storm with the breaker; page-limit run deletes nothing (**C7**, Eneo partial crawl); revival within 14 days without re-processing |
| Renderer (integration, real `rag-renderer` image) | SPA fixture rendered and its links followed; every browser request goes through the worker (test server sees only worker connections; renderer on an internal network with no egress); blocked types; download and popup attempts; 30 s timeout → origin indexed `degraded`; per-host lease; variant switch moves the reference with `variant-changed` |
| ClamAV (integration, real image) | EICAR upload → `malware`, quarantine row and blob, 5102, run degraded; EICAR as an email attachment → parent indexed, child `malware`; clamd stopped → files requeue, HTML continues, after 24 h `failed`; stale signatures (fake `VERSION` reply) → no new files |
| Uploads | sniff vs declared (PDF named `.docx`, EXE named `.pdf`, `.docm`, zip, OOXML zip accepted); same bytes in two departments → one document, two display names, title not from the name; replace and delete; email with nested email → depth 2 indexed, depth 3 `skipped` |
| Crash/resume | kill the worker during listing (resume from checkpoint, no duplicate claims or run items), during a fetch after the blob write and before commit (orphan swept, retry idempotent), during reconcile (guard decision computed once), during a render; leader loss of the releaser (another instance takes over, no double release beyond dedup) |
| Telemetry | captured spans, metrics and logs of a `internal`-source crawl contain no URL, host or file name |
| SharePoint (P2) | S1–S10 against a Graph fake (WireMock) plus one run against a test tenant |
| Performance | `perf.yml` (6a §18): 10,000-page generated municipal site; unchanged re-crawl does no parse/embed |

## 19. Milestones

| Phase | Deliverable |
|---|---|
| P1 | connector interfaces and runner with checkpoints; canonicaliser; URL list; crawl with robots, sitemaps, scope, traps, traversal/incremental, report; frontier claims, releaser, host budgets, back-off, breaker, night window; egress guard + canary; renderer with worker-side fetching and variants; uploads (batches, sniffing, replace/delete); attachments; ClamAV + quarantine; change detection with noise rules v1; effect tags; deletion guard with reconcile phase and commands; preflight for URL list and crawl; crawl hierarchy for pattern building; tests of §18 |
| P2 | SharePoint (§14, S1–S10); learned noise selectors (proposed per host like cleanup rules, plan §6); PDF link extraction for crawls; quarantine release workflow if needed; per-instance-independent connection caps if the ingestion zone gets several instances |
| P3 | more connectors on the same contracts; an alternative renderer backend behind `IRenderer` if anti-bot sites must be crawled (answered 14) |

## 20. Changes to other documents

Applied in the step 6b review commit (2026-10-29), except: the admin plan items (step 10) and the knowledge-api
items (step 7), which those steps take up; design-engine.md got a pointer note, and the consistency pass (step 11)
applied its rows inline (6a §9.1, §10.1, §10.4, §14, §17).

| Document | Change |
|---|---|
| `platform/data-model.md` §2.2 `Documents` | `Variant varchar(10) NOT NULL DEFAULT 'origin'` (`origin`/`rendered`); unique key → (`Classification`, `IdentityKind`, `IdentityHash`, `Variant`); `CanonicalHint text NULL`; `RenderRecommendedAt timestamptz NULL`; `ContentHashVersion smallint NOT NULL`; `Title` never from an upload file name |
| `platform/data-model.md` §2.2 `DocumentSources` | `DisplayName varchar(300) NULL` (upload name per source); `HeldAt`, `HeldBy` (rejected deletions); `ConsecutiveFailures smallint NOT NULL DEFAULT 0` |
| `platform/data-model.md` §2.2 crawl tables | `CrawlHosts` columns of §6.1; `CrawlFrontier` reshaped (`State` `idle`/`pending`/`released`, `LastFetchedAt`, `LastStatus`, `RedirectTo`; `SourceIds`, `Depth` removed); new `CrawlClaims` (L) and `CrawlRuns` (L) |
| `platform/data-model.md` §2.2 new tables | `ConnectorCheckpoints` (L, §2.3), `Uploads` (L, §9.1), `DeletionProposals` (L, §13.1), `QuarantinedFiles` (L, §11); grants: worker S I U D, admin S |
| `platform/data-model.md` §2.2 `Runs` | `Phase varchar(20)` (`list`/`fetch`/`reconcile`/`done`), `Report jsonb`, `SettingsHashes jsonb`; `Trigger` adds `config-change`, `notification` (P2) |
| `platform/data-model.md` §2.1 `Settings` | `DeletionGuardMinCount` 5, `DeletionGuardMaxCount` 500; `Crawl jsonb` (municipal host patterns, rates and connections per kind, host overrides, blocked hosts, allowed ports, freshness 12 h, default window, default limits 10,000/10, HTML cap 10 MB, file cap 50 MB, max redirects 5, identity rules version); `QuarantineRetentionDays` 30 |
| `platform/data-model.md` §4.2, §9 | fetch dedup key `fetch:{level}:{identityHash}[:r]`; upload sync dedup `sync-upload:{batchId}`; blobs `documents/{id}/raw/{contentHash}.links.json.gz`, `uploads/{sourceId}/batches/{batchId}.json`, `tmp/{jobId}/…` (24 h sweep) |
| `ingestion-worker/design-engine.md` §9.1–§9.2 | `preview` kinds `preflight`, `hierarchy`; `sync` payload `phase` and checkpoint reference instead of inline checkpoint; §9.2 step 2: URL-list and crawl items become frontier claims released by the releaser (uploads, attachments, SharePoint enqueue directly); step 4: deletions in the reconcile phase |
| `ingestion-worker/design-engine.md` §10.1, §10.4 | `Runs.Phase`; triggers `config-change`, `notification`; the finalizer enqueues the reconcile phase when a run's fetches are done, and finalizes after it; `awaiting-deletion-confirmation` set from `DeletionProposals` |
| `ingestion-worker/design-engine.md` §13.1, §14, §17 | the 6b open item is decided (§8.4: variants); `headless-render` sets `RenderRecommendedAt`; `SoftDeleteReferencesAsync` also removes child (attachment) references of the same source and revives within the grace; metrics of §16 |
| `ingestion-worker/plan.md` §3, §8 | identity rules incl. variant and final-URL identity; upload types: answered 3 is superseded by the §3 pilot list (state it); deletion guard min/max counts and exempt reasons; rendering per page (`auto`) |
| `platform/repo-and-deployment.md` §5.4, §6.1 | renderer: the worker fetches and fulfils every browser request; `rag-renderer` **removed from the `crawl` network** (only `ingestion`) |
| `platform/repo-and-deployment.md` §5.5, §7.1, §9 | ClamAV freshness via `zVERSION`; `UME_RAG_CRAWL_ALLOW` also feeds `Crawl__AllowedPrivateRanges`; egress canary; metrics of §16; trace filter removing `url.full`/`server.address` in the worker |
| `identity/design.md` §10 | attributes of 5101 (§7) and 5102 (§11); SIEM alerts: any 5102, 5101 from `Render` to private classes |
| `platform/plan.md` §8 | quarantine 30 days (confirm); upload blobs purged 14 days after deletion |
| `admin/plan.md` §4 | Sources: preflight results, hierarchy tree for patterns, impact count before saving scope/identity changes, two-variant warning, crawl report (§5.5), upload list with states, quarantine list, deletion confirmation view (§13.5), held references; Settings: municipal host patterns, host overrides, blocked hosts |
| `knowledge-api` (step 7) | upload citations use `DocumentSources.DisplayName` of the citing collection's source; consider de-duplicating chunks by `TextHash` across documents of one collection (duplicate URLs the normaliser cannot merge, §3.1) |
| `postponed-decisions.md` | the crawler about page (answered 10) needs an owner and URL before the pilot; SharePoint ACL mirroring if §21 Q2 is answered "later" |
| `01-general-idea.md` §10 | after review, a "Connectors (step 6b)" row: final-URL identity, canonical not trusted, claims + releaser, worker-fetched rendering, variants, guard with reconcile phase |

## 21. Answered questions (2026-10-29)

1. **External sites**: any department may crawl any public external host under our user agent and robots.txt
   (1 req/s); the preflight shows the host's robots status; platform admins can block hosts.
2. **SharePoint permissions (P2)**: not mirrored; content is visible to the collection's audience; the preflight
   warns about items with unique permissions. ACL mirroring is a possible P3 item.
3. **Malware findings**: SIEM alert, metadata-only admin view, no download or release in P1, quarantine purged after
   30 days.

Decided here without asking (reasons in the sections): final-URL identity (§3.2); canonical not trusted (§3.3);
trailing slash and `index.html` kept (§3.1); releaser instead of eager enqueue (§6.3); 12 h fetch freshness (§6.2);
night window for all traversals (§6.5); 5 redirects (§7); worker-side browser fetching (§8.3); variants (§8.4);
crawled HTML not virus-scanned (§11); guard min 5 / max 500 (§13.2); holds after rejection (§13.3).

## 22. To verify

- Playwright .NET: `RouteAsync("**/*")` + `FulfillAsync` for every resource type the page needs (incl. module
  scripts, CORS preflights), behaviour when the fulfilled response is a redirect, `ServiceWorkers = Block`, and
  whether any request can bypass routing (WebSockets via `RouteWebSocketAsync` in recent versions; DNS prefetch).
- `System.Uri` escaping and IDN behaviour vs our rules (`IdnHost`, `UriKind`, `%2F` handling); `IdnMapping`
  UTS 46 flags in .NET 10.
- `SocketsHttpHandler.ConnectCallback` with HTTP/2 and TLS SNI to the URL host; decompression counting with
  `AutomaticDecompression`; Brotli support.
- Docker IPv6 on the `crawl` network and the firewall rules for IPv6.
- clamd `zVERSION` output format and date parsing on ClamAV 1.4; `INSTREAM` reply on `StreamMaxLength`.
- Graph: maximum subscription lifetime for `driveItem` resources; a call that lists items with unique permissions
  under `Sites.Selected`; whether delta repeats children after a folder move.
- Licence of Google's robotstxt test corpus for reuse as fixtures.
- Planning numbers of §17 (link-extraction CPU, frontier statements per page) in the R1 test.
- The about-page URL for the user agent (answered 10) and that umea.se's robots.txt does not block the new agent.
