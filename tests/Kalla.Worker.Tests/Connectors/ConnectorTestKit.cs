using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Kalla.Infrastructure.Blobs;
using Kalla.Worker.Connectors;
using Kalla.Worker.Connectors.Egress;
using Kalla.Worker.Connectors.Hosts;
using Kalla.Worker.Connectors.Http;
using Kalla.Worker.Connectors.Malware;
using Kalla.Worker.Connectors.Render;
using Kalla.Worker.Connectors.Robots;
using Kalla.Worker.Connectors.UrlList;

namespace Kalla.Worker.Tests.Connectors;

/// <summary>A resolver with fixed answers per host; <see cref="Calls"/> counts lookups (DNS pinning tests).</summary>
internal sealed class FakeDnsResolver : IDnsResolver
{
    private readonly ConcurrentDictionary<string, Func<int, IPAddress[]>> _answers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.OrdinalIgnoreCase);

    public FakeDnsResolver Add(string host, params string[] addresses)
    {
        var parsed = addresses.Select(IPAddress.Parse).ToArray();
        _answers[host] = _ => parsed;
        return this;
    }

    /// <summary>An answer that changes with the call number (0-based): rebinding.</summary>
    public FakeDnsResolver Add(string host, Func<int, string[]> answer)
    {
        _answers[host] = n => [.. answer(n).Select(IPAddress.Parse)];
        return this;
    }

    public int Calls(string host) => _calls.TryGetValue(host, out var n) ? n : 0;

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        var call = _calls.AddOrUpdate(host, 1, (_, n) => n + 1) - 1;
        return _answers.TryGetValue(host, out var answer)
            ? Task.FromResult(answer(call))
            : Task.FromException<IPAddress[]>(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound));
    }
}

internal sealed class FixedEgressPolicy(params int[] ports) : IEgressPolicySource
{
    public HashSet<string> Blocked { get; } = new(StringComparer.OrdinalIgnoreCase);

    public EgressPolicy Current => new(new HashSet<int>(ports), Blocked);
}

/// <summary>A scanner with a scripted verdict, counting scans.</summary>
internal sealed class ScriptedScanner(ScanResult result) : IMalwareScanner
{
    public int Scans { get; private set; }

    public Task<ScanResult> ScanAsync(Stream content, string origin, CancellationToken cancellationToken)
    {
        Scans++;
        return Task.FromResult(result);
    }

    public Task<SignatureState> GetSignaturesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SignatureState(SignatureStatus.Fresh, null, TimeSpan.Zero));
}

internal static class ConnectorTestKit
{
    public static IMeterFactory Meters { get; } = new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>();

    public static ConnectorMetrics Metrics() => new(Meters);

    public static EgressGuard Guard(FakeDnsResolver dns, IEgressPolicySource? policy = null, string? allowedPrivateRanges = null) =>
        new(Options.Create(new CrawlOptions { AllowedPrivateRanges = allowedPrivateRanges }), dns, policy ?? new FixedEgressPolicy(), Metrics());
}

/// <summary>
/// The fetch path against an in-process Kestrel test site (connectors design §18: no internet, never umea.se): the site
/// answers on loopback for the host names <c>site.test</c> (HTTP) and <c>secure.test</c> (HTTPS, HTTP/1.1 and HTTP/2,
/// a self-signed certificate the client trusts in tests only). The guard admits loopback only through
/// <c>Crawl:AllowedPrivateRanges = 127.0.0.1/32</c> and the site's ports through the run-time policy, as a deployment
/// would allow an internal host.
/// </summary>
internal sealed class TestSite : IAsyncDisposable
{
    public const string Host = "site.test";
    public const string SecureHost = "secure.test";

    private readonly WebApplication _app;
    private readonly X509Certificate2 _certificate;

    private TestSite(WebApplication app, X509Certificate2 certificate, int port, int securePort, ConcurrentBag<string?> serverNames)
    {
        _app = app;
        _certificate = certificate;
        Port = port;
        SecurePort = securePort;
        ServerNames = serverNames;
    }

    public int Port { get; }

    public int SecurePort { get; }

    /// <summary>Request headers seen by the site, per path (last request wins).</summary>
    public ConcurrentDictionary<string, IHeaderDictionary> Requests { get; } = new(StringComparer.Ordinal);

    /// <summary>SNI names the HTTPS listener was asked for.</summary>
    public ConcurrentBag<string?> ServerNames { get; }

