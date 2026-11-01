namespace Ume.Rag.Retrieval;

/// <summary>One ranked candidate list with its RRF weight.</summary>
public sealed record CandidateList(CandidateListKind Kind, double Weight, IReadOnlyList<Candidate> Candidates);

public enum CandidateListKind
{
    /// <summary>Ordered by distance ascending, then chunk id.</summary>
    Vector,

    /// <summary>Ordered by keyword score descending, then chunk id.</summary>
    Keyword,
}

/// <summary>A fused candidate with its ranks and scores.</summary>
public sealed record FusedCandidate(
    Candidate Candidate,
    double Fused,
    double Score,
    double? Distance,
    int? VectorRank,
    double? KeywordScore,
    int? KeywordRank);

/// <summary>
/// The seam for the score multipliers applied after fusion (knowledge design §6.5): per-collection age decay
/// (<c>RetrievalSettings.recency</c>, <see cref="RecencyAdjuster"/>) and source <c>demote</c> patterns (weakness review §3).
/// Without an adjuster every factor is 1.
/// </summary>
public interface IScoreAdjuster
{
    /// <summary>The factor (0–1] for <paramref name="candidate"/>; 1 leaves the RRF score unchanged.</summary>
    double Factor(Candidate candidate);
}

/// <summary>
/// Reciprocal rank fusion in .NET (knowledge design §6.5):
/// <c>rrf(c) = Σ_L w_L / (k + rank_L(c))</c>, a missing rank contributing 0; <c>score = rrf · adjuster factor</c>;
/// order by score desc, distance asc (nulls last), chunk id asc. Each list is re-sorted first (relaxed-order HNSW may
/// return rows slightly out of order). Several vector lists (one per profile during a profile migration, P3) each add
/// their own term.
/// </summary>
public static class RrfFusion
{
    public const int K = 60;

    public static IReadOnlyList<FusedCandidate> Fuse(IReadOnlyList<CandidateList> lists, IScoreAdjuster? adjuster = null)
    {
        ArgumentNullException.ThrowIfNull(lists);
        var fused = new Dictionary<Guid, Accumulator>();
        foreach (var list in lists)
        {
            var ordered = list.Kind == CandidateListKind.Vector
                ? list.Candidates.Where(c => c.Distance is not null).OrderBy(c => c.Distance!.Value).ThenBy(c => c.ChunkId, GuidOrder.Instance)
                : list.Candidates.Where(c => c.KeywordScore is not null).OrderByDescending(c => c.KeywordScore!.Value).ThenBy(c => c.ChunkId, GuidOrder.Instance);
            var rank = 0;
            foreach (var candidate in ordered)
            {
                rank++;
                if (!fused.TryGetValue(candidate.ChunkId, out var entry))
                {
                    entry = new Accumulator(candidate);
                    fused[candidate.ChunkId] = entry;
                }

                entry.Rrf += list.Weight / (K + rank);
                if (list.Kind == CandidateListKind.Vector)
                {
                    if (entry.VectorRank is null || candidate.Distance < entry.Distance)
                    {
                        entry.Distance = candidate.Distance;
                        entry.VectorRank = rank;
                    }
                }
                else if (entry.KeywordRank is null)
                {
                    entry.KeywordScore = candidate.KeywordScore;
                    entry.KeywordRank = rank;
                }
            }
        }

        return
        [
            .. fused.Values
                .Select(e => new FusedCandidate(e.Candidate, e.Rrf, e.Rrf * (adjuster?.Factor(e.Candidate) ?? 1.0), e.Distance, e.VectorRank,
                    e.KeywordScore, e.KeywordRank))
                .OrderByDescending(f => f.Score)
                .ThenBy(f => f.Distance is null)
                .ThenBy(f => f.Distance ?? 0)
                .ThenBy(f => f.Candidate.ChunkId, GuidOrder.Instance),
        ];
    }

    private sealed class Accumulator(Candidate candidate)
    {
        public Candidate Candidate { get; } = candidate;

        public double Rrf { get; set; }

        public double? Distance { get; set; }

        public int? VectorRank { get; set; }

        public double? KeywordScore { get; set; }

        public int? KeywordRank { get; set; }
    }
}

/// <summary>Orders uuids as Postgres does (bytes in RFC 4122 order), so .NET and SQL tie-breaks agree.</summary>
public sealed class GuidOrder : IComparer<Guid>
{
    public static GuidOrder Instance { get; } = new();

    public int Compare(Guid x, Guid y)
    {
        Span<byte> a = stackalloc byte[16];
        Span<byte> b = stackalloc byte[16];
        x.TryWriteBytes(a, bigEndian: true, out _);
        y.TryWriteBytes(b, bigEndian: true, out _);
        return a.SequenceCompareTo(b);
    }
}
