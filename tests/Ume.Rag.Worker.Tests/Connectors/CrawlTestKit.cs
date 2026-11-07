using System.Data.Common;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Pipelines.Crawl;
using Ume.Rag.Worker.Connectors;
using Ume.Rag.Worker.Connectors.Crawl;
using Ume.Rag.Worker.Connectors.Egress;
using Ume.Rag.Worker.Connectors.Hosts;
using Ume.Rag.Worker.Connectors.Robots;
using Ume.Rag.Worker.Connectors.Uploads;

namespace Ume.Rag.Worker.Tests.Connectors;

/// <summary>
/// <c>CrawlHosts</c> in memory with the semantics of the Npgsql store: one lock per statement, the arithmetic of
/// <see cref="HostBudgetPolicy"/>, the clock of <paramref name="time"/> (the database's <c>now()</c>).
/// </summary>
internal sealed class InMemoryCrawlHostStore(TimeProvider time) : ICrawlHostStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, CrawlHostState> _rows = new(StringComparer.Ordinal);

    public CrawlHostState this[string host]
    {
        get
        {
            lock (_lock)
            {
                return _rows[host];
            }
        }

        set
        {
            lock (_lock)
            {
                _rows[host] = value;
            }
        }
    }

    public Task<CrawlHostState> EnsureAsync(CrawlHostState initial, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var row = _rows.TryGetValue(initial.Host, out var existing)
                ? existing with { Kind = initial.Kind, RequestsPerSecond = initial.RequestsPerSecond, MaxConnections = initial.MaxConnections }
                : initial;
            _rows[initial.Host] = row;
            return Task.FromResult(row);
        }
    }

    public Task<SlotTake?> TryTakeSlotAsync(string host, TimeSpan interval, TimeSpan maxWait, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_rows.TryGetValue(host, out var row))
            {
                return Task.FromResult<SlotTake?>(null);
            }

            var (state, slot) = HostBudgetPolicy.TakeSlot(row, interval, time.GetUtcNow(), maxWait);
            _rows[host] = state;
            return Task.FromResult(slot);
        }
    }

    public Task<CrawlHostState> RecordResponseAsync(string host, HostResponse response, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var state = HostBudgetPolicy.Apply(_rows[host], response, time.GetUtcNow());
            _rows[host] = state;
            return Task.FromResult(state);
        }
    }

    public Task PauseAsync(string host, DateTimeOffset until, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var row = _rows[host];
            _rows[host] = row with { PausedUntil = row.PausedUntil > until ? row.PausedUntil : until };
            return Task.CompletedTask;
        }
    }

    public Task RecordRobotsAsync(string host, RobotsStatus status, DateTimeOffset fetchedAt, int? crawlDelaySeconds, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_rows.TryGetValue(host, out var row))
            {
                _rows[host] = row with { RobotsStatus = status, RobotsFetchedAt = fetchedAt, CrawlDelaySeconds = crawlDelaySeconds };
            }

            return Task.CompletedTask;
        }
    }
}

