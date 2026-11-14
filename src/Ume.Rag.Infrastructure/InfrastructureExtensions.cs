using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.Infrastructure.Keys;
using Ume.Rag.Infrastructure.Security;
using Ume.Rag.Infrastructure.Persistence;

namespace Ume.Rag.Infrastructure;

public static class InfrastructureExtensions
{
    public const string DatabaseResourceName = "ragdb";
    public const string DataProtectionApplicationName = "Ume.Rag";

    /// <summary>The file names <c>Security:ExtraSecretsDirectory</c> may hold (M-17 C6).</summary>
    public static IReadOnlyList<string> ExtraSecretKeys { get; } = ["OpenWebUi__AdminApiKey"];

    /// <summary>
    /// Secret files (<c>Security:SecretsDirectory</c>, one file per key) as configuration, as in the gateway. Reloaded on
    /// change, so a key written by a deployment's bootstrap (a Portainer stack update) applies without a restart where the
    /// code reads it through <c>IOptionsMonitor</c>: the gateway credential key and the Open WebUI feedback key.
    /// <c>Security:ExtraSecretsDirectory</c> is a second such directory that another job writes, so that job never sees
    /// this service's own secrets: rag-api's Open WebUI feedback key from <c>rag-openwebui-setup</c> (M-17 C6). It is
    /// optional, and it yields only <see cref="ExtraSecretKeys"/>: another uid owns it, so any other file there (a
    /// <c>Gateway__BaseUrl</c>, say) is ignored rather than overriding this service's configuration (#689 review).
    /// </summary>
    public static IHostApplicationBuilder AddDeploymentSecrets(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var directory = builder.Configuration["Security:SecretsDirectory"];
        if (!string.IsNullOrWhiteSpace(directory))
        {
            builder.Configuration.AddKeyPerFile(directory, optional: false, reloadOnChange: true);
        }

        var extra = builder.Configuration["Security:ExtraSecretsDirectory"];
        if (!string.IsNullOrWhiteSpace(extra))
        {
            builder.Configuration.AddKeyPerFile(source =>
            {
                // As AddKeyPerFile(path, optional) does: no provider for a missing directory, which optional allows.
                source.FileProvider = Directory.Exists(extra) ? new PhysicalFileProvider(extra) : null;
                source.Optional = true;
                source.ReloadOnChange = true;
                // Only the keys another job writes here; its "ignore." temp file and anything else are skipped.
                source.IgnoreCondition = name => !ExtraSecretKeys.Contains(name, StringComparer.Ordinal);
            });
        }

        return builder;
    }

    /// <summary>
    /// One <see cref="NpgsqlDataSource"/> for <c>ragdb</c>, registered as a singleton and shared by EF Core and raw Npgsql
    /// (issue #22), so a process has one connection pool of <c>Maximum Pool Size</c> whatever mix of EF and SQL it runs
    /// (data-model §1.1 budget). pgvector's types are mapped on it (<c>UseVector()</c>, #110), so raw Npgsql can bind
    /// <c>Pgvector.Vector</c> and write embeddings with binary <c>COPY</c>; the <c>vector</c> columns stay out of the EF model. EF Core on top of it with
    /// Aspire's enrichment (context pooling, retries, health check, tracing). Production refuses the superuser and
    /// anything but <c>VerifyFull</c> TLS, as the gateway's <c>AddGatewayDatabase</c>. <paramref name="configure"/> adds a
    /// service's own data source settings to that one pool (rag-api: auto-prepare for the retrieval statements, knowledge §6.6).
    /// </summary>
    public static IHostApplicationBuilder AddRagDatabase(this IHostApplicationBuilder builder, Action<NpgsqlDataSourceBuilder>? configure = null)
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

