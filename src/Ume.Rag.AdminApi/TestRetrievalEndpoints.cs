using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ume.Rag.Answering;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.Retrieval;
using Ume.Rag.Retrieval.Postgres;

namespace Ume.Rag.AdminApi;

/// <summary>
/// The retrieval-only test panel (admin design §4.5 "Collections and retrieval", §4.9): one collection, published or not,
/// searched as the knowledge role searches it (<see cref="RetrievalEngine"/> with the collection's resolved settings, its
/// sources' <c>demote</c> patterns and every level), hydrated, and shown with the per-list scores and coverage. Nothing is
/// stored; no audit entry and no 6007 (the content is the caller's own department's, §4.9). The query embedding runs at
/// the profile's residency ceiling with the in-process cache only, keyed with a per-process random key (§4.9).
/// </summary>
public static class TestRetrievalEndpoints
{
    /// <summary>The candidate text shown per hit (§4.9: 600 characters, expandable through the document's chunks).</summary>
    public const int TextLength = 600;

    public static RouteGroupBuilder MapTestRetrieval(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        api.MapPost("/collections/{id:guid}/test-retrieval", async (Guid id, TestRetrievalRequest request, AdminContext admin, AdminRetrieval retrieval,
                    CancellationToken ct) =>
                await retrieval.RunAsync(admin, id, request, ct))
            .WithTags("Collections").RequireAuthorization(AdminPolicies.Edit).WithName("TestRetrieval")
            .WithSummary("Search one collection (also unpublished) as the knowledge role does; nothing is stored");
        return api;
    }
}

/// <summary>The search core for the admin's retrieval test, on the process's one data source (issue #22).</summary>
public sealed class AdminRetrieval(NpgsqlDataSource dataSource, IHttpClientFactory clients, TimeProvider time, ILogger<GatewayQueryEmbedder> logger) : IDisposable
{
    private readonly RetrievalEngine _engine = CreateEngine(dataSource);
    private readonly PostgresHydrator _hydrator = new(dataSource);

    // §4.9: query embeddings use the in-process cache only, keyed with a per-process random key.
    private readonly GatewayQueryEmbedder _embedder = new(clients, new QueryEmbeddingOptions(), RandomNumberGenerator.GetBytes(32), time, logger);

    private static RetrievalEngine CreateEngine(NpgsqlDataSource dataSource)
    {
        var index = new PostgresSearchIndex(dataSource);
        return new RetrievalEngine(index, new ScopeSizeCache(index, TimeProvider.System));
    }

    public async Task<TestRetrievalResult> RunAsync(AdminContext admin, Guid id, TestRetrievalRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(request);
        var collection = await CollectionEndpoints.Scoped(admin).AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct)
            ?? throw AdminFaults.Of(AdminErrorCodes.NotFound);
        var profile = await admin.Db.EmbeddingProfiles.AsNoTracking().Where(p => p.Id == collection.EmbeddingProfileId)
            .Select(p => new EmbeddingProfileEntry(p.Id, p.Name, p.GatewayModel, p.Dimensions, p.MaxResidency, p.State, p.CanaryText, p.RequestDimensions))
            .SingleOrDefaultAsync(ct) ?? throw AdminFaults.Of(AdminErrorCodes.NoEmbeddingProfile);
        var platform = RetrievalSettingsResolver.Platform(await admin.Db.Settings.AsNoTracking().Select(s => s.Retrieval).SingleOrDefaultAsync(ct));
        var settings = RetrievalSettingsResolver.Collection(platform, collection.RetrievalSettings);
        var sources = await admin.Db.Sources.AsNoTracking().Where(s => s.CollectionId == id && s.DeletedAt == null)
            .Select(s => new { s.Id, s.DemoteRules }).ToListAsync(ct);
        var scope = new SearchScope(profile.Id, profile.Dimensions, [new CollectionInScope(id, settings)], Classification.All, platform)
        {
            Demote = sources.Select(s => (s.Id, Patterns: DemoteAdjuster.Compile(s.DemoteRules))).Where(s => s.Patterns.Count > 0)
                .ToDictionary(s => s.Id, s => s.Patterns),
        };

