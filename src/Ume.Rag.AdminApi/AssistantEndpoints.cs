using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;

namespace Ume.Rag.AdminApi;

/// <summary>
/// Assistants (admin design §4.5 "Assistants", §4.6; simplification pass §15; P1a item 14d): create (with its first draft),
/// the assistant's own fields, the one draft (saved with the V-rules as <c>warnings</c>), publish (V34 and the assistant
/// rules as refusals, then V8 against the gateway, in one transaction), and audiences of kind <c>client</c>. Restore,
/// archive, copy, diff, test runs and retention edits are later phases. Department-scoped.
/// </summary>
public static class AssistantEndpoints
{
    public static RouteGroupBuilder MapAssistants(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var assistants = api.MapGroup("/assistants").WithTags("Assistants");

        assistants.MapGet("", async ([AsParameters] PageQuery page, AdminContext admin, CancellationToken ct) =>
            {
                var query = Scoped(admin);
                if (!string.IsNullOrWhiteSpace(page.Q))
                {
                    var q = $"%{page.Q.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
                    query = query.Where(a => EF.Functions.ILike(a.Name, q, "\\") || EF.Functions.ILike(a.Slug, q, "\\"));
                }

                var total = await query.CountAsync(ct);
                var rows = await query.OrderBy(a => a.Name).Skip(page.Skip).Take(page.Take)
                    .Select(a => new { a.Id, a.Slug, a.Name, a.DepartmentId, a.Classification, a.State, a.PublishedVersionId, HasDraft = a.DraftVersionId != null })
                    .ToListAsync(ct);
                var publishedIds = rows.Where(r => r.PublishedVersionId != null).Select(r => r.PublishedVersionId!.Value).ToList();
                var steps = await admin.Db.AssistantVersions.AsNoTracking().Where(v => publishedIds.Contains(v.Id)).Select(v => new { v.Id, v.QuerySteps })
                    .ToDictionaryAsync(v => v.Id, v => v.QuerySteps, ct);
                var items = rows.Select(a => new AssistantSummary(a.Id, a.Slug, a.Name, a.DepartmentId, a.Classification, a.State, a.PublishedVersionId != null, a.HasDraft,
                    a.PublishedVersionId is { } v && steps.TryGetValue(v, out var json) ? GeneralKnowledgeOf(json) : null)).ToList();
                return new PageDto<AssistantSummary>(items, total);
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("ListAssistants").WithSummary("Assistants of the caller's departments");

        assistants.MapGet("/{id:guid}", async (Guid id, AdminContext admin, CancellationToken ct) =>
                await DetailAsync(admin, await LoadAsync(admin, id, ct, tracked: false), ct))
            .RequireAuthorization(AdminPolicies.Read).WithName("GetAssistant").WithSummary("One assistant: own fields, versions, audiences");

        assistants.MapPost("", async (AssistantCreateRequest request, AdminContext admin, CancellationToken ct) =>
            {
                var created = await admin.WriteAsync(async attempt =>
                {
                    if (!await admin.Departments().AnyAsync(d => d.Id == request.DepartmentId, attempt))
                    {
                        throw AdminFaults.Field("departmentId", AdminErrorCodes.NotInScope);
                    }

                    await CheckTeamAsync(admin, request.DepartmentId, request.OwnerTeamId, attempt);
                    // Teams are hidden in create forms: the creator's first team in the department (Decisions "Organisation").
                    var ownerTeam = request.OwnerTeamId ?? await admin.DefaultOwnerTeamAsync(request.DepartmentId, attempt);
                    var slug = string.IsNullOrWhiteSpace(request.Slug) ? Slugs.From(request.Name) : request.Slug;
                    if (await admin.Db.Assistants.AsNoTracking().AnyAsync(a => a.Slug == slug, attempt))
                    {
                        throw AdminFaults.Field("slug", AdminErrorCodes.AlreadyExists);
                    }

                    var assistant = new Assistant
                    {
                        Slug = slug, Name = request.Name.Trim(), DepartmentId = request.DepartmentId, OwnerTeamId = ownerTeam, Classification = request.Classification,
                        UpdatedAt = admin.Now, UpdatedBy = admin.Actor,
                    };
                    request.ApplyTo(assistant);
                    admin.Db.Assistants.Add(assistant);
                    await admin.Db.SaveChangesAsync(attempt);
                    // Created with only a draft: no published version, so no client lists it (admin design §4.5). The draft starts from
                    // the platform's citizen-service instructions (Settings.Chat.defaultSystemPrompt, P1b item 39).
                    var draft = new AssistantVersion
                    {
                        AssistantId = assistant.Id, Number = 1, SystemPrompt = await DefaultSystemPromptAsync(admin, attempt), CreatedBy = admin.Actor, CreatedAt = admin.Now,
                        UpdatedAt = admin.Now, UpdatedBy = admin.Actor,
                    };
                    admin.Db.AssistantVersions.Add(draft);
                    await admin.Db.SaveChangesAsync(attempt);
                    assistant.DraftVersionId = draft.Id;
                    return new AdminChange<Assistant>(assistant, "create", AuditEntityTypes.Assistant, assistant.Id, null, AssistantAudit.Of(assistant))
                    {
                        DepartmentId = assistant.DepartmentId, Classification = assistant.Classification,
                    };
                }, ct);
                return Results.Created($"/api/assistants/{created.Id}", await DetailAsync(admin, created, ct));
            })
            .RequireAuthorization(AdminPolicies.Edit).WithName("CreateAssistant").WithSummary("Create an assistant with a draft that starts from the default instructions");

        assistants.MapPut("/{id:guid}", async (Guid id, AssistantUpdateRequest request, AdminContext admin, GatewayModelCheck models, CancellationToken ct) =>
            {
                var saved = await admin.WriteAsync(async attempt =>
                {
                    var assistant = await LoadAsync(admin, id, attempt);
                    admin.ExpectVersion(assistant, request.Version);
                    await CheckTeamAsync(admin, assistant.DepartmentId, request.OwnerTeamId, attempt);
                    var before = AssistantAudit.Of(assistant);
                    var levelChanged = request.Classification != assistant.Classification;
                    assistant.Name = request.Name.Trim();
                    assistant.OwnerTeamId = request.OwnerTeamId;
                    assistant.Classification = request.Classification;
                    request.ApplyTo(assistant);
                    assistant.UpdatedAt = admin.Now;
                    assistant.UpdatedBy = admin.Actor;
                    if (levelChanged && assistant.PublishedVersionId is { } published)
                    {
                        // Dependants are re-checked against the published version (admin design §4.6): V3, V7, V9 and V8.
                        var version = await admin.Db.AssistantVersions.AsNoTracking().SingleAsync(v => v.Id == published, attempt);
                        var audience = await AudienceClientsAsync(admin, assistant.Id, attempt);
                        Refuse(await AssistantRules.CheckAsync(admin, assistant, version, audience, attempt));
                        await models.EnsureServableAsync(version.GatewayModel!, AssistantRules.Ceiling(assistant, audience), attempt);
                    }

                    return new AdminChange<Assistant>(assistant, "update", AuditEntityTypes.Assistant, assistant.Id, before, AssistantAudit.Of(assistant))
                    {
                        DepartmentId = assistant.DepartmentId, Classification = assistant.Classification,
                    };
                }, ct);
                return await DetailAsync(admin, saved, ct);
            })
            .RequireAuthorization(AdminPolicies.Edit).WithName("UpdateAssistant").WithSummary("Change the assistant's own fields (a level change re-checks V3, V7, V8, V9)");

        assistants.MapGet("/{id:guid}/draft", async (Guid id, AdminContext admin, CancellationToken ct) =>
            {
                var assistant = await LoadAsync(admin, id, ct, tracked: false);
                var draft = assistant.DraftVersionId is { } d ? await admin.Db.AssistantVersions.AsNoTracking().SingleAsync(v => v.Id == d, ct) : throw AdminFaults.Of(AdminErrorCodes.NotFound);
                return DraftDto.Of(draft, await WarningsAsync(admin, assistant, draft, ct));
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("GetAssistantDraft").WithSummary("The draft with the V-rules as warnings");

        assistants.MapPut("/{id:guid}/draft", async (Guid id, DraftRequest request, AdminContext admin, CancellationToken ct) =>
            {
                var (assistant, draft) = await admin.WriteAsync(async attempt =>
                {
                    var assistant = await LoadAsync(admin, id, attempt);
                    AssistantVersion draft;
                    if (assistant.DraftVersionId is { } existing)
                    {
                        draft = await admin.Db.AssistantVersions.SingleAsync(v => v.Id == existing, attempt);
                        if (request.Version is null)
                        {
                            throw AdminFaults.Field("version", AdminErrorCodes.InvalidValue);
                        }

                        admin.ExpectVersion(draft, request.Version);
                    }
                    else
                    {
                        // The first edit after a publish: a copy of the published version with the next number.
                        var published = assistant.PublishedVersionId is { } p ? await admin.Db.AssistantVersions.AsNoTracking().SingleAsync(v => v.Id == p, attempt) : null;
                        var next = await admin.Db.AssistantVersions.AsNoTracking().Where(v => v.AssistantId == id).MaxAsync(v => (int?)v.Number, attempt) ?? 0;
                        draft = new AssistantVersion
                        {
                            AssistantId = id, Number = next + 1, GatewayModel = published?.GatewayModel, SystemPrompt = published?.SystemPrompt ?? "",
                            QuerySteps = published?.QuerySteps ?? "{}", NotFound = published?.NotFound ?? "{}", CollectionLinks = published?.CollectionLinks ?? [],
                            CreatedBy = admin.Actor, CreatedAt = admin.Now,
                        };
                        admin.Db.AssistantVersions.Add(draft);
                        await admin.Db.SaveChangesAsync(attempt);
                        assistant.DraftVersionId = draft.Id;
                        assistant.UpdatedAt = admin.Now;
                        assistant.UpdatedBy = admin.Actor;
                    }

                    request.ApplyTo(draft);
                    draft.UpdatedAt = admin.Now;
                    draft.UpdatedBy = admin.Actor;
                    return new AdminChange<(Assistant, AssistantVersion)>((assistant, draft), "draft", AuditEntityTypes.Assistant, assistant.Id, null, DraftAudit.Of(draft))
                    {
                        DepartmentId = assistant.DepartmentId, Classification = assistant.Classification,
                    };
                }, ct);
                return DraftDto.Of(draft, await WarningsAsync(admin, assistant, draft, ct));
            })
            .RequireAuthorization(AdminPolicies.Edit).WithName("SaveAssistantDraft").WithSummary("Save the draft (created on the first edit); V-rules come back as warnings");

        assistants.MapDelete("/{id:guid}/draft", async (Guid id, [Microsoft.AspNetCore.Mvc.FromQuery] string? version, AdminContext admin, CancellationToken ct) =>
            {
                await admin.WriteAsync(async attempt =>
                {
                    var assistant = await LoadAsync(admin, id, attempt);
                    var draft = assistant.DraftVersionId is { } d ? await admin.Db.AssistantVersions.SingleAsync(v => v.Id == d, attempt) : throw AdminFaults.Of(AdminErrorCodes.NotFound);
                    if (assistant.PublishedVersionId is null)
                    {
                        throw AdminFaults.Of(AdminErrorCodes.DraftIsOnlyVersion);
                    }

                    admin.ExpectVersion(draft, version);
                    assistant.DraftVersionId = null;
                    assistant.UpdatedAt = admin.Now;
                    assistant.UpdatedBy = admin.Actor;
                    await admin.Db.SaveChangesAsync(attempt);
                    admin.Db.AssistantVersions.Remove(draft);
                    return new AdminChange<bool>(true, "draft-discard", AuditEntityTypes.Assistant, assistant.Id, DraftAudit.Of(draft), null)
                    {
                        DepartmentId = assistant.DepartmentId, Classification = assistant.Classification,
                    };
                }, ct);
                return Results.NoContent();
            })
            .RequireAuthorization(AdminPolicies.Edit).WithName("DiscardAssistantDraft").WithSummary("Drop the draft (not when it is the only version)");

        assistants.MapPost("/{id:guid}/publish", async (Guid id, PublishRequest request, AdminContext admin, GatewayModelCheck models, CancellationToken ct) =>
            {
                var saved = await admin.WriteAsync(async attempt =>
                {
                    var assistant = await LoadAsync(admin, id, attempt);
                    admin.ExpectVersion(assistant, request.Version);
                    var draft = assistant.DraftVersionId is { } d ? await admin.Db.AssistantVersions.SingleAsync(v => v.Id == d, attempt) : throw AdminFaults.Of(AdminErrorCodes.NoDraft);
                    admin.ExpectVersion(draft, request.DraftVersion);
                    var published = assistant.PublishedVersionId is { } p ? await admin.Db.AssistantVersions.SingleAsync(v => v.Id == p, attempt) : null;
                    if (published is not null && draft.Number < published.Number && !request.ConfirmBaseChanged)
                    {
                        throw AdminFaults.Of(AdminErrorCodes.DraftBaseChanged);
                    }

                    var audience = await AudienceClientsAsync(admin, id, attempt);
                    Refuse(await AssistantRules.CheckAsync(admin, assistant, draft, audience, attempt, publishing: true));
                    await models.EnsureServableAsync(draft.GatewayModel!, AssistantRules.Ceiling(assistant, audience), attempt);

                    var before = AssistantAudit.Of(assistant);
                    if (published is not null)
                    {
                        published.State = AssistantVersionStates.Superseded;
                        await admin.Db.SaveChangesAsync(attempt);
                    }

                    draft.State = AssistantVersionStates.Published;
                    draft.PublishedAt = admin.Now;
                    draft.PublishedBy = admin.Actor;
                    assistant.PublishedVersionId = draft.Id;
                    assistant.DraftVersionId = null;
                    assistant.UpdatedAt = admin.Now;
                    assistant.UpdatedBy = admin.Actor;
                    return new AdminChange<Assistant>(assistant, "publish", AuditEntityTypes.Assistant, assistant.Id, before,
                        new { assistant = AssistantAudit.Of(assistant), version = DraftAudit.Of(draft) })
                    {
                        DepartmentId = assistant.DepartmentId, Classification = assistant.Classification,
                    };
                }, ct);
                return await DetailAsync(admin, saved, ct);
            })
            .RequireAuthorization(AdminPolicies.Edit).WithName("PublishAssistant").WithSummary("Publish the draft (V34, assistant rules, V8 at the gateway)");

        assistants.MapPut("/{id:guid}/audiences", async (Guid id, AudiencesRequest request, AdminContext admin, GatewayModelCheck models, CancellationToken ct) =>
            {
                var saved = await admin.WriteAsync(async attempt =>
                {
                    var assistant = await LoadAsync(admin, id, attempt);
                    admin.ExpectVersion(assistant, request.Version);
                    var ids = request.ClientIds.Distinct().ToList();
                    var clients = await admin.Db.Clients.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(attempt);
                    if (clients.Count != ids.Count)
                    {
                        throw AdminFaults.Field("clientIds", AdminErrorCodes.InvalidValue);
                    }

                    if (assistant.PublishedVersionId is { } published)
                    {
                        var version = await admin.Db.AssistantVersions.AsNoTracking().SingleAsync(v => v.Id == published, attempt);
                        Refuse(await AssistantRules.CheckAsync(admin, assistant, version, clients, attempt));
                        await models.EnsureServableAsync(version.GatewayModel!, AssistantRules.Ceiling(assistant, clients), attempt);
                    }
                    else
                    {
                        Refuse(await AssistantRules.AudienceRulesAsync(admin, assistant, clients, attempt));
                    }

                    var current = await admin.Db.AssistantAudiences.Where(a => a.AssistantId == id).ToListAsync(attempt);
                    var before = current.Select(a => a.SubjectId).ToList();
                    admin.Db.AssistantAudiences.RemoveRange(current.Where(a => a.Kind != AssistantAudienceKinds.Client || !ids.Contains(a.SubjectId ?? Guid.Empty)));
                    foreach (var client in ids.Where(c => !current.Any(a => a.Kind == AssistantAudienceKinds.Client && a.SubjectId == c)))
                    {
                        admin.Db.AssistantAudiences.Add(new AssistantAudience { AssistantId = id, Kind = AssistantAudienceKinds.Client, SubjectId = client });
                    }

                    // The aggregate root's stamp moves its xmin, so a concurrent audience save conflicts (admin design §4.3).
                    assistant.UpdatedAt = admin.Now;
                    assistant.UpdatedBy = admin.Actor;
                    return new AdminChange<Assistant>(assistant, "audiences", AuditEntityTypes.Assistant, assistant.Id, new { clientIds = before }, new { clientIds = ids })
                    {
                        DepartmentId = assistant.DepartmentId, Classification = assistant.Classification,
                    };
                }, ct);
                return await DetailAsync(admin, saved, ct);
            })
            .RequireAuthorization(AdminPolicies.Manage).WithName("SetAssistantAudiences").WithSummary("The clients that offer the assistant (V7, V8, V9)");

        assistants.MapGet("/{id:guid}/versions", async (Guid id, AdminContext admin, CancellationToken ct) =>
            {
                _ = await LoadAsync(admin, id, ct, tracked: false);
                var versions = await admin.Db.AssistantVersions.AsNoTracking().Where(v => v.AssistantId == id).OrderByDescending(v => v.Number).ToListAsync(ct);
                var numbers = versions.ToDictionary(v => v.Id, v => v.Number);
                return versions.Select(v => VersionSummary.Of(v, numbers)).ToList();
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("ListAssistantVersions").WithSummary("Every version, newest first: state, who and when, restored-from");

        assistants.MapGet("/{id:guid}/versions/{number}", async (Guid id, string number, AdminContext admin, CancellationToken ct) =>
            {
                _ = await LoadAsync(admin, id, ct, tracked: false);
                var version = await VersionAsync(admin, id, number, ct);
                var restoredFrom = version.RestoredFromVersionId is { } from
                    ? await admin.Db.AssistantVersions.AsNoTracking().Where(v => v.Id == from).Select(v => (int?)v.Number).SingleOrDefaultAsync(ct)
                    : null;
                return VersionDto.Of(version, restoredFrom);
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("GetAssistantVersion").WithSummary("One version's settings");

        assistants.MapPost("/{id:guid}/versions/{number}/restore", async (Guid id, string number, VersionRequest request, AdminContext admin, GatewayModelCheck models,
                CancellationToken ct) =>
            {
                var saved = await admin.WriteAsync(async attempt =>
                {
                    var assistant = await LoadAsync(admin, id, attempt);
                    admin.ExpectVersion(assistant, request.Version);
                    var source = await VersionAsync(admin, id, number, attempt);
                    if (source.State == AssistantVersionStates.Draft || source.Id == assistant.PublishedVersionId)
                    {
                        // Only an earlier published version is restored; the draft is published, the current one is live already.
                        throw AdminFaults.Of(AdminErrorCodes.VersionNotRestorable, new Dictionary<string, string> { ["number"] = source.Number.ToString(CultureInfo.InvariantCulture) });
                    }

                    var next = await admin.Db.AssistantVersions.Where(v => v.AssistantId == id).MaxAsync(v => v.Number, attempt) + 1;
                    var restored = new AssistantVersion
                    {
                        AssistantId = id, Number = next, State = AssistantVersionStates.Published, GatewayModel = source.GatewayModel, SystemPrompt = source.SystemPrompt,
                        QuerySteps = source.QuerySteps, NotFound = source.NotFound, CollectionLinks = source.CollectionLinks, RestoredFromVersionId = source.Id,
                        CreatedAt = admin.Now, CreatedBy = admin.Actor, UpdatedAt = admin.Now, UpdatedBy = admin.Actor, PublishedAt = admin.Now, PublishedBy = admin.Actor,
                    };

                    // Exactly as publish (admin design §4.5): V34, then every assistant rule as refusals and V8's /v1/models call.
                    var audience = await AudienceClientsAsync(admin, id, attempt);
                    Refuse(await AssistantRules.CheckAsync(admin, assistant, restored, audience, attempt, publishing: true));
                    await models.EnsureServableAsync(restored.GatewayModel!, AssistantRules.Ceiling(assistant, audience), attempt);

                    var before = AssistantAudit.Of(assistant);
                    if (assistant.PublishedVersionId is { } current)
                    {
                        var published = await admin.Db.AssistantVersions.SingleAsync(v => v.Id == current, attempt);
                        published.State = AssistantVersionStates.Superseded;
                        await admin.Db.SaveChangesAsync(attempt);
                    }

                    // An open draft is kept; its base is now older, so its publish asks for confirmation (draft_base_changed).
                    admin.Db.AssistantVersions.Add(restored);
                    assistant.PublishedVersionId = restored.Id;
                    assistant.UpdatedAt = admin.Now;
                    assistant.UpdatedBy = admin.Actor;
                    return new AdminChange<Assistant>(assistant, "restore", AuditEntityTypes.Assistant, assistant.Id, before,
                        new { assistant = AssistantAudit.Of(assistant), version = DraftAudit.Of(restored), restoredFrom = source.Number })
                    {
                        DepartmentId = assistant.DepartmentId, Classification = assistant.Classification,
                    };
                }, ct);
                return await DetailAsync(admin, saved, ct);
            })
            .RequireAuthorization(AdminPolicies.Edit).WithName("RestoreAssistantVersion").WithSummary("Publish a new version with an earlier version's settings (as publish)");

        assistants.MapPost("/{id:guid}/archive", (Guid id, VersionRequest request, AdminContext admin, CancellationToken ct) =>
                SetStateAsync(admin, id, request, AssistantStates.Archived, ct))
            .RequireAuthorization(AdminPolicies.Edit).WithName("ArchiveAssistant").WithSummary("Hide the assistant from every client; versions, audiences and statistics are kept");

        assistants.MapPost("/{id:guid}/unarchive", (Guid id, VersionRequest request, AdminContext admin, CancellationToken ct) =>
                SetStateAsync(admin, id, request, AssistantStates.Active, ct))
            .RequireAuthorization(AdminPolicies.Edit).WithName("UnarchiveAssistant").WithSummary("Offer the assistant again with the same published version");

        assistants.MapPut("/{id:guid}/retention", async (Guid id, RetentionRequest request, AdminContext admin, CancellationToken ct) =>
            {
                var saved = await admin.WriteAsync(async attempt =>
                {
                    var assistant = await LoadAsync(admin, id, attempt);
                    admin.ExpectVersion(assistant, request.Version);
                    var policy = await admin.Db.DataProtectionPolicies.AsNoTracking().SingleOrDefaultAsync(p => p.Classification == assistant.Classification, attempt)
                        ?? DataProtectionEndpoints.Defaults(assistant.Classification);
                    // V10: a mode the level allows (and the schema accepts: none until A1), at most the level's maximum days.
                    if (!policy.AllowedHistoryModes.Contains(request.HistoryMode) || !HistoryModes.All.Contains(request.HistoryMode) ||
                        request.RetentionDays > policy.RetentionMaxDays)
                    {
                        throw AdminFaults.Of(AdminErrorCodes.RetentionOutsidePolicy, new Dictionary<string, string>
                        {
                            ["level"] = assistant.Classification, ["modes"] = string.Join(", ", policy.AllowedHistoryModes.Where(HistoryModes.All.Contains)),
                            ["maxDays"] = policy.RetentionMaxDays.ToString(CultureInfo.InvariantCulture),
                        });
                    }

                    var before = new { assistant.HistoryMode, assistant.RetentionDays };
                    assistant.HistoryMode = request.HistoryMode;
                    assistant.RetentionDays = request.RetentionDays;
                    assistant.UpdatedAt = admin.Now;
                    assistant.UpdatedBy = admin.Actor;
                    return new AdminChange<Assistant>(assistant, "retention", AuditEntityTypes.Assistant, assistant.Id, before,
                        new { assistant.HistoryMode, assistant.RetentionDays })
                    {
                        DepartmentId = assistant.DepartmentId, Classification = assistant.Classification,
                    };
                }, ct);
                return await DetailAsync(admin, saved, ct);
            })
            .RequireAuthorization(AdminPolicies.Manage).WithName("SetAssistantRetention").WithSummary("History mode and retention days within the level's policy (V10)");

        return api;
    }

    /// <summary>Version <paramref name="number"/> of the assistant, or 404 (the number is parsed here, so any route value reaches the policy first).</summary>
    private static async Task<AssistantVersion> VersionAsync(AdminContext admin, Guid assistantId, string number, CancellationToken ct)
    {
        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            throw AdminFaults.Of(AdminErrorCodes.NotFound);
        }

        return await admin.Db.AssistantVersions.AsNoTracking().SingleOrDefaultAsync(v => v.AssistantId == assistantId && v.Number == n, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
    }

    /// <summary>Archive or unarchive (admin design §4.5): only the state changes; an unchanged state is answered without a write.</summary>
    private static async Task<AssistantDto> SetStateAsync(AdminContext admin, Guid id, VersionRequest request, string state, CancellationToken ct)
    {
        var saved = await admin.WriteAsync(async attempt =>
        {
            var assistant = await LoadAsync(admin, id, attempt);
            admin.ExpectVersion(assistant, request.Version);
            var before = AssistantAudit.Of(assistant);
            assistant.State = state;
            assistant.UpdatedAt = admin.Now;
            assistant.UpdatedBy = admin.Actor;
            return new AdminChange<Assistant>(assistant, state == AssistantStates.Archived ? "archive" : "unarchive", AuditEntityTypes.Assistant, assistant.Id, before,
                AssistantAudit.Of(assistant))
            {
                DepartmentId = assistant.DepartmentId, Classification = assistant.Classification,
            };
        }, ct);
        return await DetailAsync(admin, saved, ct);
    }

    private static IQueryable<Assistant> Scoped(AdminContext admin)
    {
        var departments = admin.Departments().Select(d => d.Id);
        return admin.Db.Assistants.Where(a => departments.Contains(a.DepartmentId));
    }

    private static async Task<Assistant> LoadAsync(AdminContext admin, Guid id, CancellationToken ct, bool tracked = true) =>
        await (tracked ? Scoped(admin) : Scoped(admin).AsNoTracking()).SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);

    private static async Task CheckTeamAsync(AdminContext admin, Guid departmentId, Guid? teamId, CancellationToken ct)
    {
        if (teamId is { } team && !await admin.Db.Teams.AnyAsync(t => t.Id == team && t.DepartmentId == departmentId, ct))
        {
            throw AdminFaults.Field("ownerTeamId", AdminErrorCodes.InvalidValue);
        }
    }

    private static Task<List<Client>> AudienceClientsAsync(AdminContext admin, Guid assistantId, CancellationToken ct)
    {
        var ids = admin.Db.AssistantAudiences.Where(a => a.AssistantId == assistantId && a.Kind == AssistantAudienceKinds.Client).Select(a => a.SubjectId);
        return admin.Db.Clients.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct);
    }

    private static async Task<IReadOnlyList<RuleProblem>> WarningsAsync(AdminContext admin, Assistant assistant, AssistantVersion draft, CancellationToken ct) =>
        await AssistantRules.CheckAsync(admin, assistant, draft, await AudienceClientsAsync(admin, assistant.Id, ct), ct, publishing: true);

    /// <summary>The first problem as the refusal (its code and params), with every problem in <c>problems</c>.</summary>
    private static void Refuse(IReadOnlyList<RuleProblem> problems)
    {
        if (problems.Count == 0)
        {
            return;
        }

        var first = problems[0];
        throw AdminFaults.Of(first.Code, first.Params, new Dictionary<string, object?> { ["problems"] = problems.Select(p => new { code = p.Code.Code, p.Params, p.Affected }).ToList(), ["affected"] = first.Affected });
    }

    private static async Task<AssistantDto> DetailAsync(AdminContext admin, Assistant a, CancellationToken ct)
    {
        var versions = await admin.Db.AssistantVersions.AsNoTracking().Where(v => v.Id == a.PublishedVersionId || v.Id == a.DraftVersionId)
            .Select(v => new { v.Id, v.Number, v.UpdatedAt, v.Version, v.QuerySteps }).ToListAsync(ct);
        var published = versions.SingleOrDefault(v => v.Id == a.PublishedVersionId);
        var draft = versions.SingleOrDefault(v => v.Id == a.DraftVersionId);
        var audiences = await admin.Db.AssistantAudiences.AsNoTracking().Where(x => x.AssistantId == a.Id && x.Kind == AssistantAudienceKinds.Client)
            .Select(x => x.SubjectId!.Value).ToListAsync(ct);
        return new AssistantDto(a.Id, a.Slug, a.Name, a.Description, a.ContactName, a.ContactEmail, a.DepartmentId, a.OwnerTeamId, a.Classification, a.HistoryMode,
            a.RetentionDays, a.State, published?.Number,
            draft is null ? null : new DraftInfo(draft.Number, draft.UpdatedAt, published is not null && draft.Number < published.Number, draft.Version.ToString(CultureInfo.InvariantCulture)),
            audiences, a.UpdatedAt, a.UpdatedBy, a.Version.ToString(CultureInfo.InvariantCulture), published is null ? null : GeneralKnowledgeOf(published.QuerySteps));
    }

    /// <summary>
    /// <c>Settings.Chat.defaultSystemPrompt</c> (P1b item 39): the saved text, also when an admin emptied it; the code default
    /// when the key is missing (a <c>Settings</c> row from before the key existed, or none yet).
    /// </summary>
    public static async Task<string> DefaultSystemPromptAsync(AdminContext admin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        var chat = await admin.Db.Settings.AsNoTracking().Select(s => s.Chat).SingleOrDefaultAsync(ct);
        var value = (JsonNode.Parse(string.IsNullOrWhiteSpace(chat) ? "{}" : chat) as JsonObject)?[ChatDefaults.SystemPromptKey];
        return value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : ChatDefaults.SystemPrompt;
    }

    /// <summary>A version's <c>answer.generalKnowledge</c> (admin design §5.5.4 badge): the stored value, <c>off</c> when unset or unknown.</summary>
    public static string GeneralKnowledgeOf(string querySteps) =>
        Ume.Rag.Answering.Chat.GeneralKnowledgeLevels.ToValue(Ume.Rag.Answering.Chat.AssistantChatSettings.Parse(querySteps).GeneralKnowledge);
}

/// <summary>A broken rule: its code and template parameters, and the objects it names.</summary>
public sealed record RuleProblem(AdminErrorCode Code, IReadOnlyDictionary<string, string>? Params, IReadOnlyList<string> Affected);

/// <summary>
/// The assistant rules of P1a (admin design §4.6): V3 (level ≥ every linked collection), V7 (every audience client's ceiling
/// ≥ the level), V8's ceiling (the ladder of max(level, ceilings of the audience clients that store history)), V9
/// (<c>sensitive</c> only for clients without history when the policy says so), V11 (≤ 20 collections, each of the
/// assistant's department or shared with it, ≤ 20 topics per link in the chunk tag format), V15 (not-found texts and links), V17 (query step ranges), V34 (the definition resolves;
/// description and contact set) and V36 (a <c>public</c> assistant keeps <c>answer.generalKnowledge</c> <c>off</c>). Returned as problems: refusals on publish, warnings on draft save.
/// </summary>
public static class AssistantRules
{
    /// <summary>The residency ceiling V8 checks the model at.</summary>
    public static Residency Ceiling(Assistant assistant, IReadOnlyList<Client> audience)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(audience);
        var level = audience.Where(c => c.StoresHistory).Select(c => c.ClassificationCeiling).Append(assistant.Classification).MaxBy(Levels.Rank)!;
        return ResidencyCeiling.ForLevel(level);
    }

    public static async Task<IReadOnlyList<RuleProblem>> CheckAsync(AdminContext admin, Assistant assistant, AssistantVersion version, IReadOnlyList<Client> audience,
        CancellationToken ct, bool publishing = false)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(version);
        var problems = new List<RuleProblem>();

        // V34: the definition resolves; description and contact are set.
        if (publishing)
        {
            if (string.IsNullOrWhiteSpace(assistant.Description) || string.IsNullOrWhiteSpace(assistant.ContactName) || string.IsNullOrWhiteSpace(assistant.ContactEmail))
            {
                problems.Add(new(AdminErrorCodes.AssistantContactMissing, null, []));
            }

            if (string.IsNullOrWhiteSpace(version.GatewayModel) || !await admin.Db.GatewayModels.AsNoTracking().AnyAsync(m => m.Name == version.GatewayModel, ct))
            {
                problems.Add(new(AdminErrorCodes.VersionLinkMissing, new Dictionary<string, string> { ["link"] = "model " + (version.GatewayModel ?? "(none)") }, []));
            }
        }

        // V11 and V34: ≤ 20 collections, every one live and of the assistant's department or shared with it (admin design §3.2).
        var ids = version.LinkedCollectionIds().Distinct().ToList();
        if (ids.Count > AssistantVersion.MaxCollections)
        {
            problems.Add(new(AdminErrorCodes.TooManyCollections, null, []));
        }

        var collections = await admin.Db.Collections.AsNoTracking().Where(c => ids.Contains(c.Id) && c.DeletedAt == null).ToListAsync(ct);
        var missing = ids.Except(collections.Select(c => c.Id)).ToList();
        if (missing.Count > 0)
        {
            problems.Add(new(AdminErrorCodes.VersionLinkMissing, new Dictionary<string, string> { ["link"] = "collection" }, [.. missing.Select(m => m.ToString())]));
        }

        var linkable = await CollectionSharing.LinkableAsync(admin.Db, assistant.DepartmentId, ids, ct);
        var foreign = collections.Where(c => !linkable.Contains(c.Id)).Select(c => c.Name).ToList();
        if (foreign.Count > 0)
        {
            problems.Add(new(AdminErrorCodes.CollectionNotShared, null, foreign));
        }

        // V11: ≤ 20 topics per link, each in the chunk tag format (processing design §7).
        var names = collections.ToDictionary(c => c.Id, c => c.Name);
        var badTopics = version.CollectionLinks.Where(l => l.Tags.Count > CollectionLink.MaxTags || !l.Tags.All(TopicTags.IsValid))
            .Select(l => names.GetValueOrDefault(l.CollectionId) ?? l.CollectionId.ToString()).Distinct().ToList();
        if (badTopics.Count > 0)
        {
            problems.Add(new(AdminErrorCodes.TopicInvalid, null, badTopics));
        }

        if (publishing && ids.Count == 0)
        {
            problems.Add(new(AdminErrorCodes.VersionLinkMissing, new Dictionary<string, string> { ["link"] = "collection" }, []));
        }

        // V3: the assistant's level is at least every linked collection's.
        var above = collections.Where(c => Levels.Rank(c.Classification) > Levels.Rank(assistant.Classification)).ToList();
        if (above.Count > 0)
        {
            problems.Add(new(AdminErrorCodes.AssistantBelowCollection,
                new Dictionary<string, string> { ["collection"] = above[0].Name, ["level"] = above[0].Classification }, [.. above.Select(c => c.Name)]));
        }

        problems.AddRange(await AudienceRulesAsync(admin, assistant, audience, ct));
        problems.AddRange(QuerySteps(version.QuerySteps));
        problems.AddRange(GeneralKnowledgeForLevel(assistant, version.QuerySteps));
        problems.AddRange(await NotFoundAsync(admin, version.NotFound, ct));
        return problems;
    }

    /// <summary>V7 and V9 for an audience.</summary>
    public static async Task<IReadOnlyList<RuleProblem>> AudienceRulesAsync(AdminContext admin, Assistant assistant, IReadOnlyList<Client> audience, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(audience);
        var problems = new List<RuleProblem>();
        var below = audience.Where(c => Levels.Rank(c.ClassificationCeiling) < Levels.Rank(assistant.Classification)).Select(c => c.Name).ToList();
        if (below.Count > 0)
        {
            problems.Add(new(AdminErrorCodes.AudienceAboveCeiling, new Dictionary<string, string> { ["level"] = assistant.Classification }, below));
        }

        if (assistant.Classification == Classification.Sensitive && audience.Any(c => c.StoresHistory))
        {
            var policy = await admin.Db.DataProtectionPolicies.AsNoTracking().SingleOrDefaultAsync(p => p.Classification == Classification.Sensitive, ct)
                ?? DataProtectionEndpoints.Defaults(Classification.Sensitive);
            if (policy.ClientsWithoutHistoryOnly)
            {
                problems.Add(new(AdminErrorCodes.ClientStoresHistory, null, [.. audience.Where(c => c.StoresHistory).Select(c => c.Name)]));
            }
        }

        return problems;
    }

    /// <summary>V17 (chat design §5–§6, §10.3): the ranges of the query steps that exist in P1a.</summary>
    private static IEnumerable<RuleProblem> QuerySteps(string json)
    {
        var steps = JsonNode.Parse(json) as JsonObject ?? [];
        var problems = new List<string>();
        Check(steps["retrieval"]?["topK"], 1, 50, "retrieval.topK");
        Check(steps["history"]?["maxMessages"], 0, 20, "history.maxMessages");
        Check(steps["history"]?["maxTokens"], 0, 8000, "history.maxTokens");
        Check(steps["answer"]?["maxOutputTokens"], 64, 4096, "answer.maxOutputTokens");
        Check(steps["answer"]?["temperature"], 0, 1, "answer.temperature");
        if (steps["answer"]?["language"] is JsonValue language && language.GetValueKind() == JsonValueKind.String && language.GetValue<string>() is not ("auto" or "sv"))
        {
            problems.Add("answer.language");
        }

        if (steps["answer"]?["generalKnowledge"] is { } generalKnowledge &&
            !(generalKnowledge is JsonValue g && g.GetValueKind() == JsonValueKind.String && Ume.Rag.Answering.Chat.GeneralKnowledgeLevels.All.Contains(g.GetValue<string>())))
        {
            problems.Add("answer.generalKnowledge");
        }

        return problems.Select(p => new RuleProblem(AdminErrorCodes.QueryStepsInvalid, new Dictionary<string, string> { ["field"] = p }, []));

        void Check(JsonNode? node, double min, double max, string name)
        {
            if (node is null)
            {
                return;
            }

            if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number || v.GetValue<double>() < min || v.GetValue<double>() > max)
            {
                problems.Add(name);
            }
        }
    }

    /// <summary>
    /// V36 (chat design §7.3, §10.4): a <c>public</c> assistant answers only from its sources; <c>supplement</c>,
    /// <c>fallback</c> and <c>open</c> need level <c>internal</c> or higher.
    /// </summary>
    private static IEnumerable<RuleProblem> GeneralKnowledgeForLevel(Assistant assistant, string json)
    {
        var value = (JsonNode.Parse(json) as JsonObject)?["answer"]?["generalKnowledge"] is JsonValue g && g.GetValueKind() == JsonValueKind.String ? g.GetValue<string>() : null;
        if (assistant.Classification == Classification.Public && value is not null && value != Ume.Rag.Answering.Chat.GeneralKnowledgeLevels.Off)
        {
            yield return new RuleProblem(AdminErrorCodes.GeneralKnowledgeNotAllowedForLevel,
                new Dictionary<string, string> { ["level"] = assistant.Classification, ["generalKnowledge"] = value }, []);
        }
    }

    /// <summary>V15 (chat design §9.4, §10.1): texts ≤ 1,000 characters, ≤ 5 links, https, hosts in <c>Settings.Chat.allowedLinkHosts</c>.</summary>
    private static async Task<IEnumerable<RuleProblem>> NotFoundAsync(AdminContext admin, string json, CancellationToken ct)
    {
        var notFound = JsonNode.Parse(json) as JsonObject ?? [];
        var problems = new List<RuleProblem>();
        foreach (var language in new[] { "sv", "en" })
        {
            if (notFound[language] is JsonValue text && text.GetValueKind() == JsonValueKind.String && text.GetValue<string>().Length > 1000)
            {
                problems.Add(new(AdminErrorCodes.QueryStepsInvalid, new Dictionary<string, string> { ["field"] = "notFound." + language }, []));
            }
        }

        if (notFound["links"] is not JsonArray links || links.Count == 0)
        {
            return problems;
        }

        var chat = JsonNode.Parse(await admin.Db.Settings.AsNoTracking().Select(s => s.Chat).SingleOrDefaultAsync(ct) ?? "{}") as JsonObject ?? [];
        var allowed = (chat["allowedLinkHosts"] as JsonArray)?.Select(h => h?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var bad = new List<string>();
        foreach (var link in links.Take(6))
        {
            var url = link?["url"]?.GetValue<string>();
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !allowed.Contains(uri.Host))
            {
                bad.Add(url ?? "(none)");
            }
        }

        if (links.Count > 5 || bad.Count > 0)
        {
            problems.Add(new(AdminErrorCodes.LinkHostNotAllowed, null, bad));
        }

        return problems;
    }
}

/// <summary><see cref="GeneralKnowledge"/>: the published version's <c>answer.generalKnowledge</c>, null before the first publish.</summary>
public sealed record AssistantSummary(Guid Id, string Slug, string Name, Guid DepartmentId, string Classification, string State, bool Published, bool HasDraft,
    string? GeneralKnowledge);

public sealed record DraftInfo(int Number, DateTimeOffset UpdatedAt, bool BaseChanged, string Version);

public sealed record AssistantDto(
    Guid Id, string Slug, string Name, string? Description, string? ContactName, string? ContactEmail, Guid DepartmentId, Guid? OwnerTeamId, string Classification,
    string HistoryMode, int RetentionDays, string State, int? PublishedVersion, DraftInfo? Draft, IReadOnlyList<Guid> AudienceClientIds, DateTimeOffset UpdatedAt,
    string? UpdatedBy, string Version, string? GeneralKnowledge);

public sealed record DraftDto(int Number, string? GatewayModel, string SystemPrompt, JsonElement QuerySteps, JsonElement NotFound, IReadOnlyList<CollectionLink> CollectionLinks,
    IReadOnlyList<object> Warnings, DateTimeOffset UpdatedAt, string Version)
{
    public static DraftDto Of(AssistantVersion v, IReadOnlyList<RuleProblem> warnings)
    {
        ArgumentNullException.ThrowIfNull(v);
        using var steps = JsonDocument.Parse(v.QuerySteps);
        using var notFound = JsonDocument.Parse(v.NotFound);
        return new(v.Number, v.GatewayModel, v.SystemPrompt, steps.RootElement.Clone(), notFound.RootElement.Clone(), v.CollectionLinks,
            [.. warnings.Select(w => (object)new { code = w.Code.Code, @params = w.Params, affected = w.Affected })], v.UpdatedAt, v.Version.ToString(CultureInfo.InvariantCulture));
    }
}

public sealed record AssistantAudit(
    Guid Id, string Slug, string Name, string? Description, string? ContactName, string? ContactEmail, Guid DepartmentId, Guid? OwnerTeamId, string Classification,
    string State, Guid? PublishedVersionId, Guid? DraftVersionId)
{
    public static AssistantAudit Of(Assistant a) => new(a.Id, a.Slug, a.Name, a.Description, a.ContactName, a.ContactEmail, a.DepartmentId, a.OwnerTeamId,
        a.Classification, a.State, a.PublishedVersionId, a.DraftVersionId);
}

public sealed record DraftAudit(
    Guid Id, int Number, string State, string? GatewayModel, string SystemPrompt, string QuerySteps, string NotFound, IReadOnlyList<CollectionLink> CollectionLinks)
{
    public static DraftAudit Of(AssistantVersion v) => new(v.Id, v.Number, v.State, v.GatewayModel, v.SystemPrompt, v.QuerySteps, v.NotFound, v.CollectionLinks);
}

public record AssistantFields : AdminRequest, IValidatableObject
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string Name { get; init; } = "";

    public Guid? OwnerTeamId { get; init; }

    [Required]
    public string Classification { get; init; } = "";

    [StringLength(500)]
    public string? Description { get; init; }

    [StringLength(200)]
    public string? ContactName { get; init; }

    [StringLength(254), EmailAddress]
    public string? ContactEmail { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Domain.Classification.IsValid(Classification))
        {
            yield return new ValidationResult($"The level must be one of {string.Join(", ", Domain.Classification.All)}.", [nameof(Classification)]);
        }
    }

    public void ApplyTo(Assistant assistant)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        assistant.Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
        assistant.ContactName = string.IsNullOrWhiteSpace(ContactName) ? null : ContactName.Trim();
        assistant.ContactEmail = string.IsNullOrWhiteSpace(ContactEmail) ? null : ContactEmail.Trim();
    }
}

