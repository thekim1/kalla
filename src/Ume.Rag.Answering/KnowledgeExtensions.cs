using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Infrastructure;
using Ume.Rag.Infrastructure.Access;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Retrieval;
using Ume.Rag.Retrieval.Postgres;

namespace Ume.Rag.Answering;

/// <summary>Knowledge role settings (<c>Knowledge:*</c>, knowledge design §2.3).</summary>
public sealed class KnowledgeOptions
{
    public const string SectionName = "Knowledge";

    /// <summary>Client kinds the knowledge routes are open to (step 1a); P1 <c>internal</c>.</summary>
    public string[] OpenToClientKinds { get; set; } = ["internal"];

    /// <summary>Lifetime of a download grant for <c>public</c> and <c>internal</c> (§4.6); <c>sensitive</c> is 1 hour.</summary>
    public TimeSpan DownloadGrantLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>The origin citation download links use (the staff-network address of <c>rag-api</c>); unset: relative links.</summary>
    public Uri? PublicBaseUrl { get; set; }

    /// <summary>Content downloads per minute per client address (§3.5); in process per instance in P1.</summary>
    public int DownloadsPerMinutePerAddress { get; set; } = 120;

    /// <summary>OpenAPI document and Scalar at <c>/scalar</c> (§3.7); off in production until P2.</summary>
    public bool PublishApiReference { get; set; } = true;
}

