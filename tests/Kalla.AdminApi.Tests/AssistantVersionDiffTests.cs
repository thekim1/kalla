using Kalla.Domain.Entities;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// Admin §11.1, assistant versions: the structured diff (B-56): settings by name, links, a line diff of the instructions with
/// Swedish characters and empty texts, and the size limit; the Myers diff is checked against a plain LCS on random input.
/// </summary>
public sealed class AssistantVersionDiffTests
{
    private static readonly Guid Bygglov = Guid.Parse("01929b6e-0000-7000-8000-000000000001");
    private static readonly Guid Detaljplaner = Guid.Parse("01929b6e-0000-7000-8000-000000000002");
    private static readonly Guid Avfall = Guid.Parse("01929b6e-0000-7000-8000-000000000003");
    private static readonly Dictionary<Guid, string> Names = new() { [Bygglov] = "Bygglov", [Detaljplaner] = "Detaljplaner", [Avfall] = "Avfall" };

    private static AssistantVersion Version(int number, string prompt = "", string? model = "fors/chat-eu", string querySteps = "{}", string notFound = "{}",
        params CollectionLink[] links) =>
        new() { Number = number, SystemPrompt = prompt, GatewayModel = model, QuerySteps = querySteps, NotFound = notFound, CollectionLinks = links };

    [Fact]
    public void Settings_are_named_by_their_path_and_unchanged_ones_are_left_out()
    {
        var from = Version(1, querySteps: """{"retrieval":{"topK":8,"rerank":true},"answer":{"generalKnowledge":"off"}}""",
            notFound: """{"sv":"Hittar inget.","links":[{"title":"Kontakt","url":"https://www.umea.se/kontakt"}]}""");
        var to = Version(2, model: "fors/chat-large", querySteps: """{"retrieval":{"topK":10,"rerank":true},"history":{"turns":4}}""",
            notFound: """{"sv":"Hittar inget.","links":[]}""");

        var diff = AssistantVersionDiff.Of(from, to, toDraft: false, Names);

        diff.Settings.Select(s => (s.Name, s.From?.ToJsonString(), s.To?.ToJsonString())).ShouldBe([
            ("model", "\"fors/chat-eu\"", "\"fors/chat-large\""),
            ("querySteps.answer.generalKnowledge", "\"off\"", null),
            ("querySteps.history.turns", null, "4"),
            ("querySteps.retrieval.topK", "8", "10"),
            ("notFound.links", """[{"title":"Kontakt","url":"https://www.umea.se/kontakt"}]""", "[]"),
        ]);
        (diff.From, diff.To, diff.ToDraft, diff.NotFoundSv.Changed, diff.Changed).ShouldBe((1, 2, false, false, true));
    }

    [Fact]
    public void Collections_added_and_removed_carry_their_names_and_a_link_kept_with_other_topics_is_a_topic_change()
    {
        var from = Version(1, links: [new CollectionLink(Bygglov, ["bygglov"]), new CollectionLink(Detaljplaner, [])]);
        var to = Version(2, links: [new CollectionLink(Bygglov, ["bygglov", "altan"]), new CollectionLink(Avfall, ["sortering"])]);

        var diff = AssistantVersionDiff.Of(from, to, toDraft: true, Names);

        diff.CollectionsAdded.Select(l => (l.Name, string.Join(',', l.Tags))).ShouldBe([("Avfall", "sortering")]);
        diff.CollectionsRemoved.Select(l => l.Name).ShouldBe(["Detaljplaner"]);
        diff.TopicsChanged.Single().ShouldSatisfyAllConditions(
            t => t.Name.ShouldBe("Bygglov"), t => t.From.ShouldBe(["bygglov"]), t => t.To.ShouldBe(["bygglov", "altan"]));
        diff.Settings.ShouldBeEmpty();
        AssistantVersionDiff.Of(from, from, toDraft: false, Names).Changed.ShouldBeFalse();
    }

    [Fact]
    public void The_instructions_get_a_line_diff_with_Swedish_characters_and_CRLF_lines_compared_as_LF()
    {
        var from = "Du svarar på frågor om bygglov.\r\nSvara kort.\r\nHänvisa till kontaktcenter.";
        var to = "Du svarar på frågor om bygglov.\nSvara kort och vänligt.\nHänvisa till kontaktcenter.\nNämn aldrig personnummer.";

        var diff = AssistantVersionDiff.Text(from, to);

        diff.Changed.ShouldBeTrue();
        diff.Lines.Select(l => (l.Op, l.Text)).ShouldBe([
            (DiffLine.Equal, "Du svarar på frågor om bygglov."),
            (DiffLine.Delete, "Svara kort."),
            (DiffLine.Insert, "Svara kort och vänligt."),
            (DiffLine.Equal, "Hänvisa till kontaktcenter."),
            (DiffLine.Insert, "Nämn aldrig personnummer."),
        ]);
        AssistantVersionDiff.Text("Rad ett.\r\nRad två.", "Rad ett.\nRad två.").Changed.ShouldBeFalse("only the line ends differ");
    }