        SearchResult search;
        try
        {
            search = await _engine.SearchAsync(new SearchRequest(request.Question, scope, request.TopK, request.Mode,
                Embed: request.Mode == RetrievalMode.Keyword ? null : (query, token) => _embedder.EmbedAsync(profile, query, null, token)), ct);
        }
        catch (GatewayKeyMissingException missing)
        {
            // A configuration gap of this process (#237): said as such, not as an outage.
            throw AdminFaults.Of(AdminErrorCodes.GatewayKeyMissing,
                new Dictionary<string, string> { ["model"] = profile.GatewayModel, ["ceiling"] = ResidencyCeiling.ToHeader(missing.Ceiling) });
        }
        catch (Exception ex) when (ex is SearchUnavailableException or QueryEmbeddingUnavailableException)
        {
            throw AdminFaults.Of(AdminErrorCodes.SearchUnavailable);
        }
        catch (ArgumentException)
        {
            throw AdminFaults.Field("question", AdminErrorCodes.InvalidValue);
        }

        var hydration = await _hydrator.HydrateAsync(new HydrationRequest([.. search.Hits.Select(h => h.Candidate.ChunkId)],
            [.. search.Hits.Select(h => h.Candidate.DocumentId).Distinct()], Classification.All, [id], [.. sources.Select(s => s.Id)]), ct);
        var chunks = hydration.Chunks.Where(c => c.ChunkId == c.HitId).ToDictionary(c => c.HitId);
        var documents = hydration.Documents.Where(d => d.Visible).ToDictionary(d => d.Id);
        var candidates = new List<TestRetrievalCandidate>();
        foreach (var hit in search.Hits)
        {
            // A hit missing from the hydration is stale (swapped out or deleted meanwhile) and is dropped, as the knowledge role does.
            if (!chunks.TryGetValue(hit.Candidate.ChunkId, out var chunk) || !documents.TryGetValue(hit.Candidate.DocumentId, out var document))
            {
                continue;
            }

            candidates.Add(new TestRetrievalCandidate(candidates.Count + 1, document.Id, chunk.ChunkId, document.Title ?? document.DisplayName, document.Url,
                document.DisplayName, chunk.HeadingPath, chunk.Text.Length <= TestRetrievalEndpoints.TextLength ? chunk.Text : chunk.Text[..TestRetrievalEndpoints.TextLength],
                chunk.Text.Length > TestRetrievalEndpoints.TextLength, hit.Scores.VectorDistance, hit.Scores.VectorRank, hit.Scores.KeywordScore, hit.Scores.KeywordRank,
                hit.Scores.Fused, hit.Score));
        }

        return new TestRetrievalResult(new TestRetrievalTarget(id, collection.Name, collection.PublishedAt is not null), search.NormalizedQuery, search.Coverage,
            search.Degraded, candidates, search.Timings);
    }

    public void Dispose()
    {
        _engine.Dispose();
        _embedder.Dispose();
    }
}

public sealed record TestRetrievalRequest
{
    [Required, StringLength(1000, MinimumLength = 1)]
    public string Question { get; init; } = "";

    [Range(1, SearchRequest.MaxTopK)]
    public int TopK { get; init; } = 10;

    /// <summary><c>Hybrid</c> (default), <c>Vector</c> or <c>Keyword</c>.</summary>
    public RetrievalMode Mode { get; init; } = RetrievalMode.Hybrid;
}

/// <summary>The searched collection; <see cref="Published"/> false is the "not published" flag of §4.9.</summary>
public sealed record TestRetrievalTarget(Guid Id, string Name, bool Published);

/// <summary>
/// One candidate in final order: the document (title, URL or file name), heading path, the first 600 characters
/// (<see cref="Truncated"/> when there is more), and the scores per list (§4.9).
/// </summary>
public sealed record TestRetrievalCandidate(
    int Rank, Guid DocumentId, Guid ChunkId, string? Title, string? Url, string? FileName, string? HeadingPath, string Text, bool Truncated,
    double? Distance, int? VectorRank, double? KeywordScore, int? KeywordRank, double? Fused, double Score);

public sealed record TestRetrievalResult(
    TestRetrievalTarget Collection, string NormalizedQuery, Coverage Coverage, string? Degraded, IReadOnlyList<TestRetrievalCandidate> Candidates,
    SearchTimings Timings);
