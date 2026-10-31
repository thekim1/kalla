using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Ume.Rag.Pipelines.Json;

/// <summary>
/// The serializer options of every worker contract (source settings, pipeline definitions and templates, job payloads
/// and results, site profiles): camelCase names, enums as kebab-case strings, numbers as numbers only, nullable
/// annotations respected, nulls not written, non-ASCII text unescaped (payloads go to <c>jsonb</c>, never into HTML,
/// and stay small). The exported schemas (<see cref="Schemas.ContractSchemas"/>) are generated
/// with the same options, so what the schema allows is what these options read and write.
/// </summary>
public static class ContractJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static T Deserialize<T>(JsonElement element) =>
        element.Deserialize<T>(Options) ?? throw new JsonException($"{typeof(T).Name} is null.");

    public static object Deserialize(JsonElement element, Type type) =>
        element.Deserialize(type, Options) ?? throw new JsonException($"{type.Name} is null.");

    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, Options);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }
}
