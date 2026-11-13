using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Ume.Rag.FakeGateway;

// OpenAI-compatible fake of the team's LLM gateway for local runs and tests (repo-and-deployment §4.3). Behaves like
// gateway release 1 as built (thekim1/llmgateway#66, #390): per-level dev keys that require x-ume-max-residency (400
// invalid_request when it is missing, empty, repeated or not one of the three names), x-ume-residency on every answer,
// 403 max_residency_not_met vs 503 no_eligible_provider + Retry-After, no circuit-open 503 (failing in-ceiling targets end
// in 502/504 all_providers_failed), /v1/models filtered by the ceiling, the optional Cohere-compatible /v1/rerank
// (thekim1/llmgateway#68; off unless FakeGateway:RerankEnabled), and /v1/ume/context. Behaviour by model name
// (appsettings.json):
//   fake-fail-500 → 502 all_providers_failed; fake-fail-timeout → 504; fake-fail-fallback → its OnPrem target fails, so
//   OnPrem gets 502 and Eu an answer from Eu; fake-slow → 3 s delay; fake-wrong-residency → answers with x-ume-residency
//   External; fake-drained → 503 + Retry-After (its in-ceiling targets are drained);
//   fake-stream-broken | -stall | -no-done | -empty-deltas | -length → streaming edge cases (chat design §18.4);
//   fake-no-cost → answers without x-ume-cost-usd and without the stream cost comment.
// With stream_options.include_usage the last chunk carries usage only, as OpenAI's do. The cost as gateway phase 2
// (gateway-changes §5.4, GatewayRequestHandler at v0.10.0): x-ume-cost-usd (eight decimals) on every complete JSON answer,
// and with x-ume-stream-cost: 1 (exactly "1") the comment ": ume-cost-usd=<usd>" (six decimals) just before data: [DONE]
// (after the last event of a stream without one), both at the catalogue's prices (FakeCatalog.Cost).
// FakeGateway:CachedInputTokens scripts the cached prompt tokens of chat answers. NOT for production.
if (await WebDefaults.ExitIfHealthProbeAsync(args))
{
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddOptions<FakeGatewayOptions>().BindConfiguration(FakeGatewayOptions.SectionName);
builder.Services.AddSingleton<FakeUsage>(); // the served requests by the key they ran as (B-42)
var latency = builder.Configuration.GetSection(FakeLatency.SectionName).Get<FakeLatency>() ?? new FakeLatency();
builder.Services.AddHttpClient("upstream").RemoveStandardResilienceHandler();
builder.Services.AddSingleton<FakeChatScripts>();

var app = builder.Build();
app.Use((ctx, next) =>
{
    // Every gateway answer carries its request id (the gateway's RequestIdMiddleware), error bodies too.
    ctx.Response.Headers[FakeAccess.RequestIdHeader] = "fake-" + Guid.NewGuid().ToString("N")[..16];
    return next(ctx);
});
app.MapDefaultEndpoints();
app.MapGet("/", () => "Ume.Rag fake gateway – endast för test och utveckling.");

app.MapGet("/v1/models", async (HttpContext ctx, IOptionsSnapshot<FakeGatewayOptions> options) =>
{
    var o = options.Value;
    if (FakeAccess.Authorize(ctx, o, out var error) is not { } call)
    {
        await error!.ExecuteAsync(ctx);
        return;
    }

    // As the gateway's ModelListEndpoint (thekim1/llmgateway#84): only models with an available target within the
    // ceiling (gateway design §3.4); a multi-target name is a route alias (ume.residencies, the limits all its targets
    // have), a single-target one a deployment (ume.residency); ume.kind is the gateway's ModelKind name; an ETag.
    var data = new JsonArray();
    foreach (var (model, residencies) in o.Served
                 .Select(m => (Model: m, Residencies: FakeAccess.ServableResidencies(m, call)))
                 .Where(m => m.Residencies.Length > 0)
                 .OrderBy(m => m.Model.Targets.Count == 1).ThenBy(m => m.Model.Name, StringComparer.Ordinal))
    {
        var alias = model.Targets.Count > 1;
        var price = FakeCatalog.PriceOf(model);
        var facts = new JsonObject
        {
            ["type"] = alias ? "alias" : "model",
            ["kind"] = FakeCatalog.KindName(model.Kind),
        };
        if (alias)
        {
            facts["description"] = model.Description;
            facts["residencies"] = new JsonArray([.. residencies.Select(r => (JsonNode?)Ume.Rag.Domain.ResidencyCeiling.ToHeader(r))]);
        }
        else
        {
            facts["residency"] = Ume.Rag.Domain.ResidencyCeiling.ToHeader(residencies[0]);
        }

        facts["context_window"] = model.ContextWindow;
        facts["max_output_tokens"] = model.MaxOutputTokens;
        facts["features"] = new JsonArray([.. model.Features.Order(StringComparer.Ordinal).Select(f => (JsonNode?)f)]);
        facts["pricing"] = new JsonObject
        {
            ["input_per_million_usd"] = price.InputPerMillionUsd,
            ["cached_input_per_million_usd"] = price.CachedInputPerMillionUsd ?? price.InputPerMillionUsd,
            ["output_per_million_usd"] = price.OutputPerMillionUsd,
        };
        data.Add(new JsonObject
        {
            ["id"] = model.Name,
            ["object"] = "model",
            ["created"] = 0,
            ["owned_by"] = alias ? "ume-llm-gateway" : FakeCatalog.ProviderName(residencies[0]),
            ["ume"] = facts,
        });
    }

    await CacheableJson.WriteAsync(ctx, new JsonObject { ["object"] = "list", ["data"] = data }, ctx.RequestAborted);
});

// GET /v1/ume/context (gateway integration phase 1, docs/integrations.md "Discovery"): the presented key, its
// integration, assigned keys and organisation, with an ETag. No ceiling is read, as the gateway's ContextEndpoint.
app.MapGet("/v1/ume/context", async (HttpContext ctx, IOptionsSnapshot<FakeGatewayOptions> options) =>
{
    if (FakeAccess.Authenticate(ctx, options.Value, out var error) is not { } key)
    {
        await error!.ExecuteAsync(ctx);
        return;
    }

    await CacheableJson.WriteAsync(ctx, FakeContext.Build(key, options.Value, DateTimeOffset.UtcNow), ctx.RequestAborted);
});

app.MapPost("/v1/chat/completions", async (HttpContext ctx, IOptionsSnapshot<FakeGatewayOptions> options, FakeChatScripts scripts) =>
{
    var o = options.Value;
    if (FakeAccess.Authorize(ctx, o, out var error) is not { } call)
    {
        return error!;
    }

    var body = await ReadAsync(ctx);
    var modelName = body["model"]?.GetValue<string>();
    if (FakeAccess.Route(ctx, o, call, modelName, FakeModel.Chat, out error) is not { } model)
    {
        return error!;
    }

    if (await FailureAsync(ctx, model.Name) is { } failure)
    {
        return failure;
    }

    var question = FakeContent.LastUserText(body["messages"]);
    // The worker's evaluation steps and the reranker get scripted JSON answers (#129, B-12); every other call the echo.
    var answer = scripts.Answer(ctx.Request.Headers[FakeChatScripts.StepHeader].ToString(), question)
                 ?? latency.Pad(FakeContent.Answer(question, FakeContent.AllText(body["messages"])));
    var promptTokens = FakeContent.Tokens(FakeContent.AllText(body["messages"]));
    var finishReason = model.Name == "fake-stream-length" ? "length" : "stop";
    // B-31 (chat design §6.3, §8.1): the max_tokens it got is echoed (`none` when left out), and an answer longer than it is
    // cut there with finish_reason length, as a provider does.
    var maxTokens = body["max_tokens"] is JsonValue m && m.TryGetValue<int>(out var limit) ? limit : (int?)null;
    ctx.Response.Headers[FakeContent.MaxTokensHeader] = maxTokens?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";
    if (maxTokens is { } cap && FakeContent.Tokens(answer) > cap)
    {
        answer = FakeContent.Truncate(answer, cap);
        finishReason = "length";
    }

    var completionTokens = FakeContent.Tokens(answer);
    var cachedTokens = Math.Clamp(o.CachedInputTokens, 0, promptTokens);
    var cost = FakeCatalog.Cost(model, promptTokens, cachedTokens, completionTokens);
    ctx.RequestServices.GetRequiredService<FakeUsage>().Record(ctx, call, model, promptTokens, completionTokens, cost);
    var id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..12];
    var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    if (body["stream"]?.GetValue<bool>() == true)
    {
        // Copied from the gateway's FakeLlm, plus the stream modes of chat design §18.4.
        var includeUsage = body["stream_options"]?["include_usage"]?.GetValue<bool>() == true;
        Dictionary<string, object?> Chunk(object delta, string? finish)
        {
            var chunk = new Dictionary<string, object?>
            {
                ["id"] = id, ["object"] = "chat.completion.chunk", ["created"] = created, ["model"] = model.Name,
                ["choices"] = new[] { new { index = 0, delta, finish_reason = finish } },
            };
            if (includeUsage)
            {
                chunk["usage"] = null; // as OpenAI and Azure send it in every chunk but the last
            }

            return chunk;
        }

        await StartSse(ctx);
        await Sse(ctx, Chunk(new { role = "assistant", content = "" }, null));
        var first = true;
        var sent = 0;
        foreach (var word in FakeContent.StreamWords(answer))
        {
            await (first ? latency.FirstTokenAsync(promptTokens, ctx.RequestAborted) : latency.NextTokenAsync(ctx.RequestAborted));
            first = false;
            if (model.Name == "fake-stream-broken" && sent == 2)
            {
                ctx.Abort(); // the connection drops mid-answer
                return Results.Empty;
            }

            if (model.Name == "fake-stream-empty-deltas")
            {
                await Sse(ctx, Chunk(new { content = "" }, null));
            }

            await Sse(ctx, Chunk(new { content = word }, null));
            sent++;
            if (model.Name == "fake-stream-stall" && sent == 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(o.StallSeconds), ctx.RequestAborted);
            }
        }

        await Sse(ctx, Chunk(new { }, finishReason));
        if (includeUsage)
        {
            await Sse(ctx, new
            {
                id, @object = "chat.completion.chunk", created, model = model.Name, choices = Array.Empty<object>(),
                usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens, total_tokens = promptTokens + completionTokens, prompt_tokens_details = new { cached_tokens = cachedTokens } },
            });
        }

        // The gateway's cost comment goes before the closing [DONE] (OpenAI-style clients stop reading there), or after the
        // last event of a stream without one; only when asked with exactly "1".
        if (ctx.Request.Headers[FakeContent.StreamCostHeader] == "1" && model.Name != FakeCatalog.NoCostModel)
        {
            await ctx.Response.WriteAsync(string.Create(System.Globalization.CultureInfo.InvariantCulture, $": ume-cost-usd={decimal.Round(cost, 6):0.000000}\n\n"));
        }

        if (model.Name != "fake-stream-no-done")
        {
            await ctx.Response.WriteAsync("data: [DONE]\n\n");
        }

        return Results.Empty;
    }

    await latency.WholeAnswerAsync(FakeContent.StreamWords(answer).Count(), promptTokens, ctx.RequestAborted);
    SetCost(ctx, model, cost);
    return Results.Json(new
    {
        id,
        @object = "chat.completion",
        created,
        model = model.Name,
        choices = new[] { new { index = 0, message = new { role = "assistant", content = answer }, finish_reason = finishReason } },
        usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens, total_tokens = promptTokens + completionTokens, prompt_tokens_details = new { cached_tokens = cachedTokens } },
    });
});

