extern alias fakegateway;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Kalla.Domain.Admin;
using Kalla.Domain.Entities;
using Kalla.Infrastructure.Gateway;
using Kalla.TestKit;
using FakeGatewayProgram = fakegateway::Program;

namespace Kalla.AdminApi.Tests;

/// <summary>
/// B-45 (gateway phase 3; gateway-changes §5.6; admin design §4.5, §8.6): rag-admin reads a key's usage per step and its budgets
/// from the gateway's Integration API with its credential key (<c>usage.read</c>), cached 5 minutes per key and query, at the
/// address its gateway context names (<c>integration_api_url</c>, M-17 C11); without one the routes answer null without a
/// call; a refusal is null with its reason on the status.
/// </summary>
[Collection(AdminDatabaseGroup3.Name)]
public sealed class IntegrationReadbackTests(AdminDatabaseFixture database)
{
    private const string AdminKey = "sk-ume-readback-admin-00000000000000";
    private const string ApiKey = "sk-ume-readback-api-0000000000000000";
    private static readonly Guid Chatbot = new("019a0000-0000-7000-8000-00000000b001"); // a dev assigned key of the FakeGateway
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private int _integrationCalls;

    /// <summary>The FakeGateway; <paramref name="integrationApiUrl"/> false: its context names no Integration API address.</summary>
    private static WebApplicationFactory<FakeGatewayProgram> Gateway(bool usageRead = true, bool integrationApiUrl = true) => new WebApplicationFactory<FakeGatewayProgram>().WithWebHostBuilder(web =>
    {
        web.UseEnvironment("Development");
        if (!integrationApiUrl)
        {
            web.UseSetting("FakeGateway:Gateway:IntegrationApiUrl", "");
        }

        web.UseSetting("FakeGateway:CredentialKeys:admin:Key", AdminKey);
        web.UseSetting("FakeGateway:CredentialKeys:admin:Scopes:0", usageRead ? "usage.read" : "organisation.read");
        web.UseSetting("FakeGateway:CredentialKeys:api:Key", ApiKey);
        web.UseSetting("FakeGateway:AssignedKeys:0:Budgets:0:Scope", "Department");
        web.UseSetting("FakeGateway:AssignedKeys:0:Budgets:0:Limit", "1000");
        web.UseSetting("FakeGateway:AssignedKeys:0:Budgets:0:Spent", "1000");
    });

    /// <summary>rag-admin with its context read once from <paramref name="gateway"/>, which names the Integration API's address (M-17 C11).</summary>
    private async Task<WebApplicationFactory<Program>> AdminAsync(WebApplicationFactory<FakeGatewayProgram> gateway, HttpMessageHandler? integration = null)
    {
        var factory = database.HeaderFactory().WithWebHostBuilder(web =>
        {
            web.UseSetting("Gateway:BaseUrl", "http://fake-gateway/");
            web.UseSetting("Gateway:Key", AdminKey);
            web.ConfigureTestServices(services =>
            {
                services.AddHttpClient(GatewayContextReader.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler());
                services.AddHttpClient(IntegrationApiClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new Counting(integration ?? gateway.Server.CreateHandler(), () => Interlocked.Increment(ref _integrationCalls)));
            });
        });
        (await factory.Services.GetRequiredService<GatewayContextReader>().ReadAsync(Ct)).ShouldBeTrue();
        return factory;
    }

