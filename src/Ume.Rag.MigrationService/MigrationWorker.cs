namespace Ume.Rag.MigrationService;

/// <summary>
/// One-shot migrator: applies the schema, then stops the host so dependants waiting with <c>WaitForCompletion</c> start.
/// Stage 0 has no schema yet; stream B adds the migrations, <c>app-roles.sql</c> and the seeder (P0 checklist #6).
/// </summary>
internal sealed partial class MigrationWorker(IHostApplicationLifetime lifetime, ILogger<MigrationWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogNothingToApply(logger);
        lifetime.StopApplication();
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "No migrations to apply")]
    private static partial void LogNothingToApply(ILogger logger);
}
