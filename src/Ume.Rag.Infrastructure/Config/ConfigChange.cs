namespace Ume.Rag.Infrastructure.Config;

/// <summary>
/// What changed between two confirmed config versions (data-model §5): the entity types (table names) of the outbox
/// rows in between, or <see cref="FullReload"/> when the outbox no longer covers the gap (first load, pruned rows).
/// </summary>
public sealed record ConfigChange(long FromVersion, long ToVersion, bool FullReload, IReadOnlySet<string> EntityTypes, bool SecurityRelevant)
{
    /// <summary>Whether a handler that caches <paramref name="entityType"/> must reload it.</summary>
    public bool Affects(string entityType) => FullReload || EntityTypes.Contains(entityType);
}

/// <summary>
/// Reloads what a service caches from config tables (a snapshot, the key cache) when the config version moves. Called by
/// <see cref="ConfigVersionMonitor"/> before the new version is confirmed, one change at a time; an exception leaves the
/// old version in place and the change is offered again on the next notification or poll.
/// </summary>
/// <remarks>
/// A handler may see the same change more than once (after its own or another handler's failure, or a reconnect in the
/// middle of a refresh), so it must be an idempotent reload of what the change affects, never an incremental update.
/// </remarks>
public interface IConfigChangeHandler
{
    Task ApplyAsync(ConfigChange change, CancellationToken cancellationToken);
}
