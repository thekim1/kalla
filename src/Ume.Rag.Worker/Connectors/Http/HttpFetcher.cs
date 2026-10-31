using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using Ume.Rag.Worker.Connectors.Egress;
using Ume.Rag.Worker.Connectors.Urls;

namespace Ume.Rag.Worker.Connectors.Http;

/// <param name="Url">The canonical URL to fetch (§3.1).</param>
/// <param name="Purpose">Why (event 5101's <c>Purpose</c>).</param>
/// <param name="Validators">Sent as <c>If-None-Match</c> / <c>If-Modified-Since</c> exactly as stored; null for none.</param>
/// <param name="AcceptedMediaTypes">The pipeline's formats; anything else stops after the headers. Null accepts every type.</param>
/// <param name="Canonicalizer">The rules for redirect targets (the source's and host's options).</param>
/// <param name="Robots">
/// Asked for every hop: null when robots.txt allows the URL, else the refusal (<c>robots.disallow</c>, or <c>egress.*</c>
/// when robots.txt itself could not be fetched past the guard). Null skips robots (robots.txt itself).
/// </param>
/// <param name="MaxBytes">A cap below the type's body limit; null for the limit of §4.</param>
/// <param name="Truncate">Keep the first <paramref name="MaxBytes"/> of a longer body instead of refusing it (robots.txt).</param>
/// <param name="InScope">
/// Asked for every redirect target (§3.2): false ends the fetch with <see cref="HttpFetchResult.OutOfScope"/>. Null follows
/// every target the guard and robots.txt allow (URL lists, robots.txt, sitemaps).
/// </param>
public sealed record HttpFetchRequest(
    Uri Url,
    EgressPurpose Purpose,
    Validators? Validators,
    IReadOnlySet<string>? AcceptedMediaTypes,
    CanonicalizerOptions Canonicalizer,
    Func<Uri, CancellationToken, ValueTask<string?>>? Robots,
    long? MaxBytes = null,
    bool Truncate = false,
    Func<Uri, bool>? InScope = null);

/// <summary>The transport-level result of one fetch with its redirects; the connector maps it to a run item outcome.</summary>
public abstract record HttpFetchResult
{
    private HttpFetchResult()
    {
    }

    /// <summary>The URL that answered and the hops before it; empty when the first request answered.</summary>
    public IReadOnlyList<Uri> Redirects { get; init; } = [];

    /// <summary>A 2xx body within the limits; dispose it.</summary>
    public sealed record Ok(FetchedBody Body, Validators Validators, Uri FinalUrl, string FinalIdentity) : HttpFetchResult
    {
        /// <summary>The response's <c>X-Robots-Tag</c> values joined by commas (§5.2); null when absent.</summary>
        public string? RobotsTag { get; init; }
    }

    public sealed record NotModified(Validators Validators, Uri FinalUrl) : HttpFetchResult;

    /// <summary>Any other final status (4xx, 5xx, a 3xx without a usable <c>Location</c> is <see cref="Error"/>).</summary>
    public sealed record Status(HttpStatusCode Code, TimeSpan? RetryAfter, Uri FinalUrl) : HttpFetchResult;

    /// <summary>Refused before or during the transfer: egress, downgrade, robots on a redirect target, type, size.</summary>
    public sealed record Refused(RunItemOutcome Outcome, string Reason) : HttpFetchResult;

    /// <summary>robots.txt disallows the requested URL itself.</summary>
    public sealed record Skipped(string Reason) : HttpFetchResult;

    /// <summary>
    /// A redirect to <paramref name="Target"/> outside the source scope (§3.2): <paramref name="Permanent"/> (301, 308) on a
    /// known document is a deletion candidate, a temporary one (302, 303, 307: a sign-in wall) a skip.
    /// </summary>
    public sealed record OutOfScope(Uri Target, bool Permanent) : HttpFetchResult;

    /// <summary>A network or protocol failure: <c>net.*</c>, <c>tls.*</c>, <c>redirect.*</c>.</summary>
    public sealed record Error(string Reason, bool Transient) : HttpFetchResult;
}

