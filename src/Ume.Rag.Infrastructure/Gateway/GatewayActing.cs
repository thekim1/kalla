using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ume.Rag.Infrastructure.Gateway;

/// <summary>
/// What a gateway call is made for (cost-attribution §4.1): the object's assigned gateway key, which the call runs as once
/// the gateway acts as keys (<see cref="GatewayFeatures.ActAsKey"/>), and the step and object named by event 5011.
/// Carried on the request (<see cref="GatewayRequestExtensions.For"/>) to <see cref="ResidencyCeilingHandler"/>.
/// </summary>
public sealed record GatewayCall(Guid? GatewayKeyId, string? Step = null, Guid? AssistantId = null, Guid? CollectionId = null)
{
    internal static readonly HttpRequestOptionsKey<GatewayCall> OptionsKey = new("ume.gateway.call");
}

/// <summary>
/// The call of the work in progress (a chat turn, a Knowledge API search, a test-panel turn), for every gateway request made
/// inside it that carries none of its own: set once where the object is known, so the clients below need no parameter.
/// An async-local value: it flows into the requests awaited inside the scope and ends with it.
/// </summary>
public static class GatewayCallScope
{
    private static readonly AsyncLocal<GatewayCall?> Ambient = new();

    public static GatewayCall? Current => Ambient.Value;

    /// <summary>Makes <paramref name="call"/> current until the result is disposed (the previous one comes back then).</summary>
    public static IDisposable Begin(GatewayCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var previous = Ambient.Value;
        Ambient.Value = call;
        return new Restore(previous);
    }

    private sealed class Restore(GatewayCall? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}

/// <summary>
/// The gateway's <c>features</c> as this service last read them from its own <c>GET /v1/ume/context</c> (identity §4.7,
/// gateway-changes §5.2), held in memory. Empty until the first answer, so no call acts as a key before then.
/// </summary>
public sealed class GatewayFeatures
{
    public const string ActAsKeyFeature = "act-as-key";
    public const string RunMetadataFeature = "run-metadata";

    private volatile HashSet<string> _features = new(StringComparer.Ordinal);

    public IReadOnlySet<string> Current => _features;

    /// <summary>Calls for an object with a gateway key send <c>x-ume-on-behalf-of</c> (phase 2).</summary>
    public bool ActAsKey => _features.Contains(ActAsKeyFeature);

    public void Update(IEnumerable<string> features) => _features = new HashSet<string>(features, StringComparer.Ordinal);
}

/// <summary>
/// Reads this service's own <c>GET /v1/ume/context</c> with its credential key at start and every
/// <see cref="GatewayOptions.ContextInterval"/>, with <c>If-None-Match</c>, into <see cref="GatewayFeatures"/> (identity §4.7).
/// Off without a gateway or a key. A failed read keeps the last features.
/// </summary>
public sealed partial class GatewayContextReader(
    IHttpClientFactory clients, GatewayFeatures features, IOptions<GatewayOptions> options, TimeProvider time, ILogger<GatewayContextReader> logger) : BackgroundService
{
    public const string HttpClientName = "gateway-context";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private string? _etag;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.BaseUrl) || string.IsNullOrEmpty(options.Value.Key))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await ReadAsync(stoppingToken);
            await Task.Delay(options.Value.ContextInterval, time, stoppingToken);
        }
    }

    /// <summary>One read; never throws except on shutdown. Returns whether the features are current.</summary>
    public async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("v1/ume/context", UriKind.Relative));
            if (_etag is not null)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
            }

            using var response = await clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return true;
            }

            response.EnsureSuccessStatusCode();
            var context = await response.Content.ReadFromJsonAsync<ContextFeatures>(Json, cancellationToken);
            features.Update(context?.Gateway?.Features ?? []);
            _etag = response.Headers.ETag?.ToString();
            LogRead(logger, string.Join(", ", features.Current.Order(StringComparer.Ordinal)));
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogFailed(logger, ex.GetType().Name, (ex as HttpRequestException)?.StatusCode is { } status ? (int)status : null);
            return false;
        }
    }

    private sealed record ContextFeatures(ContextGatewayPart? Gateway);

    private sealed record ContextGatewayPart(IReadOnlyList<string>? Features);

    [LoggerMessage(LogLevel.Information, "Gateway features: {Features}")]
    private static partial void LogRead(ILogger logger, string features);

    [LoggerMessage(LogLevel.Warning, "Reading the gateway context failed ({Error}, HTTP {Status}); the last features are kept")]
    private static partial void LogFailed(ILogger logger, string error, int? status);
}

/// <summary>
/// The gateway refused the object's assigned key on a call made as it (gateway-changes §5.7): <c>403 delegation_not_allowed</c>,
/// <c>403 key_disabled</c>, <c>403 key_revoked</c>/<c>key_expired</c> (gateway v0.10.1) or <c>402 budget_exceeded</c> (with the gateway's
/// <see cref="GatewayMessage"/>). Final for the call; never retried. Callers map it (B-44, A-34).
/// </summary>
public sealed class GatewayKeyRefusedException(int status, string code, string? gatewayMessage, Guid gatewayKeyId)
    : HttpRequestException($"The gateway refused the key {gatewayKeyId}: {status} {code}.", null, (HttpStatusCode)status)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string? GatewayMessage { get; } = gatewayMessage;
    public Guid GatewayKeyId { get; } = gatewayKeyId;

    /// <summary>The refusals that mean the object's key cannot be used now (gateway-changes §5.7).</summary>
    public static bool IsKeyRefusal(int status, string? code) => (status, code) switch
    {
        // From gateway v0.10.1 every refusal of the acted key is 403; a 401 is always the credential key's own (#580).
        (403, "delegation_not_allowed" or "key_disabled" or "key_revoked" or "key_expired") => true,
        (402, _) => true,
        _ => false,
    };
}

/// <summary>
/// A <c>2xx</c> or <c>304</c> answer to a call made as an assigned key without the key's echo, or with another id (event
/// 5011): the answer was discarded before use.
/// </summary>
public sealed class GatewayDelegationEchoException(Guid gatewayKeyId, string? echo)
    : HttpRequestException($"The gateway did not echo the key {gatewayKeyId} (echo: '{echo ?? "(missing)"}'); the answer was discarded.")
{
    public Guid GatewayKeyId { get; } = gatewayKeyId;
    public string? Echo { get; } = echo;
}
