namespace Ume.Rag.Retrieval;

/// <summary>
/// What a search may see, resolved by the caller (knowledge design §2.1): the core reads no config and knows no
/// principals. The knowledge role passes the caller's visible, published collections and allowed levels; the
/// evaluator one collection with every level (processing §13.5).
/// </summary>
/// <param name="EmbeddingProfileId">
/// The profile of every collection in the scope. It is inlined into the statements as a literal (data-model §3.3), so
/// it must come from config, never from user input; a <see cref="Guid"/> cannot carry SQL.
/// </param>
/// <param name="Dimensions">The profile's <c>Dimensions</c> (README §1: from the profile row, not a constant).</param>
/// <param name="Collections">The searched collections with their resolved settings.</param>
/// <param name="AllowedLevels">The levels the caller may see (defence in depth on every statement).</param>
/// <param name="Platform">The platform values of <c>Settings.Retrieval</c>.</param>
public sealed record SearchScope(
    Guid EmbeddingProfileId,
    int Dimensions,
    IReadOnlyList<CollectionInScope> Collections,
    IReadOnlyList<string> AllowedLevels,
    RetrievalPlatformSettings Platform)
{
    public IReadOnlyList<Guid> CollectionIds { get; } = [.. Collections.Select(c => c.CollectionId)];

    /// <summary>
    /// The settings that act on the whole ranking (weights, candidate depth, per-document cap): the collections' values
    /// when all agree, else the platform defaults (knowledge design §6.5: one coherent ranking).
    /// </summary>
    public RetrievalSettings Ranking
    {
        get
        {
            if (Collections.Count == 0)
            {
                return Platform.Defaults;
            }

            var first = Collections[0].Settings;
            var agree = Collections.All(c =>
                c.Settings.VectorWeight.Equals(first.VectorWeight) && c.Settings.KeywordWeight.Equals(first.KeywordWeight) &&
                c.Settings.Candidates == first.Candidates && c.Settings.MaxChunksPerDocument == first.MaxChunksPerDocument);
            return agree ? first : Platform.Defaults;
        }
    }

    /// <summary>Throws when the scope cannot be searched as given.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Dimensions, 1, nameof(Dimensions));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Dimensions, 4000, nameof(Dimensions));
        if (EmbeddingProfileId == Guid.Empty)
        {
            throw new ArgumentException("The scope needs its embedding profile.", nameof(EmbeddingProfileId));
        }

        if (CollectionIds.Distinct().Count() != CollectionIds.Count)
        {
            throw new ArgumentException("A collection appears twice in the scope.", nameof(Collections));
        }
    }
}

/// <summary>One searched collection and its resolved <c>RetrievalSettings</c>.</summary>
public sealed record CollectionInScope(Guid CollectionId, RetrievalSettings Settings);
