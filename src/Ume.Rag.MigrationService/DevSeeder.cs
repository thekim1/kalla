using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Persistence;

namespace Ume.Rag.MigrationService;

/// <summary>
/// The development seeder (data-model §10), run after the migrations when <c>Seed:Enabled</c> is true (the AppHost sets
/// it in run mode only). Insert-if-absent, so it is safe on every start. Seeds the development embedding profile with the
/// fixed id of data-model §3.2: 1024 dimensions (FakeGateway's <c>bge-m3</c>), ceiling <c>Eu</c> as the pilot's EU-hosted
/// profile (P1 README §1, §8 O1), its canary text and its partial HNSW index. The pilot profile comes from
/// <see cref="EmbeddingProfileSeeder"/>; the admin API records each profile's canary embedding from the gateway.
/// </summary>
public static class DevSeeder
{
    public static readonly Guid EmbeddingProfileId = Guid.Parse("01929b6e-0000-7000-8000-00000000b6e3");
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
                UpdatedBy = EmbeddingProfileSeeder.Actor,
            };
            db.EmbeddingProfiles.Add(profile);
            await db.SaveChangesAsync(cancellationToken);
        }

        // A profile seeded before item 13 gets its canary text now; the admin API records the canary embedding.
        await EmbeddingProfileSeeder.EnsureCanaryTextAndIndexAsync(db, profile, clock, cancellationToken);
    }
}
