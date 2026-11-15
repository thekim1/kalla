using System.Text.Json;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain.Retrieval;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// The <c>ume</c> extension's pure parts (chat design §3.8, §9.7, §18.6, §18.9; M-7): the request member's parsing and
/// refusals, and <see cref="RelatedDocuments"/> from a retrieval result.
/// </summary>
public sealed class RelatedDocumentsTests
{
    private static readonly Guid Altan = Guid.NewGuid();
    private static readonly Guid Taxa = Guid.NewGuid();
    private static readonly Guid Plank = Guid.NewGuid();

    private static JsonElement Body(string ume) =>
        JsonDocument.Parse($$"""{"model":"bygg","messages":[{"role":"user","content":"Behöver jag bygglov?"}],"ume":{{ume}}}""").RootElement.Clone();

    private static ChatException Refused(string ume) => Should.Throw<ChatException>(() => ChatRequestParser.Parse(Body(ume)));

    [Theory]
    [InlineData("""{}""", 0, 0)]
    [InlineData("""{"related_documents":5}""", 5, 0)]
    [InlineData("""{"related_documents":0,"follow_ups":0}""", 0, 0)]
    [InlineData("""{"related_documents":10,"follow_ups":5}""", 10, 5)]
    public void The_ume_member_is_parsed_with_its_counts(string ume, int related, int followUps) =>
        ChatRequestParser.Parse(Body(ume)).Ume.ShouldBe(new UmeRequest(related, followUps));

