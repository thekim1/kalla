using System.Collections.Concurrent;

namespace Kalla.TestKit;

/// <summary>
/// A gateway (or any upstream) that answers every request itself with <c>answer</c> and records it
/// (<see cref="RecordedRequest"/>); also the <see cref="IHttpClientFactory"/> of code that takes one, with clients on
/// <c>http://gateway.invalid/</c>. Given <c>clientName</c>, a client of any other name is refused.
/// </summary>
public sealed class StubHandler : HttpMessageHandler, IHttpClientFactory
{
    private static readonly Uri BaseAddress = new("http://gateway.invalid/");

    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _answer;
    private readonly string? _clientName;
    private readonly ConcurrentQueue<RecordedRequest> _requests = new();

    public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer, string? clientName = null)
        : this((request, _) => answer(request), clientName)
    {
    }

    public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer, string? clientName = null)
    {
        _answer = answer;
        _clientName = clientName;
    }

    /// <summary>The requests in the order they came.</summary>
    public IReadOnlyList<RecordedRequest> Requests => [.. _requests];

    public HttpClient CreateClient(string name) =>
        _clientName is null || name == _clientName
            ? new HttpClient(this, disposeHandler: false) { BaseAddress = BaseAddress }
            : throw new InvalidOperationException($"Client '{name}' was asked for; this stub serves '{_clientName}'.");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var recorded = await RecordedRequest.ReadAsync(request, cancellationToken);
        _requests.Enqueue(recorded);
        var response = await _answer(request, cancellationToken);
        recorded.Status = response.StatusCode;
        return response;
    }
}
