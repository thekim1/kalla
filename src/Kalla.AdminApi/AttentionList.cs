using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.Pipelines.Definitions;

namespace Kalla.AdminApi;

/// <summary>The kinds of <see cref="AttentionItem"/> (admin design §5.5.1's table).</summary>
public static class AttentionKinds
{
    /// <summary>A run held for the deletion decision (engine §8).</summary>
    public const string DeletionWaiting = "deletion_waiting";

    /// <summary>A retrieval report (after a publish, or nightly) 5 points or more worse than the run it compares with.</summary>
    public const string EvaluationWorse = "evaluation_worse";

    /// <summary>The nightly recall@10 5 points below its median of the last 28 days (processing §14.8).</summary>
    public const string RecallBelowMedian = "recall_below_median";

    /// <summary>Documents flagged for personal data in a <c>public</c> knowledge base.</summary>
    public const string PersonalDataPublic = "personal_data_public";

    /// <summary>A source whose latest run finished with remarks.</summary>
    public const string RunDegraded = "run_degraded";

    /// <summary>A source whose last 3 runs failed.</summary>
    public const string SourceFailing = "source_failing";

    /// <summary>A website source whose last <c>Settings.Crawl.IncompleteRunsAlert</c> (3) crawls were not complete traversals.</summary>
    public const string SourceIncomplete = "source_incomplete";

    /// <summary>An Open WebUI client whose feedback import failed 4 runs in a row (chat §14.3).</summary>
    public const string FeedbackImportFailing = "feedback_import_failing";

    /// <summary>The organisation or the model sync failing (§4.10).</summary>
    public const string SyncFailing = "sync_failing";

    /// <summary>An assistant's or knowledge base's gateway key that cannot be used for what the object does (§4.10, V39).</summary>
    public const string GatewayKey = "gateway_key";

    /// <summary>V32: a platform model setting whose model is no longer servable within the platform ceiling.</summary>
    public const string PlatformModel = "platform_model";

    /// <summary>A pipeline's published template version with a stage version no live worker runs (engine §4 rule 4).</summary>
    public const string TemplateUnsupported = "template_unsupported";

    /// <summary>An assistant's test run with fewer questions as expected than the previous version's (§7.4, B-57).</summary>
    public const string AssistantTestsWorse = "assistant_tests_worse";

    /// <summary>The nightly answer run's faithfulness or citation precision 5 points below its 28-day median (B-58).</summary>
    public const string AnswersBelowMedian = "answers_below_median";
}

/// <summary>What an <see cref="AttentionItem"/> is about; the admin UI links each to its page.</summary>
public static class AttentionObjects
{
    public const string Collection = "collection";
    public const string Source = "source";
    public const string Assistant = "assistant";
    public const string Client = "client";
    public const string Pipeline = "pipeline";
    public const string Setting = "setting";
    public const string Sync = "sync";
}

/// <summary>
/// One item of Home's <i>Needs attention</i> (admin design §5.5.1): its <see cref="Kind"/>, the object (type, id when it has
/// one, name), its department (null for a platform object), when it began where that is known, and the figures its text
/// names (<see cref="Details"/>, by kind).
/// </summary>
public sealed record AttentionItem(string Kind, string ObjectType, Guid? ObjectId, string ObjectName, Guid? DepartmentId, DateTimeOffset? Since,
    IReadOnlyDictionary<string, string> Details);

/// <summary>
/// <c>GET /api/overview</c>'s attention list (admin design §5.5.1, V32; B-59): one query per kind, each from data the admin
/// already has, within the caller's scope (the departments of their roles; platform objects, the syncs, the platform model
/// settings and the feedback import only for platform admins). Nothing is stored: an item is gone as soon as its cause is.
/// </summary>
public static class AttentionList
{
    /// <summary>"5 points" of a figure kept as 0–1.</summary>
    public const decimal Points = 0.05m;

    /// <summary>The window of the medians (processing §14.8).</summary>
    public static readonly TimeSpan MedianWindow = TimeSpan.FromDays(28);

    /// <summary>The earlier runs a median needs before it is compared with.</summary>
    public const int MedianMinRuns = 3;

