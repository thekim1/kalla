using System.Text.Json;
using Npgsql;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Pipelines.Processing;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Processing.Persistence;

namespace Ume.Rag.Worker.Commands;

/// <summary>
/// <c>clear-pii-flag</c> (processing design §9.5; engine design §14): a person's decision on the document. With the job, the
/// clearance is recorded (<c>Documents.PiiClearedAt</c>, <c>PiiClearedBy</c>, <c>PiiClearedCounts</c> = the current set's
/// <c>PiiCounts</c>) and a <c>held</c> current set is released to <c>live</c> with its chunks' <c>DeletedAt</c> cleared,
/// without re-processing; flag and counts stay on the set. Without a held set the clearance is still recorded and covers
/// later sets. The reason and audit event 6001 <c>pii.clear</c> are the admin's. Result <see cref="ClearPiiFlagResult"/>.
/// </summary>
public sealed class ClearPiiFlagHandler(JobDatabase database, IJobQueue queue, WorkerIdentity identity, TimeProvider time) : ICommandHandler
{
    public const string DocumentUnknown = "document.unknown";
    public const string DocumentDeleted = "document.deleted";

    private const string DocumentSql = """
        SELECT d."DeletedAt" IS NOT NULL, s."Id", s."State", s."PiiCounts"::text
        FROM content."Documents" d LEFT JOIN content."ChunkSets" s ON s."Id" = d."LiveChunkSetId"
        WHERE d."Id" = $1
        FOR UPDATE OF d
        """;

    private const string ClearanceSql = """
        UPDATE content."Documents" SET "PiiClearedAt" = $2, "PiiClearedBy" = $3, "PiiClearedCounts" = $4::jsonb WHERE "Id" = $1
        """;

    public string Kind => CommandKinds.ClearPiiFlag;

    public async Task<JobResult> ExecuteAsync(JobContext context, JsonElement payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (CommandPayload.Read<ClearPiiFlagCommand>(payload) is not { } clear)
        {
            return new JobResult.FatalError(CommandJobHandler.InvalidPayload);
        }

        var now = time.GetUtcNow();
        await using var commit = await EngineCommit.BeginAsync(database, cancellationToken);
        bool deleted;
        Guid? setId;
        string? state;
        string? counts;
        await using (var command = new NpgsqlCommand(DocumentSql, commit.Connection, commit.Transaction) { Parameters = { Db.P(clear.DocumentId) } })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new JobResult.FatalError(DocumentUnknown);
            }

            deleted = reader.GetBoolean(0);
            setId = Db.Nullable<Guid>(reader, 1);
            state = Db.String(reader, 2);
            counts = Db.String(reader, 3);
        }

        if (deleted)
        {
            return new JobResult.FatalError(DocumentDeleted);
        }

        await CommandCommit.ExecuteAsync(commit, ClearanceSql, cancellationToken,
            Db.P(clear.DocumentId), Db.P(now.ToUniversalTime()), Db.P(clear.DecidedBy), Db.P(counts ?? ContractJson.Serialize(new PiiCounts())));
        Guid? released = null;
        if (state == ChunkSetState.Held && setId is { } held)
        {
            await IndexWriter.SetStateAsync(commit.Connection, commit.Transaction, held, ChunkSetState.Live, now, cancellationToken);
            released = held;
        }

        return await CommandCommit.CompleteAsync(commit, queue, context.Job, identity, new ClearPiiFlagResult { ReleasedChunkSetId = released }, cancellationToken);
    }
}
