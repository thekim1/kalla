using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Ume.Rag.Answering.Chat;

namespace Ume.Rag.Benchmarks;

/// <summary>
/// One streamed answer of about 250 words (≈ 400 deltas of one token each, as the gateway streams them), per stage of
/// the chat path that runs once per delta (chat design §8–§9): reading the gateway's SSE line (<c>ChatService</c>'s
/// <c>ReadAsync</c>), the <see cref="AnswerTransducer"/> (citations, link filter), and writing our SSE chunk
/// (<c>ChatStreaming.WriteAsync</c>). Each <c>Current</c> benchmark copies the production code path; the matching
/// <c>Candidate</c> one is a cheaper way to do the same work, there to size the possible gain.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ChatStreamBenchmarks
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private readonly CancellationTokenSource _request = new();
    private string[] _deltas = null!;
    private string[] _upstreamLines = null!;
    private IReadOnlyList<PromptSource> _sources = null!;

    [GlobalSetup]
    public void Setup()
    {
        _sources = PromptData.Sources(5);
        _deltas = PromptData.AnswerDeltas(250);
        _upstreamLines = [.. _deltas.Select(d =>
            "data: " + JsonSerializer.Serialize(new
            {
                id = "chatcmpl-1", @object = "chat.completion.chunk", created = 1_762_000_000, model = "ume/chat-eu",
                choices = new[] { new { index = 0, delta = new { content = d }, finish_reason = (string?)null } },
            }))];
    }

    [GlobalCleanup]
    public void Cleanup() => _request.Dispose();

    /// <summary>As <c>ChatService.ReadAsync</c>: a linked token source with an idle timer per line, then a JSON DOM per line.</summary>
    [Benchmark(Baseline = true, Description = "Upstream: read 400 SSE lines (current: CTS per line + JsonNode)")]
    [BenchmarkCategory("Upstream")]
    public int UpstreamCurrent()
    {
        var chars = 0;
        foreach (var line in _upstreamLines)
        {
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(_request.Token))
            {
                idle.CancelAfter(TimeSpan.FromSeconds(60));
            }

            var chunk = JsonNode.Parse(line.AsSpan(5).Trim().ToString());
            var choice = chunk?["choices"] is JsonArray { Count: > 0 } choices ? choices[0] : null;
            if (choice?["delta"]?["content"] is JsonValue content && content.GetValueKind() == JsonValueKind.String && content.GetValue<string>() is { Length: > 0 } text)
            {
                chars += text.Length;
            }
        }

        return chars;
    }

    /// <summary>One idle timer for the stream, re-armed per line, and a forward-only reader for the one field needed.</summary>
    [Benchmark(Description = "Upstream: read 400 SSE lines (candidate: one CTS re-armed + Utf8JsonReader)")]
    [BenchmarkCategory("Upstream")]
    public int UpstreamCandidate()
    {
        var chars = 0;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(_request.Token);
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            foreach (var line in _upstreamLines)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(60));
                var length = Encoding.UTF8.GetBytes(line.AsSpan(6), buffer);
                var reader = new Utf8JsonReader(buffer.AsSpan(0, length));
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("content"u8) && reader.Read() &&
                        reader.TokenType == JsonTokenType.String)
                    {
                        chars += reader.GetString()!.Length;
                        break;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return chars;
    }

    [Benchmark(Description = "Answer transducer: feed 400 deltas + flush")]
    [BenchmarkCategory("Transducer")]
    public int Transducer()
    {
        var transducer = new AnswerTransducer(_sources, ["www.umea.se"]);
        var pieces = 0;
        foreach (var delta in _deltas)
        {
            pieces += transducer.Feed(delta).Count;
        }

        return pieces + transducer.Flush().Count;
    }

    /// <summary>As <c>ChatStreaming.WriteAsync</c>: an anonymous object serialised to a string, then <c>"data: " + json + "\n\n"</c>.</summary>
    [Benchmark(Baseline = true, Description = "Downstream: 400 SSE chunks (current: anonymous object → string)")]
    [BenchmarkCategory("Downstream")]
    public long DownstreamCurrent()
    {
        long bytes = 0;
        var id = "chatcmpl-0192f0c8a1b27c3e9d4f5a6b7c8d9e0f";
        object Chunk(object delta, string? finish) => new
        {
            id, @object = "chat.completion.chunk", created = 1_762_000_000L, model = "bygglovsassistenten",
            choices = new[] { new { index = 0, delta, finish_reason = finish } },
        };
        foreach (var delta in _deltas)
        {
            var text = "data: " + JsonSerializer.Serialize(Chunk(new { content = delta }, null), Json) + "\n\n";
            bytes += Encoding.UTF8.GetByteCount(text);
        }

        return bytes;
    }

    /// <summary>The same bytes written with one reused <see cref="Utf8JsonWriter"/> into a pooled buffer.</summary>
    [Benchmark(Description = "Downstream: 400 SSE chunks (candidate: Utf8JsonWriter, reused buffer)")]
    [BenchmarkCategory("Downstream")]
    public long DownstreamCandidate()
    {
        long bytes = 0;
        var buffer = new ArrayBufferWriter<byte>(512);
        using var writer = new Utf8JsonWriter(buffer);
        foreach (var delta in _deltas)
        {
            buffer.ResetWrittenCount();
            writer.Reset(buffer);
            writer.WriteStartObject();
            writer.WriteString("id"u8, "chatcmpl-0192f0c8a1b27c3e9d4f5a6b7c8d9e0f");
            writer.WriteString("object"u8, "chat.completion.chunk"u8);
            writer.WriteNumber("created"u8, 1_762_000_000L);
            writer.WriteString("model"u8, "bygglovsassistenten");
            writer.WriteStartArray("choices"u8);
            writer.WriteStartObject();
            writer.WriteNumber("index"u8, 0);
            writer.WriteStartObject("delta"u8);
            writer.WriteString("content"u8, delta);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
            bytes += 6 + buffer.WrittenCount + 2;
        }

        return bytes;
    }
}
