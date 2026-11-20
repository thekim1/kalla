using Microsoft.Extensions.Logging;
using Npgsql;
using Kalla.Domain;

namespace Kalla.Infrastructure.Blobs;

/// <summary>Which master key wraps the data keys of a level's blobs (data-model §7; null: not envelope-encrypted).</summary>
public interface IBlobKeyPolicy
{
    Task<string?> MasterKeyNameAsync(string level, CancellationToken cancellationToken);
}

/// <summary>
/// <c>config."DataProtectionPolicies"."BlobMasterKeyName"</c> per level, read at most once per <see cref="MaxAge"/>. No row
/// means the defaults in code (<see cref="DefaultKeyName"/>), as for <c>Settings</c>; a row of a level other than
/// <c>public</c> can rename the key but never switch encryption off, and a failed read keeps the last values (at start:
/// the defaults), so a level is never stored in plaintext by mistake.
/// </summary>
public sealed partial class DataProtectionBlobKeyPolicy(NpgsqlDataSource dataSource, TimeProvider time, ILogger<DataProtectionBlobKeyPolicy> logger)
    : IBlobKeyPolicy
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);

    private const string ReadSql = """
        SELECT "Classification", "BlobMasterKeyName" FROM config."DataProtectionPolicies" ORDER BY "Classification"
        """;

    private volatile Snapshot? _snapshot;

    /// <summary><c>rag-blob-&lt;level&gt;</c> for every level but <c>public</c>, whose blobs are not envelope-encrypted.</summary>
    public static string? DefaultKeyName(string level) => level == Classification.Public ? null : $"rag-blob-{level}";

    public async Task<string?> MasterKeyNameAsync(string level, CancellationToken cancellationToken)
    {
        if (!Classification.IsValid(level))
        {
            throw new ArgumentException($"'{level}' is not a level.", nameof(level));
        }

        var now = time.GetUtcNow();
        if (_snapshot is not { } current || now - current.ReadAt >= MaxAge)
        {
            current = new Snapshot(await ReadAsync(cancellationToken) ?? _snapshot?.Names ?? new Dictionary<string, string?>(StringComparer.Ordinal), now);
            _snapshot = current;
        }

        var named = current.Names.GetValueOrDefault(level);
        return level == Classification.Public ? named : named ?? DefaultKeyName(level);
    }

    private async Task<Dictionary<string, string?>?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(ReadSql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var names = new Dictionary<string, string?>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken))
            {
                names[reader.GetString(0)] = await reader.IsDBNullAsync(1, cancellationToken) ? null : reader.GetString(1);
            }

            return names;
        }
        catch (Exception e) when (e is NpgsqlException or TimeoutException)
        {
            LogReadFailed(logger, e.GetType().Name);
            return null;
        }
    }

    [LoggerMessage(LogLevel.Warning, "Blob key policies could not be read ({ExceptionType}); the last values stay in force.")]
    private static partial void LogReadFailed(ILogger logger, string exceptionType);

    private sealed record Snapshot(IReadOnlyDictionary<string, string?> Names, DateTimeOffset ReadAt);
}
