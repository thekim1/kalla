using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.Retrieval;

namespace Ume.Rag.Answering;

/// <summary>Query embedding settings (knowledge design §5.3, §5.4); deployment knobs <c>Retrieval:*</c>.</summary>
public sealed class QueryEmbeddingOptions
{
    /// <summary>One attempt's limit (<c>Retrieval:EmbeddingTimeout</c>, 1,500 ms).</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>Both attempts together (2,000 ms).</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromMilliseconds(2000);

    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(50);
    public TimeSpan CacheLifetime { get; set; } = TimeSpan.FromMinutes(5);
    public int CacheEntries { get; set; } = 2000;

    /// <summary>The circuit opens after this many failures within <see cref="CircuitWindow"/>, for <see cref="CircuitOpenFor"/>.</summary>
    public int CircuitFailures { get; set; } = 5;

    public TimeSpan CircuitWindow { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan CircuitOpenFor { get; set; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Embeds a normalised query with the profile's model at the profile's ceiling (knowledge design §5; P1 README §8 O1:
/// the ceiling comes from the profile) on the gateway client <see cref="HttpClientName"/>, whose
/// <see cref="ResidencyCeilingHandler"/> checks key, ceiling and <c>x-ume-residency</c>. L1 cache keyed by
/// (profile, HMAC of the query) so no query text is held as a key; concurrent misses share one call; one retry only for
/// a connection failure or 502/503 without <c>Retry-After</c>; after 5 failures in 30 s the circuit skips the call for 15 s,
/// then one call decides. Every failure is a <see cref="QueryEmbeddingUnavailableException"/> whose reason is the
/// <c>degraded</c> value (§5.5). The query text is never logged.
/// </summary>
public sealed partial class GatewayQueryEmbedder : IDisposable
{
    public const string HttpClientName = "gateway-query";
    public const string RunIdHeader = "x-ume-run-id";
    public const string StepHeader = "x-ume-step";
    public const string PiiHeader = "x-ume-pii";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _clients;
    private readonly QueryEmbeddingOptions _options;
    private readonly byte[] _cacheKey;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly MemoryCache _cache;
    private readonly ConcurrentDictionary<string, Lazy<Task<float[]>>> _inflight = new(StringComparer.Ordinal);
    private readonly Lock _circuit = new();
    private readonly Queue<DateTimeOffset> _failures = new();
    private DateTimeOffset? _openUntil;
    private bool _probing;

    /// <summary><paramref name="cacheKey"/> is the cache key's HMAC secret: <c>Security:CacheKeyPepper</c> in <c>rag-api</c>, a random per-process key in the admin (§5.4).</summary>
    public GatewayQueryEmbedder(IHttpClientFactory clients, QueryEmbeddingOptions options, byte[] cacheKey, TimeProvider time, ILogger<GatewayQueryEmbedder> logger)
    {
        ArgumentNullException.ThrowIfNull(cacheKey);
        if (cacheKey.Length < 32)
        {
            throw new ArgumentException("The cache key needs at least 32 bytes.", nameof(cacheKey));
        }

        _clients = clients;
        _options = options;
        _cacheKey = cacheKey;
        _time = time;
        _logger = logger;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = options.CacheEntries, Clock = null });
    }

    /// <summary>Whether the circuit currently skips calls (for health and the status page).</summary>
    public bool CircuitOpen
    {
        get
        {
            lock (_circuit)
            {
                return _openUntil is { } until && _time.GetUtcNow() < until;
            }
        }
    }

    /// <summary>The query's vector in <paramref name="profile"/>'s space.</summary>
    public async Task<ReadOnlyMemory<float>> EmbedAsync(EmbeddingProfileEntry profile, string normalizedQuery, Guid? runId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrEmpty(normalizedQuery);
        var key = profile.Id.ToString("N") + ":" + QueryMac(normalizedQuery);
        if (_cache.TryGetValue(key, out float[]? cached) && cached is not null)
        {
            return cached;
        }

        if (!TryEnterCircuit())
        {
            throw new QueryEmbeddingUnavailableException(RetrievalDegradation.EmbeddingUnavailable, "The query embedding circuit is open.");
        }

        // The shared call runs on its own time limit, so one caller's cancellation does not fail the others.
        var call = _inflight.GetOrAdd(key, _ => new Lazy<Task<float[]>>(() => CallAndRecordAsync(key, profile, normalizedQuery, runId)));
        return await call.Value.WaitAsync(cancellationToken);
    }

    /// <summary>Hex of the first 16 bytes of HMAC-SHA256(cache key, query): not reversible by dictionary without the key.</summary>
    public string QueryMac(string normalizedQuery) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_cacheKey, Encoding.UTF8.GetBytes(normalizedQuery)).AsSpan(0, 16));

