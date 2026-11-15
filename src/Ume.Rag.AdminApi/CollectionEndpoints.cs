using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Pipelines.Json;
using Ume.Rag.Retrieval;

namespace Ume.Rag.AdminApi;

/// <summary>
/// Collections (admin design §4.5 "Collections and retrieval", P1a item 14, P1b item 35): list (<c>view=mine|shared|all</c>),
/// create (with contact and the owner team defaulted), read with <c>usedBy</c>, change, delete, publish and retrieval
/// settings (V16, with an advisory evaluation run). Department-scoped: a collection outside the caller's departments is
/// 404; one shared with them is listed as the shared summary only (<see cref="CollectionSharing"/>).
/// </summary>
public static class CollectionEndpoints
{
    public static RouteGroupBuilder MapCollections(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var collections = api.MapGroup("/collections").WithTags("Collections");

        collections.MapGet("", async ([AsParameters] PageQuery page, string? view, AdminContext admin, CancellationToken ct) =>
            {
                var departments = admin.Departments().Select(d => d.Id);
                var query = (view ?? "mine") switch
                {
                    "mine" => Scoped(admin),
                    "shared" => CollectionSharing.SharedWith(admin.Db, departments),
                    "all" => CollectionSharing.Visible(admin),
                    _ => throw AdminFaults.Field("view", AdminErrorCodes.InvalidValue),
                };
                if (!string.IsNullOrWhiteSpace(page.Q))
                {
                    var q = $"%{Like.Escape(page.Q.Trim())}%";
                    query = query.Where(c => EF.Functions.ILike(c.Name, q, Like.EscapeCharacter) || EF.Functions.ILike(c.Slug, q, Like.EscapeCharacter));
                }

                var total = await query.CountAsync(ct);
                var items = await SummariesAsync(admin, query.OrderBy(c => c.Name).ThenBy(c => c.Id).Skip(page.Skip).Take(page.Take), ct);
                return new PageDto<CollectionSummary>(items, total);
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("ListCollections")
            .WithSummary("Collections of the caller's departments (view=mine), those shared with them (shared) or both (all)");

        collections.MapGet("/{id:guid}", async (Guid id, AdminContext admin, CancellationToken ct) =>
            {
                if (await Scoped(admin).AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) is { } collection)
                {
                    return Results.Ok(await DetailAsync(admin, collection, ct));
                }

                // A department it is shared with reads it as the shared summary (admin design §3.2, §5.5.2: read-only Overview and Topics).
                var departments = admin.Departments().Select(d => d.Id);
                var shared = await SummariesAsync(admin, CollectionSharing.SharedWith(admin.Db, departments).Where(c => c.Id == id), ct);
                return shared.Count == 1 ? Results.Ok(shared[0]) : throw AdminFaults.Of(AdminErrorCodes.NotFound);
            })
            .RequireAuthorization(AdminPolicies.Read).WithName("GetCollection")
            .WithSummary("One collection with the assistants that link it; for a department it is shared with, the shared summary");

        collections.MapPost("", async (CollectionCreateRequest request, AdminContext admin, GatewayModelCheck models, CancellationToken ct) =>
            {
                // V39's list as the key is fetched before the write, outside the config version lock (admin design §4.4, #696).
                await models.WarmKeyAsync(ResidencyCeiling.ForLevel(request.Classification), request.GatewayKeyId, request.DepartmentId, ct);
                var created = await admin.WriteAsync(async attempt =>
                {
                    await CheckDepartmentAsync(admin, request.DepartmentId, request.OwnerTeamId, attempt);
                    // Teams are hidden in create forms: the creator's first team in the department (Decisions "Organisation").
                    var ownerTeam = request.OwnerTeamId ?? await admin.DefaultOwnerTeamAsync(request.DepartmentId, attempt);
                    var profile = await admin.Db.EmbeddingProfiles.AsNoTracking().Where(p => p.IsDefault && p.State == EmbeddingProfileStates.Active)
                        .Select(p => (Guid?)p.Id).FirstOrDefaultAsync(attempt) ?? throw AdminFaults.Of(AdminErrorCodes.NoEmbeddingProfile);
                    var slug = string.IsNullOrWhiteSpace(request.Slug) ? Slugs.From(request.Name) : request.Slug;
                    // Slugs are never reused, also after a delete: API clients may still send an old one (admin design §4.5).
                    if (await admin.Db.Collections.AsNoTracking().AnyAsync(c => c.Slug == slug, attempt))
                    {
                        throw AdminFaults.Field("slug", AdminErrorCodes.SlugTaken, new Dictionary<string, string>
                        {
                            ["slug"] = slug, ["suggestion"] = await Slugs.FreeAsync(admin.Db.Collections.AsNoTracking().Select(c => c.Slug), slug, attempt),
                        });
                    }

                    var collection = new Collection
                    {
                        Slug = slug,
                        Name = request.Name.Trim(),
                        DepartmentId = request.DepartmentId,
                        OwnerTeamId = ownerTeam,
                        Classification = request.Classification,
                        EmbeddingProfileId = profile,
                        ContactName = Contact(request.ContactName),
                        ContactEmail = Contact(request.ContactEmail),
                        GatewayKeyId = request.GatewayKeyId,
                        UpdatedAt = admin.Now,
                        UpdatedBy = admin.Actor,
                    };
                    await CheckGatewayKeyAsync(admin, collection, models, attempt);
                    admin.Db.Collections.Add(collection);
                    return new AdminChange<Collection>(collection, "create", AuditEntityTypes.Collection, collection.Id, null, CollectionAudit.Of(collection))
                    {
                        DepartmentId = collection.DepartmentId, Classification = collection.Classification,
                    };
                }, ct);
                return Results.Created($"/api/collections/{created.Id}", await DetailAsync(admin, created, ct));
            })
            .RequireAuthorization(AdminPolicies.Edit).WithName("CreateCollection").WithSummary("Create a collection on the default embedding profile");

        collections.MapPut("/{id:guid}", async (Guid id, CollectionUpdateRequest request, AdminContext admin, GatewayModelCheck models, CancellationToken ct) =>
            {
                if (await Scoped(admin).AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) is { } current
                    && (request.GatewayKeyId != current.GatewayKeyId || request.Classification != current.Classification))
                {
                    await models.WarmKeyAsync(ResidencyCeiling.ForLevel(request.Classification), request.GatewayKeyId, current.DepartmentId, ct); // as on create
                }

                var saved = await admin.WriteAsync(async attempt =>
                {
                    var collection = await LoadAsync(admin, id, attempt);
                    admin.ExpectVersion(collection, request.Version);
                    await CheckDepartmentAsync(admin, collection.DepartmentId, request.OwnerTeamId, attempt);
                    var before = CollectionAudit.Of(collection);
                    if (request.Classification != collection.Classification)
                    {
                        await CheckLevelChangeAsync(admin, collection, request.Classification, attempt);
                    }

                    collection.Name = request.Name.Trim();
                    collection.OwnerTeamId = request.OwnerTeamId;
                    collection.Classification = request.Classification;
                    collection.ContactName = Contact(request.ContactName);
                    collection.ContactEmail = Contact(request.ContactEmail);
                    var recheckKey = request.GatewayKeyId != collection.GatewayKeyId || before.Classification != collection.Classification;
                    collection.GatewayKeyId = request.GatewayKeyId;
                    if (recheckKey)
                    {
                        await CheckGatewayKeyAsync(admin, collection, models, attempt);
                    }

                    collection.UpdatedAt = admin.Now;
                    collection.UpdatedBy = admin.Actor;
                    return new AdminChange<Collection>(collection, "update", AuditEntityTypes.Collection, collection.Id, before, CollectionAudit.Of(collection))
                    {
                        DepartmentId = collection.DepartmentId, Classification = collection.Classification,
                    };
                }, ct);
                return await DetailAsync(admin, saved, ct);
            })
            .RequireAuthorization(AdminPolicies.Edit).WithName("UpdateCollection").WithSummary("Change a collection's name, owner team, level (V2, V3), contact or gateway key (V39)");

        collections.MapDelete("/{id:guid}", async (Guid id, [Microsoft.AspNetCore.Mvc.FromQuery] string? version, AdminContext admin, CancellationToken ct) =>
            {
                await admin.WriteAsync(async attempt =>
                {
                    var collection = await LoadAsync(admin, id, attempt);
                    admin.ExpectVersion(collection, version);
                    var users = await UsedByAsync(admin, id, attempt);
                    var sources = await admin.Db.Sources.AsNoTracking().Where(s => s.CollectionId == id && s.DeletedAt == null).Select(s => s.Name).ToListAsync(attempt);
                    if (users.Count > 0 || sources.Count > 0)
                    {
                        throw AdminFaults.Of(AdminErrorCodes.CollectionInUse, extensions: new Dictionary<string, object?>
                        {
                            ["affected"] = users.Select(u => u.Name).Concat(sources).ToList(),
                            ["usedBy"] = users,
                            ["sources"] = sources,
                        });
                    }

                    var before = CollectionAudit.Of(collection);
                    collection.DeletedAt = admin.Now;
                    collection.UpdatedAt = admin.Now;
                    collection.UpdatedBy = admin.Actor;
                    return new AdminChange<bool>(true, "delete", AuditEntityTypes.Collection, collection.Id, before, null)
                    {
                        DepartmentId = collection.DepartmentId, Classification = collection.Classification,
                    };
                }, ct);
                return Results.NoContent();
            })
            .RequireAuthorization(AdminPolicies.Manage).WithName("DeleteCollection").WithSummary("Delete a collection no assistant and no source uses");

        collections.MapPost("/{id:guid}/publish", async (Guid id, VersionRequest request, AdminContext admin, CancellationToken ct) =>
            {
                var saved = await admin.WriteAsync(async attempt =>
                {
                    var collection = await LoadAsync(admin, id, attempt);
                    admin.ExpectVersion(collection, request.Version);
                    var before = CollectionAudit.Of(collection);
                    collection.PublishedAt ??= admin.Now;
                    collection.UpdatedAt = admin.Now;
                    collection.UpdatedBy = admin.Actor;
                    return new AdminChange<Collection>(collection, "publish", AuditEntityTypes.Collection, collection.Id, before, CollectionAudit.Of(collection))
                    {
                        DepartmentId = collection.DepartmentId, Classification = collection.Classification,
                    };
                }, ct);
                return await DetailAsync(admin, saved, ct);
            })
            .RequireAuthorization(AdminPolicies.Manage).WithName("PublishCollection").WithSummary("Publish a collection (visible to retrieval); never blocked");

        collections.MapPut("/{id:guid}/retrieval-settings", async (Guid id, RetrievalSettingsRequest request, AdminContext admin, CancellationToken ct) =>
            {
                var settings = RetrievalSettingsRules.Normalize(request.Settings);
                var platform = await admin.Db.Settings.AsNoTracking().Select(s => s.Retrieval).SingleOrDefaultAsync(ct);
                var saved = await admin.WriteAsync(async attempt =>
                {
                    var collection = await LoadAsync(admin, id, attempt);
                    admin.ExpectVersion(collection, request.Version);
                    RetrievalSettingsRules.CheckResolves(platform, settings);
                    var before = CollectionAudit.Of(collection);
                    collection.RetrievalSettings = settings;
                    collection.UpdatedAt = admin.Now;
                    collection.UpdatedBy = admin.Actor;
                    // The advisory evaluation run (§4.7): only when the collection has a set to run.
                    var hasSet = await admin.Db.EvaluationSets.AsNoTracking().AnyAsync(s => s.CollectionId == id, attempt);
                    return new AdminChange<Collection>(collection, "retrieval-settings", AuditEntityTypes.Collection, collection.Id, before,
                        CollectionAudit.Of(collection))
                    {
                        DepartmentId = collection.DepartmentId,
                        Classification = collection.Classification,
                        Jobs = hasSet ? [EvaluationJob(collection)] : [],
                    };
                }, ct);
                return await DetailAsync(admin, saved, ct);
            })
            .RequireAuthorization(AdminPolicies.Edit).WithName("SetRetrievalSettings").WithSummary("Save retrieval settings (V16); starts an advisory evaluation run");

        return api;
    }