app.MapPost("/v1/embeddings", async (HttpContext ctx, IOptionsSnapshot<FakeGatewayOptions> options, IHttpClientFactory clients) =>
{
    var o = options.Value;
    if (FakeAccess.Authorize(ctx, o, out var error) is not { } call)
    {
        return error!;
    }

    var body = await ReadAsync(ctx);
    var modelName = body["model"]?.GetValue<string>();
    if (FakeAccess.Route(ctx, o, call, modelName, FakeModel.Embedding, out error) is not { } model)
    {
        return error!;
    }

    if (await FailureAsync(ctx, model.Name) is { } failure)
    {
        return failure;
    }

    string[] inputs = body["input"] switch
    {
        JsonArray a => [.. a.Select(x => x?.GetValue<string>() ?? "")],
        JsonValue v when v.TryGetValue<string>(out var s) => [s],
        _ => [],
    };
    if (inputs.Length == 0)
    {
        return FakeAccess.Error(ctx, 400, "invalid_request", "'input' must be a string or an array of strings.");
    }

    // Dimensions (gateway design §6): the OpenAI parameter, else a "-<n>" model suffix, else 1024 (BGE-M3).
    var dimensions = FakeContent.DefaultDimensions;
    if (body["dimensions"] is JsonValue d)
    {
        dimensions = d.GetValue<int>();
        if (dimensions is < 1 or > 4096)
        {
            return FakeAccess.Error(ctx, 400, "invalid_request", "'dimensions' must be between 1 and 4096.");
        }
    }
    else if (model.Name.LastIndexOf('-') is var dash and > 0 && int.TryParse(model.Name.AsSpan(dash + 1), System.Globalization.CultureInfo.InvariantCulture, out var suffix) && suffix is >= 1 and <= 4096)
    {
        dimensions = suffix;
    }

    var vectors = o.Upstream.Embeddings is { Length: > 0 } upstream
        ? await UpstreamEmbeddingsAsync(clients.CreateClient("upstream"), upstream, inputs, ctx.RequestAborted)
        : [.. inputs.Select(text => FakeContent.Vector(text, dimensions))];
    var base64 = body["encoding_format"]?.GetValue<string>() == "base64";
    var inputTokens = inputs.Sum(FakeContent.Tokens);
    ctx.RequestServices.GetRequiredService<FakeUsage>().Record(ctx, call, model, inputTokens, 0, FakeCatalog.Cost(model, inputTokens, 0, 0));
    SetCost(ctx, model, FakeCatalog.Cost(model, inputTokens, 0, 0));
    return Results.Json(new
    {
        @object = "list",
        model = model.Name,
        data = vectors.Select((vector, index) => new
        {
            @object = "embedding",
            index,
            embedding = base64 ? (object)Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(vector.AsSpan())) : vector,
        }),
        usage = new { prompt_tokens = inputTokens, total_tokens = inputTokens },
    });
});

