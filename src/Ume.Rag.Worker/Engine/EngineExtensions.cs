using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Ume.Rag.Worker.Connectors.Hosts;
using Ume.Rag.Worker.Connectors.Uploads;
using Ume.Rag.Worker.Connectors.UrlList;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Parsing;
using Ume.Rag.Worker.Processing;
using Ume.Rag.Worker.Processing.Detect;
using Ume.Rag.Worker.Processing.Parse;
using Ume.Rag.Worker.Processing.Persistence;
using Ume.Rag.Worker.Processing.Pii;

namespace Ume.Rag.Worker.Engine;

/// <summary>The parser sidecar's address (<c>Parser:BaseUrl</c>, repo-and-deployment §3); without it parser-bound documents wait.</summary>
public sealed class ParserOptions
{
    public const string SectionName = "Parser";

    public Uri? BaseUrl { get; set; }

    /// <summary>The client's timeout: the parser's request deadline plus 15 s (parser design §10.4).</summary>
    public int TimeoutSeconds { get; set; } = 135;
}

public static class EngineExtensions
{
    /// <summary>
    /// The pipeline engine (engine design §2–§10, §14, §16): the parser client and its adapters (when
    /// <c>Parser:BaseUrl</c> is set), the processing stages with their SQL stores, the connectors the engine runs, the
    /// <c>sync</c>, <c>fetch</c>, <c>parse</c> and <c>embed</c> handlers, the run finalizer and the scheduler. Call
    /// after <c>AddRagWorker</c>, which registers the parts the job machinery itself needs (the blob store, the stage manifest,
    /// runs, the index writer and the maintenance sweeps).
    /// </summary>
    public static IHostApplicationBuilder AddRagEngine(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        services.AddOptions<ParserOptions>().BindConfiguration(ParserOptions.SectionName);
        if (builder.Configuration[$"{ParserOptions.SectionName}:{nameof(ParserOptions.BaseUrl)}"] is { Length: > 0 })
        {
            services.AddHttpClient<ParserClient>((sp, client) =>
            {
                var parser = sp.GetRequiredService<IOptions<ParserOptions>>().Value;
                client.BaseAddress = new Uri(parser.BaseUrl!.AbsoluteUri.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(parser.TimeoutSeconds);
            }).RemoveStandardResilienceHandler();
            services.TryAddSingleton<IParserConverter>(sp => new GatedParserConverter(new ParserClientConverter(sp.GetRequiredService<ParserClient>()),
                sp.GetRequiredService<IParserSlotGate>()));
            services.TryAddSingleton<IParserBuild>(sp => new ParserInfoBuild(sp.GetRequiredService<ParserClient>(), sp.GetRequiredService<TimeProvider>()));
            services.TryAddSingleton<IDocumentInspector>(sp => new ParserInspector(sp.GetRequiredService<ParserClient>()));
            services.TryAddSingleton<INerClient>(sp => new ParserNerClient(sp.GetRequiredService<ParserClient>()));
        }

        // Preview first (engine design §9.5): one gate over the parser's conversion slots, the parse class's concurrency in P1.
        services.TryAddSingleton<IParserSlotGate>(sp =>
            new ParserSlotGate(sp.GetRequiredService<IOptions<WorkerOptions>>().Value.ConcurrencyOf(Domain.Entities.JobClasses.Parse)));
        services.TryAddSingleton<INodeOutputStore, PostgresNodeOutputStore>();
        services.TryAddSingleton<IEmbeddingProfileStore, PostgresEmbeddingProfileStore>();
        services.TryAddSingleton<IEmbeddingCache, PostgresEmbeddingCache>();
        services.TryAddSingleton<IParseObservationStore, PostgresParseObservationStore>();
        services.TryAddSingleton<IChunkSetStore, PostgresChunkSetStore>();
        builder.AddRagProcessing();

        services.TryAddSingleton<HostBudget>();
        services.TryAddSingleton<UrlListConnector>();
        services.TryAddSingleton<UploadConnector>();

        services.TryAddSingleton<SourceContexts>();
        services.TryAddSingleton<DocumentStore>();
        services.TryAddSingleton<StageOutputs>();
        services.TryAddSingleton<SiteProfiles>();
        services.TryAddSingleton(sp => new ProcessingSegment(sp.GetRequiredService<StageOutputs>(), sp.GetRequiredService<Domain.Ports.IBlobStore>(),
            sp.GetRequiredService<DetectStage>(), sp.GetRequiredService<Processing.Html.HtmlExtractor>(), sp.GetRequiredService<Processing.Chunking.Chunker>(),
            sp.GetRequiredService<PersonalDataStage>(), sp.GetRequiredService<IParsePlanner>(), sp.GetRequiredService<IOptions<ParseOptions>>(),
            sp.GetRequiredService<EngineSettings>(), sp.GetService<IParserConverter>(), sp.GetService<IParserBuild>()));
        services.TryAddSingleton<DocumentIngest>();
        services.TryAddSingleton<UrlListSource>();
        services.TryAddSingleton<UploadSource>();
        services.TryAddSingleton<Reconciler>();
        services.TryAddSingleton<DriftSweep>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, SyncJobHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, FetchJobHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, ParseJobHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, EmbedJobHandler>());

        services.AddHostedService<RunFinalizer>();
        services.AddHostedService<SyncScheduler>();
        services.AddHostedService<DriftSweepService>();
        return builder;
    }
}
