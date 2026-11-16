using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;

namespace Ume.Rag.AdminApi;

/// <summary>
/// Changing the owner department of a knowledge base or an assistant (admin design §3.2 "Changing the owner department";
/// Decisions "Moving to another department"; #811). Platform admins only. <c>…/move/impact</c> answers what the move does
/// and a token; <c>…/move</c> needs that token, so the admin confirms exactly the shares listed. The move keeps every link
/// working by sharing knowledge bases with the departments that would lose them; history (usage rows, audit entries)
/// keeps its department.
/// </summary>
public static class DepartmentMoves
{
    public const string MoveAction = "move";

    public static RouteGroupBuilder MapDepartmentMoves(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapPost("/collections/{id:guid}/move/impact", async (Guid id, MoveRequest request, AdminContext admin, CancellationToken ct) =>
            {
                var collection = await CollectionEndpoints.LoadAsync(admin, id, ct);
                var team = await CheckTargetAsync(admin, collection.DepartmentId, request, ct);
                var plan = await PlanCollectionAsync(admin, collection, request.DepartmentId, ct);
                return Impact(plan, collection.Id, collection.Version, request, team);
            })
            .RequireAuthorization(AdminPolicies.Admin).WithTags("Collections").WithName("CollectionMoveImpact")
            .WithSummary("What moving a collection to another department does: the shares it adds, the sources that block it, and the token the move needs");

        api.MapPost("/collections/{id:guid}/move", async (Guid id, MoveRequest request, AdminContext admin, GatewayModelCheck models, CancellationToken ct) =>
            {
                if (await CollectionEndpoints.Scoped(admin).AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) is { } current)
                {
                    // Before the write and outside the config lock, as on a key save (admin design §4.4).
                    await models.WarmKeyAsync(ResidencyCeiling.ForLevel(current.Classification), request.GatewayKeyId, request.DepartmentId, ct);
                }

                var saved = await admin.WriteAsync(async attempt =>
                {
                    var collection = await CollectionEndpoints.LoadAsync(admin, id, attempt);
                    admin.ExpectVersion(collection, request.Version);
                    var from = collection.DepartmentId;
                    var team = await CheckTargetAsync(admin, from, request, attempt);
                    var plan = await PlanCollectionAsync(admin, collection, request.DepartmentId, attempt);
                    if (plan.BlockingSources.Count > 0)
                    {
                        throw AdminFaults.Of(AdminErrorCodes.MovePipelineDepartment, null,
                            new Dictionary<string, object?> { ["affected"] = plan.BlockingSources });
                    }

                    Confirm(request, Token(collection.Id, collection.Version, request, team, plan.Shares));
                    var before = MoveAudit.Of(collection.DepartmentId, collection.OwnerTeamId, collection.GatewayKeyId);
                    collection.DepartmentId = request.DepartmentId;
                    collection.OwnerTeamId = team;
                    collection.GatewayKeyId = request.GatewayKeyId;
                    collection.UpdatedAt = admin.Now;
                    collection.UpdatedBy = admin.Actor;
                    var also = await ApplySharesAsync(admin, plan, [collection], attempt);
                    await CollectionEndpoints.CheckGatewayKeyAsync(admin, collection, models, attempt);
                    var after = MoveAudit.Of(collection.DepartmentId, collection.OwnerTeamId, collection.GatewayKeyId);
                    also.Add(new AuditAlso(MoveAction, AuditEntityTypes.Collection, collection.Id, before, after) { DepartmentId = from });
                    return new AdminChange<Collection>(collection, MoveAction, AuditEntityTypes.Collection, collection.Id, before, after)
                    {
                        DepartmentId = collection.DepartmentId, Classification = collection.Classification, Also = also,
                    };
                }, ct);
                return await CollectionEndpoints.DetailAsync(admin, saved, ct);
            })
            .RequireAuthorization(AdminPolicies.Admin).WithTags("Collections").WithName("MoveCollection")
            .WithSummary("Move a collection to another department (the impact's token; shares added so every link keeps working)");

        api.MapPost("/assistants/{id:guid}/move/impact", async (Guid id, MoveRequest request, AdminContext admin, CancellationToken ct) =>
            {
                var assistant = await AssistantEndpoints.LoadAsync(admin, id, ct, tracked: false);
                var team = await CheckTargetAsync(admin, assistant.DepartmentId, request, ct);
                var plan = await PlanAssistantAsync(admin, assistant, request.DepartmentId, ct);
                return Impact(plan, assistant.Id, assistant.Version, request, team);
            })
            .RequireAuthorization(AdminPolicies.Admin).WithTags("Assistants").WithName("AssistantMoveImpact")
            .WithSummary("What moving an assistant to another department does: the shares it adds and the token the move needs");

