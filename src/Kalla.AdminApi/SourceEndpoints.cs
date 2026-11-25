using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.Domain.Ports;
using Kalla.Pipelines.Definitions;
using Kalla.Pipelines.Jobs;
using Kalla.Pipelines.Json;
using Kalla.Pipelines.Schemas;
using Kalla.Pipelines.Sources;

namespace Kalla.AdminApi;

/// <summary>
/// Sources (admin design §4.5 "Sources, runs, uploads", P1a item 14b): create in a collection, read, change (V1, V25 with
/// the impact token for scope and identity changes), delete, impact, sync now, retry failed, pause and resume. A source is
/// in scope when its collection is (<see cref="CollectionEndpoints.Scoped"/>).
/// </summary>
public static class SourceEndpoints
{
    public static RouteGroupBuilder MapSources(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/collections/{id:guid}/sources", async (Guid id, AdminContext admin, CancellationToken ct) =>
            {
                _ = await CollectionEndpoints.Scoped(admin).AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                return await admin.Db.Sources.AsNoTracking().Where(s => s.CollectionId == id && s.DeletedAt == null).OrderBy(s => s.Name)
                    .Select(s => new SourceSummary(s.Id, s.CollectionId, s.Type, s.Name, s.Classification, s.State,
                        // Live documents (they have a live chunk set, so they can be found): the count of the knowledge base list (#374).
                        admin.Db.Documents.Count(d => d.SourceId == s.Id && d.DeletedAt == null && d.LiveChunkSetId != null),
                        admin.Db.Runs.Where(r => r.SourceId == s.Id).OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id)
                            .Select(r => new SourceLastRun(r.Id, r.State, r.StartedAt, r.FinishedAt)).FirstOrDefault()))
                    .ToListAsync(ct);
            })
            .RequireAuthorization(AdminPolicies.Read).WithTags("Sources").WithName("ListSources")
            .WithSummary("A collection's sources with their live document count and latest run");

        api.MapPost("/collections/{id:guid}/sources", async (Guid id, SourceCreateRequest request, AdminContext admin, CancellationToken ct) =>
            {
                var settings = SourceRules.Settings(request.Type, request.Settings);
                var created = await admin.WriteAsync(async attempt =>
                {
                    var collection = await CollectionEndpoints.LoadAsync(admin, id, attempt);
                    SourceRules.CheckLevel(collection, request.Classification);
                    CheckThirdPartyText(admin, request.ContainsThirdPartyText, current: true);
                    var source = new Source
                    {
                        CollectionId = collection.Id,
                        Type = request.Type,
                        Name = request.Name.Trim(),
                        Classification = request.Classification,
                        Settings = settings,
                        Schedule = string.IsNullOrWhiteSpace(request.Schedule) ? null : request.Schedule.Trim(),
                        PipelineId = await SourceRules.PipelineAsync(admin, collection, request.Type, request.PipelineId, attempt),
                        ContainsThirdPartyText = request.ContainsThirdPartyText ?? true,
                        UpdatedAt = admin.Now,
                        UpdatedBy = admin.Actor,
                    };
                    // The first website (crawl) source turns on age decay for PDFs unless the collection already sets recency
                    // (weakness review §3; admin design §5.4 step 3; knowledge design §6.5).
                    var also = new List<AuditAlso>();
                    if (source.Type == Kalla.Domain.Entities.SourceTypes.Crawl &&
                        !await admin.Db.Sources.AnyAsync(s => s.CollectionId == collection.Id && s.Type == Kalla.Domain.Entities.SourceTypes.Crawl && s.DeletedAt == null, attempt) &&
                        RetrievalSettingsRules.WithWebsiteRecency(collection.RetrievalSettings) is { } turnedOn)
                    {
                        also.Add(new AuditAlso("retrieval-settings", AuditEntityTypes.Collection, collection.Id,
                            new { retrievalSettings = System.Text.Json.Nodes.JsonNode.Parse(collection.RetrievalSettings) }, new { retrievalSettings = System.Text.Json.Nodes.JsonNode.Parse(turnedOn), reason = "first-website-source" }));
                        collection.RetrievalSettings = turnedOn;
                        collection.UpdatedAt = admin.Now;
                        collection.UpdatedBy = admin.Actor;
                    }

                    admin.Db.Sources.Add(source);
                    return new AdminChange<Source>(source, "create", AuditEntityTypes.Source, source.Id, null, SourceAudit.Of(source))
                    {
                        DepartmentId = collection.DepartmentId, Classification = source.Classification, Also = also,
                    };
                }, ct);
                return Results.Created($"/api/sources/{created.Id}", await SourceDto.LoadAsync(admin, created, ct));
            })
            .RequireAuthorization(AdminPolicies.Edit).WithTags("Sources").WithName("CreateSource").WithSummary("Add a source to a collection (V1, V25)");

        var sources = api.MapGroup("/sources").WithTags("Sources");

        sources.MapGet("/{id:guid}", async (Guid id, AdminContext admin, CancellationToken ct) =>
                await SourceDto.LoadAsync(admin, await Scoped(admin).AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound), ct))
            .RequireAuthorization(AdminPolicies.Read).WithName("GetSource").WithSummary("One source");

        sources.MapPost("/{id:guid}/impact", async (Guid id, SourceImpactRequest request, AdminContext admin, CancellationToken ct) =>
            {
                var source = await Scoped(admin).AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                var settings = SourceRules.Settings(source.Type, request.Settings);
                var effects = SourceRules.Effects(source, settings);
                var documents = await admin.Db.Documents.AsNoTracking().CountAsync(d => d.SourceId == id && d.DeletedAt == null, ct);
                var usedBy = await CollectionEndpoints.UsedByAsync(admin, source.CollectionId, ct);
                string[] refetch = SourceRules.Refetches(source, settings) ? [SourceRules.Refetch] : [];
                return new SourceImpact([.. effects.Select(e => JsonNamingPolicy.KebabCaseLower.ConvertName(e.ToString())).Concat(refetch).Order(StringComparer.Ordinal)],
                    documents, usedBy, SourceRules.NeedsToken(effects) ? SourceRules.ImpactToken(source, settings) : null);
            })
            .RequireAuthorization(AdminPolicies.Edit).WithName("SourceImpact").WithSummary("What a settings change does; the token a scope or identity change needs");

        sources.MapPut("/{id:guid}", async (Guid id, SourceUpdateRequest request, AdminContext admin, CancellationToken ct) =>
                await SourceDto.LoadAsync(admin, await admin.WriteAsync(async attempt =>
                {
                    var source = await Scoped(admin).SingleOrDefaultAsync(s => s.Id == id, attempt) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                    admin.ExpectVersion(source, request.Version);
                    var settings = SourceRules.Settings(source.Type, request.Settings);
                    var effects = SourceRules.Effects(source, settings);
                    if (SourceRules.NeedsToken(effects) && !string.Equals(request.ImpactToken, SourceRules.ImpactToken(source, settings), StringComparison.Ordinal))
                    {
                        throw AdminFaults.Of(AdminErrorCodes.ImpactNotConfirmed);
                    }

                    var collection = await admin.Db.Collections.AsNoTracking().SingleAsync(c => c.Id == source.CollectionId, attempt);
                    SourceRules.CheckLevel(collection, request.Classification);
                    CheckThirdPartyText(admin, request.ContainsThirdPartyText, source.ContainsThirdPartyText);
                    var before = SourceAudit.Of(source);
                    source.Name = request.Name.Trim();
                    source.Classification = request.Classification;
                    source.Settings = settings;
                    source.Schedule = string.IsNullOrWhiteSpace(request.Schedule) ? null : request.Schedule.Trim();
                    source.ContainsThirdPartyText = request.ContainsThirdPartyText ?? source.ContainsThirdPartyText;
                    source.UpdatedAt = admin.Now;
                    source.UpdatedBy = admin.Actor;
                    return new AdminChange<Source>(source, "update", AuditEntityTypes.Source, source.Id, before, SourceAudit.Of(source))
                    {
                        DepartmentId = collection.DepartmentId, Classification = source.Classification,
                    };
                }, ct), ct))
            .RequireAuthorization(AdminPolicies.Edit).WithName("UpdateSource").WithSummary("Change a source (V1, V25; scope and identity changes need the impact token)");

        sources.MapDelete("/{id:guid}", async (Guid id, [Microsoft.AspNetCore.Mvc.FromQuery] string? version, AdminContext admin, CancellationToken ct) =>
            {
                await admin.WriteAsync(async attempt =>
                {
                    var source = await Scoped(admin).SingleOrDefaultAsync(s => s.Id == id, attempt) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
                    admin.ExpectVersion(source, version);
                    var department = await DepartmentOfAsync(admin, source, attempt);
                    var before = SourceAudit.Of(source);
                    source.DeletedAt = admin.Now;
                    source.UpdatedAt = admin.Now;
                    source.UpdatedBy = admin.Actor;
                    // The worker takes its content out of the index with the delete (admin design §4.5; data-model §8).
                    return new AdminChange<bool>(true, "delete", AuditEntityTypes.Source, source.Id, before, null)
                    {
                        DepartmentId = department, Classification = source.Classification,
                        Jobs = [CommandJobs.DeleteSourceJob(source.Id, department, source.Classification)],
                    };
                }, ct);
                return Results.NoContent();
            })
            .RequireAuthorization(AdminPolicies.Manage).WithName("DeleteSource").WithSummary("Delete a source; its documents leave the index");

        sources.MapPost("/{id:guid}/sync", (Guid id, AdminContext admin, CancellationToken ct) => SyncAsync(id, SyncKind.Sync, admin, ct))
            .RequireAuthorization(AdminPolicies.Edit).WithName("SyncSource").WithSummary("Sync now; answers the running run when there is one");

        sources.MapPost("/{id:guid}/retry-failed", (Guid id, AdminContext admin, CancellationToken ct) => SyncAsync(id, SyncKind.RetryFailed, admin, ct))
            .RequireAuthorization(AdminPolicies.Edit).WithName("RetryFailedSource").WithSummary("Fetch again only the documents whose last run item failed");

        sources.MapPost("/{id:guid}/pause", (Guid id, VersionRequest request, AdminContext admin, CancellationToken ct) => SetStateAsync(id, SourceStates.Paused, request, admin, ct))
            .RequireAuthorization(AdminPolicies.Edit).WithName("PauseSource").WithSummary("Stop scheduled runs");

        sources.MapPost("/{id:guid}/resume", (Guid id, VersionRequest request, AdminContext admin, CancellationToken ct) => SetStateAsync(id, SourceStates.Active, request, admin, ct))
            .RequireAuthorization(AdminPolicies.Edit).WithName("ResumeSource").WithSummary("Run on schedule again");

        return api;
    }

    /// <summary>Sources in the caller's collections that are not deleted.</summary>
    public static IQueryable<Source> Scoped(AdminContext admin)
    {
        var collections = CollectionEndpoints.Scoped(admin).Select(c => c.Id);
        return admin.Db.Sources.Where(s => s.DeletedAt == null && collections.Contains(s.CollectionId));
    }

    public static Task<Guid> DepartmentOfAsync(AdminContext admin, Source source, CancellationToken ct) =>
        admin.Db.Collections.AsNoTracking().Where(c => c.Id == source.CollectionId).Select(c => c.DepartmentId).SingleAsync(ct);

    /// <summary>
    /// Sync now and retry failed (admin design §4.7): <c>sync</c> with <c>trigger</c> <c>manual</c>, priority 10, dedup
    /// <c>sync:{sourceId}</c>. When a run is already running the job is not enqueued again and the run is answered.
    /// </summary>
    private static async Task<SyncStarted> SyncAsync(Guid id, SyncKind kind, AdminContext admin, CancellationToken ct) =>
        await admin.WriteAsync(async attempt =>
        {
            var source = await Scoped(admin).AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, attempt) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
            if (source.State == SourceStates.Paused)
            {
                // The worker would end the job at once without a run (source.paused); pausing is deliberate, so no implicit resume.
                throw AdminFaults.Of(AdminErrorCodes.SourcePaused);
            }

            var collection = await admin.Db.Collections.AsNoTracking().SingleAsync(c => c.Id == source.CollectionId, attempt);
            var action = kind == SyncKind.Sync ? "sync" : "retry-failed";
            if (await QueuedSyncAsync(admin, id, attempt) is { } already)
            {
                object details = already.RunId is { } runId ? new { runId } : new { jobId = already.JobId };
                return new AdminChange<SyncStarted>(already, action, AuditEntityTypes.Source, id, null, details)
                {
                    DepartmentId = collection.DepartmentId, Classification = source.Classification,
                };
            }

            var dedup = JobDedupKeys.Sync(id);
            var job = new JobRequest(JobClasses.Sync, collection.DepartmentId, source.Classification,
                JsonSerializer.Serialize(new SyncPayload { SourceId = id, Kind = kind, Trigger = RunTrigger.Manual }, ContractJson.Options))
            {
                Priority = 10,
                DedupKey = dedup,
                TeamId = collection.OwnerTeamId,
            };
            return new AdminChange<SyncStarted>(new SyncStarted(job.Id, null, false, KindName(kind)), action, AuditEntityTypes.Source, id, null, new { jobId = job.Id })
            {
                DepartmentId = collection.DepartmentId, Classification = source.Classification, Jobs = [job],
            };
        }, ct);

    /// <summary>
    /// The source's run already running, else its <c>sync</c> job already queued or running (dedup <c>sync:{sourceId}</c>),
    /// as the answer to a request that enqueues nothing; null when neither.
    /// </summary>
    public static async Task<SyncStarted?> QueuedSyncAsync(AdminContext admin, Guid sourceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        var running = await admin.Db.Runs.AsNoTracking().Where(r => r.SourceId == sourceId && r.State == RunStates.Running)
            .OrderByDescending(r => r.StartedAt).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct);
        if (running is not null)
        {
            return new SyncStarted(null, running, true, null);
        }

        var dedup = JobDedupKeys.Sync(sourceId);
        var queued = await admin.Db.Jobs.AsNoTracking().Where(j => j.DedupKey == dedup && (j.State == JobStates.Queued || j.State == JobStates.Running))
            .Select(j => (Guid?)j.Id).FirstOrDefaultAsync(ct);
        return queued is null ? null : new SyncStarted(queued, null, true, await QueuedKindAsync(admin, queued.Value, ct));
    }

    private static string KindName(SyncKind kind) => kind == SyncKind.RetryFailed ? "retry-failed" : "sync";

    /// <summary>The kind of the queued sync job, so the caller sees whether its request was answered by a sync or a retry.</summary>
    private static async Task<string?> QueuedKindAsync(AdminContext admin, Guid jobId, CancellationToken ct)
    {
        var payload = await admin.Db.Jobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => j.Payload).SingleOrDefaultAsync(ct);
        if (payload is null)
        {
            return null;
        }

        using var json = JsonDocument.Parse(payload);
        return json.RootElement.TryGetProperty("kind", out var kind) ? kind.GetString() : null;
    }

    private static async Task<SourceDto> SetStateAsync(Guid id, string state, VersionRequest request, AdminContext admin, CancellationToken ct) =>
        await SourceDto.LoadAsync(admin, await admin.WriteAsync(async attempt =>
        {
            var source = await Scoped(admin).SingleOrDefaultAsync(s => s.Id == id, attempt) ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
            if (state == SourceStates.Paused && source.Type == Domain.Entities.SourceTypes.Upload)
            {
                // An upload source has no schedule; paused, it would take a batch whose sync job the worker then drops.
                throw AdminFaults.Of(AdminErrorCodes.SourceNotPausable);
            }

            admin.ExpectVersion(source, request.Version);
            var before = SourceAudit.Of(source);
            source.State = state;
            source.UpdatedAt = admin.Now;
            source.UpdatedBy = admin.Actor;
            return new AdminChange<Source>(source, state == SourceStates.Paused ? "pause" : "resume", AuditEntityTypes.Source, source.Id, before, SourceAudit.Of(source))
            {
                DepartmentId = await DepartmentOfAsync(admin, source, attempt), Classification = source.Classification,
            };
        }, ct), ct);

    /// <summary>V13's input: <c>ContainsThirdPartyText</c> is writable by platform admins only (tools §5.4).</summary>
    private static void CheckThirdPartyText(AdminContext admin, bool? requested, bool current)
    {
        if (requested is { } value && value != current && !admin.IsPlatformAdmin)
        {
            throw AdminFaults.Field("containsThirdPartyText", AdminErrorCodes.AdminOnly);
        }
    }
}

