using System.Text.Json;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Pipelines.Sources;

namespace Ume.Rag.Retrieval;

/// <summary>One compiled <c>demote</c> entry of a source (connectors design §5.2): a URL pattern and its factor in (0, 1).</summary>
public sealed record DemotePattern(UrlPattern Pattern, double Factor);

/// <summary>
/// Source <c>demote</c> after fusion (knowledge design §6.5; weakness review §3): the lowest factor of the chunk's source
/// patterns that match the document's URL (<c>Documents.Url</c>, #102, #228), else 1. Uploads have no URL and are never
/// demoted. Patterns match as the crawler matches them (<see cref="UrlPattern"/>, shared through <c>Ume.Rag.Pipelines</c>).
/// </summary>
public sealed class DemoteAdjuster : IScoreAdjuster
{
    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<DemotePattern>> _bySource;

    /// <summary>
    /// Factors already worked out in this search, by source and URL: every chunk of a document shares them, so the
    /// patterns run once per document instead of once per chunk (#333 finding 6). One adjuster serves one search
    /// (<see cref="For"/>), so the memo needs no locking.
    /// </summary>
    private readonly Dictionary<(Guid SourceId, string Url), double> _memo = [];

    private DemoteAdjuster(IReadOnlyDictionary<Guid, IReadOnlyList<DemotePattern>> bySource) => _bySource = bySource;

    /// <summary>The adjuster for <paramref name="scope"/>, or null when no source in it has demote patterns.</summary>
    public static DemoteAdjuster? For(SearchScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return scope.Demote.Count == 0 ? null : new DemoteAdjuster(scope.Demote);
    }

    public double Factor(Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.Url is not { Length: > 0 } url || !_bySource.TryGetValue(candidate.SourceId, out var patterns))
        {
            return 1.0;
        }

        if (_memo.TryGetValue((candidate.SourceId, url), out var known))
        {
            return known;
        }

        var factor = 1.0;
        foreach (var (pattern, value) in patterns)
        {
            if (value < factor && pattern.Matches(url))
            {
                factor = value;
            }
        }

        _memo[(candidate.SourceId, url)] = factor;
        return factor;
    }

    /// <summary>
    /// Compiles <c>Sources.DemoteRules</c> (the settings' <c>demote</c> list). Entries that do not bind or whose pattern does
    /// not parse are skipped (the admin validates them on save, so this only guards rows written another way).
    /// </summary>
    public static IReadOnlyList<DemotePattern> Compile(string? demoteRulesJson)
    {
        if (string.IsNullOrWhiteSpace(demoteRulesJson))
        {
            return [];
        }

        var compiled = new List<DemotePattern>();
        try
        {
            using var json = JsonDocument.Parse(demoteRulesJson);
            if (json.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            foreach (var entry in json.RootElement.EnumerateArray())
            {
                try
                {
                    var rule = ContractJson.Deserialize<DemoteRule>(entry);
                    if (rule.Factor is > 0 and < 1)
                    {
                        compiled.Add(new DemotePattern(UrlPattern.Parse(rule.Pattern), rule.Factor));
                    }
                }
                catch (Exception e) when (e is JsonException or ArgumentException)
                {
                    // skipped: see the summary
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return compiled;
    }
}
