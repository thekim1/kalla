using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Infrastructure.Access;
using Ume.Rag.Retrieval;
using Ume.Rag.Retrieval.Postgres;

namespace Ume.Rag.Answering;

/// <summary>Issues the download link of a citation without a public URL (knowledge design §4.6); null until grants exist (part 3).</summary>
public interface IDownloadLinks
{
    string? For(RagPrincipal who, Guid documentId, Guid collectionId, Guid? assistantId, string level);
}

/// <summary>No download links (a citation of an upload then has no link).</summary>
public sealed class NoDownloadLinks : IDownloadLinks
{
    public static NoDownloadLinks Instance { get; } = new();

    public string? For(RagPrincipal who, Guid documentId, Guid collectionId, Guid? assistantId, string level) => null;
}

/// <summary>
/// The knowledge role's core (knowledge design §2.1, §4, §6, §10), without HTTP: access on one confirmed snapshot,
/// collection resolution (an assistant's published collections, or requested ones), allowed levels, the search through
/// <see cref="RetrievalEngine"/> with the profile's query embedding, hydration re-checking every hit, citations, and the
/// retrieval counters. Nothing touches Postgres or the gateway before the in-memory checks pass (§4.1). The query text
/// is never logged.
/// </summary>
public sealed partial class KnowledgeService(
    IPolicySnapshotSource snapshots,
    IAccessPolicy policy,
    RetrievalEngine engine,
    PostgresHydrator hydrator,
    GatewayQueryEmbedder embedder,
    RetrievalCounter counter,
    NpgsqlDataSource dataSource,
    IDownloadLinks links,
    ILogger<KnowledgeService> logger) : IRetriever
{
    /// <summary>At most 20 collections per query (§4.2).</summary>
    public const int MaxCollections = 20;

    private (long Version, RetrievalPlatformSettings Platform, Dictionary<Guid, RetrievalSettings> Collections)? _settings;

    public async Task<RetrievalResult> RetrieveAsync(RagPrincipal who, RetrievalRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(who);
        ArgumentNullException.ThrowIfNull(request);
        var started = Stopwatch.GetTimestamp();
        if (request.TopK is < 1 or > SearchRequest.MaxTopK || request.CollectionIds is { Count: > MaxCollections } ||
            request.CollectionIds is { } ids && ids.Distinct().Count() != ids.Count)
        {
            throw new RetrievalDeniedException(400, KnowledgeCodes.InvalidRequest);
        }

        var snapshot = snapshots.Current;
        var confirmed = snapshots.IsConfirmed;
        var action = request.Purpose == RetrievalPurpose.Chat ? RagAction.Chat : RagAction.Search;
        var (collections, effectiveLevel) = request.AssistantId is { } assistantId
            ? AssistantCollections(who, assistantId, request.CollectionIds, action, snapshot, confirmed)
            : RequestedCollections(who, request.CollectionIds, snapshot, confirmed);
        var allowedLevels = Levels.UpTo(Levels.Rank(effectiveLevel) <= Levels.Rank(who.Client.Ceiling) ? effectiveLevel : who.Client.Ceiling);

        // One candidate run per embedding profile; P1 has one, so a collection on another profile is skipped (§4.3).
        var profileId = collections.Count > 0 ? collections[0].EmbeddingProfileId : Guid.Empty;
        foreach (var other in collections.Where(c => c.EmbeddingProfileId != profileId))
        {
            LogSkipped(logger, other.Id, "profile_mismatch");
        }

        collections = [.. collections.Where(c => c.EmbeddingProfileId == profileId)];
        if (collections.Count == 0 || !snapshot.EmbeddingProfiles.TryGetValue(profileId, out var profile) || profile.State != EmbeddingProfileStates.Active)
        {
            if (collections.Count > 0)
            {
                LogSkipped(logger, profileId, "profile_unavailable");
            }

            // Nothing searchable: no results and coverage low, so the chat role answers with the not-found text (§4.4).
            return new RetrievalResult([], [], new Coverage(CoverageStatus.Low, null, null, null, null), null, snapshot.Version,
                new RetrievalTimings(Ms(started), 0, 0, 0, 0, Ms(started)));
        }

        var (platform, settings) = Settings(snapshot);
        var scope = new SearchScope(profile.Id, profile.Dimensions,
            [.. collections.Select(c => new CollectionInScope(c.Id, settings.GetValueOrDefault(c.Id, platform.Defaults)))], allowedLevels, platform);
        var policyMs = Ms(started);

        SearchResult search;
        try
        {
            search = await engine.SearchAsync(new SearchRequest(request.Query, scope, request.TopK, request.Mode, request.Filters,
                request.Mode == RetrievalMode.Keyword ? null : (query, token) => embedder.EmbedAsync(profile, query, request.RunId, token),
                request.AllowDegraded), ct);
        }
        catch (SearchUnavailableException)
        {
            throw new RetrievalDeniedException(503, KnowledgeCodes.SearchUnavailable);
        }
        catch (QueryEmbeddingUnavailableException)
        {
            throw new RetrievalDeniedException(503, KnowledgeCodes.EmbeddingUnavailable);
        }
        catch (ArgumentException)
        {
            throw new RetrievalDeniedException(400, KnowledgeCodes.InvalidRequest); // an empty or over-long query (§3.1)
        }

        var hydrateStarted = Stopwatch.GetTimestamp();
        var results = await HydrateAsync(who, request, snapshot, collections, allowedLevels, search.Hits, ct);
        counter.Retrieved(results);
        var timings = new RetrievalTimings(policyMs, search.Timings.KeywordMs, search.Timings.EmbeddingMs, search.Timings.VectorMs,
            Ms(hydrateStarted), Ms(started));
        return new RetrievalResult(results, [], search.Coverage, search.Degraded, snapshot.Version, timings);
    }

    public async Task<DocumentInfo> GetDocumentAsync(RagPrincipal who, Guid documentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(who);
        var snapshot = snapshots.Current;
        var confirmed = snapshots.IsConfirmed;
        var visible = Searchable(who, snapshot, confirmed).ToDictionary(c => c.Id);
        var sources = visible.Values.SelectMany(c => snapshot.SourcesByCollection.GetValueOrDefault(c.Id, [])).ToArray();
        if (sources.Length == 0)
        {
            throw new RetrievalDeniedException(404, KnowledgeCodes.NotFound);
        }

        await using var command = dataSource.CreateCommand(RetrievalStatements.Documents);
        command.Parameters.Add(new NpgsqlParameter { Value = new[] { documentId } });
        command.Parameters.Add(new NpgsqlParameter { Value = Levels.UpTo(who.Client.Ceiling).ToArray() });
        command.Parameters.Add(new NpgsqlParameter { Value = sources });
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || !reader.GetBoolean(11))
        {
            throw new RetrievalDeniedException(404, KnowledgeCodes.NotFound);
        }

        var document = ReadDocument(reader);
        var collection = visible.Values.First(c => snapshot.SourcesByCollection.GetValueOrDefault(c.Id, []).Contains(document.SourceId));
        return new DocumentInfo(Cite(who, document, null, null, collection, null), document.PageCount, collection.Id, collection.Slug);
    }

    public Task<IReadOnlyList<CollectionInfo>> ListCollectionsAsync(RagPrincipal who, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(who);
        var snapshot = snapshots.Current;
        IReadOnlyList<CollectionInfo> list = [.. Searchable(who, snapshot, snapshots.IsConfirmed).OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => new CollectionInfo(c.Id, c.Slug, c.Name, c.Classification, snapshot.DepartmentNames.GetValueOrDefault(c.DepartmentId), c.PublishedAt))];
        return Task.FromResult(list);
    }

    public void MarkCited(RetrievalResult result, IReadOnlyCollection<int> citedRanks) => counter.Cited(result, citedRanks);

    /// <summary>The live collections <paramref name="who"/> may search, silently (§3.3); an unconfirmed snapshot refuses.</summary>
    private IEnumerable<CollectionEntry> Searchable(RagPrincipal who, PolicySnapshot snapshot, bool confirmed)
    {
        if (!confirmed)
        {
            throw new RetrievalDeniedException(503, DenyReasons.ConfigUnconfirmed);
        }

        var live = snapshot.Collections.Values.Where(c => c.IsLive).ToList();
        var allowed = policy.Filter(who, live.Select(c => ResourceRef.Collection(c.Id)), RagAction.Search, snapshot, confirmed).Select(r => r.Id).ToHashSet();
        return live.Where(c => allowed.Contains(c.Id));
    }

    /// <summary>§4.2 chat and <c>rag-internal</c>: the published version's live collections, optionally narrowed.</summary>
    private (List<CollectionEntry>, string) AssistantCollections(RagPrincipal who, Guid assistantId, IReadOnlyList<Guid>? narrow, RagAction action,
        PolicySnapshot snapshot, bool confirmed)
    {
        var decision = policy.Decide(who, ResourceRef.Assistant(assistantId), action, snapshot, confirmed);
        if (!decision.Allowed)
        {
            throw Denied(decision);
        }

        var assistant = snapshot.Assistants[assistantId];
        var linked = assistant.Published!.CollectionIds;
        if (narrow is { Count: > 0 } && narrow.FirstOrDefault(id => !linked.Contains(id)) is var outside && outside != Guid.Empty)
        {
            throw new RetrievalDeniedException(404, KnowledgeCodes.CollectionNotFound, outside.ToString());
        }

        var collections = new List<CollectionEntry>();
        foreach (var id in narrow is { Count: > 0 } ? narrow : linked)
        {
            if (!snapshot.Collections.TryGetValue(id, out var collection) || !collection.IsLive)
            {
                continue; // unpublished or deleted: skipped silently (§4.4)
            }

            if (!Levels.AtMost(collection.Classification, assistant.Classification))
            {
                LogSkipped(logger, id, "above_assistant_level"); // should be impossible (admin V-rules); never searched
                continue;
            }

            collections.Add(collection);
        }

        return (collections, assistant.Classification);
    }

    /// <summary>§4.2 direct callers: each requested collection must be live and allowed (404 naming the first that is not); none = all searchable.</summary>
    private (List<CollectionEntry>, string) RequestedCollections(RagPrincipal who, IReadOnlyList<Guid>? requested, PolicySnapshot snapshot, bool confirmed)
    {
        List<CollectionEntry> collections;
        if (requested is not { Count: > 0 })
        {
            collections = [.. Searchable(who, snapshot, confirmed).Take(MaxCollections)];
        }
        else
        {
            collections = [];
            foreach (var id in requested)
            {
                var decision = policy.Decide(who, ResourceRef.Collection(id), RagAction.Search, snapshot, confirmed);
                if (decision is { Allowed: false, HttpStatus: not 404 })
                {
                    throw Denied(decision);
                }

                if (!decision.Allowed || !snapshot.Collections.TryGetValue(id, out var collection) || !collection.IsLive)
                {
                    throw new RetrievalDeniedException(404, KnowledgeCodes.CollectionNotFound, id.ToString());
                }

                collections.Add(collection);
            }
        }

        var level = collections.Select(c => c.Classification).Aggregate(Classification.Public, Levels.Max);
        return (collections, level);
    }

    private async Task<List<RetrievedChunk>> HydrateAsync(RagPrincipal who, RetrievalRequest request, PolicySnapshot snapshot,
        List<CollectionEntry> collections, IReadOnlyList<string> levels, IReadOnlyList<SearchHit> hits, CancellationToken ct)
    {
        if (hits.Count == 0)
        {
            return [];
        }

        var hydration = await hydrator.HydrateAsync(new HydrationRequest(
            [.. hits.Select(h => h.Candidate.ChunkId)], [.. hits.Select(h => h.Candidate.DocumentId).Distinct()], levels,
            [.. collections.Select(c => c.Id)], [.. collections.SelectMany(c => snapshot.SourcesByCollection.GetValueOrDefault(c.Id, []))]), ct);
        var chunks = hydration.Chunks.Where(c => c.ChunkId == c.HitId).ToDictionary(c => c.HitId);
        var documents = hydration.Documents.Where(d => d.Visible).ToDictionary(d => d.Id);
        var byId = collections.ToDictionary(c => c.Id);
        var results = new List<RetrievedChunk>(hits.Count);
        foreach (var hit in hits)
        {
            // A hit missing here went stale between search and hydration (swapped out, deleted, out of scope): dropped (§6.3).
            if (!chunks.TryGetValue(hit.Candidate.ChunkId, out var chunk) || !documents.TryGetValue(hit.Candidate.DocumentId, out var document) ||
                !byId.TryGetValue(hit.Candidate.CollectionId, out var collection))
            {
                continue;
            }

            results.Add(new RetrievedChunk(results.Count + 1, chunk.ChunkId, document.Id, hit.Candidate.TextHash, chunk.Text, chunk.HeadingPath ?? "",
                chunk.Page, chunk.PageEnd, hit.Score, hit.Scores, null, collection.Id, collection.Slug, hit.Candidate.Level,
                Cite(who, document, chunk.DocumentType, chunk.SourceModifiedAt, collection, request.AssistantId, hit.Candidate.Level)));
        }

        return results;
    }

    /// <summary>§10.1, §10.2: the title rule per identity kind, the modified-date fallbacks, and the download link for uploads.</summary>
    private Citation Cite(RagPrincipal who, HydratedDocument document, string? documentType, DateTimeOffset? chunkModified, CollectionEntry collection,
        Guid? assistantId, string? level = null)
    {
        var upload = document.IdentityKind == "upload";
        var title = (upload ? document.DisplayName ?? document.Title : document.Title) is { Length: > 0 } t ? t : "Dokument utan titel";
        var sourceType = document.IdentityKind switch { "url" => "web", "upload" => "file", var other => other };
        var downloadUrl = document.IdentityKind == "url" ? null : links.For(who, document.Id, collection.Id, assistantId, level ?? collection.Classification);
        return new Citation(document.Id, title, upload ? document.DisplayName : null, document.IdentityKind == "url" ? document.Url : null, downloadUrl,
            sourceType, documentType, document.MediaType, document.Language, chunkModified ?? document.SourceModifiedAt ?? document.FetchedAt);
    }

    private static HydratedDocument ReadDocument(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7),
        r.IsDBNull(8) ? null : r.GetInt32(8), r.GetGuid(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetBoolean(11));

    /// <summary>The settings resolved per collection (§12.1), rebuilt when the snapshot's version changes.</summary>
    private (RetrievalPlatformSettings, Dictionary<Guid, RetrievalSettings>) Settings(PolicySnapshot snapshot)
    {
        if (_settings is { } cached && cached.Version == snapshot.Version)
        {
            return (cached.Platform, cached.Collections);
        }

        var platform = RetrievalSettingsResolver.Platform(snapshot.RetrievalSettings);
        var collections = snapshot.Collections.Values.ToDictionary(c => c.Id, c => RetrievalSettingsResolver.Collection(platform, c.RetrievalSettings));
        _settings = (snapshot.Version, platform, collections);
        return (platform, collections);
    }

    /// <summary>A denial as the route answers it: 404s as <c>not_found</c> (the real reason stays in the decision for events), others by reason.</summary>
    private static RetrievalDeniedException Denied(AccessDecision decision) =>
        new(decision.HttpStatus, decision.HttpStatus == 404 ? KnowledgeCodes.NotFound : decision.Reason);

    private static double Ms(long started) => Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    [LoggerMessage(LogLevel.Error, "Collection {CollectionId} skipped in a retrieval ({Reason}); it was not searched")]
    private static partial void LogSkipped(ILogger logger, Guid collectionId, string reason);
}
