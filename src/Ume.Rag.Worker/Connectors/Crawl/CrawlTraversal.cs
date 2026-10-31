using System.Collections.Concurrent;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Worker.Connectors.Deletion;
using Ume.Rag.Worker.Connectors.Hosts;
using Ume.Rag.Worker.Connectors.Robots;
using Ume.Rag.Worker.Connectors.Urls;

namespace Ume.Rag.Worker.Connectors.Crawl;

/// <summary>How a page relates to its declared <c>rel=canonical</c> (§3.3).</summary>
public enum CanonicalFoldKind
{
    /// <summary>No canonical, or the page's own identity.</summary>
    None,

    /// <summary>Folded: the canonical is in scope on the same host and its content hash in this run equals the page's; no document of its own (<c>identity.folded</c>).</summary>
    Folded,

    /// <summary>Kept: "canonical elsewhere, not verified" in the crawl report.</summary>
    Unverified,
}

public sealed record CanonicalFold(CanonicalFoldKind Kind, string? Canonical)
{
    public static CanonicalFold None { get; } = new(CanonicalFoldKind.None, null);
}

/// <summary>What a crawl <c>fetch</c> job does next.</summary>
public abstract record CrawlFetchResult
{
    private CrawlFetchResult()
    {
    }

    /// <summary>
    /// The URL is done for the run: the runner writes the document and run item from <paramref name="Outcome"/> (skipped
    /// when <paramref name="Fold"/> folded it). <paramref name="Facts"/> are run item facts such as <c>robots.noindex_ignored</c>.
    /// </summary>
    public sealed record Done(FetchOutcome Outcome, CanonicalFold Fold, int Admitted, IReadOnlyList<string> Facts) : CrawlFetchResult;

    /// <summary>Requeue the job after <paramref name="Delay"/> (null: the queue's back-off); the frontier row stays <c>released</c>.</summary>
    public sealed record Requeue(TimeSpan? Delay, string Reason, bool CountsAttempt) : CrawlFetchResult;

    /// <summary>Not fetched in this run (the host paused or blocked for it): no document change, the traversal is incomplete.</summary>
    public sealed record NotReached(string Reason) : CrawlFetchResult;

    /// <summary>The row is not released for this run (a duplicate or cancelled job): nothing to do.</summary>
    public sealed record Stale : CrawlFetchResult;
}

