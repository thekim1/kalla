using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Ume.Rag.Domain.Access;
using Ume.Rag.Domain.Entities;
using Ume.Rag.Infrastructure.Access;
using Ume.Rag.TestKit;

namespace Ume.Rag.Api.Tests;

/// <summary>
/// The Open WebUI feedback import (chat design §14.2–§14.3, §13.3) against a recorded <c>/api/v1/evaluations/feedbacks/list</c>
/// page in Open WebUI v0.12.5's shape (unchanged since v0.12.3) (<c>FeedbackListResponse</c> of <c>FeedbackUserResponse</c>, <c>models/feedbacks.py</c>):
/// the rating maps to the right message, the comment and the user's details are dropped, a foreign model is skipped, a
/// young unmapped item holds the watermark back, feedback older than the client's retention is deleted, and failed runs
/// in a row alert from the fourth.
/// </summary>
public sealed class FeedbackImporterTests(KnowledgeFixture db) : IClassFixture<KnowledgeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A recorded page; <c>{{NOW}}</c> and <c>{{OLD}}</c> are epoch seconds filled in by the test.</summary>
    private const string RecordedPage = """
        {"items": [
          {"id": "fb-mapped", "user_id": "u-1", "version": 0, "type": "rating",
           "data": {"rating": -1, "model_id": "kompost", "sibling_model_ids": null, "reason": "not_factually_correct", "comment": "Fel adress till återvinningen.",
                    "details": {"rating": 3}},
           "meta": {"arena": false, "chat_id": "chat-1", "message_id": "owui-msg-2", "tags": ["x"], "base_models": {"kompost": null}},
           "created_at": {{NOW}}, "updated_at": {{NOW}},
           "user": {"id": "u-1", "name": "Anna Andersson", "email": "anna@umea.se", "role": "user", "profile_image_url": "/user.png"}},
          {"id": "fb-foreign", "user_id": "u-1", "version": 0, "type": "rating",
           "data": {"rating": 1, "model_id": "gpt-4o", "reason": "accurate_information", "comment": ""},
           "meta": {"chat_id": "chat-9", "message_id": "m-9"}, "created_at": {{NOW}}, "updated_at": {{NOW}}, "user": null},
          {"id": "fb-young", "user_id": "u-1", "version": 0, "type": "rating",
           "data": {"rating": "1", "model_id": "kompost"}, "meta": {"chat_id": "chat-1", "message_id": "owui-msg-queued"},
           "created_at": {{YOUNG}}, "updated_at": {{YOUNG}}, "user": null},
          {"id": "fb-old", "user_id": "u-2", "version": 0, "type": "rating",
           "data": {"rating": 1, "model_id": "kompost"}, "meta": {"chat_id": "chat-gone", "message_id": "m-gone"},
           "created_at": {{OLD}}, "updated_at": {{OLD}}, "user": null}
        ], "total": 4}
        """;

    private sealed class FakeOpenWebUi(string page) : HttpMessageHandler
    {
        public List<string> Deleted { get; } = [];

        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            if (request.Method == HttpMethod.Delete)
            {
                Deleted.Add(request.RequestUri!.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("true") });
            }

            var body = request.RequestUri!.Query.Contains("page=1", StringComparison.Ordinal) ? page : """{"items": [], "total": 4}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Snapshot(PolicySnapshot snapshot) : IPolicySnapshotSource
    {
        public PolicySnapshot Current => snapshot;

        public bool IsConfirmed => true;
    }

    private sealed class Meters : IMeterFactory
    {
        public Meter Create(MeterOptions options) => new(options);

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task A_recorded_feedback_page_maps_ratings_drops_comments_and_prunes_old_feedback()
    {
        var clientId = Guid.CreateVersion7();
        var assistantId = Guid.CreateVersion7();
        var department = Guid.CreateVersion7();
        await using (var owner = TestPostgres.Context(db.Connection(null)))
        {
            owner.Clients.Add(new Client
            {
                Id = clientId, Name = "Open WebUI import " + clientId.ToString("N")[..8], Kind = ClientKinds.OpenWebUi, ClassificationCeiling = "internal",
                StoresHistory = true, ClientRetentionDays = 90, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await owner.SaveChangesAsync(Ct);
        }

        // As the writer leaves an Open WebUI turn: the conversation by chat id, the answer by Open WebUI's message id.
        var answer = Guid.CreateVersion7();
        await using var api = NpgsqlDataSource.Create(db.Connection(RagDatabase.Api));
        await using (var seed = api.CreateCommand($"""
            INSERT INTO chat."Conversations" ("Id", "Classification", "AssistantId", "ClientId", "SubjectKind", "SubjectId", "ExternalChatId", "HistoryMode",
                "RetentionDays", "CreatedAt", "LastMessageAt", "ExpiresAt")
            VALUES ('{Guid.CreateVersion7()}', 'internal', '{assistantId}', '{clientId}', 'owui-user', 'owui:u-1', 'chat-1', 'none', 90, now(), now(), now() + interval '90 days');
            INSERT INTO chat."Messages" ("Id", "ConversationId", "Classification", "Seq", "Role", "ExpiresAt", "Outcome", "CreatedAt", "CondenseUsed", "ExternalMessageId")
            SELECT '{answer}', "Id", 'internal', 2, 'assistant', now() + interval '90 days', 'answered', now(), false, 'owui-msg-2'
            FROM chat."Conversations" WHERE "ClientId" = '{clientId}';
            """))
        {
            await seed.ExecuteNonQueryAsync(Ct);
        }

        var now = DateTimeOffset.UtcNow;
        var page = RecordedPage.Replace("{{NOW}}", now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{YOUNG}}", now.AddHours(-2).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{OLD}}", now.AddDays(-200).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        using var openWebUi = new FakeOpenWebUi(page);
        var assistant = new AssistantEntry(assistantId, "kompost", "Kompostguiden", "internal", "none", "active", department, null, null, []);
        var snapshot = PolicySnapshot.Empty with { Version = 1, Assistants = new Dictionary<Guid, AssistantEntry> { [assistantId] = assistant } };
        var options = new StaticOptionsMonitor<OpenWebUiOptions>(new OpenWebUiOptions
        {
            ClientId = clientId.ToString(), BaseUrl = new Uri("http://owui.test/"), AdminApiKey = "sk-feedback-test",
        });
        using var meters = new Meters();
        var importer = new FeedbackImporter(new Clients(openWebUi), api, new Snapshot(snapshot), options, TimeProvider.System, meters,
            NullLogger<FeedbackImporter>.Instance);

        (await importer.RunOnceAsync(Ct)).ShouldBeTrue();
        (await importer.RunOnceAsync(Ct)).ShouldBeTrue(); // a second run changes nothing

        openWebUi.Authorization.ShouldBe("Bearer sk-feedback-test");
        openWebUi.Deleted.ShouldBe(["/api/v1/evaluations/feedback/fb-old", "/api/v1/evaluations/feedback/fb-old"]);
        await using (var feedback = api.CreateCommand($"""
            SELECT "MessageId", "ExternalId", "Rating", "Score", "ReasonCategory", "CommentCiphertext" IS NULL FROM chat."Feedback" WHERE "ClientId" = '{clientId}'
            """))
        await using (var reader = await feedback.ExecuteReaderAsync(Ct))
        {
            (await reader.ReadAsync(Ct)).ShouldBeTrue();
            (reader.GetGuid(0), reader.GetString(1), reader.GetInt16(2), reader.GetInt16(3), reader.GetString(4), reader.GetBoolean(5))
                .ShouldBe((answer, "fb-mapped", (short)-1, (short)3, "not_factually_correct", true));
            (await reader.ReadAsync(Ct)).ShouldBeFalse(); // the foreign model, the queued turn and the deleted chat are not stored
        }

        await using (var state = api.CreateCommand($"""
            SELECT "WatermarkUpdatedAt", "LastOutcome", "Unmapped", (SELECT "ThumbsDown" FROM chat."AssistantDailyTotals" WHERE "AssistantId" = '{assistantId}')
            FROM chat."FeedbackImportState" WHERE "ClientId" = '{clientId}'
            """))
        await using (var reader = await state.ExecuteReaderAsync(Ct))
        {
            (await reader.ReadAsync(Ct)).ShouldBeTrue();
            // The watermark stays at the young unmapped item (retried for 24 h); the 200-day-old unmapped one counts once per run.
            (reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3))
                .ShouldBe((now.AddHours(-2).ToUnixTimeSeconds(), "ok", 1, 1));
        }
    }

    /// <summary>Open WebUI that answers 401 while <see cref="Failing"/>, else an empty feedback list.</summary>
    private sealed class FlakyOpenWebUi : HttpMessageHandler
    {
        public bool Failing { get; set; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Failing
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"items": [], "total": 0}""", Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task Four_failed_runs_in_a_row_alert_and_a_good_run_clears_the_count()
    {
        var clientId = Guid.CreateVersion7();
        await using var api = NpgsqlDataSource.Create(db.Connection(RagDatabase.Api));
        using var openWebUi = new FlakyOpenWebUi();
        var options = new StaticOptionsMonitor<OpenWebUiOptions>(new OpenWebUiOptions { ClientId = clientId.ToString(), BaseUrl = new Uri("http://owui.test/"), AdminApiKey = "sk-feedback-test" });
        using var meters = new Meters();
        var logs = new CapturingLoggerProvider();
        using var loggers = new LoggerFactory([logs]);
        var importer = new FeedbackImporter(new Clients(openWebUi), api, new Snapshot(PolicySnapshot.Empty with { Version = 1 }), options, TimeProvider.System, meters,
            loggers.CreateLogger<FeedbackImporter>());

        async Task<(int FailedRuns, string Outcome)> StateAsync()
        {
            await using var read = api.CreateCommand($"""SELECT "FailedRuns", "LastOutcome" FROM chat."FeedbackImportState" WHERE "ClientId" = '{clientId}'""");
            await using var reader = await read.ExecuteReaderAsync(Ct);
            (await reader.ReadAsync(Ct)).ShouldBeTrue();
            return (reader.GetInt32(0), reader.GetString(1));
        }

        int Alerts() => logs.Entries.Count(e => e.Level == LogLevel.Error && e.Message.Contains("feedback import alert", StringComparison.Ordinal));

        for (var run = 1; run <= 3; run++)
        {
            (await importer.RunOnceAsync(Ct)).ShouldBeFalse();
            (await StateAsync()).ShouldBe((run, "failed:401"));
        }

        Alerts().ShouldBe(0);
        (await importer.RunOnceAsync(Ct)).ShouldBeFalse();
        (await StateAsync()).ShouldBe((FeedbackImportState.AlertAfterFailedRuns, "failed:401"));
        Alerts().ShouldBe(1);

        openWebUi.Failing = false;
        (await importer.RunOnceAsync(Ct)).ShouldBeTrue();
        (await StateAsync()).ShouldBe((0, "ok"));
        Alerts().ShouldBe(1);
    }
}
