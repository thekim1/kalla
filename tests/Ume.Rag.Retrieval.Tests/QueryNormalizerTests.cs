using Ume.Rag.Retrieval.Text;

namespace Ume.Rag.Retrieval.Tests;

/// <summary>Knowledge design §16.1 <c>QueryNormalizer</c> (§5.1) and the keyword statement's query search text.</summary>
public sealed class QueryNormalizerTests
{
    [Theory]
    [InlineData("  Hur  söker\tjag\n bygglov? ", "Hur söker jag bygglov?")]
    [InlineData("bygglov\u0000altan\u0085fönster\u007f", "bygglov altan fönster")]         // C0, C1 (NEL), DEL → space
    [InlineData("A\u00A0\u2003B", "A B")]                                                   // no-break and em spaces
    [InlineData("Umea\u030A", "Ume\u00E5")]                                                // NFC: a + combining ring = å
    [InlineData("Bygglov", "Bygglov")]                                                       // no lower-casing (cased tokenizer)
    [InlineData("\uD83D\uDE00 ok", "\uD83D\uDE00 ok")]                                     // a surrogate pair stays
    public void Normalises_controls_nfc_and_whitespace(string query, string expected) =>
        QueryNormalizer.Normalize(query).ShouldBe(expected);

    [Fact]
    public void Replaces_a_lone_surrogate_instead_of_throwing_on_it() =>
        QueryNormalizer.Normalize("x" + (char)0xD800 + "y").ShouldBe("x\uFFFDy");

    [Fact]
    public void Refuses_empty_and_over_long_queries_after_normalisation()
    {
        Should.Throw<ArgumentException>(() => QueryNormalizer.NormalizeAndValidate(" \t\u0001 "));
        QueryNormalizer.NormalizeAndValidate(new string('a', QueryNormalizer.MaxLength)).Length.ShouldBe(QueryNormalizer.MaxLength);
        Should.Throw<ArgumentException>(() => QueryNormalizer.NormalizeAndValidate(new string('a', QueryNormalizer.MaxLength + 1)));
        // Length counts characters, not UTF-16 units: 2,000 emoji are accepted.
        QueryNormalizer.NormalizeAndValidate(string.Concat(Enumerable.Repeat("\uD83D\uDE00", QueryNormalizer.MaxLength)));
        // Whitespace that collapses does not count.
        QueryNormalizer.NormalizeAndValidate(new string('a', 1000) + new string(' ', 5000) + new string('b', 999));
    }

    [Fact]
    public void The_query_search_text_appends_the_decompounder_parts_as_the_worker_does()
    {
        const string query = "bygglovsansökan för altan";
        var searchText = QueryNormalizer.SearchText(query, SwedishDecompounder.Default);

        searchText.ShouldBe("bygglovsansökan för altan\nbygglov ansökan");
        searchText.ShouldBe(SwedishDecompounder.Default.WithParts(query)); // the worker's SearchText rule
        QueryNormalizer.SearchText(query, null).ShouldBe(query);            // hybrid-no-decompounder variant
    }
}
