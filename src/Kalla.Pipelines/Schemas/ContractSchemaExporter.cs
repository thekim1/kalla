using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Kalla.Pipelines.Json;

namespace Kalla.Pipelines.Schemas;

/// <summary>
/// Exports a contract record as JSON Schema 2020-12 with .NET's <see cref="JsonSchemaExporter"/> under
/// <see cref="ContractJson.Options"/>, plus what the exporter does not read: <see cref="DescriptionAttribute"/>,
/// <see cref="RangeAttribute"/>, <see cref="MinLengthAttribute"/>, <see cref="MaxLengthAttribute"/>,
/// <see cref="RegularExpressionAttribute"/>, <see cref="EffectAttribute"/> (<c>x-effect</c>),
/// <see cref="SchemaItemsAttribute"/>, <see cref="SchemaKeywordAttribute"/>, and each optional member's C# default
/// (read from a new instance) as <c>default</c>. The C# record is the source; the files in <c>contracts/</c> are its
/// export, and a test fails when they differ.
/// </summary>
public static class ContractSchemaExporter
{
    public const string Dialect = "https://json-schema.org/draft/2020-12/schema";

    private static readonly ConcurrentDictionary<Type, object?> DefaultInstances = new();

    public static JsonObject Export(Type type, string id, string title)
    {
        var options = new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true, TransformSchemaNode = Transform };
        var exported = (JsonObject)ContractJson.Options.GetJsonSchemaAsNode(type, options);
        var root = new JsonObject { ["$schema"] = Dialect, ["$id"] = id, ["title"] = title };
        foreach (var name in exported.Select(p => p.Key).ToList())
        {
            var value = exported[name];
            exported.Remove(name);
            root[name] = value;
        }

        return root;
    }

    private static JsonObject Transform(JsonSchemaExporterContext context, JsonNode schema)
    {
        if (schema is not JsonObject node)
        {
            // "true" (any value, e.g. a JsonElement member) becomes {} so annotations can be added to it.
            node = [];
        }

        if (context.TypeInfo.Kind == JsonTypeInfoKind.Object)
        {
            foreach (var keyword in AttributesOf<SchemaKeywordAttribute>(context.TypeInfo.Type))
            {
                node[keyword.Keyword] = JsonNode.Parse(keyword.Json);
            }

            if (context.PropertyInfo is null && AttributesOf<DescriptionAttribute>(context.TypeInfo.Type).FirstOrDefault() is { } typeDescription)
            {
                node["description"] = typeDescription.Description;
            }
        }

        if (context.PropertyInfo is { } property)
        {
            ApplyMemberAttributes(node, property);
            if (!property.IsRequired && DefaultOf(property) is { } defaultValue)
            {
                node["default"] = defaultValue;
            }
        }

        // A nullable member restricted by an "enum" attribute still accepts null, as its "type" says.
        if (node["enum"] is JsonArray values && node["type"] is JsonNode types && TypeNames(types).Contains("null")
            && !values.Any(v => v is null))
        {
            values.Add(null);
        }

        return node;
    }

    private static void ApplyMemberAttributes(JsonObject node, JsonPropertyInfo property)
    {
        var provider = property.AttributeProvider;
        if (provider is null)
        {
            return;
        }

        var isArray = node["type"] is JsonNode type && TypeNames(type).Contains("array");
        foreach (var attribute in provider.GetCustomAttributes(inherit: true))
        {
            switch (attribute)
            {
                case DescriptionAttribute description:
                    node["description"] = description.Description;
                    break;
                case EffectAttribute effect:
                    node["x-effect"] = JsonNamingPolicy.KebabCaseLower.ConvertName(effect.Effect.ToString());
                    break;
                case RangeAttribute range:
                    node[range.MinimumIsExclusive ? "exclusiveMinimum" : "minimum"] = Number(range.Minimum);
                    node[range.MaximumIsExclusive ? "exclusiveMaximum" : "maximum"] = Number(range.Maximum);
                    break;
                case MinLengthAttribute minLength:
                    node[isArray ? "minItems" : "minLength"] = minLength.Length;
                    break;
                case MaxLengthAttribute maxLength:
                    node[isArray ? "maxItems" : "maxLength"] = maxLength.Length;
                    break;
                case RegularExpressionAttribute pattern:
                    node["pattern"] = pattern.Pattern;
                    break;
                case SchemaItemsAttribute items when node["items"] is JsonObject itemSchema:
                    if (items.MinLength > 0)
                    {
                        itemSchema["minLength"] = items.MinLength;
                    }

                    if (items.MaxLength > 0)
                    {
                        itemSchema["maxLength"] = items.MaxLength;
                    }

                    if (items.Pattern is not null)
                    {
                        itemSchema["pattern"] = items.Pattern;
                    }

                    if (items.Format is not null)
                    {
                        itemSchema["format"] = items.Format;
                    }

                    break;
                case SchemaItemsAttribute:
                    throw new InvalidOperationException($"{property.DeclaringType.Name}.{property.Name}: [SchemaItems] needs an array member.");
                case SchemaKeywordAttribute keyword:
                    node[keyword.Keyword] = JsonNode.Parse(keyword.Json);
                    break;
            }
        }
    }

    private static JsonNode? DefaultOf(JsonPropertyInfo property)
    {
        if (property.Get is null || property.DeclaringType.IsAbstract)
        {
            return null;
        }

        var instance = DefaultInstances.GetOrAdd(property.DeclaringType, type =>
            type.GetConstructor(Type.EmptyTypes) is null ? null : Activator.CreateInstance(type));
        var value = instance is null ? null : property.Get(instance);
        return value is null or JsonElement { ValueKind: JsonValueKind.Undefined }
            ? null
            : JsonSerializer.SerializeToNode(value, property.PropertyType, ContractJson.Options);
    }

    private static IEnumerable<T> AttributesOf<T>(Type type) where T : Attribute =>
        type.GetCustomAttributes(typeof(T), inherit: true).Cast<T>();

    private static HashSet<string> TypeNames(JsonNode type) => type switch
    {
        JsonArray array => [.. array.Select(t => t!.GetValue<string>())],
        _ => [type.GetValue<string>()],
    };

    private static JsonValue Number(object value) => value switch
    {
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        _ => throw new InvalidOperationException($"Unsupported range bound {value}."),
    };
}
