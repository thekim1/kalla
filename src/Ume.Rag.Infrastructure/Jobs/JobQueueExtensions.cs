using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Ume.Rag.Domain.Ports;

namespace Ume.Rag.Infrastructure.Jobs;

public static class JobQueueExtensions
{
    /// <summary>
    /// The Postgres <see cref="IJobQueue"/> on <c>ConnectionStrings:ragdb</c> (the pool <c>RagDbContext</c> uses). Enough for a
    /// service that only enqueues (the admin, hand-off H4); the worker adds the reaper, instance store and maintenance.
    /// </summary>
    public static IHostApplicationBuilder AddRagJobQueue(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<JobDatabase>();
        builder.Services.TryAddSingleton<JobWakeUps>();
        builder.Services.TryAddSingleton<IJobQueue, PostgresJobQueue>();
        return builder;
    }
}
