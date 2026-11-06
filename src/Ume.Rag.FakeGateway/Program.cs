using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Ume.Rag.FakeGateway;

// OpenAI-compatible fake of the team's LLM gateway for local runs and tests (repo-and-deployment §4.3). Behaves like
// gateway release 1: per-level dev keys that require x-ume-max-residency, x-ume-residency on every answer, the 403/503
// split, /v1/models filtered by the ceiling, and the Data API catalogue. Behaviour by model name (appsettings.json):
//   fake-fail-500 → 500; fake-slow → 3 s delay; fake-wrong-residency → answers with x-ume-residency External;
//   fake-drained → 503 + Retry-After (its in-ceiling targets are drained);
//   fake-stream-broken | -stall | -no-done | -empty-deltas | -length → streaming edge cases (chat design §18.4).
// With stream_options.include_usage the last chunk carries usage only, as OpenAI's do. NOT for production.
if (await WebDefaults.ExitIfHealthProbeAsync(args))
{
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddOptions<FakeGatewayOptions>().BindConfiguration(FakeGatewayOptions.SectionName);
var latency = builder.Configuration.GetSection(FakeLatency.SectionName).Get<FakeLatency>() ?? new FakeLatency();
builder.Services.AddHttpClient("upstream").RemoveStandardResilienceHandler();
builder.Services.AddSingleton<FakeChatScripts>();

var app = builder.Build();
app.MapDefaultEndpoints();
app.MapGet("/", () => "Ume.Rag fake gateway – endast för test och utveckling.");

app.MapGet("/v1/models", (HttpContext ctx, IOptionsSnapshot<FakeGatewayOptions> options) =>
{
    var o = options.Value;
    if (FakeAccess.Authorize(ctx, o, out var error) is not { } call)
    {
        return error!;
    }

    // As the gateway from release 1: only models with an available target within the ceiling (gateway design §3.4).
    return Results.Json(new
    {
        @object = "list",
        data = o.Models
            .Select(m => (Model: m, Residencies: FakeAccess.ServableResidencies(m, call)))
            .Where(m => m.Residencies.Length > 0)
            .Select(m => new
            {
                id = m.Model.Name,
                @object = "model",
                owned_by = "ume-fake",
                ume = new { kind = m.Model.Kind, residencies = m.Residencies.Select(Ume.Rag.Domain.ResidencyCeiling.ToHeader) },
            }),
    });
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
    if (FakeAccess.Route(ctx, o, call, modelName, "chat", out error) is not { } model)
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
    var completionTokens = FakeContent.Tokens(answer);
    var id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..12];
    var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var finishReason = model.Name == "fake-stream-length" ? "length" : "stop";

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
            await Sse(ctx, new { id, @object = "chat.completion.chunk", created, model = model.Name, choices = Array.Empty<object>(), usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens, total_tokens = promptTokens + completionTokens } });
        }

        if (model.Name != "fake-stream-no-done")
        {
            await ctx.Response.WriteAsync("data: [DONE]\n\n");
        }

        return Results.Empty;
    }

    await latency.WholeAnswerAsync(FakeContent.StreamWords(answer).Count(), promptTokens, ctx.RequestAborted);
    return Results.Json(new
    {
        id,
        @object = "chat.completion",
        created,
        model = model.Name,
        choices = new[] { new { index = 0, message = new { role = "assistant", content = answer }, finish_reason = finishReason } },
        usage = new { prompt_tokens = promptTokens, completion_tokens = completionTokens, total_tokens = promptTokens + completionTokens, prompt_tokens_details = new { cached_tokens = 0 } },
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
    if (FakeAccess.Route(ctx, o, call, modelName, "embedding", out error) is not { } model)
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
        return FakeAccess.Error(400, "invalid_request", "invalid_request_error", "'input' must be a string or an array of strings.");
    }

    // Dimensions (gateway design §6): the OpenAI parameter, else a "-<n>" model suffix, else 1024 (BGE-M3).
    var dimensions = FakeContent.DefaultDimensions;
    if (body["dimensions"] is JsonValue d)
    {
        dimensions = d.GetValue<int>();
        if (dimensions is < 1 or > 4096)
        {
            return FakeAccess.Error(400, "invalid_request", "invalid_request_error", "'dimensions' must be between 1 and 4096.");
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
        usage = new { prompt_tokens = inputs.Sum(FakeContent.Tokens), total_tokens = inputs.Sum(FakeContent.Tokens) },
    });
});

