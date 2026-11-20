using System.Text.Json;
using System.Text.Json.Nodes;
using Kalla.Pipelines.Json;
using Kalla.Pipelines.Schemas;

namespace Kalla.Pipelines.Definitions;

/// <summary>The template versions a validator knows (seeded rows in the admin and the worker; files in tests).</summary>
public interface ITemplateCatalog
{
    CompiledTemplate? Find(string template, int version);
}

/// <summary>A template version that passed <see cref="TemplateCatalog.Check"/>, with its parameter schemas compiled.</summary>
public sealed class CompiledTemplate
{
    private readonly Dictionary<string, SchemaValidator> _parameters;
    private readonly SchemaValidator? _rules;

    internal CompiledTemplate(PipelineTemplate template)
    {
        Template = template;
        _parameters = template.Parameters.ToDictionary(p => p.Key, p => SchemaValidator.Compile(WithDefs(template, p.Value.Schema)), StringComparer.Ordinal);
        _rules = template.Rules.Count == 0 ? null : SchemaValidator.Compile(WithDefs(template, RulesSchema(template)));
        ParamsSchema = BuildParamsSchema(template);
    }

    public PipelineTemplate Template { get; }

    /// <summary>
    /// One JSON Schema for the whole <c>params</c> object, for the admin's template endpoint and generated form (admin
    /// design §4.5, §6.3): each parameter's schema with its <c>default</c>, <c>x-stage</c>, <c>x-applies-to</c> and
    /// <c>x-labels</c>; unknown parameters refused; the template's rules as <c>allOf</c>.
    /// </summary>
    public JsonObject ParamsSchema { get; }

    /// <summary>The definition's parameters over the template defaults (engine design §3.1: defaults apply before hashing).</summary>
    public JsonObject Resolve(IReadOnlyDictionary<string, JsonElement> parameters)
    {
        var resolved = new JsonObject();
        foreach (var (name, parameter) in Template.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            resolved[name] = JsonNode.Parse((parameters.TryGetValue(name, out var value) ? value : parameter.Default).GetRawText());
        }

        return resolved;
    }

    internal IReadOnlyList<SchemaError>? ValidateParameter(string name, JsonElement value) =>
        _parameters.TryGetValue(name, out var validator) ? validator.Validate(value) : null;

    internal IReadOnlyList<SchemaError> ValidateRules(JsonObject resolved) =>
        _rules is null ? [] : _rules.Validate(JsonSerializer.SerializeToElement(resolved));

    internal static JsonObject WithDefs(PipelineTemplate template, JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText()) as JsonObject ?? [];
        node["$schema"] = ContractSchemaExporter.Dialect;
        if (template.Defs.Count > 0)
        {
            node["$defs"] = Defs(template);
        }

        return node;
    }

    private static JsonElement RulesSchema(PipelineTemplate template) =>
        JsonSerializer.SerializeToElement(new JsonObject { ["allOf"] = new JsonArray([.. template.Rules.Select(r => JsonNode.Parse(r.GetRawText()))]) });

    private static JsonObject Defs(PipelineTemplate template) =>
        new([.. template.Defs.OrderBy(d => d.Key, StringComparer.Ordinal).Select(d => KeyValuePair.Create(d.Key, JsonNode.Parse(d.Value.GetRawText())))]);

    private static JsonObject BuildParamsSchema(PipelineTemplate template)
    {
        var properties = new JsonObject();
        foreach (var (name, parameter) in template.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var schema = JsonNode.Parse(parameter.Schema.GetRawText()) as JsonObject ?? [];
            schema["default"] = JsonNode.Parse(parameter.Default.GetRawText());
            schema["x-stage"] = parameter.Stage;
            if (parameter.AppliesTo is { } routes)
            {
                schema["x-applies-to"] = new JsonArray([.. routes.Select(r => JsonValue.Create(r))]);
            }

            if (template.Labels.TryGetValue(name, out var label))
            {
                schema["x-labels"] = new JsonObject { ["sv"] = label.Sv, ["en"] = label.En };
            }

            properties[name] = schema;
        }

        var root = new JsonObject
        {
            ["$schema"] = ContractSchemaExporter.Dialect,
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };
        if (template.Rules.Count > 0)
        {
            root["allOf"] = new JsonArray([.. template.Rules.Select(r => JsonNode.Parse(r.GetRawText()))]);
        }

        if (template.Defs.Count > 0)
        {
            root["$defs"] = Defs(template);
        }

        return root;
    }
}

/// <summary>The template versions, each checked and compiled once.</summary>
public sealed class TemplateCatalog : ITemplateCatalog
{
    private readonly Dictionary<(string, int), CompiledTemplate> _templates;

    private TemplateCatalog(Dictionary<(string, int), CompiledTemplate> templates) => _templates = templates;

    private const string ResourcePrefix = "Kalla.Pipelines.Templates.";

    private static readonly Lazy<TemplateCatalog> BuiltInCatalog = new(LoadBuiltIn);

    /// <summary>
    /// The template files of <c>src/Kalla.MigrationService/Templates/</c>, embedded in this assembly: the versions the
    /// migrator seeds are the versions the admin and the worker validate against.
    /// </summary>
    public static TemplateCatalog BuiltIn => BuiltInCatalog.Value;