    /// <summary>The detail: the collection, its share list (when <c>selected</c>), its live document count and <c>usedBy</c>.</summary>
    public static async Task<CollectionDto> DetailAsync(AdminContext admin, Collection collection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(collection);
        var shares = collection.SharedWith == CollectionSharingLevels.Selected
            ? await admin.Db.CollectionShares.AsNoTracking().Where(s => s.CollectionId == collection.Id).Select(s => s.DepartmentId).OrderBy(d => d).ToListAsync(ct)
            : [];
        var documents = (await LiveDocumentCountsAsync(admin, [collection.Id], ct)).GetValueOrDefault(collection.Id);
        return CollectionDto.Of(collection, shares, documents, await UsedByAsync(admin, collection.Id, ct));
    }

    /// <summary>
    /// The one document count of a knowledge base (list and Overview, #374): its live documents, those not deleted that have
    /// a live chunk set, so they can be found.
    /// </summary>
    private static async Task<Dictionary<Guid, int>> LiveDocumentCountsAsync(AdminContext admin, Guid[] ids, CancellationToken ct) =>
        await (from d in admin.Db.Documents.AsNoTracking()
               join src in admin.Db.Sources.AsNoTracking() on d.SourceId equals src.Id
               where d.DeletedAt == null && d.LiveChunkSetId != null && ids.Contains(src.CollectionId)
               group d by src.CollectionId into g
               select new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);

