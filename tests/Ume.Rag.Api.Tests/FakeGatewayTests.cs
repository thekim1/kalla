extern alias fakegateway;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// The FakeGateway behaves like gateway release 1 as built (repo-and-deployment §4.3, gateway design §3, #390 Feature B
/// points 1–4 and 8, checked against thekim1/llmgateway#66 and #68): keys that require a ceiling, the 400/403/503 split
/// with <c>Retry-After</c> only on the ceiling 503, 502/504 instead of a circuit-open 503, <c>x-ume-residency</c>,
/// <c>/v1/models</c> by ceiling, 1024-dim embeddings, cited answers, the stream modes of chat design §18.4, the optional
/// Cohere-compatible rerank and the catalogue.
/// </summary>
public sealed class FakeGatewayTests(FakeGatewayFactory fake) : IClassFixture<FakeGatewayFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/v1/models", null)]
    [InlineData("/v1/chat/completions", """{"model":"ume/chat-onprem","messages":[{"role":"user","content":"Hej"}]}""")]
    [InlineData("/v1/embeddings", """{"model":"bge-m3","input":"Hej"}""")]
    [InlineData("/v1/rerank", """{"model":"bge-reranker-v2-m3","query":"a","documents":["a"]}""")]
    public async Task Every_endpoint_requires_the_ceiling_for_dev_keys(string path, string? body)
    {
        using var rerank = Rerank();
        using var noCeiling = await SendAsync(path, body, FakeGatewayFactory.EuKey, ceiling: null, rerank);
        await AssertErrorAsync(noCeiling, HttpStatusCode.BadRequest, "invalid_request", "This API key requires the x-ume-max-residency header (OnPrem, Eu or External).");

        using var noKey = await SendAsync(path, body, key: null, ceiling: "Eu", rerank);
        await AssertErrorAsync(noKey, HttpStatusCode.Unauthorized, "invalid_api_key");

        using var unknownKey = await SendAsync(path, body, "made-up", ceiling: "Eu", rerank);
        await AssertErrorAsync(unknownKey, HttpStatusCode.Unauthorized, "invalid_api_key");

        using var ok = await SendAsync(path, body, FakeGatewayFactory.EuKey, ceiling: " eu ", rerank);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Any")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("EU-West")]
    [InlineData(" ")]
    [InlineData("OnPrem, Eu")]
    public async Task A_malformed_or_empty_ceiling_is_refused_on_every_endpoint(string ceiling)
    {
        // #390 Feature B point 4: one message for every malformed value (RequestStages.ReadMaxResidency).
        foreach (var (path, body) in new[] { ("/v1/models", (string?)null), ("/v1/chat/completions", """{"model":"ume/chat-onprem","messages":[]}""") })
        {
            using var response = await SendAsync(path, body, FakeGatewayFactory.OnPremKey, ceiling);
            await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request", "x-ume-max-residency must be one of OnPrem, Eu, External.");
        }
    }

    [Fact]
    public async Task An_empty_ceiling_header_is_malformed_not_missing()
    {
        // HttpClient drops an empty header value, so the request is built on the server side (Kestrel passes it as "").
        var context = await fake.Server.SendAsync(c =>
        {
            c.Request.Method = "GET";
            c.Request.Path = "/v1/models";
            c.Request.Headers.Authorization = "Bearer " + FakeGatewayFactory.OnPremKey;
            c.Request.Headers["x-ume-max-residency"] = "";
        }, Ct);
        context.Response.StatusCode.ShouldBe(400);
        using var json = await JsonDocument.ParseAsync(context.Response.Body, cancellationToken: Ct);
        json.RootElement.GetProperty("error").GetProperty("message").GetString().ShouldBe("x-ume-max-residency must be one of OnPrem, Eu, External.");
    }

    [Fact]
    public async Task A_repeated_ceiling_header_is_refused_even_with_the_same_value()
    {
        using var client = fake.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", FakeGatewayFactory.OnPremKey);
        request.Headers.TryAddWithoutValidation("x-ume-max-residency", ["OnPrem", "OnPrem"]).ShouldBeTrue();
        using var response = await client.SendAsync(request, Ct);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request", "x-ume-max-residency must be one of OnPrem, Eu, External.");
    }

    [Fact]
    public async Task Without_RequireMaxResidency_no_header_is_no_ceiling_and_its_503_has_no_retry_after()
    {
        // The key's level alone applies; the drained model's 503 is the plain no_eligible_provider, without Retry-After
        // (gateway: Retry-After only on the ceiling 503).
        using var optional = fake.WithWebHostBuilder(b => b.ConfigureAppConfiguration(c => c.AddInMemoryCollection([new("FakeGateway:RequireMaxResidency", "false")])));
        using var answered = await SendAsync("/v1/chat/completions", Chat("ume/chat-standard"), FakeGatewayFactory.EuKey, ceiling: null, optional);
        answered.StatusCode.ShouldBe(HttpStatusCode.OK);
        answered.Headers.GetValues("x-ume-residency").ShouldBe(["Eu"]);

        using var drained = await SendAsync("/v1/chat/completions", Chat("fake-drained"), FakeGatewayFactory.OnPremKey, ceiling: null, optional);
        await AssertErrorAsync(drained, HttpStatusCode.ServiceUnavailable, "no_eligible_provider", "No available provider can handle the request for this alias within the key's restrictions.");
        drained.Headers.RetryAfter.ShouldBeNull();
    }

    [Fact]
    public async Task A_key_sent_with_another_levels_ceiling_is_refused()
    {
        using var response = await SendAsync("/v1/models", null, FakeGatewayFactory.OnPremKey, "Eu");
        await AssertErrorAsync(response, HttpStatusCode.Forbidden, "key_level_mismatch");
    }

    [Fact]
    public async Task Models_are_listed_by_ceiling_with_their_residencies()
    {
        // #390 Feature B point 8: only what the ceiling allows, an alias's residencies narrowed (ModelListEndpoint).
        var onPrem = await ModelsAsync(FakeGatewayFactory.OnPremKey, "OnPrem");
        onPrem.Keys.ShouldContain("bge-m3");
        onPrem.Keys.ShouldContain("ume/chat-onprem");
        onPrem["ume/chat-standard"].ShouldBe(["OnPrem"]);
        onPrem.Keys.ShouldNotContain("ume/chat-eu");
        onPrem.Keys.ShouldNotContain("ume/chat-advanced");
        onPrem.Keys.ShouldNotContain("fake-drained"); // its in-ceiling target is drained

        var eu = await ModelsAsync(FakeGatewayFactory.EuKey, "Eu");
        eu["ume/chat-standard"].ShouldBe(["OnPrem", "Eu"]);
        eu["ume/chat-eu"].ShouldBe(["Eu"]);
        eu.Keys.ShouldNotContain("ume/chat-advanced");

        var external = await ModelsAsync(FakeGatewayFactory.ExternalKey, "External");
        external["ume/chat-standard"].ShouldBe(["OnPrem", "Eu"]);
        external["ume/chat-advanced"].ShouldBe(["External"]);
        external["fake-drained"].ShouldBe(["External"]);
    }

    [Fact]
    public async Task The_model_list_has_the_gateways_shape()
    {
        using var response = await SendAsync("/v1/models", null, FakeGatewayFactory.EuKey, "Eu");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var models = json.RootElement.GetProperty("data").EnumerateArray().ToDictionary(m => m.GetProperty("id").GetString()!, m => m.Clone());

        var alias = models["ume/chat-standard"].GetProperty("ume");
        alias.GetProperty("type").GetString().ShouldBe("alias");
        alias.GetProperty("kind").GetString().ShouldBe("Chat");
        var deployment = models["bge-m3"];
        deployment.GetProperty("owned_by").GetString().ShouldBe("fake-onprem");
        deployment.GetProperty("ume").GetProperty("type").GetString().ShouldBe("model");
        deployment.GetProperty("ume").GetProperty("kind").GetString().ShouldBe("Embedding");
        deployment.GetProperty("ume").GetProperty("residency").GetString().ShouldBe("OnPrem");
    }

    [Fact]
    public async Task No_target_within_the_ceiling_is_403_and_a_drained_one_is_503_with_retry_after()
    {
        // #390 Feature B point 2, with the gateway's messages (GatewayError.MaxResidencyNotMet, NoProviderWithin).
        using var notMet = await ChatAsync("ume/chat-eu", "Hej");
        await AssertErrorAsync(notMet, HttpStatusCode.Forbidden, "max_residency_not_met",
            "No provider for 'ume/chat-eu' is configured at or below the residency 'OnPrem' that the request allows.");
        notMet.Headers.RetryAfter.ShouldBeNull();

        using var drained = await ChatAsync("fake-drained", "Hej");
        await AssertErrorAsync(drained, HttpStatusCode.ServiceUnavailable, "no_eligible_provider",
            "No provider for 'fake-drained' within the residency 'OnPrem' is available right now. Try again later.");
        drained.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Failing_targets_within_the_ceiling_end_in_502_or_504_never_a_circuit_open_503()
    {
        // #390 Feature B point 1: the circuit-open 503 is gone; every target within the ceiling is tried, and when all
        // fail the answer is all_providers_failed, 504 when the last one timed out, with no Retry-After.
        using var failed = await ChatAsync("fake-fail-500", "Hej");
        await AssertErrorAsync(failed, HttpStatusCode.BadGateway, "all_providers_failed", "All 1 attempt for 'fake-fail-500' failed. Last error: http_500.");
        failed.Headers.RetryAfter.ShouldBeNull();

        using var timedOut = await ChatAsync("fake-fail-timeout", "Hej");
        await AssertErrorAsync(timedOut, HttpStatusCode.GatewayTimeout, "all_providers_failed", "All 1 attempt for 'fake-fail-timeout' failed. Last error: timeout.");

        // Its OnPrem target fails: at OnPrem nothing else is within the ceiling, so 502; at Eu the gateway falls back to Eu.
        using var onPrem = await ChatAsync("fake-fail-fallback", "Hej");
        await AssertErrorAsync(onPrem, HttpStatusCode.BadGateway, "all_providers_failed");
        onPrem.Headers.Contains("x-ume-residency").ShouldBeFalse();
        using var eu = await ChatAsync("fake-fail-fallback", "Hej", FakeGatewayFactory.EuKey, "Eu");
        eu.StatusCode.ShouldBe(HttpStatusCode.OK);
        eu.Headers.GetValues("x-ume-residency").ShouldBe(["Eu"]);
    }

    [Fact]
    public async Task Errors_have_the_gateways_body_and_a_request_id()
    {
        using var response = await ChatAsync("no-such-model", "Hej");
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var requestId = response.Headers.GetValues("x-request-id").Single();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var error = json.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("model_not_found");
        error.GetProperty("type").GetString().ShouldBe("not_found_error");
        error.GetProperty("message").GetString().ShouldBe("The model 'no-such-model' does not exist. See GET /v1/models for the models you can use.");
        error.GetProperty("request_id").GetString().ShouldBe(requestId);
        error.GetProperty("param").ValueKind.ShouldBe(JsonValueKind.Null);

        using var wrongKind = await SendAsync("/v1/embeddings", """{"model":"ume/chat-onprem","input":"x"}""", FakeGatewayFactory.OnPremKey, "OnPrem");
        await AssertErrorAsync(wrongKind, HttpStatusCode.BadRequest, "invalid_request", "The model 'ume/chat-onprem' cannot be used with this endpoint.");
    }

    [Fact]
    public async Task Answers_carry_their_residency_and_wrong_residency_lies_about_it()
    {
        using var standard = await ChatAsync("ume/chat-standard", "Hej", key: FakeGatewayFactory.EuKey, ceiling: "Eu");
        standard.Headers.GetValues("x-ume-residency").ShouldBe(["Eu"]);

        using var onPrem = await ChatAsync("ume/chat-standard", "Hej");
        onPrem.Headers.GetValues("x-ume-residency").ShouldBe(["OnPrem"]);

        using var wrong = await ChatAsync("fake-wrong-residency", "Hej");
        wrong.StatusCode.ShouldBe(HttpStatusCode.OK);
        wrong.Headers.GetValues("x-ume-residency").ShouldBe(["External"]);
    }

    [Fact]
    public async Task A_chat_answer_echoes_the_question_and_cites_the_sources_in_its_context()
    {
        using var response = await SendAsync("/v1/chat/completions", """
            {"model":"ume/chat-onprem","messages":[
              {"role":"system","content":"Källor: [1] Bygglov ... [2] Avgifter ..."},
              {"role":"user","content":"Vad kostar bygglov?"}]}
            """, FakeGatewayFactory.OnPremKey, "OnPrem", onBehalfOf: "digitalisering-ai");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("x-ume-on-behalf-of").ShouldBe(["digitalisering-ai"]);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var content = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
        content.ShouldContain("Vad kostar bygglov?");
        content.ShouldContain("[1]");
        content.ShouldContain("[2]");
        json.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt32().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_stream_sends_words_a_usage_only_final_chunk_and_done()
    {
        var events = await StreamAsync("ume/chat-onprem", includeUsage: true);
        events[^1].ShouldBe("[DONE]");
        using var usage = JsonDocument.Parse(events[^2]);
        usage.RootElement.GetProperty("choices").GetArrayLength().ShouldBe(0);
        usage.RootElement.GetProperty("usage").GetProperty("completion_tokens").GetInt32().ShouldBeGreaterThan(0);
        using var finish = JsonDocument.Parse(events[^3]);
        finish.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString().ShouldBe("stop");
        string.Concat(events.SkipLast(3).Select(Delta)).ShouldContain("Testsvar");
    }

    [Fact]
    public async Task Stream_modes_cover_the_edge_cases()
    {
        (await StreamAsync("fake-stream-no-done")).ShouldNotContain("[DONE]");
        var length = await StreamAsync("fake-stream-length");
        length[^2].ShouldContain("\"finish_reason\":\"length\"");
        var empty = await StreamAsync("fake-stream-empty-deltas");
        empty.Where(e => e != "[DONE]").Count(e => Delta(e) == "").ShouldBeGreaterThan(2);
        (await StreamAsync("fake-stream-stall")).ShouldContain("[DONE]"); // StallSeconds = 1 in the factory
        await Should.ThrowAsync<Exception>(() => StreamAsync("fake-stream-broken"));
    }

    [Fact]
    public async Task Max_tokens_is_echoed_and_a_longer_answer_stops_at_it_with_finish_reason_length()
    {
        // B-31: tests see whether max_tokens was sent (x-fake-max-tokens, "none" when left out), and an answer over it is cut.
        var question = string.Join(' ', Enumerable.Repeat("Hur lång får en altan vara utan bygglov i Umeå?", 20));
        string Body(int? maxTokens) => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["model"] = "ume/chat-onprem", ["messages"] = new[] { new { role = "user", content = question } }, ["max_tokens"] = maxTokens,
        }.Where(p => p.Value is not null).ToDictionary());

        using var open = await SendAsync("/v1/chat/completions", Body(null), FakeGatewayFactory.OnPremKey, "OnPrem");
        open.Headers.GetValues("x-fake-max-tokens").ShouldBe(["none"]);
        var whole = await open.Content.ReadFromJsonAsync<JsonElement>(Ct);
        whole.GetProperty("choices")[0].GetProperty("finish_reason").GetString().ShouldBe("stop");
        var full = whole.GetProperty("usage").GetProperty("completion_tokens").GetInt32();
        full.ShouldBeGreaterThan(5);

        using var capped = await SendAsync("/v1/chat/completions", Body(5), FakeGatewayFactory.OnPremKey, "OnPrem");
        capped.Headers.GetValues("x-fake-max-tokens").ShouldBe(["5"]);
        var cut = await capped.Content.ReadFromJsonAsync<JsonElement>(Ct);
        cut.GetProperty("choices")[0].GetProperty("finish_reason").GetString().ShouldBe("length");
        cut.GetProperty("usage").GetProperty("completion_tokens").GetInt32().ShouldBeLessThanOrEqualTo(5);
    }

    [Fact]
    public async Task Embeddings_are_normalised_1024_dimensions_and_similar_texts_are_closer()
    {
        var vectors = await EmbedAsync("""{"model":"bge-m3","input":["Ansök om bygglov","Ansökan om bygglov för garage","Matsedel för skolan"]}""");
        vectors.ShouldAllBe(v => v.Length == 1024);
        foreach (var v in vectors)
        {
            Math.Sqrt(v.Sum(x => (double)x * x)).ShouldBe(1, 1e-5);
        }

        Cosine(vectors[0], vectors[1]).ShouldBeGreaterThan(Cosine(vectors[0], vectors[2]));
        (await EmbedAsync("""{"model":"bge-m3","input":"Ansök om bygglov"}"""))[0].ShouldBe(vectors[0]); // deterministic
        (await EmbedAsync("""{"model":"bge-m3","input":"x","dimensions":8}"""))[0].Length.ShouldBe(8);
    }

    [Fact]
    public async Task Base64_embeddings_round_trip()
    {
        var floats = (await EmbedAsync("""{"model":"bge-m3","input":"Bygglov"}"""))[0];
        using var response = await SendAsync("/v1/embeddings", """{"model":"bge-m3","input":"Bygglov","encoding_format":"base64"}""", FakeGatewayFactory.OnPremKey, "OnPrem");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var bytes = Convert.FromBase64String(json.RootElement.GetProperty("data")[0].GetProperty("embedding").GetString()!);
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes).ToArray().ShouldBe(floats);
        response.Headers.GetValues("x-ume-residency").ShouldBe(["OnPrem"]);
    }

    [Fact]
    public async Task Rerank_scores_strings_and_text_objects_in_the_cohere_shape()
    {
        // gateway design §7.4, thekim1/llmgateway#68: { model, query, documents: (string | { text })[], top_n?,
        // return_documents? } → results[] with index and relevance_score (document when asked) and usage.
        using var rerank = Rerank();
        using var response = await SendAsync("/v1/rerank", """
            {"model":"bge-reranker-v2-m3","query":"bygglov avgift","documents":["Matsedel",{"text":"Avgift för bygglov"},"Bygglov"],"top_n":2}
            """, FakeGatewayFactory.EuKey, "Eu", rerank);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.GetValues("x-ume-residency").ShouldBe(["Eu"]);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var results = json.RootElement.GetProperty("results");
        results.GetArrayLength().ShouldBe(2);
        results[0].GetProperty("index").GetInt32().ShouldBe(1);
        results[0].GetProperty("relevance_score").GetDouble().ShouldBe(1);
        results[1].GetProperty("index").GetInt32().ShouldBe(2);
        results[0].TryGetProperty("document", out _).ShouldBeFalse();
        json.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt32().ShouldBeGreaterThan(0);
        json.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt32().ShouldBe(json.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt32());

        using var all = await SendAsync("/v1/rerank", """
            {"model":"bge-reranker-v2-m3","query":"bygglov","documents":["Matsedel",{"text":"Bygglov"}],"return_documents":true}
            """, FakeGatewayFactory.EuKey, "Eu", rerank);
        using var allJson = JsonDocument.Parse(await all.Content.ReadAsStringAsync(Ct));
        var allResults = allJson.RootElement.GetProperty("results");
        allResults.GetArrayLength().ShouldBe(2); // no top_n: every document
        allResults[0].GetProperty("document").GetProperty("text").GetString().ShouldBe("Bygglov");
    }

    [Fact]
    public async Task Rerank_refuses_like_the_gateway()
    {
        using var rerank = Rerank();
        using var noDocuments = await SendAsync("/v1/rerank", """{"model":"bge-reranker-v2-m3","query":"bygglov","documents":[]}""", FakeGatewayFactory.EuKey, "Eu", rerank);
        await AssertErrorAsync(noDocuments, HttpStatusCode.BadRequest, "invalid_request");

        using var chatModel = await SendAsync("/v1/rerank", """{"model":"ume/chat-eu","query":"a","documents":["a"]}""", FakeGatewayFactory.EuKey, "Eu", rerank);
        await AssertErrorAsync(chatModel, HttpStatusCode.BadRequest, "invalid_request", "The model 'ume/chat-eu' cannot be used with this endpoint.");

        using var onChat = await SendAsync("/v1/chat/completions", Chat("bge-reranker-v2-m3"), FakeGatewayFactory.EuKey, "Eu", rerank);
        await AssertErrorAsync(onChat, HttpStatusCode.BadRequest, "invalid_request");

        // The reranker is an EU provider (Berget AI): an OnPrem ceiling cannot reach it.
        using var onPrem = await SendAsync("/v1/rerank", """{"model":"bge-reranker-v2-m3","query":"a","documents":["a"]}""", FakeGatewayFactory.OnPremKey, "OnPrem", rerank);
        await AssertErrorAsync(onPrem, HttpStatusCode.Forbidden, "max_residency_not_met");

        var models = await ModelsAsync(FakeGatewayFactory.EuKey, "Eu", rerank);
        models["bge-reranker-v2-m3"].ShouldBe(["Eu"]);
    }

    [Fact]
    public async Task By_default_no_rerank_model_is_configured_and_rerank_answers_404()
    {
        // Rerank is optional (user, 2026-11-06): the default fake is a gateway without a rerank provider, which answers
        // 404 for the model (400 for a model of another kind), lists none and has none in its catalogue.
        using var unknown = await SendAsync("/v1/rerank", """{"model":"bge-reranker-v2-m3","query":"a","documents":["a"]}""", FakeGatewayFactory.EuKey, "Eu");
        await AssertErrorAsync(unknown, HttpStatusCode.NotFound, "model_not_found");

        using var chatModel = await SendAsync("/v1/rerank", """{"model":"ume/chat-eu","query":"a","documents":["a"]}""", FakeGatewayFactory.EuKey, "Eu");
        await AssertErrorAsync(chatModel, HttpStatusCode.BadRequest, "invalid_request");

        (await ModelsAsync(FakeGatewayFactory.ExternalKey, "External")).Keys.ShouldNotContain("bge-reranker-v2-m3");

        using var client = fake.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FakeGatewayFactory.DataApiKey);
        (await client.GetStringAsync("/v1/catalog/models", Ct)).ShouldNotContain("Rerank");
        using var enabled = Rerank();
        using var enabledClient = enabled.CreateClient();
        enabledClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FakeGatewayFactory.DataApiKey);
        (await enabledClient.GetStringAsync("/v1/catalog/models", Ct)).ShouldContain("\"kind\":\"Rerank\"");
    }

    [Fact]
    public async Task The_catalogue_lists_departments_with_member_groups_and_teams_with_codes()
    {
        using var client = fake.CreateClient();
        (await client.GetAsync("/v1/catalog/departments", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", FakeGatewayFactory.DataApiKey);
        using var departments = JsonDocument.Parse(await client.GetStringAsync("/v1/catalog/departments", Ct));
        var social = departments.RootElement.GetProperty("items").EnumerateArray().Single(d => d.GetProperty("costCenterCode").GetString() == "3000");
        social.GetProperty("memberGroups").EnumerateArray().Select(g => g.GetString()).ShouldBe(["Byggnadsnämnden"]);

        using var teams = JsonDocument.Parse(await client.GetStringAsync("/v1/catalog/teams", Ct));
        teams.RootElement.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("code").GetString()).ShouldContain("digitalisering-ai");
    }

    /// <summary>The fake with a rerank model (<c>FakeGateway:RerankEnabled</c>).</summary>
    private WebApplicationFactory<fakegateway::Program> Rerank() =>
        fake.WithWebHostBuilder(b => b.ConfigureAppConfiguration(c => c.AddInMemoryCollection([new("FakeGateway:RerankEnabled", "true")])));

    private static string Chat(string model) => JsonSerializer.Serialize(new { model, messages = new[] { new { role = "user", content = "Hej" } } });

    private async Task<HttpResponseMessage> SendAsync(string path, string? body, string? key, string? ceiling, WebApplicationFactory<fakegateway::Program>? factory = null,
        string? onBehalfOf = null)
    {
        using var client = (factory ?? fake).CreateClient();
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        }

        if (key is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        if (ceiling is not null)
        {
            request.Headers.TryAddWithoutValidation("x-ume-max-residency", ceiling);
        }

        if (onBehalfOf is not null)
        {
            request.Headers.Add("x-ume-on-behalf-of", onBehalfOf);
        }

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
    }

    private Task<HttpResponseMessage> ChatAsync(string model, string question, string key = FakeGatewayFactory.OnPremKey, string ceiling = "OnPrem") =>
        SendAsync("/v1/chat/completions", JsonSerializer.Serialize(new { model, messages = new[] { new { role = "user", content = question } } }), key, ceiling);

    private async Task<List<string>> StreamAsync(string model, bool includeUsage = false)
    {
        var body = JsonSerializer.Serialize(new
        {
            model,
            stream = true,
            stream_options = new { include_usage = includeUsage },
            messages = new[] { new { role = "user", content = "Hur söker jag bygglov?" } },
        });
        using var response = await SendAsync("/v1/chat/completions", body, FakeGatewayFactory.OnPremKey, "OnPrem");
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream");
        var text = await response.Content.ReadAsStringAsync(Ct);
        return [.. text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(e => e["data: ".Length..])];
    }

    private static string Delta(string chunk)
    {
        using var json = JsonDocument.Parse(chunk);
        var choices = json.RootElement.GetProperty("choices");
        return choices.GetArrayLength() > 0 && choices[0].GetProperty("delta").TryGetProperty("content", out var c) ? c.GetString() ?? "" : "-";
    }

    /// <summary>The listed models and their residencies (an alias's <c>ume.residencies</c>, a deployment's <c>ume.residency</c>).</summary>
    private async Task<Dictionary<string, string[]>> ModelsAsync(string key, string ceiling, WebApplicationFactory<fakegateway::Program>? factory = null)
    {
        using var response = await SendAsync("/v1/models", null, key, ceiling, factory);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.GetProperty("data").EnumerateArray().ToDictionary(
            m => m.GetProperty("id").GetString()!,
            m => m.GetProperty("ume").TryGetProperty("residencies", out var list)
                ? list.EnumerateArray().Select(r => r.GetString()!).ToArray()
                : [m.GetProperty("ume").GetProperty("residency").GetString()!]);
    }

    private async Task<List<float[]>> EmbedAsync(string body)
    {
        using var response = await SendAsync("/v1/embeddings", body, FakeGatewayFactory.OnPremKey, "OnPrem");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return [.. json.GetProperty("data").EnumerateArray().Select(d => d.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray())];
    }

    private static double Cosine(float[] a, float[] b) => a.Zip(b).Sum(p => (double)p.First * p.Second);

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code, string? message = null)
    {
        response.StatusCode.ShouldBe(status);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe(code);
        if (message is not null)
        {
            json.RootElement.GetProperty("error").GetProperty("message").GetString().ShouldBe(message);
        }
    }
}
