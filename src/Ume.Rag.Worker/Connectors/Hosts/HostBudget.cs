using System.Collections.Concurrent;
using Ume.Rag.Worker.Connectors.Robots;

namespace Ume.Rag.Worker.Connectors.Hosts;

/// <summary>
/// <c>content."CrawlHosts"</c> (data-model §2.2). Each method is one atomic statement on the host's row; the arithmetic is
/// <see cref="HostBudgetPolicy"/>'s, so the Npgsql store (after J2) and the tests' in-memory store agree:
/// <see cref="TryTakeSlotAsync"/> is the slot <c>UPDATE … RETURNING</c> of connectors design §6.3 with the database clock.
/// </summary>
public interface ICrawlHostStore
{
    /// <summary>The host's row, inserted with <paramref name="initial"/> when absent; budget columns follow the current kind and override.</summary>
    Task<CrawlHostState> EnsureAsync(CrawlHostState initial, CancellationToken cancellationToken);

    /// <summary>
    /// Takes the next slot when it is at most <paramref name="maxWait"/> away, else returns the free time untaken
    /// (<see cref="HostBudgetPolicy.TakeSlot"/>); null when the host is blocked, paused or unknown.
    /// </summary>
    Task<SlotTake?> TryTakeSlotAsync(string host, TimeSpan interval, TimeSpan maxWait, CancellationToken cancellationToken);

    /// <summary>Applies one response (<see cref="HostBudgetPolicy.Apply"/>) and returns the row after it.</summary>
    Task<CrawlHostState> RecordResponseAsync(string host, HostResponse response, CancellationToken cancellationToken);

    /// <summary>Sets <c>PausedUntil</c> (circuit breaker, §6.4) unless a later pause is already set.</summary>
    Task PauseAsync(string host, DateTimeOffset until, CancellationToken cancellationToken);

    /// <summary>Records the robots.txt fetch: <c>RobotsStatus</c>, <c>RobotsFetchedAt</c>, <c>CrawlDelaySeconds</c>.</summary>
    Task RecordRobotsAsync(string host, RobotsStatus status, DateTimeOffset fetchedAt, int? crawlDelaySeconds, CancellationToken cancellationToken);
}

/// <summary>The outcome of asking for a host slot.</summary>
public abstract record HostSlot
{
    private HostSlot()
    {
    }

    /// <summary>The request may go now; complete the lease with the response, then dispose it.</summary>
    public sealed record Granted(HostLease Lease) : HostSlot;

    /// <summary>Not yet: requeue the job without an attempt after <paramref name="Delay"/> (<c>politeness</c>, §6.3).</summary>
    public sealed record Later(TimeSpan Delay, string Reason) : HostSlot;

    /// <summary>
    /// No slot: <c>crawl.host_blocked</c> (a platform admin's block) or <c>crawl.host_paused</c> until
    /// <paramref name="Until"/> (back-off or breaker, §6.4).
    /// </summary>
    public sealed record Refused(string Reason, DateTimeOffset? Until) : HostSlot;
}

/// <summary>What a completed request did to the host.</summary>
/// <param name="PausedUntil">Set when this response paused the host (a <c>Retry-After</c> above 1 h, or the breaker).</param>
/// <param name="Reason"><c>crawl.retry_after</c> or <c>crawl.breaker</c> when paused.</param>
public sealed record HostVerdict(DateTimeOffset? PausedUntil, string? Reason)
{
    public static HostVerdict Running { get; } = new(null, null);
}

/// <summary>
/// One in-flight request's hold on its host: a connection of the in-process cap (§6.3), or none for a request made under a
/// connection the caller already holds (<see cref="HostBudget.PaceAsync"/>).
/// </summary>
public sealed class HostLease : IDisposable
{
    private readonly HostBudget _budget;
    private readonly SemaphoreSlim? _connections;
    private int _released;

    internal HostLease(HostBudget budget, string host, SemaphoreSlim? connections)
    {
        _budget = budget;
        Host = host;
        _connections = connections;
    }

    public string Host { get; }

    /// <summary>Records the response on the host's row and the breaker window.</summary>
    public Task<HostVerdict> CompleteAsync(HostResponse response, CancellationToken cancellationToken) =>
        _budget.CompleteAsync(Host, response, cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _connections?.Release();
        }
    }
}

/// <summary>
/// The per-host politeness budget every crawl request takes before it is sent (connectors design §6.0, §6.3–§6.4): a slot
/// from <c>CrawlHosts.NextAllowedAt</c> spaced by the host's interval (shared by every source and level), then a
/// connection of the host's in-process cap (P1: one worker instance; the Redis lease is P2). A slot more than 1 s away is
/// neither taken nor waited for: the job requeues at it without an attempt and the row is left as it was, so waiting jobs
/// never push the slots further out (a livelock on a busy 1 req/s host, pilot crawl 2026-11-01). Responses feed back-off,
/// latency and the circuit breaker (by default ≥ 50 of the last 100 responses 5xx, timeouts or 404 → paused 30 minutes). The breaker
/// window and the connection semaphores are per instance; the row is the authority for slots and pauses. Every number
/// comes from the host's politeness profile (§6.6, <see cref="CrawlPolicy.ProfileOf"/>): rate, connections, the interval's
/// terms, the back-off caps and the breaker's threshold and pause, or no breaker.
/// </summary>
public sealed class HostBudget(ICrawlHostStore store, ICrawlPolicySource policy, TimeProvider time)
{
    public const string Blocked = "crawl.host_blocked";
    public const string Paused = "crawl.host_paused";
    public const string Politeness = "politeness";
    public const string RetryAfterPause = "crawl.retry_after";
    public const string BreakerPause = "crawl.breaker";

    public const int BreakerWindow = 100;

