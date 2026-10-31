using System.Diagnostics;
using System.Text.Json;
using Ume.Rag.Worker.Processing.Chunking;

namespace Ume.Rag.Worker.Tests.Processing;

/// <summary>
/// To-verify #26 (processing design §6.1): <c>Microsoft.ML.Tokenizers</c> 2.0.1 over BGE-M3's SentencePiece model counts
/// exactly as Hugging Face's tokenizer for <c>BAAI/bge-m3</c> on 1,000 passages
/// (<c>tests/fixtures/processing/tokenizer/passages.jsonl</c>, generated once by its <c>generate.py</c>), also when one
/// shared instance counts from many threads.
/// </summary>
public sealed class TokenizerParityTests
{
    private static readonly Lazy<List<(string Text, int Count)>> Passages = new(() =>
        [.. File.ReadLines(Path.Combine(Fixtures.Processing, "tokenizer", "passages.jsonl"))
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Select(e => (e.GetProperty("text").GetString()!, e.GetProperty("count").GetInt32()))]);

    [Fact]
    public void Counts_equal_the_Hugging_Face_tokenizer_on_1000_passages()
    {
        var passages = Passages.Value;
        passages.Count.ShouldBe(1000);
        var mismatches = passages.Where(p => Bgem3TokenCounter.Shared.Count(p.Text) != p.Count)
            .Select(p => $"{p.Count} vs {Bgem3TokenCounter.Shared.Count(p.Text)}: {p.Text[..Math.Min(80, p.Text.Length)]}")
            .ToList();
        mismatches.ShouldBeEmpty(string.Join("\n", mismatches.Take(10)));
    }

    [Fact]
    public void One_shared_instance_counts_the_same_from_many_threads()
    {
        var passages = Passages.Value;
        var parallel = new int[passages.Count];
        Parallel.For(0, passages.Count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => parallel[i] = Bgem3TokenCounter.Shared.Count(passages[i].Text));
        parallel.ShouldBe(passages.Select(p => p.Count).ToArray());
    }

    [Fact]
    public void A_5000_character_page_is_counted_in_milliseconds()
    {
        var page = string.Join(' ', Passages.Value.Select(p => p.Text)).Substring(0, 5000);
        Bgem3TokenCounter.Shared.Count(page); // load and warm
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            Bgem3TokenCounter.Shared.Count(page);
        }

        var perPage = watch.Elapsed.TotalMilliseconds / 20;
        TestContext.Current.TestOutputHelper?.WriteLine($"to-verify #26: {perPage:F2} ms per 5,000-character page");
        perPage.ShouldBeLessThan(250);
    }

    [Fact]
    public void Cuts_land_on_word_boundaries_within_the_limit()
    {
        const string text = "Ansökan om bygglov görs via e-tjänsten och handläggningstiden är normalt tio veckor.";
        var cut = TokenCuts.PrefixLength(Bgem3TokenCounter.Shared, text, 8);
        Bgem3TokenCounter.Shared.Count(text.AsSpan(0, cut)).ShouldBeLessThanOrEqualTo(8);
        char.IsWhiteSpace(text[cut]).ShouldBeTrue();

        var start = TokenCuts.SuffixStart(Bgem3TokenCounter.Shared, text, 6);
        Bgem3TokenCounter.Shared.Count(text.AsSpan(start)).ShouldBeLessThanOrEqualTo(6);
        char.IsWhiteSpace(text[start - 1]).ShouldBeTrue();
    }
}
