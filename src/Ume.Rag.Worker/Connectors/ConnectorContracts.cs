using System.Text.Json;
using Ume.Rag.Domain.Ports;

namespace Ume.Rag.Worker.Connectors;

/// <summary>
/// A source type's connector (connectors design §2.1). Listing runs in the <c>sync</c> job, fetching in the <c>fetch</c>
/// job; connectors get no <c>DbContext</c>: the runner writes frontier rows, documents and run items.
/// </summary>
public interface IConnector
{
    /// <summary><c>url-list</c>, <c>crawl</c>, <c>upload</c> (<c>Sources.Type</c>).</summary>
    string SourceType { get; }

    /// <summary>Bound from <c>Sources.Settings</c>; schema <c>contracts/sources/v1/{type}.schema.json</c>.</summary>
    Type SettingsType { get; }
}

public interface IFetchConnector<TCheckpoint> : IConnector where TCheckpoint : ConnectorCheckpoint, new()
{
    /// <summary>
    /// The <c>sync</c> job: yields work and failures; a <see cref="ListingEvent{TCheckpoint}.Checkpoint"/> is a safe resume
    /// point, and the last event before <see cref="ListingEvent{TCheckpoint}.Done"/> is one.
    /// </summary>
    IAsyncEnumerable<ListingEvent<TCheckpoint>> ListAsync(ListingContext context, TCheckpoint from, CancellationToken cancellationToken);

    /// <summary>The <c>fetch</c> job's fetch stage: one work item to content, not modified, gone, skipped, refused or transient.</summary>
    ValueTask<FetchOutcome> FetchAsync(FetchContext context, WorkItem item, CancellationToken cancellationToken);
}

/// <summary>Cheap "what exists now", for deletion detection (§2.1).</summary>
public interface IIdListingConnector : IConnector
{
    IAsyncEnumerable<string> ListIdentitiesAsync(ListingContext context, CancellationToken cancellationToken);
}

/// <summary>The last run item of an identity that failed (<c>RunItems</c>, outcome <c>failed</c>).</summary>
/// <param name="DocumentId">Its document, when one exists.</param>
/// <param name="Reason">The item's reason code.</param>
/// <param name="FoundUrl">
/// The URL it was last found or fetched under, when kept (a crawl's <c>CrawlFrontier.FetchUrl</c>, else <c>Documents.Url</c>; #228).
/// </param>
public sealed record FailedItem(Guid? DocumentId, string? Reason, string? FoundUrl = null);

/// <summary>
/// A <c>retry-failed</c> run (§2.2, engine design §9.2): rebuilds the work item of an identity whose last run item failed;
/// null when the identity names nothing the connector can fetch again (a batch manifest, an invalid URL).
/// </summary>
public interface IRetryFailedConnector : IConnector
{
    WorkItem? FromIdentity(string canonicalIdentity, FailedItem last);
}

/// <summary>The work item of a canonical URL identity again (URL lists and crawls), at depth 0 with no hints.</summary>
public static class RetryWork
{
    /// <param name="canonicalIdentity">The identity.</param>
    /// <param name="fetchUrl">The URL to fetch instead of the identity (a crawl's kept path form); ignored unless an http(s) URL.</param>
    public static WorkItem? FromUrl(string canonicalIdentity, string? fetchUrl = null)
    {
        ArgumentNullException.ThrowIfNull(canonicalIdentity);
        return Http(canonicalIdentity) is { } url
            ? new WorkItem(IdentityKind.Url, canonicalIdentity, (fetchUrl is null ? null : Http(fetchUrl)) ?? url, 0, new WorkHints())
            : null;
    }

    private static Uri? Http(string text) => Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" ? url : null;
}

/// <summary>A typed resume point, stored in <c>content."ConnectorCheckpoints"</c> (§2.3).</summary>
public abstract record ConnectorCheckpoint
{
    public int SchemaVersion { get; init; }
}

public abstract record ListingEvent<TCheckpoint>
{
    private ListingEvent()
    {
    }

    public sealed record Item(WorkItem Work) : ListingEvent<TCheckpoint>;

    public sealed record Failure(string Identity, string Reason) : ListingEvent<TCheckpoint>;

