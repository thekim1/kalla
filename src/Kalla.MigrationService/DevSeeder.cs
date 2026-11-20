using Microsoft.EntityFrameworkCore;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.Domain.Services;
using Kalla.Infrastructure.Persistence;

namespace Kalla.MigrationService;

/// <summary>
/// The development seeder (data-model §10), run after the migrations when <c>Seed:Enabled</c> is true (the AppHost sets
/// it in run mode only). Insert-if-absent, so it is safe on every start. Seeds the development embedding profile with the
/// fixed id of data-model §3.2: 1024 dimensions (FakeGateway's <c>bge-m3</c>), ceiling <c>Eu</c> as the pilot's EU-hosted
/// profile (P1 README §1, §8 O1), its canary text and its partial HNSW index. A deployment's profile is created by a platform
/// admin in the admin UI (Decisions "Deploy configuration"); the admin API records each profile's canary embedding.
/// </summary>
public static class DevSeeder
{
    public static readonly Guid EmbeddingProfileId = Guid.Parse("01929b6e-0000-7000-8000-00000000b6e3");

    /// <summary>The development Open WebUI client of repo-and-deployment §4 "Seed" (ceiling <c>internal</c>).</summary>
    public static readonly Guid DevClientId = Guid.Parse("01929b6e-0000-7000-8000-0000000c11e7");
    public const string DevClientName = "Open WebUI (dev)";
    public const int EmbeddingDimensions = 1024;

    public static async Task SeedAsync(RagDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        var profile = await db.EmbeddingProfiles.SingleOrDefaultAsync(p => p.Id == EmbeddingProfileId, cancellationToken);
        if (profile is null)
        {
            profile = new EmbeddingProfile
            {
                Id = EmbeddingProfileId,
                Name = "dev-embedding",
                GatewayModel = "bge-m3",
                Dimensions = EmbeddingDimensions,
                MaxInputTokens = 8192,
                MaxResidency = Residency.Eu,
                CanaryText = EmbeddingCanaryText.Default,
                IsDefault = !await db.EmbeddingProfiles.AnyAsync(p => p.IsDefault, cancellationToken),
                UpdatedAt = clock.GetUtcNow(),
                UpdatedBy = SettingsSeeder.Actor,
            };
            db.EmbeddingProfiles.Add(profile);
            await db.SaveChangesAsync(cancellationToken);
        }

        // A profile seeded before item 13 gets its canary text now; the admin API records the canary embedding.
        await EnsureCanaryTextAndIndexAsync(db, profile, clock, cancellationToken);
    }

    /// <summary>Sets the canary text when the profile has none, and creates the profile's partial HNSW index once.</summary>
    private static async Task EnsureCanaryTextAndIndexAsync(RagDbContext db, EmbeddingProfile profile, TimeProvider clock, CancellationToken cancellationToken)
    {
        if (profile.CanaryText is null)
        {
            profile.CanaryText = EmbeddingCanaryText.Default;
            profile.UpdatedAt = clock.GetUtcNow();
            profile.UpdatedBy = SettingsSeeder.Actor;
            await db.SaveChangesAsync(cancellationToken);
        }

        await db.Database.ExecuteSqlAsync($"SELECT content.rag_ensure_profile_index({profile.Id}, {profile.Dimensions})", cancellationToken);
    }

    /// <summary>
    /// The client <see cref="DevClientName"/> (<c>open-webui</c>, ceiling <c>internal</c>, no history) with the key
    /// <c>ume-rag-sk-</c> + <paramref name="devKey"/> (the AppHost's <c>dev-key</c> parameter, 43 base62 characters), hashed
    /// with the deployment's pepper as every key (identity §6.1): the development deployment's own Open WebUI client, made and
    /// kept by <see cref="OpenWebUiClientSeeder"/> as a deployment's is (identity §5.2). <paramref name="portalUrl"/>
    /// (<c>Seed:DevClientPortalUrl</c>, the AppHost's Open WebUI) is the client's <c>PortalUrl</c>, the assistant page's "Open
    /// in" link (admin design §5.4 step 13), set when the client is made.
    /// </summary>
    public static Task SeedDevClientAsync(
        RagDbContext db, KeyHasher hasher, string devKey, string? portalUrl, TimeProvider clock, CancellationToken cancellationToken) =>
        OpenWebUiClientSeeder.SeedAsync(db, hasher, KeyHasher.SecretPrefix + devKey, portalUrl, clock, cancellationToken, DevClientId, DevClientName,
            storesHistory: false);
}
