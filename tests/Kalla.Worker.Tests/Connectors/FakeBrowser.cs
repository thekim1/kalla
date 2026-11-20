using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Microsoft.Playwright;
using Kalla.Worker.Connectors.Render;

namespace Kalla.Worker.Tests.Connectors;

/// <summary>
/// A page without Chromium for the real <see cref="PlaywrightRenderer"/> (its <c>CreatePlaywright</c>): the Playwright
/// interfaces the renderer calls, as <see cref="DispatchProxy"/> fakes. The renderer's own route answers every request: the
/// navigation (the main document the worker fetched) and the requests of <see cref="Script"/>, which runs once <c>load</c>
/// has fired, as a single-page app's code does. The page's DOM is a set of selectors with their text
/// (<see cref="Show"/>); <c>WaitForSelectorAsync</c> waits for one of them, or throws the driver's error
/// <see cref="SelectorError"/>, and <c>ContentAsync</c> serialises them.
/// </summary>
internal sealed class FakeBrowser
{
    private readonly ConcurrentDictionary<string, string> _dom = new(StringComparer.Ordinal);
    private readonly IBrowserType _chromium;
    private readonly IBrowser _browser;
    private readonly IBrowserContext _context;
    private readonly IPage _page;
    private readonly IFrame _frame;
    private Func<IRoute, Task>? _route;

    public FakeBrowser()
    {
        _frame = FakeProxy.Of<IFrame>((m, _) => m.Name switch
        {
            "get_Page" => _page,
            "get_ParentFrame" => null,
            _ => throw Unexpected<IFrame>(m),
        });
        _page = FakeProxy.Of<IPage>((m, a) => m.Name switch
        {
            "add_Popup" or "add_Download" or "remove_Popup" or "remove_Download" => null,
            "GotoAsync" => FakeProxy.Later(m, () => LoadAsync((string)a[0]!)),
            "WaitForSelectorAsync" => FakeProxy.Later(m, () => WaitForSelectorAsync((string)a[0]!, (PageWaitForSelectorOptions?)a[1])),
            "Locator" => Locator((string)a[0]!),
            "ContentAsync" => FakeProxy.Done(m, Content()),
            "get_Context" => _context,
            "get_IsClosed" => false,
            _ => throw Unexpected<IPage>(m),
        });
        _context = FakeProxy.Of<IBrowserContext>((m, a) => m.Name switch
        {
            "RouteAsync" => Route(m, (Func<IRoute, Task>)a[1]!),
            "RouteWebSocketAsync" or "CloseAsync" => FakeProxy.Done(m),
            "NewPageAsync" => FakeProxy.Done(m, _page),
            "get_Browser" => _browser,
            _ => throw Unexpected<IBrowserContext>(m),
        });
        _browser = FakeProxy.Of<IBrowser>((m, _) => m.Name switch
        {
            "get_IsConnected" => true,
            "get_Version" => "fake",
            "add_Disconnected" => null,
            "NewContextAsync" => FakeProxy.Done(m, _context),
            "CloseAsync" => FakeProxy.Done(m),
            _ => throw Unexpected<IBrowser>(m),
        });
        _chromium = FakeProxy.Of<IBrowserType>((m, _) => m.Name == "ConnectAsync" ? FakeProxy.Done(m, _browser) : throw Unexpected<IBrowserType>(m));
    }

    /// <summary>What the page's code does once <c>load</c> has fired; runs in the background.</summary>
    public Func<FakeBrowser, Task>? Script { get; set; }

    /// <summary>The driver's message for a selector it refuses; null accepts every selector.</summary>
    public string? SelectorError { get; set; }

    /// <summary>The playwright the renderer starts (<c>PlaywrightRenderer.CreatePlaywright</c>).</summary>
    public Task<IPlaywright> CreateAsync() => Task.FromResult(FakeProxy.Of<IPlaywright>((m, _) => m.Name switch
    {
        "get_Chromium" => _chromium,
        "Dispose" => null,
        _ => throw Unexpected<IPlaywright>(m),
    }));

    /// <summary>The element <paramref name="selector"/> names now holds <paramref name="text"/>.</summary>
    public void Show(string selector, string text) => _dom[selector] = text;

