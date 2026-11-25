using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;

namespace Kalla.AdminApi;

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
                    var q = Like.Contains(page.Q.Trim());
                    query = query.Where(a => EF.Functions.ILike(a.Name, q, Like.EscapeCharacter) || EF.Functions.ILike(a.Slug, q, Like.EscapeCharacter));
                }

                var total = await query.CountAsync(ct);
                return new PageDto<AssistantSummary>(await SummariesAsync(admin, query.OrderBy(a => a.Name).Skip(page.Skip).Take(page.Take), ct), total);
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("ListAssistants").WithSummary("Assistants of the caller's departments");

        assistants.MapGet("/{id:guid}", async (Guid id, AdminContext admin, CancellationToken ct) =>
                await DetailAsync(admin, await LoadAsync(admin, id, ct, tracked: false), ct))
            .RequireAuthorization(AdminPolicies.Read).WithName("GetAssistant").WithSummary("One assistant: own fields, versions, audiences");

        assistants.MapPost("", async (AssistantCreateRequest request, AdminContext admin, GatewayModelCheck models, CancellationToken ct) =>
            {
                await models.WarmKeyAsync(ResidencyCeiling.ForLevel(request.Classification), request.GatewayKeyId, request.DepartmentId, ct);
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
                    // V39 (admin design §4.6): a key chosen at create is checked for status, department and residency; the
                    // draft that follows has no models yet.
                    Refuse(await GatewayKeyRules.CheckAsync(admin, assistant.GatewayKeyId, assistant.DepartmentId,
                        ResidencyCeiling.ForLevel(assistant.Classification), [], attempt, models));
                    admin.Db.Assistants.Add(assistant);
                    await admin.Db.SaveChangesAsync(attempt);
                    // Created with only a draft: no published version, so no client lists it (admin design §4.5). The draft starts from
                    // the chosen template's instructions, not-found texts and query steps, copied with no link kept (admin design §5
                    // step 9, P2); without one, from the platform's citizen-service instructions (Settings.Chat.defaultSystemPrompt,
                    // P1b item 39).
                    var template = request.TemplateId is { } templateId ? await AssistantTemplateEndpoints.ForNewAssistantAsync(admin, templateId, attempt) : null;
                    var draft = new AssistantVersion
                    {
                        AssistantId = assistant.Id, Number = 1, SystemPrompt = template?.SystemPrompt ?? await DefaultSystemPromptAsync(admin, attempt),
                        NotFound = template?.NotFound ?? "{}", QuerySteps = template?.QuerySteps ?? "{}",
                        CreatedBy = admin.Actor, CreatedAt = admin.Now, UpdatedAt = admin.Now, UpdatedBy = admin.Actor,
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

        // Copy (admin design §4.5, P2; B-55): a new assistant whose only version is a draft equal to the source's published
        // version (its draft when never published), with the source's test questions (B-57). Audiences, the gateway key, the
        // other versions, the test runs and the statistics are not copied. The caller reads the source and edits in the target
        // department; V11/V12 come back as warnings.
        assistants.MapPost("/{id:guid}/copy", async (Guid id, AssistantCopyRequest request, AdminContext admin, CancellationToken ct) =>
            {
                var (copy, draft, dropped) = await admin.WriteAsync(async attempt =>
                {
                    var source = await LoadAsync(admin, id, attempt, tracked: false);
                    if (!await admin.Departments().AnyAsync(d => d.Id == request.DepartmentId, attempt))
                    {
                        throw AdminFaults.Field("departmentId", AdminErrorCodes.NotInScope);
                    }

                    await CheckTeamAsync(admin, request.DepartmentId, request.OwnerTeamId, attempt);
                    var ownerTeam = request.OwnerTeamId ?? await admin.DefaultOwnerTeamAsync(request.DepartmentId, attempt);
                    var slug = string.IsNullOrWhiteSpace(request.Slug) ? Slugs.From(request.Name) : request.Slug;
                    if (await admin.Db.Assistants.AsNoTracking().AnyAsync(a => a.Slug == slug, attempt))
                    {
                        throw AdminFaults.Field("slug", AdminErrorCodes.AlreadyExists);
                    }

                    var from = (source.PublishedVersionId ?? source.DraftVersionId) is { } versionId
                        ? await admin.Db.AssistantVersions.AsNoTracking().SingleAsync(v => v.Id == versionId, attempt)
                        : null;
                    // The assistant's own settings come along; who may use it (audiences, origins) and whose budget it runs on
                    // (the gateway key) do not.
                    var copy = new Assistant
                    {
                        Slug = slug, Name = request.Name.Trim(), Description = source.Description, ContactName = source.ContactName, ContactEmail = source.ContactEmail,
                        DepartmentId = request.DepartmentId, OwnerTeamId = ownerTeam, Classification = source.Classification, HistoryMode = source.HistoryMode,
                        RetentionDays = source.RetentionDays, UpdatedAt = admin.Now, UpdatedBy = admin.Actor,
                    };
                    admin.Db.Assistants.Add(copy);
                    await admin.Db.SaveChangesAsync(attempt);
                    // Only the links the target department may make (V11: its own collections and those shared with it). Another
                    // department's unshared knowledge base is left out, so no test of the copy searches it; a link to a deleted one
                    // comes along as it is (it reaches nothing, and publish refuses it).
                    var sourceLinks = from?.CollectionLinks ?? [];
                    var linkable = await CollectionSharing.LinkableAsync(admin.Db, request.DepartmentId, CollectionLink.Ids(sourceLinks), attempt);
                    var existing = await admin.Db.Collections.AsNoTracking().Where(c => CollectionLink.Ids(sourceLinks).Contains(c.Id) && c.DeletedAt == null)
                        .Select(c => c.Id).ToListAsync(attempt);
                    var links = sourceLinks.Where(l => linkable.Contains(l.CollectionId) || !existing.Contains(l.CollectionId)).ToList();
                    var draft = new AssistantVersion
                    {
                        AssistantId = copy.Id, Number = 1, GatewayModel = from?.GatewayModel, SystemPrompt = from?.SystemPrompt ?? "", QuerySteps = from?.QuerySteps ?? "{}",
                        NotFound = from?.NotFound ?? "{}", CollectionLinks = links,
                        CreatedBy = admin.Actor, CreatedAt = admin.Now, UpdatedAt = admin.Now, UpdatedBy = admin.Actor,
                    };
                    admin.Db.AssistantVersions.Add(draft);
                    // The test questions come along (admin design §4.5, §7.4: the set belongs to the assistant); their runs do not.
                    var questions = await admin.Db.AssistantTestQuestions.AsNoTracking().Where(q => q.AssistantId == source.Id).ToListAsync(attempt);
                    // An expected document in a knowledge base the target department may not link is cleared, so the copy never
                    // names another department's document; one that is merely unlinked (V33 marks it) comes along.
                    var expected = questions.Select(q => q.ExpectedDocumentId).OfType<Guid>().Distinct().ToList();
                    var holders = await (from d in admin.Db.Documents.AsNoTracking()
                                         join s in admin.Db.Sources.AsNoTracking() on d.SourceId equals s.Id
                                         where expected.Contains(d.Id)
                                         select new { d.Id, s.CollectionId }).ToListAsync(attempt);
                    var holderLinkable = await CollectionSharing.LinkableAsync(admin.Db, request.DepartmentId, [.. holders.Select(h => h.CollectionId).Distinct()], attempt);
                    var reachable = holders.Where(h => holderLinkable.Contains(h.CollectionId)).Select(h => h.Id).ToHashSet();
                    admin.Db.AssistantTestQuestions.AddRange(questions.Select(q => new AssistantTestQuestion
                    {
                        AssistantId = copy.Id, Ordinal = q.Ordinal, Question = q.Question, ExpectedBehaviour = q.ExpectedBehaviour,
                        ExpectedDocumentId = q.ExpectedDocumentId is { } document && (reachable.Contains(document) || holders.All(h => h.Id != document)) ? document : null,
                        ExpectedPhrase = q.ExpectedPhrase,
                        CreatedBy = admin.Actor, CreatedAt = admin.Now, UpdatedAt = admin.Now, UpdatedBy = admin.Actor,
                    }));
                    await admin.Db.SaveChangesAsync(attempt);
                    copy.DraftVersionId = draft.Id;
                    var origin = from is null ? $"{source.Slug}" : $"{source.Slug} version {from.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                    var left = new CopyLeftOut(sourceLinks.Count - links.Count,
                        questions.Count(q => q.ExpectedDocumentId is { } document && holders.Any(h => h.Id == document) && !reachable.Contains(document)));
                    return new AdminChange<(Assistant, AssistantVersion, CopyLeftOut)>((copy, draft, left), "copy", AuditEntityTypes.Assistant, copy.Id, null,
                        new { assistant = AssistantAudit.Of(copy), testQuestions = questions.Count, linksLeftOut = left.Links, expectedDocumentsCleared = left.ExpectedDocuments })
                    {
                        DepartmentId = copy.DepartmentId, Classification = copy.Classification, Reason = $"Copied from {origin} ({source.Id})",
                    };
                }, ct);
                return Results.Created($"/api/assistants/{copy.Id}",
                    AssistantCopyDto.Of(await DetailAsync(admin, copy, ct), await WarningsAsync(admin, copy, draft, ct), dropped));
            })
            .RequireAuthorization(AdminPolicies.Edit).WithName("CopyAssistant").WithSummary("Copy an assistant into a draft of a new one; V11/V12 for the target department as warnings");

        assistants.MapPut("/{id:guid}", async (Guid id, AssistantUpdateRequest request, AdminContext admin, GatewayModelCheck models, CancellationToken ct) =>
            {
                await WarmUpdateAsync(admin, models, id, request, ct);
                var saved = await admin.WriteAsync(async attempt =>
                {
                    var assistant = await LoadAsync(admin, id, attempt);
                    admin.ExpectVersion(assistant, request.Version);
                    await CheckTeamAsync(admin, assistant.DepartmentId, request.OwnerTeamId, attempt);
                    var before = AssistantAudit.Of(assistant);
                    var levelChanged = request.Classification != assistant.Classification;
                    var keyChanged = request.GatewayKeyId != assistant.GatewayKeyId;
                    var confirmed = false;
                    assistant.Name = request.Name.Trim();
                    assistant.OwnerTeamId = request.OwnerTeamId;
                    assistant.Classification = request.Classification;
                    request.ApplyTo(assistant);
                    assistant.UpdatedAt = admin.Now;
                    assistant.UpdatedBy = admin.Actor;
                    // V39: once the gateway acts as keys, a published assistant's key cannot be cleared: it would then run on
                    // kalla-api's credential key, outside its department's budget (#618 review).
                    if (keyChanged && assistant.GatewayKeyId is null && assistant.PublishedVersionId is not null && models.ActsAsKeys)
                    {
                        Refuse([new(AdminErrorCodes.GatewayKeyRequired, null, [])]);
                    }

                    if (levelChanged && assistant.PublishedVersionId is { } published)
                    {
                        // Dependants are re-checked against the published version (admin design §4.6): V3, V7, V9 and V8.
                        var version = await admin.Db.AssistantVersions.AsNoTracking().SingleAsync(v => v.Id == published, attempt);
                        var audience = await AudienceClientsAsync(admin, assistant.Id, attempt);
                        Refuse(await AssistantRules.CheckAsync(admin, assistant, version, audience, attempt, confirmGeneralKnowledge: request.ConfirmGeneralKnowledge, asKey: models));
                        await models.EnsureServableAsync(version.GatewayModel!, AssistantRules.Ceiling(assistant, audience), attempt, assistant.GatewayKeyId);
                        confirmed = AssistantRules.NeedsGeneralKnowledgeConfirmation(assistant, version.QuerySteps);
                    }
                    else if (keyChanged && (assistant.PublishedVersionId ?? assistant.DraftVersionId) is { } current)
                    {
                        // V39 on a key save: the key against the models of the version that runs (else the draft) at its ceiling.
                        var version = await admin.Db.AssistantVersions.AsNoTracking().SingleAsync(v => v.Id == current, attempt);
                        var audience = await AudienceClientsAsync(admin, assistant.Id, attempt);
                        Refuse(await GatewayKeyRules.CheckAsync(admin, assistant.GatewayKeyId, assistant.DepartmentId, AssistantRules.Ceiling(assistant, audience),
                            await GatewayKeyRules.AssistantModelsAsync(admin, version, attempt), attempt, models));
                    }

                    object after = confirmed ? new { assistant = AssistantAudit.Of(assistant), generalKnowledgeConfirmed = true } : AssistantAudit.Of(assistant);
                    return new AdminChange<Assistant>(assistant, "update", AuditEntityTypes.Assistant, assistant.Id, before, after)
                    {
                        DepartmentId = assistant.DepartmentId, Classification = assistant.Classification,
                    };
                }, ct);
                return await DetailAsync(admin, saved, ct);
            })
            .RequireAuthorization(AdminPolicies.Edit).WithName("UpdateAssistant").WithSummary("Change the assistant's own fields (a level change re-checks V3, V7, V8, V9, V36, V39; a key change V39)");

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

        assistants.MapPost("/{id:guid}/publish", async (Guid id, PublishRequest request, AdminContext admin, GatewayModelCheck models, AssistantTestRunner tests,
                CancellationToken ct) =>
            {
                await WarmAsync(admin, models, await LoadAsync(admin, id, ct, tracked: false), null, ct);
                var saved = await admin.WriteAsync(async attempt =>
                {
                    var assistant = await LoadAsync(admin, id, attempt);
                    admin.ExpectVersion(assistant, request.Version);
                    var draft = assistant.DraftVersionId is { } d ? await admin.Db.AssistantVersions.SingleAsync(v => v.Id == d, attempt) : throw AdminFaults.Of(AdminErrorCodes.NoDraft);
                    admin.ExpectVersion(draft, request.DraftVersion);
                    var published = assistant.PublishedVersionId is { } p ? await admin.Db.AssistantVersions.SingleAsync(v => v.Id == p, attempt) : null;
                    if (published is not null && draft.Number < published.Number && !request.ConfirmBaseChanged)
                    {
                        // With what publishing would change against the published version (P2, B-56), for the confirmation dialog.
                        throw AdminFaults.Of(AdminErrorCodes.DraftBaseChanged, extensions: new Dictionary<string, object?>
                        {
                            ["diff"] = await DiffAsync(admin, published, draft, toDraft: true, attempt),
                        });
                    }

                    var audience = await AudienceClientsAsync(admin, id, attempt);
                    Refuse(await AssistantRules.CheckAsync(admin, assistant, draft, audience, attempt, publishing: true, confirmGeneralKnowledge: request.ConfirmGeneralKnowledge,
                        asKey: models));
                    await models.EnsureServableAsync(draft.GatewayModel!, AssistantRules.Ceiling(assistant, audience), attempt, assistant.GatewayKeyId);

                    var before = AssistantAudit.Of(assistant);
                    var confirmed = AssistantRules.NeedsGeneralKnowledgeConfirmation(assistant, draft.QuerySteps);
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
                    object after = confirmed
                        ? new { assistant = AssistantAudit.Of(assistant), version = DraftAudit.Of(draft), generalKnowledgeConfirmed = true }
                        : new { assistant = AssistantAudit.Of(assistant), version = DraftAudit.Of(draft) };
                    return new AdminChange<Assistant>(assistant, "publish", AuditEntityTypes.Assistant, assistant.Id, before, after)
                    {
                        DepartmentId = assistant.DepartmentId, Classification = assistant.Classification,
                    };
                }, ct);
                await StartTestsAsync(admin, tests, saved, ct);
                return await DetailAsync(admin, saved, ct);
            })
            .RequireAuthorization(AdminPolicies.Edit).WithName("PublishAssistant").WithSummary("Publish the draft (V34, assistant rules, V8 at the gateway)");

        assistants.MapPut("/{id:guid}/audiences", async (Guid id, AudiencesRequest request, AdminContext admin, GatewayModelCheck models, CancellationToken ct) =>
            {
                if (await LoadAsync(admin, id, ct, tracked: false) is { PublishedVersionId: not null } offered)
                {
                    var ids = request.ClientIds.Distinct().ToList();
                    await WarmAsync(admin, models, offered, await admin.Db.Clients.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct), ct);
                }

                var saved = await admin.WriteAsync(async attempt =>
                {
                    var assistant = await LoadAsync(admin, id, attempt);
                    admin.ExpectVersion(assistant, request.Version);
                    var ids = request.ClientIds.Distinct().ToList();
                    var found = await admin.Db.Clients.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(attempt);
                    if (found.Count != ids.Count)
                    {
                        throw AdminFaults.Field("clientIds", AdminErrorCodes.InvalidValue);
                    }

                    var before = await ApplyAudienceAsync(admin, models, assistant, [.. ids.Select(i => found.Single(c => c.Id == i))], attempt);
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

        // The structured diff (admin design §4.5, P2; B-56): from version {number} to `against` (a number or `draft`; without it,
        // the draft when there is one, else the published version).
        assistants.MapGet("/{id:guid}/versions/{number}/diff", async (Guid id, string number, [Microsoft.AspNetCore.Mvc.FromQuery] string? against, AdminContext admin,
                CancellationToken ct) =>
            {
                var assistant = await LoadAsync(admin, id, ct, tracked: false);
                var from = await VersionAsync(admin, id, number, ct);
                var toDraft = against == "draft" || (against is null && assistant.DraftVersionId is not null);
                AssistantVersion to;
                if (toDraft)
                {
                    to = assistant.DraftVersionId is { } d ? await admin.Db.AssistantVersions.AsNoTracking().SingleAsync(v => v.Id == d, ct) : throw AdminFaults.Of(AdminErrorCodes.NotFound);
                }
                else if (against is not null)
                {
                    to = await VersionAsync(admin, id, against, ct);
                }
                else
                {
                    to = assistant.PublishedVersionId is { } p ? await admin.Db.AssistantVersions.AsNoTracking().SingleAsync(v => v.Id == p, ct) : throw AdminFaults.Of(AdminErrorCodes.NotFound);
                }

                return await DiffAsync(admin, from, to, toDraft, ct);
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("DiffAssistantVersions").WithSummary("What changed between two versions, or a version and the draft");

        assistants.MapPost("/{id:guid}/versions/{number}/restore", async (Guid id, string number, RestoreRequest request, AdminContext admin, GatewayModelCheck models,
                AssistantTestRunner tests, CancellationToken ct) =>
            {
                await WarmAsync(admin, models, await LoadAsync(admin, id, ct, tracked: false), null, ct);
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
                    Refuse(await AssistantRules.CheckAsync(admin, assistant, restored, audience, attempt, publishing: true, confirmGeneralKnowledge: request.ConfirmGeneralKnowledge,
                        asKey: models));
                    await models.EnsureServableAsync(restored.GatewayModel!, AssistantRules.Ceiling(assistant, audience), attempt, assistant.GatewayKeyId);
                    var confirmed = AssistantRules.NeedsGeneralKnowledgeConfirmation(assistant, restored.QuerySteps);

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
                    object after = confirmed
                        ? new { assistant = AssistantAudit.Of(assistant), version = DraftAudit.Of(restored), restoredFrom = source.Number, generalKnowledgeConfirmed = true }
                        : new { assistant = AssistantAudit.Of(assistant), version = DraftAudit.Of(restored), restoredFrom = source.Number };
                    return new AdminChange<Assistant>(assistant, "restore", AuditEntityTypes.Assistant, assistant.Id, before, after)
                    {
                        DepartmentId = assistant.DepartmentId, Classification = assistant.Classification,
                    };
                }, ct);
                await StartTestsAsync(admin, tests, saved, ct);
                return await DetailAsync(admin, saved, ct);
            })
            .RequireAuthorization(AdminPolicies.Edit).WithName("RestoreAssistantVersion").WithSummary("Publish a new version with an earlier version's settings (as publish)");

        assistants.MapPost("/{id:guid}/archive", (Guid id, VersionRequest request, AdminContext admin, CancellationToken ct) =>
                SetStateAsync(admin, id, request, AssistantStates.Archived, ct))
            .RequireAuthorization(AdminPolicies.Edit).WithName("ArchiveAssistant").WithSummary("Hide the assistant from every client; versions, audiences and statistics are kept");

        // Keep on Home's unused-assistant item (admin design §4.5, §5.5.1; B-68): not raised again for 90 days.
        assistants.MapPost("/{id:guid}/keep", async (Guid id, VersionRequest request, AdminContext admin, CancellationToken ct) =>
            {
                var saved = await admin.WriteAsync(async attempt =>
                {
                    var assistant = await LoadAsync(admin, id, attempt);
                    admin.ExpectVersion(assistant, request.Version);
                    var before = AssistantAudit.Of(assistant);
                    assistant.UnusedKeptUntil = admin.Now + AttentionList.UnusedKeep;
                    assistant.UpdatedAt = admin.Now;
                    assistant.UpdatedBy = admin.Actor;
                    return new AdminChange<Assistant>(assistant, "keep", AuditEntityTypes.Assistant, assistant.Id, before, AssistantAudit.Of(assistant))
                    {
                        DepartmentId = assistant.DepartmentId, Classification = assistant.Classification,
                    };
                }, ct);
                return await DetailAsync(admin, saved, ct);
            })
            .RequireAuthorization(AdminPolicies.Edit).WithName("KeepAssistant").WithSummary("Keep an unused assistant: Home does not raise it as unused for 90 days");

        assistants.MapPost("/{id:guid}/unarchive", (Guid id, VersionRequest request, AdminContext admin, CancellationToken ct) =>
                SetStateAsync(admin, id, request, AssistantStates.Active, ct))
            .RequireAuthorization(AdminPolicies.Edit).WithName("UnarchiveAssistant").WithSummary("Offer the assistant again with the same published version");

        // Decisions "Deleting knowledge bases, sources and assistants": from any state, a soft delete; the name is free at once,
        // no client lists or reaches it, its conversations and statistics stay their own time, and the config retention step
        // purges the row 30 days later once no conversation names it. The draft goes with the delete.
        assistants.MapDelete("/{id:guid}", async (Guid id, [Microsoft.AspNetCore.Mvc.FromQuery] string? version, AdminContext admin, CancellationToken ct) =>
            {
                await admin.WriteAsync(async attempt =>
                {
                    var assistant = await LoadAsync(admin, id, attempt);
                    admin.ExpectVersion(assistant, version);
                    var before = AssistantAudit.Of(assistant);
                    var draft = assistant.DraftVersionId is { } d ? await admin.Db.AssistantVersions.SingleAsync(v => v.Id == d, attempt) : null;
                    assistant.DraftVersionId = null;
                    assistant.DeletedAt = admin.Now;
                    assistant.UpdatedAt = admin.Now;
                    assistant.UpdatedBy = admin.Actor;
                    await admin.Db.SaveChangesAsync(attempt);
                    if (draft is not null)
                    {
                        admin.Db.AssistantVersions.Remove(draft);
                    }

                    return new AdminChange<bool>(true, "delete", AuditEntityTypes.Assistant, assistant.Id, before, null)
                    {
                        DepartmentId = assistant.DepartmentId, Classification = assistant.Classification,
                    };
                }, ct);
                return Results.NoContent();
            })
            .RequireAuthorization(AdminPolicies.Manage).WithName("DeleteAssistant").WithSummary("Delete an assistant from any state; its name is free at once");

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

    /// <summary>
    /// The diff of two versions, with the names of the knowledge bases they link that the caller may see: their own
    /// departments' (deleted ones too) and those shared with them. Another department's unshared one shows its id.
    /// </summary>
    private static async Task<VersionDiffDto> DiffAsync(AdminContext admin, AssistantVersion from, AssistantVersion to, bool toDraft, CancellationToken ct)
    {
        var ids = from.LinkedCollectionIds().Concat(to.LinkedCollectionIds()).Distinct().ToList();
        var departments = admin.Departments().Select(d => d.Id);
        var shared = CollectionSharing.SharedWith(admin.Db, departments).Select(c => c.Id);
        var names = await admin.Db.Collections.IgnoreQueryFilters().AsNoTracking()
            .Where(c => ids.Contains(c.Id) && (departments.Contains(c.DepartmentId) || shared.Contains(c.Id)))
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return AssistantVersionDiff.Of(from, to, toDraft, names);
    }

    /// <summary>
    /// The test-question run on the newly published version (admin design §7.4), started after publish and restore: none
    /// without questions, and none while a run is still going (the report then shows that run; Run test questions starts one).
    /// </summary>
    private static async Task StartTestsAsync(AdminContext admin, AssistantTestRunner tests, Assistant assistant, CancellationToken ct)
    {
        if (assistant.PublishedVersionId is not { } published)
        {
            return;
        }

        var version = await admin.Db.AssistantVersions.AsNoTracking().SingleAsync(v => v.Id == published, ct);
        try
        {
            _ = await tests.StartAsync(admin.Db, assistant, version, admin.Actor ?? "admin", ct);
        }
        catch (Kalla.ServiceDefaults.ApiFaultException fault) when (fault.Extensions?.TryGetValue("code", out var code) == true && Equals(code, AdminErrorCodes.TestRunRunning.Code))
        {
            // One run per assistant at a time: the running one finishes, and the owner can run the questions again.
        }
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

    /// <summary>
    /// The list rows of <paramref name="query"/> (admin design §5.5.4), in grouped queries for the page: the knowledge bases and
    /// model of the published version, else the draft; the published version's number, date and general knowledge; and the
    /// questions (<c>AssistantDailyTotals.Messages</c>) of the last 7 Europe/Stockholm days, today included.
    /// </summary>
    private static async Task<List<AssistantSummary>> SummariesAsync(AdminContext admin, IQueryable<Assistant> query, CancellationToken ct)
    {
        var rows = await query.Select(a => new { a.Id, a.Slug, a.Name, a.DepartmentId, a.Classification, a.State, a.PublishedVersionId, a.DraftVersionId })
            .ToListAsync(ct);
        var versionIds = rows.Select(r => r.PublishedVersionId ?? r.DraftVersionId).OfType<Guid>().ToList();
        var versions = await admin.Db.AssistantVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id))
            .Select(v => new { v.Id, v.Number, v.GatewayModel, v.QuerySteps, v.CollectionLinks, v.PublishedAt })
            .ToDictionaryAsync(v => v.Id, ct);
        var collectionIds = versions.Values.SelectMany(v => v.CollectionLinks).Select(l => l.CollectionId).Distinct().ToList();
        // Named only when the caller may see them (their departments' or shared with them); another department's unshared one
        // shows its id.
        var departments = admin.Departments().Select(d => d.Id);
        var shared = CollectionSharing.SharedWith(admin.Db, departments).Select(c => c.Id);
        var collections = await admin.Db.Collections.AsNoTracking().Where(c => collectionIds.Contains(c.Id) && c.DeletedAt == null)
            .Select(c => new { c.Id, Name = departments.Contains(c.DepartmentId) || shared.Contains(c.Id) ? c.Name : null })
            .ToDictionaryAsync(c => c.Id, c => c.Name ?? c.Id.ToString(), ct);
        var ids = rows.Select(r => r.Id).ToList();
        var today = StockholmTime.DayOf(admin.Now);
        var firstDay = today.AddDays(-6);
        var questions = await admin.Db.AssistantDailyTotals.AsNoTracking().Where(t => ids.Contains(t.AssistantId) && t.Day >= firstDay && t.Day <= today)
            .GroupBy(t => t.AssistantId).Select(g => new { g.Key, Messages = g.Sum(t => t.Messages) })
            .ToDictionaryAsync(g => g.Key, g => g.Messages, ct);
        return
        [
            .. rows.Select(a =>
            {
                var shown = (a.PublishedVersionId ?? a.DraftVersionId) is { } id ? versions.GetValueOrDefault(id) : null;
                var published = a.PublishedVersionId is { } p ? versions.GetValueOrDefault(p) : null;
                return new AssistantSummary(a.Id, a.Slug, a.Name, a.DepartmentId, a.Classification, a.State, published is not null, a.DraftVersionId is not null,
                    published is null ? null : GeneralKnowledgeOf(published.QuerySteps),
                    [.. (shown?.CollectionLinks ?? []).Where(l => collections.ContainsKey(l.CollectionId)).Select(l => new AssistantCollectionRef(l.CollectionId, collections[l.CollectionId]))],
                    shown?.GatewayModel, published?.Number, published?.PublishedAt, questions.GetValueOrDefault(a.Id));
            }),
        ];
    }

    private static IQueryable<Assistant> Scoped(AdminContext admin)
    {
        var departments = admin.Departments().Select(d => d.Id);
        return admin.Db.Assistants.Where(a => departments.Contains(a.DepartmentId));
    }

    /// <summary>
    /// Makes <paramref name="clients"/> the assistant's audience within the caller's write: <see cref="CheckAudienceAsync"/>, then
    /// the rows and the assistant's stamp. Shared by the assistant's own audience save and a client's assistants
    /// (<c>POST /api/clients</c>, <c>PUT /api/clients/{id}/assistants</c>, admin design §5.9.1), where a client may be new in this
    /// write. Returns the audience before, for the audit entry.
    /// </summary>
    internal static async Task<List<Guid?>> ApplyAudienceAsync(AdminContext admin, GatewayModelCheck models, Assistant assistant, IReadOnlyList<Client> clients,
        CancellationToken ct)
    {
        await CheckAudienceAsync(admin, models, assistant, clients, ct);
        var ids = clients.Select(c => c.Id).ToList();
        var id = assistant.Id;
        var current = await admin.Db.AssistantAudiences.Where(a => a.AssistantId == id).ToListAsync(ct);
        var before = current.Select(a => a.SubjectId).ToList();
        admin.Db.AssistantAudiences.RemoveRange(current.Where(a => a.Kind != AssistantAudienceKinds.Client || !ids.Contains(a.SubjectId ?? Guid.Empty)));
        foreach (var client in ids.Where(c => !current.Any(a => a.Kind == AssistantAudienceKinds.Client && a.SubjectId == c)))
        {
            admin.Db.AssistantAudiences.Add(new AssistantAudience { AssistantId = id, Kind = AssistantAudienceKinds.Client, SubjectId = client });
        }

        // The aggregate root's stamp moves its xmin, so a concurrent audience save conflicts (admin design §4.3).
        assistant.UpdatedAt = admin.Now;
        assistant.UpdatedBy = admin.Actor;
        return before;
    }

    /// <summary>
    /// The rules of <paramref name="clients"/> as the assistant's audience: V7 and V9, for a published assistant its whole rule set
    /// and V8 at the gateway. Also run for the assistants a client offers when the client's ceiling or history changes (admin
    /// design §4.5, #700).
    /// </summary>
    internal static async Task CheckAudienceAsync(AdminContext admin, GatewayModelCheck models, Assistant assistant, IReadOnlyList<Client> clients, CancellationToken ct)
    {
        if (assistant.PublishedVersionId is { } published)
        {
            var version = await admin.Db.AssistantVersions.AsNoTracking().SingleAsync(v => v.Id == published, ct);
            // V36 is not re-checked on an audience or client change (admin §4.6: draft save, publish, restore, level change); the
            // published level was confirmed at publish, so it does not block this save.
            Refuse(await AssistantRules.CheckAsync(admin, assistant, version, clients, ct, confirmGeneralKnowledge: true, asKey: models));
            await models.EnsureServableAsync(version.GatewayModel!, AssistantRules.Ceiling(assistant, clients), ct, assistant.GatewayKeyId);
        }
        else
        {
            Refuse(await AssistantRules.AudienceRulesAsync(admin, assistant, clients, ct));
        }
    }

    /// <summary>
    /// Fetches, before the write and outside the config version lock (admin design §4.4, #696), the gateway list V8 and V39 will
    /// check <paramref name="assistant"/> (as read now) against with <paramref name="audience"/> (null: its audience now). The write
    /// reads the assistant again under the lock; a list it needs that differs is asked for there.
    /// </summary>
    internal static async Task WarmAsync(AdminContext admin, GatewayModelCheck models, Assistant assistant, IReadOnlyList<Client>? audience, CancellationToken ct)
    {
        var ceiling = AssistantRules.Ceiling(assistant, audience ?? await AudienceClientsAsync(admin, assistant.Id, ct));
        await models.WarmAsync([(ceiling, assistant.GatewayKeyId, assistant.DepartmentId)], ct);
    }

    /// <summary>
    /// <see cref="WarmAsync"/> for <c>PUT /api/assistants/{id}</c>: V8 and V39 on a published assistant's level change, V39 on a key
    /// change, at the ceiling and with the key the request sets.
    /// </summary>
    private static async Task WarmUpdateAsync(AdminContext admin, GatewayModelCheck models, Guid id, AssistantUpdateRequest request, CancellationToken ct)
    {
        var assistant = await LoadAsync(admin, id, ct, tracked: false);
        var servable = request.Classification != assistant.Classification && assistant.PublishedVersionId is not null;
        if (!servable && request.GatewayKeyId == assistant.GatewayKeyId)
        {
            return;
        }

        assistant.Classification = request.Classification;
        assistant.GatewayKeyId = request.GatewayKeyId;
        var ceiling = AssistantRules.Ceiling(assistant, await AudienceClientsAsync(admin, id, ct));
        await (servable
            ? models.WarmAsync([(ceiling, assistant.GatewayKeyId, assistant.DepartmentId)], ct)
            : models.WarmKeyAsync(ceiling, assistant.GatewayKeyId, assistant.DepartmentId, ct));
    }

    internal static async Task<Assistant> LoadAsync(AdminContext admin, Guid id, CancellationToken ct, bool tracked = true) =>
        await (tracked ? Scoped(admin) : Scoped(admin).AsNoTracking()).SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);

    internal static async Task CheckTeamAsync(AdminContext admin, Guid departmentId, Guid? teamId, CancellationToken ct)
    {
        if (teamId is { } team && !await admin.Db.Teams.AnyAsync(t => t.Id == team && t.DepartmentId == departmentId, ct))
        {
            throw AdminFaults.Field("ownerTeamId", AdminErrorCodes.InvalidValue);
        }
    }

    internal static Task<List<Client>> AudienceClientsAsync(AdminContext admin, Guid assistantId, CancellationToken ct)
    {
        var ids = admin.Db.AssistantAudiences.Where(a => a.AssistantId == assistantId && a.Kind == AssistantAudienceKinds.Client).Select(a => a.SubjectId);
        return admin.Db.Clients.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync(ct);
    }

    private static async Task<IReadOnlyList<RuleProblem>> WarningsAsync(AdminContext admin, Assistant assistant, AssistantVersion draft, CancellationToken ct) =>
    [
        .. await AssistantRules.CheckAsync(admin, assistant, draft, await AudienceClientsAsync(admin, assistant.Id, ct), ct, publishing: true),
        .. await GatewayKeyRules.WarningsAsync(admin, assistant.GatewayKeyId, ct), // V39's warnings: no key, a key with a system prompt
    ];

    /// <summary>The first problem as the refusal (its code and params), with every problem in <c>problems</c>.</summary>
    internal static void Refuse(IReadOnlyList<RuleProblem> problems)
    {
        if (problems.Count == 0)
        {
            return;
        }

        var first = problems[0];
        throw AdminFaults.Of(first.Code, first.Params, new Dictionary<string, object?> { ["problems"] = problems.Select(p => new { code = p.Code.Code, p.Params, p.Affected }).ToList(), ["affected"] = first.Affected });
    }

    internal static async Task<AssistantDto> DetailAsync(AdminContext admin, Assistant a, CancellationToken ct)
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
            audiences, a.UpdatedAt, a.UpdatedBy, a.Version.ToString(CultureInfo.InvariantCulture), published is null ? null : GeneralKnowledgeOf(published.QuerySteps),
            await RerankersAsync(admin, ct), await AnswerCeilingAsync(admin, ct), a.GatewayKeyId);
    }

    /// <summary>The platform answer ceiling <c>Settings.Chat.MaxAnswerTokens</c> (chat §6.3), null when unset: the Draft tab shows it when lower.</summary>
    public static async Task<int?> AnswerCeilingAsync(AdminContext admin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        return Kalla.Answering.Chat.PlatformChatSettings.AnswerCeilingOf(await admin.Db.Settings.AsNoTracking().Select(s => s.Chat).SingleOrDefaultAsync(ct));
    }

    /// <summary>
    /// The <c>retrieval.rerank</c> values the platform has a model for (B-16): <c>chat</c> while <c>Settings.Retrieval.rerankModel</c>
    /// is set, <c>gateway</c> while <c>gatewayRerankModel</c> is. Empty on most deployments (rerank is optional): the
    /// assistant page then disables the rerank choice with a hint.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RerankersAsync(AdminContext admin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        var retrieval = await admin.Db.Settings.AsNoTracking().Select(s => s.Retrieval).SingleOrDefaultAsync(ct);
        var json = JsonNode.Parse(string.IsNullOrWhiteSpace(retrieval) ? "{}" : retrieval) as JsonObject;
        static bool Set(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetValue<string>());
        List<string> available = [];
        if (Set(json?["rerankModel"]))
        {
            available.Add(Kalla.Domain.Retrieval.RerankerKinds.ChatModel);
        }

        if (Set(json?["gatewayRerankModel"]))
        {
            available.Add(Kalla.Domain.Retrieval.RerankerKinds.Gateway);
        }

        return available;
    }

    /// <summary>
    /// <c>Settings.Chat.defaultSystemPrompt</c> (P1b item 39): the saved text, also when an admin emptied it; the code default
    /// when the key is missing (a <c>Settings</c> row from before the key existed, or none yet).
    /// </summary>
    public static async Task<string> DefaultSystemPromptAsync(AdminContext admin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        return ChatDefaults.SystemPromptFrom(await admin.Db.Settings.AsNoTracking().Select(s => s.Chat).SingleOrDefaultAsync(ct));
    }

    /// <summary>A version's <c>answer.generalKnowledge</c> (admin design §5.5.4 badge): the stored value, <c>off</c> when unset or unknown.</summary>
    public static string GeneralKnowledgeOf(string querySteps) =>
        Kalla.Answering.Chat.GeneralKnowledgeLevels.ToValue(Kalla.Answering.Chat.AssistantChatSettings.Parse(querySteps).GeneralKnowledge);
}

