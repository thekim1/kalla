using Ume.Rag.Pipelines.Sources;

namespace Ume.Rag.Pipelines.Schemas;

/// <summary>
/// The source-setting effect tag (connectors design §12.3), exported as <c>"x-effect"</c>: what the smallest re-sync is
/// when the setting changes. Every member of a source settings record carries one.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class EffectAttribute(SettingEffect effect) : Attribute
{
    public SettingEffect Effect { get; } = effect;
}

/// <summary>Constraints on the items of an array member, exported into its <c>items</c> schema.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SchemaItemsAttribute : Attribute
{
    /// <summary>Minimum string length of each item; 0 for none.</summary>
    public int MinLength { get; init; }

    /// <summary>Maximum string length of each item; 0 for none.</summary>
    public int MaxLength { get; init; }

    /// <summary>A <c>pattern</c> each item matches.</summary>
    public string? Pattern { get; init; }

    /// <summary>A JSON Schema <c>format</c> each item has.</summary>
    public string? Format { get; init; }
}

/// <summary>
/// A JSON Schema keyword with its value as JSON text, merged into the member's or the type's schema: <c>const</c>,
/// <c>enum</c>, <c>format</c>, <c>uniqueItems</c>, <c>propertyNames</c>, or an <c>allOf</c> of <c>if</c>/<c>then</c>
/// rules that a C# type cannot express.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, AllowMultiple = true)]
public sealed class SchemaKeywordAttribute(string keyword, string json) : Attribute
{
    public string Keyword { get; } = keyword;

    public string Json { get; } = json;
}
