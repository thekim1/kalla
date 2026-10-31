using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Ume.Rag.Infrastructure;
using Ume.Rag.Worker.Jobs;

namespace Ume.Rag.Worker.Evaluation;

public static class EvaluationExtensions
{
    /// <summary>
    /// The <c>eval</c> class (engine design §9.1, processing design §13–§14): its handler, the retrieval evaluator and the
    /// nightly schedule. Called by <c>AddRagEngine</c>, whose embedding client, canary, profile store and embedding cache
    /// it uses. The search statements bind pgvector values, so the worker's one <c>ragdb</c> data source gets pgvector's
    /// type mapping (<c>UseVector()</c>): the evaluator shares the worker's pool and stays within the role's connection cap
    /// (data-model §1.1); the mapping changes nothing for the statements that do not use it.
    /// </summary>
    public static IHostApplicationBuilder AddRagEvaluation(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.Replace(ServiceDescriptor.Singleton(sp =>
        {
            var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString(InfrastructureExtensions.DatabaseResourceName)
                ?? throw new InvalidOperationException($"ConnectionStrings:{InfrastructureExtensions.DatabaseResourceName} is not configured.");
            var dataSource = new NpgsqlDataSourceBuilder(connectionString);
            dataSource.UseVector();
            return dataSource.Build();
        }));
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
