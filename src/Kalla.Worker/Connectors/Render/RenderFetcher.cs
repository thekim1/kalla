using System.Net;
using Kalla.Worker.Connectors.Egress;
using Kalla.Worker.Connectors.Hosts;
using Kalla.Worker.Connectors.Http;

namespace Kalla.Worker.Connectors.Render;

/// <summary>The page a <see cref="RenderFetcher"/> serves.</summary>
/// <param name="Url">The page's URL: its host's requests are paced without a second connection, and only it may receive a <c>POST</c>.</param>
/// <param name="Classification">The source's level (the asset cache's partition).</param>
/// <param name="RunId">The run (or preview job) the page belongs to (the asset cache's partition).</param>
/// <param name="Robots">robots.txt for every request and redirect hop (<see cref="HttpFetchRequest.Robots"/>); null skips it.</param>
/// <param name="Limits">The page's request and byte limits.</param>
public sealed record RenderPage(Uri Url, string Classification, Guid RunId, Func<Uri, CancellationToken, ValueTask<string?>>? Robots, RenderLimits Limits);

/// <summary>Creates the <see cref="RenderFetcher"/> of each rendered page from the worker's shared fetch parts.</summary>
public sealed class RenderFetchers(
    CrawlHttpClient client, IEgressGuard guard, ICrawlPolicySource policy, HostBudget budget, RenderAssetCache assets, RenderBlocklist blocklist, TimeProvider time)
{
    public RenderFetcher For(RenderPage page) => new(client, guard, policy, budget, assets, blocklist, time, page);
}

