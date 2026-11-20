using Kalla.Domain.Retrieval;
using Kalla.Retrieval;
using Kalla.Worker.Jobs;
using Kalla.Worker.Processing;
using Kalla.Worker.Processing.Embedding;
using Kalla.Worker.Processing.Persistence;

namespace Kalla.Worker.Evaluation;

/// <summary>
/// A question's vector for one evaluation run: a call it makes carries the run's id and counts under the run's
/// collection (<see cref="EmbeddingCall"/>; only cache misses call the gateway).
/// </summary>
public delegate Task<ReadOnlyMemory<float>> RunQueryEmbedder(string normalizedQuery, Guid runId, UsageSink usage, CancellationToken cancellationToken);

/// <summary>
/// The evaluator's query embedder (processing design §13.5, knowledge design §5): the normalised question
/// through the worker's gateway client with the collection's profile (its model, dimensions and ceiling, <c>Eu</c> for
/// the P1a pilot) and <c>x-ume-step: query-embed</c> with the evaluation run's id as <c>x-ume-run-id</c>, cached durably in
/// <c>EmbeddingCache</c> by the question's hash at the collection's level; a cache miss's call is counted under the
/// collection (processing design §14.7). A failed call throws <see cref="QueryEmbeddingUnavailableException"/> with the gateway
/// error inside; the evaluator passes <c>AllowDegraded = false</c>, so the run fails rather than falling back (§5.5).
/// </summary>
public sealed class EvaluationQueryEmbedder(EmbeddingClient client, IEmbeddingCache cache, TimeProvider time)
{
    public const string Step = "query-embed";

    public RunQueryEmbedder For(EmbeddingProfileRecord profile, string classification)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(classification);
        return async (normalizedQuery, runId, usage, cancellationToken) =>
        {
            var hash = ProcessingJson.Sha256Hex(normalizedQuery);
            if ((await cache.GetAsync(classification, profile.Id, [hash], cancellationToken)).TryGetValue(hash, out var cached)
                && cached.Length == profile.Dimensions)
            {
                return cached;
            }

            float[] vector;
            try
            {
                vector = (await client.EmbedAsync(profile, [normalizedQuery], new EmbeddingCall(runId, usage, Step), cancellationToken))[0];
            }
            catch (StageTransientException e)
            {
                throw new QueryEmbeddingUnavailableException(RetrievalDegradation.EmbeddingUnavailable, e.Code, e);
            }
            catch (StageFatalException e)
            {
                var reason = e.Reason == EmbeddingFailures.ResidencyAboveCeiling ? RetrievalDegradation.EmbeddingResidency : RetrievalDegradation.EmbeddingUnavailable;
                throw new QueryEmbeddingUnavailableException(reason, e.Reason, e);
            }

            await cache.PutAsync(classification, profile, new Dictionary<string, float[]>(StringComparer.Ordinal) { [hash] = vector }, time.GetUtcNow(),
                cancellationToken);
            return vector;
        };
    }
}

/// <summary>The embedding client's failure reasons the evaluator tells apart.</summary>
internal static class EmbeddingFailures
{
    public const string ResidencyAboveCeiling = "gateway.residency_above_ceiling";
}
