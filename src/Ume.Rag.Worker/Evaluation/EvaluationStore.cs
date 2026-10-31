using Npgsql;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Retrieval;
using Ume.Rag.Worker.Commands;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Processing.Persistence;

namespace Ume.Rag.Worker.Evaluation;

/// <summary>
/// One active question of the set as the run found it at its start (processing design §13.3); no expected documents means
/// the expected outcome is "not found" (weakness review §2).
/// </summary>
public sealed record EvaluationQuestionRow(Guid Id, EvaluationLayer Layer, string Question, IReadOnlyList<Guid> ExpectedDocumentIds)
{
    public bool Answerable => ExpectedDocumentIds.Count > 0;
}

/// <summary>
/// The collection under evaluation: its level, department, profile and resolved retrieval settings (knowledge design
/// §12.1), and its set's active questions, <c>human</c> first (processing design §13.4).
/// </summary>
public sealed record EvaluationTarget(
    Guid CollectionId,
    string Classification,
    Guid DepartmentId,
    DateTimeOffset? PublishedAt,
    EmbeddingProfileRecord Profile,
    RetrievalPlatformSettings Platform,
    RetrievalSettings Settings,
    Guid? SetId,
    IReadOnlyList<EvaluationQuestionRow> Questions)
{
    /// <summary>
    /// The evaluator's scope (processing design §13.5): the one collection over its live sets with every level, under
    /// <paramref name="settings"/> (the collection's own, or an on-demand run's candidate settings).
    /// </summary>
    public SearchScope Scope(RetrievalSettings settings) =>
        new(Profile.Id, Profile.Dimensions, [new CollectionInScope(CollectionId, settings)], Domain.Classification.All, Platform);
}

/// <summary>A finished earlier run to compare with: its id and, per answerable question, the rank it found (null: not found).</summary>
public sealed record ComparedRun(Guid Id, IReadOnlyDictionary<Guid, int?> Ranks);

/// <summary>One <c>EvaluationRuns</c> row with its <c>EvaluationResults</c> rows, written together.</summary>
public sealed record EvaluationRunRecord(EvaluationRun Run, IReadOnlyList<EvaluationResult> Results);

/// <summary>
/// The evaluation tables as the <c>eval</c> class reads and writes them (data-model §2.2, schema part 2 of PR #93): the
/// collection, its settings and profile, the set's questions, earlier runs, and the runs a job writes in the same
/// transaction as its completion (engine design §7.6), so a retried job never leaves half a run.
/// </summary>
public sealed class EvaluationStore(JobDatabase database, IEmbeddingProfileStore profiles)
{
    private const string CollectionSql = """
        SELECT c."Classification", c."DepartmentId", c."PublishedAt", c."EmbeddingProfileId", c."RetrievalSettings"::text,
               (SELECT st."Retrieval"::text FROM config."Settings" st ORDER BY st."Id" LIMIT 1),
               (SELECT s."Id" FROM content."EvaluationSets" s WHERE s."CollectionId" = c."Id")
        FROM config."Collections" c
        WHERE c."Id" = $1 AND c."DeletedAt" IS NULL
        """;

    // Human first (reports lead with the gold set), then in creation order (UUIDv7 ids): a fixed order per set.
    private const string QuestionsSql = $"""
        SELECT "Id", "Layer", "Question", "ExpectedDocumentIds" FROM content."EvaluationQuestions"
        WHERE "SetId" = $1 AND "State" = '{EvaluationQuestionStates.Active}' AND "Layer" IN ('{EvaluationLayers.Human}', '{EvaluationLayers.Generated}')
        ORDER BY ("Layer" = '{EvaluationLayers.Human}') DESC, "Id"
        """;

    // Publish reports and nightly runs only: they use the collection's own settings in the hybrid variant. On-demand runs
    // can carry candidate settings, which EvaluationRuns does not record, so they are never compared with.
    private const string ComparedSql = $"""
        SELECT r."Id" FROM content."EvaluationRuns" r
        WHERE r."SetId" = $1 AND r."Kind" IN ('{EvaluationRunKinds.PublishReport}', '{EvaluationRunKinds.Nightly}')
          AND r."Variant" = '{EvaluationVariants.Hybrid}' AND r."FinishedAt" IS NOT NULL AND r."StartedAt" < $2
        ORDER BY r."StartedAt" DESC, r."Id" DESC
        LIMIT 1
        """;

