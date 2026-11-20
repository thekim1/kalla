using Microsoft.Extensions.DependencyInjection.Extensions;
using Kalla.Infrastructure;
using Kalla.Retrieval.Text;
using Kalla.Worker.Processing.Chunking;
using Kalla.Worker.Processing.Detect;
using Kalla.Worker.Processing.Embedding;
using Kalla.Worker.Processing.Html;
using Kalla.Worker.Processing.Parse;
using Kalla.Worker.Processing.Pii;

namespace Kalla.Worker.Processing;

public static class ProcessingExtensions
{
    /// <summary>
    /// The processing stages (processing design §2–§11) and the embedding gateway client with the canary health (the stored result of the last check).
    /// Needs the persistence ports of <see cref="Persistence"/> (<c>IEmbeddingProfileStore</c>, <c>IEmbeddingCache</c>,
    /// <c>IChunkSetStore</c>), registered by the engine with the SQL implementations once the schema is merged (J2, item 9);
    /// the parser ports (<see cref="IParserConverter"/>, <see cref="IDocumentInspector"/>, <see cref="INerClient"/>) are
    /// optional. Presets: <see cref="HtmlPresets"/> (Sitevision, else generic) unless another
    /// <see cref="IHtmlPresetCatalog"/> is registered first.
    /// </summary>
    public static IHostApplicationBuilder AddRagProcessing(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.AddOptions<ParseOptions>().BindConfiguration(ParseOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ITokenCounter>(_ => Bgem3TokenCounter.Shared);
        services.TryAddSingleton(_ => SwedishDecompounder.Default);
        services.TryAddSingleton<IHtmlPresetCatalog, HtmlPresets>();
        services.TryAddSingleton<IFirstNameGazetteer, EmptyGazetteer>();
        services.TryAddSingleton<IParsePlanner, ParsePlanner>();
        services.TryAddSingleton(sp => new DetectStage(sp.GetService<IDocumentInspector>()));
        services.TryAddSingleton<HtmlExtractor>();
        services.TryAddSingleton<Chunker>();
        services.TryAddSingleton<PiiPatterns>();
        services.TryAddSingleton(sp => new PersonalDataStage(sp.GetRequiredService<PiiPatterns>(), sp.GetService<INerClient>(),
            sp.GetRequiredService<IFirstNameGazetteer>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<DocumentProcessor>();
        services.TryAddSingleton<PiiLifecycle>();

        builder.AddGatewayClient(EmbeddingClient.HttpClientName);
        services.TryAddSingleton<EmbeddingClient>();
        services.TryAddSingleton<EmbeddingConcurrency>();
        services.TryAddSingleton<EmbedStage>();
        services.AddHealthChecks().AddCheck<EmbeddingCanaryHealth>("embedding-canary");
        return builder;
    }
}