    /// <summary>
    /// The list rows (the shared summary) of <paramref name="query"/>, with owner names, live document counts and topics; the
    /// caller's own rows also with their last fetch and quality (admin design §5.5.2). A shared row never shows its sources or
    /// evaluation (§4.5), so both are null there.
    /// </summary>
    private static async Task<List<CollectionSummary>> SummariesAsync(AdminContext admin, IQueryable<Collection> query, CancellationToken ct)
    {
        var rows = await query.Select(c => new { c.Id, c.Slug, c.Name, c.DepartmentId, c.Classification, c.PublishedAt, c.SharedWith, c.ContactName, c.ContactEmail })
            .ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToArray();
        var owners = rows.Select(r => r.DepartmentId).Distinct().ToList();
        var own = (await admin.Departments().Select(d => d.Id).ToListAsync(ct)).ToHashSet();
        var names = await admin.Db.Departments.AsNoTracking().Where(d => owners.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Name, ct);
        var documents = await LiveDocumentCountsAsync(admin, ids, ct);
        var topics = await CollectionSharing.TopicCountsAsync(admin.Db, ids, ct);
        var ownIds = rows.Where(r => own.Contains(r.DepartmentId)).Select(r => r.Id).ToArray();
        var latest = await LatestAsync(admin, ownIds, ct);
        return [.. rows.Select(r => new CollectionSummary(r.Id, r.Slug, r.Name, r.DepartmentId, names.GetValueOrDefault(r.DepartmentId) ?? "", r.Classification,
            r.PublishedAt, r.SharedWith, !own.Contains(r.DepartmentId), r.ContactName, r.ContactEmail, documents.GetValueOrDefault(r.Id),
            [.. topics.Where(t => t.CollectionId == r.Id).Select(t => t.Tag)], latest.GetValueOrDefault(r.Id).LastRun, latest.GetValueOrDefault(r.Id).LatestEvaluation))];
    }

