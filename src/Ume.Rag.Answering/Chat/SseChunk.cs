using System.Buffers;
using System.Text.Json;

namespace Ume.Rag.Answering.Chat;

/// <summary>
/// The per-token server-sent event of a streamed answer (chat design §9.1): <c>data: {chat.completion.chunk with one
/// content delta}\n\n</c>, written straight to UTF-8 with a reusable <see cref="Utf8JsonWriter"/> instead of serialising an
/// anonymous object to a string per token (#333 finding 4). The bytes equal what <see cref="JsonSerializer"/> writes for
/// <c>{ id, object, created, model, choices = [{ index = 0, delta = { content } }] }</c> with nulls omitted
/// (<c>SseChunkTests</c>). Other events (role, sources, annotations, finish, usage, errors) stay on the serializer.
/// </summary>
public sealed class SseChunk : IDisposable
{
    private static readonly byte[] Prefix = "data: "u8.ToArray();
    private static readonly byte[] Suffix = "\n\n"u8.ToArray();
    private readonly ArrayBufferWriter<byte> _buffer = new(512);
    private readonly Utf8JsonWriter _writer;

    public SseChunk() => _writer = new Utf8JsonWriter(_buffer);

    /// <summary>The event for one content delta; valid until the next call.</summary>
    public ReadOnlyMemory<byte> Content(string id, long created, string? model, string content)
    {
        _buffer.ResetWrittenCount();
        _buffer.Write(Prefix);
        _writer.Reset(_buffer);
        _writer.WriteStartObject();
        _writer.WriteString("id"u8, id);
        _writer.WriteString("object"u8, "chat.completion.chunk"u8);
        _writer.WriteNumber("created"u8, created);
        if (model is not null)
        {
            _writer.WriteString("model"u8, model);
        }

        _writer.WriteStartArray("choices"u8);
        _writer.WriteStartObject();
        _writer.WriteNumber("index"u8, 0);
        _writer.WriteStartObject("delta"u8);
        _writer.WriteString("content"u8, content);
        _writer.WriteEndObject();
        _writer.WriteEndObject();
        _writer.WriteEndArray();
        _writer.WriteEndObject();
        _writer.Flush();
        _buffer.Write(Suffix);
        return _buffer.WrittenMemory;
    }

    public void Dispose() => _writer.Dispose();
}
