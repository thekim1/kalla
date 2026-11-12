using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.Pipelines.Jobs;
using Ume.Rag.Worker.Jobs;
using Ume.Rag.Worker.Processing.Persistence;

namespace Ume.Rag.Worker.Processing.Embedding;

/// <summary>
/// Who an embedding call is for (engine design §7.7 headers, §10.6): the run id it carries as <c>x-ume-run-id</c> (the run's,
/// the evaluation run's, or the job's id for set generation and preview), where its usage is counted, and the step type.
/// </summary>
public sealed record EmbeddingCall(Guid RunId, UsageSink Usage, string Step = EmbeddingClient.EmbedStep);

/// <summary>
/// <c>POST /v1/embeddings</c> through the worker's gateway client <see cref="HttpClientName"/> (<c>AddGatewayClient</c> with its
/// <see cref="ResidencyCeilingHandler"/>): the profile's model, the profile's ceiling as <c>x-ume-max-residency</c> (so the
/// worker's key of that level, <c>Eu</c> for the P1a pilot), <c>dimensions</c> when the profile requests it,
/// <c>x-ume-run-id</c> and <c>x-ume-step</c>. The answer is checked before anything is stored: one vector per input in input
/// order, each of the profile's dimensions and finite (else fatal <c>embed.bad_vector</c>); the residency is checked by the
/// handler (above the ceiling: fatal, event 5003). The call runs as the knowledge base's gateway key
/// (<see cref="UsageOwner.Acting"/>, engine design §7.7): a refusal of the key waits until the next full hour with an attempt
/// counted (<see cref="GatewayRefusals"/>), and an answer without the key's echo is fatal <c>gateway.delegation_not_applied</c>
/// before a vector is read (event 5011 by the handler). Status codes map per engine design §7.3. Every answered call's usage
/// (<c>usage.prompt_tokens</c> and <c>x-ume-cost-usd</c>, <see cref="GatewayUsageReader"/>) goes to the call's
/// <see cref="EmbeddingCall.Usage"/> before the vectors are checked, since the gateway charged it; an error answer records nothing.
/// </summary>
public sealed class EmbeddingClient(IHttpClientFactory clients, TimeProvider clock)
{
    public const string HttpClientName = "gateway-embed";
    public const string RunIdHeader = "x-ume-run-id";
    public const string StepHeader = "x-ume-step";
    public const string EmbedStep = "embed";

    private static readonly Uri EmbeddingsPath = new("v1/embeddings", UriKind.Relative);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<float[]>> EmbedAsync(EmbeddingProfileRecord profile, IReadOnlyList<string> inputs, EmbeddingCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(call);
        using var request = new HttpRequestMessage(HttpMethod.Post, EmbeddingsPath)
        {
            Content = JsonContent.Create(new EmbeddingRequest(profile.GatewayModel, inputs, "float", profile.DimensionsParameter), options: Json),
        };
        request.WithMaxResidency(profile.MaxResidency);
        request.Headers.Add(RunIdHeader, call.RunId.ToString());
        request.Headers.Add(StepHeader, call.Step);
        request.For(call.Usage.Owner.Acting(call.Step));
        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        }
        catch (GatewayResidencyException)
        {
            throw new StageFatalException(RunItemOutcome.Failed, "gateway.residency_above_ceiling");
        }
        catch (GatewayKeyRefusedException refused)
        {
            throw new StageTransientException(GatewayRefusals.Reason(refused), GatewayRefusals.UntilNextHour(clock), countsAttempt: true);
        }
        catch (GatewayDelegationEchoException)
        {
            throw new StageFatalException(RunItemOutcome.Failed, GatewayRefusals.DelegationNotApplied);
        }
        catch (HttpRequestException)
        {
            throw new StageTransientException("gateway.unreachable", null, countsAttempt: true);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new StageTransientException("gateway.timeout", null, countsAttempt: true);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await MapAsync(response, cancellationToken);
            }

            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            }
            catch (JsonException)
            {
                await call.Usage.RecordAsync(new GatewayCallUsage(call.Step, profile.GatewayModel, default(GatewayTokens), GatewayUsageReader.CostHeader(response)),
                    cancellationToken);
                throw new StageFatalException(RunItemOutcome.Failed, "embed.bad_vector");
            }

            using (document)
            {
                await call.Usage.RecordAsync(GatewayUsageReader.Read(response, document.RootElement, call.Step, profile.GatewayModel), cancellationToken);
                EmbeddingResponse? body;
                try
                {
                    body = document.RootElement.Deserialize<EmbeddingResponse>(Json);
                }
                catch (JsonException)
                {
                    throw new StageFatalException(RunItemOutcome.Failed, "embed.bad_vector");
                }

                return Check(body, inputs.Count, profile.Dimensions);
            }
        }
    }

    /// <summary>One vector per input, in input order, each <paramref name="dimensions"/> finite floats.</summary>
    internal static IReadOnlyList<float[]> Check(EmbeddingResponse? body, int inputs, int dimensions)
    {
        if (body?.Data is not { } data || data.Count != inputs)
        {
            throw new StageFatalException(RunItemOutcome.Failed, "embed.bad_vector");
        }

        var vectors = new float[inputs][];
        foreach (var item in data)
        {
            if (item.Index is < 0 || item.Index >= inputs || vectors[item.Index] is not null || item.Embedding is not { } vector
                || vector.Length != dimensions || vector.Any(v => !float.IsFinite(v)))
            {
                throw new StageFatalException(RunItemOutcome.Failed, "embed.bad_vector");
            }

            vectors[item.Index] = vector;
        }

        return vectors;
    }

    private async Task<Exception> MapAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var code = await ErrorCodeAsync(response, cancellationToken);
        var retryAfter = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date ? date - clock.GetUtcNow() : null);
        return response.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => new StageTransientException("gateway.rate_limited", retryAfter ?? TimeSpan.FromSeconds(5), countsAttempt: false),
            HttpStatusCode.ServiceUnavailable when retryAfter is not null => new StageTransientException("gateway.unavailable", retryAfter, countsAttempt: false),
            HttpStatusCode.PaymentRequired => new StageTransientException(GatewayRefusals.BudgetExhausted, GatewayRefusals.UntilNextHour(clock), countsAttempt: true),
            HttpStatusCode.Forbidden => new StageFatalException(RunItemOutcome.Failed, "gateway." + (code ?? "forbidden")),
            HttpStatusCode.BadRequest => new StageFatalException(RunItemOutcome.Failed, "embed.input_rejected"),
            >= HttpStatusCode.InternalServerError => new StageTransientException("gateway.http_" + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), retryAfter, countsAttempt: true),
            _ => new StageFatalException(RunItemOutcome.Failed, "gateway.http_" + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)),
        };
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
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

    /// <summary><c>dimensions</c> only for a profile that requests it (#142); BGE-M3 must never get it.</summary>
    private sealed record EmbeddingRequest(
        string Model, IReadOnlyList<string> Input, [property: JsonPropertyName("encoding_format")] string EncodingFormat,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Dimensions = null);

    internal sealed record EmbeddingResponse(IReadOnlyList<EmbeddingItem>? Data);

    internal sealed record EmbeddingItem(int Index, float[]? Embedding);
}