    /// <summary>An Integration API that answers every call with 503.</summary>
    private sealed class Unavailable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed class Counting(HttpMessageHandler inner, Action counted) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            counted();
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>A department with the dev key <see cref="Chatbot"/> in its mirror.</summary>
    private async Task<string> SeedAsync()
    {
        var code = "I" + Random.Shared.Next(100_000, 999_999);
        await using var db = TestPostgres.Context(database.Connection(null));
        var department = new Department { Id = Guid.CreateVersion7(), Name = "Readback " + code, CostCenterCode = code, SyncedAt = DateTimeOffset.UtcNow };
        db.Add(department);
        await db.GatewayKeys.Where(k => k.Id == Chatbot).ExecuteDeleteAsync(Ct);
        db.GatewayKeys.Add(new GatewayKey
        {
            Id = Chatbot, Name = "Kontaktcenter – chatbot", TeamId = Guid.CreateVersion7(), DepartmentId = department.Id, Status = "Active", OwnerActive = true,
            Usable = true, PiiPolicy = "Off", ContentGuardPolicy = "Off", SyncedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
        return code;
    }

    /// <summary>A chat answer on the fake as <see cref="Chatbot"/> through rag-api's credential key, with <paramref name="step"/>.</summary>
    private static async Task ChatAsync(WebApplicationFactory<FakeGatewayProgram> gateway, string step)
    {
        using var client = gateway.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("""{"model":"ume/chat-eu","messages":[{"role":"user","content":"Hej"}]}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        request.Headers.Add("x-ume-max-residency", "Eu");
        request.Headers.Add("x-ume-step", step);
        request.Headers.Add("x-ume-on-behalf-of", Chatbot.ToString());
        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    private static string Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm")).DateTime)
        .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_keys_usage_per_step_and_its_budgets_come_from_the_gateway_and_a_second_read_is_cached()
    {
        var code = await SeedAsync();
        await using var gateway = Gateway();
        await ChatAsync(gateway, "condense");
        await ChatAsync(gateway, "chat-answer");
        await ChatAsync(gateway, "chat-answer");
        var served = gateway.Services.GetRequiredService<fakegateway::Kalla.FakeGateway.FakeUsage>().Records.Where(r => r.KeyId == Chatbot).ToList();
        await using var factory = await AdminAsync(gateway);
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, code);

        var usage = await admin.GetFromJsonAsync<JsonElement>($"/api/gateway/keys/{Chatbot}/usage?from={Today}&to={Today}", Ct);
        var rows = usage.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("step").GetString()!);
        rows.Keys.Order().ShouldBe(["chat-answer", "condense"]);
        rows["chat-answer"].GetProperty("requests").GetInt64().ShouldBe(2);
        rows["chat-answer"].GetProperty("costUsd").GetDecimal().ShouldBe(served.Where(r => r.Step == "chat-answer").Sum(r => r.CostUsd));

        var byMonth = await admin.GetFromJsonAsync<JsonElement>($"/api/gateway/keys/{Chatbot}/usage?from={Today}&to={Today}&granularity=month", Ct);
        byMonth.GetProperty("granularity").GetString().ShouldBe("month");
        byMonth.GetProperty("rows").EnumerateArray().Sum(r => r.GetProperty("requests").GetInt64()).ShouldBe(3);

        var budgets = (await admin.GetFromJsonAsync<JsonElement[]>($"/api/gateway/keys/{Chatbot}/budget", Ct))!;
        var department = budgets.Single();
        (department.GetProperty("scope").GetString(), department.GetProperty("unit").GetString(), department.GetProperty("exhausted").GetBoolean())
            .ShouldBe(("department", "usd", true));

        // Cached 5 minutes per key and query: the same reads make no further call.
        var calls = _integrationCalls;
        await admin.GetFromJsonAsync<JsonElement>($"/api/gateway/keys/{Chatbot}/usage?from={Today}&to={Today}", Ct);
        await admin.GetFromJsonAsync<JsonElement[]>($"/api/gateway/keys/{Chatbot}/budget", Ct);
        _integrationCalls.ShouldBe(calls);

        // The key's department in scope, else 404 before any gateway call; the period at most 366 days.
        using var stranger = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, "Z" + code);
        (await stranger.GetAsync($"/api/gateway/keys/{Chatbot}/budget", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var tooLong = await admin.GetAsync($"/api/gateway/keys/{Chatbot}/usage?from=2025-01-01&to=2026-01-02", Ct);
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await tooLong.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe("period_too_long");
        _integrationCalls.ShouldBe(calls);
    }

    [Fact]
    public async Task Without_an_address_in_the_context_the_routes_answer_null_without_a_call_and_a_missing_scope_is_shown_on_the_status()
    {
        var code = await SeedAsync();
        await using (var gateway = Gateway(integrationApiUrl: false))
        await using (var unset = await AdminAsync(gateway))
        {
            unset.Services.GetRequiredService<IntegrationApiClient>().Configured.ShouldBeFalse();
            using var admin = await AdminDatabaseFixture.UserAsync(unset, RagRoles.DepartmentAdmin, code);
            (await admin.GetStringAsync($"/api/gateway/keys/{Chatbot}/budget", Ct)).ShouldBe("null");
            (await admin.GetStringAsync($"/api/gateway/keys/{Chatbot}/usage", Ct)).ShouldBe("null");
            _integrationCalls.ShouldBe(0);
        }

        await using var withoutScope = Gateway(usageRead: false);
        await using var factory = await AdminAsync(withoutScope);
        using var departmentAdmin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, code);
        (await departmentAdmin.GetStringAsync($"/api/gateway/keys/{Chatbot}/budget", Ct)).ShouldBe("null");
        using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var status = await platform.GetFromJsonAsync<JsonElement>("/api/gateway/status", Ct);
        var integration = status.GetProperty("integration");
        integration.GetProperty("configured").GetBoolean().ShouldBeTrue();
        (integration.GetProperty("lastError").GetProperty("reason").GetString(), integration.GetProperty("lastError").GetProperty("status").GetInt32())
            .ShouldBe(("scope_missing", 403));

        // #642: the organisation sync's status carries the same state, for the platform's Gateway view (no key checks).
        var sync = (await platform.GetFromJsonAsync<JsonElement>("/api/organisation/sync", Ct)).GetProperty("integration");
        (sync.GetProperty("configured").GetBoolean(), sync.GetProperty("lastError").GetProperty("reason").GetString()).ShouldBe((true, "scope_missing"));
    }

    [Fact]
    public async Task A_refused_read_back_is_sent_once_and_answers_null_with_its_reason()
    {
        // #701: the standard resilience handler is off this client, so a 503 is not retried (3 attempts spent the API's budget).
        var code = await SeedAsync();
        await using var gateway = Gateway();
        using var unavailable = new Unavailable();
        await using var factory = await AdminAsync(gateway, unavailable);
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.DepartmentAdmin, code);

        (await admin.GetStringAsync($"/api/gateway/keys/{Chatbot}/budget", Ct)).ShouldBe("null");

        _integrationCalls.ShouldBe(1);
        using var platform = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var error = (await platform.GetFromJsonAsync<JsonElement>("/api/gateway/status", Ct)).GetProperty("integration").GetProperty("lastError");
        (error.GetProperty("reason").GetString(), error.GetProperty("status").GetInt32()).ShouldBe(("failed", 503));
        factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(IntegrationApiClient.HttpClientName).Timeout.ShouldBe(IntegrationApiClient.Timeout);
    }

    [Theory]
    [InlineData("https://adminapi:8443/integration/v1", "https://gateway:8443", true)]
    [InlineData("https://adminapi:8443/integration/v1", "http://fake-gateway/", true)]
    [InlineData("http://adminapi:8080/integration/v1", "https://gateway:8443", false)]
    [InlineData("http://fake-gateway/integration/v1", "http://fake-gateway/", true)]
    [InlineData("ftp://adminapi/integration/v1", "http://fake-gateway/", false)]
    [InlineData("/integration/v1", "https://gateway:8443", false)]
    [InlineData(null, "https://gateway:8443", false)]
    public void The_credential_key_goes_only_to_https_unless_the_gateway_itself_is_http(string? address, string gatewayBaseUrl, bool usable) =>
        IntegrationApiClient.Usable(address, gatewayBaseUrl).ShouldBe(usable);
}
