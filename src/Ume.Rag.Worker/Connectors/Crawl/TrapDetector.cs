using System.Globalization;
using System.Text.RegularExpressions;

namespace Ume.Rag.Worker.Connectors.Crawl;

/// <summary>
/// URL-space traps of one crawl run (connectors design §5.3), detected on the fetch path before a discovered link is
/// admitted: endless paths, faceted query variants, calendars, and soft-404 fan-out (links of a content hash seen on ≥ 20
/// URLs are expanded once). Counters grow only with <see cref="Admitted"/>, so a link found on many pages counts once.
/// Session-id candidates are reported, never acted on. The state is per run and worker instance (P1: one instance); the
/// run's totals go to <c>CrawlRuns.TrapStats</c>, and a restarted worker starts its counters again.
/// </summary>
public sealed partial class TrapDetector(TimeProvider time)
{
    public const string EndlessPath = "trap.endless_path";
    public const string QueryVariants = "trap.query_variants";
    public const string Calendar = "trap.calendar";

    public const int MaxUrlLength = 1024;
    public const int MaxSegments = 15;
    public const int MaxRepeats = 3;
    public const int MaxQueryParameters = 6;
    public const int MaxQueryVariantsPerPath = 50;
    public const int MaxDatedUrlsPerPrefix = 100;
    public const int DuplicateContentFanOut = 20;
    public const int SessionParameterThreshold = 20;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _queryVariants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _datedPrefixes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _contentHashes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Key, string Parameter), HashSet<string>> _parameterValues = [];
    private readonly HashSet<string> _sessionCandidates = new(StringComparer.Ordinal);

    /// <summary>Query parameters that vary alone on more than 20 URLs: candidates for <c>ignoreQueryParams</c> (report only).</summary>
    public IReadOnlyCollection<string> SessionParameterCandidates
    {
        get
        {
            lock (_lock)
            {
                return [.. _sessionCandidates];
            }
        }
    }

    /// <summary>The trap a canonical URL falls into, or null.</summary>
    public string? Check(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.AbsoluteUri.Length > MaxUrlLength)
        {
            return EndlessPath;
        }

        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > MaxSegments || HasRepeatedSequence(segments))
        {
            return EndlessPath;
        }

        var parameters = Parameters(url);
        if (parameters.Count > MaxQueryParameters)
        {
            return QueryVariants;
        }

        var now = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var dated = DatePrefix(url, segments, parameters, out var outOfRange, now);
        if (outOfRange)
        {
            return Calendar;
        }

        lock (_lock)
        {
            if (parameters.Count > 0 && _queryVariants.GetValueOrDefault(PathKey(url)) >= MaxQueryVariantsPerPath)
            {
                return QueryVariants;
            }

            if (dated is not null && _datedPrefixes.GetValueOrDefault(dated) >= MaxDatedUrlsPerPrefix)
            {
                return Calendar;
            }
        }

        return null;
    }

    /// <summary>Counts an admitted URL towards the query-variant, calendar and session-parameter counters.</summary>
    public void Admitted(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var parameters = Parameters(url);
        var dated = DatePrefix(url, segments, parameters, out _, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime));
        lock (_lock)
        {
            if (parameters.Count > 0)
            {
                var key = PathKey(url);
                _queryVariants[key] = _queryVariants.GetValueOrDefault(key) + 1;
                for (var i = 0; i < parameters.Count; i++)
                {
                    var rest = string.Join('&', parameters.Where((_, j) => j != i).Select(p => p.Name + "=" + p.Value));
                    var values = _parameterValues.TryGetValue((key + "?" + rest, parameters[i].Name), out var set)
                        ? set
                        : _parameterValues[(key + "?" + rest, parameters[i].Name)] = new HashSet<string>(StringComparer.Ordinal);
                    if (values.Add(parameters[i].Value) && values.Count > SessionParameterThreshold)
                    {
                        _sessionCandidates.Add(parameters[i].Name);
                    }
                }
            }

            if (dated is not null)
            {
                _datedPrefixes[dated] = _datedPrefixes.GetValueOrDefault(dated) + 1;
            }
        }
    }

    /// <summary>
    /// Whether the links of a page with <paramref name="contentHash"/> are expanded: always, until 20 URLs of the run had
    /// that hash; after that only if no page with it was expanded yet (soft-404 and duplicate fan-out, §5.3).
    /// </summary>
    public bool ShouldExpand(string contentHash)
    {
        ArgumentNullException.ThrowIfNull(contentHash);
        lock (_lock)
        {
            var seen = _contentHashes[contentHash] = _contentHashes.GetValueOrDefault(contentHash) + 1;
            return _expanded.Add(contentHash) || seen < DuplicateContentFanOut;
        }
    }

    internal static bool HasRepeatedSequence(string[] segments)
    {
        for (var length = 1; length * MaxRepeats <= segments.Length; length++)
        {
            for (var start = 0; start + (length * MaxRepeats) <= segments.Length; start++)
            {
                var repeated = true;
                for (var k = 1; k < MaxRepeats && repeated; k++)
                {
                    for (var i = 0; i < length; i++)
                    {
                        if (!string.Equals(segments[start + i], segments[start + (k * length) + i], StringComparison.Ordinal))
                        {
                            repeated = false;
                            break;
                        }
                    }
                }

                if (repeated)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string PathKey(Uri url) => url.Authority + url.AbsolutePath;

    private static List<(string Name, string Value)> Parameters(Uri url) =>
        [.. url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2) is var kv ? (kv[0], kv.Length > 1 ? kv[1] : string.Empty) : default)];

    /// <summary>
    /// The path prefix of a date-like URL (before its first date segment, or the whole path for a date in the query), or
    /// null; <paramref name="outOfRange"/> when a date lies outside [now − 5 years, now + 2 years].
    /// </summary>
    private static string? DatePrefix(Uri url, string[] segments, List<(string Name, string Value)> parameters, out bool outOfRange, DateOnly today)
    {
        outOfRange = false;
        string? prefix = null;
        for (var i = 0; i < segments.Length; i++)
        {
            if (ParseDate(segments[i]) is { } date)
            {
                outOfRange |= date.Last < today.AddYears(-5) || date.First > today.AddYears(2);
                prefix ??= url.Authority + "/" + string.Join('/', segments[..i]);
            }
        }

        foreach (var (_, value) in parameters)
        {
            if (ParseDate(Uri.UnescapeDataString(value)) is { } date)
            {
                outOfRange |= date.Last < today.AddYears(-5) || date.First > today.AddYears(2);
                prefix ??= PathKey(url);
            }
        }

        return prefix;
    }

    /// <summary>A whole value that is a year (1900–2099), a year-month or a date: the first and last day it covers.</summary>
    private static (DateOnly First, DateOnly Last)? ParseDate(string value)
    {
        if (!DateLike().IsMatch(value))
        {
            return null;
        }

        var c = CultureInfo.InvariantCulture;
        return value.Length switch
        {
            4 when DateOnly.TryParseExact(value, "yyyy", c, DateTimeStyles.None, out var year) => (year, year.AddYears(1).AddDays(-1)),
            7 when DateOnly.TryParseExact(value, "yyyy-MM", c, DateTimeStyles.None, out var month) => (month, month.AddMonths(1).AddDays(-1)),
            10 when DateOnly.TryParseExact(value, "yyyy-MM-dd", c, DateTimeStyles.None, out var day) => (day, day),
            _ => null,
        };
    }

    [GeneratedRegex(@"^(19|20)\d{2}(-(0[1-9]|1[0-2])(-(0[1-9]|[12]\d|3[01]))?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DateLike();
}
