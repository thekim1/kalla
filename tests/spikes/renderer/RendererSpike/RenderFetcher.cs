using System.Collections.Concurrent;
using Ume.Rag.Worker.Connectors.Egress;

namespace RendererSpike;

/// <summary>One browser request as the route handler sees it.</summary>
public sealed record SubRequest(string Method, Uri Url, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[]? Body, string ResourceType);

/// <summary>What the worker answers the browser with.</summary>
public sealed record SubResponse(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body, Uri FinalUrl, int Hops, string? Refused)
{
    public static SubResponse Blocked(Uri url, string reason) =>
        new(403, new Dictionary<string, string> { ["content-type"] = "text/plain" }, [], url, 0, reason);
}

/// <summary>
/// The worker-side fetch for every browser request (connectors design §8.3, <c>IRenderFetcher</c>): the worker's real
/// <see cref="CrawlHttpClient"/> (DNS-pinned connect through the <see cref="IEgressGuard"/>), the guard's URL check on every
/// hop, no cookies. Unlike <c>HttpFetcher</c> it forwards the method, the request headers and the body, and hands back
/// the response headers (CORS, content type, caching), which the browser needs.
/// </summary>
public sealed class RenderFetcher(CrawlHttpClient client, IEgressGuard guard)
{
    public const string UserAgent = "UmeaKommunBot/1.0 (renderer spike)";

    private static readonly HashSet<string> DroppedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "connection", "keep-alive", "proxy-connection", "transfer-encoding", "upgrade", "te", "trailer",
        "cookie", "user-agent", "content-length", "accept-encoding",
    };

    private static readonly HashSet<string> DroppedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "transfer-encoding", "content-encoding", "content-length", "set-cookie", "set-cookie2",
        "alt-svc", "strict-transport-security", "report-to", "nel",
    };

    public int MaxRedirects { get; init; } = 5;

    /// <summary>Forward the browser's <c>Cookie</c> header and hand <c>Set-Cookie</c> back (the cookie check); off by default.</summary>
    public bool PassCookies { get; init; }

    /// <summary>True: follow redirects here and answer with the final response. False: hand the 30x to the browser.</summary>
    public bool FollowRedirects { get; init; } = true;

    public async Task<SubResponse> FetchAsync(SubRequest request, CancellationToken ct)
    {
        var url = request.Url;
        var method = request.Method;
        var body = request.Body;
        for (var hop = 0; ; hop++)
        {
            var decision = guard.CheckUrl(url, EgressPurpose.Fetch);
            if (!decision.IsAllowed)
            {
                return SubResponse.Blocked(url, "egress." + decision.Reason);
            }

            // Spike only: the fixture speaks plain HTTP, so https://*.test is fetched over http (a secure context for the page).
            var wire = url.Scheme == Uri.UriSchemeHttps && url.Host.EndsWith(".test", StringComparison.Ordinal)
                ? new UriBuilder(url) { Scheme = Uri.UriSchemeHttp, Port = 80 }.Uri
                : url;
            using var message = new HttpRequestMessage(new HttpMethod(method), wire);
            message.Options.Set(CrawlHttpClient.PurposeKey, EgressPurpose.Fetch);
            message.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            foreach (var (name, value) in request.Headers)
            {
                if ((DroppedRequestHeaders.Contains(name) && !(PassCookies && name.Equals("cookie", StringComparison.OrdinalIgnoreCase))) || name.StartsWith(':'))
                {
                    continue;
                }

                if (!message.Headers.TryAddWithoutValidation(name, value) && body is not null)
                {
                    message.Content ??= new ByteArrayContent(body);
                    message.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }

            if (body is not null)
            {
                message.Content ??= new ByteArrayContent(body);
            }

            HttpResponseMessage response;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                response = await client.Http.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token);
            }
            catch (HttpRequestException e) when (Find<EgressBlockedException>(e) is { } blocked)
            {
                return SubResponse.Blocked(url, "egress." + blocked.Decision.Reason);
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
            {
                return SubResponse.Blocked(url, "net." + (e is OperationCanceledException ? "timeout" : "error"));
            }

            using (response)
            {
                var code = (int)response.StatusCode;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
                {
                    if (!DroppedResponseHeaders.Contains(name) || (PassCookies && name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase)))
                    {
                        headers[name.ToLowerInvariant()] = string.Join(name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase) ? "\n" : ", ", values);
                    }
                }

                if (code is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
                {
                    var next = new Uri(url, location);
                    if (!FollowRedirects)
                    {
                        headers["location"] = next.AbsoluteUri;
                        return new SubResponse(code, headers, [], url, hop, null);
                    }

                    if (hop >= MaxRedirects)
                    {
                        return SubResponse.Blocked(url, "redirect.limit");
                    }

                    if (url.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttp)
                    {
                        return SubResponse.Blocked(url, "redirect.downgrade");
                    }

                    if (code is 301 or 302 or 303 && method != "GET" && method != "HEAD")
                    {
                        method = "GET";
                        body = null;
                    }

                    url = next;
                    continue;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                return new SubResponse(code, headers, bytes, url, hop, null);
            }
        }
    }

    private static T? Find<T>(Exception e)
        where T : Exception
    {
        for (Exception? inner = e; inner is not null; inner = inner.InnerException)
        {
            if (inner is T found)
            {
                return found;
            }
        }

        return null;
    }
}

/// <summary>Scripts and stylesheets kept across pages (connectors design §8.3 "Asset cache"), by URL; <c>no-store</c> is not kept.</summary>
public sealed class AssetCache
{
    private readonly ConcurrentDictionary<string, SubResponse> _entries = new(StringComparer.Ordinal);
    private long _bytes;

    public long Bytes => Interlocked.Read(ref _bytes);

    public int Count => _entries.Count;

    public static bool Cacheable(SubRequest request) =>
        request.Method == "GET" && request.ResourceType is "script" or "stylesheet";

    public bool TryGet(Uri url, out SubResponse response) => _entries.TryGetValue(url.AbsoluteUri, out response!);

    public void Put(Uri url, SubResponse response)
    {
        if (response.Status != 200 || response.Refused is not null
            || (response.Headers.TryGetValue("cache-control", out var cc) && cc.Contains("no-store", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (_entries.TryAdd(url.AbsoluteUri, response))
        {
            Interlocked.Add(ref _bytes, response.Body.Length);
        }
    }

    public void Clear()
    {
        _entries.Clear();
        Interlocked.Exchange(ref _bytes, 0);
    }
}

/// <summary>The guard's run-time policy: ports 80/443, no blocked hosts.</summary>
public sealed class StaticPolicy : IEgressPolicySource
{
    public EgressPolicy Current { get; } = EgressPolicy.Default;
}