/// <summary><c>CrawlFrontier</c> and <c>CrawlRuns</c> in memory, with the store contracts' semantics (one lock per statement).</summary>
internal sealed class InMemoryCrawlStore : ICrawlFrontierStore, ICrawlRunStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(Guid, string, string), FrontierRow> _frontier = [];
    private readonly Dictionary<Guid, CrawlRunRecord> _runs = [];
    private long _order;
    private readonly Dictionary<(Guid, string, string), long> _admittedOrder = [];

    public IReadOnlyList<FrontierRow> Rows(Guid sourceId)
    {
        lock (_lock)
        {
            return [.. _frontier.Values.Where(r => r.SourceId == sourceId)];
        }
    }

    public FrontierRow? Row(Guid sourceId, string url)
    {
        lock (_lock)
        {
            return _frontier.Values.SingleOrDefault(r => r.SourceId == sourceId && r.Url == url);
        }
    }

    public Task<AdmitResult> AdmitAsync(CrawlRunKey run, IReadOnlyList<FrontierAdmission> urls, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var record = _runs[run.RunId];
            var admitted = new List<FrontierRow>();
            var limitHit = false;
            foreach (var url in FrontierAdmission.OnePerIdentity(urls))
            {
                var key = (run.SourceId, run.Classification, url.UrlHash);
                _frontier.TryGetValue(key, out var row);
                if (row?.RunId == run.RunId)
                {
                    continue;
                }

                if (record.Admitted >= record.MaxPages)
                {
                    limitHit = true;
                    continue;
                }

                var updated = (row ?? new FrontierRow { Classification = run.Classification, SourceId = run.SourceId, UrlHash = url.UrlHash, Url = url.Url, Host = url.Host })
                    with
                    {
                        State = FrontierState.Pending, RunId = run.RunId, Depth = url.Depth, Leaf = url.Leaf, RemovalCheck = url.RemovalCheck,
                        SitemapLastMod = url.SitemapLastMod ?? row?.SitemapLastMod, FetchUrl = url.StoredFetchUrl ?? row?.FetchUrl,
                    };
                _frontier[key] = updated;
                _admittedOrder[key] = ++_order;
                admitted.Add(updated);
                record = record with { Admitted = record.Admitted + 1, Seeds = record.Seeds + (url.Depth == 0 && !url.RemovalCheck ? 1 : 0) };
            }

            _runs[run.RunId] = record with { LimitHit = record.LimitHit || limitHit };
            return Task.FromResult(new AdmitResult(admitted, limitHit));
        }
    }

    public Task<IReadOnlySet<string>> MarkListedAsync(CrawlRunKey run, IReadOnlyList<FrontierAdmission> seeds, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var seed in FrontierAdmission.OnePerIdentity(seeds))
            {
                var key = (run.SourceId, run.Classification, seed.UrlHash);
                if (_frontier.TryGetValue(key, out var row) && row.State == FrontierState.Idle && row.RunId != run.RunId
                    && row.LastFetchedAt is { } fetched && (seed.SitemapLastMod is null || seed.SitemapLastMod <= fetched))
                {
                    _frontier[key] = row with
                    {
                        RunId = run.RunId, RemovalCheck = false, SitemapLastMod = seed.SitemapLastMod ?? row.SitemapLastMod, FetchUrl = seed.StoredFetchUrl ?? row.FetchUrl,
                    };
                    listed.Add(seed.UrlHash);
                }
            }

            return Task.FromResult<IReadOnlySet<string>>(listed);
        }
    }

    public Task<IReadOnlyList<FrontierRow>> LeftSitemapAsync(CrawlRunKey run, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<FrontierRow> rows = [.. _frontier.Values
                .Where(r => r.SourceId == run.SourceId && r.Classification == run.Classification && r.State == FrontierState.Idle
                    && r.RunId != run.RunId && r.SitemapLastMod is not null)
                .OrderBy(r => r.Url, StringComparer.Ordinal)];
            return Task.FromResult(rows);
        }
    }

    public Task ClearSitemapLastModAsync(CrawlRunKey run, IReadOnlyList<string> urlHashes, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            foreach (var hash in urlHashes)
            {
                var key = (run.SourceId, run.Classification, hash);
                _frontier[key] = _frontier[key] with { SitemapLastMod = null };
            }

            return Task.CompletedTask;
        }
    }

    public Task<FrontierRow?> GetAsync(Guid sourceId, string classification, string urlHash, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_frontier.GetValueOrDefault((sourceId, classification, urlHash)));
        }
    }

    public Task<IReadOnlyList<HostLoad>> HostLoadAsync(CrawlRunKey run, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<HostLoad> loads = [.. _frontier.Values
                .Where(r => r.SourceId == run.SourceId && r.Classification == run.Classification && r.RunId == run.RunId && r.State != FrontierState.Idle)
                .GroupBy(r => r.Host, StringComparer.Ordinal)
                .Select(g => new HostLoad(g.Key, g.Count(r => r.State == FrontierState.Pending), g.Count(r => r.State == FrontierState.Released)))];
            return Task.FromResult(loads);
        }
    }

    public Task<IReadOnlyList<FrontierRow>> ReleaseAsync(CrawlRunKey run, string host, int max, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var rows = _frontier
                .Where(kv => kv.Key.Item1 == run.SourceId && kv.Value.RunId == run.RunId && kv.Value.Host == host && kv.Value.State == FrontierState.Pending)
                .OrderByDescending(kv => kv.Value.RemovalCheck).ThenBy(kv => kv.Value.Depth).ThenBy(kv => _admittedOrder[kv.Key])
                .Take(max)
                .ToList();
            IReadOnlyList<FrontierRow> released = [.. rows.Select(kv => _frontier[kv.Key] = kv.Value with { State = FrontierState.Released })];
            return Task.FromResult(released);
        }
    }

    public Task CompleteAsync(CrawlRunKey run, string urlHash, FrontierFacts? facts, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var key = (run.SourceId, run.Classification, urlHash);
            var row = _frontier[key] with { State = FrontierState.Idle };
            _frontier[key] = facts is null ? row : Apply(row, facts);
            return Task.CompletedTask;
        }
    }

    public Task RecordTargetAsync(CrawlRunKey run, FrontierAdmission target, FrontierFacts facts, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var key = (run.SourceId, run.Classification, target.UrlHash);
            var row = _frontier.GetValueOrDefault(key)
                ?? new FrontierRow { Classification = run.Classification, SourceId = run.SourceId, UrlHash = target.UrlHash, Url = target.Url, Host = target.Host };
            _frontier[key] = Apply(row with { State = FrontierState.Idle, RunId = run.RunId, Depth = target.Depth, Leaf = target.Leaf, RemovalCheck = target.RemovalCheck }, facts);
            return Task.CompletedTask;
        }
    }

    public Task StartAsync(CrawlRunRecord run, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _runs.TryAdd(run.RunId, run);
            return Task.CompletedTask;
        }
    }

    public Task<CrawlRunRecord?> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_runs.GetValueOrDefault(runId));
        }
    }

    public Task SeedFailedAsync(Guid runId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _runs[runId] = _runs[runId] with { SeedsFailed = _runs[runId].SeedsFailed + 1 };
            return Task.CompletedTask;
        }
    }

    public Task<int> PauseHostAsync(Guid runId, string host, string reason, DateTimeOffset? until, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var run = _runs[runId];
            var count = (run.PausedHosts.GetValueOrDefault(host)?.Count ?? 0) + 1;
            _runs[runId] = run with { PausedHosts = new Dictionary<string, HostPause>(run.PausedHosts) { [host] = new HostPause(count, reason, until) } };
            return Task.FromResult(count);
        }
    }

    public Task RecordTrapsAsync(Guid runId, IReadOnlyDictionary<string, TrapStat> traps, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var run = _runs[runId];
            var stats = new Dictionary<string, TrapStat>(run.TrapStats);
            foreach (var (kind, stat) in traps)
            {
                stats[kind] = stats.TryGetValue(kind, out var existing) ? existing with { Count = existing.Count + stat.Count } : stat;
            }

            _runs[runId] = run with { TrapStats = stats };
            return Task.CompletedTask;
        }
    }

    public Task SetCompleteAsync(Guid runId, bool complete, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _runs[runId] = _runs[runId] with { Complete = complete };
            return Task.CompletedTask;
        }
    }

    public Task SetIncrementalAsync(Guid runId, bool incremental, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _runs[runId] = _runs[runId] with { Incremental = incremental };
            return Task.CompletedTask;
        }
    }

    private static FrontierRow Apply(FrontierRow row, FrontierFacts facts) => row with
    {
        LastFetchedAt = facts.FetchedAt,
        LastStatus = facts.LastStatus,
        ETag = facts.ETag,
        LastModified = facts.LastModified,
        ContentHash = facts.ContentHash,
        RedirectTo = facts.RedirectTo,
    };
}

