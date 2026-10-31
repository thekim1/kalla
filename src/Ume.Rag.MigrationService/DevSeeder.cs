using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Persistence;

namespace Ume.Rag.MigrationService;

/// <summary>
/// The development seeder (data-model §10), run after the migrations when <c>Seed:Enabled</c> is true (the AppHost sets
/// it in run mode only). Insert-if-absent, so it is safe on every start. Seeds the development embedding profile with the
/// fixed id of data-model §3.2: 1024 dimensions (FakeGateway's <c>bge-m3</c>), ceiling <c>Eu</c> as the pilot's EU-hosted
/// profile (P1 README §1, §8 O1), and its partial HNSW index. The pilot profile and its canary embedding are seeded from
/// the gateway by P1 item 13.
/// </summary>
public static class DevSeeder
{
    public static readonly Guid EmbeddingProfileId = Guid.Parse("01929b6e-0000-7000-8000-00000000b6e3");
    public const int EmbeddingDimensions = 1024;

    public static async Task SeedAsync(RagDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        if (!await db.EmbeddingProfiles.AnyAsync(p => p.Id == EmbeddingProfileId, cancellationToken))
        {
            db.EmbeddingProfiles.Add(new EmbeddingProfile
            {
                Id = EmbeddingProfileId,
                Name = "dev-embedding",
                GatewayModel = "bge-m3",
                Dimensions = EmbeddingDimensions,
                MaxInputTokens = 8192,
                MaxResidency = Residency.Eu,
                IsDefault = !await db.EmbeddingProfiles.AnyAsync(p => p.IsDefault, cancellationToken),
                UpdatedAt = clock.GetUtcNow(),
                UpdatedBy = "system:seed",
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        await db.Database.ExecuteSqlAsync(
            $"SELECT content.rag_ensure_profile_index({EmbeddingProfileId}, {EmbeddingDimensions})", cancellationToken);
    }
}
