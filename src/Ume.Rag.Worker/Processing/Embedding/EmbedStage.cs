using System.Collections.Concurrent;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Processing.Persistence;

namespace Ume.Rag.Worker.Processing.Embedding;

/// <summary>The embed stage's limits (processing design §11; gateway limit to verify, to-verify #43).</summary>
public static class EmbedLimits
{
    public const int MaxInputs = 64;
    public const int MaxTokens = 16_384;
    public const double CanaryMinCosine = 0.999;
}

/// <summary>
/// The canary of processing design §11 per profile: at startup and hourly the worker embeds the profile's canary text and
/// compares it with <c>EmbeddingProfiles.CanaryEmbedding</c>; below <see cref="EmbedLimits.CanaryMinCosine"/> the profile
/// is refused on this instance (the gateway alias now serves another model) and health is unhealthy. Until a profile's
/// canary has passed on this instance it is refused too (fail closed); a profile without a recorded canary is allowed
/// and reported as degraded (item 13 records the pilot's).
/// </summary>
public sealed partial class EmbeddingCanary(IEmbeddingProfileStore profiles, EmbeddingClient client, ILogger<EmbeddingCanary> logger) : IHealthCheck
{
    private readonly ConcurrentDictionary<Guid, CanaryResult> _results = new();

    public enum CanaryState
    {
        Passed,
        Failed,
        NotRecorded,
    }

    public sealed record CanaryResult(CanaryState State, double? Similarity);

    /// <summary>The state of a profile on this instance, or null when it was not checked yet.</summary>
    public CanaryResult? For(Guid profileId) => _results.GetValueOrDefault(profileId);

    /// <summary>Checks every active profile; called at startup and hourly by <see cref="EmbeddingCanaryService"/>.</summary>
    public async Task CheckAllAsync(CancellationToken cancellationToken)
    {
        foreach (var profile in await profiles.ActiveAsync(cancellationToken))
        {
            await CheckAsync(profile, cancellationToken);
        }
    }

    public async Task<CanaryResult> CheckAsync(EmbeddingProfileRecord profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.CanaryText is not { Length: > 0 } text || profile.CanaryEmbedding is not { Count: > 0 } recorded)
        {
            return _results[profile.Id] = new CanaryResult(CanaryState.NotRecorded, null);
        }

        var vector = (await client.EmbedAsync(profile, [text], new EmbeddingCall(null, "embed"), cancellationToken))[0];
        var similarity = Cosine(vector, recorded);
        var result = new CanaryResult(similarity >= EmbedLimits.CanaryMinCosine ? CanaryState.Passed : CanaryState.Failed, similarity);
        if (result.State == CanaryState.Failed)
        {
            LogMismatch(logger, profile.Id, similarity);
        }

        return _results[profile.Id] = result;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var failed = _results.Where(r => r.Value.State == CanaryState.Failed).Select(r => r.Key).ToList();
        if (failed.Count > 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy($"Embedding canary mismatch for {failed.Count} profile(s); the embed class is stopped here."));
        }

        return Task.FromResult(_results.Values.Any(r => r.State == CanaryState.NotRecorded)
            ? HealthCheckResult.Degraded("An embedding profile has no recorded canary.")
            : HealthCheckResult.Healthy());
    }

    internal static double Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a.Count != b.Count)
        {
            return 0;
        }

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += a[i] * (double)b[i];
            na += a[i] * (double)a[i];
            nb += b[i] * (double)b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    [LoggerMessage(LogLevel.Critical, "Embedding canary of profile {ProfileId} has cosine {Similarity:F5} < 0.999: the gateway serves another model; embedding is stopped on this instance.")]
    private static partial void LogMismatch(ILogger logger, Guid profileId, double similarity);
}

/// <summary>Runs the canary at startup and every hour (<see cref="TimeProvider"/>, so tests drive it with a fake clock).</summary>
public sealed partial class EmbeddingCanaryService(EmbeddingCanary canary, TimeProvider clock, ILogger<EmbeddingCanaryService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await canary.CheckAllAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(logger, e.GetType().Name);
            }

            await Task.Delay(Interval, clock, stoppingToken);
        }
    }

    [LoggerMessage(LogLevel.Warning, "The embedding canary check failed ({ExceptionType}); it is retried at the next interval.")]
    private static partial void LogFailed(ILogger logger, string exceptionType);
}

/// <summary>The embedded chunks: one vector per chunk in ordinal order, with the cache hits counted.</summary>
public sealed record EmbeddedChunks(IReadOnlyList<ChunkDraft> Chunks, IReadOnlyList<float[]> Vectors, Guid EmbeddingProfileId, int CacheHits, long EmbeddingTokens);

