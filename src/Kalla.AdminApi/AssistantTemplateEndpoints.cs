using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;

namespace Kalla.AdminApi;

/// <summary>
/// <c>GET/POST/PUT/DELETE /api/assistant-templates[/{id}]</c> (admin design §4.5, P2; read for every role, change for
/// platform admins): the platform's starting points for new assistants, edited through the generic form (§5.8). A
/// template's instructions, not-found texts and query steps are copied into a new assistant's first draft
/// (<c>POST /api/assistants</c> with <c>templateId</c>), and no link is kept. Audited <c>assistanttemplate.*</c>.
/// </summary>
public static class AssistantTemplateEndpoints
{
    public static RouteGroupBuilder MapAssistantTemplates(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var templates = api.MapGroup("/assistant-templates").WithTags("Assistant templates");

        templates.MapGet("", async (AdminContext admin, CancellationToken ct) =>
                (await admin.Db.AssistantTemplates.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct)).Select(AssistantTemplateDto.Of).ToList())
            .RequireAuthorization(AdminPolicies.Read).WithName("ListAssistantTemplates").WithSummary("Starting points for new assistants");

        templates.MapGet("/{id:guid}", async (Guid id, AdminContext admin, CancellationToken ct) =>
                AssistantTemplateDto.Of(await LoadAsync(admin, id, ct, tracked: false)))
            .RequireAuthorization(AdminPolicies.Read).WithName("GetAssistantTemplate").WithSummary("One assistant template");

        templates.MapPost("", async (AssistantTemplateRequest request, AdminContext admin, CancellationToken ct) =>
            {
                var created = await admin.WriteAsync(async attempt =>
                {
                    var template = new AssistantTemplate { Name = request.Name.Trim(), UpdatedAt = admin.Now, UpdatedBy = admin.Actor };
                    await CheckNameAsync(admin, template.Name, null, attempt);
                    request.ApplyTo(template);
                    admin.Db.AssistantTemplates.Add(template);
                    return new AdminChange<AssistantTemplate>(template, "create", AuditEntityTypes.AssistantTemplate, template.Id, null, AssistantTemplateAudit.Of(template));
                }, ct);
                return Results.Created($"/api/assistant-templates/{created.Id}", AssistantTemplateDto.Of(created));
            })
            .RequireAuthorization(AdminPolicies.Admin).WithName("CreateAssistantTemplate").WithSummary("Add an assistant template");

        templates.MapPut("/{id:guid}", async (Guid id, AssistantTemplateRequest request, AdminContext admin, CancellationToken ct) =>
                AssistantTemplateDto.Of(await admin.WriteAsync(async attempt =>
                {
                    var template = await LoadAsync(admin, id, attempt);
                    admin.ExpectVersion(template, request.Version);
                    var before = AssistantTemplateAudit.Of(template);
                    template.Name = request.Name.Trim();
                    await CheckNameAsync(admin, template.Name, id, attempt);
                    request.ApplyTo(template);
                    template.UpdatedAt = admin.Now;
                    template.UpdatedBy = admin.Actor;
                    return new AdminChange<AssistantTemplate>(template, "update", AuditEntityTypes.AssistantTemplate, template.Id, before, AssistantTemplateAudit.Of(template));
                }, ct)))
            .RequireAuthorization(AdminPolicies.Admin).WithName("UpdateAssistantTemplate").WithSummary("Change an assistant template; existing assistants keep their copy");

        templates.MapDelete("/{id:guid}", async (Guid id, [Microsoft.AspNetCore.Mvc.FromQuery] string? version, AdminContext admin, CancellationToken ct) =>
            {
                await admin.WriteAsync(async attempt =>
                {
                    var template = await LoadAsync(admin, id, attempt);
                    admin.ExpectVersion(template, version);
                    var before = AssistantTemplateAudit.Of(template);
                    admin.Db.AssistantTemplates.Remove(template);
                    return new AdminChange<bool>(true, "delete", AuditEntityTypes.AssistantTemplate, id, before, null);
                }, ct);
                return Results.NoContent();
            })
            .RequireAuthorization(AdminPolicies.Admin).WithName("DeleteAssistantTemplate").WithSummary("Remove an assistant template; existing assistants keep their copy");

