using System.Data.Common;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.Worker.Connectors.Egress;
using Kalla.Worker.Connectors.Hosts;

namespace Kalla.Worker.Connectors.Crawl;

/// <summary>What the runner knows about the crawl run a job serves.</summary>
/// <param name="Run">The source, level and run.</param>
/// <param name="DepartmentId">The source's department (the jobs' fairness key).</param>
/// <param name="Kind">The run's kind, carried into continuations.</param>
/// <param name="Trigger">Manual runs ignore the night window (§6.5).</param>
/// <param name="Settings">The source's settings.</param>
/// <param name="Revalidate">
/// Every <c>revalidateEvery</c>-th traversal and forced runs send no validators (§4) and make no <c>lastmod</c> skip
/// (§5.4); see <see cref="CrawlFreshness.Revalidates"/>.
/// </param>
/// <param name="Incremental">
/// An incremental run (<c>sync</c> payload <c>incremental</c>, §5.4): only seeds with a newer sitemap <c>lastmod</c> and new
/// sitemap URLs are fetched, links are expanded only from changed pages, the traversal is never complete. Kept only when
/// every scope host's site profile marks <c>lastmod</c> reliable (<see cref="CrawlTraversal.StartAsync"/>).
/// </param>
public sealed record CrawlRunContext(CrawlRunKey Run, Guid DepartmentId, SyncKind Kind, RunTrigger Trigger, JsonElement Settings, bool Revalidate, bool Incremental = false)
{
    public FetchContext FetchContext => new(Run.SourceId, Run.Classification, Run.RunId, Settings, Revalidate);
}

/// <param name="Enqueued">The <c>fetch</c> jobs enqueued.</param>
/// <param name="WindowOpensAt">
/// Set when the closed night window holds back pending rows (or the one host asked for keeps to the window): nothing was
/// released for the hosts that keep to it, and the run's <c>sync</c> continuation (phase <c>fetch</c>,
/// <see cref="FetchTopUp.Continuation"/>) must wait until then so the run stays <c>running</c> (§6.5). Hosts whose
/// politeness profile says <c>crawlWindow: any</c> were topped up all the same.
/// </param>
public sealed record TopUpResult(int Enqueued, DateTimeOffset? WindowOpensAt);

/// <summary>
/// P1's release of a source's frontier (connectors design §6.0): each host keeps at most <c>Crawl:FetchBatchPerHost</c>
/// <c>fetch</c> jobs queued or running, topped up from its <c>pending</c> rows after the listing and whenever one of its
/// fetches finishes; dedup <c>fetch:{sourceId}:{urlHash}</c>. A scheduled traversal releases only inside the source's
/// night window, except to hosts whose politeness profile says <c>crawlWindow: any</c> (§6.6); a host the run paused three
/// times (§6.4) gets no more fetches, so its rows stay pending and the traversal is incomplete. The slot each fetch takes
/// (§6.3) still paces the requests.
/// </summary>
public sealed class FetchTopUp(
    ICrawlFrontierStore frontier, ICrawlRunStore runs, IJobQueue queue, ICrawlPolicySource policy, IOptions<CrawlOptions> options, TimeProvider time)
{
    /// <summary>Pauses after which a host's part of the run ends (§6.4).</summary>
    public const int MaxPausesPerRun = 3;

    /// <param name="context">The run.</param>
    /// <param name="host">One host (after its fetch finished), or null for every host of the run.</param>
    /// <param name="transaction">The caller's transaction, so released rows and their jobs commit together.</param>
    /// <param name="cancellationToken">Cancels the top-up.</param>
    public async Task<TopUpResult> TopUpAsync(CrawlRunContext context, string? host, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = ContractJson.Deserialize<Pipelines.Sources.CrawlSettings>(context.Settings);
        var now = time.GetUtcNow();
        var window = CrawlWindow.Parse(settings.CrawlWindow);
        var closed = context.Trigger != RunTrigger.Manual && !window.IsOpen(now);
        var current = policy.Current;
        if (closed && host is not null && !current.ProfileOf(host).AnyTime)
        {
            return new TopUpResult(0, window.NextOpening(now));
        }

        var run = await runs.GetAsync(context.Run.RunId, cancellationToken);
        var ended = run?.PausedHosts.Where(p => p.Value.Count >= MaxPausesPerRun).Select(p => p.Key).ToHashSet(StringComparer.Ordinal) ?? [];
        var batch = options.Value.FetchBatchPerHost;
        var jobs = new List<JobRequest>();
        var waiting = false;
        foreach (var load in await frontier.HostLoadAsync(context.Run, cancellationToken))
        {
            if ((host is not null && !string.Equals(load.Host, host, StringComparison.Ordinal)) || ended.Contains(load.Host) || load.Pending == 0)
            {
                continue;
            }

            if (closed && !current.ProfileOf(load.Host).AnyTime)
            {
                waiting = true;
                continue;
            }

            var want = batch - load.Released;
            if (want <= 0)
            {
                continue;
            }

            foreach (var row in await frontier.ReleaseAsync(context.Run, load.Host, want, cancellationToken))
            {
                var payload = new FetchPayload { SourceId = context.Run.SourceId, RunId = context.Run.RunId, UrlHash = row.UrlHash };
                jobs.Add(new JobRequest(JobClasses.Fetch, context.DepartmentId, context.Run.Classification, ContractJson.Serialize(payload))
                {
                    DedupKey = JobDedupKeys.FetchUrl(context.Run.SourceId, row.UrlHash),
                    RunId = context.Run.RunId,
                });
            }
        }

        if (jobs.Count > 0)
        {
            await queue.EnqueueAsync(jobs, transaction, cancellationToken);
        }

        return new TopUpResult(jobs.Count, waiting ? window.NextOpening(now) : null);
    }

    /// <summary>The run's <c>sync</c> continuation in phase <c>fetch</c>, ready at <paramref name="at"/> (the window's opening).</summary>
    public JobRequest Continuation(CrawlRunContext context, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(context);
        var payload = new SyncPayload
        {
            SourceId = context.Run.SourceId,
            RunId = context.Run.RunId,
            Kind = context.Kind,
            Trigger = context.Trigger,
            Phase = SyncPhase.Fetch,
            Incremental = context.Incremental,
        };
        var delay = at - time.GetUtcNow();
        return new JobRequest(JobClasses.Sync, context.DepartmentId, context.Run.Classification, ContractJson.Serialize(payload))
        {
            DedupKey = JobDedupKeys.Sync(context.Run.SourceId),
            RunId = context.Run.RunId,
            Delay = delay > TimeSpan.Zero ? delay : TimeSpan.Zero,
        };
    }
}
