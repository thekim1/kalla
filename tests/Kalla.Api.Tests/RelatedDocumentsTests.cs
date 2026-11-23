using System.Text.Json;
using Kalla.Answering.Chat;
using Kalla.Domain.Retrieval;

namespace Kalla.Api.Tests;

/// <summary>
/// The <c>kalla</c> extension's pure parts (chat design §3.8, §9.7, §18.6, §18.9; M-7): the request member's parsing and
/// refusals, and <see cref="RelatedDocuments"/> from a retrieval result.
/// </summary>
public sealed class RelatedDocumentsTests
{
    private static readonly Guid Altan = Guid.NewGuid();
    private static readonly Guid Taxa = Guid.NewGuid();
    private static readonly Guid Plank = Guid.NewGuid();

    private static JsonElement Body(string kalla) =>
        JsonDocument.Parse($$"""{"model":"bygg","messages":[{"role":"user","content":"Behöver jag bygglov?"}],"kalla":{{kalla}}}""").RootElement.Clone();

    private static ChatException Refused(string kalla) => Should.Throw<ChatException>(() => ChatRequestParser.Parse(Body(kalla)));

    [Theory]
    [InlineData("""{}""", 0, 0)]
    [InlineData("""{"related_documents":5}""", 5, 0)]
    [InlineData("""{"related_documents":0,"follow_ups":0}""", 0, 0)]
    [InlineData("""{"related_documents":10,"follow_ups":5}""", 10, 5)]
    public void The_ume_member_is_parsed_with_its_counts(string kalla, int related, int followUps) =>
        ChatRequestParser.Parse(Body(kalla)).Kalla.ShouldBe(new KallaRequest(related, followUps));

    [Theory]
    [InlineData("""{"general_knowledge":"off"}""", GeneralKnowledge.Off)]
    [InlineData("""{"general_knowledge":"supplement"}""", GeneralKnowledge.Supplement)]
    [InlineData("""{"general_knowledge":"fallback","related_documents":3}""", GeneralKnowledge.Fallback)]
    [InlineData("""{"general_knowledge":"open"}""", GeneralKnowledge.Open)]
    [InlineData("""{"general_knowledge":null}""", null)]
    [InlineData("""{}""", null)]
    public void The_ume_member_carries_the_requested_general_knowledge_level(string kalla, GeneralKnowledge? expected) =>
        ChatRequestParser.Parse(Body(kalla)).Kalla!.GeneralKnowledge.ShouldBe(expected);

    [Theory]
    [InlineData(GeneralKnowledge.Fallback, null, GeneralKnowledge.Fallback)]
    [InlineData(GeneralKnowledge.Fallback, GeneralKnowledge.Open, GeneralKnowledge.Fallback)]
    [InlineData(GeneralKnowledge.Fallback, GeneralKnowledge.Supplement, GeneralKnowledge.Supplement)]
    [InlineData(GeneralKnowledge.Fallback, GeneralKnowledge.Off, GeneralKnowledge.Off)]
    [InlineData(GeneralKnowledge.Off, GeneralKnowledge.Open, GeneralKnowledge.Off)]
    [InlineData(GeneralKnowledge.Open, GeneralKnowledge.Open, GeneralKnowledge.Open)]
    public void The_effective_level_is_the_stricter_of_the_assistants_and_the_requested(GeneralKnowledge own, GeneralKnowledge? requested, GeneralKnowledge expected) =>
        GeneralKnowledgeLevels.Effective(own, requested).ShouldBe(expected);

    [Fact]
    public void Without_ume_or_with_null_there_is_no_extension()
    {
        ChatRequestParser.Parse(JsonDocument.Parse("""{"model":"bygg","messages":[{"role":"user","content":"Behöver jag bygglov?"}]}""").RootElement).Kalla.ShouldBeNull();
        ChatRequestParser.Parse(Body("null")).Kalla.ShouldBeNull();
    }

