extern alias fakegateway;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Ume.Rag.TestKit;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// The FakeGateway's Integration API (gateway phase 3; gateway-changes §5.6; repo-and-deployment §4.3; B-45): usage per key,
/// step and period from the requests it served, the budgets from its options, <c>usage.read</c> on the admin's credential key
/// only, and both answers held to the pinned contract (<c>IntegrationUsage</c>, <c>IntegrationBudgets</c>).
/// </summary>
public sealed class FakeIntegrationApiTests(FakeGatewayFactory fake) : IClassFixture<FakeGatewayFactory>
{
    private static readonly Guid Chatbot = new("019a0000-0000-7000-8000-00000000b001"); // a dev assigned key (Eu; the FakeGateway's appsettings.json)
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<fakegateway::Program> Gateway() => fake.WithWebHostBuilder(web =>
    {
        web.UseSetting("FakeGateway:CredentialKeys:admin:Scopes:1", "usage.read");
        web.UseSetting("FakeGateway:AssignedKeys:0:Budgets:0:Scope", "Department");
        web.UseSetting("FakeGateway:AssignedKeys:0:Budgets:0:Limit", "1000");
        web.UseSetting("FakeGateway:AssignedKeys:0:Budgets:0:Spent", "1000");
        web.UseSetting("FakeGateway:AssignedKeys:0:Budgets:1:Scope", "VirtualKey");
        web.UseSetting("FakeGateway:AssignedKeys:0:Budgets:1:Limit", "50");
        web.UseSetting("FakeGateway:AssignedKeys:0:Budgets:1:Spent", "12.5");
    });

