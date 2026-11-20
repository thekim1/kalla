using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kalla.Domain;

namespace Kalla.Infrastructure.Gateway;

/// <summary>
/// The admin's and the status page's gateway calls (gateway-changes §13.4, identity §4.7) on a client registered with
/// <c>AddGatewayClient(GatewayApi.HttpClientName)</c>, so every call carries an explicit ceiling and the level's key and its
/// answer's <c>x-ume-residency</c> is checked by <see cref="ResidencyCeilingHandler"/>. P1 sends no
/// <c>x-ume-on-behalf-of</c> (gateway release 2).
/// </summary>
public sealed class GatewayApi(IHttpClientFactory clients)
{
    public const string HttpClientName = "gateway-admin";

    /// <summary>
    /// How long <see cref="ListModelsAsync"/> waits for the list (#696): the admin's model checks and pickers wait on it, some of them
    /// before a config write. The client's other calls (the model mirror, canary embeddings) keep HttpClient's timeout.
    /// </summary>
    public static readonly TimeSpan ListModelsTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The gateway's discovery answers are snake_case (<c>contracts/gateway/integrations.json</c>).</summary>
    private static readonly JsonSerializerOptions SnakeCase = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>
    /// <c>GET /v1/ume/context</c> with this service's credential key (gateway-changes §5.5): no ceiling, since no model
    /// answers it. With <paramref name="etag"/> it sends <c>If-None-Match</c>, and an unchanged context comes back as
    /// <see cref="GatewayContextAnswer.Context"/> null (<c>304</c>).
    /// </summary>
    public async Task<GatewayContextAnswer> GetContextAsync(string? etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("v1/ume/context", UriKind.Relative));
        if (!string.IsNullOrEmpty(etag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        using var response = await SendAsync(request, cancellationToken);
        var tag = response.Headers.ETag?.ToString() ?? etag;
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new GatewayContextAnswer(null, tag);
        }

        var context = await response.Content.ReadFromJsonAsync<GatewayContext>(SnakeCase, cancellationToken)
            ?? throw new GatewayUnavailableException("The gateway's context was empty.", null);
        return new GatewayContextAnswer(context, tag);
    }

