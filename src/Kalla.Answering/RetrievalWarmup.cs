using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Kalla.Retrieval;

namespace Kalla.Answering;

/// <summary>
/// Reads the chunk counts and the keyword stop list's statistics (the worker's <c>content."KeywordStatistics"</c>) when
/// the api starts (#333 finding 2), so the first search does not wait for them. The host does
/// not wait for it; readiness (<c>retrieval-warmup</c>) does, until the one attempt ends, read or failed, and at most
/// <see cref="MaxWait"/>, so a database that cannot give the statistics never keeps the api from serving.
/// </summary>
public sealed partial class RetrievalWarmup(ScopeSizeCache sizes, ILogger<RetrievalWarmup> logger, TimeProvider time) : IHostedService, IHealthCheck, IDisposable
{
    /// <summary>The longest readiness waits for the warm-up.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(60);

    private readonly CancellationTokenSource _stopping = new();
    private int _disposed;
    private long _started;
    private volatile bool _done;
    private Task? _run;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _started = time.GetTimestamp();
        _run = Task.Run(RunAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Stops the attempt; safe in any order with <see cref="Dispose"/> (a host may stop after its services are disposed).</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                await _stopping.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // disposed meanwhile: nothing left to stop
            }
        }

        if (_run is { } run)
        {
            await run.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(_done || time.GetElapsedTime(_started) >= MaxWait ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("retrieval_warming"));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stopping.Cancel(); // the attempt ends too, so a later StopAsync does not wait for a held read
            _stopping.Dispose();
        }
    }

    /// <summary>Whether the attempt has ended (tests).</summary>
    public bool Done => _done;

    private async Task RunAsync()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            limit.CancelAfter(MaxWait);
            if (await sizes.WarmAsync(limit.Token))
            {
                LogWarmed(logger, (long)watch.Elapsed.TotalMilliseconds);
            }
            else
            {
                LogNoStatistics(logger, (long)watch.Elapsed.TotalMilliseconds);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException && Volatile.Read(ref _disposed) == 1 || e is OperationCanceledException && _stopping.IsCancellationRequested)
        {
            // stopping
        }
        catch (Exception e)
        {
            LogFailed(logger, e, (long)watch.Elapsed.TotalMilliseconds);
        }
        finally
        {
            _done = true;
        }
    }

    [LoggerMessage(LogLevel.Information, "Retrieval warmed: chunk counts and keyword stop-list statistics read in {ElapsedMs} ms")]
    private static partial void LogWarmed(ILogger logger, long elapsedMs);

    [LoggerMessage(LogLevel.Warning, "Keyword stop-list statistics could not be read at start ({ElapsedMs} ms); statement K runs with every term until the next read")]
    private static partial void LogNoStatistics(ILogger logger, long elapsedMs);

    [LoggerMessage(LogLevel.Warning, "Retrieval warm-up failed after {ElapsedMs} ms; the first search reads the counts")]
    private static partial void LogFailed(ILogger logger, Exception exception, long elapsedMs);
}
