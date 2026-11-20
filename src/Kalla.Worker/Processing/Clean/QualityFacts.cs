namespace Kalla.Worker.Processing.Clean;

/// <summary>
/// The quality facts of processing design §8 (<c>quality.*</c>): information for the run item and the crawl report,
/// never routing; only missing pages make a document <c>degraded</c>.
/// </summary>
public static class QualityFacts
{
    /// <summary>Under this many characters a document gets the info note <c>quality.short</c> (never <c>degraded</c>).</summary>
    public const int ShortChars = 200;

    /// <summary>The parse-output facts, computed on the cleaned document.</summary>
    public static ParsedDocument ForParsed(ParsedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var facts = new SortedDictionary<string, double>(document.Facts.ToDictionary(), StringComparer.Ordinal);
        var chars = document.Blocks.Where(b => b.Kind != BlockKinds.Table).Sum(b => b.Text.Length)
            + document.Tables.Sum(t => t.Cells.Sum(c => c.Text.Length));
        facts["quality.chars"] = chars;
        facts["quality.short"] = chars < ShortChars ? 1 : 0;
        facts["quality.missingPages"] = document.MissingPages.Count;
        if (document.Pages.Count > 0)
        {
            var nonBlank = document.Pages.Count(p => !p.Blank);
            facts["quality.charsPerPage"] = nonBlank == 0 ? 0 : Math.Round(chars / (double)nonBlank, 1);
            facts["quality.garble"] = Math.Round(document.Pages.Where(p => !p.Blank).Select(p => p.GarbledScore).DefaultIfEmpty(0).Average(), 4);
            facts["quality.emptyPageRatio"] = Math.Round(document.Pages.Count(p => p.Chars == 0 && !p.Blank) / (double)document.Pages.Count, 4);
            var ocr = document.Pages.Where(p => p.OcrP10 is not null).Select(p => p.OcrP10!.Value).ToList();
            if (ocr.Count > 0)
            {
                facts["quality.ocrConfidence"] = Math.Round(ocr.Average(), 4);
            }
        }
        else
        {
            // HTML and text: letter ratio below 0.5 counts as garbled.
            var text = string.Concat(document.Blocks.Select(b => b.Text));
            var visible = text.Count(c => !char.IsWhiteSpace(c));
            facts["quality.garble"] = visible > 0 && text.Count(char.IsLetter) / (double)visible < 0.5 ? 1 : 0;
        }

        return document with { Facts = facts };
    }

    /// <summary>
    /// The observation score (§8): <c>min(1, chars/(1500 × pages)) × (1 − garble) × coalesce(ocrConfidence, 1) × (1 − emptyPageRatio)</c>.
    /// </summary>
    public static double Score(ParsedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var f = document.Facts;
        var pages = Math.Max(1, document.Pages.Count);
        var score = Math.Min(1, f.GetValueOrDefault("quality.chars") / (1500.0 * pages))
            * (1 - f.GetValueOrDefault("quality.garble"))
            * (f.TryGetValue("quality.ocrConfidence", out var ocr) ? ocr : 1)
            * (1 - f.GetValueOrDefault("quality.emptyPageRatio"));
        return Math.Round(Math.Clamp(score, 0, 1), 4);
    }

    /// <summary>
    /// The chunk facts: <c>emptyChunkRatio</c> (&lt; 20 body tokens) and <c>duplicateChunkRatio</c> (shared <c>TextHash</c>).
    /// <paramref name="bodyTokens"/> gives the token count of chunk <c>i</c>'s <c>Text</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, double> ForChunks(IReadOnlyList<ChunkDraft> chunks, Func<int, int> bodyTokens)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(bodyTokens);
        var facts = new SortedDictionary<string, double>(StringComparer.Ordinal) { ["quality.chunks"] = chunks.Count };
        if (chunks.Count == 0)
        {
            return facts;
        }

        facts["quality.emptyChunkRatio"] = Math.Round(Enumerable.Range(0, chunks.Count).Count(i => bodyTokens(i) < 20) / (double)chunks.Count, 4);
        facts["quality.duplicateChunkRatio"] = Math.Round(
            chunks.GroupBy(c => c.TextHash, StringComparer.Ordinal).Where(g => g.Count() > 1).Sum(g => g.Count()) / (double)chunks.Count, 4);
        return facts;
    }
}
