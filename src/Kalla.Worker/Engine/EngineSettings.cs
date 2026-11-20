using System.Text.Json;
using Npgsql;
using Kalla.Infrastructure.Jobs;
using Kalla.Pipelines.Json;
using Kalla.Pipelines.Processing;
using Kalla.Worker.Connectors.Deletion;
using Kalla.Worker.Processing.Pii;

namespace Kalla.Worker.Engine;

/// <summary>
/// The run-time values of <c>config."Settings"</c> the engine reads (repo-and-deployment §3 settings rule; data-model §2.1):
/// the degraded ratio, schedule jitter and defaults, the blob purge delay, the personal-data rules and the deletion guard.
/// </summary>
public sealed record EngineSettingsValues
{
    public static EngineSettingsValues Default { get; } = new();

    public decimal DegradedFailureRatio { get; init; } = 0.05m;

    public int ScheduleJitterMinutes { get; init; } = 90;

    /// <summary><c>Settings.DefaultSchedules</c> by source type; the design's defaults when the group names none (engine design §9.6).</summary>
    public IReadOnlyDictionary<string, string> DefaultSchedules { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["url-list"] = "0 2 * * *",
        ["crawl"] = "0 1 * * 6",
    };

    public int BlobPurgeDelayDays { get; init; } = 35;

    public PiiFlagRules PiiFlagRules { get; init; } = PiiFlagRules.Default;

    public IReadOnlyList<string> PiiOwnDomains { get; init; } = [];

    public double NerMinScore { get; init; } = 0.80;

    public GuardSettings Guard { get; init; } = GuardSettings.Default;

    /// <summary>The personal-data stage's context for a collection of <paramref name="collectionClassification"/>.</summary>
    public PersonalDataContext PersonalData(string collectionClassification) =>
        new(collectionClassification) { OwnDomains = PiiOwnDomains, Rules = PiiFlagRules, NerMinScore = NerMinScore };
}

/// <summary>
/// Reads <see cref="EngineSettingsValues"/> from the singleton settings row, at most once per <see cref="MaxAge"/>; a failed
/// read keeps the last values (at start: the defaults).
/// </summary>
public sealed partial class EngineSettings(JobDatabase database, TimeProvider time, ILogger<EngineSettings> logger)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);

    private const string ReadSql = """
        SELECT "DegradedFailureRatio", "ScheduleJitterMinutes", "DefaultSchedules"::text, "BlobPurgeDelayDays", "PiiFlagRules"::text,
               "PiiOwnDomains", "NerMinScore", "DeletionGuardPercent", "DeletionGuardMinCount", "DeletionGuardMaxCount"
        FROM config."Settings" ORDER BY "Id" LIMIT 1
        """;

    private volatile Snapshot? _snapshot;

    /// <summary>The current values; two callers that find them stale at once may both read the row, which is harmless.</summary>
    public async Task<EngineSettingsValues> GetAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (_snapshot is { } current && now - current.ReadAt < MaxAge)
        {
            return current.Values;
        }

        var values = await ReadAsync(cancellationToken) ?? _snapshot?.Values ?? EngineSettingsValues.Default;
        _snapshot = new Snapshot(values, now);
        return values;
    }

    private async Task<EngineSettingsValues?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(ReadSql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return EngineSettingsValues.Default;
            }

            var defaults = EngineSettingsValues.Default;
            var schedules = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(2)) ?? [];
            using var rules = JsonDocument.Parse(reader.GetString(4));
            return new EngineSettingsValues
            {
                DegradedFailureRatio = reader.GetDecimal(0),
                ScheduleJitterMinutes = reader.GetInt32(1),
                DefaultSchedules = schedules.Count > 0 ? schedules : defaults.DefaultSchedules,
                BlobPurgeDelayDays = reader.GetInt32(3),
                PiiFlagRules = rules.RootElement.EnumerateObject().Any() ? ContractJson.Deserialize<PiiFlagRules>(rules.RootElement) : PiiFlagRules.Default,
                PiiOwnDomains = reader.GetFieldValue<string[]>(5),
                NerMinScore = (double)reader.GetDecimal(6),
                Guard = new GuardSettings(reader.GetInt16(7), reader.GetInt32(8), reader.GetInt32(9)),
            };
        }
        catch (Exception e) when (e is NpgsqlException or JsonException or TimeoutException)
        {
            LogReadFailed(logger, e.GetType().Name);
            return null;
        }
    }

    [LoggerMessage(LogLevel.Warning, "Engine settings could not be read ({ExceptionType}); the last values stay in force.")]
    private static partial void LogReadFailed(ILogger logger, string exceptionType);

    private sealed record Snapshot(EngineSettingsValues Values, DateTimeOffset ReadAt);
}
