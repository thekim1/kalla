using Ume.Rag.Worker.Connectors.Crawl;
using Ume.Rag.Worker.Connectors.Urls;

namespace Ume.Rag.Worker.Connectors.Deletion;

/// <summary>
/// A crawl run's deletion candidates with the source's settings-change effects (connectors design §12.3, §13.1):
/// <list type="bullet">
/// <item>scope narrowing: a live document whose stored URL the current scope no longer admits (<c>startUrls</c>,
/// <c>extraHosts</c>, <c>include</c>, <c>exclude</c>, or a type in its host's site profile <c>excludedTypes</c>, by
/// extension or by its stored media type) is <c>scope-narrowed</c> (exempt), in any run, complete or not, and without a
/// request: the traversal never admits such a URL;</item>
/// <item>identity: a document a complete traversal did not reach is <c>identity-changed</c> (exempt) instead of
/// <c>not-seen</c> when the run's identity settings differ from the last applied ones, or when its identity is no longer
/// canonical under the current rules (source or site profile <c>ignoreQueryParams</c>, <c>directoryIndexNames</c>, a
/// verified identity rule), so it can never be found again;</item>
/// <item>limits: a document a complete traversal did not reach because its last depth is beyond the current
/// <c>maxDepth</c> is no candidate (a lowered limit never deletes; a lowered <c>maxPages</c> makes the traversal
/// partial already).</item>
/// </list>
/// The rest are <see cref="DeletionCandidates.FromTraversal"/>'s.
/// </summary>
public static class SettingsCandidates
{
    /// <param name="runId">The reconciling run.</param>
    /// <param name="documents">The source's documents.</param>
    /// <param name="traversal">The run's completeness.</param>
    /// <param name="explicitGone">Reasons of this run's explicit answers, by document.</param>
    /// <param name="failedSeeds">Documents whose URL is a seed that failed in this run.</param>
    /// <param name="now">The reconcile time.</param>
    /// <param name="source">The source's current scope and canonicaliser, with its hosts' applied site profiles.</param>
    /// <param name="mediaTypes">The live documents' stored media types, by document.</param>
    /// <param name="beyondDepth">Documents whose last frontier depth is beyond the current <c>maxDepth</c>.</param>
    /// <param name="identityChanged">The run's identity settings differ from the last applied ones.</param>
    public static IReadOnlyList<DeletionCandidate> Crawl(
        Guid runId,
        IReadOnlyList<SourceDocument> documents,
        TraversalVerdict traversal,
        IReadOnlyDictionary<Guid, string> explicitGone,
        IReadOnlySet<Guid> failedSeeds,
        DateTimeOffset now,
        CrawlSource source,
        IReadOnlyDictionary<Guid, string> mediaTypes,
        IReadOnlySet<Guid> beyondDepth,
        bool identityChanged)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mediaTypes);
        ArgumentNullException.ThrowIfNull(beyondDepth);
        var narrowed = documents
            .Where(d => d.DeletedAt is null && d.HeldAt is null && OutOfScope(source.Scope, d, mediaTypes))
            .Select(d => new DeletionCandidate(d.Id, DeletionReasons.ScopeNarrowed))
            .ToList();
        var removed = narrowed.Select(c => c.DocumentId).ToHashSet();
        var byId = documents.ToDictionary(d => d.Id);
        var rest = DeletionCandidates.FromTraversal(runId, [.. documents.Where(d => !removed.Contains(d.Id))], traversal, explicitGone, failedSeeds, now);
        foreach (var candidate in rest)
        {
            if (candidate.Reason != DeletionReasons.NotSeen)
            {
                narrowed.Add(candidate);
            }
            else if (!beyondDepth.Contains(candidate.DocumentId))
            {
                narrowed.Add(identityChanged || !Canonical(byId[candidate.DocumentId].CanonicalIdentity, source.Canonicalizer)
                    ? candidate with { Reason = DeletionReasons.IdentityChanged }
                    : candidate);
            }
        }

        return narrowed;
    }

    private static bool OutOfScope(CrawlScope scope, SourceDocument document, IReadOnlyDictionary<Guid, string> mediaTypes)
    {
        if (!Uri.TryCreate(document.CanonicalIdentity, UriKind.Absolute, out _))
        {
            return false;
        }

        return !scope.Check(document.CanonicalIdentity).Admitted
            || (mediaTypes.TryGetValue(document.Id, out var type) && scope.ExcludesType(document.CanonicalIdentity, type));
    }

    private static bool Canonical(string identity, CanonicalizerOptions options) =>
        string.Equals(UrlCanonicalizer.Canonicalize(identity, options).Value, identity, StringComparison.Ordinal);
}