    /// <summary><c>Settings.Crawl.IncompleteRunsAlert</c>'s default (the worker's <c>TraversalCompleteness.DefaultAlertThreshold</c>).</summary>
    public const int DefaultIncompleteRunsAlert = 3;

    /// <summary>Failed runs in a row that raise <see cref="AttentionKinds.SourceFailing"/>.</summary>
    public const int FailedRuns = 3;

    /// <summary>Consecutive failures of a sync that raise <see cref="AttentionKinds.SyncFailing"/> (15 minutes at the 5-minute interval).</summary>
    public const int SyncFailures = 3;

    public static async Task<IReadOnlyList<AttentionItem>> ComputeAsync(AdminContext admin, OrgSyncStatus organisation, GatewayModelSyncStatus models, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(organisation);
        ArgumentNullException.ThrowIfNull(models);
        List<AttentionItem> items = [];
        items.AddRange(await DeletionWaitingAsync(admin, ct));
        items.AddRange(await EvaluationAsync(admin, ct));
        items.AddRange(await PersonalDataPublicAsync(admin, ct));
        items.AddRange(await SourceRunsAsync(admin, ct));
        items.AddRange(await SourceIncompleteAsync(admin, ct));
        items.AddRange(await GatewayKeysAsync(admin, ct));
        items.AddRange(await TemplatesAsync(admin, ct));
        items.AddRange(await AssistantTestsAsync(admin, ct));
        items.AddRange(await AnswersAsync(admin, ct));
        if (admin.IsPlatformAdmin)
        {
            items.AddRange(await FeedbackImportAsync(admin, ct));
            items.AddRange(Syncs(organisation, models));
            items.AddRange(await PlatformModelsAsync(admin, ct));
        }

        return items;
    }

    /// <summary>The figures of an item as invariant text; a decimal without trailing zeros (a <c>numeric(5,4)</c> reads as 0.9000).</summary>
    private static Dictionary<string, string> Details(params (string Key, object? Value)[] values) =>
        values.Where(v => v.Value is not null).ToDictionary(v => v.Key, v => v.Value is decimal d
            ? d.ToString("0.0###", CultureInfo.InvariantCulture)
            : Convert.ToString(v.Value, CultureInfo.InvariantCulture)!, StringComparer.Ordinal);

    private static IQueryable<Guid> DepartmentIds(AdminContext admin) => admin.Departments().Select(d => d.Id);

    /// <summary>The sources of the knowledge bases in scope, not deleted, with their knowledge base.</summary>
    private static IQueryable<ScopedSource> Sources(AdminContext admin) =>
        from s in admin.Db.Sources.AsNoTracking()
        join c in CollectionEndpoints.Scoped(admin).AsNoTracking() on s.CollectionId equals c.Id
        where s.DeletedAt == null
        select new ScopedSource { Id = s.Id, Name = s.Name, CollectionId = c.Id, CollectionName = c.Name, DepartmentId = c.DepartmentId };

    /// <summary>Set by an object initializer, so EF can compose further queries on its members.</summary>
    private sealed class ScopedSource
    {
        public Guid Id { get; init; }
        public required string Name { get; init; }
        public Guid CollectionId { get; init; }
        public required string CollectionName { get; init; }
        public Guid DepartmentId { get; init; }
    }

    private static async Task<List<AttentionItem>> DeletionWaitingAsync(AdminContext admin, CancellationToken ct)
    {
        var rows = await (from r in admin.Db.Runs.AsNoTracking()
                          join s in Sources(admin) on r.SourceId equals s.Id
                          where r.State == RunStates.AwaitingDeletionConfirmation
                          orderby r.StartedAt
                          select new { RunId = r.Id, r.StartedAt, r.ProposedDeletions, Source = s }).ToListAsync(ct);
        return
        [
            .. rows.Select(r => new AttentionItem(AttentionKinds.DeletionWaiting, AttentionObjects.Source, r.Source.Id, r.Source.Name, r.Source.DepartmentId, r.StartedAt,
                Details(("runId", r.RunId), ("collectionId", r.Source.CollectionId), ("collectionName", r.Source.CollectionName), ("proposed", r.ProposedDeletions)))),
        ];
    }

