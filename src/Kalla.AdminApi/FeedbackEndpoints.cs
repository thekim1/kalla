using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;

namespace Kalla.AdminApi;

/// <summary>
/// Återkoppling (admin design §8.1, chat design §14.4; P1b item 13): ratings of the caller's departments' assistants,
/// without content: rating, score, reason category, the answering message's outcome, coverage and language, the
/// assistant version that answered, the cited documents' titles, assistant and client names, the review state and
/// whether a comment is stored (in P1 never: mode <c>none</c>, weakness review §4); the list and the totals are
/// <c>Feedback</c>, the page's four roles (admin design §3.3), not the editor roles. Totals from
/// <c>AssistantDailyTotals</c>; import health per Open WebUI client from <c>FeedbackImportState</c>. Marking an item
/// reviewed or dismissed is <c>FeedbackComments</c> and audited.
/// </summary>
public static class FeedbackEndpoints
{
    public static RouteGroupBuilder MapFeedback(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var feedback = api.MapGroup("/feedback").WithTags("Feedback");

        feedback.MapGet("", async ([AsParameters] FeedbackQuery q, AdminContext admin, CancellationToken ct) =>
            {
                var take = Keyset.Take(q.Limit);
                var rows = Filtered(admin, q);
                if (q.After is { } last)
                {
                    // Keyset by (CreatedAt, Id), newest first; the cursor is the last item's id.
                    var at = await admin.Db.Feedback.AsNoTracking().Where(f => f.Id == last).Select(f => (DateTimeOffset?)f.CreatedAt).SingleOrDefaultAsync(ct);
                    rows = at is { } created ? rows.Where(f => f.CreatedAt < created || (f.CreatedAt == created && f.Id.CompareTo(last) < 0)) : rows.Where(_ => false);
                }

                var page = await (from f in rows
                                  join m in admin.Db.Messages.AsNoTracking() on f.MessageId equals m.Id
                                  join c in admin.Db.Conversations.AsNoTracking() on f.ConversationId equals c.Id
                                  join a in admin.Db.Assistants.IgnoreQueryFilters().AsNoTracking() on c.AssistantId equals a.Id
                                  orderby f.CreatedAt descending, f.Id descending
                                  select new
                                  {
                                      f.Id, f.CreatedAt, f.Rating, f.Score, f.ReasonCategory, f.ReviewState, HasComment = f.CommentCiphertext != null, f.Source,
                                      AssistantId = a.Id, AssistantName = a.Name, c.ClientId, m.AssistantVersionId, m.Outcome, m.Coverage, m.Language, MessageId = m.Id,
                                  })
                    .Take(take + 1).ToListAsync(ct);
                var (items, next) = KeysetPage.From(page, take, i => i.Id);

                // Joined in memory (§8.1): version numbers, client names and the cited documents' titles.
                var versionIds = items.Select(i => i.AssistantVersionId).OfType<Guid>().Distinct().ToList();
                var versions = await admin.Db.AssistantVersions.AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Number, ct);
                var clientIds = items.Select(i => i.ClientId).Distinct().ToList();
                var clients = await admin.Db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
                var messageIds = items.Select(i => i.MessageId).ToList();
                var cited = await (from s in admin.Db.MessageSources.AsNoTracking()
                                   join d in admin.Db.Documents.AsNoTracking() on s.DocumentId equals d.Id into documents
                                   from d in documents.DefaultIfEmpty()
                                   where messageIds.Contains(s.MessageId) && s.Used
                                   orderby s.MessageId, s.Number, s.Rank
                                   select new { s.MessageId, s.DocumentId, Title = d == null ? null : d.Title ?? d.DisplayName ?? d.Url })
                    .ToListAsync(ct);
                var byMessage = cited.GroupBy(s => s.MessageId).ToDictionary(g => g.Key, g => g.DistinctBy(s => s.DocumentId).Select(s => new CitedDocument(s.DocumentId, s.Title)).ToList());

                return new KeysetPage<FeedbackItem, Guid?>(
                    [
                        .. items.Select(i => new FeedbackItem(
                            i.Id, i.CreatedAt, i.Rating, i.Score, i.ReasonCategory, i.ReviewState, i.HasComment, i.Source, i.AssistantId, i.AssistantName,
                            i.AssistantVersionId is { } v && versions.TryGetValue(v, out var n) ? n : null, i.ClientId, clients.GetValueOrDefault(i.ClientId),
                            i.Outcome, i.Coverage, i.Language, byMessage.GetValueOrDefault(i.MessageId) ?? [])),
                    ],
                    next);
            })
            .RequireAuthorization(AdminPolicies.Feedback).WithName("ListFeedback")
            .WithSummary("Ratings of the caller's assistants, newest first (keyset; period, assistant, rating, outcome, has comment, review state, cited document); no content");

