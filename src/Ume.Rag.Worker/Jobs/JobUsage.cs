using Npgsql;
using Ume.Rag.Domain;
using Ume.Rag.Infrastructure.Gateway;
using Ume.Rag.Infrastructure.Jobs;
using Ume.Rag.Worker.Connectors.Persistence;

namespace Ume.Rag.Worker.Jobs;

/// <summary>
/// The knowledge base a worker call is counted under and runs as (engine design §7.7, §10.6; data-model §2.2
/// <c>CollectionDailyUsage</c>; cost-attribution §4.1): the collection, its level, its department and its assigned gateway key
/// (<c>Collections.GatewayKeyId</c>; null: the call runs as the worker's credential key) at the time of the call.
/// </summary>
public sealed record UsageOwner(Guid CollectionId, string Classification, Guid DepartmentId, Guid? GatewayKeyId)
{
    /// <summary>
    /// What a call of <paramref name="step"/> for this knowledge base is made for: <see cref="ResidencyCeilingHandler"/> sends
    /// <c>x-ume-on-behalf-of</c> with <see cref="GatewayKeyId"/> once the gateway acts as keys, and checks the echo (identity §4.7).
    /// </summary>
    public GatewayCall Acting(string? step) => new(GatewayKeyId, step, CollectionId: CollectionId);
}

/// <summary>Where a call's usage goes: the job attempt's <see cref="JobUsage"/> under the knowledge base <see cref="Owner"/>.</summary>
public sealed record UsageSink(JobUsage Usage, UsageOwner Owner)
{
    /// <summary>Adds <paramref name="call"/>, answered by the gateway, to the attempt's usage (<see cref="JobUsage.RecordAsync"/>).</summary>
    public Task RecordAsync(GatewayCallUsage call) => Usage.RecordAsync(Owner, call, embedding: false);

    /// <summary>
    /// Adds the embedding call <paramref name="call"/>, answered by the gateway, to the attempt's usage; its input tokens also count
    /// in <c>rag.embed.tokens</c> (<see cref="JobUsage.RecordAsync"/>).
    /// </summary>
    public Task RecordEmbeddingAsync(GatewayCallUsage call) => Usage.RecordAsync(Owner, call, embedding: true);
}

