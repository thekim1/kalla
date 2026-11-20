using System.Collections.Concurrent;

namespace Kalla.FakeGateway;

/// <summary>
/// A request the fake served, as the gateway's usage record keeps it (gateway-changes §5.3, §7.1): the key it ran as (the
/// assigned key when acting, else the presented credential key; null for a per-level dev key), the credential key it came
/// through, the run metadata, the model, tokens and cost.
/// </summary>
public sealed record FakeUsageRecord(
    DateTimeOffset At, Guid? KeyId, Guid? ViaKeyId, string? RunId, string? Step, string Endpoint, string Model, long InputTokens, long OutputTokens, decimal CostUsd);

/// <summary>
/// The served requests in memory (repo-and-deployment §4.3, B-42; the Integration API's <c>usage</c> reads them in B-45).
/// <c>x-fors-run-id</c> and <c>x-fors-step</c> are trimmed and cut at 100 characters, as the gateway stores them.
/// </summary>
public sealed class FakeUsage
{
    public const string RunIdHeader = "x-fors-run-id";
    public const string StepHeader = "x-fors-step";
    private const int MaxMetadata = 100;

    private readonly ConcurrentQueue<FakeUsageRecord> _records = new();

    public IReadOnlyList<FakeUsageRecord> Records => [.. _records];

    public void Record(HttpContext context, FakeCall call, FakeModel model, long inputTokens, long outputTokens, decimal cost)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(model);
        _records.Enqueue(new FakeUsageRecord(DateTimeOffset.UtcNow, call.Acting?.Id ?? call.CredentialId, call.Acting is null ? null : call.CredentialId,
            Metadata(context, RunIdHeader), Metadata(context, StepHeader), context.Request.Path, model.Name, inputTokens, outputTokens, cost));
    }

    private static string? Metadata(HttpContext context, string header)
    {
        var value = context.Request.Headers[header].ToString().Trim();
        return value.Length == 0 ? null : value.Length > MaxMetadata ? value[..MaxMetadata] : value;
    }
}
