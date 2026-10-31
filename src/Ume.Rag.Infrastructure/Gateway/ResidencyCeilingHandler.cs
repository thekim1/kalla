using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain;
using Ume.Rag.Infrastructure.Security;

namespace Ume.Rag.Infrastructure.Gateway;

/// <summary>
/// On every gateway <see cref="HttpClient"/> (chat design §8.1, identity design §4.7, engine design §7.7): refuses,
/// before anything is sent, a request without exactly one valid <c>x-ume-max-residency</c>, or whose key is not this
/// process's key of that level, and adds the level's key when the caller set none (a process without the key of a level,
/// such as the worker for <c>External</c>, cannot call at it). On a successful answer it checks
/// <c>x-ume-residency</c> against the ceiling before the caller reads a byte: a missing header or a residency above the
/// ceiling disposes the response (which cancels a stream at the gateway), raises event 5003 and throws. <c>GET /v1/models</c>
/// is the exception: no model answers it, so it carries no residency (the gateway's <c>GatewayRequestHandler</c>).
/// </summary>
public sealed class ResidencyCeilingHandler(IOptionsMonitor<GatewayOptions> options, ILoggerFactory loggers) : DelegatingHandler
{
    private readonly ILogger _security = SecurityEvents.CreateLogger(loggers);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ceiling = Ceiling(request);
        var key = options.CurrentValue.Keys.GetValueOrDefault(ceiling);
        if (string.IsNullOrEmpty(key))
        {
            throw new GatewayCallRefusedException($"This service holds no gateway key for the ceiling {ResidencyCeiling.ToHeader(ceiling)}.");
        }

        if (request.Headers.Authorization is { } authorization)
        {
            if (authorization.Scheme != "Bearer" || !string.Equals(authorization.Parameter, key, StringComparison.Ordinal))
            {
                throw new GatewayCallRefusedException($"The request's gateway key is not the key of the ceiling {ResidencyCeiling.ToHeader(ceiling)}.");
            }
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return response; // 403 max_residency_not_met, 503 + Retry-After …: the caller maps them (identity design §4.7)
        }

        if (IsModelList(request))
        {
            return response; // a model list carries no model output; the gateway sets x-ume-residency only when a deployment answers
        }

        var observed = response.Headers.TryGetValues(GatewayHeaders.Residency, out var values) ? string.Join(",", values) : null;
        if (observed is not null && ResidencyCeiling.TryParse(observed, out var residency) && residency <= ceiling)
        {
            return response;
        }

        var requestId = response.Headers.TryGetValues(GatewayHeaders.RequestId, out var ids) ? ids.FirstOrDefault() : null;
        response.Dispose();
        SecurityEvents.ResidencyRefused(_security, ResidencyCeiling.ToHeader(ceiling), observed is null ? "(missing)" : SecurityEvents.Printable(observed),
            request.RequestUri?.AbsolutePath ?? "", requestId is null ? null : SecurityEvents.Printable(requestId));
        throw new GatewayResidencyException(ceiling, observed);
    }

    /// <summary><c>GET /v1/models</c> (V8's check, gateway-changes §3.4): filtered by the ceiling, answered by no model.</summary>
    private static bool IsModelList(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get && request.RequestUri is { } uri &&
        (uri.IsAbsoluteUri ? uri.AbsolutePath : "/" + uri.OriginalString.TrimStart('/')).EndsWith("/v1/models", StringComparison.Ordinal);

    private static Residency Ceiling(HttpRequestMessage request)
    {
        if (!request.Headers.TryGetValues(GatewayHeaders.MaxResidency, out var values))
        {
            throw new GatewayCallRefusedException("A gateway call must carry an explicit x-ume-max-residency.");
        }

        var list = values.ToList();
        if (list.Count != 1 || list[0].Contains(',', StringComparison.Ordinal) || !ResidencyCeiling.TryParse(list[0], out var ceiling))
        {
            throw new GatewayCallRefusedException("x-ume-max-residency must be exactly one of OnPrem, Eu, External.");
        }

        return ceiling;
    }
}

/// <summary>A gateway call refused by this process before sending (programming error: no ceiling, wrong key).</summary>
public sealed class GatewayCallRefusedException(string message) : InvalidOperationException(message);

/// <summary>The gateway answered from a residency above the call's ceiling, or did not say; the answer was discarded.</summary>
public sealed class GatewayResidencyException(Residency ceiling, string? observed)
    : HttpRequestException($"The gateway answered with residency '{observed ?? "(missing)"}' above the ceiling {ResidencyCeiling.ToHeader(ceiling)}; the answer was discarded.")
{
    public Residency Ceiling { get; } = ceiling;
    public string? Observed { get; } = observed;
}

/// <summary>Setting the ceiling of a gateway request.</summary>
public static class GatewayRequestExtensions
{
    public static HttpRequestMessage WithMaxResidency(this HttpRequestMessage request, Residency ceiling)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.Remove(GatewayHeaders.MaxResidency);
        request.Headers.Add(GatewayHeaders.MaxResidency, ResidencyCeiling.ToHeader(ceiling));
        return request;
    }
}
