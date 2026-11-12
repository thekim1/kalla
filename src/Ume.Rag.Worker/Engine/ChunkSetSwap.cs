using Npgsql;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Processing.Persistence;
using Ume.Rag.Worker.Processing.Pii;
using Outcome = Ume.Rag.Pipelines.Jobs.RunItemOutcome;

namespace Ume.Rag.Worker.Engine;

/// <summary>One document's swap: the set to put live and what the commit is fenced on.</summary>
/// <param name="Source">The document's source.</param>
/// <param name="Document">The document.</param>
/// <param name="ExecutionId">The execution the swap is fenced on (engine design §7.5).</param>
/// <param name="ContentHash">The content the set was built from.</param>
/// <param name="PipelineVersionId">The version the set was built with; it must still be the source's published one.</param>
/// <param name="RunId">The run the item is written to.</param>
/// <param name="Set">The set to swap in.</param>
/// <param name="Live">The document's current set, or null.</param>
/// <param name="SwappedBack">The set is a kept <c>superseded</c> one (rollback within 14 days), not a new write.</param>
/// <param name="Job">The leased job completed in the commit; null inside a <c>reprocess</c> iteration (nothing completes).</param>
/// <param name="Write">
/// A new set of at most <see cref="IndexWriter.BatchSize"/> chunks not written yet: the swap inserts it (engine design §14
/// rule 2a, #501). Null for a set already in the table (written invisible, or a kept one).
/// </param>
public sealed record SwapRequest(SourceContext Source, DocumentRow Document, Guid ExecutionId, byte[] ContentHash, Guid PipelineVersionId, Guid RunId,
    ChunkSetRecord Set, ChunkSetRow? Live, bool SwappedBack, LeasedJob? Job, SetWrite? Write = null)
{
    /// <summary>
    /// The <c>embed</c> job's model calls: their cost is the run item's <c>CostUsd</c>, and they go to <c>CollectionDailyUsage</c> as
    /// the swap's last statement (engine design §10.6); null for a swap without calls (a <c>reprocess</c> iteration).
    /// </summary>
    public JobUsage? Usage { get; init; }
}

/// <summary>The chunks of a set the swap inserts (rule 2a) and the profile they were embedded with (the profile guard).</summary>
public sealed record SetWrite(IReadOnlyList<ChunkRecord> Chunks, EmbeddingProfileRecord EmbeddedWith);

/// <summary>What <see cref="ChunkSetSwap.SwapAsync"/> did.</summary>
public enum SwapResult
{
    /// <summary>Committed: the set is <c>live</c> or <c>held</c>, the previous one <c>superseded</c>.</summary>
    Swapped,

    /// <summary>The execution was superseded; nothing written.</summary>
    Fenced,

    /// <summary>The set's version is no longer the source's published one; nothing written.</summary>
    Republished,

    /// <summary>The job's lease was lost; nothing written.</summary>
    LeaseLost,
}

