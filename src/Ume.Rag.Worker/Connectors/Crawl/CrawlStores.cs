namespace Ume.Rag.Worker.Connectors.Crawl;

/// <summary><c>CrawlFrontier.State</c> (connectors design §6.1): <c>pending</c> admitted and waiting, <c>released</c> a <c>fetch</c> job holds it, <c>idle</c> done.</summary>
public enum FrontierState
{
    Idle,
    Pending,
    Released,
}

/// <summary>One crawl run of one source: the key of its frontier rows (§6.0) and its <c>CrawlRuns</c> row.</summary>
public sealed record CrawlRunKey(Guid SourceId, string Classification, Guid RunId);

/// <summary>
/// A <c>content."CrawlFrontier"</c> row (data-model §2.2, P1 per source: PK (<c>Classification</c>, <c>SourceId</c>,
/// <c>UrlHash</c>) with the source's <c>RunId</c>, <c>Depth</c> and <c>Leaf</c>, connectors design §6.0). Hashes are
/// lower-case hex here; the Npgsql store maps them to the <c>bytea</c> columns.
/// </summary>
public sealed record FrontierRow
{
    public required string Classification { get; init; }

    public required Guid SourceId { get; init; }

    /// <summary>SHA-256 (hex) of <see cref="Url"/>.</summary>
    public required string UrlHash { get; init; }

    /// <summary>The canonical URL (§3.1).</summary>
    public required string Url { get; init; }

    /// <summary>
    /// The URL as found: canonical but for the site profile's identity rule (<see cref="Urls.UrlCanonicalizer.PathForm"/>,
    /// §3.1 rule 10); null when it equals <see cref="Url"/> or is not known (a row from before #228). The fetch requests it.
    /// </summary>
    public string? FetchUrl { get; init; }

    /// <summary>The URL a fetch requests: <see cref="FetchUrl"/>, else <see cref="Url"/>.</summary>
    public string RequestUrl => FetchUrl ?? Url;

    /// <summary>The URL's authority: host, with <c>:port</c> when not the default (the <c>CrawlHosts</c> key).</summary>
    public required string Host { get; init; }

    public FrontierState State { get; init; } = FrontierState.Pending;

    /// <summary>The run that last admitted the row; a row of this run is not admitted again (the page counter stays exact).</summary>
    public Guid? RunId { get; init; }

    public short Depth { get; init; }

    /// <summary>Off-site: fetched, its links never followed (§5.2).</summary>
    public bool Leaf { get; init; }

    public DateTimeOffset? LastFetchedAt { get; init; }

    public short? LastStatus { get; init; }

    public string? RedirectTo { get; init; }

    public string? ETag { get; init; }

    public string? LastModified { get; init; }

    /// <summary>The last response's content hash (hex): its outlinks blob serves a 304 (§5.4), and canonical folding compares it (§3.3).</summary>
    public string? ContentHash { get; init; }

    public DateTimeOffset? SitemapLastMod { get; init; }

    /// <summary>
    /// A removal check (§5.4): a URL that left the sitemap, admitted at depth 0 for one <c>HEAD</c> (then <c>GET</c> if
    /// refused) and not counted as a seed. Its links, when a <c>GET</c> follows, are admitted at depth 1.
    /// </summary>
    public bool RemovalCheck { get; init; }

    /// <summary>The depth that marked a removal check before <c>CrawlFrontier.RemovalCheck</c> (#102); such a row can outlive a deploy.</summary>
    public const short LegacyRemovalCheckDepth = -1;

    public bool IsRemovalCheck => RemovalCheck || Depth == LegacyRemovalCheckDepth;
}