        return api;
    }

    /// <summary>The template a new assistant starts from (<c>POST /api/assistants</c>); an unknown or inactive one is <c>not_found</c> on <c>templateId</c>.</summary>
    public static async Task<AssistantTemplate> ForNewAssistantAsync(AdminContext admin, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        return await admin.Db.AssistantTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id && t.IsActive, ct)
            ?? throw AdminFaults.Field("templateId", AdminErrorCodes.NotFound);
    }

    private static async Task<AssistantTemplate> LoadAsync(AdminContext admin, Guid id, CancellationToken ct, bool tracked = true)
    {
        var query = tracked ? admin.Db.AssistantTemplates : admin.Db.AssistantTemplates.AsNoTracking();
        return await query.SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
    }

    private static async Task CheckNameAsync(AdminContext admin, string name, Guid? self, CancellationToken ct)
    {
        if (name.Length == 0)
        {
            throw AdminFaults.Field("name", AdminErrorCodes.InvalidValue);
        }

        if (await admin.Db.AssistantTemplates.AsNoTracking().AnyAsync(t => t.Name == name && t.Id != self, ct))
        {
            throw AdminFaults.Field("name", AdminErrorCodes.AlreadyExists);
        }
    }
}

public sealed record AssistantTemplateDto(
    Guid Id, string Name, string Description, string SystemPrompt, JsonElement NotFound, JsonElement QuerySteps, bool IsActive,
    DateTimeOffset UpdatedAt, string? UpdatedBy, string Version)
{
    public static AssistantTemplateDto Of(AssistantTemplate t)
    {
        ArgumentNullException.ThrowIfNull(t);
        return new(t.Id, t.Name, t.Description, t.SystemPrompt, Json(t.NotFound), Json(t.QuerySteps), t.IsActive, t.UpdatedAt, t.UpdatedBy,
            t.Version.ToString(CultureInfo.InvariantCulture));
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(string.IsNullOrWhiteSpace(value) ? "{}" : value).RootElement.Clone();
}

/// <summary>The audit snapshot of a template: its own columns.</summary>
public sealed record AssistantTemplateAudit(Guid Id, string Name, string Description, string SystemPrompt, JsonElement NotFound, JsonElement QuerySteps, bool IsActive)
{
    public static AssistantTemplateAudit Of(AssistantTemplate t)
    {
        var dto = AssistantTemplateDto.Of(t);
        return new(dto.Id, dto.Name, dto.Description, dto.SystemPrompt, dto.NotFound, dto.QuerySteps, dto.IsActive);
    }
}

public sealed record AssistantTemplateRequest : AdminRequest
{
    [Required, StringLength(AssistantTemplate.MaxName, MinimumLength = 1)]
    public string Name { get; init; } = "";

    [StringLength(AssistantTemplate.MaxDescription)]
    public string? Description { get; init; }

    /// <summary>The instructions a new assistant's first draft starts from.</summary>
    [StringLength(20_000)]
    public string SystemPrompt { get; init; } = "";

    /// <summary>Chat design §10.1: <c>sv</c>, <c>en</c>, <c>links[]{title,url}</c>, as an assistant version has them.</summary>
    public JsonElement? NotFound { get; init; }

    /// <summary>Chat design §15.2: condense, retrieval, history, answer, as an assistant version has them.</summary>
    public JsonElement? QuerySteps { get; init; }

    /// <summary>Offered when an assistant is created.</summary>
    public bool IsActive { get; init; } = true;

    /// <summary>The row's <c>version</c>; required to change it.</summary>
    public string? Version { get; init; }

    public void ApplyTo(AssistantTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        template.Description = Description?.Trim() ?? "";
        template.SystemPrompt = SystemPrompt;
        template.NotFound = Object("notFound", NotFound);
        template.QuerySteps = Object("querySteps", QuerySteps);
        template.IsActive = IsActive;
    }

    /// <summary>A JSON object as stored; null or absent is <c>{}</c>, anything else is refused.</summary>
    private static string Object(string field, JsonElement? value) => value switch
    {
        null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => "{}",
        { ValueKind: JsonValueKind.Object } o => o.GetRawText(),
        _ => throw AdminFaults.Field(field, AdminErrorCodes.InvalidValue),
    };
}
