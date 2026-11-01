using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Jobs;

namespace Ume.Rag.Worker.Tests.Engine;

/// <summary>
/// The scheduler's anchor on the test database (engine design §9.6): a never-scheduled source counts its first occurrence
/// from <c>Sources.CreatedAt</c>, so an edit after that occurrence (which moves <c>UpdatedAt</c>) does not push the first
/// scheduled run to the next night.
/// </summary>
[Collection(RagDatabaseGroup3.Name)]
public sealed class SyncSchedulerTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse("2026-10-10T12:00:00Z", CultureInfo.InvariantCulture);

    // 02:00 CEST on the 11th.
    private static readonly DateTimeOffset FirstOccurrence = DateTimeOffset.Parse("2026-10-11T00:00:00Z", CultureInfo.InvariantCulture);

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_new_source_s_first_scheduled_run_is_anchored_on_its_creation_not_its_last_edit()
    {
        var marker = Guid.NewGuid().ToString("N");
        var seed = await EngineKit.SeedUrlListAsync(database, [new Uri($"https://scheduler.example/{marker}")]);
        try
        {
            await EngineKit.PauseOtherSourcesAsync(database, seed);
            // Edited one second after the first occurrence: counted from UpdatedAt the first run would be the 12th.
            await Sql.ExecuteAsync(database.Connection(null), """
                UPDATE config."Sources" SET "Schedule" = '0 2 * * *', "CreatedAt" = $2, "UpdatedAt" = $3 WHERE "Id" = $1
                """, seed.SourceId, CreatedAt, FirstOccurrence.AddSeconds(1));
            var db = database.Database(RagDatabase.Worker);
            var settings = new EngineSettings(db, TimeProvider.System, NullLogger<EngineSettings>.Instance);
            var jitter = SyncScheduler.Jitter(seed.SourceId, (await settings.GetAsync(Ct)).ScheduleJitterMinutes);
            var time = new FakeTimeProvider(FirstOccurrence + jitter - TimeSpan.FromMinutes(1));
            var scheduler = new SyncScheduler(db, database.Queue(RagDatabase.Worker), settings, Options.Create(new WorkerOptions()), time,
                NullLogger<SyncScheduler>.Instance);

            (await scheduler.TickAsync(Ct)).ShouldBe(0, "one minute before the occurrence plus jitter nothing is due");

            time.Advance(TimeSpan.FromMinutes(2));
            (await scheduler.TickAsync(Ct)).ShouldBe(1);
            var scheduledFor = await Sql.ScalarAsync<string>(database.Connection(null), """
                SELECT "Payload" ->> 'scheduledFor' FROM content."Jobs"
                WHERE "Class" = 'sync' AND "Payload" ->> 'sourceId' = $1 AND "Payload" ->> 'trigger' = 'schedule'
                ORDER BY "Id" DESC LIMIT 1
                """, seed.SourceId.ToString());
            DateTimeOffset.Parse(scheduledFor, CultureInfo.InvariantCulture).ShouldBe(FirstOccurrence);
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }
}
