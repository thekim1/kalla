using Ume.Rag.Domain.Access;
using Ume.Rag.Infrastructure.Gateway;

namespace Ume.Rag.Answering.Chat;

/// <summary>
/// The usage records of one chat turn's model calls (chat design §8.3, §12.1): condense, long-question condense, the query
/// embedding (a cache miss only), rerank, the answer and the follow-ups each add theirs as the call answers, so a turn that
/// fails later still has what it paid for. Each call becomes a <see cref="UsageRecord"/> at once, priced from the config
/// snapshot's mirror (<see cref="PolicySnapshot.ModelPrices"/>) when the gateway gave no cost, and is counted
/// (<c>rag.chat.tokens</c>, <c>rag.chat.cost</c>) by the owner's callback. Calls may add from several tasks.
/// </summary>
public sealed class TurnUsage(IReadOnlyDictionary<string, ModelPricesEntry> prices, DateTimeOffset started, Action<UsageRecord>? counted = null)
{
    private readonly Lock _lock = new();
    private readonly List<UsageRecord> _records = [];

    /// <summary>When the turn started: the day of a usage-only turn (§12.1).</summary>
    public DateTimeOffset Started { get; } = started;

    /// <summary>Whether a call has added a record that <see cref="Take"/> has not taken yet.</summary>
    public bool HasRecords
    {
        get
        {
            lock (_lock)
            {
                return _records.Count > 0;
            }
        }
    }

    /// <summary>Adds the record of <paramref name="call"/>: the gateway's cost, else the estimate at the model's mirrored prices.</summary>
    public void Add(GatewayCallUsage call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var record = UsageRecord.From(call, prices.TryGetValue(call.Model, out var price) ? ModelPrices.Of(price) : null);
        lock (_lock)
        {
            _records.Add(record);
        }

        counted?.Invoke(record);
    }

    /// <summary>The records added since the last take, in call order, and forgets them, so each is written once.</summary>
    public IReadOnlyList<UsageRecord> Take()
    {
        lock (_lock)
        {
            UsageRecord[] taken = [.. _records];
            _records.Clear();
            return taken;
        }
    }
}
