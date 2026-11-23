extern alias fakegateway;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Kalla.Infrastructure.Gateway;
using FakeGatewayOptions = fakegateway::Kalla.FakeGateway.FakeGatewayOptions;
using FakeGatewayProgram = fakegateway::Program;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// The organisation's logo from the gateway's admin console (<see cref="GatewayBranding"/>): read from the Integration API
/// (feature <c>organisation-logo</c>) with kalla-admin's credential key, named in the user document as <c>logoUrl</c> before sign-in
/// and served from kalla-admin's own origin; none without the feature, an address or a logo, and a refused image is never served.
/// </summary>
[Collection(AdminDatabaseGroup5.Name)]
public sealed class GatewayBrandingTests(AdminDatabaseFixture database)
{
    private const string ContextKey = "sk-ume-bff-branding-000000000000000";
    private static readonly byte[] Svg = Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><rect width="10" height="10" fill="#0a6"/></svg>""");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private int _logoReads;
    private Exception? _logoFailure;

    /// <summary>kalla-admin with its context read once from a FakeGateway whose admin console has <paramref name="logo"/>.</summary>
    private async Task<(WebApplicationFactory<FakeGatewayProgram> Gateway, WebApplicationFactory<Program> Admin)> StartAsync(byte[]? logo, bool feature = true)
    {
        var gateway = new WebApplicationFactory<FakeGatewayProgram>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");
            web.UseSetting("FakeGateway:CredentialKeys:admin:Key", ContextKey);
            if (logo is not null)
            {
                web.UseSetting("FakeGateway:Gateway:OrganisationLogo", Convert.ToBase64String(logo));
            }

            if (!feature)
            {
                // A gateway up to v0.12.2 (the binder appends to a list with a default, so the feature is removed here).
                web.ConfigureTestServices(services => services.PostConfigure<FakeGatewayOptions>(o => o.Gateway.Features.Remove(GatewayFeatures.OrganisationLogoFeature)));
            }
        });
        var admin = database.Factory().WithWebHostBuilder(web =>
        {
            web.UseSetting("Gateway:BaseUrl", "http://fake-gateway/");
            web.UseSetting("Gateway:Key", ContextKey);
            web.ConfigureTestServices(services =>
            {
                services.AddHttpClient(GatewayContextReader.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());
                services.AddHttpClient(GatewayBranding.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new Counting(gateway.Server.CreateHandler(), () => { Interlocked.Increment(ref _logoReads); return _logoFailure; }));
            });
        });
        (await admin.Services.GetRequiredService<GatewayContextReader>().ReadAsync(Ct)).ShouldBeTrue();
        return (gateway, admin);
    }

    private static async Task<JsonElement> LogoUrlAsync(HttpClient client)
    {
        using var json = JsonDocument.Parse(await client.GetStringAsync("/bff/user", Ct));
        return json.RootElement.GetProperty("logoUrl").Clone();
    }

    [Fact]
    public async Task The_gateways_logo_is_named_before_sign_in_and_served_from_this_origin()
    {
        var (gateway, admin) = await StartAsync(Svg);
        await using var _ = gateway;
        await using var __ = admin;
        using var client = AdminDatabaseFixture.HttpsClient(admin);

        var url = (await LogoUrlAsync(client)).GetString()!;
        url.ShouldStartWith(GatewayBranding.LogoPath + "?v=");

        using var image = await client.GetAsync(url, Ct);
        image.StatusCode.ShouldBe(HttpStatusCode.OK);
        image.Content.Headers.ContentType!.MediaType.ShouldBe("image/svg+xml");
        (await image.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(Svg);
        image.Headers.CacheControl!.ToString().ShouldContain("immutable");
        image.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("sandbox");

        // Held for a while: the page loads above asked the gateway once.
        _logoReads.ShouldBe(1);
        using var stale = await client.GetAsync(GatewayBranding.LogoPath + "?v=0000", Ct);
        stale.Headers.CacheControl!.NoCache.ShouldBeTrue();
    }

    [Fact]
    public async Task Without_a_logo_or_the_feature_the_admin_ui_shows_its_own_mark()
    {
        foreach (var (logo, feature) in new[] { ((byte[]?)null, true), (Svg, false) })
        {
            var (gateway, admin) = await StartAsync(logo, feature);
            await using var _ = gateway;
            await using var __ = admin;
            using var client = AdminDatabaseFixture.HttpsClient(admin);

            (await LogoUrlAsync(client)).ValueKind.ShouldBe(JsonValueKind.Null);
            (await client.GetAsync(GatewayBranding.LogoPath, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task An_image_that_is_not_of_an_accepted_type_is_never_served()
    {
        // HTML that a misbehaving gateway calls an SVG: it has no <svg element, so it is refused.
        var (gateway, admin) = await StartAsync(Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"));
        await using var _ = gateway;
        await using var __ = admin;
        using var client = AdminDatabaseFixture.HttpsClient(admin);

        (await LogoUrlAsync(client)).ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, "image/webp")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38 }, null)] // GIF: the gateway does not take it
    public void The_type_is_detected_from_the_content_as_the_gateway_does(byte[] data, string? expected) =>
        GatewayBranding.Detect(data).ShouldBe(expected);

    [Fact]
    public async Task A_logo_read_that_fails_in_any_way_never_fails_the_user_document()
    {
        // A plain OperationCanceledException (a body read's own timeout) and an unexpected fault: neither is the caller's.
        foreach (var failure in new Exception[] { new OperationCanceledException(), new InvalidOperationException("unexpected") })
        {
            _logoFailure = failure;
            _logoReads = 0;
            var (gateway, admin) = await StartAsync(Svg);
            await using var _ = gateway;
            await using var __ = admin;
            using var client = AdminDatabaseFixture.HttpsClient(admin);

            (await LogoUrlAsync(client)).ValueKind.ShouldBe(JsonValueKind.Null);
            (await LogoUrlAsync(client)).ValueKind.ShouldBe(JsonValueKind.Null);
            _logoReads.ShouldBe(1); // the failure is held like an answer
        }
    }

    /// <summary>Counts the logo reads; throws what <c>counted</c> returns, when it returns something.</summary>
    private sealed class Counting(HttpMessageHandler inner, Func<Exception?> counted) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            counted() is { } failure ? Task.FromException<HttpResponseMessage>(failure) : base.SendAsync(request, cancellationToken);
    }
}
