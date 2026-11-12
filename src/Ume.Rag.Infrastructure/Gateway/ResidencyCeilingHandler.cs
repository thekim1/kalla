using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain;
using Ume.Rag.Infrastructure.Security;

namespace Ume.Rag.Infrastructure.Gateway;

/// <summary>
/// On every gateway <see cref="HttpClient"/> (chat design §8.1, identity design §4.7, engine design §7.7): refuses,
/// before anything is sent, a request without exactly one valid <c>x-ume-max-residency</c>, one whose ceiling is above
/// the service's maximum (<see cref="GatewayOptions.Maximum"/>: the worker's is <c>Eu</c>; logged), or one whose key is
/// not this service's, and adds the service's credential key when the caller set none (<see cref="GatewayOptions.KeyFor"/>). On a successful answer it checks
/// <c>x-ume-residency</c> against the ceiling before the caller reads a byte: a missing header or a residency above the
/// ceiling disposes the response (which cancels a stream at the gateway), raises event 5003 and throws. <c>GET /v1/models</c>
/// is the exception: no model answers it, so it carries no residency (the gateway's <c>GatewayRequestHandler</c>).
/// <c>GET /v1/ume/context</c> needs no ceiling either: it gets the service's credential key and nothing else is checked.
/// </summary>
public sealed partial class ResidencyCeilingHandler(IOptionsMonitor<GatewayOptions> options, ILoggerFactory loggers) : DelegatingHandler
{
    private readonly ILogger _security = SecurityEvents.CreateLogger(loggers);
    private readonly ILogger _log = loggers.CreateLogger<ResidencyCeilingHandler>();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var gateway = options.CurrentValue;
        if (IsContext(request) && !request.Headers.Contains(GatewayHeaders.MaxResidency))
        {
            // The context (gateway-changes §5.5) is about the key, not a model: authenticated, no ceiling, no residency.
            AddCredentialKey(request, gateway.Key ?? throw new GatewayKeyMissingException(gateway.Maximum));
            return await base.SendAsync(request, cancellationToken);
        }

        var ceiling = Ceiling(request);
        var key = gateway.KeyFor(ceiling);
        if (key is null)
        {
            if (!string.IsNullOrEmpty(gateway.Key))
            {
                LogAboveMaximum(_log, ResidencyCeiling.ToHeader(ceiling), ResidencyCeiling.ToHeader(gateway.Maximum), request.RequestUri?.AbsolutePath ?? "");
            }

            throw new GatewayKeyMissingException(ceiling);
        }

        AddCredentialKey(request, key);
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

    [LoggerMessage(LogLevel.Warning,
        "Gateway call at {Ceiling} to {Path} refused before sending: above this service's maximum {Maximum} (Gateway:MaxResidency, identity design §4.7)")]
    private static partial void LogAboveMaximum(ILogger logger, string ceiling, string maximum, string path);

    /// <summary>Adds the service's key, or refuses a request that carries another key.</summary>
    private static void AddCredentialKey(HttpRequestMessage request, string key)
    {
        if (request.Headers.Authorization is { } authorization)
        {
            if (authorization.Scheme != "Bearer" || !string.Equals(authorization.Parameter, key, StringComparison.Ordinal))
            {
                throw new GatewayCallRefusedException("The request's gateway key is not this service's key.");
            }
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    /// <summary><c>GET /v1/ume/context</c>: what the presented key is and may use; answered by no model.</summary>
    private static bool IsContext(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get && request.RequestUri is { } uri &&
        (uri.IsAbsoluteUri ? uri.AbsolutePath : "/" + uri.OriginalString.TrimStart('/')).EndsWith("/v1/ume/context", StringComparison.Ordinal);

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
public class GatewayCallRefusedException(string message) : InvalidOperationException(message);

/// <summary>
/// This service may not call at the ceiling (a configuration gap, not a programming error): above its maximum, e.g. a
/// public assistant at <c>External</c> while <c>Gateway:MaxResidency</c> is <c>Eu</c>, or with no key at all (#237).
/// </summary>
public sealed class GatewayKeyMissingException(Residency ceiling)
    : GatewayCallRefusedException($"This service holds no gateway key for the ceiling {ResidencyCeiling.ToHeader(ceiling)}.")
{
    public Residency Ceiling { get; } = ceiling;
}

/// <summary>The gateway answered from a residency above the call's ceiling, or did not say; the answer was discarded.</summary>
public sealed class GatewayResidencyException(Residency ceiling, string? observed)
    : HttpRequestException($"The gateway answered with residency '{observed ?? "(missing)"}' above the ceiling {ResidencyCeiling.ToHeader(ceiling)}; the answer was discarded.")
{
    public Residency Ceiling { get; } = ceiling;
    public string? Observed { get; } = observed;
}

/// <summary>Setting the ceiling of a gateway request, and asking for a stream's cost.</summary>
public static class GatewayRequestExtensions
{
    public static HttpRequestMessage WithMaxResidency(this HttpRequestMessage request, Residency ceiling)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.Remove(GatewayHeaders.MaxResidency);
        request.Headers.Add(GatewayHeaders.MaxResidency, ResidencyCeiling.ToHeader(ceiling));
        return request;
    }

    /// <summary>
    /// <c>x-ume-stream-cost: 1</c> (chat design §8.1, gateway-changes §5.4): the streamed answer ends with the cost comment,
    /// which <see cref="SseReader"/> takes; a gateway without the feature ignores the header.
    /// </summary>
    public static HttpRequestMessage WithStreamCost(this HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.Remove(GatewayHeaders.StreamCost);
        request.Headers.Add(GatewayHeaders.StreamCost, "1");
        return request;
    }
}