    /// <summary>
    /// In one query for all <paramref name="ids"/>: the latest run of any of a collection's sources that are not deleted, and
    /// its latest finished evaluation of the saved settings (not a candidate run), the hybrid variant first as on the Overview.
    /// </summary>
    private static async Task<Dictionary<Guid, (CollectionLastRun? LastRun, CollectionEvaluation? LatestEvaluation)>> LatestAsync(
        AdminContext admin, Guid[] ids, CancellationToken ct)
    {
        if (ids.Length == 0)
        {
            return [];
        }

        var db = admin.Db;
        var rows = await db.Collections.AsNoTracking().Where(c => ids.Contains(c.Id))
            .Select(c => new
            {
                c.Id,
                LastRun = (from r in db.Runs
                           join s in db.Sources on r.SourceId equals s.Id
                           where s.CollectionId == c.Id && s.DeletedAt == null
                           orderby r.StartedAt descending, r.Id descending
                           select new CollectionLastRun(r.Id, r.SourceId, r.State, r.StartedAt, r.FinishedAt)).FirstOrDefault(),
                LatestEvaluation = (from r in db.EvaluationRuns
                                    join set in db.EvaluationSets on r.SetId equals set.Id
                                    where set.CollectionId == c.Id && r.FinishedAt != null && !r.Candidate && r.RecallAtK != null
                                    orderby r.Variant == EvaluationVariants.Hybrid descending, r.FinishedAt descending, r.Id descending
                                    select new CollectionEvaluation(r.RecallAtK!.Value, r.FinishedAt!.Value)).FirstOrDefault(),
            })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => (r.LastRun, r.LatestEvaluation));
    }

    private static string? Contact(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The caller's collections that are not deleted (department scope, admin design §3.2).</summary>
    public static IQueryable<Collection> Scoped(AdminContext admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        var departments = admin.Departments().Select(d => d.Id);
        return admin.Db.Collections.Where(c => c.DeletedAt == null && departments.Contains(c.DepartmentId));
    }

    /// <summary>A tracked collection in scope, or 404.</summary>
    public static async Task<Collection> LoadAsync(AdminContext admin, Guid id, CancellationToken ct) =>
        await Scoped(admin).SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);

    /// <summary>
    /// The assistants whose published version or draft links the collection (admin design §4.5), of every department (a
    /// shared collection's owner sees who uses it). Archived ones too, with their state: they still block deletion and can
    /// be restored (#374).
    /// </summary>
    public static async Task<List<CollectionUser>> UsedByAsync(AdminContext admin, Guid id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        var rows = await (from v in admin.Db.AssistantVersionsLinking([id]).AsNoTracking()
                          join a in admin.Db.Assistants.AsNoTracking() on v.AssistantId equals a.Id
                          join d in admin.Db.Departments.AsNoTracking() on a.DepartmentId equals d.Id
                          where a.PublishedVersionId == v.Id || a.DraftVersionId == v.Id
                          select new { a.Id, a.Name, a.Classification, a.State, Published = a.PublishedVersionId == v.Id, a.DepartmentId, Department = d.Name, v.CollectionLinks })
            .ToListAsync(ct);
        // The topics of the published link, else the draft's (empty = the whole collection).
        return [.. rows.GroupBy(r => r.Id).Select(g => new CollectionUser(g.Key, g.First().Name, g.First().Classification, g.First().State,
            g.Any(r => r.Published), g.Any(r => !r.Published), g.First().DepartmentId, g.First().Department,
            g.OrderByDescending(r => r.Published).First().CollectionLinks.FirstOrDefault(l => l.CollectionId == id)?.Tags ?? []))
            .OrderBy(u => u.Name, StringComparer.Ordinal)];
    }

    /// <summary>The department must be one of the caller's; the owner team, when given, one of that department's.</summary>
    /// <summary>V39 (admin design §4.6): the collection's gateway key against its models at its level's ceiling.</summary>
    private static async Task CheckGatewayKeyAsync(AdminContext admin, Collection collection, GatewayModelCheck models, CancellationToken ct) =>
        AssistantEndpoints.Refuse(await GatewayKeyRules.CheckAsync(admin, collection.GatewayKeyId, collection.DepartmentId,
            ResidencyCeiling.ForLevel(collection.Classification), await GatewayKeyRules.CollectionModelsAsync(admin, collection, ct), ct, models));

    private static async Task CheckDepartmentAsync(AdminContext admin, Guid departmentId, Guid? ownerTeamId, CancellationToken ct)
    {
        if (!await admin.Departments().AnyAsync(d => d.Id == departmentId, ct))
        {
            throw AdminFaults.Field("departmentId", AdminErrorCodes.NotInScope);
        }

        if (ownerTeamId is { } team && !await admin.Db.Teams.AnyAsync(t => t.Id == team && t.DepartmentId == departmentId, ct))
        {
            throw AdminFaults.Field("ownerTeamId", AdminErrorCodes.InvalidValue);
        }
    }

    /// <summary>
    /// V2: lowering below a live source's level is refused (<c>collection_level_conflict</c>); V3: raising above the level of
    /// an assistant whose published version links it is refused (<c>assistant_below_collection</c>). Both list every
    /// affected object in <c>affected</c>.
    /// </summary>
    private static async Task CheckLevelChangeAsync(AdminContext admin, Collection collection, string level, CancellationToken ct)
    {
        var rank = Levels.Rank(level);
        var sources = (await admin.Db.Sources.AsNoTracking().Where(s => s.CollectionId == collection.Id && s.DeletedAt == null)
                .Select(s => new { s.Name, s.Classification }).ToListAsync(ct))
            .Where(s => Levels.Rank(s.Classification) > rank).Select(s => s.Name).ToList();
        if (sources.Count > 0)
        {
            throw AdminFaults.Of(AdminErrorCodes.CollectionLevelConflict, new Dictionary<string, string> { ["level"] = level },
                new Dictionary<string, object?> { ["affected"] = sources });
        }

        var assistants = (await UsedByAsync(admin, collection.Id, ct))
            .Where(u => u.Published && Levels.Rank(u.Classification) < rank).Select(u => u.Name).ToList();
        if (assistants.Count > 0)
        {
            throw AdminFaults.Of(AdminErrorCodes.AssistantBelowCollection, new Dictionary<string, string> { ["collection"] = collection.Name, ["level"] = level },
                new Dictionary<string, object?> { ["affected"] = assistants });
        }
    }

    private static JobRequest EvaluationJob(Collection collection) =>
        new(JobClasses.Eval, collection.DepartmentId, collection.Classification,
            JsonSerializer.Serialize(new EvalOnDemandPayload { CollectionId = collection.Id }, ContractJson.Options))
        {
            DedupKey = JobDedupKeys.Eval(collection.Id),
            TeamId = collection.OwnerTeamId,
        };
}

