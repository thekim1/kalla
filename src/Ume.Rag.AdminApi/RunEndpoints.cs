using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;

namespace Ume.Rag.AdminApi;

/// <summary>
/// Runs and the crawl report (admin design §4.5 "Sources, runs, uploads", P1a item 14b): a source's runs and a run's items
/// by keyset, a run with its <c>Report</c>, and <c>cancel-run</c>. A run is in scope when its source is.
/// </summary>
public static class RunEndpoints
{
    public const int MaxLimit = 200;

    public static RouteGroupBuilder MapRuns(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/sources/{id:guid}/runs", async (Guid id, Guid? after, int? limit, AdminContext admin, CancellationToken ct) =>
            {
                _ = await SourceEndpoints.Scoped(admin).AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                var take = Math.Clamp(limit ?? 50, 1, MaxLimit);
                // Newest first by start, then id; the keyset continues after the last run seen.
                var query = admin.Db.Runs.AsNoTracking().Where(r => r.SourceId == id);
                if (after is { } last)
                {
                    var start = await admin.Db.Runs.AsNoTracking().Where(r => r.Id == last && r.SourceId == id).Select(r => (DateTimeOffset?)r.StartedAt)
                        .SingleOrDefaultAsync(ct) ?? throw AdminFaults.Field("after", AdminErrorCodes.InvalidValue);
                    query = query.Where(r => r.StartedAt < start || (r.StartedAt == start && r.Id.CompareTo(last) < 0));
                }

                var rows = await query.OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).Take(take + 1).Select(r => RunSummary.Of(r)).ToListAsync(ct);
                return new KeysetPage<RunSummary, Guid?>([.. rows.Take(take)], rows.Count > take ? rows[take - 1].Id : null);
            })
            .RequireAuthorization(AdminPolicies.Read).WithTags("Runs").WithName("ListRuns").WithSummary("A source's runs, newest first (keyset)");

        var runs = api.MapGroup("/runs").WithTags("Runs");

        runs.MapGet("/{id:guid}", async (Guid id, AdminContext admin, CancellationToken ct) =>
                RunDto.Of(await ScopedRunAsync(admin, id, ct)))
            .RequireAuthorization(AdminPolicies.Read).WithName("GetRun").WithSummary("A run with its crawl report");

        runs.MapGet("/{id:guid}/items", async (Guid id, long? after, int? limit, string? outcome, string? reason, string? q, AdminContext admin, CancellationToken ct) =>
            {
                var run = await ScopedRunAsync(admin, id, ct);
                var take = Math.Clamp(limit ?? 50, 1, MaxLimit);
                // RunItems is partitioned by day: bound the scan to the run's days.
                var from = run.StartedAt.UtcDateTime.Date;
                var query = admin.Db.RunItems.AsNoTracking().Where(i => i.RunId == id && i.CreatedAt >= from);
                if (run.FinishedAt is { } finished)
                {
                    var to = finished.UtcDateTime.Date.AddDays(1);
                    query = query.Where(i => i.CreatedAt < to);
                }

                if (!string.IsNullOrWhiteSpace(outcome))
                {
                    query = query.Where(i => i.Outcome == outcome);
                }

                if (!string.IsNullOrWhiteSpace(reason))
                {
                    query = query.Where(i => i.Reason == reason);
                }

                if (!string.IsNullOrWhiteSpace(q))
                {
                    var pattern = $"%{Like.Escape(q.Trim())}%";
                    query = query.Where(i => EF.Functions.ILike(i.Identity, pattern, Like.EscapeCharacter));
                }

                if (after is { } last)
                {
                    query = query.Where(i => i.Id > last);
                }

                var rows = await query.OrderBy(i => i.Id).Take(take + 1)
                    .Select(i => new RunItemDto(i.Id, i.CreatedAt, i.DocumentId, i.Identity, i.Outcome, i.Reason, i.Strategy, i.DurationMs, i.CostUsd))
                    .ToListAsync(ct);
                return new KeysetPage<RunItemDto, long?>([.. rows.Take(take)], rows.Count > take ? rows[take - 1].Id : null);
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("ListRunItems").WithSummary("A run's items (keyset; outcome, reason, q)");

        runs.MapPost("/{id:guid}/cancel", async (Guid id, AdminContext admin, CancellationToken ct) =>
                await admin.WriteAsync(async attempt =>
                {
                    var run = await ScopedRunAsync(admin, id, attempt);
                    if (run.State is not (RunStates.Running or RunStates.AwaitingDeletionConfirmation))
                    {
                        throw AdminFaults.Of(AdminErrorCodes.RunNotActive, new Dictionary<string, string> { ["state"] = run.State });
                    }

                    var source = await admin.Db.Sources.AsNoTracking().SingleAsync(s => s.Id == run.SourceId, attempt);
                    var department = await SourceEndpoints.DepartmentOfAsync(admin, source, attempt);
                    var job = CommandJobs.CancelRunJob(run.Id, department, run.Classification);
                    return new AdminChange<CancelStarted>(new CancelStarted(job.Id, run.Id), "cancel", AuditEntityTypes.Run, run.Id, null, new { jobId = job.Id })
                    {
                        DepartmentId = department, Classification = run.Classification, Jobs = [job],
                    };
                }, ct))
            .RequireAuthorization(AdminPolicies.Edit).WithName("CancelRun").WithSummary("Cancel a running run (command cancel-run)");

        return api;
    }

    private static async Task<Run> ScopedRunAsync(AdminContext admin, Guid id, CancellationToken ct)
    {
        var sources = SourceEndpoints.Scoped(admin).Select(s => s.Id);
        return await admin.Db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && sources.Contains(r.SourceId), ct)
            ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
    }
}

/// <summary>A keyset page (admin design §4.1): the items and the <c>after</c> value of the next page, null at the end.</summary>
public sealed record KeysetPage<T, TKey>(IReadOnlyList<T> Items, TKey Next);

public sealed record RunSummary(
    Guid Id, string Kind, string Trigger, string State, string? Phase, int Added, int Updated, int Unchanged, int Deleted, int Failed, int ProposedDeletions,
    decimal CostUsd, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt)
{
    public static RunSummary Of(Run r) => new(r.Id, r.Kind, r.Trigger, r.State, r.Phase, r.Added, r.Updated, r.Unchanged, r.Deleted, r.Failed,
        r.ProposedDeletions, r.CostUsd, r.StartedAt, r.FinishedAt);
}

/// <summary>A run with its <c>Report</c> (the crawl report: traversal, site-profile version, extraction quality, failures by reason).</summary>
public sealed record RunDto(RunSummary Run, Guid SourceId, Guid? PipelineVersionId, DateTimeOffset? ScheduledFor, JsonElement? Report)
{
    public static RunDto Of(Run r)
    {
        ArgumentNullException.ThrowIfNull(r);
        JsonElement? report = null;
        if (!string.IsNullOrEmpty(r.Report))
        {
            using var json = JsonDocument.Parse(r.Report);
            report = json.RootElement.Clone();
        }

        return new(RunSummary.Of(r), r.SourceId, r.PipelineVersionId, r.ScheduledFor, report);
    }
}

public sealed record RunItemDto(long Id, DateTimeOffset CreatedAt, Guid? DocumentId, string Identity, string Outcome, string? Reason, string? Strategy,
    int? DurationMs, decimal? CostUsd);

public sealed record CancelStarted(Guid JobId, Guid RunId);
