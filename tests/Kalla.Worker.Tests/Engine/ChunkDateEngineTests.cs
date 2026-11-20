using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Infrastructure.Blobs;
using Kalla.Infrastructure.Jobs;
using Kalla.Pipelines.Definitions;
using Kalla.Pipelines.Jobs;
using Kalla.Worker.Engine;
using Kalla.Worker.Processing;
using Kalla.Worker.Processing.Detect;
using Kalla.Worker.Processing.Html;
using Kalla.Worker.Processing.Persistence;
using Kalla.Worker.Tests.Connectors;
using Kalla.Worker.Tests.Processing;
using SiteProfile = Kalla.Pipelines.SiteProfiles.SiteProfile;

namespace Kalla.Worker.Tests.Engine;

/// <summary>
/// Issue #435 (processing design §7): a chunk's date in production is the first of the path-rule <c>date</c>, the
/// sitemap <c>lastmod</c> when the host's site profile marks it reliable, the document's own date and HTTP
/// <c>Last-Modified</c>; the fetch stores <c>Last-Modified</c> (and the frontier's <c>lastmod</c>) on the document,
/// so a later re-processing without a request dates its chunks the same way.
/// </summary>
[Collection(RagDatabaseGroup3.Name)]
public sealed class ChunkDateEngineTests(RagDatabaseFixture database, FakeGatewayFactory gateway)
    : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private static readonly DateTimeOffset LastModified = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Lastmod = new(2026, 1, 15, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly OwnDate = new(2025, 6, 1);

    /// <summary>Enough text that another chunk size gives other chunks.</summary>
    private static readonly string LongText = string.Concat(Enumerable.Repeat(
        "Bygglov krävs för nya byggnader och ändringar av fasaden. Handläggaren granskar ritningar och planer. ", 40));

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("kalla-chunk-dates-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_production_segment_dates_the_chunks_by_the_first_available_source_in_the_design_order()
    {
        await using var site = await EngineKit.SiteAsync((name, _) =>
            Task.FromResult(EngineKit.Html(EngineKit.Page(name))));
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        await EngineKit.PauseAsync(database, seed);
        var withRule = await EngineKit.PublishAsync(database, seed, "url-list",
            """{"pathRules":[{"match":"/p/**","on":"path","date":"2024-05-01"}]}""");
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        var rig = new SegmentRig(worker.Services, seed);
        var reliable = new SiteProfile { MainRoot = "main", LastmodReliable = true };
        var unreliable = new SiteProfile { MainRoot = "main" };
        var version = seed.VersionId;

        (await rig.DateAsync(withRule, "regel", reliable, Lastmod, OwnDate, LastModified))
            .ShouldBe(new DateOnly(2024, 5, 1), "a path-rule date comes first");
        (await rig.DateAsync(version, "pålitlig", reliable, Lastmod, OwnDate, LastModified))
            .ShouldBe(new DateOnly(2026, 1, 15), "a reliable lastmod comes before the document's own date");
        (await rig.DateAsync(version, "opålitlig", unreliable, Lastmod, null, LastModified))
            .ShouldBe(new DateOnly(2026, 3, 1), "an unreliable lastmod is no date");
        (await rig.DateAsync(version, "egen", unreliable, Lastmod, OwnDate, LastModified))
            .ShouldBe(OwnDate, "the document's own date comes before Last-Modified");
        (await rig.DateAsync(version, "http", null, null, null, LastModified))
            .ShouldBe(new DateOnly(2026, 3, 1), "Last-Modified comes last");
        (await rig.DateAsync(version, "ingen", null, Lastmod, null, null))
            .ShouldBeNull("a lastmod on a host without a profile is not reliable");
    }

    [Fact]
    public async Task A_fetched_last_modified_is_stored_on_the_document_and_dates_the_chunks_of_a_later_reprocess()
    {
        var header = "Sun, 01 Mar 2026 08:00:00 GMT";
        await using var site = await EngineKit.SiteAsync((name, context) =>
        {
            context.Response.Headers.LastModified = header;
            return Task.FromResult(EngineKit.Html(EngineKit.Page("bygglov " + name, LongText)));
        });
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/a")]);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        try
        {
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed))).Added.ShouldBe(1);
            (await Sql.ScalarAsync<DateTime>(database.Connection(null), """
                SELECT "HttpLastModified" FROM content."Documents" WHERE "SourceId" = $1
                """, seed.SourceId)).ShouldBe(LastModified.UtcDateTime);
            (await LiveDatesAsync(seed)).ShouldBe("2026-03-01");

            // The site's clock moves on, but a reprocess makes no request: the stored date dates the new chunks.
            header = "Tue, 01 Sep 2026 08:00:00 GMT";
            var smaller = await EngineKit.PublishAsync(database, seed, "url-list", """{"chunkTokens":300}""");
            var runId = await EngineKit.SyncNowAsync(database, seed, SyncKind.Reprocess, RunTrigger.Publish);
            (await EngineKit.WaitForRunAsync(database, runId)).Updated.ShouldBe(1);

            (await Sql.ScalarAsync<Guid>(database.Connection(null), """
                SELECT s."PipelineVersionId"
                FROM content."Documents" d JOIN content."ChunkSets" s ON s."Id" = d."LiveChunkSetId"
                WHERE d."SourceId" = $1
                """, seed.SourceId)).ShouldBe(smaller);
            (await LiveDatesAsync(seed)).ShouldBe("2026-03-01");
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    /// <summary>The distinct dates of the source's live chunks, <c>yyyy-MM-dd</c>, comma-separated.</summary>
    private Task<string> LiveDatesAsync(SourceSeed seed) =>
        Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT coalesce(string_agg(DISTINCT to_char(c."SourceModifiedAt" AT TIME ZONE 'UTC', 'YYYY-MM-DD'), ','),
                            '')
            FROM content."Chunks" c JOIN content."Documents" d ON d."LiveChunkSetId" = c."ChunkSetId"
            WHERE d."SourceId" = $1 AND c."DeletedAt" IS NULL
            """, seed.SourceId);

    /// <summary>
    /// The worker's own <see cref="ProcessingSegment"/>, run on a document whose raw page is in the blob store, as a
    /// <c>fetch</c> job runs it; a document date is given through a cached parse output (no parser reports one yet).
    /// </summary>
    private sealed class SegmentRig(IServiceProvider services, SourceSeed seed)
    {
        private readonly ProcessingSegment _segment = services.GetRequiredService<ProcessingSegment>();
        private readonly StageOutputs _outputs = services.GetRequiredService<StageOutputs>();
        private readonly IBlobStore _blobs = services.GetRequiredService<IBlobStore>();
        private readonly JobDatabase _database = services.GetRequiredService<JobDatabase>();
        private readonly SourceContexts _sources = services.GetRequiredService<SourceContexts>();

        public async Task<DateOnly?> DateAsync(Guid versionId, string name, SiteProfile? profile,
            DateTimeOffset? sitemapLastMod, DateOnly? ownDate, DateTimeOffset? httpLastModified)
        {
            var pipeline = await _sources.VersionAsync(versionId, SourceTypes.UrlList, Ct);
            var bytes = Encoding.UTF8.GetBytes(EngineKit.Page("sida " + name));
            var hash = SHA256.HashData(bytes);
            var url = $"http://{TestSite.Host}/p/{Uri.EscapeDataString(name)}";
            var document = new DocumentRow(Guid.CreateVersion7(), seed.SourceId, seed.Classification, IdentityKinds.Url,
                url, hash, url, "text/html", null, null, null, null, Guid.CreateVersion7(), null, httpLastModified,
                sitemapLastMod);
            var raw = RawContentKeys.Of(document);
            await _blobs.ForLevel(seed.Classification).PutAsync(raw, new MemoryStream(bytes), bytes.Length, Ct);
            (Guid Id, HtmlProfileVersion Profile)? applied = profile is null
                ? null
                : (Guid.CreateVersion7(), new HtmlProfileVersion(TestSite.Host, 1, profile));
            if (ownDate is { } date)
            {
                var (key, settings) = ProcessingSegment.ParseKey(pipeline.Pipeline, hash, ParseRoute.Html, applied?.Id,
                    null);
                var extracted = new HtmlExtractor(new HtmlPresets()).Extract(bytes, applied?.Profile).Document;
                var parsed = extracted with { DocumentDate = date };
                await CommitAsync([
                    await _outputs.PutAsync(seed.Classification, StageNames.Parse, key, settings, hash, parsed, Ct),
                ]);
            }

            var input = new SegmentInput(document, pipeline, seed.Classification, raw)
            {
                DeclaredMediaType = "text/html; charset=utf-8",
                SiteProfile = applied,
            };
            var result = (await _segment.RunAsync(input, JobClasses.Fetch, null, Ct))
                .ShouldBeOfType<SegmentResult.Embed>();
            await CommitAsync(result.Outputs);
            var chunks = await _outputs.GetAsync<ChunkSetDraft>(result.PersonalDataKey, Ct);
            chunks.HasValue.ShouldBeTrue();
            return chunks.Value.Value.Chunks.Select(c => c.SourceModifiedAt).Distinct().ShouldHaveSingleItem();
        }

        private async Task CommitAsync(IReadOnlyList<NodeOutputRecord> outputs)
        {
            await using var commit = await EngineCommit.BeginAsync(_database, Ct);
            await commit.AddOutputsAsync(outputs, Ct);
            await commit.CommitAsync(Ct);
        }
    }
}
