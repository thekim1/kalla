using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Infrastructure.Persistence;

namespace Ume.Rag.Infrastructure.Stores;

/// <summary>Where the admin BFF keeps its sessions (the gateway's interface, <c>Infrastructure/Stores/Stores.cs</c>).</summary>
public interface IAdminSessionStore
{
    Task SetAsync(string sessionId, byte[] value, TimeSpan timeToLive, CancellationToken cancellationToken);

    Task<byte[]?> GetAsync(string sessionId, CancellationToken cancellationToken);

    Task RemoveAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>
/// Admin sessions in <c>config."AdminSessions"</c> (simplification pass §14, identity design §1.1): replaces the gateway's
/// Redis store, so every admin instance shares them without Redis. A read ignores expired rows; the retention step
/// <c>config.rag_retention_config_step</c> deletes them.
/// </summary>
public sealed class PostgresAdminSessionStore(IServiceScopeFactory scopes, TimeProvider time) : IAdminSessionStore
{
    public async Task SetAsync(string sessionId, byte[] value, TimeSpan timeToLive, CancellationToken cancellationToken)
    {
        var expires = time.GetUtcNow() + timeToLive;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO config."AdminSessions" ("SessionId", "Value", "ExpiresAt") VALUES ({sessionId}, {value}, {expires})
            ON CONFLICT ("SessionId") DO UPDATE SET "Value" = excluded."Value", "ExpiresAt" = excluded."ExpiresAt"
            """, cancellationToken);
    }

    public async Task<byte[]?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        return await db.AdminSessions.AsNoTracking()
            .Where(s => s.SessionId == sessionId && s.ExpiresAt > now)
            .Select(s => s.Value)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task RemoveAsync(string sessionId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        await db.AdminSessions.Where(s => s.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken);
    }
}
