using Microsoft.EntityFrameworkCore;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Persistence;

namespace Kalla.MigrationService;

/// <summary>
/// Seeds the three assistant templates of the architecture plan's item 3 (admin design §4.5, P2) into an empty
/// <c>config."AssistantTemplates"</c>, in every environment: <i>Allmän rådgivning</i> (citizen service, its instructions the
/// text of <c>Settings.Chat.defaultSystemPrompt</c> at seeding), <i>Intern policy</i> and <i>Handläggningsstöd</i>. Only into
/// an empty table, so a template the platform team deleted or changed stays as they left it. Runs after the settings row is
/// seeded.
/// </summary>
public static class AssistantTemplateSeeder
{
    public const string Actor = "system:seed";

    public const string GeneralAdvice = "Allmän rådgivning";
    public const string InternalPolicy = "Intern policy";
    public const string CaseHandling = "Handläggningsstöd";

    /// <summary>Internal policy questions from staff (Swedish, in the style of <see cref="ChatDefaults.SystemPrompt"/>).</summary>
    public const string InternalPolicyPrompt = """
        Du svarar medarbetare i Umeå kommun som har frågor om kommunens interna policyer, riktlinjer och rutiner.
        Svara sakligt och kort, och börja med det viktigaste. Svara bara utifrån de dokument du har fått, och gissa aldrig.
        Ange vilket dokument svaret bygger på och vem som äger det, så att medarbetaren vet vart en fråga om regeln ska ställas.
        Om en regel är otydlig, motsägs av ett annat dokument eller inte täcker frågan, säg det rakt ut och hänvisa till dokumentets ägare.
        """;

    /// <summary>Case-handling support for officers (Swedish, in the style of <see cref="ChatDefaults.SystemPrompt"/>).</summary>
    public const string CaseHandlingPrompt = """
        Du stöder handläggare i Umeå kommun i deras arbete med ärenden.
        Ge vägledning steg för steg utifrån rutinerna och de dokument du har fått, och använd en numrerad lista när det finns flera steg.
        Säg vilken rutin varje steg kommer från. Om rutinerna inte täcker frågan, säg det och föreslå vem handläggaren kan fråga.
        Du fattar aldrig beslut och förutsäger aldrig hur ett ärende kommer att avgöras; det gör handläggaren.
        Be aldrig om personuppgifter, och skriv inga uppgifter om enskilda personer i svaret.
        """;

    /// <summary>What was seeded: the number of templates added (0 when the table already had rows).</summary>
    public static async Task<int> SeedAsync(RagDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async ct =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // One seeder at a time: two migration runs at once must not both find the table empty.
            await db.Database.ExecuteSqlRawAsync("""LOCK TABLE config."AssistantTemplates" IN SHARE ROW EXCLUSIVE MODE""", ct);
            if (await db.AssistantTemplates.AnyAsync(ct))
            {
                return 0;
            }

            var now = clock.GetUtcNow();
            var general = ChatDefaults.SystemPromptFrom(await db.Settings.AsNoTracking().Select(s => s.Chat).FirstOrDefaultAsync(ct));
            AssistantTemplate[] templates =
            [
                Template(GeneralAdvice, "Invånare, företagare och besökare med frågor om kommunens tjänster.", general, now),
                Template(InternalPolicy, "Medarbetare med frågor om interna policyer och riktlinjer. Svarar bara utifrån dokumenten och anger dokumentets ägare.",
                    InternalPolicyPrompt, now),
                Template(CaseHandling, "Handläggare som vill ha vägledning steg för steg ur rutinerna. Fattar aldrig beslut i ett ärende.",
                    CaseHandlingPrompt, now),
            ];
            db.AssistantTemplates.AddRange(templates);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return templates.Length;
        }, cancellationToken);
    }

    private static AssistantTemplate Template(string name, string description, string prompt, DateTimeOffset now) =>
        new() { Name = name, Description = description, SystemPrompt = prompt, UpdatedAt = now, UpdatedBy = Actor };
}