/// <summary>Registration of the knowledge role's core (<see cref="KnowledgeService"/> as <see cref="IRetriever"/>).</summary>
public static class KnowledgeExtensions
{
    /// <summary>
    /// The snapshot (a config change handler), the policy, the retrieval data source (<see cref="RetrievalDataSource"/>:
    /// pgvector types and auto-prepare, §6.6), engine, hydrator, the query embedder on gateway client
    /// <see cref="GatewayQueryEmbedder.HttpClientName"/> with <c>Security:CacheKeyPepper</c> (≥ 32 bytes), the retrieval counter,
    /// the section window <c>Retrieval:ExpandNeighbourChunks</c> (8), the download grants (<c>Security:DownloadGrantKeys:&lt;kid&gt;</c>,
    /// at least one of ≥ 32 bytes; <c>Knowledge:DownloadGrantLifetime</c>, <c>Knowledge:PublicBaseUrl</c>)
    /// and <see cref="KnowledgeService"/>. Needs <c>AddRagDatabase</c> and <c>AddRagConfigMonitor</c>.
    /// </summary>
    public static IHostApplicationBuilder AddRagKnowledge(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var configuration = builder.Configuration;
        var connectionString = configuration.GetConnectionString(InfrastructureExtensions.DatabaseResourceName)
            ?? throw new InvalidOperationException($"ConnectionStrings:{InfrastructureExtensions.DatabaseResourceName} is not configured.");
        var pepper = configuration["Security:CacheKeyPepper"] is { Length: >= 32 } value
            ? Encoding.UTF8.GetBytes(value)
            : throw new InvalidOperationException("Security:CacheKeyPepper must be set (at least 32 characters).");
        var knowledge = configuration.GetSection(KnowledgeOptions.SectionName).Get<KnowledgeOptions>() ?? new KnowledgeOptions();
        var embedding = configuration.GetSection("Retrieval").Get<QueryEmbeddingOptions>() ?? new QueryEmbeddingOptions();
        var grantKeys = configuration.GetSection("Security:DownloadGrantKeys").GetChildren()
            .Where(k => !string.IsNullOrEmpty(k.Value)).ToDictionary(k => k.Key, k => Encoding.UTF8.GetBytes(k.Value!), StringComparer.Ordinal);
        var grants = new DownloadGrants(new DownloadGrantOptions
        {
            Keys = grantKeys, Lifetime = knowledge.DownloadGrantLifetime, PublicBaseUrl = knowledge.PublicBaseUrl,
        }, TimeProvider.System);
        var neighbourRadius = configuration.GetValue("Retrieval:ExpandNeighbourChunks", SectionAssembler.DefaultNeighbourRadius);
        if (neighbourRadius is < 0 or > 32)
        {
            throw new InvalidOperationException("Retrieval:ExpandNeighbourChunks must be between 0 and 32.");
        }

        builder.AddGatewayClient(GatewayQueryEmbedder.HttpClientName);
        var services = builder.Services;
        services.AddOptions<KnowledgeOptions>().BindConfiguration(KnowledgeOptions.SectionName);
        services.AddSingleton<PolicySnapshotStore>(sp => new PolicySnapshotStore(sp.GetRequiredService<Npgsql.NpgsqlDataSource>(),
            sp.GetRequiredService<ConfigVersionState>(), sp.GetRequiredService<ILogger<PolicySnapshotStore>>()));
        services.AddSingleton<IConfigChangeHandler>(sp => sp.GetRequiredService<PolicySnapshotStore>());
        services.AddSingleton<IPolicySnapshotSource>(sp => sp.GetRequiredService<PolicySnapshotStore>());
        services.AddSingleton<IAccessPolicy>(new AccessPolicy(new HashSet<string>(knowledge.OpenToClientKinds, StringComparer.Ordinal)));

        var dataSource = RetrievalDataSource.Create(connectionString);
        var index = new PostgresSearchIndex(dataSource);
        services.AddSingleton<RetrievalMetrics>();
        services.AddSingleton(sp => new ScopeSizeCache(index, TimeProvider.System, metrics: sp.GetRequiredService<RetrievalMetrics>()));
        services.AddSingleton(sp => new RetrievalEngine(index, sp.GetRequiredService<ScopeSizeCache>(), metrics: sp.GetRequiredService<RetrievalMetrics>()));
        // The counts and stop-list statistics read at start; readiness waits for that one attempt (#333).
        services.AddSingleton(sp => new RetrievalWarmup(sp.GetRequiredService<ScopeSizeCache>(), sp.GetRequiredService<ILogger<RetrievalWarmup>>(), TimeProvider.System));
        services.AddHostedService(sp => sp.GetRequiredService<RetrievalWarmup>());
        services.AddHealthChecks().Add(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckRegistration(
            "retrieval-warmup", sp => sp.GetRequiredService<RetrievalWarmup>(), failureStatus: null, tags: null));
        services.AddSingleton(new PostgresHydrator(dataSource));
        services.AddSingleton(sp => new GatewayQueryEmbedder(sp.GetRequiredService<IHttpClientFactory>(), embedding, pepper, TimeProvider.System,
            sp.GetRequiredService<ILogger<GatewayQueryEmbedder>>()));
        services.AddSingleton(sp => new RetrievalCounter(dataSource, TimeProvider.System, sp.GetRequiredService<ILogger<RetrievalCounter>>()));
        services.AddHostedService(sp => sp.GetRequiredService<RetrievalCounter>());
        services.AddSingleton(grants);
        services.AddSingleton<IDownloadLinks>(new GrantDownloadLinks(grants));
        services.AddSingleton<IDocumentContentReader, EnvelopeContentReader>(); // needs IBlobEnvelope (AddRagBlobStore + AddRagBlobEnvelope)
        services.AddSingleton(sp => new KnowledgeService(sp.GetRequiredService<IPolicySnapshotSource>(), sp.GetRequiredService<IAccessPolicy>(),
            sp.GetRequiredService<RetrievalEngine>(), sp.GetRequiredService<PostgresHydrator>(), sp.GetRequiredService<GatewayQueryEmbedder>(),
            sp.GetRequiredService<RetrievalCounter>(), dataSource, sp.GetRequiredService<IDownloadLinks>(), sp.GetRequiredService<ILogger<KnowledgeService>>(),
            neighbourRadius));
        services.AddSingleton<IRetriever>(sp => sp.GetRequiredService<KnowledgeService>());
        return builder;
    }
}
