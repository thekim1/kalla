# Ingestion worker, part B: connectors, crawl frontier, change detection, deletion guard

Detail step 6b of the Ume.Rag design. Details [`plan.md`](plan.md) §3 and §8 (binding, incl. answered items 1–14)
and consumes the contracts of [`design-engine.md`](design-engine.md) (6a): fetch stages `http-fetch`, `crawl` and
`upload`, the `sync`/`fetch` jobs and their fan-out (§9.2), dedup keys, `EnsureCurrent` and the drift sweep (§2.3),
run outcomes (§10.2), the index writer (§14), config rules (§15) and the execution-id fence (§7.5). Limits come from
[`../platform/data-model.md`](../platform/data-model.md) §2, §4, §7–§9,
[`../platform/repo-and-deployment.md`](../platform/repo-and-deployment.md) §5.5, §6, §9 and
[`../parser/design.md`](../parser/design.md) §3, §11. Rewritten to the simplification pass
([`../simplification-pass.md`](../simplification-pass.md), incl. §13 umea.se findings); removed and deferred items are
listed in §23; items moved to P2 by the [weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer) in §24. Nothing is implemented. Changes needed elsewhere: §20; questions: §21.

Citations: `onyx/…` = `~/git/rag-platforms/onyx/backend/onyx/…`, `owui/…` =
`~/git/rag-platforms/open-webui/backend/open_webui/…`, `eneo/…` = `~/git/eneo/backend/src/intric/…`;
`../../platforms/*.md` are our analyses.

## 1. Summary of decisions

