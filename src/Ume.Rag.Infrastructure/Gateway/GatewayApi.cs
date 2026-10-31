using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ume.Rag.Domain;

namespace Ume.Rag.Infrastructure.Gateway;

/// <summary>
/// The admin's and the status page's gateway calls (gateway-changes §13.4, identity §4.7) on a client registered with
/// <c>AddGatewayClient(GatewayApi.HttpClientName)</c>, so every call carries an explicit ceiling and the level's key and its
/// answer's <c>x-ume-residency</c> is checked by <see cref="ResidencyCeilingHandler"/>. P1 sends no
/// <c>x-ume-on-behalf-of</c> (gateway release 2).
/// </summary>
public sealed class GatewayApi(IHttpClientFactory clients)
{
    public const string HttpClientName = "gateway-admin";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// <c>GET /v1/models</c> at <paramref name="ceiling"/>: the model ids the gateway will serve with this level's key (gateway
    /// release 1 lists only models with an available target within the ceiling, gateway-changes §3.4; before release 1 the
    /// key's <c>AllowedResidencies</c> narrows it).
    /// </summary>
    public async Task<IReadOnlySet<string>> ListModelsAsync(Residency ceiling, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("v1/models", UriKind.Relative)).WithMaxResidency(ceiling);
        using var response = await SendAsync(request, cancellationToken);
        var list = await response.Content.ReadFromJsonAsync<ModelList>(Json, cancellationToken)
            ?? throw new GatewayUnavailableException("The gateway's model list was empty.", null);
        return list.Data?.Select(m => m.Id).OfType<string>().ToHashSet(StringComparer.Ordinal) ?? [];
    }

    /// <summary><c>POST /v1/embeddings</c> of <paramref name="inputs"/> with <paramref name="model"/> at <paramref name="ceiling"/>.</summary>
    public async Task<GatewayEmbedding> EmbedAsync(string model, IReadOnlyList<string> inputs, Residency ceiling, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(model);
        ArgumentNullException.ThrowIfNull(inputs);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("v1/embeddings", UriKind.Relative))
        {
            Content = JsonContent.Create(new EmbeddingRequest(model, inputs, "float"), options: Json),
        }.WithMaxResidency(ceiling);
        var started = Stopwatch.GetTimestamp();
        using var response = await SendAsync(request, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(Json, cancellationToken);
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
        catch (GatewayResidencyException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new GatewayUnavailableException("The gateway did not answer.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GatewayUnavailableException("The gateway did not answer in time.", ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
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

    private sealed record ModelItem(string? Id);

    private sealed record EmbeddingRequest(string Model, IReadOnlyList<string> Input, [property: JsonPropertyName("encoding_format")] string EncodingFormat);

    private sealed record EmbeddingResponse(IReadOnlyList<EmbeddingItem>? Data);

    private sealed record EmbeddingItem(int Index, float[]? Embedding);

    private sealed record ErrorEnvelope(ErrorBody? Error);

    private sealed record ErrorBody(string? Code);
}

/// <summary>Embeddings in input order, the residency that answered, and the call's duration.</summary>
public sealed record GatewayEmbedding(IReadOnlyList<float[]> Vectors, Residency Residency, TimeSpan Duration);

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
