using Kalla.Worker.Connectors.Crawl;

namespace Kalla.Worker.Connectors.Deletion;

/// <summary>
/// Which seed rule decides a traversal's completeness (<c>Crawl:CompletenessRule</c>; weakness review §3 "Phasing"):
/// <see cref="Ratio"/> is P1b's rule and the default; <see cref="Strict"/> is P1a's, kept selectable.
/// </summary>
public enum CompletenessRule
{
    /// <summary>≥ 99 % of the seeds resolved (connectors design §5.4).</summary>
    Ratio,

    /// <summary>Every seed resolved: one failed seed makes the traversal incomplete (P1a).</summary>
    Strict,
}

/// <param name="Complete">Documents not reached may become <c>not-seen</c> candidates.</param>
/// <param name="Reasons">Why not: <c>crawl.incremental</c>, <c>crawl.limit_pages</c>, <c>crawl.host_paused</c>, <c>crawl.seeds_failed</c>, <c>crawl.seeds_unreachable</c>, <c>crawl.claims_pending</c>, <c>crawl.cancelled</c>.</param>
/// <param name="RunFailure">The run fails (<c>crawl.seeds_unreachable</c>: no seed was resolved, §15).</param>
/// <param name="RunDegradation">The run is degraded (<c>crawl.limit_pages</c>, then <c>crawl.host_paused</c>, §15).</param>
public sealed record TraversalVerdict(bool Complete, IReadOnlyList<string> Reasons, string? RunFailure, string? RunDegradation);

/// <summary>A source's earlier crawl run as the incomplete-traversal alert reads it: incremental runs are no traversals.</summary>
public sealed record TraversalHistoryEntry(bool Complete, bool Incremental);

/// <summary>
/// Whether a traversal was complete (connectors design §5.4, weakness review §3): no host paused for the run (an
/// unreachable robots.txt or an unreadable sitemap counts as paused), no admitted row left pending or released, the page
/// limit not hit, the run neither cancelled nor incremental, and enough seeds resolved: by
/// <see cref="CompletenessRule.Ratio"/> at least 99 % of them (P1b), by <see cref="CompletenessRule.Strict"/> all of them
/// (P1a). A seed is resolved when it was fetched, returned 304, was skipped by <c>lastmod</c> or is <c>Gone</c>; the
/// others are <c>CrawlRuns.SeedsFailed</c>. Only a complete traversal turns documents not reached into <c>not-seen</c>
/// candidates; explicit <c>Gone</c> counts in any run.
/// </summary>
public static class TraversalCompleteness
{
    public const string LimitPages = "crawl.limit_pages";
    public const string HostPaused = "crawl.host_paused";
    public const string SeedsFailed = "crawl.seeds_failed";
    public const string ClaimsPending = "crawl.claims_pending";
    public const string Cancelled = "crawl.cancelled";
    public const string SeedsUnreachable = "crawl.seeds_unreachable";
    public const string Incremental = "crawl.incremental";
    public const string Continued = "crawl.continued";

    /// <summary>The share of seeds (percent) <see cref="CompletenessRule.Ratio"/> needs resolved.</summary>
    public const int ResolvedPercent = 99;

    /// <summary><c>Settings.Crawl.IncompleteRunsAlert</c>'s default.</summary>
    public const int DefaultAlertThreshold = 3;

    /// <param name="run">The run's <c>CrawlRuns</c> row.</param>
    /// <param name="openRows">The run's frontier rows still <c>pending</c> or <c>released</c>.</param>
    /// <param name="cancelled">The run was cancelled.</param>
    /// <param name="rule">The seed rule.</param>
    /// <param name="incremental">An incremental run (§5.4): never a complete traversal.</param>
    /// <param name="continued">
    /// A <c>continue</c> run (#804): it sees only the part of the site the run it continues left, so it is never a complete
    /// traversal either. Its seeds are the carried depth-0 rows (often none); like an incremental run, it fails as
    /// <c>crawl.seeds_unreachable</c> only when it had seeds and every one failed again.
    /// </param>
    public static TraversalVerdict Evaluate(CrawlRunRecord run, int openRows, bool cancelled, CompletenessRule rule, bool incremental, bool continued = false)
    {
        ArgumentNullException.ThrowIfNull(run);
        var reasons = new List<string>();
        if (incremental)
        {
            reasons.Add(Incremental);
        }

        if (continued)
        {
            reasons.Add(Continued);
        }

        if (run.LimitHit)
        {
            reasons.Add(LimitPages);
        }

        if (run.PausedHosts.Count > 0)
        {
            reasons.Add(HostPaused);
        }

        // An incremental run admits only changed and new seeds, often none: it fails only when it had seeds and none resolved.
        var unreachable = incremental || continued ? run.Seeds > 0 && run.SeedsFailed >= run.Seeds : run.Seeds == 0 || run.SeedsFailed >= run.Seeds;
        if (unreachable)
        {
            reasons.Add(SeedsUnreachable);
        }
        else if (!EnoughResolved(run, rule))
        {
            reasons.Add(SeedsFailed);
        }

        if (openRows > 0)
        {
            reasons.Add(ClaimsPending);
        }

        if (cancelled)
        {
            reasons.Add(Cancelled);
        }

        var degradation = run.LimitHit ? LimitPages : run.PausedHosts.Count > 0 ? HostPaused : null;
        return new TraversalVerdict(reasons.Count == 0, reasons, unreachable && !cancelled ? SeedsUnreachable : null, degradation);
    }

    /// <summary>The seeds resolved: the admitted seeds less the failed ones.</summary>
    public static int Resolved(CrawlRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return Math.Max(0, run.Seeds - run.SeedsFailed);
    }

    /// <summary>
    /// How many traversals in a row, newest first, were incomplete. Incremental runs are skipped (they never complete); a
    /// URL-list or upload listing that ended partial is passed as an incomplete traversal.
    /// </summary>
    public static int ConsecutiveIncomplete(IEnumerable<TraversalHistoryEntry> newestFirst)
    {
        ArgumentNullException.ThrowIfNull(newestFirst);
        return newestFirst.Where(e => !e.Incremental).TakeWhile(e => !e.Complete).Count();
    }

    /// <summary>
    /// The incomplete-traversal alert (§5.4): the last <paramref name="threshold"/> traversals were all incomplete
    /// (<c>Settings.Crawl.IncompleteRunsAlert</c>, default 3), so the source's <c>not-seen</c> deletions never run.
    /// </summary>
    public static bool ShouldAlert(int consecutiveIncomplete, int threshold = DefaultAlertThreshold) =>
        threshold > 0 && consecutiveIncomplete >= threshold;

    private static bool EnoughResolved(CrawlRunRecord run, CompletenessRule rule) => rule switch
    {
        CompletenessRule.Strict => run.SeedsFailed == 0,
        _ => Resolved(run) * 100L >= (long)ResolvedPercent * run.Seeds,
    };
}
