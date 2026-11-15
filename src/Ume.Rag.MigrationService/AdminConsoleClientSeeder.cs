using Microsoft.EntityFrameworkCore;
using Ume.Rag.Domain;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Persistence;

namespace Ume.Rag.MigrationService;

/// <summary>
/// The admin test panel's client (identity design §4.1; admin design §4.9; Decisions "Deploy configuration", #712): rag-migrate
/// makes sure, on every run and in every deployment, that the <c>internal</c> client <see cref="Client.AdminConsoleName"/> marked
/// <see cref="Client.IsManagedByDeployment"/> exists (found by kind and name; the name is not editable), insert-if-absent with
/// ceiling <c>sensitive</c> and no history. The panel runs in the admin process and needs no key, so the client keeps none: a key made on
/// it earlier is revoked (the admin cannot, <c>client_managed_by_deployment</c>). A run that changes nothing writes nothing, so
/// it does not move the config version.
/// </summary>
/// <remarks>
/// A platform admin edits its settings like any other client's. An <c>admin-console</c> row of kind <c>internal</c> made earlier
/// (by hand, or by the development seed before #712) becomes the managed one, its settings kept.
/// </remarks>
public static class AdminConsoleClientSeeder
{
    /// <summary>What a run did, for the log.</summary>
    public enum Outcome
    {
        Unchanged,
        Created,
        Adopted,
        KeysRevoked,
    }

    /// <summary>The managed row: kind <c>internal</c>, name <see cref="Client.AdminConsoleName"/> (<c>IX_Clients_Managed</c>).</summary>
    private static readonly System.Linq.Expressions.Expression<Func<Client, bool>> IsManaged =
        c => c.Kind == ClientKinds.Internal && c.Name == Client.AdminConsoleName && c.IsManagedByDeployment;

    public static async Task<Outcome> SeedAsync(RagDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        if (await IsCurrentAsync(db, clock.GetUtcNow(), cancellationToken))
        {
            return Outcome.Unchanged;
        }

        return await db.ExecuteConfigWriteAsync(async (_, ct) =>
        {
            var now = clock.GetUtcNow();
            var outcome = Outcome.KeysRevoked;
            var client = await db.Clients.SingleOrDefaultAsync(IsManaged, ct);
            if (client is null)
            {
                client = await db.Clients.Where(c => c.Kind == ClientKinds.Internal && c.Name == Client.AdminConsoleName).OrderBy(c => c.Id).FirstOrDefaultAsync(ct);
                if (client is not null)
                {
                    client.IsManagedByDeployment = true;
                    client.UpdatedAt = now;
                    client.UpdatedBy = SettingsSeeder.Actor;
                    outcome = Outcome.Adopted;
                }
            }

            if (client is null)
            {
                client = new Client
                {
                    Name = Client.AdminConsoleName, Kind = ClientKinds.Internal, ClassificationCeiling = Classification.Sensitive, StoresHistory = false,
                    IsManagedByDeployment = true, UpdatedAt = now, UpdatedBy = SettingsSeeder.Actor,
                };
                db.Clients.Add(client);
                outcome = Outcome.Created;
            }

            foreach (var key in await db.Keys.Where(k => k.ClientId == client.Id && (k.RevokedAt == null || k.RevokedAt > now)).ToListAsync(ct))
            {
                key.RevokedAt = now;
            }

            await db.SaveChangesAsync(ct);
            return outcome;
        }, cancellationToken);
    }

    /// <summary>The managed <c>admin-console</c> exists and has no key that still works.</summary>
    private static async Task<bool> IsCurrentAsync(RagDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var client = await db.Clients.AsNoTracking().SingleOrDefaultAsync(IsManaged, ct);
        return client is not null && !await db.Keys.AsNoTracking().AnyAsync(k => k.ClientId == client.Id && (k.RevokedAt == null || k.RevokedAt > now), ct);
    }
}