| # | Topic | Decision |
|---|---|---|
| 1 | Contracts | Onyx-style capability interfaces; listing in the `sync` job, fetching in the `fetch` job; a typed checkpoint is committed with the work it covers (§2) |
| 2 | Identity | Own URL normaliser (RFC 3986 + fixed tracking/session parameter list + the site profile's session parameters); identity = **final URL after redirects**, mapped by the site profile's identity rule only when verified; `rel=canonical` folds an alias **only when verified** by equal content hash (§3) |
| 3 | Politeness | `CrawlHosts.NextAllowedAt` is the per-host budget shared by every source; each `fetch` takes its slot before the request. P1: each source has its own frontier (§6.0). **P2 (weakness review §1)**: per-URL `fetch` jobs are **released by a frontier releaser** at each host's rate, so the queue never holds more than ~2 s of work per host (§6) |
| 4 | Sharing | Documents belong to one source; raw blobs are content-addressed, parse output and embeddings cached by content (P1). **P2 (weakness review §1)**: one frontier row per (level, URL) with one **claim** per source, one request serves every claim, and a fetch younger than 12 h is reused by scheduled runs (§6.2) |
| 5 | Crawl runs | Every scheduled crawl is a full **traversal** with stored outlinks, skipping unchanged pages by conditional GET or reliable sitemap `lastmod`; `not-seen` deletions only after a **complete** traversal, complete when ≥ 99 % of seeds are resolved (weakness review §3); URLs that leave the sitemap get a removal check; an alert after 3 runs without a complete traversal (§5.4) |
| 6 | Egress | `IEgressGuard` resolves once and connects only to the checked addresses (`ConnectCallback`), max 5 redirects re-checked per hop; host firewall as second layer plus a startup canary; cookies never stored or sent (§7) |
| 7 | Renderer | **P2**, and only if a P1-start check shows that pilot sites need JavaScript (umea.se does not); `IRenderer` stays as a seam; short pages are an info note in the crawl report (§8) |
| 8 | Render coexistence | Removed (simplification pass): no document variants; the fetch mode will be a document attribute (§8.4) |
| 9 | Uploads | Identity = (source, **upload slot**); the content hash is the version, so a replace keeps the document id; the file name is the document's `DisplayName`, never `Title` (§9) |
| 10 | Attachments | P2 with email (Z1); none in P1 (§10) |
| 11 | ClamAV | Own `INSTREAM` client; fail closed (no file enters parsing unscanned); quarantine blob kept 30 days, metadata only in the UI (§11) |
| 12 | Change detection | ETag/Last-Modified where sent, sitemap `lastmod` where reliable (measured on a 500-URL sample first, weakness review §3), then a hash over **noise-normalised** HTML (raw bytes for other types); a metric measures the remaining noise (§12) |
| 13 | Effect tags | Declared per setting in the source JSON schemas (`x-effect`); the worker plans from per-tag settings hashes, the admin shows the impact before save (§12.3) |
| 14 | Deletion guard | Proposals collected per run, applied in a **reconcile phase** after all fetches; pause above 20 % (min 5) or 500, or on an empty source; held documents after a rejection (§13) |
| 15 | SharePoint (P2) | Stored `deltaLink` per drive, tombstones and moves processed, 410 → enumeration through the guard, subscriptions as hints only; ACLs not mirrored (§14) |
| 16 | Site profiles | A **versioned profile per host**; P1: a manual profile JSON (umea.se: the hand-checked Sitevision preset); **P2 (weakness review §1)**: **learned**, (CMS preset such as Sitevision, extraction selectors, verified identity rule, session parameters, excluded types, `lastmod` reliability, JavaScript need), applied automatically with guardrails, audited, one-click rollback (§5.6) |

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
    // fetch job, fetch stage: one work item → content, 304, gone, skipped, refused or transient.
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

public sealed record WorkItem(IdentityKind Kind, string CanonicalIdentity, Uri? Url, short Depth,
    WorkHints Hints);                                                       // hints: sitemap lastmod, upload slot

public abstract record FetchOutcome
{
    public sealed record Content(RawContentRef Raw, Validators Validators, IReadOnlyList<Link> Links, FetchFacts Facts) : FetchOutcome;
    public sealed record NotModified(Validators Validators) : FetchOutcome;   // 304 or lastmod skip; links from stored outlinks (§5.4)
    public sealed record Gone(string Reason) : FetchOutcome;                  // http.404, http.410, http.moved_out_of_scope
    public sealed record Skipped(string Reason) : FetchOutcome;               // robots.*, trap.*, redirect.out_of_scope
    public sealed record Refused(RunItemOutcome Outcome, string Reason) : FetchOutcome; // blocked, unsupported, too-large
    public sealed record Quarantined(BlobKey Key, string Sha256, string Signature, string Reason) : FetchOutcome; // malware (§11)
    public sealed record Transient(string Reason, TimeSpan? RetryAfter, bool CountsAttempt) : FetchOutcome;
    public sealed record Failed(string Reason) : FetchOutcome;                // no retry fixes it: 401/403, TLS, too many redirects
}

public interface IIdListingConnector : IConnector        // cheap "what exists now" for deletion detection
{ IAsyncEnumerable<string> ListIdentitiesAsync(ListingContext context, CancellationToken ct); }

public interface IRetryFailedConnector : IConnector      // rebuild a work item from a failed run item
{ WorkItem? FromIdentity(string canonicalIdentity, FailedItem last); }   // null: not fetchable (PR #122)

public interface IHierarchyConnector : IConnector        // tree for the admin (P2: SharePoint site → drive → folder)
{ IAsyncEnumerable<HierarchyNode> GetChildrenAsync(HierarchyContext context, string? parentId, CancellationToken ct); }

public interface IPreflightCheck : IConnector            // runs on an unsaved configuration
{ IAsyncEnumerable<PreflightResult> CheckAsync(PreflightContext context, JsonElement settings, CancellationToken ct); }

public sealed record PreflightResult(string Check, PreflightState State /* pass | warn | fail */, string Code,
    string? Target, JsonElement? Facts);    // Code = <area>.<code>; the admin maps it to Swedish text and remediation
```

As built in P1a item 6 part 1 (PR #82, 2026-10-31) and P1b item 2 (PR #88, 2026-10-31):
- `FetchOutcome` adds `Quarantined` (a ClamAV finding: the bytes are already in `quarantine/{sourceId}/{sha256}`; the
  runner writes the `QuarantinedFiles` row and event 5102) and `Failed` (run item `failed`). `Content` has no
  `Links` yet; link extraction comes with the crawl (item 6 part 2).
- Preflight results carry codes only, never Swedish text (AGENTS.md: UI text only in `labels.ts`/`messages.ts`).
  Each check has a `<area>.<code>` code (`http.404`, `robots.disallow`, `egress.blocked`, …), an optional `Target`
  (URL or host) and small `Facts`; the admin shows the text and remediation for the code. The job result
  (`contracts/jobs/v1/preview.preflight.result.schema.json`) adds the worst state, `estimatedPages` (crawl) and the
  recognised `cms`. The preflight payload carries the unsaved settings as an object, which the admin validates
  against `contracts/sources/v1/{type}.schema.json` (V25) before it enqueues. As built in P1b item 5 (PR #117,
  2026-10-31): further codes `preflight.none_reachable`, `pages.within_limit`, `sitemap.found` / `sitemap.none`,
  `robots.ok` / `robots.missing` and `cms.unknown`. In a URL-list preflight a problem with one URL is a `warn`, and
  `fail` only when no sampled URL answers (`preflight.none_reachable`); in a crawl preflight start-URL and robots
  problems are a `fail`.
- `UrlListConnector` is built but not registered in DI yet: it needs `IBlobStore` in the worker (item 9 wires the
  fetch stage).
- **Raw blobs of `internal` sources are not envelope-encrypted yet**: the data-model §7 blob layer is missing. It must
  exist before an `internal` source fetches (tracked for item 6 part 2 / item 9).

C# async iterators cannot return a value, so the checkpoint is an event (Onyx returns it from the generator and its
runner enforces "exactly one, at the end", `onyx/connectors/connector_runner.py:58-95`); our runner enforces "the last
event before `Done` is a `Checkpoint`". Connectors get no `DbContext` (as stages, 6a §5.1): frontier, documents and
run items are written by the runner.

| Connector | Fetch | Id listing | Retry failed | Hierarchy | Preflight |
|---|---|---|---|---|---|
| `UrlListConnector` | yes | yes (the configured list) | yes | – | yes |
| `CrawlConnector` | yes | no: deletions come from a complete traversal (§5.4) | yes | – | yes |
| `UploadConnector` | yes | yes (`Uploads` slots not deleted) | yes | – | – |
| `SharePointConnector` (P2) | yes | yes (delta state / enumeration) | yes | yes | yes (grants) |

### 2.2 Mapping onto 6a jobs and stages

| Step | Job (6a §9.1) | What runs |
|---|---|---|
| Listing | `sync`, phase `list` | `ListAsync`; items become frontier **claims** (URL list, crawl) or direct `fetch` jobs (uploads, SharePoint); yields after 1,000 items or 2 min (6a §9.2) |
| Fetch | `fetch` | fetch stage `http-fetch` / `crawl` / `upload` calls `FetchAsync`; the raw blob is written once under its content hash; for each served source whose document gets a new content hash it commits the document (new `CurrentExecutionId`, 6a §7.5) and continues that document's pipeline; on `NotModified` or same hash it calls `EnsureCurrent` (6a §2.3) |
| Discovery | `fetch` | `Content.Links` / stored outlinks become new claims for every source claiming the URL (§6.2) |
| Reconcile | `sync`, phase `reconcile` | deletion candidates, guard, soft deletes (§13); enqueued by the finalizer when a run has no open jobs |
| Retry | `sync` (`kind = retry-failed`) | `FromIdentity` for each failed item of the last run; no reconcile deletions |
| Preflight, site profile | `preview` (new kinds `preflight`, `site-profile`) | interactive, highest priority; results in `Jobs.Result`; a URL-list preflight samples ≤ 20 URLs (admin design §4.7); `site-profile` learns or checks a host's profile (§5.6) |

### 2.3 Checkpoints and resume

- Stored in `content."ConnectorCheckpoints"` (may hold paths), not in the job payload (4 KB cap, a SharePoint delta
  link alone is ~500 bytes per drive): `SourceId`, `Classification`, `Kind` (`run` = position of the current listing;
  `state` = durable cursor such as delta links), `SchemaVersion`, `Checkpoint jsonb`, `RunId`, `UpdatedAt`. Serialised
  with System.Text.Json source generation.
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
| 2 | Host lower-case, trailing dot removed, IDN → A-label (UTS 46 non-transitional, `IdnMapping`, STD3 rules); a numeric IPv4 host in any spelling → dotted quad (as built, PR #82) | `Umeå.SE.` → `xn--ume-wla.se`; `0x7f.1` → `127.0.0.1` |
| 3 | Default port removed (80/443); userinfo refused (`url.userinfo`) | `:443/` → `/` |
| 4 | Empty path → `/`; dot segments removed (RFC 3986 §5.2.4); duplicate slashes kept (servers differ) | `/a/./b/../c` → `/a/c` |
| 5 | Percent-encoding: decode unreserved (`A–Z a–z 0–9 - . _ ~`), upper-case remaining hex, encode non-ASCII as UTF-8; reserved escapes (`%2F`, `%3F`) kept | `%7e` → `~`; `ö` → `%C3%B6` |
| 6 | Fragment removed (also `#!`); SPA hash routing is not supported in P1 | `/a#top` → `/a` |
| 7 | Query: drop the parameters in the list below, the site profile's `ignoreQueryParams` (§5.6) and the source's `ignoreQueryParams`; sort the rest by name (stable for repeated names); drop an empty `?`. An `ignoreQueryParams` entry ending in `*` is a prefix, like `utm_*` in the fixed list (as built in P1a item 8, PR #104, 2026-10-31; the Sitevision preset drops `sv.*` and `open.*`) | `?b=2&utm_source=x&a=1` → `?a=1&b=2` |
| 8 | Session tokens in the path removed: `;jsessionid=…`, ASP.NET `/(S(…))/` | `/p;jsessionid=AB` → `/p` |
| 9 | Trailing slash **not** changed; `index.html` **not** removed (both can be different resources); the source setting `directoryIndexNames` opts in | – |
| 10 | Site-profile **identity rule** (§5.6), only when verified for the host: the URL maps to the rule's form (Sitevision: the node id, `/{path}.4.88db09b1724ee319ca45d.html` → `/4.88db09b1724ee319ca45d.html`), so a page moved in the tree keeps its identity; `Documents.Url` keeps the URL as fetched for citations | – |

Dropped query parameters (case-insensitive): `utm_*`, `gclid`, `gbraid`, `wbraid`, `dclid`, `fbclid`, `msclkid`,
`yclid`, `twclid`, `igshid`, `mc_cid`, `mc_eid`, `_ga`, `_gl`, `_hsenc`, `_hsmi`, `hsCtaTracking`, `mkt_tok`,
`jsessionid`, `phpsessid`, `aspsessionid*`, `sessionid`, `cfid`, `cftoken`. The list and rules carry a version
(`Settings.Crawl.IdentityRulesVersion`); changing it is a release-notes item with a one-time identity re-sync of crawl
and URL-list sources (identity tag, §12.3). A site profile's identity-relevant fields carry the profile version; a
change is an identity re-sync of the sources on that host.

`IdentityHash` = SHA-256 of the canonical identity (data-model §2.2). Duplicates the rules leave (trailing slash,
`index.html`, mirror hosts, unverified aliases) are **reported**, not guessed: the crawl report lists groups of URLs
with equal content hash and suggests a setting or an exclude pattern (§5.5).

### 3.2 Redirects

- Followed manually, at most **5 hops**, each hop re-checked by the egress guard, the source scope and the target
  host's robots.txt (§7). Loops and hop 6 → `failed` `redirect.limit`; https → http → `blocked` `redirect.downgrade`.
- **Identity = final URL** (then rule 10, if the host's profile has a verified identity rule), whatever the redirect
  type, so `/` and `/start` that both land on `/start` are one document. The run item records the chain; the frontier
  row of the first URL stores `RedirectTo`.
- Target outside the source scope: permanent (301/308) on a known document → deletion candidate
  `http.moved_out_of_scope`; temporary (302/303/307, typically a sign-in wall) → `skipped` `redirect.out_of_scope`,
  never a deletion. Target blocked by robots → `blocked` (6a §10.2).

### 3.3 `rel=canonical`: trusted only when verified

Misconfigured canonicals (every page pointing at the start page, at a staging host, at http) are common on CMS sites,
and acting on one blindly drops content silently, the worst failure class here (comparison F6/F7). Aliases that serve
a page with 200 and no redirect are common too (umea.se: `/doit` and the id-only URL serve the same page and declare
the full path canonical, simplification pass §13.1). The rule is therefore **trust only when verified**:

- The declared canonical is stored (`Documents.CanonicalHint`) and its URL is added as a discovered link (same scope
  rules).
- A page is **folded** into its canonical's identity only when the canonical is in scope on the same host and its
  content hash (from the frontier: fetched in this run or within the freshness window, §6.2) equals the page's own
  normalised content hash. A folded alias creates no document of its own; the run item records `identity.folded`.
- Otherwise the page keeps its own identity and the crawl report lists it under "canonical elsewhere, not verified" so
  an admin can add an exclude.

### 3.4 Uploads

| Kind | `IdentityKind` | `CanonicalIdentity` | Notes |
|---|---|---|---|
| URL | `url` | canonical final URL (§3.1, incl. rule 10) | – |
| Upload | `upload` | `upload:<uploadId>` (the upload slot, §9) | the content hash is the version; a replace keeps the document (§9.3) |
| SharePoint item (P2) | `sharepoint` | `sharepoint:<driveId>:<itemId>` | stable across moves and renames (§14) |

A document is (source, identity): two sources with the same URL or the same file have two documents, which share the
frontier request, the content-addressed raw blob and the parse and embedding caches (simplification pass §2).

## 4. URL list

- Exactly the listed URLs (≤ 5,000 per source), normalised on save by the admin with the shared canonicaliser (so
  the form shows duplicates), never following links. Listing yields claims at depth 0; politeness and sharing are
  the frontier's (§6).
- **Conditional GET**: `If-None-Match` with the stored ETag (weak ETags sent as received) and `If-Modified-Since`
  with the stored `Last-Modified` string (never our own clock). Validators are stored only from 200 responses, on the
  frontier row (one request serves every source, §6.2). A forced run (`force`, or every `revalidateEvery`-th run,
  default 4) sends no validators, catching servers whose 304s are wrong (the every-`revalidateEvery`-th run is not
  counted yet: as built in P1a item 9, PR #108, 2026-10-31, every sync runs with `Revalidate` false). Hosts that send neither (umea.se: no `ETag`,
  no `Last-Modified`, `Cache-Control: no-store`) get a plain GET; change detection then rests on sitemap `lastmod`
  (crawl, §5.4) and the content hash (§12).
- **Cookies are never stored or sent** (`UseCookies = false`, `Set-Cookie` ignored; umea.se sets session cookies).
- **robots.txt is honoured for URL lists too**, with the same rules as the crawl (§5.1 "always"; this section was
  silent; as built in P1a item 6 part 1, PR #82, 2026-10-31). A listed URL that robots disallows ends `skipped`
  `robots.disallow`.
- **User agent** `UmeaKommunBot/1.0` (as built, PR #82). The about-page URL is not in it yet: it waits for user item
  U1 (§22).
- **Frontier admission** (as built in P1a item 9, PR #108, 2026-10-31): a URL-list run admits its URLs through a
  `CrawlRuns` row like a crawl (`MaxPages` 5,000, `MaxDepth` 0), so both use the same frontier code (§6.0).

| Response | Result | Outcome / reason | Deletion |
|---|---|---|---|
| 200 | content; stage 2 change detection (§12) | `added` / `updated` / `unchanged` | – |
| 304 | `NotModified`; `EnsureCurrent` | `unchanged` | – |
| 301/302/303/307/308 | follow (§3.2) | – | §3.2 |
| 404, 410 | `Gone` | known document: `deleted` after reconcile; never indexed: `failed` `http.404` (a listed URL is broken) | candidate |
| 401, 403 | failure | `failed` `http.403` | after 4 consecutive runs and 14 days (§13.1) |
| 429, 503 with `Retry-After` | `Transient`, no attempt; host back-off (§6.4) | – | – |
| other 5xx, timeout, connection reset, DNS failure | `Transient` with attempt (5, data-model §4.2), then `failed` `http.5xx` / `net.timeout` / `net.dns` | – | as 401 |
| TLS failure | `failed` `tls.invalid_certificate`; no "ignore certificate" option (internal hosts use the internal CA bundle, repo-and-deployment §6.3) | – | as 401 |

**Limits**: HTML/XHTML ≤ 10 MB, other types ≤ 50 MB (= upload limit; parser body 60 MB, parser §11.1), counted
after decompression (a gzip bomb hits the cap); response headers within 15 s; whole body within 30 s (HTML) or 120 s
(files). Accepted content types are the formats of the source type's pipeline (6a §13.1, parser design §3) plus
`application/xml`/`text/xml` for sitemaps; anything else stops before download (`unsupported` `http.content_type`,
only the headers are read).

## 5. Website crawl

### 5.1 Seeds: robots.txt and sitemaps

- **Seeds** = start URLs + URLs from sitemaps (depth 0). Sitemaps come from the source's `sitemaps`, every
  `Sitemap:` line of the scope hosts' robots.txt, and `/sitemap.xml` if it exists.
- **robots.txt** (RFC 9309, own parser of ~250 lines: the group matching `UmeaKommunBot` (case-insensitive) else
  `*`; longest match wins, `Allow` beats `Disallow` on ties; `*` and `$`; 500 KiB parse cap). Cached per host in
  `CrawlHosts` + blob `crawl/{host}/robots.txt` (data-model §9) for 24 h. Fetch outcomes: 2xx parse; 4xx →
  **allow all** (RFC 9309 "unavailable"); 5xx or unreachable → **disallow all** for the run (`RobotsStatus =
  unreachable`; the run's seeds on that host fail and the host counts as **paused** for the run, so the traversal is
  incomplete and nothing is `not-seen` however few seeds the host has, §5.4). As built in P1a item 6 part 1 (PR #82,
  2026-10-31): a robots 429 counts as unreachable (disallow all), not as a 4xx; a robots.txt request the egress guard
  refuses is unreachable too, and the URLs behind it end `blocked` with the guard's `egress.*` reason. The cache
  is per origin (scheme, host, port) and, until the fetch stage is wired to the database (item 9), in memory per
  worker instance; an unreachable answer is kept for 10 minutes only, so the next run asks again.
  robots.txt is always honoured (answered 4, 11), also on municipal hosts; it is re-checked per redirect hop.
- `Crawl-delay` is honoured only when it **lowers** our rate, capped at 60 s.
- **Sitemaps**: `XmlReader` with `DtdProcessing.Prohibit` and no resolver; gzip allowed; sitemap index depth ≤ 2;
  ≤ 50,000 URLs and ≤ 50 MB uncompressed per file (sitemaps.org limits) and ≤ `maxPages` in total; out-of-scope
  and excluded-type entries dropped and counted. Each URL's `lastmod` is stored on its frontier row
  (`SitemapLastMod`) and used by the `lastmod` skip and incremental runs (§5.4). umea.se: `robots.txt` →
  `sitemapindex.xml` → one `sitemap1.xml.gz` with 24,184 URLs, all with `lastmod`. As built in P1b item 4 (PR #101,
  2026-10-31): a sitemap that cannot be read **pauses its host** for the run (once), like an unreachable robots.txt,
  instead of counting as one failed seed; otherwise one failed sitemap among several would leave the ratio rule
  (§5.4) satisfied and allow mass `not-seen` proposals for the URLs it lists.

### 5.2 Scope

| Rule | Definition |
|---|---|
| Scope hosts | hosts of the start URLs + `extraHosts` (answered 13) |
| Patterns | `include` / `exclude` / `demote` lists over the canonical URL without scheme (`host/path?query`). Glob: `*` = any characters except `/`, `**` = any characters, everything else literal (also `?` and `.`); host part case-insensitive, path case-sensitive. Advanced: `re:<regex>` (.NET `RegexOptions.NonBacktracking`, 50 ms timeout). Exclude wins; empty `include` = everything on the scope hosts. **`demote`** ([weakness review §3](../weakness-review.md#3-outdated-or-removed-content-keeps-being-answered)) entries are `{ "pattern", "factor" }` (factor in (0, 1), default 0.5) for archive sections such as old minutes: they change no crawling, only ranking (knowledge design §6.5, the lowest matching factor applies); set on the source and shown on the source page; the worker compiles them with the scope (`CrawlScope.DemoteFactor`) but keeps no copy on documents or chunks, and retrieval reads `Sources.DemoteRules` and needs the same glob semantics (`UrlPattern`; as built in P1b item 4, PR #101, 2026-10-31). Eneo passes the start URL itself as a regex (`LinkExtractor(allow=url)`, `eneo/crawler/spiders/crawl_spider.py:25-34`), so `.` and `?` match anything |
| Off-site | a link to a non-scope host is admitted **only** if an `include` pattern names that host explicitly; it is fetched as a **leaf** (its links are never followed). Otherwise counted in the report, not fetched (answered 13) |
| Depth | link distance from a seed; default `maxDepth` 10 (answered 12); a redirect does not add depth |
| Pages | default `maxPages` 10,000 (answered 12), counted as **admitted claims** per run (`CrawlRuns.Admitted`), seeds included. Reaching it: no more admissions, run `degraded` `crawl.limit_pages`, traversal partial → no deletions. umea.se has ≈ 14,100 HTML and PDF sitemap URLs, so its source needs `maxPages` ≥ 20,000 (the preflight warns when the sitemaps alone exceed `maxPages`) |
| Municipal host | host name matches `Settings.Crawl.MunicipalHosts` (platform-admin list, e.g. `umea.se`, `*.umea.se`; audited). Decides rate (§6.3) and `noindex` handling. Separate from the **address** allow-list `UME_RAG_CRAWL_ALLOW` (§7), which only lifts the private-range block: an internal municipal host on 10.x must be in both |
| `noindex` / `nofollow` | meta robots and `X-Robots-Tag` (also the `UmeaKommunBot:`-prefixed form; `none` = both). **External host**: `noindex` → `skipped` `robots.noindex` (links still followed unless `nofollow`); `nofollow` → no links taken from that page. **Municipal host**: both ignored, recorded as facts (answered 11). `rel="nofollow"` on single links is a ranking hint and ignored everywhere |
| Content types | links to types outside the pipeline's formats are not admitted (`.zip`, `.exe` never fetched). **Images and video are excluded by default** (site profile `excludedTypes`, §5.6; their alt text and captions come from the pages; umea.se lists ≈ 8,900 images and 233 videos). Archives and macro-enabled files fetched anyway end `unsupported` (Z1); legacy Office, RTF and email end `unsupported` `http.format_deferred` in P1 (§9.2) |

### 5.3 URL-space traps

Detected per run in the fetch path; each detection is a `skipped` `trap.<kind>` for admitted URLs and a counter
with an example pattern in the report.

| Trap | Detection | Action |
|---|---|---|
| Endless paths | URL > 1,024 chars, > 15 path segments, or a segment sequence repeated ≥ 3 times (`/a/b/a/b/a/b`) | not admitted |
| Faceted search, sorting | > 50 distinct query strings admitted for one (host, path) in a run, or > 6 query parameters | further variants not admitted (`trap.query_variants`) |
| Calendars | a path or query value parses as a date/year outside [now − 5 y, now + 2 y], or > 100 date-like URLs under one path prefix | not admitted (`trap.calendar`) |
| Session ids | removed by normalisation (§3.1 rule 7–8); > 20 URLs whose only difference is one unknown parameter → that parameter is reported as a candidate for `ignoreQueryParams` (and proposed to the site profile, §5.6) | report |
| Soft-404 / duplicate fan-out | ≥ 20 URLs in a run with the same content hash | their links are expanded once per content hash (a page already expanded adds nothing new) |
| Default excludes in the `website` template form | `**/sok?*`, `**/search?*`, `**?*sort=*`, `**?*print=*`, `**/login*`, `**/logout*` | editable |

### 5.4 Traversal, incremental runs and deletions

- **Traversal** (every scheduled crawl, default weekly Saturday 01:00 with jitter, 6a §9.6; answered 7): BFS from the
  seeds through the frontier. For every reached URL, one of: a **`lastmod` skip** (no request) when the host's site
  profile marks `lastmod` reliable (§5.6) and the URL's `SitemapLastMod` is not newer than the frontier's
  `LastFetchedAt`; otherwise a GET, conditional when validators are stored (§4). A skip, a 304 or an unchanged hash
  expands links from the **stored outlinks** of that content (`raw/{level}/{contentHash}.links.json.gz`, written with
  the raw blob), so an unchanged site costs at most one request per page and no parsing (plan §9 target).
- **Complete** traversal ([weakness review §3](../weakness-review.md#3-outdated-or-removed-content-keeps-being-answered),
  replacing "no seed failed": one failing URL among 14,000 blocked every `not-seen` deletion) = **≥ 99 % of seeds
  resolved**, no admitted claim of the run is left pending, no host was paused for the run (§6.4) and the page limit
  was not hit (`CrawlRuns.Complete`). A seed is **resolved** when it was fetched, returned 304, was skipped by
  `lastmod`, or is `Gone`. Only then are documents not reached in this run deletion candidates (`not-seen`); a
  document whose URL is a **failed seed** of the run is never a deletion candidate in that run (its failures count
  towards `persistent-failure`, §13.1; as built in P1a item 9 part 2, PR #122, 2026-10-31: failed seeds are the run's
  depth-0 frontier rows whose document the run did not see). The deletion guard (§13.2) stays. Eneo salvages a timed-out crawl and then
  deletes every page it did not reach (`eneo/worker/crawl_tasks.py:1008-1014`, `:1206-1232`); acceptance test C7
  (§18) covers that case. As built in P1b item 4 (PR #101, 2026-10-31): the rule is chosen by the deployment value
  `Crawl:CompletenessRule` = `Ratio` (default) or `Strict` (P1a's "no seed failed", weakness review §3 "Phasing").
- Explicit `Gone` (404/410, permanent redirect out of scope, robots now disallowing) is a candidate in any run,
  complete or not.
- **Incremental** run (`incrementalSchedule`, e.g. daily; a crawl setting only, a five-field cron that the schema
  checks by field count only, default `null`; as built in P1b item 2, PR #88, 2026-10-31; the `sync` payload marks
  the run `incremental`): fetches seeds whose sitemap `lastmod` is newer than the
  stored `LastFetchedAt` plus sitemap URLs not yet known, expands links only from changed pages, never marks a
  traversal complete, so it deletes only on `Gone`. The `website` template sets a daily `incrementalSchedule` **by
  default** when the host's site profile marks `lastmod` reliable; the crawl report measures the share of `lastmod`
  hits that really changed ([`../performance-pass.md`](../performance-pass.md) §2.3), which feeds the profile's
  reliability flag. An incremental run on a host whose applied profile does not set `lastmodReliable` runs as a full
  traversal; it is not refused (as built in P1b item 4, PR #101, 2026-10-31).
- **Removal check** (weakness review §3): a URL that **leaves the sitemap** (the listing clears the frontier row's
  `SitemapLastMod`, which was set before) is re-requested in the next incremental run, by `HEAD`, then `GET` if `HEAD`
  is refused (405/501). A source without incremental runs does the check in its next traversal. 404 or 410 makes it
  `Gone` (a deletion candidate in any run, above); a 200 counts as reached in that run and keeps the document until
  the next complete traversal that does not reach it (`not-seen`). Other outcomes follow §4. As built in P1b item 4
  (PR #101, 2026-10-31): the check runs in the run whose **complete listing** finds the departure (incremental or
  full); a sitemap entry that never had a `lastmod` never triggers one. In a full traversal a live answer to the `HEAD`
  is followed by the normal GET, and `HEAD` and GET share one host slot. Until a column exists, removal-check
  frontier rows carry `Depth = -1` (schema request to stream B: `CrawlFrontier.RemovalCheck`, with
  `CrawlRuns.Incremental`).
- **`lastmod` is measured before it is trusted** (weakness review §3): a host's `lastmodReliable` (§5.6) is set only
  after a sample of **500 random sitemap URLs** has been fetched and their noise-normalised content hashes compared
  with their `lastmod` over time ([`../to-verify.md`](../to-verify.md) #170). Until then the host gets no `lastmod`
  skip and no default incremental runs: the umea.se source runs **full traversals** (every URL fetched; the content
  hash still saves parsing and embedding). umea.se has `lastmod` on every URL and the example page's `lastmod` equals
  its visible "Sidan uppdaterades" date, but one page is not evidence. As built in P1b item 4 (PR #101, 2026-10-31):
  the sample's comparison (≤ 2 %, ≥ 7 days apart) and its `edit-site-profile` proposal exist; the fetch runner for
  the live first pass does not (to-verify #170, after U1).
- `revalidateEvery` (default 4): every 4th traversal sends no validators and makes no `lastmod` skips (§4), catching
  wrong 304s and stale `lastmod` values. As built: the crawler drops the `lastmod` skip for a revalidating run
  (`CrawlFreshness.Revalidates`, PR #101); the engine counts the source's earlier full traversals and fixes the run's
  mode (`incremental`, `revalidate`) when its listing starts, in `Runs.Report.mode`, so every job of the run reads the
  same answer (P1a item 9 part 2, PR #122, 2026-10-31).
- **Incomplete-traversal alert**: a source whose last N traversals (`Settings.Crawl.IncompleteRunsAlert`, default 3)
  were all incomplete raises an alert (metric `rag.crawl.incomplete_runs`, the source page and the Overview page),
  because its deletions never run and removed pages stay in the index. A URL-list or upload listing that ends
  `Partial` counts the same way.
- **Engine wiring** (P1b item 4, PR #101, P1a item 9, PR #108, and P1a item 9 part 2, PR #122, 2026-10-31): crawl
  sources run through the engine (the `fetch` handler dispatches them to `CrawlSourceRunner`; the frontier row
  completes in the document's commit), with the run's mode, the report sections in `Runs.Report` (§5.5), the
  `rag.crawl.incomplete_runs` gauge and the alert's history query.

### 5.5 Crawl report

Built by the finalizer from `RunItems` and `CrawlRuns` into `Runs.Report` (jsonb) and shown on the source page
(admin plan §4). URLs appear only in the admin, never in telemetry (§16). As built in P1a item 9 part 2 (PR #122,
2026-10-31): `Runs.Report` holds `mode` (§5.4) and the sections `completeness`, `verdict`, `removalChecks` and
`demote` (live documents per pattern); the other rows below are not built yet.

| Section | Contents |
|---|---|
| Summary | traversal complete or why not (limit, resolved seeds below 99 %, host paused, cancelled), the share of resolved seeds, failed seeds protected from `not-seen`, removal checks and their results (weakness review §3) and the count of consecutive incomplete traversals (§5.4); pages admitted / fetched / `lastmod`-skipped / not modified / indexed; duration; requests per host and effective rate |
| Failures by reason | count per `Reason` code with ≤ 10 example URLs each; broken links (`http.404` of never-indexed URLs) separately; Eneo's `failure_counts` idea (`eneo/worker/crawl_tasks.py:956-958`) |
| Exclusions | robots-disallowed, out of scope, off-site, excluded by pattern or type, `noindex`: counts + top path prefixes |
| Traps | kind, pattern, count, example |
| Duplicates | groups of URLs with equal content hash; folded aliases; canonicals elsewhere not verified (§3.3); suggested setting |
| Short pages | info note: pages with < 200 visible characters while the HTML is > 5 KB; never `degraded` (rendering is not part of P1, §8.1) |
| Site profile | profile version used; empty-root rate, boilerplate share and text yield against the previous run (the re-learn trigger from P2, §5.6) |
| Noise | pages whose hash changed but whose parsed output did not (§12.2), by host |
| Deletions | proposed / applied / held, by reason (§13) |

### 5.6 Site profiles (learned per host)

**P1 ([weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer)):** profiles are **manual**. The umea.se profile is a **hand-checked Sitevision preset**: a
profile JSON with the schema below (every field set by hand and checked against the fixture corpus), stored as a
`manual` version and versioned like any config (edit and one-click rollback through `edit-site-profile` and
`rollback-site-profile`). Learning, automatic application, proposals and re-learning below are **P2** and add to
manual profiles of the same schema. `lastmodReliable` is set by hand in P1, and **only from the recorded result of
the 500-URL `lastmod` sample** (§5.4, [weakness review §3](../weakness-review.md#3-outdated-or-removed-content-keeps-being-answered), `to-verify.md` #170); until then it is `false` and
the umea.se source runs full traversals.

A **site profile** belongs to a (level, host), is shared by every source crawling that host, and is versioned JSON:
`content."SiteProfiles"` (one row per level and host, `PresetId`, `AppliedVersionId`, `NextLearnAfter`) and immutable
`content."SiteProfileVersions"` (`Profile jsonb`, `Origin` `learned`/`manual`/`preset`, `State`
`proposed`/`applied`/`superseded`/`rejected`, `Sample jsonb` with the check result, `CreatedBy` `system:worker` or an
admin `oid`, `AppliedAt`, `AppliedBy`; data-model §2.2). Platform admins may edit it (a `manual` version). As built
in P1a item 10 (PR #115, 2026-10-31): `edit-site-profile` and `rollback-site-profile` carry an optional `decidedBy`
for `CreatedBy`/`AppliedBy` (absent = `system:worker`). As built in PR #136 (2026-10-31): the host key is the
canonical host (§3.1 rule 2) **without a port**, so one profile serves its host on every scheme and port; the
`edit-site-profile` contract, the only writer, allows no port, and the crawler, the follow-up and the applied-profile
lookup all derive the key the same way (`SiteProfileHost`). A fetch on another worker may use a profile up to 60 s
old (PR #141).
It is the one place where learning configures a stage automatically (simplification pass §2 exception, §13.2): it is
deterministic, previewed and checked, not trial-and-score learning.

| Field | Derived from the sample | Used by |
|---|---|---|
| `cms` | fingerprint (Sitevision: `sv-*` classes, `/sitevision/` resources, `svid…` ids) | selects the built-in **preset** whose values seed every field below |
| `mainRoot` | landmark (`main`, `article`, `role=main`), else text density | HTML extraction (processing design); the Sitevision preset's root is `main, .pagecontent` (landing pages have no `main`), alternatives tried in order (as built in P1a item 8, PR #104, 2026-10-31) |
| `boilerplate` | DOM blocks and phrases inside the root repeated on ≥ 60 % of sampled pages (umea.se: share widget, short-URL line) | HTML extraction strips them |
| `linkBlocks` | repeated child-link lists inside the root (umea.se: "Undersidor") | kept for link discovery, not chunk text |
| `breadcrumb` | `aria-label` (Brödsmulor/breadcrumb), `BreadcrumbList`, or class name; screen-reader text removed | chunk prefix (processing design) |
| `datePattern` | visible dates matched against sitemap `lastmod` ("Sidan uppdaterades YYYY-MM-DD") | `SourceModifiedAt`, fallback `lastmod` |
| `identityRule` | id-only URLs fetched for a few sampled pages; verified when their extracted output (`OutputHash`) equals the full URL's (as built for umea.se in P1a item 8, PR #104, 2026-10-31: the raw hashes differ by per-request noise and by the page's own path in a form `action`, so content hashes never match) | identity (§3.1 rule 10) |
| `ignoreQueryParams` | preset list plus parameters whose values differ between fetches of the same page; a trailing `*` is a prefix (PR #104) | URL normalisation (§3.1 rule 7) |
| `excludedTypes` | default `image/*`, `video/*` | scope (§5.2): by extension at admission and by response Content-Type at fetch, so an extensionless file of an excluded type is neither stored nor ingested (as built in PR #136, 2026-10-31) |
| `lastmodReliable` | `lastmod` against visible dates and content-hash changes across runs; never `true` before the 500-URL sample has passed (§5.4, weakness review §3) | `lastmod` skip and default incremental runs (§5.4) |
| `javascriptNeeded` | raw text yield of the sampled pages | the P1-start renderer check (§8.1) |

- **Learning** (P2; deterministic, no LLM): when a crawl source's host has no profile (at its first preview or first
  sync) a `preview` job of kind `site-profile` samples 30–50 pages from the sitemap, spread over path sections and
  fetched through the frontier's host budget (§6.3), and derives the profile. Built-in presets live in code next to
  `IPipelineNode` (Sitevision in P1; WordPress and Episerver/Optimizely later); learned values override the preset
  field by field.
- **Applied automatically, with guardrails** (P2): a new host's profile is applied at once and shown in the source form
  preview (before/after text of sample pages, admin design). A **re-learned** profile for a host in use is applied
  automatically only when its check passes on the sample: no page loses more than 10 % of its main-text words, no page
  ends up empty, and the boilerplate share does not rise; otherwise it is stored `proposed` for a platform admin.
  Every application moves `AppliedVersionId` and is written to the admin audit log; a platform admin's decisions are
  `command` jobs: `approve-site-profile` and `reject-site-profile` (a proposal), `edit-site-profile` (a manual
  version) and `rollback-site-profile` (one click, to the previous version). Each document records the version that
  extracted it
  (`Documents.SiteProfileVersionId`).
- **Re-learning** (P2) runs when a run report shows a quality drop (empty-root rate, boilerplate share or text yield moved
  by more than a threshold, e.g. after a CMS redesign; thresholds *to tune* on the fixture corpus) and at most weekly
  per host.
- **Effects of a new version** (§12.3): extraction fields re-process the host's HTML documents from stored raw
  content without fetching (as built in P1a item 10, PR #115, 2026-10-31: `edit-site-profile` and
  `rollback-site-profile` enqueue a `sync` `reprocess`, trigger `site-profile`, dedup `sync:{sourceId}`, per source
  with live HTML documents on the (level, host); there is no per-document entry point, and the drift sweep is the
  backstop); `identityRule` and `ignoreQueryParams` changes are an identity re-sync of the sources on
  the host; `excludedTypes` is a scope change. As built in PR #141 (2026-10-31): when a version changes
  `excludedTypes`, `identityRule` or `ignoreQueryParams`, each crawl source with live documents of any type on the
  host gets a `sync` of kind `sync` (trigger `site-profile`) instead of the `reprocess`; its traversal admits by the
  new profile, its fetches run `EnsureCurrent` on what they reach, and its reconcile removes the documents of a newly
  excluded type without a request (`scope-narrowed`) and the identities the new rules no longer produce
  (`identity-changed`), both exempt (§12.3). Pages the traversal does not reach are re-extracted by the nightly drift
  sweep.
- **Acceptance (P1)**: the umea.se example page (simplification pass §13.1) extracts to exactly its main text, without
  share widget, short-URL line and child-link list, with its breadcrumb and the date 2026-01-15; ≈ 20 saved umea.se
  pages (HTML and PDF) are checked in as a regression corpus (§18). As built in P1a item 8 (PR #104, 2026-10-31):
  20 HTML pages and 1 PDF in `tests/fixtures/umea/` with goldens, byte-identical to what umea.se served (officials'
  names and contact details as published; one form action keeps the expired anonymous `jsessionid` of the capture
  request, so the SHA-256s hold); the live breadcrumb's third crumb is "Förvaltningar, verksamheter"; hub pages (the
  landing page, the news archive) extract to their heading only and end `skipped` `parse.empty`. The SmartReader
  fallback is not built (to-verify #31).

## 6. Shared crawl frontier and politeness (R2)

### 6.0 P1: one frontier per source

The shared frontier with claims and the releaser (§6.1–§6.3) is **P2** ([weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer)). In P1 each source has
**its own frontier**: `CrawlFrontier` rows are keyed by (`Classification`, `SourceId`, `UrlHash`) and carry the source's
`RunId`, `Depth` and `Leaf`, so admission is one upsert per URL and no `CrawlClaims` table, freshness-window reuse
across sources or `fetch.shared` fact exists. The source's own `sync` traversal enqueues its `fetch` jobs from its
`pending` rows in small batches per host (`Crawl:FetchBatchPerHost` in configuration), topping up as they finish. The
host budget does not change: every `fetch` takes its slot in `CrawlHosts` as in §6.3, so rates, connection caps,
back-off, breaker (§6.4) and the night window (§6.5) hold across all sources and previews. Two sources on one host
fetch a page twice; the content-addressed raw blob and the parse and embedding caches still make the second one cheap.
The rest of §6 is the P2 design.

### 6.1 Tables

| Table | Tier | Columns (new in **bold**) |
|---|---|---|
| `CrawlHosts` | M | `Host` PK, `RequestsPerSecond`, `MaxConnections`, `NextAllowedAt`, `RobotsBlobPath`, `RobotsFetchedAt`, **`Kind`** (`municipal`/`external`, derived from settings), **`RobotsStatus`**, **`CrawlDelaySeconds`**, **`BackoffFactor numeric(6,2)`**, **`PausedUntil`**, **`LatencyEwmaMs`**, **`BlockedAt`** (platform admin block) |
| `CrawlFrontier` | L | PK (`Classification`, `UrlHash`), `Url`, `Host`, `State` (**`idle`/`pending`/`released`**), `NextFetchAt`, **`LastFetchedAt`**, **`LastStatus smallint`**, **`RedirectTo`**, **`ETag`**, **`LastModified`**, **`ContentHash`**, **`SitemapLastMod`** (the last response's facts: one request serves every source, §6.2); `SourceIds` and `Depth` move to claims |
| **`CrawlClaims`** | L | PK (`Classification`, `UrlHash`, `SourceId`), `RunId`, `Depth smallint`, `Leaf bool`, `ClaimedAt`; IX (`RunId`) |
| **`CrawlRuns`** | L | `RunId` PK, `Classification`, `SourceId`, `Admitted int`, `MaxPages`, `MaxDepth`, `LimitHit`, `Seeds int`, `SeedsFailed` (the ratio of weakness review §3), `PausedHosts jsonb`, `TrapStats jsonb`, `Complete bool` |
| **`SiteProfiles`**, **`SiteProfileVersions`** | L | §5.6 (per level and host) |

### 6.2 Claims: one request for every source

1. A source admits a URL by upserting its claim; the statement returns only claims new to this run, so a URL is
   admitted once per run and the page counter stays exact:

```sql
INSERT INTO content."CrawlClaims" ("Classification", "UrlHash", "SourceId", "RunId", "Depth", "Leaf", "ClaimedAt")
SELECT @level, u.h, @source, @run, @depth, u.leaf, now() FROM unnest(@hashes, @leaf) AS u(h, leaf)
ON CONFLICT ("Classification", "UrlHash", "SourceId") DO UPDATE
   SET "RunId" = EXCLUDED."RunId", "Depth" = EXCLUDED."Depth", "ClaimedAt" = now()
 WHERE content."CrawlClaims"."RunId" IS DISTINCT FROM EXCLUDED."RunId"
RETURNING "UrlHash";
```

2. The frontier row (insert if absent) goes to `pending`, unless it was fetched within the **freshness window**
   (`Settings.Crawl.FreshnessHours`, 12 h; 0 for manual and forced runs): then the claim is served at once from the
   frontier's last response (content hash, raw blob, outlinks), without a request.
3. The **releaser** (§6.3) enqueues one `fetch` job per URL with `DedupKey = fetch:{sourceId}:{urlHash}` (6a §9.2;
   `urlHash` = SHA-256 hex of the canonical URL. In P1 there is one frontier per source, §6.0. The key names the
   frontier entry, because the identity is known only after redirects; P1a item 4, PR #78, 2026-10-31).
4. The `fetch` job writes the raw blob once under its content hash (`raw/{level}/{contentHash}`; write-once, so an
   existing key is not written again), updates the frontier row, then reads all claims of open runs for its URL at
   commit and serves each `(source, run)`: upsert of **that source's** document (`LastSeenAt`, `LastRunId`), run
   item, and new claims from the links under that source's scope and depth. A document whose content hash changed gets
   a new execution and its pipeline; parse output and embeddings are cached by content and text hash (6a §8), so every
   source after the first is cheap. Unchanged documents get `EnsureCurrent` (6a §2.3).
5. Outcome per served run: the real one for that source's document (`added`, `updated`, `unchanged`, …); a run item
   served by another run's request carries the fact `fetch.shared` (run id).

Two departments crawling umea.se on the default Saturday schedule therefore cause one request per page (plan §9).

**Nav-link claim cache** ([`../performance-pass.md`](../performance-pass.md) §3.6): per run and instance, an LRU
(100k entries) of URL hashes already admitted in this run; site-chrome links (header, footer, menus: most of a page's
≈ 100 links) skip the claims upsert after the first page. Exactness is kept because the upsert remains the authority
for the first sighting on each instance.

### 6.3 Releaser and host budgets

`FrontierReleaser`: `BackgroundService`, `PeriodicTimer` 1 s, leader by `pg_try_advisory_xact_lock` (6a §9.6
pattern). For each host with `pending` entries and `NextAllowedAt < now() + 2 s`, not paused or blocked:
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

Rates are per host and shared by every source, preview and profile sample of every level (`CrawlHosts` has no level).
On the single-host pilot the connection cap is an **in-process semaphore per host** in the one worker instance; slot
pacing bounds in-flight requests as well. A refused slot requeues the job at `now + interval` without an attempt
(`rate_limited`). Platform admins may block hosts and override a host's budget (`Settings.Crawl.HostOverrides`).
P2 (first multi-host deployment): the cap moves to a Redis in-flight lease per host (§23).

**The crawler rules stay** (answered 4; performance pass answered 7): ≤ 10 req/s per municipal host and the night
window (§6.5). Higher rates for allow-listed municipal hosts and a **daytime trickle** (e.g. 2 req/s outside the
window) are per-host settings in `Settings.Crawl.HostOverrides`, **off by default**, enabled only with the web team's
written consent ([`../performance-pass.md`](../performance-pass.md) §2.3, §10.1).

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
and shows "waiting for crawl window". At 10 req/s a 10,000-page municipal site takes ~17 minutes (umea.se's ≈ 14,100
HTML and PDF URLs ~24 minutes); at 1 req/s an external one ~2.8 h, both inside one window. Manual "sync now" and URL
lists (nightly by default) ignore the window. As built in P1a item 9 part 2 (PR #122, 2026-10-31): a `retry-failed`
crawl run follows the window unless its trigger is `manual`. A daytime trickle outside the window exists only as an off-by-default
per-host override with the web team's written consent (§6.3).

## 7. Egress / SSRF guard

```csharp
public interface IEgressGuard
{
    EgressDecision CheckUrl(Uri url, EgressPurpose purpose);   // scheme, port, userinfo, literal IPs, blocked hosts
    // Resolves once; throws EgressBlockedException (→ outcome blocked, event 5101) if any address is not allowed.
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string asciiHost, EgressPurpose purpose, CancellationToken ct);
}
public enum EgressPurpose { Fetch, Robots, Sitemap, Preflight, Preview }   // Render added with the renderer (P2)
```

| Topic | Rule |
|---|---|
| DNS pinning | One `SocketsHttpHandler` for all crawling with `ConnectCallback`: resolve via the guard, then connect **only** to the returned addresses (no second lookup, no rebinding window). TLS still uses the URL host for SNI and certificate checks. `PooledConnectionLifetime` 2 min, `UseProxy = false`, `AllowAutoRedirect = false`, `UseCookies = false` (no cookie is ever stored or sent). Same as Open WebUI's pinned connect (`owui/retrieval/web/utils.py:187-215`) and Onyx's pinned request (`onyx/utils/url.py:417-435`) |
| Any-address rule | if **any** resolved address is blocked, the host is refused (round-robin rebinding cannot win by luck) |
| Blocked IPv4 | `0.0.0.0/8`, `10/8`, `100.64/10`, `127/8`, `169.254/16`, `172.16/12` (covers our compose subnets), `192.0.0/24`, `192.0.2/24`, `192.88.99/24`, `192.168/16`, `198.18/15`, `198.51.100/24`, `203.0.113/24`, `224/4`, `240/4` |
| Blocked IPv6 | `::/128`, `::1`, `fc00::/7`, `fe80::/10`, `ff00::/8`, `2001:db8::/32`, `100::/64`; addresses that **embed** IPv4 (`::ffff:0:0/96`, `::/96`, `64:ff9b::/96`, `64:ff9b:1::/48`, 6to4 `2002::/16`, Teredo `2001::/32`) are judged by the embedded address too (Open WebUI `_embedded_ipv4`, `owui/retrieval/web/utils.py:73-93`). IPv4 is tried first; the `crawl` network may have no IPv6 at all (*to verify*). **As built, stricter** (P1a item 6 part 1, PR #82, 2026-10-31): an IPv6 address outside global unicast `2000::/3` (and outside NAT64 `64:ff9b::/96`) is blocked whole (`reserved`); local-use NAT64 `64:ff9b:1::/48` and IPv4-compatible `::/96` are blocked whole too, because where an IPv4 address would sit in them is not fixed; `::ffff:0:0/96` is judged as its IPv4 address, and NAT64 `64:ff9b::/96`, 6to4 and Teredo by the embedded one |
| Allowed exceptions | CIDRs in `Crawl:AllowedPrivateRanges`, set from the same `UME_RAG_CRAWL_ALLOW` that feeds the host firewall (`init-deployment.sh --crawl-allow`, repo-and-deployment §7.1); nothing else can lift the block |
| URL rules | `http`/`https`; ports 80 and 443 (`Settings.Crawl.AllowedPorts` may add e.g. 8443); no userinfo; IP-literal hosts only if global or allow-listed; IDN hosts checked in A-label form after UTS 46 mapping, so homograph spellings resolve to the same rules. Numeric IPv4 host spellings (decimal `2130706433`, octal `0177.0.0.1`, hex `0x7f.1`, which `IPAddress.TryParse` reads) are canonicalised to dotted quads (§3.1 rule 2) before the guard judges them (as built, PR #82; to-verify #27) |
| Redirects | manual; ≤ 5 hops; each hop through `CheckUrl` + `ResolveAsync` + scope + robots (§3.2) |
| Firewall | the `crawl` network drops RFC 1918, link-local, loopback and the host's addresses except the allow-list (repo-and-deployment §6.1); the guard is the first layer and decides the outcome text |
| Canary | at startup in Production the worker opens a raw socket (bypassing the guard) to `169.254.169.254:80` and to a private address outside the allow-list; a successful connect means the firewall is missing: critical log, health `degraded`, crawling disabled until fixed |
| Event 5101 `rag.egress.blocked` | Warning; at most once a minute per (source, reason) with `Count`. Attributes: `Reason` (`private_address`, `embedded_address`, `scheme`, `port`, `userinfo`, `blocked_host`, `redirect_downgrade`), `AddressClass` (`loopback`, `rfc1918`, `link_local`, `cgnat`, `ula`, `multicast`, `reserved`), `Purpose`, `SourceId`, `RunId`, `Classification`, `HostHash` (HMAC); the host name itself only for `public` sources |

## 8. Renderer (P2, conditional)

### 8.1 When to render

Not in P1. At P1 start a check over the pilot sites (the site profile's `javascriptNeeded`, §5.6, and the crawl
report's short-page note, §5.5) decides whether any pilot site needs JavaScript; umea.se is server-rendered
(simplification pass §13.1), so none is expected. Only if one does is a renderer built, in P2: a source then either
renders or not (a source setting), identity stays the final URL, and the fetch mode is a document attribute. Short
pages are an info note in the crawl report, never `degraded` because rendering is off.

### 8.2 Contract (`IRenderer`, the seam)

```csharp
public interface IRenderer
{
    ValueTask<RenderResult> RenderAsync(RenderRequest request, IRenderFetcher fetcher, CancellationToken ct);
}
public sealed record RenderRequest(Uri Url, FetchedResponse MainDocument, TimeSpan Timeout, RenderWait WaitFor,
    string? Selector, long MaxDomBytes, int MaxSubrequests, long MaxTransferBytes);
public sealed record RenderResult(Uri FinalUrl, string Html, string? Title, IReadOnlyList<Link> Links,
    RenderStats Stats, bool Truncated);
```

The interface is backend-agnostic (answered 14); P1 has no implementation and no `rag-renderer` container.

### 8.3 Egress rule for a P2 renderer

Decided now so the seam fits: the worker **fetches every browser request itself** with the guarded client
(`IRenderFetcher`) and fulfils it (`Page.RouteAsync("**/*")` → `FulfillAsync`), as Open WebUI does
(`owui/retrieval/web/utils.py:850-900`), because a route handler that only checks URLs leaves Chromium free to resolve
again (rebinding) and does not see redirect hops (`onyx/utils/playwright_fetch.py:407`). Chromium then opens no
network connection, the renderer sits only on the internal `ingestion` network, one fresh context per page, no
cookies, downloads aborted, per-host concurrency 1 (answered 2).

### 8.4 Rendered and non-rendered content of one URL

Removed (simplification pass): no `origin`/`rendered` document variants; identity is the final URL and the fetch mode
is a document attribute (§8.1).

### 8.5 Alternatives considered: a self-hosted crawler service

A self-hosted Firecrawl was rejected (plan answered 14, 2026-10-29): the hard parts here (frontier and claims,
per-host budgets, change detection, deletion guard, crawl rules, egress guard, site profiles, run integration) would
still be ours, and it adds a second queue stack and an AGPL-3.0 component. If anti-bot sites ever matter, such a
service can sit behind `IRenderer` (§8.2).

## 9. Uploads

### 9.1 Flow

| Step | Who | What |
|---|---|---|
| 1 | admin API | batch create (`POST /api/sources/{id}/upload-batches` → `batchId`), then **one request per file** (raw body, per-file progress and retry; admin design §4.5); each file is an `add` (new slot) or a `replace {uploadId}`, and a batch may carry `delete {uploadId}` entries; streams each file: per file ≤ `maxFileSizeMb` (default 50, platform cap 50; answered 3), ≤ 100 files and ≤ 500 MB per batch; extension pre-check gives an immediate 415 for refused and deferred types (§9.2); SHA-256 while streaming |
| 2 | admin API | writes each file once to `uploads/{sourceId}/{slot}/{sha256}` (`slot` = the `uploadId`; write-once; `internal`: envelope-encrypted with the data key wrapped by `rag-blob-<level>` in the blob header, data-model §7; `public`: not envelope-encrypted, simplification pass §4); uploads are the only content that cannot be fetched again, so they are **copied to the backup target within 15 minutes** (repo-and-deployment backup); at **commit** (`POST /api/upload-batches/{batchId}/commit`) writes the batch manifest `uploads/{sourceId}/batches/{batchId}.json` (entries, names, declared types, sizes, hashes); inserts one `sync` job, `Trigger = upload`, payload `{sourceId, batchId}`, `DedupKey = sync-upload:{batchId}` (uploads never wait for a running crawl-style sync); uncommitted batches and their blobs are swept after 24 h |
| 3 | worker, `sync` | reads the manifest, upserts the `Uploads` slots, opens the run, enqueues one `fetch` job per added or replaced file (`DedupKey = fetch:{sourceId}:upload:{uploadId}`, 6a §9.2; P1a item 4, PR #78) and applies the batch's deletes |
| 4 | worker, `fetch` (stage `upload`) | ClamAV scan (§11) → sniff → type check → upsert of the source's document `upload:<uploadId>` with the file's content hash → the upload blob is the document's raw content (no copy) → pipeline continues |

`content."Uploads"` (L, written by the worker), one row per **upload slot**: `Id` (the slot, used in the identity),
`SourceId`, `Classification`, `BatchId` (last change), `FileName`, `DeclaredMediaType`, `SniffedMediaType`, `Bytes`,
`Sha256` (current version), `State` (`received`/`clean`/`malware`/`refused`/`indexed`/`deleted`), `Reason`,
`DocumentId`, `UploadedBy`, `UploadedAt`, `ReplacedAt`, `DeletedAt`. The admin reads it for the source's file list.

### 9.2 Types

- Allowed in P1 (simplification pass §1; answered 3 is the earlier subset): PDF, DOCX/PPTX/XLSX,
  `.odt`/`.ods`/`.odp`, JPG/PNG/TIFF, TXT/MD/CSV, HTML.
- **Deferred to P2** (postponed Z1; refused in P1 unless the pilot source inventory taken at P1 start shows them
  common): `.doc`/`.xls`/`.ppt`, `.rtf`, `.eml`/`.msg` → `unsupported` `upload.format_deferred` (crawled:
  `http.format_deferred`). The parser refuses them too (parser §3).
- **Sniffed type wins** (magic bytes; OOXML `[Content_Types].xml` incl. `macroEnabled` content types; OLE root
  CLSIDs, which recognise legacy Office and `.msg` to refuse them; text heuristics). Declared type and extension are
  compared: a mismatch is a fact; a declared allowed type whose sniffed type is not allowed → `unsupported`
  `upload.type_mismatch`.
- Refused (Z1, postponed): zip/7z/rar/tar/gz that are not OOXML/ODF → `upload.archive`; `.docm`/`.xlsm`/`.pptm` →
  `upload.macro_enabled`; executables and unknown → `upload.type_unknown`. The parser refuses them again (parser §3).
- Uploaded HTML is never rendered and its links are never followed (`files` template, 6a §13.1).

### 9.3 Replace and delete

- **Replace**: a `replace` entry writes new bytes into the same slot, so identity and document id stay; the new
  content hash is a new version: the document gets a new execution and its chunk set is swapped (6a §14), and
  evaluation questions, feedback and citation history keep pointing at the same document. The previous version's blob
  is tombstoned and purged after 14 days (the chunk-set rollback window). Identical bytes → `unchanged`.
- **Delete**: the admin adds a delete entry to a batch; the sync soft-deletes the document (`upload.deleted`, exempt
  from the guard: an explicit admin action); `Uploads.DeletedAt` set; the upload blob is tombstoned and purged after
  the 14-day grace, so an accidental delete can be undone within it (admin "restore" adds the entry back and the
  document is revived, §13.4).
- **Duplicate**: an `add` whose bytes equal a live slot of the same source → `skipped` `upload.duplicate`, no new slot.

### 9.4 Identity and names

Identity is the slot, not the hash: two departments uploading the same PDF have two documents, because documents
belong to one source; the second is cheap because parse output and embeddings are cached by content and text hash
(simplification pass §2). The file name is the document's `DisplayName` (used in citations); `Documents.Title` comes
from the parsed document (metadata title or first heading), never from a file name.

## 10. Email attachments as child documents

Removed (simplification pass): email (`.eml`/`.msg`) with attachments as child documents moves to P2 with postponed
item Z1; in P1 email files are refused (§9.2), so no connector creates child documents.

## 11. ClamAV

| Topic | Rule |
|---|---|
| Placement | clamd has no TLS or authentication on TCP 3310, so it stays **host-local** on the `ingestion` network of every host that runs `sync`/`fetch`, never published on the LAN; each instance updates its signatures through its host's `mirror` network ([`../performance-pass.md`](../performance-pass.md) §3.7) |
| Client | own `IMalwareScanner` over TCP 3310 (`ingestion` network): `zINSTREAM\0`, chunks of 64 KiB as `<uint32 BE length><bytes>`, zero-length terminator; reply `stream: OK`, `stream: <signature> FOUND`, or `… ERROR`. clamd closes the connection on an over-long stream while the client is still sending, so the client reads the reply after a failed send. Any `ERROR` reply other than the size limit, and a `zVERSION` reply it cannot parse, mean `clamav.unavailable`: with no engine (a failed reload), clamd 1.4.5 still answers `zPING` with `PONG`, answers every scan with `… ERROR` and `zVERSION` with `ClamAV 1.4.5/4294967295/invalid timestam`, so `zPING` alone is no health signal. Reply formats measured in P0 (PR #26, to-verify #81). ~150 lines, no package |
| What is scanned | every upload and every crawled or listed non-HTML/non-text file (PDF, Office, images) before it is stored as document content or sent to the parser. Fetched HTML, text and CSV, parsed in-process, are not scanned |
| Streaming | the body (encrypted for `internal`; `public` blobs are not envelope-encrypted) is written to a **local** temp file (`/tmp` tmpfs or a scratch volume, ≤ 60 MB) while hashing, streamed to clamd, and if clean stored with one `IBlobStore.PutAsync` to its content-addressed key (write-once: an existing key is not written again): no `tmp/` object and no rename, which S3 lacks ([`../performance-pass.md`](../performance-pass.md) §3.13); no 50 MB buffers in memory; the local file is deleted in `finally`. Uploads are already blobs (`uploads/…`) and are scanned by the worker reading them |
| Limits | `StreamMaxLength 60M`, `MaxFileSize 60M`, `MaxScanSize 200M`, `AlertExceedsMax yes` (repo-and-deployment §5.5, `images/clamav/clamd.conf`); a size-limit reply (`INSTREAM size limit exceeded. ERROR`) and a `Heuristics.Limits.Exceeded.<limit> FOUND` reply → `too-large` `clamav.limit`, never "clean" and never `malware` (as built in P0 #12, PR #26; settled at the integration sync point) |
| Timeouts | connect 5 s, scan 120 s; on timeout transient with attempt, reason `clamav.timeout` (as built, PR #82) |
| Signature freshness | `zVERSION` → `ClamAV <engine>/<db version>/<db date>` (the daily database's build date in C `asctime` form without a time zone, e.g. `ClamAV 1.4.5/28169/Fri Oct 30 06:26:48 2026`; just `ClamAV <engine>` when only a custom database is loaded), read every 10 minutes: > 48 h old → health `degraded`, > 7 days → `unhealthy` and **no new files** (repo-and-deployment §5.5); `rag.clamav.signature_age_hours` gauge. An undated reply (custom database only) → health `degraded`, scans continue (as built in P1a item 6 part 1, PR #82, 2026-10-31; to-verify #81) |
| Down or stale (fail closed) | file jobs requeue without attempt (`clamav.unavailable`, 30 s); after 24 h waiting the item ends `failed` `clamav.unavailable` and the run degrades. HTML continues. Nothing unscanned reaches the parser. As built (PR #143, 2026-10-31, to-verify #81): the 24 h are counted from the job's `EnqueuedAt` once its previous attempt was also refused `clamav.unavailable` (the job row has no first-refusal time), so a job queued for more than 24 h can fail after one 30 s requeue (`retry-failed` recovers it); an upload slot that ends so is set `refused` |
| Malware | outcome `malware`, reason `clamav.<platform>.<category>` (the signature's first two dot parts when it has three or more, else the whole name, lower-case, e.g. `clamav.win.trojan`; as built, PR #82); the content is written to `quarantine/{sourceId}/{sha256}` (data-model §9; self-contained, the wrapped key is in the blob header; an upload blob is copied there and the original tombstoned: blobs are write-once, no move); `content."QuarantinedFiles"` (L: `Id`, `SourceId`, `RunId`, `Classification`, `Sha256`, `Signature`, `Origin` (`upload`/`crawl`), `Name` or `Url`, `DetectedAt`, `PurgeAfter`); event **5102**; run `degraded` (6a §10.3). A crawled page that was clean before keeps its last clean version (not a deletion) |
| Who sees it | department admins: name/URL, signature, time; platform admins also the hash. **No download or release in P1** (§21 Q3); a false positive is resolved by IT security adding an ignore signature and re-uploading. Purged after 30 days (platform plan §8, proposed) |
| Event 5102 `rag.malware.detected` | Warning; attributes `Signature`, `Sha256`, `Origin`, `SourceId`, `RunId`, `DocumentId` (if any), `Classification`; SIEM alert on any |

## 12. Change detection

### 12.1 Two stages

| Stage | Check | Unchanged → |
|---|---|---|
| 1 | conditional GET (§4) or `lastmod` skip (§5.4); SharePoint (P2): delta reports no change | `NotModified`: validators and `FetchedAt` updated, document touched, `EnsureCurrent`, run item `unchanged`; no blob read |
| 2 | `ContentHash` = SHA-256 over the **normalised** content: HTML per §12.2 (rules version in `Documents.ContentHashVersion`), everything else the raw bytes | no new blob, no new execution id; as above |

Stage 1 saves nothing on hosts without validators and without reliable `lastmod`; stage 2 always applies (umea.se:
no validators, so `lastmod` and the hash carry change detection). Only a new hash for a document sets
`CurrentExecutionId` (6a §7.5) and runs the pipeline; the raw blob is written only if its content-addressed key does
not exist yet. The first stage's input hash is this `ContentHash` (6a §8), so the whole engine agrees on what "changed"
means. Onyx gates the same way but only after parsing (`../../platforms/onyx.md:122-123`); Eneo stores the hash and
never compares it (`../../platforms/eneo.md:78-82`).

As built with noise rules v2 (PR #107, 2026-10-31): the HTML hash input is `html-noise-v{N}\n` followed by the
normalised content, because no store keeps the rules version next to a hash and every comparison is hash equality;
a hash taken under another rules version therefore never matches, and "v1 hashes are recomputed on the next fetch"
holds everywhere. Non-HTML content keeps raw-byte hashes but is labelled `ContentHashVersion` 2 too. "Changed" is
the `ContentHash` comparison alone; `ContentHashVersion` is a label the engine never compares (P1a item 9, PR #108).
A `lastmod` sample pair spanning the upgrade counts as changed (conservative).

### 12.2 HTML noise normalisation (rules v1)

On the AngleSharp DOM the fetch stage already builds for links: drop comments; drop `<script>` except
`application/json` and `application/ld+json` (SPA data stays significant); drop `<style>`, `<template>`; drop the
attributes `nonce`, `integrity`, `data-csrf*`; blank `<input type=hidden>` values and `<meta>` named `csrf-token`,
`csrf-param`, `__RequestVerificationToken`, `__VIEWSTATE`, `__EVENTVALIDATION`; strip query strings from `src`/`href`
of `script`, `link`, `img` (cache busters); replace text equal to the fetch day's date (`2026-10-29`, `29 oktober
2026`, `idag`) outside `<main>`/`<article>` with a placeholder; remove elements matching the source's
`noiseSelectors` (e.g. `#cookie-banner`, `.senast-uppdaterad`); collapse whitespace; serialise.

**Finding (P1a item 8, PR #104, 2026-10-31):** Sitevision on umea.se serves per-request values that rules v1 keep: random
`data-cid` UUIDs, `sv_…` element ids and the `aria-labelledby` values that point at them. Under v1 every umea.se fetch
hashes differently, so every full traversal re-parses every page (the extracted output is unchanged and counted as
noise). **Rules v2** (as built, PR #107, 2026-10-31): drop `data-cid`; normalise `id`/`for`/`aria-labelledby`/`aria-describedby`
values matching `^sv_` to a stable placeholder by first occurrence; bump `RulesVersion` (hashes taken with v1 are
recomputed on the next fetch, one extra re-parse; the version is part of the hash input, §12.1). Not in v2: the
anonymous `jsessionid` in a form action and `sv_` values in other attributes (none in the corpus). Rules are deliberately conservative: hiding a real change is worse than one extra re-process. Remaining noise is
measured: when a new hash produces a parsed output whose `OutputHash` equals the previous execution's (6a §8), the
worker counts `rag.connector.change.noise` and lists the page under "Noise" in the crawl report (§5.5) so an admin
can add a selector. A rules-version bump re-processes HTML documents once (embedding cache keeps it cheap;
release-notes item).

### 12.3 Source-setting effect tags

Declared in `contracts/sources/v1/{type}.schema.json` as `"x-effect": "identity" | "scope" | "behaviour" |
"cosmetic"` (Onyx `FieldClass` and scope descriptors, `onyx/connectors/field_policy.py:20-28`, `:40-93`); the shared
library (`Ume.Rag.Pipelines`, 6a §4) computes per-tag hashes of a settings object. The admin shows the impact on
save; the run stores `Runs.SettingsHashes` (`{identity, scope, behaviour}`) and the worker compares the current hashes
with the last completed run's at every sync, so a lost "apply" job cannot leave an edit unapplied. As built in P1a
item 5/9 (PR #141, 2026-10-31): the **scheduler** compares each idle source's current hashes with its newest `sync`
run's and enqueues one `sync` with `Trigger = config-change` (dedup `sync:{sourceId}`) when the scope or identity
hash differs, so a change takes effect within one scheduler tick without an admin "apply" job; behaviour and
cosmetic changes enqueue nothing. A run compares its own hashes with the last `sync` run that applied its settings
(finished; for a crawl, a complete traversal), so a lost or cancelled `config-change` run leaves the effect to the
next sync. The count shown on save is not built (admin).

```json
{ "startUrls": ["https://www.umea.se/"], "sitemaps": [], "extraHosts": [],
  "include": ["www.umea.se/**"], "exclude": ["**/sok?*", "**?*print=*"], "demote": [], "maxDepth": 10, "maxPages": 20000,
  "ignoreQueryParams": [], "directoryIndexNames": [], "noiseSelectors": [],
  "conditionalGet": true, "revalidateEvery": 4, "crawlWindow": "22:00-06:00", "incrementalSchedule": null }
```

| Setting | Source types | Tag | Smallest re-sync |
|---|---|---|---|
| `name`, `description` | all | cosmetic | none |
| `schedule` (the `Sources.Schedule` column), `incrementalSchedule` (crawl only), `crawlWindow` (crawl only in P1) | url-list, crawl, sharepoint | cosmetic | none (the scheduler reads them) |
| `classification` | all | identity (level) | full re-sync into the new level; old-level documents removed at reconcile (exempt); jobs at the old level fail the commit check (6a §15) |
| pipeline parameters | all | behaviour | P1: every document of the collection re-processed via `EnsureCurrent`, no fetching, unchanged stages from the caches; P2 (weakness review §1): re-processing plan from the changed parameters (6a §12) |
| `urls` | url-list | scope | added: claims for the added URLs only; removed: documents soft-deleted at once (`removed-from-list`, exempt). As built in PR #141 (2026-10-31): a `config-change` run marks a listed URL with a live document as listed without a fetch |
| `startUrls`, `sitemaps`, `extraHosts`, `include`, `exclude` | crawl | scope | widening: a traversal now (unchanged pages cost a 304, a `lastmod` skip or a hash compare); narrowing: documents whose stored URL fails the new scope removed without fetching (`scope-narrowed`, exempt, count shown on save). As built in PR #141 (2026-10-31): a narrowing also starts a traversal and the removals happen at its reconcile, not on save; the check runs at every crawl reconcile (idempotent, also covers site-profile `excludedTypes`), and no request goes to an out-of-scope URL, removal checks included |
| `demote` (weakness review §3) | crawl | cosmetic | none: ranking only, read by retrieval from the config snapshot (knowledge design §6.5) |
| `maxDepth`, `maxPages` | crawl | scope | raise: traversal now; lower: next traversal, never deletes (a limit makes it partial). As built in PR #141 (2026-10-31): lowering also starts a traversal (the hashes cannot tell raising from lowering), still never deleting; a lowered `maxDepth` skips the deeper documents instead of marking the traversal partial |
| `ignoreQueryParams`, `directoryIndexNames` | url-list, crawl | identity | full re-sync: every URL re-canonicalised and fetched; old identities not re-found removed at reconcile (exempt, count shown on save). As built in PR #141 (2026-10-31): a crawl's removals are `identity-changed`, and an identity no longer canonical under the current rules counts as changed too (covering site-profile `identityRule`/`ignoreQueryParams`); a URL list's old identities are removed as `removed-from-list` (exempt, same effect) |
| `noiseSelectors` | url-list, crawl | behaviour | next sync; pages whose hash changes re-process once |
| `conditionalGet`, `revalidateEvery` (1–100, default 4), `timeoutSeconds` (5–120, default 30) | url-list, crawl | behaviour | next sync |
| `maxFileSizeMb` | upload | behaviour | new uploads only |
| site profile version (host, §5.6) | url-list, crawl on the host | behaviour; `identityRule`/`ignoreQueryParams`: identity; `excludedTypes`: scope | extraction fields: the host's HTML documents re-processed from stored raw content; identity fields: identity re-sync of the host's sources (exempt removals); `excludedTypes`: as scope. As built in PR #141 (2026-10-31): for crawl sources a `sync` replaces the reprocess when `excludedTypes` or identity changes (§5.6) |
| `sites`, `drives`, `folders`, `includeSitePages`, `excludedPaths` (P2) | sharepoint | scope | widening: enumerate the new scope; narrowing: from stored paths, exempt |
| `certificate`, `tenantId` (P2) | sharepoint | behaviour | preflight, then next sync |

A source never moves between collections (a new source is created), so `collectionId` is not editable.

As built in P1a item 4 (PR #78) and P1b item 2 (PR #88), 2026-10-31: the schemas in `contracts/sources/v1/` hold
the settings above per type, each with its `x-effect`.
- `url-list`: `urls`, `ignoreQueryParams`, `directoryIndexNames`, `noiseSelectors`, `conditionalGet`,
  `revalidateEvery`, `timeoutSeconds`.
- `crawl`: the URL-list behaviour and identity settings, plus `startUrls`, `sitemaps`, `extraHosts`, `include`,
  `exclude`, `demote`, `maxDepth` (0–50, default 10), `maxPages` (1–1,000,000, default 10,000), `crawlWindow`
  (default `22:00-06:00`) and `incrementalSchedule` (default `null`).
- `upload`: `maxFileSizeMb` (1–50, default 50).

`name`, `classification` and `schedule` are `Sources` columns, not settings (`Sources` has no description column). `incrementalSchedule` was
left out of P1a item 4 until the `lastmod` sample, and added for crawl only in P1b item 2 (§5.4). `timeoutSeconds` is
validated but not applied yet: the fetcher uses the fixed §4 timeouts (PR #82).

### 12.4 Smallest re-sync per tag

| Tag | Run | Fetches | Deletions |
|---|---|---|---|
| cosmetic | none | – | – |
| behaviour | none (next scheduled) | per normal rules | normal guard |
| scope | immediate `sync`, `Trigger = config-change` (as built, PR #141: enqueued by the scheduler within one tick, §12.3) | only newly in-scope items (crawl: traversal with conditional GET or `lastmod` skip) | narrowing removals exempt (admin confirmed the shown count; the count on save is not built) |
| identity | immediate full `sync`, `force` (as built, PR #141: the same `config-change` `sync`, which finds the identity change itself and runs a full revalidating traversal) | everything | identity removals exempt; unexpected losses still go through the guard |

> **Decided in 6c** (`design-processing.md` §12): a noise-only change (new `ContentHash`, same parsed output)
> creates no new chunk set; the set is re-keyed and the run item is `unchanged`, reason `change.noise`.

## 13. Deletion guard

### 13.1 Candidates

Collected during the run in `content."DeletionProposals"` (L: `RunId`, `DocumentId`, `SourceId`, `Classification`,
`Reason`, `Exempt bool`, `Decision`, `DecidedBy`, `DecidedAt`; PK (`RunId`, `DocumentId`)):

| Reason | Source | Exempt |
|---|---|---|
| `not-seen` | complete traversal or id listing without the document | no |
| `http.404`, `http.410`, `http.moved_out_of_scope` | fetch | no |
| `robots.disallow` | robots.txt now disallows a known URL | no (a staging robots.txt deployed to production must not empty the index) |
| `persistent-failure` | 401/403/5xx/TLS for ≥ 4 consecutive runs **and** ≥ 14 days since `LastSeenAt` (`Documents.ConsecutiveFailures`) | no |
| `sharepoint.deleted`, `sharepoint.moved_out_of_scope` (P2) | delta | no |
| `removed-from-list`, `scope-narrowed`, `identity-changed`, `level-changed`, `upload.deleted` | configuration or explicit admin action | yes |

### 13.2 The guard

Runs in the `reconcile` phase, after every `fetch` job of the run is terminal (the finalizer enqueues it, 6a §10.4).
Let `n` = non-exempt candidates, `N` = live documents of the source (held documents excluded):

- **ratio**: `n ≥ DeletionGuardMinCount` (5) and `n / N > 0.20` (plan §8, `Settings`);
- **absolute**: `n > DeletionGuardMaxCount` (500): 20 % of a 50,000-document source is too many to delete unasked;
- **empty source**: the listing or traversal completed but yielded no indexable item while `N > 0` (Onyx deleted
  189,833 documents after an empty listing and recorded success, `../../platforms/onyx.md:264`,
  `onyx/background/celery/tasks/pruning/tasks.py:816`);
- **404 storm**: covered twice: the circuit breaker (§6.4) makes the traversal partial (so `not-seen` cannot fire),
  and explicit 404s still count in `n`.

Not tripped → exempt and non-exempt candidates are soft-deleted through the index writer (6a §14) in batches of
1,000, run items `deleted`. Tripped → exempt ones are applied, the rest wait; `Runs.ProposedDeletions = n`; the run
finishes all other work and the finalizer sets **`awaiting-deletion-confirmation`**.

### 13.3 Confirm, reject, supersede

| Event | Effect |
|---|---|
| `command` `confirm-deletion {runId}` (`Manage` policy of the source's department: `rag-department-admin` or `rag-platform-admin`, admin design V28) | re-validates each proposal (a document seen again by a later run is dropped), soft-deletes the rest, run items `deleted`; the finalizer recomputes the run state (as built in P1b item 8, PR #115, 2026-10-31: the command sets the run back to `running`, phase `done`); audit 6001 |
| `command` `reject-deletion {runId}` | nothing deleted; the documents get `Documents.HeldAt/HeldBy`, excluded from later guards and listed as "kept by decision" on the source page until they are seen again (hold cleared) or an admin releases the hold (`command` `release-hold {sourceId}`, admin design §4.7: holds cleared, documents become candidates at the next reconcile); run → `degraded` `deletion.rejected` (as built, PR #115: no reason is stored, since `Runs` has no reason column) |
| a newer run of the source reaches reconcile while a proposal waits | the newer run computes afresh; the older run → `degraded` `deletion.superseded`; one waiting proposal per source at a time |
| no decision | waits indefinitely, never auto-confirmed; shown on the Overview page (admin plan §4) and as a daily reminder metric `rag.deletion.awaiting` |

### 13.4 Grace and store removal

A soft delete sets `Documents.DeletedAt` and `PurgeAfter = +14 days` and sets `DeletedAt` on the chunks of its live
set in the same transaction (6a §14), so they leave retrieval at once. A document seen again within the grace is
**revived** (`DeletedAt = null` on the document and its live set's chunks, no re-processing; chunk sets are kept 14
days, data-model §2.2). After `PurgeAfter` the worker's content retention purges the document with its chunk sets,
outputs and own blobs; content-addressed raw and parse blobs are tombstoned when no live document of the level references their
hash any more, and purged after the delay (write-once blobs, simplification pass §4). As built in P1a item 9 (PR
#108, 2026-10-31): a document seen again after the grace but before the purge keeps its row (the identity index is
unique) and is processed as new content.

### 13.5 What the admin sees

The run page in state `awaiting-deletion-confirmation`: `n`, `N` and the ratio, the rule that tripped, a breakdown by
reason, traversal facts (pages reached this run vs. the previous run, paused hosts, seed failures), 50 example URLs
or names with `LastSeenAt` and last status, a CSV of all proposals, and **Confirm** / **Reject** buttons that insert
the `command` jobs. The source page shows held documents and the exempt removals of config changes.

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
| 410 `resyncRequired` | full enumeration from a fresh delta; documents not seen → `not-seen` candidates **through the guard** (Onyx's 410 restart, `drive_delta.py:275-312`; Eneo `clients/sharepoint_content_client.py:563`, `:637`) |
| Throttling | 429/503 `Retry-After` honoured through a pseudo-host budget `graph:{tenantId}` in `CrawlHosts` (Eneo #427) |
| Subscriptions | one per drive (`changeType = updated`), renewed at half their lifetime and at least 48 h before expiry (Eneo renews every 12 h, `eneo/integration/infrastructure/sharepoint_subscription_worker.py:125`); random `clientState` per subscription **always** checked (Eneo checks only if configured, `sharepoint_webhook_service.py:84-85`); endpoint on the admin API behind the reverse proxy, validation handshake, notifications enqueue a `sync` (`Trigger = notification`, dedup per source); lifecycle notifications (`missed`, `reauthorizationRequired`) → delta run. Notifications are **hints**: delta also runs every 6 h |
| Hierarchy | `IHierarchyConnector`: site → drive → folder for scope picking (admin) |
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
| 200 new / changed / same hash; 304; `lastmod` skip | `added` / `updated` / `unchanged` | – | – |
| robots, `noindex` (external), trap, duplicate upload, broken discovered link | `skipped` | `robots.disallow`, `robots.noindex`, `trap.calendar`, `upload.duplicate`, `http.404` (never indexed, found by a link) | – |
| applied deletion | `deleted` | `http.410`, `not-seen`, `upload.deleted` | – |
| refused or deferred type | `unsupported` | `http.content_type`, `upload.archive`, `upload.macro_enabled`, `upload.format_deferred`, `http.format_deferred` | – |
| size cap | `too-large` | `http.body_limit`, `clamav.limit` | – |
| ClamAV finding | `malware` | `clamav.<platform>.<category>` (§11) | – |
| egress guard, robots on a redirect target, downgrade | `blocked` | `egress.private_address`, `redirect.downgrade` | – |
| listed URL 404, 401/403, TLS, 5xx after retries, ClamAV down 24 h | `failed` | `http.404`, `http.403`, `tls.invalid_certificate`, `http.5xx`, `clamav.unavailable` | 5 for transient |
| 429/503, host slot not yet due, ClamAV down | requeue | `rate_limited`, `politeness`, `clamav.unavailable` | no |

Run-level: seeds unreachable → run `failed` `crawl.seeds_unreachable` (no deletions); page limit → `degraded`
`crawl.limit_pages`; host paused → `degraded` `crawl.host_paused`; malware → `degraded` (6a §10.3). As built in P1a
item 9 part 2 (PR #122, 2026-10-31): the finalizer applies this traversal verdict from `Runs.Report.verdict`.

## 16. Observability

New metrics (meter `Ume.Rag`; dimensions low-cardinality; **never** host names, URLs, file names or titles):

| Metric | Type | Dimensions |
|---|---|---|
| `rag.connector.fetch.duration` (s), `rag.connector.fetch.bytes` | histogram | source type, status class (`2xx`, `304`, `3xx`, `4xx`, `5xx`, `error`), host kind |
| `rag.connector.items` | counter | source type, result (`new`, `changed`, `not_modified`, `lastmod_skip`, `same_hash`, `gone`, `skipped`) |
| `rag.connector.change.noise` | counter | host kind |
| `rag.crawl.frontier.pending`, `rag.crawl.released` | gauge, counter | host kind |
| `rag.crawl.politeness.wait` (s), `rag.crawl.backoff`, `rag.crawl.host_paused` | histogram, counter, counter | host kind; reason (`429`, `503`, `breaker`) |
| `rag.crawl.traps` | counter | kind |
| `rag.crawl.incomplete_runs` | gauge (max consecutive incomplete traversals over sources) | – |
| `rag.site_profile.applied`, `rag.site_profile.proposed` | counter | origin (`learned`, `admin`, `rollback`) |
| `rag.egress.blocked` | counter | reason, purpose |
| `rag.clamav.scan.duration` (s), `rag.clamav.detections` (existing), `rag.clamav.signature_age_hours` | histogram, counter, gauge | origin |
| `rag.deletion.proposed`, `.applied`, `.guard_tripped`, `.awaiting` | counter, counter, counter, gauge | reason / rule |
| `rag.sharepoint.subscription.expires_in` (P2) | gauge (min over sources) | – |

- **Traces**: spans `rag.connector.list`, `rag.connector.fetch`, `rag.clamav.scan`, `rag.site_profile.learn` under the
  job span (6a §17); HTTP client spans have `url.full` and `server.address` **removed** for every source (a filter in
  the worker's OTel setup; repo-and-deployment §9 already strips query strings). Attributes: source type, host kind,
  status class, outcome.
- **Logs**: URLs only at Debug and only for `public` sources; otherwise `UrlHash`.
- **Events**: 5101 (§7), 5102 (§11). SIEM alerts to add: any 5102; the canary failure (§7).
- **Health**: ClamAV (a parseable `zVERSION`, signature age; `PONG` alone is not enough, §11), egress canary, releaser lag (P2; oldest due `pending` entry), feeding the
  *Ingestion* dashboard; alert `rag.crawl.incomplete_runs` ≥ 3 (§5.4).

## 17. Performance (R1: 10,000 pages/hour)

| Connector cost per typical page | Estimate (*to measure*) | At 2.8 pages/s |
|---|---|---|
| One GET (municipal host; conditional where validators exist) | 1 politeness slot, ~200 ms latency | 2.8 req/s of 10 allowed; `fetch` concurrency 8 covers latency |
| AngleSharp parse for links + noise hash | ≈ 10–15 ms CPU | ≈ 0.04 cores (on top of 6a §18's 80 ms per page) |
| Frontier statements: claims upsert (1 per page, ~100 links), slot update, document upsert, run items batch | ≈ 4 statements | ≈ 11 statements/s |
| Releaser (P2) | 1 query/s | – |
| ClamAV | files only (PDF ≈ 50–200 ms) | negligible for HTML-heavy sites |

An unchanged re-crawl costs per page at most one request plus one parse for links, and no blob write, parse or
embedding; a `lastmod` skip costs no request, which matters on hosts without validators (umea.se, ≈ 7,960 PDFs). One
external host is capped at 3,600 pages/hour by politeness (answered 4), so the R1 test uses a municipal test host.

## 18. Tests

| Level | What |
|---|---|
| Unit (`Ume.Rag.Worker.Tests`) | canonicaliser: every §3.1 rule with RFC 3986 normalisation vectors and IDN/punycode cases, incl. the Sitevision identity rule; tracking list; glob/regex patterns (Eneo's unescaped-regex case); robots parser against the RFC 9309 examples and Google's robotstxt test cases (*to verify* licence); sitemap parser (index, gzip, XXE payload refused, oversize, `lastmod`); noise normaliser (CSRF tokens, nonces, cache busters, dates: equal hashes; a changed paragraph: different hash); trap detectors; guard arithmetic incl. exempt reasons and min/max counts; effect-tag hashes per §12.3 row; site-profile derivation on synthetic samples (root, ≥ 60 % repetition, breadcrumb, date pattern, guardrail check); ClamAV client against recorded replies (`OK`, `FOUND`, size limit, garbage); outcome mapping table §15 |
| SSRF fixtures | guard with a fake resolver: every blocked range, embedded IPv4 in IPv6 (mapped, NAT64, 6to4, Teredo), mixed public/private answers (refused), DNS answer changing between calls (pinning: connect goes to the checked address), decimal/octal/hex IP literals, IDN homographs, userinfo, ports, redirect to `169.254.169.254`, https → http, 6 hops |
| Integration (Testcontainers + an in-process Kestrel test site) | crawl of a generated site: robots allow/disallow/5xx/404, `Crawl-delay`, sitemap index, redirects (loops, chains, off-site 302 sign-in wall, 301 out of scope), calendar and faceted traps, session ids, `noindex`/`nofollow` on a "municipal" vs "external" host name, 429 with `Retry-After`, slow responses, gzip bomb, oversize body, wrong `Content-Type`, `Set-Cookie` never echoed; a server without validators and with `lastmod` (the umea.se shape: `lastmod` skip, `revalidateEvery` full pass; with `lastmodReliable` false every URL is fetched and no incremental run is scheduled, weakness review §3); a URL dropped from the sitemap gets the removal check (`HEAD`; `GET` after a 405; 404/410 → `Gone`; 200 → kept); aliases served with 200 and a canonical (folded only when hashes match; a canonical to the start page not folded); images and video not admitted; two sources on one host (one request per page, two documents, rate never above budget); preview fetch with the releaser running; URL list ETag/Last-Modified/304 and a server with changing ETags |
| umea.se corpus (§5.6) | ≈ 20 saved umea.se pages (HTML and PDF) served by the test site: the hand-checked Sitevision preset applied, the example page's main text, breadcrumb and date exact (acceptance), identity rule verified on node-id URLs; P2: profile learned from the corpus, and a "redesigned" variant triggers re-learning and the guardrail turns a lossy profile into a proposal |
| Deletion guard (integration) | 25 % removed → pause; confirm, reject (hold), supersede; empty listing (S4); 404 storm with the breaker; page-limit run deletes nothing (**C7**, Eneo partial crawl); one failing seed of 200 still completes the traversal and its document is not a candidate, 3 of 200 (< 99 % resolved) make it partial (weakness review §3); revival within 14 days without re-processing; three incomplete traversals raise the alert |
| ClamAV (integration, real image) | EICAR upload → `malware`, quarantine row and blob, 5102, run degraded; clamd stopped → files requeue, HTML continues, after 24 h `failed`; stale signatures (fake `VERSION` reply) → no new files |
| Uploads | sniff vs declared (PDF named `.docx`, EXE named `.pdf`, `.docm`, zip, OOXML zip accepted; `.doc`, `.rtf`, `.msg` → `upload.format_deferred`); same bytes in two departments → two documents, second parse a cache hit, title not from the name; replace keeps the document id and swaps the chunk set; delete and restore; duplicate add skipped; upload blob present on the backup target within 15 minutes. As built in P1a items 5/6 (PR #139, 2026-10-31): the engine-level upload tests use the development `EicarOnlyScanner`; the real ClamAV image is tested at the scanner level |
| Crash/resume | kill the worker during listing (resume from checkpoint, no duplicate claims or run items), during a fetch after the blob write and before commit (orphan swept, retry idempotent), during reconcile (guard decision computed once); leader loss of the releaser (another instance takes over, no double release beyond dedup) |
| Telemetry | captured spans, metrics and logs of a `internal`-source crawl contain no URL, host or file name |
| SharePoint (P2) | S1–S10 against a Graph fake (WireMock) plus one run against a test tenant |
| Performance | `perf.yml` (6a §18): 10,000-page generated municipal site; unchanged re-crawl does no parse/embed |

## 19. Milestones

| Phase | Deliverable |
|---|---|
| P1 | (items moved by weakness review §1 in §24) connector interfaces and runner with checkpoints; canonicaliser incl. verified identity rules and canonical folding; URL list; crawl with robots, sitemaps (`lastmod`), scope, `demote` patterns, traps, traversal with `lastmod` skip, incremental runs, completeness by ratio and the removal check (weakness review §3), report, incomplete-traversal alert; the 500-URL `lastmod` sample for umea.se (full traversals until it passes, weakness review §3); site profiles (manual profile JSON, the hand-checked Sitevision preset for umea.se, edit and rollback) with the umea.se acceptance test; one frontier per source (§6.0), host budgets (in-process connection cap), back-off, breaker, night window; egress guard + canary; uploads (slots, batches, sniffing, replace/delete, backup copy); ClamAV + quarantine; change detection with noise rules v1; effect tags; deletion guard with reconcile phase and commands; preflight for URL list and crawl; P1-start check whether pilot sites need JavaScript; tests of §18 |
| P2 | site-profile learning, guardrails, proposals and re-learning (§5.6); shared frontier with claims and releaser (§6.1–§6.3); SharePoint (§14, S1–S10) with `IHierarchyConnector`; Redis per-host in-flight leases (first multi-host deployment); renderer behind `IRenderer` only if the P1-start check found a pilot site that needs JavaScript (§8); email with attachments as child documents and legacy Office/RTF (Z1); presets for WordPress and Episerver/Optimizely; PDF link extraction for crawls; quarantine release workflow if needed |
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
| `ingestion-worker/plan.md` §3, §8 | final-URL identity; deletion guard min/max counts and exempt reasons. **(SP)** identity per source and upload slot; verified canonical folding and identity rules; site profiles; renderer P2 conditional; upload types per §9.2 |
| `platform/repo-and-deployment.md` | ClamAV freshness via `zVERSION`; `UME_RAG_CRAWL_ALLOW` feeds `Crawl__AllowedPrivateRanges`; egress canary; metrics of §16; trace filter removing `url.full`/`server.address`. **(SP)** no `rag-renderer` in P1; Redis crawl lease P2; upload backup copy within 15 minutes |
| `identity/design.md` §10 | attributes of 5101 (§7) and 5102 (§11); SIEM alert on any 5102. **(SP)** the 5101 `Purpose = Render` alert moves to P2 |
| `platform/plan.md` §8 | quarantine 30 days (confirm); upload blobs purged 14 days after deletion or replacement |
| `admin/plan.md` §4 | preflight results, impact count before scope/identity changes, crawl report (§5.5), upload list, quarantine list, deletion confirmation (§13.5), held documents; municipal hosts, host overrides, blocked hosts. **(SP)** no crawl hierarchy tree or two-variant warning; site profile preview in the source form, profile versions, proposals and rollback; upload replace; incomplete-traversal alert |
| `knowledge-api` | upload citations use the document's `DisplayName` **(SP: no "Bilaga … i …" in P1)**; consider de-duplicating chunks by `TextHash` within a collection (§3.1) |
| `postponed-decisions.md` | the crawler about page (answered 10) needs an owner and URL; SharePoint ACL mirroring if §21 Q2 is answered "later". **(SP)** Z1 joined by legacy Office, RTF and email with child documents |
| `01-general-idea.md` §10 | "Connectors (step 6b)" row: final-URL identity, canonical trusted only when verified, claims + releaser, site profiles, guard with reconcile phase |

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

## 22. To verify

- ~~`System.Uri` escaping and IDN behaviour vs our rules (`IdnHost`, `UriKind`, `%2F` handling); `IdnMapping`
  UTS 46 flags in .NET 10.~~ Done (PR #82, to-verify #27): non-transitional UTS 46 with STD3; the canonicaliser does
  not rely on `System.Uri`.
- ~~`SocketsHttpHandler.ConnectCallback` with HTTP/2 and TLS SNI to the URL host; decompression counting with
  `AutomaticDecompression`; Brotli support.~~ Done (PR #82, to-verify #28).
- Docker IPv6 on the `crawl` network and the firewall rules for IPv6.
- ~~clamd `zVERSION` output format and date parsing on ClamAV 1.4; `INSTREAM` reply on `StreamMaxLength`.~~ Done
  (P0 PR #26, P1a PR #82, to-verify #81).
- Graph: maximum subscription lifetime for `driveItem` resources; a call that lists items with unique permissions
  under `Sites.Selected`; whether delta repeats children after a folder move.
- ~~Licence of Google's robotstxt test corpus for reuse as fixtures.~~ Apache-2.0 (PR #82, to-verify #112): its
  cases may be ported with the licence text and attribution; porting stays P2.
- Planning numbers of §17 (link-extraction CPU, frontier statements per page) in the R1 test.
- The about-page URL for the user agent (answered 10) and that umea.se's robots.txt does not block the new agent.
- ~~umea.se: that the id-only Sitevision URL returns the same normalised content hash as the full path on a sample
  (identity rule), and the breadcrumb path of the example page.~~ Done (P1a item 8, PR #104, 2026-10-31; to-verify
  #138, #158): the id-only URL gives the same output hash (`OutputHash`) on three pages, while the raw content hashes
  differ (§5.6); the breadcrumb is as on the live page.
- umea.se `lastmod` (weakness review §3, `to-verify.md` #170): 500 random sitemap URLs fetched, their noise-normalised
  content hashes compared with `lastmod` over several weeks; `lastmodReliable` is set by hand only from the recorded
  result, and the umea.se source runs full traversals until then. The pass threshold is set when the result is
  recorded.
- The Sitevision fingerprint and the preset's session parameters (`sv.*` query parameters seen in umea.se's
  robots.txt) on two or three other Sitevision municipal sites.
- P2: site-profile re-learn thresholds (empty-root rate, boilerplate share, text yield) on the umea.se corpus.
- At P1 start: whether any pilot site needs JavaScript (decides the P2 renderer), and the pilot source inventory's
  share of legacy Office, RTF and email files (decides whether they stay deferred).
- P2 renderer only: Playwright .NET `RouteAsync("**/*")` + `FulfillAsync` for every resource type (module scripts,
  CORS preflights), fulfilled redirects, `ServiceWorkers = Block`, and requests that bypass routing (WebSockets, DNS
  prefetch).

## 23. Deferred or removed (simplification pass)

Per [`../simplification-pass.md`](../simplification-pass.md):

| Item | Where it went |
|---|---|
| Renderer (`rag-renderer`, Playwright, render leases `CrawlHosts.RenderLease*`, JavaScript-only score, `render` source setting, `headless-render` fallback node, render metrics and health, `EgressPurpose.Render`) | P2, only if the P1-start check finds a pilot site that needs JavaScript (§1, §13.1); `IRenderer` seam and its egress rule kept (§8) |
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

Per [weakness review §1](../weakness-review.md#1-p1-builds-a-platform-before-anyone-sees-one-good-answer):

| Item | Where it went |
|---|---|
| Site-profile learning (`preview` kind `site-profile`, sampling, field derivation), automatic application with guardrails, `proposed` versions with approve/reject, re-learning and its thresholds, `NextLearnAfter` | P2; P1 uses manual profiles of the same schema (umea.se: a hand-checked Sitevision preset) (§5.6) |
| Crawl frontier shared between sources: `CrawlClaims`, one request serving every claim, freshness-window reuse across sources, `fetch.shared`, `FrontierReleaser`, nav-link claim cache | P2; P1 has one frontier per source with the shared host budget (§6.0) |
| Re-processing plan from changed pipeline parameters | P2; P1 re-processes every document of the collection (§12.3) |
