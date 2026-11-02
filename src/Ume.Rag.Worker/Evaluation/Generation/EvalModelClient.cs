using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Ume.Rag.Domain;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.Worker.Jobs;

namespace Ume.Rag.Worker.Evaluation.Generation;

/// <summary>One chat answer: the message text (empty when the answer had none) and the tokens the gateway counted.</summary>
public sealed record ChatReply(string Content, int Tokens);

/// <summary>A gateway answer that ends the job: <see cref="Result"/> is what the job ends with.</summary>
public sealed class EvalStopException : Exception
{
    public EvalStopException()
    {
        Result = new JobResult.FatalError("eval.stopped");
    }

    public EvalStopException(string message)
        : base(message)
    {
        Result = new JobResult.FatalError("eval.stopped");
    }

    public EvalStopException(string message, Exception innerException)
        : base(message, innerException)
    {
        Result = new JobResult.FatalError("eval.stopped");
    }

    public EvalStopException(JobResult result)
        : base($"The evaluation set job stopped: {result}.")
    {
        Result = result;
    }

    public JobResult Result { get; }
}

/// <summary>
/// The evaluation models through the worker's gateway client <see cref="HttpClientName"/> (<c>AddGatewayClient</c> with its
/// <see cref="ResidencyCeilingHandler"/>), at the collection's ceiling (<see cref="CeilingFor"/>, processing design §13.1)
/// with the worker's key of that ceiling. <see cref="RequireAtCeilingAsync"/> refuses the job with
/// <see cref="ModelNotAtCeiling"/> unless the gateway lists every model at that ceiling (<see cref="CatalogueClientName"/>);
/// a gateway answer of 403 <c>max_residency_not_met</c> is refused the same way, and an answer from above the ceiling is
/// discarded by the handler.
/// Other status codes map as the embedding client's (engine design §7.3).
/// </summary>
public sealed class EvalModelClient(IHttpClientFactory clients, IOptionsMonitor<GatewayOptions> gateway, TimeProvider clock)
{
    public const string HttpClientName = "gateway-eval";

    /// <summary>
    /// The catalogue read (<c>GET /v1/models</c>): a list of model names, no model output, so the gateway sends no
    /// <c>x-ume-residency</c> and the answer check of <see cref="ResidencyCeilingHandler"/> does not apply. The call still
    /// carries the ceiling and this process's key of that level.
    /// </summary>
    public const string CatalogueClientName = "gateway-eval-models";
    public const string GenerateStep = "eval-generate";
    public const string JudgeStep = "eval-judge";

    public const string ModelNotAtCeiling = "eval.model_not_at_ceiling";
    public const string NoKey = "eval.no_key";
    public const string ModelUnknown = "eval.model_unknown";
    public const string RequestRejected = "eval.request_rejected";
    public const string ResidencyAboveCeiling = "gateway.residency_above_ceiling";

    private static readonly Uri ModelsPath = new("v1/models", UriKind.Relative);
    private static readonly Uri ChatPath = new("v1/chat/completions", UriKind.Relative);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The ceiling of a collection's level (<see cref="ResidencyCeiling.ForLevel"/>), at most <c>Eu</c> because the worker
    /// holds no <c>External</c> key, then lowered by <c>Gateway:MaxResidency</c>.
    /// </summary>
    public Residency CeilingFor(string classification)
    {
        var level = ResidencyCeiling.ForLevel(classification);
        return gateway.CurrentValue.Cap(level < Residency.Eu ? level : Residency.Eu);
    }

    /// <summary>Refuses (<see cref="ModelNotAtCeiling"/>) unless <c>GET /v1/models</c> at <paramref name="ceiling"/> lists every model.</summary>
    public async Task RequireAtCeilingAsync(IReadOnlyCollection<string> models, Residency ceiling, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (gateway.CurrentValue.Keys.GetValueOrDefault(ceiling) is not { Length: > 0 } key)
        {
            throw new EvalStopException(new JobResult.FatalError(NoKey));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, ModelsPath).WithMaxResidency(ceiling);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await SendAsync(CatalogueClientName, request, cancellationToken);
        ModelList? list;
        try
        {
            list = await response.Content.ReadFromJsonAsync<ModelList>(Json, cancellationToken);
        }
        catch (JsonException)
        {
            list = null;
        }

        var listed = list?.Data?.Select(m => m.Id).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
        if (!models.All(listed.Contains))
        {
            throw new EvalStopException(new JobResult.FatalError(ModelNotAtCeiling));
        }
    }

