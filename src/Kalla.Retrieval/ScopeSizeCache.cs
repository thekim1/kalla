using Microsoft.Extensions.Logging;

namespace Kalla.Retrieval;

/// <summary>
/// Chunk counts per collection, refreshed every <see cref="RefreshInterval"/> (knowledge design §6.4), and the
/// exact-versus-HNSW rule built on them. A collection the counts do not know yet (new since the last refresh) counts
/// as empty, so it is searched exactly. A failed refresh keeps the previous counts; with none yet it throws.
/// <para>
/// A collection linked with topics counts only its chunks that carry one of the link's tags (Decisions "M-2 open design
/// calls"), so a narrow topic in a large collection falls under the exact-search limit. The counts are kept per
/// (profile, collection, tag set), so the union of a link's topics is exact: a chunk with two of its tags counts once.
/// </para>
/// <para>
/// Also the keyword stop list (§6.2, §20; Decisions "Keyword stop list before the pilot crawl"): the frequent lexemes
/// per collection from <c>content."KeywordStatistics"</c>, which the worker keeps (A-29; kalla-api runs no <c>ts_stat</c>,
/// #499 step 2), read with the same interval. A stale list is refreshed in the background while searches use the
/// previous one; only the first search waits for it, unless
/// <see cref="WarmAsync"/> read them at start. A failed read keeps the previous statistics until the next interval; with
/// none yet it is retried after <see cref="FailedReadRetry"/>, not by every search (#333: each search used to wait for
/// the failing read again, up to the role's statement timeout).
/// </para>
/// <para>A failed read of either is logged at Warning with the statement's name (#499, B-25).</para>
/// </summary>
public sealed partial class ScopeSizeCache(
    IScopeSizeSource source, TimeProvider time, TimeSpan? refreshInterval = null, RetrievalMetrics? metrics = null, ILogger? logger = null)
    : IDisposable
{
    /// <summary>A lexeme in more than this share of the scope's chunks is dropped from statement K's query.</summary>
    public const double KeywordStopShare = 0.30;

    /// <summary>With no statistics yet, how long a failed read is not tried again (searches meanwhile run K with every term).</summary>
    public static readonly TimeSpan FailedReadRetry = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Scopes below this many chunks get no stop list: K is cheap there, and in a small collection a share says little
    /// (a topic word in 3 of 8 chunks is the word the question is about).
    /// </summary>
    public const long KeywordStopMinChunks = 1_000;

    /// <summary>
    /// The worker keeps lexemes per collection from this share up, so a scope of several collections can add up a lexeme's
    /// chunks: a lexeme under this share in a collection counts as absent there (the sum is a lower bound, so the list
    /// only errs towards keeping a term).
    /// </summary>
    public const double KeywordStatisticsShare = 0.10;

    /// <summary>Collections below this many chunks get no lexeme statistics from the worker (they move a scope's shares little).</summary>
    public const long KeywordStatisticsMinChunks = 100;

    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly Lock _lexemeLock = new();
    private Snapshot? _snapshot;
    private LexemeSnapshot? _lexemes;
    private Task<LexemeSnapshot?>? _lexemeRefresh;
    private int _lexemeGeneration;
    private DateTimeOffset? _lexemeFailedAt;

    public TimeSpan RefreshInterval { get; } = refreshInterval ?? TimeSpan.FromMinutes(10);

    /// <summary>
    /// The chunks in <paramref name="scope"/> and in its profile's whole index. A collection linked with topics adds the
    /// chunks carrying one of its topics (<see cref="CollectionInScope.Topics"/>), one without topics all its chunks.
    /// </summary>
    public async ValueTask<ScopeSize> SizeAsync(SearchScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var snapshot = await CurrentAsync(cancellationToken);
        long inScope = 0;
        foreach (var collection in scope.Collections)
        {
            var key = (scope.EmbeddingProfileId, collection.CollectionId);
            inScope += collection.Topics.Count == 0 ? snapshot.ByCollection.GetValueOrDefault(key) : TopicChunks(snapshot, key, collection.Topics);
        }

        return new ScopeSize(inScope, snapshot.ByProfile.GetValueOrDefault(scope.EmbeddingProfileId));
    }

    /// <summary>
    /// The lexemes statement K drops for <paramref name="scope"/>: those in more than <see cref="KeywordStopShare"/> of
    /// the scope's chunks, in a scope of at least <see cref="KeywordStopMinChunks"/> live chunks. A share is the rows'
    /// <c>Ndoc</c> over their stored <c>ChunkCount</c>, summed over the scope's collections (§6.2); a collection without
    /// rows (under 100 chunks, not computed yet, or no lexeme at 10 %) adds its live count and no chunks of any lexeme.
    /// Sorted, so the list is the same for the same statistics. Empty while no statistics could be read (K then runs with
    /// every term).
    /// </summary>
    public async ValueTask<IReadOnlyList<string>> StopLexemesAsync(SearchScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        // The lexeme statistics are per whole collection, so the shares are taken of the collections' whole counts, also
        // for a link with topics (a topic's own count would turn a collection-wide count into an inflated share).
        var snapshot = await CurrentAsync(cancellationToken);
        long scopeChunks = 0;
        foreach (var id in scope.CollectionIds)
        {
            scopeChunks += snapshot.ByCollection.GetValueOrDefault((scope.EmbeddingProfileId, id));
        }

        if (scopeChunks < KeywordStopMinChunks)
        {
            return [];
        }

        var lexemes = await LexemesAsync(cancellationToken);
        if (lexemes is null)
        {
            return [];
        }

        var chunks = new Dictionary<string, long>(StringComparer.Ordinal);
        long statisticsChunks = 0;
        foreach (var id in scope.CollectionIds)
        {
            var key = (scope.EmbeddingProfileId, id);
            if (lexemes.ByCollection.TryGetValue(key, out var statistics))
            {
                statisticsChunks += statistics.ChunkCount;
                foreach (var (lexeme, count) in statistics.Lexemes)
                {
                    chunks[lexeme] = chunks.GetValueOrDefault(lexeme) + count;
                }
            }
            else
            {
                statisticsChunks += snapshot.ByCollection.GetValueOrDefault(key);
            }
        }

        var limit = KeywordStopShare * statisticsChunks;
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
            _lexemeFailedAt = null;
            _lexemeGeneration++;
        }
    }

    /// <summary>Whether lexeme statistics are held (K can drop frequent terms).</summary>
    public bool HasKeywordStatistics
    {
        get
        {
            lock (_lexemeLock)
            {
                return _lexemes is not null;
            }
        }
    }

    /// <summary>
    /// Reads the chunk counts and the lexeme statistics now, so the first search does not wait for them (the api's
    /// start, before readiness). Returns whether the statistics could be read; a failure is not thrown.
    /// </summary>
    public async Task<bool> WarmAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CurrentAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The counts are read again by the first search.
        }

        return await LexemesAsync(cancellationToken) is not null;
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
                    counts.GroupBy(c => (c.EmbeddingProfileId, c.CollectionId)).ToDictionary(g => g.Key, g => g.Sum(c => c.Chunks)),
                    counts.GroupBy(c => c.EmbeddingProfileId).ToDictionary(g => g.Key, g => g.Sum(c => c.Chunks)),
                    counts.Where(c => c.Tags.Count > 0)
                        .GroupBy(c => (c.EmbeddingProfileId, c.CollectionId))
                        .ToDictionary(g => g.Key, g => (IReadOnlyList<CollectionChunkCount>)[.. g]));
                _snapshot = snapshot;
                return snapshot;
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                if (logger is not null)
                {
                    LogReadFailed(logger, e, "CountChunks", snapshot is null ? "none yet" : "the previous ones");
                }

                if (snapshot is null)
                {
                    throw;
                }

                return snapshot; // keep the previous counts; the next search tries again
            }
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>The chunks of a collection that carry one of <paramref name="topics"/> (each tag set counted once).</summary>
    private static long TopicChunks(Snapshot snapshot, (Guid, Guid) key, IReadOnlyList<string> topics)
    {
        if (!snapshot.Tagged.TryGetValue(key, out var tagSets))
        {
            return 0;
        }

        long chunks = 0;
        foreach (var tagSet in tagSets)
        {
            foreach (var tag in tagSet.Tags)
            {
                if (topics.Contains(tag, StringComparer.Ordinal))
                {
                    chunks += tagSet.Chunks;
                    break;
                }
            }
        }

        return chunks;
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

            // A failed read with nothing to fall back on is not tried again at once: no search waits for it again meanwhile.
            if (current is null && _lexemeFailedAt is { } failed && time.GetUtcNow() - failed < FailedReadRetry &&
                _lexemeRefresh is { IsCompleted: true })
            {
                return null;
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
            var rows = await source.KeywordStatisticsAsync(CancellationToken.None);
            next = new LexemeSnapshot(
                time.GetUtcNow(),
                rows.GroupBy(r => (r.EmbeddingProfileId, r.CollectionId))
                    .ToDictionary(g => g.Key, g => new CollectionLexemes(g.Max(r => r.ChunkCount), [.. g.Select(r => (r.Lexeme, r.Ndoc))])));
        }
#pragma warning disable CA1031 // Any failure leaves K without a stop list (or with the previous one), never fails a search.
        catch (Exception e)
#pragma warning restore CA1031
        {
            if (logger is not null)
            {
                LogReadFailed(logger, e, "KeywordStatistics", previous is null ? "none yet" : "the previous ones");
            }

            next = previous is null ? null : previous with { ReadAt = time.GetUtcNow() };
            metrics?.KeywordStatisticsUnavailable();
            lock (_lexemeLock)
            {
                if (generation == _lexemeGeneration)
                {
                    _lexemeFailedAt = time.GetUtcNow();
                }
            }
        }

        lock (_lexemeLock)
        {
            if (next is not null && generation == _lexemeGeneration)
            {
                _lexemes = next;
                _lexemeFailedAt = null;
            }
        }

        return next;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The search statistics statement {Statement} failed; searches keep {Kept}")]
    private static partial void LogReadFailed(ILogger logger, Exception exception, string statement, string kept);

    private sealed record LexemeSnapshot(DateTimeOffset ReadAt, Dictionary<(Guid Profile, Guid Collection), CollectionLexemes> ByCollection);

    /// <summary>One (profile, collection)'s rows: the stored chunk count and each lexeme's <c>Ndoc</c>.</summary>
    private sealed record CollectionLexemes(long ChunkCount, IReadOnlyList<(string Lexeme, long Ndoc)> Lexemes);

    private sealed record Snapshot(
        DateTimeOffset ReadAt,
        Dictionary<(Guid Profile, Guid Collection), long> ByCollection,
        Dictionary<Guid, long> ByProfile,
        Dictionary<(Guid Profile, Guid Collection), IReadOnlyList<CollectionChunkCount>> Tagged);
}

public readonly record struct ScopeSize(long ScopeChunks, long ProfileChunks);

/// <summary>How statement V runs for a scope; <see cref="ExactLarge"/> marks a large scope searched exactly.</summary>
public readonly record struct VectorPlan(VectorSearch Search, bool ExactLarge);
