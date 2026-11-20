using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Kalla.Worker.Engine;
using Kalla.Worker.Jobs;
using Kalla.Worker.Processing.Chunking;
using Kalla.Worker.Processing.Detect;
using Kalla.Worker.Processing.Embedding;
using Kalla.Worker.Processing.Html;
using Kalla.Worker.Processing.Parse;
using Kalla.Worker.Processing.Persistence;
using Kalla.Worker.Processing.Pii;

namespace Kalla.Worker.Preview;

public static class PreviewExtensions
{
    /// <summary>
    /// The <c>preview</c> class (engine design §11, §12.2): its handler with <c>stage-preview</c>, <c>preflight</c> and
    /// <c>publish-plan</c>, and the hourly sweep of <c>previews/</c> blobs. Called by <c>AddRagEngine</c>, whose stages,
    /// stores and connectors it uses.
    /// </summary>
    public static IHostApplicationBuilder AddRagPreview(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;
        services.TryAddSingleton<PreviewConfig>();
        services.TryAddSingleton(sp => new PreviewSegment(sp.GetRequiredService<DetectStage>(), sp.GetRequiredService<HtmlExtractor>(),
            sp.GetRequiredService<IHtmlPresetCatalog>(), sp.GetRequiredService<Chunker>(), sp.GetRequiredService<PersonalDataStage>(),
            sp.GetRequiredService<PiiPatterns>(), sp.GetRequiredService<IParsePlanner>(), sp.GetRequiredService<IOptions<ParseOptions>>(),
            sp.GetRequiredService<EngineSettings>(), sp.GetRequiredService<SiteProfiles>(), sp.GetRequiredService<IChunkSetStore>(),
            sp.GetRequiredService<EmbeddingClient>(), sp.GetRequiredService<IEmbeddingProfileStore>(), sp.GetRequiredService<IEmbeddingCache>(),
            sp.GetRequiredService<EmbeddingConcurrency>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IParserConverter>(), sp.GetService<IParserBuild>(), sp.GetRequiredService<ParseRuleCatalog>()));
        services.TryAddSingleton<StagePreview>();
        services.TryAddSingleton<Preflight>();
        services.TryAddSingleton<PublishPlan>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, PreviewJobHandler>());
        services.TryAddSingleton<PreviewBlobSweep>();
        services.AddHostedService(sp => sp.GetRequiredService<PreviewBlobSweep>());
        return builder;
    }
}