// Cohere-compatible rerank as the gateway serves it from thekim1/llmgateway#68 (gateway design §7.4): documents as strings
// or { text }, results[] with index and relevance_score (and document with return_documents), usage in input tokens.
// Optional: with FakeGateway:RerankEnabled off (the default) no rerank model exists, so the call answers 404 (400 for a
// model of another kind), as the gateway does without a rerank provider. Scores from word overlap, deterministic.
app.MapFakeIntegration(); // gateway phase 3: /integration/v1 usage and budgets (gateway-changes §5.6)

app.MapPost("/v1/rerank", async (HttpContext ctx, IOptionsSnapshot<FakeGatewayOptions> options) =>
{
    var o = options.Value;
    if (FakeAccess.Authorize(ctx, o, out var error) is not { } call)
    {
        return error!;
    }

    var body = await ReadAsync(ctx);
    if (FakeAccess.Route(ctx, o, call, body["model"]?.GetValue<string>(), FakeModel.Rerank, out error) is not { } model)
    {
        return error!;
    }

    if (await FailureAsync(ctx, model.Name) is { } failure)
    {
        return failure;
    }

    // The provider's refusal, passed on by the gateway with its status and message (the gateway's FakeLlm's text).
    var query = body["query"] is JsonValue q && q.TryGetValue<string>(out var text) ? text : "";
    if (string.IsNullOrWhiteSpace(query) || body["documents"] is not JsonArray { Count: > 0 } docs)
    {
        return FakeAccess.Error(ctx, 400, "invalid_request", "query and documents are required.");
    }

    List<string> documents = [.. docs.Select(x => x is JsonObject obj ? obj["text"]?.ToString() ?? "" : x?.ToString() ?? "")];
    var topN = body["top_n"] is JsonValue n && n.TryGetValue<int>(out var top) && top > 0 ? top : documents.Count;
    var returnDocuments = body["return_documents"] is JsonValue r && r.TryGetValue<bool>(out var rd) && rd;
    var tokens = documents.Sum(d => FakeContent.Tokens(query) + FakeContent.Tokens(d));
    ctx.RequestServices.GetRequiredService<FakeUsage>().Record(ctx, call, model, tokens, 0, FakeCatalog.Cost(model, tokens, 0, 0));
    SetCost(ctx, model, FakeCatalog.Cost(model, tokens, 0, 0));
    return Results.Json(new
    {
        id = "rerank-" + Guid.NewGuid().ToString("N"),
        model = model.Name,
        @object = "reranking",
        results = documents
            .Select((document, index) => (Index: index, Score: FakeContent.Overlap(query, document), Text: document))
            .OrderByDescending(x => x.Score).ThenBy(x => x.Index)
            .Take(topN)
            .Select(x => returnDocuments
                ? (object)new { index = x.Index, relevance_score = x.Score, document = new { text = x.Text } }
                : new { index = x.Index, relevance_score = x.Score }),
        usage = new { prompt_tokens = tokens, total_tokens = tokens },
    });
});