/// <summary>
/// One GET with manual redirects (connectors design §3.2, §4, §7): at most 5 hops, each re-checked by the egress guard and
/// robots.txt, https → http refused; the body streamed to a temp file within the type's cap counted after decompression;
/// response headers within 15 s, the body within 30 s (HTML) or 120 s (files). Cookies are never sent or stored.
/// </summary>
public sealed class HttpFetcher(CrawlHttpClient client, IEgressGuard guard, TimeProvider time)
{
    public const int MaxRedirects = 5;

    /// <summary>The product token robots.txt groups are matched against (§5.1); the about-page URL joins it with to-verify #94.</summary>
    public const string UserAgent = "UmeaKommunBot/1.0";

    public static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan HtmlBodyTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan FileBodyTimeout = TimeSpan.FromSeconds(120);

    public async Task<HttpFetchResult> FetchAsync(HttpFetchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var url = request.Url;
        var redirects = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { url.AbsoluteUri };
        while (true)
        {
            var decision = guard.CheckUrl(url, request.Purpose);
            if (!decision.IsAllowed)
            {
                return WithRedirects(new HttpFetchResult.Refused(RunItemOutcome.Blocked, "egress." + decision.Reason), redirects);
            }

            if (request.Robots is { } robots && await robots(url, cancellationToken) is { } refusal)
            {
                // A robots refusal of the URL itself is a skip; of a redirect target, or a guard refusal, a block.
                return redirects.Count == 0 && !refusal.StartsWith("egress.", StringComparison.Ordinal)
                    ? new HttpFetchResult.Skipped(refusal)
                    : WithRedirects(new HttpFetchResult.Refused(RunItemOutcome.Blocked, refusal), redirects);
            }

            var (result, location, permanent) = await SendAsync(request, url, cancellationToken);
            if (result is not null)
            {
                return WithRedirects(result, redirects);
            }

            var target = UrlCanonicalizer.Resolve(url, location!, request.Canonicalizer);
            if (!target.IsValid)
            {
                return WithRedirects(new HttpFetchResult.Error("redirect.invalid", Transient: false), redirects);
            }

            var next = target.ToUri();
            redirects.Add(url);
            if (url.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttp)
            {
                return WithRedirects(new HttpFetchResult.Refused(RunItemOutcome.Blocked, "redirect.downgrade"), redirects);
            }

            if (request.InScope is { } inScope && !inScope(next))
            {
                return WithRedirects(new HttpFetchResult.OutOfScope(next, permanent), redirects);
            }

            if (redirects.Count > MaxRedirects || !seen.Add(next.AbsoluteUri))
            {
                return WithRedirects(new HttpFetchResult.Error("redirect.limit", Transient: false), redirects);
            }

            url = next;
        }
    }

    private static HttpFetchResult WithRedirects(HttpFetchResult result, List<Uri> redirects) =>
        redirects.Count == 0 ? result : result with { Redirects = [.. redirects] };

    /// <summary>One request: a final result, or the <c>Location</c> of a redirect to follow and whether it is permanent.</summary>
    private async Task<(HttpFetchResult? Result, string? Location, bool Permanent)> SendAsync(HttpFetchRequest request, Uri url, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, url)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        message.Options.Set(CrawlHttpClient.PurposeKey, request.Purpose);
        message.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        message.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*;q=0.8");
        if (request.Validators is { ETag: { } etag })
        {
            message.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        if (request.Validators is { LastModified: { } lastModified })
        {
            message.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);
        }

        using var headers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headers.CancelAfter(HeaderTimeout);
        HttpResponseMessage response;
        try
        {
            response = await client.Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, headers.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (new HttpFetchResult.Error("net.timeout", Transient: true), null, false);
        }
        catch (HttpRequestException e)
        {
            return (MapException(e), null, false);
        }

