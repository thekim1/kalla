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
}

/// <summary>A URL to admit: canonical, in scope, below the depth limit, not a trap.</summary>
public sealed record FrontierAdmission(string Url, string Host, short Depth, bool Leaf, DateTimeOffset? SitemapLastMod = null)
{
    public string UrlHash => Convert.ToHexStringLower(Urls.UrlCanonicalizer.IdentityHash(Url));
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
    /// Upserts the URLs for the run: a row not yet of this run becomes <c>pending</c> with the run, depth and leaf (and the
    /// sitemap <c>lastmod</c> when given); a row already of this run is left as it is. New rows are admitted in order while
    /// <c>CrawlRuns.Admitted</c> is below <c>MaxPages</c>, which counts them (depth 0 also counts <c>Seeds</c>); the first
    /// refused one sets <c>LimitHit</c>.
    /// </summary>
    Task<AdmitResult> AdmitAsync(CrawlRunKey run, IReadOnlyList<FrontierAdmission> urls, CancellationToken cancellationToken);

    Task<FrontierRow?> GetAsync(Guid sourceId, string classification, string urlHash, CancellationToken cancellationToken);

    /// <summary>Hosts with <c>pending</c> or <c>released</c> rows of the run.</summary>
    Task<IReadOnlyList<HostLoad>> HostLoadAsync(CrawlRunKey run, CancellationToken cancellationToken);

    /// <summary>Marks up to <paramref name="max"/> <c>pending</c> rows of the host <c>released</c>, lowest depth first (BFS), and returns them.</summary>
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
}