/// <summary>
/// A URL to admit: canonical, in scope, below the depth limit, not a trap; <c>RemovalCheck</c> admits it for the removal
/// check (<see cref="FrontierRow.RemovalCheck"/>). <c>FetchUrl</c> is the URL's path form as found
/// (<see cref="FrontierRow.FetchUrl"/>), null when not known.
/// </summary>
public sealed record FrontierAdmission(string Url, string Host, short Depth, bool Leaf, DateTimeOffset? SitemapLastMod = null, bool RemovalCheck = false,
    string? FetchUrl = null)
{
    /// <summary>The length of <c>CrawlFrontier.FetchUrl</c> (data-model §2.2).</summary>
    public const int MaxFetchUrlLength = 2048;

    public string UrlHash => Convert.ToHexStringLower(Urls.UrlCanonicalizer.IdentityHash(Url));

    /// <summary>
    /// <c>CrawlFrontier.FetchUrl</c> as written: <see cref="FetchUrl"/>, null when it equals <see cref="Url"/> or is longer
    /// than the column (the row then fetches its identity, as before #228).
    /// </summary>
    public string? StoredFetchUrl => FetchUrl is null || FetchUrl.Length > MaxFetchUrlLength || string.Equals(FetchUrl, Url, StringComparison.Ordinal) ? null : FetchUrl;

    /// <summary>One admission per identity, in the order of first appearance: the first with a stored path form, else the first.</summary>
    public static IReadOnlyList<FrontierAdmission> OnePerIdentity(IEnumerable<FrontierAdmission> urls)
    {
        ArgumentNullException.ThrowIfNull(urls);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var distinct = new List<FrontierAdmission>();
        foreach (var url in urls)
        {
            if (!index.TryGetValue(url.UrlHash, out var at))
            {
                index[url.UrlHash] = distinct.Count;
                distinct.Add(url);
            }
            else if (distinct[at].StoredFetchUrl is null && url.StoredFetchUrl is not null)
            {
                distinct[at] = url;
            }
        }

        return distinct;
    }
}

/// <param name="Admitted">The rows new to the run, now <c>pending</c>.</param>
/// <param name="LimitHit">A new URL was refused because the run reached <c>maxPages</c>.</param>
public sealed record AdmitResult(IReadOnlyList<FrontierRow> Admitted, bool LimitHit);

/// <summary>The facts of a response, written to the frontier row with <c>State = idle</c>.</summary>
public sealed record FrontierFacts(DateTimeOffset FetchedAt, short? LastStatus, string? ETag, string? LastModified, string? ContentHash, string? RedirectTo);

/// <summary>A host's open rows of a run: waiting and held by a <c>fetch</c> job.</summary>
public sealed record HostLoad(string Host, int Pending, int Released);

/// <summary>
/// The P1 frontier of one source (connectors design §6.0): admission is one upsert per URL, <c>sync</c> releases
/// <c>pending</c> rows per host in batches as <c>fetch</c> jobs. Every method is one statement or transaction.
/// </summary>
public interface ICrawlFrontierStore
{
    /// <summary>
    /// Upserts the URLs for the run: a row not yet of this run becomes <c>pending</c> with the run, depth, leaf and
    /// removal-check flag (and the sitemap <c>lastmod</c> and <c>FetchUrl</c> when given: the latest path form found wins,
    /// an admission without one keeps the stored one); a row already of this run is left as it is. Of several URLs with one
    /// identity in a call, the first with a path form is admitted (else the first). New rows
    /// are admitted in order while <c>CrawlRuns.Admitted</c> is below <c>MaxPages</c>, which counts them (depth 0 also
    /// counts <c>Seeds</c>, a removal check does not); the first refused one sets <c>LimitHit</c>.
    /// </summary>
    Task<AdmitResult> AdmitAsync(CrawlRunKey run, IReadOnlyList<FrontierAdmission> urls, CancellationToken cancellationToken);

    Task<FrontierRow?> GetAsync(Guid sourceId, string classification, string urlHash, CancellationToken cancellationToken);

    /// <summary>
    /// An incremental run's listing (§5.4): the seeds that need no fetch are marked listed for the run (<c>RunId</c> set,
    /// <c>SitemapLastMod</c> updated when given, nothing admitted or counted) and their hashes returned. A seed needs no
    /// fetch when its row is <c>idle</c>, was fetched, and its sitemap <c>lastmod</c> is absent or not newer than
    /// <c>LastFetchedAt</c>; every other seed is left for <see cref="AdmitAsync"/>. A listed seed's path form refreshes
    /// <c>FetchUrl</c> as in <see cref="AdmitAsync"/>.
    /// </summary>
    Task<IReadOnlySet<string>> MarkListedAsync(CrawlRunKey run, IReadOnlyList<FrontierAdmission> seeds, CancellationToken cancellationToken);

    /// <summary>
    /// After a complete listing: the source's <c>idle</c> rows not listed in this run whose <c>SitemapLastMod</c> is set,
    /// i.e. the URLs that left the sitemap since they were listed (the removal check, §5.4).
    /// </summary>
    Task<IReadOnlyList<FrontierRow>> LeftSitemapAsync(CrawlRunKey run, CancellationToken cancellationToken);

