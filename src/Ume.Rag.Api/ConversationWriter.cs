using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Npgsql;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Retrieval;
using Ume.Rag.Infrastructure.Gateway;

namespace Ume.Rag.Api;

/// <summary>One source row of a turn (chat design §12.1): a retrieved chunk, its prompt number and whether the answer cited it.</summary>
public sealed record TurnSource(int Rank, Guid DocumentId, Guid ChunkId, Guid CollectionId, short? Number, int? Page, int? PageEnd, bool Used);

/// <summary>
/// A finished turn (chat design §12.1) in history mode <c>none</c> (all of P1, weakness review §4): metadata only, no text.
/// <see cref="MessageId"/> is the answer's <c>chatcmpl-</c> id. The conversation is the issued id (<see cref="IssuedId"/>) or Open WebUI's chat (<see cref="ExternalChatId"/>).
/// <see cref="Usage"/> holds the usage records of the turn's model calls (§8.3); a turn that ended without an answer after a
/// model call is <see cref="UsageOnly"/> (<see cref="UsageOnlyOf"/>): no conversation or message rows, only its usage.
/// </summary>
public sealed record ChatTurn(
    Guid MessageId, Guid? IssuedId, string? ExternalChatId, Guid AssistantId, Guid? AssistantVersionId, Guid DepartmentId, Guid ClientId, string SubjectKind, string SubjectId,
    string Classification, string HistoryMode, int RetentionDays, string Outcome, string? Coverage, string? Degraded, string? Language, bool CondenseUsed,
    string? GatewayModel, string? Residency, int? InputTokens, int? OutputTokens, int? LatencyMs, int? TimeToFirstTokenMs, IReadOnlyList<TurnSource> Sources,
    DateTimeOffset At)
{
    /// <summary>Open WebUI's id of the user message (<c>X-Rag-Owui-User-Message-Id</c>); a regeneration resends it (§11.4).</summary>
    public string? UserExternalId { get; init; }

    /// <summary>Open WebUI's id of the answer (<c>X-Rag-Owui-Message-Id</c>): what its feedback names (§14.2).</summary>
    public string? AnswerExternalId { get; init; }

    /// <summary>The usage records of the turn's model calls, upserted into <c>AssistantDailyUsage</c> per step (§12.2).</summary>
    public IReadOnlyList<UsageRecord> Usage { get; init; } = [];

    /// <summary>Only <see cref="Usage"/> is written: the turn failed or broke off after a model call (§12.1).</summary>
    public bool UsageOnly { get; init; }

    /// <summary>
    /// A usage-only turn that ended <c>unavailable</c> (§3.6: the assistant's key unusable or its budget spent): it adds one to the
    /// day's <c>AssistantDailyTotals.Unavailable</c>, with no conversation or message rows.
    /// </summary>
    public bool Unavailable { get; init; }

    /// <summary><c>Messages.CostUsd</c>: the answer call's cost, the gateway's or the estimate (§12.2); null without an answer call.</summary>
    public decimal? AnswerCostUsd => Usage.FirstOrDefault(r => r.Step == GatewayChatClient.AnswerStep) is { } answer
        ? answer.Estimated ? answer.EstimatedCostUsd : answer.CostUsd
        : null;

    /// <summary>
    /// A usage-only turn (§12.1): the usage of a turn that ended without an answer after a model call (e.g. the answer call
    /// failing after condense and the query embedding were paid), under its assistant, client and level on <paramref name="at"/>'s day.
    /// </summary>
    public static ChatTurn UsageOnlyOf(Guid assistantId, Guid departmentId, Guid clientId, string classification, DateTimeOffset at, IReadOnlyList<UsageRecord> usage) =>
        new(Guid.Empty, null, null, assistantId, null, departmentId, clientId, "", "", classification, "", 0, "", null, null, null, false, null, null, null, null, null,
            null, [], at)
        {
            Usage = usage,
            UsageOnly = true,
        };

    /// <summary>The sort key of a batch (chat design §12.2: sorted keys, so overlapping batches cannot deadlock).</summary>
    public string Key => IssuedId is { } id ? "i:" + id.ToString("N") : $"o:{ClientId:N}:{SubjectId}:{ExternalChatId}:{AssistantId:N}";

    /// <summary>The <c>Messages.Outcome</c> value of a chat outcome.</summary>
    public static string OutcomeOf(ChatOutcome outcome) => ChatOutcomes.Name(outcome);

    /// <summary>The <c>Messages.Coverage</c> value; <c>skipped</c> when no retrieval ran (a conversational message).</summary>
    public static string CoverageOf(RetrievalResult? retrieval) => retrieval?.Coverage.Status switch
    {
        CoverageStatus.Ok => "ok",
        CoverageStatus.Low => "low",
        CoverageStatus.Unknown => "unknown",
        _ => "skipped",
    };

    /// <summary><c>Conversations.SubjectKind</c> of a principal.</summary>
    public static string SubjectKindOf(SubjectKind kind) => kind switch
    {
        Domain.Access.SubjectKind.OwuiUser => "owui-user",
        Domain.Access.SubjectKind.Application => "application",
        Domain.Access.SubjectKind.Anonymous => "anonymous",
        _ => "entra-user",
    };
}