/// <summary>
/// The crawl's part of the <c>sync</c> and <c>fetch</c> jobs (connectors design §5, §6.0): seeds admitted to the source's
/// frontier, each released URL fetched within its host's budget, the outcome written to the frontier row, links
/// admitted by scope, depth, traps and <c>maxPages</c> (BFS by depth), canonical aliases folded only when verified,
/// <c>noindex</c>/<c>nofollow</c> honoured on external hosts and recorded on municipal ones, and the host topped up.
/// Documents and run items stay the engine's (item 9); it calls <see cref="AbandonAsync"/> when a fetch's attempts run
/// out and <see cref="EvaluateAsync"/> before the reconcile phase.
/// </summary>
public sealed class CrawlTraversal(
    CrawlConnector connector,
    RobotsCache robots,
    HostBudget budget,
    ICrawlPolicySource policy,
    ICrawlFrontierStore frontier,
    ICrawlRunStore runs,
    FetchTopUp topUp,
    IBlobStore blobs,
    IJobQueue queue,
    TimeProvider time)
{
    public const string NoIndex = "robots.noindex";
    public const string NoIndexIgnored = "robots.noindex_ignored";
    public const string NoFollowIgnored = "robots.nofollow_ignored";
    public const string Folded = "identity.folded";

    private readonly ConcurrentDictionary<Guid, TrapDetector> _traps = new();

    /// <summary>Opens the run's <c>CrawlRuns</c> row with the source's limits (kept when it exists: a resumed listing).</summary>
    public Task StartAsync(CrawlRunContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = connector.Source(context.Run.SourceId, context.Settings).Settings;
        return runs.StartAsync(new CrawlRunRecord
        {
            RunId = context.Run.RunId,
            Classification = context.Run.Classification,
            SourceId = context.Run.SourceId,
            MaxPages = settings.MaxPages,
            MaxDepth = settings.MaxDepth,
        }, cancellationToken);
    }

    /// <summary>
    /// One committed batch of the listing (the events since the last checkpoint): seeds admitted at depth 0, an unreachable
    /// robots.txt pauses its host for the run, every other failure is a failed seed. Returns the seeds admitted.
    /// </summary>
    public async Task<int> AdmitSeedsAsync(CrawlRunContext context, IReadOnlyList<ListingEvent<CrawlCheckpoint>> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(events);
        var admissions = new List<FrontierAdmission>();
        foreach (var e in events)
        {
            switch (e)
            {
                case ListingEvent<CrawlCheckpoint>.Item { Work: var work }:
                    admissions.Add(new FrontierAdmission(work.CanonicalIdentity, work.Url!.Authority, 0, false, work.Hints.SitemapLastMod));
                    break;
                case ListingEvent<CrawlCheckpoint>.Failure { Reason: CrawlConnector.RobotsUnreachable, Identity: var robotsUrl }:
                    await runs.PauseHostAsync(context.Run.RunId, new Uri(robotsUrl).Authority, CrawlConnector.RobotsUnreachable, null, cancellationToken);
                    break;
                case ListingEvent<CrawlCheckpoint>.Failure:
                    await runs.SeedFailedAsync(context.Run.RunId, cancellationToken);
                    break;
            }
        }

        return admissions.Count == 0 ? 0 : (await frontier.AdmitAsync(context.Run, admissions, cancellationToken)).Admitted.Count;
    }

    /// <summary>The <c>fetch</c> job of one released frontier row.</summary>
    public async Task<CrawlFetchResult> ProcessAsync(CrawlRunContext context, string urlHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var run = context.Run;
        var row = await frontier.GetAsync(run.SourceId, run.Classification, urlHash, cancellationToken);
        if (row is null || row.RunId != run.RunId || row.State != FrontierState.Released)
        {
            return new CrawlFetchResult.Stale();
        }

        var url = new Uri(row.Url);
        var robotsEntry = await robots.GetAsync(url, cancellationToken);
        await budget.RecordRobotsAsync(row.Host, robotsEntry, cancellationToken);
        if (robotsEntry.Status == RobotsStatus.Unreachable)
        {
            // An unreachable robots.txt pauses the host for the run (weakness review §3): its seeds fail, nothing is not-seen.
            await runs.PauseHostAsync(run.RunId, row.Host, CrawlConnector.RobotsUnreachable, null, cancellationToken);
            return await NotReachedAsync(context, row, CrawlConnector.RobotsUnreachable, cancellationToken);
        }

        switch (await budget.AcquireAsync(row.Host, cancellationToken))
        {
            case HostSlot.Later later:
                return new CrawlFetchResult.Requeue(later.Delay, later.Reason, CountsAttempt: false);
            case HostSlot.Refused { Reason: HostBudget.Blocked }:
                await runs.PauseHostAsync(run.RunId, row.Host, HostBudget.Blocked, null, cancellationToken);
                return await NotReachedAsync(context, row, HostBudget.Blocked, cancellationToken);
            case HostSlot.Refused refused:
                var record = await runs.GetAsync(run.RunId, cancellationToken);
                var pauses = record?.PausedHosts.GetValueOrDefault(row.Host)?.Count ?? 0;
                if (pauses >= FetchTopUp.MaxPausesPerRun || refused.Until is not { } until)
                {
                    return await NotReachedAsync(context, row, HostBudget.Paused, cancellationToken);
                }

                return new CrawlFetchResult.Requeue(until - time.GetUtcNow(), HostBudget.Paused, CountsAttempt: false);
            case HostSlot.Granted granted:
                using (granted.Lease)
                {
                    return await FetchAsync(context, row, granted.Lease, cancellationToken);
                }

            default:
                throw new InvalidOperationException("Unexpected host slot.");
        }
    }

    /// <summary>The fetch's attempts ran out (5xx, timeouts): the row is done for the run, a seed counts as failed.</summary>
    public async Task AbandonAsync(CrawlRunContext context, string urlHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var row = await frontier.GetAsync(context.Run.SourceId, context.Run.Classification, urlHash, cancellationToken);
        if (row is null || row.RunId != context.Run.RunId || row.State == FrontierState.Idle)
        {
            return;
        }

        await NotReachedAsync(context, row, "fetch.abandoned", cancellationToken);
    }

    /// <summary>
    /// Before the reconcile phase: decides the traversal's completeness from the run's facts and its open frontier rows
    /// (P1a's strict rule, <see cref="TraversalCompleteness"/>), stores it on <c>CrawlRuns.Complete</c> and drops the run's
    /// trap counters.
    /// </summary>
    public async Task<TraversalVerdict> EvaluateAsync(CrawlRunKey run, bool cancelled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var record = await runs.GetAsync(run.RunId, cancellationToken) ?? throw new InvalidOperationException("The crawl run has no CrawlRuns row.");
        var open = (await frontier.HostLoadAsync(run, cancellationToken)).Sum(l => l.Pending + l.Released);
        var verdict = TraversalCompleteness.Evaluate(record, open, cancelled);
        await runs.SetCompleteAsync(run.RunId, verdict.Complete, cancellationToken);
        _traps.TryRemove(run.RunId, out _);
        return verdict;
    }

    private async Task<CrawlFetchResult> FetchAsync(CrawlRunContext context, FrontierRow row, HostLease lease, CancellationToken cancellationToken)
    {
        var run = context.Run;
        var hasValidators = row.ETag is not null || row.LastModified is not null;
        // A 304 expands the stored outlinks; without them the page is fetched in full so its links are not lost.
        var conditional = hasValidators && row.ContentHash is { } stored && await blobs.ExistsAsync(PageLinks.KeyOf(run.Classification, stored), cancellationToken);
        var item = new WorkItem(IdentityKind.Url, row.Url, new Uri(row.Url), row.Depth,
            new WorkHints(conditional ? new Validators(row.ETag, row.LastModified) : null, row.ContentHash, row.SitemapLastMod));

        var started = time.GetTimestamp();
        var page = await connector.FetchPageAsync(context.FetchContext, item, cancellationToken);
        var verdict = await lease.CompleteAsync(Response(page.Outcome, time.GetElapsedTime(started)), cancellationToken);
        if (verdict.PausedUntil is { } until)
        {
            await runs.PauseHostAsync(run.RunId, row.Host, verdict.Reason!, until, cancellationToken);
        }

        if (page.Outcome is FetchOutcome.Transient transient)
        {
            return new CrawlFetchResult.Requeue(transient.RetryAfter, transient.Reason, transient.CountsAttempt);
        }

        var now = time.GetUtcNow();
        var contentHash = page.Outcome switch
        {
            FetchOutcome.Content content => content.Raw.ContentHash,
            FetchOutcome.NotModified => row.ContentHash,
            _ => null,
        };
        var validators = page.Outcome switch
        {
            FetchOutcome.Content content => content.Validators,
            FetchOutcome.NotModified notModified => notModified.Validators,
            _ => new Validators(row.ETag, row.LastModified),
        };
        var redirectTo = page.Outcome is FetchOutcome.Content { Facts.Redirects.Count: > 0 } redirected ? redirected.Facts.CanonicalIdentity : null;
        var facts = new FrontierFacts(now, StatusOf(page.Outcome), validators.ETag, validators.LastModified, contentHash ?? row.ContentHash, redirectTo);
        await frontier.CompleteAsync(run, row.UrlHash, facts, cancellationToken);
        if (redirectTo is not null && page.Outcome is FetchOutcome.Content final)
        {
            await frontier.RecordTargetAsync(run, new FrontierAdmission(redirectTo, final.Facts.FinalUrl!.Authority, row.Depth, row.Leaf), facts with { RedirectTo = null }, cancellationToken);
        }

        if (row.Depth == 0 && page.Outcome is FetchOutcome.Failed or FetchOutcome.Refused { Outcome: RunItemOutcome.Blocked })
        {
            await runs.SeedFailedAsync(run.RunId, cancellationToken);
        }

        var outcome = page.Outcome;
        var runFacts = new List<string>();
        var admitted = 0;
        if (page.Links is { } links && contentHash is not null)
        {
            var municipal = policy.Current.KindOf(row.Host) == HostKind.Municipal;
            var follow = !links.NoFollow || municipal;
            if (links.NoIndex && municipal)
            {
                runFacts.Add(NoIndexIgnored);
            }

            if (links.NoFollow && municipal)
            {
                runFacts.Add(NoFollowIgnored);
            }

            if (links.NoIndex && !municipal && outcome is FetchOutcome.Content)
            {
                outcome = new FetchOutcome.Skipped(NoIndex);
            }

            if (follow && !row.Leaf)
            {
                admitted = await AdmitLinksAsync(context, row, links.Links, contentHash, cancellationToken);
            }
        }

        var fold = outcome is FetchOutcome.Content page200 ? await FoldAsync(context, page200, cancellationToken) : CanonicalFold.None;
        if (fold.Kind == CanonicalFoldKind.Folded)
        {
            runFacts.Add(Folded);
        }

        // Every host, not only this one: the page may have admitted links to other hosts (extra hosts, off-site leaves).
        await TopUpAsync(context, admitted > 0 ? null : row.Host, cancellationToken);
        return new CrawlFetchResult.Done(outcome, fold, admitted, runFacts);
    }

    private async Task<int> AdmitLinksAsync(CrawlRunContext context, FrontierRow row, IReadOnlyList<string> links, string contentHash, CancellationToken cancellationToken)
    {
        var source = connector.Source(context.Run.SourceId, context.Settings);
        var traps = _traps.GetOrAdd(context.Run.RunId, _ => new TrapDetector(time));
        if (row.Depth >= source.Settings.MaxDepth || !traps.ShouldExpand(contentHash))
        {
            return 0;
        }

        var admissions = new List<FrontierAdmission>();
        var found = new Dictionary<string, TrapStat>(StringComparer.Ordinal);
        foreach (var link in links)
        {
            if (UrlCanonicalizer.Canonicalize(link, source.Canonicalizer).Value is not { } canonical)
            {
                continue;
            }

            var scope = source.Scope.Check(canonical);
            if (!scope.Admitted)
            {
                continue;
            }

            var uri = new Uri(canonical);
            if (traps.Check(uri) is { } trap)
            {
                found[trap] = found.TryGetValue(trap, out var stat) ? stat with { Count = stat.Count + 1 } : new TrapStat(1, canonical);
                continue;
            }

            admissions.Add(new FrontierAdmission(canonical, uri.Authority, (short)(row.Depth + 1), scope.Leaf));
        }

        if (found.Count > 0)
        {
            await runs.RecordTrapsAsync(context.Run.RunId, found, cancellationToken);
        }

        if (admissions.Count == 0)
        {
            return 0;
        }

        var result = await frontier.AdmitAsync(context.Run, admissions, cancellationToken);
        foreach (var admitted in result.Admitted)
        {
            traps.Admitted(new Uri(admitted.Url));
        }

        return result.Admitted.Count;
    }

    /// <summary>§3.3: fold only into a canonical in scope on the same host whose content hash, fetched in this run, equals the page's.</summary>
    private async Task<CanonicalFold> FoldAsync(CrawlRunContext context, FetchOutcome.Content content, CancellationToken cancellationToken)
    {
        if (content.Facts.CanonicalHint is not { } hint || string.Equals(hint, content.Facts.CanonicalIdentity, StringComparison.Ordinal))
        {
            return CanonicalFold.None;
        }

        var source = connector.Source(context.Run.SourceId, context.Settings);
        var sameHost = string.Equals(new Uri(hint).Authority, content.Facts.FinalUrl!.Authority, StringComparison.OrdinalIgnoreCase);
        if (sameHost && source.Scope.Check(hint).Admitted)
        {
            var hash = Convert.ToHexStringLower(UrlCanonicalizer.IdentityHash(hint));
            var target = await frontier.GetAsync(context.Run.SourceId, context.Run.Classification, hash, cancellationToken);
            if (target is { LastFetchedAt: not null } && target.RunId == context.Run.RunId
                && string.Equals(target.ContentHash, content.Raw.ContentHash, StringComparison.Ordinal))
            {
                return new CanonicalFold(CanonicalFoldKind.Folded, hint);
            }
        }

        return new CanonicalFold(CanonicalFoldKind.Unverified, hint);
    }

    private async Task<CrawlFetchResult> NotReachedAsync(CrawlRunContext context, FrontierRow row, string reason, CancellationToken cancellationToken)
    {
        await frontier.CompleteAsync(context.Run, row.UrlHash, null, cancellationToken);
        if (row.Depth == 0)
        {
            await runs.SeedFailedAsync(context.Run.RunId, cancellationToken);
        }

        await TopUpAsync(context, row.Host, cancellationToken);
        return new CrawlFetchResult.NotReached(reason);
    }

    private async Task TopUpAsync(CrawlRunContext context, string? host, CancellationToken cancellationToken)
    {
        var result = await topUp.TopUpAsync(context, host, null, cancellationToken);
        if (result.WindowOpensAt is { } opens)
        {
            // Keeps the run running through the day: the sync continuation releases the rest when the window opens.
            await queue.EnqueueAsync([topUp.Continuation(context, opens)], null, cancellationToken);
        }
    }

    private static HostResponse Response(FetchOutcome outcome, TimeSpan latency) => outcome switch
    {
        FetchOutcome.Content or FetchOutcome.NotModified => new HostResponse(HostResponseKind.Success, latency),
        FetchOutcome.Transient { Reason: "rate_limited", RetryAfter: var retryAfter } => new HostResponse(HostResponseKind.RateLimited, latency, retryAfter),
        FetchOutcome.Transient { Reason: "http.5xx" or "net.timeout" } or FetchOutcome.Gone { Reason: "http.404" } => new HostResponse(HostResponseKind.Failure, latency),
        _ => new HostResponse(HostResponseKind.Neutral, latency),
    };

    /// <summary><c>CrawlFrontier.LastStatus</c> from the outcome: 200, 304, or the HTTP code in an <c>http.NNN</c> reason.</summary>
    private static short? StatusOf(FetchOutcome outcome)
    {
        var reason = outcome switch
        {
            FetchOutcome.Content => "http.200",
            FetchOutcome.NotModified => "http.304",
            FetchOutcome.Gone { Reason: "http.moved_out_of_scope" } => "http.301",
            FetchOutcome.Gone gone => gone.Reason,
            FetchOutcome.Failed failed => failed.Reason,
            _ => null,
        };
        return reason is not null && reason.StartsWith("http.", StringComparison.Ordinal)
            && short.TryParse(reason.AsSpan(5), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var code)
            ? code
            : null;
    }
}
