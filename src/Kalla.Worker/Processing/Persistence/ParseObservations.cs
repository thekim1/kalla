using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kalla.Worker.Processing.Clean;

namespace Kalla.Worker.Processing.Persistence;

/// <summary>
/// One <c>ParseObservations</c> row per non-preview parse (processing design §3.5): the domain, the path pattern (digits
/// and the last segment replaced by <c>*</c>), the document type, the share of pages per strategy, the §8 score and the
/// quality facts. P1 shows them on the document page; from P2 they feed rule proposals.
/// </summary>
public static partial class ParseObservations
{
    public static ParseObservationRecord Create(Guid documentId, string classification, Uri? url, string? displayName, string documentType,
        ParsedDocument parsed, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        var domain = url?.IdnHost ?? "upload";
        var path = url is not null ? Uri.UnescapeDataString(url.AbsolutePath) : "/" + (displayName ?? string.Empty);
        var shares = parsed.Pages.Count == 0
            ? new SortedDictionary<string, double>(StringComparer.Ordinal) { [parsed.Strategy ?? "unknown"] = 1 }
            : new SortedDictionary<string, double>(parsed.Pages.GroupBy(p => p.Strategy ?? "unknown", StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => Math.Round(g.Count() / (double)parsed.Pages.Count, 4), StringComparer.Ordinal), StringComparer.Ordinal);
        var metrics = parsed.Facts.Where(f => f.Key.StartsWith("quality.", StringComparison.Ordinal))
            .ToDictionary(f => f.Key["quality.".Length..], f => f.Value, StringComparer.Ordinal);
        return new ParseObservationRecord(Guid.CreateVersion7(now), classification, documentId, domain, PathPattern(path), documentType,
            JsonSerializer.Serialize(shares), (decimal)QualityFacts.Score(parsed), JsonSerializer.Serialize(new SortedDictionary<string, double>(metrics, StringComparer.Ordinal)), now);
    }

    /// <summary><c>/kommun/protokoll/2024/kf-12.pdf</c> → <c>/kommun/protokoll/0/*</c>.</summary>
    public static string PathPattern(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segments = path.Split('/');
        if (segments.Length > 1)
        {
            segments[^1] = "*";
        }

        return string.Join('/', segments.Select(s => s == "*" ? s : Digits().Replace(s, "0"))).ToLower(CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
