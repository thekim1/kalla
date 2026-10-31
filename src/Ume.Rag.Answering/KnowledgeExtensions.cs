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
}

/// <summary>Registration of the knowledge role's core (<see cref="KnowledgeService"/> as <see cref="IRetriever"/>).</summary>
public static class KnowledgeExtensions
{
    /// <summary>
    /// The snapshot (a config change handler), the policy, the retrieval data source (<see cref="RetrievalDataSource"/>:
    /// pgvector types and auto-prepare, §6.6), engine, hydrator, the query embedder on gateway client
    /// <see cref="GatewayQueryEmbedder.HttpClientName"/> with <c>Security:CacheKeyPepper</c> (≥ 32 bytes), the retrieval counter
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

        builder.AddGatewayClient(GatewayQueryEmbedder.HttpClientName);
        var services = builder.Services;
        services.AddSingleton<PolicySnapshotStore>(sp => new PolicySnapshotStore(sp.GetRequiredService<Npgsql.NpgsqlDataSource>(),
            sp.GetRequiredService<ConfigVersionState>(), sp.GetRequiredService<ILogger<PolicySnapshotStore>>()));
        services.AddSingleton<IConfigChangeHandler>(sp => sp.GetRequiredService<PolicySnapshotStore>());
        services.AddSingleton<IPolicySnapshotSource>(sp => sp.GetRequiredService<PolicySnapshotStore>());
        services.AddSingleton<IAccessPolicy>(new AccessPolicy(new HashSet<string>(knowledge.OpenToClientKinds, StringComparer.Ordinal)));

        var dataSource = RetrievalDataSource.Create(connectionString);
        var index = new PostgresSearchIndex(dataSource);
        services.AddSingleton(sp => new RetrievalEngine(index, new ScopeSizeCache(index, TimeProvider.System)));
        services.AddSingleton(new PostgresHydrator(dataSource));
        services.AddSingleton(sp => new GatewayQueryEmbedder(sp.GetRequiredService<IHttpClientFactory>(), embedding, pepper, TimeProvider.System,
            sp.GetRequiredService<ILogger<GatewayQueryEmbedder>>()));
        services.AddSingleton(sp => new RetrievalCounter(dataSource, TimeProvider.System, sp.GetRequiredService<ILogger<RetrievalCounter>>()));
        services.AddHostedService(sp => sp.GetRequiredService<RetrievalCounter>());
        services.AddSingleton<IDownloadLinks>(NoDownloadLinks.Instance);
        services.AddSingleton(sp => new KnowledgeService(sp.GetRequiredService<IPolicySnapshotSource>(), sp.GetRequiredService<IAccessPolicy>(),
            sp.GetRequiredService<RetrievalEngine>(), sp.GetRequiredService<PostgresHydrator>(), sp.GetRequiredService<GatewayQueryEmbedder>(),
            sp.GetRequiredService<RetrievalCounter>(), dataSource, sp.GetRequiredService<IDownloadLinks>(), sp.GetRequiredService<ILogger<KnowledgeService>>()));
        services.AddSingleton<IRetriever>(sp => sp.GetRequiredService<KnowledgeService>());
        return builder;
    }
}