    private const string ComparedResultsSql = """
        SELECT "QuestionId", "ExpectedRank" FROM content."EvaluationResults" WHERE "RunId" = $1 AND "Hit" IS NOT NULL
        """;

    private const string NightlyRecallsSql = $"""
        SELECT "RecallAtK" FROM content."EvaluationRuns"
        WHERE "SetId" = $1 AND "Kind" = '{EvaluationRunKinds.Nightly}' AND "Variant" = '{EvaluationVariants.Hybrid}'
          AND "RecallAtK" IS NOT NULL AND "StartedAt" >= $2 AND "StartedAt" < $3
        """;

    private const string RunStartedSql = """SELECT "StartedAt" FROM content."Runs" WHERE "Id" = $1""";

    private const string VersionNumberSql = """SELECT "Number" FROM config."PipelineVersions" WHERE "Id" = $1""";

    private const string InsertRunSql = """
        INSERT INTO content."EvaluationRuns" ("Id", "SetId", "Classification", "Kind", "SubjectId", "SubjectVersion", "ComparedRunId", "QuestionCount",
                                             "K", "Found", "Wins", "Losses", "RecallAtK", "Mrr", "Reranked", "Variant", "StartedAt", "FinishedAt")
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18)
        """;

    private const string InsertResultsSql = """
        INSERT INTO content."EvaluationResults" ("RunId", "QuestionId", "Classification", "ExpectedRank", "Hit", "Coverage", "BestDistance", "BestKeywordScore")
        SELECT $1, u.q, $2, u.r, u.h, u.c, u.d, u.k
        FROM unnest($3::uuid[], $4::int[], $5::bool[], $6::text[], $7::real[], $8::real[]) AS u(q, r, h, c, d, k)
        """;

    /// <summary>The collection with its set and questions; null when it is unknown or deleted.</summary>
    /// <exception cref="FormatException">The collection's or the platform's retrieval settings do not resolve.</exception>
    public async Task<EvaluationTarget?> LoadAsync(Guid collectionId, CancellationToken cancellationToken)
    {
        string classification, collectionSettings;
        string? platformSettings;
        Guid department, profileId;
        Guid? setId;
        DateTimeOffset? publishedAt;
        await using (var connection = await database.OpenAsync(cancellationToken))
        {
            await using var command = new NpgsqlCommand(CollectionSql, connection) { Parameters = { Db.P(collectionId) } };
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            classification = reader.GetString(0);
            department = reader.GetGuid(1);
            publishedAt = Db.Nullable<DateTimeOffset>(reader, 2);
            profileId = reader.GetGuid(3);
            collectionSettings = reader.GetString(4);
            platformSettings = Db.String(reader, 5);
            setId = Db.Nullable<Guid>(reader, 6);
        }

        var profile = await profiles.GetAsync(profileId, cancellationToken)
            ?? throw new FormatException($"The collection's embedding profile {profileId} is unknown.");
        RetrievalPlatformSettings platform;
        RetrievalSettings settings;
        try
        {
            platform = RetrievalSettingsResolver.Platform(platformSettings);
            settings = RetrievalSettingsResolver.Collection(platform, collectionSettings);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            throw new FormatException("The retrieval settings do not resolve.", e);
        }

        var questions = setId is { } set ? await QuestionsAsync(set, cancellationToken) : [];
        return new EvaluationTarget(collectionId, classification, department, publishedAt, profile, platform, settings, setId, questions);
    }

