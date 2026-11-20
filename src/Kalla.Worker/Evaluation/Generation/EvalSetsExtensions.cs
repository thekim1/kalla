using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Kalla.Infrastructure;
using Kalla.Infrastructure.Gateway;
using Kalla.Worker.Processing.Chunking;
using Kalla.Worker.Processing.Pii;

namespace Kalla.Worker.Evaluation.Generation;

public static class EvalSetsExtensions
{
    /// <summary>
    /// Set generation (<see cref="IEvalSetHandler"/>, processing design §13.1–§13.3), its gateway client
    /// <see cref="EvalModelClient.HttpClientName"/> (at the collection's ceiling, so the worker needs its key of that ceiling) and
    /// the catalogue client <see cref="EvalModelClient.CatalogueClientName"/>.
    /// Needs what <c>AddRagWorker</c> (<c>JobDatabase</c>, <c>IJobQueue</c>, <c>WorkerIdentity</c>) and <c>AddRagEngine</c>
    /// (<c>EmbeddingClient</c>, <c>IEmbeddingProfileStore</c>) register.
    /// </summary>
    public static IHostApplicationBuilder AddRagEvalSets(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        builder.AddGatewayClient(EvalModelClient.HttpClientName);
        services.AddHttpClient(EvalModelClient.CatalogueClientName, (sp, client) =>
            {
                if (sp.GetRequiredService<IOptions<GatewayOptions>>().Value.BaseUrl is { Length: > 0 } baseUrl)
                {
                    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
                }
            })
            .RemoveStandardResilienceHandler();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ITokenCounter>(_ => Bgem3TokenCounter.Shared);
        services.TryAddSingleton<PiiPatterns>();
        services.TryAddSingleton<QuestionFilters>();
        services.TryAddSingleton<EvalModelClient>();
        services.TryAddSingleton<EvalSetStore>();
        services.TryAddSingleton<IEvalSetHandler, EvalSetGenerator>();
        return builder;
    }
}
