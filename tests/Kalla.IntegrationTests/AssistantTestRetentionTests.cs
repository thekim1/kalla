using Microsoft.EntityFrameworkCore;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.TestKit;

namespace Kalla.IntegrationTests;

/// <summary>
/// Data-model §8 (B-57, B-58): the nightly content step keeps the last 20 <c>assistant-tests</c> runs and the last 60
/// <c>answers</c> runs per assistant, each kind counted on its own, and removes every run of an assistant whose row is gone;
/// a run's results go with it.
/// </summary>
public sealed class AssistantTestRetentionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_content_step_keeps_the_last_twenty_test_runs_and_sixty_answer_runs_per_assistant_and_none_of_a_deleted_one()
    {
        await using var database = new RagDatabase(); // its own: the step looks at every run
        await database.InitializeAsync();
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Migrator));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Retention", CostCenterCode = "RT1", SyncedAt = DateTimeOffset.UtcNow };
        var assistant = new Assistant { Slug = "retention", Name = "Retention", DepartmentId = department.Id, Classification = Classification.Internal, UpdatedAt = DateTimeOffset.UtcNow };
        db.AddRange(department, assistant);
        await db.SaveChangesAsync(Ct);

        var start = DateTimeOffset.UtcNow.AddDays(-30);
        EvaluationRun Run(Guid subject, int i, string kind = EvaluationRunKinds.AssistantTests) => new()
        {
            SetId = null, Classification = Classification.Internal, Kind = kind, SubjectId = subject, SubjectVersion = 1,
            QuestionCount = 1, Found = 1, StartedAt = start.AddHours(i), FinishedAt = start.AddHours(i).AddMinutes(1),
        };
        var kept = Enumerable.Range(0, 23).Select(i => Run(assistant.Id, i)).ToList();
        var gone = Guid.CreateVersion7(); // an assistant whose row was deleted
        var orphans = Enumerable.Range(0, 2).Select(i => Run(gone, i)).Append(Run(gone, 5, EvaluationRunKinds.Answers)).ToList();
        // 62 nightly answer runs: the 60 newest stay, whatever the test runs' count.
        var nights = Enumerable.Range(0, 62).Select(i => Run(assistant.Id, i, EvaluationRunKinds.Answers)).ToList();
        db.EvaluationRuns.AddRange([.. kept, .. orphans, .. nights]);
        await db.SaveChangesAsync(Ct);
        db.EvaluationResults.AddRange(kept.Concat(orphans).Concat(nights).Select(r => new EvaluationResult
        {
            RunId = r.Id, QuestionId = Guid.CreateVersion7(), Classification = Classification.Internal, Hit = true, Verdict = TestVerdicts.Pass, Question = "Fråga?", Answer = "Svar.",
        }));
        await db.SaveChangesAsync(Ct);

        var worker = database.Connection(RagDatabase.Worker);
        while (await Sql.ScalarAsync<long>(worker, "SELECT content.rag_retention_content_step(2)") > 0)
        {
        }

        var left = await db.EvaluationRuns.AsNoTracking().Where(r => r.Kind == EvaluationRunKinds.AssistantTests).OrderBy(r => r.StartedAt).Select(r => r.Id).ToListAsync(Ct);
        left.ShouldBe(kept.Skip(3).Select(r => r.Id)); // the newest 20 of the living assistant, none of the deleted one's
        var answers = await db.EvaluationRuns.AsNoTracking().Where(r => r.Kind == EvaluationRunKinds.Answers).OrderBy(r => r.StartedAt).Select(r => r.Id).ToListAsync(Ct);
        answers.ShouldBe(nights.Skip(2).Select(r => r.Id));
        (await db.EvaluationResults.AsNoTracking().CountAsync(Ct)).ShouldBe(80);
    }
}
