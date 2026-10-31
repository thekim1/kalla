using Ume.Rag.Worker.Connectors.Crawl;

namespace Ume.Rag.Worker.Connectors.Deletion;

/// <param name="Complete">Documents not reached may become <c>not-seen</c> candidates.</param>
/// <param name="Reasons">Why not: <c>crawl.limit_pages</c>, <c>crawl.host_paused</c>, <c>crawl.seeds_failed</c>, <c>crawl.claims_pending</c>, <c>crawl.cancelled</c>, <c>crawl.seeds_unreachable</c>.</param>
/// <param name="RunFailure">The run fails (<c>crawl.seeds_unreachable</c>: no seed was resolved, §15).</param>
/// <param name="RunDegradation">The run is degraded (<c>crawl.limit_pages</c>, then <c>crawl.host_paused</c>, §15).</param>
public sealed record TraversalVerdict(bool Complete, IReadOnlyList<string> Reasons, string? RunFailure, string? RunDegradation);

/// <summary>
/// Whether a traversal was complete, by <b>P1a's stricter rule</b> (weakness review §3 "Phasing": completeness by ratio is
/// P1b): every seed resolved (no failed seed at all), no host paused for the run (an unreachable robots.txt counts as
/// paused), no admitted row left pending or released, the page limit not hit, and the run not cancelled. Only a complete
/// traversal turns documents not reached into <c>not-seen</c> candidates; explicit <c>Gone</c> counts in any run.
/// </summary>
public static class TraversalCompleteness
{
    public const string LimitPages = "crawl.limit_pages";
    public const string HostPaused = "crawl.host_paused";
    public const string SeedsFailed = "crawl.seeds_failed";
    public const string ClaimsPending = "crawl.claims_pending";
    public const string Cancelled = "crawl.cancelled";
    public const string SeedsUnreachable = "crawl.seeds_unreachable";

    /// <param name="run">The run's <c>CrawlRuns</c> row.</param>
    /// <param name="openRows">The run's frontier rows still <c>pending</c> or <c>released</c>.</param>
    /// <param name="cancelled">The run was cancelled.</param>
    public static TraversalVerdict Evaluate(CrawlRunRecord run, int openRows, bool cancelled)
    {
        ArgumentNullException.ThrowIfNull(run);
        var reasons = new List<string>();
        if (run.LimitHit)
        {
            reasons.Add(LimitPages);
        }

        if (run.PausedHosts.Count > 0)
        {
            reasons.Add(HostPaused);
        }

        var unreachable = run.Seeds == 0 || run.SeedsFailed >= run.Seeds;
        if (unreachable)
        {
            reasons.Add(SeedsUnreachable);
        }
        else if (run.SeedsFailed > 0)
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

    /// <summary>
    /// The incomplete-traversal alert (§5.4): the last <paramref name="threshold"/> traversals (newest first) were all
    /// incomplete (<c>Settings.Crawl.IncompleteRunsAlert</c>, default 3). A URL-list or upload listing that ended partial counts the same.
    /// </summary>
    public static bool ShouldAlert(IReadOnlyList<bool> recentCompleteness, int threshold = 3)
    {
        ArgumentNullException.ThrowIfNull(recentCompleteness);
        return threshold > 0 && recentCompleteness.Count >= threshold && recentCompleteness.Take(threshold).All(complete => !complete);
    }
}
