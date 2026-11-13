using System.Collections.Concurrent;
using Npgsql;

namespace Ume.Rag.Api;

/// <summary>
/// Writes when a key was last used to <c>config."KeyLastUse"</c>, at most every <see cref="Interval"/> per key and instance
/// (identity design §6.2), so key use never bumps the config version (data-model §2.1). The write runs beside the request
/// and never fails it: a failed write is logged and tried again on the key's next use. The admin's Apps and keys page
/// shows the value (admin design §5.9.1).
/// </summary>
public sealed partial class KeyLastUseRecorder(NpgsqlDataSource data, TimeProvider time, ILogger<KeyLastUseRecorder> log)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>The last write per key on this instance; one entry per key that was used here.</summary>
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _written = new();

    /// <summary>Records a use of the key that just authenticated; the returned task is the write, or done when none was due.</summary>
    public Task TouchAsync(Guid keyId)
    {
        var now = time.GetUtcNow();
        if (_written.TryGetValue(keyId, out var last) && now - last < Interval)
        {
            return Task.CompletedTask;
        }

        // Two requests of one key may both pass the check; the second write is harmless.
        _written[keyId] = now;
        return WriteAsync(keyId, now);
    }

    private async Task WriteAsync(Guid keyId, DateTimeOffset now)
    {
        try
        {
            await using var command = data.CreateCommand("""
                INSERT INTO config."KeyLastUse" ("KeyId", "LastUsedAt") VALUES ($1, $2)
                ON CONFLICT ("KeyId") DO UPDATE SET "LastUsedAt" = EXCLUDED."LastUsedAt" WHERE config."KeyLastUse"."LastUsedAt" < EXCLUDED."LastUsedAt"
                """);
            command.Parameters.Add(new NpgsqlParameter { Value = keyId });
            command.Parameters.Add(new NpgsqlParameter { Value = now.ToUniversalTime() });
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // The write is beside the request: whatever fails, the request goes on and the next use tries again.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _written.TryRemove(keyId, out _);
            LogFailed(log, ex, keyId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The last use of key {KeyId} could not be recorded; the next use tries again")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid keyId);
}
