namespace Kalla.Worker.Connectors.Deletion;

/// <summary>The <c>content."Documents"</c> columns the reconcile phase decides on (data-model §2.2).</summary>
public sealed record SourceDocument
{
    public required Guid Id { get; init; }

    public required string CanonicalIdentity { get; init; }

    /// <summary><c>Documents.Url</c>: the URL as fetched (a crawl's path form since #228; retrieval matches <c>demote</c> on it).</summary>
    public string? Url { get; init; }

    /// <summary>The <c>CrawlFrontier.FetchUrl</c> of the document's identity: the path form it was last found under (#228).</summary>
    public string? FetchUrl { get; init; }

    public Guid? LastRunId { get; init; }

    public DateTimeOffset LastSeenAt { get; init; }

    public short ConsecutiveFailures { get; init; }

    public DateTimeOffset? HeldAt { get; init; }

    public string? HeldBy { get; init; }

    public DateTimeOffset? DeletedAt { get; init; }

    public DateTimeOffset? PurgeAfter { get; init; }
}

/// <summary>Collects a run's deletion candidates (connectors design §13.1).</summary>
public static class DeletionCandidates
{
    public const int PersistentFailureRuns = 4;
    public static readonly TimeSpan PersistentFailureAge = TimeSpan.FromDays(14);

    /// <summary>
    /// The candidates of a crawl run's reconcile phase. Held and deleted documents are never candidates. An explicit
    /// answer of this run (<c>http.404</c>, <c>http.410</c>, <c>http.moved_out_of_scope</c>, <c>robots.disallow</c>) is a
    /// candidate in any run; 401/403/5xx/TLS on ≥ 4 consecutive runs and ≥ 14 days unseen is <c>persistent-failure</c>;
    /// documents this run did not reach are <c>not-seen</c> only when <paramref name="traversal"/> is complete, and never
    /// the document of a URL that failed in this run (a seed, or a failed URL at any depth).
    /// </summary>
    /// <param name="runId">The reconciling run.</param>
    /// <param name="documents">The source's documents.</param>
    /// <param name="traversal">The run's completeness.</param>
    /// <param name="explicitGone">Reasons of this run's explicit answers, by document.</param>
    /// <param name="failedInRun">Documents whose URL failed in this run (a failed seed, or a failed URL at any depth): never <c>not-seen</c>.</param>
    /// <param name="now">The reconcile time.</param>
    public static IReadOnlyList<DeletionCandidate> FromTraversal(
        Guid runId,
        IReadOnlyList<SourceDocument> documents,
        TraversalVerdict traversal,
        IReadOnlyDictionary<Guid, string> explicitGone,
        IReadOnlySet<Guid> failedInRun,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(traversal);
        ArgumentNullException.ThrowIfNull(explicitGone);
        ArgumentNullException.ThrowIfNull(failedInRun);
        var candidates = new List<DeletionCandidate>();
        foreach (var document in documents.Where(d => d.DeletedAt is null && d.HeldAt is null))
        {
            if (explicitGone.TryGetValue(document.Id, out var reason) && DeletionReasons.Explicit.Contains(reason))
            {
                candidates.Add(new DeletionCandidate(document.Id, reason));
            }
            else if (document.ConsecutiveFailures >= PersistentFailureRuns && now - document.LastSeenAt >= PersistentFailureAge)
            {
                candidates.Add(new DeletionCandidate(document.Id, DeletionReasons.PersistentFailure));
            }
            else if (traversal.Complete && document.LastRunId != runId && !failedInRun.Contains(document.Id))
            {
                candidates.Add(new DeletionCandidate(document.Id, DeletionReasons.NotSeen));
            }
        }

        return candidates;
    }

