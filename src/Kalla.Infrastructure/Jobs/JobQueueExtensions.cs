using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Kalla.Domain.Ports;

namespace Kalla.Infrastructure.Jobs;

public static class JobQueueExtensions
{
    /// <summary>
    /// The Postgres <see cref="IJobQueue"/> on the <c>ragdb</c> <see cref="NpgsqlDataSource"/> that <c>RagDbContext</c> uses,
    /// so call <c>AddRagDatabase</c> first (checked here). Enough for a service that only enqueues (the admin, hand-off
    /// H4): its enqueues notify <c>rag_jobs</c>, and it does not listen. The worker adds the listener, the reaper, the
    /// instance store and maintenance.
    /// </summary>
    public static IHostApplicationBuilder AddRagJobQueue(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!builder.Services.Any(d => d.ServiceType == typeof(NpgsqlDataSource)))
        {
            throw new InvalidOperationException("AddRagJobQueue needs the ragdb NpgsqlDataSource: call AddRagDatabase first.");
        }

        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<JobDatabase>();
        builder.Services.TryAddSingleton<JobWakeUps>();
        builder.Services.TryAddSingleton<IJobQueue, PostgresJobQueue>();
        return builder;
    }
}
