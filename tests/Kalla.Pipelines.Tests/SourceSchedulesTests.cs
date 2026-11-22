using System.Globalization;
using System.Text.Json;
using Kalla.Pipelines.Sources;

namespace Kalla.Pipelines.Tests;

/// <summary>
/// The source schedule rule (engine design §9.6) that the worker's scheduler and the admin's next-run row share: the
/// schedule in force, the next occurrence with missed ones collapsed, and the per-source jitter.
/// </summary>
public sealed class SourceSchedulesTests
{
    private static readonly Guid SourceId = Guid.Parse("0193a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

    /// <summary>SHA-256 of <see cref="SourceId"/>'s bytes, the first eight as a little-endian ulong, mod 90; the value #935 pins.</summary>
    private const int ExpectedJitterMinutes = 8;

    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    [Fact]
    public void The_schedule_in_force_is_the_sources_own_else_the_default_for_its_type_and_an_upload_has_none()
    {
        var defaults = SourceSchedules.Defaults;
        SourceSchedules.InForce(SourceTypes.Crawl, "30 3 * * *", defaults).ShouldBe("30 3 * * *");
        SourceSchedules.InForce(SourceTypes.UrlList, null, defaults).ShouldBe("0 2 * * *");
        SourceSchedules.InForce(SourceTypes.Crawl, null, defaults).ShouldBe("0 1 * * 6");
        SourceSchedules.InForce(SourceTypes.Upload, null, defaults).ShouldBeNull();
        SourceSchedules.InForce(SourceTypes.Upload, "0 4 * * *", defaults).ShouldBe("0 4 * * *", "a schedule of its own is kept");
    }

    [Fact]
    public void The_setting_replaces_the_defaults_only_when_it_names_a_type()
    {
        SourceSchedules.ReadDefaults("""{"crawl":"0 3 * * 0"}""").ShouldBe(new Dictionary<string, string> { ["crawl"] = "0 3 * * 0" });
        SourceSchedules.InForce(SourceTypes.UrlList, null, SourceSchedules.ReadDefaults("""{"crawl":"0 3 * * 0"}""")).ShouldBeNull(
            "a setting that names a type replaces the design's defaults as a whole");
        SourceSchedules.ReadDefaults("{}").ShouldBeSameAs(SourceSchedules.Defaults);
        SourceSchedules.ReadDefaults("null").ShouldBeSameAs(SourceSchedules.Defaults);
        SourceSchedules.ReadDefaults(" ").ShouldBeSameAs(SourceSchedules.Defaults);
        Should.Throw<JsonException>(() => SourceSchedules.ReadDefaults("{"));
    }

    [Fact]
    public void The_jitter_is_stable_per_source_and_within_the_setting()
    {
        SourceSchedules.Jitter(SourceId, 0).ShouldBe(TimeSpan.Zero);
        SourceSchedules.Jitter(SourceId, 90).ShouldBe(SourceSchedules.Jitter(SourceId, 90));
        SourceSchedules.Jitter(SourceId, 90).ShouldBe(TimeSpan.FromMinutes(ExpectedJitterMinutes));
        var other = Guid.Parse("01929b6e-0000-7000-8000-0000000de4c1");
        SourceSchedules.Jitter(other, 90).ShouldBeLessThan(TimeSpan.FromMinutes(90));
    }

    [Fact]
    public void The_next_run_is_the_next_occurrence_after_the_last_scheduled_run_else_after_creation_plus_the_jitter()
    {
        var created = At("2026-10-10T12:00:00Z");
        var now = At("2026-10-10T13:00:00Z");

        var first = SourceSchedules.Next("0 2 * * *", SourceId, 90, created, null, now).ShouldNotBeNull();
        first.Occurrence.ShouldBe(At("2026-10-11T02:00:00+02:00"));
        first.At.ShouldBe(first.Occurrence + TimeSpan.FromMinutes(ExpectedJitterMinutes));

        var after = SourceSchedules.Next("0 2 * * *", SourceId, 90, created, first.Occurrence, now).ShouldNotBeNull();
        after.Occurrence.ShouldBe(At("2026-10-12T02:00:00+02:00"));
    }

    [Fact]
    public void Missed_occurrences_collapse_into_the_latest_whose_start_has_passed()
    {
        var created = At("2026-10-01T12:00:00Z");
        var now = At("2026-10-20T00:05:00Z"); // 02:05 CEST: the 20th's 02:00 has passed, its start (02:08 with the jitter) not yet

        var run = SourceSchedules.Next("0 2 * * *", SourceId, 90, created, null, now).ShouldNotBeNull();
        run.Occurrence.ShouldBe(At("2026-10-19T02:00:00+02:00"));
        (run.At <= now).ShouldBeTrue("the collapsed occurrence is due");

        SourceSchedules.Next("0 2 * * *", SourceId, 0, created, null, now).ShouldNotBeNull().Occurrence.ShouldBe(At("2026-10-20T02:00:00+02:00"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0 0 2 * * *")]
    [InlineData("H 2 * * *")]
    [InlineData("not a schedule")]
    public void No_schedule_or_one_that_does_not_parse_has_no_next_run(string? schedule)
    {
        SourceSchedules.Next(schedule, SourceId, 90, At("2026-10-01T12:00:00Z"), null, At("2026-10-20T12:00:00Z")).ShouldBeNull();
        if (schedule is not null)
        {
            SourceSchedules.IsValid(schedule).ShouldBeFalse();
        }
    }

    [Fact]
    public void A_five_field_schedule_is_valid() => SourceSchedules.IsValid("*/15 6-18 * * 1-5").ShouldBeTrue();
}
