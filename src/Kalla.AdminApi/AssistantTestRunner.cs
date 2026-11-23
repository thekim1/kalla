using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Kalla.Answering.Chat;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Persistence;

namespace Kalla.AdminApi;

/// <summary>
/// The answer judge of a test-question run (admin design §7.4): it gets the question, the expected behaviour, the delivered
/// answer and the cited titles, and answers <c>{"verdict": "pass"|"fail"|"unsure", "note": "…"}</c>. The prompt is of the
/// family of the <c>answers</c> run's judge (processing §14.8): Swedish, strict about what the answer says, unsure rather
/// than guessing.
/// </summary>
public static class AssistantTestJudge
{
    /// <summary>The judge call's <c>x-fors-step</c>; the answer itself is <c>assistant-test</c>.</summary>
    public const string Step = "assistant-test-judge";

    public const string AnswerStep = "assistant-test";

    /// <summary>The prompt's version, stored as the run's <c>PromptVersion</c>.</summary>
    public const string PromptVersion = "assistant-test-judge.v1";

    public const int MaxTokens = 300;

    public const int MaxNote = 1000;

    public const string System = """
        Du granskar svar från en AI-assistent i Umeå kommun. Du får en fråga, ett förväntat beteende, svaret som användaren
        fick och titlarna på de källor svaret hänvisar till. Avgör om svaret uppfyller det förväntade beteendet.
        Bedöm bara det som står i svaret, inte om det är välskrivet. "pass": svaret gör det som förväntas och inget som det
        förväntade beteendet förbjuder. "fail": svaret saknar något som förväntas eller gör något som förbjuds. "unsure":
        det går inte att avgöra utifrån svaret.
        Svara bara med JSON: {"verdict": "pass" | "fail" | "unsure", "note": "en kort mening på svenska om varför"}
        """;

    public static IReadOnlyList<ChatMessageIn> Messages(string question, string expected, string answer, IReadOnlyList<string> citedTitles) =>
    [
        new("system", System),
        new("user", $"""
            Fråga: {question}
            Förväntat beteende: {expected}
            Svar: {answer}
            Källor i svaret: {(citedTitles.Count == 0 ? "(inga)" : string.Join("; ", citedTitles))}
            """),
    ];

    /// <summary>The verdict and note from the judge's text; anything unreadable is <c>unsure</c> with a note that says so.</summary>
    public static (string Verdict, string Note) Parse(string? text)
    {
        var json = text ?? "";
        var start = json.IndexOf('{', StringComparison.Ordinal);
        var end = json.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                using var doc = JsonDocument.Parse(json[start..(end + 1)]);
                var verdict = doc.RootElement.TryGetProperty("verdict", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim().ToLowerInvariant() : "";
                var note = doc.RootElement.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : "";
                if (TestVerdicts.All.Contains(verdict))
                {
                    return (verdict, Cut(note));
                }
            }
            catch (JsonException)
            {
                // Falls through to unsure.
            }
        }

        return (TestVerdicts.Unsure, "The judge's answer could not be read.");
    }

    public static string Cut(string note) => note.Length <= MaxNote ? note : note[..MaxNote];

    /// <summary>
    /// A failure as a stored note may show it: the admin's own refusal text (<see cref="Kalla.ServiceDefaults.ApiFaultException"/>), else
    /// only the exception's type and status, never its message, which can name the gateway's host (#232).
    /// </summary>
    public static string FailureText(Exception exception) => exception is Kalla.ServiceDefaults.ApiFaultException fault ? fault.Message : GatewayModelSync.ErrorText(exception);
}