        using (response)
        {
            return await ReadAsync(request, url, response, cancellationToken);
        }
    }

    private async Task<(HttpFetchResult? Result, string? Location, bool Permanent)> ReadAsync(HttpFetchRequest request, Uri url, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var code = (int)response.StatusCode;
        if (code is 301 or 302 or 303 or 307 or 308)
        {
            return response.Headers.Location is { } location
                ? (null, location.OriginalString, code is 301 or 308)
                : (new HttpFetchResult.Error("redirect.invalid", Transient: false), null, false);
        }

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return (new HttpFetchResult.NotModified(ValidatorsOf(response, request.Validators), url), null, false);
        }

        if (code is < 200 or > 299)
        {
            return (new HttpFetchResult.Status(response.StatusCode, RetryAfter(response), url), null, false);
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "application/octet-stream";
        if (request.AcceptedMediaTypes is { } accepted && !accepted.Contains(mediaType))
        {
            return (new HttpFetchResult.Refused(RunItemOutcome.Unsupported,
                MediaTypes.Deferred.Contains(mediaType) ? "http.format_deferred" : "http.content_type"), null, false);
        }

        var limit = Math.Min(request.MaxBytes ?? long.MaxValue, MediaTypes.BodyLimit(mediaType));
        if (!request.Truncate && response.Content.Headers.ContentLength > limit)
        {
            return (new HttpFetchResult.Refused(RunItemOutcome.TooLarge, "http.body_limit"), null, false);
        }

        using var body = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        body.CancelAfter(MediaTypes.IsHtml(mediaType) ? HtmlBodyTimeout : FileBodyTimeout);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(body.Token);
            var captured = await FetchedBody.CaptureAsync(stream, limit, request.Truncate, mediaType, response.Content.Headers.ContentType?.CharSet, body.Token);
            if (captured is null)
            {
                return (new HttpFetchResult.Refused(RunItemOutcome.TooLarge, "http.body_limit"), null, false);
            }

            var identity = UrlCanonicalizer.Canonicalize(url.AbsoluteUri, request.Canonicalizer).Value ?? url.AbsoluteUri;
            var robotsTag = response.Headers.TryGetValues("X-Robots-Tag", out var tags) ? string.Join(",", tags) : null;
            return (new HttpFetchResult.Ok(captured, ValidatorsOf(response, null), url, identity) { RobotsTag = robotsTag }, null, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (new HttpFetchResult.Error("net.timeout", Transient: true), null, false);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException)
        {
            // A broken stream, or a body that does not decompress.
            return (new HttpFetchResult.Error("net.body", Transient: true), null, false);
        }
    }

    private static HttpFetchResult MapException(HttpRequestException e)
    {
        for (Exception? inner = e; inner is not null; inner = inner.InnerException)
        {
            switch (inner)
            {
                case EgressBlockedException blocked:
                    return new HttpFetchResult.Refused(RunItemOutcome.Blocked, "egress." + blocked.Decision.Reason);
                case AuthenticationException:
                    return new HttpFetchResult.Error("tls.invalid_certificate", Transient: false);
                case SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain }:
                    return new HttpFetchResult.Error("net.dns", Transient: true);
            }
        }

        return e.HttpRequestError == HttpRequestError.NameResolutionError
            ? new HttpFetchResult.Error("net.dns", Transient: true)
            : new HttpFetchResult.Error("net.connect", Transient: true);
    }

    /// <summary>The response's validators as sent; a 304 without them keeps the ones that were sent.</summary>
    private static Validators ValidatorsOf(HttpResponseMessage response, Validators? sent)
    {
        var etag = response.Headers.TryGetValues("ETag", out var etags) ? etags.FirstOrDefault() : null;
        var lastModified = response.Content.Headers.TryGetValues("Last-Modified", out var dates) ? dates.FirstOrDefault() : null;
        return new Validators(etag ?? sent?.ETag, lastModified ?? sent?.LastModified);
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response) => response.Headers.RetryAfter switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } => date - time.GetUtcNow() is var wait && wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
        _ => null,
    };
}
