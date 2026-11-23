using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Kalla.Answering.Chat;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Gateway;
using Kalla.Infrastructure.Persistence;
using Kalla.ServiceDefaults;

namespace Kalla.AdminApi;

/// <summary>
/// The judge of the nightly answer run (processing design §14.8): it gets the question, the delivered answer and the text of
/// each source the answer cites, and counts the answer's claims, the claims the cited sources support, and which cited sources
/// support a claim. <b>Faithfulness</b> is the share of claims supported; <b>citation precision</b> the share of cited sources
/// that support a claim. The prompt is of the family of the test-question judge (<see cref="AssistantTestJudge"/>).
/// </summary>
public static class AnswerJudge
{
    /// <summary>The judge call's <c>x-fors-step</c> (processing §14.8).</summary>
    public const string Step = "eval-answer";

    /// <summary>The answer call's <c>x-fors-step</c>, so the night's cost shows the answers apart from the judge.</summary>
    public const string AnswerStep = "eval-answer-chat";

    /// <summary>The prompt's version, stored as the run's <c>PromptVersion</c>.</summary>
    public const string PromptVersion = "eval-answer.v1";

    public const int MaxTokens = 600;

    /// <summary>How much of each cited passage the judge reads.</summary>
    public const int SourceTextLength = 4000;

    public const string System = """
        Du granskar svar från en AI-assistent i Umeå kommun. Du får en fråga, svaret som användaren fick och texten i varje
        källa som svaret hänvisar till, med källans nummer. Dela upp svaret i påståenden om sakförhållanden (inte hälsningar,
        hänvisningar eller frågor tillbaka). Räkna hur många påståenden det finns och hur många av dem som stöds av texten i de
        källor svaret hänvisar till. Avgör för varje källa om den stöder något av påståendena. Bedöm bara mot källornas text,
        inte mot vad du själv vet.
        Svara bara med JSON: {"claims": antal påståenden, "supported": antal som stöds,
        "sources": [{"number": källans nummer, "supports": true | false}], "note": "en kort mening på svenska om varför"}
        """;

    /// <summary>A source the answer cites: its number in the answer, its title and the text of its cited passages.</summary>
    public sealed record CitedSource(int Number, string Title, string Text);

    /// <summary>The judge's figures for one answer; null where nothing can be said (no claims, or no source cited).</summary>
    public sealed record Judgement(decimal? Faithfulness, decimal? CitationPrecision, string Note);

    public static IReadOnlyList<ChatMessageIn> Messages(string question, string answer, IReadOnlyList<CitedSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var text = new StringBuilder();
        foreach (var source in sources)
        {
            text.Append('[').Append(source.Number).Append("] ").AppendLine(source.Title).AppendLine(source.Text).AppendLine();
        }

        return
        [
            new("system", System),
            new("user", $"""
                Fråga: {question}
                Svar: {answer}
                Källor i svaret:
                {(sources.Count == 0 ? "(inga)" : text.ToString().TrimEnd())}
                """),
        ];
    }

    /// <summary>
    /// The figures from the judge's text for an answer that cites <paramref name="cited"/>: faithfulness = supported / claims
    /// (null without claims), citation precision = the cited sources the judge says support a claim / the cited sources (null
    /// without any). Anything unreadable gives no figures and a note that says so.
    /// </summary>
    public static Judgement Parse(string? text, IReadOnlyCollection<int> cited)
    {
        ArgumentNullException.ThrowIfNull(cited);
        var json = text ?? "";
        var start = json.IndexOf('{', StringComparison.Ordinal);
        var end = json.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                using var doc = JsonDocument.Parse(json[start..(end + 1)]);
                var root = doc.RootElement;
                if (root.TryGetProperty("claims", out var c) && c.TryGetInt32(out var claims) && claims >= 0
                    && root.TryGetProperty("supported", out var s) && s.TryGetInt32(out var supported) && supported >= 0)
                {
                    var supporting = new HashSet<int>();
                    if (root.TryGetProperty("sources", out var list) && list.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in list.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("number", out var n) && n.TryGetInt32(out var number)
                                && item.TryGetProperty("supports", out var yes) && yes.ValueKind == JsonValueKind.True)
                            {
                                supporting.Add(number);
                            }
                        }
                    }

