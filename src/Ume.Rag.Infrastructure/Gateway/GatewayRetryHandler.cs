using System.Net;
using Microsoft.Extensions.Options;

namespace Ume.Rag.Infrastructure.Gateway;

/// <summary>
/// On the gateway clients of <c>AddGatewayClient</c> (Decisions "Gateway release 1 and shared Keycloak as built", #390
/// Feature B point 1): a <c>502</c> or <c>504</c> (<c>all_providers_failed</c>: every target within the ceiling failed) is
/// sent once more after <c>Gateway:RetryDelay</c>, as the same request with the same headers, so with the <b>same</b>
/// <c>x-ume-max-residency</c>, never a wider one. Nothing else is retried here: <c>403 max_residency_not_met</c> can never
/// work, a <c>503</c> with <c>Retry-After</c> asks the caller to wait, and a stream that has started is never re-sent (the
/// gateway answers 502/504 before its first byte). It sits inside <see cref="ResidencyCeilingHandler"/>, which has checked
/// the ceiling and the key before the first attempt and checks the final answer's residency. The query embedding leaves
/// this handler out and runs its own loop (knowledge §5.3: per-attempt timeout, 50 ms pause, one retry in all, also for
/// a connection failure and a 503 without <c>Retry-After</c>), deciding on statuses with <see cref="Retries"/>.
/// </summary>
public sealed class GatewayRetryHandler(IOptionsMonitor<GatewayOptions> options) : DelegatingHandler
{
    /// <summary>
    /// The one gateway answer every gateway client sends again (#390): <c>502</c>/<c>504 all_providers_failed</c>, with the
    /// same ceiling. Never <c>403</c>, <c>404</c> or a <c>503</c> with <c>Retry-After</c>.
    /// </summary>
    public static bool Retries(HttpStatusCode status) => status is HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Content is not null)
        {
            // The body is sent twice at most; JSON bodies are small, and a buffered body can be read again.
            await request.Content.LoadIntoBufferAsync(cancellationToken);
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (!Retries(response.StatusCode))
        {
            return response;
        }

        response.Dispose();
        await Task.Delay(options.CurrentValue.RetryDelay, cancellationToken);
        return await base.SendAsync(request, cancellationToken); // the same message: the same ceiling, key and body
    }
}
