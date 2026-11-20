using System.Data.Common;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Worker.Processing.Detect;
using Kalla.Worker.Processing.Html;
using Kalla.Worker.Processing.Parse;
using Kalla.Worker.Processing.Persistence;

namespace Kalla.Worker.Engine;

/// <summary>What bringing a document to its wanted chunk set takes (engine design §2.3).</summary>
public enum ReconcileAction
{
    /// <summary>The live set (<c>live</c> or <c>held</c>) has the wanted key: nothing to do.</summary>
    Current,

    /// <summary>A kept <c>superseded</c> set has the wanted key (rollback within 14 days, §12.3, §14 rule 1): swapped back, no <c>parse</c> or <c>embed</c>.</summary>
    SwapBack,

    /// <summary>Neither: the stages run again from the first one without cached output.</summary>
    Reprocess,
}

/// <summary>
/// A document's wanted chunk set against its live one (engine design §2.3).
/// </summary>
/// <param name="Action">What reaching <paramref name="Wanted"/> takes.</param>
/// <param name="Wanted">The key the live set should have.</param>
/// <param name="SiteProfile">The applied site profile of the document's host (URL documents), which the segment applies too.</param>
/// <param name="Live">The document's live set, or null.</param>
/// <param name="Kept">The kept set with the wanted key when <paramref name="Action"/> is <see cref="ReconcileAction.SwapBack"/>.</param>
public sealed record Reconciliation(ReconcileAction Action, ChunkSetKey Wanted, (Guid Id, HtmlProfileVersion Profile)? SiteProfile, ChunkSetRow? Live,
    ChunkSetRecord? Kept);

/// <summary>
/// <c>EnsureCurrent(D)</c>'s decision (engine design §2.3), the one place that computes a document's wanted set: the
/// source's published pipeline version, the applied site-profile version of its host (URL documents, as the fetch path
/// applies it), the collection's embedding profile, the document's content hash and, for a parser-bound document, the
/// rule part of the salt (<see cref="ChunkSetSalt"/>): the admin parse rules in its scope, as the parse stage takes them
/// (<see cref="ParseRuleCatalog"/>), so a rule change makes the document's set stale. The live set that meets that key
/// (<see cref="ChunkSetKey.IsMetBy"/>; a forced set does too) is current; else a kept <c>superseded</c> set with it is
/// swapped back (<see cref="DocumentIngest.SwapBackAsync"/>); else the document is re-processed. The fetch path, a
/// <c>reprocess</c> run (<see cref="DocumentIngest"/>) and the drift sweep (<see cref="DriftSweep"/>) all decide here.
/// Outside a fetch, re-processing is a <c>parse</c> job from the document's <c>detect</c> key (<see cref="EnqueueAsync"/>)
/// or the segment run inline.
/// </summary>
public sealed class Reconciler(SiteProfiles siteProfiles, ParseRuleCatalog parseRules, IndexWriter index, IChunkSetStore chunkSets, IJobQueue queue)
{
    /// <summary>The decision for <paramref name="document"/> under <paramref name="pipelineVersionId"/>, its live set read here.</summary>
    public async Task<Reconciliation> ReconcileAsync(SourceContext source, DocumentRow document, Guid pipelineVersionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        return await ReconcileAsync(source, document, pipelineVersionId, await index.LiveAsync(document.Id, cancellationToken), cancellationToken);
    }

    /// <summary>The decision for <paramref name="document"/> under <paramref name="pipelineVersionId"/> against <paramref name="live"/>.</summary>
    public async Task<Reconciliation> ReconcileAsync(SourceContext source, DocumentRow document, Guid pipelineVersionId, ChunkSetRow? live,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(document);
        var profile = document.IdentityKind == IdentityKinds.Url
            ? await siteProfiles.AppliedAsync(document.Classification, document.Url, cancellationToken)
            : null;
        var rules = document.MediaType is { } mediaType && ProcessingMediaTypes.Route(mediaType) == ParseRoute.Parser
            ? await parseRules.CandidatesAsync(document.Classification, document.SourceId, document.Url, mediaType, cancellationToken)
            : [];
        var wanted = new ChunkSetKey(document.Id, pipelineVersionId, profile?.Id, source.EmbeddingProfileId,
            document.ContentHash ?? throw new InvalidOperationException("The document has no content yet."), ChunkSetSalt.Rule(AdminParseRuleSet.Key(rules)));
        if (live is { State: ChunkSetStates.Live or ChunkSetStates.Held } && wanted.IsMetBy(live.Key))
        {
            return new Reconciliation(ReconcileAction.Current, wanted, profile, live, null);
        }

        var kept = await index.FindAsync(wanted, cancellationToken) is { State: ChunkSetStates.Superseded } found
            ? await chunkSets.GetAsync(found.Id, cancellationToken)
            : null;
        return new Reconciliation(kept is null ? ReconcileAction.Reprocess : ReconcileAction.SwapBack, wanted, profile, live, kept);
    }

    /// <summary>
    /// Enqueues the document's <c>parse</c> job for run <paramref name="runId"/> in <paramref name="transaction"/>, deduplicated
    /// by <c>parse:{doc}:{exec}</c>. The job runs every route (HTML, text, CSV and parser-bound) from the caches, so only the
    /// stages whose key has no output do work, and its <c>embed</c> continuation re-keys or writes the set (§14). Returns
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
}