    public IReadOnlyList<CompiledTemplate> All => [.. _templates.Values.OrderBy(t => t.Template.Name, StringComparer.Ordinal).ThenBy(t => t.Template.Version)];

    public CompiledTemplate? Find(string template, int version) => _templates.GetValueOrDefault((template, version));

    /// <summary>Checks and compiles every template; throws <see cref="InvalidOperationException"/> listing every problem.</summary>
    public static TemplateCatalog Create(IEnumerable<PipelineTemplate> templates)
    {
        var compiled = new Dictionary<(string, int), CompiledTemplate>();
        var problems = new List<string>();
        foreach (var template in templates)
        {
            var found = Check(template).ToList();
            if (compiled.ContainsKey((template.Name, template.Version)))
            {
                found.Add("the same template version is listed twice");
            }
            else if (found.Count == 0)
            {
                compiled[(template.Name, template.Version)] = new CompiledTemplate(template);
            }

            problems.AddRange(found.Select(p => $"{template.Name}.v{template.Version}: {p}"));
        }

        return problems.Count == 0 ? new TemplateCatalog(compiled) : throw Invalid(problems);
    }

    /// <summary>Parses template files (the template schema is checked first) and creates the catalog.</summary>
    public static TemplateCatalog Load(IEnumerable<string> templateJson)
    {
        var validator = ContractSchemas.Validator(typeof(PipelineTemplate));
        var templates = new List<PipelineTemplate>();
        var problems = new List<string>();
        foreach (var json in templateJson)
        {
            using var document = JsonDocument.Parse(json);
            var errors = validator.Validate(document.RootElement);
            if (errors.Count > 0)
            {
                problems.AddRange(errors.Select(e => $"{e.InstanceLocation}: {e.Message}"));
                continue;
            }

            templates.Add(ContractJson.Deserialize<PipelineTemplate>(document.RootElement));
        }

        return problems.Count == 0 ? Create(templates) : throw Invalid(problems);
    }

    /// <summary>
    /// What makes a template unusable: unknown, missing or out-of-order stages, a parameter tagged with a stage the
    /// template lacks or narrowed to a route of another stage, a parameter schema that does not compile, a default that
    /// fails its schema, defaults that fail the rules, labels for unknown parameters.
    /// </summary>
    public static IReadOnlyList<string> Check(PipelineTemplate template)
    {
        var problems = new List<string>();
        var order = template.Stages.Keys.Select(s => StageNames.Order.ToList().IndexOf(s)).ToList();
        problems.AddRange(template.Stages.Keys.Where(s => Stages.Find(s) is null).Select(s => $"unknown stage '{s}'"));
        problems.AddRange(StageNames.Required.Where(s => !template.Stages.ContainsKey(s)).Select(s => $"missing stage '{s}'"));
        if (!order.SequenceEqual(order.Order()))
        {
            problems.Add($"stages are not in the order {string.Join(", ", StageNames.Order)}");
        }

        problems.AddRange(template.Stages.Where(s => s.Value < 1).Select(s => $"stage '{s.Key}' has version {s.Value}"));
        if (!SourceTypesKnown(template.SourceType))
        {
            problems.Add($"unknown source type '{template.SourceType}'");
        }

        foreach (var (name, parameter) in template.Parameters)
        {
            if (!template.Stages.ContainsKey(parameter.Stage))
            {
                problems.Add($"parameter '{name}' is tagged with stage '{parameter.Stage}', which the template does not run");
            }

            if (parameter.AppliesTo is not null && !string.Equals(parameter.Stage, StageNames.Parse, StringComparison.Ordinal))
            {
                problems.Add($"parameter '{name}' narrows routes, which exist only in '{StageNames.Parse}'");
            }

            try
            {
                var errors = SchemaValidator.Compile(CompiledTemplate.WithDefs(template, parameter.Schema)).Validate(parameter.Default);
                problems.AddRange(errors.Take(1).Select(e => $"the default of '{name}' fails its schema: {e.Message}"));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                problems.Add($"the schema of '{name}' does not compile ({ex.GetType().Name})");
            }
        }

        problems.AddRange(template.Labels.Keys.Where(k => !template.Parameters.ContainsKey(k)).Select(k => $"label for unknown parameter '{k}'"));
        if (problems.Count == 0 && template.Rules.Count > 0)
        {
            try
            {
                var compiled = new CompiledTemplate(template);
                problems.AddRange(compiled.ValidateRules(compiled.Resolve(new Dictionary<string, JsonElement>())).Take(1)
                    .Select(e => $"the defaults fail a rule: {e.Message}"));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                problems.Add($"a rule does not compile ({ex.GetType().Name})");
            }
        }

        return problems;
    }

    private static TemplateCatalog LoadBuiltIn()
    {
        var assembly = typeof(TemplateCatalog).Assembly;
        return Load(assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(n =>
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(n)!);
                return reader.ReadToEnd();
            }));
    }

    private static bool SourceTypesKnown(string sourceType) => Sources.SourceTypes.All.Contains(sourceType, StringComparer.Ordinal);

    private static InvalidOperationException Invalid(IEnumerable<string> problems) =>
        new("Invalid pipeline templates: " + string.Join("; ", problems));
}