public sealed record AssistantCreateRequest : AssistantFields
{
    [Required]
    public Guid DepartmentId { get; init; }

    [StringLength(100), RegularExpression(Slugs.Pattern)]
    public string? Slug { get; init; }
}

public sealed record AssistantUpdateRequest : AssistantFields
{
    [Required]
    public string? Version { get; init; }
}

public sealed record DraftRequest : AdminRequest
{
    [StringLength(200)]
    public string? GatewayModel { get; init; }

    [StringLength(20_000)]
    public string SystemPrompt { get; init; } = "";

    /// <summary>Chat design §15.2: condense, retrieval, history, answer.</summary>
    public JsonElement? QuerySteps { get; init; }

    /// <summary>Chat design §10.1: <c>sv</c>, <c>en</c>, <c>links[]{title,url}</c>.</summary>
    public JsonElement? NotFound { get; init; }

    /// <summary>The linked collections with their topics (<c>[{ collectionId, tags[] }]</c>; no tags = the whole collection; V11).</summary>
    [MaxLength(AssistantVersion.MaxCollections)]
    public IReadOnlyList<CollectionLinkRequest> CollectionLinks { get; init; } = [];

    /// <summary>The draft's <c>version</c>; required once a draft exists (none for the first edit after a publish).</summary>
    public string? Version { get; init; }

