using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;

namespace Kalla.AdminApi;

/// <summary>
/// The content-gap report (admin design §8.7; Decisions "Content-gap report"): which questions the knowledge does not answer, as
/// counts. <c>GET /api/assistants/{id}/gaps</c> (Read, within the assistant's scope, else 404) reads the period's turns from
/// <c>AssistantDailyTotals</c> and the <c>not-found</c> and <c>answered-general</c> turns per nearest document from
/// <c>AssistantDailyGaps</c>, most first, at most <see cref="MaxDocuments"/>, <i>no hit</i> as its own row. Periods and scope as
/// the cost report (§8.6): inclusive Europe/Stockholm dates, the rows' <c>DepartmentId</c> among the caller's departments. Names and
/// titles are read by id (§4.8), never joined to the counts. A knowledge base of another department shows its name and the
/// document's title only while the assistant's department may link it today (V11, as the test questions' documents, #983/#999)
/// or the caller's departments own it: a row lives 3 years and a share can be withdrawn, so a knowledge base no longer shared
/// is <see cref="ContentGapRow.NotShared"/>, without name or title. No question, answer or subject: reads and exports are not audited.
/// </summary>
public static class GapEndpoints
{
    /// <summary>The most nearest documents a report lists (§8.7).</summary>
    public const int MaxDocuments = 200;

    /// <summary>The CSV headers of admin §8.7, in order.</summary>
    public static IReadOnlyList<string> CsvHeaders { get; } = ["knowledge_base", "document", "not_found", "answered_general"];

    public static RouteGroupBuilder MapGaps(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/assistants/{id:guid}/gaps", async (Guid id, DateOnly? from, DateOnly? to, string? format, AdminContext admin, CancellationToken ct) =>
            {
                var period = CostPeriod.Of(from, to, null, CostEndpoints.Today(admin));
                var assistant = await AssistantEndpoints.LoadAsync(admin, id, ct, tracked: false);
                var departments = await admin.Departments().Select(d => d.Id).ToArrayAsync(ct);
                var turns = await admin.Db.AssistantDailyTotals.AsNoTracking()
                    .Where(t => t.AssistantId == id && t.Day >= period.From && t.Day <= period.To && departments.Contains(t.DepartmentId))
                    .SumAsync(t => (long)t.Messages, ct);
                var counts = await admin.Db.AssistantDailyGaps.AsNoTracking()
                    .Where(g => g.AssistantId == id && g.Day >= period.From && g.Day <= period.To && departments.Contains(g.DepartmentId))
                    .GroupBy(g => new { g.CollectionId, g.DocumentId })
                    .Select(g => new
                    {
                        g.Key.CollectionId,
                        g.Key.DocumentId,
                        NotFound = g.Sum(x => x.Outcome == MessageOutcomes.NotFound ? (long)x.Turns : 0L),
                        AnsweredGeneral = g.Sum(x => x.Outcome == MessageOutcomes.AnsweredGeneral ? (long)x.Turns : 0L),
                    })
                    .ToListAsync(ct);
                var top = counts.OrderByDescending(c => c.NotFound + c.AnsweredGeneral).ThenByDescending(c => c.NotFound)
                    .ThenBy(c => c.CollectionId).ThenBy(c => c.DocumentId).Take(MaxDocuments).ToList();

                Guid[] collectionIds = [.. top.Select(c => c.CollectionId).Where(c => c != Guid.Empty).Distinct()];
                Guid[] documentIds = [.. top.Select(c => c.DocumentId).Where(d => d != Guid.Empty).Distinct()];
                var collections = await admin.Db.Collections.AsNoTracking().Where(c => collectionIds.Contains(c.Id) && c.DeletedAt == null)
                    .Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
                var titles = await admin.Db.Documents.AsNoTracking().Where(d => documentIds.Contains(d.Id) && d.DeletedAt == null)
                    .Select(d => new { d.Id, Title = d.Title ?? d.DisplayName ?? d.Url ?? "" }).ToDictionaryAsync(d => d.Id, d => d.Title, ct);
                // The document opens in its knowledge base for those whose departments own it (§8.7).
                var openable = (await CollectionEndpoints.Scoped(admin).AsNoTracking().Where(c => collectionIds.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct)).ToHashSet();
                // Named while the assistant's department may link it today (its own, or shared with it) or the caller owns it.
                var shown = await CollectionSharing.LinkableAsync(admin.Db, assistant.DepartmentId, collectionIds, ct);
                shown.UnionWith(openable);

                List<ContentGapRow> rows = [.. top.Select(c => ContentGapRow.Of(c.CollectionId, c.DocumentId, collections.GetValueOrDefault(c.CollectionId),
                    titles.GetValueOrDefault(c.DocumentId), shown.Contains(c.CollectionId), openable.Contains(c.CollectionId), c.NotFound, c.AnsweredGeneral))];
                if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
                {
                    var csv = Csv.Of(CsvHeaders, rows.Select(CsvRow), ';');
                    return Results.File([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv)], "text/csv; charset=utf-8",
                        $"content-gaps-{period.From:yyyy-MM-dd}-{period.To:yyyy-MM-dd}.csv");
                }

                return Results.Ok(new ContentGapReport(period.From, period.To, turns, counts.Sum(c => c.NotFound), counts.Sum(c => c.AnsweredGeneral), rows));
            })
            .RequireAuthorization(AdminPolicies.Read).WithTags("Content gaps").WithName("GetAssistantGaps")
            .WithSummary("An assistant's not-found and general-knowledge turns of a period per nearest document, most first (format=csv: one row each)");

        return api;
    }

    /// <summary>
    /// A CSV row of §8.7: <i>no hit</i> as "(no hit)", a knowledge base no longer shared as "(not shared)" in both columns, a deleted
    /// knowledge base or document as its id and "(deleted)".
    /// </summary>
    private static string?[] CsvRow(ContentGapRow r) =>
    [
        r.NoHit ? "" : r.NotShared ? "(not shared)" : r.CollectionName ?? $"{r.CollectionId} (deleted)",
        r.NoHit ? "(no hit)" : r.NotShared ? "(not shared)" : r.DocumentTitle ?? $"{r.DocumentId} (deleted)",
        r.NotFound.ToString(CultureInfo.InvariantCulture), r.AnsweredGeneral.ToString(CultureInfo.InvariantCulture),
    ];
}