    /// <summary>The last publish report or nightly run of the set that started before <paramref name="before"/>, with its ranks.</summary>
    public async Task<ComparedRun?> LastComparableAsync(Guid setId, DateTimeOffset before, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        Guid runId;
        await using (var command = new NpgsqlCommand(ComparedSql, connection) { Parameters = { Db.P(setId), Db.P(before.ToUniversalTime()) } })
        {
            if (await command.ExecuteScalarAsync(cancellationToken) is not Guid id)
            {
                return null;
            }

            runId = id;
        }

        var ranks = new Dictionary<Guid, int?>();
        await using (var command = new NpgsqlCommand(ComparedResultsSql, connection) { Parameters = { Db.P(runId) } })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                ranks[reader.GetGuid(0)] = Db.Nullable<int>(reader, 1);
            }
        }

        return new ComparedRun(runId, ranks);
    }

    /// <summary>recall@k of the set's nightly runs that started in [<paramref name="from"/>, <paramref name="to"/>).</summary>
    public async Task<IReadOnlyList<decimal>> NightlyRecallsAsync(Guid setId, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(NightlyRecallsSql, connection)
        {
            Parameters = { Db.P(setId), Db.P(from.ToUniversalTime()), Db.P(to.ToUniversalTime()) },
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var recalls = new List<decimal>();
        while (await reader.ReadAsync(cancellationToken))
        {
            recalls.Add(reader.GetDecimal(0));
        }

        return recalls;
    }

    /// <summary>When the <c>reprocess</c> run of a publish started (<c>content."Runs"</c>); null when it is not there.</summary>
    public async Task<DateTimeOffset?> RunStartedAtAsync(Guid runId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(RunStartedSql, connection) { Parameters = { Db.P(runId) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? reader.GetFieldValue<DateTimeOffset>(0) : null;
    }

    /// <summary>The <c>PipelineVersions.Number</c> of a published version; null when it is not there.</summary>
    public async Task<int?> VersionNumberAsync(Guid versionId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(VersionNumberSql, connection) { Parameters = { Db.P(versionId) } };
        return await command.ExecuteScalarAsync(cancellationToken) is int number ? number : null;
    }

    /// <summary>
    /// Writes the runs and their results and completes the job with <paramref name="result"/> in one transaction; the
    /// completion is the fence (a job that lost its lease writes nothing).
    /// </summary>
    public async Task<JobResult> CompleteAsync<TResult>(LeasedJob job, IJobQueue queue, WorkerIdentity identity, IReadOnlyList<EvaluationRunRecord> runs,
        TResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(runs);
        await using var commit = await EngineCommit.BeginAsync(database, cancellationToken);
        foreach (var (run, results) in runs)
        {
            await CommandCommit.ExecuteAsync(commit, InsertRunSql, cancellationToken,
                Db.P(run.Id), Db.P(run.SetId), Db.P(run.Classification), Db.P(run.Kind), Db.P(run.SubjectId), Db.P(run.SubjectVersion), Db.P(run.ComparedRunId),
                Db.P(run.QuestionCount), Db.P(run.K), Db.P(run.Found), Db.P(run.Wins), Db.P(run.Losses), Db.P(run.RecallAtK), Db.P(run.Mrr), Db.P(run.Reranked),
                Db.P(run.Variant), Db.P(run.StartedAt.ToUniversalTime()), Db.P(run.FinishedAt?.ToUniversalTime()));
            if (results.Count > 0)
            {
                await CommandCommit.ExecuteAsync(commit, InsertResultsSql, cancellationToken,
                    Db.P(run.Id), Db.P(run.Classification), Db.P(results.Select(r => r.QuestionId).ToArray()), Db.P(results.Select(r => r.ExpectedRank).ToArray()),
                    Db.P(results.Select(r => r.Hit).ToArray()), Db.P(results.Select(r => r.Coverage).ToArray()),
                    Db.P(results.Select(r => r.BestDistance).ToArray()), Db.P(results.Select(r => r.BestKeywordScore).ToArray()));
            }
        }

        return await CommandCommit.CompleteAsync(commit, queue, job, identity, result, cancellationToken);
    }

    private async Task<IReadOnlyList<EvaluationQuestionRow>> QuestionsAsync(Guid setId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(QuestionsSql, connection) { Parameters = { Db.P(setId) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var questions = new List<EvaluationQuestionRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var layer = reader.GetString(1) == EvaluationLayers.Human ? EvaluationLayer.Human : EvaluationLayer.Generated;
            questions.Add(new EvaluationQuestionRow(reader.GetGuid(0), layer, reader.GetString(2), reader.GetFieldValue<Guid[]>(3)));
        }

        return questions;
    }
}