    /// <summary>
    /// The retrieval reports (worker runs over a knowledge base's set, the saved settings): the latest finished one per set
    /// 5 points or more below the run it compares with, and the latest nightly 5 points below the median of the nightly runs
    /// of the 28 days before it (at least <see cref="MedianMinRuns"/> of them).
    /// </summary>
    private static async Task<List<AttentionItem>> EvaluationAsync(AdminContext admin, CancellationToken ct)
    {
        var collections = CollectionEndpoints.Scoped(admin).AsNoTracking();
        var since = admin.Now - MedianWindow - TimeSpan.FromDays(1);
        var runs = await (from r in admin.Db.EvaluationRuns.AsNoTracking()
                          join s in admin.Db.EvaluationSets.AsNoTracking() on r.SetId equals s.Id
                          join c in collections on s.CollectionId equals c.Id
                          where (r.Kind == EvaluationRunKinds.PublishReport || r.Kind == EvaluationRunKinds.Nightly) && r.FinishedAt != null && !r.Candidate
                                && r.Variant == EvaluationVariants.Hybrid && r.RecallAtK != null && r.StartedAt >= since
                          select new { r.Id, r.SetId, r.Kind, r.StartedAt, r.RecallAtK, r.ComparedRunId, CollectionId = c.Id, CollectionName = c.Name, c.DepartmentId })
            .ToListAsync(ct);
        var latestCompared = runs.GroupBy(r => r.SetId).Select(g => g.MaxBy(r => r.StartedAt)!).Where(r => r.ComparedRunId is not null).ToList();
        var comparedIds = latestCompared.Select(r => r.ComparedRunId!.Value).ToList();
        var compared = await admin.Db.EvaluationRuns.AsNoTracking().Where(r => comparedIds.Contains(r.Id) && r.RecallAtK != null)
            .ToDictionaryAsync(r => r.Id, r => r.RecallAtK!.Value, ct);
        List<AttentionItem> items = [];
        foreach (var run in latestCompared)
        {
            if (compared.TryGetValue(run.ComparedRunId!.Value, out var before) && run.RecallAtK!.Value <= before - Points)
            {
                items.Add(new AttentionItem(AttentionKinds.EvaluationWorse, AttentionObjects.Collection, run.CollectionId, run.CollectionName, run.DepartmentId, run.StartedAt,
                    Details(("runId", run.Id), ("runKind", run.Kind), ("recall", run.RecallAtK), ("before", before), ("points", PointsOf(before - run.RecallAtK.Value)))));
            }
        }

        foreach (var set in runs.Where(r => r.Kind == EvaluationRunKinds.Nightly).GroupBy(r => r.SetId))
        {
            var latest = set.MaxBy(r => r.StartedAt)!;
            var earlier = set.Where(r => r.Id != latest.Id && r.StartedAt >= latest.StartedAt - MedianWindow).Select(r => r.RecallAtK!.Value).ToList();
            if (earlier.Count >= MedianMinRuns && Median(earlier) is var median && latest.RecallAtK!.Value < median - Points)
            {
                items.Add(new AttentionItem(AttentionKinds.RecallBelowMedian, AttentionObjects.Collection, latest.CollectionId, latest.CollectionName, latest.DepartmentId,
                    latest.StartedAt, Details(("runId", latest.Id), ("recall", latest.RecallAtK), ("median", median), ("points", PointsOf(median - latest.RecallAtK.Value)))));
            }
        }

        return items;
    }

    /// <summary>Documents with a flagged, not cleared chunk set (held, or the live one) in each <c>public</c> knowledge base in scope.</summary>
    private static async Task<List<AttentionItem>> PersonalDataPublicAsync(AdminContext admin, CancellationToken ct)
    {
        var db = admin.Db;
        var rows = await (from d in db.Documents.AsNoTracking()
                          join src in db.Sources.AsNoTracking() on d.SourceId equals src.Id
                          join c in CollectionEndpoints.Scoped(admin).AsNoTracking() on src.CollectionId equals c.Id
                          where c.Classification == Classification.Public && d.DeletedAt == null && d.PiiClearedAt == null
                                && db.ChunkSets.Any(cs => cs.DocumentId == d.Id && cs.PiiFlagged
                                                          && (cs.State == ChunkSetStates.Held || (cs.State == ChunkSetStates.Live && cs.Id == d.LiveChunkSetId)))
                          group d by new { c.Id, c.Name, c.DepartmentId } into g
                          select new { g.Key.Id, g.Key.Name, g.Key.DepartmentId, Count = g.Count() }).ToListAsync(ct);
        return [.. rows.Select(r => new AttentionItem(AttentionKinds.PersonalDataPublic, AttentionObjects.Collection, r.Id, r.Name, r.DepartmentId, null, Details(("documents", r.Count))))];
    }