/// <summary>V16 (admin design §4.6): the keys of knowledge design §12.1 and their ranges, as the retrieval reads them.</summary>
public static class RetrievalSettingsRules
{
    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    {
        "vectorWeight", "keywordWeight", "candidates", "maxChunksPerDocument", "recency", "coverageMaxDistance", "coverageMinKeywordScore", "expandMaxTokens",
    };

    /// <summary>The <c>website</c> template's age decay (weakness review §3): PDFs, half-life 730 days, weight 0.15.</summary>
    public static readonly JsonObject WebsiteRecency = new() { ["halfLifeDays"] = 730, ["weight"] = 0.15, ["kinds"] = new JsonArray("pdf") };

    /// <summary>
    /// <paramref name="retrievalSettings"/> with <see cref="WebsiteRecency"/>, or null when it already has a <c>recency</c> key
    /// (an explicit <c>null</c> keeps decay off: an editor's choice).
    /// </summary>
    public static string? WithWebsiteRecency(string retrievalSettings)
    {
        var json = JsonNode.Parse(string.IsNullOrWhiteSpace(retrievalSettings) ? "{}" : retrievalSettings) as JsonObject ?? [];
        if (json.ContainsKey("recency"))
        {
            return null;
        }

        json["recency"] = WebsiteRecency.DeepClone();
        return json.ToJsonString();
    }