/// <summary>
/// The worker side of every request a rendered page makes (connectors design §8.3, <see cref="IRenderFetcher"/>), in this
/// order: images, fonts and media, and hosts on the <see cref="RenderBlocklist"/>, get an empty 204 (blocked); past
/// <c>Render:MaxRequests</c> or <c>Render:MaxBytes</c> a 204 (truncated); the method policy allows <c>GET</c> and
/// <c>HEAD</c> anywhere and <c>POST</c> only to the page's own host, every other method gets a 204 (refused); scripts and
/// stylesheets come from the <see cref="RenderAssetCache"/> when they can; everything else is fetched with the guarded
/// <see cref="CrawlHttpClient"/> under <see cref="EgressPurpose.Render"/>: the egress guard (event 5101 with
/// <c>Purpose = Render</c>), robots.txt and the host budget's pacing on every hop, the host's politeness User-Agent, the
/// request's method, headers (the browser's <c>Cookie</c> header included) and body, redirects followed here (a fulfilled
/// 30x would be continued by the browser unrouted, and fail) and https → http refused. The final response is handed back
/// with its headers (<c>Set-Cookie</c> included, so the page's own context keeps its cookies) but without
/// <c>content-encoding</c> and <c>content-length</c>. A refused request is aborted (<c>blockedbyclient</c>), a network
/// failure aborted as <c>failed</c>. Requests to the page's own host take a pacing slot only (<see cref="HostBudget.PaceAsync"/>):
/// the page's fetch job holds the host's connection already.
/// </summary>
public sealed class RenderFetcher : IRenderFetcher
{
    private static readonly HashSet<string> DroppedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "connection", "keep-alive", "proxy-connection", "proxy-authorization", "transfer-encoding", "upgrade", "te", "trailer",
        "user-agent", "content-length", "accept-encoding",
    };

    private static readonly HashSet<string> DroppedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "transfer-encoding", "content-encoding", "content-length", "set-cookie2", "alt-svc",
        "strict-transport-security", "report-to", "nel",
    };

    private readonly CrawlHttpClient _client;
    private readonly IEgressGuard _guard;
    private readonly ICrawlPolicySource _policy;
    private readonly HostBudget _budget;
    private readonly RenderAssetCache _assets;
    private readonly RenderBlocklist _blocklist;
    private readonly TimeProvider _time;
    private readonly RenderPage _page;
    private int _requests;
    private long _bytes;

    internal RenderFetcher(
        CrawlHttpClient client, IEgressGuard guard, ICrawlPolicySource policy, HostBudget budget, RenderAssetCache assets, RenderBlocklist blocklist,
        TimeProvider time, RenderPage page)
    {
        _client = client;
        _guard = guard;
        _policy = policy;
        _budget = budget;
        _assets = assets;
        _blocklist = blocklist;
        _time = time;
        _page = page;
    }

    public async ValueTask<RenderResponse> FetchAsync(RenderSubrequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ResourceType is "image" or "font" or "media" || _blocklist.Blocks(request.Url))
        {
            return RenderResponse.NoContent(RenderResponseKind.Blocked);
        }

        if (Interlocked.Increment(ref _requests) > _page.Limits.MaxRequests || Interlocked.Read(ref _bytes) >= _page.Limits.MaxBytes)
        {
            return RenderResponse.NoContent(RenderResponseKind.Truncated);
        }

        if (!Allowed(request))
        {
            return RenderResponse.NoContent(RenderResponseKind.Refused);
        }

        var cacheable = RenderAssetCache.Cacheable(request);
        if (cacheable && _assets.TryGet(_page.Classification, _page.RunId, request.Url, out var cached))
        {
            return cached;
        }

        var response = await SendAsync(request, cancellationToken);
        if (cacheable)
        {
            _assets.Put(_page.Classification, _page.RunId, request.Url, response);
        }

        return response;
    }

    /// <summary>
    /// The host budget's slot for one request to <paramref name="url"/> that leaves the worker: pacing only on the page's
    /// own host, a slot and a connection elsewhere; a slot more than a second away is waited for (the page's deadline
    /// cancels the wait). Null when the host is blocked or paused.
    /// </summary>
    public async Task<HostLease?> SlotAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        var own = string.Equals(url.Authority, _page.Url.Authority, StringComparison.OrdinalIgnoreCase);
        while (true)
        {
            switch (own ? await _budget.PaceAsync(url.Authority, cancellationToken) : await _budget.AcquireAsync(url.Authority, cancellationToken))
            {
                case HostSlot.Granted granted:
                    return granted.Lease;
                case HostSlot.Later later:
                    await Task.Delay(later.Delay, _time, cancellationToken);
                    break;
                default:
                    return null;
            }
        }
    }

    /// <summary>The method policy: <c>GET</c> and <c>HEAD</c> anywhere, <c>POST</c> to the page's own host (CORS preflights never reach the worker).</summary>
    private bool Allowed(RenderSubrequest request) =>
        request.Url.Scheme is "http" or "https"
        && (request.Method is "GET" or "HEAD"
            || (request.Method == "POST" && string.Equals(request.Url.IdnHost, _page.Url.IdnHost, StringComparison.OrdinalIgnoreCase)));

    private async Task<RenderResponse> SendAsync(RenderSubrequest request, CancellationToken cancellationToken)
    {
        var url = request.Url;
        var method = request.Method;
        var body = request.Body;
        for (var hop = 0; ; hop++)
        {
            if (!_guard.CheckUrl(url, EgressPurpose.Render).IsAllowed)
            {
                return RenderResponse.Aborted(RenderResponseKind.Refused, "blockedbyclient");
            }

            if (_page.Robots is { } robots && await robots(url, cancellationToken) is not null)
            {
                return RenderResponse.Aborted(RenderResponseKind.Refused, "blockedbyclient");
            }

            using var lease = await SlotAsync(url, cancellationToken);
            if (lease is null)
            {
                return RenderResponse.Aborted(RenderResponseKind.Refused, "blockedbyclient");
            }

            using var message = Message(method, url, request.Headers, body);
            var started = _time.GetTimestamp();
            HttpResponseMessage response;
            try
            {
                using var headers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                headers.CancelAfter(HttpFetcher.HeaderTimeout);
                response = await _client.Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, headers.Token);
            }
            catch (HttpRequestException e) when (Blocked(e))
            {
                return RenderResponse.Aborted(RenderResponseKind.Refused, "blockedbyclient");
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
            {
                var timedOut = e is OperationCanceledException;
                await lease.CompleteAsync(new HostResponse(timedOut ? HostResponseKind.Failure : HostResponseKind.Neutral, _time.GetElapsedTime(started)), CancellationToken.None);
                return RenderResponse.Aborted(RenderResponseKind.Failed, timedOut ? "timedout" : "failed");
            }

            using (response)
            {
                var code = (int)response.StatusCode;
                await lease.CompleteAsync(Response(response, _time.GetElapsedTime(started)), CancellationToken.None);
                if (code is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
                {
                    var next = new Uri(url, location);
                    if (hop >= HttpFetcher.MaxRedirects || (url.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttp))
                    {
                        return RenderResponse.Aborted(RenderResponseKind.Refused, "blockedbyclient");
                    }

                    if (code is 301 or 302 or 303 && method is not ("GET" or "HEAD"))
                    {
                        (method, body) = ("GET", null);
                    }

                    url = next;
                    continue;
                }

                return await ReadAsync(response, method, cancellationToken);
            }
        }
    }

    private HttpRequestMessage Message(string method, Uri url, IReadOnlyList<KeyValuePair<string, string>> headers, byte[]? body)
    {
        var message = new HttpRequestMessage(new HttpMethod(method), url)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        message.Options.Set(CrawlHttpClient.PurposeKey, EgressPurpose.Render);
        message.Headers.TryAddWithoutValidation("User-Agent", _policy.Current.UserAgentOf(url.Authority));
        if (body is not null)
        {
            message.Content = new ByteArrayContent(body);
        }

        foreach (var (name, value) in headers)
        {
            if (name.StartsWith(':') || DroppedRequestHeaders.Contains(name))
            {
                continue;
            }

            if (!message.Headers.TryAddWithoutValidation(name, value))
            {
                message.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return message;
    }

    /// <summary>The body within what is left of <c>Render:MaxBytes</c>; past it the request is truncated.</summary>
    private async Task<RenderResponse> ReadAsync(HttpResponseMessage response, string method, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            if (!DroppedResponseHeaders.Contains(name))
            {
                // Several Set-Cookie values go to Playwright one per line.
                headers[name.ToLowerInvariant()] = string.Join(name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase) ? "\n" : ", ", values);
            }
        }

        if (method == "HEAD")
        {
            return new RenderResponse((int)response.StatusCode, headers, [], RenderResponseKind.Fetched);
        }

        var left = _page.Limits.MaxBytes - Interlocked.Read(ref _bytes);
        if (response.Content.Headers.ContentLength > left)
        {
            return RenderResponse.NoContent(RenderResponseKind.Truncated);
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                Interlocked.Add(ref _bytes, read);
                if (buffer.Length + read > left)
                {
                    return RenderResponse.NoContent(RenderResponseKind.Truncated);
                }

                buffer.Write(chunk, 0, read);
            }

            return new RenderResponse((int)response.StatusCode, headers, buffer.ToArray(), RenderResponseKind.Fetched);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
        {
            return RenderResponse.Aborted(RenderResponseKind.Failed, "failed");
        }
    }

    /// <summary>What a response tells the host budget: a 429/503 backs off, a 5xx counts toward the breaker; a sub-resource's 404 does not.</summary>
    private static HostResponse Response(HttpResponseMessage response, TimeSpan latency) => (int)response.StatusCode switch
    {
        429 or 503 => new HostResponse(HostResponseKind.RateLimited, latency, response.Headers.RetryAfter?.Delta),
        >= 500 => new HostResponse(HostResponseKind.Failure, latency),
        < 400 => new HostResponse(HostResponseKind.Success, latency),
        _ => new HostResponse(HostResponseKind.Neutral, latency),
    };

    private static bool Blocked(Exception e)
    {
        for (Exception? inner = e; inner is not null; inner = inner.InnerException)
        {
            if (inner is EgressBlockedException)
            {
                return true;
            }
        }

        return false;
    }
}
