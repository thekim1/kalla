using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ume.Rag.Pipelines.Schemas;

namespace Ume.Rag.Pipelines.Definitions;

/// <summary>
/// A template version (engine design §3.2, §13.1): <c>src/Ume.Rag.MigrationService/Templates/{template}.v{version}.json</c>,
/// seeded insert-if-absent; a changed template is a new version. It names its stages with their code versions and
/// declares its parameters.
/// </summary>
[Description("A pipeline template version (engine design §3.2, §13.1): its stages with their code versions and its parameters with their effect tags, schemas and defaults.")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PipelineTemplate
{
    public const string NamePattern = "^[a-z][a-z0-9-]{0,49}$";

    [RegularExpression(NamePattern)]
    [JsonPropertyName("template")]
    public required string Name { get; init; }

    [Range(1, int.MaxValue)]
    public required int Version { get; init; }

    [Description("The source type the template serves (admin V24).")]
    [SchemaKeyword("enum", "[\"url-list\", \"crawl\", \"upload\"]")]
    public required string SourceType { get; init; }

    [Description("A deprecated version still runs; saving a definition on it warns (§4 rule 2).")]
    public bool Deprecated { get; init; }

    [Description("Stage name to code version, a subset of the fixed order fetch, scan, detect, parse, clean, chunk, personal-data, embed, index.")]
    [SchemaKeyword("propertyNames", "{\"enum\": [\"fetch\", \"scan\", \"detect\", \"parse\", \"clean\", \"chunk\", \"personal-data\", \"embed\", \"index\"]}")]
    [SchemaKeyword("additionalProperties", "{\"type\": \"integer\", \"minimum\": 1}")]
    public required IReadOnlyDictionary<string, int> Stages { get; init; }

    [SchemaKeyword("propertyNames", "{\"pattern\": \"^[a-z][A-Za-z0-9]{0,49}$\"}")]
    public IReadOnlyDictionary<string, TemplateParameter> Parameters { get; init; } = new Dictionary<string, TemplateParameter>();

    [Description("Optional display names per parameter (sv, en); the admin UI's labels.ts takes precedence.")]
    public IReadOnlyDictionary<string, LocalizedText> Labels { get; init; } = new Dictionary<string, LocalizedText>();

    [Description("Shared subschemas, reachable from parameter schemas as #/$defs/<name>.")]
    public IReadOnlyDictionary<string, JsonElement> Defs { get; init; } = new Dictionary<string, JsonElement>();

    [Description("Cross-parameter rules (if/then) over the parameters with defaults applied, so the admin can check them (§4 rule 3).")]
    [MaxLength(20)]
    public IReadOnlyList<JsonElement> Rules { get; init; } = [];

    [Description("Defaults the admin sets on the source's collection when they were never set (engine design §13.1).")]
    public CollectionDefaults? CollectionDefaults { get; init; }
}

/// <summary>One template parameter (engine design §3.2).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TemplateParameter
{
    [Description("Effect tag: the first stage a change affects; the parameter is part of that stage's settings hash.")]
    [SchemaKeyword("enum", "[\"fetch\", \"scan\", \"detect\", \"parse\", \"clean\", \"chunk\", \"personal-data\", \"embed\", \"index\"]")]
    public required string Stage { get; init; }

    [Description("Routes inside the stage the parameter applies to (html, parser, text, csv); absent = every route.")]
    [MinLength(1)]
    [SchemaItems(Pattern = "^(html|parser|text|csv)$")]
    [SchemaKeyword("uniqueItems", "true")]
    public IReadOnlyList<string>? AppliesTo { get; init; }

    [Description("JSON Schema 2020-12 of the value.")]
    [SchemaKeyword("type", "[\"object\", \"boolean\"]")]
    public required JsonElement Schema { get; init; }

    [Description("The value when the definition does not set it; valid against schema.")]
    public required JsonElement Default { get; init; }
}

/// <summary>Swedish and English text.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LocalizedText
{
    [MinLength(1)]
    [MaxLength(200)]
    public required string Sv { get; init; }

    [MinLength(1)]
    [MaxLength(200)]
    public required string En { get; init; }
}

/// <summary>Collection defaults a template carries (engine design §13.1; knowledge design §6.5, §12.1).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CollectionDefaults
{
    [Description("RetrievalSettings.recency: age decay after fusion (weakness review §3).")]
    public RecencyDefault? Recency { get; init; }
}

/// <summary>An age-decay default within the V16 ranges.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RecencyDefault
{
    [Range(30, 3650)]
    public required int HalfLifeDays { get; init; }

    [Range(0.0, 0.5)]
    public required double Weight { get; init; }

    [MinLength(1)]
    [SchemaItems(MinLength = 1, MaxLength = 50)]
    public required IReadOnlyList<string> Kinds { get; init; }
}