    public void ApplyTo(AssistantVersion draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        draft.GatewayModel = string.IsNullOrWhiteSpace(GatewayModel) ? null : GatewayModel.Trim();
        draft.SystemPrompt = SystemPrompt;
        draft.QuerySteps = QuerySteps is { ValueKind: JsonValueKind.Object } steps ? steps.GetRawText() : "{}";
        draft.NotFound = NotFound is { ValueKind: JsonValueKind.Object } notFound ? notFound.GetRawText() : "{}";
        // One link per collection (the first wins); topics trimmed and de-duplicated in order. Their format is V11's, a warning here.
        draft.CollectionLinks = [.. CollectionLinks.Where(l => l is not null).DistinctBy(l => l.CollectionId).Select(l => new CollectionLink(l.CollectionId,
            [.. (l.Tags ?? []).Where(t => t is not null).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal)]))];
    }
}

/// <summary>One collection link of a draft.</summary>
public sealed record CollectionLinkRequest
{
    [Required]
    public Guid CollectionId { get; init; }

    [MaxLength(100)]
    public IReadOnlyList<string>? Tags { get; init; }
}

public sealed record PublishRequest : AdminRequest
{
    [Required]
    public string? Version { get; init; }

    [Required]
    public string? DraftVersion { get; init; }

