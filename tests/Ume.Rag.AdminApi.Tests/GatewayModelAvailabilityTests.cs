extern alias fakegateway;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Ume.Rag.Domain.Admin;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.TestKit;
using FakeGatewayProgram = fakegateway::Program;

namespace Ume.Rag.AdminApi.Tests;

/// <summary>
/// The model pickers' availability flag and V32 at the gateway (Decisions "Model pickers show what the key may use", #447;
/// admin design §4.5 <c>GET /api/gateway/models?level=</c>, §4.6 V32).
/// </summary>
[Collection(AdminDatabaseGroup2.Name)]
public sealed class GatewayModelAvailabilityTests(AdminDatabaseFixture database)
{
    private const string AdminKey = "fake-eu-key-availability-0123456"; // rag-admin\'s credential key
    private const string Listed = "ume/chat-eu"; // the FakeGateway serves it at Eu
    private const string ListedRerank = "bge-reranker-v2-m3"; // served while FakeGateway:RerankEnabled
    private const string ExternalOnly = "ume/chat-advanced"; // served only at External

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Counts the gateway's <c>/v1/models</c> calls on the way to the FakeGateway.</summary>
    private sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public int ModelLists;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/v1/models", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref ModelLists);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }

    private static WebApplicationFactory<FakeGatewayProgram> Gateway() => new WebApplicationFactory<FakeGatewayProgram>().WithWebHostBuilder(web =>
    {
        web.UseEnvironment("Development");
        web.UseSetting("FakeGateway:CredentialKeys:admin:Key", AdminKey);
        web.UseSetting("FakeGateway:RerankEnabled", "true");
    });

    private WebApplicationFactory<Program> Admin(WebApplicationFactory<FakeGatewayProgram> gateway, CountingHandler? counter = null, TimeProvider? time = null) =>
        database.HeaderFactory().WithWebHostBuilder(web =>
        {
            web.UseSetting("Gateway:BaseUrl", "http://fake-gateway/");
            web.UseSetting("Gateway:Key", AdminKey);
            web.ConfigureTestServices(services =>
            {
                services.UseGatewayCredentialCeiling();
                services.AddHttpClient(GatewayApi.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => counter ?? (HttpMessageHandler)gateway.Server.CreateHandler());
                if (time is not null)
                {
                    services.AddSingleton(time);
                }
            });
        });

    private async Task<string> MirrorAsync()
    {
        var unlisted = "unlisted-chat-" + Guid.NewGuid().ToString("N")[..8];
        await using var db = TestPostgres.Context(database.Connection(RagDatabase.Admin));
        foreach (var (name, kind, residency) in new[]
                 {
                     (Listed, GatewayModelModelKinds.Chat, "Eu"), (ListedRerank, GatewayModelModelKinds.Rerank, "Eu"),
                     (ExternalOnly, GatewayModelModelKinds.Chat, "External"), (unlisted, GatewayModelModelKinds.Chat, "Eu"),
                 })
        {
            if (!await db.GatewayModels.AnyAsync(m => m.Name == name, Ct))
            {
                db.GatewayModels.Add(new GatewayModel { Name = name, ModelKind = kind, Residencies = [residency], Source = GatewayModelSources.Catalog });
            }
        }

        await db.SaveChangesAsync(Ct);
        return unlisted;
    }

