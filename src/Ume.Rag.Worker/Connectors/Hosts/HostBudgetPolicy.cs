using Ume.Rag.Worker.Connectors.Robots;

namespace Ume.Rag.Worker.Connectors.Hosts;

/// <summary>
/// A <c>content."CrawlHosts"</c> row (data-model §2.2, connectors design §6.1): the politeness budget of one host, shared
/// by every source, preview and level. <c>RobotsBlobPath</c> joins with the database-backed robots cache (J2). The
/// Npgsql store maps <c>numeric</c> columns to <see cref="double"/> and <c>Kind</c>/<c>RobotsStatus</c> to their lower-case text.
/// </summary>
public sealed record CrawlHostState
{
    /// <summary>Lower-case A-label host with <c>:port</c> when not the default (the URL's authority).</summary>
    public required string Host { get; init; }

    public required HostKind Kind { get; init; }

    public required double RequestsPerSecond { get; init; }

    public required int MaxConnections { get; init; }

    public DateTimeOffset NextAllowedAt { get; init; }

    public RobotsStatus? RobotsStatus { get; init; }

    public DateTimeOffset? RobotsFetchedAt { get; init; }

    /// <summary>The robots.txt <c>Crawl-delay</c> as read (honoured only when it lowers our rate, capped at 60 s).</summary>
    public int? CrawlDelaySeconds { get; init; }

    public double BackoffFactor { get; init; } = 1;

    public DateTimeOffset? PausedUntil { get; init; }

    public int? LatencyEwmaMs { get; init; }

    /// <summary>A platform admin's block: no slot is ever granted.</summary>
    public DateTimeOffset? BlockedAt { get; init; }
}

/// <summary>What one response tells the budget (§6.4).</summary>
public enum HostResponseKind
{
    /// <summary>2xx or 304: the back-off decays.</summary>
    Success,

    /// <summary>429 or 503: wait for <c>Retry-After</c>, double the back-off.</summary>
    RateLimited,

    /// <summary>5xx, a timeout or a 404: counts towards the circuit breaker.</summary>
    Failure,

    /// <summary>Anything else (other 4xx, refusals): latency only.</summary>
    Neutral,
}

/// <param name="Kind">The response's class.</param>
/// <param name="Latency">Time to the response headers, or null when no response came.</param>
/// <param name="RetryAfter">A 429/503's <c>Retry-After</c>.</param>
public sealed record HostResponse(HostResponseKind Kind, TimeSpan? Latency, TimeSpan? RetryAfter = null);

/// <summary>
/// The budget arithmetic of connectors design §6.3–§6.4, pure so the database statement and the in-memory store of the
/// tests compute the same thing: the interval, the slot taken from <c>NextAllowedAt</c>, back-off and latency.
/// </summary>
public static class HostBudgetPolicy
{
    public const double MunicipalRequestsPerSecond = 10;
    public const int MunicipalConnections = 4;
    public const double ExternalRequestsPerSecond = 1;
    public const int ExternalConnections = 1;

    public const double MaxBackoff = 16;
    public const double BackoffDecay = 0.95;

    /// <summary>Weight of the newest response in <c>LatencyEwmaMs</c>.</summary>
    public const double LatencyWeight = 0.2;

    public static readonly TimeSpan MaxCrawlDelay = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>A <c>Retry-After</c> above this pauses the host for the run instead (§6.4).</summary>
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);

    /// <summary>A slot further away than this is not waited for in process: the job requeues without an attempt at the slot.</summary>
    public static readonly TimeSpan MaxInProcessWait = TimeSpan.FromSeconds(1);

    /// <summary>The default budget of a host of <paramref name="kind"/>, with the platform admin's override applied.</summary>
    public static (double RequestsPerSecond, int MaxConnections) Budget(HostKind kind, HostOverride? hostOverride)
    {
        var (rate, connections) = kind == HostKind.Municipal
            ? (MunicipalRequestsPerSecond, MunicipalConnections)
            : (ExternalRequestsPerSecond, ExternalConnections);
        return (hostOverride?.RequestsPerSecond is > 0.0 and var r ? r : rate, hostOverride?.MaxConnections is > 0 and var c ? c : connections);
    }

    /// <summary>
    /// <c>max(1/rate, Crawl-delay, latency) × BackoffFactor</c>, where latency is <c>LatencyEwma / 4</c> on municipal hosts
    /// and <c>LatencyEwma</c> on external ones; <c>Crawl-delay</c> only lowers the rate and counts up to 60 s.
    /// </summary>
    public static TimeSpan Interval(CrawlHostState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var seconds = 1 / state.RequestsPerSecond;
        if (state.CrawlDelaySeconds is > 0 and var delay)
        {
            seconds = Math.Max(seconds, Math.Min(delay, MaxCrawlDelay.TotalSeconds));
        }

        if (state.LatencyEwmaMs is > 0 and var latency)
        {
            seconds = Math.Max(seconds, latency / 1000.0 / (state.Kind == HostKind.Municipal ? 4 : 1));
        }

        return TimeSpan.FromSeconds(seconds * Math.Max(1, state.BackoffFactor));
    }

    /// <summary>
    /// The slot statement of §6.3: no slot for a blocked or paused host; otherwise
    /// <c>NextAllowedAt = greatest(NextAllowedAt, now) + interval</c> and the slot is the value before the interval.
    /// </summary>
    public static (CrawlHostState State, DateTimeOffset? Slot) TakeSlot(CrawlHostState state, TimeSpan interval, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.BlockedAt is not null || (state.PausedUntil is { } paused && paused >= now))
        {
            return (state, null);
        }

        var next = (state.NextAllowedAt > now ? state.NextAllowedAt : now) + interval;
        return (state with { NextAllowedAt = next }, next - interval);
    }

    /// <summary>
    /// The response's effect (§6.4): a rate limit sets <c>NextAllowedAt = now + Retry-After</c> (default 60 s) and doubles
    /// the back-off up to 16, or pauses the host when <c>Retry-After</c> exceeds 1 h; a success decays the back-off by 0.95
    /// down to 1; every response with a latency feeds <c>LatencyEwmaMs</c>.
    /// </summary>
    public static CrawlHostState Apply(CrawlHostState state, HostResponse response, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(response);
        var updated = state;
        if (response.Latency is { } latency)
        {
            var ms = latency.TotalMilliseconds;
            updated = updated with
            {
                LatencyEwmaMs = (int)Math.Round(updated.LatencyEwmaMs is { } ewma ? ((1 - LatencyWeight) * ewma) + (LatencyWeight * ms) : ms),
            };
        }

        switch (response.Kind)
        {
            case HostResponseKind.Success:
                return updated with { BackoffFactor = Math.Max(1, updated.BackoffFactor * BackoffDecay) };
            case HostResponseKind.RateLimited:
                var wait = response.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero ? retryAfter : DefaultRetryAfter;
                var backoff = Math.Min(MaxBackoff, updated.BackoffFactor * 2);
                if (wait > MaxRetryAfter)
                {
                    return updated with { BackoffFactor = backoff, PausedUntil = now + wait };
                }

                var until = now + wait;
                return updated with { BackoffFactor = backoff, NextAllowedAt = until > updated.NextAllowedAt ? until : updated.NextAllowedAt };
            default:
                return updated;
        }
    }
}
