using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Kalla.Infrastructure.Jobs;
using Kalla.TestKit;
using Kalla.Worker.Jobs;

namespace Kalla.Worker.Tests;

/// <summary><c>Worker:Maintenance</c> (data-model §4.4, §8): run-item partitions and terminal-job cleanup, as <c>kalla_worker</c>.</summary>
[Collection(RagDatabaseGroup3.Name)]
public sealed class MaintenanceTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Terminal_jobs_older_than_7_days_are_deleted_in_batches_and_nothing_else()
    {
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."Jobs" ("Id", "Class", "DepartmentId", "Classification", "Payload", "MaxAttempts", "State", "CompletedAt")
            SELECT gen_random_uuid(), 'fetch', gen_random_uuid(), 'internal', '{}', 5,
                   (ARRAY['succeeded','failed','dead','cancelled'])[1 + i % 4], now() - interval '8 days'
            FROM generate_series(1, 2500) AS i;
            INSERT INTO content."Jobs" ("Id", "Class", "DepartmentId", "Classification", "Payload", "MaxAttempts", "State", "CompletedAt") VALUES
                (gen_random_uuid(), 'fetch', gen_random_uuid(), 'internal', '{"keep":"recent"}', 5, 'succeeded', now() - interval '6 days'),
                (gen_random_uuid(), 'fetch', gen_random_uuid(), 'internal', '{"keep":"open"}', 5, 'queued', NULL);
            """);

        var deleted = await new ContentMaintenance(database.Database(RagDatabase.Worker)).DeleteTerminalJobsAsync(ContentMaintenance.TerminalJobRetention, Ct);

        deleted.ShouldBe(2500);
        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."Jobs" """)).ShouldBe(2);
    }

    [Fact]
    public async Task A_maintenance_run_creates_partitions_and_warns_when_run_items_lag_in_the_default_partition()
    {
        var logs = new CapturingLoggerProvider();
        await using var worker = await WorkerHost.StartAsync(database, logs: logs);
        var runId = Guid.NewGuid();
        // A run item beyond today + 7 days lands in RunItems_default (worker role, through the parent).
        await Sql.ExecuteAsync(database.Connection(RagDatabase.Worker), """
            INSERT INTO content."RunItems" ("CreatedAt", "RunId", "Classification", "Identity", "Outcome")
            VALUES (now() + interval '30 days', $1, 'internal', 'https://example.test/a', 'failed')
            """, runId);
        try
        {
            await worker.Services.GetRequiredService<MaintenanceService>().RunAsync(Ct);

            logs.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("RunItems_default", StringComparison.Ordinal));
            logs.Entries.ShouldContain(e => e.Message.StartsWith("Run-item partitions:", StringComparison.Ordinal));
            logs.Entries.ShouldContain(e => e.Message.StartsWith("Deleted 0 terminal jobs", StringComparison.Ordinal));
            (await Sql.ScalarAsync<long>(database.Connection(null),
                """SELECT count(*) FROM pg_inherits WHERE inhparent = 'content."RunItems"'::regclass""")).ShouldBeGreaterThanOrEqualTo(9);
        }
        finally
        {
            await Sql.ExecuteAsync(database.Connection(RagDatabase.Worker), """DELETE FROM content."RunItems" WHERE "RunId" = $1""", runId);
        }

        var clean = await new ContentMaintenance(database.Database(RagDatabase.Worker)).MaintainPartitionsAsync(Ct);
        clean.DefaultRows.ShouldBe(0);
    }

    [Theory]
    [InlineData("2026-10-30T12:00:00Z", "2026-10-31T01:00:00Z")] // CET: 02:00 = 01:00 UTC
    [InlineData("2026-10-31T00:59:00Z", "2026-10-31T01:00:00Z")]
    [InlineData("2026-10-31T01:00:00Z", "2026-11-01T01:00:00Z")]
    [InlineData("2026-07-01T12:00:00Z", "2026-07-02T00:00:00Z")] // CEST: 02:00 = 00:00 UTC
    [InlineData("2026-03-28T12:00:00Z", "2026-03-29T01:00:00Z")] // 02:00 does not exist on 29 March: 03:00 CEST
    [InlineData("2026-10-24T12:00:00Z", "2026-10-25T01:00:00Z")] // 02:00 happens twice on 25 October: once, in CET
    public void The_nightly_run_is_at_two_in_the_morning_in_Stockholm(string now, string expected)
    {
        MaintenanceService.NextRunAt(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture))
            .ShouldBe(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }
}