    [Theory]
    [InlineData("\"x\"", "invalid_request", "kalla")]
    [InlineData("[]", "invalid_request", "kalla")]
    [InlineData("5", "invalid_request", "kalla")]
    [InlineData("""{"related_documents":11}""", "invalid_request", "kalla.related_documents")]
    [InlineData("""{"related_documents":-1}""", "invalid_request", "kalla.related_documents")]
    [InlineData("""{"related_documents":2.5}""", "invalid_request", "kalla.related_documents")]
    [InlineData("""{"related_documents":"3"}""", "invalid_request", "kalla.related_documents")]
    [InlineData("""{"related_documents":null}""", "invalid_request", "kalla.related_documents")]
    [InlineData("""{"follow_ups":6}""", "invalid_request", "kalla.follow_ups")]
    [InlineData("""{"follow_ups":true}""", "invalid_request", "kalla.follow_ups")]
    [InlineData("""{"general_knowledge":"loose"}""", "invalid_request", "kalla.general_knowledge")]
    [InlineData("""{"general_knowledge":"Off"}""", "invalid_request", "kalla.general_knowledge")]
    [InlineData("""{"general_knowledge":""}""", "invalid_request", "kalla.general_knowledge")]
    [InlineData("""{"general_knowledge":1}""", "invalid_request", "kalla.general_knowledge")]
    [InlineData("""{"general_knowledge":false}""", "invalid_request", "kalla.general_knowledge")]
    [InlineData("""{"general_knowledge":["off"]}""", "invalid_request", "kalla.general_knowledge")]
    [InlineData("""{"generalKnowledge":"off"}""", "unsupported_parameter", "kalla.generalKnowledge")]
    [InlineData("""{"foo":1}""", "unsupported_parameter", "kalla.foo")]
    [InlineData("""{"related_documents":3,"relatedDocuments":3}""", "unsupported_parameter", "kalla.relatedDocuments")]
    public void An_invalid_ume_member_is_refused_with_its_param(string kalla, string code, string param)
    {
        var refused = Refused(kalla);
        (refused.Status, refused.Code, refused.Param).ShouldBe((400, code, param));
    }

    [Theory]
    [InlineData("\"x\"")]
    [InlineData("""{"foo":1,"related_documents":99}""")]
    [InlineData("""{"related_documents":5}""")]
    [InlineData("""{"general_knowledge":"loose"}""")]
    public void A_client_that_does_not_read_ume_ignores_it_without_validation(string kalla) =>
        ChatRequestParser.Parse(Body(kalla), readKalla: false).Kalla.ShouldBeNull();

    private static RetrievedChunk Chunk(int rank, Guid document, string title, string? url = null, string? grant = null, int? page = null, string collection = "bygg",
        string[]? tags = null) =>
        new(rank, Guid.NewGuid(), document, [], "text", "Bygglov › " + title, page, page, 1.0 / rank, new ChunkScores(null, null, null, null, null, null), null, Guid.Empty,
            collection, "internal", new Citation(document, title, grant is null ? null : title + ".pdf", url, grant, grant is null ? "web" : "file", "page",
                grant is null ? "text/html" : "application/pdf", "sv", null)) { Tags = tags ?? [] };

    private static RetrievalResult Result(params RetrievedChunk[] chunks) =>
        new(chunks, [], new Coverage(CoverageStatus.Ok, 0.2, 0.5, null, null), null, 1, new RetrievalTimings(0, 0, 0, 0, 0, 0));

    /// <summary>Altan (ranks 2, 4, 5), Taxa, an uploaded PDF (ranks 1 without a page, 3 on page 7, 6 on page 2), Plank (rank 7).</summary>
    private static RetrievalResult Retrieval() => Result(
        Chunk(5, Altan, "Altan", url: "https://www.umea.se/bygga/altan"),
        Chunk(3, Taxa, "Taxa 2026", grant: "https://kalla-api.umea.local/v1/documents/t/content?grant=g1.k.p.m", page: 7),
        Chunk(2, Altan, "Altan", url: "https://www.umea.se/bygga/altan"),
        Chunk(7, Plank, "Plank", url: "https://www.umea.se/bygga/plank", collection: "plank"),
        Chunk(1, Taxa, "Taxa 2026", grant: "https://kalla-api.umea.local/v1/documents/t/content?grant=g1.k.p.m"),
        Chunk(6, Taxa, "Taxa 2026", grant: "https://kalla-api.umea.local/v1/documents/t/content?grant=g1.k.p.m", page: 2),
        Chunk(4, Altan, "Altan", url: "https://www.umea.se/bygga/altan"));

