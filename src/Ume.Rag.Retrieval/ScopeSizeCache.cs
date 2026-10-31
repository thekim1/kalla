namespace Ume.Rag.Retrieval;

/// <summary>
/// Chunk counts per collection, refreshed every <see cref="RefreshInterval"/> (knowledge design §6.4), and the
/// exact-versus-HNSW rule built on them. A collection the counts do not know yet (new since the last refresh) counts
/// as empty, so it is searched exactly. A failed refresh keeps the previous counts; with none yet it throws.
/// </summary>
public sealed class ScopeSizeCache(IScopeSizeSource source, TimeProvider time, TimeSpan? refreshInterval = null) : IDisposable
{
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private Snapshot? _snapshot;

    public TimeSpan RefreshInterval { get; } = refreshInterval ?? TimeSpan.FromMinutes(10);

    /// <summary>The chunks in <paramref name="scope"/> and in its profile's whole index.</summary>
    public async ValueTask<ScopeSize> SizeAsync(SearchScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var snapshot = await CurrentAsync(cancellationToken);
        long inScope = 0;
        foreach (var id in scope.CollectionIds)
        {
            inScope += snapshot.ByCollection.GetValueOrDefault((scope.EmbeddingProfileId, id));
        }

        return new ScopeSize(inScope, snapshot.ByProfile.GetValueOrDefault(scope.EmbeddingProfileId));
    }

    /// <summary>Drops the counts, so the next search reads them again (tests, and after a large load).</summary>
    public void Invalidate() => _snapshot = null;

    public void Dispose() => _refresh.Dispose();

    /// <summary>
    /// The rule (knowledge design §6.4): a scope of at most <c>exactScanMaxChunks</c> chunks is exact; a larger one uses
    /// HNSW when its share of the profile's index is at least <c>minHnswSelectivity</c>, else it is exact too (the
    /// <c>rag.retrieval.exact_large</c> case).
    /// </summary>
    public static VectorPlan Choose(ScopeSize size, RetrievalPlatformSettings platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        if (size.ScopeChunks <= platform.ExactScanMaxChunks)
        {
            return new VectorPlan(VectorSearch.Exact, ExactLarge: false);
        }

        var share = size.ProfileChunks <= 0 ? 1.0 : (double)size.ScopeChunks / size.ProfileChunks;
        return share >= platform.MinHnswSelectivity
            ? new VectorPlan(VectorSearch.Approximate, ExactLarge: false)
            : new VectorPlan(VectorSearch.Exact, ExactLarge: true);
    }

    private async ValueTask<Snapshot> CurrentAsync(CancellationToken cancellationToken)
    {
        var snapshot = _snapshot;
        if (snapshot is not null && time.GetUtcNow() - snapshot.ReadAt < RefreshInterval)
        {
            return snapshot;
        }

        await _refresh.WaitAsync(cancellationToken);
        try
        {
            snapshot = _snapshot;
            if (snapshot is not null && time.GetUtcNow() - snapshot.ReadAt < RefreshInterval)
            {
                return snapshot;
            }

            try
            {
                var counts = await source.CountChunksAsync(cancellationToken);
                snapshot = new Snapshot(
                    time.GetUtcNow(),
                    counts.ToDictionary(c => (c.EmbeddingProfileId, c.CollectionId), c => c.Chunks),
                    counts.GroupBy(c => c.EmbeddingProfileId).ToDictionary(g => g.Key, g => g.Sum(c => c.Chunks)));
                _snapshot = snapshot;
                return snapshot;
            }
            catch (Exception) when (snapshot is not null && !cancellationToken.IsCancellationRequested)
            {
                return snapshot; // keep the previous counts; the next search tries again
            }
        }
        finally
        {
            _refresh.Release();
        }
    }

    private sealed record Snapshot(
        DateTimeOffset ReadAt,
        Dictionary<(Guid Profile, Guid Collection), long> ByCollection,
        Dictionary<Guid, long> ByProfile);
}

public readonly record struct ScopeSize(long ScopeChunks, long ProfileChunks);

/// <summary>How statement V runs for a scope; <see cref="ExactLarge"/> marks a large scope searched exactly.</summary>
public readonly record struct VectorPlan(VectorSearch Search, bool ExactLarge);
