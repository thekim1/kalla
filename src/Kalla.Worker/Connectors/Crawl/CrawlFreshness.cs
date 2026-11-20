using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.Worker.Connectors.Deletion;

namespace Kalla.Worker.Connectors.Crawl;

/// <summary>The run-level freshness rules of connectors design §5.4 that the engine applies when it opens a crawl run.</summary>
public static class CrawlFreshness
{
    /// <summary>
    /// <c>revalidateEvery</c> (default 4): every n-th traversal of the source, and every forced run, sends no validators and
    /// makes no <c>lastmod</c> skip, catching wrong 304s and stale <c>lastmod</c> values. Incremental runs are no traversals:
    /// they never revalidate and are not counted in <paramref name="traversalsBefore"/>.
    /// </summary>
    /// <param name="traversalsBefore">The source's earlier traversals (incremental runs not counted).</param>
    /// <param name="revalidateEvery">The source's <c>revalidateEvery</c>.</param>
    /// <param name="forced">A forced run.</param>
    /// <param name="incremental">The run is incremental.</param>
    public static bool Revalidates(int traversalsBefore, int revalidateEvery, bool forced, bool incremental)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(traversalsBefore);
        ArgumentOutOfRangeException.ThrowIfLessThan(revalidateEvery, 1);
        return !incremental && (forced || (traversalsBefore + 1) % revalidateEvery == 0);
    }
}

/// <summary>What one removal check ended with (§5.4): <c>Gone</c> deletes the document in any run, a live URL keeps it.</summary>
public enum RemovalResult
{
    Gone,
    Kept,
    Other,
}

/// <summary>The crawl report's completeness part of the summary (§5.5, weakness review §3).</summary>
/// <param name="Complete">The traversal was complete.</param>
/// <param name="Reasons">Why not (<see cref="TraversalVerdict.Reasons"/>).</param>
/// <param name="Rule">The seed rule (<c>ratio</c> or <c>strict</c>).</param>
/// <param name="Incremental">An incremental run (never complete).</param>
/// <param name="Seeds">Seeds admitted.</param>
/// <param name="SeedsResolved">Seeds fetched, 304, skipped by <c>lastmod</c> or <c>Gone</c>.</param>
/// <param name="SeedsFailed">Seeds not resolved.</param>
/// <param name="ResolvedShare">Resolved seeds over seeds (0 without seeds).</param>
/// <param name="FailedProtected">
/// Live documents kept from <c>not-seen</c> in this run because their URL failed in it: a failed seed, or a failed URL at
/// any depth (§5.4, §13.1).
/// </param>
/// <param name="PausedHosts">Hosts paused for the run, with the last reason (shown in the admin only, never in telemetry).</param>
/// <param name="ConsecutiveIncomplete">Incomplete traversals in a row, this one included.</param>
/// <param name="Alert">The incomplete-traversal alert is raised.</param>
public sealed record CompletenessSection(
    bool Complete,
    IReadOnlyList<string> Reasons,
    string Rule,
    bool Incremental,
    int Seeds,
    int SeedsResolved,
    int SeedsFailed,
    double ResolvedShare,
    int FailedProtected,
    IReadOnlyDictionary<string, string> PausedHosts,
    int ConsecutiveIncomplete,
    bool Alert);

/// <summary>The crawl report's removal checks (§5.5): how many URLs that left the sitemap were re-requested and what they answered.</summary>
/// <param name="Checked">Removal checks with an outcome.</param>
/// <param name="Gone">Answered 404, 410 or a permanent redirect out of scope: deletion candidates.</param>
/// <param name="Kept">Still answered: kept until a complete traversal does not reach them.</param>
/// <param name="Other">Anything else (failures, robots), by reason.</param>
/// <param name="GoneByReason">The <c>Gone</c> answers by reason.</param>
public sealed record RemovalSection(int Checked, int Gone, int Kept, IReadOnlyDictionary<string, int> Other, IReadOnlyDictionary<string, int> GoneByReason)
{
    /// <summary>The removal checks' outcomes tallied (run items carrying <see cref="CrawlTraversal.RemovalCheck"/>).</summary>
    public static RemovalSection From(IEnumerable<FetchOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var gone = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var other = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var kept = 0;
        var count = 0;
        foreach (var outcome in outcomes)
        {
            count++;
            switch (Classify(outcome))
            {
                case RemovalResult.Gone:
                    var reason = ((FetchOutcome.Gone)outcome).Reason;
                    gone[reason] = gone.GetValueOrDefault(reason) + 1;
                    break;
                case RemovalResult.Kept:
                    kept++;
                    break;
                default:
                    var key = ReasonOf(outcome);
                    other[key] = other.GetValueOrDefault(key) + 1;
                    break;
            }
        }

        return new RemovalSection(count, gone.Values.Sum(), kept, other, gone);
    }

