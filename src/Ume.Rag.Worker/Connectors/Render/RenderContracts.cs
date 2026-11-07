using Ume.Rag.Worker.Connectors.Http;

namespace Ume.Rag.Worker.Connectors.Render;

/// <summary>
/// Renders one page in a browser (connectors design §8.2, §8.3): every request the page makes goes to the
/// <see cref="IRenderFetcher"/>, the main document is <see cref="RenderRequest.MainDocument"/>. Throws
/// <see cref="RenderUnavailableException"/> when no browser can be reached or a browser operation fails; a page that does
/// not get ready in time is a result with <see cref="RenderResult.Ready"/> false.
/// </summary>
public interface IRenderer
{
    ValueTask<RenderResult> RenderAsync(RenderRequest request, IRenderFetcher fetcher, CancellationToken cancellationToken);
}

/// <summary>The worker's guarded fetch for every request the browser makes (§8.3), one instance per page.</summary>
public interface IRenderFetcher
{
    ValueTask<RenderResponse> FetchAsync(RenderSubrequest request, CancellationToken cancellationToken);
}

/// <param name="MaxRequests"><c>Render:MaxRequests</c>: requests past it are answered 204 (fact <c>truncated</c>).</param>
/// <param name="MaxBytes"><c>Render:MaxBytes</c>: bytes fetched for the page past which requests are answered 204.</param>
/// <param name="PageTimeout"><c>Render:PageTimeout</c>: the page must be ready within it.</param>
/// <param name="QuietPeriod"><c>Render:QuietPeriod</c>: no routed request open for this long after <c>load</c> is ready.</param>
public sealed record RenderLimits(int MaxRequests, long MaxBytes, TimeSpan PageTimeout, TimeSpan QuietPeriod);

/// <param name="Url">The page's URL: the final URL of the main document's HTTP redirects.</param>
/// <param name="MainDocument">The body the worker already fetched; the browser gets it for the first navigation.</param>
/// <param name="ReadySelector">The source's <c>readySelector</c>; null for the quiet rule.</param>
/// <param name="Limits">The page's limits.</param>
public sealed record RenderRequest(Uri Url, FetchedBody MainDocument, string? ReadySelector, RenderLimits Limits)
{
    /// <summary>The source's <c>scrollToLoad</c>: scroll to the bottom until the page stops growing (at most <see cref="MaxScrollSteps"/>).</summary>
    public bool ScrollToLoad { get; init; }

    /// <summary>The page host's politeness User-Agent (§6.6), also what page script sees.</summary>
    public string UserAgent { get; init; } = HttpFetcher.UserAgent;

    /// <summary>The main document's <c>Set-Cookie</c> values, handed to the page's context only (§8.3).</summary>
    public IReadOnlyList<string> SetCookies { get; init; } = [];

    public const int MaxScrollSteps = 10;
}

/// <summary>What the browser saw of one page.</summary>
/// <param name="Html">The serialised DOM without scripts (<see cref="RenderedHtml.Clean"/>); empty when not ready or navigated away.</param>
/// <param name="Stats">Requests by how they were answered, and the render's duration.</param>
/// <param name="Ready">The page reached ready within <see cref="RenderLimits.PageTimeout"/>.</param>
public sealed record RenderResult(string Html, RenderStats Stats, bool Ready)
{
    /// <summary>
    /// A main-frame navigation by script or meta refresh before ready (§8.3): a redirect hop the worker fetches and renders
    /// instead; null when the page stayed.
    /// </summary>
    public Uri? Navigation { get; init; }

    /// <summary>Navigations after ready, popups and downloads: links of the page besides its <c>&lt;a href&gt;</c>.</summary>
    public IReadOnlyList<Uri> Discovered { get; init; } = [];
}

/// <summary>Requests of one render by how they were answered (metric <c>rag.worker.render.requests</c>).</summary>
public sealed record RenderStats(int Fetched, int Cached, int Blocked, int Refused, int Truncated, int Failed, TimeSpan Duration)
{
    /// <summary>The requests the page made, the main document included.</summary>
    public int Requests => 1 + Fetched + Cached + Blocked + Refused + Truncated + Failed;
}

/// <summary>One request the browser made, as the route sees it.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Url">The absolute URL.</param>
/// <param name="Headers">The browser's request headers (the <c>Cookie</c> header included).</param>
/// <param name="Body">The request body, or null.</param>
/// <param name="ResourceType">Playwright's resource type (<c>script</c>, <c>stylesheet</c>, <c>image</c>, <c>fetch</c>, …).</param>
public sealed record RenderSubrequest(string Method, Uri Url, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[]? Body, string ResourceType);

/// <summary>How a browser request was answered.</summary>
public enum RenderResponseKind
{
    /// <summary>Fetched by the worker.</summary>
    Fetched,

    /// <summary>From the asset cache.</summary>
    Cached,

    /// <summary>Not fetched by design: images, fonts, media, iframe documents, tracker hosts (204).</summary>
    Blocked,

    /// <summary>Refused: the egress guard, robots.txt, the host budget, or a method outside the method policy.</summary>
    Refused,

    /// <summary>Past <see cref="RenderLimits.MaxRequests"/> or <see cref="RenderLimits.MaxBytes"/> (204).</summary>
    Truncated,

    /// <summary>A network failure or timeout.</summary>
    Failed,
}

/// <summary>The answer to a browser request: fulfilled with status, headers and body, or aborted with <see cref="AbortReason"/>.</summary>
public sealed record RenderResponse(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body, RenderResponseKind Kind)
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    /// <summary>Playwright's abort error code (<c>blockedbyclient</c>, <c>failed</c>, <c>timedout</c>); null fulfils the response.</summary>
    public string? AbortReason { get; init; }

    public static RenderResponse NoContent(RenderResponseKind kind) => new(204, NoHeaders, [], kind);

    public static RenderResponse Aborted(RenderResponseKind kind, string reason) => new(0, NoHeaders, [], kind) { AbortReason = reason };
}

/// <summary>No browser could be used for the page: <c>render.unavailable</c> (§8.4); transient.</summary>
public sealed class RenderUnavailableException : Exception
{
    public RenderUnavailableException()
        : base("The renderer is unavailable.")
    {
    }

    public RenderUnavailableException(string message)
        : base(message)
    {
    }

    public RenderUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The run item reasons of the renderer (connectors design §8.3, §8.4).</summary>
public static class RenderReasons
{
    /// <summary>No render slot free (§8.3): the job is released without an attempt.</summary>
    public const string Busy = "render.busy";

    /// <summary>No <c>Render:Endpoint</c>: the job waits without an attempt.</summary>
    public const string NotConfigured = "render.not_configured";

    /// <summary>Not ready within <c>Render:PageTimeout</c>.</summary>
    public const string Timeout = "render.timeout";

    /// <summary>No renderer connection, or a browser operation failed.</summary>
    public const string Unavailable = "render.unavailable";

    /// <summary>Every reason of the renderer starts with this.</summary>
    public const string Prefix = "render.";

    /// <summary>A failed or unconfigured render is tried again after this (§8.4).</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    /// <summary>A job without a free render slot comes back after this.</summary>
    public static readonly TimeSpan BusyDelay = TimeSpan.FromSeconds(3);
}
