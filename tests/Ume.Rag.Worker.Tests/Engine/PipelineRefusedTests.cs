using Microsoft.Extensions.Logging;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Security;
using Ume.Rag.Pipelines.Definitions;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.Engine;

/// <summary>
/// Engine design §4, §17: a published pipeline version that fails the worker's validation is not run, and the refusal is
/// event 5105 <c>rag.pipeline.refused</c>, once, with the pipeline and version ids and the reason.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class PipelineRefusedTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-refused-");
    private readonly CapturingLoggerProvider _logs = new();

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_sync_of_an_invalid_published_version_fails_without_a_run_and_logs_event_5105_once()
    {
        var marker = Guid.NewGuid().ToString("N");
        await using var site = await EngineKit.SiteAsync((_, _) => Task.FromResult(EngineKit.Html(EngineKit.Page("Refused " + marker))));
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url($"/p/{marker}")]);
        var version = await EngineKit.PublishAsync(database, seed, "no-such-template-" + marker[..8], "{}");
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, logs: _logs);
        try
        {
            var job = await EngineKit.SyncNowAsync(database, seed);
            await Commands.WaitForStateAsync(database, job, JobStates.Failed);

            (await Sql.JobAsync(database.Connection(null), job)).LastError.ShouldBe(PipelineValidationCodes.TemplateUnknown);
            (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."Runs" WHERE "SourceId" = $1""", seed.SourceId))
                .ShouldBe(0, "a refused version opens no run");
            var events = _logs.Entries
                .Where(e => e.Category == SecurityEvents.Category && e.EventId.Id == 5105 && e.Message.Contains(version.ToString(), StringComparison.Ordinal))
                .ToList();
            events.Count.ShouldBe(1);
            events[0].EventId.Name.ShouldBe("rag.pipeline.refused");
            events[0].Level.ShouldBe(LogLevel.Warning);
            events[0].Message.ShouldContain(seed.PipelineId.ToString());
            events[0].Message.ShouldContain(PipelineValidationCodes.TemplateUnknown);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }
}