        api.MapPost("/assistants/{id:guid}/move", async (Guid id, MoveRequest request, AdminContext admin, GatewayModelCheck models, CancellationToken ct) =>
            {
                var current = await AssistantEndpoints.LoadAsync(admin, id, ct, tracked: false);
                var ceiling = AssistantRules.Ceiling(current, await AssistantEndpoints.AudienceClientsAsync(admin, id, ct));
                await models.WarmKeyAsync(ceiling, request.GatewayKeyId, request.DepartmentId, ct);

                var saved = await admin.WriteAsync(async attempt =>
                {
                    var assistant = await AssistantEndpoints.LoadAsync(admin, id, attempt);
                    admin.ExpectVersion(assistant, request.Version);
                    var from = assistant.DepartmentId;
                    var team = await CheckTargetAsync(admin, from, request, attempt);
                    var plan = await PlanAssistantAsync(admin, assistant, request.DepartmentId, attempt);
                    Confirm(request, Token(assistant.Id, assistant.Version, request, team, plan.Shares));
                    var before = MoveAudit.Of(assistant.DepartmentId, assistant.OwnerTeamId, assistant.GatewayKeyId);
                    assistant.DepartmentId = request.DepartmentId;
                    assistant.OwnerTeamId = team;
                    assistant.GatewayKeyId = request.GatewayKeyId;
                    assistant.UpdatedAt = admin.Now;
                    assistant.UpdatedBy = admin.Actor;
                    var also = await ApplySharesAsync(admin, plan, [], attempt);

                    // V39: a published assistant keeps a key once the gateway acts as keys; a key is checked against the models
                    // of the version that runs (else the draft) at its ceiling, as on a key save.
                    if (assistant.GatewayKeyId is null && assistant.PublishedVersionId is not null && models.ActsAsKeys)
                    {
                        AssistantEndpoints.Refuse([new(AdminErrorCodes.GatewayKeyRequired, null, [])]);
                    }

                    if (assistant.GatewayKeyId is not null && (assistant.PublishedVersionId ?? assistant.DraftVersionId) is { } runs)
                    {
                        var version = await admin.Db.AssistantVersions.AsNoTracking().SingleAsync(v => v.Id == runs, attempt);
                        var audience = await AssistantEndpoints.AudienceClientsAsync(admin, assistant.Id, attempt);
                        AssistantEndpoints.Refuse(await GatewayKeyRules.CheckAsync(admin, assistant.GatewayKeyId, assistant.DepartmentId,
                            AssistantRules.Ceiling(assistant, audience), await GatewayKeyRules.AssistantModelsAsync(admin, version, attempt), attempt, models));
                    }

                    var after = MoveAudit.Of(assistant.DepartmentId, assistant.OwnerTeamId, assistant.GatewayKeyId);
                    also.Add(new AuditAlso(MoveAction, AuditEntityTypes.Assistant, assistant.Id, before, after) { DepartmentId = from });
                    return new AdminChange<Assistant>(assistant, MoveAction, AuditEntityTypes.Assistant, assistant.Id, before, after)
                    {
                        DepartmentId = assistant.DepartmentId, Classification = assistant.Classification, Also = also,
                    };
                }, ct);
                return await AssistantEndpoints.DetailAsync(admin, saved, ct);
            })
            .RequireAuthorization(AdminPolicies.Admin).WithTags("Assistants").WithName("MoveAssistant")
            .WithSummary("Move an assistant to another department (the impact's token; its linked collections shared with the new department)");