    private static PromptSource Source(int number, RetrievalResult retrieval, Guid document)
    {
        var chunks = retrieval.Results.Where(r => r.DocumentId == document).OrderBy(r => r.Rank).ToList();
        return new PromptSource(number, chunks[0].Citation, chunks);
    }

    [Fact]
    public void Each_document_is_listed_once_in_best_rank_order_with_its_citation_link()
    {
        var retrieval = Retrieval();
        var list = RelatedDocuments.For(ChatOutcome.Answered, retrieval, [], 10);

        list.Select(d => (d.DocumentId, d.Rank)).ShouldBe([(Taxa, 1), (Altan, 2), (Plank, 3)]);
        // An upload links by its grant, with #page= of its best-ranked chunk that has a page; a crawled page by its own URL.
        list[0].Url.ShouldBe("https://kalla-api.umea.local/v1/documents/t/content?grant=g1.k.p.m#page=7");
        list[0].Title.ShouldBe("Taxa 2026");
        list[1].Url.ShouldBe("https://www.umea.se/bygga/altan");
        (list[2].Title, list[2].Collection).ShouldBe(("Plank", "plank"));
        list.ShouldAllBe(d => d.Cited == false && d.Number == null);
    }

    [Fact]
    public void The_link_is_the_one_the_citation_gets()
    {
        var retrieval = Retrieval();
        var list = RelatedDocuments.For(ChatOutcome.Answered, retrieval, [], 10);
        list[0].Url.ShouldBe(Citations.Link(Source(1, retrieval, Taxa)));
        list[1].Url.ShouldBe(Citations.Link(Source(2, retrieval, Altan)));
    }

    [Fact]
    public void N_limits_the_list()
    {
        RelatedDocuments.For(ChatOutcome.Answered, Retrieval(), [], 2).Select(d => d.DocumentId).ShouldBe([Taxa, Altan]);
        RelatedDocuments.For(ChatOutcome.Answered, Retrieval(), [], 0).ShouldBeEmpty();
    }

    [Fact]
    public void Cited_documents_carry_their_display_number()
    {
        // The answer cited Plank first and Altan second (display order); Taxa, the best-ranked, is not cited.
        var retrieval = Retrieval();
        var list = RelatedDocuments.For(ChatOutcome.Answered, retrieval, [Source(3, retrieval, Plank), Source(2, retrieval, Altan)], 10);
        list.Select(d => (d.DocumentId, d.Cited, d.Number)).ShouldBe([(Taxa, false, (int?)null), (Altan, true, 2), (Plank, true, 1)]);
    }

    [Theory]
    [InlineData(ChatOutcome.NotFound)]
    [InlineData(ChatOutcome.AnsweredGeneral)]
    [InlineData(ChatOutcome.Unavailable)]
    public void Turns_whose_answer_says_nothing_matched_list_nothing(ChatOutcome outcome) =>
        RelatedDocuments.For(outcome, Retrieval(), [], 10).ShouldBeEmpty();

    [Fact]
    public void A_conversational_turn_has_no_retrieval_and_lists_nothing() => RelatedDocuments.For(ChatOutcome.Answered, null, [], 10).ShouldBeEmpty();

