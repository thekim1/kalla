using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Parsing;
using Ume.Rag.Worker.Processing.Detect;
using Ume.Rag.Worker.Processing.Parse;
using Ume.Rag.Worker.Tests.Connectors;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.Engine;

/// <summary>
/// The explicit cache bypass (engine design §7.5, §8; admin design §4.7): a <c>sync</c> <c>reprocess</c> with <c>force</c>
/// and <c>fromStage</c> re-processes every live document of the source under a new execution id, its stages from
/// <c>fromStage</c> on under new keys (the parser is called again) and the earlier ones from the cache; a
/// <c>reprocess-document</c> bypasses from its <c>fromStage</c>. The old outputs stay (write-once). Each PDF's content and
/// parse output carry the test's marker, so the outputs counted are this test's.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class ForcedReprocessTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-forced-");
    private readonly string _marker = Guid.NewGuid().ToString("N");
    private readonly CountingConverter _converter = new();

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_forced_reprocess_of_a_source_re_parses_every_live_document_under_a_new_execution()
    {
        await using var site = await PdfSiteAsync();
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url($"/p/a-{_marker}.pdf"), site.Url($"/p/b-{_marker}.pdf")]);
        await using var worker = await StartAsync(site);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(2);
            _converter.Calls.ShouldBe(2);
            var documents = new[] { await DocumentAsync(seed, "/p/a-"), await DocumentAsync(seed, "/p/b-") };
            var before = new Dictionary<Guid, (byte[] ParseKey, Guid Execution, byte[] Content)>();
            foreach (var document in documents)
            {
                before[document] = (await ParseKeyAsync(document), await ExecutionAsync(document), await ContentHashAsync(document));
            }

            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed, SyncKind.Reprocess))).Unchanged.ShouldBe(2);
            _converter.Calls.ShouldBe(2, "without force a reprocess recomputes nothing");

            var run = await ForcedSyncAsync(seed, "parse");
            (await EngineKit.WaitForRunAsync(database, run)).ShouldBe(new RunCounters(RunStates.Succeeded, 0, 0, 2, 0, 0, 0));
            _converter.Calls.ShouldBe(4, "a forced reprocess from parse calls the parser again for every live document");
            foreach (var document in documents)
            {
                var (parseKey, execution, content) = before[document];
                var forced = await ExecutionAsync(document);
                forced.ShouldNotBe(execution);
                forced.ShouldBe(DocumentIngest.ForcedExecutionId(run, document));
                (await ParseKeyAsync(document)).ShouldNotBe(parseKey, "the forced parse is stored under a new key");
                (await OutputsAsync(content, "detect")).ShouldBe(1, "detect, before fromStage, is served from the cache");
                (await OutputsAsync(content, "parse")).ShouldBe(2);
                (await OutputExistsAsync(parseKey)).ShouldBeTrue("the old parse output is never overwritten");
                File.Exists(Path.Join(_blobs.FullName, "parse", seed.Classification, Convert.ToHexStringLower(parseKey), "result.json")).ShouldBeTrue();
                (await Sql.ScalarAsync<string>(database.Connection(null), """
                    SELECT string_agg("Outcome" || ':' || "ExecutionId"::text, ',') FROM content."RunItems" WHERE "RunId" = $1 AND "DocumentId" = $2
                    """, run, document)).ShouldBe($"unchanged:{forced}");
            }

            (await EngineKit.LiveChunksAsync(database, seed)).ShouldBeGreaterThanOrEqualTo(2);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_forced_reprocess_from_fetch_is_refused()
    {
        await using var site = await PdfSiteAsync();
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url($"/p/a-{_marker}.pdf")]);
        await using var worker = await StartAsync(site);
        try
        {
            var job = await ForcedSyncAsync(seed, "fetch");
            await Commands.WaitForStateAsync(database, job, JobStates.Failed);
            (await Sql.JobAsync(database.Connection(null), job)).LastError.ShouldBe(StageBypass.FetchUnsupported);
            _converter.Calls.ShouldBe(0);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task Reprocess_document_bypasses_the_cache_from_its_from_stage()
    {
        await using var site = await PdfSiteAsync();
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url($"/p/a-{_marker}.pdf")]);
        await using var worker = await StartAsync(site);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(1);
            var document = await DocumentAsync(seed, "/p/a-");
            var content = await ContentHashAsync(document);
            var parseKey = await ParseKeyAsync(document);
            (await OutputsAsync(content, "chunk")).ShouldBe(1);
            var chunkKey = await FirstOutputKeyAsync(content, "chunk");

            await ReprocessDocumentAsync(seed, document, "chunk");
            _converter.Calls.ShouldBe(1, "a bypass from chunk serves parse from the cache");
            (await ParseKeyAsync(document)).ShouldBe(parseKey);
            (await OutputsAsync(content, "parse")).ShouldBe(1);
            (await OutputsAsync(content, "clean")).ShouldBe(1, "clean, before fromStage, is served from the cache");
            (await OutputsAsync(content, "chunk")).ShouldBe(2, "chunk runs again and is stored under a new key");
            (await OutputsAsync(content, "personal-data")).ShouldBe(2, "every stage after fromStage is bypassed too");
            (await OutputExistsAsync(chunkKey)).ShouldBeTrue("the old chunk output is never overwritten");

            await ReprocessDocumentAsync(seed, document, "parse");
            _converter.Calls.ShouldBe(2, "a bypass from parse calls the parser again");
            (await ParseKeyAsync(document)).ShouldNotBe(parseKey);
            (await OutputsAsync(content, "parse")).ShouldBe(2);
            (await OutputExistsAsync(parseKey)).ShouldBeTrue("the old parse output is never overwritten");
            (await EngineKit.LiveChunksAsync(database, seed)).ShouldBeGreaterThanOrEqualTo(1);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    /// <summary>
    /// The <c>NodeOutputs</c> rows of one content's <paramref name="stage"/> (engine design §3.3): <c>detect</c> and
    /// <c>parse</c> take the content hash as input, every later stage its predecessor's outputs.
    /// </summary>
    private static string OutputsWhere(string stage)
    {
        var where = "\"InputHash\" = $1";
        if (stage is "detect" or "parse")
        {
            return $"\"NodeType\" = '{stage}' AND {where}";
        }

        string[] chain = ["parse", "clean", "chunk", "personal-data"];
        for (var i = 1; i <= Array.IndexOf(chain, stage); i++)
        {
            where = $"\"InputHash\" IN (SELECT \"OutputHash\" FROM content.\"NodeOutputs\" WHERE \"NodeType\" = '{chain[i - 1]}' AND {where})";
        }

        return $"\"NodeType\" = '{stage}' AND {where}";
    }

    private Task<long> OutputsAsync(byte[] content, string stage) =>
        Sql.ScalarAsync<long>(database.Connection(null), $"""SELECT count(*) FROM content."NodeOutputs" WHERE {OutputsWhere(stage)}""", content);

    private Task<byte[]> FirstOutputKeyAsync(byte[] content, string stage) =>
        Sql.ScalarAsync<byte[]>(database.Connection(null), $"""
            SELECT "CacheKey" FROM content."NodeOutputs" WHERE {OutputsWhere(stage)} ORDER BY "CreatedAt", "CacheKey" LIMIT 1
            """, content);

    private Task<bool> OutputExistsAsync(byte[] key) =>
        Sql.ScalarAsync<bool>(database.Connection(null), """SELECT EXISTS (SELECT 1 FROM content."NodeOutputs" WHERE "CacheKey" = $1)""", key);

    private Task<TestSite> PdfSiteAsync() =>
        EngineKit.SiteAsync((name, _) => Task.FromResult(Results.Bytes(Encoding.ASCII.GetBytes($"%PDF-1.7\n% {name}-{_marker}\n%%EOF\n"), "application/pdf")));

    private Task<WorkerHost> StartAsync(TestSite site) =>
        EngineKit.StartWorkerAsync(database, site, gateway, _blobs, services: services =>
        {
            services.AddSingleton<IParserConverter>(_converter);
            services.AddSingleton<IParserBuild>(new FixedBuild());
            services.AddSingleton<IDocumentInspector>(new FakeInspector(new InspectFacts("pdf", 1, true, [new InspectPageSignals(1) { TextChars = 1000, ImageCoverage = 0 }])));
        });

    /// <summary>A forced <c>reprocess</c> as the admin would enqueue it (dedup <c>sync:{sourceId}</c>); the run's id is the job's.</summary>
    private async Task<Guid> ForcedSyncAsync(SourceSeed seed, string fromStage)
    {
        var payload = new SyncPayload { SourceId = seed.SourceId, Kind = SyncKind.Reprocess, Trigger = RunTrigger.Manual, Force = true, FromStage = fromStage };
        var job = new JobRequest(JobClasses.Sync, seed.DepartmentId, seed.Classification, ContractJson.Serialize(payload))
        {
            DedupKey = JobDedupKeys.Sync(seed.SourceId),
            Priority = 10,
        };
        (await database.Queue(RagDatabase.Admin).EnqueueAsync([job], null, Ct)).ShouldContain(job.Id);
        return job.Id;
    }

    private async Task ReprocessDocumentAsync(SourceSeed seed, Guid document, string fromStage)
    {
        var command = new ReprocessDocumentCommand { DocumentId = document, FromStage = fromStage };
        var request = new JobRequest(JobClasses.Command, seed.DepartmentId, seed.Classification, ContractJson.Serialize(command))
        {
            DedupKey = JobDedupKeys.Reprocess(document),
        };
        (await database.Queue(RagDatabase.Admin).EnqueueAsync([request], null, Ct)).ShouldContain(request.Id);
        await Commands.WaitForStateAsync(database, request.Id, JobStates.Succeeded);
        await EngineKit.WaitForRunAsync(database, request.Id);
    }

    private Task<Guid> DocumentAsync(SourceSeed seed, string path) =>
        Sql.ScalarAsync<Guid>(database.Connection(null), """
            SELECT "Id" FROM content."Documents" WHERE "SourceId" = $1 AND "CanonicalIdentity" LIKE '%' || $2 || '%'
            """, seed.SourceId, path);

    private Task<byte[]> ParseKeyAsync(Guid document) =>
        Sql.ScalarAsync<byte[]>(database.Connection(null), """SELECT "ParseCacheKey" FROM content."Documents" WHERE "Id" = $1""", document);

    private Task<byte[]> ContentHashAsync(Guid document) =>
        Sql.ScalarAsync<byte[]>(database.Connection(null), """SELECT "ContentHash" FROM content."Documents" WHERE "Id" = $1""", document);

    private Task<Guid> ExecutionAsync(Guid document) =>
        Sql.ScalarAsync<Guid>(database.Connection(null), """SELECT "CurrentExecutionId" FROM content."Documents" WHERE "Id" = $1""", document);

    /// <summary>A parser that counts calls and answers one paragraph per page that names the file (its comment line), so each document's parse output is its own.</summary>
    private sealed class CountingConverter : IParserConverter
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<ParserConvertResult> ConvertAsync(ReadOnlyMemory<byte> file, ParserConvertOptions options, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var name = Encoding.ASCII.GetString(file.Span).Split('\n')[1];
            var result = FakeConverter.Pages(options.Pages!.From, options.Pages.To);
            return Task.FromResult(result with { Blocks = [.. result.Blocks.Select(b => b with { Md = $"{b.Md} {name}" })] });
        }
    }

    private sealed class FixedBuild : IParserBuild
    {
        public Task<string> GetAsync(CancellationToken cancellationToken) => Task.FromResult("sha256:forced-reprocess");
    }
}
