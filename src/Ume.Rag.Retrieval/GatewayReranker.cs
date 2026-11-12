using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Ume.Rag.Infrastructure.Gateway;

namespace Ume.Rag.Retrieval;

/// <summary>
/// The reranker on the gateway's optional rerank endpoint (B-16; Decisions "Gateway release 1 and shared Keycloak as
/// built"; gateway-changes §7.4): one Cohere-compatible <c>POST /v1/rerank</c> with the platform's rerank model
/// (<c>Settings.Retrieval.gatewayRerankModel</c>; <c>Cohere-rerank-v5-fast</c> on Azure AI Foundry (EU) for the POC,
/// on-prem later) at the call's ceiling (<see cref="RerankRequest.Ceiling"/>, sent as <c>x-ume-max-residency</c>; the gateway client's
/// <see cref="ResidencyCeilingHandler"/> adds the level's key and checks <c>x-ume-residency</c>), the query and the passages as
/// strings. The answer's <c>results[]</c> (<c>index</c>, <c>relevance_score</c>) are the scores; a passage without a result
/// stays unscored. Usage is billed per input token (<c>usage.prompt_tokens</c>, <c>usage.total_tokens</c> or Cohere's
/// <c>meta.billed_units.input_tokens</c>) and the cost is the gateway's <c>x-ume-cost-usd</c>
/// (<see cref="GatewayUsageReader"/>).
/// <para>
/// Rerank is optional: the engine calls this only when <c>gatewayRerankModel</c> is set. A gateway without that model
/// answers 404 (unknown model) or 400 (a model of another kind); that, any other refusal and a timeout throw, so the engine
/// keeps the fused order (<c>rerank_unavailable</c>) and the answer goes on. Each kind of failure is logged once per model
/// until a call succeeds again, so a missing model does not log on every search. The query and passages are never logged.
/// </para>
/// </summary>
public sealed partial class GatewayReranker(IHttpClientFactory clients, string httpClientName = ChatModelReranker.HttpClientName, ILogger<GatewayReranker>? logger = null)
    : IReranker
{
    /// <summary>The <c>x-ume-step</c> of the call.</summary>
    public const string Step = "rerank";

    private readonly ILogger _logger = logger ?? NullLogger<GatewayReranker>.Instance;

    // The failures already logged, by model and reason; cleared when a call succeeds.
    private readonly ConcurrentDictionary<(string Model, string Reason), byte> _logged = new();

    public async Task<RerankScores?> ScoreAsync(RerankRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            throw new InvalidOperationException("Settings.Retrieval.gatewayRerankModel is not set.");
        }

        if (request.Passages.Count == 0)
        {
            return new RerankScores([]);
        }

        var model = request.Model;
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri("v1/rerank", UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                model,
                query = request.Query,
                documents = request.Passages.Select(p => p.Text).ToArray(),
                return_documents = false,
            }),
        }.WithMaxResidency(request.Ceiling);
        message.Headers.Add("x-ume-step", Step);
        if (request.RunId is { } runId)
        {
            message.Headers.Add("x-ume-run-id", runId.ToString());
        }

        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(httpClientName).SendAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (First(model, "timeout"))
            {
                LogTimedOut(_logger, model);
            }

            throw;
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                if (First(model, status.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                {
                    if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                    {
                        LogNotServed(_logger, model, status);
                    }
                    else
                    {
                        LogRefused(_logger, model, status);
                    }
                }

                throw new HttpRequestException($"The gateway answered the rerank call with {status}.", null, response.StatusCode);
            }

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var scores = Read(json.RootElement, request.Passages.Count);
            if (!_logged.IsEmpty)
            {
                _logged.Clear();
            }

            return new RerankScores(scores, GatewayUsageReader.Read(response, json.RootElement, Step, model));
        }
    }

    /// <summary>
    /// The scores in passage order from a Cohere-compatible answer: <c>results[]</c> with <c>index</c> (0-based, the
    /// documents' order) and a finite <c>relevance_score</c>; an entry that is not that, or repeats an index, is skipped, and a
    /// passage without an entry is null (unscored). No <c>results</c> list or no score at all throws
    /// <see cref="RerankOutputException"/>.
    /// </summary>
    public static IReadOnlyList<double?> Read(JsonElement answer, int count)
    {
        if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            throw new RerankOutputException("The rerank answer has no results list.");
        }

        var scores = new double?[count];
        foreach (var result in results.EnumerateArray())
        {
            if (result.ValueKind == JsonValueKind.Object &&
                result.TryGetProperty("index", out var index) && index.ValueKind == JsonValueKind.Number && index.TryGetInt32(out var i) && i >= 0 && i < count &&
                result.TryGetProperty("relevance_score", out var score) && score.ValueKind == JsonValueKind.Number && score.TryGetDouble(out var s) && double.IsFinite(s) &&
                scores[i] is null)
            {
                scores[i] = s;
            }
        }

        if (scores.All(s => s is null))
        {
            throw new RerankOutputException("The rerank answer scores no passage.");
        }

        return scores;
    }

    private bool First(string model, string reason) => _logged.TryAdd((model, reason), 0);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The gateway does not serve rerank model {Model} (HTTP {Status}); searches that rerank with it keep the fused order. Set Settings.Retrieval.gatewayRerankModel to a model of kind Rerank, or clear it")]
    private static partial void LogNotServed(ILogger logger, string model, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The gateway refused a rerank call with {Model}: HTTP {Status}; the fused order stands")]
    private static partial void LogRefused(ILogger logger, string model, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A rerank call with {Model} timed out or was cancelled; the fused order stands")]
    private static partial void LogTimedOut(ILogger logger, string model);
}
