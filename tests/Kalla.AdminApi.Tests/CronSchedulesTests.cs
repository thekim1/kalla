using Kalla.Domain;
using Kalla.Domain.Entities;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// The schedule in force and the next run the source page shows (engine design §9.6), as the worker's scheduler
/// (<c>SyncScheduler.Due</c>, <c>Jitter</c>; <c>EngineSettings.DefaultSchedules</c>) decides them.
/// </summary>
public sealed class CronSchedulesTests
{
    private static readonly Guid SourceId = Guid.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

    private static Source Source(string type = SourceTypes.UrlList, string? schedule = null, string state = SourceStates.Active) => new()
    {
        Id = SourceId, CollectionId = Guid.NewGuid(), Type = type, Name = "S", Classification = Classification.Internal, Settings = "{}",
        Schedule = schedule, State = state, PipelineId = Guid.NewGuid(), CreatedAt = Local(2026, 11, 2, 12, 0), UpdatedAt = Local(2026, 11, 2, 12, 0),
    };

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute)
    {
        var local = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, StockholmTime.Zone.GetUtcOffset(local));
    }

    [Fact]
    public void The_schedule_in_force_is_the_sources_own_else_the_setting_for_its_type_else_the_designs_default()
    {
        var empty = new PlatformSettings { DefaultSchedules = "{}" };
        CronSchedules.InForce(Source(schedule: "0 3 * * *"), empty).ShouldBe("0 3 * * *");
        CronSchedules.InForce(Source(), empty).ShouldBe("0 2 * * *");
        CronSchedules.InForce(Source(SourceTypes.Crawl), empty).ShouldBe("0 1 * * 6");
        CronSchedules.InForce(Source(SourceTypes.Upload), empty).ShouldBeNull();

        // A setting that names one type replaces the design's defaults as a whole, as the worker reads it.
        var crawlOnly = new PlatformSettings { DefaultSchedules = """{"crawl": "0 4 * * *"}""" };
        CronSchedules.InForce(Source(SourceTypes.Crawl), crawlOnly).ShouldBe("0 4 * * *");
        CronSchedules.InForce(Source(), crawlOnly).ShouldBeNull();
    }

    [Fact]
    public void The_jitter_is_the_workers_hash_of_the_source_id()
    {
        CronSchedules.Jitter(SourceId, 0).ShouldBe(TimeSpan.Zero);
        var jitter = CronSchedules.Jitter(SourceId, 90);
        jitter.ShouldBe(CronSchedules.Jitter(SourceId, 90));
        jitter.ShouldBeInRange(TimeSpan.Zero, TimeSpan.FromMinutes(89));
        jitter.ShouldBe(TimeSpan.FromMinutes(ExpectedJitterMinutes));
    }

    /// <summary>SHA-256 of <see cref="SourceId"/>'s bytes, the first eight as a little-endian ulong, mod 90 (pinned).</summary>
    private const int ExpectedJitterMinutes = 8;

    [Fact]
    public void The_next_run_is_the_next_occurrence_after_the_last_scheduled_run_plus_the_jitter()
    {
        var source = Source();
        var jitter = CronSchedules.Jitter(SourceId, 90);
        var now = Local(2026, 11, 2, 15, 0);

        // Never scheduled: counts from CreatedAt (12:00 on the 2nd), so 02:00 on the 3rd.
        CronSchedules.NextRun(source, "0 2 * * *", 90, null, now).ShouldBe(Local(2026, 11, 3, 2, 0) + jitter);
        // After a scheduled run for 02:00 on the 3rd: 02:00 on the 4th.
        CronSchedules.NextRun(source, "0 2 * * *", 90, Local(2026, 11, 3, 2, 0), Local(2026, 11, 3, 9, 0)).ShouldBe(Local(2026, 11, 4, 2, 0) + jitter);
        // No jitter setting: on the hour.
        CronSchedules.NextRun(source, "0 2 * * *", 0, null, now).ShouldBe(Local(2026, 11, 3, 2, 0));
    }

    [Fact]
    public void Missed_occurrences_collapse_into_the_latest_and_it_is_due()
    {
        // The worker was down from the 3rd to the 6th at 12:00: one run, for 02:00 on the 6th, due now.
        var next = CronSchedules.NextRun(Source(), "0 2 * * *", 0, Local(2026, 11, 2, 2, 0), Local(2026, 11, 6, 12, 0));
        next.ShouldBe(Local(2026, 11, 6, 2, 0));
    }

    [Fact]
    public void A_paused_source_or_one_without_a_valid_schedule_has_no_next_run()
    {
        var now = Local(2026, 11, 2, 15, 0);
        CronSchedules.NextRun(Source(state: SourceStates.Paused), "0 2 * * *", 90, null, now).ShouldBeNull();
        CronSchedules.NextRun(Source(SourceTypes.Upload), null, 90, null, now).ShouldBeNull();
        CronSchedules.NextRun(Source(), "not cron", 90, null, now).ShouldBeNull();
    }
}
