using System.Text.Json;
using Npgsql;
using Kalla.Infrastructure.Jobs;
using Kalla.Worker.Connectors.Deletion;
using Kalla.Worker.Connectors.Malware;
using Kalla.Worker.Connectors.Uploads;

namespace Kalla.Worker.Connectors.Persistence;

/// <summary>Writes the upload slots a batch plan changed (<see cref="UploadBatchPlan.Writes"/>), and the fetch stage's state.</summary>
public interface IUploadSlotWriter
{
    /// <summary>Upserts the slots by <c>Id</c>; a re-planned batch writes the same rows again.</summary>
    Task WriteAsync(IReadOnlyList<UploadSlot> slots, CancellationToken cancellationToken);

    /// <summary>
    /// The fetch stage's verdict on a slot's bytes (<paramref name="sha256"/>, lower-case hex), written only while the slot
    /// still holds them and is not deleted: <c>clean</c> with <paramref name="scannedAt"/>, <c>unscanned</c> (the scan is off)
    /// or another state without it, the sniffed type and reason.
    /// </summary>
    Task SetStateAsync(Guid uploadId, string sha256, UploadState state, DateTimeOffset? scannedAt, string? sniffedMediaType, string? reason, Guid? documentId,
        CancellationToken cancellationToken);
}

/// <summary><c>content."Uploads"</c>, one row per slot (connectors design §9.1).</summary>
public sealed class PostgresUploadStore(JobDatabase database) : IUploadSlotReader, IUploadSlotWriter
{
    private const string ListSql = """
        SELECT "Id", "SourceId", "Classification", "BatchId", "FileName", "DeclaredMediaType", "SniffedMediaType", "Bytes", "Sha256",
               "State", "Reason", "DocumentId", "UploadedBy", "UploadedAt", "ReplacedAt", "DeletedAt", "ScannedAt"
        FROM content."Uploads" WHERE "SourceId" = $1
        """;

    private const string UpsertSql = """
        INSERT INTO content."Uploads" ("Id", "SourceId", "Classification", "BatchId", "FileName", "DeclaredMediaType", "SniffedMediaType",
                                      "Bytes", "Sha256", "State", "Reason", "DocumentId", "UploadedBy", "UploadedAt", "ReplacedAt", "DeletedAt", "ScannedAt")
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17)
        ON CONFLICT ("Id") DO UPDATE
           SET "BatchId" = EXCLUDED."BatchId", "FileName" = EXCLUDED."FileName", "DeclaredMediaType" = EXCLUDED."DeclaredMediaType",
               "SniffedMediaType" = EXCLUDED."SniffedMediaType", "Bytes" = EXCLUDED."Bytes", "Sha256" = EXCLUDED."Sha256",
               "State" = EXCLUDED."State", "Reason" = EXCLUDED."Reason", "DocumentId" = coalesce(EXCLUDED."DocumentId", content."Uploads"."DocumentId"),
               "UploadedBy" = EXCLUDED."UploadedBy", "UploadedAt" = EXCLUDED."UploadedAt", "ReplacedAt" = EXCLUDED."ReplacedAt",
               "DeletedAt" = EXCLUDED."DeletedAt", "ScannedAt" = EXCLUDED."ScannedAt"
         WHERE content."Uploads"."SourceId" = EXCLUDED."SourceId"
        """;

    private const string StateSql = """
        UPDATE content."Uploads"
           SET "State" = $3, "ScannedAt" = $4, "SniffedMediaType" = coalesce($5, "SniffedMediaType"), "Reason" = $6, "DocumentId" = coalesce($7, "DocumentId")
         WHERE "Id" = $1 AND "Sha256" = $2 AND "DeletedAt" IS NULL
        """;

