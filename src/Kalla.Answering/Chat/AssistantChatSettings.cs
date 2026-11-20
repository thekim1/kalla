using System.Text.Json;
using System.Text.Json.Nodes;
using Kalla.Domain.Retrieval;

namespace Kalla.Answering.Chat;

/// <summary>
/// How far the model may use its own knowledge (<c>QuerySteps.answer.generalKnowledge</c>, chat design §6.2, §7.3, §10.4;
/// Decisions "General knowledge per assistant"): <see cref="Off"/> is the default and the behaviour without the setting.
/// </summary>
public enum GeneralKnowledge
{
    Off,
    Supplement,
    Fallback,
    Open,
}

/// <summary>The <c>answer.generalKnowledge</c> values as stored; anything else reads as <see cref="GeneralKnowledge.Off"/>.</summary>
public static class GeneralKnowledgeLevels
{
    public const string Off = "off";
    public const string Supplement = "supplement";
    public const string Fallback = "fallback";
    public const string Open = "open";

    public static IReadOnlyList<string> All { get; } = [Off, Supplement, Fallback, Open];

    public static GeneralKnowledge Parse(string? value) => value switch
    {
        Supplement => GeneralKnowledge.Supplement,
        Fallback => GeneralKnowledge.Fallback,
        Open => GeneralKnowledge.Open,
        _ => GeneralKnowledge.Off,
    };

    public static string ToValue(GeneralKnowledge level) => level switch
    {
        GeneralKnowledge.Supplement => Supplement,
        GeneralKnowledge.Fallback => Fallback,
        GeneralKnowledge.Open => Open,
        _ => Off,
    };

    /// <summary>
    /// The turn's level (chat design §3.8, §7.3): the stricter of the assistant's own and the one a request asked for, so a
    /// request never loosens what the assistant's admins set; null asks for nothing.
    /// </summary>
    public static GeneralKnowledge Effective(GeneralKnowledge own, GeneralKnowledge? requested) => requested is { } r && r < own ? r : own;

    /// <summary>Whether coverage <c>Low</c> gets a model answer without sources instead of the not-found text (§6.2).</summary>
    public static bool AnswersWithoutSources(GeneralKnowledge level) => level is GeneralKnowledge.Fallback or GeneralKnowledge.Open;
}

/// <summary>An assistant's chat settings from its published <c>QuerySteps</c> (chat design §15.2), with the defaults.</summary>
/// <remarks>
/// <see cref="ContextTokens"/> and <see cref="MaxOutputTokens"/> are the assistant's overrides, null (the default, *Use the
/// model's limit*) taking the model's limits (Decisions "Answer length from the model"; resolved by <see cref="AnswerLimits"/>).
/// <see cref="Temperature"/> null (the default) leaves <c>temperature</c> out of the gateway call (#275). <see cref="Rerank"/>
/// (<c>retrieval.rerank</c>: <c>chat</c> or <c>gateway</c>, B-16) is the reranker of the fused candidates; null (absent,
/// <c>off</c> or anything else) does not rerank (Decisions "Rerank pulled forward"; off by default).
/// </remarks>
public sealed record AssistantChatSettings(int TopK, int HistoryMaxMessages, int HistoryMaxTokens, int? ContextTokens, int? MaxOutputTokens, double? Temperature,
    bool AlwaysSwedish, bool CondenseEnabled = true, GeneralKnowledge GeneralKnowledge = GeneralKnowledge.Off, RerankerKind? Rerank = null)
{
    public static AssistantChatSettings Default { get; } = new(10, 10, 4000, null, null, null, false);

    public static AssistantChatSettings Parse(string? querySteps)
    {
        var json = string.IsNullOrWhiteSpace(querySteps) ? null : JsonNode.Parse(querySteps) as JsonObject;
        var d = Default;
        return new AssistantChatSettings(
            TryInt(json?["retrieval"]?["topK"]) ?? d.TopK,
            TryInt(json?["history"]?["maxMessages"]) ?? d.HistoryMaxMessages,
            TryInt(json?["history"]?["maxTokens"]) ?? d.HistoryMaxTokens,
            TryInt(json?["answer"]?["contextTokens"]),
            TryInt(json?["answer"]?["maxOutputTokens"]),
            json?["answer"]?["temperature"] is JsonValue t && t.GetValueKind() == JsonValueKind.Number ? t.GetValue<double>() : d.Temperature,
            json?["answer"]?["language"] is JsonValue l && l.GetValueKind() == JsonValueKind.String && l.GetValue<string>() == "sv",
            json?["condense"]?["enabled"] is not JsonValue c || c.GetValueKind() != JsonValueKind.False,
            GeneralKnowledgeLevels.Parse(json?["answer"]?["generalKnowledge"] is JsonValue g && g.GetValueKind() == JsonValueKind.String ? g.GetValue<string>() : null),
            RerankerKinds.Parse(json?["retrieval"]?["rerank"] is JsonValue r && r.GetValueKind() == JsonValueKind.String ? r.GetValue<string>() : null));
    }

    /// <summary>
    /// The integer this parser reads from a <c>QuerySteps</c> value, or null when it would use the default: a JSON number
    /// written as an integer (<c>10</c>; not <c>10.5</c>, <c>10.0</c> or <c>1e1</c>) in the <see cref="int"/> range. Admin
    /// rule V17 refuses what this does not read (admin design §4.6).
    /// </summary>
    public static int? TryInt(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<int>(out var i) ? i : null;
}
