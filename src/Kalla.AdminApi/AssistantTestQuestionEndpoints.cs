using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.ServiceDefaults;

namespace Kalla.AdminApi;

/// <summary>
/// An assistant's test questions and their runs (admin design §4.5, §7.4, V33; B-57):
/// <c>GET/POST/PUT/DELETE /api/assistants/{id}/test-questions[/{qid}]</c> (Read / Edit, audited <c>assistanttest.*</c>),
/// <c>POST /api/assistants/{id}/test-runs</c> (Edit: run on the draft or the published version) and
/// <c>GET /api/assistants/{id}/test-runs[/{runId}]</c> (Read: the runs and a run's report). Everything is within the
/// assistant's scope: a caller who cannot read the assistant gets 404.
/// </summary>
public static class AssistantTestQuestionEndpoints
{
    /// <summary>The runs listed: the last 20 per assistant are kept (data-model §8).</summary>
    public const int RunsListed = 20;

    public static RouteGroupBuilder MapAssistantTestQuestions(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var group = api.MapGroup("/assistants/{id:guid}").WithTags("Assistant test questions");

        group.MapGet("/test-questions", async (Guid id, AdminContext admin, CancellationToken ct) =>
            {
                var assistant = await AssistantEndpoints.LoadAsync(admin, id, ct, tracked: false);
                var questions = await admin.Db.AssistantTestQuestions.AsNoTracking().Where(q => q.AssistantId == id).OrderBy(q => q.Ordinal).ThenBy(q => q.Id).ToListAsync(ct);
                var linked = await LinkedCollectionsAsync(admin, assistant, ct);
                var documents = await DocumentsAsync(admin, questions.Select(q => q.ExpectedDocumentId).OfType<Guid>(), ct);
                return questions.Select(q => TestQuestionDto.Of(q, documents, linked)).ToList();
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("ListAssistantTestQuestions").WithSummary("The assistant's test questions, in order");

        group.MapPost("/test-questions", async (Guid id, TestQuestionRequest request, AdminContext admin, CancellationToken ct) =>
            {
                var saved = await admin.WriteAsync(async attempt =>
                {
                    var assistant = await AssistantEndpoints.LoadAsync(admin, id, attempt, tracked: false);
                    var existing = await admin.Db.AssistantTestQuestions.AsNoTracking().Where(q => q.AssistantId == id).Select(q => q.Ordinal).ToListAsync(attempt);
                    if (existing.Count >= AssistantTestQuestion.MaxPerAssistant)
                    {
                        throw Invalid($"at most {AssistantTestQuestion.MaxPerAssistant} test questions per assistant");
                    }

                    await CheckDocumentAsync(admin, assistant, request.ExpectedDocumentId, attempt);
                    var question = new AssistantTestQuestion
                    {
                        AssistantId = id, Ordinal = (short)(existing.Count == 0 ? 0 : existing.Max() + 1), Question = "", ExpectedBehaviour = "",
                        CreatedBy = admin.Actor, CreatedAt = admin.Now, UpdatedAt = admin.Now, UpdatedBy = admin.Actor,
                    };
                    request.ApplyTo(question);
                    admin.Db.AssistantTestQuestions.Add(question);
                    return new AdminChange<AssistantTestQuestion>(question, "create", AuditEntityTypes.AssistantTest, question.Id, null, TestQuestionAudit.Of(question))
                    {
                        DepartmentId = assistant.DepartmentId, Classification = assistant.Classification,
                    };
                }, ct);
                return Results.Created($"/api/assistants/{id}/test-questions/{saved.Id}", await OneAsync(admin, id, saved, ct));
            })
            .RequireAuthorization(AdminPolicies.Edit).WithName("CreateAssistantTestQuestion").WithSummary("Add a test question (V33)");

        group.MapPut("/test-questions/{qid:guid}", async (Guid id, Guid qid, TestQuestionRequest request, AdminContext admin, CancellationToken ct) =>
            {
                var saved = await admin.WriteAsync(async attempt =>
                {
                    var assistant = await AssistantEndpoints.LoadAsync(admin, id, attempt, tracked: false);
                    var question = await admin.Db.AssistantTestQuestions.SingleOrDefaultAsync(q => q.Id == qid && q.AssistantId == id, attempt)
                        ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                    admin.ExpectVersion(question, request.Version);
                    var before = TestQuestionAudit.Of(question);
                    if (request.ExpectedDocumentId != question.ExpectedDocumentId)
                    {
                        await CheckDocumentAsync(admin, assistant, request.ExpectedDocumentId, attempt);
                    }

                    request.ApplyTo(question);
                    question.UpdatedAt = admin.Now;
                    question.UpdatedBy = admin.Actor;
                    return new AdminChange<AssistantTestQuestion>(question, "update", AuditEntityTypes.AssistantTest, question.Id, before, TestQuestionAudit.Of(question))
                    {
                        DepartmentId = assistant.DepartmentId, Classification = assistant.Classification,
                    };
                }, ct);
                return await OneAsync(admin, id, saved, ct);
            })
            .RequireAuthorization(AdminPolicies.Edit).WithName("UpdateAssistantTestQuestion").WithSummary("Change a test question (V33)");

        group.MapDelete("/test-questions/{qid:guid}", async (Guid id, Guid qid, [Microsoft.AspNetCore.Mvc.FromQuery] string? version, AdminContext admin, CancellationToken ct) =>
            {
                await admin.WriteAsync(async attempt =>
                {
                    var assistant = await AssistantEndpoints.LoadAsync(admin, id, attempt, tracked: false);
                    var question = await admin.Db.AssistantTestQuestions.SingleOrDefaultAsync(q => q.Id == qid && q.AssistantId == id, attempt)
                        ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                    admin.ExpectVersion(question, version);
                    var before = TestQuestionAudit.Of(question);
                    admin.Db.AssistantTestQuestions.Remove(question);
                    return new AdminChange<bool>(true, "delete", AuditEntityTypes.AssistantTest, qid, before, null)
                    {
                        DepartmentId = assistant.DepartmentId, Classification = assistant.Classification,
                    };
                }, ct);
                return Results.NoContent();
            })
            .RequireAuthorization(AdminPolicies.Edit).WithName("DeleteAssistantTestQuestion").WithSummary("Remove a test question; earlier runs keep it as asked");

        group.MapPost("/test-runs", async (Guid id, TestRunRequest request, AdminContext admin, AssistantTestRunner runner, CancellationToken ct) =>
            {
                var assistant = await AssistantEndpoints.LoadAsync(admin, id, ct, tracked: false);
                var versionId = request.Target == AssistantTestTarget.Published ? assistant.PublishedVersionId : assistant.DraftVersionId ?? assistant.PublishedVersionId;
                var version = versionId is { } v ? await admin.Db.AssistantVersions.AsNoTracking().SingleAsync(x => x.Id == v, ct) : throw AdminFaults.Of(AdminErrorCodes.NoDraft);
                if (string.IsNullOrWhiteSpace(version.GatewayModel))
                {
                    throw AdminFaults.Field("gatewayModel", AdminErrorCodes.InvalidValue);
                }

                var run = await runner.StartAsync(admin.Db, assistant, version, admin.Actor ?? "admin", ct) ?? throw AdminFaults.Of(AdminErrorCodes.NoTestQuestions);
                await admin.AuditAsync(_ => Task.FromResult(new AdminChange<bool>(true, "run", AuditEntityTypes.AssistantTest, run.Id, null,
                    new { assistantId = id, version = version.Number, questions = run.QuestionCount })
                {
                    DepartmentId = assistant.DepartmentId, Classification = assistant.Classification,
                }), ct);
                return Results.Accepted($"/api/assistants/{id}/test-runs/{run.Id}", TestRunSummary.Of(run, null));
            })
            // the test panel's rate policy, so an editor cannot fill the runner's queue ahead of the runs after a publish.
            .RequireAuthorization(AdminPolicies.Edit).RequireRateLimiting(AssistantTestEndpoints.RatePolicy).WithName("RunAssistantTestQuestions")
            .WithSummary("Run the test questions on the draft or the published version");

        group.MapGet("/test-runs", async (Guid id, AdminContext admin, CancellationToken ct) =>
            {
                _ = await AssistantEndpoints.LoadAsync(admin, id, ct, tracked: false);
                var runs = await admin.Db.EvaluationRuns.AsNoTracking().Where(r => r.Kind == EvaluationRunKinds.AssistantTests && r.SubjectId == id)
                    .OrderByDescending(r => r.StartedAt).Take(RunsListed).ToListAsync(ct);
                var compared = await ComparedAsync(admin, runs.Select(r => r.ComparedRunId).OfType<Guid>(), ct);
                return runs.Select(r => TestRunSummary.Of(r, r.ComparedRunId is { } c ? compared.GetValueOrDefault(c) : null)).ToList();
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("ListAssistantTestRuns").WithSummary("The latest test-question runs, newest first");

        group.MapGet("/test-runs/{runId:guid}", async (Guid id, Guid runId, AdminContext admin, CancellationToken ct) =>
            {
                var assistant = await AssistantEndpoints.LoadAsync(admin, id, ct, tracked: false);
                var run = await admin.Db.EvaluationRuns.AsNoTracking()
                    .SingleOrDefaultAsync(r => r.Id == runId && r.Kind == EvaluationRunKinds.AssistantTests && r.SubjectId == id, ct)
                    ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                var results = await admin.Db.EvaluationResults.AsNoTracking().Where(r => r.RunId == runId).ToListAsync(ct);
                var previous = run.ComparedRunId is { } c
                    ? await admin.Db.EvaluationResults.AsNoTracking().Where(r => r.RunId == c).ToDictionaryAsync(r => r.QuestionId, ct)
                    : [];
                var compared = run.ComparedRunId is { } cr ? (await ComparedAsync(admin, [cr], ct)).GetValueOrDefault(cr) : null;
                var questions = await admin.Db.AssistantTestQuestions.AsNoTracking().Where(q => q.AssistantId == id).ToDictionaryAsync(q => q.Id, ct);
                var linked = await LinkedCollectionsAsync(admin, assistant, ct);
                var documents = await DocumentsAsync(admin, questions.Values.Select(q => q.ExpectedDocumentId).OfType<Guid>(), ct);
                var order = questions.Values.OrderBy(q => q.Ordinal).Select((q, i) => (q.Id, i)).ToDictionary(x => x.Id, x => x.i);
                return new TestRunReport(TestRunSummary.Of(run, compared),
                    [.. results.OrderBy(r => order.GetValueOrDefault(r.QuestionId, int.MaxValue)).Select(r => TestResultDto.Of(r, questions.GetValueOrDefault(r.QuestionId),
                        previous.GetValueOrDefault(r.QuestionId), documents, linked))]);
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("GetAssistantTestRun").WithSummary("One run's report: each question's answer, checks and the judge's note");

        return api;
    }

    private static ApiFaultException Invalid(string rule) => AdminFaults.Of(AdminErrorCodes.TestQuestionInvalid, new Dictionary<string, string> { ["rule"] = rule });

    /// <summary>V33: an expected document must be in a collection the draft or the published version links.</summary>
    private static async Task CheckDocumentAsync(AdminContext admin, Assistant assistant, Guid? documentId, CancellationToken ct)
    {
        if (documentId is not { } id)
        {
            return;
        }

        var linked = await LinkedCollectionsAsync(admin, assistant, ct);
        var collection = await (from d in admin.Db.Documents.AsNoTracking()
                                join s in admin.Db.Sources.AsNoTracking() on d.SourceId equals s.Id
                                where d.Id == id && d.DeletedAt == null
                                select (Guid?)s.CollectionId).SingleOrDefaultAsync(ct);
        if (collection is null || !linked.Contains(collection.Value))
        {
            throw AdminFaults.Field("expectedDocumentId", AdminErrorCodes.TestQuestionDocumentUnlinked);
        }
    }

    /// <summary>
    /// The collections the draft or the published version links that the assistant's department may link (V11: its own and
    /// those shared with it).  another department's unshared collection, carried over by a copy, counts as unlinked, so
    /// V33 refuses its documents and their titles are not shown.
    /// </summary>
    internal static async Task<HashSet<Guid>> LinkedCollectionsAsync(AdminContext admin, Assistant assistant, CancellationToken ct)
    {
        var ids = new[] { assistant.DraftVersionId, assistant.PublishedVersionId }.OfType<Guid>().ToList();
        var versions = await admin.Db.AssistantVersions.AsNoTracking().Where(v => ids.Contains(v.Id)).ToListAsync(ct);
        return await CollectionSharing.LinkableAsync(admin.Db, assistant.DepartmentId, [.. versions.SelectMany(v => v.LinkedCollectionIds()).Distinct()], ct);
    }

    /// <summary>The expected documents' titles and collections (a deleted document is absent, so its question shows it as gone).</summary>
    private static async Task<Dictionary<Guid, (string Title, Guid CollectionId)>> DocumentsAsync(AdminContext admin, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var rows = await (from d in admin.Db.Documents.AsNoTracking()
                          join s in admin.Db.Sources.AsNoTracking() on d.SourceId equals s.Id
                          where wanted.Contains(d.Id) && d.DeletedAt == null
                          select new { d.Id, Title = d.Title ?? d.DisplayName ?? d.Url ?? "", s.CollectionId }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => (r.Title, r.CollectionId));
    }

    private static async Task<Dictionary<Guid, EvaluationRun>> ComparedAsync(AdminContext admin, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        return wanted.Count == 0 ? [] : await admin.Db.EvaluationRuns.AsNoTracking().Where(r => wanted.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
    }

    private static async Task<TestQuestionDto> OneAsync(AdminContext admin, Guid id, AssistantTestQuestion question, CancellationToken ct)
    {
        var assistant = await AssistantEndpoints.LoadAsync(admin, id, ct, tracked: false);
        var documents = await DocumentsAsync(admin, question.ExpectedDocumentId is { } d ? [d] : [], ct);
        return TestQuestionDto.Of(question, documents, await LinkedCollectionsAsync(admin, assistant, ct));
    }
}

/// <summary>
/// A test question's expected document: its title, or <c>Deleted</c> when it is gone; <c>Unlinked</c>, without a title, when no
/// collection the assistant links and may link holds it now.
/// </summary>
public sealed record ExpectedDocumentDto(Guid Id, string? Title, bool Deleted, bool Unlinked);

public sealed record TestQuestionDto(
    Guid Id, int Ordinal, string Question, string ExpectedBehaviour, ExpectedDocumentDto? ExpectedDocument, string? ExpectedPhrase, DateTimeOffset UpdatedAt,
    string? UpdatedBy, string Version)
{
    internal static TestQuestionDto Of(AssistantTestQuestion q, IReadOnlyDictionary<Guid, (string Title, Guid CollectionId)> documents, IReadOnlySet<Guid> linked) =>
        new(q.Id, q.Ordinal, q.Question, q.ExpectedBehaviour, Expected(q.ExpectedDocumentId, documents, linked), q.ExpectedPhrase, q.UpdatedAt, q.UpdatedBy,
            q.Version.ToString(CultureInfo.InvariantCulture));

    /// <summary>V33: an expected document no linked collection holds any more is marked, never refused.</summary>
    internal static ExpectedDocumentDto? Expected(Guid? id, IReadOnlyDictionary<Guid, (string Title, Guid CollectionId)> documents, IReadOnlySet<Guid> linked) =>
        id is not { } d ? null
        // a document no linkable collection holds shows no title (it may be another department's).
        : documents.TryGetValue(d, out var doc) ? linked.Contains(doc.CollectionId)
            ? new ExpectedDocumentDto(d, doc.Title, false, false)
            : new ExpectedDocumentDto(d, null, false, true)
        : new ExpectedDocumentDto(d, null, true, true);
}

public sealed record TestQuestionAudit(Guid Id, Guid AssistantId, string Question, string ExpectedBehaviour, Guid? ExpectedDocumentId, string? ExpectedPhrase)
{
    public static TestQuestionAudit Of(AssistantTestQuestion q) => new(q.Id, q.AssistantId, q.Question, q.ExpectedBehaviour, q.ExpectedDocumentId, q.ExpectedPhrase);
}

/// <summary>A test question (V33): question and expected behaviour 1–2,000 characters, an optional document and phrase (≤ 200).</summary>
public sealed record TestQuestionRequest : AdminRequest
{
    [Required, StringLength(AssistantTestQuestion.MaxText, MinimumLength = 1)]
    public string Question { get; init; } = "";

    [Required, StringLength(AssistantTestQuestion.MaxText, MinimumLength = 1)]
    public string ExpectedBehaviour { get; init; } = "";

    public Guid? ExpectedDocumentId { get; init; }

    [StringLength(AssistantTestQuestion.MaxPhrase)]
    public string? ExpectedPhrase { get; init; }

    /// <summary>The row's <c>version</c>; required to change it.</summary>
    public string? Version { get; init; }

    public void ApplyTo(AssistantTestQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var text = Question.Trim();
        var expected = ExpectedBehaviour.Trim();
        if (text.Length == 0)
        {
            throw AdminFaults.Field("question", AdminErrorCodes.InvalidValue);
        }

        if (expected.Length == 0)
        {
            throw AdminFaults.Field("expectedBehaviour", AdminErrorCodes.InvalidValue);
        }

        question.Question = text;
        question.ExpectedBehaviour = expected;
        question.ExpectedDocumentId = ExpectedDocumentId;
        question.ExpectedPhrase = string.IsNullOrWhiteSpace(ExpectedPhrase) ? null : ExpectedPhrase.Trim();
    }
}

/// <summary><c>Draft</c> (the published version when there is no draft) or <c>Published</c>.</summary>
public sealed record TestRunRequest : AdminRequest
{
    public AssistantTestTarget Target { get; init; } = AssistantTestTarget.Published;
}

/// <summary>A run: the version, when, how many as expected of how many, and against the compared run (the previous version's).</summary>
public sealed record TestRunSummary(
    Guid Id, int Version, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, int Questions, int? AsExpected, int? Better, int? Worse, Guid? ComparedRunId,
    int? ComparedVersion, int? ComparedAsExpected)
{
    public static TestRunSummary Of(EvaluationRun run, EvaluationRun? compared)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new(run.Id, run.SubjectVersion ?? 0, run.StartedAt, run.FinishedAt, run.QuestionCount ?? 0, run.FinishedAt is null ? null : run.Found, run.Wins,
            run.Losses, run.ComparedRunId, compared?.SubjectVersion, compared?.Found);
    }
}

/// <summary>
/// One question in a run: the answer, the deterministic checks (<c>Checks</c>: the expected document cited and the expected
/// phrase present, where the question had them; null when it had neither), the judge, and the change against the compared run.
/// </summary>
public sealed record TestResultDto(
    Guid QuestionId, string Question, string? Answer, bool? Checks, string? Verdict, string? JudgeNote, bool AsExpected, string? Change,
    ExpectedDocumentDto? ExpectedDocument, string? ExpectedPhrase, string? ExpectedBehaviour)
{
    public const string Better = "better";
    public const string Worse = "worse";

    internal static TestResultDto Of(EvaluationResult r, AssistantTestQuestion? q, EvaluationResult? before, IReadOnlyDictionary<Guid, (string Title, Guid CollectionId)> documents,
        IReadOnlySet<Guid> linked)
    {
        var asExpected = AssistantTestChecks.AsExpected(r.Hit, null, r.Verdict);
        var was = before is null ? (bool?)null : AssistantTestChecks.AsExpected(before.Hit, null, before.Verdict);
        return new(r.QuestionId, r.Question ?? q?.Question ?? "", r.Answer, r.Hit, r.Verdict, r.JudgeNote, asExpected,
            was is null || was == asExpected ? null : asExpected ? Better : Worse, q is null ? null : TestQuestionDto.Expected(q.ExpectedDocumentId, documents, linked),
            q?.ExpectedPhrase, q?.ExpectedBehaviour);
    }
}

public sealed record TestRunReport(TestRunSummary Run, IReadOnlyList<TestResultDto> Results);