/// <summary>
/// The per-document swap of engine design §14 rule 3, shared by the <c>embed</c> job (a new set, or a kept one) and a
/// <c>reprocess</c> run's iteration (a kept <c>superseded</c> set swapped back without a <c>parse</c> or <c>embed</c> job,
/// §2.3, §12.3). One short fenced transaction: the published-version check, for a set of at most 1,000 chunks its insert
/// (rule 2a: the profile guard, the set row and its chunks, written once), the swap (the set <c>live</c>, or <c>held</c>
/// when personal data blocks it in a <c>public</c> collection under the source's <c>piiMode</c>; a flagged set's first
/// <c>PiiFlaggedAt</c> at the swap time, #175), the document's site-profile version set to the set's,
/// the run item and the job's completion, then the job's model calls (<see cref="SwapRequest.Usage"/>). Live answers see the old
/// set until this commit and the new one after it.
/// </summary>
public sealed partial class ChunkSetSwap(
    EngineSettings settings,
    IChunkSetStore chunkSets,
    IndexWriter index,
    PiiLifecycle pii,
    IJobQueue queue,
    JobDatabase database,
    WorkerIdentity identity,
    ConfigVersionState config,
    TimeProvider time,
    ILogger<ChunkSetSwap> logger)
{
    /// <summary>The reason of the <c>updated</c> item of a set swapped back (engine design §12.3).</summary>
    public const string SwappedBackReason = "version.swapped_back";

    private const string PublishedSql = """SELECT "PublishedVersionId" FROM config."Pipelines" WHERE "Id" = $1""";

    private const string SiteProfileSql = """UPDATE content."Documents" SET "SiteProfileVersionId" = $2 WHERE "Id" = $1""";

    public async Task<SwapResult> SwapAsync(SwapRequest swap, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(swap);
        config.EnsureConfirmed();
        var (source, document, set) = (swap.Source, swap.Document, swap.Set);
        var values = await settings.GetAsync(cancellationToken);
        var clearance = await chunkSets.GetClearanceAsync(document.Id, cancellationToken);
        var mode = source.PiiMode;
        var state = PiiLifecycle.StateFor(set, source.CollectionClassification, mode, clearance, values.PiiFlagRules);
        await using var commit = await EngineCommit.BeginAsync(database, cancellationToken);
        if (!await commit.FenceAsync(document.Id, document.Classification, swap.ExecutionId, swap.ContentHash, cancellationToken))
        {
            LogFenced(logger, document.Id, set.Id);
            return SwapResult.Fenced;
        }

        if (!await IsPublishedAsync(commit, source.PipelineId, swap.PipelineVersionId, cancellationToken))
        {
            // A set written invisible stays writing for the orphan sweep (a rule 2a set was never written); the publish's
            // reprocess brings the document up to date.
            LogRepublished(logger, document.Id, swap.PipelineVersionId);
            return SwapResult.Republished;
        }

        var now = time.GetUtcNow();
        if (swap.Write is { } write)
        {
            // Rule 2a: nothing is written before this transaction, so a refusal leaves nothing behind.
            if (!await index.WriteAndSwapInAsync(commit.Connection, commit.Transaction, set, state, write.Chunks, write.EmbeddedWith, now, cancellationToken))
            {
                throw IndexWriter.ProfileChanged(); // disposing the commit rolls it back
            }
        }
        else
        {
            await IndexWriter.SwapInAsync(commit.Connection, commit.Transaction, document.Id, set.Id, state, swap.PipelineVersionId, now, cancellationToken);
        }

        if (PiiLifecycle.Flags(set, clearance, values.PiiFlagRules))
        {
            await IndexWriter.MarkPiiFlaggedAsync(commit.Connection, commit.Transaction, set.Id, now, cancellationToken);
        }

        await using (var profile = new NpgsqlCommand(SiteProfileSql, commit.Connection, commit.Transaction)
        {
            Parameters = { Db.P(document.Id), Db.P(set.Key.SiteProfileVersionId) },
        })
        {
            await profile.ExecuteNonQueryAsync(cancellationToken);
        }

        await commit.AddRunItemAsync(new RunItemRow(swap.RunId, document.Classification, document.Id, document.CanonicalIdentity,
            swap.Live is null ? Outcome.Added : Outcome.Updated)
        {
            Reason = PiiLifecycle.ReasonFor(set, state, source.CollectionClassification, clearance, values.PiiFlagRules)
                ?? (swap.SwappedBack ? SwappedBackReason : null),
            Strategy = document.ParseStrategy,
            PipelineVersionId = swap.PipelineVersionId,
            ExecutionId = swap.ExecutionId,
            CostUsd = swap.Usage?.PendingCostUsd,
            ContentHash = swap.ContentHash,
        }, cancellationToken);
        if (swap.Job is not null && !await commit.CompleteJobAsync(queue, swap.Job.Id, identity.InstanceId, null, cancellationToken))
        {
            return SwapResult.LeaseLost;
        }

        if (swap.Usage is { } usage)
        {
            await commit.AddUsageAsync(usage, cancellationToken);
        }

        await commit.CommitAsync(cancellationToken);
        if (swap.Write is not null)
        {
            index.Committed(swap.Write.EmbeddedWith.Id);
        }

        LogSwapped(logger, document.Id, set.Id, swap.SwappedBack ? "swapped-back" : "new", state);
        pii.RaiseIfFlagged(set, new PiiSwapContext(source.CollectionClassification, source.SourceId, swap.RunId, values.PiiFlagRules, mode), clearance);
        return SwapResult.Swapped;
    }

    /// <summary>True when <paramref name="versionId"/> is still the pipeline's published version (engine design §14 rule 3), in the commit.</summary>
    internal static async Task<bool> IsPublishedAsync(EngineCommit commit, Guid pipelineId, Guid versionId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(PublishedSql, commit.Connection, commit.Transaction) { Parameters = { Db.P(pipelineId) } };
        return await command.ExecuteScalarAsync(cancellationToken) is Guid published && published == versionId;
    }

    [LoggerMessage(LogLevel.Information, "Document {DocumentId}: set {ChunkSetId} {Kind}, now {State}")]
    private static partial void LogSwapped(ILogger logger, Guid documentId, Guid chunkSetId, string kind, string state);

    [LoggerMessage(LogLevel.Information, "Document {DocumentId}: the fence refused set {ChunkSetId}; it stays as it is")]
    private static partial void LogFenced(ILogger logger, Guid documentId, Guid chunkSetId);

    [LoggerMessage(LogLevel.Information, "Document {DocumentId}: pipeline version {PipelineVersionId} is no longer published; the set is not swapped")]
    private static partial void LogRepublished(ILogger logger, Guid documentId, Guid pipelineVersionId);
}