// Provisional shape (gateway design §7.4: path and shape are agreed with the gateway team before P2): Cohere/Jina style.
app.MapPost("/v1/rerank", async (HttpContext ctx, IOptionsSnapshot<FakeGatewayOptions> options) =>
{
    var o = options.Value;
    if (FakeAccess.Authorize(ctx, o, out var error) is not { } call)
    {
        return error!;
    }

    var body = await ReadAsync(ctx);
    if (FakeAccess.Route(ctx, o, call, body["model"]?.GetValue<string>(), "rerank", out error) is not { } model)
    {
        return error!;
    }

    var query = body["query"]?.GetValue<string>() ?? "";
    List<string> documents = body["documents"] is JsonArray docs
        ? [.. docs.Select(x => x is JsonObject obj ? obj["text"]?.GetValue<string>() ?? "" : x?.GetValue<string>() ?? "")]
        : [];
    var topN = body["top_n"]?.GetValue<int>() ?? documents.Count;
    return Results.Json(new
    {
        model = model.Name,
        results = documents
            .Select((text, index) => new { index, relevance_score = FakeContent.Overlap(query, text) })
            .OrderByDescending(r => r.relevance_score).ThenBy(r => r.index)
            .Take(topN),
        usage = new { total_tokens = FakeContent.Tokens(query) + documents.Sum(FakeContent.Tokens) },
    });
});

// Data API catalogue (gateway docs/data-access.md, catalog.read); memberGroups and code come with gateway release 2.
var catalog = app.MapGroup("/v1/catalog");
catalog.AddEndpointFilter(async (context, next) =>
{
    var key = context.HttpContext.RequestServices.GetRequiredService<IOptionsSnapshot<FakeGatewayOptions>>().Value.DataApiKey;
    var presented = FakeAccess.BearerOrApiKey(context.HttpContext.Request);
    return presented is null || (key is not null && !FakeAccess.FixedTimeEquals(key, presented))
        ? Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized")
        : await next(context);
});
catalog.MapGet("/departments", () => Results.Json(new { items = FakeCatalog.Departments }, FakeCatalog.Json));
catalog.MapGet("/teams", () => Results.Json(new { items = FakeCatalog.Teams }, FakeCatalog.Json));
// The model catalogue as the gateway's DataEndpoints.cs serves it: one provider per residency, one deployment per
// single-target chat or embedding model (a multi-target name is a route alias, not in the catalogue), and its price.
catalog.MapGet("/providers", () => Results.Json(new { items = FakeCatalog.Providers }, FakeCatalog.Json));
catalog.MapGet("/models", (IOptionsSnapshot<FakeGatewayOptions> options) =>
    Results.Json(new { items = FakeCatalog.Models(options.Value.Models) }, FakeCatalog.Json));
catalog.MapGet("/prices", (IOptionsSnapshot<FakeGatewayOptions> options) =>
    Results.Json(new { items = FakeCatalog.Prices(options.Value.Models) }, FakeCatalog.Json));

app.Run();

static async Task<JsonObject> ReadAsync(HttpContext ctx) =>
    await JsonNode.ParseAsync(ctx.Request.Body) as JsonObject ?? [];

static async Task<IResult?> FailureAsync(HttpContext ctx, string model)
{
    switch (model)
    {
        case "fake-fail-500":
            return FakeAccess.Error(StatusCodes.Status500InternalServerError, "provider_error", "server_error", "Fake provider failure");
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
