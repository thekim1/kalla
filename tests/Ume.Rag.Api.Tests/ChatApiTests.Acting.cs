extern alias fakegateway;

using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Ume.Rag.Domain.Access;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.TestKit;
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
    private WebApplicationFactory<fakegateway::Program> ActingFake(bool dropEcho = false, string? budgetExceeded = null) => CostFake().WithWebHostBuilder(web =>
    {
        web.UseSetting("FakeGateway:AssignedKeys:5:Id", TurnKey.ToString());
        web.UseSetting("FakeGateway:AssignedKeys:5:Name", "Bygglov – chat");
        web.UseSetting("FakeGateway:AssignedKeys:5:TeamId", "019a0000-0000-7000-8000-000000001002");
        web.UseSetting("FakeGateway:DropOnBehalfOfEcho", dropEcho ? "true" : "false");
        if (budgetExceeded is not null)
        {
            web.UseSetting("FakeGateway:AssignedKeys:5:BudgetExceeded", budgetExceeded);
        }
    });

    /// <summary>
    /// <see cref="CostHost"/> with the assistant's <paramref name="key"/> and the host's context read from <paramref name="gateway"/>,
    /// read once before the turn so the features are current.
    /// </summary>
    private async Task<WebApplicationFactory<Program>> ActingHostAsync(WebApplicationFactory<fakegateway::Program> gateway, Guid? key, bool requireEcho = true,
        GatewayKeyEntry? keyRow = null)
    {
        var calls = new ConcurrentQueue<CostCall>();
        var host = CostHost(calls, gateway, gatewayKeyId: key, keyRow: keyRow).WithWebHostBuilder(web =>
        {
            web.UseSetting("Gateway:RequireDelegationEcho", requireEcho ? "true" : "false");
            web.ConfigureTestServices(services =>
                services.UseGatewayCredentialCeiling().AddHttpClient(GatewayContextReader.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => gateway.Server.CreateHandler()));
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
            // §3.6: a failed key-echo check is 502 upstream_error (B-44; it was the connection failure's 503 before).
            response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain("data:");
        }

        await using var lenient = await ActingHostAsync(gateway, TurnKey, requireEcho: false);
        using var passing = Client(lenient, db.IntegrationKey);
        using var answered = await AskCost(passing, "Behöver jag bygglov för en altan?", stream: true);
        answered.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>The day's <c>Unavailable</c> total of the integration client's turns to the assistant, after the writer's flush.</summary>
    private async Task<long> UnavailableTodayAsync(WebApplicationFactory<Program> api)
    {
        await api.Services.GetRequiredService<ConversationWriter>().FlushAsync(Ct);
        await using var connection = new Npgsql.NpgsqlConnection(db.Connection(RagDatabase.Api));
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("""
            SELECT coalesce(sum("Unavailable"), 0) FROM chat."AssistantDailyTotals" WHERE "AssistantId" = $1 AND "ClientId" = $2
            """, connection) { Parameters = { new() { Value = db.AssistantId }, new() { Value = db.IntegrationClient } } };
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task A_spent_department_budget_is_402_with_the_gateways_message_before_the_first_byte_and_an_unavailable_turn()
    {
        // B-44 (chat §3.6): the gateway names the scope in its message, which is passed on.
        await using var gateway = ActingFake(budgetExceeded: "Department");
        await using var api = await ActingHostAsync(gateway, TurnKey);
        using var client = Client(api, db.IntegrationKey);
        var before = await UnavailableTodayAsync(api);

        using var response = await AskCost(client, "Behöver jag bygglov för en altan?", stream: true);

        response.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain("data:");
        var error = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("budget_exceeded");
        error.GetProperty("message").GetString()!.ShouldStartWith("The department's budget");
        (await UnavailableTodayAsync(api)).ShouldBe(before + 1);
    }

    [Fact]
    public async Task A_disabled_key_in_the_mirror_is_503_assistant_unavailable_before_any_model_call()
    {
        await using var gateway = ActingFake();
        await using var api = await ActingHostAsync(gateway, TurnKey, keyRow: new GatewayKeyEntry(TurnKey, "Disabled", OwnerActive: true, Usable: true));
        using var client = Client(api, db.IntegrationKey);
        var usage = gateway.Services.GetRequiredService<FakeUsage>();
        var served = usage.Records.Count;
        var before = await UnavailableTodayAsync(api);

        using var response = await AskCost(client, "Behöver jag bygglov för en altan?", stream: false);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var error = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("error");
        (error.GetProperty("code").GetString(), error.GetProperty("message").GetString())
            .ShouldBe(("assistant_unavailable", "The assistant is unavailable: its gateway key is disabled."));
        error.GetProperty("message").GetString()!.ShouldNotContain("Bygglov – chat"); // never the key's name
        usage.Records.Count.ShouldBe(served); // the FakeGateway saw no call
        (await UnavailableTodayAsync(api)).ShouldBe(before + 1);
    }

    [Fact]
    public async Task A_key_the_gateway_refuses_while_the_mirror_shows_it_active_is_503_assistant_unavailable()
    {
        // The mirror lags the gateway: the id is no longer assigned to the integration (403 delegation_not_allowed).
        var unassigned = Guid.NewGuid();
        await using var gateway = ActingFake();
        await using var api = await ActingHostAsync(gateway, unassigned);
        using var client = Client(api, db.IntegrationKey);

        using var response = await AskCost(client, "Behöver jag bygglov för en altan?", stream: true);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain("data:");
        System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("assistant_unavailable");
    }
}