    /// <summary>A <c>Chunks.DocumentType</c> value (processing design §6.5: <c>page</c>, <c>pdf</c>, … or a metadata rule's own).</summary>
    private static readonly System.Text.RegularExpressions.Regex DocumentType = new("^[a-z][a-z0-9-]{0,29}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The settings object checked against V16 and written back compactly; refusals are 400 <c>retrieval_settings_invalid</c> per field.</summary>
    public static string Normalize(JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("settings", "an object");
        }

        var json = JsonNode.Parse(settings.GetRawText())!.AsObject();
        foreach (var (key, value) in json)
        {
            switch (key)
            {
                case "vectorWeight" or "keywordWeight":
                    Range(key, value, 0, 5);
                    break;
                case "candidates":
                    Range(key, value, 20, 200, integer: true);
                    break;
                case "maxChunksPerDocument":
                    Range(key, value, 1, 10, integer: true);
                    break;
                case "coverageMaxDistance":
                    Range(key, value, 0.05, 1.0);
                    break;
                case "coverageMinKeywordScore":
                    if (value is not null && (Number(key, value) is var score && score <= 0))
                    {
                        throw Invalid(key, "null or greater than 0");
                    }

                    break;
                case "expandMaxTokens":
                    Range(key, value, 0, 4000, integer: true);
                    break;
                case "recency":
                    Recency(value);
                    break;
                default:
                    if (!Keys.Contains(key))
                    {
                        throw Invalid(key, "a retrieval setting (" + string.Join(", ", Keys) + ")");
                    }

                    break;
            }
        }

        return json.ToJsonString();
    }

    /// <summary>The saved value must resolve as the knowledge role resolves it (over <c>Settings.Retrieval</c>).</summary>
    public static void CheckResolves(string? platformJson, string collectionJson)
    {
        try
        {
            RetrievalSettingsResolver.Collection(RetrievalSettingsResolver.Platform(platformJson), collectionJson);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or JsonException)
        {
            throw Invalid("settings", "valid with the platform's retrieval defaults");
        }
    }

    private static void Recency(JsonNode? value)
    {
        if (value is null)
        {
            return;
        }

        if (value is not JsonObject o || o.Any(p => p.Key is not ("halfLifeDays" or "weight" or "kinds")))
        {
            throw Invalid("recency", "null or { halfLifeDays, weight, kinds }");
        }

        Range("recency.halfLifeDays", o["halfLifeDays"], 30, 3650);
        Range("recency.weight", o["weight"], 0, 0.5);
        if (o["kinds"] is not JsonArray kinds || kinds.Count == 0 ||
            kinds.Any(k => k is not JsonValue v || v.GetValueKind() != JsonValueKind.String || !DocumentType.IsMatch(v.GetValue<string>())))
        {
            throw Invalid("recency.kinds", "a non-empty list of document types");
        }
    }

    private static void Range(string key, JsonNode? value, double min, double max, bool integer = false)
    {
        var number = Number(key, value);
        if (number < min || number > max || (integer && number != Math.Floor(number)))
        {
            throw Invalid(key, string.Create(CultureInfo.InvariantCulture, $"{(integer ? "a whole number" : "a number")} from {min} to {max}"));
        }
    }

    private static double Number(string key, JsonNode? value) =>
        value is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : throw Invalid(key, "a number");