    private static HttpClient As(WebApplicationFactory<fakegateway::Program> gateway, string key)
    {
        var client = gateway.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    /// <summary>One chat answer through the api's credential key, as <paramref name="actingAs"/> when set, with its step and run id.</summary>
    private static async Task ChatAsync(HttpClient api, Guid? actingAs, string step, Guid runId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("""{"model":"ume/chat-eu","messages":[{"role":"user","content":"Hej på dig"}]}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-ume-max-residency", "Eu");
        request.Headers.Add("x-ume-step", step);
        request.Headers.Add("x-ume-run-id", runId.ToString());
        if (actingAs is { } key)
        {
            request.Headers.Add("x-ume-on-behalf-of", key.ToString());
        }

        using var response = await api.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    private static string Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm")).DateTime)
        .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Usage_per_key_and_step_holds_that_keys_calls_only_by_day_and_by_month()
    {
        await using var gateway = Gateway();
        using var api = As(gateway, FakeGatewayFactory.ApiCredentialKey);
        using var admin = As(gateway, FakeGatewayFactory.AdminCredentialKey);
        var run = Guid.NewGuid();
        await ChatAsync(api, Chatbot, "condense", run);
        await ChatAsync(api, Chatbot, "chat-answer", run);
        await ChatAsync(api, Chatbot, "chat-answer", Guid.NewGuid());
        await ChatAsync(api, null, "chat-answer", Guid.NewGuid()); // the credential key itself: another key's usage
        var served = gateway.Services.GetRequiredService<fakegateway::Ume.Rag.FakeGateway.FakeUsage>().Records;
        var mine = served.Where(r => r.KeyId == Chatbot).ToList();

        var byDay = await admin.GetFromJsonAsync<JsonObject>($"/integration/v1/usage?from={Today}&to={Today}&groupBy=step&keyId={Chatbot}", Ct);
        GatewayContract.Differences(byDay, "IntegrationUsage").ShouldBeEmpty();
        var rows = byDay!["items"]!.AsArray().Select(i => i!.AsObject()).ToList();
        rows.Select(r => r["step"]!.GetValue<string>()).ShouldBe(["chat-answer", "condense"]);
        rows.ShouldAllBe(r => r["keyId"] == null && r["model"] == null); // dimensions not grouped by are null
        var answer = rows.Single(r => r["step"]!.GetValue<string>() == "chat-answer");
        answer["requests"]!.GetValue<long>().ShouldBe(2);
        answer["costUsd"]!.GetValue<decimal>().ShouldBe(mine.Where(r => r.Step == "chat-answer").Sum(r => r.CostUsd));
        rows.Sum(r => r["inputTokens"]!.GetValue<long>()).ShouldBe(mine.Sum(r => r.InputTokens));

        var byMonth = await admin.GetFromJsonAsync<JsonObject>($"/integration/v1/usage?from={Today}&to={Today}&granularity=month&groupBy=key,step&keyId={Chatbot}", Ct);
        GatewayContract.Differences(byMonth, "IntegrationUsage").ShouldBeEmpty();
        byMonth!["from"]!.GetValue<string>().ShouldEndWith("-01"); // a month widens the period to whole months
        var monthly = byMonth["items"]!.AsArray().Select(i => i!.AsObject()).ToList();
        monthly.ShouldAllBe(r => r["keyId"]!.GetValue<Guid>() == Chatbot && r["period"]!.GetValue<string>().EndsWith("-01", StringComparison.Ordinal));
        monthly.Sum(r => r["requests"]!.GetValue<long>()).ShouldBe(3);

        var byRun = await admin.GetFromJsonAsync<JsonObject>($"/integration/v1/usage?from={Today}&to={Today}&groupBy=&keyId={Chatbot}&runId={run}", Ct);
        byRun!["items"]!.AsArray().Single()!["requests"]!.GetValue<long>().ShouldBe(2); // one row per period, the run's calls
    }

    [Fact]
    public async Task Budgets_come_from_the_options_and_an_exhausted_one_says_so()
    {
        await using var gateway = Gateway();
        using var admin = As(gateway, FakeGatewayFactory.AdminCredentialKey);

        var budgets = await admin.GetFromJsonAsync<JsonObject>($"/integration/v1/budgets?keyId={Chatbot}", Ct);

        GatewayContract.Differences(budgets, "IntegrationBudgets").ShouldBeEmpty();
        var items = budgets!["items"]!.AsArray().Select(i => i!.AsObject()).ToList();
        var department = items.Single(b => b["scope"]!.GetValue<string>() == "Department");
        (department["exhausted"]!.GetValue<bool>(), department["percentUsed"]!.GetValue<decimal>()).ShouldBe((true, 100m));
        department["scopeId"]!.GetValue<Guid>().ShouldNotBe(Guid.Empty); // the key's team's department
        var own = items.Single(b => b["scope"]!.GetValue<string>() == "VirtualKey");
        (own["exhausted"]!.GetValue<bool>(), own["percentUsed"]!.GetValue<decimal>(), own["scopeId"]!.GetValue<Guid>()).ShouldBe((false, 25m, Chatbot));
    }

    [Fact]
    public async Task Only_a_credential_key_with_usage_read_may_read_and_the_parameters_are_checked()
    {
        await using var gateway = Gateway();
        var period = $"from={Today}&to={Today}";
        using (var none = gateway.CreateClient())
        {
            (await none.GetAsync($"/integration/v1/usage?{period}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        using var dev = As(gateway, FakeGatewayFactory.EuKey); // not a credential key
        (await dev.GetAsync($"/integration/v1/usage?{period}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var api = As(gateway, FakeGatewayFactory.ApiCredentialKey); // a credential key without usage.read
        (await api.GetAsync($"/integration/v1/usage?{period}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await api.GetAsync($"/integration/v1/budgets?keyId={Chatbot}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using var admin = As(gateway, FakeGatewayFactory.AdminCredentialKey);
        (await admin.GetAsync("/integration/v1/usage?from=2025-01-01&to=2026-01-02", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest); // 367 days
        (await admin.GetAsync($"/integration/v1/usage?{period}&groupBy=team", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.GetAsync($"/integration/v1/usage?{period}&keyId={Guid.NewGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.GetAsync($"/integration/v1/budgets?keyId={Guid.NewGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await admin.GetAsync("/integration/v1/usage?from=2025-01-01&to=2025-12-31", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK); // 365 days
    }
}
