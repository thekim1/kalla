using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Microsoft.Extensions.Options;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.Infrastructure.Keys;
using Ume.Rag.Infrastructure.Security;
using Ume.Rag.Infrastructure.Persistence;

namespace Ume.Rag.Infrastructure;

public static class InfrastructureExtensions
{
    public const string DatabaseResourceName = "ragdb";

    /// <summary>
    /// Secret files (<c>Security:SecretsDirectory</c>, one file per key) as configuration, as in the gateway.
    /// </summary>
    public static IHostApplicationBuilder AddDeploymentSecrets(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var directory = builder.Configuration["Security:SecretsDirectory"];
        if (!string.IsNullOrWhiteSpace(directory))
        {
            builder.Configuration.AddKeyPerFile(directory, optional: false, reloadOnChange: false);
        }

        return builder;
    }

    /// <summary>
    /// EF Core (Npgsql) via the Aspire integration: pooling, retries, health check and tracing. Production refuses the
    /// superuser and anything but <c>VerifyFull</c> TLS, as the gateway's <c>AddGatewayDatabase</c>.
    /// </summary>
    public static IHostApplicationBuilder AddRagDatabase(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Environment.IsProduction())
        {
            var connection = new NpgsqlConnectionStringBuilder(builder.Configuration.GetConnectionString(DatabaseResourceName));
            if (connection.SslMode != SslMode.VerifyFull || string.IsNullOrWhiteSpace(connection.RootCertificate) ||
                connection.Username is null or "postgres")
            {
                throw new InvalidOperationException("Production ragdb requires a non-superuser role and SSL Mode=VerifyFull with a Root Certificate.");
            }
        }

        builder.AddNpgsqlDbContext<RagDbContext>(DatabaseResourceName,
            configureDbContextOptions: options => options.UseNpgsql(RagDbContext.ConfigureNpgsql));
        return builder;
    }

    /// <summary>
    /// The config-version library (data-model §5): <see cref="ConfigVersionState"/>, the <see cref="ConfigVersionMonitor"/>
    /// and the readiness check. Register what the service caches as <see cref="IConfigChangeHandler"/>s; call
    /// <see cref="ConfigVersionState.EnsureConfirmed"/> before security-relevant decisions and
    /// <c>app.UseConfigVersionLogScope()</c> in the request pipeline.
    /// </summary>
    public static IHostApplicationBuilder AddRagConfigMonitor(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<ConfigOptions>().BindConfiguration(ConfigOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<ConfigVersionState>();
        builder.Services.AddHostedService<ConfigVersionMonitor>();
        builder.Services.AddHealthChecks().AddCheck<ConfigConfirmedHealthCheck>("config");
        return builder;
    }

    /// <summary>
    /// Key hashing with Ume.Rag's pepper (<c>Security:KeyPepper</c>) and the <see cref="KeyStore"/> (identity design §6),
    /// registered as an <see cref="IConfigChangeHandler"/> so key and client changes clear its caches. Pair with
    /// <see cref="AddRagConfigMonitor"/> in a service that authenticates keys.
    /// </summary>
    public static IHostApplicationBuilder AddRagKeys(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<SecurityOptions>().BindConfiguration(SecurityOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        builder.Services.AddOptions<KeyStoreOptions>().BindConfiguration(KeyStoreOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton(sp => KeyHasherFactory.Create(sp.GetRequiredService<IOptions<SecurityOptions>>().Value));
        builder.Services.TryAddSingleton<KeyStore>();
        builder.Services.AddSingleton<IConfigChangeHandler>(sp => sp.GetRequiredService<KeyStore>());
        return builder;
    }

    /// <summary>
    /// A named gateway <see cref="HttpClient"/> (<c>gateway-chat</c>, <c>gateway-query</c>, …) with the
    /// <see cref="ResidencyCeilingHandler"/>: base address <c>Gateway:BaseUrl</c>, the per-level keys from
    /// <c>Gateway:Keys</c>, and no standard resilience handler (the gateway retries before its first byte; chat design
    /// §8.3). Every gateway call of the services goes through a client registered here.
    /// </summary>
    public static IHttpClientBuilder AddGatewayClient(this IHostApplicationBuilder builder, string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<GatewayOptions>().BindConfiguration(GatewayOptions.SectionName);
        builder.Services.TryAddTransient<ResidencyCeilingHandler>();
        return builder.Services.AddHttpClient(name, (sp, client) =>
            {
                if (sp.GetRequiredService<IOptions<GatewayOptions>>().Value.BaseUrl is { Length: > 0 } baseUrl)
                {
                    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
                }
            })
            .RemoveStandardResilienceHandler()
            .AddHttpMessageHandler<ResidencyCeilingHandler>();
    }
}
