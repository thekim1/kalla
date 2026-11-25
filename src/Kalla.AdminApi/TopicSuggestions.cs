using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Persistence;
using Kalla.Pipelines.Processing;

namespace Kalla.AdminApi;

/// <summary>
/// The Topics tab's suggestions (admin design §4.5, §5.5.2; Decisions "Topics editor"): per source, the sections of its live
/// documents, the first path segment of their URLs (website) or the first folder of their display names (upload), or with
/// <c>under=</c> the next level under one section. Sections under <see cref="MinDocuments"/> documents are left out. A
/// section names the topics that already cover it through the worker's matcher (<see cref="PathRules"/>, processing §7).
/// The owning department only: the paths are URLs and file names (§4.8), so a department the collection is shared with
/// gets 404, and nothing here is logged.
/// </summary>
public static partial class TopicSuggestions
{
    /// <summary>A section with fewer live documents is not suggested.</summary>
    public const int MinDocuments = 5;

    /// <summary>The longest tag (processing §7).</summary>
    private const int MaxTagLength = 50;

    public static RouteGroupBuilder MapTopicSuggestions(this RouteGroupBuilder collections)
    {
        ArgumentNullException.ThrowIfNull(collections);
        collections.MapGet("/{id:guid}/topic-suggestions", async (Guid id, Guid? sourceId, string? under, AdminContext admin, CancellationToken ct) =>
            {
                // Scoped to the caller's own departments: a shared collection is 404 here, as its sources are (§3.2).
                var collection = await CollectionEndpoints.Scoped(admin).AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct)
                    ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                if (under is not null && sourceId is null)
                {
                    throw AdminFaults.Field("sourceId", AdminErrorCodes.InvalidValue);
                }

                var sources = await (from s in admin.Db.Sources.AsNoTracking()
                                     where s.CollectionId == collection.Id && s.DeletedAt == null && (sourceId == null || s.Id == sourceId)
                                     orderby s.Name
                                     select new SourceRow(s.Id, s.Name, s.Type, s.PipelineId)).ToListAsync(ct);
                if (sourceId is not null && sources.Count == 0)
                {
                    throw AdminFaults.Of(AdminErrorCodes.NotFound);
                }

                var rules = await PathRulesAsync(admin.Db, sources, ct);
                var blocks = new List<SourceSuggestionsDto>();
                foreach (var source in sources)
                {
                    var upload = source.Type == SourceTypes.Upload;
                    var parent = under is null ? Root(upload) : Parent(under, upload) ?? throw AdminFaults.Field("under", AdminErrorCodes.InvalidValue);
                    var sections = await SectionsAsync(admin.Db, source.Id, upload, parent, ct);
                    blocks.Add(new SourceSuggestionsDto(source.Id, source.Name, upload ? "upload" : "website", under is null ? null : parent,
                        [.. sections.Select(s => Suggest(s, upload, rules.GetValueOrDefault(source.Id) ?? []))]));
                }

                return new TopicSuggestionsDto(collection.Id, blocks);
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("GetTopicSuggestions")
            .WithSummary("The sections of a collection's sources' live documents, as topics to pick (owning department only)");
        return collections;
    }

    /// <summary>The level-one parent: a URL path starts at <c>/</c>, an upload's display name is relative.</summary>
    private static string Root(bool upload) => upload ? "" : "/";

    /// <summary>
    /// <paramref name="under"/> as a section path (<c>/bygga-bo-och-miljo/</c>, <c>HR/</c>): it ends in <c>/</c>, a website's starts with
    /// one, an upload's does not (a leading one is dropped), and it has no empty segment; null when it is not one.
    /// </summary>
    private static string? Parent(string under, bool upload)
    {
        var path = under.Trim();
        if (upload)
        {
            path = path.TrimStart('/');
        }
        else if (!path.StartsWith('/'))
        {
            return null;
        }

        if (!path.EndsWith('/') || path.Length < 2 || path.Length > 2000)
        {
            return null;
        }

        var segments = upload ? path[..^1] : path[1..^1];
        return segments.Length > 0 && !segments.Split('/').Any(string.IsNullOrEmpty) ? path : null;
    }

    /// <summary>
    /// The sections one level below <paramref name="parent"/> with their live documents (those under the section's path, as a
    /// <c>path</c> rule ending in <c>/</c> matches them) and the title of the section's own page. One grouping query over
    /// <c>content."Documents"</c> on the raw path; the paths are decoded here, so <c>%C3%A5</c> and <c>å</c> are one section.
    /// </summary>
    private static async Task<List<Section>> SectionsAsync(RagDbContext db, Guid sourceId, bool upload, string parent, CancellationToken ct)
    {
        // The section's segments as string_to_array gives them: a URL path has a leading empty one (`/a/b` is '', a, b), a
        // display name has none (`HR/a` is HR, a); either way one more than the parent's slashes. A display name is taken
        // as stored, as the matcher takes it: `/HR/a` is in no folder section, since an `HR/` rule does not match it.
        var depth = parent.Count(c => c == '/') + 1;
        var rows = await db.Database.SqlQuery<SectionRow>($"""
            WITH docs AS (
                SELECT d."Title",
                       substring(d."Url" from '^[A-Za-z][A-Za-z0-9+.-]*://([^/?#:]*)') AS host,
                       string_to_array(CASE WHEN {upload} THEN d."DisplayName"
                                            ELSE regexp_replace(regexp_replace(d."Url", '^[A-Za-z][A-Za-z0-9+.-]*://[^/?#]*', ''), '[?#].*$', '') END, '/') AS segs
                FROM content."Documents" d
                WHERE d."SourceId" = {sourceId} AND d."DeletedAt" IS NULL AND d."LiveChunkSetId" IS NOT NULL
            )
            SELECT array_to_string(segs[1:{depth}], '/') AS "Prefix",
                   array_remove(array_agg(DISTINCT host), NULL) AS "Hosts",
                   (count(*) FILTER (WHERE cardinality(segs) > {depth}))::int AS "Documents",
                   min("Title") FILTER (WHERE cardinality(segs) = {depth} OR (cardinality(segs) = {depth} + 1 AND segs[{depth} + 1] = '')) AS "Title"
            FROM docs
            WHERE cardinality(segs) >= {depth} AND segs[{depth}] <> ''
            GROUP BY 1
            """).ToListAsync(ct);

        return [.. rows
            .Select(r => r with { Prefix = upload ? r.Prefix : Unescape(r.Prefix) })
            .Where(r => (r.Prefix + "/").StartsWith(parent, StringComparison.Ordinal))
            .GroupBy(r => r.Prefix + "/", StringComparer.Ordinal)
            .Select(g => new Section(g.Key, [.. g.SelectMany(r => r.Hosts).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], g.Sum(r => r.Documents), upload ? null : g.Select(r => r.Title).FirstOrDefault(t => t is not null)))
            .Where(s => s.Documents >= MinDocuments)
            .OrderByDescending(s => s.Documents).ThenBy(s => s.Path, StringComparer.Ordinal)];
    }

    private static string Unescape(string path)
    {
        try
        {
            return Uri.UnescapeDataString(path);
        }
        catch (UriFormatException)
        {
            return path;
        }
    }

    private static SectionDto Suggest(Section section, bool upload, IReadOnlyList<PathRule> rules)
    {
        // What the worker would see for the section's own page: a URL on each of its hosts (a rule that names one host covers
        // the section on that host, #1075), or the folder as a display name.
        IEnumerable<PathRuleSubject> subjects = upload
            ? [new PathRuleSubject("") { DisplayName = section.Path }]
            : (section.Hosts.Count > 0 ? section.Hosts : ["localhost"])
                .Select(host => new PathRuleSubject("") { Url = Uri.TryCreate($"https://{host}{section.Path}", UriKind.Absolute, out var url) ? url : null });
        var coveredBy = subjects.SelectMany(subject => rules.Where(r => r.On == "path").Select(r => PathRules.Match(r, subject))).OfType<PathRuleMatch>()
            .SelectMany(m => m.Tags).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        // The hosts the section was found on: the Topics tab writes one host-first rule per host (Decisions "Topics editor", #1082).
        return new SectionDto(section.Path, section.Documents, section.Title, SuggestedTag(section.Path), coveredBy, upload ? [] : section.Hosts);
    }

    /// <summary>
    /// The section's last segment as a tag (processing §7): lower case, NFC, every run of other characters one <c>-</c>, cut to
    /// <see cref="MaxTagLength"/>; null when nothing of it is left.
    /// </summary>
    public static string? SuggestedTag(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var segment = path.TrimEnd('/');
        segment = segment[(segment.LastIndexOf('/') + 1)..];
        var tag = NotTagCharacters().Replace(segment.ToLower(CultureInfo.InvariantCulture).Normalize(NormalizationForm.FormC), "-").Trim('-');
        tag = tag.Length > MaxTagLength ? tag[..MaxTagLength].TrimEnd('-') : tag;
        return tag.Length == 0 ? null : PathRules.NormaliseTag(tag);
    }

    /// <summary>The published <c>pathRules</c> of each source's pipeline.</summary>
    private static async Task<Dictionary<Guid, IReadOnlyList<PathRule>>> PathRulesAsync(RagDbContext db, List<SourceRow> sources, CancellationToken ct)
    {
        var pipelineIds = sources.Select(s => s.PipelineId).Distinct().ToList();
        var definitions = await (from p in db.Pipelines.AsNoTracking()
                                 join v in db.PipelineVersions.AsNoTracking() on p.PublishedVersionId equals v.Id
                                 where pipelineIds.Contains(p.Id)
                                 select new { p.Id, v.Definition }).ToDictionaryAsync(p => p.Id, p => p.Definition, ct);
        return sources.ToDictionary(s => s.Id, s => definitions.TryGetValue(s.PipelineId, out var definition) ? Read(definition) : (IReadOnlyList<PathRule>)[]);
    }

    /// <summary>The rules of a pipeline definition's <c>params.pathRules</c>; a definition that cannot be read has none.</summary>
    public static IReadOnlyList<PathRule> Read(string definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        try
        {
            using var document = JsonDocument.Parse(definition);
            return document.RootElement.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object &&
                parameters.TryGetProperty("pathRules", out var pathRules)
                ? PathRules.Read(pathRules)
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NotTagCharacters();

    private sealed record SourceRow(Guid Id, string Name, string Type, Guid PipelineId);

    private sealed record Section(string Path, IReadOnlyList<string> Hosts, int Documents, string? Title);

    private sealed record SectionRow(string Prefix, string[] Hosts, int Documents, string? Title);
}

/// <summary>The suggestions per source of a collection (admin design §4.5).</summary>
public sealed record TopicSuggestionsDto(Guid CollectionId, IReadOnlyList<SourceSuggestionsDto> Sources);

/// <summary>
/// One source's sections: <see cref="Kind"/> <c>website</c> (URL paths) or <c>upload</c> (folders); <see cref="Under"/> the
/// section whose next level this is, null for the first level.
/// </summary>
public sealed record SourceSuggestionsDto(Guid SourceId, string SourceName, string Kind, string? Under, IReadOnlyList<SectionDto> Sections);

/// <summary>
/// A section to pick: its <see cref="Path"/> (<c>/bygga-bo-och-miljo/</c>, <c>HR/</c>), the live documents under it, the title of its
/// own page when crawled, the tag to propose (null when the segment has no letter or digit), and the topics whose <c>path</c>
/// rules already cover it.
/// </summary>
/// <summary><see cref="Hosts"/>: a website section's hosts, sorted (the host-first rules to write, #1082); none for an upload's folder.</summary>
public sealed record SectionDto(string Path, int Documents, string? Title, string? SuggestedTag, IReadOnlyList<string> CoveredBy, IReadOnlyList<string> Hosts);