/// <summary>A broken rule: its code and template parameters, and the objects it names.</summary>
public sealed record RuleProblem(AdminErrorCode Code, IReadOnlyDictionary<string, string>? Params, IReadOnlyList<string> Affected);

/// <summary>
/// The assistant rules of P1a (admin design §4.6): V3 (level ≥ every linked collection), V7 (every audience client's ceiling
/// ≥ the level), V8's ceiling (the ladder of max(level, ceilings of the audience clients that store history)), V9
/// (<c>sensitive</c> only for clients without history when the policy says so), V11 (≤ 20 collections, each of the
/// assistant's department or shared with it, ≤ 20 topics per link in the chunk tag format), V15 (not-found texts and links), V17 (query step ranges), V34 (the definition resolves;
/// description and contact set), V36 (a <c>public</c> assistant with general knowledge needs <c>confirmGeneralKnowledge</c>, #445)
/// and V39's refusals (the gateway key, <see cref="GatewayKeyRules"/>). Returned as problems: refusals on publish, warnings on draft save.
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
        CancellationToken ct, bool publishing = false, bool confirmGeneralKnowledge = false, GatewayModelCheck? asKey = null)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(assistant);
        ArgumentNullException.ThrowIfNull(version);
        var problems = new List<RuleProblem>();

        // V34: the definition resolves (the model a chat row of the mirror); description and contact are set.
        if (publishing)
        {
            if (string.IsNullOrWhiteSpace(assistant.Description) || string.IsNullOrWhiteSpace(assistant.ContactName) || string.IsNullOrWhiteSpace(assistant.ContactEmail))
            {
                problems.Add(new(AdminErrorCodes.AssistantContactMissing, null, []));
            }

            if (string.IsNullOrWhiteSpace(version.GatewayModel) || !await admin.Db.GatewayModels.AsNoTracking()
                    .AnyAsync(m => m.Name == version.GatewayModel && m.ModelKind == GatewayModelModelKinds.Chat, ct))
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
        // The ids, not the names: the names belong to another department that has not shared them.
        var foreign = collections.Where(c => !linkable.Contains(c.Id)).Select(c => c.Id.ToString()).ToList();
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
        var model = await admin.Db.GatewayModels.AsNoTracking().SingleOrDefaultAsync(m => m.Name == version.GatewayModel, ct);
        problems.AddRange(QuerySteps(version.QuerySteps, model, await AssistantEndpoints.AnswerCeilingAsync(admin, ct)));
        if (!confirmGeneralKnowledge && NeedsGeneralKnowledgeConfirmation(assistant, version.QuerySteps))
        {
            problems.Add(new RuleProblem(AdminErrorCodes.GeneralKnowledgePublicUnconfirmed,
                new Dictionary<string, string> { ["level"] = assistant.Classification, ["generalKnowledge"] = GeneralKnowledgeValue(version.QuerySteps)! }, []));
        }

        problems.AddRange(await NotFoundAsync(admin, version.NotFound, ct));
        // V39: the assistant's gateway key against this version's models at the V8 ceiling; as the key once the gateway acts as
        // keys (B-43), when then publish and restore also need a key.
        if (publishing && asKey is { ActsAsKeys: true } && assistant.GatewayKeyId is null)
        {
            problems.Add(new(AdminErrorCodes.GatewayKeyRequired, null, []));
        }

        problems.AddRange(await GatewayKeyRules.CheckAsync(admin, assistant.GatewayKeyId, assistant.DepartmentId, Ceiling(assistant, audience),
            await GatewayKeyRules.AssistantModelsAsync(admin, version, ct), ct, asKey));
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

    /// <summary>The upper bound of <c>answer.maxOutputTokens</c> when the model's output limit is unknown (V17).</summary>
    public const int UnknownOutputLimit = 1_000_000;

    /// <summary>The upper bound of <c>answer.contextTokens</c> when the model's window is unknown (V17).</summary>
    public const int UnknownContextWindow = 1_048_576;

    /// <summary>
    /// V17 (chat design §5–§6, §10.3): the types and ranges of the query steps that exist in P1a. The integer fields are
    /// checked with <see cref="Kalla.Answering.Chat.AssistantChatSettings.TryInt"/>, the runtime's own reader, so a value
    /// the runtime would replace by its default (a fraction, a string, <c>10.0</c>) is refused here. The answer's two
    /// overrides are bounded by the version's model row (Decisions "Answer length from the model"): absent means the model's
    /// limit; set, at most the model's limit, or a wide bound when the mirror does not know it. A draft whose override is
    /// above a limit lowered later gets the warning; the runtime lowers it to the limit.
    /// </summary>
    public static IEnumerable<RuleProblem> QuerySteps(string json, GatewayModel? model, int? answerCeiling = null)
    {
        var steps = JsonNode.Parse(json) as JsonObject ?? [];
        var problems = new List<string>();
        CheckInt(steps["retrieval"]?["topK"], 1, 50, "retrieval.topK");
        CheckInt(steps["history"]?["maxMessages"], 0, 20, "history.maxMessages");
        CheckInt(steps["history"]?["maxTokens"], 0, 8000, "history.maxTokens");
        // The platform ceiling bounds it too: lowering the ceiling below an override is a warning on the draft (§6.3).
        CheckInt(steps["answer"]?["maxOutputTokens"], 64, Math.Min(model?.MaxOutputTokens ?? UnknownOutputLimit, answerCeiling ?? int.MaxValue), "answer.maxOutputTokens");
        CheckInt(steps["answer"]?["contextTokens"], 4096, model?.ContextWindow ?? UnknownContextWindow, "answer.contextTokens");
        Check(steps["answer"]?["temperature"], 0, 1, "answer.temperature");
        if (steps["condense"]?["enabled"] is { } condense && !(condense is JsonValue c && c.GetValueKind() is JsonValueKind.True or JsonValueKind.False))
        {
            problems.Add("condense.enabled");
        }

        if (steps["answer"]?["language"] is JsonValue language && language.GetValueKind() == JsonValueKind.String && language.GetValue<string>() is not ("auto" or "sv"))
        {
            problems.Add("answer.language");
        }

        // retrieval.rerank (Decisions "Rerank pulled forward", B-16): off, chat or gateway; absent is off. A reranker the
        // platform has no model for is not a problem: rerank is optional and such an assistant gets the fused order.
        if (steps["retrieval"]?["rerank"] is { } rerank &&
            !(rerank is JsonValue r && r.GetValueKind() == JsonValueKind.String && Kalla.Domain.Retrieval.RerankerKinds.All.Contains(r.GetValue<string>())))
        {
            problems.Add("retrieval.rerank");
        }

        if (steps["answer"]?["generalKnowledge"] is { } generalKnowledge &&
            !(generalKnowledge is JsonValue g && g.GetValueKind() == JsonValueKind.String && Kalla.Answering.Chat.GeneralKnowledgeLevels.All.Contains(g.GetValue<string>())))
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

        void CheckInt(JsonNode? node, int min, int max, string name)
        {
            if (node is null)
            {
                return;
            }

            if (Kalla.Answering.Chat.AssistantChatSettings.TryInt(node) is not { } value || value < min || value > max)
            {
                problems.Add(name);
            }
        }
    }

    /// <summary>
    /// V36 (Decisions "General knowledge on a public assistant", #445): a <c>public</c> assistant whose version uses general
    /// knowledge (<c>supplement</c>, <c>fallback</c> or <c>open</c>) needs an explicit confirmation.
    /// </summary>
    public static bool NeedsGeneralKnowledgeConfirmation(Assistant assistant, string querySteps)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        return assistant.Classification == Classification.Public && GeneralKnowledgeValue(querySteps) is { } value
            && value != Kalla.Answering.Chat.GeneralKnowledgeLevels.Off;
    }

    private static string? GeneralKnowledgeValue(string json) =>
        (JsonNode.Parse(json) as JsonObject)?["answer"]?["generalKnowledge"] is JsonValue g && g.GetValueKind() == JsonValueKind.String ? g.GetValue<string>() : null;

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