/// <summary>The deterministic checks of a test question (admin design §7.4).</summary>
public static class AssistantTestChecks
{
    /// <summary>The expected phrase in the delivered answer, case-insensitive after Unicode normalisation (NFKC).</summary>
    public static bool ContainsPhrase(string answer, string phrase)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(phrase);
        static string Fold(string s) => string.Join(' ', s.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return Fold(answer).Contains(Fold(phrase), StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether the question came out as expected: every check it has passes (the document cited and the phrase present where
    /// set, and the judge's <c>pass</c>).
    /// </summary>
    public static bool AsExpected(bool? documentCited, bool? phraseFound, string? verdict) =>
        documentCited != false && phraseFound != false && verdict == TestVerdicts.Pass;
}

/// <summary>
/// <c>AssistantTestRunner</c> (admin design §7.4, B-57): runs an assistant's test questions against one version, after every
/// publish and restore and on demand, one run per assistant at a time. Each question is answered once through
/// <see cref="AdminTestPanel"/> (as the test panel answers, <c>x-fors-step: assistant-test</c>), then checked: the expected
/// document cited, the expected phrase present, and the judge on the expected behaviour. Stored as an
/// <c>EvaluationRuns</c> row of kind <c>assistant-tests</c> with one <c>EvaluationResults</c> row per question; a run on the
/// published version is that version's <c>TestRunId</c>. Runs go one after another in this instance; a run that was not
/// finished an hour after it started (the instance stopped) no longer holds the assistant.
/// </summary>
public sealed partial class AssistantTestRunner(
    IServiceScopeFactory scopes, AdminTestPanel panel, TimeProvider time, ILogger<AssistantTestRunner> logger, IMeterFactory meters) : BackgroundService
{
    /// <summary>A run older than this without a finish no longer blocks a new one.</summary>
    public static readonly TimeSpan Stale = TimeSpan.FromHours(1);

    private readonly Channel<(Guid RunId, string Actor)> _queue = Channel.CreateUnbounded<(Guid, string)>();
    private readonly Counter<long> _outcomes = meters.Create("Kalla.AdminApi").CreateCounter<long>("rag.admin.assistant_tests");

    /// <summary>
    /// Starts a run of <paramref name="version"/>'s answers to the assistant's questions: the run row is written now (one at a
    /// time per assistant, under an advisory lock) and the questions run in the background. Null when the assistant has no
    /// questions; <see cref="AdminErrorCodes.TestRunRunning"/> while a run is going.
    /// </summary>
    public async Task<EvaluationRun?> StartAsync(RagDbContext db, Assistant assistant, AssistantVersion version, string actor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(version);
        var strategy = db.Database.CreateExecutionStrategy();
        var run = await strategy.ExecuteAsync(async token =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({assistant.Id.ToString()}, 4711))", token);
            var questions = await db.AssistantTestQuestions.AsNoTracking().CountAsync(q => q.AssistantId == assistant.Id, token);
            if (questions == 0)
            {
                return null;
            }

            var now = time.GetUtcNow();
            if (await db.EvaluationRuns.AsNoTracking().AnyAsync(r => r.Kind == EvaluationRunKinds.AssistantTests && r.SubjectId == assistant.Id
                    && r.FinishedAt == null && r.StartedAt > now - Stale, token))
            {
                throw AdminFaults.Of(AdminErrorCodes.TestRunRunning);
            }

            var started = new EvaluationRun
            {
                SetId = null, Classification = assistant.Classification, Kind = EvaluationRunKinds.AssistantTests, SubjectId = assistant.Id,
                SubjectVersion = version.Number, ComparedRunId = await ComparedRunAsync(db, assistant, version, token), QuestionCount = questions,
                PromptVersion = AssistantTestJudge.PromptVersion, StartedAt = now,
            };
            db.EvaluationRuns.Add(started);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return started;
        }, ct);
        if (run is not null)
        {
            await _queue.Writer.WriteAsync((run.Id, actor), ct);
        }

        return run;
    }

    /// <summary>
    /// The run to compare with: for the published version, the last finished run of the version published before it; for a
    /// draft, the last finished run of the published version.
    /// </summary>
    private static async Task<Guid?> ComparedRunAsync(RagDbContext db, Assistant assistant, AssistantVersion version, CancellationToken ct)
    {
        int? previous;
        if (version.State == AssistantVersionStates.Draft)
        {
            previous = assistant.PublishedVersionId is { } p
                ? await db.AssistantVersions.AsNoTracking().Where(v => v.Id == p).Select(v => (int?)v.Number).SingleOrDefaultAsync(ct)
                : null;
        }
        else
        {
            previous = await db.AssistantVersions.AsNoTracking()
                .Where(v => v.AssistantId == assistant.Id && v.Id != version.Id && v.PublishedAt != null && v.PublishedAt < version.PublishedAt)
                .OrderByDescending(v => v.PublishedAt).Select(v => (int?)v.Number).FirstOrDefaultAsync(ct);
        }

        return previous is null
            ? null
            : await db.EvaluationRuns.AsNoTracking()
                .Where(r => r.Kind == EvaluationRunKinds.AssistantTests && r.SubjectId == assistant.Id && r.SubjectVersion == previous && r.FinishedAt != null)
                .OrderByDescending(r => r.StartedAt).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (runId, actor) in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RunAsync(runId, actor, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogRunFailed(logger, ex, runId);
                _outcomes.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
                await FinishFailedAsync(runId);
            }
        }
    }

