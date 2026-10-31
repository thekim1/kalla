using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Ume.Rag.Domain.Retrieval;

namespace Ume.Rag.Answering;

/// <summary>
/// Counters only (knowledge design §4.7): per (Europe/Stockholm day, document, text hash, collection, level) a
/// <c>Retrieved</c> count for every returned result and a <c>Cited</c> count for the ranks the chat role cites, aggregated
/// in memory and flushed every 30 s or at 5,000 keys through <c>content.rag_count_retrievals</c>, off the request path.
/// A crash loses at most one interval. No query text is involved.
/// </summary>
public sealed partial class RetrievalCounter(NpgsqlDataSource dataSource, TimeProvider time, ILogger<RetrievalCounter> logger) : BackgroundService
{
    public const int FlushAtKeys = 5000;
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    private ConcurrentDictionary<Key, Counts> _counts = new();
    private readonly SemaphoreSlim _flushNow = new(0, 1);

    /// <summary>Counts every result as retrieved.</summary>
    public void Retrieved(IEnumerable<RetrievedChunk> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        Add(results, retrieved: 1, cited: 0);
    }

    /// <summary>Counts the results with <paramref name="ranks"/> as cited.</summary>
    public void Cited(RetrievalResult result, IReadOnlyCollection<int> ranks)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(ranks);
        Add(result.Results.Where(r => ranks.Contains(r.Rank)), retrieved: 0, cited: 1);
    }

    private void Add(IEnumerable<RetrievedChunk> results, int retrieved, int cited)
    {
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), Stockholm).DateTime);
        var counts = _counts;
        foreach (var r in results)
        {
            var entry = counts.GetOrAdd(new Key(day, r.DocumentId, Convert.ToHexString(r.TextHash), r.CollectionId, r.Level), _ => new Counts());
            Interlocked.Add(ref entry.Retrieved, retrieved);
            Interlocked.Add(ref entry.Cited, cited);
        }

        if (counts.Count >= FlushAtKeys && _flushNow.CurrentCount == 0)
        {
            try
            {
                _flushNow.Release();
            }
            catch (SemaphoreFullException)
            {
                // a flush is already requested
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _flushNow.WaitAsync(FlushInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await FlushAsync(stoppingToken);
        }

        await FlushAsync(CancellationToken.None); // on shutdown
    }

    /// <summary>Writes and clears the current counts; a failed write keeps them for the next flush.</summary>
    public async Task<int> FlushAsync(CancellationToken cancellationToken)
    {
        var batch = Interlocked.Exchange(ref _counts, new ConcurrentDictionary<Key, Counts>());
        if (batch.IsEmpty)
        {
            return 0;
        }

        var written = 0;
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            foreach (var group in batch.GroupBy(e => e.Key.Day))
            {
                foreach (var chunk in group.Chunk(FlushAtKeys))
                {
                    await using var command = new NpgsqlCommand("SELECT content.rag_count_retrievals($1, $2, $3, $4, $5, $6, $7)", connection)
                    {
                        Parameters =
                        {
                            new() { Value = group.Key, NpgsqlDbType = NpgsqlDbType.Date },
                            new() { Value = chunk.Select(e => e.Key.DocumentId).ToArray() },
                            new() { Value = chunk.Select(e => Convert.FromHexString(e.Key.TextHashHex)).ToArray() },
                            new() { Value = chunk.Select(e => e.Key.CollectionId).ToArray() },
                            new() { Value = chunk.Select(e => e.Key.Level).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Varchar },
                            new() { Value = chunk.Select(e => Volatile.Read(ref e.Value.Retrieved)).ToArray() },
                            new() { Value = chunk.Select(e => Volatile.Read(ref e.Value.Cited)).ToArray() },
                        },
                    };
                    written += (int)(await command.ExecuteScalarAsync(cancellationToken))!;
                }
            }

            return written;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            // Put the counts back for the next flush (merged with what arrived meanwhile).
            foreach (var (key, counts) in batch)
            {
                var entry = _counts.GetOrAdd(key, _ => new Counts());
                Interlocked.Add(ref entry.Retrieved, counts.Retrieved);
                Interlocked.Add(ref entry.Cited, counts.Cited);
            }

            LogFlushFailed(logger, ex, batch.Count);
            return 0;
        }
    }

    public override void Dispose()
    {
        _flushNow.Dispose();
        base.Dispose();
    }

    private sealed record Key(DateOnly Day, Guid DocumentId, string TextHashHex, Guid CollectionId, string Level);

    private sealed class Counts
    {
        public int Retrieved;
        public int Cited;
    }

    [LoggerMessage(LogLevel.Warning, "Writing {Keys} retrieval counters failed; they are kept for the next flush")]
    private static partial void LogFlushFailed(ILogger logger, Exception ex, int keys);
}