    /// <summary>Per source in scope: its latest finished run degraded, or its last <see cref="FailedRuns"/> finished runs failed.</summary>
    private static async Task<List<AttentionItem>> SourceRunsAsync(AdminContext admin, CancellationToken ct)
    {
        var finished = new[] { RunStates.Succeeded, RunStates.Degraded, RunStates.Failed };
        var recent = await (from s in Sources(admin)
                            from r in admin.Db.Runs.AsNoTracking().Where(r => r.SourceId == s.Id && finished.Contains(r.State))
                                .OrderByDescending(r => r.StartedAt).Take(FailedRuns)
                            select new { Source = s, RunId = r.Id, r.State, r.StartedAt }).ToListAsync(ct);
        List<AttentionItem> items = [];
        foreach (var source in recent.GroupBy(r => r.Source.Id))
        {
            var runs = source.OrderByDescending(r => r.StartedAt).ToList();
            var s = runs[0].Source;
            if (runs.Count == FailedRuns && runs.All(r => r.State == RunStates.Failed))
            {
                items.Add(new AttentionItem(AttentionKinds.SourceFailing, AttentionObjects.Source, s.Id, s.Name, s.DepartmentId, runs[^1].StartedAt,
                    Details(("runId", runs[0].RunId), ("collectionId", s.CollectionId), ("collectionName", s.CollectionName), ("failed", FailedRuns))));
            }
            else if (runs[0].State == RunStates.Degraded)
            {
                items.Add(new AttentionItem(AttentionKinds.RunDegraded, AttentionObjects.Source, s.Id, s.Name, s.DepartmentId, runs[0].StartedAt,
                    Details(("runId", runs[0].RunId), ("collectionId", s.CollectionId), ("collectionName", s.CollectionName))));
            }
        }

        return items;
    }

    /// <summary>Website sources whose last <c>Settings.Crawl.IncompleteRunsAlert</c> full crawls were all incomplete traversals.</summary>
    private static async Task<List<AttentionItem>> SourceIncompleteAsync(AdminContext admin, CancellationToken ct)
    {
        var crawl = await admin.Db.Settings.AsNoTracking().Select(s => s.Crawl).FirstOrDefaultAsync(ct);
        // As the worker reads it (PlatformCrawlSettings): the member in any case, a value of 0 or less meaning the default.
        var threshold = IntOf(crawl, "incompleteRunsAlert") is int alert and > 0 ? alert : DefaultIncompleteRunsAlert;

        var recent = await (from s in Sources(admin)
                            from c in (from c in admin.Db.CrawlRuns.AsNoTracking()
                                       join r in admin.Db.Runs.AsNoTracking() on c.RunId equals r.Id
                                       where c.SourceId == s.Id && !c.Incremental && r.State != RunStates.Running
                                       orderby r.StartedAt descending
                                       select new { c.Complete, r.StartedAt, RunId = r.Id }).Take(threshold)
                            select new { Source = s, c.Complete, c.StartedAt, c.RunId }).ToListAsync(ct);
        return
        [
            .. recent.GroupBy(r => r.Source.Id).Where(g => g.Count() == threshold && g.All(r => !r.Complete)).Select(g =>
            {
                var s = g.First().Source;
                var latest = g.MaxBy(r => r.StartedAt)!;
                return new AttentionItem(AttentionKinds.SourceIncomplete, AttentionObjects.Source, s.Id, s.Name, s.DepartmentId, g.Min(r => r.StartedAt),
                    Details(("runId", latest.RunId), ("collectionId", s.CollectionId), ("collectionName", s.CollectionName), ("runs", threshold)));
            }),
        ];
    }

