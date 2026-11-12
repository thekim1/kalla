using Npgsql;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Worker.Commands;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Jobs;

namespace Ume.Rag.Worker.Evaluation.Generation;

/// <summary>The collection a round is for: its level, embedding profile and department (the owner of the round's model calls).</summary>
public sealed record EvalTarget(Guid Id, string Classification, Guid EmbeddingProfileId, Guid DepartmentId);

/// <summary><c>Settings.EvaluationModel</c>, <c>Settings.EvaluationJudgeModel</c> and <c>Settings.EvaluationMaxQuestions</c>.</summary>
public sealed record EvalSettings(string? Model, string? JudgeModel, int MaxQuestions)
{
    public const int DefaultMaxQuestions = 100;
}

/// <summary>A chunk with its text, its document's title, the content hash of its set and its content kind.</summary>
public sealed record EvalChunk(Guid ChunkId, Guid DocumentId, string Text, string HeadingPath, string Title, string DocumentType, byte[] TextHash,
    byte[] ContentHash, string ContentKind);

/// <summary>An active generated question of the set.</summary>
public sealed record ActiveQuestion(Guid Id, string Question, string ExpectedAnswer, Guid[] ExpectedDocumentIds, byte[]? SourceContentHash,
    Guid? SourceChunkId, Guid? SourceDocumentId);

/// <summary>An expected document now: deleted, or the content hash of its live set (null without one).</summary>
public sealed record DocumentNow(bool Deleted, byte[]? LiveContentHash);

/// <summary>A generated question to insert.</summary>
public sealed record NewQuestion(Guid Id, string Question, string ExpectedAnswer, Guid[] ExpectedDocumentIds, byte[] SourceContentHash, string Origin,
    Guid SourceChunkId);

/// <summary>
/// What a round writes: questions retired as stale, questions whose changed document still answers them, new questions, and
/// the set's next round when this one sampled (<c>EvaluationSets."Round"</c>; null when it generated nothing).
/// </summary>
public sealed record EvalSetWrite(IReadOnlyList<Guid> Stale, IReadOnlyList<(Guid QuestionId, byte[] ContentHash)> Rechecked, IReadOnlyList<NewQuestion> Added,
    int? NextRound = null);

/// <summary>
/// The SQL of set generation (data-model §2.2: <c>EvaluationSets</c>, <c>EvaluationQuestions</c>). Eligible chunks are the
/// children of the collection's live sets that are not personal-data flagged and not a large table's second or later row
/// group (processing design §13.1, #129). The round's writes and the job's completion commit in one transaction, fenced
/// by the lease (engine design §7.6).
/// </summary>
public sealed class EvalSetStore(JobDatabase database, IJobQueue queue, WorkerIdentity identity, TimeProvider time)
{
    private const string CollectionSql = """
        SELECT "Classification", "EmbeddingProfileId", "DepartmentId" FROM config."Collections" WHERE "Id" = $1 AND "DeletedAt" IS NULL
        """;

    private const string SettingsSql = """
        SELECT "EvaluationModel", "EvaluationJudgeModel", "EvaluationMaxQuestions" FROM config."Settings" ORDER BY "Id" LIMIT 1
        """;

    private const string CreateSetSql = """
        INSERT INTO content."EvaluationSets" ("Id", "CollectionId", "Classification") VALUES ($1, $2, $3) ON CONFLICT ("CollectionId") DO NOTHING
        """;

    private const string SetSql = """SELECT "Id" FROM content."EvaluationSets" WHERE "CollectionId" = $1""";

    // The set's "Round" is the round the next generation uses (#130).
    private const string RoundSql = """SELECT "Round" FROM content."EvaluationSets" WHERE "Id" = $1""";

    private const string NextRoundSql = """UPDATE content."EvaluationSets" SET "Round" = greatest("Round", $2) WHERE "Id" = $1""";

    private const string ActiveSql = $"""
        SELECT "Id", "Question", coalesce("ExpectedAnswer", ''), "ExpectedDocumentIds", "SourceContentHash", "SourceChunkId",
               ("Origin" ->> 'sourceDocumentId')::uuid
        FROM content."EvaluationQuestions"
        WHERE "SetId" = $1 AND "Layer" = '{EvaluationLayers.Generated}' AND "State" = '{EvaluationQuestionStates.Active}' AND "RetiredAt" IS NULL
        ORDER BY "Id"
        """;

    private const string LiveDocumentsSql = """
        SELECT count(*) FROM content."Documents" d JOIN content."ChunkSets" cs ON cs."Id" = d."LiveChunkSetId"
        WHERE cs."CollectionId" = $1 AND cs."State" = 'live' AND d."DeletedAt" IS NULL
        """;

    private const string EligibleSql = """
        SELECT c."Id", c."DocumentId", coalesce(c."DocumentType", ''), c."Ordinal", c."ContentKind"
        FROM content."Chunks" c
        JOIN content."ChunkSets" cs ON cs."Id" = c."ChunkSetId"
        JOIN content."Documents" d ON d."Id" = c."DocumentId" AND d."LiveChunkSetId" = cs."Id"
        WHERE c."CollectionId" = $1 AND c."DeletedAt" IS NULL AND c."Kind" = 'child' AND c."TokenCount" >= $2 AND NOT c."LargeTable"
          AND cs."State" = 'live' AND NOT cs."PiiFlagged" AND d."DeletedAt" IS NULL
          AND (d."Language" IS NULL OR d."Language" = 'sv')
        """;

