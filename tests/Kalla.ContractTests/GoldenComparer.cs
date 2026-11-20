using System.Text.Json.Nodes;

namespace Kalla.ContractTests;

/// <summary>
/// Compares a <c>result.json</c> with its golden (parser design §15.2): timings, versions and <c>build</c> are ignored;
/// status, input, block kinds, levels, layers, page numbers, table shapes, warnings and OCR use exactly; text by
/// similarity of at least 0.98 per block, because a Docling or Tesseract upgrade shifts whitespace. Goldens change only
/// through src/parser/scripts/update_goldens.py.
/// </summary>
public static class GoldenComparer
{
    public const double MinSimilarity = 0.98;

    public static IReadOnlyList<string> Compare(JsonNode golden, JsonNode actual)
    {
        var problems = new List<string>();

        void Exact(string what, JsonNode? expected, JsonNode? got)
        {
            if (!JsonNode.DeepEquals(expected, got))
            {
                problems.Add($"{what}: expected {expected?.ToJsonString() ?? "null"}, got {got?.ToJsonString() ?? "null"}");
            }
        }

        void Similar(string what, string? expected, string? got)
        {
            var similarity = Similarity(expected ?? "", got ?? "");
            if (similarity < MinSimilarity)
            {
                problems.Add($"{what}: text similarity {similarity:F3} below {MinSimilarity}");
            }
        }

        foreach (var key in new[] { "contract", "request_id", "status", "input" })
        {
            Exact(key, golden[key], actual[key]);
        }

        var goldenPages = golden["pages"]!.AsArray();
        var pages = actual["pages"]!.AsArray();
        Exact("page count", goldenPages.Count, pages.Count);
        foreach (var (expected, got) in goldenPages.Zip(pages))
        {
            var page = $"page {expected!["page"]}";
            foreach (var key in new[] { "page", "label", "width", "height" })
            {
                Exact($"{page} {key}", expected[key], got![key]);
            }

            foreach (var key in new[] { "ocr_applied", "empty", "table_count", "text_layer_chars", "image_coverage" })
            {
                Exact($"{page} signals.{key}", expected["signals"]![key], got!["signals"]![key]);
            }
        }

        var goldenBlocks = golden["blocks"]!.AsArray();
        var blocks = actual["blocks"]!.AsArray();
        Exact("block count", goldenBlocks.Count, blocks.Count);
        foreach (var (expected, got) in goldenBlocks.Zip(blocks))
        {
            var block = $"block {expected!["id"]}";
            foreach (var key in new[] { "id", "kind", "level", "level_source", "page", "page_end", "layer", "table" })
            {
                Exact($"{block} {key}", expected[key], got![key]);
            }

            Similar($"{block} md", expected["md"]!.GetValue<string>(), got!["md"]!.GetValue<string>());
        }

        var goldenTables = golden["tables"]!.AsArray();
        var tables = actual["tables"]!.AsArray();
        Exact("table count", goldenTables.Count, tables.Count);
        foreach (var (expected, got) in goldenTables.Zip(tables))
        {
            foreach (var key in new[] { "id", "rows", "cols", "header_rows" })
            {
                Exact($"table {expected!["id"]} {key}", expected[key], got![key]);
            }
        }

        Exact("picture count", golden["pictures"]!.AsArray().Count, actual["pictures"]!.AsArray().Count);
        Exact("warnings", golden["warnings"], actual["warnings"]);
        Similar("markdown", golden["markdown"]?.GetValue<string>(), actual["markdown"]?.GetValue<string>());
        return problems;
    }

    /// <summary>1 − Levenshtein distance ÷ the longer length; 1 for two empty strings.</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
        {
            return 1;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return 1.0 - ((double)previous[b.Length] / Math.Max(a.Length, b.Length));
    }
}
