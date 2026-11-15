using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Services;
using Ume.Rag.MigrationService;
using Ume.Rag.TestKit;
using static Ume.Rag.TestKit.RagDatabase;

namespace Ume.Rag.IntegrationTests;

/// <summary>
/// The deployment's own Open WebUI client (identity design §5.2; M-17 C4): rag-migrate makes it from the key the deployment
/// generated, keeps exactly that key active on it, writes nothing when nothing changed, and takes over an Open WebUI client
/// that already holds the key; under another name it replaces the managed one. Its own database: the managed rows are unique per
/// kind and name.
/// </summary>
public sealed class OpenWebUiClientSeederTests(RagDatabaseFixture database) : IClassFixture<RagDatabaseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly KeyHasher Hasher = new(System.Text.Encoding.UTF8.GetBytes(new string('p', 32)));

    private static string Key(char c) => KeyHasher.SecretPrefix + new string(c, 43);

    private Task<long> VersionAsync() => Sql.ScalarAsync<long>(database.Connection(null), """SELECT "Version" FROM config."ConfigVersion" WHERE "Id" = 1""");

    [Fact]
    public async Task Rag_migrate_makes_the_client_keeps_its_one_key_and_replaces_it_with_a_new_one()
    {
        await using var db = TestPostgres.Context(database.Connection(Migrator));
        (await OpenWebUiClientSeeder.SeedAsync(db, Hasher, Key('A'), "https://chat.example.invalid", TimeProvider.System, Ct))
            .ShouldBe(OpenWebUiClientSeeder.Outcome.Created);

        var client = await db.Clients.AsNoTracking().Include(c => c.Keys).SingleAsync(c => c.IsManagedByDeployment, Ct);
        (client.Name, client.Kind, client.ClassificationCeiling, client.StoresHistory, client.ClientRetentionDays, client.PortalUrl).ShouldBe(
            (OpenWebUiClientSeeder.DefaultName, ClientKinds.OpenWebUi, Classification.Internal, true, Client.OpenWebUiFeedbackRetentionDays,
                "https://chat.example.invalid"));
        var first = client.Keys.ShouldHaveSingleItem();
        Hasher.Verify(Key('A'), first.KeyHash).ShouldBeTrue();
        first.Prefix.ShouldBe("ume-rag-sk-AAAA");

        // Every deploy runs it again: nothing changed, nothing written, the config version stays.
        var version = await VersionAsync();
        (await OpenWebUiClientSeeder.SeedAsync(db, Hasher, Key('A'), null, TimeProvider.System, Ct)).ShouldBe(OpenWebUiClientSeeder.Outcome.Unchanged);
        (await VersionAsync()).ShouldBe(version);

        // A new key from the deployment is the rotation: it is added, the old one revoked, in one config write.
        (await OpenWebUiClientSeeder.SeedAsync(db, Hasher, Key('B'), null, TimeProvider.System, Ct)).ShouldBe(OpenWebUiClientSeeder.Outcome.KeyReplaced);
        (await VersionAsync()).ShouldBe(version + 1);
        var keys = await db.Keys.AsNoTracking().Where(k => k.ClientId == client.Id).ToListAsync(Ct);
        keys.Count.ShouldBe(2);
        keys.Single(k => k.Id == first.Id).RevokedAt.ShouldNotBeNull();
        Hasher.Verify(Key('B'), keys.Single(k => k.Id != first.Id).KeyHash).ShouldBeTrue();
        keys.Single(k => k.Id != first.Id).RevokedAt.ShouldBeNull();

        // Going back to the first key revives it rather than adding a third.
        (await OpenWebUiClientSeeder.SeedAsync(db, Hasher, Key('A'), null, TimeProvider.System, Ct)).ShouldBe(OpenWebUiClientSeeder.Outcome.KeyReplaced);
        var after = await db.Keys.AsNoTracking().Where(k => k.ClientId == client.Id).ToListAsync(Ct);
        after.Count.ShouldBe(2);
        after.Single(k => k.Id == first.Id).RevokedAt.ShouldBeNull();
        after.Single(k => k.Id != first.Id).RevokedAt.ShouldNotBeNull();

        // A key that is not an ume-rag-sk- key is refused before anything is written; so is another client's key.
        await Should.ThrowAsync<InvalidOperationException>(() => OpenWebUiClientSeeder.SeedAsync(db, Hasher, "sk-not-ours", null, TimeProvider.System, Ct));
        var other = new Client { Name = "Integration " + Guid.NewGuid().ToString("N")[..6], Kind = ClientKinds.Integration, ClassificationCeiling = Classification.Public };
        db.Clients.Add(other);
        db.Keys.Add(new ApiKey { ClientId = other.Id, Name = "k", Prefix = Key('C')[..15], KeyHash = Hasher.Hash(Key('C')), CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(Ct);
        await Should.ThrowAsync<InvalidOperationException>(() => OpenWebUiClientSeeder.SeedAsync(db, Hasher, Key('C'), null, TimeProvider.System, Ct));

        // The deployment has one Open WebUI client, found by kind and name (#712): seeding under another name (the development
        // setup) makes a new managed one, and the old one stops being managed, its keys revoked.
        db.ChangeTracker.Clear();
        (await OpenWebUiClientSeeder.SeedAsync(db, Hasher, Key('D'), null, TimeProvider.System, Ct, name: DevSeeder.DevClientName))
            .ShouldBe(OpenWebUiClientSeeder.Outcome.Created);
        var old = await db.Clients.AsNoTracking().Include(c => c.Keys).SingleAsync(c => c.Id == client.Id, Ct);
        old.IsManagedByDeployment.ShouldBeFalse();
        old.Keys.ShouldAllBe(k => k.RevokedAt != null);
        (await db.Clients.AsNoTracking().SingleAsync(c => c.Kind == ClientKinds.OpenWebUi && c.IsManagedByDeployment, Ct)).Name.ShouldBe(DevSeeder.DevClientName);
        (await OpenWebUiClientSeeder.SeedAsync(db, Hasher, Key('D'), null, TimeProvider.System, Ct, name: DevSeeder.DevClientName))
            .ShouldBe(OpenWebUiClientSeeder.Outcome.Unchanged);

        // At most one managed client per kind and name, in the database too.
        db.ChangeTracker.Clear();
        db.Clients.Add(new Client { Name = DevSeeder.DevClientName, Kind = ClientKinds.OpenWebUi, ClassificationCeiling = Classification.Internal, IsManagedByDeployment = true });
        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }
}