        feedback.MapGet("/summary", async (DateOnly? from, DateOnly? to, Guid? assistantId, AdminContext admin, CancellationToken ct) =>
            {
                var departments = admin.Departments().Select(d => d.Id);
                var totals = admin.Db.AssistantDailyTotals.AsNoTracking().Where(t => departments.Contains(t.DepartmentId));
                if (from is { } start)
                {
                    totals = totals.Where(t => t.Day >= start);
                }

                if (to is { } end)
                {
                    totals = totals.Where(t => t.Day <= end);
                }

                if (assistantId is { } assistant)
                {
                    totals = totals.Where(t => t.AssistantId == assistant);
                }

                var sums = await totals.GroupBy(t => 1).Select(g => new FeedbackTotals(
                        g.Sum(t => t.Messages), g.Sum(t => t.NotFound), g.Sum(t => t.ThumbsUp), g.Sum(t => t.ThumbsDown)))
                    .SingleOrDefaultAsync(ct) ?? new FeedbackTotals(0, 0, 0, 0);

                // Import health per Open WebUI client (chat §14.2–§14.3): only clients serving one of the caller's assistants.
                var assistants = admin.Db.Assistants.IgnoreQueryFilters().AsNoTracking().Where(a => departments.Contains(a.DepartmentId)).Select(a => a.Id);
                var served = admin.Db.AssistantAudiences.AsNoTracking()
                    .Where(x => x.Kind == AssistantAudienceKinds.Client && x.SubjectId != null && assistants.Contains(x.AssistantId)).Select(x => x.SubjectId!.Value);
                var imports = await (from s in admin.Db.FeedbackImportState.AsNoTracking()
                                     join c in admin.Db.Clients.AsNoTracking() on s.ClientId equals c.Id
                                     where admin.IsPlatformAdmin || served.Contains(s.ClientId)
                                     orderby c.Name
                                     select new FeedbackImportHealth(c.Id, c.Name, s.LastRunAt, s.LastOutcome, s.Unmapped, s.FailedRuns,
                                         s.FailedRuns >= FeedbackImportState.AlertAfterFailedRuns))
                    .ToListAsync(ct);
                return new FeedbackSummary(sums, imports);
            })
            .RequireAuthorization(AdminPolicies.Feedback).WithName("GetFeedbackSummary")
            .WithSummary("Totals of the period from AssistantDailyTotals (answers, not found, thumbs) and the Open WebUI import health");

        feedback.MapPut("/{id:guid}/review-state", async (Guid id, ReviewStateRequest request, AdminContext admin, CancellationToken ct) =>
                await admin.WriteAsync(async attempt =>
                {
                    if (!FeedbackReviewStates.All.Contains(request.State))
                    {
                        throw AdminFaults.Field("state", AdminErrorCodes.InvalidValue);
                    }

                    var departments = admin.Departments().Select(d => d.Id);
                    var department = await (from f in admin.Db.Feedback.AsNoTracking()
                                            join c in admin.Db.Conversations.AsNoTracking() on f.ConversationId equals c.Id
                                            join a in admin.Db.Assistants.IgnoreQueryFilters().AsNoTracking() on c.AssistantId equals a.Id
                                            where f.Id == id && departments.Contains(a.DepartmentId)
                                            select (Guid?)a.DepartmentId)
                        .SingleOrDefaultAsync(attempt) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                    var feedback = await admin.Db.Feedback.AsTracking().SingleAsync(f => f.Id == id, attempt);
                    var before = feedback.ReviewState;
                    feedback.ReviewState = request.State;
                    return new AdminChange<FeedbackReviewed>(new FeedbackReviewed(id, request.State), "review_state", AuditEntityTypes.Feedback, id,
                        new { reviewState = before }, new { reviewState = request.State })
                    {
                        DepartmentId = department, Classification = feedback.Classification,
                    };
                }, ct))
            .RequireAuthorization(AdminPolicies.FeedbackComments).WithName("SetFeedbackReviewState")
            .WithSummary("Mark a feedback item reviewed, dismissed or open again (Markera som hanterad)");

