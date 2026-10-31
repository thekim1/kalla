using System.Text.Json;
using System.Text.RegularExpressions;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Pipelines.Definitions;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Worker.Connectors;
using Ume.Rag.Worker.Connectors.Hosts;
using Ume.Rag.Worker.Connectors.Http;
using Ume.Rag.Worker.Connectors.Malware;
using Ume.Rag.Worker.Connectors.Robots;
using Ume.Rag.Worker.Connectors.Uploads;
using Ume.Rag.Worker.Connectors.UrlList;
using Ume.Rag.Worker.Connectors.Urls;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Persistence;
using Outcome = Ume.Rag.Pipelines.Jobs.RunItemOutcome;

namespace Ume.Rag.Worker.Preview;

/// <summary>
/// <c>stage-preview</c> (engine design §11; admin design §4.7, §6.4): up to five existing documents (their raw blobs), URLs
/// (fetched live through the URL-list connector, so the egress guard, robots.txt and the malware scan apply, after a slot of
/// the host's politeness budget) or upload slots (through the upload connector) through the pipeline version, draft or
/// published, up to <c>untilStage</c>, within a 2-minute deadline. Every write goes to <c>previews/{jobId}/</c>
/// (<see cref="PreviewBlobs"/>); the one <c>content</c> write is the host budget's <c>CrawlHosts</c> row, and the job's
/// <c>Result</c>. With <c>basePreviewJobId</c> the earlier preview's raw content and stage outputs are reused, so only the
/// stages whose key changed run.
/// </summary>
public sealed partial class StagePreview(
    PreviewConfig config,
    SourceContexts sources,
    DocumentStore documents,
    INodeOutputStore nodeOutputs,
    IBlobStore blobs,
    PreviewSegment segment,
    HttpFetcher fetcher,
    RobotsCache robots,
    IMalwareScanner scanner,
    IUploadSlotReader slots,
    HostBudget budget,
    TimeProvider time,
    ILogger<StagePreview> logger)
{
    public const int MaxDocuments = 5;

    /// <summary>The preview's whole run (engine design §11).</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromMinutes(2);

    /// <summary>How long preview blobs are kept (data-model §9).</summary>
    public static readonly TimeSpan BlobLifetime = TimeSpan.FromHours(24);

    public const string LimitDocuments = "preview.limit_documents";
    public const string CollectionUnknown = "preview.collection_unknown";
    public const string SourceUnknown = "preview.source_unknown";
    public const string DocumentUnknown = "preview.document_unknown";
    public const string UploadUnknown = "preview.upload_unknown";
    public const string DeadlineReached = "preview.deadline";

    public async Task<JobResult> RunAsync(LeasedJob job, StagePreviewPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(payload);
        var lists = new[] { payload.DocumentIds?.Count, payload.Urls?.Count, payload.UploadIds?.Count };
        if (lists.Count(c => c is not null) != 1 || lists.Any(c => c == 0) || (payload.UploadIds is not null && payload.SourceId is null))
        {
            return new JobResult.FatalError(PreviewJobHandler.InvalidPayload);
        }

        var count = lists.Sum(c => c ?? 0);
        if (count > MaxDocuments)
        {
            return new JobResult.FatalError(LimitDocuments);
        }

        if (await config.CollectionAsync(payload.CollectionId, cancellationToken) is not { } collection)
        {
            return new JobResult.FatalError(CollectionUnknown);
        }

        SourceContext? source = null;
        if (payload.SourceId is { } sourceId && (source = await sources.GetAsync(sourceId, cancellationToken)) is null)
        {
            return new JobResult.FatalError(SourceUnknown);
        }

        PipelineRun pipeline;
        try
        {
            pipeline = await config.VersionAsync(payload.PipelineVersionId, source?.Type, cancellationToken);
        }
        catch (PipelineRefusedException refused)
        {
            return new JobResult.FatalError(refused.Reason);
        }

        var store = new PreviewStore(new PreviewBlobs(blobs, job.Id), nodeOutputs, payload.BasePreviewJobId);
        var run = new PreviewRun(job.Id, pipeline, collection, Stages(pipeline.Pipeline, payload.UntilStage), store)
        {
            Embed = payload.Embed,
            PiiSpans = payload.PiiSpans,
        };
        var previous = payload.BasePreviewJobId is { } baseId ? await config.BaseAsync(baseId, cancellationToken) : null;
        var level = source?.Classification ?? collection.Classification;

        using var deadline = new CancellationTokenSource(Deadline, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var results = new List<PreviewDocumentResult>();
        var started = time.GetTimestamp();
        for (var i = 0; i < count; i++)
        {
            var log = new PreviewStageLog(run.Stages, time);
            var named = new PreviewDocumentResult
            {
                DocumentId = payload.DocumentIds?[i],
                Url = payload.Urls?[i],
                UploadId = payload.UploadIds?[i],
                Stages = log.Results,
            };
            if (deadline.IsCancellationRequested)
            {
                log.Fail(DeadlineReached);
                results.Add(named);
                continue;
            }

            PreviewSiteProfile? siteProfile = null;
            try
            {
                log.Begin(StageNames.Fetch);
                var content = await FetchAsync(run, store, source, level, previous, payload, i, log, linked.Token);
                siteProfile = await segment.RunAsync(run, content, log, linked.Token);
            }
            catch (StageFatalException fatal)
            {
                log.Fail(fatal.Reason);
            }
            catch (StageTransientException transient) when (transient.CountsAttempt)
            {
                log.Fail(transient.Code);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                log.Fail(DeadlineReached);
            }

            results.Add(named with { SiteProfile = siteProfile });
        }

        LogPreviewed(logger, job.Id, results.Count, time.GetElapsedTime(started).TotalMilliseconds);
        var result = new StagePreviewResult { Documents = results, ExpiresAt = time.GetUtcNow() + BlobLifetime };
        return new JobResult.Success(ContractJson.Serialize(result));
    }

    /// <summary>The template's stages in order, up to <paramref name="untilStage"/>.</summary>
    internal static IReadOnlyList<string> Stages(ResolvedPipeline pipeline, string untilStage)
    {
        var last = StageNames.Order.ToList().IndexOf(untilStage);
        return [.. StageNames.Order.Where((s, i) => i <= (last < 0 ? StageNames.Order.Count - 1 : last) && pipeline.Template.Template.Stages.ContainsKey(s))];
    }

    /// <summary>The document's raw content, with the <c>fetch</c> (and <c>scan</c>) stage logged; a fetch that ends the document throws.</summary>
    private async Task<PreviewContent> FetchAsync(PreviewRun run, PreviewStore store, SourceContext? source, string level, StagePreviewResult? previous,
        StagePreviewPayload payload, int index, PreviewStageLog log, CancellationToken cancellationToken)
    {
        PreviewContent content;
        bool reused;
        if (payload.DocumentIds?[index] is { } documentId)
        {
            var document = await documents.GetAsync(documentId, cancellationToken);
            if (document is not { DeletedAt: null, ContentHash: { } hash } || (source is not null && document.SourceId != source.SourceId))
            {
                throw new StageFatalException(Outcome.Failed, DocumentUnknown);
            }

            content = new PreviewContent(index, document.Classification, hash, RawContentKeys.Of(document))
            {
                MediaType = document.MediaType,
                Url = document.Url,
                DisplayName = document.DisplayName,
                DocumentId = document.Id,
                SourceId = document.SourceId,
            };
            reused = true;
        }
        else if (payload.Urls?[index] is { } url)
        {
            var copied = await ReuseAsync(store, previous, level, url, index, cancellationToken);
            reused = copied is not null;
            content = (copied ?? await FetchUrlAsync(run, store, source, level, url, index, cancellationToken)) with { SourceId = source?.SourceId };
        }
        else
        {
            content = await FetchUploadAsync(store, source!, payload.UploadIds![index], index, cancellationToken) with { SourceId = source!.SourceId };
            reused = false;
        }

        log.Done(reused, new { contentHash = Convert.ToHexStringLower(content.ContentHash), content.MediaType });
        if (log.Wants(StageNames.Scan))
        {
            // The connectors scan files before they are stored; an existing document was scanned when it was fetched.
            log.Begin(StageNames.Scan);
            log.Done(reused);
        }

        return content;
    }

    /// <summary>The base preview's raw content of the same URL, copied into this preview; null when there is none.</summary>
    private async Task<PreviewContent?> ReuseAsync(PreviewStore store, StagePreviewResult? previous, string level, string url, int index,
        CancellationToken cancellationToken)
    {
        if (previous?.Documents.FirstOrDefault(d => string.Equals(d.Url, url, StringComparison.Ordinal)) is not { } earlier ||
            earlier.Stages.FirstOrDefault(s => s.Stage == StageNames.Fetch && s.State == PreviewStageState.Succeeded)?.Facts is not { } facts ||
            !facts.TryGetProperty("contentHash", out var hashText) || hashText.GetString() is not { } hex || !Sha256().IsMatch(hex) ||
            store.BaseJobId is not { } baseId)
        {
            return null;
        }

        var raw = new BlobKey($"raw/{level}/{hex}");
        await using (var stream = await blobs.OpenReadAsync(new BlobKey($"{PreviewBlobs.PrefixOf(baseId).Value}/{raw.Value}"), null, cancellationToken))
        {
            if (stream is null)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;
            await store.Blobs.PutAsync(raw, buffer, buffer.Length, cancellationToken);
        }

        return new PreviewContent(index, level, Convert.FromHexString(hex), raw)
        {
            MediaType = facts.TryGetProperty("mediaType", out var media) ? media.GetString() : null,
            Url = earlier.Url,
        };
    }

    private async Task<PreviewContent> FetchUrlAsync(PreviewRun run, PreviewStore store, SourceContext? source, string level, string url, int index,
        CancellationToken cancellationToken)
    {
        var settings = source?.Settings ?? EmptySettings;
        var canonical = UrlCanonicalizer.Canonicalize(url, (settings.Deserialize<UrlListSettings>(JsonSerializerOptions.Web) ?? new()).Canonicalizer());
        if (!canonical.IsValid)
        {
            throw new StageFatalException(Outcome.Failed, canonical.Refusal!);
        }

        var item = new WorkItem(IdentityKind.Url, canonical.Value!, canonical.ToUri(), 0, WorkHints.None);
        var connector = new UrlListConnector(fetcher, robots, scanner, store.Blobs, time);
        var context = new FetchContext(source?.SourceId ?? Guid.Empty, level, run.JobId, settings, Revalidate: true);
        var (lease, refusal) = await PreviewHosts.SlotAsync(budget, item.Url!.Authority, time, cancellationToken);
        if (lease is null)
        {
            throw new StageFatalException(Outcome.Failed, refusal!);
        }

        using (lease)
        {
            var started = time.GetTimestamp();
            var outcome = await connector.FetchAsync(context, item, cancellationToken);
            await lease.CompleteAsync(PreviewHosts.Response(outcome, time.GetElapsedTime(started)), cancellationToken);
            return Content(outcome, level, index, canonical.Value!, null);
        }
    }

    private async Task<PreviewContent> FetchUploadAsync(PreviewStore store, SourceContext source, Guid uploadId, int index, CancellationToken cancellationToken)
    {
        var slot = (await slots.ListAsync(source.SourceId, cancellationToken)).FirstOrDefault(s => s.Id == uploadId && s.DeletedAt is null)
            ?? throw new StageFatalException(Outcome.Failed, UploadUnknown);
        var item = new WorkItem(IdentityKind.Upload, slot.Identity, null, 0,
            new WorkHints(Upload: new UploadHint(slot.Id, slot.Sha256, slot.FileName, slot.DeclaredMediaType)));
        var connector = new UploadConnector(store.Blobs, scanner, slots, time);
        var outcome = await connector.FetchAsync(new FetchContext(source.SourceId, source.Classification, Guid.Empty, source.Settings, false), item, cancellationToken);
        return Content(outcome, source.Classification, index, null, slot.FileName);
    }

    /// <summary>The content of a fetch, or the fetch stage's failure for every other outcome.</summary>
    private static PreviewContent Content(FetchOutcome outcome, string level, int index, string? url, string? displayName) => outcome switch
    {
        FetchOutcome.Content content => new PreviewContent(index, level, Convert.FromHexString(content.Raw.ContentHash), content.Raw.Key)
        {
            MediaType = content.Raw.MediaType,
            Url = content.Facts.FinalUrl?.AbsoluteUri ?? url,
            DisplayName = displayName,
        },
        FetchOutcome.NotModified => throw new StageFatalException(Outcome.Failed, "http.304"),
        FetchOutcome.Gone gone => throw new StageFatalException(Outcome.Failed, gone.Reason),
        FetchOutcome.Skipped skipped => throw new StageFatalException(Outcome.Skipped, skipped.Reason),
        FetchOutcome.Refused refused => throw new StageFatalException(Outcome.Failed, refused.Reason),
        FetchOutcome.Quarantined quarantined => throw new StageFatalException(Outcome.Malware, quarantined.Reason),
        FetchOutcome.Transient transient => throw new StageFatalException(Outcome.Failed, Reason(transient.Reason)),
        FetchOutcome.Failed failed => throw new StageFatalException(Outcome.Failed, failed.Reason),
        _ => throw new InvalidOperationException($"Unexpected fetch outcome {outcome.GetType().Name}."),
    };

    /// <summary>An <c>&lt;area&gt;.&lt;code&gt;</c> reason; a bare code (<c>rate_limited</c>) gets the <c>http</c> area.</summary>
    private static string Reason(string reason) => reason.Contains('.', StringComparison.Ordinal) ? reason : "http." + reason;

    private static readonly JsonElement EmptySettings = JsonDocument.Parse("{}").RootElement.Clone();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256();

    [LoggerMessage(LogLevel.Information, "Preview {JobId}: {Documents} documents in {DurationMs} ms")]
    private static partial void LogPreviewed(ILogger logger, Guid jobId, int documents, double durationMs);
}
