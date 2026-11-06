using Microsoft.Extensions.Options;
using Npgsql;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Sources;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Processing.Persistence;
using Ume.Rag.Worker.Processing.Pii;

namespace Ume.Rag.Worker.Engine;

/// <summary>What one drift sweep did.</summary>
/// <param name="Checked">Documents read.</param>
/// <param name="Enqueued">Documents re-enqueued.</param>
/// <param name="SwappedBack">Documents whose kept set with the wanted key was swapped back, with no job.</param>
/// <param name="PendingSettled">Sets whose names were still unchecked and are now settled.</param>
/// <remarks><c>rag.reconcile.drift</c> counts <paramref name="Enqueued"/> and <paramref name="SwappedBack"/>; 0 when no work was lost.</remarks>
public sealed record DriftResult(long Checked, long Enqueued, long SwappedBack, long PendingSettled);

/// <summary>
/// The nightly drift sweep (engine design §2.3, §9.4, §16): the documents of active sources, in id order, 1,000 per
/// transaction under an advisory lock, the cursor carried from batch to batch. <c>EnsureCurrent</c>
/// (<see cref="Reconciler"/>, as a <c>reprocess</c> run decides it) runs for a document whose live set is not the wanted
/// one (pipeline version, site-profile version, embedding profile or content hash differ) and for a <b>stalled</b> one: no
/// live set and no queued or running <c>parse</c> or <c>embed</c> job. A kept <c>superseded</c> set with the wanted key is
/// swapped back directly (<see cref="DocumentIngest.SwapBackAsync"/>, its own short transaction); else a <c>parse</c> job
/// is enqueued. The jobs are deduplicated by their <c>DedupKey</c>, so a sweep while jobs are in flight, or a second sweep,
/// enqueues nothing twice. The jobs and swaps of a batch belong to one <c>reprocess</c> run per source (trigger
/// <c>schedule</c>), opened in the batch's transaction with its first job or before its first swap, whose items carry no
/// foreign key to the run, so the finalizer sees the run only with its jobs and items. Skipped: documents of a source with a
/// running <c>sync</c> run (that run reconciles them), and documents whose last processing ended at a stage
/// (<c>skipped</c>, <c>unsupported</c>, <c>failed</c>, …: settled until their next fetch). Then the sets whose name check is
/// still <c>pending</c> get <c>personal-data</c> again in place (<see cref="PiiLifecycle.SweepPendingAsync"/>, processing
/// design §9.6), their state settled by the source's <c>piiMode</c>.
/// </summary>
public sealed partial class DriftSweep(
    JobDatabase database,
    SourceContexts sources,
    DocumentStore documents,
    Reconciler reconciler,
    DocumentIngest ingest,
    PiiLifecycle pii,
    PersonalDataStage personalData,
    EngineSettings settings,
    JobMetrics metrics,
    ILogger<DriftSweep> logger)
{
    public const int BatchSize = 1000;

    private const long LockKey = 0x5241_4731_0004;

    private const string BatchSql = $"""
        WITH batch AS (
            SELECT {DocumentStore.Columns}, "ParseOutcome" FROM content."Documents"
            WHERE "Id" > $1 AND "DeletedAt" IS NULL AND "ContentHash" IS NOT NULL AND "CurrentExecutionId" IS NOT NULL
            ORDER BY "Id" LIMIT $2)
        SELECT b.*, s."Id", s."PipelineVersionId", s."SiteProfileVersionId", s."EmbeddingProfileId", s."ContentHash", s."State", s."InputHash",
               s."ChunkCount",
               EXISTS (SELECT 1 FROM content."Runs" r WHERE r."SourceId" = b."SourceId" AND r."Kind" = 'sync' AND r."State" = 'running'),
               EXISTS (SELECT 1 FROM content."Jobs" j
                        WHERE j."State" IN ('queued', 'running') AND j."Class" IN ('parse', 'embed') AND j."Payload" ->> 'documentId' = b."Id"::text),
               s."ParseRuleKey"
        FROM batch b LEFT JOIN content."ChunkSets" s ON s."Id" = b."LiveChunkSetId"
        ORDER BY b."Id"
        """;

    // As RunStore.OpenAsync, but in the batch's transaction, so the finalizer never sees the run without its jobs.
    private const string OpenRunSql = """
        INSERT INTO content."Runs" ("Id", "SourceId", "Classification", "Kind", "Trigger", "PipelineVersionId", "Phase", "State",
                                   "Added", "Updated", "Unchanged", "Deleted", "Failed", "ProposedDeletions", "CostUsd")
        VALUES ($1, $2, $3, $4, $5, $6, 'list', 'running', 0, 0, 0, 0, 0, 0, 0)
        """;

    /// <summary>Parse outcomes that leave a document to the sweep; any other ended at a stage.</summary>
    private static readonly HashSet<string?> SweptOutcomes = new(StringComparer.Ordinal) { null, "pending", "success" };

    public async Task<DriftResult> RunAsync(CancellationToken cancellationToken)
    {
        var contexts = new Dictionary<Guid, (SourceContext Source, PipelineRun Pipeline)?>();
        long checkedCount = 0, enqueued = 0, swappedBack = 0;
        var cursor = Guid.Empty;
        var locked = true;
        while (locked)
        {
            var batch = await BatchAsync(cursor, contexts, cancellationToken);
            locked = batch is not null;
            if (batch is not { } done)
            {
                break;
            }

            checkedCount += done.Read;
            enqueued += done.Enqueued;
            swappedBack += done.SwappedBack;
            if (done.Read < BatchSize)
            {
                break;
            }

            cursor = done.Cursor;
        }

        var settled = locked ? await SettlePendingAsync(contexts, cancellationToken) : 0;
        metrics.Drift(enqueued + swappedBack);
        LogSwept(logger, checkedCount, enqueued, swappedBack, settled);
        return new DriftResult(checkedCount, enqueued, swappedBack, settled);
    }

    /// <summary>One batch in its own transaction; null when another sweep holds the lock.</summary>
    private async Task<Batch?> BatchAsync(Guid cursor, Dictionary<Guid, (SourceContext Source, PipelineRun Pipeline)?> contexts,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await LockAsync(connection, transaction, cancellationToken))
        {
            LogLocked(logger);
            return null;
        }

        var rows = new List<Row>();
        await using (var command = new NpgsqlCommand(BatchSql, connection, transaction) { Parameters = { Db.P(cursor), Db.P(BatchSize) } })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var document = DocumentStore.Read(reader);
                ChunkSetRow? live = reader.IsDBNull(17)
                    ? null
                    : new ChunkSetRow(reader.GetGuid(17),
                        new ChunkSetKey(document.Id, reader.GetGuid(18), Db.Nullable<Guid>(reader, 19),
                            reader.GetGuid(20), reader.GetFieldValue<byte[]>(21), Db.String(reader, 27)),
                        reader.GetString(22), reader.GetFieldValue<byte[]>(23), reader.GetInt32(24));
                rows.Add(new Row(document, Db.String(reader, 16), live, reader.GetBoolean(25), reader.GetBoolean(26)));
            }
        }

        var runs = new Dictionary<Guid, Guid>();
        int enqueued = 0, swappedBack = 0;
        foreach (var row in rows)
        {
            if (row.Syncing || !SweptOutcomes.Contains(row.ParseOutcome) ||
                await ContextAsync(row.Document.SourceId, contexts, cancellationToken) is not { } context)
            {
                continue;
            }

            var (source, pipeline) = context;
            var stalled = row.Live is null;
            if (stalled && row.OpenJob)
            {
                continue;
            }

            var reconciliation = await reconciler.ReconcileAsync(source, row.Document, pipeline.VersionId, row.Live, cancellationToken);
            if (reconciliation.Action == ReconcileAction.Current)
            {
                continue;
            }

            var runId = runs.TryGetValue(source.SourceId, out var open) ? open : Guid.CreateVersion7();
            if (reconciliation.Action == ReconcileAction.SwapBack)
            {
                await OpenRunAsync(connection, transaction, runs, source, pipeline.VersionId, runId, cancellationToken);
                if (await ingest.SwapBackAsync(source, runId, pipeline.VersionId, row.Document, reconciliation, cancellationToken) == SwapResult.Swapped)
                {
                    swappedBack++;
                    LogSwappedBack(logger, row.Document.Id, reconciliation.Kept!.Id, runId);
                }

                continue;
            }

            if (!await reconciler.EnqueueAsync(transaction, source, pipeline, row.Document, runId, cancellationToken))
            {
                continue;
            }

            await OpenRunAsync(connection, transaction, runs, source, pipeline.VersionId, runId, cancellationToken);
            enqueued++;
            LogEnqueued(logger, row.Document.Id, stalled ? "stalled" : "drift", runId);
        }

        await transaction.CommitAsync(cancellationToken);
        return new Batch(rows.Count, enqueued, swappedBack, rows.Count > 0 ? rows[^1].Document.Id : cursor);
    }

    /// <summary>Opens the source's <c>reprocess</c> run <paramref name="runId"/> in the batch's transaction, once per batch and source.</summary>
    private static async Task OpenRunAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Dictionary<Guid, Guid> runs, SourceContext source,
        Guid pipelineVersionId, Guid runId, CancellationToken cancellationToken)
    {
        if (!runs.TryAdd(source.SourceId, runId))
        {
            return;
        }

        await using var openRun = new NpgsqlCommand(OpenRunSql, connection, transaction)
        {
            Parameters =
            {
                Db.P(runId), Db.P(source.SourceId), Db.P(source.Classification), Db.P(RunKinds.Reprocess), Db.P(RunTriggers.Schedule),
                Db.P(pipelineVersionId),
            },
        };
        await openRun.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>The <c>pending</c> name checks (processing design §9.6), under the same lock.</summary>
    private async Task<long> SettlePendingAsync(Dictionary<Guid, (SourceContext Source, PipelineRun Pipeline)?> contexts, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await LockAsync(connection, transaction, cancellationToken))
        {
            return 0;
        }

        var values = await settings.GetAsync(cancellationToken);
        var settled = await pii.SweepPendingAsync(personalData, async (set, ct) =>
        {
            var document = await documents.GetAsync(set.Key.DocumentId, ct);
            var source = document is null ? null : (await ContextAsync(document.SourceId, contexts, ct))?.Source ?? await sources.GetAsync(document.SourceId, ct);
            // Without its source the set's own level decides, in hold mode: fail closed at least as strictly.
            var level = source?.CollectionClassification ?? set.Classification;
            return (values.PersonalData(level),
                new PiiSwapContext(level, source?.SourceId ?? Guid.Empty, null, values.PiiFlagRules, source?.PiiMode ?? PiiMode.Hold));
        }, BatchSize, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return settled;
    }

    /// <summary>The active source and its published, runnable version; null (cached) when the source has none.</summary>
    private async Task<(SourceContext Source, PipelineRun Pipeline)?> ContextAsync(Guid sourceId,
        Dictionary<Guid, (SourceContext Source, PipelineRun Pipeline)?> contexts, CancellationToken cancellationToken)
    {
        if (contexts.TryGetValue(sourceId, out var cached))
        {
            return cached;
        }

        (SourceContext, PipelineRun)? context = null;
        if (await sources.GetAsync(sourceId, cancellationToken) is { Active: true, PublishedVersionId: { } versionId } source)
        {
            try
            {
                context = (source, await sources.VersionAsync(versionId, source.Type, cancellationToken));
            }
            catch (PipelineRefusedException refused)
            {
                LogRefused(logger, sourceId, refused.Reason);
            }
        }

        contexts[sourceId] = context;
        return context;
    }

    private static async Task<bool> LockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock($1)", connection, transaction) { Parameters = { Db.P(LockKey) } };
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private sealed record Row(DocumentRow Document, string? ParseOutcome, ChunkSetRow? Live, bool Syncing, bool OpenJob);

    private sealed record Batch(int Read, int Enqueued, int SwappedBack, Guid Cursor);

    [LoggerMessage(LogLevel.Information, "Drift sweep: document {DocumentId} re-enqueued ({Kind}) in run {RunId}")]
    private static partial void LogEnqueued(ILogger logger, Guid documentId, string kind, Guid runId);

    [LoggerMessage(LogLevel.Information, "Drift sweep: document {DocumentId} swapped back to kept set {ChunkSetId} in run {RunId}")]
    private static partial void LogSwappedBack(ILogger logger, Guid documentId, Guid chunkSetId, Guid runId);

    [LoggerMessage(LogLevel.Information,
        "Drift sweep: {DocumentsChecked} documents checked, {Enqueued} re-enqueued, {SwappedBack} swapped back, {Settled} pending name checks settled")]
    private static partial void LogSwept(ILogger logger, long documentsChecked, long enqueued, long swappedBack, long settled);

    [LoggerMessage(LogLevel.Information, "Drift sweep: another sweep holds the lock; this one stops")]
    private static partial void LogLocked(ILogger logger);

    [LoggerMessage(LogLevel.Warning, "Drift sweep: source {SourceId} is skipped, its published version is refused ({Reason})")]
    private static partial void LogRefused(ILogger logger, Guid sourceId, string reason);
}

/// <summary>Runs the <see cref="DriftSweep"/> at 01:30 Europe/Stockholm on the instance with <c>Worker:Maintenance=true</c> (engine design §2.3, §9.4).</summary>
public sealed partial class DriftSweepService(DriftSweep sweep, IOptions<WorkerOptions> options, TimeProvider time, ILogger<DriftSweepService> logger)
    : BackgroundService
{
    public static readonly TimeOnly RunAt = new(1, 30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Maintenance)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            await Task.Delay(MaintenanceService.NextRunAt(now, RunAt) - now, time, stoppingToken);
            try
            {
                await sweep.RunAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(LogLevel.Error, "The drift sweep failed; it runs again tomorrow")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
