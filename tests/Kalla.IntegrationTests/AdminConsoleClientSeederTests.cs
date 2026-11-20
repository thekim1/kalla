using Microsoft.EntityFrameworkCore;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.MigrationService;
using Kalla.TestKit;
using static Kalla.TestKit.RagDatabase;

namespace Kalla.IntegrationTests;

/// <summary>
/// The admin test panel's client (identity design §4.1; #712): rag-migrate makes <c>admin-console</c> in every deployment,
/// managed by the deployment and without keys, writes nothing when nothing changed, finds it by kind and name (another managed
/// internal client, as P2's <c>rag-internal</c>, stands beside it), and takes over an <c>admin-console</c> made by hand, revoking its
/// keys. Its own database: the managed rows are unique per kind and name.
/// </summary>
public sealed class AdminConsoleClientSeederTests(RagDatabaseFixture database) : IClassFixture<RagDatabaseFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<long> VersionAsync() => Sql.ScalarAsync<long>(database.Connection(null), """SELECT "Version" FROM config."ConfigVersion" WHERE "Id" = 1""");

    private static ApiKey Key(Guid clientId, string name) => new()
    {
        ClientId = clientId, Name = name, Prefix = "ume-rag-sk-" + name[..4].ToUpperInvariant(), KeyHash = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Rag_migrate_makes_admin_console_finds_it_by_kind_and_name_and_takes_over_a_hand_made_one_without_its_keys()
    {
        await using var db = TestPostgres.Context(database.Connection(Migrator));
        (await AdminConsoleClientSeeder.SeedAsync(db, TimeProvider.System, Ct)).ShouldBe(AdminConsoleClientSeeder.Outcome.Created);
        var client = await db.Clients.AsNoTracking().Include(c => c.Keys).SingleAsync(c => c.IsManagedByDeployment, Ct);
        (client.Name, client.Kind, client.ClassificationCeiling, client.StoresHistory, client.UpdatedBy).ShouldBe(
            (Client.AdminConsoleName, ClientKinds.Internal, Classification.Sensitive, false, SettingsSeeder.Actor));
        client.Keys.ShouldBeEmpty();

        // Every deploy runs it again: nothing changed, nothing written, the config version stays. Another managed internal client
        // (P2's rag-internal) stands beside it: the managed rows are unique per kind and name, not per kind.
        db.Clients.Add(new Client
        {
            Name = "rag-internal", Kind = ClientKinds.Internal, ClassificationCeiling = Classification.Sensitive, IsManagedByDeployment = true,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
        var version = await VersionAsync();
        (await AdminConsoleClientSeeder.SeedAsync(db, TimeProvider.System, Ct)).ShouldBe(AdminConsoleClientSeeder.Outcome.Unchanged);
        (await AdminConsoleClientSeeder.SeedAsync(db, TimeProvider.System, Ct)).ShouldBe(AdminConsoleClientSeeder.Outcome.Unchanged);
        (await VersionAsync()).ShouldBe(version);
        (await db.Clients.AsNoTracking().CountAsync(c => c.Kind == ClientKinds.Internal && c.IsManagedByDeployment, Ct)).ShouldBe(2);
        db.ChangeTracker.Clear();
        db.Clients.Add(new Client
        {
            Name = Client.AdminConsoleName, Kind = ClientKinds.Internal, ClassificationCeiling = Classification.Sensitive, IsManagedByDeployment = true,
        });
        await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct)); // a second managed admin-console
        db.ChangeTracker.Clear();

        // A key that got onto it (no admin can revoke it) is revoked by the next run.
        db.Keys.Add(Key(client.Id, "stray"));
        await db.SaveChangesAsync(Ct);
        (await AdminConsoleClientSeeder.SeedAsync(db, TimeProvider.System, Ct)).ShouldBe(AdminConsoleClientSeeder.Outcome.KeysRevoked);
        (await db.Keys.AsNoTracking().SingleAsync(k => k.ClientId == client.Id, Ct)).RevokedAt.ShouldNotBeNull();

        // Before #712 a platform admin made it by hand, with a first key: rag-migrate takes it over, keeps its settings and revokes
        // the key. Another (unmanaged) internal client and its key stay as they are.
        await db.Keys.Where(k => k.ClientId == client.Id).ExecuteDeleteAsync(Ct);
        await db.Clients.Where(c => c.Id == client.Id).ExecuteDeleteAsync(Ct);
        var handMade = new Client
        {
            Name = Client.AdminConsoleName, Kind = ClientKinds.Internal, ClassificationCeiling = Classification.Internal, UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "anna",
        };
        var other = new Client { Name = "Evaluation", Kind = ClientKinds.Internal, ClassificationCeiling = Classification.Sensitive, UpdatedAt = DateTimeOffset.UtcNow };
        db.Clients.AddRange(handMade, other);
        db.Keys.AddRange(Key(handMade.Id, "first"), Key(other.Id, "service"));
        await db.SaveChangesAsync(Ct);

        (await AdminConsoleClientSeeder.SeedAsync(db, TimeProvider.System, Ct)).ShouldBe(AdminConsoleClientSeeder.Outcome.Adopted);
        var adopted = await db.Clients.AsNoTracking().Include(c => c.Keys).SingleAsync(c => c.Id == handMade.Id, Ct);
        (adopted.IsManagedByDeployment, adopted.ClassificationCeiling).ShouldBe((true, Classification.Internal));
        adopted.Keys.ShouldHaveSingleItem().RevokedAt.ShouldNotBeNull();
        var untouched = await db.Clients.AsNoTracking().Include(c => c.Keys).SingleAsync(c => c.Id == other.Id, Ct);
        untouched.IsManagedByDeployment.ShouldBeFalse();
        untouched.Keys.ShouldHaveSingleItem().RevokedAt.ShouldBeNull();
    }
}
