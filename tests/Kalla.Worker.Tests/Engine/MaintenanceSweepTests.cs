using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Kalla.Domain.Ports;
using Kalla.Infrastructure.Blobs;
using Kalla.TestKit;
using Kalla.Worker.Engine;

namespace Kalla.Worker.Tests.Engine;

/// <summary>
/// The blob-tombstone purge (data-model §8, §9; issue #34): a tombstone past <c>Settings.BlobPurgeDelayDays</c> purges its
/// blob, and the negative proof: a tombstoned key that a row references again is never purged. A younger tombstone waits.
/// </summary>
[Collection(RagDatabaseGroup1.Name)]
public sealed class MaintenanceSweepTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("kalla-purge-");

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync()
    {
        _root.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task The_purge_deletes_old_unreferenced_blobs_and_never_a_referenced_one()
    {
        var blobs = new FileSystemBlobStore(Options.Create(new BlobStoreOptions { Root = _root.FullName }), TimeProvider.System);
        var referencedKey = new byte[32];
        Random.Shared.NextBytes(referencedKey);
        var referenced = new BlobKey($"stages/internal/chunk/{Convert.ToHexStringLower(referencedKey)}.json.gz");
        var unreferenced = new BlobKey($"stages/internal/chunk/{Convert.ToHexStringLower(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray())}.json.gz");
        var young = new BlobKey($"stages/internal/clean/{Convert.ToHexStringLower(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray())}.json.gz");
        foreach (var key in new[] { referenced, unreferenced, young })
        {
            using var content = new MemoryStream([1, 2, 3]);
            await blobs.PutAsync(key, content, 3, Ct);
        }

        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."NodeOutputs" ("CacheKey", "Classification", "NodeType", "NodeVersion", "ParamsHash", "InputHash", "OutputHash", "BlobPath",
                                              "IsParseOutput", "CreatedAt", "LastUsedAt")
            VALUES ($1, 'internal', 'chunk', 1, $2, $2, $2, $3, false, now(), now())
            """, referencedKey, new byte[32], referenced.Value);
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO content."BlobTombstones" ("Key", "DeletedAt", "Reason") VALUES
                ($1, now() - interval '40 days', 'test'), ($2, now() - interval '40 days', 'test'), ($3, now() - interval '2 days', 'test')
            """, referenced.Value, unreferenced.Value, young.Value);

        var settings = new EngineSettings(database.Database(RagDatabase.Worker), TimeProvider.System, NullLogger<EngineSettings>.Instance);
        var sweep = new BlobPurgeSweep(database.Database(RagDatabase.Worker), blobs, settings, NullLogger<BlobPurgeSweep>.Instance);
        var result = await sweep.RunAsync(Ct);

        result.Purged.ShouldBeGreaterThanOrEqualTo(1);
        result.Kept.ShouldBeGreaterThanOrEqualTo(1);
        (await blobs.ExistsAsync(referenced, Ct)).ShouldBeTrue("a referenced blob is never purged");
        (await blobs.ExistsAsync(unreferenced, Ct)).ShouldBeFalse();
        (await blobs.ExistsAsync(young, Ct)).ShouldBeTrue("younger than BlobPurgeDelayDays");
        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."BlobTombstones" WHERE "Key" = ANY($1)""",
            (object)new[] { referenced.Value, unreferenced.Value })).ShouldBe(0);
        (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."BlobTombstones" WHERE "Key" = $1""", young.Value)).ShouldBe(1);
        await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM content."BlobTombstones" WHERE "Key" = $1""", young.Value);
    }
}

/// <summary>The scheduler's due rule (engine design §9.6) and the finalizer's state rule (§10.3), without a database.</summary>
public sealed class SchedulingRulesTests
{
    private static readonly Guid Source = Guid.Parse("01929b6e-0000-7000-8000-00000000abcd");

    private static ScheduledSource Nightly(DateTimeOffset since, DateTimeOffset? last = null, bool running = false, string? schedule = "0 2 * * *") =>
        new(Source, "url-list", "internal", Guid.Empty, schedule, since, last, running);

    private static EngineSettingsValues NoJitter => EngineSettingsValues.Default with { ScheduleJitterMinutes = 0 };

    [Fact]
    public void A_nightly_source_is_due_after_two_in_Stockholm_once()
    {
        var since = DateTimeOffset.Parse("2026-10-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        SyncScheduler.Due(Nightly(since), NoJitter, since.AddHours(10)).ShouldBeNull("02:00 CEST is 00:00Z on the 11th");
        var due = SyncScheduler.Due(Nightly(since), NoJitter, since.AddHours(12.5)).ShouldNotBeNull();
        due.Occurrence.ShouldBe(DateTimeOffset.Parse("2026-10-11T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        SyncScheduler.Due(Nightly(since, last: due.Occurrence), NoJitter, since.AddHours(13)).ShouldBeNull("the occurrence ran");
    }

    [Fact]
    public void Missed_occurrences_collapse_into_one_run_and_a_running_source_waits()
    {
        var since = DateTimeOffset.Parse("2026-10-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var now = DateTimeOffset.Parse("2026-10-20T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        SyncScheduler.Due(Nightly(since), NoJitter, now).ShouldNotBeNull().Occurrence
            .ShouldBe(DateTimeOffset.Parse("2026-10-20T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        SyncScheduler.Due(Nightly(since, running: true), NoJitter, now).ShouldBeNull();
    }

    [Fact]
    public void The_jitter_is_stable_per_source_and_within_the_setting()
    {
        SyncScheduler.Jitter(Source, 90).ShouldBe(SyncScheduler.Jitter(Source, 90));
        SyncScheduler.Jitter(Source, 90).ShouldBeLessThan(TimeSpan.FromMinutes(90));
        SyncScheduler.Jitter(Source, 0).ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Uploads_are_never_scheduled_and_a_null_schedule_takes_the_default()
    {
        var since = DateTimeOffset.Parse("2026-10-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        SyncScheduler.Due(Nightly(since, schedule: null) with { Type = "upload" }, NoJitter, since.AddDays(3)).ShouldBeNull();
        SyncScheduler.Due(Nightly(since, schedule: null), NoJitter, since.AddDays(3)).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(3, 0, 0, 0, 0, false, 0, "succeeded")]
    [InlineData(0, 0, 0, 0, 3, false, 0, "failed")]
    [InlineData(19, 0, 0, 0, 1, false, 0, "succeeded")]
    [InlineData(18, 0, 0, 0, 2, false, 0, "degraded")]
    [InlineData(5, 0, 0, 0, 0, true, 0, "failed")]
    [InlineData(1, 0, 5, 0, 5, false, 5, "awaiting-deletion-confirmation")]
    public void The_run_state_follows_the_degraded_rule(long added, long updated, long unchanged, long deleted, long failed, bool syncFailed, long proposed, string state) =>
        RunFinalizer.StateOf(new RunFinalizer.Counters(added, updated, unchanged, deleted, failed, 0, 0, syncFailed, proposed), 0.05m).ShouldBe(state);
}