    public async Task<IReadOnlyList<UploadSlot>> ListAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(ListSql, connection) { Parameters = { Db.P(sourceId) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var slots = new List<UploadSlot>();
        while (await reader.ReadAsync(cancellationToken))
        {
            slots.Add(new UploadSlot
            {
                Id = reader.GetGuid(0),
                SourceId = reader.GetGuid(1),
                Classification = reader.GetString(2),
                BatchId = reader.GetGuid(3),
                FileName = reader.GetString(4),
                DeclaredMediaType = Db.String(reader, 5),
                SniffedMediaType = Db.String(reader, 6),
                Bytes = reader.GetInt64(7),
                Sha256 = Db.Hex(reader.GetFieldValue<byte[]>(8)),
                State = Db.Enum<UploadState>(reader.GetString(9)),
                Reason = Db.String(reader, 10),
                DocumentId = Db.Nullable<Guid>(reader, 11),
                UploadedBy = Db.String(reader, 12),
                UploadedAt = reader.GetFieldValue<DateTimeOffset>(13),
                ReplacedAt = Db.Nullable<DateTimeOffset>(reader, 14),
                DeletedAt = Db.Nullable<DateTimeOffset>(reader, 15),
                ScannedAt = Db.Nullable<DateTimeOffset>(reader, 16),
            });
        }

        return slots;
    }

    public async Task WriteAsync(IReadOnlyList<UploadSlot> slots, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count == 0)
        {
            return;
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var slot in slots)
        {
            await using var command = new NpgsqlCommand(UpsertSql, connection, transaction)
            {
                Parameters =
                {
                    Db.P(slot.Id), Db.P(slot.SourceId), Db.P(slot.Classification), Db.P(slot.BatchId), Db.P(slot.FileName),
                    Db.P(slot.DeclaredMediaType), Db.P(slot.SniffedMediaType), Db.P(slot.Bytes), Db.P(Db.Bytes(slot.Sha256)), Db.P(Db.Text(slot.State)),
                    Db.P(slot.Reason), Db.P(slot.DocumentId), Db.P(slot.UploadedBy), Db.P(slot.UploadedAt.ToUniversalTime()),
                    Db.P(Db.Utc(slot.ReplacedAt)), Db.P(Db.Utc(slot.DeletedAt)), Db.P(Db.Utc(slot.ScannedAt)),
                },
            };
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SetStateAsync(Guid uploadId, string sha256, UploadState state, DateTimeOffset? scannedAt, string? sniffedMediaType, string? reason,
        Guid? documentId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(StateSql, connection)
        {
            Parameters =
            {
                Db.P(uploadId), Db.P(Db.Bytes(sha256)), Db.P(Db.Text(state)), Db.P(Db.Utc(scannedAt)), Db.P(sniffedMediaType), Db.P(reason), Db.P(documentId),
            },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary><c>content."DeletionProposals"</c> (connectors design §13): the reconcile phase's rows and the commands' decisions.</summary>
public interface IDeletionProposalStore
{
    /// <summary>Upserts by (<c>RunId</c>, <c>DocumentId</c>): a repeated reconcile or decision writes the same row.</summary>
    Task WriteAsync(IReadOnlyList<DeletionProposal> proposals, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeletionProposal>> ListAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>The run of the source whose proposal still waits for a decision (one per source, §13.3), or null.</summary>
    Task<Guid?> WaitingRunAsync(Guid sourceId, CancellationToken cancellationToken);
}

public sealed class PostgresDeletionProposalStore(JobDatabase database) : IDeletionProposalStore
{
    private const string UpsertSql = """
        INSERT INTO content."DeletionProposals" ("RunId", "DocumentId", "SourceId", "Classification", "Reason", "Exempt", "Decision", "DecidedBy", "DecidedAt")
        SELECT * FROM unnest($1::uuid[], $2::uuid[], $3::uuid[], $4::varchar[], $5::varchar[], $6::boolean[], $7::varchar[], $8::varchar[], $9::timestamptz[])
        ON CONFLICT ("RunId", "DocumentId") DO UPDATE
           SET "Decision" = EXCLUDED."Decision", "DecidedBy" = EXCLUDED."DecidedBy", "DecidedAt" = EXCLUDED."DecidedAt"
        """;

    private const string ListSql = """
        SELECT "RunId", "DocumentId", "SourceId", "Classification", "Reason", "Exempt", "Decision", "DecidedBy", "DecidedAt"
        FROM content."DeletionProposals" WHERE "RunId" = $1 ORDER BY "DocumentId"
        """;

    private const string WaitingSql = $"""
        SELECT "RunId" FROM content."DeletionProposals" WHERE "SourceId" = $1 AND "Decision" = '{ProposalDecisions.Pending}' LIMIT 1
        """;

    public async Task WriteAsync(IReadOnlyList<DeletionProposal> proposals, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        if (proposals.Count == 0)
        {
            return;
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(UpsertSql, connection)
        {
            Parameters =
            {
                Db.P(proposals.Select(p => p.RunId).ToArray()), Db.P(proposals.Select(p => p.DocumentId).ToArray()),
                Db.P(proposals.Select(p => p.SourceId).ToArray()), Db.P(proposals.Select(p => p.Classification).ToArray()),
                Db.P(proposals.Select(p => p.Reason).ToArray()), Db.P(proposals.Select(p => p.Exempt).ToArray()),
                Db.P(proposals.Select(p => p.Decision).ToArray()), Db.P(proposals.Select(p => p.DecidedBy).ToArray()),
                Db.P(proposals.Select(p => Db.Utc(p.DecidedAt)).ToArray()),
            },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DeletionProposal>> ListAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(ListSql, connection) { Parameters = { Db.P(runId) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var proposals = new List<DeletionProposal>();
        while (await reader.ReadAsync(cancellationToken))
        {
            proposals.Add(new DeletionProposal
            {
                RunId = reader.GetGuid(0),
                DocumentId = reader.GetGuid(1),
                SourceId = reader.GetGuid(2),
                Classification = reader.GetString(3),
                Reason = reader.GetString(4),
                Exempt = reader.GetBoolean(5),
                Decision = Db.String(reader, 6),
                DecidedBy = Db.String(reader, 7),
                DecidedAt = Db.Nullable<DateTimeOffset>(reader, 8),
            });
        }

        return proposals;
    }

    public async Task<Guid?> WaitingRunAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(WaitingSql, connection) { Parameters = { Db.P(sourceId) } };
        return await command.ExecuteScalarAsync(cancellationToken) is Guid runId ? runId : null;
    }
}

/// <summary>
/// <c>content."QuarantinedFiles"</c> (connectors design §11): metadata of a ClamAV finding. Event 5102 is the runner's
/// (<c>Engine.DocumentIngest</c>), which knows the document.
/// </summary>
public interface IQuarantineStore
{
    /// <summary>Inserts the row; the same <see cref="QuarantineRecord.Id"/> again (a retried job) is kept as it is.</summary>
    Task AddAsync(QuarantineRecord record, CancellationToken cancellationToken);
}

public sealed class PostgresQuarantineStore(JobDatabase database) : IQuarantineStore
{
    private const string InsertSql = """
        INSERT INTO content."QuarantinedFiles" ("Id", "SourceId", "RunId", "Classification", "Sha256", "Signature", "Origin", "Name", "Url", "DetectedAt", "PurgeAfter")
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
        ON CONFLICT ("Id") DO NOTHING
        """;

    public async Task AddAsync(QuarantineRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(InsertSql, connection)
        {
            Parameters =
            {
                Db.P(record.Id), Db.P(record.SourceId), Db.P(record.RunId), Db.P(record.Classification), Db.P(Db.Bytes(record.Sha256)),
                Db.P(record.Signature), Db.P(record.Origin), Db.P(record.Name), Db.P(record.Url), Db.P(record.DetectedAt.ToUniversalTime()),
                Db.P(record.PurgeAfter.ToUniversalTime()),
            },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary><c>ConnectorCheckpoints.Kind</c> (connectors design §2.3).</summary>
public enum CheckpointKind
{
    /// <summary>The position of the current listing.</summary>
    Run,

    /// <summary>A durable cursor such as a delta link, replaced only when its listing reaches <c>Done</c>.</summary>
    State,
}

/// <summary><c>content."ConnectorCheckpoints"</c>, one per (source, kind) (connectors design §2.3).</summary>
public interface IConnectorCheckpointStore
{
    /// <summary>The stored checkpoint, or null when there is none (or it was written for another run, for <see cref="CheckpointKind.Run"/>).</summary>
    Task<T?> GetAsync<T>(Guid sourceId, CheckpointKind kind, Guid? runId, CancellationToken cancellationToken) where T : ConnectorCheckpoint;

    Task SaveAsync<T>(Guid sourceId, string classification, CheckpointKind kind, Guid? runId, T checkpoint, CancellationToken cancellationToken) where T : ConnectorCheckpoint;
}

public sealed class PostgresConnectorCheckpointStore(JobDatabase database, TimeProvider time) : IConnectorCheckpointStore
{
    private const string GetSql = """
        SELECT "Checkpoint"::text, "RunId" FROM content."ConnectorCheckpoints" WHERE "SourceId" = $1 AND "Kind" = $2
        """;

    private const string SaveSql = """
        INSERT INTO content."ConnectorCheckpoints" ("SourceId", "Kind", "Classification", "SchemaVersion", "Checkpoint", "RunId", "UpdatedAt")
        VALUES ($1, $2, $3, $4, $5::jsonb, $6, $7)
        ON CONFLICT ("SourceId", "Kind") DO UPDATE
           SET "Classification" = EXCLUDED."Classification", "SchemaVersion" = EXCLUDED."SchemaVersion", "Checkpoint" = EXCLUDED."Checkpoint",
               "RunId" = EXCLUDED."RunId", "UpdatedAt" = EXCLUDED."UpdatedAt"
        """;

    public async Task<T?> GetAsync<T>(Guid sourceId, CheckpointKind kind, Guid? runId, CancellationToken cancellationToken) where T : ConnectorCheckpoint
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(GetSql, connection) { Parameters = { Db.P(sourceId), Db.P(Db.Text(kind)) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || (kind == CheckpointKind.Run && Db.Nullable<Guid>(reader, 1) != runId))
        {
            return null;
        }

        return JsonSerializer.Deserialize<T>(reader.GetString(0), JsonSerializerOptions.Web);
    }

    public async Task SaveAsync<T>(Guid sourceId, string classification, CheckpointKind kind, Guid? runId, T checkpoint, CancellationToken cancellationToken)
        where T : ConnectorCheckpoint
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(SaveSql, connection)
        {
            Parameters =
            {
                Db.P(sourceId), Db.P(Db.Text(kind)), Db.P(classification), Db.P(checkpoint.SchemaVersion),
                Db.P(JsonSerializer.Serialize(checkpoint, JsonSerializerOptions.Web)), Db.P(runId), Db.P(time.GetUtcNow().ToUniversalTime()),
            },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
