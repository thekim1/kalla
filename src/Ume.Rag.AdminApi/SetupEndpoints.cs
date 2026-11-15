using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Pipelines.Jobs;

namespace Ume.Rag.AdminApi;

/// <summary>
/// The setup checklist "Kom igång" (admin design §5.4, §5.5.2): the pilot journey's steps per collection, computed from
/// state (no stored progress flags). A collection's checklist is done once an assistant whose published version links it
/// has a client audience (it is in Open WebUI). <c>GET /api/overview</c> is Home (§5.5.1, "P1's Home"): the caller's
/// published assistants over the last 7 days (Live), the runs, queue and syncs (Health), and the collections whose
/// checklist is not done yet (the attention items join it in P2).
/// </summary>
public static class SetupEndpoints
{
    /// <summary>The newest collections the overview computes a checklist for.</summary>
    public const int OverviewCollections = 50;

    /// <summary>Live's period: the last 7 Europe/Stockholm days, today included.</summary>
    public const int LiveDays = 7;

    /// <summary>Health's period: the runs started in the last 24 hours.</summary>
    public static readonly TimeSpan RunWindow = TimeSpan.FromHours(24);

    public static RouteGroupBuilder MapSetup(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/collections/{id:guid}/setup", async (Guid id, AdminContext admin, CancellationToken ct) =>
            {
                var collection = await CollectionEndpoints.Scoped(admin).AsNoTracking().Where(c => c.Id == id)
                    .Select(c => new CollectionRef(c.Id, c.Name, c.PublishedAt, c.DepartmentId)).SingleOrDefaultAsync(ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                return (await ComputeAsync(admin, [collection], ct)).Single();
            })
            .RequireAuthorization(AdminPolicies.Read).WithTags("Collections").WithName("GetCollectionSetup")
            .WithSummary("The collection's setup checklist (Kom igång), computed from state");

        api.MapGet("/overview", async (AdminContext admin, OrganisationSync organisation, GatewayModelSync models, CancellationToken ct) =>
            {
                var collections = await CollectionEndpoints.Scoped(admin).AsNoTracking().OrderByDescending(c => c.UpdatedAt).ThenBy(c => c.Id).Take(OverviewCollections)
                    .Select(c => new CollectionRef(c.Id, c.Name, c.PublishedAt, c.DepartmentId)).ToListAsync(ct);
                var setups = await ComputeAsync(admin, collections, ct);
                // The platform's first-run step (Decisions "Deploy configuration"): no knowledge base can be created before it.
                var needsProfile = admin.IsPlatformAdmin
                    && !await admin.Db.EmbeddingProfiles.AsNoTracking().AnyAsync(p => p.IsDefault && p.State == EmbeddingProfileStates.Active, ct);
                return new Overview(collections.Count > 0, [.. setups.Where(s => !s.Done)], needsProfile, await LiveAsync(admin, ct),
                    await HealthAsync(admin, organisation.Status, models.Status, ct));
            })
            .RequireAuthorization(AdminPolicies.Read).WithTags("Overview").WithName("GetOverview")
            .WithSummary("Home: the caller's published assistants over the last 7 days, the runs, queue and syncs, the setup checklists not done yet, and for a platform admin whether the embedding profile is still to be created");

        return api;
    }

    private sealed record CollectionRef(Guid Id, string Name, DateTimeOffset? PublishedAt, Guid DepartmentId);

    /// <summary>
    /// Live (§5.5.1 item 1): every published, active assistant in the caller's departments with its last <see cref="LiveDays"/>
    /// days of <c>AssistantDailyTotals</c> and <c>AssistantDailyUsage</c> (the gateway's cost plus the estimate, as §8.6), summed
    /// over every client; the rows' own department in scope, as the feedback totals and the cost report. Most questions first.
    /// </summary>
    private static async Task<List<LiveAssistant>> LiveAsync(AdminContext admin, CancellationToken ct)
    {
        var db = admin.Db;
        var departments = admin.Departments().Select(d => d.Id);
        var published = await (from a in db.Assistants.AsNoTracking()
                               join v in db.AssistantVersions.AsNoTracking() on a.PublishedVersionId equals (Guid?)v.Id
                               where a.State == AssistantStates.Active && departments.Contains(a.DepartmentId)
                               select new { a.Id, a.Name, a.DepartmentId, v.Number }).ToListAsync(ct);
        if (published.Count == 0)
        {
            return [];
        }

        Guid[] ids = [.. published.Select(a => a.Id)];
        var to = CostEndpoints.Today(admin);
        var from = to.AddDays(1 - LiveDays);
        var totals = await db.AssistantDailyTotals.AsNoTracking()
            .Where(t => ids.Contains(t.AssistantId) && departments.Contains(t.DepartmentId) && t.Day >= from && t.Day <= to)
            .GroupBy(t => t.AssistantId)
            .Select(g => new { g.Key, Questions = g.Sum(t => t.Messages), NotFound = g.Sum(t => t.NotFound), ThumbsUp = g.Sum(t => t.ThumbsUp), ThumbsDown = g.Sum(t => t.ThumbsDown) })
            .ToDictionaryAsync(x => x.Key, ct);
        var costs = await db.AssistantDailyUsage.AsNoTracking()
            .Where(u => ids.Contains(u.AssistantId) && departments.Contains(u.DepartmentId) && u.Day >= from && u.Day <= to)
            .GroupBy(u => u.AssistantId).Select(g => new { g.Key, Cost = g.Sum(u => u.CostUsd + u.EstimatedCostUsd) })
            .ToDictionaryAsync(x => x.Key, x => x.Cost, ct);

        return
        [
            .. published.Select(a =>
                {
                    var t = totals.GetValueOrDefault(a.Id);
                    return new LiveAssistant(a.Id, a.Name, a.DepartmentId, a.Number, t?.Questions ?? 0, t?.NotFound ?? 0, t?.ThumbsUp ?? 0, t?.ThumbsDown ?? 0,
                        costs.GetValueOrDefault(a.Id));
                })
                .OrderByDescending(l => l.Questions).ThenBy(l => l.Name, StringComparer.Ordinal).ThenBy(l => l.AssistantId),
        ];
    }