    /// <summary>
    /// One chat completion with JSON output at <paramref name="ceiling"/>, with <c>x-ume-step</c> <paramref name="step"/>. No
    /// <c>temperature</c> is sent: some models refuse it, and the model's default applies.
    /// </summary>
    public async Task<ChatReply> CompleteAsync(string model, string step, Residency ceiling, string system, string user, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ChatPath)
        {
            Content = JsonContent.Create(new ChatRequest(model, [new ChatMessage("system", system), new ChatMessage("user", user)],
                new ResponseFormat("json_object")), options: Json),
        };
        request.WithMaxResidency(ceiling);
        request.Headers.Add(Processing.Embedding.EmbeddingClient.StepHeader, step);
        using var response = await SendAsync(HttpClientName, request, cancellationToken);
        ChatResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<ChatResponse>(Json, cancellationToken);
        }
        catch (JsonException)
        {
            body = null;
        }

        var choice = body?.Choices is { Count: > 0 } choices ? choices[0] : null;
        return new ChatReply(choice?.Message?.Content ?? string.Empty, body?.Usage?.TotalTokens ?? 0);
    }

    private async Task<HttpResponseMessage> SendAsync(string clientName, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(clientName).SendAsync(request, cancellationToken);
        }
        catch (GatewayCallRefusedException)
        {
            // This worker holds no key of the ceiling: the evaluation models cannot be called there.
            throw new EvalStopException(new JobResult.FatalError(NoKey));
        }
        catch (GatewayResidencyException)
        {
            throw new EvalStopException(new JobResult.FatalError(ResidencyAboveCeiling));
        }
        catch (HttpRequestException)
        {
            throw new EvalStopException(new JobResult.TransientError("gateway.unreachable"));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EvalStopException(new JobResult.TransientError("gateway.timeout"));
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw new EvalStopException(await MapAsync(response, cancellationToken));
        }
    }

    private async Task<JobResult> MapAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var code = await ErrorCodeAsync(response, cancellationToken);
        var retryAfter = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date ? date - clock.GetUtcNow() : null);
        var status = "gateway.http_" + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        return response.StatusCode switch
        {
            HttpStatusCode.Forbidden when code == "max_residency_not_met" => new JobResult.FatalError(ModelNotAtCeiling),
            HttpStatusCode.Forbidden => new JobResult.FatalError("gateway." + (code ?? "forbidden")),
            HttpStatusCode.NotFound => new JobResult.FatalError(ModelUnknown),
            HttpStatusCode.BadRequest => new JobResult.FatalError(RequestRejected),
            HttpStatusCode.TooManyRequests => new JobResult.TransientError("gateway.rate_limited", retryAfter ?? TimeSpan.FromSeconds(5), CountsAttempt: false),
            HttpStatusCode.ServiceUnavailable when retryAfter is not null => new JobResult.TransientError("gateway.unavailable", retryAfter, CountsAttempt: false),
            HttpStatusCode.PaymentRequired => new JobResult.TransientError("gateway.budget_exhausted", UntilNextHour()),
            >= HttpStatusCode.InternalServerError => new JobResult.TransientError(status, retryAfter),
            _ => new JobResult.FatalError(status),
        };
    }

    private TimeSpan UntilNextHour()
    {
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(1) - now;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                && code.GetString() is { } value && System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-z0-9_]{1,60}$"))
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // Not the OpenAI error shape.
        }

        return null;
    }

    private sealed record ChatRequest(string Model, IReadOnlyList<ChatMessage> Messages,
        [property: JsonPropertyName("response_format")] ResponseFormat ResponseFormat);

    private sealed record ChatMessage(string Role, string Content);

    private sealed record ResponseFormat(string Type);

    private sealed record ChatResponse(IReadOnlyList<ChatChoice>? Choices, ChatUsage? Usage);

    private sealed record ChatChoice(ChatAnswer? Message);

    private sealed record ChatAnswer(string? Content);

    private sealed record ChatUsage([property: JsonPropertyName("total_tokens")] int? TotalTokens);

    private sealed record ModelList(IReadOnlyList<ModelItem>? Data);

    private sealed record ModelItem(string? Id);
}
