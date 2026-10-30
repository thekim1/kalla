using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Domain.Services;
using Ume.Rag.Infrastructure.Config;
using Ume.Rag.Infrastructure.Persistence;

namespace Ume.Rag.Infrastructure.Keys;

/// <summary>
/// Resolves a presented key, adapted from the gateway's <c>KeyAuthenticator</c> (identity design §6.2). Only the HMAC
/// hash is used for lookup; the plaintext key is never logged, cached or stored. Entries are cached briefly; an entry
/// older than the TTL is still served while one background lookup refreshes it, and concurrent misses for the same key
/// share one query. Unknown keys are remembered for the same TTL in a separate, smaller cache, so made-up keys cannot
/// evict real ones. Adapted: invalidation comes from the config version (<see cref="IConfigChangeHandler"/>, outbox
/// types <c>Keys</c> and <c>Clients</c>) instead of the gateway's Redis bus, so a revocation or a new key reaches every
/// instance with the notification, and within the 2 s poll if a notification is missed.
/// </summary>
public sealed class KeyStore : IConfigChangeHandler, IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 100_000 });
    private readonly MemoryCache _unknown = new(new MemoryCacheOptions { SizeLimit = 10_000 });
    private readonly ConcurrentDictionary<string, SharedLoad> _loading = new(StringComparer.Ordinal);
    private readonly KeyHasher _hasher;
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsMonitor<KeyStoreOptions> _options;
    private readonly TimeProvider _time;
    private CancellationTokenSource _generation = new();

    private sealed record CachedKey(ApiKey Key, DateTimeOffset LoadedAt);

    /// <summary>A database lookup in progress, and the cache generation it started in.</summary>
    private sealed record SharedLoad(Task<ApiKey?> Task, CancellationToken Generation);

    public KeyStore(KeyHasher hasher, IServiceScopeFactory scopes, IOptionsMonitor<KeyStoreOptions> options, TimeProvider time)
    {
        _hasher = hasher;
        _scopes = scopes;
        _options = options;
        _time = time;
    }

    /// <summary>The presented key from <c>Authorization: Bearer</c>, <c>x-api-key</c> or <c>api-key</c> (copied).</summary>
    public static string? ExtractKey(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var auth = request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return auth["Bearer ".Length..].Trim();
        }

        foreach (var header in (string[])["x-api-key", "api-key"])
        {
            if (request.Headers.TryGetValue(header, out var value) && !StringValues.IsNullOrEmpty(value))
            {
                return value.ToString().Trim();
            }
        }

        return null;
    }

    /// <summary>The key if it is in the cache (fresh or stale); never queries the database. For throttled client addresses.</summary>
    public ApiKey? FindCached(string? presentedKey) =>
        KeyHasher.LooksLikeKey(presentedKey) && _cache.TryGetValue(_hasher.Hash(presentedKey!), out CachedKey? cached) ? cached?.Key : null;

    /// <summary>
    /// The key with its client, or null when it is not shaped like a Ume.Rag key or unknown. The caller then applies
    /// <see cref="KeyChecks.Refusal"/> (status, client) after <see cref="ConfigVersionState.EnsureConfirmed"/>.
    /// </summary>
    public async Task<ApiKey?> FindAsync(string? presentedKey, CancellationToken cancellationToken)
    {
        if (!KeyHasher.LooksLikeKey(presentedKey))
        {
            return null;
        }

        var hash = _hasher.Hash(presentedKey!);
        var ttl = TimeSpan.FromSeconds(_options.CurrentValue.KeyCacheSeconds);
        if (_cache.TryGetValue(hash, out CachedKey? cached) && cached is not null)
        {
            if (_time.GetUtcNow() - cached.LoadedAt >= ttl)
            {
                // Refresh in the background; a failure keeps the cached entry and surfaces on the next miss.
                _ = LoadSharedAsync(hash, ttl).ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }

            return cached.Key;
        }

        if (_unknown.TryGetValue(hash, out DateTimeOffset unknownSince) && _time.GetUtcNow() - unknownSince < ttl)
        {
            return null;
        }

        return await LoadSharedAsync(hash, ttl).WaitAsync(cancellationToken);
    }

    /// <summary>Clears both caches when keys or clients changed (revocation, new key, client disabled).</summary>
    public Task ApplyAsync(ConfigChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Affects("Keys") || change.Affects("Clients"))
        {
            InvalidateAll();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// One database lookup per key hash at a time, not tied to any single request's cancellation. A lookup that started
    /// before the latest invalidation is not shared: it may have read the key before a revocation, so a new one starts.
    /// </summary>
    private Task<ApiKey?> LoadSharedAsync(string hash, TimeSpan ttl)
    {
        // Captured before the query: if a revocation lands while it runs, the result is neither cached nor shared.
        var generation = CurrentGeneration();
        if (_loading.TryGetValue(hash, out var running) && !running.Generation.IsCancellationRequested)
        {
            return running.Task;
        }

        var load = new TaskCompletionSource<ApiKey?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new SharedLoad(load.Task, generation);
        if (!(running is null ? _loading.TryAdd(hash, entry) : _loading.TryUpdate(hash, entry, running)))
        {
            return LoadSharedAsync(hash, ttl); // another request registered a lookup first
        }

        _ = Task.Run(async () =>
        {
            try
            {
                load.SetResult(await LoadAsync(hash, ttl, generation));
            }
            catch (Exception ex)
            {
                load.SetException(ex);
            }
            finally
            {
                _loading.TryRemove(new KeyValuePair<string, SharedLoad>(hash, entry));
            }
        });
        return load.Task;
    }

    private CancellationToken CurrentGeneration()
    {
        try
        {
            return Volatile.Read(ref _generation).Token;
        }
        catch (ObjectDisposedException)
        {
            return new CancellationToken(canceled: true); // invalidated this instant: don't cache or share
        }
    }

    private async Task<ApiKey?> LoadAsync(string hash, TimeSpan ttl, CancellationToken generation)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RagDbContext>();
        var key = await db.Keys.AsNoTracking()
            .Include(k => k.Client)
            .FirstOrDefaultAsync(k => k.KeyHash == hash, CancellationToken.None);
        if (key is null)
        {
            _cache.Remove(hash);
            if (ttl > TimeSpan.Zero && !generation.IsCancellationRequested)
            {
                using var unknown = _unknown.CreateEntry(hash);
                // Freshness is checked against TimeProvider like the positive cache; the cache's own expiry only evicts.
                unknown.Value = _time.GetUtcNow();
                unknown.AbsoluteExpirationRelativeToNow = ttl;
                unknown.Size = 1;
                unknown.AddExpirationToken(new CancellationChangeToken(generation));
            }

            return null;
        }

        if (ttl > TimeSpan.Zero && !generation.IsCancellationRequested)
        {
            using var entry = _cache.CreateEntry(hash);
            entry.Value = new CachedKey(key, _time.GetUtcNow());
            // Kept past the TTL so a busy key is refreshed in the background instead of missing; idle keys drop out.
            entry.AbsoluteExpirationRelativeToNow = ttl * 2;
            entry.Size = 1;
            entry.AddExpirationToken(new CancellationChangeToken(generation));
        }

        return key;
    }

    public void InvalidateAll()
    {
        var old = Interlocked.Exchange(ref _generation, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
    }

    public void Dispose()
    {
        _generation.Dispose();
        _cache.Dispose();
        _unknown.Dispose();
    }
}
