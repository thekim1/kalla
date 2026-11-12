extern alias fakegateway;

using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Infrastructure.Gateway;
using FakeUsage = fakegateway::Ume.Rag.FakeGateway.FakeUsage;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// B-42 (#580, gateway phase 2; identity §4.7, cost-attribution §4.1): every model call of a chat turn (condense, query
/// embedding, rerank, answer, follow-ups) runs as the assistant's gateway key, and the FakeGateway records it under that key;
/// an assistant without a key runs as rag-api's credential key; a missing echo ends the turn before a byte reaches the client.
/// </summary>
public sealed partial class ChatApiTests
{
    /// <summary>An assigned key of the FakeGateway's integration with no model or residency limit, added for these tests.</summary>
    private static readonly Guid TurnKey = new("019a0000-0000-7000-8000-00000000b0aa");

    /// <summary><see cref="CostFake"/> with <see cref="TurnKey"/> assigned (and <paramref name="dropEcho"/> as an old gateway instance).</summary>
    private WebApplicationFactory<fakegateway::Program> ActingFake(bool dropEcho = false) => CostFake().WithWebHostBuilder(web =>
    {
        web.UseSetting("FakeGateway:AssignedKeys:5:Id", TurnKey.ToString());
        web.UseSetting("FakeGateway:AssignedKeys:5:Name", "Bygglov – chat");
        web.UseSetting("FakeGateway:AssignedKeys:5:TeamId", "019a0000-0000-7000-8000-000000001002");
        web.UseSetting("FakeGateway:DropOnBehalfOfEcho", dropEcho ? "true" : "false");
    });

    /// <summary>
    /// <see cref="CostHost"/> with the assistant's <paramref name="key"/> and the host's context read from <paramref name="gateway"/>,
    /// read once before the turn so the features are current.
    /// </summary>
    private async Task<WebApplicationFactory<Program>> ActingHostAsync(WebApplicationFactory<fakegateway::Program> gateway, Guid? key, bool requireEcho = true)
    {
        var calls = new ConcurrentQueue<CostCall>();
        var host = CostHost(calls, gateway, gatewayKeyId: key).WithWebHostBuilder(web =>
        {
            web.UseSetting("Gateway:RequireDelegationEcho", requireEcho ? "true" : "false");
            web.ConfigureTestServices(services =>
                services.AddHttpClient(GatewayContextReader.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler()));
        });
        (await host.Services.GetRequiredService<GatewayContextReader>().ReadAsync(Ct)).ShouldBeTrue();
        host.Services.GetRequiredService<GatewayFeatures>().ActAsKey.ShouldBeTrue();
        return host;
    }

    [Fact]
    public async Task Every_call_of_a_turn_runs_as_the_assistants_gateway_key()
    {
        await using var gateway = ActingFake();
        await using var api = await ActingHostAsync(gateway, TurnKey);
        using var client = Client(api, db.IntegrationKey);
        var usage = gateway.Services.GetRequiredService<FakeUsage>();
        var before = usage.Records.Count;

        using var response = await AskFollowUp(client, stream: true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await response.Content.ReadAsStringAsync(Ct);
        var records = usage.Records.Skip(before).ToList();
        records.Select(r => r.Endpoint).Distinct().ShouldBe(["/v1/chat/completions", "/v1/embeddings"], ignoreOrder: true);
        records.Select(r => r.Step).ShouldContain("chat-answer");
        records.Count.ShouldBeGreaterThanOrEqualTo(5); // condense, query embedding, rerank, answer, follow-ups
        records.ShouldAllBe(r => r.KeyId == TurnKey); // condense, query embedding, rerank, answer and follow-ups
        records.ShouldAllBe(r => r.ViaKeyId != null);
    }

    [Fact]
    public async Task An_assistant_without_a_key_runs_as_rag_apis_credential_key()
    {
        await using var gateway = ActingFake();
        await using var api = await ActingHostAsync(gateway, key: null);
        using var client = Client(api, db.IntegrationKey);
        var usage = gateway.Services.GetRequiredService<FakeUsage>();
        var before = usage.Records.Count;

        using var response = await AskCost(client, "Behöver jag bygglov för en altan?", stream: false);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var records = usage.Records.Skip(before).ToList();
        records.ShouldNotBeEmpty();
        records.ShouldAllBe(r => r.KeyId != TurnKey && r.ViaKeyId == null); // no acting: the credential key itself
    }

    [Fact]
    public async Task A_missing_echo_ends_the_turn_before_a_byte_reaches_the_client_unless_the_check_is_off()
    {
        await using var gateway = ActingFake(dropEcho: true);
        await using (var api = await ActingHostAsync(gateway, TurnKey))
        {
            using var client = Client(api, db.IntegrationKey);
            using var response = await AskCost(client, "Behöver jag bygglov för en altan?", stream: true);
            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("data:");
        }

        await using var lenient = await ActingHostAsync(gateway, TurnKey, requireEcho: false);
        using var passing = Client(lenient, db.IntegrationKey);
        using var answered = await AskCost(passing, "Behöver jag bygglov för en altan?", stream: true);
        answered.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
