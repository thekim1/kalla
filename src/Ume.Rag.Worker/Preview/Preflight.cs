using System.Globalization;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Pipelines.Sources;
using Ume.Rag.Worker.Connectors;
using Ume.Rag.Worker.Connectors.Crawl;
using Ume.Rag.Worker.Connectors.Egress;
using Ume.Rag.Worker.Connectors.Hosts;
using Ume.Rag.Worker.Connectors.Http;
using Ume.Rag.Worker.Connectors.Malware;
using Ume.Rag.Worker.Connectors.Robots;
using Ume.Rag.Worker.Connectors.Urls;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Processing.Html;

namespace Ume.Rag.Worker.Preview;

/// <summary>
/// <c>preflight</c> (connectors design §2.2; admin design §4.7, §5.4 step 4) of an unsaved URL-list or crawl configuration,
/// within the 2-minute preview deadline. Each request goes through the production fetch path (egress guard, robots.txt,
/// redirects) after a slot of the host's budget; results are <c>&lt;area&gt;.&lt;code&gt;</c> codes only.
/// <list type="bullet">
/// <item>URL list: the first 20 URLs, one entry per URL that is not reachable (<c>reachability</c>, <c>robots</c> or
/// <c>egress</c>, a <c>warn</c>: the rest of the list still syncs), the robots.txt status per host, and a <c>fail</c>
/// when no sampled URL answers.</item>
/// <item>Crawl: the start URLs (any failure is a <c>fail</c>: the crawl would have no seeds), robots.txt per start host
/// (unreachable is a <c>fail</c>: the host is paused for the run, connectors design §5.1), the sitemaps and the pages they
/// list in scope (the crawl's own listing) against <c>maxPages</c>.</item>
/// <item>Both: the CMS preset the first HTML page's fingerprint selects.</item>
/// </list>
/// </summary>
public sealed partial class Preflight(
    PreviewConfig config,
    HttpFetcher fetcher,
    RobotsCache robots,
    IMalwareScanner scanner,
    IBlobStore blobs,
    HostBudget budget,
    TimeProvider time,
    ILogger<Preflight> logger)
{
    /// <summary>The URLs a URL-list preflight samples (admin design §4.7).</summary>
    public const int MaxUrls = 20;

    /// <summary>The listing cap of the page estimate: the sources schema's <c>maxPages</c> maximum.</summary>
    public const int EstimateCap = 1_000_000;

    public const string Reachability = "reachability";
    public const string Robots = "robots";
    public const string Egress = "egress";
    public const string Sitemaps = "sitemaps";
    public const string Pages = "pages";
    public const string Cms = "cms";

    public const string NoneReachable = "preflight.none_reachable";
    public const string DeadlineReached = "preflight.deadline";

    public async Task<JobResult> RunAsync(LeasedJob job, PreflightPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(payload);
        if (await config.CollectionAsync(payload.CollectionId, cancellationToken) is not { } collection)
        {
            return new JobResult.FatalError(StagePreview.CollectionUnknown);
        }

        var checks = new List<PreflightCheckResult>();
        var found = new Findings();
        using var deadline = new CancellationTokenSource(StagePreview.Deadline, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            switch (payload.SourceType)
            {
                case SourceTypes.UrlList:
                    if (Bind<UrlListSettings>(payload.Settings) is not { } list)
                    {
                        return new JobResult.FatalError(PreviewJobHandler.InvalidPayload);
                    }

                    await UrlListAsync(list, checks, found, linked.Token);
                    break;
                case SourceTypes.Crawl:
                    if (Bind<CrawlSettings>(payload.Settings) is not { } crawl)
                    {
                        return new JobResult.FatalError(PreviewJobHandler.InvalidPayload);
                    }

                    await CrawlAsync(job.Id, collection.Classification, crawl, checks, found, linked.Token);
                    break;
                default:
                    return new JobResult.FatalError(PreviewJobHandler.InvalidPayload);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            checks.Add(Check(Reachability, PreflightState.Fail, DeadlineReached, null));
        }

        checks.Add(found.Cms is { } cms ? Check(Cms, PreflightState.Pass, "cms." + cms, found.CmsHost) : Check(Cms, PreflightState.Pass, "cms.unknown", null));
        var result = new PreflightResult
        {
            State = checks.Max(c => c.State),
            Checks = [.. checks.Take(100)],
            EstimatedPages = found.EstimatedPages,
            Cms = found.Cms,
        };
        LogPreflight(logger, job.Id, payload.SourceType, result.State.ToString());
        return new JobResult.Success(ContractJson.Serialize(result));
    }

    private async Task UrlListAsync(UrlListSettings settings, List<PreflightCheckResult> checks, Findings found, CancellationToken cancellationToken)
    {
        var canonicalizer = new CanonicalizerOptions { IgnoreQueryParams = settings.IgnoreQueryParams, DirectoryIndexNames = settings.DirectoryIndexNames };
        var reachable = 0;
        var origins = new List<Uri>();
        foreach (var url in settings.Urls.Take(MaxUrls))
        {
            var (check, code, asked) = await ProbeAsync(url, canonicalizer, found, cancellationToken);
            if (check is null)
            {
                reachable++;
            }
            else
            {
                checks.Add(Check(check, PreflightState.Warn, code!, url));
            }

            AddOrigin(origins, asked);
        }

        foreach (var origin in origins)
        {
            checks.Add(await RobotsAsync(origin, unreachable: PreflightState.Warn, cancellationToken));
        }

        checks.Add(reachable == 0
            ? Check(Reachability, PreflightState.Fail, NoneReachable, null)
            : Check(Reachability, PreflightState.Pass, "http.200", null, new { reachable, sampled = Math.Min(settings.Urls.Count, MaxUrls) }));
    }

    private async Task CrawlAsync(Guid jobId, string level, CrawlSettings settings, List<PreflightCheckResult> checks, Findings found,
        CancellationToken cancellationToken)
    {
        var canonicalizer = new CanonicalizerOptions { IgnoreQueryParams = settings.IgnoreQueryParams, DirectoryIndexNames = settings.DirectoryIndexNames };
        var origins = new List<Uri>();
        foreach (var url in settings.StartUrls.Take(MaxUrls))
        {
            var (check, code, asked) = await ProbeAsync(url, canonicalizer, found, cancellationToken);
            checks.Add(check is null ? Check(Reachability, PreflightState.Pass, "http.200", url) : Check(check, PreflightState.Fail, code!, url));
            AddOrigin(origins, asked);
        }

        foreach (var origin in origins)
        {
            checks.Add(await RobotsAsync(origin, unreachable: PreflightState.Fail, cancellationToken));
        }

        // The crawl's own listing: start URLs, then every sitemap's URLs in scope (index depth ≤ 2), up to the estimate cap.
        var connector = new CrawlConnector(fetcher, robots, scanner, new PreviewBlobs(blobs, jobId), time);
        var estimate = ContractJson.ToElement(settings with { MaxPages = EstimateCap });
        var listed = 0;
        await foreach (var listing in connector.ListAsync(new ListingContext(jobId, level, jobId, estimate), new CrawlCheckpoint(), cancellationToken))
        {
            switch (listing)
            {
                case ListingEvent<CrawlCheckpoint>.Item:
                    listed++;
                    break;
                case ListingEvent<CrawlCheckpoint>.Failure { Reason: var reason, Identity: var identity }
                    when reason.StartsWith(CrawlConnector.SitemapReasonPrefix, StringComparison.Ordinal):
                    checks.Add(Check(Sitemaps, PreflightState.Warn, reason, identity));
                    break;
                case ListingEvent<CrawlCheckpoint>.Failure { Reason: var reason, Identity: var identity }
                    when reason != CrawlConnector.RobotsUnreachable && reason != CrawlConnector.LimitPages:
                    checks.Add(Check(Reachability, PreflightState.Warn, reason, identity));
                    break;
            }
        }

        var fromSitemaps = Math.Max(0, listed - settings.StartUrls.Count);
        checks.Add(fromSitemaps > 0
            ? Check(Sitemaps, PreflightState.Pass, "sitemap.found", null, new { urls = fromSitemaps })
            : Check(Sitemaps, PreflightState.Warn, "sitemap.none", null));
        found.EstimatedPages = listed;
        checks.Add(listed > settings.MaxPages
            ? Check(Pages, PreflightState.Warn, CrawlConnector.LimitPages, null, new { estimatedPages = listed, settings.MaxPages })
            : Check(Pages, PreflightState.Pass, "pages.within_limit", null, new { estimatedPages = listed, settings.MaxPages }));
    }

    /// <summary>
    /// One GET of <paramref name="url"/>: the check and code it failed (both null when it answered 2xx), and the URL whose
    /// robots.txt was asked (null when the guard refused it first).
    /// </summary>
    private async Task<(string? Check, string? Code, Uri? Asked)> ProbeAsync(string url, CanonicalizerOptions canonicalizer, Findings found,
        CancellationToken cancellationToken)
    {
        var canonical = UrlCanonicalizer.Canonicalize(url, canonicalizer);
        if (!canonical.IsValid)
        {
            return (Reachability, canonical.Refusal, null);
        }

        var target = canonical.ToUri();
        var request = new HttpFetchRequest(target, EgressPurpose.Preflight, null, MediaTypes.DocumentFormats, canonicalizer, robots.CheckAsync);
        HttpFetchResult result;
        var (lease, refusal) = await PreviewHosts.SlotAsync(budget, target.Authority, time, cancellationToken);
        if (lease is null)
        {
            return (Reachability, refusal, null);
        }

        using (lease)
        {
            var started = time.GetTimestamp();
            result = await fetcher.FetchAsync(request, cancellationToken);
            var outcome = result is HttpFetchResult.Ok ? null : FetchOutcomes.From(result);
            await lease.CompleteAsync(outcome is null
                ? new HostResponse(HostResponseKind.Success, time.GetElapsedTime(started))
                : PreviewHosts.Response(outcome, time.GetElapsedTime(started)), cancellationToken);
        }

        switch (result)
        {
            case HttpFetchResult.Ok ok:
                await using (ok.Body)
                {
                    if (found.Cms is null && MediaTypes.IsHtml(ok.Body.MediaType))
                    {
                        await using var stream = ok.Body.OpenRead();
                        using var document = await new HtmlParser().ParseDocumentAsync(stream, cancellationToken);
                        if (SitevisionPreset.Recognises(document))
                        {
                            (found.Cms, found.CmsHost) = (SitevisionPreset.Name, target.IdnHost);
                        }
                    }
                }

                return (null, null, target);
            case HttpFetchResult.Skipped skipped:
                return (Robots, skipped.Reason, target);
            case HttpFetchResult.Refused refused when refused.Reason.StartsWith("egress.", StringComparison.Ordinal):
                return (Egress, refused.Reason, null);
            case HttpFetchResult.Refused refused when refused.Reason.StartsWith("robots.", StringComparison.Ordinal):
                return (Robots, refused.Reason, target);
            case HttpFetchResult.Status status:
                return (Reachability, "http." + ((int)status.Code).ToString(CultureInfo.InvariantCulture), target);
            default:
                return (Reachability, FetchOutcomes.From(result) switch
                {
                    FetchOutcome.Refused refused => refused.Reason,
                    FetchOutcome.Failed failed => failed.Reason,
                    FetchOutcome.Transient transient => transient.Reason.Contains('.', StringComparison.Ordinal) ? transient.Reason : "http." + transient.Reason,
                    FetchOutcome.Gone gone => gone.Reason,
                    FetchOutcome.Skipped skipped => skipped.Reason,
                    _ => "http.unexpected",
                }, target);
        }
    }

    private static void AddOrigin(List<Uri> origins, Uri? asked)
    {
        if (asked is not null && !origins.Any(o => o.Scheme == asked.Scheme && o.Authority == asked.Authority))
        {
            origins.Add(asked);
        }
    }

    /// <summary>The robots.txt status of <paramref name="origin"/>'s host (the entry the fetches above cached).</summary>
    private async Task<PreflightCheckResult> RobotsAsync(Uri origin, PreflightState unreachable, CancellationToken cancellationToken)
    {
        var entry = await robots.GetAsync(origin, cancellationToken);
        var target = origin.GetLeftPart(UriPartial.Authority);
        return entry.Status switch
        {
            RobotsStatus.Ok => Check(Robots, PreflightState.Pass, "robots.ok", target),
            RobotsStatus.Missing => Check(Robots, PreflightState.Pass, "robots.missing", target),
            _ => Check(entry.EgressRefusal is null ? Robots : Egress, unreachable, entry.EgressRefusal ?? CrawlConnector.RobotsUnreachable, target),
        };
    }

    private static PreflightCheckResult Check(string check, PreflightState state, string code, string? target, object? facts = null) => new()
    {
        Check = check,
        State = state,
        Code = code,
        Target = target,
        Facts = facts is null ? null : JsonSerializer.SerializeToElement(facts, ContractJson.Options),
    };

    private static T? Bind<T>(JsonElement settings) where T : class
    {
        try
        {
            return ContractJson.Deserialize<T>(settings);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What the checks learned on the way: the CMS of the first recognised HTML page and the page estimate.</summary>
    private sealed class Findings
    {
        public string? Cms { get; set; }

        public string? CmsHost { get; set; }

        public int? EstimatedPages { get; set; }
    }

    [LoggerMessage(LogLevel.Information, "Preflight {JobId} ({SourceType}): {State}")]
    private static partial void LogPreflight(ILogger logger, Guid jobId, string sourceType, string state);
}
