using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Kalla.TestKit;
using Kalla.Worker.Tests.Connectors;

namespace Kalla.Worker.Tests.Scale;

/// <summary>Requests of one virtual host: in flight now, the most at once, and the pages it served.</summary>
internal sealed class HostLoad
{
    private int _current;
    private int _max;
    private int _pages;

    public int MaxInFlight => Volatile.Read(ref _max);

    public int Pages => Volatile.Read(ref _pages);

    public void Enter()
    {
        var now = Interlocked.Increment(ref _current);
        var max = Volatile.Read(ref _max);
        while (now > max)
        {
            var seen = Interlocked.CompareExchange(ref _max, now, max);
            if (seen == max)
            {
                break;
            }

            max = seen;
        }
    }

    public void Exit() => Interlocked.Decrement(ref _current);

    public void Served() => Interlocked.Increment(ref _pages);
}

/// <summary>
/// One in-process Kestrel site (<see cref="TestSite"/>) answering the virtual hosts <c>h01.scale.test</c> … by the
/// <c>Host</c> header: per host a robots.txt that allows everything and <see cref="PagesPerHost"/> deterministic Swedish
/// pages (<c>/</c> and <c>/sida/1</c> …), each linking to two more on its host as a binary tree, so a crawl from <c>/</c>
/// reaches every page within five levels. The text is the smoke set's (<see cref="SmokeSet"/>), with the host and page
/// number in the title and a sentence of their own, so no two pages share a content hash. Every request waits
/// <c>latency</c> before it is answered; the site counts each host's requests in flight and keeps the most at once.
/// </summary>
internal sealed class ScaleSite : IAsyncDisposable
{
    public const string Domain = "scale.test";
    private const string PagePrefix = "/sida/";

    private readonly ConcurrentDictionary<string, HostLoad> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _numbers;
    private readonly IReadOnlyList<(string Title, string Body)> _texts;
    private readonly TimeSpan _latency;
    private TestSite? _site;
    private int _inFlight;
    private long _firstPage = long.MaxValue;
    private long _lastPage = long.MinValue;

    private ScaleSite(int hosts, int pagesPerHost, TimeSpan latency)
    {
        Hosts = [.. Enumerable.Range(1, hosts).Select(HostName)];
        _numbers = Hosts.Select((host, number) => (host, number)).ToDictionary(h => h.host, h => h.number, StringComparer.OrdinalIgnoreCase);
        PagesPerHost = pagesPerHost;
        _latency = latency;
        _texts = [.. SmokeSet.Documents().Select(d => Html(d.Markdown))];
    }

    public IReadOnlyList<string> Hosts { get; }

    public int PagesPerHost { get; }

    public TestSite Site => _site ?? throw new InvalidOperationException("The site is not started.");

    /// <summary>Requests in flight on every host.</summary>
    public int InFlight => Volatile.Read(ref _inFlight);

    /// <summary>Pages served (200) on every host.</summary>
    public int PagesServed => _hosts.Values.Sum(h => h.Pages);

    /// <summary>From the first page request's arrival to the last page's answer; zero before any.</summary>
    public TimeSpan PageSpan
    {
        get
        {
            var first = Interlocked.Read(ref _firstPage);
            var last = Interlocked.Read(ref _lastPage);
            return last > first ? Stopwatch.GetElapsedTime(first, last) : TimeSpan.Zero;
        }
    }

    /// <summary>When the first page request arrived (a <see cref="Stopwatch"/> timestamp), or null.</summary>
    public long? FirstPageAt => Interlocked.Read(ref _firstPage) is var first && first != long.MaxValue ? first : null;

    /// <summary>When the last page was answered (a <see cref="Stopwatch"/> timestamp), or null.</summary>
    public long? LastPageAt => Interlocked.Read(ref _lastPage) is var last && last != long.MinValue ? last : null;

    public static string HostName(int number) => string.Create(CultureInfo.InvariantCulture, $"h{number:D2}.{Domain}");

    public static async Task<ScaleSite> StartAsync(int hosts, int pagesPerHost, TimeSpan latency)
    {
        var site = new ScaleSite(hosts, pagesPerHost, latency);
        site._site = await TestSite.StartAsync(site.Map);
        return site;
    }

    public HostLoad Load(string host) => _hosts.GetOrAdd(host, _ => new HostLoad());

    public Uri StartUrl(string host) => new(string.Create(CultureInfo.InvariantCulture, $"http://{host}:{Site.Port}/"));

    /// <summary>The site's resolver with every virtual host on loopback.</summary>
    public FakeDnsResolver Resolver()
    {
        var resolver = Site.Resolver();
        foreach (var host in Hosts)
        {
            resolver.Add(host, "127.0.0.1");
        }

        return resolver;
    }