/// <summary>
/// The background writer (chat design §12; adapted from the gateway's <c>UsageWriter</c>): a bounded channel the request
/// path enqueues into after the answer, drained in batches of ≤ 200 turns or 250 ms, one transaction per batch: the
/// conversation upsert, the messages with <c>Seq</c> under a row lock, their sources, then the <c>AssistantDailyTotals</c>
/// deltas and the <c>AssistantDailyUsage</c> deltas per step (usage-only turns included) as the last statements, each in
/// sorted key order. A failed batch is retried after 2 s; after three failures it
/// is split and each turn tried alone three times, then dropped (<c>rag.chat.writer.dropped{reason}</c>). No journal.
/// </summary>
public sealed partial class ConversationWriter : BackgroundService
{
    public const int Capacity = 5000;
    public const int BatchSize = 200;
    public static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan EnqueueTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");

    private readonly Channel<ChatTurn> _channel = Channel.CreateBounded<ChatTurn>(new BoundedChannelOptions(Capacity)
    {
        SingleReader = true, FullMode = BoundedChannelFullMode.Wait,
    });

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<ConversationWriter> _logger;
    private readonly Counter<long> _dropped;
    private readonly Counter<long> _written;
    private int _pending;

    public ConversationWriter(NpgsqlDataSource dataSource, IMeterFactory meters, ILogger<ConversationWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(meters);
        _dataSource = dataSource;
        _logger = logger;
        var meter = meters.Create("Ume.Rag.Api");
        _dropped = meter.CreateCounter<long>("rag.chat.writer.dropped");
        _written = meter.CreateCounter<long>("rag.chat.writer.written");
    }