    [Theory]
    [InlineData("""{"general_knowledge":"off"}""", GeneralKnowledge.Off)]
    [InlineData("""{"general_knowledge":"supplement"}""", GeneralKnowledge.Supplement)]
    [InlineData("""{"general_knowledge":"fallback","related_documents":3}""", GeneralKnowledge.Fallback)]
    [InlineData("""{"general_knowledge":"open"}""", GeneralKnowledge.Open)]
    [InlineData("""{"general_knowledge":null}""", null)]
    [InlineData("""{}""", null)]
    public void The_ume_member_carries_the_requested_general_knowledge_level(string ume, GeneralKnowledge? expected) =>
        ChatRequestParser.Parse(Body(ume)).Ume!.GeneralKnowledge.ShouldBe(expected);

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
        ChatRequestParser.Parse(JsonDocument.Parse("""{"model":"bygg","messages":[{"role":"user","content":"Behöver jag bygglov?"}]}""").RootElement).Ume.ShouldBeNull();
        ChatRequestParser.Parse(Body("null")).Ume.ShouldBeNull();
    }

    [Theory]
    [InlineData("\"x\"", "invalid_request", "ume")]
    [InlineData("[]", "invalid_request", "ume")]
    [InlineData("5", "invalid_request", "ume")]
    [InlineData("""{"related_documents":11}""", "invalid_request", "ume.related_documents")]
    [InlineData("""{"related_documents":-1}""", "invalid_request", "ume.related_documents")]
    [InlineData("""{"related_documents":2.5}""", "invalid_request", "ume.related_documents")]
    [InlineData("""{"related_documents":"3"}""", "invalid_request", "ume.related_documents")]
    [InlineData("""{"related_documents":null}""", "invalid_request", "ume.related_documents")]
    [InlineData("""{"follow_ups":6}""", "invalid_request", "ume.follow_ups")]
    [InlineData("""{"follow_ups":true}""", "invalid_request", "ume.follow_ups")]
    [InlineData("""{"general_knowledge":"loose"}""", "invalid_request", "ume.general_knowledge")]
    [InlineData("""{"general_knowledge":"Off"}""", "invalid_request", "ume.general_knowledge")]
    [InlineData("""{"general_knowledge":""}""", "invalid_request", "ume.general_knowledge")]
    [InlineData("""{"general_knowledge":1}""", "invalid_request", "ume.general_knowledge")]
    [InlineData("""{"general_knowledge":false}""", "invalid_request", "ume.general_knowledge")]
    [InlineData("""{"general_knowledge":["off"]}""", "invalid_request", "ume.general_knowledge")]
    [InlineData("""{"generalKnowledge":"off"}""", "unsupported_parameter", "ume.generalKnowledge")]
    [InlineData("""{"foo":1}""", "unsupported_parameter", "ume.foo")]
    [InlineData("""{"related_documents":3,"relatedDocuments":3}""", "unsupported_parameter", "ume.relatedDocuments")]
    public void An_invalid_ume_member_is_refused_with_its_param(string ume, string code, string param)
    {
        var refused = Refused(ume);
        (refused.Status, refused.Code, refused.Param).ShouldBe((400, code, param));
    }

    [Theory]
    [InlineData("\"x\"")]
    [InlineData("""{"foo":1,"related_documents":99}""")]
    [InlineData("""{"related_documents":5}""")]
    [InlineData("""{"general_knowledge":"loose"}""")]
    public void A_client_that_does_not_read_ume_ignores_it_without_validation(string ume) =>
        ChatRequestParser.Parse(Body(ume), readUme: false).Ume.ShouldBeNull();

    private static RetrievedChunk Chunk(int rank, Guid document, string title, string? url = null, string? grant = null, int? page = null, string collection = "bygg") =>
        new(rank, Guid.NewGuid(), document, [], "text", "Bygglov › " + title, page, page, 1.0 / rank, new ChunkScores(null, null, null, null, null, null), null, Guid.Empty,
            collection, "internal", new Citation(document, title, grant is null ? null : title + ".pdf", url, grant, grant is null ? "web" : "file", "page",
                grant is null ? "text/html" : "application/pdf", "sv", null));

    private static RetrievalResult Result(params RetrievedChunk[] chunks) =>
        new(chunks, [], new Coverage(CoverageStatus.Ok, 0.2, 0.5, null, null), null, 1, new RetrievalTimings(0, 0, 0, 0, 0, 0));

    /// <summary>Altan (ranks 2, 4, 5), Taxa, an uploaded PDF (ranks 1 without a page, 3 on page 7, 6 on page 2), Plank (rank 7).</summary>
    private static RetrievalResult Retrieval() => Result(
        Chunk(5, Altan, "Altan", url: "https://www.umea.se/bygga/altan"),
        Chunk(3, Taxa, "Taxa 2026", grant: "https://rag-api.umea.local/v1/documents/t/content?grant=g1.k.p.m", page: 7),
        Chunk(2, Altan, "Altan", url: "https://www.umea.se/bygga/altan"),
        Chunk(7, Plank, "Plank", url: "https://www.umea.se/bygga/plank", collection: "plank"),
        Chunk(1, Taxa, "Taxa 2026", grant: "https://rag-api.umea.local/v1/documents/t/content?grant=g1.k.p.m"),
        Chunk(6, Taxa, "Taxa 2026", grant: "https://rag-api.umea.local/v1/documents/t/content?grant=g1.k.p.m", page: 2),
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
        list[0].Url.ShouldBe("https://rag-api.umea.local/v1/documents/t/content?grant=g1.k.p.m#page=7");
        list[0].Title.ShouldBe("Taxa 2026");
        list[1].Url.ShouldBe("https://www.umea.se/bygga/altan");
        (list[2].Title, list[2].Collection).ShouldBe(("Plank", "plank"));
        list.ShouldAllBe(d => !d.Cited && d.Number == null);
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

    [Fact]
    public void The_ume_member_names_are_fixed_and_absent_members_are_left_out()
    {
        // Snake case and the default names give the same member, so the JSON answer and the stream chunk agree.
        var ume = new UmeAnswer("answered", false, [new RelatedDocument(Altan, "Altan", "https://www.umea.se/bygga/altan", "bygg", 1, false, null)], null);
        foreach (var options in new[] { new JsonSerializerOptions(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower } })
        {
            JsonSerializer.Serialize(ume, options).ShouldBe(
                $$"""{"outcome":"answered","degraded":false,"related_documents":[{"document_id":"{{Altan}}","title":"Altan","url":"https://www.umea.se/bygga/altan","collection":"bygg","rank":1,"cited":false}]}""");
        }

        JsonSerializer.Serialize(new UmeAnswer("not-found", false, null, [])).ShouldBe("""{"outcome":"not-found","degraded":false,"follow_ups":[]}""");
    }
}
