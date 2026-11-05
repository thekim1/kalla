using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Persistence;

namespace Ume.Rag.MigrationService;

/// <summary>
/// Deployment knobs for the first <c>Settings</c> row (<c>SettingsSeed:*</c>). <see cref="PiiOwnDomains"/> defaults to
/// <c>umea.se</c> (issue #175): the municipality's own addresses do not count as external e-mail in the personal-data
/// stage. Bare domains; subdomains and case are matched by the worker.
/// </summary>
public sealed class SettingsSeedOptions
{
    public const string SectionName = "SettingsSeed";

    public static readonly IReadOnlyList<string> DefaultOwnDomains = ["umea.se"];

    /// <summary>Unset: <see cref="DefaultOwnDomains"/>.</summary>
    public string[]? PiiOwnDomains { get; set; }

    /// <summary>
    /// <c>Settings.Crawl.allowedPorts</c> of the first row (connectors §7). Unset: none beyond 80 and 443. The browser tests'
    /// stack sets the fixture website's port here, so the worker has it from its first read.
    /// </summary>
    public int[]? CrawlAllowedPorts { get; set; }
}

/// <summary>
/// Seeds <c>Settings.Engine.PiiOwnDomains</c> insert-if-absent (issue #175): when no <c>Settings</c> row exists, one is
/// created with the code defaults and the seeded own domains. An existing row is never changed, so the municipal
/// companies' domains added later through <c>PUT /api/settings/…</c> are kept.
/// </summary>
public static partial class SettingsSeeder
{
    public const string Actor = "system:seed";

    public static async Task<bool> SeedAsync(RagDbContext db, SettingsSeedOptions options, TimeProvider clock, ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        if (await db.Settings.AnyAsync(cancellationToken))
        {
            LogKept(logger);
            return false;
        }

        var domains = options.PiiOwnDomains is { Length: > 0 } configured ? configured : [.. SettingsSeedOptions.DefaultOwnDomains];
        db.Settings.Add(new PlatformSettings
        {
            PiiOwnDomains = [.. domains.Select(d => d.Trim()).Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)],
            Crawl = options.CrawlAllowedPorts is { Length: > 0 } ports
                ? System.Text.Json.JsonSerializer.Serialize(new { allowedPorts = ports.Distinct().Order().ToArray() })
                : "{}",
            UpdatedAt = clock.GetUtcNow(),
            UpdatedBy = Actor,
        });
        await db.SaveChangesAsync(cancellationToken);
        LogSeeded(logger, string.Join(", ", domains));
        return true;
    }

    [LoggerMessage(LogLevel.Information, "Settings seeded with PiiOwnDomains {Domains}.")]
    private static partial void LogSeeded(ILogger logger, string domains);

    [LoggerMessage(LogLevel.Information, "Settings row exists; PiiOwnDomains left as saved.")]
    private static partial void LogKept(ILogger logger);
}
