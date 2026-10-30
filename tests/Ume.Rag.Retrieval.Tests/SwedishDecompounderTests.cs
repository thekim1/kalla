using Ume.Rag.Retrieval.Text;

namespace Ume.Rag.Retrieval.Tests;

/// <summary>The compound cases of data-model §3.1 and simplification pass §3 against the embedded sv_SE word list.</summary>
public sealed class SwedishDecompounderTests
{
    private static SwedishDecompounder Decompounder => SwedishDecompounder.Default;

    [Theory]
    [InlineData("bygglovsansökan", new[] { "bygglov", "ansökan" })]      // linking s
    [InlineData("förskoleplats", new[] { "förskole", "plats" })]         // vowel change: förskola → förskole-
    [InlineData("avfallstaxa", new[] { "avfall", "taxa" })]
    [InlineData("parkeringstillstånd", new[] { "parkering", "tillstånd" })]
    [InlineData("äldreomsorgen", new[] { "äldre", "omsorg" })]           // inflected end part
    [InlineData("Bygglovsansökningar", new[] { "bygglov" })]             // case-insensitive; irregular plural end
    public void Splits_compounds_into_their_dictionary_parts(string word, string[] expected)
    {
        var parts = Decompounder.Split(word);

        parts.Count.ShouldBeGreaterThanOrEqualTo(2, string.Join(" + ", parts));
        foreach (var part in expected)
        {
            parts.ShouldContain(part);
        }
    }

    [Fact]
    public void Prefers_the_fewest_and_longest_parts()
    {
        // "bygg" + "lov" + "s" + "ansökan" would also be valid; two parts win.
        Decompounder.Split("bygglovsansökan").ShouldBe(["bygglov", "ansökan"]);
    }

    [Theory]
    [InlineData("kommun")]
    [InlineData("parkering")]
    [InlineData("ansökan")]
    [InlineData("och")]
    [InlineData("")]
    [InlineData("xyzxyzxyzxyz")]
    public void Leaves_simple_words_and_non_words_alone(string word) => Decompounder.Split(word).ShouldBeEmpty();

    [Fact]
    public void Parts_of_a_text_are_distinct_and_appended_after_it()
    {
        const string text = "Skicka bygglovsansökan och ansökan om parkeringstillstånd. Bygglovsansökan granskas.";

        var parts = Decompounder.PartsOf(text);
        parts.ShouldBe(parts.Distinct());
        parts.ShouldContain("bygglov");
        parts.ShouldContain("tillstånd");

        var searchText = Decompounder.WithParts(text);
        searchText.ShouldStartWith(text);
        searchText.ShouldEndWith(string.Join(' ', parts));
        Decompounder.WithParts("kommun").ShouldBe("kommun");
    }

    [Fact]
    public void Very_long_tokens_are_not_searched()
    {
        Decompounder.Split(new string('a', SwedishDecompounder.MaxWordLength + 1)).ShouldBeEmpty();
    }

    [Fact]
    public void Splits_most_compounds_of_the_municipal_sample_and_no_simple_word()
    {
        // to-verify #128: 85 of 92 compounds split (Postgres ispell with the same list: 44), no simple word split.
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Data", "municipal-words.txt"))
            .Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        var simple = lines.Where(l => l.StartsWith("= ", StringComparison.Ordinal)).Select(l => l[2..]).ToList();
        var compounds = lines.Where(l => !l.StartsWith("= ", StringComparison.Ordinal)).ToList();

        simple.Where(w => Decompounder.Split(w).Count > 0).ShouldBeEmpty();
        compounds.Count(w => Decompounder.Split(w).Count >= 2).ShouldBeGreaterThanOrEqualTo(85);
    }
}