                    var note = root.TryGetProperty("note", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()!.Trim() : "";
                    decimal? faithfulness = claims == 0 ? null : Share(Math.Min(supported, claims), claims);
                    decimal? precision = cited.Count == 0 ? null : Share(cited.Count(supporting.Contains), cited.Count);
                    return new Judgement(faithfulness, precision, AssistantTestJudge.Cut(note));
                }
            }
            catch (JsonException)
            {
                // Falls through: no figures.
            }
        }

        return new Judgement(null, null, "The judge's answer could not be read.");
    }

    /// <summary><paramref name="part"/> of <paramref name="whole"/>, rounded to the column's four decimals.</summary>
    public static decimal Share(int part, int whole) => Math.Round((decimal)part / whole, 4, MidpointRounding.AwayFromZero);

    /// <summary>The mean of the figures present, rounded to four decimals; null when none is.</summary>
    public static decimal? Mean(IEnumerable<decimal?> values)
    {
        var present = values.OfType<decimal>().ToList();
        return present.Count == 0 ? null : Math.Round(present.Average(), 4, MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// <c>AnswerEvaluationService</c> (admin design §2.2, processing design §14.8, Decisions "Nightly answer run", B-58): every
/// night at <see cref="RunAt"/> Stockholm time, each published assistant answers at most
/// <c>Settings.EvaluationAnswerQuestions</c> questions of its knowledge bases' evaluation sets (the <c>human</c> layer first,
/// then the newest generated questions) through <see cref="AdminTestPanel"/> with the published version's settings, and
/// <see cref="AnswerJudge"/> rates each answer. One <c>EvaluationRuns</c> row of kind <c>answers</c> per assistant and night
/// (no set, <c>SubjectId</c> the assistant, <c>SubjectVersion</c> the published version, <c>ComparedRunId</c> its previous
/// finished run), one <c>EvaluationResults</c> row per question. The setting at 0 starts no run; an assistant without
/// questions gets none. A refusal of the assistant's key or its budget ends that assistant's run for the night: the row stays
/// unfinished, which the Statistics tab and the attention rule skip. The calls run as the assistant's gateway key and are
/// counted under <c>admin-console</c> like the test panel's.
/// </summary>
public sealed partial class AnswerEvaluationService(
    IServiceScopeFactory scopes, AdminTestPanel panel, TimeProvider time, ILogger<AnswerEvaluationService> logger, IMeterFactory meters) : BackgroundService
{
    /// <summary>After the worker's nightly retrieval runs (03:30) and the config retention (03:00).</summary>
    public static readonly TimeOnly RunAt = new(4, 0);

    /// <summary>The actor of the run's calls (its audit-free test turns name it as the panel names the user).</summary>
    public const string Actor = "answer-evaluation";

    /// <summary>The second key of the per-assistant advisory lock (the test-question runner's is 4711).</summary>
    private const long LockKey = 4712;

    private readonly Counter<long> _tokens = meters.Create("Kalla.AdminApi").CreateCounter<long>("rag.evaluation.llm_tokens");
    private readonly Counter<long> _runs = meters.Create("Kalla.AdminApi").CreateCounter<long>("rag.admin.answer_runs");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            await Task.Delay(RetentionService.NextRunAt(now, RunAt) - now, time, stoppingToken);
            try
            {
                await RunNightAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogNightFailed(logger, ex);
            }
        }
    }

    /// <summary>The start of the Stockholm day of <paramref name="now"/>: a run started since then is tonight's.</summary>
    public static DateTimeOffset NightOf(DateTimeOffset now)
    {
        var midnight = StockholmTime.DayOf(now).ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(midnight, StockholmTime.Zone.GetUtcOffset(midnight)).ToUniversalTime();
    }

    /// <summary>
    /// One night: a run for every published assistant that has questions and none tonight yet. <paramref name="only"/>
    /// limits it to some assistants (tests share one database). Returns the runs started.
    /// </summary>
    public async Task<IReadOnlyList<EvaluationRun>> RunNightAsync(CancellationToken ct, Func<Guid, bool>? only = null)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        var settings = await db.Settings.AsNoTracking().Select(s => new { s.EvaluationAnswerQuestions, Judge = s.EvaluationJudgeModel ?? s.EvaluationModel })
            .FirstOrDefaultAsync(ct);
        var max = settings?.EvaluationAnswerQuestions ?? new PlatformSettings().EvaluationAnswerQuestions;
        if (max <= 0)
        {
            LogSwitchedOff(logger);
            return [];
        }

        // Without a judge the answers would be paid for and never rated: no run until a model is set (Settings, Evaluation).
        if (string.IsNullOrWhiteSpace(settings?.Judge))
        {
            LogNoJudge(logger);
            return [];
        }

        // Published and active: an archived assistant keeps its published version but no client reaches it (admin §4.5).
        var assistants = await db.Assistants.AsNoTracking()
            .Where(a => a.PublishedVersionId != null && a.DeletedAt == null && a.State == AssistantStates.Active).OrderBy(a => a.Id)
            .Select(a => a.Id).ToListAsync(ct);
        var night = NightOf(time.GetUtcNow());
        var started = new List<EvaluationRun>();
        foreach (var id in assistants.Where(a => only?.Invoke(a) ?? true))
        {
            try
            {
                if (await RunAssistantAsync(id, max, settings?.Judge, night, ct) is { } run)
                {
                    started.Add(run);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // One assistant's failure leaves its row unfinished and the others run.
                _runs.Add(1, new KeyValuePair<string, object?>("outcome", "failed"));
                LogRunFailed(logger, ex, id);
            }
        }

        return started;
    }

    private async Task<EvaluationRun?> RunAssistantAsync(Guid assistantId, int max, string? judgeModel, DateTimeOffset night, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        var assistant = await db.Assistants.AsNoTracking().SingleOrDefaultAsync(a => a.Id == assistantId && a.DeletedAt == null && a.State == AssistantStates.Active, ct);
        var version = assistant?.PublishedVersionId is { } published
            ? await db.AssistantVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == published && v.State == AssistantVersionStates.Published, ct)
            : null;
        if (assistant is null || version is null)
        {
            return null;
        }

        var questions = await QuestionsAsync(db, version, max, ct);
        if (questions.Count == 0)
        {
            return null;
        }

        var run = await StartAsync(db, assistant, version, questions.Count, night, ct);
        if (run is null)
        {
            return null;
        }

        var previous = run.ComparedRunId is { } compared
            ? await db.EvaluationResults.AsNoTracking().Where(r => r.RunId == compared).ToDictionaryAsync(r => r.QuestionId, r => r.Hit, ct)
            : [];
        var results = new List<EvaluationResult>();
        foreach (var question in questions)
        {
            var result = await AnswerAsync(db, assistant, version, question, judgeModel, ct);
            if (result is null)
            {
                // The key or its budget was refused: the run stays unfinished for the night (processing §14.8).
                _runs.Add(1, new KeyValuePair<string, object?>("outcome", "refused"));
                LogRefused(logger, run.Id, assistant.Id);
                return run;
            }

            result.RunId = run.Id;
            db.EvaluationResults.Add(result);
            await db.SaveChangesAsync(ct);
            results.Add(result);
        }

        run.Found = results.Count(r => r.Hit == true);
        run.Faithfulness = AnswerJudge.Mean(results.Select(r => r.Faithfulness));
        run.CitationPrecision = AnswerJudge.Mean(results.Select(r => r.CitationPrecision));
        if (run.ComparedRunId is not null)
        {
            run.Wins = results.Count(r => r.Hit == true && previous.TryGetValue(r.QuestionId, out var before) && before == false);
            run.Losses = results.Count(r => r.Hit == false && previous.TryGetValue(r.QuestionId, out var before) && before == true);
        }

        run.FinishedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        _runs.Add(1, new KeyValuePair<string, object?>("outcome", "finished"));
        LogRunFinished(logger, run.Id, assistant.Id, version.Number, results.Count, run.Faithfulness, run.CitationPrecision);
        return run;
    }

    /// <summary>
    /// At most <paramref name="max"/> active questions of the evaluation sets of the version's knowledge bases: the
    /// <c>human</c> layer first, then the newest generated ones (their ids are time-ordered).
    /// </summary>
    private static async Task<List<EvaluationQuestion>> QuestionsAsync(RagDbContext db, AssistantVersion version, int max, CancellationToken ct)
    {
        var collections = version.LinkedCollectionIds().ToList();
        if (collections.Count == 0)
        {
            return [];
        }

        var sets = db.EvaluationSets.Where(s => collections.Contains(s.CollectionId)).Select(s => s.Id);
        return await db.EvaluationQuestions.AsNoTracking()
            .Where(q => sets.Contains(q.SetId) && q.State == EvaluationQuestionStates.Active && q.RetiredAt == null)
            .OrderBy(q => q.Layer == EvaluationLayers.Human ? 0 : 1).ThenByDescending(q => q.Id)
            .Take(max).ToListAsync(ct);
    }

    /// <summary>
    /// Tonight's run row, written under a per-assistant advisory lock; null when another instance holds the lock or the
    /// assistant already has a run since <paramref name="night"/>.
    /// </summary>
    private async Task<EvaluationRun?> StartAsync(RagDbContext db, Assistant assistant, AssistantVersion version, int questions, DateTimeOffset night, CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async token =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var locked = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(hashtextextended({assistant.Id.ToString()}, {LockKey})) AS \"Value\"")
                .SingleAsync(token);
            if (!locked || await db.EvaluationRuns.AsNoTracking().AnyAsync(r => r.Kind == EvaluationRunKinds.Answers && r.SubjectId == assistant.Id && r.StartedAt >= night, token))
            {
                return null;
            }

            var compared = await db.EvaluationRuns.AsNoTracking()
                .Where(r => r.Kind == EvaluationRunKinds.Answers && r.SubjectId == assistant.Id && r.FinishedAt != null)
                .OrderByDescending(r => r.StartedAt).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(token);
            var run = new EvaluationRun
            {
                SetId = null, Classification = assistant.Classification, Kind = EvaluationRunKinds.Answers, SubjectId = assistant.Id, SubjectVersion = version.Number,
                ComparedRunId = compared, QuestionCount = questions, PromptVersion = AnswerJudge.PromptVersion, StartedAt = time.GetUtcNow(),
            };
            db.EvaluationRuns.Add(run);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return run;
        }, ct);
    }

    /// <summary>One question answered and judged; null when the gateway refused the assistant's key or its budget.</summary>
    private async Task<EvaluationResult?> AnswerAsync(RagDbContext db, Assistant assistant, AssistantVersion version, EvaluationQuestion question, string? judgeModel,
        CancellationToken ct)
    {
        var result = new EvaluationResult { QuestionId = question.Id, Classification = assistant.Classification, Question = question.Question };
        AssistantTestResult answered;
        try
        {
            answered = await panel.AnswerAsync(db, Actor, assistant, version, new AssistantTestRequest { Question = question.Question }, AnswerJudge.AnswerStep, ct,
                AnswerJudge.SourceTextLength);
        }
        catch (ApiFaultException fault) when (fault.Status is StatusCodes.Status402PaymentRequired or StatusCodes.Status503ServiceUnavailable)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A question that could not be answered has no figures; the note says why and the run goes on.
            result.JudgeNote = AssistantTestJudge.Cut($"Not answered: {AssistantTestJudge.FailureText(ex)}");
            return result;
        }

        _tokens.Add((answered.Gateway.InputTokens ?? 0) + (answered.Gateway.OutputTokens ?? 0), new KeyValuePair<string, object?>("step", "answer"));
        var delivered = answered.Answer.Delivered;
        result.Answer = delivered;
        var citedDocuments = answered.Answer.Citations.Select(c => c.DocumentId).ToHashSet();
        // A human question without expected documents expects "not found" (data-model §2.2, weakness review §2).
        result.Hit = question.ExpectedDocumentIds.Length == 0
            ? answered.Answer.Outcome == "not-found"
            : question.ExpectedDocumentIds.Any(citedDocuments.Contains);
        if (answered.Answer.Outcome != "answered" && answered.Answer.Outcome != "answered-general")
        {
            result.JudgeNote = $"Not judged: the outcome was {answered.Answer.Outcome}.";
            return result;
        }

        if (string.IsNullOrWhiteSpace(judgeModel))
        {
            result.JudgeNote = "No judge model is set (Settings, Evaluation).";
            return result;
        }

        var sources = answered.Answer.Citations.Select(c => new AnswerJudge.CitedSource(c.Number, c.Title, string.Join("\n\n",
            answered.Retrieval.Candidates.Where(k => k.Cited && k.DocumentId == c.DocumentId).OrderBy(k => k.Rank).Select(k => k.Text)))).ToList();
        try
        {
            var judged = await panel.JudgeAsync(assistant, judgeModel, AnswerJudge.Messages(question.Question, delivered, sources), ct, AnswerJudge.Step,
                AnswerJudge.MaxTokens);
            _tokens.Add(judged.PromptTokens + judged.CompletionTokens, new KeyValuePair<string, object?>("step", "answer"));
            var judgement = AnswerJudge.Parse(judged.Text, [.. sources.Select(s => s.Number)]);
            (result.Faithfulness, result.CitationPrecision, result.JudgeNote) = (judgement.Faithfulness, judgement.CitationPrecision, judgement.Note);
        }
        catch (GatewayKeyRefusedException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result.JudgeNote = AssistantTestJudge.Cut($"The judge could not answer: {AssistantTestJudge.FailureText(ex)}");
        }

        return result;
    }

    [LoggerMessage(LogLevel.Information, "Nightly answer run: switched off (Settings.EvaluationAnswerQuestions = 0)")]
    private static partial void LogSwitchedOff(ILogger logger);

    [LoggerMessage(LogLevel.Warning, "Nightly answer run: no judge model is set (Settings.EvaluationJudgeModel or EvaluationModel), so no assistant gets a run")]
    private static partial void LogNoJudge(ILogger logger);

    [LoggerMessage(LogLevel.Information,
        "Answer run {RunId} of assistant {AssistantId} version {Version}: {Count} questions, faithfulness {Faithfulness}, citation precision {CitationPrecision}")]
    private static partial void LogRunFinished(ILogger logger, Guid runId, Guid assistantId, int version, int count, decimal? faithfulness, decimal? citationPrecision);

    [LoggerMessage(LogLevel.Warning, "Answer run {RunId} of assistant {AssistantId} ended for the night: the gateway refused the assistant's key or its budget")]
    private static partial void LogRefused(ILogger logger, Guid runId, Guid assistantId);

    [LoggerMessage(LogLevel.Error, "Answer run of assistant {AssistantId} failed")]
    private static partial void LogRunFailed(ILogger logger, Exception exception, Guid assistantId);

    [LoggerMessage(LogLevel.Error, "The nightly answer run failed")]
    private static partial void LogNightFailed(ILogger logger, Exception exception);
}