/// <summary>
/// One nearest document of the report: the knowledge base's name and the document's title, each null when it is gone or deleted
/// (<see cref="Deleted"/>: the document is), or <see cref="NoHit"/> with the zero ids when the search found nothing, or
/// <see cref="NotShared"/> (both null) when the knowledge base is another department's that the assistant's department may no
/// longer link; whether the caller may open it in its knowledge base; the turns that ended <c>not-found</c> and <c>answered-general</c>.
/// </summary>
public sealed record ContentGapRow(
    Guid CollectionId, Guid DocumentId, bool NoHit, string? CollectionName, string? DocumentTitle, bool Deleted, bool NotShared, bool CanOpen, long NotFound,
    long AnsweredGeneral)
{
    /// <summary>
    /// The row of a nearest hit: <paramref name="collectionName"/> null when the knowledge base is gone or deleted (then the row is
    /// deleted, whatever <paramref name="shown"/> says); otherwise, unless <paramref name="shown"/>, neither name nor title.
    /// </summary>
    public static ContentGapRow Of(Guid collectionId, Guid documentId, string? collectionName, string? documentTitle, bool shown, bool canOpen, long notFound,
        long answeredGeneral)
    {
        if (collectionId == Guid.Empty && documentId == Guid.Empty)
        {
            return new(collectionId, documentId, true, null, null, false, false, false, notFound, answeredGeneral);
        }

        if (collectionName is not null && !shown)
        {
            return new(collectionId, documentId, false, null, null, false, true, false, notFound, answeredGeneral);
        }

        var deleted = collectionName is null || documentTitle is null;
        return new(collectionId, documentId, false, collectionName, deleted && collectionName is null ? null : documentTitle, deleted, false, !deleted && canOpen,
            notFound, answeredGeneral);
    }
}

/// <summary>
/// The content-gap report of a period (admin §8.7): the assistant's turns (<c>AssistantDailyTotals.Messages</c>), those that ended
/// <c>not-found</c> and <c>answered-general</c>, and the nearest documents, most first, at most <see cref="GapEndpoints.MaxDocuments"/>.
/// </summary>
public sealed record ContentGapReport(DateOnly From, DateOnly To, long Turns, long NotFound, long AnsweredGeneral, IReadOnlyList<ContentGapRow> Documents);
