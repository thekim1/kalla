namespace Kalla.TestKit;

/// <summary>
/// Counts the requests that pass on to <paramref name="inner"/>, all of them or those <paramref name="counts"/> picks;
/// <paramref name="counted"/> is called for each counted one, for a count kept across the handler instances a client
/// factory builds.
/// </summary>
public sealed class CountingHandler(HttpMessageHandler inner, Func<HttpRequestMessage, bool>? counts = null, Action? counted = null)
    : DelegatingHandler(inner)
{
    private int _count;

    public int Count => Volatile.Read(ref _count);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (counts?.Invoke(request) ?? true)
        {
            Interlocked.Increment(ref _count);
            counted?.Invoke();
        }

        return base.SendAsync(request, cancellationToken);
    }
}
