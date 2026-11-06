using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Processing.Persistence;

namespace Ume.Rag.Worker.Processing.Embedding;

/// <summary>The embed stage's fixed limits (processing design §11).</summary>
public static class EmbedLimits
{
    public const int MaxInputs = 64;
    public const double CanaryMinCosine = EmbeddingCanaryThreshold.MinCosine;
}

/// <summary>
/// The canary of processing design §11 as the worker sees it: a platform admin runs the check in the admin API, which
/// stores the result on the profile row; the worker makes no canary calls. A profile whose last check failed is refused
/// for embedding, preview and evaluation until a later check passes; one never checked, or without a recorded canary,
/// is allowed.
/// </summary>
public static class EmbeddingCanary
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    public static bool Failed(EmbeddingProfileRecord profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.CanarySimilarity is { } similarity && similarity < EmbedLimits.CanaryMinCosine;
    }

    /// <exception cref="StageTransientException"><c>embed.canary_mismatch</c>, requeued without an attempt.</exception>
    public static void EnsureNotFailed(EmbeddingProfileRecord profile)
    {
        if (Failed(profile))
        {
            throw new StageTransientException("embed.canary_mismatch", RetryAfter, countsAttempt: false);
        }
    }
}

/// <summary>Unhealthy while an active profile's last canary check failed; one read of the profile rows, no gateway call.</summary>
public sealed class EmbeddingCanaryHealth(IEmbeddingProfileStore profiles) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var failed = (await profiles.ActiveAsync(cancellationToken)).Count(EmbeddingCanary.Failed);
        return failed > 0
            ? HealthCheckResult.Unhealthy($"Embedding canary mismatch for {failed} profile(s); embedding with them is refused.")
            : HealthCheckResult.Healthy();
    }
}

/// <summary>The embedded chunks: one vector per chunk in ordinal order, with the cache hits counted.</summary>
public sealed record EmbeddedChunks(IReadOnlyList<ChunkDraft> Chunks, IReadOnlyList<float[]> Vectors, Guid EmbeddingProfileId, int CacheHits, long EmbeddingTokens);

/// <summary>
/// <c>embed</c> (processing design §11): the collection's embedding profile (model, dimensions and ceiling from its row),
/// one cache lookup per document by (level, profile, <c>TextHash</c>), misses deduplicated and embedded in batches of
/// ≤ 64 inputs, each batch whole or not at all, cached in their own short write. A batch has no token cap: the gateway
/// checks each input against the model's context window (gateway v0.9.0), and the chunker keeps every chunk far below the
/// profile's <c>MaxInputTokens</c> (template <c>chunkTokens</c> ≤ 800, processing design §6.2). Gateway requests are
/// bounded per instance by the fixed <c>Worker:Concurrency:embed</c> (AIMD is P2). A profile whose last canary check failed
/// is refused; the job is requeued without an attempt.
/// </summary>
public sealed class EmbedStage(EmbeddingClient client, IEmbeddingProfileStore profiles, IEmbeddingCache cache,
    EmbeddingConcurrency concurrency, TimeProvider clock)
{
    public const int Version = 1;

    public async Task<EmbeddedChunks> RunAsync(ChunkSetDraft chunks, string classification, Guid embeddingProfileId, EmbeddingCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        var profile = await profiles.GetAsync(embeddingProfileId, cancellationToken)
            ?? throw new StageFatalException(Pipelines.Jobs.RunItemOutcome.Failed, "embed.profile_unknown");
        EmbeddingCanary.EnsureNotFailed(profile);

        var hashes = chunks.Chunks.Select(c => c.TextHash).Distinct(StringComparer.Ordinal).ToList();
        var hits = await cache.GetAsync(classification, profile.Id, hashes, cancellationToken);
        var vectors = new Dictionary<string, float[]>(hits.Where(h => h.Value.Length == profile.Dimensions), StringComparer.Ordinal);
        if (vectors.Count > 0)
        {
            await cache.TouchAsync(classification, profile.Id, [.. vectors.Keys], clock.GetUtcNow(), cancellationToken);
        }

        var misses = chunks.Chunks.Where(c => !vectors.ContainsKey(c.TextHash)).DistinctBy(c => c.TextHash, StringComparer.Ordinal).ToList();
        long tokens = 0;
        foreach (var batch in misses.Chunk(EmbedLimits.MaxInputs))
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
}

/// <summary>The per-instance bound on concurrent embedding requests: <c>Worker:Concurrency:embed</c> (fixed in P1).</summary>
public sealed class EmbeddingConcurrency(IOptions<WorkerOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(options.Value.ConcurrencyOf(JobClasses.Embed), options.Value.ConcurrencyOf(JobClasses.Embed));

    public Task WaitAsync(CancellationToken cancellationToken) => _slots.WaitAsync(cancellationToken);

    public void Release() => _slots.Release();

    public void Dispose() => _slots.Dispose();
}
