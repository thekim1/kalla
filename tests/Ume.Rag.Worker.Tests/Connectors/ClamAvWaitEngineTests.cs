using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Worker.Connectors.Malware;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Tests.Engine;
using Ume.Rag.Worker.Tests.Processing;

namespace Ume.Rag.Worker.Tests.Connectors;

/// <summary>
/// Connectors design §11 and §18 "clamd stopped → files requeue, HTML continues, after 24 h <c>failed</c>", through the
/// engine on the test database: a URL list with a page and a PDF while ClamAV gives no verdict. The page goes live; the
/// file's <c>fetch</c> job requeues without an attempt while it has waited less than 24 h (<see cref="ScanWait"/> on a fake
/// clock), and after 24 h it ends <c>failed</c> <c>clamav.unavailable</c>, counted in the run, which ends degraded.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class ClamAvWaitEngineTests(RagDatabaseFixture database, FakeGatewayFactory gateway) : IClassFixture<FakeGatewayFactory>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _blobs = Directory.CreateTempSubdirectory("ume-rag-clamav-wait-");

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _blobs.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_file_without_a_verdict_requeues_under_24_h_then_fails_clamav_unavailable_while_the_page_goes_live()
    {
        var marker = Guid.NewGuid().ToString("N");
        await using var site = await EngineKit.SiteAsync((name, _) => Task.FromResult(name.EndsWith(".pdf", StringComparison.Ordinal)
            ? Results.Bytes(Encoding.ASCII.GetBytes($"%PDF-1.4\n% {marker}\n%%EOF\n"), "application/pdf")
            : EngineKit.Html(EngineKit.Page($"sophämtning {marker}"))));
        var file = site.Url("/p/taxa.pdf");
        var seed = await EngineKit.SeedUrlListAsync(database, [site.Url("/p/sida"), file]);
        await EngineKit.PauseOtherSourcesAsync(database, seed);
        var scanner = new ScriptedScanner(new ScanResult.Unavailable(ScanWait.Unavailable, CountsAttempt: false));
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var worker = await EngineKit.StartWorkerAsync(database, site, gateway, _blobs, services: services =>
        {
            services.AddSingleton<IMalwareScanner>(scanner);
            services.AddSingleton(new ScanWait(clock));
        });
        try
        {
            var run = await EngineKit.SyncNowAsync(database, seed);

            // HTML continues: the page goes live while the file waits for a verdict.
            await WaitUntilAsync(async () => await EngineKit.DocumentsAsync(database, seed) == 1 && await WaitingFilesAsync(run) == 1);
            var scans = scanner.Scans;
            scans.ShouldBeGreaterThanOrEqualTo(1);

            // Under 24 h a later scan without a verdict requeues again (the 30 s delay is cut short here).
            await MakeDueAsync(run);
            await WaitUntilAsync(async () => scanner.Scans > scans && await WaitingFilesAsync(run) == 1);
            (await EngineKit.RunAsync(database, run)).ShouldBe(new RunCounters(RunStates.Running, 0, 0, 0, 0, 0, 0));
            (await FailedItemsAsync(run)).ShouldBeNull();

            clock.Advance(ScanWait.Limit + TimeSpan.FromMinutes(1));
            await MakeDueAsync(run);

            (await EngineKit.WaitForRunAsync(database, run)).ShouldBe(new RunCounters(RunStates.Degraded, 1, 0, 0, 0, 1, 0));
            (await FailedItemsAsync(run)).ShouldBe(file.AbsoluteUri + " clamav.unavailable");
            (await EngineKit.LiveChunksAsync(database, seed)).ShouldBeGreaterThanOrEqualTo(1);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    /// <summary>The run's <c>fetch</c> jobs requeued for a missing verdict.</summary>
    private Task<long> WaitingFilesAsync(Guid runId) =>
        Sql.ScalarAsync<long>(database.Connection(null), """
            SELECT count(*) FROM content."Jobs" WHERE "RunId" = $1 AND "Class" = 'fetch' AND "State" = 'queued' AND "LastError" = 'clamav.unavailable'
            """, runId);

    private Task MakeDueAsync(Guid runId) =>
        Sql.ExecuteAsync(database.Connection(null), """
            UPDATE content."Jobs" SET "AvailableAt" = now() WHERE "RunId" = $1 AND "Class" = 'fetch' AND "State" = 'queued'
            """, runId);

    /// <summary>The run's failed items as <c>identity reason</c>, one per line; null when there are none.</summary>
    private Task<string?> FailedItemsAsync(Guid runId) =>
        Sql.ScalarAsync<string?>(database.Connection(null), """
            SELECT string_agg("Identity" || ' ' || "Reason", E'\n' ORDER BY "Identity") FROM content."RunItems" WHERE "RunId" = $1 AND "Outcome" = 'failed'
            """, runId);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var waited = Stopwatch.StartNew();
        while (!await condition())
        {
            if (waited.Elapsed > TimeSpan.FromSeconds(60))
            {
                throw new TimeoutException("The condition did not hold within 60 s.");
            }

            await Task.Delay(100, Ct);
        }
    }
}
