using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ume.Rag.ContractTests;

/// <summary>
/// A JSON Schema 2020-12 validator for the keywords the parser's pydantic schemas use, and nothing else: <c>$ref</c>
/// into <c>$defs</c>, <c>type</c>, <c>enum</c>, <c>const</c>, <c>properties</c>, <c>required</c>,
/// <c>additionalProperties</c>, <c>items</c>, <c>minItems</c>, <c>maxItems</c>, <c>anyOf</c>, <c>minimum</c>,
/// <c>maximum</c>, <c>minLength</c>, <c>maxLength</c>, <c>pattern</c>; annotations are ignored. Any other keyword throws,
/// so a schema that starts to use one fails the tests instead of being half-checked. Test code only: the candidate
/// library (JsonSchema.Net) ships its binaries under a maintenance-fee EULA, which is the user's decision.
/// </summary>
public sealed class JsonSchemaSubset
{
    private static readonly HashSet<string> Annotations =
        ["$schema", "$id", "$defs", "title", "description", "default", "examples"];

    private readonly JsonElement _root;

    private JsonSchemaSubset(JsonElement root) => _root = root;

    public static JsonSchemaSubset Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return new JsonSchemaSubset(document.RootElement.Clone());
    }

    /// <summary>Every violation as "<c>path: problem</c>"; empty when <paramref name="instance"/> is valid.</summary>
    public IReadOnlyList<string> Validate(JsonElement instance)
    {
        var errors = new List<string>();
        Check(_root, instance, "$", errors);
        return errors;
    }

    private void Check(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        foreach (var keyword in schema.EnumerateObject())
        {
            var rule = keyword.Value;
            switch (keyword.Name)
            {
                case var name when Annotations.Contains(name):
                    break;
                case "$ref":
                    Check(Resolve(rule.GetString()!), value, path, errors);
                    break;
                case "type":
                    var types = rule.ValueKind == JsonValueKind.Array
                        ? rule.EnumerateArray().Select(t => t.GetString()!).ToList()
                        : [rule.GetString()!];
                    if (!types.Any(t => HasType(value, t)))
                    {
                        errors.Add($"{path}: expected {string.Join(" or ", types)}, got {value.ValueKind}");
                    }

                    break;
                case "enum":
                    if (!rule.EnumerateArray().Any(e => JsonElement.DeepEquals(e, value)))
                    {
                        errors.Add($"{path}: {value.GetRawText()} is not in the enum");
                    }

                    break;
                case "const":
                    if (!JsonElement.DeepEquals(rule, value))
                    {
                        errors.Add($"{path}: expected {rule.GetRawText()}");
                    }

                    break;
                case "properties":
                    if (value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var property in rule.EnumerateObject())
                        {
                            if (value.TryGetProperty(property.Name, out var child))
                            {
                                Check(property.Value, child, $"{path}.{property.Name}", errors);
                            }
                        }
                    }

                    break;
                case "required":
                    if (value.ValueKind == JsonValueKind.Object)
                    {
                        errors.AddRange(rule.EnumerateArray()
                            .Select(r => r.GetString()!)
                            .Where(r => !value.TryGetProperty(r, out _))
                            .Select(r => $"{path}: missing required property {r}"));
                    }

                    break;
                case "additionalProperties":
                    if (value.ValueKind == JsonValueKind.Object)
                    {
                        var declared = schema.TryGetProperty("properties", out var properties)
                            ? properties.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
                            : [];
                        foreach (var extra in value.EnumerateObject().Where(p => !declared.Contains(p.Name)))
                        {
                            if (rule.ValueKind == JsonValueKind.False)
                            {
                                errors.Add($"{path}: additional property {extra.Name}");
                            }
                            else if (rule.ValueKind == JsonValueKind.Object)
                            {
                                Check(rule, extra.Value, $"{path}.{extra.Name}", errors);
                            }
                        }
                    }

                    break;
                case "items":
                    if (value.ValueKind == JsonValueKind.Array)
                    {
                        var index = 0;
                        foreach (var item in value.EnumerateArray())
                        {
                            Check(rule, item, $"{path}[{index++}]", errors);
                        }
                    }

                    break;
                case "minItems" when value.ValueKind == JsonValueKind.Array && value.GetArrayLength() < rule.GetInt32():
                    errors.Add($"{path}: fewer than {rule.GetInt32()} items");
                    break;
                case "maxItems" when value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > rule.GetInt32():
                    errors.Add($"{path}: more than {rule.GetInt32()} items");
                    break;
                case "minItems" or "maxItems":
                    break;
                case "anyOf":
                    if (!rule.EnumerateArray().Any(branch =>
                    {
                        var branchErrors = new List<string>();
                        Check(branch, value, path, branchErrors);
                        return branchErrors.Count == 0;
                    }))
                    {
                        errors.Add($"{path}: matches no anyOf branch");
                    }

                    break;
                case "minimum" when value.ValueKind == JsonValueKind.Number && value.GetDouble() < rule.GetDouble():
                    errors.Add($"{path}: below the minimum {rule.GetRawText()}");
                    break;
                case "maximum" when value.ValueKind == JsonValueKind.Number && value.GetDouble() > rule.GetDouble():
                    errors.Add($"{path}: above the maximum {rule.GetRawText()}");
                    break;
                case "minimum" or "maximum":
                    break;
                case "minLength" when value.ValueKind == JsonValueKind.String && Length(value) < rule.GetInt32():
                    errors.Add($"{path}: shorter than {rule.GetInt32()}");
                    break;
                case "maxLength" when value.ValueKind == JsonValueKind.String && Length(value) > rule.GetInt32():
                    errors.Add($"{path}: longer than {rule.GetInt32()}");
                    break;
                case "minLength" or "maxLength":
                    break;
                case "pattern":
                    if (value.ValueKind == JsonValueKind.String
                        && !Regex.IsMatch(value.GetString()!, rule.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                    {
                        errors.Add($"{path}: does not match {rule.GetString()}");
                    }

                    break;
                default:
                    throw new NotSupportedException($"JSON Schema keyword {keyword.Name} at {path} is not supported by the test validator.");
            }
        }
    }

    private JsonElement Resolve(string reference)
    {
        const string prefix = "#/$defs/";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"$ref {reference} is not supported by the test validator.");
        }

        return _root.GetProperty("$defs").GetProperty(reference[prefix.Length..]);
    }

    private static bool HasType(JsonElement value, string type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        _ => throw new NotSupportedException($"JSON Schema type {type} is not supported by the test validator."),
    };

    // JSON Schema counts code points, not UTF-16 units.
    private static int Length(JsonElement value) => value.GetString()!.EnumerateRunes().Count();
}
