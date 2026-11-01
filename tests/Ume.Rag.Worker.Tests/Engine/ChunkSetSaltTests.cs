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
using Ume.Rag.Worker.Tests.CommandJobs;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.Engine;

/// <summary>
/// The salt of the chunk-set key, <c>ChunkSets."ParseRuleKey"</c> (#134; engine design §14): an admin parse-rule change
/// makes a document's live set stale, so the change's <c>reprocess</c> sync writes a new set keyed by the rule
/// (<c>rule:{profileId}@{revision}</c>) and swaps it in, and disabling the rule swaps the kept built-in set back; a forced
/// reprocess writes its own set (<c>force:{executionId}</c>, before the rule part when a rule applies) and swaps it in, and
/// later normal runs of the unchanged content keep it. Page texts and paths carry the test's marker.
/// </summary>
[Collection(RagDatabaseGroup2.Name)]
public sealed class ChunkSetSaltTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-chunk-set-salt-");
    private readonly string _marker = Guid.NewGuid().ToString("N");

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_parse_rule_change_puts_a_new_set_live_through_its_reprocess_sync_and_disabling_it_swaps_the_old_set_back()
    {
        await using var site = await EngineKit.SiteAsync((name, _) =>
            Task.FromResult(Results.Bytes(Encoding.ASCII.GetBytes($"%PDF-1.7\n% {name}-{_marker}\n%%EOF\n"), "application/pdf")));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url($"/p/kallelse-{_marker}.pdf")]);
        var converter = new OcrConverter();
        var profileId = Guid.CreateVersion7();
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, services: services =>
        {
            services.AddSingleton<IParserConverter>(converter);
            services.AddSingleton<IParserBuild>(new FixedBuild());
            services.AddSingleton<IDocumentInspector>(new FakeInspector(new InspectFacts("pdf", 1, true, [new InspectPageSignals(1) { TextChars = 1000, ImageCoverage = 0 }])));
        });
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(1);
            var document = await Sql.ScalarAsync<Guid>(database.Connection(null), """SELECT "Id" FROM content."Documents" WHERE "SourceId" = $1""", seed.SourceId);
            var (builtin, builtinSalt) = await LiveAsync(document);
            builtinSalt.ShouldBeNull("no admin rule applies yet");

            var edit = await CommandAsync(seed, new EditParseRuleCommand
            {
                ProfileId = profileId,
                DepartmentId = seed.DepartmentId,
                Match = new ParseRuleMatch { PathGlob = $"/p/kallelse-{_marker}*", MediaType = "application/pdf" },
                Strategy = new ParseRuleStrategy { Ocr = "force" },
            });
            var ruleSalt = $"rule:{profileId:D}@{edit.GetProperty("revision").GetString()}";
            var ruleRun = await ReleaseSyncAsync(seed);
            (await EngineKit.WaitForRunAsync(database, ruleRun)).ShouldBe(new RunCounters(RunStates.Succeeded, 0, 1, 0, 0, 0, 0));
            var (ruled, ruledSalt) = await LiveAsync(document);
            ruled.ShouldNotBe(builtin, "the rule's parse output is a new set");
            ruledSalt.ShouldBe(ruleSalt);
            (await SetStateAsync(builtin)).ShouldBe(ChunkSetStates.Superseded);
            converter.Modes.Count.ShouldBe(2);
            converter.Modes[^1].ShouldBe("Force", "the rule forces OCR");

            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed, SyncKind.Reprocess))).Unchanged.ShouldBe(1,
                "the live set has the rule's key: a normal reprocess has nothing to do");

            var execution = await ReprocessDocumentAsync(seed, document);
            var (forced, forcedSalt) = await LiveAsync(document);
            forced.ShouldNotBe(ruled, "a forced reprocess writes its own set");
            forcedSalt.ShouldBe($"force:{execution:N};{ruleSalt}");
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed, SyncKind.Reprocess))).Unchanged.ShouldBe(1,
                "the forced set carries the rule part the document wants");
            (await LiveAsync(document)).Id.ShouldBe(forced);

            (await CommandAsync(seed, new DisableParseRuleCommand { ProfileId = profileId })).GetProperty("state").GetString().ShouldBe("disabled");
            var disableRun = await ReleaseSyncAsync(seed);
            (await EngineKit.WaitForRunAsync(database, disableRun)).ShouldBe(new RunCounters(RunStates.Succeeded, 0, 1, 0, 0, 0, 0));
            (await LiveAsync(document)).ShouldBe((builtin, (string?)null), "without the rule the kept built-in set is swapped back");
            (await ItemsAsync(disableRun)).ShouldBe($"updated:{ChunkSetSwap.SwappedBackReason}");
            converter.Modes.Count.ShouldBe(2, "neither the forced run from embed nor the swap-back parses");
            (await EngineKit.DuplicateSetsAsync(database, seed)).ShouldBe(0);
            (await EngineKit.StrayChunksAsync(database, seed)).ShouldBe(0);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task A_forced_reprocess_of_unchanged_content_swaps_in_its_own_set_and_later_normal_runs_keep_it()
    {
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(EngineKit.Html(EngineKit.Page($"bygglov {name} {_marker}"))));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url($"/p/a-{_marker}")]);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(1);
            var document = await Sql.ScalarAsync<Guid>(database.Connection(null), """SELECT "Id" FROM content."Documents" WHERE "SourceId" = $1""", seed.SourceId);
            var (original, _) = await LiveAsync(document);

            var execution = await ReprocessDocumentAsync(seed, document, "chunk");
            var (forced, forcedSalt) = await LiveAsync(document);
            forced.ShouldNotBe(original, "the same chunks are not re-keyed: a forced run writes its own set");
            forcedSalt.ShouldBe($"force:{execution:N}");
            (await SetStateAsync(original)).ShouldBe(ChunkSetStates.Superseded);

            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Unchanged.ShouldBe(1,
                "a normal sync of the unchanged page keeps the forced set");
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed, SyncKind.Reprocess))).Unchanged.ShouldBe(1);
            (await LiveAsync(document)).Id.ShouldBe(forced);

            var run = await ForcedSyncAsync(seed, "parse");
            (await EngineKit.WaitForRunAsync(database, run)).ShouldBe(new RunCounters(RunStates.Succeeded, 0, 1, 0, 0, 0, 0));
            var (again, againSalt) = await LiveAsync(document);
            again.ShouldNotBe(forced);
            againSalt.ShouldBe($"force:{DocumentIngest.ForcedExecutionId(run, document):N}");
            (await EngineKit.DuplicateSetsAsync(database, seed)).ShouldBe(0);
            (await EngineKit.StrayChunksAsync(database, seed)).ShouldBe(0);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    /// <summary>The document's live set and its salt.</summary>
    private async Task<(Guid Id, string? Salt)> LiveAsync(Guid document)
    {
        var row = await Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT s."Id"::text || '|' || coalesce(s."ParseRuleKey", '-')
            FROM content."ChunkSets" s JOIN content."Documents" d ON d."LiveChunkSetId" = s."Id" WHERE d."Id" = $1
            """, document);
        var parts = row.Split('|', 2);
        return (Guid.Parse(parts[0]), parts[1] == "-" ? null : parts[1]);
    }

    private Task<string> SetStateAsync(Guid chunkSet) =>
        Sql.ScalarAsync<string>(database.Connection(null), """SELECT "State" FROM content."ChunkSets" WHERE "Id" = $1""", chunkSet);

    private Task<string> ItemsAsync(Guid run) =>
        Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT string_agg("Outcome" || ':' || coalesce("Reason", '-'), ',' ORDER BY "DocumentId") FROM content."RunItems" WHERE "RunId" = $1
            """, run);

    /// <summary>Enqueues the parse-rule command as the admin does, waits for it and returns its result.</summary>
    private async Task<System.Text.Json.JsonElement> CommandAsync<T>(SourceSeed seed, T command) where T : JobPayload
    {
        var profileId = command switch
        {
            EditParseRuleCommand e => e.ProfileId,
            DisableParseRuleCommand d => d.ProfileId,
            _ => throw new ArgumentException("Not a parse-rule command.", nameof(command)),
        };
        var request = new JobRequest(JobClasses.Command, seed.DepartmentId, seed.Classification, ContractJson.Serialize(command))
        {
            DedupKey = JobDedupKeys.ParseRule(profileId),
        };
        (await database.Queue(RagDatabase.Admin).EnqueueAsync([request], null, Ct)).ShouldContain(request.Id);
        await Commands.WaitForStateAsync(database, request.Id, JobStates.Succeeded);
        return await CommandSeeds.ResultAsync(database, request.Id);
    }

    /// <summary>
    /// Makes the <c>reprocess</c> sync the command enqueued available now instead of after its settle delay (this worker's
    /// rule cache was dropped by the command itself) and returns its id, which is its run's.
    /// </summary>
    private async Task<Guid> ReleaseSyncAsync(SourceSeed seed)
    {
        var job = await Sql.ScalarAsync<Guid>(database.Connection(null), """
            UPDATE content."Jobs" SET "AvailableAt" = now()
            WHERE "Class" = 'sync' AND "State" = 'queued' AND "Payload" ->> 'sourceId' = $1 AND "Payload" ->> 'kind' = 'reprocess'
            RETURNING "Id"
            """, seed.SourceId.ToString());
        job.ShouldNotBe(Guid.Empty, "the command enqueued the source's reprocess sync");
        return job;
    }

    /// <summary>A <c>reprocess-document</c> from <paramref name="fromStage"/>; returns the execution it ran under once its run has finished.</summary>
    private async Task<Guid> ReprocessDocumentAsync(SourceSeed seed, Guid document, string fromStage = "embed")
    {
        var command = new ReprocessDocumentCommand { DocumentId = document, FromStage = fromStage };
        var request = new JobRequest(JobClasses.Command, seed.DepartmentId, seed.Classification, ContractJson.Serialize(command))
        {
            DedupKey = JobDedupKeys.Reprocess(document),
        };
        (await database.Queue(RagDatabase.Admin).EnqueueAsync([request], null, Ct)).ShouldContain(request.Id);
        await Commands.WaitForStateAsync(database, request.Id, JobStates.Succeeded);
        (await EngineKit.WaitForRunAsync(database, request.Id)).ShouldBe(new RunCounters(RunStates.Succeeded, 0, 1, 0, 0, 0, 0));
        return (await CommandSeeds.ResultAsync(database, request.Id)).GetProperty("executionId").GetGuid();
    }

    /// <summary>A forced <c>reprocess</c> sync as the admin would enqueue it; the run's id is the job's.</summary>
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

    /// <summary>A parser whose text names the file and the OCR mode, so a rule that forces OCR changes the chunks; records the modes.</summary>
    private sealed class OcrConverter : IParserConverter
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _modes = new();

        public IReadOnlyList<string> Modes => [.. _modes];

        public Task<ParserConvertResult> ConvertAsync(ReadOnlyMemory<byte> file, ParserConvertOptions options, CancellationToken cancellationToken)
        {
            _modes.Enqueue(options.Ocr.Mode.ToString());
            var name = Encoding.ASCII.GetString(file.Span).Split('\n')[1];
            var result = FakeConverter.Pages(options.Pages!.From, options.Pages.To);
            return Task.FromResult(result with { Blocks = [.. result.Blocks.Select(b => b with { Md = $"{b.Md} {name} ocr {options.Ocr.Mode}" })] });
        }
    }

    private sealed class FixedBuild : IParserBuild
    {
        public Task<string> GetAsync(CancellationToken cancellationToken) => Task.FromResult("sha256:chunk-set-salt");
    }
}
