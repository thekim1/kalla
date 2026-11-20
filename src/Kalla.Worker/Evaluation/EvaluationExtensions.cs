using Microsoft.Extensions.DependencyInjection.Extensions;
using Kalla.Infrastructure;
using Kalla.Retrieval;
using Kalla.Worker.Jobs;

namespace Kalla.Worker.Evaluation;

public static class EvaluationExtensions
{
    /// <summary>
    /// The <c>eval</c> class (engine design §9.1, processing design §13–§14): its handler, the retrieval evaluator and the
    /// nightly schedule. Called by <c>AddRagEngine</c>, whose embedding client, profile store and embedding cache
    /// it uses. The search statements bind pgvector values on the worker's one <c>ragdb</c> data source, which
    /// <c>AddRagDatabase</c> maps pgvector on (#110): the evaluator shares the worker's pool and stays within the role's
    /// connection cap (data-model §1.1). The <c>hybrid-rerank</c> (#399) and <c>hybrid-rerank-gateway</c> (#414) variants
    /// call the gateway on the rerankers' client <see cref="ChatModelReranker.HttpClientName"/>.
    /// </summary>
    public static IHostApplicationBuilder AddRagEvaluation(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        builder.AddGatewayClient(ChatModelReranker.HttpClientName);
        services.TryAddSingleton<EvaluationStore>();
        services.TryAddSingleton<EvaluationQueryEmbedder>();
        services.TryAddSingleton<RetrievalEvaluator>();
        services.TryAddSingleton<EvaluationMetrics>();
        services.TryAddSingleton<EvaluationRunner>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, EvalJobHandler>());
        services.TryAddSingleton<EvalScheduler>();
        services.AddHostedService(sp => sp.GetRequiredService<EvalScheduler>());
        return builder;
    }
}
