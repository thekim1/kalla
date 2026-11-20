using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Kalla.Worker.Connectors.Crawl;
using Kalla.Worker.Connectors.Egress;
using Kalla.Worker.Connectors.Hosts;
using Kalla.Worker.Connectors.Http;
using Kalla.Worker.Connectors.Malware;
using Kalla.Worker.Connectors.Persistence;
using Kalla.Worker.Connectors.Render;
using Kalla.Worker.Connectors.Robots;
using Kalla.Worker.Connectors.Uploads;

namespace Kalla.Worker.Connectors;

public static class ConnectorExtensions
{
    /// <summary>
    /// The fetch path's shared parts (connectors design §7, §8, §11): <c>Crawl:*</c>, <c>Render:*</c> and <c>ClamAv:*</c>
    /// options, the egress guard and the one crawl HTTP client, the fetcher, the renderer and the page fetcher, robots.txt,
    /// the host budget, the malware scanner and its readiness check, the
    /// <c>Settings.Crawl</c> policies and the connectors' Postgres stores (on the queue's <c>JobDatabase</c>: call after
    /// <c>AddRagJobQueue</c>). <c>ClamAv:Mode = off</c> gets the scanner that does not scan and event 5106 at start; with
    /// <c>on</c> (the default) and no <c>ClamAv:Host</c> the Development environment gets the EICAR-only scanner and every
    /// other environment refuses to start, as does a <c>ClamAv:Mode</c> other than <c>on</c> or <c>off</c>.
    /// </summary>
    public static IHostApplicationBuilder AddRagConnectors(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<CrawlOptions>().BindConfiguration(CrawlOptions.SectionName)
            .Configure<IConfiguration>((o, configuration) =>
                o.MaxRenderPerHost = configuration.GetValue($"{RenderOptions.SectionName}:{nameof(RenderOptions.MaxConcurrent)}", new RenderOptions().MaxConcurrent))
            .ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CrawlOptions>, CrawlOptionsValidator>());
        builder.Services.AddOptions<RenderOptions>().BindConfiguration(RenderOptions.SectionName).ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RenderOptions>, RenderOptionsValidator>());
        builder.Services.AddOptions<ClamAvOptions>().BindConfiguration(ClamAvOptions.SectionName);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<ConnectorMetrics>();
        builder.Services.TryAddSingleton<IDnsResolver, SystemDnsResolver>();
        // Settings.Crawl from the database, refreshed every minute; the same instance serves both policies.
        builder.Services.TryAddSingleton<PlatformCrawlSettingsSource>();
        builder.Services.TryAddSingleton<IEgressPolicySource>(sp => sp.GetRequiredService<PlatformCrawlSettingsSource>());
        builder.Services.TryAddSingleton<ICrawlPolicySource>(sp => sp.GetRequiredService<PlatformCrawlSettingsSource>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<PlatformCrawlSettingsSource>());
        builder.Services.TryAddSingleton<ICrawlHostStore, PostgresCrawlHostStore>();
        builder.Services.TryAddSingleton<PostgresCrawlStore>();
        builder.Services.TryAddSingleton<ICrawlFrontierStore>(sp => sp.GetRequiredService<PostgresCrawlStore>());
        builder.Services.TryAddSingleton<ICrawlRunStore>(sp => sp.GetRequiredService<PostgresCrawlStore>());
        builder.Services.TryAddSingleton<PostgresUploadStore>();
        builder.Services.TryAddSingleton<IUploadSlotReader>(sp => sp.GetRequiredService<PostgresUploadStore>());
        builder.Services.TryAddSingleton<IUploadSlotWriter>(sp => sp.GetRequiredService<PostgresUploadStore>());
        builder.Services.TryAddSingleton<IDeletionProposalStore, PostgresDeletionProposalStore>();
        builder.Services.TryAddSingleton<IQuarantineStore, PostgresQuarantineStore>();
        builder.Services.TryAddSingleton<IConnectorCheckpointStore, PostgresConnectorCheckpointStore>();
        builder.Services.TryAddSingleton<ISiteProfileReader, PostgresSiteProfileReader>();
        builder.Services.TryAddSingleton<LastmodReliability>();
        builder.Services.TryAddSingleton<IEgressGuard, EgressGuard>();
        builder.Services.TryAddSingleton(sp => new CrawlHttpClient(sp.GetRequiredService<IEgressGuard>()));
        builder.Services.TryAddSingleton<HttpFetcher>();
        builder.Services.TryAddSingleton<RobotsCache>();
        // The renderer (connectors design §8): one connection to rag-renderer per worker, its slots, cache and per-page fetch.
        builder.Services.TryAddSingleton<HostBudget>();
        builder.Services.TryAddSingleton<RenderMetrics>();
        builder.Services.TryAddSingleton<RenderBlocklist>();
        builder.Services.TryAddSingleton<RenderAssetCache>();
        builder.Services.TryAddSingleton<RenderSlots>();
        builder.Services.TryAddSingleton<RenderFetchers>();
        builder.Services.TryAddSingleton<IRenderer, PlaywrightRenderer>();
        builder.Services.TryAddSingleton<PageFetcher>();

        if (ClamAvOptions.ScanOffIn(builder.Configuration))
        {
            builder.Services.TryAddSingleton<IMalwareScanner, MalwareScanOff>();
            builder.Services.AddHostedService<MalwareScanOffNotice>();
        }
        else if (!string.IsNullOrWhiteSpace(builder.Configuration[$"{ClamAvOptions.SectionName}:{nameof(ClamAvOptions.Host)}"]))
        {
            builder.Services.TryAddSingleton<IMalwareScanner, ClamAvScanner>();
        }
        else if (builder.Environment.IsDevelopment())
        {
            builder.Services.TryAddSingleton<IMalwareScanner, EicarOnlyScanner>();
        }
        else
        {
            throw new InvalidOperationException(
                "ClamAv:Host is not set: with ClamAv:Mode = on (the default) the worker scans every file with clamd. Set ClamAv:Host to clamd's address, " +
                "or set ClamAv:Mode = off to store files without a malware scan.");
        }

        builder.Services.AddHealthChecks().AddCheck<ClamAvHealthCheck>("clamav");
        return builder;
    }
}
