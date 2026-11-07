using Ume.Rag.Worker.Connectors.Crawl;
using Ume.Rag.Worker.Connectors.Urls;

namespace Ume.Rag.Worker.Connectors.Deletion;

/// <summary>
/// A crawl run's deletion candidates with the source's settings-change effects (connectors design §12.3, §13.1):
/// <list type="bullet">
/// <item>scope narrowing: a live document whose stored URL the current scope no longer admits (<c>startUrls</c>,
/// <c>extraHosts</c>, <c>include</c>, <c>exclude</c>, or a type in its host's site profile <c>excludedTypes</c>, by
/// extension or by its stored media type) is <c>scope-narrowed</c> (exempt), in any run, complete or not, and without a
/// request: the traversal never admits such a URL (<c>include</c> and <c>exclude</c> over the path form it was found or
/// fetched under, or over the identity where none is kept, <see cref="CrawlScope.CheckStored"/>);</item>
/// <item>identity: a document a complete traversal did not reach is <c>identity-changed</c> (exempt) instead of
/// <c>not-seen</c> when the run's identity settings differ from the last applied ones, or when its identity is no longer
/// canonical under the current rules (source or site profile <c>ignoreQueryParams</c>, <c>directoryIndexNames</c>, a
/// verified identity rule), so it can never be found again;</item>
/// <item>limits: a document a complete traversal did not reach because its last depth is beyond the current
/// <c>maxDepth</c> is no candidate (a lowered limit never deletes; a lowered <c>maxPages</c> makes the traversal
/// partial already).</item>
/// </list>
/// The rest are <see cref="DeletionCandidates.FromTraversal"/>'s. A URL list's identity change: <see cref="UrlList"/>.
/// </summary>
public static class SettingsCandidates
{
    /// <summary>
    /// A URL list's listing candidates (§12.3, §13.1): a live document whose identity the complete listing no longer has is
    /// <c>removed-from-list</c>, or <c>identity-changed</c> (both exempt) when the run's identity settings differ from the
    /// last applied ones or its identity is no longer canonical under the current <c>ignoreQueryParams</c> and
    /// <c>directoryIndexNames</c>, so the list cannot name it again.
    /// </summary>
    /// <param name="documents">The source's documents.</param>
    /// <param name="listed">The identities of the run's complete listing.</param>
    /// <param name="canonicalizer">The source's current canonicaliser options.</param>
    /// <param name="identityChanged">The run's identity settings differ from the last applied ones.</param>
    public static IReadOnlyList<DeletionCandidate> UrlList(
        IReadOnlyList<SourceDocument> documents, IReadOnlySet<string> listed, CanonicalizerOptions canonicalizer, bool identityChanged)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(canonicalizer);
        var byId = documents.ToDictionary(d => d.Id);
        return [.. DeletionCandidates.FromListing(documents, listed, listingComplete: true, DeletionReasons.RemovedFromList)
            .Select(c => identityChanged || !Canonical(byId[c.DocumentId].CanonicalIdentity, canonicalizer)
                ? c with { Reason = DeletionReasons.IdentityChanged }
                : c)];
    }

    /// <param name="runId">The reconciling run.</param>
    /// <param name="documents">The source's documents.</param>
    /// <param name="traversal">The run's completeness.</param>
    /// <param name="explicitGone">Reasons of this run's explicit answers, by document.</param>
    /// <param name="failedInRun">Documents whose URL failed in this run (a failed seed, or a failed URL at any depth): never <c>not-seen</c>.</param>
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
        IReadOnlySet<Guid> failedInRun,
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
            .Where(d => d.DeletedAt is null && d.HeldAt is null && OutOfScope(source, d, mediaTypes))
            .Select(d => new DeletionCandidate(d.Id, DeletionReasons.ScopeNarrowed))
            .ToList();
        var removed = narrowed.Select(c => c.DocumentId).ToHashSet();
        var byId = documents.ToDictionary(d => d.Id);
        var rest = DeletionCandidates.FromTraversal(runId, [.. documents.Where(d => !removed.Contains(d.Id))], traversal, explicitGone, failedInRun, now);
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

    /// <summary>
    /// Over the path form the document was last found under (<c>CrawlFrontier.FetchUrl</c>) or fetched under
    /// (<c>Documents.Url</c>), else over the stored identity: where the host's identity rule mapped the path and no path
    /// form is kept, <c>include</c> and <c>exclude</c> cannot narrow it (<see cref="CrawlScope.CheckStored"/>); such a
    /// document goes only when a complete traversal misses it.
    /// </summary>
    private static bool OutOfScope(CrawlSource source, SourceDocument document, IReadOnlyDictionary<Guid, string> mediaTypes)
    {
        if (!Uri.TryCreate(document.CanonicalIdentity, UriKind.Absolute, out _))
        {
            return false;
        }

        return !source.Scope.CheckStored(document.CanonicalIdentity, document.FetchUrl ?? document.Url, source.Canonicalizer).Admitted
            || (mediaTypes.TryGetValue(document.Id, out var type) && source.Scope.ExcludesType(document.CanonicalIdentity, type));
    }

    private static bool Canonical(string identity, CanonicalizerOptions options) =>
        string.Equals(UrlCanonicalizer.Canonicalize(identity, options).Value, identity, StringComparison.Ordinal);
}