        return api;
    }

    /// <summary>
    /// The target is an active department other than the current one. Returns the owner team (#811 review, main session):
    /// the request's, one of the new department's active teams; without one, the department's only active team, none when
    /// it has no team, and <c>invalid_value</c> on <c>ownerTeamId</c> when it has several (the admin chooses).
    /// </summary>
    private static async Task<Guid?> CheckTargetAsync(AdminContext admin, Guid current, MoveRequest request, CancellationToken ct)
    {
        if (request.DepartmentId == current || !await admin.Db.Departments.AnyAsync(d => d.Id == request.DepartmentId && d.IsActive, ct))
        {
            throw AdminFaults.Field("departmentId", AdminErrorCodes.InvalidValue);
        }

        var teams = await admin.Db.Teams.AsNoTracking().Where(t => t.DepartmentId == request.DepartmentId && t.IsActive).Select(t => t.Id).ToListAsync(ct);
        if (request.OwnerTeamId is { } team)
        {
            return teams.Contains(team) ? team : throw AdminFaults.Field("ownerTeamId", AdminErrorCodes.InvalidValue);
        }

        return teams.Count switch
        {
            0 => null,
            1 => teams[0],
            _ => throw AdminFaults.Field("ownerTeamId", AdminErrorCodes.InvalidValue),
        };
    }

    /// <summary>
    /// A collection moving to <paramref name="target"/>: each other department whose assistants (published version or draft,
    /// archived too) link it and that its sharing would not reach with the new owner gets a share; a live source on a
    /// pipeline of another department blocks the move.
    /// </summary>
    private static async Task<MovePlan> PlanCollectionAsync(AdminContext admin, Collection collection, Guid target, CancellationToken ct)
    {
        var users = await CollectionEndpoints.UsedByAsync(admin, collection.Id, ct);
        var shares = await admin.Db.CollectionShares.AsNoTracking().Where(s => s.CollectionId == collection.Id).Select(s => s.DepartmentId).ToListAsync(ct);
        var reach = await CollectionSharing.ReachAsync(admin.Db, target, collection.SharedWith, shares, ct);
        var needed = users.Select(u => u.DepartmentId).Where(d => d != target && !reach.Contains(d)).Distinct().ToList();
        var blocking = await (from s in admin.Db.Sources.AsNoTracking()
                              join p in admin.Db.Pipelines.AsNoTracking() on s.PipelineId equals p.Id
                              where s.CollectionId == collection.Id && s.DeletedAt == null && p.DepartmentId != null && p.DepartmentId != target
                              orderby s.Name
                              select s.Name + " (" + p.Name + ")").ToListAsync(ct);
        return new MovePlan(await NamedAsync(admin, needed.Select(d => (collection.Id, collection.Name, d)), ct), blocking);
    }

    /// <summary>
    /// An assistant moving to <paramref name="target"/>: each collection its published version or draft links, owned by
    /// another department and not reaching the new one, gets a share for it.
    /// </summary>
    private static async Task<MovePlan> PlanAssistantAsync(AdminContext admin, Assistant assistant, Guid target, CancellationToken ct)
    {
        Guid[] versions = [.. new[] { assistant.PublishedVersionId, assistant.DraftVersionId }.OfType<Guid>().Distinct()];
        var links = await admin.Db.AssistantVersions.AsNoTracking().Where(v => versions.Contains(v.Id)).Select(v => v.CollectionLinks).ToListAsync(ct);
        var ids = links.SelectMany(l => l).Select(l => l.CollectionId).Distinct().ToList();
        var collections = await admin.Db.Collections.AsNoTracking().Where(c => ids.Contains(c.Id) && c.DeletedAt == null && c.DepartmentId != target)
            .OrderBy(c => c.Name).ToListAsync(ct);
        var needed = new List<(Guid, string, Guid)>();
        foreach (var collection in collections)
        {
            var shares = await admin.Db.CollectionShares.AsNoTracking().Where(s => s.CollectionId == collection.Id).Select(s => s.DepartmentId).ToListAsync(ct);
            if (!(await CollectionSharing.ReachAsync(admin.Db, collection.DepartmentId, collection.SharedWith, shares, ct)).Contains(target))
            {
                needed.Add((collection.Id, collection.Name, target));
            }
        }

        return new MovePlan(await NamedAsync(admin, needed, ct), []);
    }

    private static async Task<List<MoveShare>> NamedAsync(AdminContext admin, IEnumerable<(Guid Collection, string Name, Guid Department)> shares, CancellationToken ct)
    {
        var list = shares.ToList();
        var ids = list.Select(s => s.Department).Distinct().ToList();
        var names = await admin.Db.Departments.AsNoTracking().Where(d => ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        return [.. list.Select(s => new MoveShare(s.Collection, s.Name, s.Department, names.GetValueOrDefault(s.Department, s.Department.ToString())))
            .OrderBy(s => s.Collection).ThenBy(s => s.DepartmentId)];
    }

    private static MoveImpact Impact(MovePlan plan, Guid id, uint version, MoveRequest request, Guid? team) =>
        new(plan.Shares, plan.BlockingSources, team, plan.BlockingSources.Count > 0 ? null : Token(id, version, request, team, plan.Shares));

    /// <summary>
    /// Adds the plan's shares (a collection at <c>department</c> level becomes <c>selected</c> with only them, after dropping
    /// the rows kept unused at that level) and drops a moved collection's share row for its new owner. One audited
    /// <c>share</c> per collection whose sharing changed, under its owner's department.
    /// </summary>
    private static async Task<List<AuditAlso>> ApplySharesAsync(AdminContext admin, MovePlan plan, IReadOnlyList<Collection> moved, CancellationToken ct)
    {
        var also = new List<AuditAlso>();
        var touched = plan.Shares.Select(s => s.CollectionId).Concat(moved.Select(c => c.Id)).Distinct().ToList();
        foreach (var id in touched)
        {
            var collection = moved.FirstOrDefault(c => c.Id == id) ?? await admin.Db.Collections.SingleAsync(c => c.Id == id, ct);
            var rows = await admin.Db.CollectionShares.Where(s => s.CollectionId == id).ToListAsync(ct);
            var before = new SharingAudit(collection.SharedWith,
                collection.SharedWith == CollectionSharingLevels.Selected ? [.. rows.Select(r => r.DepartmentId).Order()] : []);
            var adding = plan.Shares.Where(s => s.CollectionId == id).Select(s => s.DepartmentId).ToList();
            if (adding.Count > 0 && collection.SharedWith == CollectionSharingLevels.Department)
            {
                admin.Db.CollectionShares.RemoveRange(rows);
                rows = [];
                collection.SharedWith = CollectionSharingLevels.Selected;
            }

            var owner = rows.Where(r => r.DepartmentId == collection.DepartmentId).ToList();
            admin.Db.CollectionShares.RemoveRange(owner);
            var kept = rows.Except(owner).Select(r => r.DepartmentId).ToList();
            foreach (var department in adding.Where(d => !kept.Contains(d)))
            {
                admin.Db.CollectionShares.Add(new CollectionShare { CollectionId = id, DepartmentId = department, SharedBy = admin.Actor, SharedAt = admin.Now });
                kept.Add(department);
            }

            var after = new SharingAudit(collection.SharedWith, collection.SharedWith == CollectionSharingLevels.Selected ? [.. kept.Order()] : []);
            if (adding.Count > 0 || owner.Count > 0)
            {
                // The aggregate root's stamp moves its xmin, so a concurrent sharing save conflicts (admin design §4.3).
                collection.UpdatedAt = admin.Now;
                collection.UpdatedBy = admin.Actor;
                also.Add(new AuditAlso("share", AuditEntityTypes.Collection, id, before, new { sharing = after, reason = MoveAction })
                {
                    DepartmentId = collection.DepartmentId,
                });
            }
        }

        return also;
    }

    private static void Confirm(MoveRequest request, string token)
    {
        if (!string.Equals(request.ImpactToken, token, StringComparison.Ordinal))
        {
            throw AdminFaults.Of(AdminErrorCodes.MoveNotConfirmed);
        }
    }

    /// <summary>A hash of the object, its version, the request and the shares to add: valid for exactly the move the admin saw.</summary>
    internal static string Token(Guid id, uint version, MoveRequest request, Guid? team, IReadOnlyList<MoveShare> shares)
    {
        var text = new StringBuilder()
            .Append(id.ToString("N")).Append('\n').Append(version).Append('\n')
            .Append(request.DepartmentId.ToString("N")).Append('\n')
            .Append(team?.ToString("N")).Append('\n')
            .Append(request.GatewayKeyId?.ToString("N")).Append('\n');
        foreach (var share in shares)
        {
            text.Append(share.CollectionId.ToString("N")).Append(':').Append(share.DepartmentId.ToString("N")).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }

    private sealed record MovePlan(IReadOnlyList<MoveShare> Shares, IReadOnlyList<string> BlockingSources);

    private sealed record MoveAudit(Guid DepartmentId, Guid? OwnerTeamId, Guid? GatewayKeyId)
    {
        public static MoveAudit Of(Guid department, Guid? team, Guid? key) => new(department, team, key);
    }
}

/// <summary>A move to another department (#811): the new department, its owner team and gateway key (or none), the version seen.</summary>
public sealed record MoveRequest : AdminRequest
{
    [Required]
    public Guid DepartmentId { get; init; }

    public Guid? OwnerTeamId { get; init; }

    public Guid? GatewayKeyId { get; init; }

    [Required]
    public string? Version { get; init; }

    /// <summary>The token of <c>…/move/impact</c>; required by <c>…/move</c>.</summary>
    public string? ImpactToken { get; init; }
}

/// <summary>A share the move adds so a link keeps working.</summary>
public sealed record MoveShare(Guid CollectionId, string Collection, Guid DepartmentId, string Department);

/// <summary>What a move does: the shares, the blocking sources, the owner team it gets; <see cref="ImpactToken"/> is null while sources block it.</summary>
public sealed record MoveImpact(IReadOnlyList<MoveShare> Shares, IReadOnlyList<string> SourcesOnDepartmentPipelines, Guid? OwnerTeamId, string? ImpactToken);