/// <summary>Test sites that serve a Sitevision node under any path that ends in its id (#228: the crawler requests the path as found).</summary>
internal static class Sitevision
{
    /// <summary>The node a request path names: <c>section/sida-a.4.10000000000a.html</c> → <c>4.10000000000a.html</c>; another last segment as it is.</summary>
    public static string NodeOf(string path)
    {
        var last = path[(path.LastIndexOf('/') + 1)..];
        var at = last.IndexOf(".4.", StringComparison.Ordinal);
        return at < 0 ? last : last[(at + 1)..];
    }
}

/// <summary>A queue that records what was enqueued (dedup by key among the recorded, open jobs).</summary>
internal sealed class RecordingJobQueue : IJobQueue
{
    private readonly Lock _lock = new();
    private readonly List<JobRequest> _jobs = [];

    public IReadOnlyList<JobRequest> Jobs
    {
        get
        {
            lock (_lock)
            {
                return [.. _jobs];
            }
        }
    }

    public List<JobRequest> Take()
    {
        lock (_lock)
        {
            var taken = _jobs.ToList();
            _jobs.Clear();
            return taken;
        }
    }

    public Task<IReadOnlySet<Guid>> EnqueueAsync(IReadOnlyList<JobRequest> jobs, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var inserted = new HashSet<Guid>();
            foreach (var job in jobs.Where(j => j.DedupKey is null || _jobs.All(o => o.DedupKey != j.DedupKey)))
            {
                _jobs.Add(job);
                inserted.Add(job.Id);
            }

            return Task.FromResult<IReadOnlySet<Guid>>(inserted);
        }
    }

    public Task<IReadOnlyList<ReadyDepartment>> ReadyDepartmentsAsync(string jobClass, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<LeasedJob?> DequeueAsync(DequeueRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlySet<Guid>> HeartbeatAsync(Guid owner, IReadOnlyList<JobLease> running, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<bool> CompleteAsync(Guid jobId, Guid owner, string? resultJson, DbTransaction? transaction, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<string?> FailAsync(Guid jobId, Guid owner, string error, bool fatal, TimeSpan? retryAfter, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<bool> ReleaseAsync(Guid jobId, Guid owner, string reason, TimeSpan delay, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<int> CancelRunAsync(Guid runId, string reason, Guid? exceptJobId, CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>Municipal hosts and politeness profiles by host name, set by the test; the ceilings let the test sites run at up to 1,000 req/s.</summary>
internal sealed class FixedCrawlPolicy(params string[] municipalHosts) : ICrawlPolicySource
{
    public Dictionary<string, HostPoliteness> Overrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    public PolitenessCeilings Ceilings { get; set; } = new(1000, PolitenessCeilings.DefaultMaxHostConnections, PolitenessCeilings.DefaultMaxRenderPerHost);

    public CrawlPolicy Current => new(municipalHosts, Overrides) { Ceilings = Ceilings };

    /// <summary>A fast budget for a test host: <paramref name="rate"/> req/s and <paramref name="connections"/> connections.</summary>
    public static HostPoliteness Budget(double rate, int connections = 4) => new() { RequestsPerSecond = rate, MaxConnections = connections };
}

/// <summary>Applied site profiles by (level, host), set by the test.</summary>
internal sealed class InMemorySiteProfiles : ISiteProfileReader
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, string), AppliedSiteProfile> _profiles = new();

    /// <summary>Applies a profile with <c>lastmodReliable</c> as given to the host at the level.</summary>
    public AppliedSiteProfile Set(string classification, string host, bool lastmodReliable, int number = 1) =>
        Set(classification, host, new Pipelines.SiteProfiles.SiteProfile { MainRoot = "main", LastmodReliable = lastmodReliable }, number);

    /// <summary>Applies <paramref name="profile"/> to the host at the level.</summary>
    public AppliedSiteProfile Set(string classification, string host, Pipelines.SiteProfiles.SiteProfile profile, int number = 1)
    {
        var applied = new AppliedSiteProfile(Guid.NewGuid(), number, "sitevision", profile);
        _profiles[(classification, host)] = applied;
        return applied;
    }

    public Task<AppliedSiteProfile?> GetAppliedAsync(string classification, string host, CancellationToken cancellationToken) =>
        Task.FromResult(_profiles.GetValueOrDefault((classification, host)));
}

internal sealed class InMemoryUploadSlots : IUploadSlotReader
{
    public List<UploadSlot> Slots { get; } = [];

    public Task<IReadOnlyList<UploadSlot>> ListAsync(Guid sourceId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UploadSlot>>([.. Slots.Where(s => s.SourceId == sourceId)]);
}

/// <summary>
/// A crawl wired to the test site, on in-memory stores or on the given (Postgres) ones; <see cref="DrainAsync"/> runs the
/// fetch jobs as the engine would.
/// </summary>
internal sealed class CrawlRig : IAsyncDisposable
{
    private static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxRequeueWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinRequeueWait = TimeSpan.FromMilliseconds(10);

    private readonly InMemoryCrawlStore? _memory;

    private CrawlRig(FetchPath path, CrawlConnector connector, CrawlTraversal traversal, ICrawlFrontierStore frontier, ICrawlRunStore runs,
        InMemoryCrawlStore? memory, RecordingJobQueue queue, FetchTopUp topUp, InMemorySiteProfiles profiles, FakeTimeProvider profileClock)
    {
        Profiles = profiles;
        ProfileClock = profileClock;
        Path = path;
        Connector = connector;
        Traversal = traversal;
        Frontier = frontier;
        Runs = runs;
        _memory = memory;
        Queue = queue;
        TopUp = topUp;
    }

    public FetchPath Path { get; }

    public CrawlConnector Connector { get; }

    public CrawlTraversal Traversal { get; }

    public ICrawlFrontierStore Frontier { get; }

    public ICrawlRunStore Runs { get; }

    /// <summary>The in-memory store (rigs created without database stores).</summary>
    public InMemoryCrawlStore Store => _memory ?? throw new InvalidOperationException("This rig runs on database stores.");

    public RecordingJobQueue Queue { get; }

    public FetchTopUp TopUp { get; }

    /// <summary>The fetch jobs <see cref="DrainAsync"/> sent back (no host slot yet, a transient answer).</summary>
    public int Requeues { get; private set; }

    /// <summary>The hosts' site profiles, read by the traversal and the connector (none: no host marks <c>lastmod</c> reliable).</summary>
    public InMemorySiteProfiles Profiles { get; }

    /// <summary>The clock of the profile caches: advance it past <see cref="LastmodReliability.CacheFor"/> after changing a profile.</summary>
    public FakeTimeProvider ProfileClock { get; }

    /// <param name="site">The test site.</param>
    /// <param name="policy">Municipal hosts and politeness profiles; the default makes the test hosts fast (500 req/s, within numeric(5,2); 4 connections).</param>
    /// <param name="batch"><c>Crawl:FetchBatchPerHost</c>.</param>
    /// <param name="dns">Extra host names.</param>
    /// <param name="hosts">The <c>CrawlHosts</c> store; in memory when null.</param>
    /// <param name="store">The frontier and run store; in memory when null.</param>
    /// <param name="rule"><c>Crawl:CompletenessRule</c>.</param>
    public static CrawlRig Create(
        TestSite site, FixedCrawlPolicy? policy = null, int batch = 10, FakeDnsResolver? dns = null, ICrawlHostStore? hosts = null,
        (ICrawlFrontierStore Frontier, ICrawlRunStore Runs)? store = null, Worker.Connectors.Deletion.CompletenessRule rule = Worker.Connectors.Deletion.CompletenessRule.Ratio)
    {
        if (policy is null)
        {
            policy = new FixedCrawlPolicy();
            foreach (var host in new[] { TestSite.Host, "kommun.test", "other.test" })
            {
                policy.Overrides[host] = FixedCrawlPolicy.Budget(500);
            }
        }

        var path = site.Fetch(dns: dns, policy: policy);

        var profiles = new InMemorySiteProfiles();
        var profileClock = new FakeTimeProvider();
        var connector = new CrawlConnector(path.Fetcher, path.Robots, new ScriptedScanner(Worker.Connectors.Malware.ScanResult.Clean), path.Blobs, TimeProvider.System,
            new SiteProfileCanonicalRules(profiles, profileClock));
        var memory = store is null ? new InMemoryCrawlStore() : null;
        var (frontier, runs) = store ?? (memory!, memory!);
        var queue = new RecordingJobQueue();
        var budget = new HostBudget(hosts ?? new InMemoryCrawlHostStore(TimeProvider.System), policy, TimeProvider.System);
        var options = Options.Create(new CrawlOptions { FetchBatchPerHost = batch, CompletenessRule = rule });
        var topUp = new FetchTopUp(frontier, runs, queue, policy, options, TimeProvider.System);
        var traversal = new CrawlTraversal(connector, path.Robots, budget, policy, frontier, runs, topUp, path.Blobs, queue,
            new LastmodReliability(profiles, profileClock), options, TimeProvider.System);
        return new CrawlRig(path, connector, traversal, frontier, runs, memory, queue, topUp, profiles, profileClock);
    }

    /// <summary>
    /// Opens the run, lists and admits the seeds, reports the listing's end (removal checks), then runs every released
    /// fetch until none is left; returns the context the run continued with and the fetch results by URL.
    /// </summary>
    public async Task<(CrawlRunContext Context, Dictionary<string, CrawlFetchResult> Results)> RunAsync(CrawlRunContext context)
    {
        var ct = TestContext.Current.CancellationToken;
        context = await Traversal.StartAsync(context, ct);
        var batch = new List<ListingEvent<CrawlCheckpoint>>();
        var listing = new ListingContext(context.Run.SourceId, context.Run.Classification, context.Run.RunId, context.Settings);
        await foreach (var e in Connector.ListAsync(listing, new CrawlCheckpoint(), ct))
        {
            batch.Add(e);
            if (e is ListingEvent<CrawlCheckpoint>.Checkpoint)
            {
                await Traversal.AdmitSeedsAsync(context, batch, ct);
                batch.Clear();
            }
            else if (e is ListingEvent<CrawlCheckpoint>.Done done)
            {
                await Traversal.ListingDoneAsync(context, done.Completeness, ct);
            }
        }

        await TopUp.TopUpAsync(context, null, null, ct);
        return (context, await DrainAsync(context));
    }

    /// <summary><see cref="RunAsync"/> for a run that stays as given (no incremental downgrade expected).</summary>
    public async Task<Dictionary<string, CrawlFetchResult>> CrawlAsync(CrawlRunContext context) => (await RunAsync(context)).Results;

    /// <summary>
    /// Runs the queued fetch jobs until none is left. A round in which every job was requeued (no host slot yet: the budget's
    /// interval follows the host's latency, which a cold start can stretch past a second) waits for the shortest requeue
    /// delay, at most a second, as the queue would; the drain is bounded by time, not by rounds.
    /// </summary>
    public async Task<Dictionary<string, CrawlFetchResult>> DrainAsync(CrawlRunContext context)
    {
        var ct = TestContext.Current.CancellationToken;
        var results = new Dictionary<string, CrawlFetchResult>(StringComparer.Ordinal);
        var drain = System.Diagnostics.Stopwatch.StartNew();
        while (drain.Elapsed < DrainLimit)
        {
            var jobs = Queue.Take().Where(j => j.Class == "fetch").ToList();
            if (jobs.Count == 0)
            {
                return results;
            }

            TimeSpan? wait = MaxRequeueWait;
            foreach (var job in jobs)
            {
                var hash = System.Text.Json.JsonDocument.Parse(job.Payload).RootElement.GetProperty("urlHash").GetString()!;
                var url = (await Frontier.GetAsync(context.Run.SourceId, context.Run.Classification, hash, ct))!.Url;
                var result = await Traversal.ProcessAsync(context, hash, ct);
                if (result is CrawlFetchResult.Requeue requeue)
                {
                    // As the queue would: the job comes back without an attempt.
                    await Queue.EnqueueAsync([job], null, ct);
                    Requeues++;
                    wait = wait is { } shortest && requeue.Delay is { } delay && delay < shortest ? delay : wait;
                }
                else
                {
                    wait = null;
                    if (result is CrawlFetchResult.Done done)
                    {
                        await CommitAsync(context, done);
                    }
                }

                results[url] = result;
            }

            if (wait is { } pause)
            {
                await Task.Delay(pause > MinRequeueWait ? pause : MinRequeueWait, ct);
            }
        }

        throw new InvalidOperationException("The crawl did not finish.");
    }

    /// <summary>What the engine's document commit does with a fetched row (without a document): idle with its facts, a failed seed counted, then the top-up.</summary>
    public async Task CommitAsync(CrawlRunContext context, CrawlFetchResult.Done done)
    {
        var ct = TestContext.Current.CancellationToken;
        await Frontier.CompleteAsync(context.Run, done.Frontier.Row.UrlHash, done.Frontier.Facts, ct);
        if (done.Frontier.SeedFailed)
        {
            await Runs.SeedFailedAsync(context.Run.RunId, ct);
        }

        await Traversal.CommittedAsync(context, done, ct);
    }

    public ValueTask DisposeAsync() => Path.DisposeAsync();
}
