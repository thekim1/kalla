using System.Text.Json;
using Npgsql;
using RunStates = Kalla.Domain.Entities.RunStates;
using Kalla.Domain.Ports;
using Kalla.Infrastructure.Jobs;
using Kalla.Pipelines.Jobs;
using Kalla.Worker.Connectors.Deletion;
using Kalla.Worker.Connectors.Persistence;
using Kalla.Worker.Engine;
using Kalla.Worker.Jobs;
using Outcome = Kalla.Pipelines.Jobs.RunItemOutcome;

namespace Kalla.Worker.Commands;

/// <summary>A run waiting for a deletion decision and its proposals, read under row locks (connectors design §13.3).</summary>
internal sealed record WaitingDeletion(Guid RunId, string Classification, DateTimeOffset ProposedAt, IReadOnlyList<DeletionProposal> Proposals,
    IReadOnlyDictionary<Guid, SourceDocument> Documents);

/// <summary>The rows <c>confirm-deletion</c> and <c>reject-deletion</c> read and write, all in the command's transaction.</summary>
internal static class DeletionRows
{
    public const string RunUnknown = "run.unknown";
    public const string NotAwaiting = "deletion.not_awaiting";

    // The reconcile ran before the finalizer set the state, so FinishedAt is the latest the proposals can be from.
    private const string RunSql = """
        SELECT "Classification", "State", coalesce("FinishedAt", "StartedAt") FROM content."Runs" WHERE "Id" = $1 FOR UPDATE
        """;

    private const string ProposalsSql = """
        SELECT p."DocumentId", p."SourceId", p."Classification", p."Reason", p."Exempt", p."Decision", p."DecidedBy", p."DecidedAt",
               d."CanonicalIdentity", d."LastSeenAt", d."DeletedAt"
        FROM content."DeletionProposals" p LEFT JOIN content."Documents" d ON d."Id" = p."DocumentId"
        WHERE p."RunId" = $1
        ORDER BY p."DocumentId"
        FOR UPDATE OF p
        """;

    private const string DecideSql = """
        UPDATE content."DeletionProposals" p SET "Decision" = u.decision, "DecidedBy" = u.by, "DecidedAt" = u.at
          FROM unnest($2::uuid[], $3::varchar[], $4::varchar[], $5::timestamptz[]) AS u(doc, decision, by, at)
         WHERE p."RunId" = $1 AND p."DocumentId" = u.doc
        """;

    /// <summary>The run locked with its proposals, or the refusal: unknown run, or a run not waiting for a decision.</summary>
    public static async Task<(WaitingDeletion? Waiting, string? Refusal)> LockAsync(EngineCommit commit, Guid runId, CancellationToken cancellationToken)
    {
        string classification;
        DateTimeOffset proposedAt;
        await using (var run = new NpgsqlCommand(RunSql, commit.Connection, commit.Transaction) { Parameters = { Db.P(runId) } })
        await using (var reader = await run.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return (null, RunUnknown);
            }

            if (reader.GetString(1) != RunStates.AwaitingDeletionConfirmation)
            {
                return (null, NotAwaiting);
            }

            classification = reader.GetString(0);
            proposedAt = reader.GetFieldValue<DateTimeOffset>(2);
        }

        var proposals = new List<DeletionProposal>();
        var documents = new Dictionary<Guid, SourceDocument>();
        await using (var command = new NpgsqlCommand(ProposalsSql, commit.Connection, commit.Transaction) { Parameters = { Db.P(runId) } })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var documentId = reader.GetGuid(0);
                proposals.Add(new DeletionProposal
                {
                    RunId = runId,
                    DocumentId = documentId,
                    SourceId = reader.GetGuid(1),
                    Classification = reader.GetString(2),
                    Reason = reader.GetString(3),
                    Exempt = reader.GetBoolean(4),
                    Decision = Db.String(reader, 5),
                    DecidedBy = Db.String(reader, 6),
                    DecidedAt = Db.Nullable<DateTimeOffset>(reader, 7),
                });
                if (Db.String(reader, 8) is { } identity)
                {
                    documents[documentId] = new SourceDocument
                    {
                        Id = documentId,
                        CanonicalIdentity = identity,
                        LastSeenAt = reader.GetFieldValue<DateTimeOffset>(9),
                        DeletedAt = Db.Nullable<DateTimeOffset>(reader, 10),
                    };
                }
            }
        }

        return (new WaitingDeletion(runId, classification, proposedAt, proposals, documents), null);
    }

    /// <summary>Writes the decisions of the proposals that <paramref name="decided"/> changed.</summary>
    public static Task DecideAsync(EngineCommit commit, WaitingDeletion waiting, IReadOnlyList<DeletionProposal> decided, CancellationToken cancellationToken)
    {
        var before = waiting.Proposals.ToDictionary(p => p.DocumentId);
        var changed = decided.Where(p => before[p.DocumentId] != p).ToList();
        return changed.Count == 0
            ? Task.CompletedTask
            : CommandCommit.ExecuteAsync(commit, DecideSql, cancellationToken, Db.P(waiting.RunId), Db.P(changed.Select(p => p.DocumentId).ToArray()),
                Db.P(changed.Select(p => p.Decision).ToArray()), Db.P(changed.Select(p => p.DecidedBy).ToArray()),
                Db.P(changed.Select(p => Db.Utc(p.DecidedAt)).ToArray()));
    }
}

