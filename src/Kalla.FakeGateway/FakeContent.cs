using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kalla.FakeGateway;

/// <summary>Deterministic content: embeddings by feature hashing, echo answers with citations, word-overlap rerank scores.</summary>
public static partial class FakeContent
{
    public const int DefaultDimensions = 1024;
    private const ulong FnvOffset = 14695981039346656037;
    private const ulong FnvPrime = 1099511628211;

    /// <summary>
    /// An L2-normalised vector of <paramref name="dimensions"/> from feature hashing of lower-cased word unigrams and
    /// character trigrams with FNV-1a 64: bucket = hash mod n, sign from the top bit (gateway design §6, so texts that
    /// share words are close). Deterministic across processes and platforms.
    /// </summary>
    public static float[] Vector(string text, int dimensions)
    {
        var vector = new float[dimensions];
        foreach (var word in Words(text.ToLowerInvariant()))
        {
            Add(vector, word);
            var padded = "#" + word + "#";
            for (var i = 0; i + 3 <= padded.Length; i++)
            {
                Add(vector, padded.Substring(i, 3));
            }
        }

        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        if (norm == 0)
        {
            vector[0] = 1; // empty text: still a unit vector
            return vector;
        }

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / norm);
        }

        return vector;
    }

    /// <summary>
    /// The echo answer: the question, then every source marker <c>[n]</c> found in the context (up to three), so citation
    /// tests can assert on the markers.
    /// </summary>
    public static string Answer(string question, string context)
    {
        var shortQuestion = question.Length > 200 ? question[..200] + "…" : question;
        var cited = SourceMarker().Matches(context).Select(m => m.Value).Distinct().Take(3).ToList();
        var answer = $"Testsvar på \"{shortQuestion}\".";
        return cited.Count == 0 ? answer : answer + " Se " + string.Join(" ", cited) + ".";
    }

    /// <summary>Share of the query's words found in the document (0–1).</summary>
    public static double Overlap(string query, string document)
    {
        var q = Words(query.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        if (q.Count == 0)
        {
            return 0;
        }

        var d = Words(document.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        return (double)q.Count(d.Contains) / q.Count;
    }

    public static string LastUserText(JsonNode? messages) =>
        messages is JsonArray a
            ? a.OfType<JsonObject>().LastOrDefault(m => m["role"]?.GetValue<string>() == "user") is { } m ? TextOf(m["content"]) : string.Empty
            : string.Empty;

    public static string AllText(JsonNode? messages) =>
        messages is JsonArray a ? string.Join("\n", a.OfType<JsonObject>().Select(m => TextOf(m["content"]))) : string.Empty;

    public static string TextOf(JsonNode? content) => content switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray parts => string.Join("\n", parts.OfType<JsonObject>().Select(p => p["text"]?.GetValue<string>() ?? string.Empty)),
        _ => string.Empty,
    };

    public static int Tokens(string text) => Math.Max(1, text.Length / 4);

    /// <summary>The response header that echoes the request's <c>max_tokens</c>, <c>none</c> when it was left out (B-31).</summary>
    public const string MaxTokensHeader = "x-fake-max-tokens";

    /// <summary>The <c>reasoning_effort</c> a chat call sent, <c>none</c> when left out (#1106).</summary>
    public const string ReasoningEffortHeader = "x-fake-reasoning-effort";

    /// <summary>The gateway's cost header on a complete JSON answer (gateway <c>GatewayHeaders.CostUsd</c>).</summary>
    public const string CostHeader = "x-fors-cost-usd";

    /// <summary>The request header <c>1</c> that asks for the stream cost comment (gateway <c>GatewayHeaders.StreamCost</c>).</summary>
    public const string StreamCostHeader = "x-fors-stream-cost";

    /// <summary>The text cut to at most <paramref name="tokens"/> by <see cref="Tokens"/>, at a word boundary when there is one.</summary>
    public static string Truncate(string text, int tokens)
    {
        var length = Math.Max(1, tokens) * 4;
        if (text.Length <= length)
        {
            return text;
        }

        var space = text.LastIndexOf(' ', length - 1, length);
        return text[..(space > 0 ? space : length)];
    }

    /// <summary>The answer split into stream events, one word each (copied from the gateway's FakeLlm).</summary>
    public static IEnumerable<string> StreamWords(string text)
    {
        var parts = text.Split(' ');
        for (var i = 0; i < parts.Length; i++)
        {
            yield return i == 0 ? parts[i] : " " + parts[i];
        }
    }

    private static void Add(float[] vector, string feature)
    {
        var hash = FnvOffset;
        foreach (var b in Encoding.UTF8.GetBytes(feature))
        {
            hash ^= b;
            hash *= FnvPrime;
        }

        vector[(int)(hash % (ulong)vector.Length)] += (hash >> 63) == 0 ? 1f : -1f;
    }

    private static IEnumerable<string> Words(string text)
    {
        var word = new StringBuilder();
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(c);
            }
            else if (word.Length > 0)
            {
                yield return word.ToString();
                word.Clear();
            }
        }

        if (word.Length > 0)
        {
            yield return word.ToString();
        }
    }

    [GeneratedRegex(@"\[\d{1,2}\]")]
    private static partial Regex SourceMarker();
}
