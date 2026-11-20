using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Kalla.Infrastructure.Config;

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
    ILogger<ConfigVersionMonitor> logger,
    IEnumerable<INotificationListener> listeners) : BackgroundService
{
    public const string ListenConnectionName = "ragdb-listen";
    public const string ConfigChannel = "rag_config";
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MinimumWait = TimeSpan.FromMilliseconds(10);
    private readonly ILookup<string, INotificationListener> _listeners = Validated(listeners).ToLookup(l => l.Channel, StringComparer.Ordinal);
    private int _notified;
    private long _refreshes;

    /// <summary>How many times the version was read (tests: other channels must not cause config reads).</summary>
    internal long Refreshes => Interlocked.Read(ref _refreshes);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = ConnectionString(configuration);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                connection.Notification += (_, e) => OnNotification(e.Channel, e.Payload);
                await connection.OpenAsync(stoppingToken);
                // Channel names are validated identifiers (Validated), so they can be put into the statement.
                foreach (var channel in _listeners.Select(g => g.Key).Prepend(ConfigChannel).Distinct(StringComparer.Ordinal))
                {
                    await using var listen = new NpgsqlCommand($"LISTEN {channel}", connection);
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }

                while (true)
                {
                    Interlocked.Exchange(ref _notified, 0);
                    var applied = await RefreshAsync(connection, stoppingToken);
                    // A notification that arrived while refreshing was consumed by the refresh's own commands, so it
                    // refreshes again at once; after a handler failure the next poll offers the change again.
                    if (!applied)
                    {
                        Interlocked.Exchange(ref _notified, 0);
                    }

                    if (Volatile.Read(ref _notified) == 0)
                    {
                        await WaitForConfigAsync(connection, stoppingToken);
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
                LogConnectionFailed(logger, ex, state.Version);
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

    /// <summary>
    /// Confirms the database's version, applying the change first if it moved. False when a handler failed: the version
    /// stays unconfirmed (fail-closed; 503 after the grace window) and the change is offered again on the next poll.
    /// </summary>
    private async Task<bool> RefreshAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _refreshes);
        var current = await ScalarAsync<long>(connection, """SELECT "Version" FROM config."ConfigVersion" WHERE "Id" = 1""", cancellationToken);
        var cached = state.Version;
        if (cached == current)
        {
            state.Confirm(current);
            return true;
        }

        var change = await ChangeAsync(connection, cached, current, cancellationToken);
        var started = Stopwatch.GetTimestamp();
        await using (var scope = scopes.CreateAsyncScope())
        {
            foreach (var handler in scope.ServiceProvider.GetServices<IConfigChangeHandler>())
            {
                try
                {
                    await handler.ApplyAsync(change, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    LogHandlerFailed(logger, ex, handler.GetType().Name, cached, current);
                    return false;
                }
            }
        }

        state.Confirm(current);
        LogApplied(logger, cached, current, change.FullReload, string.Join(',', change.EntityTypes), Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return true;
    }

    /// <summary>
    /// Waits for a config notification or the poll deadline. Notifications on other channels wake the wait up, are passed
    /// to their listeners by <see cref="OnNotification"/>, and the wait goes on to the same deadline, so a burst of job
    /// notifications costs no config reads.
    /// </summary>
    private async Task WaitForConfigAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (Volatile.Read(ref _notified) == 0)
        {
            if (WaitMilliseconds(PollInterval - Stopwatch.GetElapsedTime(started)) is not { } milliseconds)
            {
                return;
            }

            await connection.WaitAsync(milliseconds, cancellationToken);
        }
    }

    /// <summary>
    /// The timeout to pass to Npgsql's <c>WaitAsync(int)</c> for <paramref name="remaining"/>, or null when the deadline
    /// counts as reached. Npgsql takes whole milliseconds and treats 0 (or less) as an infinite wait, and its timer may
    /// return a little early, so a remainder under <see cref="MinimumWait"/> is "reached" and the rest is rounded up,
    /// never down to 0 (review of #24).
    /// </summary>
    internal static int? WaitMilliseconds(TimeSpan remaining) =>
        remaining < MinimumWait ? null : (int)Math.Ceiling(remaining.TotalMilliseconds);

    private void OnNotification(string channel, string payload)
    {
        if (channel == ConfigChannel)
        {
            Interlocked.Exchange(ref _notified, 1);
            return;
        }

        foreach (var listener in _listeners[channel])
        {
            try
            {
                listener.OnNotification(payload);
            }
            catch (Exception ex)
            {
                LogListenerFailed(logger, ex, listener.GetType().Name, channel);
            }
        }
    }

    private static IEnumerable<INotificationListener> Validated(IEnumerable<INotificationListener> listeners)
    {
        foreach (var listener in listeners)
        {
            if (listener.Channel is not { Length: > 0 and <= 63 } channel || !channel.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_') ||
                char.IsDigit(channel[0]))
            {
                throw new InvalidOperationException($"Notification channel '{listener.Channel}' must be a lower-case identifier.");
            }

            yield return listener;
        }
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

    [LoggerMessage(LogLevel.Warning, "Config monitor lost its database connection; serving version {Version} within the grace window and reconnecting")]
    private static partial void LogConnectionFailed(ILogger logger, Exception exception, long version);

    [LoggerMessage(LogLevel.Error, "Notification listener {Listener} failed on channel {Channel}")]
    private static partial void LogListenerFailed(ILogger logger, Exception exception, string listener, string channel);

    [LoggerMessage(LogLevel.Error, "Config change handler {Handler} failed applying version {From} -> {To}; version {From} stays unconfirmed and the change is retried on the next poll")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, string handler, long from, long to);
}
