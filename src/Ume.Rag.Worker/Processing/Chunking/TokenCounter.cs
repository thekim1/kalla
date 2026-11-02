using System.Security.Cryptography;
using Microsoft.ML.Tokenizers;

namespace Ume.Rag.Worker.Processing.Chunking;

/// <summary>Token counting for the chunk budget (processing design §6.1). Counts exclude <c>&lt;s&gt;</c>/<c>&lt;/s&gt;</c> (the gateway adds 2).</summary>
public interface ITokenCounter
{
    int Count(ReadOnlySpan<char> text);
}

/// <summary>
/// <c>Microsoft.ML.Tokenizers</c> 2.0.1 <c>SentencePieceTokenizer</c> over BGE-M3's own SentencePiece model (Unigram,
/// <c>nmt_nfkc</c>), embedded with its SHA-256 checked on load; no beginning or end of sentence tokens; counts equal Hugging
/// Face's <c>XLMRobertaTokenizerFast</c> for <c>BAAI/bge-m3</c> (parity test, to-verify #26). One shared
/// instance (<see cref="Shared"/>); encoding holds no mutable state between calls (to-verify #26: thread safety is
/// exercised by the parity test's parallel pass).
/// </summary>
public sealed class Bgem3TokenCounter : ITokenCounter
{
    public const string ModelSha256 = "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865";
    private const string Resource = "Ume.Rag.Worker.Processing.Chunking.bge-m3.sentencepiece.bpe.model";

    private static readonly Lazy<Bgem3TokenCounter> Instance = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly SentencePieceTokenizer _tokenizer;

    private Bgem3TokenCounter(SentencePieceTokenizer tokenizer) => _tokenizer = tokenizer;

    public static Bgem3TokenCounter Shared => Instance.Value;

    /// <summary>
    /// The Hugging Face count (to-verify #26): the same as the SentencePiece count, except that Hugging Face's
    /// <c>Metaspace</c> keeps one trailing <c>▁</c> for trailing whitespace (and zero-width space), which
    /// <c>SentencePieceTokenizer</c> drops; that token is added here. Embedded strings are trimmed, so it rarely applies.
    /// </summary>
    public int Count(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return 0;
        }

        var count = _tokenizer.CountTokens(text, addBeginningOfSentence: false, addEndOfSentence: false, considerPreTokenization: true, considerNormalization: true);
        var last = text[^1];
        return char.IsWhiteSpace(last) || last == '\u200B' ? count + 1 : count;
    }

    private static Bgem3TokenCounter Load()
    {
        using var stream = typeof(Bgem3TokenCounter).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException("The embedded BGE-M3 SentencePiece model is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        var hash = Convert.ToHexStringLower(SHA256.HashData(copy.GetBuffer().AsSpan(0, (int)copy.Length)));
        if (!string.Equals(hash, ModelSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The embedded SentencePiece model has SHA-256 {hash}, not {ModelSha256}.");
        }

        copy.Position = 0;
        return new Bgem3TokenCounter(SentencePieceTokenizer.Create(copy, addBeginningOfSentence: false, addEndOfSentence: false));
    }
}

/// <summary>Cuts at word boundaries by token count, with any <see cref="ITokenCounter"/> (processing design §6.2 rules 2–3).</summary>
public static class TokenCuts
{
    /// <summary>
    /// How far a sum of separately counted whitespace-separated parts may fall short of the exact count of the joined
    /// string. BGE-M3's vocabulary has no piece with <c>▁</c> after its first character and the normaliser folds every
    /// whitespace run into one <c>▁</c>, so the Viterbi path breaks at each word start and the sum equals the joined count
    /// except for float rounding between near-tied paths. The packer allows it per joined unit; <see cref="SuffixStart"/>
    /// once per suffix, since per word it would exceed the tokens a word adds.
    /// </summary>
    public const int JoinSlack = 2;

    /// <summary>
    /// The longest prefix of <paramref name="text"/> ending before a whitespace that fits <paramref name="maxTokens"/>
    /// (a unit larger than the whole budget, rule 2). Falls back to a hard cut inside the first word when even that is too long.
    /// </summary>
    public static int PrefixLength(ITokenCounter counter, string text, int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentNullException.ThrowIfNull(text);
        if (counter.Count(text) <= maxTokens)
        {
            return text.Length;
        }

        var boundaries = Boundaries(text);
        int lo = 0, hi = boundaries.Count - 1, best = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (counter.Count(text.AsSpan(0, boundaries[mid])) <= maxTokens)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (best >= 0 && boundaries[best] > 0)
        {
            return boundaries[best];
        }

        // One word longer than the budget: the longest prefix of characters that fits (at least one character).
        int a = 1, b = text.Length, fit = 1;
        while (a <= b)
        {
            var mid = (a + b) / 2;
            if (counter.Count(text.AsSpan(0, mid)) <= maxTokens)
            {
                fit = mid;
                a = mid + 1;
            }
            else
            {
                b = mid - 1;
            }
        }

        if (char.IsHighSurrogate(text[fit - 1]) && fit < text.Length)
        {
            fit++;
        }

        return fit;
    }

    /// <summary>
    /// The start of the longest suffix beginning at a word that has at most <paramref name="maxTokens"/> (overlap of one long
    /// sentence, rule 3): the first word start from the left whose suffix fits. A start whose suffix has a sum of word counts
    /// over <paramref name="maxTokens"/> + <see cref="JoinSlack"/> is passed over without counting the suffix.
    /// </summary>
    public static int SuffixStart(ITokenCounter counter, string text, int maxTokens)
    {
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentNullException.ThrowIfNull(text);
        var starts = Boundaries(text).Select(b => b + 1).Where(s => s < text.Length).Prepend(0).ToList();
        var sums = new int[starts.Count + 1];
        for (var i = starts.Count - 1; i >= 0; i--)
        {
            var end = i + 1 < starts.Count ? starts[i + 1] : text.Length;
            // Without the token Bgem3TokenCounter adds for a trailing zero-width space, which falls away inside the suffix.
            sums[i] = sums[i + 1] + counter.Count(text.AsSpan(starts[i], end - starts[i]).Trim().TrimEnd('\u200B'));
        }

        for (var i = 0; i < starts.Count; i++)
        {
            if (sums[i] - JoinSlack <= maxTokens && counter.Count(text.AsSpan(starts[i])) <= maxTokens)
            {
                return starts[i];
            }
        }

        return text.Length;
    }

    /// <summary>Indexes of whitespace characters (cut points before them).</summary>
    private static List<int> Boundaries(string text)
    {
        var list = new List<int>();
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                list.Add(i);
            }
        }

        return list;
    }
}