    private async Task<float[]> CallAndRecordAsync(string key, EmbeddingProfileEntry profile, string query, Guid? runId)
    {
        try
        {
            var vector = await CallAsync(profile, query, runId);
            _cache.Set(key, vector, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = _options.CacheLifetime });
            RecordSuccess();
            return vector;
        }
        catch (QueryEmbeddingUnavailableException)
        {
            RecordFailure();
            throw;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    private async Task<float[]> CallAsync(EmbeddingProfileEntry profile, string query, Guid? runId)
    {
        using var total = new CancellationTokenSource(_options.TotalTimeout, _time);
        for (var attempt = 1; ; attempt++)
        {
            using var attemptLimit = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
            attemptLimit.CancelAfter(_options.AttemptTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("v1/embeddings", UriKind.Relative))
            {
                Content = JsonContent.Create(new EmbeddingRequest(profile.GatewayModel, query, "float"), options: Json),
            }.WithMaxResidency(profile.MaxResidency);
            request.Headers.Add(StepHeader, "query-embed");
            if (runId is { } id)
            {
                request.Headers.Add(RunIdHeader, id.ToString());
            }

            bool retryable;
            try
            {
                using var response = await _clients.CreateClient(HttpClientName).SendAsync(request, attemptLimit.Token);
                if (response.IsSuccessStatusCode)
                {
                    return await ReadAsync(response, profile, attemptLimit.Token);
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    LogRefused(_logger, profile.Name, (int)response.StatusCode); // max_residency_not_met: a configuration fault
                }

                retryable = response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable && response.Headers.RetryAfter is null;
                if (!retryable || attempt == 2)
                {
                    throw Unavailable($"The gateway answered {(int)response.StatusCode}.");
                }
            }
            catch (GatewayResidencyException ex)
            {
                throw new QueryEmbeddingUnavailableException(RetrievalDegradation.EmbeddingResidency, "The query embedding came from above the ceiling.", ex);
            }
            catch (GatewayCallRefusedException ex)
            {
                LogNoKey(_logger, profile.Name);
                throw new QueryEmbeddingUnavailableException(RetrievalDegradation.EmbeddingUnavailable, "This service holds no key for the profile's ceiling.", ex);
            }
            catch (HttpRequestException ex) when (attempt == 1 && ex.StatusCode is null && !total.IsCancellationRequested)
            {
                retryable = true; // a connection failure: one retry
            }
            catch (HttpRequestException ex)
            {
                throw Unavailable("The gateway did not answer.", ex);
            }
            catch (OperationCanceledException ex)
            {
                throw Unavailable("The query embedding timed out.", ex);
            }

            await Task.Delay(_options.RetryDelay, _time, total.Token).ConfigureAwait(false);
        }
    }

    private static async Task<float[]> ReadAsync(HttpResponseMessage response, EmbeddingProfileEntry profile, CancellationToken cancellationToken)
    {
        // A redacted or blocked query would embed other text than asked (§5.2).
        if (response.Headers.TryGetValues(PiiHeader, out var pii) && pii.Any(v => v is "redacted" or "blocked"))
        {
            throw Unavailable("The gateway changed the query (x-ume-pii).");
        }

        var body = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(Json, cancellationToken);
        if (body?.Data is not [{ Embedding: { } vector }])
        {
            throw Unavailable("The gateway answered without exactly one embedding.");
        }

        if (vector.Length != profile.Dimensions || vector.Any(f => !float.IsFinite(f)) || vector.All(f => f == 0f))
        {
            throw Unavailable("The query embedding has the wrong shape.");
        }

        return vector;
    }

    private static QueryEmbeddingUnavailableException Unavailable(string message, Exception? inner = null) =>
        new(RetrievalDegradation.EmbeddingUnavailable, message, inner);

    /// <summary>Closed: call. Open: skip until the window ends, then let exactly one probe through.</summary>
    private bool TryEnterCircuit()
    {
        lock (_circuit)
        {
            if (_openUntil is not { } until)
            {
                return true;
            }

            if (_time.GetUtcNow() < until || _probing)
            {
                return false;
            }

            _probing = true;
            return true;
        }
    }

    private void RecordSuccess()
    {
        lock (_circuit)
        {
            _failures.Clear();
            _openUntil = null;
            _probing = false;
        }
    }

    private void RecordFailure()
    {
        lock (_circuit)
        {
            var now = _time.GetUtcNow();
            if (_probing)
            {
                _probing = false;
                _openUntil = now + _options.CircuitOpenFor;
                return;
            }

            _failures.Enqueue(now);
            while (_failures.Count > 0 && now - _failures.Peek() > _options.CircuitWindow)
            {
                _failures.Dequeue();
            }

            if (_failures.Count >= _options.CircuitFailures)
            {
                _failures.Clear();
                _openUntil = now + _options.CircuitOpenFor;
                LogCircuitOpen(_logger, _options.CircuitOpenFor.TotalSeconds);
            }
        }
    }

    public void Dispose() => _cache.Dispose();

    private sealed record EmbeddingRequest(string Model, string Input, [property: JsonPropertyName("encoding_format")] string EncodingFormat);

    private sealed record EmbeddingResponse(IReadOnlyList<EmbeddingItem>? Data);

    private sealed record EmbeddingItem(float[]? Embedding);

    [LoggerMessage(LogLevel.Error, "The gateway refused the query embedding of profile {Profile} with {Status}: no target within the profile's ceiling")]
    private static partial void LogRefused(ILogger logger, string profile, int status);

    [LoggerMessage(LogLevel.Error, "This service holds no gateway key for the ceiling of profile {Profile}")]
    private static partial void LogNoKey(ILogger logger, string profile);

    [LoggerMessage(LogLevel.Warning, "Query embedding failed repeatedly; hybrid searches go keyword-only for {Seconds} s")]
    private static partial void LogCircuitOpen(ILogger logger, double seconds);
}
