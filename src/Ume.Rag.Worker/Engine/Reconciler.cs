using System.Data.Common;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;

namespace Ume.Rag.Worker.Engine;

/// <summary>
/// The chunk set a document should have live (engine design §2.3): the source's published pipeline version, the applied
/// site-profile version of its host (URL documents, as the fetch path applies it), the collection's embedding profile and
/// the document's content hash.
/// </summary>
public sealed record WantedSet(Guid PipelineVersionId, Guid? SiteProfileVersionId, Guid EmbeddingProfileId, byte[] ContentHash)
{
    /// <summary>True when <paramref name="live"/> is that set, <c>live</c> or <c>held</c>.</summary>
    public bool IsMetBy(ChunkSetRow? live) =>
        live is { State: ChunkSetStates.Live or ChunkSetStates.Held } &&
        live.Key.PipelineVersionId == PipelineVersionId &&
        live.Key.SiteProfileVersionId == SiteProfileVersionId &&
        live.Key.EmbeddingProfileId == EmbeddingProfileId &&
        live.Key.ContentHash.AsSpan().SequenceEqual(ContentHash);
}

/// <summary>
/// <c>EnsureCurrent(D)</c> outside a fetch (engine design §2.3): reads, and when the live set is not the wanted one enqueues
/// a <c>parse</c> job from the document's <c>detect</c> key in the caller's transaction, deduplicated by
/// <c>parse:{doc}:{exec}</c>. The <c>parse</c> job runs every route (HTML, text, CSV and parser-bound) from the caches, so
/// only the stages whose key has no output do work, and its <c>embed</c> continuation swaps back, re-keys or writes the
/// set (§14). The fetch path keeps its own inline check (<see cref="DocumentIngest"/>), which reads the same columns.
/// </summary>
public sealed class Reconciler(SiteProfiles siteProfiles, IJobQueue queue)
{
    /// <summary>The wanted set of <paramref name="document"/> under <paramref name="pipelineVersionId"/>.</summary>
    public async Task<WantedSet> WantedAsync(SourceContext source, DocumentRow document, Guid pipelineVersionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(document);
        var profile = document.IdentityKind == IdentityKinds.Url
            ? await siteProfiles.AppliedAsync(document.Classification, document.Url, cancellationToken)
            : null;
        return new WantedSet(pipelineVersionId, profile?.Id, source.EmbeddingProfileId,
            document.ContentHash ?? throw new InvalidOperationException("The document has no content yet."));
    }

    /// <summary>
    /// Enqueues the document's <c>parse</c> job for run <paramref name="runId"/> in <paramref name="transaction"/>. Returns
    /// false when a queued or running job already holds its dedup key (nothing enqueued twice).
    /// </summary>
    public async Task<bool> EnqueueAsync(DbTransaction transaction, SourceContext source, PipelineRun pipeline, DocumentRow document, Guid runId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(document);
        var executionId = document.CurrentExecutionId ?? throw new InvalidOperationException("The document has no execution id.");
        var contentHash = document.ContentHash ?? throw new InvalidOperationException("The document has no content yet.");
        var job = SyncJobs.Parse(source, document, executionId, pipeline.VersionId, runId, ProcessingSegment.DetectKey(pipeline.Pipeline, contentHash));
        return (await queue.EnqueueAsync([job], transaction, cancellationToken)).Contains(job.Id);
    }

    /// <summary><c>EnsureCurrent</c>: nothing when <paramref name="live"/> is the wanted set, else <see cref="EnqueueAsync"/>.</summary>
    public async Task<bool> EnsureCurrentAsync(DbTransaction transaction, SourceContext source, PipelineRun pipeline, DocumentRow document, ChunkSetRow? live,
        Guid runId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var wanted = await WantedAsync(source, document, pipeline.VersionId, cancellationToken);
        return !wanted.IsMetBy(live) && await EnqueueAsync(transaction, source, pipeline, document, runId, cancellationToken);
    }
}
