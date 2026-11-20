using System.Collections.Concurrent;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.TestKit;
using Kalla.Worker.Parsing;
using Kalla.Worker.Processing.Detect;
using Kalla.Worker.Processing.Parse;
using Kalla.Worker.Tests.CommandJobs;
using Kalla.Worker.Tests.Processing;

namespace Kalla.Worker.Tests.Engine;

/// <summary>
/// The parse-rule commands in a running worker (P1b item 8; processing design §3.5): an <c>edit-parse-rule</c> job's rule
/// applies to the next parse of a matching PDF in the same worker without a config-version change (the command drops the
/// worker's rule cache), and after a <c>disable-parse-rule</c> job the document is back on its built-in parse key.
/// </summary>
[Collection(RagDatabaseGroup3.Name)]
public sealed class ParseRuleCommandEngineTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("kalla-parse-rule-commands-");
    private readonly string _marker = Guid.NewGuid().ToString("N");

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task An_edited_rule_applies_to_the_next_parse_and_a_disabled_one_no_longer_does()
    {
        await using var site = await EngineKit.SiteAsync((name, _) =>
            Task.FromResult(Results.Bytes(Encoding.ASCII.GetBytes($"%PDF-1.7\n% {name} {_marker}\n%%EOF\n"), "application/pdf")));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url($"/p/kallelse-{_marker}.pdf")]);
        var converter = new RecordingConverter();
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
            var builtinKey = await ParseKeyAsync(document);
            converter.Calls.Count.ShouldBe(1);

            var edit = await CommandAsync(seed, new EditParseRuleCommand
            {
                ProfileId = profileId,
                DepartmentId = seed.DepartmentId,
                Match = new ParseRuleMatch { PathGlob = $"/p/kallelse-{_marker}*", MediaType = "application/pdf" },
                Strategy = new ParseRuleStrategy { Ocr = "force" },
            });
            edit.GetProperty("reprocessSyncs").GetInt32().ShouldBe(1, "the source's PDF is in the rule's scope");
            await ReprocessAsync(seed, document);

            converter.Calls.Skip(1).ShouldBe([$"kallelse-{_marker}.pdf:Force"], "the next parse applies the rule's override");
            (await ParseKeyAsync(document)).ShouldNotBe(builtinKey);
            (await StrategyAsync(document)).ShouldStartWith("parse=auto(rule=" + profileId.ToString("D")[..10]);

            var disable = await CommandAsync(seed, new DisableParseRuleCommand { ProfileId = profileId });
            disable.GetProperty("state").GetString().ShouldBe("disabled");
            await ReprocessAsync(seed, document);

            (await ParseKeyAsync(document)).ShouldBe(builtinKey, "without the rule the document is back on its built-in key");
            converter.Calls.Count.ShouldBe(2, "the built-in key's parse output is still cached: no parser call");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    /// <summary>
    /// Enqueues the command as the admin does, waits for it and returns its result; the reprocess sync it enqueued (held back
    /// a few config polls) is dropped, so it cannot start during the test.
    /// </summary>
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
        await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM content."Jobs" WHERE "Class" = 'sync' AND "State" = 'queued'""");
        return await CommandSeeds.ResultAsync(database, request.Id);
    }

    /// <summary>A <c>reprocess-document</c> from <c>embed</c>: no cached stage is bypassed, so the parse key decides.</summary>
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
        public Task<string> GetAsync(CancellationToken cancellationToken) => Task.FromResult("sha256:parse-rule-commands");
    }
}
