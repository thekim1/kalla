using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Pipelines.Schemas;

namespace Ume.Rag.Pipelines.Sources;

/// <summary>
/// <c>Runs.SettingsHashes</c> (connectors design §12.3): one SHA-256 (lower-case hex) per effect tag over the RFC 8785
/// form of the settings members carrying that tag, defaults applied, so an omitted member and its default hash alike.
/// Cosmetic members are not hashed: changing them needs no run. <paramref name="Fetch"/> hashes the <c>behaviour</c> members
/// marked <see cref="EffectAttribute.Refetch"/> once more: a change of them starts a sync that fetches every page again
/// (§12.3: <c>fetchMode</c>, <c>readySelector</c>, <c>scrollToLoad</c>).
/// </summary>
public sealed record SettingsHashes(string Identity, string Scope, string Behaviour, string Fetch);

/// <summary>Binding, per-tag hashing and change classification of <c>Sources.Settings</c>.</summary>
public static class SourceSettings
{
    public static Type SettingsType(string sourceType) => sourceType switch
    {
        SourceTypes.UrlList => typeof(UrlListSettings),
        SourceTypes.Crawl => typeof(CrawlSettings),
        SourceTypes.Upload => typeof(UploadSettings),
        _ => throw new ArgumentOutOfRangeException(nameof(sourceType), sourceType, "Unknown source type."),
    };

    /// <summary>The settings as their record (<see cref="UrlListSettings"/>, …); unknown members throw <see cref="JsonException"/>.</summary>
    public static object Bind(string sourceType, JsonElement settings) => ContractJson.Deserialize(settings, SettingsType(sourceType));

    /// <summary>
    /// The source's <c>piiMode</c>; <see cref="PiiMode.Hold"/> (the strictest) when the type is unknown or the settings do
    /// not bind, so settings the admin would refuse never relax a hold.
    /// </summary>
    public static PiiMode PiiModeOf(string sourceType, JsonElement settings)
    {
        if (!SourceTypes.All.Contains(sourceType, StringComparer.Ordinal))
        {
            return PiiMode.Hold;
        }

        try
        {
            return Bind(sourceType, settings) switch
            {
                UrlListSettings urlList => urlList.PiiMode,
                CrawlSettings crawl => crawl.PiiMode,
                UploadSettings upload => upload.PiiMode,
                _ => PiiMode.Hold,
            };
        }
        catch (JsonException)
        {
            return PiiMode.Hold;
        }
    }

    /// <summary>The effect tag of every member, by JSON name.</summary>
    public static IReadOnlyDictionary<string, SettingEffect> Effects(string sourceType)
    {
        var typeInfo = ContractJson.Options.GetTypeInfo(SettingsType(sourceType));
        return typeInfo.Properties.ToDictionary(
            p => p.Name,
            p => (p.AttributeProvider as MemberInfo)?.GetCustomAttribute<EffectAttribute>()?.Effect
                 ?? throw new InvalidOperationException($"{typeInfo.Type.Name}.{p.Name} has no [Effect]."),
            StringComparer.Ordinal);
    }

    public static SettingsHashes Hashes(string sourceType, JsonElement settings)
    {
        var bound = Normalised(sourceType, settings);
        var hashes = HashAll(sourceType, bound);
        var refetch = new JsonObject();
        foreach (var name in Refetch(sourceType))
        {
            refetch[name] = bound[name]?.DeepClone();
        }

        return new SettingsHashes(hashes[SettingEffect.Identity], hashes[SettingEffect.Scope], hashes[SettingEffect.Behaviour], Hash(refetch));
    }

    /// <summary>The tags whose members differ between two settings objects (cosmetic included), for the impact check.</summary>
    public static IReadOnlySet<SettingEffect> Changed(string sourceType, JsonElement before, JsonElement after)
    {
        var old = HashAll(sourceType, Normalised(sourceType, before));
        var current = HashAll(sourceType, Normalised(sourceType, after));
        return old.Where(h => !string.Equals(h.Value, current[h.Key], StringComparison.Ordinal)).Select(h => h.Key).ToHashSet();
    }

    /// <summary>The JSON names of the members marked <see cref="EffectAttribute.Refetch"/>.</summary>
    public static IReadOnlyList<string> Refetch(string sourceType)
    {
        var typeInfo = ContractJson.Options.GetTypeInfo(SettingsType(sourceType));
        return [.. typeInfo.Properties
            .Where(p => (p.AttributeProvider as MemberInfo)?.GetCustomAttribute<EffectAttribute>()?.Refetch == true)
            .Select(p => p.Name)];
    }

    /// <summary>The settings bound to their record and serialised again: every member present, defaults applied.</summary>
    private static JsonObject Normalised(string sourceType, JsonElement settings)
    {
        var type = SettingsType(sourceType);
        return JsonSerializer.SerializeToNode(ContractJson.Deserialize(settings, type), type, ContractJson.Options)!.AsObject();
    }

    private static Dictionary<SettingEffect, string> HashAll(string sourceType, JsonObject bound)
    {
        var effects = Effects(sourceType);
        var groups = Enum.GetValues<SettingEffect>().ToDictionary(e => e, _ => new JsonObject());
        foreach (var (name, value) in bound)
        {
            groups[effects[name]][name] = value?.DeepClone();
        }

        return groups.ToDictionary(g => g.Key, g => Hash(g.Value));
    }

    private static string Hash(JsonObject members) => Convert.ToHexStringLower(CanonicalJson.Sha256(JsonSerializer.SerializeToElement(members)));
}
