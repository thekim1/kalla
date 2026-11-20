using Microsoft.Extensions.Options;

namespace Kalla.Worker.Connectors.Render;

/// <summary>
/// Scripts and stylesheets fetched for rendered pages, kept in the worker per (level, run, URL) within
/// <c>Render:AssetCacheMb</c> (connectors design §8.3, "Asset cache"): Playwright's routing turns the browser's own cache
/// off, so this is the only one. A level never sees another level's entries and a run never sees another run's, so an
/// asset changed on the site is fetched again at the next run. A <c>200</c> without <c>no-store</c> is kept; the oldest
/// entries go first when the budget is full.
/// </summary>
public sealed class RenderAssetCache(IOptions<RenderOptions> options)
{
    private readonly long _capacity = options.Value.AssetCacheMb * 1024L * 1024L;
    private readonly Dictionary<string, RenderResponse> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly Lock _lock = new();
    private long _bytes;

    /// <summary>Bytes held.</summary>
    public long Bytes
    {
        get
        {
            lock (_lock)
            {
                return _bytes;
            }
        }
    }

    /// <summary>Whether a request may be answered from, and stored in, the cache: a <c>GET</c> of a script or a stylesheet.</summary>
    public static bool Cacheable(RenderSubrequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Method == "GET" && request.ResourceType is "script" or "stylesheet";
    }

    public bool TryGet(string level, Guid runId, Uri url, out RenderResponse response)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(Key(level, runId, url), out response!);
        }
    }

    public void Put(string level, Guid runId, Uri url, RenderResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Status != 200 || response.Kind != RenderResponseKind.Fetched || response.Body.Length > _capacity
            || (response.Headers.TryGetValue("cache-control", out var control) && control.Contains("no-store", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var key = Key(level, runId, url);
        lock (_lock)
        {
            if (!_entries.TryAdd(key, response with { Kind = RenderResponseKind.Cached }))
            {
                return;
            }

            _order.Enqueue(key);
            _bytes += response.Body.Length;
            while (_bytes > _capacity && _order.TryDequeue(out var oldest))
            {
                if (_entries.Remove(oldest, out var evicted))
                {
                    _bytes -= evicted.Body.Length;
                }
            }
        }
    }

    private static string Key(string level, Guid runId, Uri url) => $"{level}\n{runId:N}\n{url.AbsoluteUri}";
}