    private static Ume.Rag.ServiceDefaults.ApiFaultException Invalid(string field, string expected) =>
        AdminFaults.Field("settings." + field, AdminErrorCodes.RetrievalSettingsInvalid, new Dictionary<string, string> { ["field"] = field, ["expected"] = expected });
}

/// <summary>Slugs from names: lower-case ASCII letters, digits and single hyphens (Swedish letters folded), ≤ 100.</summary>
public static class Slugs
{
    public static string From(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var folded = name.Trim().ToLowerInvariant().Replace("å", "a", StringComparison.Ordinal).Replace("ä", "a", StringComparison.Ordinal)
            .Replace("ö", "o", StringComparison.Ordinal).Normalize(NormalizationForm.FormD);
        var slug = new StringBuilder();
        foreach (var c in folded)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                slug.Append(c);
            }
            else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        var result = slug.ToString().Trim('-');
        result = result.Length > 100 ? result[..100].TrimEnd('-') : result;
        return result.Length > 0 ? result : "samling";
    }

    public const string Pattern = "^[a-z0-9]+(-[a-z0-9]+)*$";

    /// <summary>The first of <c>slug-2</c>, <c>slug-3</c>, … not in <paramref name="taken"/>.</summary>
    public static async Task<string> FreeAsync(IQueryable<string> taken, string slug, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(slug);
        var prefix = (slug.Length > 95 ? slug[..95] : slug) + "-";
        var used = (await taken.Where(s => s.StartsWith(prefix)).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var n = 2;
        while (used.Contains(prefix + n.ToString(CultureInfo.InvariantCulture)))
        {
            n++;
        }

        return prefix + n.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary><c>ILIKE</c> patterns from user text: <c>%</c>, <c>_</c> and the escape character match themselves.</summary>
public static class Like
{
    public const string EscapeCharacter = "\\";

    public static string Escape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
    }
}

/// <summary><c>page</c> (from 1) and <c>pageSize</c> (≤ 200, default 50), and <c>q</c> (admin design §4.1).</summary>
public sealed record PageQuery(int? Page, int? PageSize, string? Q)
{
    public int Take => Math.Clamp(PageSize ?? 50, 1, 200);

    public int Skip => (Math.Max(Page ?? 1, 1) - 1) * Take;
}

public sealed record PageDto<T>(IReadOnlyList<T> Items, int Total);

/// <summary>
/// A list row. For a collection of another department (<see cref="Shared"/>) this is the shared summary of admin design
/// §4.5: name, owning department, level, contact, document count and topics; never its sources or settings, so its <see cref="LastRun"/> and
/// <see cref="LatestEvaluation"/> are null.
/// </summary>
public sealed record CollectionSummary(
    Guid Id, string Slug, string Name, Guid DepartmentId, string DepartmentName, string Classification, DateTimeOffset? PublishedAt, string SharedWith, bool Shared,
    string? ContactName, string? ContactEmail, int DocumentCount, IReadOnlyList<string> Topics, CollectionLastRun? LastRun, CollectionEvaluation? LatestEvaluation);

/// <summary>The latest run of any of a collection's sources (the list's Last fetch, admin design §5.5.2).</summary>
public sealed record CollectionLastRun(Guid Id, Guid SourceId, string State, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);

/// <summary>The latest finished evaluation's recall@k (k = 10) and when it finished (the list's Quality, admin design §5.5.2).</summary>
public sealed record CollectionEvaluation(decimal RecallAtK, DateTimeOffset FinishedAt);

/// <summary>
/// An assistant that links a collection: through its published version, its draft, or both; with its state
/// (<c>active</c> or <c>archived</c>), its department and the link's topics (the published version's, else the draft's;
/// empty = the whole collection).
/// </summary>
public sealed record CollectionUser(
    Guid Id, string Name, string Classification, string State, bool Published, bool Draft, Guid DepartmentId, string DepartmentName, IReadOnlyList<string> Topics);

/// <summary>
/// <see cref="SharedWithDepartmentIds"/>: the share list, only when <see cref="SharedWith"/> is <c>selected</c>;
/// <see cref="DocumentCount"/>: the live documents, as on the list row.
/// </summary>
public sealed record CollectionDto(
    Guid Id, string Slug, string Name, Guid DepartmentId, Guid? OwnerTeamId, string Classification, Guid EmbeddingProfileId, JsonElement RetrievalSettings,
    DateTimeOffset? PublishedAt, string SharedWith, IReadOnlyList<Guid> SharedWithDepartmentIds, string? ContactName, string? ContactEmail, int DocumentCount,
    IReadOnlyList<CollectionUser> UsedBy, DateTimeOffset UpdatedAt, string? UpdatedBy, string Version, Guid? GatewayKeyId)
{
    /// <summary>Always false: the detail is the owner's; a department it is shared with gets the <see cref="CollectionSummary"/> (<c>shared: true</c>).</summary>
    public bool Shared => false;

    public static CollectionDto Of(Collection c, IReadOnlyList<Guid> shares, int documentCount, IReadOnlyList<CollectionUser> usedBy)
    {
        ArgumentNullException.ThrowIfNull(c);
        using var settings = JsonDocument.Parse(c.RetrievalSettings);
        return new(c.Id, c.Slug, c.Name, c.DepartmentId, c.OwnerTeamId, c.Classification, c.EmbeddingProfileId, settings.RootElement.Clone(), c.PublishedAt,
            c.SharedWith, shares, c.ContactName, c.ContactEmail, documentCount, usedBy, c.UpdatedAt, c.UpdatedBy, c.Version.ToString(CultureInfo.InvariantCulture),
            c.GatewayKeyId);
    }
}

public sealed record CollectionAudit(
    Guid Id, string Slug, string Name, Guid DepartmentId, Guid? OwnerTeamId, string Classification, Guid EmbeddingProfileId, string RetrievalSettings,
    DateTimeOffset? PublishedAt, string SharedWith, string? ContactName, string? ContactEmail, Guid? GatewayKeyId)
{
    public static CollectionAudit Of(Collection c) => new(c.Id, c.Slug, c.Name, c.DepartmentId, c.OwnerTeamId, c.Classification, c.EmbeddingProfileId,
        c.RetrievalSettings, c.PublishedAt, c.SharedWith, c.ContactName, c.ContactEmail, c.GatewayKeyId);
}

public sealed record CollectionCreateRequest : AdminRequest, IValidatableObject
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string Name { get; init; } = "";

    /// <summary>Empty: made from the name.</summary>
    [StringLength(100), RegularExpression(Slugs.Pattern)]
    public string? Slug { get; init; }

    [Required]
    public Guid DepartmentId { get; init; }

    /// <summary>Empty: the creator's first team in the department (Decisions "Organisation": teams are hidden in create forms).</summary>
    public Guid? OwnerTeamId { get; init; }

    [Required]
    public string Classification { get; init; } = "";

    [StringLength(200)]
    public string? ContactName { get; init; }

    [StringLength(254), EmailAddress]
    public string? ContactEmail { get; init; }

    /// <summary>The assigned gateway key its indexing, re-embedding and evaluation run as (V39; null = the worker's credential key).</summary>
    public Guid? GatewayKeyId { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (DepartmentId == Guid.Empty)
        {
            yield return new ValidationResult("The department is required.", [nameof(DepartmentId)]);
        }

        if (!Domain.Classification.IsValid(Classification))
        {
            yield return new ValidationResult($"The level must be one of {string.Join(", ", Domain.Classification.All)}.", [nameof(Classification)]);
        }
    }
}

