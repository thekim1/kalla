using System.Globalization;
using System.Text.Json;
using Kalla.Domain.Access;
using Kalla.Domain.Entities;

namespace Kalla.Infrastructure.Gateway;

/// <summary>
/// The tokens a gateway answer reports (chat design §8.3): input (<c>usage.prompt_tokens</c>, cached ones included, as
/// OpenAI counts them), the cached part of it (<c>usage.prompt_tokens_details.cached_tokens</c>) and output
/// (<c>usage.completion_tokens</c>; none for an embedding or a rerank).
/// </summary>
public readonly record struct GatewayTokens(long InputTokens, long CachedInputTokens, long OutputTokens);

/// <summary>
/// What one model call cost as the gateway reported it (chat design §8.3; cost-attribution §5): the <c>x-ume-step</c> it
/// sent, the model asked for, its tokens and the gateway's cost in USD (<c>x-ume-cost-usd</c> or the stream comment),
/// null when the gateway gave none (an aborted stream, a gateway without the feature, <c>fake-no-cost</c>). Turned into a
/// <see cref="UsageRecord"/> with the model's mirrored prices.
/// </summary>
public sealed record GatewayCallUsage(string Step, string Model, long InputTokens, long CachedInputTokens, long OutputTokens, decimal? CostUsd)
{
    public GatewayCallUsage(string step, string model, GatewayTokens tokens, decimal? costUsd)
        : this(step, model, tokens.InputTokens, tokens.CachedInputTokens, tokens.OutputTokens, costUsd)
    {
    }
}

/// <summary>
/// A model's prices per million tokens as the mirror holds them (<c>config."GatewayModels"</c>, data-model §2.1), for the
/// estimate of a call without a gateway cost (chat design §8.3).
/// </summary>
public sealed record ModelPrices(decimal? InputPerMillionUsd, decimal? CachedInputPerMillionUsd, decimal? OutputPerMillionUsd)
{
    private const decimal Million = 1_000_000m;

    /// <summary>The mirrored prices of <paramref name="model"/>.</summary>
    public static ModelPrices Of(GatewayModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new ModelPrices(model.InputPerMillionUsd, model.CachedInputPerMillionUsd, model.OutputPerMillionUsd);
    }

    /// <summary>The prices of a model as the config snapshot holds them (<see cref="PolicySnapshot.ModelPrices"/>; rag-api reads no row per call).</summary>
    public static ModelPrices Of(ModelPricesEntry model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new ModelPrices(model.InputPerMillionUsd, model.CachedInputPerMillionUsd, model.OutputPerMillionUsd);
    }

