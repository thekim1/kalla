using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Ume.Rag.Infrastructure.Config;

/// <summary>
/// Keeps this process on the current config version (data-model §5, §4.3): <c>LISTEN rag_config</c> plus a poll of
/// <c>config."ConfigVersion"</c> every <see cref="PollInterval"/>, so a missed notification costs seconds. When the version
/// moves, the outbox rows in between name the changed entity types; every <see cref="IConfigChangeHandler"/> reloads,
/// then the version is confirmed. A version the database still reports is confirmed again on each poll, which keeps the
/// grace window of <see cref="ConfigVersionState"/> open while the database answers.
/// </summary>
/// <remarks>
/// Runs on one dedicated connection (<c>ConnectionStrings:ragdb-listen</c>, else <c>ragdb</c>; counted as the "+1
/// LISTEN" of data-model §1.1), never a pooled one, so a later pooler never sees session state.
/// </remarks>
public sealed partial class ConfigVersionMonitor(
    ConfigVersionState state,
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<ConfigVersionMonitor> logger) : BackgroundService
{
    public const string ListenConnectionName = "ragdb-listen";
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);
    private int _notified;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = ConnectionString(configuration);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                connection.Notification += (_, _) => Interlocked.Exchange(ref _notified, 1);
                await connection.OpenAsync(stoppingToken);
                await using (var listen = new NpgsqlCommand("LISTEN rag_config", connection))
                {
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }

                while (true)
                {
                    Interlocked.Exchange(ref _notified, 0);
                    await RefreshAsync(connection, stoppingToken);
                    // A notification that arrived while refreshing was consumed by the refresh's own commands.
                    if (Volatile.Read(ref _notified) == 0)
                    {
                        await connection.WaitAsync(PollInterval, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The grace window keeps decisions working for Config:MaxUnconfirmedSeconds (identity design §4.5).
                LogConnectionFailed(logger, ex.GetType().Name, state.Version);
                await Task.Delay(ReconnectDelay, time, stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    /// <summary>The connection string the monitor listens on.</summary>
    public static string ConnectionString(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var value = configuration.GetConnectionString(ListenConnectionName) is { Length: > 0 } listen
            ? listen
            : configuration.GetConnectionString(InfrastructureExtensions.DatabaseResourceName)
              ?? throw new InvalidOperationException($"ConnectionStrings:{InfrastructureExtensions.DatabaseResourceName} is not configured.");
        // A dedicated session: never returned to a pool another component could reuse.
        return new NpgsqlConnectionStringBuilder(value) { Pooling = false }.ConnectionString;
    }

    private async Task RefreshAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var current = await ScalarAsync<long>(connection, """SELECT "Version" FROM config."ConfigVersion" WHERE "Id" = 1""", cancellationToken);
        var cached = state.Version;
        if (cached == current)
        {
            state.Confirm(current);
            return;
        }

        var change = await ChangeAsync(connection, cached, current, cancellationToken);
        var started = Stopwatch.GetTimestamp();
        await using (var scope = scopes.CreateAsyncScope())
        {
            foreach (var handler in scope.ServiceProvider.GetServices<IConfigChangeHandler>())
            {
                await handler.ApplyAsync(change, cancellationToken);
            }
        }

        state.Confirm(current);
        LogApplied(logger, cached, current, change.FullReload, string.Join(',', change.EntityTypes), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static async Task<ConfigChange> ChangeAsync(NpgsqlConnection connection, long cached, long current, CancellationToken cancellationToken)
    {
        // First load, a version that went backwards (restore) or outbox rows already pruned: reload everything.
        var oldest = await ScalarAsync<long?>(connection, """SELECT min("Version") FROM config."ConfigOutbox" """, cancellationToken);
        if (cached < 0 || current < cached || oldest is null || oldest > cached + 1)
        {
            return new ConfigChange(cached, current, FullReload: true, new HashSet<string>(StringComparer.Ordinal), SecurityRelevant: true);
        }

        var types = new HashSet<string>(StringComparer.Ordinal);
        var securityRelevant = false;
        await using var command = new NpgsqlCommand("""
            SELECT "EntityType", bool_or("SecurityRelevant") FROM config."ConfigOutbox"
            WHERE "Version" > $1 AND "Version" <= $2 GROUP BY "EntityType"
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = cached });
        command.Parameters.Add(new NpgsqlParameter { Value = current });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            types.Add(reader.GetString(0));
            securityRelevant |= reader.GetBoolean(1);
        }

        return new ConfigChange(cached, current, FullReload: false, types, securityRelevant);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? default! : (T)value;
    }

    [LoggerMessage(LogLevel.Information, "Config version {From} -> {To} applied (full reload {FullReload}, types {EntityTypes}) in {ElapsedMs:F1} ms")]
    private static partial void LogApplied(ILogger logger, long from, long to, bool fullReload, string entityTypes, double elapsedMs);

    [LoggerMessage(LogLevel.Warning, "Config monitor lost its connection ({ExceptionType}); serving version {Version} within the grace window")]
    private static partial void LogConnectionFailed(ILogger logger, string exceptionType, long version);
}
