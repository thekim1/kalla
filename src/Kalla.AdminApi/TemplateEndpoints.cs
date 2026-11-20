using System.Text.Json.Nodes;
using Kalla.Domain.Admin;
using Kalla.Pipelines.Definitions;

namespace Kalla.AdminApi;

/// <summary>
/// <c>GET /api/templates</c>, <c>GET /api/templates/{id}/versions/{v}</c> (admin design §4.5; every role): the built-in
/// pipeline templates (J3: <see cref="TemplateCatalog.BuiltIn"/>) with the parameter schema the source form is generated
/// from (§6.1, §6.3). Editing templates (draft, publish) is P1b.
/// </summary>
public static class TemplateEndpoints
{
    public static RouteGroupBuilder MapTemplates(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var templates = api.MapGroup("/templates").WithTags("Templates");

        templates.MapGet("", () => TemplateCatalog.BuiltIn.All.Select(TemplateSummary.Of).ToList())
            .RequireAuthorization(AdminPolicies.Read).WithName("ListTemplates").WithSummary("Pipeline templates, every version");

        // No route constraint on the version: a malformed one is 404 not_found after authorization, like an unknown one.
        templates.MapGet("/{id}/versions/{version}", (string id, string version) =>
                int.TryParse(version, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) &&
                TemplateCatalog.BuiltIn.Find(id, number) is { } compiled
                    ? TemplateDetail.Of(compiled)
                    : throw AdminFaults.Of(AdminErrorCodes.NotFound))
            .RequireAuthorization(AdminPolicies.Read).WithName("GetTemplateVersion").WithSummary("One template version with its parameter schema");

        return api;
    }
}

public sealed record TemplateSummary(string Id, int Version, string SourceType, bool Deprecated, IReadOnlyList<string> Stages)
{
    public static TemplateSummary Of(CompiledTemplate t) =>
        new(t.Template.Name, t.Template.Version, t.Template.SourceType, t.Template.Deprecated, [.. t.Template.Stages.OrderBy(s => s.Value).Select(s => s.Key)]);
}

/// <summary>A template version: stages, the parameter schema (defaults, <c>x-stage</c>, <c>x-applies-to</c>, <c>x-labels</c>, rules) and collection defaults.</summary>
public sealed record TemplateDetail(string Id, int Version, string SourceType, bool Deprecated, IReadOnlyList<string> Stages, JsonObject ParamsSchema,
    CollectionDefaults? CollectionDefaults)
{
    public static TemplateDetail Of(CompiledTemplate t) => new(t.Template.Name, t.Template.Version, t.Template.SourceType, t.Template.Deprecated,
        [.. t.Template.Stages.OrderBy(s => s.Value).Select(s => s.Key)], (JsonObject)t.ParamsSchema.DeepClone(), t.Template.CollectionDefaults);
}
