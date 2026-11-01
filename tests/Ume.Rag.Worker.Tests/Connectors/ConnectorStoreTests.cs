using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Blobs;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Pipelines.Sources;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Connectors;
using Ume.Rag.Worker.Connectors.Crawl;
using Ume.Rag.Worker.Connectors.Deletion;
using Ume.Rag.Worker.Connectors.Egress;
using Ume.Rag.Worker.Connectors.Hosts;
using Ume.Rag.Worker.Connectors.Malware;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Connectors.Robots;
using Ume.Rag.Worker.Connectors.Uploads;
using Ume.Rag.Worker.Connectors.Urls;

namespace Ume.Rag.Worker.Tests.Connectors;

/// <summary>
/// The connector stores against the real schema and the worker role (connectors design §18, schema part 1 = J2): the
/// slot statement, admission under <c>maxPages</c>, C7, two sources on one host, crash and resume of listing and fetch,
/// uploads by slot, EICAR to quarantine, proposals, checkpoints and <c>Settings.Crawl</c>. No assertion depends on how
/// fast the machine is: slots are compared with each other, never with the wall clock.
/// </summary>
[Collection(RagDatabaseGroup3.Name)]
public sealed class ConnectorStoreTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private static readonly byte[] Eicar = Encoding.ASCII.GetBytes(@"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _fresh = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _freshStatus = new(StringComparer.Ordinal);
    private TestSite _site = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Infrastructure.Jobs.JobDatabase Worker => database.Database(RagDatabase.Worker);

    public async ValueTask InitializeAsync()
    {
        await database.ResetAsync();
        _site = await TestSite.StartAsync(Map);
    }

    public async ValueTask DisposeAsync() => await _site.DisposeAsync();

    [Fact]
    public async Task Slots_from_the_statement_are_never_closer_than_the_interval_even_when_taken_concurrently()
    {
        var store = new PostgresCrawlHostStore(Worker);
        var host = $"{Guid.NewGuid():N}.example";
        var state = await store.EnsureAsync(new CrawlHostState { Host = host, Kind = HostKind.Municipal, RequestsPerSecond = 10, MaxConnections = 4 }, Ct);
        var interval = HostBudgetPolicy.Interval(state);

        // Eight takers at once (within the worker role's connection cap), five slots each; the wait allows all 40 (4 s).
        var taken = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            var mine = new List<DateTimeOffset>();
            for (var i = 0; i < 5; i++)
            {
                var slot = (await store.TryTakeSlotAsync(host, interval, TimeSpan.FromMinutes(1), Ct))!;
                slot.Taken.ShouldBeTrue();
                mine.Add(slot.At);
            }

            return mine;
        }));
        var slots = taken.SelectMany(t => t).Order().ToList();
        slots.Count.ShouldBe(40);

        slots.Zip(slots.Skip(1)).ShouldAllBe(p => p.Second - p.First >= interval);
        // Negative proof of the rate: no one-second window holds more than 10 slots.
        foreach (var slot in slots)
        {
            slots.Count(s => s >= slot && s < slot.AddSeconds(1)).ShouldBeLessThanOrEqualTo(10);
        }
    }

    [Fact]
    public async Task Ten_fetch_jobs_on_a_one_per_second_host_all_get_a_slot_and_requeued_ones_never_push_the_slots_out()
    {
        // The pilot crawl's livelock (2026-11-01): each requeued job reserved a slot it never used, so the next free slot
        // ran ≈ 10 s ahead of now and no job ever found one within the in-process wait.
        var hosts = new RecordingHostStore(new PostgresCrawlHostStore(Worker));
        var budget = new HostBudget(hosts, new FixedCrawlPolicy(), TimeProvider.System);
        var host = $"{Guid.NewGuid():N}.example";
        var interval = HostBudgetPolicy.Interval(await budget.EnsureHostAsync(host, Ct));
        interval.ShouldBe(TimeSpan.FromSeconds(1));
        var bound = TimeSpan.FromSeconds(45);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(bound);

        // How far ahead of the database's now the host's next free slot is, sampled while the jobs run.
        var running = true;
        var ahead = TimeSpan.Zero;
        var monitor = Task.Run(async () =>
        {
            while (Volatile.Read(ref running))
            {
                var seconds = await Sql.ScalarAsync<double>(database.Connection(null),
                    """SELECT extract(epoch FROM "NextAllowedAt" - now())::float8 FROM content."CrawlHosts" WHERE "Host" = $1""", host);
                ahead = TimeSpan.FromSeconds(Math.Max(ahead.TotalSeconds, seconds));
                await Task.Delay(20, Ct);
            }
        }, Ct);

        // Ten fetch jobs as the dispatcher runs them: acquire; on Later wait for the returned delay (the requeue) and retry.
        var jobs = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            var requeues = 0;
            try
            {
                while (true)
                {
                    switch (await budget.AcquireAsync(host, deadline.Token))
                    {
                        case HostSlot.Granted granted:
                            using (granted.Lease)
                            {
                                await Task.Delay(50, deadline.Token);
                                await granted.Lease.CompleteAsync(new HostResponse(HostResponseKind.Success, null), deadline.Token);
                            }

                            return (Served: true, Requeues: requeues);
                        case HostSlot.Later later:
                            requeues++;
                            await Task.Delay(later.Delay, deadline.Token);
                            break;
                        default:
                            throw new InvalidOperationException("The host is neither blocked nor paused.");
                    }
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !Ct.IsCancellationRequested)
            {
                return (Served: false, Requeues: requeues);
            }
        }, Ct)));
        Volatile.Write(ref running, false);
        await monitor;

        jobs.Count(j => j.Served).ShouldBe(10, $"jobs served within {bound.TotalSeconds} s ({jobs.Sum(j => j.Requeues)} requeues)");
        // Every slot taken was used: none was reserved for a job that requeued instead. No two closer than the interval.
        var slots = hosts.Slots.Order().ToList();
        slots.Count.ShouldBe(10, "slots taken");
        slots.Zip(slots.Skip(1)).ShouldAllBe(p => p.Second - p.First >= interval);
        ahead.ShouldBeLessThanOrEqualTo(HostBudgetPolicy.MaxInProcessWait + interval, "NextAllowedAt ahead of now");
    }

    [Fact]
    public async Task Blocked_and_paused_hosts_get_no_slot_and_responses_move_the_backoff()
    {
        var store = new PostgresCrawlHostStore(Worker);
        var host = $"{Guid.NewGuid():N}.example";
        await store.EnsureAsync(new CrawlHostState { Host = host, Kind = HostKind.External, RequestsPerSecond = 1, MaxConnections = 1 }, Ct);

        var limited = await store.RecordResponseAsync(host, new HostResponse(HostResponseKind.RateLimited, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5)), Ct);
        limited.BackoffFactor.ShouldBe(2);
        limited.LatencyEwmaMs.ShouldBe(200);
        var paused = await store.RecordResponseAsync(host, new HostResponse(HostResponseKind.RateLimited, null, TimeSpan.FromHours(2)), Ct);
        paused.PausedUntil.ShouldNotBeNull();
        (await store.TryTakeSlotAsync(host, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), Ct)).ShouldBeNull();

        var blocked = $"{Guid.NewGuid():N}.example";
        await store.EnsureAsync(new CrawlHostState { Host = blocked, Kind = HostKind.External, RequestsPerSecond = 1, MaxConnections = 1 }, Ct);
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."CrawlHosts" SET "BlockedAt" = now() WHERE "Host" = $1""", blocked);
        (await store.TryTakeSlotAsync(blocked, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), Ct)).ShouldBeNull();

        // A changed budget updates the row; an unchanged one returns it as it is.
        (await store.EnsureAsync(new CrawlHostState { Host = host, Kind = HostKind.Municipal, RequestsPerSecond = 10, MaxConnections = 4 }, Ct))
            .ShouldSatisfyAllConditions(s => s.Kind.ShouldBe(HostKind.Municipal), s => s.BackoffFactor.ShouldBe(4));
        await store.RecordRobotsAsync(host, RobotsStatus.Ok, DateTimeOffset.UtcNow, 5, Ct);
        (await store.EnsureAsync(new CrawlHostState { Host = host, Kind = HostKind.Municipal, RequestsPerSecond = 10, MaxConnections = 4 }, Ct))
            .CrawlDelaySeconds.ShouldBe(5);
    }

    [Fact]
    public async Task Admission_never_passes_max_pages_even_from_concurrent_fetches_and_counts_each_url_once()
    {
        var store = new PostgresCrawlStore(Worker);
        var run = await StartRunAsync(store, maxPages: 25);
        var host = "www.umea.se";

        var seeds = await store.AdmitAsync(run, [.. Enumerable.Range(0, 5).Select(i => Admission($"https://www.umea.se/s{i}", 0))], Ct);
        seeds.Admitted.Count.ShouldBe(5);
        // Again (a resumed listing): nothing new, nothing counted.
        (await store.AdmitAsync(run, [.. Enumerable.Range(0, 5).Select(i => Admission($"https://www.umea.se/s{i}", 0))], Ct)).Admitted.ShouldBeEmpty();

        // Eight fetch jobs at once, each admitting ten links (overlapping): at most 20 more fit.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(j =>
            store.AdmitAsync(run, [.. Enumerable.Range(j * 5, 10).Select(i => Admission($"https://www.umea.se/p{i}", 1))], Ct)));

        var record = (await store.GetAsync(run.RunId, Ct))!;
        record.Admitted.ShouldBe(25);
        record.Seeds.ShouldBe(5);
        record.LimitHit.ShouldBeTrue();
        results.Sum(r => r.Admitted.Count).ShouldBe(20);
        results.SelectMany(r => r.Admitted).Select(r => r.UrlHash).ShouldBeUnique();
        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."CrawlFrontier" WHERE "SourceId" = $1 AND "RunId" = $2""", run.SourceId, run.RunId))
            .ShouldBe(25);
        (await store.HostLoadAsync(run, Ct)).ShouldBe([new HostLoad(host, 25, 0)]);
    }

    [Fact]
    public async Task Rows_are_released_lowest_depth_first_completed_with_their_facts_and_runs_keep_pauses_and_traps()
    {
        var store = new PostgresCrawlStore(Worker);
        var run = await StartRunAsync(store, maxPages: 100);
        await store.AdmitAsync(run, [Admission("https://www.umea.se/deep", 2), Admission("https://www.umea.se/seed", 0, DateTimeOffset.Parse("2026-01-15T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture)), Admission("https://www.umea.se/mid", 1)], Ct);

        var released = await store.ReleaseAsync(run, "www.umea.se", 2, Ct);
        released.Select(r => r.Url).ShouldBe(["https://www.umea.se/seed", "https://www.umea.se/mid"]);
        released[0].SitemapLastMod.ShouldBe(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero));
        (await store.ReleaseAsync(run, "www.umea.se", 2, Ct)).Select(r => r.Url).ShouldBe(["https://www.umea.se/deep"]);

        var facts = new FrontierFacts(DateTimeOffset.UtcNow, 200, "W/\"1\"", "Thu, 15 Jan 2026 10:00:00 GMT", new string('a', 64), null);
        await store.CompleteAsync(run, released[0].UrlHash, facts, Ct);
        var row = (await store.GetAsync(run.SourceId, run.Classification, released[0].UrlHash, Ct))!;
        (row.State, row.LastStatus, row.ETag, row.ContentHash).ShouldBe((FrontierState.Idle, (short?)200, "W/\"1\"", new string('a', 64)));
        await store.RecordTargetAsync(run, Admission("https://www.umea.se/target", 1), facts, Ct);
        (await store.HostLoadAsync(run, Ct)).ShouldBe([new HostLoad("www.umea.se", 0, 2)]);

        (await store.PauseHostAsync(run.RunId, "www.umea.se", "crawl.breaker", DateTimeOffset.UtcNow.AddMinutes(30), Ct)).ShouldBe(1);
        (await store.PauseHostAsync(run.RunId, "www.umea.se", "crawl.breaker", null, Ct)).ShouldBe(2);
        await store.RecordTrapsAsync(run.RunId, new Dictionary<string, TrapStat> { [TrapDetector.Calendar] = new(2, "https://www.umea.se/k/2010") }, Ct);
        await store.RecordTrapsAsync(run.RunId, new Dictionary<string, TrapStat> { [TrapDetector.Calendar] = new(3, "https://www.umea.se/k/2011") }, Ct);
        await store.SeedFailedAsync(run.RunId, Ct);
        var record = (await store.GetAsync(run.RunId, Ct))!;
        record.PausedHosts["www.umea.se"].Count.ShouldBe(2);
        record.TrapStats[TrapDetector.Calendar].ShouldBe(new TrapStat(5, "https://www.umea.se/k/2010"));
        record.SeedsFailed.ShouldBe(1);
        TraversalCompleteness.Evaluate(record, 2, false, CompletenessRule.Ratio, incremental: false).Complete.ShouldBeFalse();
    }

    [Fact]
    public async Task C7_against_the_database_a_page_limit_crawl_is_incomplete_and_deletes_nothing()
    {
        var store = new PostgresCrawlStore(Worker);
        await using var rig = CrawlRig.Create(_site, hosts: new PostgresCrawlHostStore(Worker), store: (store, store));
        var context = Context(Settings(maxPages: 3));

        await rig.CrawlAsync(context);
        var verdict = await rig.Traversal.EvaluateAsync(context, cancelled: false, Ct);

        var record = (await store.GetAsync(context.Run.RunId, Ct))!;
        (record.Admitted, record.LimitHit, record.Complete).ShouldBe((3, true, false));
        verdict.RunDegradation.ShouldBe(TraversalCompleteness.LimitPages);
        var earlier = Enumerable.Range(0, 10).Select(i => new SourceDocument
        {
            Id = Guid.NewGuid(),
            CanonicalIdentity = _site.Url($"/old{i}").AbsoluteUri,
            LastRunId = Guid.NewGuid(),
            LastSeenAt = DateTimeOffset.UtcNow.AddDays(-7),
        }).ToList();
        var candidates = DeletionCandidates.FromTraversal(context.Run.RunId, earlier, verdict, new Dictionary<Guid, string>(), new HashSet<Guid>(), DateTimeOffset.UtcNow);
        candidates.ShouldBeEmpty();
        DeletionGuard.Evaluate(candidates, earlier.Count, false, GuardSettings.Default).Apply.ShouldBeEmpty();
    }

    [Fact]
    public async Task Two_sources_on_one_host_each_get_every_page_and_share_one_budget()
    {
        var hosts = new RecordingHostStore(new PostgresCrawlHostStore(Worker));
        var store = new PostgresCrawlStore(Worker);
        var policy = new FixedCrawlPolicy();
        policy.Overrides[$"{TestSite.Host}:{_site.Port}"] = new HostOverride(20, 4);
        await using var first = CrawlRig.Create(_site, policy, batch: 2, hosts: hosts, store: (store, store));
        await using var second = CrawlRig.Create(_site, policy, batch: 2, hosts: hosts, store: (store, store));
        var a = Context(Settings());
        var b = Context(Settings());

        await Task.WhenAll(first.CrawlAsync(a), second.CrawlAsync(b));

        foreach (var context in new[] { a, b })
        {
            (await first.Traversal.EvaluateAsync(context, false, Ct)).Complete.ShouldBeTrue();
            (await store.GetAsync(context.Run.RunId, Ct))!.Admitted.ShouldBe(5);
        }

        // Two documents per page (one per source), one budget: no two requests closer than 1/rate, whichever source sent them.
        var slots = hosts.Slots.Order().ToList();
        slots.Count.ShouldBeGreaterThanOrEqualTo(10);
        slots.Zip(slots.Skip(1)).ShouldAllBe(p => p.Second - p.First >= TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task A_listing_resumed_from_its_stored_checkpoint_admits_no_url_twice()
    {
        var store = new PostgresCrawlStore(Worker);
        var checkpoints = new PostgresConnectorCheckpointStore(Worker, TimeProvider.System);
        await using var rig = CrawlRig.Create(_site, store: (store, store));
        var context = Context(Settings());
        var listing = new ListingContext(context.Run.SourceId, context.Run.Classification, context.Run.RunId, context.Settings);
        await rig.Traversal.StartAsync(context, Ct);

        // First job: the start URLs up to their checkpoint, committed, then the worker dies.
        var batch = new List<ListingEvent<CrawlCheckpoint>>();
        await foreach (var e in rig.Connector.ListAsync(listing, new CrawlCheckpoint(), Ct))
        {
            batch.Add(e);
            if (e is ListingEvent<CrawlCheckpoint>.Checkpoint checkpoint)
            {
                await rig.Traversal.AdmitSeedsAsync(context, batch, Ct);
                await checkpoints.SaveAsync(context.Run.SourceId, context.Run.Classification, CheckpointKind.Run, context.Run.RunId, checkpoint.State, Ct);
                break;
            }
        }

        // The continuation resumes from the stored checkpoint; a checkpoint of another run is not resumed.
        (await checkpoints.GetAsync<CrawlCheckpoint>(context.Run.SourceId, CheckpointKind.Run, Guid.NewGuid(), Ct)).ShouldBeNull();
        var from = (await checkpoints.GetAsync<CrawlCheckpoint>(context.Run.SourceId, CheckpointKind.Run, context.Run.RunId, Ct))!;
        from.ShouldBe(new CrawlCheckpoint { SchemaVersion = 1, Phase = 1, Root = 0, Skip = 0 });
        var resumed = new List<ListingEvent<CrawlCheckpoint>>();
        await foreach (var e in rig.Connector.ListAsync(listing, from, Ct))
        {
            resumed.Add(e);
        }

        resumed.OfType<ListingEvent<CrawlCheckpoint>.Item>().Select(i => i.Work.CanonicalIdentity).ShouldNotContain(_site.Url("/").AbsoluteUri);
        await rig.Traversal.AdmitSeedsAsync(context, resumed, Ct);
        // A second resume from the same checkpoint (the continuation died too) changes nothing.
        await rig.Traversal.AdmitSeedsAsync(context, resumed, Ct);

        var record = (await store.GetAsync(context.Run.RunId, Ct))!;
        record.Seeds.ShouldBe(3);
        record.Admitted.ShouldBe(3);
    }

    [Fact]
    public async Task A_fetch_retried_after_its_blob_was_written_but_before_it_committed_is_idempotent()
    {
        var store = new PostgresCrawlStore(Worker);
        await using var rig = CrawlRig.Create(_site, store: (store, store));
        var context = Context(Settings());
        await rig.Traversal.StartAsync(context, Ct);
        await rig.Traversal.AdmitSeedsAsync(context, [new ListingEvent<CrawlCheckpoint>.Item(Item(_site.Url("/")))], Ct);
        var row = (await store.ReleaseAsync(context.Run, $"{TestSite.Host}:{_site.Port}", 1, Ct)).Single();

        // The first attempt fetched and wrote the raw blob, then the worker died before the frontier commit.
        var crashed = await rig.Connector.FetchPageAsync(context.FetchContext, Item(_site.Url("/")), Ct);
        var key = crashed.Outcome.ShouldBeOfType<FetchOutcome.Content>().Raw.Key;

        var retried = (await rig.Traversal.ProcessAsync(context, row.UrlHash, Ct)).ShouldBeOfType<CrawlFetchResult.Done>();
        retried.Outcome.ShouldBeOfType<FetchOutcome.Content>().Raw.Key.ShouldBe(key);
        // The row stays released until the engine's document commit marks it idle.
        (await store.GetAsync(context.Run.SourceId, context.Run.Classification, row.UrlHash, Ct))!.State.ShouldBe(FrontierState.Released);
        await rig.CommitAsync(context, retried);
        var admitted = (await store.GetAsync(context.Run.RunId, Ct))!.Admitted;
        // A duplicate delivery of the same job finds the row done and does nothing.
        (await rig.Traversal.ProcessAsync(context, row.UrlHash, Ct)).ShouldBeOfType<CrawlFetchResult.Stale>();
        (await store.GetAsync(context.Run.RunId, Ct))!.Admitted.ShouldBe(admitted);
        (await store.GetAsync(context.Run.SourceId, context.Run.Classification, row.UrlHash, Ct))!.State.ShouldBe(FrontierState.Idle);
    }

    [Fact]
    public async Task A_replaced_upload_keeps_its_slot_and_identity_and_two_departments_get_two_slots_of_the_same_bytes()
    {
        var uploads = new PostgresUploadStore(Worker);
        var root = Directory.CreateTempSubdirectory("ume-rag-upload-db-");
        try
        {
            var blobs = new FileSystemBlobStore(Options.Create(new BlobStoreOptions { Root = root.FullName }), TimeProvider.System);
            var connector = new UploadConnector(blobs, new EicarOnlyScanner(), uploads, TimeProvider.System);
            var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << >> endobj\n%%EOF\n");
            var pdf2 = Encoding.ASCII.GetBytes("%PDF-1.7\n2 0 obj << >> endobj\n%%EOF\n");
            var (sourceA, sourceB) = (Guid.NewGuid(), Guid.NewGuid());
            var slot = Guid.NewGuid();

            var added = await CommitAsync(uploads, blobs, sourceA, (UploadOperation.Add, slot, "beslut.pdf", pdf));
            var replaced = await CommitAsync(uploads, blobs, sourceA, (UploadOperation.Replace, slot, "beslut.pdf", pdf2));
            var stored = (await uploads.ListAsync(sourceA, Ct)).Single();
            stored.Id.ShouldBe(slot);
            stored.Sha256.ShouldBe(Sha(pdf2));
            stored.ReplacedAt.ShouldNotBeNull();
            // Same identity, so the same document (Documents is unique on (SourceId, IdentityKind, IdentityHash)).
            replaced.Fetches.Single().CanonicalIdentity.ShouldBe(added.Fetches.Single().CanonicalIdentity);

            var inB = await CommitAsync(uploads, blobs, sourceB, (UploadOperation.Add, Guid.NewGuid(), "beslut.pdf", pdf));
            inB.Skipped.ShouldBeEmpty();
            var context = new FetchContext(sourceA, "internal", Guid.NewGuid(), default, false);
            var first = (await connector.FetchAsync(context, added.Fetches.Single(), Ct)).ShouldBeOfType<FetchOutcome.Content>();
            var second = (await connector.FetchAsync(context with { SourceId = sourceB }, inB.Fetches.Single(), Ct)).ShouldBeOfType<FetchOutcome.Content>();
            // Two documents (two identities), one content hash: parse and embedding caches are shared by content.
            inB.Fetches.Single().CanonicalIdentity.ShouldNotBe(added.Fetches.Single().CanonicalIdentity);
            second.Raw.ContentHash.ShouldBe(first.Raw.ContentHash);

            // The same bytes again in source A: a duplicate, no new slot.
            var duplicate = await CommitAsync(uploads, blobs, sourceA, (UploadOperation.Add, Guid.NewGuid(), "kopia.pdf", pdf2));
            duplicate.Skipped.Single().Reason.ShouldBe(UploadBatchPlanner.Duplicate);
            (await uploads.ListAsync(sourceA, Ct)).Count.ShouldBe(1);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Eicar_upload_is_quarantined_with_a_row_and_its_slot_marked_malware()
    {
        var uploads = new PostgresUploadStore(Worker);
        var quarantine = new PostgresQuarantineStore(Worker);
        var root = Directory.CreateTempSubdirectory("ume-rag-upload-db-");
        try
        {
            var blobs = new FileSystemBlobStore(Options.Create(new BlobStoreOptions { Root = root.FullName }), TimeProvider.System);
            var connector = new UploadConnector(blobs, new EicarOnlyScanner(), uploads, TimeProvider.System);
            var sourceId = Guid.NewGuid();
            var slot = Guid.NewGuid();
            var plan = await CommitAsync(uploads, blobs, sourceId, (UploadOperation.Add, slot, "virus.pdf", Eicar));
            var context = new FetchContext(sourceId, "internal", Guid.NewGuid(), default, false);
            var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 7, 10, 0, 0, TimeSpan.Zero));

            var finding = (await connector.FetchAsync(context, plan.Fetches.Single(), Ct)).ShouldBeOfType<FetchOutcome.Quarantined>();
            var record = QuarantineRecord.For(finding, context, "virus.pdf", null, time.GetUtcNow());
            await quarantine.AddAsync(record, Ct);
            await quarantine.AddAsync(record, Ct);
            await uploads.SetStateAsync(slot, UploadState.Malware, null, finding.Reason, null, Ct);

            (await Sql.ScalarAsync<long>(database.Connection(null),
                """SELECT count(*) FROM content."QuarantinedFiles" WHERE "SourceId" = $1 AND "Origin" = 'upload' AND "Signature" = $2 AND "PurgeAfter" = $3""",
                sourceId, EicarOnlyScanner.Signature, time.GetUtcNow().AddDays(30))).ShouldBe(1);
            (await uploads.ListAsync(sourceId, Ct)).Single().State.ShouldBe(UploadState.Malware);
            (await blobs.ExistsAsync(finding.Key, Ct)).ShouldBeTrue();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Proposals_wait_for_a_decision_and_record_it()
    {
        var proposals = new PostgresDeletionProposalStore(Worker);
        var (runId, sourceId) = (Guid.NewGuid(), Guid.NewGuid());
        var candidates = Enumerable.Range(0, 6).Select(_ => new DeletionCandidate(Guid.NewGuid(), DeletionReasons.NotSeen))
            .Append(new DeletionCandidate(Guid.NewGuid(), DeletionReasons.RemovedFromList)).ToList();
        var decision = DeletionGuard.Evaluate(candidates, 10, false, GuardSettings.Default);
        var now = new DateTimeOffset(2026, 11, 8, 3, 0, 0, TimeSpan.Zero);

        await proposals.WriteAsync(DeletionProposalLifecycle.Reconcile(runId, sourceId, "public", decision, now).Proposals, Ct);
        (await proposals.WaitingRunAsync(sourceId, Ct)).ShouldBe(runId);
        var stored = await proposals.ListAsync(runId, Ct);
        stored.Count.ShouldBe(7);
        stored.Single(p => p.Exempt).Decision.ShouldBe(ProposalDecisions.Applied);

        var rejected = DeletionProposalLifecycle.Reject(stored, "admin@umea.se", now.AddHours(1));
        await proposals.WriteAsync(rejected.Proposals, Ct);
        (await proposals.WaitingRunAsync(sourceId, Ct)).ShouldBeNull();
        (await proposals.ListAsync(runId, Ct)).Count(p => p.Decision == ProposalDecisions.Rejected && p.DecidedBy == "admin@umea.se").ShouldBe(6);
    }

    [Fact]
    public async Task Settings_crawl_feeds_the_egress_and_crawl_policies_and_a_failed_read_keeps_the_last()
    {
        await using (var db = TestPostgres.Context(database.Connection(RagDatabase.Migrator)))
        {
            var row = await db.Settings.FindAsync([PlatformSettings.SingletonId], Ct);
            if (row is null)
            {
                row = new PlatformSettings { UpdatedAt = DateTimeOffset.UtcNow };
                db.Settings.Add(row);
            }

            row.Crawl = """{ "allowedPorts": [8443], "blockedHosts": ["bad.example"], "municipalHosts": ["*.umea.se"], "hostOverrides": { "www.umea.se": { "requestsPerSecond": 2 } } }""";
            await db.SaveChangesAsync(Ct);
        }

        var source = new PlatformCrawlSettingsSource(Worker, TimeProvider.System, NullLogger<PlatformCrawlSettingsSource>.Instance);
        (await source.RefreshAsync(Ct)).ShouldBeTrue();

        var egress = ((IEgressPolicySource)source).Current;
        var crawl = ((ICrawlPolicySource)source).Current;
        egress.AllowedPorts.ShouldBe([8443]);
        egress.BlockedHosts.ShouldContain("bad.example");
        crawl.KindOf("www.umea.se").ShouldBe(HostKind.Municipal);
        HostBudgetPolicy.Budget(crawl.KindOf("www.umea.se"), crawl.OverrideOf("www.umea.se")).ShouldBe((2.0, 4));

        await using var nowhere = Npgsql.NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Timeout=1");
        var unreachable = new PlatformCrawlSettingsSource(new Infrastructure.Jobs.JobDatabase(nowhere), TimeProvider.System, NullLogger<PlatformCrawlSettingsSource>.Instance);
        (await unreachable.RefreshAsync(Ct)).ShouldBeFalse();
        ((IEgressPolicySource)unreachable).Current.ShouldBe(EgressPolicy.Default);
    }

    [Fact]
    public async Task Incremental_listing_marks_unchanged_seeds_and_the_urls_that_left_the_sitemap_are_found_and_cleared()
    {
        var store = new PostgresCrawlStore(Worker);
        var first = await StartRunAsync(store, maxPages: 100);
        var old = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
        string[] urls = ["https://www.umea.se/same", "https://www.umea.se/changed", "https://www.umea.se/left", "https://www.umea.se/linked"];
        await store.AdmitAsync(first, [.. urls.Take(3).Select(u => Admission(u, 0, old)), Admission(urls[3], 1)], Ct);
        foreach (var row in await store.ReleaseAsync(first, "www.umea.se", 10, Ct))
        {
            await store.CompleteAsync(first, row.UrlHash, new FrontierFacts(DateTimeOffset.UtcNow, 200, null, null, new string('b', 64), null), Ct);
        }

        var second = first with { RunId = Guid.NewGuid() };
        await store.StartAsync(new CrawlRunRecord { RunId = second.RunId, Classification = "public", SourceId = second.SourceId, MaxPages = 100, MaxDepth = 10 }, Ct);
        var listing = new[]
        {
            Admission(urls[0], 0, old), Admission(urls[1], 0, DateTimeOffset.UtcNow.AddDays(1)), Admission("https://www.umea.se/new", 0, old),
        };
        var listed = await store.MarkListedAsync(second, listing, Ct);

        listed.ShouldBe([listing[0].UrlHash]);
        (await store.GetAsync(second.SourceId, "public", listing[0].UrlHash, Ct))!.ShouldSatisfyAllConditions(
            r => r.RunId.ShouldBe(second.RunId), r => r.State.ShouldBe(FrontierState.Idle));
        (await store.GetAsync(second.RunId, Ct))!.Admitted.ShouldBe(0);
        await store.AdmitAsync(second, [.. listing.Where(a => !listed.Contains(a.UrlHash))], Ct);

        // Only the URL listed before and missing now has left the sitemap; the linked page never was in it.
        var left = await store.LeftSitemapAsync(second, Ct);
        left.Select(r => r.Url).ShouldBe([urls[2]]);
        var checks = await store.AdmitAsync(second, [.. left.Select(r => new FrontierAdmission(r.Url, r.Host, 0, false, RemovalCheck: true))], Ct);
        await store.ClearSitemapLastModAsync(second, [.. checks.Admitted.Select(r => r.UrlHash)], Ct);

        var check = (await store.GetAsync(second.SourceId, "public", checks.Admitted.Single().UrlHash, Ct))!;
        (check.Depth, check.State, check.SitemapLastMod, check.RemovalCheck).ShouldBe(((short)0, FrontierState.Pending, (DateTimeOffset?)null, true));
        (await store.GetAsync(second.RunId, Ct))!.ShouldSatisfyAllConditions(r => r.Seeds.ShouldBe(2), r => r.Admitted.ShouldBe(3));
        (await store.LeftSitemapAsync(second, Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_site_profile_reader_returns_the_applied_version_and_lastmod_reliable()
    {
        var reader = new PostgresSiteProfileReader(Worker);
        var profileId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var host = $"{Guid.NewGuid():N}.example";
        var profile = ContractJson.Serialize(new Pipelines.SiteProfiles.SiteProfile { MainRoot = "main", LastmodReliable = true });
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."SiteProfiles" ("Id", "Classification", "Host", "PresetId", "UpdatedAt") VALUES ($1, 'public', $2, 'sitevision', now());
            """, profileId, host);
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."SiteProfileVersions" ("Id", "SiteProfileId", "Number", "Profile", "Origin", "State", "CreatedBy")
            VALUES ($1, $2, 2, $3::jsonb, 'manual', 'applied', 'system:worker');
            """, versionId, profileId, profile);
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE content."SiteProfiles" SET "AppliedVersionId" = $1 WHERE "Id" = $2""", versionId, profileId);

        var applied = (await reader.GetAppliedAsync("public", host, Ct))!;
        (applied.SiteProfileId, applied.Number, applied.PresetId, applied.Profile.LastmodReliable).ShouldBe((profileId, 2, "sitevision", true));
        (await reader.GetAppliedAsync("internal", host, Ct)).ShouldBeNull();
        (await new LastmodReliability(reader, TimeProvider.System).IsReliableAsync("public", host + ":443", Ct)).ShouldBeTrue();
        (await new LastmodReliability(reader, TimeProvider.System).IsReliableAsync("public", "other.example", Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Against_the_database_a_404_after_leaving_the_sitemap_is_gone_and_a_200_is_kept()
    {
        var store = new PostgresCrawlStore(Worker);
        await using var rig = CrawlRig.Create(_site, hosts: new PostgresCrawlHostStore(Worker), store: (store, store));
        var source = Guid.NewGuid();
        var settings = ContractJson.ToElement(new CrawlSettings { StartUrls = [_site.Url("/").AbsoluteUri], Sitemaps = [_site.Url("/fresh.xml").AbsoluteUri] });
        CrawlRunContext Run() => new(new CrawlRunKey(source, "public", Guid.NewGuid()), Guid.NewGuid(), SyncKind.Sync, RunTrigger.Manual, settings, Revalidate: false);
        _fresh["/f/keep"] = "2026-01-15";
        _fresh["/f/gone"] = "2026-01-15";
        var first = Run();
        await rig.CrawlAsync(first);
        (await rig.Traversal.EvaluateAsync(first, false, Ct)).Complete.ShouldBeTrue();

        _fresh.Clear();
        _freshStatus["/f/gone"] = 404;
        var second = Run();
        var results = await rig.CrawlAsync(second);
        var verdict = await rig.Traversal.EvaluateAsync(second, false, Ct);

        verdict.Complete.ShouldBeTrue();
        results[_site.Url("/f/gone").AbsoluteUri].ShouldBeOfType<CrawlFetchResult.Done>().Outcome.ShouldBe(new FetchOutcome.Gone("http.404"));
        results[_site.Url("/f/keep").AbsoluteUri].ShouldBeOfType<CrawlFetchResult.Done>().ShouldSatisfyAllConditions(
            d => d.Outcome.ShouldBeOfType<FetchOutcome.Content>(), d => d.Facts.ShouldContain(CrawlTraversal.RemovalCheck));
        (await Sql.ScalarAsync<long>(database.Connection(null),
            """SELECT count(*) FROM content."CrawlFrontier" WHERE "SourceId" = $1 AND "RemovalCheck" AND "Depth" = 0 AND "SitemapLastMod" IS NULL AND "RunId" = $2""", source, second.Run.RunId))
            .ShouldBe(2);
    }

    [Fact]
    public async Task A_removal_check_row_written_at_depth_minus_one_before_the_flag_is_still_a_removal_check()
    {
        // A frontier row admitted for the removal check by a worker before #102 (Depth -1, RemovalCheck false) can outlive a deploy.
        var store = new PostgresCrawlStore(Worker);
        await using var rig = CrawlRig.Create(_site, hosts: new PostgresCrawlHostStore(Worker), store: (store, store));
        var source = Guid.NewGuid();
        var settings = ContractJson.ToElement(new CrawlSettings { StartUrls = [_site.Url("/").AbsoluteUri], Sitemaps = [_site.Url("/fresh.xml").AbsoluteUri] });
        CrawlRunContext Run() => new(new CrawlRunKey(source, "public", Guid.NewGuid()), Guid.NewGuid(), SyncKind.Sync, RunTrigger.Manual, settings, Revalidate: false);
        _fresh["/f/keep"] = "2026-01-15";
        _fresh["/f/gone"] = "2026-01-15";
        await rig.CrawlAsync(Run());
        _freshStatus["/f/gone"] = 404;
        var second = Run();
        await rig.Traversal.StartAsync(second, Ct);
        foreach (var path in new[] { "/f/keep", "/f/gone" })
        {
            await Sql.ExecuteAsync(database.Connection(null), """
                UPDATE content."CrawlFrontier" SET "State" = 'pending', "RunId" = $2, "Depth" = -1, "RemovalCheck" = false, "SitemapLastMod" = NULL
                WHERE "SourceId" = $1 AND "Url" = $3
                """, source, second.Run.RunId, _site.Url(path).AbsoluteUri);
        }

        await rig.TopUp.TopUpAsync(second, null, null, Ct);
        var results = await rig.DrainAsync(second);

        results.Keys.Order(StringComparer.Ordinal).ShouldBe([_site.Url("/f/gone").AbsoluteUri, _site.Url("/f/keep").AbsoluteUri]);
        results[_site.Url("/f/gone").AbsoluteUri].ShouldBeOfType<CrawlFetchResult.Done>().ShouldSatisfyAllConditions(
            d => d.Outcome.ShouldBe(new FetchOutcome.Gone("http.404")), d => d.Facts.ShouldContain(CrawlTraversal.RemovalCheck));
        results[_site.Url("/f/keep").AbsoluteUri].ShouldBeOfType<CrawlFetchResult.Done>().ShouldSatisfyAllConditions(
            d => d.Outcome.ShouldBeOfType<FetchOutcome.Content>(), d => d.Facts.ShouldContain(CrawlTraversal.RemovalCheck));
        (await store.GetAsync(second.Run.RunId, Ct))!.ShouldSatisfyAllConditions(r => r.Seeds.ShouldBe(0), r => r.SeedsFailed.ShouldBe(0));
    }

    /// <summary>
    /// to-verify #10 in P1 (connectors §6.0): the frontier upsert statement through the Npgsql store, 10,000 URLs of one
    /// source admitted in listing batches of 1,000, first as new rows and then again by the next run (the update branch).
    /// The rate goes to the test output; it is a development-machine number, not a capacity figure.
    /// </summary>
    [Fact]
    public async Task Ten_thousand_urls_are_admitted_and_admitted_again_by_the_next_run()
    {
        var store = new PostgresCrawlStore(Worker);
        var first = await StartRunAsync(store, 20_000);
        var urls = Enumerable.Range(0, 10_000).Select(i => new FrontierAdmission($"https://www.umea.se/sida/{i}.4.{i:x12}.html", "www.umea.se", 0, false)).ToList();
        async Task<TimeSpan> AdmitAllAsync(CrawlRunKey run)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var admitted = 0;
            foreach (var batch in urls.Chunk(1000))
            {
                admitted += (await store.AdmitAsync(run, batch, Ct)).Admitted.Count;
            }

            admitted.ShouldBe(urls.Count);
            return watch.Elapsed;
        }

        var inserted = await AdmitAllAsync(first);
        var second = new CrawlRunKey(first.SourceId, first.Classification, Guid.NewGuid());
        await store.StartAsync(new CrawlRunRecord { RunId = second.RunId, Classification = second.Classification, SourceId = second.SourceId, MaxPages = 20_000, MaxDepth = 10 }, Ct);
        var updated = await AdmitAllAsync(second);

        (await store.GetAsync(second.RunId, Ct))!.Admitted.ShouldBe(10_000);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"frontier upsert: 10,000 new rows in {inserted.TotalMilliseconds:F0} ms ({10_000 / inserted.TotalSeconds:F0}/s); 10,000 rows of the next run in {updated.TotalMilliseconds:F0} ms ({10_000 / updated.TotalSeconds:F0}/s)"));
    }

    private static async Task<CrawlRunKey> StartRunAsync(PostgresCrawlStore store, int maxPages)
    {
        var run = new CrawlRunKey(Guid.NewGuid(), "public", Guid.NewGuid());
        await store.StartAsync(new CrawlRunRecord { RunId = run.RunId, Classification = run.Classification, SourceId = run.SourceId, MaxPages = maxPages, MaxDepth = 10 }, Ct);
        return run;
    }

    private static FrontierAdmission Admission(string url, short depth, DateTimeOffset? lastmod = null) => new(url, new Uri(url).Authority, depth, false, lastmod);

    private static WorkItem Item(Uri url) => new(IdentityKind.Url, UrlCanonicalizer.Canonicalize(url.AbsoluteUri).Value!, url, 0, WorkHints.None);

    private static CrawlRunContext Context(System.Text.Json.JsonElement settings) =>
        new(new CrawlRunKey(Guid.NewGuid(), "public", Guid.NewGuid()), Guid.NewGuid(), SyncKind.Sync, RunTrigger.Manual, settings, Revalidate: false);

    private System.Text.Json.JsonElement Settings(int maxPages = 100) =>
        ContractJson.ToElement(new CrawlSettings { StartUrls = [_site.Url("/").AbsoluteUri], MaxPages = maxPages });

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>What the admin's commit and the worker's sync do: blobs, manifest, plan, slot writes.</summary>
    private static async Task<UploadBatchPlan> CommitAsync(PostgresUploadStore uploads, FileSystemBlobStore blobs, Guid sourceId, params (UploadOperation Operation, Guid Slot, string Name, byte[] Bytes)[] files)
    {
        foreach (var file in files)
        {
            await using var stream = new MemoryStream(file.Bytes);
            await blobs.PutAsync(UploadConnector.BlobKeyOf(sourceId, file.Slot, Sha(file.Bytes)), stream, file.Bytes.Length, Ct);
        }

        var manifest = new UploadBatchManifest
        {
            BatchId = Guid.NewGuid(),
            SourceId = sourceId,
            Entries = [.. files.Select(f => new UploadManifestEntry { Operation = f.Operation, UploadId = f.Slot, FileName = f.Name, Bytes = f.Bytes.Length, Sha256 = Sha(f.Bytes) })],
        };
        await using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(ContractJson.Serialize(manifest))))
        {
            await blobs.PutAsync(UploadConnector.ManifestKey(sourceId, manifest.BatchId), stream, null, Ct);
        }

        var connector = new UploadConnector(blobs, new EicarOnlyScanner(), uploads, TimeProvider.System);
        var plan = (await connector.PlanAsync(new ListingContext(sourceId, "internal", Guid.NewGuid(), default) { BatchId = manifest.BatchId }, Ct))!;
        await uploads.WriteAsync(plan.Writes, Ct);
        return plan;
    }

    private void Map(WebApplication app)
    {
        static IResult Html(string body) => Results.Content($"<html><body><main>{body}</main></body></html>", "text/html");

        app.MapGet("/fresh.xml", (HttpContext http) => Results.Content(
            "<?xml version=\"1.0\"?><urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">"
            + string.Concat(_fresh.Select(e => $"<url><loc>http://{http.Request.Host}{e.Key}</loc><lastmod>{e.Value}</lastmod></url>")) + "</urlset>",
            "application/xml"));
        app.MapMethods("/f/{name}", ["GET", "HEAD"], (string name) =>
            _freshStatus.TryGetValue($"/f/{name}", out var status) ? Results.StatusCode(status) : Html($"<p>{name}</p>"));

        app.MapGet("/robots.txt", (HttpContext http) => Results.Text($"User-agent: *\nSitemap: http://{http.Request.Host}/sitemap1.xml.gz\n"));
        app.MapGet("/sitemap1.xml.gz", (HttpContext http) =>
        {
            var xml = $"<?xml version=\"1.0\"?><urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\"><url><loc>http://{http.Request.Host}/a</loc></url><url><loc>http://{http.Request.Host}/b</loc></url></urlset>";
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(Encoding.UTF8.GetBytes(xml));
            }

            return Results.Bytes(buffer.ToArray(), "application/gzip");
        });
        app.MapGet("/", () => Html("<a href=\"/a\">A</a><a href=\"/c\">C</a><a href=\"/d\">D</a>"));
        app.MapGet("/a", () => Html("<p>A</p>"));
        app.MapGet("/b", () => Html("<p>B</p>"));
        app.MapGet("/c", () => Html("<p>C</p>"));
        app.MapGet("/d", () => Html("<p>D</p>"));
    }

    /// <summary>Records every slot the statement took.</summary>
    private sealed class RecordingHostStore(ICrawlHostStore inner) : ICrawlHostStore
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<DateTimeOffset> _slots = [];

        public IReadOnlyCollection<DateTimeOffset> Slots => _slots;

        public Task<CrawlHostState> EnsureAsync(CrawlHostState initial, CancellationToken cancellationToken) => inner.EnsureAsync(initial, cancellationToken);

        public async Task<SlotTake?> TryTakeSlotAsync(string host, TimeSpan interval, TimeSpan maxWait, CancellationToken cancellationToken)
        {
            var slot = await inner.TryTakeSlotAsync(host, interval, maxWait, cancellationToken);
            if (slot is { Taken: true } taken)
            {
                _slots.Add(taken.At);
            }

            return slot;
        }

        public Task<CrawlHostState> RecordResponseAsync(string host, HostResponse response, CancellationToken cancellationToken) =>
            inner.RecordResponseAsync(host, response, cancellationToken);

        public Task PauseAsync(string host, DateTimeOffset until, CancellationToken cancellationToken) => inner.PauseAsync(host, until, cancellationToken);

        public Task RecordRobotsAsync(string host, RobotsStatus status, DateTimeOffset fetchedAt, int? crawlDelaySeconds, CancellationToken cancellationToken) =>
            inner.RecordRobotsAsync(host, status, fetchedAt, crawlDelaySeconds, cancellationToken);
    }
}
