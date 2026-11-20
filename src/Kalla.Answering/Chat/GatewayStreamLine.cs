using System.Text.Json;
using Kalla.Infrastructure.Gateway;

namespace Kalla.Answering.Chat;

/// <summary>
/// One line of the gateway's chat completion stream (chat design §8.3): the events it carries, read with a pooled
/// <see cref="JsonDocument"/> rather than a <c>JsonNode</c> tree per line (#333 finding 4; ≈ 400 lines per answer).
/// </summary>
public static class GatewayStreamLine
{
    /// <summary>
    /// Adds the events of <paramref name="line"/> to <paramref name="events"/>: a content delta, the finish reason and
    /// usage, in that order. Returns false at <c>data: [DONE]</c>; lines that are not <c>data:</c> lines carry nothing.
    /// Throws <see cref="ChatException"/> (502 <c>stream_interrupted</c>) for an error event or a line that is not JSON.
    /// </summary>
    public static bool Read(string line, ICollection<GatewayStreamEvent> events)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(events);
        if (line == "data: [DONE]")
        {
            return false;
        }

        if (!line.StartsWith("data:", StringComparison.Ordinal))
        {
            return true;
        }

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(line.AsMemory(5).Trim());
        }
        catch (JsonException)
        {
            throw new ChatException(502, "stream_interrupted", "The model's stream could not be read.");
        }

        using (json)
        {
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            {
                throw new ChatException(502, "stream_interrupted", "The model stopped answering.");
            }

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                choices[0] is { ValueKind: JsonValueKind.Object } choice)
            {
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object &&
                    delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } text)
                {
                    events.Add(new GatewayStreamEvent.Delta(text));
                }

                if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                {
                    events.Add(new GatewayStreamEvent.Finish(finish.GetString()!));
                }
            }

            if (root.TryGetProperty("usage", out var usage) && GatewayUsageReader.UsageTokens(usage) is { } tokens)
            {
                events.Add(new GatewayStreamEvent.Usage(tokens));
            }
        }

        return true;
    }
}
