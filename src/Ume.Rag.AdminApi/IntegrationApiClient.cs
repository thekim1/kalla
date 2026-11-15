using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Ume.Rag.Infrastructure.Gateway;

namespace Ume.Rag.AdminApi;

/// <summary>
/// The gateway's Integration API (gateway phase 3; gateway-changes §5.6; admin design §4.5, §8.6; B-45): an assigned key's
/// usage per period and step and its budgets, read with rag-admin's credential key (<c>usage.read</c>) from the address
/// the gateway's context names (<see cref="GatewayFeatures.IntegrationApiUrl"/>, ending in <c>/integration/v1</c>; M-17
/// C11). Each answer is cached 5 minutes per key and query (the gateway allows 60 requests a minute per key). Without an
/// address nothing is asked and the answers are null; a refusal (<c>401</c>, <c>403</c> without the scope, <c>429</c>) or a
/// failure is null too, with its reason in <see cref="LastError"/> for the <i>Gateway</i> page.
/// </summary>
public sealed partial class IntegrationApiClient(IHttpClientFactory clients, IOptionsMonitor<GatewayOptions> options, GatewayFeatures features, IMemoryCache cache,
    TimeProvider time, ILogger<IntegrationApiClient> logger)
{
    public const string HttpClientName = "gateway-integration";
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    /// <summary>One call's limit (#701): a read-back that takes longer is <c>unavailable</c>, the page shows no figures.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private IntegrationApiError? _lastError;

    /// <summary>Whether the gateway's context names the Integration API's address (an absolute URL).</summary>
    public bool Configured => Uri.TryCreate(features.IntegrationApiUrl, UriKind.Absolute, out _);

    /// <summary>The last refusal or failure (null after a success or before any call), shown on the <i>Gateway</i> page.</summary>
    public IntegrationApiError? LastError => Volatile.Read(ref _lastError);

    /// <summary><paramref name="keyId"/>'s usage per period and step (<c>groupBy=step</c>), or null.</summary>
    public Task<IntegrationUsage?> UsageAsync(Guid keyId, DateOnly from, DateOnly to, string granularity, CancellationToken ct) =>
        GetAsync<IntegrationUsage>(FormattableString.Invariant(
            $"usage?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&granularity={granularity}&groupBy=step&keyId={keyId}"), ct);

    /// <summary>The budgets that apply to <paramref name="keyId"/>, or null.</summary>
    public Task<IntegrationBudgets?> BudgetsAsync(Guid keyId, CancellationToken ct) =>
        GetAsync<IntegrationBudgets>(FormattableString.Invariant($"budgets?keyId={keyId}"), ct);

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        // Read once: a context read may change the address meanwhile.
        var address = features.IntegrationApiUrl;
        if (!Uri.TryCreate(address, UriKind.Absolute, out _))
        {
            return null;
        }

        var baseUrl = address.TrimEnd('/') + "/";
        var cacheKey = "integration:" + baseUrl + path;
        if (cache.TryGetValue(cacheKey, out T? cached))
        {
            return cached;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseUrl), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.CurrentValue.Key);
        try
        {
            using var response = await clients.CreateClient(HttpClientName).SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                Fail((int)response.StatusCode, response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "unauthorized",
                    HttpStatusCode.Forbidden => "scope_missing",
                    HttpStatusCode.TooManyRequests => "rate_limited",
                    HttpStatusCode.NotFound => "not_found",
                    _ => "failed",
                });
                return null;
            }

            var value = await response.Content.ReadFromJsonAsync<T>(Json, ct);
            Volatile.Write(ref _lastError, null);
            cache.Set(cacheKey, value, CacheFor);
            return value;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Fail(null, "unavailable");
            return null;
        }
    }

    private void Fail(int? status, string reason)
    {
        Volatile.Write(ref _lastError, new IntegrationApiError(reason, status, time.GetUtcNow()));
        LogFailed(logger, status, reason);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The gateway's Integration API refused or failed (HTTP {Status}, {Reason}); no read-back this time")]
    private static partial void LogFailed(ILogger logger, int? status, string reason);
}

/// <summary>Why the Integration API gave nothing: <c>unauthorized</c> (401), <c>scope_missing</c> (403), <c>rate_limited</c> (429), <c>not_found</c>, <c>failed</c>, <c>unavailable</c>.</summary>
public sealed record IntegrationApiError(string Reason, int? Status, DateTimeOffset At);

/// <summary>The gateway's <c>IntegrationUsage</c> (gateway-changes §5.6).</summary>
public sealed record IntegrationUsage(DateOnly From, DateOnly To, string Granularity, IReadOnlyList<string> GroupBy, IReadOnlyList<IntegrationUsageRow> Items);

/// <summary>The gateway's <c>IntegrationUsageRow</c>: a dimension not grouped by is null.</summary>
public sealed record IntegrationUsageRow(DateOnly Period, Guid? KeyId, string? KeyName, string? Model, string? Step, long Requests, long SuccessfulRequests,
    long InputTokens, long CachedInputTokens, long OutputTokens, decimal CostUsd);

/// <summary>The gateway's <c>IntegrationBudgets</c>.</summary>
public sealed record IntegrationBudgets(Guid KeyId, IReadOnlyList<IntegrationBudget> Items);

/// <summary>The gateway's <c>IntegrationBudget</c>: <c>scope</c> <c>VirtualKey</c>, <c>Team</c> or <c>Department</c>; <c>unit</c> <c>Usd</c> or <c>Tokens</c>.</summary>
public sealed record IntegrationBudget(Guid Id, string Scope, Guid ScopeId, string Unit, decimal Limit, string? Residency, string Period, DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd, decimal Spent, decimal PercentUsed, bool Exhausted);