    private static async Task<Dictionary<string, bool?>> AvailableAsync(HttpClient admin, string query = "")
    {
        var rows = await admin.GetFromJsonAsync<JsonElement>("/api/gateway/models" + query, Ct);
        return rows.EnumerateArray().ToDictionary(r => r.GetProperty("name").GetString()!,
            r => r.GetProperty("available").ValueKind == JsonValueKind.Null ? (bool?)null : r.GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task Each_mirror_model_says_whether_the_platforms_key_may_use_it_at_the_ceiling()
    {
        var unlisted = await MirrorAsync();
        await using var gateway = Gateway();
        await using var factory = Admin(gateway);
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);

        // No level: the platform ceiling (Eu).
        var platform = await AvailableAsync(admin);
        platform[Listed].ShouldBe(true);
        platform[ListedRerank].ShouldBe(true);
        platform[unlisted].ShouldBe(false);
        platform[ExternalOnly].ShouldBe(false);

        // An assistant level's ceiling: public is External, where the External-only model is listed too.
        var publicLevel = await AvailableAsync(admin, "?level=public");
        publicLevel[ExternalOnly].ShouldBe(true);
        publicLevel[unlisted].ShouldBe(false);

        using var bad = await admin.GetAsync("/api/gateway/models?level=secret", Ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Availability_is_unknown_when_the_gateway_cannot_be_asked()
    {
        await MirrorAsync();
        await using var factory = database.HeaderFactory(); // no gateway configured
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        (await AvailableAsync(admin)).Values.ShouldAllBe(v => v == null);
    }

    [Fact]
    public async Task The_gateways_list_is_cached_five_minutes_per_ceiling()
    {
        await MirrorAsync();
        await using var gateway = Gateway();
        var counter = new CountingHandler(gateway.Server.CreateHandler());
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = Admin(gateway, counter, time);
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);

        await AvailableAsync(admin);
        await AvailableAsync(admin);
        counter.ModelLists.ShouldBe(1);
        await AvailableAsync(admin, "?level=public"); // another ceiling, another list
        counter.ModelLists.ShouldBe(2);
        time.Advance(GatewayModelAvailability.CacheFor);
        await AvailableAsync(admin);
        counter.ModelLists.ShouldBe(3);
    }

    [Fact]
    public async Task V32_refuses_a_model_the_gateway_does_not_list_and_saves_one_it_lists()
    {
        var unlisted = await MirrorAsync();
        await using var gateway = Gateway();
        await using var factory = Admin(gateway);
        using var admin = await AdminDatabaseFixture.UserAsync(factory, RagRoles.PlatformAdmin);
        var evaluation = await admin.GetFromJsonAsync<JsonElement>("/api/settings/evaluation", Ct);
        var retrieval = await admin.GetFromJsonAsync<JsonElement>("/api/settings/retrieval", Ct);
        var version = evaluation.GetProperty("version").GetString();

        // A chat model within Eu in the mirror that the key may not use: 409 model_not_listed.
        using (var refused = await admin.PutAsJsonAsync("/api/settings/evaluation", new { evaluationModel = unlisted, evaluationMaxQuestions = 100, version }, Ct))
        {
            refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
            (await refused.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("code").GetString().ShouldBe(AdminErrorCodes.ModelNotListed.Code);
        }

        // Listed models save; then the shared database gets its settings back.
        using var saved = await admin.PutAsJsonAsync("/api/settings/evaluation", new { evaluationModel = Listed, evaluationJudgeModel = Listed, evaluationMaxQuestions = 100, version }, Ct);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Ct));
        version = (await saved.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("version").GetString();
        using var rerank = await admin.PutAsJsonAsync("/api/settings/retrieval", new { value = new { rerankModel = Listed, gatewayRerankModel = ListedRerank }, version }, Ct);
        rerank.StatusCode.ShouldBe(HttpStatusCode.OK, await rerank.Content.ReadAsStringAsync(Ct));
        version = (await rerank.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("version").GetString();
        using var back = await admin.PutAsJsonAsync("/api/settings/retrieval", new { value = retrieval.GetProperty("value"), version }, Ct);
        version = (await back.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("version").GetString();
        (await admin.PutAsJsonAsync("/api/settings/evaluation", new
        {
            evaluationModel = evaluation.GetProperty("evaluationModel").GetString(), evaluationJudgeModel = evaluation.GetProperty("evaluationJudgeModel").GetString(),
            evaluationMaxQuestions = evaluation.GetProperty("evaluationMaxQuestions").GetInt32(), version,
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
