using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Retrieval;

namespace Ume.Rag.AdminApi;

/// <summary>
/// V39, an object's gateway key (admin design §4.6; cost-attribution §4.2; data-model §2.1), against the <c>GatewayKeys</c>
/// mirror: a set key is a row of the object's department, <c>Active</c>, its owner active and <c>usable</c> (it shares a
/// residency with Ume.Rag's credential key); at least one of its residencies is within the object's ceiling (empty = all);
/// and its model list (empty = all) holds every model the object uses. Until the gateway acts as keys (feature
/// <c>act-as-key</c>, phase 2) the models are checked against the mirror only, and a missing key is a warning.
/// </summary>
public static class GatewayKeyRules
{
    /// <summary><c>gateway_key_invalid</c>'s <c>{reason}</c> values.</summary>
    public static class Reasons
    {
        public const string Unknown = "unknown";
        public const string NotActive = "not active";
        public const string OwnerInactive = "owner inactive";
        public const string NotUsable = "no residency in common with the platform's key";
        public const string OtherDepartment = "another department";
    }

    /// <summary>Why a key cannot be chosen at all (V39's status part), or null when it can.</summary>
    public static string? UnusableReason(GatewayKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Status != "Active" ? Reasons.NotActive
            : !key.OwnerActive ? Reasons.OwnerInactive
            : !key.Usable ? Reasons.NotUsable
            : null;
    }

    /// <summary>
    /// The refusals for <paramref name="keyId"/> on an object of <paramref name="departmentId"/> at <paramref name="ceiling"/>
    /// that uses <paramref name="models"/> (empty names are ignored). Empty when no key is set: that is
    /// <see cref="WarningsAsync"/>' part.
    /// </summary>
    public static async Task<IReadOnlyList<RuleProblem>> CheckAsync(
        AdminContext admin, Guid? keyId, Guid departmentId, Residency ceiling, IEnumerable<string?> models, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        if (keyId is not { } id)
        {
            return [];
        }

        var key = await admin.Db.GatewayKeys.AsNoTracking().SingleOrDefaultAsync(k => k.Id == id, ct);
        var name = key?.Name ?? id.ToString();
        var reason = key is null ? Reasons.Unknown : key.DepartmentId != departmentId ? Reasons.OtherDepartment : UnusableReason(key);
        if (reason is not null)
        {
            return [new(AdminErrorCodes.GatewayKeyInvalid, new Dictionary<string, string> { ["key"] = name, ["reason"] = reason }, [id.ToString()])];
        }

        var problems = new List<RuleProblem>();
        if (key!.AllowedResidencies.Length > 0 &&
            !key.AllowedResidencies.Any(r => ResidencyCeiling.TryParse(r, out var residency) && residency <= ceiling))
        {
            problems.Add(new(AdminErrorCodes.GatewayKeyResidency,
                new Dictionary<string, string> { ["key"] = name, ["ceiling"] = ResidencyCeiling.ToHeader(ceiling) }, [.. key.AllowedResidencies]));
        }

        if (key.AllowedModels.Length > 0)
        {
            var missing = models.OfType<string>().Where(m => m.Length > 0).Distinct(StringComparer.Ordinal)
                .Where(m => !key.AllowedModels.Contains(m, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                problems.Add(new(AdminErrorCodes.GatewayKeyModel, new Dictionary<string, string> { ["key"] = name }, missing));
            }
        }

        return problems;
    }

    /// <summary>
    /// V39's warnings: no key (<c>gateway_key_required</c>: the calls are charged to the platform's owner team) and a key
    /// with a system prompt (<c>gateway_key_system_prompt</c>).
    /// </summary>
    public static async Task<IReadOnlyList<RuleProblem>> WarningsAsync(AdminContext admin, Guid? keyId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        if (keyId is not { } id)
        {
            return [new(AdminErrorCodes.GatewayKeyRequired, null, [])];
        }

        var key = await admin.Db.GatewayKeys.AsNoTracking().SingleOrDefaultAsync(k => k.Id == id, ct);
        return key is { HasSystemPrompt: true }
            ? [new(AdminErrorCodes.GatewayKeySystemPrompt, new Dictionary<string, string> { ["key"] = key.Name }, [id.ToString()])]
            : [];
    }

    /// <summary>
    /// The models an assistant version uses (V39): the answer model, the platform's condense model while condense is on,
    /// each linked collection's embedding profile model, and the rerank model the version's rerank names.
    /// </summary>
    public static async Task<IReadOnlyList<string?>> AssistantModelsAsync(AdminContext admin, AssistantVersion version, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(version);
        var settings = await admin.Db.Settings.AsNoTracking().Select(s => new { s.Chat, s.Retrieval }).SingleOrDefaultAsync(ct);
        var chat = Ume.Rag.Answering.Chat.AssistantChatSettings.Parse(version.QuerySteps);
        List<string?> models = [version.GatewayModel];
        if (chat.CondenseEnabled)
        {
            models.Add(StringOf(settings?.Chat, "condenseModel"));
        }

        var ids = version.LinkedCollectionIds().Distinct().ToList();
        models.AddRange(await (from c in admin.Db.Collections.AsNoTracking()
                               join p in admin.Db.EmbeddingProfiles.AsNoTracking() on c.EmbeddingProfileId equals p.Id
                               where ids.Contains(c.Id)
                               select p.GatewayModel).Distinct().ToListAsync(ct));
        models.Add(chat.Rerank switch
        {
            RerankerKind.ChatModel => StringOf(settings?.Retrieval, "rerankModel"),
            RerankerKind.Gateway => StringOf(settings?.Retrieval, "gatewayRerankModel"),
            _ => null,
        });
        return models;
    }

    /// <summary>The models a collection uses (V39): its embedding profile's, and the evaluation and judge models.</summary>
    public static async Task<IReadOnlyList<string?>> CollectionModelsAsync(AdminContext admin, Collection collection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(collection);
        var profile = await admin.Db.EmbeddingProfiles.AsNoTracking().Where(p => p.Id == collection.EmbeddingProfileId).Select(p => p.GatewayModel).SingleOrDefaultAsync(ct);
        var evaluation = await admin.Db.Settings.AsNoTracking().Select(s => new { s.EvaluationModel, s.EvaluationJudgeModel }).SingleOrDefaultAsync(ct);
        return [profile, evaluation?.EvaluationModel, evaluation?.EvaluationJudgeModel];
    }

    private static string? StringOf(string? json, string member)
    {
        try
        {
            return (string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject)?[member] is JsonValue v && v.GetValueKind() == JsonValueKind.String &&
                   v.GetValue<string>() is { Length: > 0 } value
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
