using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Kalla.MigrationService;
using Kalla.TestKit;

namespace Kalla.IntegrationTests;

/// <summary>Issue #175: <c>Settings.Engine.PiiOwnDomains</c> seeded with <c>umea.se</c>, insert-if-absent.</summary>
public sealed class SettingsSeederTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_first_settings_row_gets_the_own_domains_and_an_existing_row_is_never_changed()
    {
        await using var database = new RagDatabase(); // its own: Settings is one row per database
        await database.InitializeAsync();
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Migrator));

        (await SettingsSeeder.SeedAsync(db, new SettingsSeedOptions(), TimeProvider.System, NullLogger.Instance, Ct)).ShouldBeTrue();
        var row = await db.Settings.AsNoTracking().SingleAsync(Ct);
        row.PiiOwnDomains.ShouldBe(["umea.se"]);
        row.UpdatedBy.ShouldBe(SettingsSeeder.Actor);
        // P1b item 39: a new assistant's first draft starts from these instructions.
        System.Text.Json.JsonDocument.Parse(row.Chat).RootElement.GetProperty(Kalla.Domain.ChatDefaults.SystemPromptKey).GetString()
            .ShouldBe(Kalla.Domain.ChatDefaults.SystemPrompt);

        // The companies' domains are added later through the admin; a second migration keeps them.
        await db.Settings.ExecuteUpdateAsync(s => s.SetProperty(x => x.PiiOwnDomains, ["umea.se", "umeaenergi.se"]), Ct);
        (await SettingsSeeder.SeedAsync(db, new SettingsSeedOptions { PiiOwnDomains = ["other.se"] }, TimeProvider.System, NullLogger.Instance, Ct)).ShouldBeFalse();
        (await db.Settings.AsNoTracking().SingleAsync(Ct)).PiiOwnDomains.ShouldBe(["umea.se", "umeaenergi.se"]);
    }

    [Fact]
    public async Task The_first_settings_row_gets_the_seeded_crawl_ports()
    {
        await using var database = new RagDatabase();
        await database.InitializeAsync();
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Migrator));

        (await SettingsSeeder.SeedAsync(db, new SettingsSeedOptions { CrawlAllowedPorts = [7416, 7416] }, TimeProvider.System, NullLogger.Instance, Ct)).ShouldBeTrue();
        var crawl = System.Text.Json.JsonDocument.Parse((await db.Settings.AsNoTracking().SingleAsync(Ct)).Crawl).RootElement;
        crawl.GetProperty("allowedPorts").EnumerateArray().Select(p => p.GetInt32()).ShouldBe([7416]);
    }
}