    /// <summary>
    /// The candidates of an id listing (URL list, uploads, §2.1): live documents whose identity the complete listing no
    /// longer has, with <paramref name="reason"/> (<c>removed-from-list</c> for a URL list, exempt). A partial listing
    /// proposes nothing.
    /// </summary>
    public static IReadOnlyList<DeletionCandidate> FromListing(
        IReadOnlyList<SourceDocument> documents, IReadOnlySet<string> listed, bool listingComplete, string reason)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(listed);
        return listingComplete
            ? [.. documents.Where(d => d.DeletedAt is null && d.HeldAt is null && !listed.Contains(d.CanonicalIdentity)).Select(d => new DeletionCandidate(d.Id, reason))]
            : [];
    }

    /// <summary>N for the guard: live documents that are not held.</summary>
    public static int Live(IReadOnlyList<SourceDocument> documents) =>
        documents.Count(d => d.DeletedAt is null && d.HeldAt is null);
}

/// <summary><c>DeletionProposals.Decision</c> values (the column has no CHECK; these are the worker's).</summary>
public static class ProposalDecisions
{
    public const string Applied = "applied";
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";
    public const string Rejected = "rejected";
    public const string Superseded = "superseded";

    /// <summary>A confirm found the document seen again (or gone already): not deleted.</summary>
    public const string Dropped = "dropped";
}

/// <summary>A <c>content."DeletionProposals"</c> row (data-model §2.2, connectors design §13.1).</summary>
public sealed record DeletionProposal
{
    public required Guid RunId { get; init; }

    public required Guid DocumentId { get; init; }

    public required Guid SourceId { get; init; }

    public required string Classification { get; init; }

    public required string Reason { get; init; }

    public bool Exempt { get; init; }

    public string? Decision { get; init; }

    public string? DecidedBy { get; init; }

    public DateTimeOffset? DecidedAt { get; init; }
}

/// <param name="Proposals">The rows with their decisions.</param>
/// <param name="Delete">Documents to soft-delete now (index writer, batches of 1,000).</param>
/// <param name="Holds">Documents to hold (<c>HeldAt</c>, <c>HeldBy</c>) after a rejection.</param>
/// <param name="RunDegradation"><c>deletion.rejected</c> or <c>deletion.superseded</c> for the run whose proposal ended so.</param>
public sealed record ProposalOutcome(IReadOnlyList<DeletionProposal> Proposals, IReadOnlyList<Guid> Delete, IReadOnlyList<Guid> Holds, string? RunDegradation);

/// <summary>
/// The proposal lifecycle (connectors design §13.2–§13.3): the reconcile phase writes one row per candidate (applied, or
/// pending when the guard tripped); <c>confirm-deletion</c> re-validates and deletes; <c>reject-deletion</c> holds the
/// documents; a newer run reaching reconcile supersedes a waiting proposal. Nothing is ever confirmed automatically.
/// </summary>
public static class DeletionProposalLifecycle
{
    public const string System = "system:worker";
    public const string Rejected = "deletion.rejected";
    public const string SupersededReason = "deletion.superseded";

    public static ProposalOutcome Reconcile(Guid runId, Guid sourceId, string classification, GuardDecision decision, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var proposals = decision.Apply.Select(c => Row(runId, sourceId, classification, c, ProposalDecisions.Applied, System, now))
            .Concat(decision.Wait.Select(c => Row(runId, sourceId, classification, c, ProposalDecisions.Pending, null, null)))
            .ToList();
        return new ProposalOutcome(proposals, [.. decision.Apply.Select(c => c.DocumentId)], [], null);
    }