/// <summary>
/// A list row (admin design §5.5.4). <see cref="GeneralKnowledge"/>: the published version's <c>answer.generalKnowledge</c>,
/// null before the first publish; <see cref="Collections"/> and <see cref="Model"/>: the published version's, else the
/// draft's; <see cref="PublishedVersion"/> and <see cref="PublishedAt"/>: null before the first publish;
/// <see cref="Questions7d"/>: the messages of the last 7 Europe/Stockholm days, today included.
/// </summary>
public sealed record AssistantSummary(Guid Id, string Slug, string Name, Guid DepartmentId, string Classification, string State, bool Published, bool HasDraft,
    string? GeneralKnowledge, IReadOnlyList<AssistantCollectionRef> Collections, string? Model, int? PublishedVersion, DateTimeOffset? PublishedAt, int Questions7d);

/// <summary>A knowledge base an assistant's version links.</summary>
public sealed record AssistantCollectionRef(Guid Id, string Name);

public sealed record DraftInfo(int Number, DateTimeOffset UpdatedAt, bool BaseChanged, string Version);

public sealed record AssistantDto(
    Guid Id, string Slug, string Name, string? Description, string? ContactName, string? ContactEmail, Guid DepartmentId, Guid? OwnerTeamId, string Classification,
    string HistoryMode, int RetentionDays, string State, int? PublishedVersion, DraftInfo? Draft, IReadOnlyList<Guid> AudienceClientIds, DateTimeOffset UpdatedAt,
    string? UpdatedBy, string Version, string? GeneralKnowledge, IReadOnlyList<string> Rerankers, int? MaxAnswerTokens = null, Guid? GatewayKeyId = null);

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
    string State, Guid? PublishedVersionId, Guid? DraftVersionId, Guid? GatewayKeyId, DateTimeOffset? UnusedKeptUntil)
{
    public static AssistantAudit Of(Assistant a) => new(a.Id, a.Slug, a.Name, a.Description, a.ContactName, a.ContactEmail, a.DepartmentId, a.OwnerTeamId,
        a.Classification, a.State, a.PublishedVersionId, a.DraftVersionId, a.GatewayKeyId, a.UnusedKeptUntil);
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

    /// <summary>The assigned gateway key its calls run as (V39; null = the service's credential key).</summary>
    public Guid? GatewayKeyId { get; init; }

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
        assistant.GatewayKeyId = GatewayKeyId;
    }
}

