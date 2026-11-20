using Kalla.Answering.Chat;
using Kalla.Domain.Retrieval;

namespace Kalla.Api.Tests;

/// <summary>Chat design §9.2–§9.4 over a stream: markers split across deltas, the first citation before its text, the output filter.</summary>
public sealed class AnswerTransducerTests
{
    private static PromptSource Source(int number, string title, string url) => new(number,
        new Citation(Guid.NewGuid(), title, null, url, null, "web", "page", "text/html", "sv", null),
        [new RetrievedChunk(number, Guid.NewGuid(), Guid.NewGuid(), [], "text", title, null, null, 1, new ChunkScores(null, null, null, null, null, null), null, Guid.Empty, "c",
            "public", new Citation(Guid.NewGuid(), title, null, url, null, "web", "page", "text/html", "sv", null))]);

    private static readonly PromptSource[] Sources = [Source(1, "Altan", "https://www.umea.se/altan"), Source(2, "Taxa", "https://www.umea.se/taxa")];

    private static (string Text, List<AnswerPiece> Pieces) Run(AnswerTransducer transducer, params string[] deltas)
    {
        var pieces = new List<AnswerPiece>();
        foreach (var delta in deltas)
        {
            pieces.AddRange(transducer.Feed(delta));
        }

        pieces.AddRange(transducer.Flush());
        return (string.Concat(pieces.OfType<AnswerPiece.Text>().Select(p => p.Content)), pieces);
    }

    [Fact]
    public void Markers_split_across_deltas_are_renumbered_and_the_first_citation_comes_before_its_text()
    {
        var transducer = new AnswerTransducer(Sources, ["umea.se"]);
        var (text, pieces) = Run(transducer, "Taxan gäller [", "2", "]. Altan kräver lov [1][", "2] men inte [", "7].");

        text.ShouldBe("Taxan gäller [1]. Altan kräver lov [2][1] men inte.");
        transducer.Cited.Select(s => s.Citation.Title).ShouldBe(["Taxa", "Altan"]);
        // Each document's first citation precedes the text that holds its marker.
        var order = pieces.Select(p => p switch { AnswerPiece.FirstCitation f => $"cite:{f.Display}", AnswerPiece.Text t => t.Content, _ => "" }).ToList();
        order.IndexOf("cite:1").ShouldBeLessThan(order.FindIndex(p => p.Contains("[1]", StringComparison.Ordinal)));
        var annotations = pieces.OfType<AnswerPiece.Text>().SelectMany(p => p.Annotations).ToList();
        annotations.Count.ShouldBe(3);
        text[annotations[0].StartIndex..annotations[0].EndIndex].ShouldBe("[1]");
        annotations[0].Url.ShouldBe("https://www.umea.se/taxa");
        transducer.Filtered["invalid_marker"].ShouldBe(1);
    }

    [Fact]
    public void Images_and_active_html_go_and_links_survive_only_to_allowed_hosts()
    {
        var transducer = new AnswerTransducer(Sources, ["umea.se", "*.umea.se"]);
        var (text, _) = Run(transducer,
            "Se ![karta](https://evil.example/x.png) och <img src=x onerror=alert(1)>. ",
            "Läs [mer](https://www.umea.se/bygglov) eller [här](https://evil.example/steal?d=1) och [js](javascript:alert(1)). ",
            "Besök https://evil.example/a och https://kommun.umea.se/b");

        text.ShouldBe("Se [karta] och . Läs [mer](https://www.umea.se/bygglov) eller här och js. Besök `https://evil.example/a` och https://kommun.umea.se/b");
        ShouldBeInert(text);
        transducer.Filtered["image"].ShouldBe(1);
        transducer.Filtered["html"].ShouldBe(1);
        transducer.Filtered["link"].ShouldBe(2);
        transducer.Filtered["url"].ShouldBe(1);
    }

    [Fact]
    public void A_link_split_across_deltas_is_judged_whole()
    {
        var transducer = new AnswerTransducer(Sources, ["umea.se"]);
        Run(transducer, "Läs [här](https://evil", ".example/x) nu.").Text.ShouldBe("Läs här nu.");
        Run(new AnswerTransducer(Sources, ["umea.se"]), "Klicka [js](javascript:alert(1", ")) nu.").Text.ShouldBe("Klicka js nu.");
    }

    [Fact]
    public void An_open_construct_is_let_through_after_the_hold_limit()
    {
        var transducer = new AnswerTransducer(Sources, ["umea.se"]);
        var held = "[" + new string('a', AnswerTransducer.MaxHeld + 10);
        transducer.Feed(held).OfType<AnswerPiece.Text>().Sum(p => p.Content.Length).ShouldBe(held.Length);
    }

    /// <summary>Nothing unvalidated may stay clickable: no "](" before an unsafe target, no raw "&lt;", no reference definition.</summary>
    private static void ShouldBeInert(string text)
    {
        text.ShouldNotContain("](javascript", Case.Insensitive);
        text.ShouldNotContain("](data:", Case.Insensitive);
        text.ShouldNotContain('<');
        System.Text.RegularExpressions.Regex.IsMatch(text, @"(?m)^[ \t]{0,3}\[[^\]]*\]:").ShouldBeFalse(text);
    }

    [Theory]
    [InlineData("[a [b] c](javascript:alert(1))")]
    [InlineData("[x](javascript:alert(1) \"t\")")]
    [InlineData("[x]( javascript:alert(1) )")]
    [InlineData("[x][r]\n[r]: javascript:alert(1)")]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("<div onmouseover=\"alert(1)\">x</div>")]
    public void Constructs_the_link_pattern_does_not_match_are_escaped(string answer)
    {
        // #203 review probes: escape by default instead of chasing every Markdown shape.
        ShouldBeInert(Run(new AnswerTransducer(Sources, ["umea.se"]), answer).Text);
    }

    [Fact]
    public void A_link_after_the_hold_limit_or_split_at_the_parenthesis_stays_inert()
    {
        var longText = "[" + new string('a', AnswerTransducer.MaxHeld + 10);
        ShouldBeInert(Run(new AnswerTransducer(Sources, ["umea.se"]), longText, "](javascript:alert(1))").Text);
        ShouldBeInert(Run(new AnswerTransducer(Sources, ["umea.se"]), "Klicka [x]", "(javascript:alert(1)) nu").Text);
    }

    [Fact]
    public void An_allowed_link_survives_the_escaping()
    {
        Run(new AnswerTransducer(Sources, ["umea.se", "*.umea.se"]), "Läs [Bygglov](https://www.umea.se/bygglov) och a < b.").Text
            .ShouldBe("Läs [Bygglov](https://www.umea.se/bygglov) och a &lt; b.");
    }
}
