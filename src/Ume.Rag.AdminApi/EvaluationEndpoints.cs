using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;

namespace Ume.Rag.AdminApi;

/// <summary>
/// Evaluation of a collection (admin design §4.5 "Evaluation", §4.7; processing design §13.4–§13.6; P1a item 14d): the set's
/// state, its questions, the human layer through the <c>edit-eval-question</c> command (the worker owns
/// <c>content."EvaluationQuestions"</c>), on-demand runs (<c>eval</c> <c>on-demand</c> with variants) and their results.
/// Department-scoped through the collection.
/// </summary>
public static class EvaluationEndpoints
{
    public static RouteGroupBuilder MapEvaluation(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var evaluation = api.MapGroup("/collections/{id:guid}/evaluation").WithTags("Evaluation");

        evaluation.MapGet("", async (Guid id, AdminContext admin, CancellationToken ct) =>
            {
                _ = await Collection(admin, id, ct);
                var sets = admin.Db.EvaluationSets.AsNoTracking().Where(s => s.CollectionId == id).Select(s => s.Id);
                var counts = await admin.Db.EvaluationQuestions.AsNoTracking().Where(q => sets.Contains(q.SetId)).GroupBy(q => new { q.Layer, q.State })
                    .Select(g => new QuestionCount(g.Key.Layer, g.Key.State, g.Count())).ToListAsync(ct);
                var runs = await admin.Db.EvaluationRuns.AsNoTracking().Where(r => sets.Contains(r.SetId)).OrderByDescending(r => r.StartedAt).Take(10)
                    .Select(r => EvaluationRunDto.Of(r)).ToListAsync(ct);
                return new EvaluationOverview(await sets.AnyAsync(ct), counts, runs);
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("GetEvaluation").WithSummary("A collection's evaluation set by layer and state, and its latest runs");

        evaluation.MapGet("/questions", async (Guid id, string? layer, string? state, string? q, Guid? after, int? limit, AdminContext admin, CancellationToken ct) =>
            {
                _ = await Collection(admin, id, ct);
                var sets = admin.Db.EvaluationSets.AsNoTracking().Where(s => s.CollectionId == id).Select(s => s.Id);
                var query = admin.Db.EvaluationQuestions.AsNoTracking().Where(x => sets.Contains(x.SetId));
                if (!string.IsNullOrWhiteSpace(layer))
                {
                    query = query.Where(x => x.Layer == layer);
                }

                if (!string.IsNullOrWhiteSpace(state))
                {
                    query = query.Where(x => x.State == state);
                }

                if (!string.IsNullOrWhiteSpace(q))
                {
                    var pattern = $"%{q.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
                    query = query.Where(x => EF.Functions.ILike(x.Question, pattern, "\\"));
                }

                if (after is { } last)
                {
                    query = query.Where(x => x.Id.CompareTo(last) > 0);
                }

                var take = Math.Clamp(limit ?? 50, 1, 200);
                var rows = await query.OrderBy(x => x.Id).Take(take + 1)
                    .Select(x => new EvaluationQuestionDto(x.Id, x.Layer, x.State, x.Question, x.ExpectedAnswer, x.ExpectedDocumentIds, x.Origin, x.CreatedBy, x.RetiredAt))
                    .ToListAsync(ct);
                return new KeysetPage<EvaluationQuestionDto, Guid?>([.. rows.Take(take)], rows.Count > take ? rows[take - 1].Id : null);
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("ListEvaluationQuestions").WithSummary("The set's questions (keyset; layer, state, q)");

        evaluation.MapPost("/questions", (Guid id, EvaluationQuestionRequest request, AdminContext admin, CancellationToken ct) =>
                EditAsync(id, Guid.CreateVersion7(), QuestionAction.Create, request, admin, ct))
            .RequireAuthorization(AdminPolicies.Edit).WithName("CreateEvaluationQuestion").WithSummary("Add a human question (command edit-eval-question)");

        evaluation.MapPut("/questions/{questionId:guid}", (Guid id, Guid questionId, EvaluationQuestionRequest request, AdminContext admin, CancellationToken ct) =>
                EditAsync(id, questionId, QuestionAction.Update, request, admin, ct))
            .RequireAuthorization(AdminPolicies.Edit).WithName("UpdateEvaluationQuestion").WithSummary("Change a human question (command edit-eval-question)");

        evaluation.MapDelete("/questions/{questionId:guid}", (Guid id, Guid questionId, AdminContext admin, CancellationToken ct) =>
                EditAsync(id, questionId, QuestionAction.Retire, null, admin, ct))
            .RequireAuthorization(AdminPolicies.Edit).WithName("RetireEvaluationQuestion").WithSummary("Retire a human question (command edit-eval-question)");

        evaluation.MapPost("/run", async (Guid id, EvaluationRunRequest request, AdminContext admin, CancellationToken ct) =>
                Results.Accepted(value: await admin.WriteAsync(async attempt =>
                {
                    var collection = await Collection(admin, id, attempt);
                    if (!await admin.Db.EvaluationSets.AsNoTracking().AnyAsync(s => s.CollectionId == id, attempt))
                    {
                        throw AdminFaults.Of(AdminErrorCodes.NoEvaluationSet);
                    }

                    var variants = request.Variants is { Count: > 0 } given ? given.Select(v => v.Value).Distinct().ToList() : [EvaluationVariant.Hybrid];
                    var job = new JobRequest(JobClasses.Eval, collection.DepartmentId, collection.Classification,
                        ContractJson.Serialize(new EvalOnDemandPayload { CollectionId = id, Variants = variants }))
                    {
                        DedupKey = JobDedupKeys.Eval(id),
                        TeamId = collection.OwnerTeamId,
                    };
                    var queued = await admin.Db.Jobs.AsNoTracking().Where(j => j.DedupKey == job.DedupKey && (j.State == JobStates.Queued || j.State == JobStates.Running))
                        .Select(j => (Guid?)j.Id).FirstOrDefaultAsync(attempt);
                    return new AdminChange<EvaluationRunStarted>(new EvaluationRunStarted(queued ?? job.Id, queued is not null), "evaluation-run", AuditEntityTypes.Collection, id,
                        null, new { variants = variants.Select(v => new VariantName(v)) }) { DepartmentId = collection.DepartmentId, Classification = collection.Classification, Jobs = queued is null ? [job] : [] };
                }, ct)))
            .RequireAuthorization(AdminPolicies.Edit).WithName("RunEvaluation").WithSummary("Run the evaluation now (eval on-demand; variants hybrid, vector, hybrid-no-decompounder)");

        var runs = api.MapGroup("/evaluation-runs").WithTags("Evaluation");

        runs.MapGet("/{runId:guid}", async (Guid runId, AdminContext admin, CancellationToken ct) =>
                EvaluationRunDto.Of(await RunAsync(admin, runId, ct)))
            .RequireAuthorization(AdminPolicies.Read).WithName("GetEvaluationRun").WithSummary("A run: recall@k and MRR with counts, compared run");

        runs.MapGet("/{runId:guid}/results", async (Guid runId, AdminContext admin, CancellationToken ct) =>
            {
                var run = await RunAsync(admin, runId, ct);
                var previous = run.ComparedRunId is { } compared
                    ? await admin.Db.EvaluationResults.AsNoTracking().Where(r => r.RunId == compared).ToDictionaryAsync(r => r.QuestionId, r => r.ExpectedRank, ct)
                    : [];
                var results = await (from r in admin.Db.EvaluationResults.AsNoTracking()
                                     join q in admin.Db.EvaluationQuestions.AsNoTracking() on r.QuestionId equals q.Id
                                     where r.RunId == runId
                                     orderby q.Question
                                     select new { r.QuestionId, q.Question, q.Layer, r.ExpectedRank, r.Hit, r.Coverage, r.BestDistance, r.BestKeywordScore })
                    .ToListAsync(ct);
                return results.Select(r => new EvaluationResultDto(r.QuestionId, r.Question, r.Layer, r.ExpectedRank, previous.GetValueOrDefault(r.QuestionId), r.Hit, r.Coverage,
                    r.BestDistance, r.BestKeywordScore)).ToList();
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("ListEvaluationResults").WithSummary("A run's per-question ranks with the compared run's rank");

        return api;
    }

    private static async Task<Collection> Collection(AdminContext admin, Guid id, CancellationToken ct) =>
        await CollectionEndpoints.Scoped(admin).AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);

    private static async Task<EvaluationRun> RunAsync(AdminContext admin, Guid runId, CancellationToken ct)
    {
        var collections = CollectionEndpoints.Scoped(admin).Select(c => c.Id);
        var sets = admin.Db.EvaluationSets.Where(s => collections.Contains(s.CollectionId)).Select(s => s.Id);
        return await admin.Db.EvaluationRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId && sets.Contains(r.SetId), ct)
            ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
    }

    /// <summary>
    /// The human layer through <c>edit-eval-question</c> (admin design §4.7; no dedup): an update or a retire names a question
    /// of the collection's set in the <c>human</c> layer; expected documents are live documents of the collection.
    /// </summary>
    private static async Task<IResult> EditAsync(Guid id, Guid questionId, QuestionAction action, EvaluationQuestionRequest? request, AdminContext admin, CancellationToken ct) =>
        Results.Accepted(value: await admin.WriteAsync(async attempt =>
        {
            var collection = await Collection(admin, id, attempt);
            if (action != QuestionAction.Create)
            {
                var sets = admin.Db.EvaluationSets.AsNoTracking().Where(s => s.CollectionId == id).Select(s => s.Id);
                var layer = await admin.Db.EvaluationQuestions.AsNoTracking().Where(q => q.Id == questionId && sets.Contains(q.SetId)).Select(q => q.Layer).SingleOrDefaultAsync(attempt)
                    ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                if (layer != EvaluationLayers.Human)
                {
                    throw AdminFaults.Of(AdminErrorCodes.QuestionNotHuman);
                }
            }

            // Commands carry no order: a second edit while one for the same question waits could apply first (#189 review).
            var key = questionId.ToString("D");
            if (await admin.Db.Database.SqlQuery<int>($"""
                    SELECT 1 AS "Value" FROM content."Jobs"
                    WHERE "Class" = 'command' AND "State" IN ('queued', 'running')
                      AND "Payload"::jsonb ->> 'kind' = 'edit-eval-question' AND "Payload"::jsonb ->> 'questionId' = {key}
                    """).AnyAsync(attempt))
            {
                throw AdminFaults.Of(AdminErrorCodes.QuestionEditPending);
            }

            var documents = request?.ExpectedDocumentIds.Distinct().ToList() ?? [];
            if (documents.Count > 0)
            {
                var sources = admin.Db.Sources.AsNoTracking().Where(s => s.CollectionId == id).Select(s => s.Id);
                var live = await admin.Db.Documents.AsNoTracking().CountAsync(d => documents.Contains(d.Id) && d.DeletedAt == null && sources.Contains(d.SourceId), attempt);
                if (live != documents.Count)
                {
                    throw AdminFaults.Field("expectedDocumentIds", AdminErrorCodes.InvalidValue);
                }
            }

            var command = new EditEvalQuestionCommand
            {
                Action = action, CollectionId = id, QuestionId = questionId, Question = request?.Question.Trim(), ExpectedAnswer = request?.ExpectedAnswer?.Trim(),
                ExpectedDocumentIds = documents,
            };
            var job = new JobRequest(JobClasses.Command, collection.DepartmentId, collection.Classification, ContractJson.Serialize(command)) { TeamId = collection.OwnerTeamId };
            return new AdminChange<EvaluationQuestionJob>(new EvaluationQuestionJob(job.Id, questionId), "evaluation-question." + action.ToString().ToLowerInvariant(),
                AuditEntityTypes.EvaluationQuestion, questionId, null, new { collectionId = id, command.Question, command.ExpectedAnswer, documents })
            {
                DepartmentId = collection.DepartmentId, Classification = collection.Classification, Jobs = [job],
            };
        }, ct));
}

public sealed record QuestionCount(string Layer, string State, int Count);

public sealed record EvaluationOverview(bool HasSet, IReadOnlyList<QuestionCount> Questions, IReadOnlyList<EvaluationRunDto> LatestRuns);

public sealed record EvaluationQuestionDto(Guid Id, string Layer, string State, string Question, string? ExpectedAnswer, IReadOnlyList<Guid> ExpectedDocumentIds,
    string? Origin, string? CreatedBy, DateTimeOffset? RetiredAt);

public sealed record EvaluationRunDto(
    Guid Id, string Kind, string Variant, int? QuestionCount, int K, int? Found, decimal? RecallAtK, decimal? Mrr, int? Wins, int? Losses, Guid? ComparedRunId,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt)
{
    public static EvaluationRunDto Of(EvaluationRun r) => new(r.Id, r.Kind, r.Variant, r.QuestionCount, r.K, r.Found, r.RecallAtK, r.Mrr, r.Wins, r.Losses,
        r.ComparedRunId, r.StartedAt, r.FinishedAt);
}

public sealed record EvaluationResultDto(Guid QuestionId, string Question, string Layer, int? Rank, int? PreviousRank, bool? Hit, string? Coverage, float? BestDistance,
    float? BestKeywordScore);

/// <summary>The command enqueued; the worker writes the question (poll <c>GET /api/jobs/{jobId}</c> or the questions).</summary>
public sealed record EvaluationQuestionJob(Guid JobId, Guid QuestionId);

/// <summary>The <c>eval</c> job: enqueued now, or the one already queued or running (<see cref="AlreadyQueued"/>).</summary>
public sealed record EvaluationRunStarted(Guid JobId, bool AlreadyQueued);

public sealed record EvaluationQuestionRequest : AdminRequest
{
    [Required, StringLength(500, MinimumLength = 1)]
    public string Question { get; init; } = "";

    [StringLength(1000)]
    public string? ExpectedAnswer { get; init; }

    /// <summary>Live documents of the collection a correct retrieval returns; empty = the question expects not found.</summary>
    [MaxLength(10)]
    public IReadOnlyList<Guid> ExpectedDocumentIds { get; init; } = [];
}

public sealed record EvaluationRunRequest : AdminRequest
{
    /// <summary>
    /// Default: <c>hybrid</c>. The P1a three-way gold-set run lists all three (item 21). The contract's names
    /// (<c>hybrid</c>, <c>vector</c>, <c>hybrid-no-decompounder</c>, as the job payload and <c>EvaluationRuns.Variant</c>);
    /// the enum names (<c>Hybrid</c>, …) are read too.
    /// </summary>
    [MaxLength(3)]
    public IReadOnlyList<VariantName>? Variants { get; init; }
}

/// <summary>An evaluation variant by the contract's kebab-case name (<see cref="EvaluationVariant"/>).</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(VariantNameConverter))]
public readonly record struct VariantName(EvaluationVariant Value);

/// <summary>Reads and writes <see cref="VariantName"/> as the contract's names, accepting the enum names on read.</summary>
public sealed class VariantNameConverter : System.Text.Json.Serialization.JsonConverter<VariantName>
{
    public override VariantName Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        var text = reader.TokenType == System.Text.Json.JsonTokenType.String ? reader.GetString() : null;
        var match = Enum.GetValues<EvaluationVariant>().Cast<EvaluationVariant?>().FirstOrDefault(v =>
            string.Equals(System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(v!.Value.ToString()), text, StringComparison.Ordinal) ||
            string.Equals(v!.Value.ToString(), text, StringComparison.OrdinalIgnoreCase));
        return match is { } value ? new VariantName(value) : throw new System.Text.Json.JsonException($"Unknown evaluation variant '{text}'.");
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, VariantName value, System.Text.Json.JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(System.Text.Json.JsonNamingPolicy.KebabCaseLower.ConvertName(value.Value.ToString()));
    }
}