    /// <summary>How long a request waits for a free connection before its job requeues (slot pacing makes this rare).</summary>
    public static readonly TimeSpan ConnectionWait = TimeSpan.FromSeconds(10);

    // By host and cap: a changed maxConnections takes effect with a new semaphore, the old one drains with its leases.
    private readonly ConcurrentDictionary<(string Host, int Cap), SemaphoreSlim> _connections = new();
    private readonly ConcurrentDictionary<string, BreakerWindowState> _windows = new(StringComparer.Ordinal);

    /// <summary>The host's row with the kind and budget the current settings give it.</summary>
    public async Task<CrawlHostState> EnsureHostAsync(string host, CancellationToken cancellationToken) =>
        (await EnsureAsync(host, cancellationToken)).State;

    /// <summary>Keeps the host's robots.txt facts on its row: <c>Crawl-delay</c> feeds the interval (§5.1).</summary>
    public Task RecordRobotsAsync(string host, RobotsEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var delay = entry.Rules.CrawlDelaySeconds is { } seconds ? (int?)Math.Ceiling(seconds) : null;
        return store.RecordRobotsAsync(host, entry.Status, entry.FetchedAt, delay, cancellationToken);
    }

    public Task<HostSlot> AcquireAsync(string host, CancellationToken cancellationToken) => AcquireAsync(host, connection: true, cancellationToken);

    /// <summary>
    /// A slot of the host's pacing without a connection of its cap: a further request made while the caller holds the
    /// host's lease (the requests a rendered page makes to its own host, connectors design §8.3), so it neither waits for
    /// its own connection nor goes unpaced. Complete the lease with the response like any other.
    /// </summary>
    public Task<HostSlot> PaceAsync(string host, CancellationToken cancellationToken) => AcquireAsync(host, connection: false, cancellationToken);

    private async Task<HostSlot> AcquireAsync(string host, bool connection, CancellationToken cancellationToken)
    {
        var (state, profile) = await EnsureAsync(host, cancellationToken);
        var now = time.GetUtcNow();
        if (state.BlockedAt is not null)
        {
            return new HostSlot.Refused(Blocked, null);
        }

        if (state.PausedUntil is { } paused && paused >= now)
        {
            return new HostSlot.Refused(Paused, paused);
        }

        var interval = HostBudgetPolicy.Interval(state, profile);
        if (await store.TryTakeSlotAsync(host, interval, HostBudgetPolicy.MaxInProcessWait, cancellationToken) is not { } slot)
        {
            return new HostSlot.Refused(Paused, null);
        }

        var wait = slot.At - time.GetUtcNow();
        if (!slot.Taken)
        {
            // The database clock decided the free time is more than MaxInProcessWait away; a skewed local clock cannot shorten that.
            return new HostSlot.Later(wait > HostBudgetPolicy.MaxInProcessWait ? wait : HostBudgetPolicy.MaxInProcessWait, Politeness);
        }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, time, cancellationToken);
        }

        if (!connection)
        {
            return new HostSlot.Granted(new HostLease(this, host, null));
        }

        // A slot lost to the connection wait costs one interval; it happens only while a request has held the connection
        // longer than ConnectionWait, so it cannot repeat without a request in flight.
        var connections = _connections.GetOrAdd((host, state.MaxConnections), key => new SemaphoreSlim(key.Cap, key.Cap));
        using var timeout = new CancellationTokenSource(ConnectionWait, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await connections.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HostSlot.Later(interval, Politeness);
        }

        return new HostSlot.Granted(new HostLease(this, host, connections));
    }

    internal async Task<HostVerdict> CompleteAsync(string host, HostResponse response, CancellationToken cancellationToken)
    {
        var profile = policy.Current.ProfileOf(host);
        var state = await store.RecordResponseAsync(host, response with { Backoff = profile.Backoff }, cancellationToken);
        var now = time.GetUtcNow();
        if (response.Kind == HostResponseKind.RateLimited && state.PausedUntil is { } until && until > now)
        {
            return new HostVerdict(until, RetryAfterPause);
        }

        if (profile.Breaker is not { } breaker)
        {
            return HostVerdict.Running;
        }

        var window = _windows.GetOrAdd(host, _ => new BreakerWindowState());
        if (window.Add(response.Kind == HostResponseKind.Failure, breaker.FailuresOf(BreakerWindow)))
        {
            var pause = now + breaker.Pause;
            await store.PauseAsync(host, pause, cancellationToken);
            return new HostVerdict(pause, BreakerPause);
        }

        return HostVerdict.Running;
    }

    private async Task<(CrawlHostState State, PolitenessProfile Profile)> EnsureAsync(string host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        var current = policy.Current;
        var profile = current.ProfileOf(host);
        var state = await store.EnsureAsync(
            new CrawlHostState { Host = host, Kind = current.KindOf(host), RequestsPerSecond = profile.RequestsPerSecond, MaxConnections = profile.MaxConnections },
            cancellationToken);
        return (state, profile);
    }

    /// <summary>The last <see cref="BreakerWindow"/> responses of a host; trips at the profile's share of failures and starts over.</summary>
    private sealed class BreakerWindowState
    {
        private readonly bool[] _failures = new bool[BreakerWindow];
        private readonly Lock _lock = new();
        private int _next;
        private int _count;

        public bool Add(bool failure, int threshold)
        {
            lock (_lock)
            {
                if (_failures[_next])
                {
                    _count--;
                }

                _failures[_next] = failure;
                _next = (_next + 1) % BreakerWindow;
                if (failure)
                {
                    _count++;
                }

                if (_count < threshold)
                {
                    return false;
                }

                Array.Clear(_failures);
                _count = 0;
                _next = 0;
                return true;
            }
        }
    }
}
