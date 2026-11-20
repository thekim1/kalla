namespace Kalla.Retrieval;

/// <summary>
/// Age decay after fusion (knowledge design §6.5; weakness review §3): for a chunk whose collection has
/// <c>RetrievalSettings.recency</c> and whose <c>DocumentType</c> is in its <c>kinds</c>,
/// <c>decay = 1 − weight · (1 − 2^(−age / halfLifeDays))</c> with age in days from <c>SourceModifiedAt</c> to now.
/// A chunk without <c>SourceModifiedAt</c>, of another type, or in a collection without recency keeps 1; a document never
/// falls below <c>1 − weight</c>. Built per search from the scope, so each chunk uses its own collection's settings.
/// </summary>
public sealed class RecencyAdjuster : IScoreAdjuster
{
    private readonly Dictionary<Guid, (RecencySettings Settings, HashSet<string> Kinds)> _byCollection;
    private readonly DateTimeOffset _now;

    private RecencyAdjuster(Dictionary<Guid, (RecencySettings Settings, HashSet<string> Kinds)> byCollection, DateTimeOffset now)
    {
        _byCollection = byCollection;
        _now = now;
    }

    /// <summary>The adjuster for <paramref name="scope"/>, or null when no collection in it has recency on.</summary>
    public static RecencyAdjuster? For(SearchScope scope, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(scope);
        // The kinds as a set, built once per search rather than scanned per candidate (#333 finding 6).
        var byCollection = scope.Collections.Where(c => c.Settings.Recency is not null)
            .ToDictionary(c => c.CollectionId, c => (c.Settings.Recency!, new HashSet<string>(c.Settings.Recency!.Kinds, StringComparer.OrdinalIgnoreCase)));
        return byCollection.Count == 0 ? null : new RecencyAdjuster(byCollection, now);
    }

    public double Factor(Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (!_byCollection.TryGetValue(candidate.CollectionId, out var entry) || candidate.SourceModifiedAt is not { } modified ||
            (entry.Kinds.Count > 0 && (candidate.DocumentType is null || !entry.Kinds.Contains(candidate.DocumentType))))
        {
            return 1.0;
        }

        return Decay(Math.Max(0, (_now - modified).TotalDays), entry.Settings);
    }

    /// <summary><c>1 − weight · (1 − 2^(−ageDays / halfLifeDays))</c>.</summary>
    public static double Decay(double ageDays, RecencySettings recency)
    {
        ArgumentNullException.ThrowIfNull(recency);
        return 1 - (recency.Weight * (1 - Math.Pow(2, -ageDays / recency.HalfLifeDays)));
    }
}

/// <summary>The product of several adjusters' factors (decay · demote, knowledge design §6.5).</summary>
public sealed class CombinedAdjuster(IReadOnlyList<IScoreAdjuster> adjusters) : IScoreAdjuster
{
    /// <summary>One adjuster for the non-null ones; null when there are none.</summary>
    public static IScoreAdjuster? Of(params IScoreAdjuster?[] adjusters)
    {
        ArgumentNullException.ThrowIfNull(adjusters);
        var present = adjusters.OfType<IScoreAdjuster>().ToList();
        return present.Count switch { 0 => null, 1 => present[0], _ => new CombinedAdjuster(present) };
    }

    public double Factor(Candidate candidate)
    {
        var product = 1.0;
        foreach (var adjuster in adjusters)
        {
            product *= adjuster.Factor(candidate);
        }

        return product;
    }
}
