using System.Diagnostics.Metrics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Ume.Rag.Infrastructure.Access;

namespace Ume.Rag.Api;

/// <summary>One Open WebUI feedback item as the import reads it (chat design §14.2: only these fields; user details discarded).</summary>
public sealed record OwuiFeedback(string Id, string UserId, long UpdatedAt, short? Rating, short? Score, string? Reason, string? ModelId, string? ChatId, string? MessageId);

/// <summary>
/// The Open WebUI feedback import (chat design §14.2–§14.3, §13.3): every 15 minutes in the chat role, one runner across
/// instances (<c>pg_try_advisory_xact_lock</c> held for the run). It pages <c>/api/v1/evaluations/feedbacks/list</c> newest first until an item is
/// older than the watermark − 1 h (≤ 50 pages), maps each item by (client, <c>owui:</c> user, chat id, assistant of
/// <c>model_id</c>) and <c>ExternalMessageId</c>, and upserts <c>chat."Feedback"</c> (<c>owui-import</c>, by its id) with
/// the rating, score and reason category; comments and reasons' text are dropped (history mode <c>none</c>). Unmapped items
/// are retried for 24 h (the turn may still be queued), then counted. The same run deletes Open WebUI feedback older than
/// the client's <c>ClientRetentionDays</c> through its API.
/// </summary>
public sealed partial class FeedbackImporter(
    IHttpClientFactory clients, NpgsqlDataSource data, IPolicySnapshotSource snapshots, IOptions<OpenWebUiOptions> options, TimeProvider time,
    IMeterFactory meters, ILogger<FeedbackImporter> logger) : BackgroundService
{
    public const string HttpClientName = "openwebui-admin";
    public const long LockKey = 0x5241_4730_0021;
    public const int MaxPages = 50;
    public static readonly TimeSpan Overlap = TimeSpan.FromHours(1);
    public static readonly TimeSpan RetryWindow = TimeSpan.FromHours(24);

    private static readonly TimeZoneInfo Stockholm = TimeZoneInfo.FindSystemTimeZoneById("Europe/Stockholm");
    private readonly Counter<long> _imported = meters.Create("Ume.Rag.Api").CreateCounter<long>("rag.chat.feedback.import");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.FeedbackImportInterval, time);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One run; false when it did not run (not configured, or another instance holds the lock) or failed.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var o = options.Value;
        if (o.BaseUrl is null || string.IsNullOrEmpty(o.AdminApiKey) || o.Client is not { } clientId)
        {
            return false;
        }

        // One runner across instances: a transaction-scoped lock on its own connection, held for the run (pooler-safe, no
        // session state); the work runs on a second connection.
        await using var lockConnection = await data.OpenConnectionAsync(ct);
        await using var lockTransaction = await lockConnection.BeginTransactionAsync(ct);
        await using (var take = new NpgsqlCommand($"SELECT pg_try_advisory_xact_lock({LockKey})", lockConnection, lockTransaction))
        {
            if (await take.ExecuteScalarAsync(ct) is not true)
            {
                return false;
            }
        }

        await using var connection = await data.OpenConnectionAsync(ct);
        var ran = await ImportAsync(connection, clientId, o, ct);
        await lockTransaction.CommitAsync(CancellationToken.None);
        return ran;
    }

    private async Task<bool> ImportAsync(NpgsqlConnection connection, Guid clientId, OpenWebUiOptions o, CancellationToken ct)
    {
        var watermark = await ScalarAsync<long?>(connection, """SELECT "WatermarkUpdatedAt" FROM chat."FeedbackImportState" WHERE "ClientId" = $1""", ct, clientId) ?? 0;
        var now = time.GetUtcNow();
        var slugs = snapshots.Current.Assistants.Values.ToDictionary(a => a.Slug, a => a, StringComparer.Ordinal);
        long newest = watermark, holdBack = long.MaxValue;
        int unmapped = 0;
        string outcome = "ok";
        try
        {
            for (var page = 1; page <= MaxPages; page++)
            {
                var items = await ListAsync(o, "desc", page, ct);
                foreach (var item in items.Where(i => i.UpdatedAt >= watermark - (long)Overlap.TotalSeconds))
                {
                    newest = Math.Max(newest, item.UpdatedAt);
                    switch (await ImportOneAsync(connection, clientId, item, slugs, ct))
                    {
                        case "unmapped" when now - DateTimeOffset.FromUnixTimeSeconds(item.UpdatedAt) < RetryWindow:
                            holdBack = Math.Min(holdBack, item.UpdatedAt); // retried next run: the watermark stays before it
                            break;
                        case "unmapped":
                            unmapped++;
                            _imported.Add(1, new KeyValuePair<string, object?>("outcome", "unmapped"));
                            LogUnmapped(logger, item.Id);
                            break;
                        case var result:
                            _imported.Add(1, new KeyValuePair<string, object?>("outcome", result));
                            break;
                    }
                }

                if (items.Count < 30 || items[^1].UpdatedAt < watermark - (long)Overlap.TotalSeconds)
                {
                    break;
                }
            }

        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            outcome = ex is HttpRequestException { StatusCode: { } status } ? "failed:" + (int)status : "failed";
            LogFailed(logger, outcome);
        }

        // §13.3: pruning failures do not hold the import back; they are their own outcome.
        if (outcome == "ok")
        {
            try
            {
                await PruneAsync(connection, clientId, o, now, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                outcome = ex is HttpRequestException { StatusCode: { } status } ? "prune-failed:" + (int)status : "prune-failed";
                LogFailed(logger, outcome);
            }
        }

        // §14.3: a failed import does not move the watermark, counts as a failed run, and alerts from the 4th in a row.
        var failed = outcome.StartsWith("failed", StringComparison.Ordinal);
        var next = !failed ? Math.Min(newest, holdBack == long.MaxValue ? newest : holdBack) : watermark;
        var failedRuns = await ScalarAsync<int>(connection, """
            INSERT INTO chat."FeedbackImportState" ("ClientId", "WatermarkUpdatedAt", "LastRunAt", "LastOutcome", "Unmapped", "FailedRuns")
            VALUES ($1, $2, $3, $4, $5, CASE WHEN $6 THEN 1 ELSE 0 END)
            ON CONFLICT ("ClientId") DO UPDATE SET "WatermarkUpdatedAt" = excluded."WatermarkUpdatedAt", "LastRunAt" = excluded."LastRunAt",
                "LastOutcome" = excluded."LastOutcome", "Unmapped" = chat."FeedbackImportState"."Unmapped" + excluded."Unmapped",
                "FailedRuns" = CASE WHEN $6 THEN chat."FeedbackImportState"."FailedRuns" + 1 ELSE 0 END
            RETURNING "FailedRuns"
            """, ct, clientId, next, now, outcome, unmapped, failed);
        if (failedRuns >= Domain.Entities.FeedbackImportState.AlertAfterFailedRuns)
        {
            LogAlert(logger, failedRuns, outcome);
        }

        return !failed;
    }

    /// <summary>Maps and upserts one item: <c>imported</c>, <c>foreign</c> (not our model), <c>unrated</c> or <c>unmapped</c>.</summary>
    private static async Task<string> ImportOneAsync(NpgsqlConnection connection, Guid clientId, OwuiFeedback item,
        Dictionary<string, Domain.Access.AssistantEntry> slugs, CancellationToken ct)
    {
        if (item.ModelId is null || !slugs.TryGetValue(item.ModelId, out var assistant))
        {
            return "foreign";
        }

        if (item.Rating is not (1 or -1))
        {
            return "unrated";
        }

        if (item.ChatId is null || item.MessageId is null)
        {
            return "unmapped";
        }

        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var find = new NpgsqlCommand("""
            SELECT m."Id", m."ConversationId", m."Classification", m."CreatedAt"
            FROM chat."Conversations" c JOIN chat."Messages" m ON m."ConversationId" = c."Id"
            WHERE c."ClientId" = $1 AND c."SubjectId" = $2 AND c."ExternalChatId" = $3 AND c."AssistantId" = $4 AND m."ExternalMessageId" = $5 AND m."Role" = 'assistant'
            FOR UPDATE OF m
            """, connection, transaction)
        {
            Parameters =
            {
                new() { Value = clientId }, new() { Value = "owui:" + item.UserId }, new() { Value = item.ChatId }, new() { Value = assistant.Id },
                new() { Value = item.MessageId },
            },
        };
        Guid messageId, conversationId;
        string level;
        DateTimeOffset at;
        short? previous;
        await using (var reader = await find.ExecuteReaderAsync(ct))
        {
            if (!await reader.ReadAsync(ct))
            {
                return "unmapped";
            }

            (messageId, conversationId, level, at) = (reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3));
        }

        // After the lock, in its own statement (a fresh READ COMMITTED snapshot; #264 review).
        await using (var current = new NpgsqlCommand("""
            SELECT "Rating" FROM chat."Feedback" WHERE "Source" = 'owui-import' AND "ClientId" = $1 AND "ExternalId" = $2
            """, connection, transaction) { Parameters = { new() { Value = clientId }, new() { Value = item.Id } } })
        {
            previous = await current.ExecuteScalarAsync(ct) is short value ? value : null;
        }

        await using (var upsert = new NpgsqlCommand("""
            INSERT INTO chat."Feedback" ("Id", "MessageId", "ConversationId", "Classification", "ClientId", "Source", "ExternalId", "Rating", "Score",
                "ReasonCategory", "ReviewState", "CreatedAt", "UpdatedAt")
            VALUES ($1, $2, $3, $4, $5, 'owui-import', $6, $7, $8, $9, 'open', now(), now())
            ON CONFLICT ("Source", "ClientId", "ExternalId") DO UPDATE SET "Rating" = excluded."Rating", "Score" = excluded."Score",
                "ReasonCategory" = excluded."ReasonCategory", "UpdatedAt" = now()
            """, connection, transaction)
        {
            Parameters =
            {
                new() { Value = Guid.CreateVersion7() }, new() { Value = messageId }, new() { Value = conversationId }, new() { Value = level },
                new() { Value = clientId }, new() { Value = item.Id }, new() { Value = item.Rating.Value },
                new() { Value = (object?)(item.Score is >= 1 and <= 10 ? item.Score : null) ?? DBNull.Value },
                new() { Value = (object?)(item.Reason is null ? null : FeedbackEndpoints.CategoryOf(item.Reason)) ?? DBNull.Value },
            },
        })
        {
            await upsert.ExecuteNonQueryAsync(ct);
        }

        var up = (item.Rating == 1 ? 1 : 0) - (previous == 1 ? 1 : 0);
        var down = (item.Rating == -1 ? 1 : 0) - (previous == -1 ? 1 : 0);
        if (up != 0 || down != 0)
        {
            await using var totals = new NpgsqlCommand("""
                INSERT INTO chat."AssistantDailyTotals" AS t ("Day", "AssistantId", "ClientId", "Classification", "DepartmentId", "Conversations", "Messages",
                    "NotFound", "Unavailable", "Degraded", "Errors", "Cancelled", "UncitedAnswers", "ThumbsUp", "ThumbsDown", "ToolCalls", "ToolErrors",
                    "AgentLimitHits")
                VALUES ($1, $2, $3, $4, $5, 0, 0, 0, 0, 0, 0, 0, 0, GREATEST($6, 0), GREATEST($7, 0), 0, 0, 0)
                ON CONFLICT ("Day", "AssistantId", "ClientId", "Classification") DO UPDATE SET
                    "ThumbsUp" = GREATEST(t."ThumbsUp" + $6, 0), "ThumbsDown" = GREATEST(t."ThumbsDown" + $7, 0)
                """, connection, transaction)
            {
                Parameters =
                {
                    new() { Value = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Stockholm).DateTime) }, new() { Value = assistant.Id }, new() { Value = clientId },
                    new() { Value = level }, new() { Value = assistant.DepartmentId }, new() { Value = up }, new() { Value = down },
                },
            };
            await totals.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return "imported";
    }

    /// <summary>§13.3: Open WebUI feedback older than the client's <c>ClientRetentionDays</c>, oldest first, deleted through its API.</summary>
    private async Task PruneAsync(NpgsqlConnection connection, Guid clientId, OpenWebUiOptions o, DateTimeOffset now, CancellationToken ct)
    {
        var days = await ScalarAsync<int?>(connection, """SELECT "ClientRetentionDays" FROM config."Clients" WHERE "Id" = $1""", ct, clientId);
        if (days is not > 0)
        {
            return;
        }

        var cutoff = now.AddDays(-days.Value).ToUnixTimeSeconds();
        var http = Client(o);
        for (var page = 0; page < MaxPages; page++)
        {
            // Page 1 again each time: the deleted items leave the list.
            var old = (await ListAsync(o, "asc", 1, ct)).Where(i => i.UpdatedAt < cutoff).ToList();
            foreach (var item in old)
            {
                using var deleted = await http.DeleteAsync($"api/v1/evaluations/feedback/{Uri.EscapeDataString(item.Id)}", ct);
                deleted.EnsureSuccessStatusCode();
            }

            if (old.Count < 30)
            {
                return;
            }
        }
    }

    private HttpClient Client(OpenWebUiOptions o)
    {
        var http = clients.CreateClient(HttpClientName);
        http.BaseAddress = o.BaseUrl;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", o.AdminApiKey);
        return http;
    }

    /// <summary>One page of the list; only the fields of §14.2 are read, the user object and tags are never touched.</summary>
    private async Task<IReadOnlyList<OwuiFeedback>> ListAsync(OpenWebUiOptions o, string direction, int page, CancellationToken ct)
    {
        using var response = await Client(o).GetAsync($"api/v1/evaluations/feedbacks/list?order_by=updated_at&direction={direction}&page={page}", ct);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var items = new List<OwuiFeedback>();
        foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
        {
            var feedbackData = item.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
            var meta = item.TryGetProperty("meta", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
            items.Add(new OwuiFeedback(
                item.GetProperty("id").GetString()!, item.GetProperty("user_id").GetString()!, item.GetProperty("updated_at").GetInt64(),
                Short(feedbackData, "rating"),
                feedbackData.ValueKind == JsonValueKind.Object && feedbackData.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Object
                    ? Short(details, "rating") : null,
                Text(feedbackData, "reason"), Text(feedbackData, "model_id"), Text(meta, "chat_id"), Text(meta, "message_id")));
        }

        return items;
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // Open WebUI's RatingData.rating is "str | int".
    private static short? Short(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt16(out var n) => n,
                JsonValueKind.String when short.TryParse(v.GetString(), out var n) => n,
                _ => null,
            }
            : null;

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken ct, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return await command.ExecuteScalarAsync(ct) is T result ? result : default;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Open WebUI feedback {FeedbackId} could not be mapped to a message for 24 hours")]
    private static partial void LogUnmapped(ILogger logger, string feedbackId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Open WebUI feedback import failed ({Outcome}); the watermark stays")]
    private static partial void LogFailed(ILogger logger, string outcome);

    [LoggerMessage(Level = LogLevel.Error, Message = "Open WebUI feedback import alert: {FailedRuns} failed runs in a row (last {Outcome}); no feedback is imported")]
    private static partial void LogAlert(ILogger logger, int failedRuns, string outcome);
}
