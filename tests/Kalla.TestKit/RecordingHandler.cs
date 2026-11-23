using System.Collections.Concurrent;
using System.Net;

namespace Kalla.TestKit;

/// <summary>
/// One request as it was sent: method, path, headers (names compared ignoring case, several values joined with a comma) and
/// body (empty without content), read at send time; <see cref="Status"/> is the answer's, once it came.
/// </summary>
public sealed class RecordedRequest(HttpMethod method, string path, IReadOnlyDictionary<string, string> headers, string body)
{
    public HttpMethod Method => method;

    public string Path => path;

    public IReadOnlyDictionary<string, string> Headers => headers;

    public string Body => body;

    public HttpStatusCode? Status { get; internal set; }

    /// <summary>The header's value, or null when the request had none.</summary>
    public string? Header(string name) => headers.TryGetValue(name, out var value) ? value : null;

    internal static async Task<RecordedRequest> ReadAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        return new RecordedRequest(request.Method, request.RequestUri!.AbsolutePath, headers, body);
    }
}

/// <summary>
/// Records each request (<see cref="RecordedRequest"/>) and passes it on to <paramref name="inner"/>; pass
/// <paramref name="requests"/> to keep the record across the handler instances a client factory builds.
/// </summary>
public sealed class RecordingHandler(HttpMessageHandler inner, ConcurrentQueue<RecordedRequest>? requests = null) : DelegatingHandler(inner)
{
    private readonly ConcurrentQueue<RecordedRequest> _requests = requests ?? new();

    /// <summary>The requests in the order they were sent.</summary>
    public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var recorded = await RecordedRequest.ReadAsync(request, cancellationToken);
        _requests.Enqueue(recorded);
        var response = await base.SendAsync(request, cancellationToken);
        recorded.Status = response.StatusCode;
        return response;
    }
}
