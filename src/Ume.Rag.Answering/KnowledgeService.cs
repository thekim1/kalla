using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Ports;
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
/// The stored original of a visible document without a public URL (knowledge design §3.5): its blob and what the
/// route's headers need. <see cref="Level"/> decides decryption (<c>public</c> blobs are stored as they are).
/// </summary>
public sealed record DocumentContent(Guid DocumentId, BlobKey Key, string Level, string? MediaType, string? DisplayName, byte[] ContentHash);

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
    ILogger<KnowledgeService> logger,
    int neighbourRadius = SectionAssembler.DefaultNeighbourRadius) : IRetriever
{
    /// <summary>At most 20 collections per query (§4.2).</summary>
    public const int MaxCollections = 20;

    private (long Version, RetrievalPlatformSettings Platform, Dictionary<Guid, RetrievalSettings> Collections)? _settings;
    private (long Version, Dictionary<Guid, IReadOnlyList<DemotePattern>> BySource)? _demote;

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
        var demote = Demote(snapshot);
        var topics = Topics(request.AssistantId, snapshot);
        var scope = new SearchScope(profile.Id, profile.Dimensions,
            [.. collections.Select(c => new CollectionInScope(c.Id, settings.GetValueOrDefault(c.Id, platform.Defaults)) { Topics = topics.GetValueOrDefault(c.Id, []) })],
            allowedLevels, platform)
        {
            // §6.5: the demote patterns of the searched collections' sources.
            Demote = collections.SelectMany(c => snapshot.SourcesByCollection.GetValueOrDefault(c.Id, []))
                .Where(demote.ContainsKey).ToDictionary(s => s, s => demote[s]),
        };
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
        var (results, sections) = await HydrateAsync(who, request, snapshot, collections, allowedLevels, search.Hits, settings,
            platform.ExpandTotalMaxTokens, ct);
        counter.Retrieved(results);
        var timings = new RetrievalTimings(policyMs, search.Timings.KeywordMs, search.Timings.EmbeddingMs, search.Timings.VectorMs,
            Ms(hydrateStarted), Ms(started));
        return new RetrievalResult(results, sections, search.Coverage, search.Degraded, snapshot.Version, timings);
    }

    public async Task<DocumentInfo> GetDocumentAsync(RagPrincipal who, Guid documentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(who);
        var (row, collection) = await VisibleAsync(who, documentId, ct);
        return new DocumentInfo(Cite(who, row.Document, null, null, collection, null), row.Document.PageCount, collection.Id, collection.Slug);
    }

    /// <summary>
    /// <c>GET /v1/documents/{id}</c> with a download grant (§4.6): only the grant's document, re-checked at the current
    /// version. The citation carries no new link; the route links the grant it was given.
    /// </summary>
    public async Task<DocumentInfo> GetDocumentAsync(DownloadGrant grant, Guid documentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var (row, collection) = await GrantedAsync(grant, documentId, ct);
        return new DocumentInfo(Cite(null, row.Document, null, null, collection, null), row.Document.PageCount, collection.Id, collection.Slug);
    }

    /// <summary><c>GET /v1/documents/{id}/content</c> with a normal credential (§3.5): visible as for §3.4, then not a <c>url</c> document.</summary>
    public async Task<DocumentContent> FindContentAsync(RagPrincipal who, Guid documentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(who);
        var (row, _) = await VisibleAsync(who, documentId, ct);
        return Content(row);
    }

    /// <summary><c>GET /v1/documents/{id}/content?grant=</c> (§3.5, §4.6).</summary>
    public async Task<DocumentContent> FindContentAsync(DownloadGrant grant, Guid documentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var (row, _) = await GrantedAsync(grant, documentId, ct);
        return Content(row);
    }

    /// <summary>
    /// The blob of a visible document; <c>url</c> documents answer 404 <c>content_not_served</c> after the access check
    /// (their citation links the page; serving crawled HTML from our origin would be an XSS vector).
    /// </summary>
    private static DocumentContent Content(DocumentRow row)
    {
        var document = row.Document;
        if (document.IdentityKind != IdentityKinds.Upload || row.ContentHash is not { Length: > 0 } hash ||
            !row.CanonicalIdentity.StartsWith("upload:", StringComparison.Ordinal) || !Guid.TryParse(row.CanonicalIdentity.AsSpan("upload:".Length), out var slot))
        {
            throw new RetrievalDeniedException(404, KnowledgeCodes.ContentNotServed);
        }

        // The upload blob is the document's raw content (connectors §9.1): uploads/{sourceId}/{slot}/{sha256}.
        var key = new BlobKey($"uploads/{document.SourceId:D}/{slot:D}/{Convert.ToHexStringLower(hash)}");
        return new DocumentContent(document.Id, key, row.Level, document.MediaType, document.DisplayName, hash);
    }

    /// <summary>§3.4: the document has a live chunk set in a collection <paramref name="who"/> may search; else 404.</summary>
    private async Task<(DocumentRow, CollectionEntry)> VisibleAsync(RagPrincipal who, Guid documentId, CancellationToken ct)
    {
        var snapshot = snapshots.Current;
        var visible = Searchable(who, snapshot, snapshots.IsConfirmed).ToList();
        var sources = visible.SelectMany(c => snapshot.SourcesByCollection.GetValueOrDefault(c.Id, [])).ToArray();
        if (sources.Length == 0 || await DocumentAsync(documentId, Levels.UpTo(who.Client.Ceiling), sources, ct) is not { Document.Visible: true } row)
        {
            throw new RetrievalDeniedException(404, KnowledgeCodes.NotFound)
            {
                Reason = DenyReasons.NotFound, ResourceType = "document", ResourceId = documentId.ToString("D"), Action = nameof(RagAction.ReadDocument),
            };
        }

        return (row, visible.First(c => snapshot.SourcesByCollection.GetValueOrDefault(c.Id, []).Contains(row.Document.SourceId)));
    }

    /// <summary>
    /// The check on use of a grant (§4.6) at the current confirmed version: the grant's document; client enabled; key
    /// usable; assistant active, published and still linked to the collection; collection live, within the client's
    /// ceiling; the document visible through the collection's sources. Every failure is 404 <c>not_found</c> with the real
    /// reason in <see cref="RetrievalDeniedException.Reason"/> (event 5002).
    /// </summary>
    private async Task<(DocumentRow, CollectionEntry)> GrantedAsync(DownloadGrant grant, Guid documentId, CancellationToken ct)
    {
        var snapshot = snapshots.Current;
        if (!snapshots.IsConfirmed)
        {
            throw new RetrievalDeniedException(503, DenyReasons.ConfigUnconfirmed);
        }

        var reason = GrantRefusal(grant, documentId, snapshot);
        if (reason is null && grant.KeyId is { } keyId && !await KeyUsableAsync(keyId, grant.ClientId, ct))
        {
            reason = "key_unusable";
        }

        CollectionEntry? collection = null;
        DocumentRow? row = null;
        if (reason is null)
        {
            collection = snapshot.Collections[grant.CollectionId];
            row = await DocumentAsync(documentId, Levels.UpTo(collection.Classification), [.. snapshot.SourcesByCollection.GetValueOrDefault(collection.Id, [])], ct);
            reason = row is { Document.Visible: true } ? null : DenyReasons.NotFound;
        }

        if (reason is not null)
        {
            LogGrantRefused(logger, documentId, reason);
            throw new RetrievalDeniedException(404, KnowledgeCodes.NotFound)
            {
                Reason = reason, ResourceType = "document", ResourceId = documentId.ToString("D"), Action = nameof(RagAction.ReadDocument),
            };
        }

        return (row!, collection!);
    }

    /// <summary>The in-memory half of the grant check; null when it passes.</summary>
    private static string? GrantRefusal(DownloadGrant grant, Guid documentId, PolicySnapshot snapshot)
    {
        if (grant.DocumentId != documentId)
        {
            return "grant_scope";
        }

        if (!snapshot.Clients.TryGetValue(grant.ClientId, out var client))
        {
            return DenyReasons.ClientUnknown;
        }

        if (!client.IsEnabled)
        {
            return DenyReasons.ClientDisabled;
        }

        if (!snapshot.Collections.TryGetValue(grant.CollectionId, out var collection) || !collection.IsLive)
        {
            return DenyReasons.NotFound;
        }

        if (!Levels.AtMost(collection.Classification, client.Ceiling))
        {
            return DenyReasons.CeilingExceeded;
        }

        if (grant.AssistantId is { } assistantId && (!snapshot.Assistants.TryGetValue(assistantId, out var assistant) ||
            assistant.State != AssistantStates.Active || assistant.Published is not { } published || !published.CollectionIds.Contains(collection.Id)))
        {
            return DenyReasons.NotFound;
        }

        return null;
    }

    private async Task<bool> KeyUsableAsync(Guid keyId, Guid clientId, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(KeyUsableStatement);
        command.Parameters.Add(new NpgsqlParameter { Value = keyId });
        command.Parameters.Add(new NpgsqlParameter { Value = clientId });
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task<DocumentRow?> DocumentAsync(Guid documentId, IReadOnlyList<string> levels, Guid[] sources, CancellationToken ct)
    {
        await using var command = dataSource.CreateCommand(DocumentStatement);
        command.Parameters.Add(new NpgsqlParameter { Value = documentId });
        command.Parameters.Add(new NpgsqlParameter { Value = levels.ToArray() });
        command.Parameters.Add(new NpgsqlParameter { Value = sources });
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new DocumentRow(ReadDocument(reader), reader.GetString(12), reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetFieldValue<byte[]>(14))
            : null;
    }

    /// <summary>Statement D of §6.3 for one document, plus the content facts of §3.5.</summary>
    private const string DocumentStatement = """
        SELECT d."Id", d."Title", d."Url", d."IdentityKind", d."MediaType", d."Language", d."SourceModifiedAt",
               d."FetchedAt", d."PageCount", d."SourceId", d."DisplayName",
               d."SourceId" = ANY ($3)
                 AND EXISTS (SELECT 1 FROM content."ChunkSets" cs
                             WHERE cs."Id" = d."LiveChunkSetId" AND cs."State" = 'live') AS "Visible",
               d."Classification", d."CanonicalIdentity", d."ContentHash"
        FROM content."Documents" d
        WHERE d."Id" = $1 AND d."Classification" = ANY ($2) AND d."DeletedAt" IS NULL
        """;

    /// <summary>The key of a grant is usable (<c>ApiKey.IsUsable</c>: not revoked, enabled, not expired, within any grace).</summary>
    private const string KeyUsableStatement = """
        SELECT EXISTS (SELECT 1 FROM config."Keys" k
                       WHERE k."Id" = $1 AND k."ClientId" = $2 AND k."IsEnabled"
                         AND (k."RevokedAt" IS NULL OR k."RevokedAt" > now())
                         AND (k."ExpiresAt" IS NULL OR k."ExpiresAt" > now())
                         AND (k."GraceUntil" IS NULL OR k."GraceUntil" > now()))
        """;

    private sealed record DocumentRow(HydratedDocument Document, string Level, string CanonicalIdentity, byte[]? ContentHash);

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
            throw Denied(decision, "assistant", assistantId, action);
        }

        var assistant = snapshot.Assistants[assistantId];
        var linked = assistant.Published!.CollectionIds;
        if (narrow is { Count: > 0 } && narrow.FirstOrDefault(id => !linked.Contains(id)) is var outside && outside != Guid.Empty)
        {
            throw new RetrievalDeniedException(404, KnowledgeCodes.CollectionNotFound, outside.ToString())
            {
                Reason = "collection_not_linked", ResourceType = "collection", ResourceId = outside.ToString("D"), Action = action.ToString(),
            };
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

            if (who.Key is { CollectionScopes.Count: > 0 } key && !key.CollectionScopes.Contains(id))
            {
                LogSkipped(logger, id, "outside_key_scope"); // identity §4.3 step 6: a collection-scoped key searches only its collections
                continue;
            }

            collections.Add(collection);
        }

        if (collections.Count == 0 && who.Key is { CollectionScopes.Count: > 0 })
        {
            // The scoped collections are not live: nothing this key may search through the assistant.
            throw new RetrievalDeniedException(404, KnowledgeCodes.NotFound)
            {
                Reason = DenyReasons.KeyScope, Step = "6", ResourceType = "assistant", ResourceId = assistantId.ToString("D"), Action = action.ToString(),
            };
        }

        return (collections, assistant.Classification);
    }

    /// <summary>
    /// §4.2: the topics of the assistant's published collection links; a direct caller (no assistant) searches whole
    /// collections.
    /// </summary>
    private static Dictionary<Guid, IReadOnlyList<string>> Topics(Guid? assistantId, PolicySnapshot snapshot)
    {
        var topics = new Dictionary<Guid, IReadOnlyList<string>>();
        if (assistantId is { } id && snapshot.Assistants.GetValueOrDefault(id)?.Published is { } published)
        {
            foreach (var link in published.CollectionLinks)
            {
                topics.TryAdd(link.CollectionId, link.Tags);
            }
        }

        return topics;
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
                    throw Denied(decision, "collection", id, RagAction.Search);
                }

                if (!decision.Allowed || !snapshot.Collections.TryGetValue(id, out var collection) || !collection.IsLive)
                {
                    // One 404 for unknown, unpublished, deleted, outside the audience or above the ceiling (§4.4); the reason is for events only.
                    throw new RetrievalDeniedException(404, KnowledgeCodes.CollectionNotFound, id.ToString())
                    {
                        Reason = decision.Allowed ? "collection_not_live" : decision.Reason, Step = decision.Allowed ? null : decision.Step,
                        ResourceType = "collection", ResourceId = id.ToString("D"), Action = nameof(RagAction.Search),
                    };
                }

                collections.Add(collection);
            }
        }

        var level = collections.Select(c => c.Classification).Aggregate(Classification.Public, Levels.Max);
        return (collections, level);
    }

    /// <summary>
    /// Statement H+D, then the results in rank order (stale hits dropped) and, for <see cref="ExpandMode.Section"/>, the
    /// sections (§9) with each result's <c>SectionRef</c>.
    /// </summary>
    private async Task<(List<RetrievedChunk>, IReadOnlyList<SectionText>)> HydrateAsync(RagPrincipal who, RetrievalRequest request, PolicySnapshot snapshot,
        List<CollectionEntry> collections, IReadOnlyList<string> levels, IReadOnlyList<SearchHit> hits, Dictionary<Guid, RetrievalSettings> settings,
        int totalMaxTokens, CancellationToken ct)
    {
        if (hits.Count == 0)
        {
            return ([], []);
        }

        var expand = request.Expand == ExpandMode.Section;
        var hydration = await hydrator.HydrateAsync(new HydrationRequest(
            [.. hits.Select(h => h.Candidate.ChunkId)], [.. hits.Select(h => h.Candidate.DocumentId).Distinct()], levels,
            [.. collections.Select(c => c.Id)], [.. collections.SelectMany(c => snapshot.SourcesByCollection.GetValueOrDefault(c.Id, []))],
            expand ? neighbourRadius : 0), ct);
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

        if (!expand || results.Count == 0)
        {
            return (results, []);
        }

        var assembly = SectionAssembler.Assemble(
            [.. results.Select(r => new SectionHit(r.ChunkId, r.DocumentId, settings.TryGetValue(r.CollectionId, out var s) ? s.ExpandMaxTokens : RetrievalSettings.Default.ExpandMaxTokens))],
            hydration.Chunks, neighbourRadius, totalMaxTokens);
        for (var i = 0; i < results.Count; i++)
        {
            if (assembly.SectionOf.TryGetValue(results[i].ChunkId, out var section))
            {
                results[i] = results[i] with { SectionRef = section };
            }
        }

        return (results, [.. assembly.Sections.Select(a => new SectionText(a.Id, a.DocumentId, a.HeadingPath, a.Text, a.TokenEstimate, a.PageStart,
            a.PageEnd, a.ChunkIds, a.Truncated))]);
    }

    /// <summary>§10.1, §10.2: the title rule per identity kind, the modified-date fallbacks, and the download link for uploads.</summary>
    private Citation Cite(RagPrincipal? who, HydratedDocument document, string? documentType, DateTimeOffset? chunkModified, CollectionEntry collection,
        Guid? assistantId, string? level = null)
    {
        var upload = document.IdentityKind == "upload";
        var title = (upload ? document.DisplayName ?? document.Title : document.Title) is { Length: > 0 } t ? t : "Dokument utan titel";
        var sourceType = document.IdentityKind switch { "url" => "web", "upload" => "file", var other => other };
        var downloadUrl = document.IdentityKind == "url" || who is null ? null : links.For(who, document.Id, collection.Id, assistantId, level ?? collection.Classification);
        return new Citation(document.Id, title, upload ? document.DisplayName : null, document.IdentityKind == "url" ? document.Url : null, downloadUrl,
            sourceType, documentType, document.MediaType, document.Language, chunkModified ?? document.SourceModifiedAt ?? document.FetchedAt);
    }

    private static HydratedDocument ReadDocument(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7),
        r.IsDBNull(8) ? null : r.GetInt32(8), r.GetGuid(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetBoolean(11));

    /// <summary>The settings resolved per collection (§12.1), rebuilt when the snapshot's version changes.</summary>
    /// <summary>The sources' compiled demote patterns, once per config version (knowledge design §6.5).</summary>
    private Dictionary<Guid, IReadOnlyList<DemotePattern>> Demote(PolicySnapshot snapshot)
    {
        if (_demote is { } cached && cached.Version == snapshot.Version)
        {
            return cached.BySource;
        }

        var bySource = snapshot.DemoteRules.Select(r => (r.Key, Patterns: DemoteAdjuster.Compile(r.Value)))
            .Where(r => r.Patterns.Count > 0).ToDictionary(r => r.Key, r => r.Patterns);
        _demote = (snapshot.Version, bySource);
        return bySource;
    }

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
    private static RetrievalDeniedException Denied(AccessDecision decision, string resourceType, Guid resourceId, RagAction action) =>
        new(decision.HttpStatus, decision.HttpStatus == 404 ? KnowledgeCodes.NotFound : decision.Reason)
        {
            Reason = decision.Reason, Step = decision.Step, ResourceType = resourceType, ResourceId = resourceId.ToString("D"), Action = action.ToString(),
        };

    private static double Ms(long started) => Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    [LoggerMessage(LogLevel.Information, "Download grant for document {DocumentId} refused at use: {Reason}")]
    private static partial void LogGrantRefused(ILogger logger, Guid documentId, string reason);

    [LoggerMessage(LogLevel.Error, "Collection {CollectionId} skipped in a retrieval ({Reason}); it was not searched")]
    private static partial void LogSkipped(ILogger logger, Guid collectionId, string reason);
}
