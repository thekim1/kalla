using Microsoft.Extensions.DependencyInjection.Extensions;
using Ume.Rag.Infrastructure;
using Ume.Rag.Retrieval.Text;
using Ume.Rag.Worker.Processing.Chunking;
using Ume.Rag.Worker.Processing.Detect;
using Ume.Rag.Worker.Processing.Embedding;
using Ume.Rag.Worker.Processing.Html;
using Ume.Rag.Worker.Processing.Parse;
using Ume.Rag.Worker.Processing.Pii;

namespace Ume.Rag.Worker.Processing;

public static class ProcessingExtensions
{
    /// <summary>
    /// The processing stages (processing design §2–§11) and the embedding gateway client with its canary check and health.
    /// Needs the persistence ports of <see cref="Persistence"/> (<c>IEmbeddingProfileStore</c>, <c>IEmbeddingCache</c>,
    /// <c>IChunkSetStore</c>), registered by the engine with the SQL implementations once the schema is merged (J2, item 9);
    /// the parser ports (<see cref="IParserConverter"/>, <see cref="IDocumentInspector"/>, <see cref="INerClient"/>) are
    /// optional. Presets: an <see cref="IHtmlPresetCatalog"/> registered before this call (item 8) replaces the generic one.
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
        services.TryAddSingleton<EmbeddingCanary>();
        services.TryAddSingleton<EmbedStage>();
        services.AddHostedService<EmbeddingCanaryService>();
        services.AddHealthChecks().AddCheck<EmbeddingCanary>("embedding-canary");
        return builder;
    }
}
