using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// The FakeGateway behaves like gateway release 1 (repo-and-deployment §4.3, gateway design §3): keys that require a
/// ceiling, the 400/403/503 split, <c>x-ume-residency</c>, <c>/v1/models</c> by ceiling, 1024-dim embeddings, cited
/// answers, the stream modes of chat design §18.4, rerank and the catalogue.
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
        using var noCeiling = await SendAsync(path, body, FakeGatewayFactory.OnPremKey, ceiling: null);
        await AssertErrorAsync(noCeiling, HttpStatusCode.BadRequest, "invalid_request");

        using var noKey = await SendAsync(path, body, key: null, ceiling: "OnPrem");
        await AssertErrorAsync(noKey, HttpStatusCode.Unauthorized, "missing_key");

        using var unknownKey = await SendAsync(path, body, "made-up", ceiling: "OnPrem");
        await AssertErrorAsync(unknownKey, HttpStatusCode.Unauthorized, "invalid_key");

        using var ok = await SendAsync(path, body, FakeGatewayFactory.OnPremKey, ceiling: "onprem");
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Any")]
    [InlineData("0")]
    [InlineData("EU-West")]
    [InlineData(" ")]
    [InlineData("OnPrem, Eu")]
    public async Task A_malformed_ceiling_is_refused(string ceiling)
    {
        using var response = await SendAsync("/v1/models", null, FakeGatewayFactory.OnPremKey, ceiling);
        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_request");
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
        var onPrem = await ModelsAsync(FakeGatewayFactory.OnPremKey, "OnPrem");
        onPrem.Keys.ShouldContain("bge-m3");
        onPrem.Keys.ShouldContain("ume/chat-onprem");
        onPrem["ume/chat-standard"].ShouldBe(["OnPrem"]);
        onPrem.Keys.ShouldNotContain("ume/chat-eu");
        onPrem.Keys.ShouldNotContain("ume/chat-advanced");
        onPrem.Keys.ShouldNotContain("fake-drained"); // its in-ceiling target is drained

        var external = await ModelsAsync(FakeGatewayFactory.ExternalKey, "External");
        external["ume/chat-standard"].ShouldBe(["OnPrem", "Eu"]);
        external["ume/chat-advanced"].ShouldBe(["External"]);
        external["fake-drained"].ShouldBe(["External"]);
    }

    [Fact]
    public async Task No_target_within_the_ceiling_is_403_and_a_drained_one_is_503_with_retry_after()
    {
        using var notMet = await ChatAsync("ume/chat-eu", "Hej");
        await AssertErrorAsync(notMet, HttpStatusCode.Forbidden, "max_residency_not_met");

        using var drained = await ChatAsync("fake-drained", "Hej");
        await AssertErrorAsync(drained, HttpStatusCode.ServiceUnavailable, "no_eligible_provider");
        drained.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(30));
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
    public async Task Rerank_scores_by_word_overlap()
    {
        using var response = await SendAsync("/v1/rerank", """
            {"model":"bge-reranker-v2-m3","query":"bygglov avgift","documents":["Matsedel","Avgift för bygglov","Bygglov"],"top_n":2}
            """, FakeGatewayFactory.OnPremKey, "OnPrem");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var results = json.RootElement.GetProperty("results");
        results.GetArrayLength().ShouldBe(2);
        results[0].GetProperty("index").GetInt32().ShouldBe(1);
        results[0].GetProperty("relevance_score").GetDouble().ShouldBe(1);
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

    private async Task<HttpResponseMessage> SendAsync(string path, string? body, string? key, string? ceiling, string? onBehalfOf = null)
    {
        using var client = fake.CreateClient();
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

    private async Task<Dictionary<string, string[]>> ModelsAsync(string key, string ceiling)
    {
        using var response = await SendAsync("/v1/models", null, key, ceiling);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.GetProperty("data").EnumerateArray().ToDictionary(
            m => m.GetProperty("id").GetString()!,
            m => m.GetProperty("ume").GetProperty("residencies").EnumerateArray().Select(r => r.GetString()!).ToArray());
    }

    private async Task<List<float[]>> EmbedAsync(string body)
    {
        using var response = await SendAsync("/v1/embeddings", body, FakeGatewayFactory.OnPremKey, "OnPrem");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return [.. json.GetProperty("data").EnumerateArray().Select(d => d.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray())];
    }

    private static double Cosine(float[] a, float[] b) => a.Zip(b).Sum(p => (double)p.First * p.Second);

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.ShouldBe(status);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        json.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe(code);
    }
}
