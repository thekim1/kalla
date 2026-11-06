using System.Text.Json.Serialization;
using Ume.Rag.Domain.Retrieval;

namespace Ume.Rag.Answering.Chat;

/// <summary>
/// One entry of <c>ume.related_documents</c> (chat design §3.8, §9.7): the document, its title and link by the citation rule,
/// its collection's slug, its 1-based position in the list, and whether the answer cites it with which display number.
/// </summary>
public sealed record RelatedDocument(
    [property: JsonPropertyName("document_id")] Guid DocumentId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("collection")] string Collection,
    [property: JsonPropertyName("rank")] int Rank,
    [property: JsonPropertyName("cited")] bool Cited,
    [property: JsonPropertyName("number"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Number);

/// <summary>
/// The answer's <c>ume</c> member (chat design §3.8): the turn's outcome and whether its sources came from keyword search only,
/// then <c>related_documents</c> and <c>follow_ups</c> when asked for (absent otherwise). The names are fixed here, so the JSON
/// answer and the stream's chunk write the same member whatever their serializer's naming policy.
/// </summary>
public sealed record UmeAnswer(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("degraded")] bool Degraded,
    [property: JsonPropertyName("related_documents"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RelatedDocument>? RelatedDocuments,
    [property: JsonPropertyName("follow_ups"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? FollowUps)
{
    /// <summary>
    /// The <c>ume</c> member of <paramref name="plan"/>'s answer, once it has ended (<paramref name="cited"/> complete). Follow-up
    /// suggestions are not made yet (M-8): asked for, they are <c>[]</c>, as §3.8 gives them when skipped before any call.
    /// </summary>
    public static UmeAnswer For(ChatPlan plan, IReadOnlyList<PromptSource> cited, UmeRequest asked)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(asked);
        return new UmeAnswer(ChatOutcomes.Name(plan.Outcome), plan.Degraded,
            asked.RelatedDocuments > 0 ? Chat.RelatedDocuments.For(plan.Outcome, plan.Retrieval, cited, asked.RelatedDocuments) : null,
            asked.FollowUps > 0 ? [] : null);
    }
}

/// <summary>
/// <c>ume.related_documents</c> (chat design §9.7), from the retrieval the answer was built from: no second search, no
/// embedding, no SQL statement, no model call.
/// </summary>
public static class RelatedDocuments
{
    /// <summary>
    /// The first <paramref name="count"/> documents of <paramref name="retrieval"/>, one entry per document in best-rank order
    /// (documents the token budget left out of the prompt included). Each links as its citation does (<see cref="Citations.Link"/>:
    /// the page's own URL or the upload's grant link, <c>#page=</c> of the best-ranked chunk with a page for PDFs); a document
    /// the answer cites (<paramref name="cited"/>, in citation order) carries its display number. Empty for every outcome but
    /// <see cref="ChatOutcome.Answered"/> and for a conversational turn (no retrieval): on coverage <c>Low</c> the answer has
    /// just said that nothing matched.
    /// </summary>
    public static IReadOnlyList<RelatedDocument> For(ChatOutcome outcome, RetrievalResult? retrieval, IReadOnlyList<PromptSource> cited, int count)
    {
        ArgumentNullException.ThrowIfNull(cited);
        if (count <= 0 || outcome != ChatOutcome.Answered || retrieval is null)
        {
            return [];
        }

        var numbers = new Dictionary<Guid, int>();
        foreach (var source in cited)
        {
            numbers.TryAdd(source.Chunks[0].DocumentId, numbers.Count + 1);
        }

        return
        [
            .. retrieval.Results.OrderBy(r => r.Rank).GroupBy(r => r.DocumentId).Take(count).Select((document, i) =>
            {
                var chunks = document.ToList();
                var first = chunks[0];
                int? number = numbers.TryGetValue(document.Key, out var n) ? n : null;
                return new RelatedDocument(document.Key, first.Citation.Title, Citations.Link(new PromptSource(0, first.Citation, chunks)), first.CollectionSlug, i + 1,
                    number is not null, number);
            }),
        ];
    }
}