public sealed record CollectionUpdateRequest : AdminRequest, IValidatableObject
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string Name { get; init; } = "";

    public Guid? OwnerTeamId { get; init; }

    [Required]
    public string Classification { get; init; } = "";

    [StringLength(200)]
    public string? ContactName { get; init; }

    [StringLength(254), EmailAddress]
    public string? ContactEmail { get; init; }

    [Required]
    public string? Version { get; init; }

    /// <summary>The assigned gateway key its indexing, re-embedding and evaluation run as (V39; null = the worker's credential key).</summary>
    public Guid? GatewayKeyId { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Domain.Classification.IsValid(Classification))
        {
            yield return new ValidationResult($"The level must be one of {string.Join(", ", Domain.Classification.All)}.", [nameof(Classification)]);
        }
    }
}

/// <summary>A state action's body: the <c>version</c> the caller read (admin design §4.3).</summary>
public sealed record VersionRequest : AdminRequest
{
    [Required]
    public string? Version { get; init; }
}

public sealed record RetrievalSettingsRequest : AdminRequest
{
    /// <summary>The collection's <c>RetrievalSettings</c> (knowledge design §12.1); keys left out fall back to <c>Settings.Retrieval</c>.</summary>
    public JsonElement Settings { get; init; }

    [Required]
    public string? Version { get; init; }
}

/// <summary>The level order <c>public &lt; internal &lt; sensitive</c> (admin design §4.6).</summary>
public static class Levels
{
    public static int Rank(string level) => level switch
    {
        Classification.Public => 0,
        Classification.Internal => 1,
        Classification.Sensitive => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown level."),
    };
}
