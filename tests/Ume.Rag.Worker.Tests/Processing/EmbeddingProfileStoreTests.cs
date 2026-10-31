using Ume.Rag.Domain;
using Ume.Rag.TestKit;
using Ume.Rag.Worker.Processing.Persistence;

namespace Ume.Rag.Worker.Tests.Processing;

/// <summary><see cref="PostgresEmbeddingProfileStore"/> reads <c>"RequestDimensions"</c> as <c>ume_rag_worker</c> (U-b, #142).</summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class EmbeddingProfileStoreTests(RagDatabaseFixture database) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await database.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_store_reads_RequestDimensions_and_its_default_is_false()
    {
        var requesting = Guid.CreateVersion7();
        var plain = Guid.CreateVersion7();
        var suffix = Guid.NewGuid().ToString("N");
        // Retired, so no other test's ActiveAsync sees them; the second row takes the column default.
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO config."EmbeddingProfiles" ("Id", "Name", "GatewayModel", "Dimensions", "Distance", "MaxInputTokens", "IsDefault", "State", "MaxResidency", "UpdatedAt", "RequestDimensions")
            VALUES ($1, $2, 'text-embedding-3-large', 256, 'cosine', 8191, false, 'retired', 'Eu', now(), true)
            """, requesting, "dims-" + suffix);
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO config."EmbeddingProfiles" ("Id", "Name", "GatewayModel", "Dimensions", "Distance", "MaxInputTokens", "IsDefault", "State", "MaxResidency", "UpdatedAt")
            VALUES ($1, $2, 'bge-m3', 1024, 'cosine', 8192, false, 'retired', 'Eu', now())
            """, plain, "plain-" + suffix);
        try
        {
            var store = new PostgresEmbeddingProfileStore(database.Database(RagDatabase.Worker));

            var large = (await store.GetAsync(requesting, Ct)).ShouldNotBeNull();
            large.RequestDimensions.ShouldBeTrue();
            large.DimensionsParameter.ShouldBe(256);
            large.MaxResidency.ShouldBe(Residency.Eu);
            var bge = (await store.GetAsync(plain, Ct)).ShouldNotBeNull();
            bge.RequestDimensions.ShouldBeFalse();
            bge.DimensionsParameter.ShouldBeNull();
        }
        finally
        {
            await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM config."EmbeddingProfiles" WHERE "Id" IN ($1, $2)""", requesting, plain);
        }
    }
}
