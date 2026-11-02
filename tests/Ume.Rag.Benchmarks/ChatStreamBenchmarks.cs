using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Ume.Rag.Answering.Chat;

namespace Ume.Rag.Benchmarks;

/// <summary>
/// One streamed answer of about 250 words (≈ 400 deltas of one token each, as the gateway streams them), per stage of
/// the chat path that runs once per delta (chat design §8–§9): reading the gateway's SSE line (<c>ChatService</c>'s
/// <c>ReadAsync</c>), the <see cref="AnswerTransducer"/> (citations, link filter), and writing our SSE chunk
/// (<c>ChatSse</c>), all through the production code. Before #333 the same work took 1.11 ms / 2.08 MB upstream and
/// 0.43 ms / 0.89 MB downstream (results/README.md).
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class ChatStreamBenchmarks
{
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

    /// <summary>As <c>ChatService.ReadAsync</c>: one idle timer re-armed per line and <see cref="GatewayStreamLine"/> per line.</summary>
    [Benchmark(Description = "Upstream: read 400 SSE lines")]
    [BenchmarkCategory("Upstream")]
    public int Upstream()
    {
        var chars = 0;
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(_request.Token);
        var events = new List<GatewayStreamEvent>(3);
        foreach (var line in _upstreamLines)
        {
            idle.CancelAfter(TimeSpan.FromSeconds(60));
            GatewayStreamLine.Read(line, events);
            foreach (var e in events)
            {
                chars += e is GatewayStreamEvent.Delta d ? d.Text.Length : 0;
            }

            events.Clear();
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

    /// <summary>As <c>ChatSse</c>: one <see cref="SseChunk"/> per response, one content event per delta.</summary>
    [Benchmark(Description = "Downstream: write 400 SSE chunks")]
    [BenchmarkCategory("Downstream")]
    public long Downstream()
    {
        long bytes = 0;
        using var chunk = new SseChunk();
        foreach (var delta in _deltas)
        {
            bytes += chunk.Content("chatcmpl-0192f0c8a1b27c3e9d4f5a6b7c8d9e0f", 1_762_000_000L, "bygglovsassistenten", delta).Length;
        }

        return bytes;
    }
}