    /// <summary>A run that failed is finished as it stands, so it does not hold the assistant; its report shows the answered questions.</summary>
    private async Task FinishFailedAsync(Guid runId)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
            var answered = await db.EvaluationResults.CountAsync(r => r.RunId == runId);
            await db.EvaluationRuns.Where(r => r.Id == runId && r.FinishedAt == null).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.FinishedAt, time.GetUtcNow()).SetProperty(r => r.QuestionCount, answered).SetProperty(r => r.Found, 0));
        }
        catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or TimeoutException)
        {
            LogRunFailed(logger, ex, runId);
        }
    }

    /// <summary>Runs every question of the run's assistant and finishes the run; exposed for tests, which await it.</summary>
    public async Task RunAsync(Guid runId, string actor, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        var run = await db.EvaluationRuns.SingleAsync(r => r.Id == runId, ct);
        var assistant = await db.Assistants.AsNoTracking().SingleOrDefaultAsync(a => a.Id == run.SubjectId, ct);
        var version = assistant is null ? null
            : await db.AssistantVersions.AsNoTracking().SingleOrDefaultAsync(v => v.AssistantId == assistant.Id && v.Number == run.SubjectVersion, ct);
        var questions = await db.AssistantTestQuestions.AsNoTracking().Where(q => q.AssistantId == run.SubjectId).OrderBy(q => q.Ordinal).ThenBy(q => q.Id).ToListAsync(ct);
        var judgeModel = await db.Settings.AsNoTracking().Select(s => s.EvaluationJudgeModel ?? s.EvaluationModel).FirstOrDefaultAsync(ct);
        var previous = run.ComparedRunId is { } compared
            ? await db.EvaluationResults.AsNoTracking().Where(r => r.RunId == compared).ToDictionaryAsync(r => r.QuestionId, ct)
            : [];

        var found = 0;
        var wins = 0;
        var losses = 0;
        if (assistant is not null && version is not null)
        {
            foreach (var question in questions)
            {
                var result = await AnswerAsync(db, actor, assistant, version, question, judgeModel, ct);
                result.RunId = run.Id;
                db.EvaluationResults.Add(result);
                var asExpected = AssistantTestChecks.AsExpected(result.Hit, null, result.Verdict);
                found += asExpected ? 1 : 0;
                if (previous.TryGetValue(question.Id, out var before))
                {
                    var wasExpected = AssistantTestChecks.AsExpected(before.Hit, null, before.Verdict);
                    wins += asExpected && !wasExpected ? 1 : 0;
                    losses += !asExpected && wasExpected ? 1 : 0;
                }

                await db.SaveChangesAsync(ct);
            }
        }

        run.QuestionCount = assistant is null || version is null ? 0 : questions.Count;
        run.Found = found;
        run.Wins = run.ComparedRunId is null ? null : wins;
        run.Losses = run.ComparedRunId is null ? null : losses;
        run.FinishedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        if (version is { State: AssistantVersionStates.Published })
        {
            await db.AssistantVersions.Where(v => v.Id == version.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.TestRunId, run.Id), ct);
        }

        _outcomes.Add(1, new KeyValuePair<string, object?>("outcome", run.Losses > 0 ? "worse" : "finished"));
        LogRunFinished(logger, run.Id, run.SubjectId ?? Guid.Empty, run.SubjectVersion ?? 0, found, run.QuestionCount ?? 0);
    }

    private async Task<EvaluationResult> AnswerAsync(RagDbContext db, string actor, Assistant assistant, AssistantVersion version, AssistantTestQuestion question,
        string? judgeModel, CancellationToken ct)
    {
        var result = new EvaluationResult { QuestionId = question.Id, Classification = assistant.Classification, Question = question.Question };
        AssistantTestResult answered;
        try
        {
            answered = await panel.AnswerAsync(db, actor, assistant, version, new AssistantTestRequest { Question = question.Question }, AssistantTestJudge.AnswerStep, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A question that could not be answered is not as expected; the note says why, the run goes on.
            result.Hit = false;
            result.JudgeNote = AssistantTestJudge.Cut($"Not answered: {AssistantTestJudge.FailureText(ex)}");
            return result;
        }

        var delivered = answered.Answer.Delivered;
        result.Answer = delivered;
        bool? cited = question.ExpectedDocumentId is { } document ? answered.Answer.Citations.Any(c => c.DocumentId == document) : null;
        bool? phrase = question.ExpectedPhrase is { Length: > 0 } p ? AssistantTestChecks.ContainsPhrase(delivered, p) : null;
        result.Hit = cited is null && phrase is null ? null : cited != false && phrase != false;
        if (string.IsNullOrWhiteSpace(judgeModel))
        {
            result.Verdict = TestVerdicts.Unsure;
            result.JudgeNote = "No judge model is set (Settings, Evaluation).";
            return result;
        }

        try
        {
            var judged = await panel.JudgeAsync(assistant, judgeModel,
                AssistantTestJudge.Messages(question.Question, question.ExpectedBehaviour, delivered, [.. answered.Answer.Citations.Select(c => c.Title)]), ct);
            (result.Verdict, result.JudgeNote) = AssistantTestJudge.Parse(judged.Text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.Verdict = TestVerdicts.Unsure;
            result.JudgeNote = AssistantTestJudge.Cut($"The judge could not answer: {AssistantTestJudge.FailureText(ex)}");
        }

        return result;
    }

    [LoggerMessage(LogLevel.Information, "Assistant test run {RunId} of assistant {AssistantId} version {Version}: {Found} of {Count} as expected")]
    private static partial void LogRunFinished(ILogger logger, Guid runId, Guid assistantId, int version, int found, int count);

    [LoggerMessage(LogLevel.Error, "Assistant test run {RunId} failed")]
    private static partial void LogRunFailed(ILogger logger, Exception exception, Guid runId);
}
