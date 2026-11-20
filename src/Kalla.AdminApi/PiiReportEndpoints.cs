using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.Pipelines.Json;
using Kalla.Pipelines.Processing;
using Kalla.Pipelines.Sources;

namespace Kalla.AdminApi;

/// <summary>
/// The web editors' report (issue #175; Decisions table "Personal data on the municipality's own published site"):
/// for a source with <c>piiMode: report</c> in a <c>public</c> collection, first the documents whose newest flagged set is
/// held (personnummer, samordningsnummer), then those live although their live set is flagged, with the counts per type from <c>PiiCounts</c> (numbers only, never values) and the last
/// <c>pii.flagged</c> run item. Read-only; any other source answers <c>applies: false</c> with no documents.
/// </summary>
public static class PiiReportEndpoints
{
    public const int MaxDocuments = 500;

    /// <summary>The run-item reason of a flagged swap (the worker's <c>PiiLifecycle.FlaggedReason</c>).</summary>
    public const string FlaggedReason = "pii.flagged";

    public static RouteGroupBuilder MapPiiReport(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/sources/{id:guid}/pii-report", async (Guid id, AdminContext admin, CancellationToken ct) =>
            {
                var source = await SourceEndpoints.Scoped(admin).AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct)
                    ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                var level = await admin.Db.Collections.AsNoTracking().Where(c => c.Id == source.CollectionId).Select(c => c.Classification).SingleAsync(ct);
                PiiMode mode;
                using (var settings = JsonDocument.Parse(source.Settings))
                {
                    mode = SourceSettings.PiiModeOf(source.Type, settings.RootElement);
                }

                var modeName = mode == PiiMode.Report ? "report" : "hold";
                if (mode != PiiMode.Report || level != Classification.Public)
                {
                    return new PiiReport(false, modeName, level, [], false);
                }

                // Held first (#217 review): under piiMode report a page still holds when it has a personnummer or a
                // samordningsnummer, an error on the public site the editors fix. Then the live flagged pages.
                var held = await (
                        from cs in admin.Db.ChunkSets.AsNoTracking()
                        join d in admin.Db.Documents.AsNoTracking() on cs.DocumentId equals d.Id
                        where d.SourceId == id && d.DeletedAt == null && cs.State == ChunkSetStates.Held && cs.PiiFlagged
                        orderby d.Title, d.Id, cs.CreatedAt descending
                        select new Row(d.Id, d.Title, d.Url, d.DisplayName, cs.PiiCounts, cs.LiveAt, ChunkSetStates.Held, d.LiveChunkSetId != null))
                    .Take(MaxDocuments + 1).ToListAsync(ct);
                var live = await (
                        from d in admin.Db.Documents.AsNoTracking()
                        join cs in admin.Db.ChunkSets.AsNoTracking() on d.LiveChunkSetId equals cs.Id
                        where d.SourceId == id && d.DeletedAt == null && cs.State == ChunkSetStates.Live && cs.PiiFlagged
                        orderby d.Title, d.Id
                        select new Row(d.Id, d.Title, d.Url, d.DisplayName, cs.PiiCounts, cs.LiveAt, ChunkSetStates.Live, false))
                    .Take(MaxDocuments + 1).ToListAsync(ct);
                var rows = held.DistinctBy(r => r.Id).Concat(live).ToList(); // the newest held set per document
                var shown = rows.Take(MaxDocuments).ToList();
                var ids = shown.Select(r => (Guid?)r.Id).ToList();
                var flagged = await admin.Db.RunItems.AsNoTracking()
                    .Where(i => ids.Contains(i.DocumentId) && i.Reason == FlaggedReason)
                    .GroupBy(i => i.DocumentId)
                    .Select(g => new { DocumentId = g.Key, At = g.Max(i => i.CreatedAt) })
                    .ToDictionaryAsync(x => x.DocumentId!.Value, x => x.At, ct);
                var lastRuns = new Dictionary<Guid, Guid>();
                foreach (var (document, at) in flagged)
                {
                    lastRuns[document] = await admin.Db.RunItems.AsNoTracking()
                        .Where(i => i.DocumentId == document && i.Reason == FlaggedReason && i.CreatedAt == at).Select(i => i.RunId).FirstAsync(ct);
                }

                return new PiiReport(true, modeName, level, [.. shown.Select(r => new PiiReportDocument(
                    r.Id, r.Title, r.Url, r.DisplayName, r.State, r.PreviousVersionLive, Counts(r.PiiCounts, out var rules, out var fired), rules, fired, r.LiveAt,
                    flagged.TryGetValue(r.Id, out var at) ? at : null, lastRuns.TryGetValue(r.Id, out var run) ? run : null))], rows.Count > MaxDocuments);
            })
            .RequireAuthorization(AdminPolicies.Read).WithTags("Sources").WithName("GetPiiReport")
            .WithSummary("Held and flagged-but-live documents of a piiMode: report source (counts only)");

        return api;
    }

    private sealed record Row(Guid Id, string? Title, string? Url, string? DisplayName, string? PiiCounts, DateTimeOffset? LiveAt, string State,
        bool PreviousVersionLive);

    /// <summary>
    /// The non-zero counts per type (<see cref="PiiTypes"/>), how many flag rules fired and which types fired, as the worker
    /// stored them (<see cref="PiiCounts.TypesFired"/>, #217); numbers and type names only, never values. A stored name that
    /// is not a <see cref="PiiTypes"/> type is dropped.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Counts(string? json, out int rules, out IReadOnlyList<string> typesFired)
    {
        rules = 0;
        typesFired = [];
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, int>();
        }

        using var document = JsonDocument.Parse(json);
        var counts = ContractJson.Deserialize<PiiCounts>(document.RootElement);
        rules = counts.Rules;
        typesFired = [.. PiiTypes.All.Where(t => counts.TypesFired.Contains(t, StringComparer.Ordinal))];
        return PiiTypes.All.Where(t => counts.Count(t) > 0).ToDictionary(t => t, counts.Count, StringComparer.Ordinal);
    }
}

/// <summary>The report: whether it applies to the source (<c>piiMode: report</c> in a <c>public</c> collection) and its documents.</summary>
public sealed record PiiReport(bool Applies, string PiiMode, string CollectionClassification, IReadOnlyList<PiiReportDocument> Documents, bool Truncated);

/// <summary>
/// One flagged document: <see cref="State"/> <c>held</c> (the new version is not served until fixed or cleared; with
/// <see cref="PreviousVersionLive"/> the older version still is) or <c>live</c> (served with its contact details); counts
/// per type, the number of fired rules and the fired types (never values), and the last <c>pii.flagged</c> run item.
/// </summary>
public sealed record PiiReportDocument(
    Guid DocumentId,
    string? Title,
    string? Url,
    string? DisplayName,
    string State,
    bool PreviousVersionLive,
    IReadOnlyDictionary<string, int> Counts,
    int RulesFired,
    IReadOnlyList<string> TypesFired,
    DateTimeOffset? LiveAt,
    DateTimeOffset? LastFlaggedAt,
    Guid? LastFlaggedRunId);