    /// <summary>
    /// The gateway key item (§5.5.1), as V39 checks a key on save: each active assistant and knowledge base in scope whose
    /// key is not in the mirror, belongs to another department, is not active, has an inactive owner or is not usable by
    /// Källa; whose model list no longer holds the model the object answers or embeds with; or whose residencies are all
    /// above the object's ceiling (an assistant's: the strictest of its level and its history-storing clients', as V39).
    /// </summary>
    private static async Task<List<AttentionItem>> GatewayKeysAsync(AdminContext admin, CancellationToken ct)
    {
        var db = admin.Db;
        var departments = DepartmentIds(admin);
        var assistantRows = await (from a in db.Assistants.AsNoTracking()
                                   join v in db.AssistantVersions.AsNoTracking() on a.PublishedVersionId ?? a.DraftVersionId equals (Guid?)v.Id into versions
                                   from v in versions.DefaultIfEmpty()
                                   where a.State == AssistantStates.Active && a.DeletedAt == null && a.GatewayKeyId != null && departments.Contains(a.DepartmentId)
                                   select new { Assistant = a, Model = v == null ? null : v.GatewayModel }).ToListAsync(ct);
        var assistantIds = assistantRows.Select(r => r.Assistant.Id).ToList();
        var audiences = (await (from x in db.AssistantAudiences.AsNoTracking()
                                join c in db.Clients.AsNoTracking() on x.SubjectId equals c.Id
                                where assistantIds.Contains(x.AssistantId) && x.Kind == AssistantAudienceKinds.Client
                                select new { x.AssistantId, Client = c }).ToListAsync(ct))
            .ToLookup(x => x.AssistantId, x => x.Client);
        var assistants = assistantRows.Select(r => new KeyUser(AttentionObjects.Assistant, r.Assistant.Id, r.Assistant.Name, r.Assistant.DepartmentId,
            AssistantRules.Ceiling(r.Assistant, [.. audiences[r.Assistant.Id]]), r.Assistant.GatewayKeyId!.Value, r.Model));
        var collections = (await (from c in CollectionEndpoints.Scoped(admin).AsNoTracking()
                                  join p in db.EmbeddingProfiles.AsNoTracking() on c.EmbeddingProfileId equals p.Id
                                  where c.GatewayKeyId != null
                                  select new { c.Id, c.Name, c.DepartmentId, c.Classification, KeyId = c.GatewayKeyId!.Value, p.GatewayModel }).ToListAsync(ct))
            .Select(c => new KeyUser(AttentionObjects.Collection, c.Id, c.Name, c.DepartmentId, ResidencyCeiling.ForLevel(c.Classification), c.KeyId, c.GatewayModel));
        var users = assistants.Concat(collections).ToList();
        var keyIds = users.Select(u => u.KeyId).Distinct().ToList();
        var keys = await db.GatewayKeys.AsNoTracking().Where(k => keyIds.Contains(k.Id)).ToDictionaryAsync(k => k.Id, ct);
        List<AttentionItem> items = [];
        foreach (var user in users)
        {
            var key = keys.GetValueOrDefault(user.KeyId);
            var (reason, model) = KeyProblem(user, key);
            if (reason is not null)
            {
                items.Add(new AttentionItem(AttentionKinds.GatewayKey, user.ObjectType, user.Id, user.Name, user.DepartmentId, null,
                    Details(("reason", reason), ("keyId", user.KeyId), ("keyName", key?.Name), ("model", model))));
            }
        }

        return items;
    }

    /// <summary>Why <paramref name="key"/> cannot serve <paramref name="user"/> (V39), with the lost model; (null, null) when it can.</summary>
    private static (string? Reason, string? Model) KeyProblem(KeyUser user, GatewayKey? key)
    {
        if (key is null)
        {
            return ("unknown", null);
        }

        if (key.DepartmentId != user.DepartmentId)
        {
            return ("other_department", null);
        }

        if (GatewayKeyRules.UnusableReason(key) is { } unusable)
        {
            return (KeyReasonCode(unusable), null);
        }

        if (user.Model is { Length: > 0 } model && key.AllowedModels.Length > 0 && !key.AllowedModels.Contains(model, StringComparer.Ordinal))
        {
            return ("model_lost", model);
        }

        return key.AllowedResidencies.Length > 0 && !key.AllowedResidencies.Any(r => ResidencyCeiling.TryParse(r, out var residency) && residency <= user.Ceiling)
            ? ("residency_lost", null)
            : (null, null);
    }

