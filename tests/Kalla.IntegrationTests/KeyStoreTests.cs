using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Kalla.Domain;
using Kalla.Domain.Entities;
using Kalla.Domain.Services;
using Kalla.Infrastructure;
using Kalla.Infrastructure.Config;
using Kalla.Infrastructure.Keys;
using Kalla.TestKit;
using static Kalla.TestKit.RagDatabase;

namespace Kalla.IntegrationTests;

/// <summary>
/// Keys in <c>config."Keys"</c> (P0 #10, identity design §6): an api-role host with the config monitor and the
/// <see cref="KeyStore"/>, the admin role creating and revoking keys. Completes the revoked-key check of P0 #7 against
/// the real table.
/// </summary>
[Collection(RagDatabaseGroup.Name)]
public sealed class KeyStoreTests(RagDatabaseFixture database)
{
    private const string Pepper = "key-store-test-pepper-0123456789abcdef";
    private static readonly KeyHasher Hasher = new(System.Text.Encoding.UTF8.GetBytes(Pepper));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_revoked_key_is_refused_as_soon_as_the_notification_arrives()
    {
        using var host = await StartApiAsync();
        var store = host.Services.GetRequiredService<KeyStore>();
        var (keyId, plain) = await CreateKeyAsync();

        var key = await store.FindAsync(plain, Ct);
        key.ShouldNotBeNull();
        KeyChecks.Refusal(key, plain, DateTimeOffset.UtcNow, null).ShouldBeNull();
        key.Client!.Name.ShouldBe("Integration test");

        await AdminWriteAsync(async db =>
        {
            var stored = await db.Keys.SingleAsync(k => k.Id == keyId, Ct);
            KeyRotation.Revoke(stored, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(Ct);
        });

        // The cache holds keys for 5 minutes here (StartApiAsync), so only the notification can clear it; no timing bound (#297).
        await WaitUntilAsync(async () => await store.FindAsync(plain, Ct) is { } k && KeyChecks.Refusal(k, plain, DateTimeOffset.UtcNow, null) == KeyChecks.KeyRevoked,
            TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_new_key_clears_the_cache_of_unknown_keys()
    {
        using var host = await StartApiAsync();
        var store = host.Services.GetRequiredService<KeyStore>();
        var generated = Hasher.Generate();
        (await store.FindAsync(generated.PlainText, Ct)).ShouldBeNull(); // now remembered as unknown for the cache's 5 minutes

        await CreateKeyAsync(generated);

        // Only the notification can clear the unknown-key cache within 30 s (#297: no timing bound).
        await WaitUntilAsync(async () => await store.FindAsync(generated.PlainText, Ct) is not null, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Disabling_the_client_refuses_its_keys_as_owner_inactive()
    {
        using var host = await StartApiAsync();
        var store = host.Services.GetRequiredService<KeyStore>();
        var (keyId, plain) = await CreateKeyAsync();
        (await store.FindAsync(plain, Ct)).ShouldNotBeNull();

        await AdminWriteAsync(async db =>
        {
            var client = await db.Clients.SingleAsync(c => c.Keys.Any(k => k.Id == keyId), Ct);
            client.IsEnabled = false;
            await db.SaveChangesAsync(Ct);
        });

        await WaitUntilAsync(async () => await store.FindAsync(plain, Ct) is { } k && KeyChecks.Refusal(k, plain, DateTimeOffset.UtcNow, null) == KeyChecks.OwnerInactive);
    }

    [Fact]
    public async Task Keys_that_are_not_ours_are_never_looked_up()
    {
        using var host = await StartApiAsync();
        var store = host.Services.GetRequiredService<KeyStore>();
        (await store.FindAsync("ume-sk-0123456789abcdefghijABCDEFGHIJ0123456789abc", Ct)).ShouldBeNull();
        (await store.FindAsync(null, Ct)).ShouldBeNull();
        (await store.FindAsync(Hasher.Generate().PlainText, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Key_use_is_recorded_without_bumping_the_config_version()
    {
        var (keyId, _) = await CreateKeyAsync();
        var before = await Sql.ScalarAsync<long>(database.Connection(Api), """SELECT "Version" FROM config."ConfigVersion" """);

        await Sql.ExecuteAsync(database.Connection(Api), $"""
            INSERT INTO config."KeyLastUse" ("KeyId", "LastUsedAt") VALUES ('{keyId}', now())
            ON CONFLICT ("KeyId") DO UPDATE SET "LastUsedAt" = excluded."LastUsedAt"
            """);

        (await Sql.ScalarAsync<long>(database.Connection(Api), """SELECT "Version" FROM config."ConfigVersion" """)).ShouldBe(before);
        await Sql.ShouldBeDeniedAsync(database.Connection(Api), $"""UPDATE config."Keys" SET "RevokedAt" = NULL WHERE "Id" = '{keyId}'""");
    }

    [Fact]
    public async Task Concurrent_client_edits_are_detected_by_xmin()
    {
        var (keyId, _) = await CreateKeyAsync();
        await using var first = TestPostgres.Context(database.Connection(Admin));
        await using var second = TestPostgres.Context(database.Connection(Admin));
        var a = await first.Clients.SingleAsync(c => c.Keys.Any(k => k.Id == keyId), Ct);
        var b = await second.Clients.SingleAsync(c => c.Id == a.Id, Ct);

        a.Name = "First";
        await first.SaveChangesAsync(Ct);
        b.Name = "Second";
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Client_rules_refuse_history_or_mcp_at_sensitive()
    {
        await using var db = TestPostgres.Context(database.Connection(Admin));
        db.Clients.Add(new Client { Name = "x", Kind = ClientKinds.OpenWebUi, ClassificationCeiling = Classification.Sensitive, StoresHistory = true });
        (await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct))).InnerException!.Message.ShouldContain("CK_Clients_History");
        db.ChangeTracker.Clear();
        db.Clients.Add(new Client { Name = "y", Kind = ClientKinds.McpClient, ClassificationCeiling = Classification.Sensitive });
        (await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct))).InnerException!.Message.ShouldContain("CK_Clients_Mcp");
    }

    private async Task<IHost> StartApiAsync()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ragdb"] = database.Connection(Api),
            ["Security:KeyPepper"] = Pepper,
            // The maximum (5 minutes), ten times the 30 s waits below, so a cleared cache proves the notification, not the expiry (#297).
            [$"{KeyStoreOptions.SectionName}:KeyCacheSeconds"] = "300",
        });
        builder.AddRagDatabase();
        builder.AddRagConfigMonitor();
        builder.AddRagKeys();
        var host = builder.Build();
        await host.StartAsync(Ct);
        var state = host.Services.GetRequiredService<ConfigVersionState>();
        await WaitUntilAsync(() => Task.FromResult(state.IsConfirmed));
        return host;
    }

    private async Task<(Guid KeyId, string Plain)> CreateKeyAsync(GeneratedKey? generated = null)
    {
        generated ??= Hasher.Generate();
        var keyId = Guid.Empty;
        await AdminWriteAsync(async db =>
        {
            var client = new Client { Name = "Integration test", Kind = ClientKinds.Integration, ClassificationCeiling = Classification.Internal, UpdatedAt = DateTimeOffset.UtcNow };
            var key = new ApiKey
            {
                Client = client,
                Name = "test key",
                Prefix = generated.Prefix,
                KeyHash = generated.Hash,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedBy = "test",
            };
            db.AddRange(client, key);
            await db.SaveChangesAsync(Ct);
            keyId = key.Id;
        });
        return (keyId, generated.PlainText);
    }

    /// <summary>A config write as the admin API makes it (P0 #11): version lock, change, commit.</summary>
    private async Task AdminWriteAsync(Func<Infrastructure.Persistence.RagDbContext, Task> change)
    {
        await using var db = TestPostgres.Context(database.Connection(Admin));
        await db.ExecuteConfigWriteAsync(async (_, _) =>
        {
            await change(db);
            return 0;
        }, Ct);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            if (deadline.Elapsed > (timeout ?? TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(10, Ct);
        }
    }
}