    private static readonly DateTimeOffset Stated = new(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Crawled = new(2026, 11, 18, 23, 30, 0, TimeSpan.FromHours(1));

    private static PromptSource Dated(string sourceType, DateTimeOffset? modified, DateTimeOffset? checkedAt)
    {
        var chunk = Chunk(1, Altan, "Altan", url: sourceType == "web" ? "https://www.umea.se/bygga/altan" : null,
            grant: sourceType == "file" ? "https://kalla-api.umea.local/v1/documents/a/content?grant=g1" : null);
        chunk = chunk with { Citation = chunk.Citation with { Modified = modified, Checked = checkedAt } };
        return new PromptSource(1, chunk.Citation, [chunk]);
    }

    [Theory]
    // Knowledge §10.4, Decisions "Source dates on citations": the stated date, then when a crawl last confirmed the page, in
    // the turn's language; the dates are UTC days (Crawled is 2026-11-18 22:30 UTC).
    [InlineData("web", true, true, "sv", "Altan (2026-03-02, kontrollerad 2026-11-18)")]
    [InlineData("web", false, true, "sv", "Altan (kontrollerad 2026-11-18)")]
    [InlineData("web", true, true, "en", "Altan (2026-03-02, checked 2026-11-18)")]
    [InlineData("file", true, false, "sv", "Altan (2026-03-02)")]
    [InlineData("web", false, false, "sv", "Altan")]
    public void The_label_shows_the_stated_date_and_the_last_check(string sourceType, bool stated, bool crawled, string language, string expected) =>
        Citations.Label(Dated(sourceType, stated ? Stated : null, crawled ? Crawled : null), language).ShouldBe(expected);

    [Fact]
    public void Each_entry_carries_its_documents_dates_in_utc_and_leaves_out_the_missing_ones()
    {
        var retrieval = Result(
            Dated("web", null, Crawled).Chunks[0],
            Chunk(2, Taxa, "Taxa 2026", grant: "https://kalla-api.umea.local/v1/documents/t/content?grant=g1.k.p.m") is var taxa
                ? taxa with { Citation = taxa.Citation with { Modified = Stated } } : null!);
        var list = RelatedDocuments.For(ChatOutcome.Answered, retrieval, [], 10);

        (list[0].Modified, list[0].Checked).ShouldBe(((DateTimeOffset?)null, Crawled));
        list[0].Checked!.Value.Offset.ShouldBe(TimeSpan.Zero);
        (list[1].Modified, list[1].Checked).ShouldBe((Stated, (DateTimeOffset?)null));

        var json = JsonSerializer.Serialize(list);
        using var parsed = JsonDocument.Parse(json);
        parsed.RootElement[0].TryGetProperty("modified", out _).ShouldBeFalse();
        parsed.RootElement[0].GetProperty("checked").GetString().ShouldBe("2026-11-18T22:30:00+00:00");
        parsed.RootElement[1].GetProperty("modified").GetString().ShouldBe("2026-03-02T00:00:00+00:00");
        parsed.RootElement[1].TryGetProperty("checked", out _).ShouldBeFalse();
    }

    [Fact]
    public void The_ume_member_names_are_fixed_and_absent_members_are_left_out()
    {
        // Snake case and the default names give the same member, so the JSON answer and the stream chunk agree.
        var kalla = new KallaAnswer("answered", false, [new RelatedDocument(Altan, "Altan", "https://www.umea.se/bygga/altan", "bygg", ["altan"], 1, false, null)], null);
        foreach (var options in new[] { new JsonSerializerOptions(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower } })
        {
            JsonSerializer.Serialize(kalla, options).ShouldBe(
                $$"""{"outcome":"answered","degraded":false,"related_documents":[{"document_id":"{{Altan}}","title":"Altan","url":"https://www.umea.se/bygga/altan","collection":"bygg","topics":["altan"],"rank":1,"cited":false}]}""");
        }

        JsonSerializer.Serialize(new KallaAnswer("not-found", false, null, [])).ShouldBe("""{"outcome":"not-found","degraded":false,"follow_ups":[]}""");
    }

    [Fact]
    public void Each_entry_carries_its_best_ranked_chunks_topics_as_stored_and_an_empty_list_without_any()
    {
        // §9.7: Altan's best-ranked chunk (rank 2) carries "altan" and "uteplats"; its rank 4 chunk's "plank" is not the entry's.
        var retrieval = Result(
            Chunk(4, Altan, "Altan", url: "https://www.umea.se/bygga/altan", tags: ["plank"]),
            Chunk(1, Taxa, "Taxa 2026", grant: "https://kalla-api.umea.local/v1/documents/t/content?grant=g1.k.p.m"),
            Chunk(2, Altan, "Altan", url: "https://www.umea.se/bygga/altan", tags: ["altan", "uteplats"]));
        var list = RelatedDocuments.For(ChatOutcome.Answered, retrieval, [], 10);

        list.Select(d => d.DocumentId).ShouldBe([Taxa, Altan]);
        list[0].Topics.ShouldBeEmpty();
        list[1].Topics.ShouldBe(["altan", "uteplats"]);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(list));
        json.RootElement[0].GetProperty("topics").GetArrayLength().ShouldBe(0); // present, never left out
        json.RootElement[1].GetProperty("topics").EnumerateArray().Select(t => t.GetString()).ShouldBe(["altan", "uteplats"]);
    }

