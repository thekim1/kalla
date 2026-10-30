using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Infrastructure.Persistence;

namespace Ume.Rag.MigrationService;

/// <summary>
/// Applies EF Core migrations (expand/contract, backwards compatible), then <c>app-roles.sql</c>
/// (<c>Migration:RolesSqlPath</c>, set by production compose; dev runs as the bootstrap superuser without the service
/// roles) and the run-item partition maintenance, then exits (data-model §10, as the gateway's MigrationWorker). The
/// services wait for this to complete, so they never run against an older schema. The dev seeder follows with the first
/// seeded table.
/// </summary>
public sealed partial class MigrationWorker(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    IConfiguration configuration,
    ILogger<MigrationWorker> logger) : BackgroundService
{
    private static readonly ActivitySource Source = new(Extensions.TelemetryName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var activity = Source.StartActivity("Migrate database", ActivityKind.Internal);
        try
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
            await RagDatabaseMigrator.MigrateAsync(db, configuration["Migration:RolesSqlPath"], stoppingToken);
            if (logger.IsEnabled(LogLevel.Information))
            {
                LogMigrated(logger, (await db.Database.GetAppliedMigrationsAsync(stoppingToken)).LastOrDefault());
            }
        }
        catch (Exception ex)
        {
            activity?.AddException(ex);
            LogFailed(logger, ex);
            Environment.ExitCode = 1;
            throw;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    [LoggerMessage(LogLevel.Information, "Database schema is at migration {Migration}")]
    private static partial void LogMigrated(ILogger logger, string? migration);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Database migration failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
