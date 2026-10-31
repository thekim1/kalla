namespace Ume.Rag.Retrieval;

/// <summary>
/// Dedup and diversity (knowledge design §8), on ids only, in fused order (reranked order when a reranker is on):
/// <list type="number">
/// <item>the first chunk of each <c>(DocumentId, TextHash)</c> is kept, later ones dropped (a swap in flight);</item>
/// <item>documents with the same identity in several sources form one identity group, and the document of the first
/// selected chunk wins: later chunks of the group's other documents are dropped;</item>
/// <item>at most <c>maxChunksPerDocument</c> chunks per identity group, until <c>top_k</c> results are taken.</item>
/// </list>
/// Steps 1–2 (<see cref="Dedup"/>) run before the reranker and step 3 (<see cref="Cap"/>) after it (§6.7).
/// Identical chunks of different identities are not collapsed.
/// </summary>
public static class ResultDiversifier
{
    public static IReadOnlyList<FusedCandidate> Dedup(IEnumerable<FusedCandidate> fused)
    {
        ArgumentNullException.ThrowIfNull(fused);
        var texts = new HashSet<string>(StringComparer.Ordinal);
        var groupOwner = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var kept = new List<FusedCandidate>();
        foreach (var f in fused)
        {
            var c = f.Candidate;
            if (groupOwner.TryGetValue(c.IdentityKey, out var owner) && owner != c.DocumentId)
            {
                continue;
            }

            if (!texts.Add(c.TextKey))
            {
                continue;
            }

            groupOwner[c.IdentityKey] = c.DocumentId;
            kept.Add(f);
        }

        return kept;
    }

    public static IReadOnlyList<FusedCandidate> Cap(IEnumerable<FusedCandidate> ordered, int maxChunksPerDocument, int topK)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChunksPerDocument, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(topK, 1);
        var perGroup = new Dictionary<string, int>(StringComparer.Ordinal);
        var taken = new List<FusedCandidate>(topK);
        foreach (var f in ordered)
        {
            var count = perGroup.GetValueOrDefault(f.Candidate.IdentityKey);
            if (count >= maxChunksPerDocument)
            {
                continue;
            }

            perGroup[f.Candidate.IdentityKey] = count + 1;
            taken.Add(f);
            if (taken.Count == topK)
            {
                break;
            }
        }

        return taken;
    }
}
