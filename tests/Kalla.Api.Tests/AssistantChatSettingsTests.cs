using System.Text.Json.Nodes;
using Kalla.Answering.Chat;

namespace Kalla.Api.Tests;

/// <summary>
/// <see cref="AssistantChatSettings.Parse"/> against admin rule V17 (admin design §4.6): every value V17 lets through is
/// read as published, never replaced by the default; what the runtime cannot read, V17 refuses.
/// </summary>
public sealed class AssistantChatSettingsTests
{
    [Theory]
    [InlineData("retrieval", "topK", 1)]
    [InlineData("retrieval", "topK", 50)]
    [InlineData("history", "maxMessages", 0)]
    [InlineData("history", "maxMessages", 20)]
    [InlineData("history", "maxTokens", 0)]
    [InlineData("history", "maxTokens", 8000)]
    [InlineData("answer", "maxOutputTokens", 64)]
    [InlineData("answer", "maxOutputTokens", 4096)]
    [InlineData("answer", "contextTokens", 4096)]
    [InlineData("answer", "contextTokens", 24_576)]
    [InlineData("answer", "contextTokens", 131_072)]
    public void An_integer_V17_accepts_is_read_unchanged(string section, string key, int value)
    {
        var settings = AssistantChatSettings.Parse(new JsonObject { [section] = new JsonObject { [key] = value } }.ToJsonString());

        var read = key switch
        {
            "topK" => settings.TopK,
            "maxMessages" => settings.HistoryMaxMessages,
            "maxTokens" => settings.HistoryMaxTokens,
            "maxOutputTokens" => settings.MaxOutputTokens,
            _ => settings.ContextTokens,
        };
        read.ShouldBe(value);
    }

    [Theory]
    [InlineData("10.5")]
    [InlineData("\"10\"")]
    [InlineData("10.0")]
    [InlineData("1e1")]
    public void A_value_that_is_not_an_integer_as_written_is_not_read(string value) =>
        AssistantChatSettings.TryInt(JsonNode.Parse(value)).ShouldBeNull();

    [Theory]
    [InlineData("""{"condense":{"enabled":false}}""", false)]
    [InlineData("""{"condense":{"enabled":true}}""", true)]
    [InlineData("{}", true)]
    public void Condense_reads_a_boolean_and_is_on_without_one(string querySteps, bool expected) =>
        AssistantChatSettings.Parse(querySteps).CondenseEnabled.ShouldBe(expected);
}
