using Ume.Rag.Retrieval.Text;

namespace Ume.Rag.Retrieval.Tests;

/// <summary>The compound cases of data-model §3.1 and simplification pass §3 against the shipped sv_SE word list.</summary>
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

    [Fact]
    public void The_word_list_ships_as_separate_files_with_its_licence_texts_not_inside_the_assembly()
    {
        // LGPL-3.0-only (#52, to-verify #113): a user can replace the list, and the notices and licence texts go with it.
        var directory = Path.Combine(AppContext.BaseDirectory, "dictionaries", "sv_SE");
        foreach (var file in new[] { "sv_SE.aff", "sv_SE.dic", "LICENSE_en_US.txt", "LICENSE_sv_SE.txt", "COPYING", "COPYING.LESSER", "README.md" })
        {
            File.Exists(Path.Combine(directory, file)).ShouldBeTrue(file);
        }

        File.ReadAllText(Path.Combine(directory, "COPYING.LESSER")).ShouldContain("GNU LESSER GENERAL PUBLIC LICENSE");
        File.ReadAllText(Path.Combine(directory, "COPYING")).ShouldContain("GNU GENERAL PUBLIC LICENSE");
        typeof(SwedishDecompounder).Assembly.GetManifestResourceNames().ShouldNotContain(n => n.Contains("sv_SE", StringComparison.Ordinal));
        SwedishDecompounder.DefaultDirectory.ShouldBe(directory);
    }

    [Fact]
    public void A_replacement_list_in_another_directory_is_read_from_there()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "dictionaries", "sv_SE");
        var replacement = Directory.CreateTempSubdirectory("sv_SE-");
        try
        {
            File.Copy(Path.Combine(shipped, "sv_SE.aff"), Path.Combine(replacement.FullName, "sv_SE.aff"));
            // A list with one stem fewer: "bygglov" is gone, so the compound no longer splits on it.
            var lines = File.ReadAllLines(Path.Combine(shipped, "sv_SE.dic"));
            var kept = lines.Skip(1).Where(l => !l.StartsWith("bygglov/", StringComparison.Ordinal) && l != "bygglov").ToList();
            kept.Count.ShouldBeLessThan(lines.Length - 1);
            File.WriteAllLines(Path.Combine(replacement.FullName, "sv_SE.dic"), [kept.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), .. kept]);

            var replaced = SwedishDecompounder.FromDirectory(replacement.FullName);

            Decompounder.Split("bygglovsansökan").ShouldContain("bygglov");
            replaced.Split("bygglovsansökan").ShouldNotContain("bygglov");
        }
        finally
        {
            replacement.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_directory_without_the_list_names_the_directory_and_the_setting()
    {
        var empty = Directory.CreateTempSubdirectory("sv_SE-empty-");
        try
        {
            var error = Should.Throw<FileNotFoundException>(() => SwedishDecompounder.FromDirectory(empty.FullName));

            error.Message.ShouldContain(empty.FullName);
            error.Message.ShouldContain(SwedishDecompounder.DirectoryVariable);
        }
        finally
        {
            empty.Delete(recursive: true);
        }
    }
}
