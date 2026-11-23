using Microsoft.EntityFrameworkCore;
using Kalla.Domain;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;

namespace Kalla.AdminApi;

/// <summary>
/// The assistant's latest answer evaluation for its Statistics tab (admin design §5.5.4, processing design §14.8; B-58):
/// <c>GET /api/assistants/{id}/answer-evaluation</c> (Read, within the assistant's scope, else 404). The last finished
/// <c>answers</c> run with its counts and figures, the run it compares with, the live <c>uncited_answer</c> rate of the last
/// <see cref="LiveDays"/> days from <c>AssistantDailyTotals</c> (chat design §18.8), and whether the nightly run is on
/// (<c>Settings.EvaluationAnswerQuestions</c> above 0). An unfinished run (a refused key, a stopped instance) is never shown.
/// </summary>
public static class AnswerEvaluationEndpoints
{
    /// <summary>The days of live traffic the uncited rate covers (the Cost card's default period).</summary>
    public const int LiveDays = 30;

    public static RouteGroupBuilder MapAnswerEvaluation(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapGet("/assistants/{id:guid}/answer-evaluation", async (Guid id, AdminContext admin, TimeProvider time, CancellationToken ct) =>
            {
                _ = await AssistantEndpoints.LoadAsync(admin, id, ct, tracked: false);
                var perNight = await admin.Db.Settings.AsNoTracking().Select(s => (int?)s.EvaluationAnswerQuestions).FirstOrDefaultAsync(ct)
                    ?? new PlatformSettings().EvaluationAnswerQuestions;
                var latest = await admin.Db.EvaluationRuns.AsNoTracking()
                    .Where(r => r.Kind == EvaluationRunKinds.Answers && r.SubjectId == id && r.FinishedAt != null)
                    .OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct);
                var previous = latest?.ComparedRunId is { } compared
                    ? await admin.Db.EvaluationRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == compared && r.FinishedAt != null, ct)
                    : null;

                var today = StockholmTime.Today(time);
                var first = today.AddDays(1 - LiveDays);
                var live = await admin.Db.AssistantDailyTotals.AsNoTracking().Where(t => t.AssistantId == id && t.Day >= first && t.Day <= today)
                    .GroupBy(_ => 1)
                    .Select(g => new
                    {
                        // The writer counts an uncited answer only among answered turns (chat §10.4).
                        Answered = g.Sum(t => t.Messages - t.NotFound - t.GeneralAnswers - t.Unavailable - t.Errors - t.Cancelled),
                        Uncited = g.Sum(t => t.UncitedAnswers),
                    })
                    .FirstOrDefaultAsync(ct);
                var answered = Math.Max(0, live?.Answered ?? 0);
                var uncited = live?.Uncited ?? 0;
                return new AnswerEvaluationDto(perNight > 0, perNight, AnswerRunSummary.Of(latest), AnswerRunSummary.Of(previous),
                    new UncitedRate(LiveDays, answered, uncited, answered == 0 ? null : AnswerJudge.Share(Math.Min(uncited, answered), answered)));
            })
            .RequireAuthorization(AdminPolicies.Read).WithTags("Assistant answer evaluation").WithName("GetAssistantAnswerEvaluation")
            .WithSummary("The latest nightly answer evaluation beside the live uncited-answer rate");
        return api;
    }
}

/// <summary>
/// The Statistics tab's answer evaluation: <see cref="Enabled"/> while the nightly run is on, <see cref="QuestionsPerNight"/>
/// the setting, <see cref="Latest"/> the last finished run and <see cref="Previous"/> the run it compares with.
/// </summary>
public sealed record AnswerEvaluationDto(bool Enabled, int QuestionsPerNight, AnswerRunSummary? Latest, AnswerRunSummary? Previous, UncitedRate Live);

/// <summary>
/// A finished <c>answers</c> run: the published version it ran, the questions asked, those whose expected source was cited
/// (or, without one, that got "not found"), the mean faithfulness and citation precision (0–1, null when nothing was judged)
/// and, against the compared run, the questions that came out better and worse.
/// </summary>
public sealed record AnswerRunSummary(Guid Id, int Version, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, int Questions, int Found, decimal? Faithfulness,
    decimal? CitationPrecision, int? Wins, int? Losses)
{
    public static AnswerRunSummary? Of(EvaluationRun? run) => run is { FinishedAt: { } finished }
        ? new(run.Id, run.SubjectVersion ?? 0, run.StartedAt, finished, run.QuestionCount ?? 0, run.Found ?? 0, run.Faithfulness, run.CitationPrecision, run.Wins, run.Losses)
        : null;
}

/// <summary>The live <c>uncited_answer</c> rate: answered turns of the last <see cref="Days"/> days and how many cited nothing.</summary>
public sealed record UncitedRate(int Days, int Answered, int Uncited, decimal? Rate);