    /// <summary>The page at <paramref name="url"/> as the site serves it (the fake renderer's output for a browser source).</summary>
    public string PageAt(Uri url) =>
        IndexOf(url.AbsolutePath) is { } index ? Page(url.Host, index) : throw new ArgumentException($"{url} is no page of the site.", nameof(url));

    public async ValueTask DisposeAsync()
    {
        if (_site is not null)
        {
            await _site.DisposeAsync();
        }
    }

    private void Map(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var started = Stopwatch.GetTimestamp();
            var load = Load(context.Request.Host.Host);
            load.Enter();
            Interlocked.Increment(ref _inFlight);
            try
            {
                await Task.Delay(_latency, context.RequestAborted);
                await next(context);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
                load.Exit();
            }

            if (context.Response.StatusCode == StatusCodes.Status200OK && IndexOf(context.Request.Path) is not null)
            {
                load.Served();
                Earliest(ref _firstPage, started);
                Latest(ref _lastPage, Stopwatch.GetTimestamp());
            }
        });
        app.MapGet("/robots.txt", () => Results.Text("User-agent: *\nAllow: /\n", "text/plain"));
        app.MapGet("/", (HttpContext context) => Served(context, 0));
        app.MapGet(PagePrefix + "{index:int}", (int index, HttpContext context) => Served(context, index));
        // Every other path, the soft-404 probe's among them: a real 404.
        app.MapFallback(() => Results.Content("<!doctype html><html lang=\"sv\"><body><h1>Sidan hittades inte</h1></body></html>",
            "text/html; charset=utf-8", statusCode: StatusCodes.Status404NotFound));
    }

    private IResult Served(HttpContext context, int index) =>
        index < PagesPerHost && _numbers.ContainsKey(context.Request.Host.Host)
            ? Results.Content(Page(context.Request.Host.Host, index), "text/html; charset=utf-8")
            : Results.NotFound();

    private int? IndexOf(string? path)
    {
        if (string.Equals(path, "/", StringComparison.Ordinal))
        {
            return 0;
        }

        return path is not null && path.StartsWith(PagePrefix, StringComparison.Ordinal)
            && int.TryParse(path.AsSpan(PagePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            && index > 0 && index < PagesPerHost
            ? index
            : null;
    }

    private string Page(string host, int index)
    {
        var (title, body) = _texts[(_numbers.GetValueOrDefault(host) + index) % _texts.Count];
        var links = new StringBuilder();
        foreach (var child in new[] { (2 * index) + 1, (2 * index) + 2 }.Where(c => c < PagesPerHost))
        {
            links.Append(CultureInfo.InvariantCulture, $"""<li><a href="{PagePrefix}{child}">Mer om {WebUtility.HtmlEncode(title)}, del {child}</a></li>""");
        }

        var heading = WebUtility.HtmlEncode(string.Create(CultureInfo.InvariantCulture, $"{title} – sida {index} på {host}"));
        return string.Create(CultureInfo.InvariantCulture, $$"""
            <!doctype html><html lang="sv"><head><title>{{heading}}</title></head><body>
            <nav><a href="/">Startsida</a><ul>{{links}}</ul></nav>
            <main><h1>{{heading}}</h1>
            <p>Den här sidan är nummer {{index}} av {{PagesPerHost}} på {{WebUtility.HtmlEncode(host)}} och beskriver {{WebUtility.HtmlEncode(title.ToLowerInvariant())}}.</p>
            {{body}}
            </main></body></html>
            """);
    }

    /// <summary>The smoke page's title (its <c># </c> line) and its other lines as headings and paragraphs.</summary>
    private static (string Title, string Body) Html(string markdown)
    {
        var title = string.Empty;
        var body = new StringBuilder();
        foreach (var line in markdown.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                title = line[2..];
            }
            else if (line.StartsWith('#'))
            {
                body.Append("<h2>").Append(WebUtility.HtmlEncode(line.TrimStart('#').Trim())).Append("</h2>\n");
            }
            else
            {
                body.Append("<p>").Append(WebUtility.HtmlEncode(line.TrimStart('-', '*', ' '))).Append("</p>\n");
            }
        }

        return (title, body.ToString());
    }

    private static void Earliest(ref long field, long value)
    {
        var seen = Interlocked.Read(ref field);
        while (value < seen)
        {
            var previous = Interlocked.CompareExchange(ref field, value, seen);
            if (previous == seen)
            {
                return;
            }

            seen = previous;
        }
    }

    private static void Latest(ref long field, long value)
    {
        var seen = Interlocked.Read(ref field);
        while (value > seen)
        {
            var previous = Interlocked.CompareExchange(ref field, value, seen);
            if (previous == seen)
            {
                return;
            }

            seen = previous;
        }
    }
}
