using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Retrieval;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Embedding;
using Ume.Rag.Worker.Processing.Persistence;

namespace Ume.Rag.Worker.Evaluation;

/// <summary>
/// The evaluator's <see cref="QueryEmbedder"/> (processing design §13.5, knowledge design §5): the normalised question
/// through the worker's gateway client with the collection's profile (its model, dimensions and ceiling, <c>Eu</c> for
/// the P1a pilot) and <c>x-ume-step: query-embed</c>, cached durably in <c>EmbeddingCache</c> by the question's hash
/// at the collection's level. A failed call throws <see cref="QueryEmbeddingUnavailableException"/> with the gateway
/// error inside; the evaluator passes <c>AllowDegraded = false</c>, so the run fails rather than falling back (§5.5).
/// </summary>
public sealed class EvaluationQueryEmbedder(EmbeddingClient client, IEmbeddingCache cache, TimeProvider time)
{
    public const string Step = "query-embed";

    public QueryEmbedder For(EmbeddingProfileRecord profile, string classification)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(classification);
        return async (normalizedQuery, cancellationToken) =>
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
                vector = (await client.EmbedAsync(profile, [normalizedQuery], new EmbeddingCall(null, Step), cancellationToken))[0];
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

            await cache.PutAsync(classification, profile.Id, new Dictionary<string, float[]>(StringComparer.Ordinal) { [hash] = vector }, time.GetUtcNow(),
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