    [Fact]
    public void Empty_texts_have_no_lines_and_a_text_from_or_to_nothing_is_all_inserted_or_deleted()
    {
        AssistantVersionDiff.Text("", "").ShouldSatisfyAllConditions(d => d.Changed.ShouldBeFalse(), d => d.Lines.ShouldBeEmpty());
        AssistantVersionDiff.Text(null, "Åtta öar.\nÄlv.").Lines.Select(l => l.Op).ShouldBe([DiffLine.Insert, DiffLine.Insert]);
        AssistantVersionDiff.Text("Åtta öar.", "").Lines.Select(l => (l.Op, l.Text)).ShouldBe([(DiffLine.Delete, "Åtta öar.")]);

        var notFound = AssistantVersionDiff.Of(Version(1, notFound: """{"en":"Nothing."}"""), Version(2, notFound: """{"sv":"Inget hittades.","en":"Nothing."}"""),
            toDraft: false, Names);
        notFound.NotFoundSv.Lines.Select(l => (l.Op, l.Text)).ShouldBe([(DiffLine.Insert, "Inget hittades.")]);
        notFound.NotFoundEn.Changed.ShouldBeFalse();
        notFound.Settings.ShouldBeEmpty("the texts are diffed as text, not listed as settings");
    }

    [Fact]
    public void A_text_over_the_limit_says_whether_it_changed_without_lines()
    {
        var limit = new string('å', AssistantVersionDiff.MaxTextLength);
        AssistantVersionDiff.Text(limit, limit[..^1] + "ä").ShouldSatisfyAllConditions(d => d.TooLong.ShouldBeFalse(), d => d.Changed.ShouldBeTrue(), d => d.Lines.Count.ShouldBe(2));
        var over = limit + "ä";
        AssistantVersionDiff.Text("kort", over).ShouldSatisfyAllConditions(d => d.TooLong.ShouldBeTrue(), d => d.Changed.ShouldBeTrue(), d => d.Lines.ShouldBeEmpty());
        AssistantVersionDiff.Text(over, over).ShouldSatisfyAllConditions(d => d.TooLong.ShouldBeTrue(), d => d.Changed.ShouldBeFalse());
    }

    [Fact]
    public void Texts_with_nothing_in_common_beyond_the_edit_bound_are_replaced_whole()
    {
        var a = Enumerable.Range(0, 1_500).Select(i => $"a{i}").ToArray();
        var b = Enumerable.Range(0, 1_500).Select(i => $"b{i}").ToArray();
        var lines = AssistantVersionDiff.Lines(a, b);
        lines.Count(l => l.Op == DiffLine.Delete).ShouldBe(1_500);
        lines.Count(l => l.Op == DiffLine.Insert).ShouldBe(1_500);
    }

    [Fact]
    public void The_line_diff_rebuilds_both_texts_with_as_few_edits_as_a_longest_common_subsequence_allows()
    {
        var random = new Random(4711);
        string[] alphabet = ["å", "ä", "ö", "a", "b"];
        for (var round = 0; round < 500; round++)
        {
            var a = Enumerable.Range(0, random.Next(0, 12)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray();
            var b = Enumerable.Range(0, random.Next(0, 12)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray();
            var lines = AssistantVersionDiff.Lines(a, b);
            lines.Where(l => l.Op != DiffLine.Insert).Select(l => l.Text).ShouldBe(a, $"round {round}");
            lines.Where(l => l.Op != DiffLine.Delete).Select(l => l.Text).ShouldBe(b, $"round {round}");
            lines.Count(l => l.Op == DiffLine.Equal).ShouldBe(Lcs(a, b), $"round {round}: {string.Join(' ', a)} | {string.Join(' ', b)}");
        }
    }

    private static int Lcs(string[] a, string[] b)
    {
        var t = new int[a.Length + 1, b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                t[i, j] = a[i - 1] == b[j - 1] ? t[i - 1, j - 1] + 1 : Math.Max(t[i - 1, j], t[i, j - 1]);
            }
        }

        return t[a.Length, b.Length];
    }
}
