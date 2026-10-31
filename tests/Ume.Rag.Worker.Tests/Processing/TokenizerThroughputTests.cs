using System.Diagnostics;
using System.Text.Json;
using Ume.Rag.Worker.Processing.Chunking;

namespace Ume.Rag.Worker.Tests.Processing;

/// <summary>
/// To-verify #26, speed (processing design §6.1, §21): tokens per second of the shared BGE-M3 counter over the parity
/// corpus (<c>tests/fixtures/processing/tokenizer/passages.jsonl</c>), serially and on two threads, reported in the test
/// output. The floor is about a third of the figure measured on two development cores: it catches a several-fold
/// slowdown, not a slow machine.
/// </summary>
public sealed class TokenizerThroughputTests
{
    private const int MinimumTokensPerSecond = 250_000;

    [Fact]
    public void Counts_the_parity_corpus_at_more_than_250000_tokens_per_second()
    {
        var passages = File.ReadLines(Path.Combine(Fixtures.Processing, "tokenizer", "passages.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("text").GetString()!)
            .ToList();
        var counter = Bgem3TokenCounter.Shared;
        var tokens = passages.Sum(p => counter.Count(p)); // load and warm

        var serial = Stopwatch.StartNew();
        for (var round = 0; round < 3; round++)
        {
            foreach (var passage in passages)
            {
                counter.Count(passage);
            }
        }

        serial.Stop();
        var parallel = Stopwatch.StartNew();
        for (var round = 0; round < 3; round++)
        {
            Parallel.ForEach(passages, new ParallelOptions { MaxDegreeOfParallelism = 2 }, passage => counter.Count(passage));
        }

        parallel.Stop();
        var serialRate = 3 * tokens / serial.Elapsed.TotalSeconds;
        var parallelRate = 3 * tokens / parallel.Elapsed.TotalSeconds;
        var characters = passages.Sum(p => p.Length);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"to-verify #26: {passages.Count} passages, {tokens} tokens, {characters} characters; serial {serialRate:F0} tokens/s " +
            $"({3 * characters / serial.Elapsed.TotalSeconds:F0} characters/s), two threads {parallelRate:F0} tokens/s");
        serialRate.ShouldBeGreaterThan(MinimumTokensPerSecond);
    }
}
