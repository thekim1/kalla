using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain.Admin;

namespace Kalla.AdminApi;

/// <summary>
/// Feedback on documents (admin design §8.8; Decisions "Downvotes per document"): which documents the rated answers cited, as
/// counts. <c>GET /api/collections/{id}/feedback</c> (Read, the knowledge base's owning department only, else 404: a department it
/// is shared with does not see it) sums <c>DocumentDailyFeedback</c> per document of the knowledge base over every assistant that
/// cited it, its own department's and others', most downvotes first, at most <see cref="MaxDocuments"/>. Assistants are counted,
/// never named. Periods as the cost report (§8.6): inclusive Europe/Stockholm dates, default 30 days, at most 400. Titles are
/// read by id, never joined to the counts. No question, answer or comment: reads and exports are not audited.
/// </summary>
public static class DocumentFeedbackEndpoints
{
    /// <summary>The most documents a report lists (§4.5).</summary>
    public const int MaxDocuments = 200;

    /// <summary>The CSV headers of admin §8.8, in order.</summary>
    public static IReadOnlyList<string> CsvHeaders { get; } = ["document", "thumbs_down", "thumbs_up", "assistants"];

    public static RouteGroupBuilder MapDocumentFeedback(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/collections/{id:guid}/feedback", async (Guid id, DateOnly? from, DateOnly? to, string? format, AdminContext admin, CancellationToken ct) =>
            {
                var period = CostPeriod.Of(from, to, null, CostEndpoints.Today(admin));
                // The owning department only: a department the knowledge base is shared with sees no feedback (§8.8), as the cost report.
                _ = await CollectionEndpoints.Scoped(admin).AsNoTracking().Where(c => c.Id == id).Select(c => (Guid?)c.Id).SingleOrDefaultAsync(ct)
                    ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                var counts = await admin.Db.DocumentDailyFeedback.AsNoTracking()
                    .Where(f => f.CollectionId == id && f.Day >= period.From && f.Day <= period.To && (f.ThumbsDown > 0 || f.ThumbsUp > 0))
                    .GroupBy(f => f.DocumentId)
                    .Select(g => new
                    {
                        DocumentId = g.Key,
                        ThumbsDown = g.Sum(x => (long)x.ThumbsDown),
                        ThumbsUp = g.Sum(x => (long)x.ThumbsUp),
                        Assistants = g.Select(x => x.AssistantId).Distinct().Count(),
                    })
                    .OrderByDescending(c => c.ThumbsDown).ThenByDescending(c => c.ThumbsUp).ThenBy(c => c.DocumentId)
                    .Take(MaxDocuments)
                    .ToListAsync(ct);

                Guid[] documentIds = [.. counts.Select(c => c.DocumentId)];
                var titles = await admin.Db.Documents.AsNoTracking().Where(d => documentIds.Contains(d.Id) && d.DeletedAt == null)
                    .Select(d => new { d.Id, Title = d.Title ?? d.DisplayName ?? d.Url ?? "" }).ToDictionaryAsync(d => d.Id, d => d.Title, ct);
                List<DocumentFeedbackRow> rows = [.. counts.Select(c => new DocumentFeedbackRow(c.DocumentId, titles.GetValueOrDefault(c.DocumentId),
                    !titles.ContainsKey(c.DocumentId), c.ThumbsDown, c.ThumbsUp, c.Assistants))];
                if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
                {
                    var csv = Csv.Of(CsvHeaders, rows.Select(CsvRow), ';');
                    return Results.File([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv)], "text/csv; charset=utf-8",
                        $"document-feedback-{period.From:yyyy-MM-dd}-{period.To:yyyy-MM-dd}.csv");
                }

                return Results.Ok(new DocumentFeedbackReport(period.From, period.To, rows));
            })
            .RequireAuthorization(AdminPolicies.Read).WithTags("Feedback on documents").WithName("GetCollectionFeedback")
            .WithSummary("A knowledge base's documents by the ratings of the answers that cited them, most downvotes first (its own department only; format=csv)");

        return api;
    }

    /// <summary>A CSV row of §8.8: a deleted document as its id and "(deleted)".</summary>
    private static string?[] CsvRow(DocumentFeedbackRow r) =>
    [
        r.Title ?? $"{r.DocumentId} (deleted)", r.ThumbsDown.ToString(CultureInfo.InvariantCulture), r.ThumbsUp.ToString(CultureInfo.InvariantCulture),
        r.Assistants.ToString(CultureInfo.InvariantCulture),
    ];
}

/// <summary>
/// One document of the report (admin §4.5): its title (null when <see cref="Deleted"/>: the document is deleted or gone), the
/// downvotes and upvotes of the answers that cited it, and how many assistants' rated answers cited it.
/// </summary>
public sealed record DocumentFeedbackRow(Guid DocumentId, string? Title, bool Deleted, long ThumbsDown, long ThumbsUp, int Assistants);

/// <summary>The ratings per cited document of a period (admin §8.8), most downvotes first, at most <see cref="DocumentFeedbackEndpoints.MaxDocuments"/>.</summary>
public sealed record DocumentFeedbackReport(DateOnly From, DateOnly To, IReadOnlyList<DocumentFeedbackRow> Documents);
