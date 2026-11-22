using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Kalla.Domain;
using Kalla.MigrationService;
using Kalla.TestKit;

namespace Kalla.IntegrationTests;

/// <summary>
/// The three assistant templates (admin design §4.5, B-54) are seeded into an empty table only: a second start adds none
/// and restores none, and the first template takes the settings row's default instructions.
/// </summary>
public sealed class AssistantTemplateSeederTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_three_templates_are_seeded_once_and_a_second_start_neither_adds_nor_restores_one()
    {
        await using var database = new RagDatabase(); // its own: the seeder looks at the whole table
        await database.InitializeAsync();
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Migrator));
        (await SettingsSeeder.SeedAsync(db, new SettingsSeedOptions(), TimeProvider.System, NullLogger.Instance, Ct)).ShouldBeTrue();
        // An admin's own default instructions are what the first template takes.
        await db.Database.ExecuteSqlRawAsync(
            """UPDATE config."Settings" SET "Chat" = jsonb_set("Chat", '{{defaultSystemPrompt}}', to_jsonb('Svara kort.'::text))""", Ct);

        (await AssistantTemplateSeeder.SeedAsync(db, TimeProvider.System, Ct)).ShouldBe(3);
        var templates = await db.AssistantTemplates.AsNoTracking().OrderBy(t => t.Name).ToListAsync(Ct);
        templates.Select(t => t.Name).ShouldBe([AssistantTemplateSeeder.GeneralAdvice, AssistantTemplateSeeder.CaseHandling, AssistantTemplateSeeder.InternalPolicy]);
        templates.ShouldAllBe(t => t.IsActive && t.UpdatedBy == AssistantTemplateSeeder.Actor && t.NotFound == "{}" && t.QuerySteps == "{}" && t.Description.Length > 0);
        templates.Single(t => t.Name == AssistantTemplateSeeder.GeneralAdvice).SystemPrompt.ShouldBe("Svara kort.");
        templates.Single(t => t.Name == AssistantTemplateSeeder.InternalPolicy).SystemPrompt.ShouldBe(AssistantTemplateSeeder.InternalPolicyPrompt);
        templates.Single(t => t.Name == AssistantTemplateSeeder.CaseHandling).SystemPrompt.ShouldBe(AssistantTemplateSeeder.CaseHandlingPrompt);

        // The platform team removes one and edits another; the next deploy changes neither.
        await db.AssistantTemplates.Where(t => t.Name == AssistantTemplateSeeder.CaseHandling).ExecuteDeleteAsync(Ct);
        await db.AssistantTemplates.Where(t => t.Name == AssistantTemplateSeeder.InternalPolicy).ExecuteUpdateAsync(s => s.SetProperty(t => t.SystemPrompt, "Egen text."), Ct);
        (await AssistantTemplateSeeder.SeedAsync(db, TimeProvider.System, Ct)).ShouldBe(0);
        (await db.AssistantTemplates.AsNoTracking().OrderBy(t => t.Name).Select(t => t.Name).ToListAsync(Ct))
            .ShouldBe([AssistantTemplateSeeder.GeneralAdvice, AssistantTemplateSeeder.InternalPolicy]);
        (await db.AssistantTemplates.AsNoTracking().SingleAsync(t => t.Name == AssistantTemplateSeeder.InternalPolicy, Ct)).SystemPrompt.ShouldBe("Egen text.");
    }

    [Fact]
    public async Task Without_a_settings_row_the_first_template_takes_the_codes_instructions()
    {
        await using var database = new RagDatabase();
        await database.InitializeAsync();
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Migrator));

        (await AssistantTemplateSeeder.SeedAsync(db, TimeProvider.System, Ct)).ShouldBe(3);
        (await db.AssistantTemplates.AsNoTracking().SingleAsync(t => t.Name == AssistantTemplateSeeder.GeneralAdvice, Ct)).SystemPrompt
            .ShouldBe(ChatDefaults.SystemPrompt);
    }
}
