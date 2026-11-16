using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ume.Rag.TestKit;

/// <summary>
/// A <see cref="WebApplicationFactory{TEntryPoint}"/> host on real Kestrel, over HTTPS on a free loopback port with a
/// self-signed certificate, for what the TestServer does not do: request body limits (it has no
/// <c>IHttpMaxRequestBodySizeFeature</c>, to-verify #32). The port comes from the <c>urls</c> setting:
/// <c>UseKestrel(port)</c> on a factory made by <c>WithWebHostBuilder</c> does not reach the host (the derived factory builds
/// the host through the root factory, whose port is unset), and Kestrel would then bind <c>localhost:5000</c>.
/// </summary>
public sealed class KestrelHost<TEntryPoint> : IAsyncDisposable
    where TEntryPoint : class
{
    private readonly X509Certificate2 _certificate = CreateCertificate();

    public KestrelHost(WebApplicationFactory<TEntryPoint> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Factory = factory.WithWebHostBuilder(web =>
        {
            web.UseSetting(WebHostDefaults.ServerUrlsKey, "https://127.0.0.1:0");
            web.ConfigureKestrel(kestrel => kestrel.ConfigureHttpsDefaults(https => https.ServerCertificate = _certificate));
        });
        Factory.UseKestrel();
        Factory.StartServer();
        Address = new Uri(Factory.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.Single());
    }

    public WebApplicationFactory<TEntryPoint> Factory { get; }

    /// <summary>The bound address, <c>https://127.0.0.1:{port}</c>.</summary>
    public Uri Address { get; }

    /// <summary>A client of the host that trusts its certificate only, keeps cookies and follows no redirects.</summary>
    public HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        SslOptions = { RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate?.GetCertHashString() == _certificate.GetCertHashString() },
    })
    {
        BaseAddress = Address,
        Timeout = TimeSpan.FromMinutes(2),
    };

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        _certificate.Dispose();
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
    }
}
