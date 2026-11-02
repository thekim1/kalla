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

    [Theory]
    [InlineData("Ansökan om bygglov görs via e-tjänsten och handläggningstiden är normalt tio veckor från komplett ansökan.", 6)]
    [InlineData("Ansökan\u200B om\u200B bygglov\u200B görs\u200B via\u200B e-tjänsten\u200B och\u200B handläggningstiden\u200B är\u200B tio veckor.", 9)]
    [InlineData("Ansökan  om\n\nbygglov \u00A0görs via e-tjänsten och handläggningstiden är normalt tio veckor. ", 7)]
    [InlineData("Ordningsföreskrifterna gäller.", 1)]
    public void The_overlap_start_is_the_first_word_start_whose_suffix_fits(string text, int maxTokens)
    {
        var counter = Bgem3TokenCounter.Shared;
        var expected = Enumerable.Range(0, text.Length)
            .Where(i => i == 0 || char.IsWhiteSpace(text[i - 1]))
            .FirstOrDefault(i => counter.Count(text.AsSpan(i)) <= maxTokens, text.Length);

        TokenCuts.SuffixStart(counter, text, maxTokens).ShouldBe(expected);
    }
}