app.Run();

static async Task<JsonObject> ReadAsync(HttpContext ctx) =>
    await JsonNode.ParseAsync(ctx.Request.Body) as JsonObject ?? [];

static async Task<IResult?> FailureAsync(HttpContext ctx, string model)
{
    switch (model)
    {
        case "fake-slow":
            await Task.Delay(TimeSpan.FromSeconds(3), ctx.RequestAborted);
            return null;
        default:
            return null;
    }
}

static async Task<List<float[]>> UpstreamEmbeddingsAsync(HttpClient client, string upstream, string[] inputs, CancellationToken ct)
{
    using var response = await client.PostAsJsonAsync(upstream.TrimEnd('/') + "/embeddings", new { model = "bge-m3", input = inputs }, ct);
    response.EnsureSuccessStatusCode();
    using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    return [.. json.RootElement.GetProperty("data").EnumerateArray()
        .OrderBy(e => e.GetProperty("index").GetInt32())
        .Select(e => e.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray())];
}

// x-ume-cost-usd on a complete JSON answer, as the gateway writes it (eight decimals); never for fake-no-cost.
static void SetCost(HttpContext ctx, FakeModel model, decimal cost)
{
    if (model.Name != FakeCatalog.NoCostModel)
    {
        ctx.Response.Headers[FakeContent.CostHeader] = cost.ToString("0.00000000", System.Globalization.CultureInfo.InvariantCulture);
    }
}

static async Task StartSse(HttpContext ctx)
{
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    await ctx.Response.Body.FlushAsync();
}

static async Task Sse(HttpContext ctx, object payload)
{
    await ctx.Response.WriteAsync("data: " + JsonSerializer.Serialize(payload) + "\n\n");
    await ctx.Response.Body.FlushAsync();
}

/// <summary>Entry point, public for <c>WebApplicationFactory</c> tests.</summary>
public partial class Program;
