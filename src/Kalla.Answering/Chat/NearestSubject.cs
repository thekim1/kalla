using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kalla.Answering.Chat;

/// <summary>
/// The nearest subject of a turn that found no answer (chat design §9.11; Decisions "Progress, timings and the nearest subject in
/// the chat extension"): the slug of the first result's collection and that chunk's topic tags (<c>[]</c> for none). The subject
/// only: no title, address, snippet or document id.
/// </summary>
public sealed record NearestSubject(string Collection, IReadOnlyList<string> Topics);

/// <summary>
/// The <c>kalla.nearest</c> member's value (§3.8, §9.11): the subject, or JSON <c>null</c> for an <c>answered</c>, <c>unavailable</c> or
/// conversational turn and when the search found nothing. A wrapper, as <see cref="EscalationMember"/>, so the member is written as
/// <c>null</c> by serializers that leave out null members (the stream's); the names are fixed whatever the naming policy.
/// </summary>
[JsonConverter(typeof(NearestMemberConverter))]
public sealed record NearestMember(NearestSubject? Value);

public sealed class NearestMemberConverter : JsonConverter<NearestMember>
{
    public override NearestMember Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("The nearest member is written only.");

    public override void Write(Utf8JsonWriter writer, NearestMember value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value?.Value is not { } subject)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("collection", subject.Collection);
        writer.WriteStartArray("topics");
        foreach (var topic in subject.Topics)
        {
            writer.WriteStringValue(topic);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