    public sealed record Checkpoint(TCheckpoint State) : ListingEvent<TCheckpoint>;

    public sealed record Done(Completeness Completeness) : ListingEvent<TCheckpoint>;
}

/// <summary>Whether a listing saw everything (<c>Partial</c> carries why); only a complete one feeds <c>not-seen</c>.</summary>
public sealed record Completeness(bool Complete, string? Reason)
{
    public static Completeness Full { get; } = new(true, null);

    public static Completeness Partial(string reason) => new(false, reason);
}

/// <summary><c>Documents.IdentityKind</c> (§3.4).</summary>
public enum IdentityKind
{
    Url,
    Upload,
}

/// <param name="Kind">The identity's kind.</param>
/// <param name="CanonicalIdentity">The canonical URL (§3.1) or <c>upload:&lt;uploadId&gt;</c>.</param>
/// <param name="Url">The URL to fetch; null for uploads.</param>
/// <param name="Depth">Link distance from a seed (0 for listed URLs).</param>
/// <param name="Hints">What the runner knows about the item from earlier runs.</param>
public sealed record WorkItem(IdentityKind Kind, string CanonicalIdentity, Uri? Url, short Depth, WorkHints Hints);

/// <param name="Stored">The validators of the last 200 response (frontier row), for a conditional GET (§4).</param>
/// <param name="ContentHash">The content hash of that response (frontier row): a 304 expands the stored outlinks of it (§5.4).</param>
/// <param name="SitemapLastMod">The URL's sitemap <c>lastmod</c>, stored on the frontier row (§5.1) for the <c>lastmod</c> skip and incremental runs (§5.4).</param>
/// <param name="Upload">The upload slot and the file's manifest entry, for upload items (§9).</param>
public sealed record WorkHints(Validators? Stored = null, string? ContentHash = null, DateTimeOffset? SitemapLastMod = null, UploadHint? Upload = null)
{
    public static WorkHints None { get; } = new();
}

/// <param name="UploadId">The slot (<c>content."Uploads"."Id"</c>).</param>
/// <param name="Sha256">The current version's SHA-256 (lower-case hex): the blob is <c>uploads/{sourceId}/{slot}/{sha256}</c>.</param>
/// <param name="FileName">The uploaded file name (the document's <c>DisplayName</c>, never its <c>Title</c>, §9.4).</param>
/// <param name="DeclaredMediaType">The type the browser declared; compared with the sniffed type (§9.2).</param>
public sealed record UploadHint(Guid UploadId, string Sha256, string FileName, string? DeclaredMediaType);

/// <summary>HTTP validators exactly as the server sent them (weak ETags kept, <c>Last-Modified</c> never our own clock).</summary>
public sealed record Validators(string? ETag, string? LastModified)
{
    public static Validators None { get; } = new(null, null);

    public bool IsEmpty => ETag is null && LastModified is null;

    /// <summary>
    /// <see cref="LastModified"/> as a date (an HTTP-date, RFC 9110 §5.6.7, in any form <c>HttpClient</c> reads),
    /// or null when absent or unreadable: the last chunk date candidate (processing design §7).
    /// </summary>
    public DateTimeOffset? LastModifiedDate =>
        LastModified is { } text && System.Net.Http.Headers.RangeConditionHeaderValue.TryParse(text, out var value)
            ? value.Date
            : null;
}

/// <summary>Where the source and its level are; settings are the source's JSON (<c>Sources.Settings</c>).</summary>
public sealed record ListingContext(Guid SourceId, string Classification, Guid RunId, JsonElement Settings)
{
    /// <summary>An upload commit's batch (<c>sync</c> payload <c>batchId</c>, §9.1); null for every other listing.</summary>
    public Guid? BatchId { get; init; }
}

/// <param name="SourceId">The source the item belongs to.</param>
/// <param name="Classification">The source's level; raw blobs are stored under it.</param>
/// <param name="RunId">The run the fetch serves.</param>
/// <param name="Settings">The source's JSON settings.</param>
/// <param name="Revalidate">
/// A forced run or every <c>revalidateEvery</c>-th run: no validators are sent (§4), catching servers whose 304s are wrong.
/// </param>
public sealed record FetchContext(Guid SourceId, string Classification, Guid RunId, JsonElement Settings, bool Revalidate);

