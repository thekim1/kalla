using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Ume.Rag.Infrastructure.Persistence;
using Ume.Rag.Infrastructure.Security;
using Ume.Rag.Pipelines.Definitions;

namespace Ume.Rag.MigrationService;

/// <summary>
/// Applies EF Core migrations (expand/contract, backwards compatible), then <c>app-roles.sql</c>
/// (<c>Migration:RolesSqlPath</c>, set by production compose; dev runs as the bootstrap superuser without the service
/// roles) and the run-item partition maintenance, in Development the HNSW defaults on the database
/// (<see cref="DevDatabaseSettings"/>), then the platform pipelines (<see cref="PipelineTemplateSeeder"/>), the first settings row (<see cref="SettingsSeeder"/>) and the <see cref="DevSeeder"/> when <c>Seed:Enabled</c> (with the dev Open WebUI
/// client when <c>Seed:DevKey</c> is set), and exits
/// (data-model §10; the platform's embedding profile is created by a platform admin in the admin UI, Decisions "Deploy
/// configuration", as the gateway's MigrationWorker). The services wait for this to complete, so they never run against
/// an older schema.
/// </summary>
public sealed partial class MigrationWorker(
    IServiceProvider services,
    IHostApplicationLifetime lifetime,
    IHostEnvironment environment,
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
            if (environment.IsDevelopment())
            {
                await DevDatabaseSettings.ApplyAsync(db, stoppingToken);
            }

            await PipelineTemplateSeeder.SeedAsync(db, [.. TemplateCatalog.BuiltIn.All.Select(t => t.Template)], TimeProvider.System,
                stoppingToken);
            await SettingsSeeder.SeedAsync(db,
                configuration.GetSection(SettingsSeedOptions.SectionName).Get<SettingsSeedOptions>() ?? new SettingsSeedOptions(),
                TimeProvider.System, logger, stoppingToken);
            // The deployment's own Open WebUI client, from the key the deployment generated (identity design §5.2, M-17 C4).
            if (configuration["OpenWebUi:ApiKey"] is { Length: > 0 } openWebUiKey)
            {
                var hasher = KeyHasherFactory.Create(new SecurityOptions { KeyPepper = configuration["Security:KeyPepper"] ?? "" });
                LogOpenWebUiClient(logger, await OpenWebUiClientSeeder.SeedAsync(db, hasher, openWebUiKey.Trim(), configuration["OpenWebUi:PortalUrl"],
                    TimeProvider.System, stoppingToken));
            }

            if (configuration.GetValue("Seed:Enabled", false))
            {
                await DevSeeder.SeedAsync(db, TimeProvider.System, stoppingToken);
                if (configuration["Seed:DevKey"] is { Length: > 0 } devKey)
                {
                    var hasher = KeyHasherFactory.Create(new SecurityOptions { KeyPepper = configuration["Security:KeyPepper"] ?? "" });
                    await DevSeeder.SeedDevClientAsync(db, hasher, devKey, configuration["Seed:DevClientPortalUrl"], TimeProvider.System,
                        stoppingToken);
                }
            }

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

    [LoggerMessage(LogLevel.Information, "The deployment's Open WebUI client: {Outcome}")]
    private static partial void LogOpenWebUiClient(ILogger logger, OpenWebUiClientSeeder.Outcome outcome);

    [LoggerMessage(LogLevel.Information, "Database schema is at migration {Migration}")]
    private static partial void LogMigrated(ILogger logger, string? migration);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Database migration failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