/// <summary>A model's prices as the mirror holds them (<c>config."GatewayModels"</c>, data-model §2.1), for the estimate of a call without a gateway cost.</summary>
public interface IGatewayModelPrices
{
    /// <summary>The prices of <paramref name="model"/>; null when the mirror does not list it (its calls are estimated at 0).</summary>
    Task<ModelPrices?> GetAsync(string model, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IGatewayModelPrices"/> over <c>config."GatewayModels"</c> (read grant, data-model §1.2): one read of every
/// model's prices serves the estimates for <see cref="CacheFor"/>, so an estimated call takes no pool connection of its own,
/// as rag-api prices from its config snapshot (<see cref="Domain.Access.PolicySnapshot.ModelPrices"/>). A mirror sync
/// shows in the estimates within that time.
/// </summary>
public sealed class PostgresGatewayModelPrices(JobDatabase database, TimeProvider time) : IGatewayModelPrices
{
    /// <summary>How long one read of the prices is used.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    private const string PricesSql = """
        SELECT "Name", "InputPerMillionUsd", "CachedInputPerMillionUsd", "OutputPerMillionUsd" FROM config."GatewayModels"
        """;

    private Snapshot? _snapshot;

    public async Task<ModelPrices?> GetAsync(string model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);
        var snapshot = Volatile.Read(ref _snapshot);
        if (snapshot is null || time.GetUtcNow() >= snapshot.Until)
        {
            // Concurrent misses may each read; the last one read is kept.
            snapshot = new Snapshot(await ReadAsync(cancellationToken), time.GetUtcNow() + CacheFor);
            Volatile.Write(ref _snapshot, snapshot);
        }

        return snapshot.Prices.GetValueOrDefault(model);
    }

    private async Task<Dictionary<string, ModelPrices>> ReadAsync(CancellationToken cancellationToken)
    {
        var prices = new Dictionary<string, ModelPrices>(StringComparer.Ordinal);
        await using var connection = await database.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(PricesSql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            prices[reader.GetString(0)] = new ModelPrices(Db.Nullable<decimal>(reader, 1), Db.Nullable<decimal>(reader, 2), Db.Nullable<decimal>(reader, 3));
        }

        return prices;
    }

    private sealed record Snapshot(Dictionary<string, ModelPrices> Prices, DateTimeOffset Until);
}

/// <summary>
/// The model calls of one job attempt that are not committed yet (engine design §10.6; cost-attribution §5 C3). Every gateway
/// call of the worker that answered adds its <see cref="UsageRecord"/> (<see cref="GatewayUsageReader"/>: the tokens and
/// <c>x-ume-cost-usd</c>, else the estimate from the mirror's prices, kept apart) under its knowledge base and the
/// Europe/Stockholm day of the call. The transaction that records the attempt's outcome adds them to
/// <c>content."CollectionDailyUsage"</c> as its last statement (<see cref="WriteAsync"/>, keys sorted) and drops them once it
/// has committed (<see cref="Committed"/>), so a rolled-back outcome leaves them for the next one and a retried attempt
/// counts only its own calls. Created by the dispatcher for each attempt (<see cref="JobContext.Usage"/>), with
/// <paramref name="counted"/> counting each call in the metrics as it is added (<see cref="JobMetrics.ModelCall"/>: the call's
/// record and whether it was an embedding call); without it (tests) the calls are not counted.
/// </summary>
public sealed class JobUsage(IGatewayModelPrices prices, TimeProvider time, Action<UsageRecord, bool>? counted = null)
{
    // One statement for every row of the attempt, in the order of the sorted arrays, so concurrent attempts lock the hot rows
    // in the same order and cannot deadlock (performance pass §3.2).
    private const string UpsertSql = """
        INSERT INTO content."CollectionDailyUsage" AS u ("Day", "CollectionId", "Classification", "Step", "DepartmentId", "Calls", "InputTokens",
                                                       "CachedInputTokens", "OutputTokens", "CostUsd", "EstimatedCalls", "EstimatedCostUsd")
        SELECT r.day, r.collection, r.level, r.step, r.department, r.calls, r.input, r.cached, r.output, r.cost, r.estimated_calls, r.estimated_cost
        FROM unnest($1::date[], $2::uuid[], $3::text[], $4::text[], $5::uuid[], $6::int[], $7::bigint[], $8::bigint[], $9::bigint[], $10::numeric[],
                    $11::int[], $12::numeric[]) WITH ORDINALITY
             AS r(day, collection, level, step, department, calls, input, cached, output, cost, estimated_calls, estimated_cost, n)
        ORDER BY r.n
        ON CONFLICT ("Day", "CollectionId", "Classification", "Step") DO UPDATE
           SET "DepartmentId" = EXCLUDED."DepartmentId",
               "Calls" = u."Calls" + EXCLUDED."Calls",
               "InputTokens" = u."InputTokens" + EXCLUDED."InputTokens",
               "CachedInputTokens" = u."CachedInputTokens" + EXCLUDED."CachedInputTokens",
               "OutputTokens" = u."OutputTokens" + EXCLUDED."OutputTokens",
               "CostUsd" = u."CostUsd" + EXCLUDED."CostUsd",
               "EstimatedCalls" = u."EstimatedCalls" + EXCLUDED."EstimatedCalls",
               "EstimatedCostUsd" = u."EstimatedCostUsd" + EXCLUDED."EstimatedCostUsd"
        """;

    private readonly Lock _lock = new();
    private readonly List<Entry> _pending = [];

    /// <summary>Whether calls are waiting for the outcome's transaction.</summary>
    public bool HasPending
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count > 0;
            }
        }
    }

    /// <summary>
    /// What the pending calls cost, the gateway's cost plus the estimates (<c>RunItems.CostUsd</c>, engine design §10.2); null
    /// when the attempt made no call.
    /// </summary>
    public decimal? PendingCostUsd
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count == 0 ? null : _pending.Sum(e => e.Record.CostUsd + e.Record.EstimatedCostUsd);
            }
        }
    }

    /// <summary>
    /// Adds a call the gateway answered: <see cref="UsageRecord.From"/> with the mirror's prices of its model when it carries
    /// no cost, on today's Europe/Stockholm date. The metrics count it here, once, whatever becomes of the attempt
    /// (<paramref name="embedding"/>: an embedding call, whose input tokens count in <c>rag.embed.tokens</c>). It takes no
    /// cancellation: the gateway has charged the call, so a cancelled job still records it (the price read is bounded by the
    /// pool's and the command's timeouts).
    /// </summary>
    public async Task RecordAsync(UsageOwner owner, GatewayCallUsage call, bool embedding)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(call);
        var record = UsageRecord.From(call, call.CostUsd is null ? await prices.GetAsync(call.Model, CancellationToken.None) : null);
        var day = StockholmTime.Today(time);
        lock (_lock)
        {
            _pending.Add(new Entry(day, owner, record));
        }

        counted?.Invoke(record, embedding);
    }

    /// <summary>
    /// Adds the pending calls to <c>content."CollectionDailyUsage"</c> in <paramref name="transaction"/>: one row per (day,
    /// collection, level, step), sorted by that key, as the transaction's last statement. Returns how many calls it wrote;
    /// pass that to <see cref="Committed"/> after the commit. Nothing is written without pending calls.
    /// </summary>
    public async Task<int> WriteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Entry[] taken;
        lock (_lock)
        {
            taken = [.. _pending];
        }

        if (taken.Length == 0)
        {
            return 0;
        }

        var rows = taken
            .GroupBy(e => (e.Day, e.Owner.CollectionId, e.Owner.Classification, e.Record.Step))
            .Select(g => new Row(g.Key.Day, g.Key.CollectionId, g.Key.Classification, g.Key.Step, g.Last().Owner.DepartmentId, g.Count(),
                g.Sum(e => e.Record.InputTokens), g.Sum(e => e.Record.CachedInputTokens), g.Sum(e => e.Record.OutputTokens), g.Sum(e => e.Record.CostUsd),
                g.Count(e => e.Record.Estimated), g.Sum(e => e.Record.EstimatedCostUsd)))
            .OrderBy(r => r.Day).ThenBy(r => r.CollectionId).ThenBy(r => r.Classification, StringComparer.Ordinal).ThenBy(r => r.Step, StringComparer.Ordinal)
            .ToList();
        await using var command = new NpgsqlCommand(UpsertSql, connection, transaction)
        {
            Parameters =
            {
                Db.P(rows.Select(r => r.Day).ToArray()), Db.P(rows.Select(r => r.CollectionId).ToArray()), Db.P(rows.Select(r => r.Classification).ToArray()),
                Db.P(rows.Select(r => r.Step).ToArray()), Db.P(rows.Select(r => r.DepartmentId).ToArray()), Db.P(rows.Select(r => r.Calls).ToArray()),
                Db.P(rows.Select(r => r.InputTokens).ToArray()), Db.P(rows.Select(r => r.CachedInputTokens).ToArray()),
                Db.P(rows.Select(r => r.OutputTokens).ToArray()), Db.P(rows.Select(r => r.CostUsd).ToArray()), Db.P(rows.Select(r => r.EstimatedCalls).ToArray()),
                Db.P(rows.Select(r => r.EstimatedCostUsd).ToArray()),
            },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
        return taken.Length;
    }

    /// <summary>The first <paramref name="written"/> pending calls are committed (<see cref="WriteAsync"/>'s count); calls added since stay pending.</summary>
    public void Committed(int written)
    {
        lock (_lock)
        {
            _pending.RemoveRange(0, written);
        }
    }

    /// <summary>
    /// The pending calls in their own transaction, for an outcome the queue writes without one (a retry, a release, a failure:
    /// <see cref="Domain.Ports.IJobQueue.FailAsync"/> and <see cref="Domain.Ports.IJobQueue.ReleaseAsync"/> take no transaction).
    /// </summary>
    public async Task WriteOwnAsync(JobDatabase database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (!HasPending)
        {
            return;
        }

        await using var connection = await database.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var written = await WriteAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        Committed(written);
    }

    private sealed record Entry(DateOnly Day, UsageOwner Owner, UsageRecord Record);

    private sealed record Row(DateOnly Day, Guid CollectionId, string Classification, string Step, Guid DepartmentId, int Calls, long InputTokens,
        long CachedInputTokens, long OutputTokens, decimal CostUsd, int EstimatedCalls, decimal EstimatedCostUsd);
}