/// <summary>The run item outcomes a connector can decide on its own (engine design §10.2).</summary>
public enum RunItemOutcome
{
    Unsupported,
    TooLarge,
    Blocked,
}

/// <summary>Stored raw content: write-once under <c>raw/{level}/{contentHash}</c> (§12.1, data-model §9).</summary>
/// <param name="Key">The blob key.</param>
/// <param name="ContentHash">SHA-256 (hex) over the noise-normalised HTML or the raw bytes (§12.1).</param>
/// <param name="ContentHashVersion">The normalisation rules version (<c>Documents.ContentHashVersion</c>).</param>
/// <param name="MediaType">The response's media type, without parameters.</param>
/// <param name="Bytes">Raw length after decompression.</param>
public sealed record RawContentRef(BlobKey Key, string ContentHash, int ContentHashVersion, string MediaType, long Bytes);

/// <param name="FinalUrl">The URL that answered 200, after redirects; null for uploads.</param>
/// <param name="CanonicalIdentity">The identity of <paramref name="FinalUrl"/> (§3.2: identity = final URL), or <c>upload:&lt;uploadId&gt;</c>.</param>
/// <param name="Redirects">The chain before <paramref name="FinalUrl"/>, in order (recorded on the run item).</param>
/// <param name="CanonicalHint">The page's declared <c>rel=canonical</c>, stored, never trusted unverified (§3.3).</param>
public sealed record FetchFacts(Uri? FinalUrl, string CanonicalIdentity, IReadOnlyList<Uri> Redirects, string? CanonicalHint)
{
    /// <summary>Run item facts (<c>&lt;area&gt;.&lt;code&gt;</c>), e.g. <c>upload.declared_type_differs</c>.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>What one fetch produced (§2.1, outcomes §15). Reasons are <c>&lt;area&gt;.&lt;code&gt;</c>, never library text.</summary>
public abstract record FetchOutcome
{
    private FetchOutcome()
    {
    }

    public sealed record Content(RawContentRef Raw, Validators Validators, FetchFacts Facts) : FetchOutcome;

    /// <summary>
    /// 304: validators refreshed, the document is touched and <c>EnsureCurrent</c> runs. <paramref name="FinalUrl"/> is the URL
    /// that answered (after redirects), or the known path form of a crawl URL that was not requested (#228); when set, it
    /// becomes <c>Documents.Url</c>, as a 200's final URL does.
    /// </summary>
    public sealed record NotModified(Validators Validators, Uri? FinalUrl = null) : FetchOutcome;

    /// <summary><c>http.404</c>, <c>http.410</c>, <c>http.moved_out_of_scope</c>: a deletion candidate for a known document (§13.1).</summary>
    public sealed record Gone(string Reason) : FetchOutcome;

    /// <summary>Excluded on purpose: <c>robots.disallow</c>, <c>robots.noindex</c>, <c>redirect.out_of_scope</c>, <c>upload.duplicate</c>.</summary>
    public sealed record Skipped(string Reason) : FetchOutcome;

    /// <summary>Blocked, unsupported or too large.</summary>
    public sealed record Refused(RunItemOutcome Outcome, string Reason) : FetchOutcome;

    /// <summary>
    /// A ClamAV finding (outcome <c>malware</c>): the bytes are already in <paramref name="Key"/>
    /// (<c>quarantine/{sourceId}/{sha256}</c>); the runner writes the <c>QuarantinedFiles</c> row and event 5102.
    /// </summary>
    public sealed record Quarantined(BlobKey Key, string Sha256, string Signature, string Reason) : FetchOutcome;

    /// <summary>Retry later: with an attempt (5xx, timeouts) or without (rate limits, ClamAV down).</summary>
    public sealed record Transient(string Reason, TimeSpan? RetryAfter, bool CountsAttempt) : FetchOutcome;

    /// <summary>A failure no retry fixes: 401/403, TLS, too many redirects (run item <c>failed</c>).</summary>
    public sealed record Failed(string Reason) : FetchOutcome;
}