    /// <summary>
    /// Health (§5.5.1 item 2): per department in scope, its collections' runs started within <see cref="RunWindow"/> by
    /// outcome (departments without one are left out; a run held for deletion confirmation counts as with remarks); the
    /// queued jobs of the caller's departments (a platform admin's: all) and the oldest one's enqueue time; and the
    /// organisation and model syncs' last success and consecutive failures.
    /// </summary>
    private static async Task<OverviewHealth> HealthAsync(AdminContext admin, OrgSyncStatus organisation, GatewayModelSyncStatus models, CancellationToken ct)
    {
        var db = admin.Db;
        var since = admin.Now - RunWindow;
        var runs = await (from r in db.Runs.AsNoTracking()
                          join s in db.Sources.AsNoTracking() on r.SourceId equals s.Id
                          join c in CollectionEndpoints.Scoped(admin).AsNoTracking() on s.CollectionId equals c.Id
                          join d in db.Departments.AsNoTracking() on c.DepartmentId equals d.Id
                          where r.StartedAt >= since && s.DeletedAt == null
                          group r by new { d.Id, d.Name } into g
                          select new
                          {
                              g.Key.Id, g.Key.Name,
                              Succeeded = g.Count(r => r.State == RunStates.Succeeded),
                              Degraded = g.Count(r => r.State == RunStates.Degraded || r.State == RunStates.AwaitingDeletionConfirmation),
                              Failed = g.Count(r => r.State == RunStates.Failed),
                              Running = g.Count(r => r.State == RunStates.Running),
                          }).OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
        var departments = admin.Departments().Select(d => d.Id);
        var everyJob = admin.IsPlatformAdmin;
        var queue = await db.Jobs.AsNoTracking().Where(j => j.State == JobStates.Queued && (everyJob || departments.Contains(j.DepartmentId)))
            .GroupBy(j => 1).Select(g => new QueueHealth(g.Count(), g.Min(j => (DateTimeOffset?)j.EnqueuedAt))).SingleOrDefaultAsync(ct) ?? new QueueHealth(0, null);
        return new OverviewHealth(
            [.. runs.Select(r => new DepartmentRuns(r.Id, r.Name, new RunCounts(r.Succeeded, r.Degraded, r.Failed, r.Running)))],
            queue, new SyncHealth(organisation.LastSuccessAt, organisation.ConsecutiveFailures), new SyncHealth(models.LastSuccessAt, models.ConsecutiveFailures));
    }