    private const string ChunkColumns = """
        c."Id", c."DocumentId", c."Text", coalesce(c."HeadingPath", ''), coalesce(d."Title", d."DisplayName", d."Url", ''), coalesce(c."DocumentType", ''),
        c."TextHash", cs."ContentHash", c."ContentKind"
        """;

    private const string ChunksSql = $"""
        SELECT {ChunkColumns}
        FROM content."Chunks" c JOIN content."ChunkSets" cs ON cs."Id" = c."ChunkSetId" JOIN content."Documents" d ON d."Id" = c."DocumentId"
        WHERE c."Id" = ANY($1)
        """;

    private const string LiveChunksSql = $"""
        SELECT {ChunkColumns}
        FROM content."Documents" d
        JOIN content."ChunkSets" cs ON cs."Id" = d."LiveChunkSetId" AND cs."State" = 'live'
        JOIN content."Chunks" c ON c."ChunkSetId" = cs."Id"
        WHERE d."Id" = $1 AND d."DeletedAt" IS NULL AND c."DeletedAt" IS NULL AND c."Kind" = 'child'
        ORDER BY c."Ordinal"
        """;

    private const string SameTextSql = """
        SELECT DISTINCT c."TextHash", c."DocumentId"
        FROM content."Chunks" c JOIN content."Documents" d ON d."Id" = c."DocumentId" AND d."LiveChunkSetId" = c."ChunkSetId"
        WHERE c."CollectionId" = $1 AND c."TextHash" = ANY($2) AND c."DeletedAt" IS NULL AND d."DeletedAt" IS NULL
        """;

    private const string DocumentsSql = """
        SELECT d."Id", d."DeletedAt" IS NOT NULL, cs."ContentHash"
        FROM content."Documents" d LEFT JOIN content."ChunkSets" cs ON cs."Id" = d."LiveChunkSetId" AND cs."State" = 'live'
        WHERE d."Id" = ANY($1)
        """;

    private const string StaleSql = $"""
        UPDATE content."EvaluationQuestions" SET "State" = '{EvaluationQuestionStates.Stale}', "RetiredAt" = $2 WHERE "Id" = ANY($1) AND "RetiredAt" IS NULL
        """;

    private const string RecheckedSql = """UPDATE content."EvaluationQuestions" SET "SourceContentHash" = $2 WHERE "Id" = $1""";

    private const string InsertSql = $"""
        INSERT INTO content."EvaluationQuestions" ("Id", "SetId", "Classification", "Layer", "Question", "ExpectedAnswer", "ExpectedDocumentIds",
                                                   "SourceContentHash", "Origin", "State", "SourceChunkId", "CreatedBy")
        VALUES ($1, $2, $3, '{EvaluationLayers.Generated}', $4, $5, $6, $7, $8::jsonb, '{EvaluationQuestionStates.Active}', $9, $10)
        """;