    /// <summary>
    /// <c>confirm-deletion</c>: a pending proposal whose document was seen after <paramref name="proposedAt"/> (the run's
    /// reconcile) or is deleted already is dropped; the rest are deleted.
    /// </summary>
    public static ProposalOutcome Confirm(
        IReadOnlyList<DeletionProposal> proposals, IReadOnlyDictionary<Guid, SourceDocument> documents, DateTimeOffset proposedAt, string actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(documents);
        var decided = new List<DeletionProposal>();
        var delete = new List<Guid>();
        foreach (var proposal in proposals)
        {
            if (proposal.Decision != ProposalDecisions.Pending)
            {
                decided.Add(proposal);
                continue;
            }

            var stale = !documents.TryGetValue(proposal.DocumentId, out var document) || document.DeletedAt is not null || document.LastSeenAt > proposedAt;
            decided.Add(proposal with { Decision = stale ? ProposalDecisions.Dropped : ProposalDecisions.Confirmed, DecidedBy = actor, DecidedAt = now });
            if (!stale)
            {
                delete.Add(proposal.DocumentId);
            }
        }

        return new ProposalOutcome(decided, delete, [], null);
    }

    /// <summary><c>reject-deletion</c>: nothing deleted, the documents are held until seen again or released; the run degrades.</summary>
    public static ProposalOutcome Reject(IReadOnlyList<DeletionProposal> proposals, string actor, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        var pending = proposals.Where(p => p.Decision == ProposalDecisions.Pending).ToList();
        var decided = proposals.Select(p => p.Decision == ProposalDecisions.Pending ? p with { Decision = ProposalDecisions.Rejected, DecidedBy = actor, DecidedAt = now } : p).ToList();
        return new ProposalOutcome(decided, [], [.. pending.Select(p => p.DocumentId)], Rejected);
    }

    /// <summary>A newer run reached reconcile while this run's proposal waits: it is superseded and the older run degrades.</summary>
    public static ProposalOutcome Supersede(IReadOnlyList<DeletionProposal> proposals, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        var decided = proposals.Select(p => p.Decision == ProposalDecisions.Pending ? p with { Decision = ProposalDecisions.Superseded, DecidedBy = System, DecidedAt = now } : p).ToList();
        return new ProposalOutcome(decided, [], [], SupersededReason);
    }

    private static DeletionProposal Row(Guid runId, Guid sourceId, string classification, DeletionCandidate candidate, string decision, string? by, DateTimeOffset? at) =>
        new()
        {
            RunId = runId,
            DocumentId = candidate.DocumentId,
            SourceId = sourceId,
            Classification = classification,
            Reason = candidate.Reason,
            Exempt = candidate.Exempt,
            Decision = decision,
            DecidedBy = by,
            DecidedAt = at,
        };
}

/// <summary>What seeing a document again, or deleting it, does to its row (connectors design §13.3, §13.4).</summary>
public static class DocumentLifecycle
{
    /// <summary>The grace before the purge, also the chunk-set rollback window.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromDays(14);

    /// <summary>
    /// Seen in <paramref name="runId"/>: <c>LastSeenAt</c> and <c>LastRunId</c> set, a hold cleared, the failure count
    /// reset, and a soft-deleted document within its grace revived without re-processing (its live set's chunks come back
    /// with it). Past the grace it stays deleted: the purge owns it, and the next fetch creates a new document.
    /// </summary>
    public static SourceDocument Seen(SourceDocument document, Guid runId, DateTimeOffset now, out bool revived)
    {
        ArgumentNullException.ThrowIfNull(document);
        revived = document.DeletedAt is not null && document.PurgeAfter is { } purge && purge > now;
        var seen = document with { LastSeenAt = now, LastRunId = runId, HeldAt = null, HeldBy = null, ConsecutiveFailures = 0 };
        return revived ? seen with { DeletedAt = null, PurgeAfter = null } : seen;
    }

    /// <summary>A soft delete: <c>DeletedAt</c> now, <c>PurgeAfter</c> after the 14-day grace.</summary>
    public static SourceDocument SoftDelete(SourceDocument document, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document with { DeletedAt = now, PurgeAfter = now + Grace };
    }

    /// <summary><c>release-hold</c>: the hold is cleared; the document is a candidate again at the next reconcile.</summary>
    public static SourceDocument ReleaseHold(SourceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document with { HeldAt = null, HeldBy = null };
    }
}
