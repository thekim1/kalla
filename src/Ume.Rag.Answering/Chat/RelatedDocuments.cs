using System.Text.Json.Serialization;
using Ume.Rag.Domain.Retrieval;

namespace Ume.Rag.Answering.Chat;

/// <summary>
/// One entry of <c>ume.related_documents</c> (chat design §3.8, §9.7): the document, its title and link by the citation rule,
/// its collection's slug, its 1-based position in the list, whether the answer cites it with which display number, and the
/// citation's dates (knowledge §10.1: <c>modified</c> by the source's own date, <c>checked</c> for a crawled page; each
/// omitted when null).
/// </summary>
public sealed record RelatedDocument(
    [property: JsonPropertyName("document_id")] Guid DocumentId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("collection")] string Collection,
    [property: JsonPropertyName("rank")] int Rank,
    [property: JsonPropertyName("cited")] bool Cited,
    [property: JsonPropertyName("number"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Number,
    [property: JsonPropertyName("modified"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? Modified = null,
    [property: JsonPropertyName("checked"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? Checked = null);

/// <summary>
/// The answer's <c>ume</c> member (chat design §3.8): the turn's outcome and whether its sources came from keyword search only,
/// then <c>related_documents</c>, <c>follow_ups</c> and <c>escalation</c> when asked for (absent otherwise). The names are fixed
/// here, so the JSON answer and the stream's chunk write the same member whatever their serializer's naming policy.
/// </summary>
public sealed record UmeAnswer(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("degraded")] bool Degraded,
    [property: JsonPropertyName("related_documents"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RelatedDocument>? RelatedDocuments,
    [property: JsonPropertyName("follow_ups"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? FollowUps,
    [property: JsonPropertyName("escalation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EscalationMember? Escalation = null)
{
    /// <summary>
    /// The <c>ume</c> member of <paramref name="plan"/>'s answer, once it has ended (<paramref name="cited"/> complete), as the
    /// first <c>ume</c> chunk carries it (§3.8). Follow-up suggestions asked for are <c>[]</c> here when they are skipped before
    /// any call; with <paramref name="followUpsPending"/> the call is still to come and <c>follow_ups</c> is left for it.
    /// <paramref name="escalation"/> is the escalation member when it goes in this chunk (null: not asked, or its summary call
    /// is still to come).
    /// </summary>
    public static UmeAnswer For(ChatPlan plan, IReadOnlyList<PromptSource> cited, UmeRequest asked, bool followUpsPending, EscalationMember? escalation = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(asked);
        return new UmeAnswer(ChatOutcomes.Name(plan.Outcome), plan.Degraded,
            asked.RelatedDocuments > 0 ? Chat.RelatedDocuments.For(plan.Outcome, plan.Retrieval, cited, asked.RelatedDocuments) : null,
            asked.FollowUps > 0 && !followUpsPending ? [] : null, escalation);
    }
}

/// <summary>
/// The second <c>ume</c> chunk (§3.8 Delivery): the members whose calls ran after the answer, <c>follow_ups</c> (§9.8) and
/// <c>escalation</c> (§9.9); each absent when it went in the first chunk or was not asked for.
/// </summary>
public sealed record UmeLater(
    [property: JsonPropertyName("follow_ups"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? FollowUps,
    [property: JsonPropertyName("escalation"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EscalationMember? Escalation);

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
                    number is not null, number, first.Citation.Modified?.ToUniversalTime(), first.Citation.Checked?.ToUniversalTime());
            }),
        ];
    }
}