/// <summary>
/// <c>confirm-deletion</c> (connectors design §13.3): the waiting proposals of a run in
/// <c>awaiting-deletion-confirmation</c> are re-validated (a document seen again after the reconcile, or deleted already, is
/// dropped) and the rest soft-deleted (connectors design §13.4: <c>DeletedAt</c>, <c>PurgeAfter</c> after the grace, the live
/// set's chunks deleted) with a <c>deleted</c> run item each. The run goes back to <c>running</c> in phase <c>done</c>, so the
/// <see cref="RunFinalizer"/> recomputes its counters and state from the run items. Everything commits with the job. Result
/// <see cref="DeletionCommandResult"/>. The admin's audit event 6001 is the admin's.
/// </summary>
public sealed class ConfirmDeletionHandler(JobDatabase database, IJobQueue queue, WorkerIdentity identity, TimeProvider time) : ICommandHandler
{
    // A document seen by a fetch after the decision was read is not deleted (the LastSeenAt re-check under the row lock).
    private const string SoftDeleteSql = """
        UPDATE content."Documents" SET "DeletedAt" = $2, "PurgeAfter" = $3
         WHERE "Id" = ANY($1) AND "DeletedAt" IS NULL AND "LastSeenAt" <= $4
        RETURNING "Id", "CanonicalIdentity", "LiveChunkSetId"
        """;

    private const string SoftDeleteChunksSql = """
        UPDATE content."Chunks" SET "DeletedAt" = $2 WHERE "ChunkSetId" = ANY($1) AND "DeletedAt" IS NULL
        """;

    private const string ReopenSql = """
        UPDATE content."Runs" SET "State" = 'running', "Phase" = 'done', "FinishedAt" = NULL WHERE "Id" = $1
        """;

    public string Kind => CommandKinds.ConfirmDeletion;

    public async Task<JobResult> ExecuteAsync(JobContext context, JsonElement payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (CommandPayload.Read<ConfirmDeletionCommand>(payload) is not { } confirm)
        {
            return new JobResult.FatalError(CommandJobHandler.InvalidPayload);
        }

        var now = time.GetUtcNow();
        await using var commit = await EngineCommit.BeginAsync(database, cancellationToken);
        var (waiting, refusal) = await DeletionRows.LockAsync(commit, confirm.RunId, cancellationToken);
        if (waiting is null)
        {
            return new JobResult.FatalError(refusal!);
        }

        var outcome = DeletionProposalLifecycle.Confirm(waiting.Proposals, waiting.Documents, waiting.ProposedAt, confirm.DecidedBy, now);
        await DeletionRows.DecideAsync(commit, waiting, outcome.Proposals, cancellationToken);
        var at = now.ToUniversalTime();
        var deleted = new List<(Guid Id, string Identity)>();
        var sets = new List<Guid>();
        await using (var softDelete = new NpgsqlCommand(SoftDeleteSql, commit.Connection, commit.Transaction)
        {
            Parameters =
            {
                Db.P(outcome.Delete.ToArray()), Db.P(at), Db.P((now + DocumentLifecycle.Grace).ToUniversalTime()), Db.P(waiting.ProposedAt.ToUniversalTime()),
            },
        })
        await using (var reader = await softDelete.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                deleted.Add((reader.GetGuid(0), reader.GetString(1)));
                if (Db.Nullable<Guid>(reader, 2) is { } set)
                {
                    sets.Add(set);
                }
            }
        }

        await CommandCommit.ExecuteAsync(commit, SoftDeleteChunksSql, cancellationToken, Db.P(sets.ToArray()), Db.P(at));
        var reasons = waiting.Proposals.ToDictionary(p => p.DocumentId, p => p.Reason);
        foreach (var (documentId, canonicalIdentity) in deleted)
        {
            await commit.AddRunItemAsync(new RunItemRow(waiting.RunId, waiting.Classification, documentId, canonicalIdentity, Outcome.Deleted)
            {
                Reason = reasons[documentId],
            }, cancellationToken);
        }

