using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Pipelines.Jobs;

namespace Ume.Rag.AdminApi;

/// <summary>
/// The setup checklist "Kom igång" (admin design §5.4, §5.5.2): the pilot journey's steps per collection, computed from
/// state (no stored progress flags). A collection's checklist is done once an assistant whose published version links it
/// has a client audience (it is in Open WebUI). <c>GET /api/overview</c> lists the caller's collections that are not done
/// yet (§5.5.1; the attention items join it later).
/// </summary>
public static class SetupEndpoints
{
    /// <summary>The newest collections the overview computes a checklist for.</summary>
    public const int OverviewCollections = 50;

    public static RouteGroupBuilder MapSetup(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/collections/{id:guid}/setup", async (Guid id, AdminContext admin, CancellationToken ct) =>
            {
                var collection = await CollectionEndpoints.Scoped(admin).AsNoTracking().Where(c => c.Id == id)
                    .Select(c => new CollectionRef(c.Id, c.Name, c.PublishedAt)).SingleOrDefaultAsync(ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                return (await ComputeAsync(admin, [collection], ct)).Single();
            })
            .RequireAuthorization(AdminPolicies.Read).WithTags("Collections").WithName("GetCollectionSetup")
            .WithSummary("The collection's setup checklist (Kom igång), computed from state");

        api.MapGet("/overview", async (AdminContext admin, CancellationToken ct) =>
            {
                var collections = await CollectionEndpoints.Scoped(admin).AsNoTracking().OrderByDescending(c => c.UpdatedAt).ThenBy(c => c.Id).Take(OverviewCollections)
                    .Select(c => new CollectionRef(c.Id, c.Name, c.PublishedAt)).ToListAsync(ct);
                var setups = await ComputeAsync(admin, collections, ct);
                return new Overview(collections.Count > 0, [.. setups.Where(s => !s.Done)]);
            })
            .RequireAuthorization(AdminPolicies.Read).WithTags("Overview").WithName("GetOverview")
            .WithSummary("The start page: whether the caller has collections, and the setup checklists not done yet");

        return api;
    }

    private sealed record CollectionRef(Guid Id, string Name, DateTimeOffset? PublishedAt);

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
        var linked = await (from v in db.AssistantVersions.AsNoTracking()
                            join a in db.Assistants.AsNoTracking() on v.AssistantId equals a.Id
                            where (a.PublishedVersionId == v.Id || a.DraftVersionId == v.Id) && a.State == AssistantStates.Active
                                  && v.CollectionIds.Any(c => ids.Contains(c))
                            select new
                            {
                                a.Id, v.CollectionIds, Published = a.PublishedVersionId == v.Id,
                                Available = a.PublishedVersionId == v.Id && db.AssistantAudiences.Any(x => x.AssistantId == a.Id && x.Kind == AssistantAudienceKinds.Client),
                            }).ToListAsync(ct);

        return
        [
            .. collections.Select(c =>
            {
                var assistants = linked.Where(l => l.CollectionIds.Contains(c.Id)).OrderByDescending(l => l.Available).ThenByDescending(l => l.Published).ThenBy(l => l.Id).ToList();
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

/// <summary>The start page: whether any collection is in scope (the empty state, §5.4 step 1), and the checklists not done.</summary>
public sealed record Overview(bool HasCollections, IReadOnlyList<CollectionSetup> Setup);
