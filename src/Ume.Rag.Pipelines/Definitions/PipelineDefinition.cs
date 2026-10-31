using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ume.Rag.Pipelines.Schemas;

namespace Ume.Rag.Pipelines.Definitions;

/// <summary>
/// <c>config."PipelineVersions"."Definition"</c> (engine design §3.1): a template version and the parameters that
/// differ from its defaults or that the editor set. Immutable once published.
/// </summary>
[Description("A pipeline version: template, template version and parameters (engine design §3.1). The parameters are checked against the template version's parameter schemas (§4 rule 3).")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PipelineDefinition
{
    public const string CurrentSchema = "pipeline/v2";

    [SchemaKeyword("const", "\"pipeline/v2\"")]
    public required string Schema { get; init; }

    [Description("Template name, e.g. website.")]
    [RegularExpression(PipelineTemplate.NamePattern)]
    public required string Template { get; init; }

    [Range(1, int.MaxValue)]
    public required int TemplateVersion { get; init; }

    [Description("Parameter values by name; at most 16 KB of JSON (§4 rule 1).")]
    [SchemaKeyword("propertyNames", "{\"pattern\": \"^[a-z][A-Za-z0-9]{0,49}$\"}")]
    public IReadOnlyDictionary<string, JsonElement> Params { get; init; } = new Dictionary<string, JsonElement>();
}