    /// <summary>
    /// The estimate in USD (chat design §8.3), rounded to eight decimals as the gateway rounds: (input − cached) × input
    /// price + cached × cached price (the input price when that is null) + output × output price. A missing price counts 0,
    /// so a model without prices costs 0; cached tokens count at most the input, and negative counts as 0.
    /// </summary>
    public decimal Estimate(long inputTokens, long cachedInputTokens, long outputTokens)
    {
        var input = Math.Max(inputTokens, 0);
        var cached = Math.Clamp(cachedInputTokens, 0, input);
        var output = Math.Max(outputTokens, 0);
        var inputPrice = InputPerMillionUsd ?? 0m;
        var usd = ((input - cached) * inputPrice + cached * (CachedInputPerMillionUsd ?? inputPrice) + output * (OutputPerMillionUsd ?? 0m)) / Million;
        return Math.Round(usd, 8, MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// The usage record of one model call (chat design §8.3, §12.1; data-model §2.2, §2.3): its step and model, input,
/// cached input and output tokens, and its cost: the gateway's in <see cref="CostUsd"/>, or, when the gateway gave none,
/// the estimate from the mirror's prices in <see cref="EstimatedCostUsd"/> with <see cref="Estimated"/> set. The two are
/// never mixed: one of them is always 0. A row of <c>AssistantDailyUsage</c> or <c>CollectionDailyUsage</c> sums these
/// per (day, object, step): <c>Calls</c> + 1, <c>EstimatedCalls</c> + 1 when estimated.
/// </summary>
public sealed record UsageRecord(
    string Step,
    string Model,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    decimal CostUsd,
    decimal EstimatedCostUsd,
    bool Estimated)
{
    /// <summary>
    /// The record of <paramref name="call"/>: the gateway's cost when it gave one, else the estimate at
    /// <paramref name="prices"/> (null: a model the mirror does not price, estimated at 0). Token counts below 0 count 0 and
    /// cached input at most the input, so the tables' checks hold.
    /// </summary>
    public static UsageRecord From(GatewayCallUsage call, ModelPrices? prices)
    {
        ArgumentNullException.ThrowIfNull(call);
        var input = Math.Max(call.InputTokens, 0);
        var cached = Math.Clamp(call.CachedInputTokens, 0, input);
        var output = Math.Max(call.OutputTokens, 0);
        return call.CostUsd is { } cost
            ? new UsageRecord(call.Step, call.Model, input, cached, output, cost, 0m, Estimated: false)
            : new UsageRecord(call.Step, call.Model, input, cached, output, 0m, prices?.Estimate(input, cached, output) ?? 0m, Estimated: true);
    }
}

/// <summary>
/// Reads a call's usage from a gateway answer (chat design §8.3; gateway-changes §5.4), once for every caller: the chat
/// path, the reranker, the query embedder and the worker (A-31). The cost comes from <c>x-ume-cost-usd</c> on a complete
/// JSON answer, or from the stream comment <c>: ume-cost-usd=&lt;usd&gt;</c> that <see cref="SseReader"/> takes; neither
/// leaves it null, for <see cref="UsageRecord.From"/> to estimate.
/// </summary>
public static class GatewayUsageReader
{
    /// <summary>The stream comment's prefix, as the gateway writes it (<c>GatewayRequestHandler.WriteCostComment</c>).</summary>
    public const string CostCommentPrefix = ": ume-cost-usd=";

    /// <summary>The call's usage: the tokens of <paramref name="body"/> (<see cref="Tokens"/>, 0 when it reports none) and the cost header.</summary>
    public static GatewayCallUsage Read(HttpResponseMessage response, JsonElement body, string step, string model)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrEmpty(step);
        ArgumentException.ThrowIfNullOrEmpty(model);
        return new GatewayCallUsage(step, model, Tokens(body) ?? default, CostHeader(response));
    }

    /// <summary><c>x-ume-cost-usd</c> as a non-negative decimal (invariant culture); null when it is missing, repeated or not a number.</summary>
    public static decimal? CostHeader(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.Headers.TryGetValues(GatewayHeaders.CostUsd, out var values) && values.ToList() is [var value] ? Usd(value) : null;
    }

    /// <summary>
    /// Whether <paramref name="line"/> is the gateway's stream cost comment <c>: ume-cost-usd=&lt;usd&gt;</c>; its cost in
    /// <paramref name="costUsd"/> (null when the figure is not a non-negative number: the comment is still the gateway's and
    /// is not passed on).
    /// </summary>
    public static bool IsCostComment(string line, out decimal? costUsd)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!line.StartsWith(CostCommentPrefix, StringComparison.Ordinal))
        {
            costUsd = null;
            return false;
        }

        costUsd = Usd(line[CostCommentPrefix.Length..]);
        return true;
    }

    /// <summary>
    /// The tokens of a JSON answer or stream chunk, null when it carries no usage (a stream chunk before the last, whose
    /// <c>usage</c> is null): <c>usage.prompt_tokens</c> (else <c>usage.total_tokens</c>, as a rerank answer that bills
    /// input only may give just the total, else Cohere's <c>meta.billed_units.input_tokens</c>),
    /// <c>usage.prompt_tokens_details.cached_tokens</c> (0 when absent) and <c>usage.completion_tokens</c> (0 when absent).
    /// </summary>
    public static GatewayTokens? Tokens(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (body.TryGetProperty("usage", out var usage) && UsageTokens(usage) is { } tokens)
        {
            return tokens;
        }

        return body.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
               meta.TryGetProperty("billed_units", out var billed) && billed.ValueKind == JsonValueKind.Object && Long(billed, "input_tokens") is { } input
            ? new GatewayTokens(input, 0, 0)
            : null;
    }

    /// <summary>The tokens of a <c>usage</c> object itself, as <see cref="Tokens"/> reads them; null when it is not an object.</summary>
    public static GatewayTokens? UsageTokens(JsonElement usage)
    {
        if (usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var cached = usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object
            ? Long(details, "cached_tokens")
            : null;
        return new GatewayTokens(Long(usage, "prompt_tokens") ?? Long(usage, "total_tokens") ?? 0, cached ?? 0, Long(usage, "completion_tokens") ?? 0);
    }

    private static decimal? Usd(string value) =>
        decimal.TryParse(value.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var usd) ? usd : null;

    private static long? Long(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
}

/// <summary>
/// Reads a gateway stream line by line (chat design §8.3, §9.1): the cost comment <c>: ume-cost-usd=&lt;usd&gt;</c>, which the
/// gateway writes just before <c>data: [DONE]</c> when the request carried <c>x-ume-stream-cost: 1</c>, is taken as the call's
/// <see cref="CostUsd"/> and never returned, so it cannot reach a client of ours. Every other line is returned as read.
/// </summary>
public sealed class SseReader(TextReader reader)
{
    /// <summary>The cost from the stream's comment; null until it has been read, or when the stream has none.</summary>
    public decimal? CostUsd { get; private set; }

    /// <summary>The next line that is not the cost comment, or null at the end of the stream.</summary>
    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return null;
            }

            if (!GatewayUsageReader.IsCostComment(line, out var cost))
            {
                return line;
            }

            CostUsd = cost ?? CostUsd;
        }
    }
}
