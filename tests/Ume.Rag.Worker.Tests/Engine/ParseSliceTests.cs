using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Worker.Parsing;
using Ume.Rag.Worker.Tests.Blobs;
using Ume.Rag.Worker.Tests.Connectors;
using Ume.Rag.Worker.Processing.Detect;
using Ume.Rag.Worker.Processing.Parse;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.Engine;

/// <summary>
/// Parse slicing and resumption in the engine (engine design §9.5, §9.4 "Crash", §19 "worker restart loses no work"): a
/// four-range PDF with a used-up slice runs as four <c>parse</c> jobs with slices 0–3, each asking the parser for one new
/// range and using one attempt, and ends with the same parse output as an unsliced run; a <c>parse</c> job killed during
/// range 3 is reclaimed and resumes at range 3. The parse cache is keyed by the parser build: another build misses, the
/// same build hits (simplification pass §2, parser design §7.6).
/// </summary>
[Collection(RagDatabaseGroup4.Name)]
public sealed class ParseSliceTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private const int Pages = 4;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-parse-slices-");
    private readonly byte[] _pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n% " + Guid.NewGuid().ToString("N") + "\n%%EOF\n");

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_multi_range_parse_yields_per_slice_without_attempts_and_ends_equal_to_the_unsliced_parse()
    {
        await using var site = await PdfSiteAsync();
        var sliced = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/sliced")]);
        var whole = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/whole")]);
        try
        {
            var converter = new RangeConverter();
            Guid slicedRun;
            await using (var worker = await StartAsync(site, converter, new FixedBuild("sha256:slice"), sliceSeconds: 0))
            {
                slicedRun = await EngineKit.SyncNowAsync(database, sliced);
                (await EngineKit.WaitForRunAsync(database, slicedRun)).Added.ShouldBe(1);
            }

            converter.Ranges.ShouldBe(["1-1", "2-2", "3-3", "4-4"], "each slice asks only for its new range");
            (await ParseJobsAsync(slicedRun)).ShouldBe(["0:succeeded:1:0", "1:succeeded:1:0", "2:succeeded:1:0", "3:succeeded:1:0"],
                "slices 0-3, one attempt each: a yield uses none");

            // The same bytes under another build miss the cache and parse unsliced in one job.
            var unsliced = new RangeConverter();
            Guid wholeRun;
            await using (var worker = await StartAsync(site, unsliced, new FixedBuild("sha256:whole"), sliceSeconds: 600))
            {
                wholeRun = await EngineKit.SyncNowAsync(database, whole);
                (await EngineKit.WaitForRunAsync(database, wholeRun)).Added.ShouldBe(1);
            }

            unsliced.Ranges.ShouldBe(["1-1", "2-2", "3-3", "4-4"]);
            (await ParseJobsAsync(wholeRun)).ShouldBe(["0:succeeded:1:0"]);
            (await ParseOutputAsync(sliced)).ShouldBe(await ParseOutputAsync(whole), "the sliced parse output equals the unsliced one");
        }
        finally
        {
            await EngineKit.PauseAsync(database, sliced);
            await EngineKit.PauseAsync(database, whole);
        }
    }

    [Fact]
    public async Task A_parse_job_killed_after_range_k_resumes_at_range_k_plus_one()
    {
        await using var site = await PdfSiteAsync();
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/doc")]);
        try
        {
            var converter = new RangeConverter { BlockAt = 3 };
            var build = new FixedBuild("sha256:kill");
            var first = await StartAsync(site, converter, build, sliceSeconds: 600);
            var runId = await EngineKit.SyncNowAsync(database, seed);
            await converter.Reached.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            await first.KillAsync();
            var beforeRestart = converter.Ranges.Count;
            converter.Release.TrySetResult();

            await using (var second = await StartAsync(site, converter, build, sliceSeconds: 600))
            {
                (await EngineKit.WaitForRunAsync(database, runId)).Added.ShouldBe(1);
            }

            converter.Ranges.Take(beforeRestart).ShouldBe(["1-1", "2-2", "3-3"]);
            converter.Ranges.Skip(beforeRestart).ShouldBe(["3-3", "4-4"], "the reclaimed job resumes at the range the kill interrupted");
            (await ParseJobsAsync(runId)).ShouldBe(["0:succeeded:1:1"], "reclaimed once, the attempt the kill used given back");
            (await EngineKit.DuplicateSetsAsync(database, seed)).ShouldBe(0);
            (await EngineKit.StrayChunksAsync(database, seed)).ShouldBe(0);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task The_parse_cache_misses_for_another_parser_build_and_hits_for_the_same()
    {
        await using var site = await PdfSiteAsync();
        var first = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a")]);
        var same = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/b")]);
        var other = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/c")]);
        try
        {
            var converter = new RangeConverter();
            var build = new FixedBuild("sha256:one");
            await using (var worker = await StartAsync(site, converter, build, sliceSeconds: 600))
            {
                (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, first))).Added.ShouldBe(1);
                converter.Ranges.Count.ShouldBe(Pages);

                (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, same))).Added.ShouldBe(1);
                converter.Ranges.Count.ShouldBe(Pages, "same content and build: the parse is a cache hit");

                build.Value = "sha256:two";
                (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, other))).Added.ShouldBe(1);
                converter.Ranges.Count.ShouldBe(2 * Pages, "another parser build: the parse is a cache miss");
            }

            (await ParseCacheKeyAsync(first)).ShouldBe(await ParseCacheKeyAsync(same));
            (await ParseCacheKeyAsync(other)).ShouldNotBe(await ParseCacheKeyAsync(first));
        }
        finally
        {
            await EngineKit.PauseAsync(database, first);
            await EngineKit.PauseAsync(database, same);
            await EngineKit.PauseAsync(database, other);
        }
    }

    private Task<TestSite> PdfSiteAsync() =>
        EngineKit.SiteAsync((_, _) => Task.FromResult(Results.Bytes(_pdf, "application/pdf")));

    private Task<WorkerHost> StartAsync(TestSite site, RangeConverter converter, FixedBuild build, int sliceSeconds) =>
        EngineKit.StartWorkerAsync(database, site, gateway, _blobs, settings: new Dictionary<string, string?>
        {
            ["Worker:Parse:RangePages:text-layer"] = "1",
            ["Worker:Parse:SliceSeconds"] = sliceSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }, services: services =>
        {
            services.AddSingleton<IParserConverter>(converter);
            services.AddSingleton<IParserBuild>(build);
            services.AddSingleton<IDocumentInspector>(new FakeInspector(new InspectFacts("pdf", Pages, true,
                [.. Enumerable.Range(1, Pages).Select(p => new InspectPageSignals(p) { TextChars = 1000, ImageCoverage = 0 })])));
        });

    /// <summary>The run's parse jobs as <c>slice:state:attempts:reclaims</c>, by slice.</summary>
    private async Task<IReadOnlyList<string>> ParseJobsAsync(Guid runId)
    {
        var text = await Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT string_agg(("Payload" ->> 'slice') || ':' || "State" || ':' || "Attempts" || ':' || "Reclaims", ',' ORDER BY ("Payload" ->> 'slice')::int)
            FROM content."Jobs" WHERE "RunId" = $1 AND "Class" = 'parse'
            """, runId);
        return text?.Split(',') ?? [];
    }

    private Task<byte[]> ParseCacheKeyAsync(SourceSeed seed) =>
        Sql.ScalarAsync<byte[]>(database.Connection(null), """SELECT "ParseCacheKey" FROM content."Documents" WHERE "SourceId" = $1""", seed.SourceId);

    /// <summary>The document's stored parse output (<c>result.json</c>).</summary>
    private async Task<string> ParseOutputAsync(SourceSeed seed)
    {
        var path = await Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT o."BlobPath" FROM content."NodeOutputs" o JOIN content."Documents" d ON d."ParseCacheKey" = o."CacheKey" WHERE d."SourceId" = $1
            """, seed.SourceId);
        // Parse outputs of an internal source are envelope-encrypted (data-model §7): read through the envelope.
        var blobs = TestBlobKeys.Store(_blobs);
        await using var stream = await blobs.OpenReadAsync(new BlobKey(path + "/result.json"), null, Ct) ?? throw new InvalidOperationException("No parse output.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(Ct);
    }

    /// <summary>A parser that answers every range with one paragraph per page, records the ranges asked, and can block one.</summary>
    private sealed class RangeConverter : IParserConverter
    {
        private readonly ConcurrentQueue<string> _ranges = new();

        public int? BlockAt { get; init; }

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> Ranges => [.. _ranges];

        public async Task<ParserConvertResult> ConvertAsync(ReadOnlyMemory<byte> file, ParserConvertOptions options, CancellationToken cancellationToken)
        {
            var pages = options.Pages!;
            _ranges.Enqueue($"{pages.From}-{pages.To}");
            if (pages.From == BlockAt && !Release.Task.IsCompleted)
            {
                Reached.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return FakeConverter.Pages(pages.From, pages.To);
        }
    }

    private sealed class FixedBuild(string value) : IParserBuild
    {
        public string Value { get; set; } = value;

        public Task<string> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Value);
    }
}
