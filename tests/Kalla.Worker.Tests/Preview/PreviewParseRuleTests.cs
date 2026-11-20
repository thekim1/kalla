using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
using Kalla.Worker.Tests.Engine;
using Kalla.Worker.Tests.Processing;

namespace Kalla.Worker.Tests.Preview;

/// <summary>
/// A <c>stage-preview</c> computes the parse key as the live segment does (P1b items 5 and 8 follow-up; engine design §11;
/// processing design §3.5): with the document's admin-made parse-rule candidates. Two PDFs are synced while a rule matches
/// one of them; a preview of each, as a document of its source and as a URL against the collection alone, finds the live
/// parse output under its own key (a cache hit, no parser call): the matched one under the key with the rule, the other
/// under the built-in key.
/// </summary>
[Collection(RagDatabaseGroup2.Name)]
public sealed class PreviewParseRuleTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("kalla-preview-rules-");
    private readonly string _marker = Guid.NewGuid().ToString("N");

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task The_previews_parse_key_is_the_live_key_with_and_without_a_matching_parse_rule()
    {
        await using var site = await EngineKit.SiteAsync((name, _) =>
            Task.FromResult(Results.Bytes(Encoding.ASCII.GetBytes($"%PDF-1.7\n% {name} {_marker}\n%%EOF\n"), "application/pdf")));
        var matchedUrl = site.Url($"/p/kallelse-{_marker}.pdf");
        var otherUrl = site.Url($"/p/rapport-{_marker}.pdf");
        var seed = await EngineKit.SeedUrlListAsync(database, [matchedUrl, otherUrl]);
        var ruleId = Guid.CreateVersion7();
        // In place before the worker starts, so the live parse already keys the matched document with it.
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."ParseProfiles" ("Id", "Classification", "DepartmentId", "Match", "Strategy", "CleanupRules", "Origin", "State")
            VALUES ($1, $2, NULL, $3::jsonb, '{"ocr":"force"}', NULL, 'manual', 'active')
            """, ruleId, seed.Classification, $$"""{"pathGlob":"/p/kallelse-{{_marker}}*"}""");
        var converter = new RecordingConverter();
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, services: services =>
        {
            services.AddSingleton<IParserConverter>(converter);
            services.AddSingleton<IParserBuild>(new FixedBuild());
            services.AddSingleton<IDocumentInspector>(new FakeInspector(new InspectFacts("pdf", 1, true, [new InspectPageSignals(1) { TextChars = 1000, ImageCoverage = 0 }])));
        });
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(2);
            converter.Calls.ShouldBe([$"kallelse-{_marker}.pdf:Force", $"rapport-{_marker}.pdf:Auto"], ignoreOrder: true);
            var matched = await DocumentAsync(seed, "/p/kallelse-");
            var other = await DocumentAsync(seed, "/p/rapport-");

            foreach (var (document, url, what) in new[] { (matched, matchedUrl, "the matched document"), (other, otherUrl, "the other document") })
            {
                var byDocument = await PreviewParseAsync(seed, new StagePreviewPayload
                {
                    PipelineVersionId = seed.VersionId,
                    CollectionId = seed.CollectionId,
                    SourceId = seed.SourceId,
                    DocumentIds = [document],
                    UntilStage = "parse",
                });
                byDocument.State.ShouldBe(PreviewStageState.Succeeded, what);
                byDocument.CacheHit.ShouldBeTrue($"{what}: the preview of the source's document finds the live parse under its key");

                var byUrl = await PreviewParseAsync(seed, new StagePreviewPayload
                {
                    PipelineVersionId = seed.VersionId,
                    CollectionId = seed.CollectionId,
                    Urls = [url.AbsoluteUri],
                    UntilStage = "parse",
                });
                byUrl.State.ShouldBe(PreviewStageState.Succeeded, what);
                byUrl.CacheHit.ShouldBeTrue($"{what}: the preview of the URL against the collection finds the live parse under its key");
            }

            converter.Calls.Count.ShouldBe(2, "no preview called the parser: every preview key was a live key");
        }
        finally
        {
            await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM content."ParseProfiles" WHERE "Id" = $1""", ruleId);
            await EngineKit.PauseAsync(database, seed);
        }
    }

    /// <summary>Runs the preview job and returns its document's <c>parse</c> stage.</summary>
    private async Task<PreviewStageResult> PreviewParseAsync(SourceSeed seed, StagePreviewPayload payload)
    {
        var job = new JobRequest(JobClasses.Preview, seed.DepartmentId, seed.Classification, ContractJson.Serialize(payload));
        (await database.Queue(RagDatabase.Admin).EnqueueAsync([job], null, Ct)).ShouldContain(job.Id);
        var waited = Stopwatch.StartNew();
        while (true)
        {
            var row = await Sql.JobAsync(database.Connection(null), job.Id);
            if (row.State is JobStates.Succeeded or JobStates.Failed or JobStates.Dead)
            {
                row.State.ShouldBe(JobStates.Succeeded, row.LastError);
                using var json = JsonDocument.Parse(row.Result!);
                var stages = ContractJson.Deserialize<StagePreviewResult>(json.RootElement).Documents.ShouldHaveSingleItem().Stages;
                return stages.Single(s => s.Stage == "parse");
            }

            waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(90), $"preview job {job.Id} is still {row.State}");
            await Task.Delay(100, Ct);
        }
    }

    private Task<Guid> DocumentAsync(SourceSeed seed, string path) =>
        Sql.ScalarAsync<Guid>(database.Connection(null), """
            SELECT "Id" FROM content."Documents" WHERE "SourceId" = $1 AND "CanonicalIdentity" LIKE '%' || $2 || '%'
            """, seed.SourceId, path);

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
        public Task<string> GetAsync(CancellationToken cancellationToken) => Task.FromResult("sha256:preview-parse-rules");
    }
}
