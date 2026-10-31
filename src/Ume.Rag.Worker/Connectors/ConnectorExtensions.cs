using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Ume.Rag.Worker.Connectors.Egress;
using Ume.Rag.Worker.Connectors.Http;
using Ume.Rag.Worker.Connectors.Malware;
using Ume.Rag.Worker.Connectors.Robots;

namespace Ume.Rag.Worker.Connectors;

public static class ConnectorExtensions
{
    /// <summary>
    /// The fetch path's shared parts (connectors design §7, §11): <c>Crawl:*</c> and <c>ClamAv:*</c> options, the egress
    /// guard and the one crawl HTTP client, the fetcher, robots.txt, the malware scanner and its readiness check. Without
    /// <c>ClamAv:Host</c> the Development environment gets the EICAR-only scanner; every other environment fails closed.
    /// </summary>
    public static IHostApplicationBuilder AddRagConnectors(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<CrawlOptions>().BindConfiguration(CrawlOptions.SectionName).ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CrawlOptions>, CrawlOptionsValidator>());
        builder.Services.AddOptions<ClamAvOptions>().BindConfiguration(ClamAvOptions.SectionName);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<ConnectorMetrics>();
        builder.Services.TryAddSingleton<IDnsResolver, SystemDnsResolver>();
        builder.Services.TryAddSingleton<IEgressPolicySource, DefaultEgressPolicySource>();
        builder.Services.TryAddSingleton<IEgressGuard, EgressGuard>();
        builder.Services.TryAddSingleton(sp => new CrawlHttpClient(sp.GetRequiredService<IEgressGuard>()));
        builder.Services.TryAddSingleton<HttpFetcher>();
        builder.Services.TryAddSingleton<RobotsCache>();

        var developmentFake = builder.Environment.IsDevelopment()
            && string.IsNullOrWhiteSpace(builder.Configuration[$"{ClamAvOptions.SectionName}:{nameof(ClamAvOptions.Host)}"]);
        if (developmentFake)
        {
            builder.Services.TryAddSingleton<IMalwareScanner, EicarOnlyScanner>();
        }
        else
        {
            builder.Services.TryAddSingleton<IMalwareScanner, ClamAvScanner>();
        }

        builder.Services.AddHealthChecks().AddCheck<ClamAvHealthCheck>("clamav");
        return builder;
    }
}
