using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Npgsql;
using Ume.Rag.Answering.Chat;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Retrieval;

namespace Ume.Rag.Api;

/// <summary>One source row of a turn (chat design §12.1): a retrieved chunk, its prompt number and whether the answer cited it.</summary>
public sealed record TurnSource(int Rank, Guid DocumentId, Guid ChunkId, Guid CollectionId, short? Number, int? Page, int? PageEnd, bool Used);

/// <summary>
/// A finished turn (chat design §12.1) in history mode <c>none</c> (all of P1, weakness review §4): metadata only, no text.
/// The conversation is the issued id (<see cref="IssuedId"/>) or Open WebUI's chat (<see cref="ExternalChatId"/>).
/// </summary>
public sealed record ChatTurn(
    Guid? IssuedId, string? ExternalChatId, Guid AssistantId, Guid? AssistantVersionId, Guid DepartmentId, Guid ClientId, string SubjectKind, string SubjectId,
    string Classification, string HistoryMode, int RetentionDays, string Outcome, string? Coverage, string? Degraded, string? Language, bool CondenseUsed,
    string? GatewayModel, string? Residency, int? InputTokens, int? OutputTokens, int? LatencyMs, int? TimeToFirstTokenMs, IReadOnlyList<TurnSource> Sources,
    DateTimeOffset At)
{
    /// <summary>The sort key of a batch (chat design §12.2: sorted keys, so overlapping batches cannot deadlock).</summary>
    public string Key => IssuedId is { } id ? "i:" + id.ToString("N") : $"o:{ClientId:N}:{SubjectId}:{ExternalChatId}:{AssistantId:N}";

    /// <summary>The <c>Messages.Outcome</c> value of a chat outcome.</summary>
    public static string OutcomeOf(ChatOutcome outcome) => outcome switch
    {
        ChatOutcome.NotFound => "not-found",
        ChatOutcome.Unavailable => "unavailable",
        _ => "answered",
    };

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
/// conversation upsert, the messages with <c>Seq</c> under a row lock, their sources, and the
/// <c>AssistantDailyTotals</c> deltas as the last statement. A failed batch is retried after 2 s; after three failures it
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
                batch.Clear();
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
        }
        finally
        {
            Interlocked.Add(ref _pending, -batch.Count);
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
        public int Unavailable { get; set; }
        public int Degraded { get; set; }
        public int UncitedAnswers { get; set; }
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
    }

    /// <summary>One batch in one transaction (chat design §12.2).</summary>
    public async Task WriteAsync(IReadOnlyList<ChatTurn> turns, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(turns);
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var totals = new SortedDictionary<TotalsKey, Totals>(Comparer<TotalsKey>.Create((a, b) =>
            (a.Day, a.AssistantId, a.ClientId, a.Classification).CompareTo((b.Day, b.AssistantId, b.ClientId, b.Classification))));
        foreach (var turn in turns.OrderBy(t => t.Key, StringComparer.Ordinal).ThenBy(t => t.At))
        {
            var expires = turn.At.AddDays(turn.RetentionDays);
            var conflict = turn.IssuedId is null ? """("ClientId", "SubjectId", "ExternalChatId", "AssistantId")""" : """("Id")""";
            var (conversationId, inserted) = await ScalarPairAsync(connection, transaction, $"""
                INSERT INTO chat."Conversations" AS c ("Id", "Classification", "AssistantId", "ClientId", "SubjectKind", "SubjectId", "ExternalChatId",
                    "HistoryMode", "RetentionDays", "CreatedAt", "LastMessageAt", "ExpiresAt")
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $10, $11)
                ON CONFLICT {conflict} DO UPDATE SET "LastMessageAt" = GREATEST(c."LastMessageAt", excluded."LastMessageAt"),
                    "ExpiresAt" = GREATEST(c."ExpiresAt", excluded."ExpiresAt")
                RETURNING "Id", (xmax = 0)
                """, ct, turn.IssuedId ?? Guid.CreateVersion7(), turn.Classification, turn.AssistantId, turn.ClientId, turn.SubjectKind, turn.SubjectId,
                (object?)turn.ExternalChatId ?? DBNull.Value, turn.HistoryMode, turn.RetentionDays, turn.At, expires);

            // Seq under a row lock on the conversation, held for this transaction only.
            await ExecuteAsync(connection, transaction, """SELECT 1 FROM chat."Conversations" WHERE "Id" = $1 FOR UPDATE""", ct, conversationId);
            var seq = await ScalarAsync<int>(connection, transaction, """SELECT coalesce(max("Seq"), 0) FROM chat."Messages" WHERE "ConversationId" = $1""", ct,
                conversationId);

            const string message = """
                INSERT INTO chat."Messages" ("Id", "ConversationId", "Classification", "Seq", "Role", "ExpiresAt", "AssistantVersionId", "GatewayModel",
                    "ObservedResidency", "InputTokens", "OutputTokens", "LatencyMs", "TimeToFirstTokenMs", "Coverage", "Degraded", "Language", "CondenseUsed",
                    "Outcome", "CreatedAt")
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19)
                """;
            await ExecuteAsync(connection, transaction, message, ct, Guid.CreateVersion7(), conversationId, turn.Classification, seq + 1, "user", expires,
                DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, false,
                turn.Outcome, turn.At);
            var answerId = Guid.CreateVersion7();
            await ExecuteAsync(connection, transaction, message, ct, answerId, conversationId, turn.Classification, seq + 2, "assistant", expires,
                Db(turn.AssistantVersionId), Db(turn.GatewayModel), Db(turn.Residency), Db(turn.InputTokens), Db(turn.OutputTokens), Db(turn.LatencyMs),
                Db(turn.TimeToFirstTokenMs), Db(turn.Coverage), Db(turn.Degraded), Db(turn.Language), turn.CondenseUsed, turn.Outcome, turn.At);
            foreach (var source in turn.Sources)
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO chat."MessageSources" ("MessageId", "Rank", "Classification", "DocumentId", "ChunkId", "CollectionId", "Number", "Page", "PageEnd", "Used")
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
                    """, ct, answerId, source.Rank, turn.Classification, source.DocumentId, source.ChunkId, source.CollectionId, Db(source.Number), Db(source.Page),
                    Db(source.PageEnd), source.Used);
            }

            var key = new TotalsKey(DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(turn.At, Stockholm).DateTime), turn.AssistantId, turn.ClientId, turn.Classification);
            if (!totals.TryGetValue(key, out var day))
            {
                totals[key] = day = new Totals { DepartmentId = turn.DepartmentId };
            }

            day.Conversations += inserted ? 1 : 0;
            day.Messages++;
            day.NotFound += turn.Outcome == "not-found" ? 1 : 0;
            day.Unavailable += turn.Outcome == "unavailable" ? 1 : 0;
            day.Degraded += turn.Degraded is not null ? 1 : 0;
            day.UncitedAnswers += turn.Outcome == "answered" && turn.Sources.Count > 0 && !turn.Sources.Any(s => s.Used) ? 1 : 0;
            day.InputTokens += turn.InputTokens ?? 0;
            day.OutputTokens += turn.OutputTokens ?? 0;
        }

        // Last, in key order: the hot totals rows are locked for this statement only (performance pass §3.2).
        foreach (var (key, day) in totals)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO chat."AssistantDailyTotals" AS t ("Day", "AssistantId", "ClientId", "Classification", "DepartmentId", "Conversations", "Messages",
                    "NotFound", "Unavailable", "Degraded", "Errors", "Cancelled", "UncitedAnswers", "ThumbsUp", "ThumbsDown", "ToolCalls", "ToolErrors",
                    "AgentLimitHits", "InputTokens", "OutputTokens", "CostUsd")
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, 0, 0, $11, 0, 0, 0, 0, 0, $12, $13, 0)
                ON CONFLICT ("Day", "AssistantId", "ClientId", "Classification") DO UPDATE SET
                    "Conversations" = t."Conversations" + excluded."Conversations", "Messages" = t."Messages" + excluded."Messages",
                    "NotFound" = t."NotFound" + excluded."NotFound", "Unavailable" = t."Unavailable" + excluded."Unavailable",
                    "Degraded" = t."Degraded" + excluded."Degraded", "UncitedAnswers" = t."UncitedAnswers" + excluded."UncitedAnswers",
                    "InputTokens" = t."InputTokens" + excluded."InputTokens", "OutputTokens" = t."OutputTokens" + excluded."OutputTokens"
                """, ct, key.Day, key.AssistantId, key.ClientId, key.Classification, day.DepartmentId, day.Conversations, day.Messages, day.NotFound, day.Unavailable,
                day.Degraded, day.UncitedAnswers, day.InputTokens, day.OutputTokens);
        }

        await transaction.CommitAsync(ct);
    }

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

    private static async Task<(Guid, bool)> ScalarPairAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct,
        params object[] values)
    {
        await using var command = Command(connection, transaction, sql, values);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetGuid(0), reader.GetBoolean(1));
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
