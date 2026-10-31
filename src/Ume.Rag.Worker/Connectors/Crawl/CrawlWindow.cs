using System.Globalization;

namespace Ume.Rag.Worker.Connectors.Crawl;

/// <summary>
/// The source's <c>crawlWindow</c> (connectors design §6.5): <c>HH:MM-HH:MM</c> in Europe/Stockholm, every day; a start
/// after the end spans midnight (the default <c>22:00-06:00</c>), equal times mean always open. Traversals release fetches
/// only inside it; manual runs ignore it.
/// </summary>
public sealed class CrawlWindow
{
    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    private CrawlWindow(TimeOnly start, TimeOnly end)
    {
        Start = start;
        End = end;
    }

    public TimeOnly Start { get; }

    public TimeOnly End { get; }

    /// <summary>Parses the setting (validated on save by <c>contracts/sources/v1/crawl.schema.json</c>).</summary>
    public static CrawlWindow Parse(string window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(window);
        var parts = window.Split('-');
        if (parts.Length != 2
            || !TimeOnly.TryParseExact(parts[0], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !TimeOnly.TryParseExact(parts[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
        {
            throw new FormatException($"'{window}' is not a crawl window such as 22:00-06:00.");
        }

        return new CrawlWindow(start, end);
    }

    public bool IsOpen(DateTimeOffset now) => Contains(TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Stockholm).DateTime));

    /// <summary>The next instant the window opens, or <paramref name="now"/> when it is open.</summary>
    public DateTimeOffset NextOpening(DateTimeOffset now)
    {
        if (IsOpen(now))
        {
            return now;
        }

        var local = TimeZoneInfo.ConvertTime(now, Stockholm);
        var day = DateOnly.FromDateTime(local.DateTime);
        for (var offset = 0; offset <= 2; offset++)
        {
            var wall = day.AddDays(offset).ToDateTime(Start);
            if (Stockholm.IsInvalidTime(wall))
            {
                // The start falls in the spring-forward gap: the window opens when the clocks resume.
                wall = wall.AddHours(1);
            }

            var opening = new DateTimeOffset(wall, Stockholm.GetUtcOffset(wall));
            if (opening > now)
            {
                return opening;
            }
        }

        throw new InvalidOperationException("A daily window opens within two days.");
    }

    private bool Contains(TimeOnly time) => Start == End || (Start < End ? time >= Start && time < End : time >= Start || time < End);
}