    /// <summary>Protocols the site answered with, per path.</summary>
    public ConcurrentDictionary<string, string> Protocols { get; } = new(StringComparer.Ordinal);

    public Uri Url(string pathAndQuery) => new($"http://{Host}:{Port}{pathAndQuery}");

    public Uri SecureUrl(string pathAndQuery) => new($"https://{SecureHost}:{SecurePort}{pathAndQuery}");

    public static async Task<TestSite> StartAsync(Action<WebApplication> map)
    {
        var certificate = CreateCertificate();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        TestSite? site = null;
        var names = new ConcurrentBag<string?>();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, 0);
            kestrel.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(https => https.ServerCertificateSelector = (_, name) =>
                {
                    names.Add(name);
                    return certificate;
                });
            });
        });
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            // Copied: Kestrel reuses the header collection after the request.
            site!.Requests[context.Request.Path + context.Request.QueryString] = new HeaderDictionary(
                context.Request.Headers.ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase));
            site.Protocols[context.Request.Path] = context.Request.Protocol;
            await next(context);
        });
        map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        var ports = app.Urls.Select(u => new Uri(u)).ToList();
        site = new TestSite(app, certificate, ports.Single(u => u.Scheme == "http").Port, ports.Single(u => u.Scheme == "https").Port, names);
        return site;
    }

    public FakeDnsResolver Resolver() => new FakeDnsResolver().Add(Host, "127.0.0.1").Add(SecureHost, "127.0.0.1");

    /// <summary>
    /// The production fetch path, wired to this site; <paramref name="policy"/> gives the hosts' politeness profiles (User-Agent,
    /// robots.txt). <paramref name="renderer"/> renders <c>browser</c> sources' pages (none: every render is
    /// <c>render.unavailable</c>) under <paramref name="render"/> (default: not configured); <paramref name="budget"/> is the
    /// host budget the rendered pages' requests are paced by.
    /// </summary>
    public FetchPath Fetch(IMalwareScanner? scanner = null, FakeDnsResolver? dns = null, string? allowedPrivateRanges = "127.0.0.1/32", FixedCrawlPolicy? policy = null,
        IRenderer? renderer = null, RenderOptions? render = null, HostBudget? budget = null)
    {
        policy ??= new FixedCrawlPolicy();
        var guard = ConnectorTestKit.Guard(dns ?? Resolver(), new FixedEgressPolicy(Port, SecurePort), allowedPrivateRanges);
        var client = new CrawlHttpClient(guard, (_, certificate, _, _) => certificate?.GetCertHashString() == _certificate.GetCertHashString());
        var fetcher = new HttpFetcher(client, guard, policy, TimeProvider.System);
        var blobRoot = Directory.CreateTempSubdirectory("ume-rag-connector-blobs-");
        var blobs = new FileSystemBlobStore(Options.Create(new BlobStoreOptions { Root = blobRoot.FullName }), TimeProvider.System);
        var robots = new RobotsCache(fetcher, policy, TimeProvider.System);
        budget ??= new HostBudget(new InMemoryCrawlHostStore(TimeProvider.System), policy, TimeProvider.System);
        var renderOptions = Options.Create(render ?? new RenderOptions());
        var metrics = new RenderMetrics(ConnectorTestKit.Meters);
        var fetchers = new RenderFetchers(client, guard, policy, budget, new RenderAssetCache(renderOptions), new RenderBlocklist(renderOptions), TimeProvider.System);
        var pages = new PageFetcher(fetcher, scanner ?? new ScriptedScanner(ScanResult.Clean), renderer ?? new FakeRenderer(), fetchers, policy, metrics, renderOptions,
            TimeProvider.System, NullLogger<PageFetcher>.Instance);
        var connector = new UrlListConnector(pages, robots, blobs);
        return new FetchPath(client, fetcher, robots, pages, connector, blobs, blobRoot, budget, new RenderSlots(renderOptions, policy, metrics), fetchers);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        _certificate.Dispose();
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + SecureHost, key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(SecureHost);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }
}

internal sealed record FetchPath(
    CrawlHttpClient Client,
    HttpFetcher Fetcher,
    RobotsCache Robots,
    PageFetcher Pages,
    UrlListConnector Connector,
    FileSystemBlobStore Blobs,
    DirectoryInfo BlobRoot,
    HostBudget Budget,
    RenderSlots Renders,
    RenderFetchers RenderFetchers) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        Client.Dispose();
        BlobRoot.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }
}
