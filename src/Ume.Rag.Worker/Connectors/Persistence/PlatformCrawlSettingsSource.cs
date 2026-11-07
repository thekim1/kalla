using Microsoft.Extensions.Options;
using Npgsql;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Worker.Connectors.Egress;
using Ume.Rag.Worker.Connectors.Hosts;

namespace Ume.Rag.Worker.Connectors.Persistence;

/// <summary>
/// The egress and crawl policies from <c>config."Settings"."Crawl"</c> (<see cref="PlatformCrawlSettings"/>), read at start
/// and every <see cref="RefreshInterval"/>. A failed read keeps the last good policy (at start: the defaults, every host
/// external at 1 req/s, no extra ports), so a database hiccup never widens the egress rules. Host politeness profiles are
/// clamped to the deployment's ceilings (<c>Crawl:MaxHostRate</c>, <c>Crawl:MaxHostConnections</c>).
/// </summary>
public sealed partial class PlatformCrawlSettingsSource(JobDatabase database, IOptions<CrawlOptions> options, TimeProvider time, ILogger<PlatformCrawlSettingsSource> logger)
    : BackgroundService, IEgressPolicySource, ICrawlPolicySource
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(60);

    private const string ReadSql = """SELECT "Crawl"::text FROM config."Settings" ORDER BY "Id" LIMIT 1""";

    private volatile Policies _current = new(EgressPolicy.Default, CrawlPolicy.Default with { Ceilings = options.Value.Ceilings() });

    EgressPolicy IEgressPolicySource.Current => _current.Egress;

    CrawlPolicy ICrawlPolicySource.Current => _current.Crawl;

    /// <summary>Reads the settings row now; false (and the last policy kept) when it cannot be read or parsed.</summary>
    public async Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(ReadSql, connection);
            var json = await command.ExecuteScalarAsync(cancellationToken) as string;
            var (egress, crawl) = PlatformCrawlSettings.Parse(json, options.Value.Ceilings());
            _current = new Policies(egress, crawl);
            return true;
        }
        catch (Exception e) when (e is NpgsqlException or System.Text.Json.JsonException or TimeoutException)
        {
            LogReadFailed(logger, e.GetType().Name);
            return false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshAsync(stoppingToken);
        using var timer = new PeriodicTimer(RefreshInterval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshAsync(stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settings.Crawl could not be read ({ExceptionType}); the last crawl policy stays in force.")]
    private static partial void LogReadFailed(ILogger logger, string exceptionType);

    private sealed record Policies(EgressPolicy Egress, CrawlPolicy Crawl);
}
