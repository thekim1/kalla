using System.Text.Json;
using Npgsql;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Worker.Connectors.Persistence;
using Ume.Rag.Worker.Engine;
using Ume.Rag.Worker.Jobs;

namespace Ume.Rag.Worker.Commands;

/// <summary>
/// <c>edit-eval-question</c> (processing design §13.4): creates a <c>human</c> question in the collection's evaluation set
/// (the set is created with its first question), or updates or retires a question of that set. The payload schema holds
/// the limits (question 500 characters, expected answer 1,000, at most 10 distinct expected documents); every expected
/// document must be a live document of the collection. The admin generates the question id, so a create that finds its
/// own id in the set again is the same result. Result <see cref="EditEvalQuestionResult"/>.
/// </summary>
public sealed class EditEvalQuestionHandler(JobDatabase database, IJobQueue queue, WorkerIdentity identity, TimeProvider time) : ICommandHandler
{
    public const string CollectionUnknown = "collection.unknown";
    public const string QuestionUnknown = "eval_question.unknown";
    public const string QuestionConflict = "eval_question.conflict";
    public const string QuestionRetired = "eval_question.retired";
    public const string DocumentUnknown = "eval_question.unknown_document";

    private const string CollectionSql = """SELECT "Classification" FROM config."Collections" WHERE "Id" = $1 AND "DeletedAt" IS NULL""";

    private const string CreateSetSql = """
        INSERT INTO content."EvaluationSets" ("Id", "CollectionId", "Classification") VALUES ($1, $2, $3) ON CONFLICT ("CollectionId") DO NOTHING
        """;

    private const string SetSql = """SELECT "Id" FROM content."EvaluationSets" WHERE "CollectionId" = $1""";

    private const string DocumentsSql = """
        SELECT count(*) FROM content."Documents" d JOIN config."Sources" s ON s."Id" = d."SourceId"
        WHERE d."Id" = ANY($1) AND s."CollectionId" = $2 AND d."DeletedAt" IS NULL
        """;

    private const string QuestionSql = """SELECT "SetId", "State" FROM content."EvaluationQuestions" WHERE "Id" = $1 FOR UPDATE""";

    private const string InsertSql = $"""
        INSERT INTO content."EvaluationQuestions" ("Id", "SetId", "Classification", "Layer", "Question", "ExpectedAnswer", "ExpectedDocumentIds", "State")
        VALUES ($1, $2, $3, '{EvaluationLayers.Human}', $4, $5, $6, '{EvaluationQuestionStates.Active}')
        ON CONFLICT ("Id") DO NOTHING
        """;

    private const string UpdateSql = """
        UPDATE content."EvaluationQuestions" SET "Question" = $2, "ExpectedAnswer" = $3, "ExpectedDocumentIds" = $4 WHERE "Id" = $1
        """;

    private const string RetireSql = $"""
        UPDATE content."EvaluationQuestions" SET "State" = '{EvaluationQuestionStates.Retired}', "RetiredAt" = coalesce("RetiredAt", $2) WHERE "Id" = $1
        """;

    public string Kind => CommandKinds.EditEvalQuestion;

    public async Task<JobResult> ExecuteAsync(JobContext context, JsonElement payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (CommandPayload.Read<EditEvalQuestionCommand>(payload) is not { } edit)
        {
            return new JobResult.FatalError(CommandJobHandler.InvalidPayload);
        }

        await using var commit = await EngineCommit.BeginAsync(database, cancellationToken);
        if (await CommandCommit.ScalarAsync(commit, CollectionSql, cancellationToken, Db.P(edit.CollectionId)) is not string level)
        {
            return new JobResult.FatalError(CollectionUnknown);
        }

        if (edit.Action == QuestionAction.Create)
        {
            await CommandCommit.ExecuteAsync(commit, CreateSetSql, cancellationToken, Db.P(Guid.CreateVersion7()), Db.P(edit.CollectionId), Db.P(level));
        }

        if (await CommandCommit.ScalarAsync(commit, SetSql, cancellationToken, Db.P(edit.CollectionId)) is not Guid setId)
        {
            return new JobResult.FatalError(QuestionUnknown);
        }

        var expected = edit.ExpectedDocumentIds.ToArray();
        if (edit.Action != QuestionAction.Retire && expected.Length > 0 &&
            (long)(await CommandCommit.ScalarAsync(commit, DocumentsSql, cancellationToken, Db.P(expected), Db.P(edit.CollectionId)))! != expected.Length)
        {
            return new JobResult.FatalError(DocumentUnknown);
        }

        var existing = await ReadQuestionAsync(commit, edit.QuestionId, cancellationToken);
        var state = existing?.State;
        if (existing is { } row && row.SetId != setId)
        {
            return new JobResult.FatalError(edit.Action == QuestionAction.Create ? QuestionConflict : QuestionUnknown);
        }

        switch (edit.Action)
        {
            case QuestionAction.Create when state is null:
                await CommandCommit.ExecuteAsync(commit, InsertSql, cancellationToken,
                    Db.P(edit.QuestionId), Db.P(setId), Db.P(level), Db.P(edit.Question!), Db.P(edit.ExpectedAnswer), Db.P(expected));
                break;
            case QuestionAction.Create:
                // The same create again (a retried admin request): the question is there.
                break;
            case QuestionAction.Update when state is null:
                return new JobResult.FatalError(QuestionUnknown);
            case QuestionAction.Update when state == EvaluationQuestionStates.Retired:
                return new JobResult.FatalError(QuestionRetired);
            case QuestionAction.Update:
                await CommandCommit.ExecuteAsync(commit, UpdateSql, cancellationToken,
                    Db.P(edit.QuestionId), Db.P(edit.Question!), Db.P(edit.ExpectedAnswer), Db.P(expected));
                break;
            case QuestionAction.Retire when state is null:
                return new JobResult.FatalError(QuestionUnknown);
            default:
                await CommandCommit.ExecuteAsync(commit, RetireSql, cancellationToken, Db.P(edit.QuestionId), Db.P(time.GetUtcNow().ToUniversalTime()));
                break;
        }

        return await CommandCommit.CompleteAsync(commit, queue, context.Job, identity, new EditEvalQuestionResult { QuestionId = edit.QuestionId, SetId = setId },
            cancellationToken);
    }

    private static async Task<(Guid SetId, string State)?> ReadQuestionAsync(EngineCommit commit, Guid questionId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(QuestionSql, commit.Connection, commit.Transaction) { Parameters = { Db.P(questionId) } };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetGuid(0), reader.GetString(1)) : null;
    }
}