    /// <summary>Clears <c>SitemapLastMod</c> of the rows: they left the sitemap and their removal check is admitted.</summary>
    Task ClearSitemapLastModAsync(CrawlRunKey run, IReadOnlyList<string> urlHashes, CancellationToken cancellationToken);

    /// <summary>Hosts with <c>pending</c> or <c>released</c> rows of the run.</summary>
    Task<IReadOnlyList<HostLoad>> HostLoadAsync(CrawlRunKey run, CancellationToken cancellationToken);

    /// <summary>Marks up to <paramref name="max"/> <c>pending</c> rows of the host <c>released</c>, removal checks first, then lowest depth (BFS), and returns them.</summary>
    Task<IReadOnlyList<FrontierRow>> ReleaseAsync(CrawlRunKey run, string host, int max, CancellationToken cancellationToken);

    /// <summary>The row is done for the run (<c>idle</c>); <paramref name="facts"/> null keeps the last response's facts (an abandoned fetch).</summary>
    Task CompleteAsync(CrawlRunKey run, string urlHash, FrontierFacts? facts, CancellationToken cancellationToken);

    /// <summary>A redirect target fetched for the run: upserted <c>idle</c> with the run, so a later link to it is not fetched again.</summary>
    Task RecordTargetAsync(CrawlRunKey run, FrontierAdmission target, FrontierFacts facts, CancellationToken cancellationToken);
}

/// <summary>A host paused for the run (<c>CrawlRuns.PausedHosts</c> by host): how often, why last, until when.</summary>
public sealed record HostPause(int Count, string Reason, DateTimeOffset? Until);

/// <summary>A trap kind's count and one example URL (<c>CrawlRuns.TrapStats</c> by kind, shown in the crawl report).</summary>
public sealed record TrapStat(int Count, string Example);

/// <summary>A <c>content."CrawlRuns"</c> row (data-model §2.2): the run's limits and the facts its completeness is decided from.</summary>
public sealed record CrawlRunRecord
{
    public required Guid RunId { get; init; }

    public required string Classification { get; init; }

    public required Guid SourceId { get; init; }

    public int Admitted { get; init; }

    public required int MaxPages { get; init; }

    public required int MaxDepth { get; init; }

    public bool LimitHit { get; init; }

    public int Seeds { get; init; }

    public int SeedsFailed { get; init; }

    public IReadOnlyDictionary<string, HostPause> PausedHosts { get; init; } = new Dictionary<string, HostPause>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, TrapStat> TrapStats { get; init; } = new Dictionary<string, TrapStat>(StringComparer.Ordinal);

    public bool Complete { get; init; }

    /// <summary>The run's fixed mode is incremental (<see cref="ICrawlRunStore.SetIncrementalAsync"/>).</summary>
    public bool Incremental { get; init; }

    public CrawlRunKey Key => new(SourceId, Classification, RunId);
}

/// <summary><c>content."CrawlRuns"</c>; <c>PausedHosts</c> and <c>TrapStats</c> are <c>jsonb</c> objects of <see cref="HostPause"/> and <see cref="TrapStat"/>.</summary>
public interface ICrawlRunStore
{
    /// <summary>Inserts the run's row; an existing row (a resumed listing) is kept.</summary>
    Task StartAsync(CrawlRunRecord run, CancellationToken cancellationToken);

    Task<CrawlRunRecord?> GetAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Counts a seed (a start URL, sitemap entry or a sitemap itself) that could not be resolved.</summary>
    Task SeedFailedAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Records a pause of the host for the run and returns how many the run has seen for it.</summary>
    Task<int> PauseHostAsync(Guid runId, string host, string reason, DateTimeOffset? until, CancellationToken cancellationToken);

    /// <summary>Adds trap counts by kind, keeping the first example.</summary>
    Task RecordTrapsAsync(Guid runId, IReadOnlyDictionary<string, TrapStat> traps, CancellationToken cancellationToken);

    Task SetCompleteAsync(Guid runId, bool complete, CancellationToken cancellationToken);

    /// <summary>Records the run's fixed mode (<c>CrawlRuns.Incremental</c>), which later runs' incomplete-traversal count reads.</summary>
    Task SetIncrementalAsync(Guid runId, bool incremental, CancellationToken cancellationToken);
}