/// <summary>V1 and V25 (admin design §4.6) against the J3 contracts (#80).</summary>
public static class SourceRules
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// The settings checked against <c>contracts/sources/v1/{type}.schema.json</c>, then V25's own checks (URL list
    /// duplicates, <c>re:</c> patterns compile with <c>NonBacktracking</c>, <c>readySelector</c> and <c>noiseSelectors</c>
    /// parse as CSS, #488); returned as stored (compact JSON).
    /// Refusals: 400 <c>source_settings_invalid</c> with the JSON pointer of the first problem.
    /// </summary>
    public static string Settings(string type, JsonElement settings)
    {
        if (settings.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("", "the settings must be an object");
        }

        if (!Domain.Entities.SourceTypes.All.Contains(type, StringComparer.Ordinal))
        {
            throw AdminFaults.Field("type", AdminErrorCodes.InvalidValue);
        }

        var errors = ContractSchemas.Validator(SourceSettings.SettingsType(type)).Validate(settings);
        if (errors.Count > 0)
        {
            // An unknown member fails the schema's "false" subschema; say so in words.
            var message = errors[0].Message.Contains("Boolean false", StringComparison.Ordinal) ? "not a setting of this source type" : errors[0].Message;
            throw Invalid(errors[0].InstanceLocation, message);
        }

        object bound;
        try
        {
            bound = SourceSettings.Bind(type, settings);
        }
        catch (JsonException ex)
        {
            throw Invalid(ex.Path ?? "", "the settings do not match the source type");
        }

        switch (bound)
        {
            case UrlListSettings list:
                var duplicates = list.Urls.GroupBy(u => u.Trim(), StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                if (duplicates.Count > 0)
                {
                    throw Invalid("/urls", "duplicate URLs: " + string.Join(", ", duplicates.Take(10)));
                }

                Selectors(list.ReadySelector, list.NoiseSelectors);
                break;
            case CrawlSettings crawl:
                Patterns("/include", crawl.Include);
                Patterns("/exclude", crawl.Exclude);
                Patterns("/demote", crawl.Demote.Select(d => d.Pattern));
                Selectors(crawl.ReadySelector, crawl.NoiseSelectors);
                break;
        }

        return JsonSerializer.Serialize(settings);
    }

    /// <summary>V1: a source's level is at most its collection's.</summary>
    public static void CheckLevel(Collection collection, string level)
    {
        ArgumentNullException.ThrowIfNull(collection);
        if (Levels.Rank(level) > Levels.Rank(collection.Classification))
        {
            throw AdminFaults.Field("classification", AdminErrorCodes.SourceAboveCollection,
                new Dictionary<string, string> { ["collection"] = collection.Name, ["level"] = collection.Classification });
        }
    }

    /// <summary>The effect tags of a settings change (J3: <see cref="SourceSettings.Changed"/>).</summary>
    public static IReadOnlySet<SettingEffect> Effects(Source source, string settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var before = JsonDocument.Parse(source.Settings);
        using var after = JsonDocument.Parse(settings);
        return SourceSettings.Changed(source.Type, before.RootElement, after.RootElement);
    }

    /// <summary>The impact's effect for a change that fetches every page again (<see cref="Refetches"/>).</summary>
    public const string Refetch = "refetch";

    /// <summary>
    /// The change starts a sync that fetches every page again in the new mode (connectors design §12.3: <c>fetchMode</c>,
    /// <c>readySelector</c>, <c>scrollToLoad</c>): a <c>behaviour</c> change that needs no token, shown on save.
    /// </summary>
    public static bool Refetches(Source source, string settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var before = JsonDocument.Parse(source.Settings);
        using var after = JsonDocument.Parse(settings);
        return !string.Equals(SourceSettings.Hashes(source.Type, before.RootElement).Fetch, SourceSettings.Hashes(source.Type, after.RootElement).Fetch,
            StringComparison.Ordinal);
    }

    public static bool NeedsToken(IReadOnlySet<SettingEffect> effects) => effects.Contains(SettingEffect.Scope) || effects.Contains(SettingEffect.Identity);

    /// <summary>
    /// The confirmation that the caller saw this change's impact: a hash of the source, its version and the new settings, so
    /// it is valid for exactly that change from exactly that state (not a secret; it only proves the impact was asked for).
    /// </summary>
    public static string ImpactToken(Source source, string settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var parsed = JsonDocument.Parse(settings);
        var input = Encoding.UTF8.GetBytes(source.Id.ToString("N") + "\n" + source.Version.ToString(CultureInfo.InvariantCulture) + "\n");
        return Convert.ToHexStringLower(SHA256.HashData([.. input, .. CanonicalJson.Serialize(parsed.RootElement)]))[..32];
    }

    /// <summary>
    /// The pipeline: the given one (a platform pipeline or one of the collection's department) or the platform pipeline of
    /// the source type's built-in template; its template must be for this source type (V24).
    /// </summary>
    public static async Task<Guid> PipelineAsync(AdminContext admin, Collection collection, string type, Guid? requested, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(collection);
        var candidates = Candidates(admin, collection, type);
        var pipeline = requested is { } id
            ? await candidates.Where(p => p.Id == id).Select(p => (Guid?)p.Id).SingleOrDefaultAsync(ct)
            : await Default(candidates).Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        return pipeline ?? throw AdminFaults.Field("pipelineId", AdminErrorCodes.TemplateUnsupported, new Dictionary<string, string> { ["type"] = type });
    }

    /// <summary>The published pipelines a source of <paramref name="type"/> in <paramref name="collection"/> can use: platform ones and the collection department's.</summary>
    public static IQueryable<Pipeline> Candidates(AdminContext admin, Collection collection, string type)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(collection);
        string[] templates = [.. TemplateCatalog.BuiltIn.All.Where(t => t.Template.SourceType == type).Select(t => t.Template.Name).Distinct()];
        return admin.Db.Pipelines.AsNoTracking()
            .Where(p => templates.Contains(p.TemplateId) && p.PublishedVersionId != null && (p.DepartmentId == null || p.DepartmentId == collection.DepartmentId));
    }

    /// <summary>The pipeline a new source gets when none is named: the first platform pipeline by name.</summary>
    public static IQueryable<Pipeline> Default(IQueryable<Pipeline> candidates) => candidates.Where(p => p.DepartmentId == null).OrderBy(p => p.Name);

    private static void Patterns(string path, IEnumerable<string> patterns)
    {
        foreach (var (pattern, i) in patterns.Select((p, i) => (p, i)))
        {
            if (!pattern.StartsWith("re:", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                _ = new Regex(pattern[3..], RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, PatternTimeout);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // NotSupportedException: a construct the non-backtracking engine lacks (lookarounds, backreferences, atomic
                // groups), which the worker would refuse too (#476).
                throw Invalid($"{path}/{i}", "the regular expression does not compile: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// #488: a selector that isn't CSS (<c>&lt;h1&gt;</c>, <c>div[</c>) is refused on save. Otherwise the renderer fails every page
    /// on it, and the worker's noise normaliser skips it without a word. AngleSharp parses it as the worker's normaliser does.
    /// </summary>
    private static void Selectors(string? readySelector, IReadOnlyList<string> noiseSelectors)
    {
        var parser = new AngleSharp.Css.Parser.CssSelectorParser();
        if (readySelector is not null && !IsSelector(parser, readySelector))
        {
            throw Invalid("/readySelector", "not a valid CSS selector");
        }

        foreach (var (selector, i) in noiseSelectors.Select((s, i) => (s, i)))
        {
            if (!IsSelector(parser, selector))
            {
                throw Invalid($"/noiseSelectors/{i}", "not a valid CSS selector");
            }
        }
    }

    private static bool IsSelector(AngleSharp.Css.Parser.CssSelectorParser parser, string selector)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(selector) && parser.ParseSelector(selector) is not null;
        }
        catch (AngleSharp.Dom.DomException)
        {
            return false;
        }
    }

    private static Kalla.ServiceDefaults.ApiFaultException Invalid(string pointer, string problem) =>
        AdminFaults.Field("settings" + pointer.Replace('/', '.'), AdminErrorCodes.SourceSettingsInvalid,
            new Dictionary<string, string> { ["field"] = pointer.Length == 0 ? "settings" : pointer.TrimStart('/'), ["problem"] = problem });
}

public sealed record SourceSummary(
    Guid Id, Guid CollectionId, string Type, string Name, string Classification, string State, int Documents, SourceLastRun? LastRun);

public sealed record SourceLastRun(Guid Id, string State, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);

/// <summary>
/// A source. <see cref="Schedule"/> is its own cron (null: the platform default for its type); <see cref="ScheduleInForce"/>
/// the cron it runs on (null for uploads); <see cref="NextRunAt"/> when the scheduler enqueues its next scheduled sync,
/// jitter included (null when paused or unscheduled; not after now: due, <see cref="SourceSchedules.Next"/>).
/// </summary>
public sealed record SourceDto(
    Guid Id, Guid CollectionId, string Type, string Name, string Classification, JsonElement Settings, string? Schedule, string? ScheduleInForce,
    DateTimeOffset? NextRunAt, Guid PipelineId, bool ContainsThirdPartyText, string State, DateTimeOffset UpdatedAt, string? UpdatedBy, string Version)
{
    /// <summary>The source with its schedule in force and next run (engine design §9.6).</summary>
    public static async Task<SourceDto> LoadAsync(AdminContext admin, Source s, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(s);
        var platform = await admin.Db.Settings.AsNoTracking().SingleOrDefaultAsync(ct) ?? new PlatformSettings();
        // The worker's rule (A-43): the schedule in force, and when the scheduler enqueues the next run (none while paused).
        var inForce = SourceSchedules.InForce(s.Type, s.Schedule, SourceSchedules.ReadDefaults(platform.DefaultSchedules));
        var last = await admin.Db.Runs.AsNoTracking().Where(r => r.SourceId == s.Id && r.Trigger == RunTriggers.Schedule).MaxAsync(r => r.ScheduledFor, ct);
        using var settings = JsonDocument.Parse(s.Settings);
        return new(s.Id, s.CollectionId, s.Type, s.Name, s.Classification, settings.RootElement.Clone(), s.Schedule, inForce,
            s.State == SourceStates.Active ? SourceSchedules.Next(inForce, s.Id, platform.ScheduleJitterMinutes, s.CreatedAt, last, admin.Now)?.At : null,
            s.PipelineId, s.ContainsThirdPartyText,
            s.State, s.UpdatedAt, s.UpdatedBy, s.Version.ToString(CultureInfo.InvariantCulture));
    }
}

public sealed record SourceAudit(
    Guid Id, Guid CollectionId, string Type, string Name, string Classification, string Settings, string? Schedule, Guid PipelineId, bool ContainsThirdPartyText,
    string State)
{
    public static SourceAudit Of(Source s) => new(s.Id, s.CollectionId, s.Type, s.Name, s.Classification, s.Settings, s.Schedule, s.PipelineId,
        s.ContainsThirdPartyText, s.State);
}

/// <summary>
/// The effect tags of a change (kebab-case; plus <c>refetch</c> when every page is fetched again, <see cref="SourceRules.Refetches"/>), the source's
/// live documents, the collection's assistants, and the token when needed.
/// </summary>
public sealed record SourceImpact(IReadOnlyList<string> Effects, int LiveDocuments, IReadOnlyList<CollectionUser> UsedBy, string? ImpactToken);

/// <summary>
/// The sync job enqueued, or the job already queued or the run already running (<see cref="AlreadyRunning"/>: nothing was
/// enqueued). <see cref="Kind"/> is the job's kind (<c>sync</c>, <c>retry-failed</c> or <c>continue</c>; null for a running run): sync
/// now, retry failed and continue share <c>sync:{sourceId}</c>, so a request may be answered by another one.
/// </summary>
public sealed record SyncStarted(Guid? JobId, Guid? RunId, bool AlreadyRunning, string? Kind);

public record SourceFields : AdminRequest, IValidatableObject
{
    [Required, StringLength(200, MinimumLength = 1)]
    public string Name { get; init; } = "";

    [Required]
    public string Classification { get; init; } = "";

    public JsonElement Settings { get; init; }

    /// <summary>Cron, 5 fields; empty = the platform default for the type.</summary>
    [StringLength(100), RegularExpression(@"^\S+( \S+){4}$")]
    public string? Schedule { get; init; }

    /// <summary>Platform admins only (tools §5.4); null keeps the current value (true for a new source).</summary>
    public bool? ContainsThirdPartyText { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Domain.Classification.IsValid(Classification))
        {
            yield return new ValidationResult($"The level must be one of {string.Join(", ", Domain.Classification.All)}.", [nameof(Classification)]);
        }

        if (!string.IsNullOrWhiteSpace(Schedule) && !CronSchedules.IsValid(Schedule.Trim()))
        {
            yield return new ValidationResult(CronSchedules.Invalid, [nameof(Schedule)]);
        }
    }
}

public sealed record SourceCreateRequest : SourceFields
{
    [Required]
    public string Type { get; init; } = "";

    /// <summary>Null: the platform pipeline of the type's template.</summary>
    public Guid? PipelineId { get; init; }
}

public sealed record SourceUpdateRequest : SourceFields
{
    /// <summary>From <c>POST /api/sources/{id}/impact</c>, required when the change has a scope or identity effect (V25).</summary>
    [StringLength(64)]
    public string? ImpactToken { get; init; }

    [Required]
    public string? Version { get; init; }
}

public sealed record SourceImpactRequest : AdminRequest
{
    public JsonElement Settings { get; init; }
}