public sealed record AssistantCreateRequest : AssistantFields
{
    [Required]
    public Guid DepartmentId { get; init; }

    [StringLength(100), RegularExpression(Slugs.Pattern)]
    public string? Slug { get; init; }

    /// <summary>
    /// The assistant template the first draft starts from (admin design §5 step 9): its instructions, not-found texts and
    /// query steps are copied, and no link is kept. Without one, the draft starts from <c>Settings.Chat.defaultSystemPrompt</c>.
    /// </summary>
    public Guid? TemplateId { get; init; }
}

/// <summary><c>POST /api/assistants/{id}/copy</c> (admin design §4.5): the copy's name, slug, department and owner team.</summary>
public sealed record AssistantCopyRequest : AdminRequest
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string Name { get; init; } = "";

    [StringLength(100), RegularExpression(Slugs.Pattern)]
    public string? Slug { get; init; }

    [Required]
    public Guid DepartmentId { get; init; }

    public Guid? OwnerTeamId { get; init; }
}

/// <summary>
/// The copy and its draft's V-rule warnings for the target department (V11, V12 and the others a draft save returns), shaped as
/// a draft's, and what was left out: <see cref="CopyLeftOut.Links"/> knowledge bases the target
/// department may not link, <see cref="CopyLeftOut.ExpectedDocuments"/> test questions' expected documents in one of them.
/// </summary>
public sealed record AssistantCopyDto(AssistantDto Assistant, IReadOnlyList<object> Warnings, CopyLeftOut LeftOut)
{
    public static AssistantCopyDto Of(AssistantDto assistant, IReadOnlyList<RuleProblem> warnings, CopyLeftOut leftOut) =>
        new(assistant, [.. warnings.Select(w => (object)new { code = w.Code.Code, @params = w.Params, affected = w.Affected })], leftOut);
}

/// <summary>What a copy into another department left out; counts only, no names of another department's objects.</summary>
public sealed record CopyLeftOut(int Links, int ExpectedDocuments);

public sealed record AssistantUpdateRequest : AssistantFields
{
    [Required]
    public string? Version { get; init; }

    /// <summary>V36 (#445): confirms general knowledge on an assistant whose level changes to <c>public</c>.</summary>
    public bool ConfirmGeneralKnowledge { get; init; }
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
public sealed record CollectionLinkRequest : AdminRequest
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

    /// <summary>V36 (#445): confirms general knowledge on a <c>public</c> assistant.</summary>
    public bool ConfirmGeneralKnowledge { get; init; }
}

/// <summary>A restore's body: the assistant's <c>version</c> and the V36 confirmation (#445).</summary>
public sealed record RestoreRequest : AdminRequest
{
    [Required]
    public string? Version { get; init; }

    public bool ConfirmGeneralKnowledge { get; init; }
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