    /// <summary>Enqueues a turn; when the queue is full, waits up to 2 s, then drops it (the answer is already delivered).</summary>
    public async ValueTask EnqueueAsync(ChatTurn turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        Interlocked.Increment(ref _pending);
        if (_channel.Writer.TryWrite(turn))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(EnqueueTimeout);
        try
        {
            await _channel.Writer.WriteAsync(turn, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Decrement(ref _pending);
            _dropped.Add(1, new KeyValuePair<string, object?>("reason", "full"));
            LogDropped(_logger, turn.IssuedId?.ToString("N") ?? "open-webui chat", "full");
        }
    }

    /// <summary>Waits until every enqueued turn is written or dropped (tests and shutdown).</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref _pending) > 0)
        {
            await Task.Delay(20, cancellationToken);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<ChatTurn>(BatchSize);
        try
        {
            while (await _channel.Reader.WaitToReadAsync(stoppingToken))
            {
                using (var window = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    window.CancelAfter(BatchWindow);
                    try
                    {
                        while (batch.Count < BatchSize)
                        {
                            if (_channel.Reader.TryRead(out var turn))
                            {
                                batch.Add(turn);
                            }
                            else if (!await _channel.Reader.WaitToReadAsync(window.Token))
                            {
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // The window closed: write what we have.
                    }
                }

                await WriteWithRetryAsync(batch, stoppingToken);
                batch.Clear(); // written or dropped and counted down; on shutdown a cancelled write throws before this and the drain retries it
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown: drain what is queued (ServiceDefaults' shutdown window bounds it).
            while (_channel.Reader.TryRead(out var turn))
            {
                batch.Add(turn);
            }

            if (batch.Count > 0)
            {
                await WriteWithRetryAsync(batch, CancellationToken.None);
            }
        }
    }

    private async Task WriteWithRetryAsync(List<ChatTurn> batch, CancellationToken ct)
    {
        var completed = false;
        try
        {
            if (!await TryAsync(batch, ct))
            {
                foreach (var turn in batch)
                {
                    if (!await TryAsync([turn], ct))
                    {
                        _dropped.Add(1, new KeyValuePair<string, object?>("reason", "poison"));
                        LogDropped(_logger, turn.IssuedId?.ToString("N") ?? "open-webui chat", "poison");
                    }
                }
            }

            completed = true;
        }
        finally
        {
            // A batch cancelled by shutdown is written again by the drain, which counts it down then (#263 review).
            if (completed || !ct.IsCancellationRequested)
            {
                Interlocked.Add(ref _pending, -batch.Count);
            }
        }
    }

    private async Task<bool> TryAsync(List<ChatTurn> turns, CancellationToken ct)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await WriteAsync(turns, ct);
                _written.Add(turns.Count);
                return true;
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException && !ct.IsCancellationRequested)
            {
                LogFailed(_logger, turns.Count, attempt, ex.GetType().Name);
                if (attempt < 3)
                {
                    await Task.Delay(RetryInterval, ct);
                }
            }
        }

        return false;
    }

    private sealed record TotalsKey(DateOnly Day, Guid AssistantId, Guid ClientId, string Classification);

    private sealed class Totals
    {
        public Guid DepartmentId { get; set; }
        public int Conversations { get; set; }
        public int Messages { get; set; }
        public int NotFound { get; set; }
        public int GeneralAnswers { get; set; }
        public int Unavailable { get; set; }
        public int Degraded { get; set; }
        public int UncitedAnswers { get; set; }
    }

    private sealed record UsageKey(DateOnly Day, Guid AssistantId, Guid ClientId, string Classification, string Step);

    /// <summary>The sort order of the usage upserts (§12.2): every key part, strings ordinal, so overlapping batches lock in one order.</summary>
    private static readonly Comparer<UsageKey> UsageOrder = Comparer<UsageKey>.Create((a, b) =>
    {
        var order = (a.Day, a.AssistantId, a.ClientId).CompareTo((b.Day, b.AssistantId, b.ClientId));
        if (order == 0)
        {
            order = string.CompareOrdinal(a.Classification, b.Classification);
        }

        return order != 0 ? order : string.CompareOrdinal(a.Step, b.Step);
    });

    private sealed class UsageSums
    {
        public Guid DepartmentId { get; init; }
        public int Calls { get; set; }
        public long InputTokens { get; set; }
        public long CachedInputTokens { get; set; }
        public long OutputTokens { get; set; }
        public decimal CostUsd { get; set; }
        public int EstimatedCalls { get; set; }
        public decimal EstimatedCostUsd { get; set; }
    }

    private const string InsertConversationSql = """
        INSERT INTO chat."Conversations" AS c ("Id", "Classification", "AssistantId", "ClientId", "SubjectKind", "SubjectId", "ExternalChatId",
            "HistoryMode", "RetentionDays", "CreatedAt", "LastMessageAt", "ExpiresAt")
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $10, $11)
        """;

    private const string OwnedUpdateSql = """
        DO UPDATE SET "LastMessageAt" = GREATEST(c."LastMessageAt", excluded."LastMessageAt"),
            "ExpiresAt" = GREATEST(c."ExpiresAt", excluded."ExpiresAt")
            WHERE c."ClientId" = excluded."ClientId" AND c."SubjectId" = excluded."SubjectId" AND c."AssistantId" = excluded."AssistantId"
        RETURNING "Id", (xmax = 0)
        """;

    /// <summary>The conversation of an issued id: the row, or none when the id is another owner's (#266).</summary>
    private const string UpsertIssuedConversationSql = $"""
        {InsertConversationSql}
        ON CONFLICT ("Id") {OwnedUpdateSql}
        """;

    /// <summary>The conversation of an Open WebUI chat, by its external chat id.</summary>
    private const string UpsertOpenWebUiConversationSql = $"""
        {InsertConversationSql}
        ON CONFLICT ("ClientId", "SubjectId", "ExternalChatId", "AssistantId") {OwnedUpdateSql}
        """;

    /// <summary>One batch in one transaction (chat design §12.2).</summary>
    public async Task WriteAsync(IReadOnlyList<ChatTurn> turns, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(turns);
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var totals = new SortedDictionary<TotalsKey, Totals>(Comparer<TotalsKey>.Create((a, b) =>
            (a.Day, a.AssistantId, a.ClientId, a.Classification).CompareTo((b.Day, b.AssistantId, b.ClientId, b.Classification))));
        var usage = new SortedDictionary<UsageKey, UsageSums>(UsageOrder);
        foreach (var turn in turns.OrderBy(t => t.Key, StringComparer.Ordinal).ThenBy(t => t.At))
        {
            // The calls were made and charged, whatever happens to the turn's rows below.
            AddUsage(usage, turn);
            if (turn.UsageOnly)
            {
                if (turn.Unavailable)
                {
                    var refusedKey = new TotalsKey(Day(turn.At), turn.AssistantId, turn.ClientId, turn.Classification);
                    if (!totals.TryGetValue(refusedKey, out var refusedDay))
                    {
                        totals[refusedKey] = refusedDay = new Totals { DepartmentId = turn.DepartmentId };
                    }

                    refusedDay.Unavailable++;
                }

                continue;
            }

            var expires = turn.At.AddDays(turn.RetentionDays);
            var owned = await ScalarPairAsync(connection, transaction, turn.IssuedId is null ? UpsertOpenWebUiConversationSql : UpsertIssuedConversationSql,
                ct, turn.IssuedId ?? Guid.CreateVersion7(), turn.Classification, turn.AssistantId, turn.ClientId, turn.SubjectKind, turn.SubjectId,
                (object?)turn.ExternalChatId ?? DBNull.Value, turn.HistoryMode, turn.RetentionDays, turn.At, expires);
            if (owned is not var (conversationId, inserted))
            {
                // The id belongs to another client, subject or assistant (#266): the SQL refuses it on its own, whatever the MAC said.
                _dropped.Add(1, new KeyValuePair<string, object?>("reason", "owner"));
                LogDropped(_logger, turn.IssuedId?.ToString("N") ?? "open-webui chat", "owner");
                continue;
            }

            // Seq under a row lock on the conversation, held for this transaction only.
            await ExecuteAsync(connection, transaction, """SELECT 1 FROM chat."Conversations" WHERE "Id" = $1 FOR UPDATE""", ct, conversationId);
            var seq = await ScalarAsync<int>(connection, transaction, """SELECT coalesce(max("Seq"), 0) FROM chat."Messages" WHERE "ConversationId" = $1""", ct,
                conversationId);

            const string message = """
                INSERT INTO chat."Messages" ("Id", "ConversationId", "Classification", "Seq", "Role", "ExpiresAt", "AssistantVersionId", "GatewayModel",
                    "ObservedResidency", "InputTokens", "OutputTokens", "LatencyMs", "TimeToFirstTokenMs", "Coverage", "Degraded", "Language", "CondenseUsed",
                    "Outcome", "CreatedAt", "ExternalMessageId", "CostUsd")
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21)
                ON CONFLICT ("ConversationId", "ExternalMessageId") WHERE "ExternalMessageId" IS NOT NULL DO NOTHING
                """;
            await ExecuteAsync(connection, transaction, message, ct, Guid.CreateVersion7(), conversationId, turn.Classification, seq + 1, "user", expires,
                DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, false,
                turn.Outcome, turn.At, Db(turn.UserExternalId), DBNull.Value);
            // The assistant message keeps the turn's id (chatcmpl-<N>), so /v1/feedback finds it (§3.5); its cost is the answer call's.
            var answerId = turn.MessageId;
            await ExecuteAsync(connection, transaction, message, ct, answerId, conversationId, turn.Classification, seq + 2, "assistant", expires,
                Db(turn.AssistantVersionId), Db(turn.GatewayModel), Db(turn.Residency), Db(turn.InputTokens), Db(turn.OutputTokens), Db(turn.LatencyMs),
                Db(turn.TimeToFirstTokenMs), Db(turn.Coverage), Db(turn.Degraded), Db(turn.Language), turn.CondenseUsed, turn.Outcome, turn.At,
                Db(turn.AnswerExternalId), Db(turn.AnswerCostUsd));
            foreach (var source in turn.Sources)
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO chat."MessageSources" ("MessageId", "Rank", "Classification", "DocumentId", "ChunkId", "CollectionId", "Number", "Page", "PageEnd", "Used")
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
                    """, ct, answerId, source.Rank, turn.Classification, source.DocumentId, source.ChunkId, source.CollectionId, Db(source.Number), Db(source.Page),
                    Db(source.PageEnd), source.Used);
            }

            var key = new TotalsKey(Day(turn.At), turn.AssistantId, turn.ClientId, turn.Classification);
            if (!totals.TryGetValue(key, out var day))
            {
                totals[key] = day = new Totals { DepartmentId = turn.DepartmentId };
            }

            day.Conversations += inserted ? 1 : 0;
            day.Messages++;
            day.NotFound += turn.Outcome == "not-found" ? 1 : 0;
            day.GeneralAnswers += turn.Outcome == "answered-general" ? 1 : 0; // §10.4: apart from NotFound, never in UncitedAnswers
            day.Unavailable += turn.Outcome == "unavailable" ? 1 : 0;
            day.Degraded += turn.Degraded is not null ? 1 : 0;
            day.UncitedAnswers += turn.Outcome == "answered" && turn.Sources.Count > 0 && !turn.Sources.Any(s => s.Used) ? 1 : 0;
        }

        // Last, in key order: the hot totals rows are locked for this statement only (performance pass §3.2).
        foreach (var (key, day) in totals)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO chat."AssistantDailyTotals" AS t ("Day", "AssistantId", "ClientId", "Classification", "DepartmentId", "Conversations", "Messages",
                    "NotFound", "GeneralAnswers", "Unavailable", "Degraded", "Errors", "Cancelled", "UncitedAnswers", "ThumbsUp", "ThumbsDown", "ToolCalls", "ToolErrors",
                    "AgentLimitHits")
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $12, $9, $10, 0, 0, $11, 0, 0, 0, 0, 0)
                ON CONFLICT ("Day", "AssistantId", "ClientId", "Classification") DO UPDATE SET
                    "Conversations" = t."Conversations" + excluded."Conversations", "Messages" = t."Messages" + excluded."Messages",
                    "NotFound" = t."NotFound" + excluded."NotFound", "GeneralAnswers" = t."GeneralAnswers" + excluded."GeneralAnswers",
                    "Unavailable" = t."Unavailable" + excluded."Unavailable",
                    "Degraded" = t."Degraded" + excluded."Degraded", "UncitedAnswers" = t."UncitedAnswers" + excluded."UncitedAnswers"
                """, ct, key.Day, key.AssistantId, key.ClientId, key.Classification, day.DepartmentId, day.Conversations, day.Messages, day.NotFound, day.Unavailable,
                day.Degraded, day.UncitedAnswers, day.GeneralAnswers);
        }

        // Then the usage per step (§12.2, data-model §2.3), also in key order; the department is the one of the row's first call.
        foreach (var (key, sums) in usage)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO chat."AssistantDailyUsage" AS u ("Day", "AssistantId", "ClientId", "Classification", "Step", "DepartmentId", "Calls", "InputTokens",
                    "CachedInputTokens", "OutputTokens", "CostUsd", "EstimatedCalls", "EstimatedCostUsd")
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
                ON CONFLICT ("Day", "AssistantId", "ClientId", "Classification", "Step") DO UPDATE SET
                    "Calls" = u."Calls" + excluded."Calls", "InputTokens" = u."InputTokens" + excluded."InputTokens",
                    "CachedInputTokens" = u."CachedInputTokens" + excluded."CachedInputTokens", "OutputTokens" = u."OutputTokens" + excluded."OutputTokens",
                    "CostUsd" = u."CostUsd" + excluded."CostUsd", "EstimatedCalls" = u."EstimatedCalls" + excluded."EstimatedCalls",
                    "EstimatedCostUsd" = u."EstimatedCostUsd" + excluded."EstimatedCostUsd"
                """, ct, key.Day, key.AssistantId, key.ClientId, key.Classification, key.Step, sums.DepartmentId, sums.Calls, sums.InputTokens,
                sums.CachedInputTokens, sums.OutputTokens, sums.CostUsd, sums.EstimatedCalls, sums.EstimatedCostUsd);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>Adds the usage records of <paramref name="turn"/> to the batch's sums per (day, assistant, client, level, step).</summary>
    private static void AddUsage(SortedDictionary<UsageKey, UsageSums> usage, ChatTurn turn)
    {
        var day = Day(turn.At);
        foreach (var record in turn.Usage)
        {
            var key = new UsageKey(day, turn.AssistantId, turn.ClientId, turn.Classification, record.Step);
            if (!usage.TryGetValue(key, out var sums))
            {
                usage[key] = sums = new UsageSums { DepartmentId = turn.DepartmentId };
            }

            sums.Calls++;
            sums.InputTokens += record.InputTokens;
            sums.CachedInputTokens += record.CachedInputTokens;
            sums.OutputTokens += record.OutputTokens;
            sums.CostUsd += record.CostUsd;
            sums.EstimatedCalls += record.Estimated ? 1 : 0;
            sums.EstimatedCostUsd += record.EstimatedCostUsd;
        }
    }

    /// <summary>The Europe/Stockholm day of <paramref name="at"/> (data-model §2.3).</summary>
    private static DateOnly Day(DateTimeOffset at) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Stockholm).DateTime);

    private static object Db(object? value) => value ?? DBNull.Value;

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct, params object[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct, params object[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        return (T)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>The upsert's row, or null when its <c>WHERE</c> refused the update (another owner's conversation).</summary>
    private static async Task<(Guid, bool)?> ScalarPairAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct,
        params object[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetGuid(0), reader.GetBoolean(1)) : null;
    }

    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, object[] values)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return command;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conversation writer dropped a turn of {Conversation} ({Reason})")]
    private static partial void LogDropped(ILogger logger, string conversation, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Conversation writer batch of {Count} failed (attempt {Attempt}, {Error})")]
    private static partial void LogFailed(ILogger logger, int count, int attempt, string error);
}