    public bool ConfirmBaseChanged { get; init; }
}

public sealed record AudiencesRequest : AdminRequest
{
    [MaxLength(50)]
    public IReadOnlyList<Guid> ClientIds { get; init; } = [];

    [Required]
    public string? Version { get; init; }
}

/// <summary>A version in the list: number, state, who and when, and the number it was restored from.</summary>
public sealed record VersionSummary(int Number, string State, DateTimeOffset CreatedAt, string? CreatedBy, DateTimeOffset? PublishedAt, string? PublishedBy, int? RestoredFrom)
{
    public static VersionSummary Of(AssistantVersion v, IReadOnlyDictionary<Guid, int> numbers)
    {
        ArgumentNullException.ThrowIfNull(v);
        ArgumentNullException.ThrowIfNull(numbers);
        return new(v.Number, v.State, v.CreatedAt, v.CreatedBy, v.PublishedAt, v.PublishedBy,
            v.RestoredFromVersionId is { } from && numbers.TryGetValue(from, out var n) ? n : null);
    }
}

/// <summary>One version's settings (the draft's fields, admin design §4.5) with its state and history.</summary>
public sealed record VersionDto(
    int Number, string State, string? GatewayModel, string SystemPrompt, JsonElement QuerySteps, JsonElement NotFound, IReadOnlyList<CollectionLink> CollectionLinks, int? RestoredFrom,
    DateTimeOffset CreatedAt, string? CreatedBy, DateTimeOffset? PublishedAt, string? PublishedBy)
{
    public static VersionDto Of(AssistantVersion v, int? restoredFrom)
    {
        ArgumentNullException.ThrowIfNull(v);
        using var steps = JsonDocument.Parse(v.QuerySteps);
        using var notFound = JsonDocument.Parse(v.NotFound);
        return new(v.Number, v.State, v.GatewayModel, v.SystemPrompt, steps.RootElement.Clone(), notFound.RootElement.Clone(), v.CollectionLinks, restoredFrom, v.CreatedAt,
            v.CreatedBy, v.PublishedAt, v.PublishedBy);
    }
}

public sealed record RetentionRequest : AdminRequest
{
    [Required, StringLength(20)]
    public string HistoryMode { get; init; } = HistoryModes.None;

    [Range(1, 3650)]
    public int RetentionDays { get; init; } = 90;

    [Required]
    public string? Version { get; init; }
}