        await CommandCommit.ExecuteAsync(commit, ReopenSql, cancellationToken, Db.P(waiting.RunId));
        var pending = waiting.Proposals.Where(p => p.Decision == ProposalDecisions.Pending).Select(p => p.DocumentId).ToHashSet();
        var dropped = outcome.Proposals.Count(p => p.Decision == ProposalDecisions.Dropped && pending.Contains(p.DocumentId));
        return await CommandCommit.CompleteAsync(commit, queue, context.Job, identity,
            new DeletionCommandResult { Deleted = deleted.Count, Dropped = dropped }, cancellationToken);
    }
}

/// <summary>
/// <c>reject-deletion</c> (connectors design §13.3): nothing is deleted; the waiting proposals are rejected, their documents
/// held (<c>Documents.HeldAt</c>, <c>HeldBy</c>: excluded from later guards until seen again or released by
/// <see cref="ReleaseHoldHandler"/>), and the run ends <c>degraded</c>, all with the job. Result
/// <see cref="DeletionCommandResult"/>.
/// </summary>
public sealed class RejectDeletionHandler(JobDatabase database, IJobQueue queue, WorkerIdentity identity, TimeProvider time) : ICommandHandler
{
    private const string HoldSql = """
        UPDATE content."Documents" SET "HeldAt" = $2, "HeldBy" = $3 WHERE "Id" = ANY($1) AND "DeletedAt" IS NULL
        """;

    private const string DegradeSql = """UPDATE content."Runs" SET "State" = 'degraded' WHERE "Id" = $1""";

    public string Kind => CommandKinds.RejectDeletion;

    public async Task<JobResult> ExecuteAsync(JobContext context, JsonElement payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (CommandPayload.Read<RejectDeletionCommand>(payload) is not { } reject)
        {
            return new JobResult.FatalError(CommandJobHandler.InvalidPayload);
        }

        var now = time.GetUtcNow();
        await using var commit = await EngineCommit.BeginAsync(database, cancellationToken);
        var (waiting, refusal) = await DeletionRows.LockAsync(commit, reject.RunId, cancellationToken);
        if (waiting is null)
        {
            return new JobResult.FatalError(refusal!);
        }

        var outcome = DeletionProposalLifecycle.Reject(waiting.Proposals, reject.DecidedBy, now);
        await DeletionRows.DecideAsync(commit, waiting, outcome.Proposals, cancellationToken);
        var held = await CommandCommit.ExecuteAsync(commit, HoldSql, cancellationToken, Db.P(outcome.Holds.ToArray()), Db.P(now.ToUniversalTime()),
            Db.P(reject.DecidedBy));
        await CommandCommit.ExecuteAsync(commit, DegradeSql, cancellationToken, Db.P(waiting.RunId));
        return await CommandCommit.CompleteAsync(commit, queue, context.Job, identity, new DeletionCommandResult { Held = held }, cancellationToken);
    }
}

/// <summary>
/// <c>release-hold</c> (connectors design §13.3, admin design §4.7): the holds a rejected deletion put on the source's
/// documents are cleared, with the job; the documents become deletion candidates again at the next reconcile. A source
/// without holds releases none. Result <see cref="ReleaseHoldResult"/>.
/// </summary>
public sealed class ReleaseHoldHandler(JobDatabase database, IJobQueue queue, WorkerIdentity identity) : ICommandHandler
{
    public const string SourceUnknown = "source.unknown";

    private const string SourceSql = """SELECT 1 FROM config."Sources" WHERE "Id" = $1""";

    private const string ReleaseSql = """
        UPDATE content."Documents" SET "HeldAt" = NULL, "HeldBy" = NULL WHERE "SourceId" = $1 AND "HeldAt" IS NOT NULL
        """;

    public string Kind => CommandKinds.ReleaseHold;

    public async Task<JobResult> ExecuteAsync(JobContext context, JsonElement payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (CommandPayload.Read<ReleaseHoldCommand>(payload) is not { } release)
        {
            return new JobResult.FatalError(CommandJobHandler.InvalidPayload);
        }

        await using var commit = await EngineCommit.BeginAsync(database, cancellationToken);
        if (await CommandCommit.ScalarAsync(commit, SourceSql, cancellationToken, Db.P(release.SourceId)) is null)
        {
            return new JobResult.FatalError(SourceUnknown);
        }

        var released = await CommandCommit.ExecuteAsync(commit, ReleaseSql, cancellationToken, Db.P(release.SourceId));
        return await CommandCommit.CompleteAsync(commit, queue, context.Job, identity, new ReleaseHoldResult { Released = released }, cancellationToken);
    }
}
