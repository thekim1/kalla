using Kalla.Domain;
using Kalla.TestKit;
using Kalla.Worker.Processing.Persistence;

namespace Kalla.Worker.Tests.Processing;

/// <summary>
/// <see cref="PostgresEmbeddingProfileStore"/> reads <c>"RequestDimensions"</c> (U-b, #142) and the last canary check
/// (#353) as <c>kalla_worker</c>.
/// </summary>
[Collection(RagDatabaseGroup4.Name)]
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

    [Fact]
    public async Task The_store_reads_the_last_canary_check_on_every_call()
    {
        var id = Guid.CreateVersion7();
        await Sql.ExecuteAsync(database.Connection(null), """
            INSERT INTO config."EmbeddingProfiles" ("Id", "Name", "GatewayModel", "Dimensions", "Distance", "MaxInputTokens", "IsDefault", "State", "MaxResidency", "UpdatedAt")
            VALUES ($1, $2, 'bge-m3', 1024, 'cosine', 8192, false, 'retired', 'Eu', now())
            """, id, "canary-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new PostgresEmbeddingProfileStore(database.Database(RagDatabase.Worker));
            var never = (await store.GetAsync(id, Ct)).ShouldNotBeNull();
            never.CanaryCheckedAt.ShouldBeNull();
            never.CanarySimilarity.ShouldBeNull();

            var checkedAt = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
            await Sql.ExecuteAsync(database.Connection(null), """
                UPDATE config."EmbeddingProfiles" SET "CanaryCheckedAt" = $2, "CanarySimilarity" = $3 WHERE "Id" = $1
                """, id, checkedAt, 0.5f);

            var failed = (await store.GetAsync(id, Ct)).ShouldNotBeNull();
            failed.CanaryCheckedAt.ShouldBe(checkedAt);
            failed.CanarySimilarity.ShouldBe(0.5);
            Worker.Processing.Embedding.EmbeddingCanary.Failed(failed).ShouldBeTrue();
        }
        finally
        {
            await Sql.ExecuteAsync(database.Connection(null), """DELETE FROM config."EmbeddingProfiles" WHERE "Id" = $1""", id);
        }
    }
}
