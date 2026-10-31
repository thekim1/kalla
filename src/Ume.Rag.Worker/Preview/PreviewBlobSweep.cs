using Microsoft.Extensions.Options;
using Ume.Rag.Domain.Ports;
using Ume.Rag.Worker.Jobs;

namespace Ume.Rag.Worker.Preview;

/// <summary>
/// Removes preview blobs 24 hours after their job wrote them (data-model §8, §9: <c>previews/</c>, worker sweep): a job's
/// prefix <c>previews/{jobId}/</c> goes once its newest blob is older than <see cref="StagePreview.BlobLifetime"/>. Runs
/// hourly on the instance with <c>Worker:Maintenance=true</c>; preview blobs are deleted directly (no tombstone).
/// </summary>
public sealed partial class PreviewBlobSweep(IBlobStore blobs, IOptions<WorkerOptions> options, TimeProvider time, ILogger<PreviewBlobSweep> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>Removes the expired job prefixes; returns how many.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var newest = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        await foreach (var blob in blobs.ListAsync(new BlobKey(PreviewBlobs.Area), cancellationToken))
        {
            var job = blob.Key.Value.Split('/')[1];
            newest[job] = newest.TryGetValue(job, out var seen) && seen > blob.LastModified ? seen : blob.LastModified;
        }

        var cutoff = time.GetUtcNow() - StagePreview.BlobLifetime;
        var removed = 0;
        foreach (var job in newest.Where(n => n.Value < cutoff).Select(n => n.Key))
        {
            await blobs.DeletePrefixAsync(new BlobKey($"{PreviewBlobs.Area}/{job}"), cancellationToken);
            removed++;
        }

        return removed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Maintenance)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await RunAsync(stoppingToken) is > 0 and var removed)
                {
                    LogSwept(logger, removed);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(logger, ex);
            }

            await Task.Delay(Interval, time, stoppingToken);
        }
    }

    [LoggerMessage(LogLevel.Information, "Removed the blobs of {Count} previews older than 24 hours")]
    private static partial void LogSwept(ILogger logger, int count);

    [LoggerMessage(LogLevel.Error, "The preview blob sweep failed; it runs again in an hour")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