    public static RemovalResult Classify(FetchOutcome outcome) => outcome switch
    {
        FetchOutcome.Gone => RemovalResult.Gone,
        FetchOutcome.Content or FetchOutcome.NotModified => RemovalResult.Kept,
        _ => RemovalResult.Other,
    };

    private static string ReasonOf(FetchOutcome outcome) => outcome switch
    {
        FetchOutcome.Skipped skipped => skipped.Reason,
        FetchOutcome.Refused refused => refused.Reason,
        FetchOutcome.Failed failed => failed.Reason,
        FetchOutcome.Transient transient => transient.Reason,
        FetchOutcome.Quarantined quarantined => quarantined.Reason,
        _ => "other",
    };
}

/// <summary>Builds the crawl report's completeness section from the run's facts (§5.5; the finalizer writes it to <c>Runs.Report</c>).</summary>
public static class CrawlReportSections
{
    /// <param name="run">The run's <c>CrawlRuns</c> row.</param>
    /// <param name="verdict">The run's completeness (<see cref="CrawlTraversal.EvaluateAsync"/>).</param>
    /// <param name="rule">The seed rule used.</param>
    /// <param name="incremental">The run was incremental.</param>
    /// <param name="failedProtected">Live documents whose URL failed in the run (<see cref="DeletionCandidates.FromTraversal"/>'s protected set).</param>
    /// <param name="earlierNewestFirst">The source's earlier crawl runs, newest first.</param>
    /// <param name="alertThreshold"><c>Settings.Crawl.IncompleteRunsAlert</c>.</param>
    /// <param name="continued">A <c>continue</c> run (#804), left out of the incomplete-traversal count like an incremental one.</param>
    public static CompletenessSection Completeness(
        CrawlRunRecord run, TraversalVerdict verdict, CompletenessRule rule, bool incremental, int failedProtected,
        IEnumerable<TraversalHistoryEntry> earlierNewestFirst, int alertThreshold, bool continued = false)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(verdict);
        ArgumentNullException.ThrowIfNull(earlierNewestFirst);
        var resolved = TraversalCompleteness.Resolved(run);
        // A continue run is no traversal either (#804): like an incremental one, it is left out of the count.
        var consecutive = TraversalCompleteness.ConsecutiveIncomplete(earlierNewestFirst.Prepend(new TraversalHistoryEntry(verdict.Complete, incremental || continued)));
        return new CompletenessSection(
            verdict.Complete,
            verdict.Reasons,
            rule == CompletenessRule.Strict ? "strict" : "ratio",
            incremental,
            run.Seeds,
            resolved,
            run.SeedsFailed,
            run.Seeds == 0 ? 0 : Math.Round((double)resolved / run.Seeds, 4),
            failedProtected,
            run.PausedHosts.ToDictionary(p => p.Key, p => p.Value.Reason, StringComparer.Ordinal),
            consecutive,
            TraversalCompleteness.ShouldAlert(consecutive, alertThreshold));
    }
}

/// <summary>One fetch of a sampled URL: its noise-normalised content hash (§12.2) and the sitemap <c>lastmod</c> it had then.</summary>
public sealed record LastmodObservation(string ContentHash, DateTimeOffset? Lastmod, DateTimeOffset FetchedAt);

/// <summary>A sampled URL's first and second fetch.</summary>
public sealed record LastmodSamplePair(string Url, LastmodObservation First, LastmodObservation Second);

