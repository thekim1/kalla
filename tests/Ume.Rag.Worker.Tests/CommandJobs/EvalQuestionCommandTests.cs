using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Worker.Commands;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Tests.Engine;

namespace Ume.Rag.Worker.Tests.CommandJobs;

/// <summary>
/// <c>edit-eval-question</c> (P1a item 10; processing design §13.4): a create makes the collection's set with its first
/// <c>human</c> question and is the same result when repeated; update and retire change only that question; the payload
/// limits (500, 1,000, 10) and unknown collections, questions and documents are refused with nothing written.
/// </summary>
[Collection(RagDatabaseGroup1.Name)]
public sealed class EvalQuestionCommandTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private CommandRunner _runner = null!;

    public async ValueTask InitializeAsync()
    {
        // After the reset: it disposes the data sources the runner's queue and database use.
        await database.ResetAsync();
        _runner = new CommandRunner(database);
    }

    public ValueTask DisposeAsync() => _runner.DisposeAsync();

    [Fact]
    public async Task A_question_is_created_updated_and_retired_and_a_repeated_create_is_the_same_result()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        try
        {
            var document = await CommandSeeds.DocumentAsync(database, seed, $"https://site.test/{Guid.NewGuid():N}");
            var question = Guid.CreateVersion7();
            var create = Question(seed, question, QuestionAction.Create) with { ExpectedDocumentIds = [document] };

            var first = await _runner.RunAsync(create, seed.DepartmentId);
            var repeated = await _runner.RunAsync(create, seed.DepartmentId);

            first.Result.ShouldBeOfType<JobResult.Committed>();
            repeated.Result.ShouldBeOfType<JobResult.Committed>();
            var setId = (await CommandSeeds.ResultAsync(database, first.Job.Id)).GetProperty("setId").GetGuid();
            (await CommandSeeds.ResultAsync(database, repeated.Job.Id)).GetProperty("setId").GetGuid().ShouldBe(setId);
            (await Sql.ScalarAsync<Guid>(database.Connection(null), """SELECT "CollectionId" FROM content."EvaluationSets" WHERE "Id" = $1""", setId))
                .ShouldBe(seed.CollectionId);
            (await RowAsync(question)).ShouldBe($"human|active|Hur söker jag bygglov?|{document}|internal");
            (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."EvaluationQuestions" WHERE "SetId" = $1""", setId)).ShouldBe(1);

            var update = await _runner.RunAsync(create with { Action = QuestionAction.Update, Question = "Vad kostar bygglov?", ExpectedDocumentIds = [] },
                seed.DepartmentId);
            update.Result.ShouldBeOfType<JobResult.Committed>();
            (await RowAsync(question)).ShouldBe("human|active|Vad kostar bygglov?||internal");

            var retire = await _runner.RunAsync(new EditEvalQuestionCommand { Action = QuestionAction.Retire, CollectionId = seed.CollectionId, QuestionId = question },
                seed.DepartmentId);
            retire.Result.ShouldBeOfType<JobResult.Committed>();
            (await RowAsync(question)).ShouldStartWith("human|retired|");
            (await Sql.ScalarAsync<bool>(database.Connection(null), """SELECT "RetiredAt" IS NOT NULL FROM content."EvaluationQuestions" WHERE "Id" = $1""", question))
                .ShouldBeTrue();

            (await _runner.RunAsync(create with { Action = QuestionAction.Update }, seed.DepartmentId)).Result
                .ShouldBe(new JobResult.FatalError(EditEvalQuestionHandler.QuestionRetired));
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
        }
    }

    [Fact]
    public async Task Unknown_ids_and_payloads_over_the_limits_are_refused_with_nothing_written()
    {
        var seed = await EngineKit.SeedUrlListAsync(database, []);
        var other = await EngineKit.SeedUrlListAsync(database, []);
        try
        {
            var foreign = await CommandSeeds.DocumentAsync(database, other, $"https://site.test/{Guid.NewGuid():N}");
            var question = Guid.CreateVersion7();
            var create = Question(seed, question, QuestionAction.Create);

            (await Run(create with { CollectionId = Guid.NewGuid() })).ShouldBe(new JobResult.FatalError(EditEvalQuestionHandler.CollectionUnknown));
            (await Run(create with { ExpectedDocumentIds = [foreign] })).ShouldBe(new JobResult.FatalError(EditEvalQuestionHandler.DocumentUnknown));
            (await Run(create with { Action = QuestionAction.Update })).ShouldBe(new JobResult.FatalError(EditEvalQuestionHandler.QuestionUnknown));
            (await Run(create with { Question = new string('x', 501) })).ShouldBe(new JobResult.FatalError(CommandJobHandler.InvalidPayload));
            (await Run(create with { ExpectedAnswer = new string('x', 1001) })).ShouldBe(new JobResult.FatalError(CommandJobHandler.InvalidPayload));
            (await Run(create with { ExpectedDocumentIds = [.. Enumerable.Range(0, 11).Select(_ => Guid.NewGuid())] }))
                .ShouldBe(new JobResult.FatalError(CommandJobHandler.InvalidPayload));
            (await Run(create with { Question = null })).ShouldBe(new JobResult.FatalError(CommandJobHandler.InvalidPayload));
            (await Sql.ScalarAsync<long>(database.Connection(null), """SELECT count(*) FROM content."EvaluationQuestions" WHERE "Id" = $1""", question)).ShouldBe(0);

            (await Run(create with { Question = new string('x', 500), ExpectedAnswer = new string('y', 1000) })).ShouldBeOfType<JobResult.Committed>();
            var otherSet = Question(other, Guid.CreateVersion7(), QuestionAction.Create);
            (await _runner.RunAsync(otherSet, other.DepartmentId)).Result.ShouldBeOfType<JobResult.Committed>();
            (await _runner.RunAsync(otherSet with { QuestionId = question }, other.DepartmentId)).Result
                .ShouldBe(new JobResult.FatalError(EditEvalQuestionHandler.QuestionConflict));
        }
        finally
        {
            await EngineKit.PauseAsync(database, seed);
            await EngineKit.PauseAsync(database, other);
        }

        async Task<JobResult> Run(EditEvalQuestionCommand command) => (await _runner.RunAsync(command, seed.DepartmentId)).Result;
    }

    private static EditEvalQuestionCommand Question(SourceSeed seed, Guid questionId, QuestionAction action) => new()
    {
        Action = action,
        CollectionId = seed.CollectionId,
        QuestionId = questionId,
        Question = "Hur söker jag bygglov?",
        ExpectedAnswer = "Via e-tjänsten.",
    };

    private Task<string> RowAsync(Guid questionId) =>
        Sql.ScalarAsync<string>(database.Connection(null), """
            SELECT "Layer" || '|' || "State" || '|' || "Question" || '|' || array_to_string("ExpectedDocumentIds", ',') || '|' || "Classification"
            FROM content."EvaluationQuestions" WHERE "Id" = $1
            """, questionId);
}