        var connectionString = builder.Configuration.GetConnectionString(DatabaseResourceName)
            ?? throw new InvalidOperationException($"ConnectionStrings:{DatabaseResourceName} is not configured.");
        builder.Services.TryAddSingleton(_ =>
        {
            var dataSource = new NpgsqlDataSourceBuilder(connectionString);
            dataSource.UseVector();
            configure?.Invoke(dataSource);
            return dataSource.Build();
        });
        builder.Services.AddDbContextPool<RagDbContext>((sp, options) =>
            options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), RagDbContext.ConfigureNpgsql));
        // Its Npgsql tracing registers a tracer provider of its own: only when traces are exported (#503, ServiceDefaults).
        var exporting = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        builder.EnrichNpgsqlDbContext<RagDbContext>(settings => settings.DisableTracing |= !exporting);
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
    /// <see cref="ResidencyCeilingHandler"/>: base address <c>Gateway:BaseUrl</c>, the service's credential key
    /// <c>Gateway:Key</c>, and no standard resilience handler (the gateway retries before its first byte; chat design
    /// §8.3). Inside it the <see cref="GatewayRetryHandler"/> sends a <c>502</c>/<c>504</c> once more with the same ceiling
    /// (#390), unless <paramref name="retryFailed"/> is false for a caller with its own retry (the query embedding,
    /// knowledge §5.3). Every gateway call of the services goes through a client registered here.
    /// </summary>
    public static IHttpClientBuilder AddGatewayClient(this IHostApplicationBuilder builder, string name, bool retryFailed = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<GatewayOptions>().BindConfiguration(GatewayOptions.SectionName);
        builder.Services.TryAddTransient<ResidencyCeilingHandler>();
        builder.Services.TryAddTransient<GatewayRetryHandler>();
        // Phase 2 (identity §4.7): the service's own context read for the gateway's features, once per service.
        if (!builder.Services.Any(d => d.ServiceType == typeof(GatewayFeatures)))
        {
            builder.Services.AddSingleton<GatewayFeatures>();
            builder.Services.TryAddSingleton(TimeProvider.System);
            Client(builder, GatewayContextReader.HttpClientName);
            builder.Services.AddSingleton<GatewayContextReader>();
            builder.Services.AddHostedService(sp => sp.GetRequiredService<GatewayContextReader>());
            // The credential key's residencies are the service's ceiling (M-17 C9).
            builder.Services.AddSingleton<GatewayCeiling>();
        }

        var client = Client(builder, name);
        return retryFailed ? client.AddHttpMessageHandler<GatewayRetryHandler>() : client;
    }

    private static IHttpClientBuilder Client(IHostApplicationBuilder builder, string name) =>
        builder.Services.AddHttpClient(name, (sp, client) =>
            {
                if (sp.GetRequiredService<IOptions<GatewayOptions>>().Value.BaseUrl is { Length: > 0 } baseUrl)
                {
                    client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
                }
            })
            .RemoveStandardResilienceHandler()
            .AddHttpMessageHandler<ResidencyCeilingHandler>();

    /// <summary>
    /// Caps every gateway call of this service at <paramref name="maximum"/> (<see cref="GatewayOptions.ServiceMaximum"/>):
    /// a call above it is refused before sending, whatever its credential key's residencies allow. The worker calls it with
    /// <c>Eu</c> (identity design §4.7).
    /// </summary>
    public static IHostApplicationBuilder CapGatewayCalls(this IHostApplicationBuilder builder, Residency maximum)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.PostConfigure<GatewayOptions>(o => o.ServiceMaximum = maximum);
        return builder;
    }

    /// <summary>
    /// ASP.NET Data Protection with the key ring in <c>config."DataProtectionKeys"</c> (data-model §2.1, as the gateway's
    /// <c>AddGatewaySecurity</c>): admin sessions and revealable key secrets. Production protects the key ring at rest with a
    /// certificate (<c>DataProtection:CertificatePath</c>); without one it is stored unencrypted (local development only).
    /// </summary>
    public static IHostApplicationBuilder AddRagDataProtection(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var dataProtection = builder.Services.AddDataProtection()
            .SetApplicationName(DataProtectionApplicationName)
            .PersistKeysToDbContext<RagDbContext>();
        var certPath = builder.Configuration["DataProtection:CertificatePath"];
        if (builder.Environment.IsProduction() && string.IsNullOrWhiteSpace(certPath))
        {
            throw new InvalidOperationException("Production requires DataProtection:CertificatePath to protect the shared key ring.");
        }

        if (!string.IsNullOrWhiteSpace(certPath))
        {
            dataProtection.ProtectKeysWithCertificate(
                X509CertificateLoader.LoadPkcs12FromFile(certPath, builder.Configuration["DataProtection:CertificatePassword"]));
        }

        return builder;
    }
}