    /// <summary>A request of the page's code (resource type <c>fetch</c>) through the renderer's route; the fulfilled body, empty when aborted.</summary>
    public async Task<string> FetchAsync(Uri url)
    {
        var fulfilled = await RouteAsync(url, navigation: false);
        return fulfilled?.BodyBytes is { } body ? Encoding.UTF8.GetString(body) : string.Empty;
    }

    private object Route(MethodInfo method, Func<IRoute, Task> handler)
    {
        _route = handler;
        return FakeProxy.Done(method);
    }

    private async Task<object?> LoadAsync(string url)
    {
        await RouteAsync(new Uri(url), navigation: true);
        if (Script is { } script)
        {
            _ = Task.Run(() => script(this));
        }

        return null;
    }

    private async Task<RouteFulfillOptions?> RouteAsync(Uri url, bool navigation)
    {
        RouteFulfillOptions? fulfilled = null;
        var request = FakeProxy.Of<IRequest>((m, _) => m.Name switch
        {
            "get_IsNavigationRequest" => navigation,
            "get_Frame" => _frame,
            "get_Url" => url.AbsoluteUri,
            "get_Method" => "GET",
            "HeadersArrayAsync" => FakeProxy.Done(m, Array.Empty<Header>()),
            "get_PostDataBuffer" => null,
            "get_ResourceType" => navigation ? "document" : "fetch",
            _ => throw Unexpected<IRequest>(m),
        });
        var route = FakeProxy.Of<IRoute>((m, a) => m.Name switch
        {
            "get_Request" => request,
            "FulfillAsync" => FakeProxy.Done(m, fulfilled = (RouteFulfillOptions?)a[0]),
            "AbortAsync" => FakeProxy.Done(m),
            _ => throw Unexpected<IRoute>(m),
        });
        await (_route ?? throw new InvalidOperationException("The context has no route."))(route);
        return fulfilled;
    }

    private async Task<object?> WaitForSelectorAsync(string selector, PageWaitForSelectorOptions? options)
    {
        if (SelectorError is { } error)
        {
            throw new PlaywrightException(error);
        }

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(options?.Timeout ?? 30_000);
        while (!_dom.ContainsKey(selector))
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timeout exceeded waiting for " + selector);
            }

            await Task.Delay(10);
        }

        return null;
    }

    private ILocator Locator(string selector)
    {
        ILocator? locator = null;
        locator = FakeProxy.Of<ILocator>((m, _) => m.Name switch
        {
            "get_First" => locator,
            "InnerTextAsync" => FakeProxy.Done(m, _dom.GetValueOrDefault(selector, string.Empty)),
            _ => throw Unexpected<ILocator>(m),
        });
        return locator;
    }

    private string Content() =>
        "<html><head><title>App</title></head><body><main>" + string.Concat(_dom.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"<p>{e.Value}</p>"))
        + "</main></body></html>";

    private static NotSupportedException Unexpected<T>(MethodInfo method) => new($"The fake browser has no {typeof(T).Name}.{method.Name}.");
}

/// <summary>An interface answered by <see cref="Handler"/> (the member and its arguments); a fake of <see cref="FakeBrowser"/>.</summary>
public class FakeProxy : DispatchProxy
{
    private static readonly MethodInfo CastMethod = typeof(FakeProxy).GetMethod(nameof(CastAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private Func<MethodInfo, object?[], object?> Handler { get; set; } = (m, _) => throw new NotSupportedException(m.Name);

    public static T Of<T>(Func<MethodInfo, object?[], object?> handler)
        where T : class
    {
        var proxy = Create<T, FakeProxy>();
        ((FakeProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    /// <summary>A completed task of the member's return type (<c>Task</c> or <c>Task&lt;T&gt;</c>) with <paramref name="value"/>.</summary>
    public static object Done(MethodInfo method, object? value = null) => Later(method, () => Task.FromResult(value));

    /// <summary>The member's task (<c>Task</c> or <c>Task&lt;T&gt;</c>) that ends with <paramref name="work"/>.</summary>
    public static object Later(MethodInfo method, Func<Task<object?>> work)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(work);
        var task = work();
        return method.ReturnType == typeof(Task) ? task : CastMethod.MakeGenericMethod(method.ReturnType.GetGenericArguments()[0]).Invoke(null, [task])!;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);

    private static async Task<T> CastAsync<T>(Task<object?> work) => (T)(await work)!;
}