/// <param name="Passed">At most 2 % of the compared URLs changed their content without a newer <c>lastmod</c>.</param>
/// <param name="Compared">Pairs fetched at least 7 days apart.</param>
/// <param name="Changed">Compared URLs whose content hash changed.</param>
/// <param name="ChangedWithoutNewerLastmod">Changed URLs whose <c>lastmod</c> did not move forward.</param>
/// <param name="Examples">Up to 10 of those URLs (for the recorded result, admin only).</param>
/// <param name="Refusal">Why the sample cannot pass whatever its share: <c>sample.too_small</c>.</param>
public sealed record LastmodSampleResult(bool Passed, int Compared, int Changed, int ChangedWithoutNewerLastmod, IReadOnlyList<string> Examples, string? Refusal)
{
    public double Share => Compared == 0 ? 0 : (double)ChangedWithoutNewerLastmod / Compared;
}

/// <summary>
/// The <c>lastmod</c> sample (connectors design §5.4, weakness review §3 "Sample threshold", to-verify #170): 500 random
/// sitemap URLs are fetched twice, at least 7 days apart, and their noise-normalised content hashes compared with their
/// <c>lastmod</c>. The sample passes when at most 2 % of them changed their content without a newer <c>lastmod</c>; only
/// then is <c>lastmodReliable</c> proposed for the host, as an <c>edit-site-profile</c> command (P1: a platform admin
/// applies it by hand from the recorded result).
/// </summary>
public static class LastmodSample
{
    public const int Size = 500;
    public const int MaxChangedPercent = 2;
    public const string TooSmall = "sample.too_small";
    public static readonly TimeSpan MinimumGap = TimeSpan.FromDays(7);

    /// <summary>Up to <paramref name="size"/> distinct URLs drawn uniformly from the host's sitemap URLs.</summary>
    public static IReadOnlyList<string> Pick(IEnumerable<string> sitemapUrls, int size = Size)
    {
        ArgumentNullException.ThrowIfNull(sitemapUrls);
        var urls = sitemapUrls.Distinct(StringComparer.Ordinal).ToArray();
        System.Security.Cryptography.RandomNumberGenerator.Shuffle<string>(urls);
        return urls.Length <= size ? urls : urls[..size];
    }

    /// <summary>Compares the pairs; a pair fetched less than 7 days apart is not compared.</summary>
    /// <param name="pairs">The sample.</param>
    /// <param name="requiredSize">Compared pairs needed to pass at all (500).</param>
    public static LastmodSampleResult Compare(IReadOnlyList<LastmodSamplePair> pairs, int requiredSize = Size)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        var compared = pairs.Where(p => p.Second.FetchedAt - p.First.FetchedAt >= MinimumGap).ToList();
        var changed = compared.Where(p => !string.Equals(p.First.ContentHash, p.Second.ContentHash, StringComparison.Ordinal)).ToList();
        var silent = changed.Where(p => !(p.Second.Lastmod > p.First.Lastmod)).ToList();
        var tooSmall = compared.Count < requiredSize;
        var passed = !tooSmall && silent.Count * 100L <= (long)MaxChangedPercent * compared.Count;
        return new LastmodSampleResult(passed, compared.Count, changed.Count, silent.Count, [.. silent.Take(10).Select(p => p.Url)], tooSmall ? TooSmall : null);
    }

    /// <summary>
    /// The <c>edit-site-profile</c> payload that sets <c>lastmodReliable</c> on the host's applied profile, or null when the
    /// sample did not pass, the host has no profile, or the flag is set already.
    /// </summary>
    public static EditSiteProfileCommand? Propose(LastmodSampleResult result, string host, AppliedSiteProfile? applied)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (!result.Passed || applied is null || applied.Profile.LastmodReliable)
        {
            return null;
        }

        return new EditSiteProfileCommand
        {
            SiteProfileId = applied.SiteProfileId,
            Host = host,
            BaseVersion = applied.Number,
            PresetId = applied.PresetId,
            Profile = applied.Profile with { LastmodReliable = true },
        };
    }

    /// <summary>The proposal as the <c>command</c> job a platform admin's confirmation enqueues (dedup <c>site-profile:{id}</c>).</summary>
    public static JobRequest ProposalJob(EditSiteProfileCommand proposal, Guid departmentId, string classification)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentException.ThrowIfNullOrWhiteSpace(classification);
        return new JobRequest(JobClasses.Command, departmentId, classification, ContractJson.Serialize(proposal))
        {
            DedupKey = proposal.SiteProfileId is { } id ? JobDedupKeys.SiteProfile(id) : JobDedupKeys.SiteProfileHost(classification, proposal.Host),
        };
    }
}
