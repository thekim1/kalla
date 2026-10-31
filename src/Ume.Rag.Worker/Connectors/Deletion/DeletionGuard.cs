namespace Ume.Rag.Worker.Connectors.Deletion;

/// <summary>Deletion reasons (connectors design §13.1); configuration changes and explicit admin actions are exempt from the guard.</summary>
public static class DeletionReasons
{
    public const string NotSeen = "not-seen";
    public const string Http404 = "http.404";
    public const string Http410 = "http.410";
    public const string MovedOutOfScope = "http.moved_out_of_scope";
    public const string RobotsDisallow = "robots.disallow";
    public const string PersistentFailure = "persistent-failure";
    public const string RemovedFromList = "removed-from-list";
    public const string ScopeNarrowed = "scope-narrowed";
    public const string IdentityChanged = "identity-changed";
    public const string LevelChanged = "level-changed";
    public const string UploadDeleted = "upload.deleted";

    /// <summary>Explicit answers of a fetch: candidates in any run, complete or not (§5.4).</summary>
    public static IReadOnlySet<string> Explicit { get; } = new HashSet<string>(StringComparer.Ordinal) { Http404, Http410, MovedOutOfScope, RobotsDisallow };

    private static readonly HashSet<string> ExemptReasons = new(StringComparer.Ordinal)
    {
        RemovedFromList, ScopeNarrowed, IdentityChanged, LevelChanged, UploadDeleted,
    };

    public static bool IsExempt(string reason) => ExemptReasons.Contains(reason);
}

/// <summary>A live document the reconcile phase would delete, and why.</summary>
public sealed record DeletionCandidate(Guid DocumentId, string Reason)
{
    public bool Exempt => DeletionReasons.IsExempt(Reason);
}

/// <summary>
/// The guard's limits (<c>config."Settings"</c>: <c>DeletionGuardPercent</c> 20, <c>DeletionGuardMinCount</c> 5,
/// <c>DeletionGuardMaxCount</c> 500).
/// </summary>
public sealed record GuardSettings(int Percent = 20, int MinCount = 5, int MaxCount = 500)
{
    public static GuardSettings Default { get; } = new();
}

/// <summary>Which rule stopped the deletions (shown on the run page, §13.5).</summary>
public enum GuardRule
{
    None,
    Ratio,
    Absolute,
    EmptySource,
}

/// <param name="Rule">The rule that tripped, or <see cref="GuardRule.None"/>.</param>
/// <param name="NonExempt">n: the non-exempt candidates (<c>Runs.ProposedDeletions</c> when tripped).</param>
/// <param name="Live">N: the source's live documents, held ones excluded.</param>
/// <param name="Apply">Soft-deleted now (exempt ones always; all when not tripped).</param>
/// <param name="Wait">Waiting for a decision (non-exempt ones when tripped).</param>
public sealed record GuardDecision(GuardRule Rule, int NonExempt, int Live, IReadOnlyList<DeletionCandidate> Apply, IReadOnlyList<DeletionCandidate> Wait)
{
    public bool Tripped => Rule != GuardRule.None;
}

/// <summary>
/// The mass-deletion stop of the reconcile phase (connectors design §13.2). With n non-exempt candidates and N live
/// documents of the source (held ones excluded): trips on n ≥ <c>MinCount</c> and n / N above the percentage, on
/// n &gt; <c>MaxCount</c>, or when the listing or traversal completed with no indexable item while N &gt; 0. Tripped,
/// only the exempt candidates are applied and the rest wait for confirm or reject; the run ends
/// <c>awaiting-deletion-confirmation</c>.
/// </summary>
public static class DeletionGuard
{
    /// <param name="candidates">The run's candidates (held documents already excluded by <see cref="DeletionCandidates"/>).</param>
    /// <param name="liveDocuments">N.</param>
    /// <param name="emptyListing">The listing or traversal completed and yielded no indexable item.</param>
    /// <param name="settings">The limits.</param>
    public static GuardDecision Evaluate(IReadOnlyList<DeletionCandidate> candidates, int liveDocuments, bool emptyListing, GuardSettings settings)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(settings);
        var exempt = candidates.Where(c => c.Exempt).ToList();
        var other = candidates.Where(c => !c.Exempt).ToList();
        var n = other.Count;
        var rule = emptyListing && liveDocuments > 0 ? GuardRule.EmptySource
            : n > settings.MaxCount ? GuardRule.Absolute
            : n >= settings.MinCount && liveDocuments > 0 && n * 100L > (long)settings.Percent * liveDocuments ? GuardRule.Ratio
            : GuardRule.None;
        return rule == GuardRule.None
            ? new GuardDecision(rule, n, liveDocuments, [.. exempt, .. other], [])
            : new GuardDecision(rule, n, liveDocuments, exempt, other);
    }
}
