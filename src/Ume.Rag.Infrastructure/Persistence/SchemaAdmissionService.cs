using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Ume.Rag.Infrastructure.Persistence;

/// <summary>
/// The first hosted service of rag-api, rag-admin and rag-worker (<c>AddRagDatabase(admitAtStart: true)</c>): asks the
/// database for admission with the last migration compiled into this service (<see cref="ServiceAdmission"/>,
/// data-model §10). A database without <c>public.ume_rag_admit</c> (development, or migrated before the function existed)
/// is not asked; a role without a fence is admitted with nothing to do. A refusal (the schema is past a migration that
/// stops this role, and this build does not contain it) is logged as Critical and fails the start, so the old container
/// does not run beside the new schema.
/// </summary>
public sealed partial class SchemaAdmissionService(
    NpgsqlDataSource dataSource,
    IServiceScopeFactory scopes,
    ILogger<SchemaAdmissionService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        string build;
        await using (var scope = scopes.CreateAsyncScope())
        {
            build = scope.ServiceProvider.GetRequiredService<RagDbContext>().Database.GetMigrations().Last();
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var exists = new NpgsqlCommand("SELECT to_regprocedure('public.ume_rag_admit(text)') IS NOT NULL", connection))
        {
            if (await exists.ExecuteScalarAsync(cancellationToken) is not true)
            {
                return;
            }
        }

        await using var admit = new NpgsqlCommand("SELECT public.ume_rag_admit($1)", connection);
        admit.Parameters.Add(new NpgsqlParameter<string> { TypedValue = build });
        try
        {
            await admit.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == ServiceAdmission.RefusedSqlState)
        {
            LogRefused(logger, ex.MessageText, build);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(LogLevel.Critical,
        "The database's schema is newer than this version can run beside ({Refusal}; this build: {Build}). This container waits to be replaced by the version that migrated it")]
    private static partial void LogRefused(ILogger logger, string refusal, string build);
}