        return api;
    }

    /// <summary>The caller's departments' feedback with the list filters (period, assistant, rating, outcome, has comment, state, cited document).</summary>
    private static IQueryable<Feedback> Filtered(AdminContext admin, FeedbackQuery q)
    {
        var departments = admin.Departments().Select(d => d.Id);
        var assistants = admin.Db.Assistants.IgnoreQueryFilters().AsNoTracking().Where(a => departments.Contains(a.DepartmentId)).Select(a => a.Id);
        var conversations = admin.Db.Conversations.AsNoTracking().Where(c => assistants.Contains(c.AssistantId));
        if (q.AssistantId is { } assistant)
        {
            conversations = conversations.Where(c => c.AssistantId == assistant);
        }

        var rows = admin.Db.Feedback.AsNoTracking().Where(f => conversations.Select(c => c.Id).Contains(f.ConversationId));
        if (q.From is { } start)
        {
            rows = rows.Where(f => f.CreatedAt >= start);
        }

        if (q.To is { } end)
        {
            rows = rows.Where(f => f.CreatedAt < end);
        }

        if (q.Rating is { } rating)
        {
            rows = rows.Where(f => f.Rating == rating);
        }

        if (!string.IsNullOrWhiteSpace(q.Outcome))
        {
            var outcome = q.Outcome.Trim();
            rows = rows.Where(f => admin.Db.Messages.Any(m => m.Id == f.MessageId && m.Outcome == outcome));
        }

        if (q.HasComment is { } hasComment)
        {
            rows = rows.Where(f => (f.CommentCiphertext != null) == hasComment);
        }

        if (!string.IsNullOrWhiteSpace(q.State))
        {
            rows = rows.Where(f => f.ReviewState == q.State.Trim());
        }

        // §8.1, §8.8: the ratings whose message cited the document (the link from Feedback on documents).
        if (q.DocumentId is { } document)
        {
            rows = rows.Where(f => admin.Db.MessageSources.Any(s => s.MessageId == f.MessageId && s.Used && s.DocumentId == document));
        }

        return rows;
    }
}

public sealed record FeedbackQuery(
    DateTimeOffset? From, DateTimeOffset? To, Guid? AssistantId, short? Rating, string? Outcome, bool? HasComment, string? State, Guid? DocumentId, Guid? After,
    int? Limit);

public sealed record CitedDocument(Guid Id, string? Title);

/// <summary>A rating without content (admin design §8.1): <see cref="HasComment"/> says whether a comment is stored, never what it says.</summary>
public sealed record FeedbackItem(
    Guid Id, DateTimeOffset CreatedAt, short Rating, short? Score, string? ReasonCategory, string ReviewState, bool HasComment, string Source, Guid AssistantId,
    string AssistantName, int? AssistantVersion, Guid ClientId, string? ClientName, string Outcome, string? Coverage, string? Language,
    IReadOnlyList<CitedDocument> CitedDocuments);

public sealed record FeedbackTotals(int Answers, int NotFound, int ThumbsUp, int ThumbsDown);

/// <summary>One Open WebUI client's import health; <paramref name="Alert"/> from <see cref="FeedbackImportState.AlertAfterFailedRuns"/> failed runs in a row (chat §14.3).</summary>
public sealed record FeedbackImportHealth(Guid ClientId, string ClientName, DateTimeOffset? LastRunAt, string? LastOutcome, int Unmapped, int FailedRuns, bool Alert);

public sealed record FeedbackSummary(FeedbackTotals Totals, IReadOnlyList<FeedbackImportHealth> Imports);

public sealed record FeedbackReviewed(Guid Id, string ReviewState);

public sealed record ReviewStateRequest : AdminRequest
{
    [Required]
    public string State { get; init; } = "";
}
