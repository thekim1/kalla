using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Persistence;

namespace Ume.Rag.MigrationService;

/// <summary>
/// The platform's embedding profile as deployment knobs (<c>EmbeddingProfile:*</c>; P1 README §1: the pilot's EU-hosted
/// model, user item U-b). Without <see cref="GatewayModel"/> nothing is seeded (development uses <see cref="DevSeeder"/>).
/// </summary>
public sealed class EmbeddingProfileSeedOptions
{
    public const string SectionName = "EmbeddingProfile";

    public string Name { get; set; } = "pilot-embedding";

    /// <summary>The gateway model or route alias of the EU embedding route.</summary>
    public string? GatewayModel { get; set; }

    public int Dimensions { get; set; } = 1024;

    /// <summary>
    /// Send <c>dimensions</c> on every embedding call: on by default for the pilot's text-embedding-3-large at 1024 (Decisions
    /// "Pilot embedding model, U-b", #142); a BGE-M3 profile sets it false.
    /// </summary>
    public bool RequestDimensions { get; set; } = true;

    /// <summary>text-embedding-3-large's input limit (Decisions "Pilot embedding model, U-b").</summary>
    public int MaxInputTokens { get; set; } = 8191;

    /// <summary>The ceiling of embed and query-embed calls (P1 README §8 O1): <c>Eu</c> for the pilot.</summary>
    public Residency MaxResidency { get; set; } = Residency.Eu;
}

/// <summary>
/// Seeds the platform profile from <see cref="EmbeddingProfileSeedOptions"/> (P1a item 13), insert-if-absent by name, as the
/// default when there is none, with the canary text and its partial HNSW index. An existing profile is never changed here:
/// a platform admin edits it in the admin UI (Platform, Gateway models) while no chunk set uses it, and a difference from the
/// knobs is logged (Decisions "Editable embedding profiles before use"). The canary embedding itself
/// is recorded from the gateway by the admin API, which holds the gateway keys.
/// </summary>
public static partial class EmbeddingProfileSeeder
{
    public const string Actor = "system:seed";

    public static async Task SeedAsync(RagDbContext db, EmbeddingProfileSeedOptions options, TimeProvider clock, ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        if (string.IsNullOrWhiteSpace(options.GatewayModel))
        {
            return;
        }

        if (options.Dimensions is < 1 or > 4000 || options.MaxInputTokens < 1 || string.IsNullOrWhiteSpace(options.Name))
        {
            throw new InvalidOperationException("EmbeddingProfile: Name is required, Dimensions 1-4000 and MaxInputTokens > 0.");
        }

        var profile = await db.EmbeddingProfiles.SingleOrDefaultAsync(p => p.Name == options.Name, cancellationToken);
        if (profile is null)
        {
            profile = new EmbeddingProfile
            {
                Name = options.Name,
                GatewayModel = options.GatewayModel,
                Dimensions = options.Dimensions,
                RequestDimensions = options.RequestDimensions,
                MaxInputTokens = options.MaxInputTokens,
                MaxResidency = options.MaxResidency,
                CanaryText = EmbeddingCanaryText.Default,
                IsDefault = !await db.EmbeddingProfiles.AnyAsync(p => p.IsDefault, cancellationToken),
                UpdatedAt = clock.GetUtcNow(),
                UpdatedBy = Actor,
            };
            db.EmbeddingProfiles.Add(profile);
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (profile.GatewayModel != options.GatewayModel || profile.Dimensions != options.Dimensions || profile.MaxResidency != options.MaxResidency ||
            profile.RequestDimensions != options.RequestDimensions || profile.MaxInputTokens != options.MaxInputTokens)
        {
            LogProfileDiffers(logger, profile.Name);
        }

        await EnsureCanaryTextAndIndexAsync(db, profile, clock, cancellationToken);
    }

    /// <summary>Sets the canary text when the profile has none, and creates the profile's partial HNSW index once.</summary>
    internal static async Task EnsureCanaryTextAndIndexAsync(RagDbContext db, EmbeddingProfile profile, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (profile.CanaryText is null)
        {
            profile.CanaryText = EmbeddingCanaryText.Default;
            profile.UpdatedAt = clock.GetUtcNow();
            profile.UpdatedBy = Actor;
            await db.SaveChangesAsync(cancellationToken);
        }

        await db.Database.ExecuteSqlAsync($"SELECT content.rag_ensure_profile_index({profile.Id}, {profile.Dimensions})", cancellationToken);
    }

    [LoggerMessage(LogLevel.Warning,
        "Embedding profile {Name} exists with other values than EmbeddingProfile:*; it is left as it is. Change it in the admin UI under Platform, Gateway models (editable while no content uses it)")]
    private static partial void LogProfileDiffers(ILogger logger, string name);
}
