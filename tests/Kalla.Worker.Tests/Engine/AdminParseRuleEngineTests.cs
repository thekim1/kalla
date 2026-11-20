using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Infrastructure.Config;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.TestKit;
using Kalla.Worker.Parsing;
using Kalla.Worker.Processing.Detect;
using Kalla.Worker.Processing.Parse;
using Kalla.Worker.Tests.Processing;

namespace Kalla.Worker.Tests.Engine;

/// <summary>
/// Admin-made parse rules in the engine (P1b item 8; processing design §3.5): the matched rules are the parse stage's
/// <c>parseRule</c> input, so a rule change re-keys only the documents it matches. Two PDFs of one source are parsed; a
/// rule for one path is added and the worker reads it at the next config version; re-running both documents re-parses
/// only the matching one, with the rule's override, while the other keeps its key; removing the rule returns the document
/// to its built-in key, which the parse cache serves without a parser call.
/// </summary>
[Collection(RagDatabaseGroup1.Name)]
public sealed class AdminParseRuleEngineTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-parse-rules-");
    private readonly string _marker = Guid.NewGuid().ToString("N");

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_rule_change_re_keys_only_the_documents_it_matches()
    {
        await using var site = await EngineKit.SiteAsync((name, _) =>
            Task.FromResult(Results.Bytes(Encoding.ASCII.GetBytes($"%PDF-1.7\n% {name} {_marker}\n%%EOF\n"), "application/pdf")));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url($"/p/kallelse-{_marker}.pdf"), site.Url($"/p/rapport-{_marker}.pdf")]);
        var converter = new RecordingConverter();
        var ruleId = Guid.CreateVersion7();
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, services: services =>
        {
            services.AddSingleton<IParserConverter>(converter);
            services.AddSingleton<IParserBuild>(new FixedBuild());
            services.AddSingleton<IDocumentInspector>(new FakeInspector(new InspectFacts("pdf", 1, true, [new InspectPageSignals(1) { TextChars = 1000, ImageCoverage = 0 }])));
        });
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(2);
            var matched = await DocumentAsync(seed, "/p/kallelse-");
            var other = await DocumentAsync(seed, "/p/rapport-");
            var matchedKey = await ParseKeyAsync(matched);
            var otherKey = await ParseKeyAsync(other);
            converter.Calls.Count.ShouldBe(2);

            await Sql.ExecuteAsync(database.Connection(null), """
                INSERT INTO content."ParseProfiles" ("Id", "Classification", "DepartmentId", "Match", "Strategy", "CleanupRules", "Origin", "State")
                VALUES ($1, $2, NULL, $3::jsonb, '{"ocr":"force"}', NULL, 'manual', 'active')
                """, ruleId, seed.Classification, $$"""{"pathGlob":"/p/kallelse-{{_marker}}*"}""");
            await NextConfigVersionAsync(worker, seed);

            await ReprocessAsync(seed, matched);
            await ReprocessAsync(seed, other);

            converter.Calls.Skip(2).ShouldBe([$"kallelse-{_marker}.pdf:Force"], "only the matching document is parsed again, with the rule's override");
            (await ParseKeyAsync(matched)).ShouldNotBe(matchedKey, "the matching document is re-keyed");
            (await ParseKeyAsync(other)).ShouldBe(otherKey, "the other document keeps its key");
            (await StrategyAsync(matched)).ShouldStartWith("parse=auto(rule=" + ruleId.ToString("D")[..10]);

            await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM content."ParseProfiles" WHERE "Id" = $1""", ruleId);
            await NextConfigVersionAsync(worker, seed);
            await ReprocessAsync(seed, matched);

            (await ParseKeyAsync(matched)).ShouldBe(matchedKey, "without the rule the document is back on its built-in key");
            converter.Calls.Count.ShouldBe(3, "the built-in key's parse output is still cached: no parser call");
        }
        finally
        {
            await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM content."ParseProfiles" WHERE "Id" = $1""", ruleId);
            await EngineKit.PauseAsync(database, seed);
        }
    }

    /// <summary>A config write the worker sees (the <c>Sources</c> outbox trigger), then waits until it has applied the new version.</summary>
    private async Task NextConfigVersionAsync(WorkerHost worker, SourceSeed seed)
    {
        var state = worker.Services.GetRequiredService<ConfigVersionState>();
        var before = state.Version;
        await Sql.ExecuteAsync(database.Connection(null), """UPDATE config."Sources" SET "State" = "State" WHERE "Id" = $1""", seed.SourceId);
        await Commands.WaitUntilAsync(() => Task.FromResult(state.Version > before));
    }

    /// <summary>A <c>reprocess-document</c> from <c>embed</c>: no cached stage is bypassed, so the parse cache decides.</summary>
    private async Task ReprocessAsync(SourceSeed seed, Guid document)
    {
        var command = new ReprocessDocumentCommand { DocumentId = document, FromStage = "embed" };
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

    private Task<string> StrategyAsync(Guid document) =>
        Sql.ScalarAsync<string>(database.Connection(null), """SELECT "ParseStrategy" FROM content."Documents" WHERE "Id" = $1""", document);

    /// <summary>A parser that records <c>path:ocr</c> per request (the path from the PDF's comment line) and answers one paragraph per page.</summary>
    private sealed class RecordingConverter : IParserConverter
    {
        private readonly ConcurrentQueue<string> _calls = new();

        public IReadOnlyList<string> Calls => [.. _calls];

        public Task<ParserConvertResult> ConvertAsync(ReadOnlyMemory<byte> file, ParserConvertOptions options, CancellationToken cancellationToken)
        {
            var path = Encoding.ASCII.GetString(file.Span).Split('\n')[1].Split(' ')[1];
            _calls.Enqueue($"{path}:{options.Ocr.Mode}");
            return Task.FromResult(FakeConverter.Pages(options.Pages!.From, options.Pages.To));
        }
    }

    private sealed class FixedBuild : IParserBuild
    {
        public Task<string> GetAsync(CancellationToken cancellationToken) => Task.FromResult("sha256:parse-rules");
    }
}
