using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LateApexEarlySpeed.Json.Schema;
using LateApexEarlySpeed.Json.Schema.Common;

namespace Ume.Rag.Pipelines.Json;

/// <summary>One violation: where in the instance (JSON pointer), the failing keyword, and the validator's message.</summary>
public sealed record SchemaError(string InstanceLocation, string Keyword, string Message);

/// <summary>
/// A compiled JSON Schema 2020-12 (to-verify #17: LateApexEarlySpeed.Json.Schema, BSD-3-Clause). A schema can only
/// reference itself: no external document is registered and no populator is set, so the library never fetches a
/// <c>$ref</c> over HTTP or from disk. <c>format</c> is asserted. Patterns are .NET regular expressions with the
/// library's 200 ms match timeout; a timed-out match is a violation, never valid.
/// </summary>
public sealed class SchemaValidator
{
    private static readonly JsonSchemaOptions ValidationOptions = new() { OutputFormat = OutputFormat.List };

    private readonly JsonValidator _validator;

    private SchemaValidator(JsonValidator validator) => _validator = validator;

    /// <summary>Compiles <paramref name="schema"/>; throws when it is not a valid schema (for example a bad pattern).</summary>
    public static SchemaValidator Compile(JsonNode schema) => new(new JsonValidator(schema.ToJsonString()));

    public static SchemaValidator Compile(JsonElement schema) => new(new JsonValidator(schema));

    public bool IsValid(JsonElement instance) => Validate(instance).Count == 0;

    /// <summary>Every failed assertion; empty when the instance is valid.</summary>
    public IReadOnlyList<SchemaError> Validate(JsonElement instance)
    {
        try
        {
            var result = _validator.Validate(instance, ValidationOptions);
            return result.IsValid
                ? []
                : [.. result.ValidationErrors.Select(e => new SchemaError(e.InstanceLocation.ToString(), e.Keyword ?? string.Empty, e.ErrorMessage))];
        }
        catch (RegexMatchTimeoutException)
        {
            return [new SchemaError(string.Empty, "pattern", "A pattern match timed out.")];
        }
    }
}