    [Theory]
    [InlineData("""{"nearest":true}""", true)]
    [InlineData("""{"nearest":false}""", false)]
    [InlineData("""{}""", false)]
    public void The_nearest_member_is_a_flag(string kalla, bool expected) => ChatRequestParser.Parse(Body(kalla)).Kalla!.Nearest.ShouldBe(expected);

    [Theory]
    [InlineData("""{"nearest":"yes"}""")]
    [InlineData("""{"nearest":1}""")]
    [InlineData("""{"nearest":null}""")]
    public void A_nearest_member_that_is_not_a_boolean_is_refused(string kalla)
    {
        var refused = Refused(kalla);
        (refused.Status, refused.Code, refused.Param).ShouldBe((400, "invalid_request", "kalla.nearest"));
    }

    [Fact]
    public void The_nearest_member_is_the_subject_or_null_when_asked_and_absent_otherwise()
    {
        // §9.11: an answered turn asked for it gets "nearest": null (written, not left out); the subject has exactly collection and topics.
        foreach (var options in new[] { new JsonSerializerOptions(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower } })
        {
            JsonSerializer.Serialize(new KallaAnswer("answered", false, null, null, Nearest: new NearestMember(null)), options)
                .ShouldBe("""{"outcome":"answered","degraded":false,"nearest":null}""");
            JsonSerializer.Serialize(new KallaAnswer("not-found", false, null, null, Nearest: new NearestMember(new NearestSubject("it-support", ["vpn", "fel-809"]))), options)
                .ShouldBe("""{"outcome":"not-found","degraded":false,"nearest":{"collection":"it-support","topics":["vpn","fel-809"]}}""");
            JsonSerializer.Serialize(new KallaAnswer("not-found", false, null, null, Nearest: new NearestMember(new NearestSubject("bygg", []))), options)
                .ShouldBe("""{"outcome":"not-found","degraded":false,"nearest":{"collection":"bygg","topics":[]}}""");
        }

        JsonSerializer.Serialize(new KallaAnswer("not-found", false, null, null)).ShouldNotContain("nearest");
    }

    [Fact]
    public void The_subject_of_a_nearest_hit_holds_no_document()
    {
        // The negative proof (§9.11): the subject is the slug and the tags; the document id stays with the writer's key.
        var hit = new NearestHit(Guid.NewGuid(), Altan, "bygg", ["altan"]);
        typeof(NearestSubject).GetProperties().Select(p => p.Name).ShouldBe(["Collection", "Topics"], ignoreOrder: true);
        var json = JsonSerializer.Serialize(new NearestMember(hit.Subject));
        json.ShouldBe("""{"collection":"bygg","topics":["altan"]}""");
        json.ShouldNotContain(Altan.ToString());
        json.ShouldNotContain(hit.CollectionId.ToString());
    }
}
