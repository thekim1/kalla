using System.Data.Common;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Ume.Rag.Domain;
using Ume.Rag.Infrastructure;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Blobs;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Worker.Commands;
using Ume.Rag.Worker.Connectors;
using Ume.Rag.Worker.Engine;

namespace Ume.Rag.Worker.Jobs;

public static class WorkerExtensions
{
    /// <summary>The worker's pool per instance (data-model §1.1: 10, plus the one listening connection; role cap 12).</summary>
    public const int DefaultMaxPoolSize = 10;

    /// <summary>Shutdown time beyond the stop grace: releasing the cancelled jobs and unregistering the instance.</summary>
    public static readonly TimeSpan ShutdownMargin = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gives <c>ConnectionStrings:ragdb</c> a <c>Maximum Pool Size</c> of <see cref="DefaultMaxPoolSize"/> when it names none
    /// (Npgsql's default is 100), so the queue and <c>RagDbContext</c>, which share the one data source built from it, stay
    /// within the role cap. Call before <c>AddRagDatabase</c>, which builds that data source from the string; an explicit
    /// value is kept.
    /// </summary>
    public static IHostApplicationBuilder DefaultWorkerPoolSize(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var name = InfrastructureExtensions.DatabaseResourceName;
        var value = builder.Configuration.GetConnectionString(name);
        if (string.IsNullOrWhiteSpace(value) || NamesMaxPoolSize(value))
        {
            return builder;
        }

        var limited = new NpgsqlConnectionStringBuilder(value) { MaxPoolSize = DefaultMaxPoolSize };
        builder.Configuration.AddInMemoryCollection([new KeyValuePair<string, string?>($"ConnectionStrings:{name}", limited.ConnectionString)]);
        return builder;
    }

    /// <summary>
    /// The job machinery (engine design §9): options, the Postgres queue, the blob store, instance registration with the stage
    /// manifest and heartbeat, the dispatcher, the reaper, the nightly maintenance with the engine's sweeps, the <c>command</c> handler with its kinds (and the
    /// index-health check behind <c>check-hnsw-index</c>), and the connectors'
    /// shared fetch path (<see cref="ConnectorExtensions.AddRagConnectors"/>). Hosted services stop
    /// in reverse order: the dispatcher drains before the heartbeat removes the instance. Call after <c>AddRagDatabase</c>
    /// (the queue's pool) and with <c>AddRagConfigMonitor</c>, whose listening connection carries <c>rag_jobs</c> to
    /// <see cref="JobWakeUps"/>; without the monitor, other processes' jobs wait for the poll.
    /// </summary>
    public static IHostApplicationBuilder AddRagWorker(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOptions<WorkerOptions>().BindConfiguration(WorkerOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<WorkerOptions>, WorkerOptionsValidator>());
        // The drain must fit the host's shutdown timeout (.NET default 30 s); a longer configured timeout is kept. Read from
        // configuration, not the validated options, so an invalid Worker section still fails at start with its own message.
        builder.Services.AddOptions<HostOptions>().PostConfigure<IConfiguration>((host, configuration) =>
        {
            var grace = configuration.GetValue($"{WorkerOptions.SectionName}:{nameof(WorkerOptions.StopGraceSeconds)}", new WorkerOptions().StopGraceSeconds);
            var needed = TimeSpan.FromSeconds(grace) + ShutdownMargin;
            if (host.ShutdownTimeout < needed)
            {
                host.ShutdownTimeout = needed;
            }
        });
        // The worker's credential key allows OnPrem and Eu only: a call above Eu is refused before sending (identity design §4.7).
        builder.CapGatewayCalls(Residency.Eu);
        builder.AddRagJobQueue();
        builder.AddRagBlobStore();
        builder.AddRagBlobEnvelope();
        builder.AddRagConnectors();
        // The same JobWakeUps the dispatcher waits on. Here, not in AddRagJobQueue: only the worker dispatches, and a service
        // that only enqueues would have its monitor woken by every job notification for semaphores nobody waits on.
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<INotificationListener, JobWakeUps>(sp => sp.GetRequiredService<JobWakeUps>()));
        builder.Services.TryAddSingleton<WorkerInstanceStore>();
        builder.Services.TryAddSingleton<JobReaper>();
        builder.Services.TryAddSingleton<ContentMaintenance>();
        builder.Services.TryAddSingleton<WorkerIdentity>();
        builder.Services.TryAddSingleton<RunningJobs>();
        builder.Services.TryAddSingleton<JobMetrics>();
        // What registration, cancel-run and maintenance need from the engine (the rest is AddRagEngine's).
        builder.Services.TryAddSingleton<StageManifestRegistrar>();
        builder.Services.TryAddSingleton<EngineSettings>();
        builder.Services.TryAddSingleton<RunStore>();
        builder.Services.TryAddSingleton<IndexWriter>();
        builder.Services.TryAddSingleton<BlobPurgeSweep>();
        builder.Services.TryAddSingleton<HnswIndexMetrics>();
        builder.Services.TryAddSingleton(sp => new HnswIndexHealth(sp.GetRequiredService<NpgsqlDataSource>(),
            HnswIndexHealth.ConnectionString(builder.Configuration), sp.GetRequiredService<HnswIndexMetrics>(), sp.GetRequiredService<ILogger<HnswIndexHealth>>()));
        // The keyword stop list's statistics (knowledge design §6.2): on the worker's pool, so they stay inside the role's
        // connection budget (data-model §1.1); statement_timeout = 0 is SET LOCAL in the refresh's transaction.
        builder.Services.TryAddSingleton(sp => new KeywordStatistics(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<KeywordStatistics>>()));
        builder.Services.TryAddSingleton<KeywordStatisticsService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<KeywordStatisticsService>());

        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IJobHandler, CommandJobHandler>());
        builder.Services.AddRagCommands();

        builder.Services.AddHostedService<WorkerHeartbeat>();
        builder.Services.AddHostedService<JobDispatcher>();
        builder.Services.AddHostedService<JobReaperService>();
        builder.Services.AddSingleton<MaintenanceService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MaintenanceService>());
        return builder;
    }

    private static bool NamesMaxPoolSize(string connectionString)
    {
        var keys = new DbConnectionStringBuilder { ConnectionString = connectionString }.Keys.Cast<string>();
        return keys.Any(k => k.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant() is "MAXIMUMPOOLSIZE" or "MAXPOOLSIZE");
    }
}