    /// <summary>A handful of grouped queries for all <paramref name="collections"/> at once (no per-collection round trips).</summary>
    private static async Task<List<CollectionSetup>> ComputeAsync(AdminContext admin, IReadOnlyList<CollectionRef> collections, CancellationToken ct)
    {
        if (collections.Count == 0)
        {
            return [];
        }

        var ids = collections.Select(c => c.Id).ToList();
        var db = admin.Db;
        var sources = await db.Sources.AsNoTracking().Where(s => ids.Contains(s.CollectionId) && s.DeletedAt == null)
            .GroupBy(s => s.CollectionId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var runs = await (from r in db.Runs.AsNoTracking()
                          join s in db.Sources.AsNoTracking() on r.SourceId equals s.Id
                          where ids.Contains(s.CollectionId) && s.DeletedAt == null
                          group r by s.CollectionId into g
                          select new
                          {
                              g.Key,
                              Finished = g.Any(r => r.State == RunStates.Succeeded || r.State == RunStates.Degraded || r.State == RunStates.AwaitingDeletionConfirmation),
                              Running = g.Any(r => r.State == RunStates.Running),
                          }).ToDictionaryAsync(x => x.Key, ct);
        var questions = await (from q in db.EvaluationQuestions.AsNoTracking()
                               join set in db.EvaluationSets.AsNoTracking() on q.SetId equals set.Id
                               where ids.Contains(set.CollectionId) && q.State == EvaluationQuestionStates.Active && q.RetiredAt == null
                               group q by set.CollectionId into g
                               select new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var evaluated = await (from r in db.EvaluationRuns.AsNoTracking()
                               join set in db.EvaluationSets.AsNoTracking() on r.SetId equals set.Id
                               where ids.Contains(set.CollectionId) && r.FinishedAt != null && !r.Candidate
                               select set.CollectionId).Distinct().ToListAsync(ct);
        var evalKeys = ids.Select(JobDedupKeys.Eval).ToList();
        var evaluating = await db.Jobs.AsNoTracking()
            .Where(j => j.DedupKey != null && evalKeys.Contains(j.DedupKey) && (j.State == JobStates.Queued || j.State == JobStates.Running))
            .Select(j => j.DedupKey!).ToListAsync(ct);
        var linked = await (from v in db.AssistantVersionsLinking([.. ids]).AsNoTracking()
                            join a in db.Assistants.AsNoTracking() on v.AssistantId equals a.Id
                            where (a.PublishedVersionId == v.Id || a.DraftVersionId == v.Id) && a.State == AssistantStates.Active
                            select new
                            {
                                a.Id, a.DepartmentId, v.CollectionLinks, Published = a.PublishedVersionId == v.Id,
                                Available = a.PublishedVersionId == v.Id && db.AssistantAudiences.Any(x => x.AssistantId == a.Id && x.Kind == AssistantAudienceKinds.Client),
                            }).ToListAsync(ct);

        return
        [
            .. collections.Select(c =>
            {
                // Only the collection's own department's assistants (#313 review): another department's link to a shared collection
                // is not this checklist's step, and its assistant would 404 for the caller.
                var assistants = linked.Where(l => l.CollectionLinks.Any(link => link.CollectionId == c.Id) && l.DepartmentId == c.DepartmentId).OrderByDescending(l => l.Available).ThenByDescending(l => l.Published).ThenBy(l => l.Id).ToList();
                var run = runs.GetValueOrDefault(c.Id);
                var available = assistants.Any(a => a.Available);
                return new CollectionSetup(
                    c.Id, c.Name, sources.GetValueOrDefault(c.Id), run?.Finished ?? false, run?.Running ?? false, questions.GetValueOrDefault(c.Id),
                    evaluated.Contains(c.Id), evaluating.Contains(JobDedupKeys.Eval(c.Id)), c.PublishedAt is not null, assistants.FirstOrDefault()?.Id,
                    assistants.Any(a => a.Published), available, available);
            }),
        ];
    }
}

/// <summary>
/// One collection's checklist (admin design §5.4 steps 3–13): sources, the first finished run (or one running), active
/// test questions, a finished evaluation (or one queued or running), published, an assistant linking it (the one to open:
/// available first, then published), its publication, a client audience; <see cref="Done"/> once available.
/// </summary>
public sealed record CollectionSetup(
    Guid CollectionId, string Name, int Sources, bool FirstSyncDone, bool SyncRunning, int TestQuestions, bool Evaluated, bool Evaluating, bool Published,
    Guid? AssistantId, bool AssistantPublished, bool Available, bool Done);

/// <summary>
/// Home (§5.5.1): whether any collection is in scope (the empty state, §5.4 step 1), the checklists not done,
/// <see cref="NeedsEmbeddingProfile"/>: the caller is a platform admin and there is no active default embedding profile yet,
/// which a new collection needs (<c>no_embedding_profile</c>), the published assistants' last 7 days (<see cref="Live"/>) and
/// <see cref="Health"/>.
/// </summary>
public sealed record Overview(
    bool HasCollections, IReadOnlyList<CollectionSetup> Setup, bool NeedsEmbeddingProfile, IReadOnlyList<LiveAssistant> Live, OverviewHealth Health);

/// <summary>
/// A published assistant's last 7 days on Home: <see cref="Questions"/> are the totals' messages, <see cref="CostUsd"/> the
/// gateway's cost plus the estimate (§8.6); <see cref="PublishedVersion"/> is the published version's number.
/// </summary>
public sealed record LiveAssistant(
    Guid AssistantId, string Name, Guid DepartmentId, int PublishedVersion, int Questions, int NotFound, int ThumbsUp, int ThumbsDown, decimal CostUsd);

/// <summary>Health on Home: the runs per department of the last 24 hours, the job queue and the two gateway syncs.</summary>
public sealed record OverviewHealth(IReadOnlyList<DepartmentRuns> Departments, QueueHealth Queue, SyncHealth OrganisationSync, SyncHealth ModelSync);

/// <summary>A department's runs started in the last 24 hours.</summary>
public sealed record DepartmentRuns(Guid DepartmentId, string DepartmentName, RunCounts Runs);

/// <summary>Runs by outcome: done, with remarks (degraded or awaiting deletion confirmation), failed, running.</summary>
public sealed record RunCounts(int Succeeded, int Degraded, int Failed, int Running);

/// <summary>The worker queue: jobs waiting (<c>queued</c>) and when the oldest of them was enqueued.</summary>
public sealed record QueueHealth(int Waiting, DateTimeOffset? OldestQueuedAt);

/// <summary>A gateway sync's last success and its failed runs in a row since (this instance's, held in memory).</summary>
public sealed record SyncHealth(DateTimeOffset? LastSuccessAt, int ConsecutiveFailures);