    /// <summary>V39's status reason as the item's code: <c>unknown</c>, <c>not_active</c>, <c>owner_inactive</c>, <c>not_usable</c>.</summary>
    private static string KeyReasonCode(string reason) => reason switch
    {
        GatewayKeyRules.Reasons.NotActive => "not_active",
        GatewayKeyRules.Reasons.OwnerInactive => "owner_inactive",
        GatewayKeyRules.Reasons.NotUsable => "not_usable",
        _ => "unknown",
    };

    private sealed record KeyUser(string ObjectType, Guid Id, string Name, Guid DepartmentId, Residency Ceiling, Guid KeyId, string? Model);

    /// <summary>
    /// Each pipeline in scope (a department's own; a platform pipeline for platform admins) whose published version a source
    /// in scope uses and that the live workers cannot run (a stage version none of them supports).
    /// </summary>
    private static async Task<List<AttentionItem>> TemplatesAsync(AdminContext admin, CancellationToken ct)
    {
        var db = admin.Db;
        var departments = DepartmentIds(admin);
        var sourcePipelines = Sources(admin).Join(db.Sources.AsNoTracking(), s => s.Id, x => x.Id, (s, x) => x.PipelineId);
        var pipelines = await (from p in db.Pipelines.AsNoTracking()
                               join v in db.PipelineVersions.AsNoTracking() on p.PublishedVersionId equals (Guid?)v.Id
                               where sourcePipelines.Contains(p.Id) && (p.DepartmentId == null ? admin.IsPlatformAdmin : departments.Contains(p.DepartmentId.Value))
                               select new { p.Id, p.Name, p.TemplateId, p.DepartmentId, v.Number, v.Definition }).ToListAsync(ct);
        if (pipelines.Count == 0)
        {
            return [];
        }

        var workers = await PipelineEndpoints.LiveWorkersAsync(admin, ct);
        if (workers.Count == 0)
        {
            // No worker at all (restarting, or not started yet): Health shows that; every pipeline would read as unsupported.
            return [];
        }

        var validator = new PipelineValidator(TemplateCatalog.BuiltIn);
        List<AttentionItem> items = [];
        foreach (var p in pipelines)
        {
            var sourceType = TemplateCatalog.BuiltIn.All.FirstOrDefault(t => t.Template.Name == p.TemplateId)?.Template.SourceType;
            using var definition = JsonDocument.Parse(p.Definition);
            var result = validator.ValidateForPublish(definition.RootElement, sourceType, workers, admin.Now);
            if (result.Errors.Any(e => e.Code == PipelineValidationCodes.StageVersionUnsupported))
            {
                var version = definition.RootElement.TryGetProperty("templateVersion", out var t) && t.TryGetInt32(out var n) ? n : (int?)null;
                items.Add(new AttentionItem(AttentionKinds.TemplateUnsupported, AttentionObjects.Pipeline, p.Id, p.Name, p.DepartmentId, null,
                    Details(("template", p.TemplateId), ("templateVersion", version), ("version", p.Number))));
            }
        }

        return items;
    }