    /// <summary>
    /// <c>GET /v1/models</c> at <paramref name="ceiling"/>: the model ids the gateway will serve with this level's key (gateway
    /// release 1 lists only models with an available target within the ceiling, gateway-changes §3.4; before release 1 the
    /// key's <c>AllowedResidencies</c> narrows it).
    /// </summary>
    /// <remarks>
    /// With <paramref name="asKey"/> (gateway phase 2) the list is the assigned key's, within both keys' residencies and the
    /// ceiling (gateway-changes §5.5), while the gateway acts as keys; a refusal of the key throws <see cref="GatewayKeyRefusedException"/>
    /// and a missing echo <see cref="GatewayDelegationEchoException"/>. An answer that takes longer than <see cref="ListModelsTimeout"/>
    /// throws <see cref="GatewayUnavailableException"/>.
    /// </remarks>
    public async Task<IReadOnlySet<string>> ListModelsAsync(Residency ceiling, CancellationToken cancellationToken, Guid? asKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("v1/models", UriKind.Relative)).WithMaxResidency(ceiling);
        if (asKey is { } key)
        {
            request.For(new GatewayCall(key, "models"));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ListModelsTimeout);
        try
        {
            using var response = await SendAsync(request, timeout.Token);
            var list = await response.Content.ReadFromJsonAsync<ModelList>(Json, timeout.Token)
                ?? throw new GatewayUnavailableException("The gateway's model list was empty.", null);
            return list.Data?.Select(m => m.Id).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GatewayUnavailableException("The gateway did not list its models in time.", ex);
        }
    }

    /// <summary>
    /// <c>GET /v1/models</c> at <paramref name="ceiling"/> with the gateway's facts (<c>ume</c>, gateway-changes §5.5), for the
    /// model mirror. With <paramref name="etag"/> it sends <c>If-None-Match</c>, and an unchanged list comes back as
    /// <see cref="GatewayModelListAnswer.Models"/> null (<c>304</c>).
    /// </summary>
    public async Task<GatewayModelListAnswer> GetModelListAsync(Residency ceiling, string? etag, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("v1/models", UriKind.Relative)).WithMaxResidency(ceiling);
        if (!string.IsNullOrEmpty(etag))
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        using var response = await SendAsync(request, cancellationToken);
        var tag = response.Headers.ETag?.ToString() ?? etag;
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new GatewayModelListAnswer(null, tag);
        }

        var list = await response.Content.ReadFromJsonAsync<FactsList>(SnakeCase, cancellationToken)
            ?? throw new GatewayUnavailableException("The gateway's model list was empty.", null);
        return new GatewayModelListAnswer([.. (list.Data ?? []).Where(m => m.Fors is not null)], tag);
    }

    /// <summary><c>POST /v1/embeddings</c> of <paramref name="inputs"/> with <paramref name="model"/> at <paramref name="ceiling"/>.</summary>
    public async Task<GatewayEmbedding> EmbedAsync(string model, IReadOnlyList<string> inputs, Residency ceiling, CancellationToken cancellationToken,
        int? dimensions = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(model);
        ArgumentNullException.ThrowIfNull(inputs);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("v1/embeddings", UriKind.Relative))
        {
            Content = JsonContent.Create(new EmbeddingRequest(model, inputs, "float", dimensions), options: Json),
        }.WithMaxResidency(ceiling);
        var started = Stopwatch.GetTimestamp();
        using var response = await SendAsync(request, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<GatewayEmbeddingResponse>(Json, cancellationToken);
        var vectors = body?.Data?.OrderBy(d => d.Index).Select(d => d.Embedding ?? []).ToList();
        if (vectors is null || vectors.Count != inputs.Count)
        {
            throw new GatewayUnavailableException("The gateway answered with a different number of embeddings.", null);
        }

        var observed = response.Headers.TryGetValues(GatewayHeaders.Residency, out var values) && ResidencyCeiling.TryParse(values.First(), out var r)
            ? r
            : ceiling; // the handler has already refused a missing or higher residency
        return new GatewayEmbedding(vectors, observed, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>Sends; a refusal or failure throws <see cref="GatewayRefusedException"/> or <see cref="GatewayUnavailableException"/>.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is GatewayResidencyException or GatewayKeyRefusedException or GatewayDelegationEchoException)
        {
            throw; // the caller maps them (identity §4.7, gateway-changes §5.7)
        }
        catch (HttpRequestException ex)
        {
            throw new GatewayUnavailableException("The gateway did not answer.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GatewayUnavailableException("The gateway did not answer in time.", ex);
        }

        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotModified)
        {
            return response; // 304 only answers a request with If-None-Match (the context)
        }

        using (response)
        {
            var code = await ErrorCodeAsync(response, cancellationToken);
            // 503 (+ Retry-After) and 5xx are retryable; 4xx (401, 403 max_residency_not_met, 404 unknown model) are not.
            throw (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests
                ? new GatewayUnavailableException($"The gateway answered {(int)response.StatusCode} {code}.", null) { Status = (int)response.StatusCode, Code = code }
                : new GatewayRefusedException((int)response.StatusCode, code);
        }
    }

    /// <summary>The OpenAI error shape's <c>error.code</c>, or null.</summary>
    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorEnvelope>(Json, cancellationToken);
            return error?.Error?.Code;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ModelList(IReadOnlyList<ModelItem>? Data);

    private sealed record FactsList(IReadOnlyList<GatewayListedModel>? Data);

    private sealed record ModelItem(string? Id);

    /// <summary><c>dimensions</c> only for a profile that requests it (#142); BGE-M3 must never get it.</summary>
    private sealed record EmbeddingRequest(
        string Model, IReadOnlyList<string> Input, [property: JsonPropertyName("encoding_format")] string EncodingFormat,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Dimensions = null);

    private sealed record ErrorEnvelope(ErrorBody? Error);

    private sealed record ErrorBody(string? Code);
}

/// <summary>Embeddings in input order, the residency that answered, and the call's duration.</summary>
public sealed record GatewayEmbedding(IReadOnlyList<float[]> Vectors, Residency Residency, TimeSpan Duration);

/// <summary>
/// The body of a <c>POST /v1/embeddings</c> answer, read in one pass by every caller: the vectors and the raw <c>usage</c>
/// object, for <see cref="GatewayUsageReader.UsageTokens"/>.
/// </summary>
public sealed record GatewayEmbeddingResponse(IReadOnlyList<GatewayEmbeddingItem>? Data, JsonElement? Usage);

/// <summary>One vector of a <see cref="GatewayEmbeddingResponse"/> and the index of its input.</summary>
public sealed record GatewayEmbeddingItem(int Index, float[]? Embedding);

/// <summary>The gateway was unreachable, timed out or answered 5xx/429: retry later.</summary>
public sealed class GatewayUnavailableException(string message, Exception? inner) : Exception(message, inner)
{
    public int? Status { get; init; }
    public string? Code { get; init; }
}

/// <summary>The gateway refused the call (4xx): <c>max_residency_not_met</c>, an unknown model, a key refused.</summary>
public sealed class GatewayRefusedException(int status, string? code)
    : Exception($"The gateway refused the call: {status} {code ?? "(no code)"}.")
{
    public int Status { get; } = status;
    public string? Code { get; } = code;
}
