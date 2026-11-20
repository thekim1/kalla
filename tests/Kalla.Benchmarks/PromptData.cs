using System.Security.Cryptography;
using System.Text;
using Kalla.Answering.Chat;
using Kalla.Domain.Retrieval;

namespace Kalla.Benchmarks;

/// <summary>Retrieved chunks, prompt sources and a streamed answer shaped like a pilot chat turn.</summary>
public static class PromptData
{
    /// <summary><paramref name="count"/> retrieved chunks of about 400 tokens (1,200 characters), two per document.</summary>
    public static List<RetrievedChunk> Chunks(int count)
    {
        var collection = Guid.NewGuid();
        var documents = Enumerable.Range(0, (count / 2) + 1).Select(i => Citation(i)).ToArray();
        return
        [
            .. Enumerable.Range(0, count).Select(i =>
            {
                var text = BenchData.SwedishText(180, seed: i);
                var citation = documents[i / 2];
                return new RetrievedChunk(i + 1, Guid.NewGuid(), citation.Id, SHA256.HashData(Encoding.UTF8.GetBytes(text)), text, "Bygga › Bygglov › Ansökan",
                    null, null, 1.0 / (i + 1), new ChunkScores(0.03, 0.25, i + 1, 0.4, i + 1, null), null, collection, "bygglov", "public", citation);
            }),
        ];
    }

    public static IReadOnlyList<PromptSource> Sources(int documents) =>
        [.. Chunks(documents * 2).GroupBy(c => c.DocumentId).Select((g, i) => new PromptSource(i + 1, g.First().Citation, [.. g]))];

    /// <summary>
    /// An answer of about <paramref name="words"/> words split into one-token deltas (2–5 characters) as models stream: with
    /// citation markers, one allowed link, one link to another host and a bare URL, so the transducer's paths all run.
    /// </summary>
    public static string[] AnswerDeltas(int words)
    {
        var text = new StringBuilder();
        var body = BenchData.SwedishText(words, seed: 11).Split(". ");
        for (var i = 0; i < body.Length; i++)
        {
            text.Append(body[i]).Append(" [").Append((i % 5) + 1).Append("]. ");
            if (i == 3)
            {
                text.Append("Läs mer på [Bygglov](https://www.umea.se/bygga/bygglov) eller [annan sida](https://example.com/x). ");
            }

            if (i == 6)
            {
                text.Append("Se även https://example.org/blankett.pdf för blanketten. ");
            }
        }

        var all = text.ToString();
        var deltas = new List<string>();
        var random = new Random(17);
        for (var i = 0; i < all.Length;)
        {
            var length = Math.Min(random.Next(2, 6), all.Length - i);
            deltas.Add(all.Substring(i, length));
            i += length;
        }

        return [.. deltas];
    }

    private static Citation Citation(int i) => new(
        Guid.NewGuid(), $"Bygglov för tillbyggnad {i}", null, $"https://www.umea.se/bygga/bygglov/sida-{i}", null, "web", "page", "text/html", "sv",
        DateTimeOffset.UtcNow.AddDays(-i));
}