    /// <summary>Published assistants in scope whose published version's test run has fewer questions as expected than the run it compares with.</summary>
    private static async Task<List<AttentionItem>> AssistantTestsAsync(AdminContext admin, CancellationToken ct)
    {
        var db = admin.Db;
        var departments = DepartmentIds(admin);
        var rows = await (from a in db.Assistants.AsNoTracking()
                          join v in db.AssistantVersions.AsNoTracking() on a.PublishedVersionId equals (Guid?)v.Id
                          join r in db.EvaluationRuns.AsNoTracking() on v.TestRunId equals (Guid?)r.Id
                          join p in db.EvaluationRuns.AsNoTracking() on r.ComparedRunId equals (Guid?)p.Id
                          where a.State == AssistantStates.Active && a.DeletedAt == null && departments.Contains(a.DepartmentId)
                                && r.FinishedAt != null && p.FinishedAt != null && r.Found < p.Found
                          select new { a.Id, a.Name, a.DepartmentId, RunId = r.Id, r.StartedAt, r.SubjectVersion, r.Found, r.QuestionCount, Before = p.Found, BeforeVersion = p.SubjectVersion })
            .ToListAsync(ct);
        return
        [
            .. rows.Select(r => new AttentionItem(AttentionKinds.AssistantTestsWorse, AttentionObjects.Assistant, r.Id, r.Name, r.DepartmentId, r.StartedAt,
                Details(("runId", r.RunId), ("version", r.SubjectVersion), ("asExpected", r.Found), ("questions", r.QuestionCount), ("before", r.Before),
                    ("beforeVersion", r.BeforeVersion)))),
        ];
    }

    /// <summary>
    /// Published assistants in scope whose latest finished nightly answer run has faithfulness or citation precision 5 points
    /// below its median over the finished runs of the 28 days before it (at least <see cref="MedianMinRuns"/> of them).
    /// </summary>
    private static async Task<List<AttentionItem>> AnswersAsync(AdminContext admin, CancellationToken ct)
    {
        var db = admin.Db;
        var departments = DepartmentIds(admin);
        var since = admin.Now - MedianWindow - TimeSpan.FromDays(1);
        var runs = await (from r in db.EvaluationRuns.AsNoTracking()
                          join a in db.Assistants.AsNoTracking() on r.SubjectId equals (Guid?)a.Id
                          where r.Kind == EvaluationRunKinds.Answers && r.FinishedAt != null && r.StartedAt >= since
                                && a.State == AssistantStates.Active && a.DeletedAt == null && a.PublishedVersionId != null && departments.Contains(a.DepartmentId)
                          select new { r.Id, r.StartedAt, r.Faithfulness, r.CitationPrecision, AssistantId = a.Id, a.Name, a.DepartmentId }).ToListAsync(ct);
        List<AttentionItem> items = [];
        foreach (var assistant in runs.GroupBy(r => r.AssistantId))
        {
            var latest = assistant.MaxBy(r => r.StartedAt)!;
            var earlier = assistant.Where(r => r.Id != latest.Id && r.StartedAt >= latest.StartedAt - MedianWindow).ToList();
            foreach (var (metric, pick) in new (string, Func<decimal?, decimal?, decimal?>)[] { ("faithfulness", (f, _) => f), ("citationPrecision", (_, c) => c) })
            {
                var value = pick(latest.Faithfulness, latest.CitationPrecision);
                var history = earlier.Select(r => pick(r.Faithfulness, r.CitationPrecision)).OfType<decimal>().ToList();
                if (value is { } now && history.Count >= MedianMinRuns && Median(history) is var median && now < median - Points)
                {
                    items.Add(new AttentionItem(AttentionKinds.AnswersBelowMedian, AttentionObjects.Assistant, latest.AssistantId, latest.Name, latest.DepartmentId,
                        latest.StartedAt, Details(("runId", latest.Id), ("metric", metric), ("value", now), ("median", median), ("points", PointsOf(median - now)))));
                    break;
                }
            }
        }

        return items;
    }

    /// <summary>Open WebUI clients whose feedback import failed <see cref="FeedbackImportState.AlertAfterFailedRuns"/> runs in a row.</summary>
    private static async Task<List<AttentionItem>> FeedbackImportAsync(AdminContext admin, CancellationToken ct)
    {
        var rows = await (from f in admin.Db.FeedbackImportState.AsNoTracking()
                          join c in admin.Db.Clients.AsNoTracking() on f.ClientId equals c.Id
                          where f.FailedRuns >= FeedbackImportState.AlertAfterFailedRuns
                          select new { c.Id, c.Name, f.FailedRuns, f.LastRunAt, f.LastOutcome }).ToListAsync(ct);
        return
        [
            .. rows.Select(r => new AttentionItem(AttentionKinds.FeedbackImportFailing, AttentionObjects.Client, r.Id, r.Name, null, r.LastRunAt,
                Details(("failed", r.FailedRuns), ("outcome", r.LastOutcome)))),
        ];
    }