    public async Task<EvalTarget?> CollectionAsync(Guid collectionId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(CollectionSql, connection) { Parameters = { Db.P(collectionId) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new EvalTarget(collectionId, reader.GetString(0), reader.GetGuid(1), reader.GetGuid(2)) : null;
    }

    /// <summary>The settings row's values; without a row the defaults (no model).</summary>
    public async Task<EvalSettings> SettingsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(SettingsSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new EvalSettings(Blank(Db.String(reader, 0)), Blank(Db.String(reader, 1)), reader.GetInt32(2))
            : new EvalSettings(null, null, EvalSettings.DefaultMaxQuestions);

        static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>The collection's set, created when it has none (as <c>edit-eval-question</c> does).</summary>
    public async Task<Guid> EnsureSetAsync(EvalTarget collection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(collection);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using (var create = new NpgsqlCommand(CreateSetSql, connection)
        {
            Parameters = { Db.P(Guid.CreateVersion7()), Db.P(collection.Id), Db.P(collection.Classification) },
        })
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var read = new NpgsqlCommand(SetSql, connection) { Parameters = { Db.P(collection.Id) } };
        return (Guid)(await read.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// The round this generation runs: the set's <c>Round</c> (1 for a new set, advanced by every round that sampled, even one
    /// that kept nothing).
    /// </summary>
    public async Task<int> RoundAsync(Guid setId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(RoundSql, connection) { Parameters = { Db.P(setId) } };
        return (int)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<IReadOnlyList<ActiveQuestion>> ActiveAsync(Guid setId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(ActiveSql, connection) { Parameters = { Db.P(setId) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var questions = new List<ActiveQuestion>();
        while (await reader.ReadAsync(cancellationToken))
        {
            questions.Add(new ActiveQuestion(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<Guid[]>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<byte[]>(4), Db.Nullable<Guid>(reader, 5), Db.Nullable<Guid>(reader, 6)));
        }

        return questions;
    }

    /// <summary><c>D</c>: the collection's documents with a live set.</summary>
    public async Task<int> LiveDocumentsAsync(Guid collectionId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(LiveDocumentsSql, connection) { Parameters = { Db.P(collectionId) } };
        return (int)(long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>
    /// The eligible chunks without their text: at least <paramref name="minTokens"/> tokens with the prefix, so the body
    /// check (<see cref="EvalSetGenerator.MinBodyTokens"/>) runs only on the sampled ones.
    /// </summary>
    public async Task<IReadOnlyList<SampleChunk>> EligibleAsync(Guid collectionId, int minTokens, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(EligibleSql, connection) { Parameters = { Db.P(collectionId), Db.P(minTokens) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var chunks = new List<SampleChunk>();
        while (await reader.ReadAsync(cancellationToken))
        {
            chunks.Add(new SampleChunk(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4)));
        }

        return chunks;
    }

    /// <summary>The chunks of <paramref name="chunkIds"/>, in that order.</summary>
    public async Task<IReadOnlyList<EvalChunk>> ChunksAsync(IReadOnlyList<Guid> chunkIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunkIds);
        var rows = await ReadChunksAsync(ChunksSql, Db.P(chunkIds.ToArray()), cancellationToken);
        var byId = rows.ToDictionary(c => c.ChunkId);
        return [.. chunkIds.Where(byId.ContainsKey).Select(id => byId[id])];
    }

    /// <summary>The live chunks of a document, in order (the re-check of a changed document, processing design §13.3).</summary>
    public Task<IReadOnlyList<EvalChunk>> LiveChunksAsync(Guid documentId, CancellationToken cancellationToken) =>
        ReadChunksAsync(LiveChunksSql, Db.P(documentId), cancellationToken);

    /// <summary>Per text hash, the collection's live documents with a live chunk of that text (duplicated pages).</summary>
    public async Task<ILookup<string, Guid>> SameTextDocumentsAsync(Guid collectionId, IReadOnlyCollection<byte[]> textHashes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(textHashes);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(SameTextSql, connection) { Parameters = { Db.P(collectionId), Db.P(textHashes.ToArray()) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var pairs = new List<(string Hash, Guid Document)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            pairs.Add((Db.Hex(reader.GetFieldValue<byte[]>(0)), reader.GetGuid(1)));
        }

        return pairs.ToLookup(p => p.Hash, p => p.Document, StringComparer.Ordinal);
    }

    /// <summary>The documents found, by id; a purged document is absent.</summary>
    public async Task<IReadOnlyDictionary<Guid, DocumentNow>> DocumentsAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(DocumentsSql, connection) { Parameters = { Db.P(documentIds.ToArray()) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var documents = new Dictionary<Guid, DocumentNow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            documents[reader.GetGuid(0)] = new DocumentNow(reader.GetBoolean(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<byte[]>(2));
        }

        return documents;
    }

    /// <summary>
    /// The round's writes and the job's completion with <paramref name="result"/>, in one transaction fenced by the lease, with
    /// the round's model calls (<paramref name="usage"/>) added to <c>CollectionDailyUsage</c> last (engine design §10.6).
    /// </summary>
    public async Task<JobResult> CommitAsync(LeasedJob job, Guid setId, string classification, string createdBy, EvalSetWrite write, EvalSetResult result,
        JobUsage usage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(write);
        await using var commit = await EngineCommit.BeginAsync(database, cancellationToken);
        if (write.Stale.Count > 0)
        {
            await CommandCommit.ExecuteAsync(commit, StaleSql, cancellationToken, Db.P(write.Stale.ToArray()), Db.P(time.GetUtcNow().ToUniversalTime()));
        }

        foreach (var (questionId, contentHash) in write.Rechecked)
        {
            await CommandCommit.ExecuteAsync(commit, RecheckedSql, cancellationToken, Db.P(questionId), Db.P(contentHash));
        }

        foreach (var question in write.Added)
        {
            await CommandCommit.ExecuteAsync(commit, InsertSql, cancellationToken, Db.P(question.Id), Db.P(setId), Db.P(classification), Db.P(question.Question),
                Db.P(question.ExpectedAnswer), Db.P(question.ExpectedDocumentIds), Db.P(question.SourceContentHash), Db.P(question.Origin),
                Db.P(question.SourceChunkId), Db.P(createdBy));
        }

        if (write.NextRound is { } next)
        {
            await CommandCommit.ExecuteAsync(commit, NextRoundSql, cancellationToken, Db.P(setId), Db.P(next));
        }

        return await CommandCommit.CompleteAsync(commit, queue, job, identity, result, usage, cancellationToken);
    }

    private async Task<IReadOnlyList<EvalChunk>> ReadChunksAsync(string sql, NpgsqlParameter parameter, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection) { Parameters = { parameter } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var chunks = new List<EvalChunk>();
        while (await reader.ReadAsync(cancellationToken))
        {
            chunks.Add(new EvalChunk(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetFieldValue<byte[]>(6), reader.GetFieldValue<byte[]>(7), reader.GetString(8)));
        }

        return chunks;
    }
}
