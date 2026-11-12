using System.IO.Compression;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Blobs;
using Ume.Rag.Pipelines.Definitions;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Preview;
using Ume.Rag.Worker.Tests.Engine;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.Preview;

/// <summary>
/// A-26 (#508; engine design §7, §11): the engine and the preview run the same stage runner, so a preview computes what the
/// engine computes. A preview of three new documents (HTML, plain text, CSV: three parse routes) runs every stage itself,
/// with nothing in the live cache; the engine then syncs the same URLs. Every stage key the preview wrote is one the engine
/// wrote (a key holds its input's output hash, so equal keys mean equal outputs up to that stage), and the last stage's
/// outputs are byte for byte the same.
/// </summary>
[Collection(RagDatabaseGroup3.Name)]
public sealed class StageRunnerParityTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private static readonly string[] Stages = [StageNames.Detect, StageNames.Parse, StageNames.Clean, StageNames.Chunk, StageNames.PersonalData];

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-stage-parity-");
    private readonly string _marker = Guid.NewGuid().ToString("N");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_preview_computes_every_stage_as_the_engine_does_for_html_text_and_csv()
    {
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(name switch
        {
            "notes" => Results.Text($"Anteckningar {_marker}\n\nBygglov söks hos byggnadsnämnden. Handläggningstiden är tio veckor.\n", "text/plain; charset=utf-8"),
            "taxa" => Results.Text($"Tjänst,Belopp\nBygglov {_marker},12000\nRivningslov,4000\nMarklov,3000\n", "text/csv; charset=utf-8"),
            _ => EngineKit.Html(EngineKit.Page($"bygglov {_marker}")),
        }));
        Uri[] urls = [site.Url("/p/page"), site.Url("/p/notes"), site.Url("/p/taxa")];
        var seed = await EngineKit.SeedUrlListAsync(database, urls);
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs);
        var blobs = worker.Services.GetRequiredService<IBlobStore>().ForLevel(seed.Classification);
        try
        {
            // The preview first: nothing is cached, so it runs every stage itself.
            var job = new JobRequest(JobClasses.Preview, seed.DepartmentId, seed.Classification, ContractJson.Serialize(new StagePreviewPayload
            {
                PipelineVersionId = seed.VersionId,
                CollectionId = seed.CollectionId,
                SourceId = seed.SourceId,
                Urls = [.. urls.Select(u => u.AbsoluteUri)],
            }));
            await database.Queue(RagDatabase.Admin).EnqueueAsync([job], null, Ct);
            await WaitForAsync(job.Id);
            var previewKeys = new List<(string Stage, string Key)>();
            await foreach (var blob in blobs.ListAsync(new BlobKey($"{PreviewBlobs.PrefixOf(job.Id).Value}/keys"), Ct))
            {
                var parts = blob.Key.Value.Split('/');
                previewKeys.Add((parts[^2], parts[^1].Replace(".json.gz", "", StringComparison.Ordinal)));
            }

            foreach (var stage in Stages)
            {
                previewKeys.Count(k => k.Stage == stage).ShouldBe(3, $"the preview ran {stage} for each document");
            }

            // Then the engine, on the same URLs.
            (await EngineKit.WaitForRunAsync(database, await EngineKit.SyncNowAsync(database, seed), TimeSpan.FromMinutes(3))).Added.ShouldBe(3);
            foreach (var (stage, key) in previewKeys)
            {
                var path = await Sql.ScalarAsync<string?>(database.Connection(null), """
                    SELECT "BlobPath" FROM content."NodeOutputs" WHERE "CacheKey" = decode($1, 'hex') AND "NodeType" = $2
                    """, key, stage);
                path.ShouldNotBeNull($"the engine wrote the preview's {stage} key {key}");
                if (stage == StageNames.PersonalData)
                {
                    (await JsonAsync(blobs, $"{PreviewBlobs.PrefixOf(job.Id).Value}/keys/{stage}/{key}.json.gz"))
                        .ShouldBe(await JsonAsync(blobs, path), "the chunk set the engine embeds is the one the preview showed");
                }
            }
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    private async Task WaitForAsync(Guid jobId)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var row = await Sql.JobAsync(database.Connection(null), jobId);
            if (row.State is JobStates.Succeeded)
            {
                return;
            }

            row.State.ShouldNotBeOneOf([JobStates.Failed, JobStates.Dead], row.LastError);
            waited.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(90), $"preview job {jobId} is still {row.State}");
            await Task.Delay(100, Ct);
        }
    }

    private static async Task<string> JsonAsync(IBlobStore blobs, string path)
    {
        await using var stream = await blobs.OpenReadAsync(new BlobKey(path), null, Ct);
        stream.ShouldNotBeNull($"{path} is missing");
        await using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return await reader.ReadToEndAsync(Ct);
    }
}
