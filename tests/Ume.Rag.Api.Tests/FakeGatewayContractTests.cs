using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Ume.Rag.Domain;
using Ume.Rag.TestKit;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// B-35 (#580): the FakeGateway's discovery answers hold to the gateway's published integration contract
/// (<c>contracts/gateway/integrations.json</c>, checked as the gateway's own tests do: an undescribed field or a missing
/// required one is a difference), and behave as the gateway's (<c>ContextEndpoint</c>, <c>ModelListEndpoint</c>,
/// <c>CacheableJson</c> at thekim1/llmgateway <c>v0.10.0</c>): the organisation and member groups under <c>organisation.read</c>, revoked keys left out,
/// <c>usable</c> against the presenting credential key, and an <c>ETag</c> with an empty <c>304</c>.
/// </summary>
public sealed class FakeGatewayContractTests(FakeGatewayFactory fake) : IClassFixture<FakeGatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(FakeGatewayFactory.AdminCredentialKey)]
    [InlineData(FakeGatewayFactory.WorkerCredentialKey)]
    [InlineData(FakeGatewayFactory.ApiCredentialKey)]
    [InlineData(FakeGatewayFactory.EuKey)]
    public async Task The_context_conforms_to_the_contract(string key)
    {
        var context = await ContextAsync(key);

        GatewayContract.Differences(context, "UmeContext").ShouldBeEmpty();
    }

    [Fact]
    public async Task The_admins_key_with_organisation_read_gets_the_whole_organisation_and_the_integrations_keys()
    {
        var context = await ContextAsync(FakeGatewayFactory.AdminCredentialKey);

        context["key"]!["integration_role"]!.GetValue<string>().ShouldBe("Credential");
        context["key"]!["integration_scopes"]!.AsArray().Select(s => s!.GetValue<string>()).ShouldBe(["organisation.read"]);
        context["integration"]!["name"]!.GetValue<string>().ShouldBe("Ume.Rag");
        context["gateway"]!["features"]!.AsArray().Select(f => f!.GetValue<string>()).ShouldContain("organisation");
        context["gateway"]!["features"]!.AsArray().Select(f => f!.GetValue<string>()).ShouldContain("member-groups");
        context["gateway"]!["features"]!.AsArray().Select(f => f!.GetValue<string>()).ShouldContain("organisation-name");
        context["gateway"]!["organisation_name"]!.GetValue<string>().ShouldBe("Umeå kommun"); // gateway v0.10.2
        context["gateway"]!["features"]!.AsArray().Select(f => f!.GetValue<string>()).ShouldContain("integration-api-url");
        context["gateway"]!["integration_api_url"]!.GetValue<string>().ShouldEndWith("/integration/v1"); // the fake's own

        var departments = context["departments"]!.AsArray();
        departments.Count.ShouldBe(4);
        departments.ShouldContain(d => d!["name"]!.GetValue<string>() == "Överförmyndarförvaltningen" && !d["is_active"]!.GetValue<bool>());
        departments.ShouldAllBe(d => d!["member_groups"] is JsonArray); // every department carries its member groups (v0.10.0)
        departments.Single(d => d!["cost_center_code"]!.GetValue<string>() == "3000")!["member_groups"]!.AsArray().Select(g => g!.GetValue<string>())
            .ShouldBe(["Byggnadsnämnden"]);
        context["teams"]!.AsArray().Count.ShouldBe(5);

        var keys = context["assigned_keys"]!.AsArray().ToDictionary(k => k!["name"]!.GetValue<string>());
        keys.Keys.ShouldNotContain("Revoked – not listed");
        keys["Bokning – paused"]!["status"]!.GetValue<string>().ShouldBe("Disabled");
        keys["Kontaktcenter – chatbot"]!["department_id"]!.GetValue<Guid>().ShouldNotBe(Guid.Empty);
        keys["Webb – external only"]!["usable"]!.GetValue<bool>().ShouldBeTrue(); // the admin's key allows every residency
    }

    [Fact]
    public async Task Without_organisation_read_only_the_keys_teams_and_departments_are_listed_and_usable_follows_the_key()
    {
        var context = await ContextAsync(FakeGatewayFactory.WorkerCredentialKey);

        context["key"]!["allowed_residencies"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe(["OnPrem", "Eu"]);
        context["departments"]!.AsArray().Select(d => d!["name"]!.GetValue<string>())
            .ShouldBe(["Fritidsförvaltningen", "Individ- och familjeomsorgen", "Kommunledningskontoret"], ignoreOrder: true);
        context["departments"]!.AsArray().ShouldAllBe(d => d!["member_groups"] == null); // member groups only under organisation.read
        var keys = context["assigned_keys"]!.AsArray().ToDictionary(k => k!["name"]!.GetValue<string>());
        keys["Webb – external only"]!["usable"]!.GetValue<bool>().ShouldBeFalse(); // no residency in common with OnPrem, Eu
        keys["Kontaktcenter – chatbot"]!["usable"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task An_ordinary_key_has_no_integration_and_no_assigned_keys()
    {
        var context = await ContextAsync(FakeGatewayFactory.EuKey);

        context["key"]!["integration_role"]!.GetValue<string>().ShouldBe("None");
        context["integration"].ShouldBeNull();
        context["assigned_keys"]!.AsArray().ShouldBeEmpty();
        context["teams"]!.AsArray().ShouldHaveSingleItem()!["name"]!.GetValue<string>().ShouldBe("Digitalisering & AI");
    }

    [Theory]
    [InlineData(Residency.OnPrem)]
    [InlineData(Residency.Eu)]
    [InlineData(Residency.External)]
    public async Task The_model_list_conforms_to_the_contract_with_the_ume_facts(Residency ceiling)
    {
        using var client = Client(FakeGatewayFactory.ApiCredentialKey);
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        request.Headers.Add("x-ume-max-residency", ResidencyCeiling.ToHeader(ceiling));
        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var list = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;

        GatewayContract.Differences(list, "ModelList").ShouldBeEmpty();
        var models = list["data"]!.AsArray().ToDictionary(m => m!["id"]!.GetValue<string>(), m => m!["ume"]!);
        models.ShouldNotBeEmpty();
        if (ceiling >= Residency.Eu)
        {
            models["ume/chat-eu"]["context_window"]!.GetValue<int>().ShouldBe(128000);
            models["ume/chat-eu"]["max_output_tokens"]!.GetValue<int>().ShouldBe(16384);
            models["ume/chat-eu"]["features"]!.AsArray().Select(f => f!.GetValue<string>()).ShouldBe(["tools"]);
            models["ume/chat-eu"]["pricing"]!["output_per_million_usd"]!.GetValue<decimal>().ShouldBe(0.60m);
        }
    }

    [Theory]
    [InlineData("v1/ume/context")]
    [InlineData("v1/models")]
    public async Task An_unchanged_answer_is_an_empty_304_for_its_etag(string path)
    {
        using var client = Client(FakeGatewayFactory.AdminCredentialKey);
        using var first = await client.SendAsync(Get(path), Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var tag = first.Headers.ETag.ShouldNotBeNull();
        first.Headers.CacheControl!.Private.ShouldBeTrue();

        using var again = Get(path);
        again.Headers.IfNoneMatch.Add(tag);
        using var second = await client.SendAsync(again, Ct);

        second.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        (await second.Content.ReadAsByteArrayAsync(Ct)).ShouldBeEmpty();
        second.Headers.ETag.ShouldBe(tag);
    }

    [Fact]
    public async Task A_renamed_or_dropped_field_is_a_difference()
    {
        // What makes this a contract test: the FakeGateway answering as camelCase, or without a required field, fails.
        var context = await ContextAsync(FakeGatewayFactory.AdminCredentialKey);
        var renamed = context.DeepClone().AsObject();
        renamed["assignedKeys"] = renamed["assigned_keys"]!.DeepClone();
        renamed.Remove("assigned_keys");
        var dropped = context.DeepClone().AsObject();
        dropped["assigned_keys"]![0]!.AsObject().Remove("usable");

        GatewayContract.Differences(renamed, "UmeContext").ShouldBe(["UmeContext.assignedKeys is not in the contract", "UmeContext.assigned_keys is missing"]);
        GatewayContract.Differences(dropped, "UmeContext").ShouldBe(["UmeContext.assigned_keys[0].usable is missing"]);
        var groups = context.DeepClone().AsObject();
        var department = groups["departments"]![0]!.AsObject();
        department["memberGroups"] = department["member_groups"]!.DeepClone();
        department.Remove("member_groups");
        GatewayContract.Differences(groups, "UmeContext").ShouldBe(["UmeContext.departments[0].memberGroups is not in the contract"]);
    }

    [Fact]
    public async Task The_context_needs_a_known_key_but_no_ceiling()
    {
        using var anonymous = fake.CreateClient();
        using var refused = await anonymous.SendAsync(Get("v1/ume/context"), Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var client = Client(FakeGatewayFactory.AdminCredentialKey);
        using var answered = await client.SendAsync(Get("v1/ume/context"), Ct); // no x-ume-max-residency
        answered.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<JsonObject> ContextAsync(string key)
    {
        using var client = Client(key);
        using var response = await client.SendAsync(Get("v1/ume/context"), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject();
    }

    private HttpClient Client(string key)
    {
        var client = fake.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    /// <summary>A GET; the model list carries a ceiling, as every Ume.Rag key requires one there (the context reads none).</summary>
    private static HttpRequestMessage Get(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (path == "v1/models")
        {
            request.Headers.Add("x-ume-max-residency", "Eu");
        }

        return request;
    }
}