/// <summary>
/// <c>embed</c> (processing design §11): the collection's embedding profile (model, dimensions and ceiling from its row),
/// one cache lookup per document by (level, profile, <c>TextHash</c>), misses deduplicated and embedded in batches of
/// ≤ 64 inputs and ≤ 16,384 tokens, each batch whole or not at all, cached in their own short write. Gateway requests are
/// bounded per instance by the fixed <c>Worker:Concurrency:embed</c> (AIMD is P2). A profile whose canary failed or has not
/// passed yet on this instance is refused; the job is requeued without an attempt.
/// </summary>
public sealed class EmbedStage(EmbeddingClient client, IEmbeddingProfileStore profiles, IEmbeddingCache cache, EmbeddingCanary canary,
    EmbeddingConcurrency concurrency, TimeProvider clock)
{
    public const int Version = 1;

    public async Task<EmbeddedChunks> RunAsync(ChunkSetDraft chunks, string classification, Guid embeddingProfileId, EmbeddingCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var profile = await profiles.GetAsync(embeddingProfileId, cancellationToken)
            ?? throw new StageFatalException(Pipelines.Jobs.RunItemOutcome.Failed, "embed.profile_unknown");
        var state = canary.For(profile.Id)?.State;
        if (state is null || state == EmbeddingCanary.CanaryState.Failed)
        {
            throw new StageTransientException(state is null ? "embed.canary_unchecked" : "embed.canary_mismatch", TimeSpan.FromMinutes(5), countsAttempt: false);
        }

        var hashes = chunks.Chunks.Select(c => c.TextHash).Distinct(StringComparer.Ordinal).ToList();
        var hits = await cache.GetAsync(classification, profile.Id, hashes, cancellationToken);
        var vectors = new Dictionary<string, float[]>(hits.Where(h => h.Value.Length == profile.Dimensions), StringComparer.Ordinal);
        if (vectors.Count > 0)
        {
            await cache.TouchAsync(classification, profile.Id, [.. vectors.Keys], clock.GetUtcNow(), cancellationToken);
        }

        var misses = chunks.Chunks.Where(c => !vectors.ContainsKey(c.TextHash)).DistinctBy(c => c.TextHash, StringComparer.Ordinal).ToList();
        long tokens = 0;
        foreach (var batch in Batches(misses))
        {
            IReadOnlyList<float[]> embedded;
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                embedded = await client.EmbedAsync(profile, [.. batch.Select(c => c.EmbeddedText)], call, cancellationToken);
            }
            finally
            {
                concurrency.Release();
            }

            var fresh = batch.Select((c, i) => (c.TextHash, Vector: embedded[i])).ToDictionary(x => x.TextHash, x => x.Vector, StringComparer.Ordinal);
            await cache.PutAsync(classification, profile.Id, fresh, clock.GetUtcNow(), cancellationToken);
            foreach (var (hash, vector) in fresh)
            {
                vectors[hash] = vector;
            }

            tokens += batch.Sum(c => (long)c.TokenCount);
        }

        return new EmbeddedChunks(chunks.Chunks, [.. chunks.Chunks.Select(c => vectors[c.TextHash])], profile.Id, chunks.Chunks.Count - misses.Count, tokens);
    }

    /// <summary>≤ 64 inputs and ≤ 16,384 tokens (sum of <c>TokenCount</c>) per request.</summary>
    internal static IEnumerable<List<ChunkDraft>> Batches(IReadOnlyList<ChunkDraft> chunks)
    {
        var batch = new List<ChunkDraft>();
        var tokens = 0;
        foreach (var chunk in chunks)
        {
            if (batch.Count > 0 && (batch.Count == EmbedLimits.MaxInputs || tokens + chunk.TokenCount > EmbedLimits.MaxTokens))
            {
                yield return batch;
                batch = [];
                tokens = 0;
            }

            batch.Add(chunk);
            tokens += chunk.TokenCount;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }
}

/// <summary>The per-instance bound on concurrent embedding requests: <c>Worker:Concurrency:embed</c> (fixed in P1).</summary>
public sealed class EmbeddingConcurrency(IOptions<WorkerOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(options.Value.ConcurrencyOf(JobClasses.Embed), options.Value.ConcurrencyOf(JobClasses.Embed));

    public Task WaitAsync(CancellationToken cancellationToken) => _slots.WaitAsync(cancellationToken);

    public void Release() => _slots.Release();

    public void Dispose() => _slots.Dispose();
}
