namespace Ume.Rag.Retrieval;

/// <summary>
/// Chunk counts per collection, refreshed every <see cref="RefreshInterval"/> (knowledge design §6.4), and the
/// exact-versus-HNSW rule built on them. A collection the counts do not know yet (new since the last refresh) counts
/// as empty, so it is searched exactly. A failed refresh keeps the previous counts; with none yet it throws.
/// <para>
/// Also the keyword stop list (§6.2, §20; Decisions "Keyword stop list before the pilot crawl"): the frequent lexemes
/// per collection, read with the same interval. Reading them scans the collections' search vectors, so a stale list is
/// refreshed in the background while searches use the previous one; only the first search waits for it.
/// </para>
/// </summary>
public sealed class ScopeSizeCache(IScopeSizeSource source, TimeProvider time, TimeSpan? refreshInterval = null) : IDisposable
{
    /// <summary>A lexeme in more than this share of the scope's chunks is dropped from statement K's query.</summary>
    public const double KeywordStopShare = 0.30;

    /// <summary>
    /// Scopes below this many chunks get no stop list: K is cheap there, and in a small collection a share says little
    /// (a topic word in 3 of 8 chunks is the word the question is about).
    /// </summary>
    public const long KeywordStopMinChunks = 1_000;

    /// <summary>
    /// Lexemes are kept per collection from this share up, so a scope of several collections can add up a lexeme's
    /// chunks: a lexeme under this share in a collection counts as absent there (the sum is a lower bound, so the list
    /// only errs towards keeping a term).
    /// </summary>
    public const double KeywordStatisticsShare = 0.10;

    /// <summary>Collections below this many chunks get no lexeme statistics (they move a scope's shares little).</summary>
    public const long KeywordStatisticsMinChunks = 100;

    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly Lock _lexemeLock = new();
    private Snapshot? _snapshot;
    private LexemeSnapshot? _lexemes;
    private Task<LexemeSnapshot?>? _lexemeRefresh;
    private int _lexemeGeneration;

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

    /// <summary>
    /// The lexemes statement K drops for <paramref name="scope"/>: those in more than <see cref="KeywordStopShare"/> of
    /// the scope's chunks, in a scope of at least <see cref="KeywordStopMinChunks"/>. Sorted, so the list is the same for
    /// the same statistics. Empty while no statistics could be read (K then runs with every term).
    /// </summary>
    public async ValueTask<IReadOnlyList<string>> StopLexemesAsync(SearchScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var size = await SizeAsync(scope, cancellationToken);
        if (size.ScopeChunks < KeywordStopMinChunks)
        {
            return [];
        }

        var lexemes = await LexemesAsync(cancellationToken);
        if (lexemes is null)
        {
            return [];
        }

        var chunks = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var id in scope.CollectionIds)
        {
            if (lexemes.ByCollection.TryGetValue((scope.EmbeddingProfileId, id), out var counts))
            {
                foreach (var (lexeme, count) in counts)
                {
                    chunks[lexeme] = chunks.GetValueOrDefault(lexeme) + count;
                }
            }
        }

        var limit = KeywordStopShare * size.ScopeChunks;
        return [.. chunks.Where(c => c.Value > limit).Select(c => c.Key).Order(StringComparer.Ordinal)];
    }

    /// <summary>Drops the counts and the lexeme statistics, so the next search reads them again (tests, and after a large load).</summary>
    public void Invalidate()
    {
        _snapshot = null;
        lock (_lexemeLock)
        {
            _lexemes = null;
            _lexemeRefresh = null;
            _lexemeGeneration++;
        }
    }

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

    /// <summary>
    /// The current lexeme statistics: fresh ones as they are; stale ones while a background refresh runs; none yet, the
    /// first read (awaited). Null when none could be read.
    /// </summary>
    private async ValueTask<LexemeSnapshot?> LexemesAsync(CancellationToken cancellationToken)
    {
        Task<LexemeSnapshot?> refresh;
        lock (_lexemeLock)
        {
            var current = _lexemes;
            if (current is not null && time.GetUtcNow() - current.ReadAt < RefreshInterval)
            {
                return current;
            }

            if (_lexemeRefresh is null || _lexemeRefresh.IsCompleted)
            {
                _lexemeRefresh = RefreshLexemesAsync(current, _lexemeGeneration);
            }

            if (current is not null)
            {
                return current;
            }

            refresh = _lexemeRefresh;
        }

        return await refresh.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the statistics (not cancelled by any one search, which may stop waiting). A failure keeps the previous
    /// statistics until the next interval, so a failing statement is not retried on every search; with none it gives null.
    /// </summary>
    private async Task<LexemeSnapshot?> RefreshLexemesAsync(LexemeSnapshot? previous, int generation)
    {
        await Task.Yield();
        LexemeSnapshot? next;
        try
        {
            var rows = await source.FrequentLexemesAsync(KeywordStatisticsMinChunks, KeywordStatisticsShare, CancellationToken.None);
            next = new LexemeSnapshot(
                time.GetUtcNow(),
                rows.GroupBy(r => (r.EmbeddingProfileId, r.CollectionId))
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<(string, long)>)[.. g.Select(r => (r.Lexeme, r.Chunks))]));
        }
#pragma warning disable CA1031 // Any failure leaves K without a stop list (or with the previous one), never fails a search.
        catch (Exception)
#pragma warning restore CA1031
        {
            next = previous is null ? null : previous with { ReadAt = time.GetUtcNow() };
        }

        lock (_lexemeLock)
        {
            if (next is not null && generation == _lexemeGeneration)
            {
                _lexemes = next;
            }
        }

        return next;
    }

    private sealed record LexemeSnapshot(DateTimeOffset ReadAt, Dictionary<(Guid Profile, Guid Collection), IReadOnlyList<(string Lexeme, long Chunks)>> ByCollection);

    private sealed record Snapshot(
        DateTimeOffset ReadAt,
        Dictionary<(Guid Profile, Guid Collection), long> ByCollection,
        Dictionary<Guid, long> ByProfile);
}

public readonly record struct ScopeSize(long ScopeChunks, long ProfileChunks);

/// <summary>How statement V runs for a scope; <see cref="ExactLarge"/> marks a large scope searched exactly.</summary>
public readonly record struct VectorPlan(VectorSearch Search, bool ExactLarge);