    /// <summary>The organisation and model syncs, each failing <see cref="SyncFailures"/> times in a row or more.</summary>
    public static IEnumerable<AttentionItem> Syncs(OrgSyncStatus organisation, GatewayModelSyncStatus models)
    {
        if (organisation.Enabled && organisation.ConsecutiveFailures >= SyncFailures)
        {
            yield return new AttentionItem(AttentionKinds.SyncFailing, AttentionObjects.Sync, null, "organisation", null, organisation.LastSuccessAt,
                Details(("failures", organisation.ConsecutiveFailures)));
        }

        if (models.Enabled && models.ConsecutiveFailures >= SyncFailures)
        {
            yield return new AttentionItem(AttentionKinds.SyncFailing, AttentionObjects.Sync, null, "models", null, models.LastSuccessAt,
                Details(("failures", models.ConsecutiveFailures)));
        }
    }

    /// <summary>V32's Overview warning: each set platform model setting whose mirror row is gone, of another kind, or no longer within the platform ceiling.</summary>
    private static async Task<List<AttentionItem>> PlatformModelsAsync(AdminContext admin, CancellationToken ct)
    {
        var settings = await admin.Db.Settings.AsNoTracking().Select(s => new { s.EvaluationModel, s.EvaluationJudgeModel, s.Chat, s.Retrieval })
            .FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            return [];
        }

        (string Setting, string? Model, string Kind)[] set =
        [
            ("Settings.EvaluationModel", settings.EvaluationModel, GatewayModelModelKinds.Chat),
            ("Settings.EvaluationJudgeModel", settings.EvaluationJudgeModel, GatewayModelModelKinds.Chat),
            ("Settings.Chat.CondenseModel", StringOf(settings.Chat, "condenseModel"), GatewayModelModelKinds.Chat),
            ("Settings.Retrieval.rerankModel", StringOf(settings.Retrieval, "rerankModel"), GatewayModelModelKinds.Chat),
            ("Settings.Retrieval.gatewayRerankModel", StringOf(settings.Retrieval, "gatewayRerankModel"), GatewayModelModelKinds.Rerank),
        ];
        var names = set.Select(s => s.Model).OfType<string>().Where(m => m.Length > 0).Distinct().ToList();
        if (names.Count == 0)
        {
            return [];
        }

        var rows = await admin.Db.GatewayModels.AsNoTracking().Where(m => names.Contains(m.Name)).ToDictionaryAsync(m => m.Name, ct);
        return
        [
            .. set.Where(s => s.Model is { Length: > 0 }).Where(s => !rows.TryGetValue(s.Model!, out var row) || row.ModelKind != s.Kind
                    || !row.Residencies.Any(r => ResidencyCeiling.TryParse(r, out var residency) && residency <= PlatformModelRules.Ceiling))
                .Select(s => new AttentionItem(AttentionKinds.PlatformModel, AttentionObjects.Setting, null, s.Setting, null, null,
                    Details(("model", s.Model), ("ceiling", ResidencyCeiling.ToHeader(PlatformModelRules.Ceiling))))),
        ];
    }

    /// <summary>The median of <paramref name="values"/> (the mean of the middle two for an even count).</summary>
    public static decimal Median(IReadOnlyCollection<decimal> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>A 0–1 difference as points, one decimal.</summary>
    private static decimal PointsOf(decimal difference) => Math.Round(difference * 100, 1, MidpointRounding.AwayFromZero);

    private static string? StringOf(string? json, string member)
    {
        try
        {
            return (string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject)?[member] is JsonValue v && v.GetValueKind() == JsonValueKind.String
                && v.GetValue<string>() is { Length: > 0 } value ? value : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A member of a JSON object read in any case, as the worker's <c>JsonSerializerOptions.Web</c> reads it.</summary>
    private static int? IntOf(string? json, string member)
    {
        try
        {
            var value = (string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject)?
                .FirstOrDefault(p => string.Equals(p.Key, member, StringComparison.OrdinalIgnoreCase)).Value;
            return value is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
