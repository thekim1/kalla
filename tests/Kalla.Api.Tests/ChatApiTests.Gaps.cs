using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Kalla.Domain.Entities;
using Kalla.TestKit;

namespace Kalla.Api.Tests;

/// <summary>
/// Content gaps (chat design §12.1, §12.2, §18.9 <i>Content gaps</i>; Decisions "Content-gap report") on the knowledge fixture: a
/// not-found or general-knowledge turn adds one to <c>AssistantDailyGaps</c> under its nearest hit, the search's first result; a
/// turn narrowed to no link adds one to <i>no hit</i>; an answered turn adds nothing; no row or column holds the question.
/// The test panel's turns are kalla-admin's and never reach this writer (<c>AssistantTestPanelUsageTests</c>).
/// </summary>
public sealed partial class ChatApiTests
{
    private const string MoonQuestion = "Hur långt är det till månen i kilometer?";

    /// <summary>The columns of a gap row, and nothing else (data-model §2.3).</summary>
    private static readonly string[] GapColumns = ["Day", "AssistantId", "ClientId", "Classification", "Outcome", "CollectionId", "DocumentId", "DepartmentId", "Turns"];

    [Theory]
    [InlineData("off", "not-found")]
    [InlineData("fallback", "answered-general")]
    public async Task A_low_coverage_turn_counts_under_its_first_result_and_an_answered_turn_adds_nothing(string level, string outcome)
    {
        await using var api = GeneralHost(level);
        using var client = Client(api, db.IntegrationKey);
        var before = await GapsAsync(api);

        // The search's weak hits are the turn's source rows: its first result is the row of the lowest rank.
        var (body, kalla) = await KallaAnswerAsync(await AskUme(client, MoonQuestion, "{}"));
        kalla.GetProperty("outcome").GetString().ShouldBe(outcome);
        var nearest = await FirstSourceAsync(api, body.GetProperty("id").GetString()!);
        nearest.ShouldNotBeNull("the fixture's search returns weak hits for the question");
        (await GapsAsync(api)).ShouldBe(Added(before, (outcome, nearest.Value.CollectionId, nearest.Value.DocumentId)), ignoreOrder: true);

        // An answered turn adds nothing.
        var afterLow = await GapsAsync(api);
        (_, kalla) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, "{}"));
        kalla.GetProperty("outcome").GetString().ShouldBe("answered");
        (await GapsAsync(api)).ShouldBe(afterLow, ignoreOrder: true);

        await ShouldHoldNoQuestionAsync(MoonQuestion);
    }

    [Fact]
    public async Task A_turn_narrowed_to_no_link_counts_as_no_hit()
    {
        // §3.8: no live link has the asked topic, so the turn ends not-found without a search; its nearest hit is none.
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = LongHost(calls, LongFault.None, new FakeTimeProvider(DateTimeOffset.UtcNow), logs,
            links: [new CollectionLink(db.PublicCollection, ["altan"]), new CollectionLink(db.SectionCollection, ["plank"])]);
        using var client = Client(api, db.IntegrationKey);
        var before = await GapsAsync(api);

        var (_, kalla) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, """{"topics":["okänt"]}"""));
        kalla.GetProperty("outcome").GetString().ShouldBe("not-found");
        calls.ShouldBeEmpty();
        (await GapsAsync(api)).ShouldBe(Added(before, ("not-found", Guid.Empty, Guid.Empty)), ignoreOrder: true);

        await ShouldHoldNoQuestionAsync(AltanQuestion);
    }

    /// <summary><paramref name="before"/> with one more turn under <paramref name="key"/>.</summary>
    private static Dictionary<(string, Guid, Guid), int> Added(Dictionary<(string, Guid, Guid), int> before, (string, Guid, Guid) key)
    {
        var expected = new Dictionary<(string, Guid, Guid), int>(before);
        expected[key] = expected.GetValueOrDefault(key) + 1;
        return expected;
    }

    /// <summary>The fixture assistant's gap rows of the integration client, by (outcome, collection, document), once the writer is idle.</summary>
    private async Task<Dictionary<(string, Guid, Guid), int>> GapsAsync(WebApplicationFactory<Program> api)
    {
        await api.Services.GetRequiredService<ConversationWriter>().FlushAsync(Ct);
        await using var connection = new NpgsqlConnection(db.Connection(RagDatabase.Api));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT "Outcome", "CollectionId", "DocumentId", sum("Turns")::int FROM chat."AssistantDailyGaps"
            WHERE "AssistantId" = $1 AND "ClientId" = $2 GROUP BY 1, 2, 3
            """, connection)
        {
            Parameters = { new() { Value = db.AssistantId }, new() { Value = db.IntegrationClient } },
        };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new Dictionary<(string, Guid, Guid), int>();
        while (await reader.ReadAsync(Ct))
        {
            rows[(reader.GetString(0), reader.GetGuid(1), reader.GetGuid(2))] = reader.GetInt32(3);
        }

        return rows;
    }

    /// <summary>The written answer's source row of the lowest rank: the search's first result (§12.1), or null without one.</summary>
    private async Task<(Guid CollectionId, Guid DocumentId)?> FirstSourceAsync(WebApplicationFactory<Program> api, string answerId)
    {
        await api.Services.GetRequiredService<ConversationWriter>().FlushAsync(Ct);
        await using var connection = new NpgsqlConnection(db.Connection(RagDatabase.Api));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT "CollectionId", "DocumentId" FROM chat."MessageSources" WHERE "MessageId" = $1 ORDER BY "Rank" LIMIT 1
            """, connection)
        {
            Parameters = { new() { Value = Guid.ParseExact(answerId["chatcmpl-".Length..], "N") } },
        };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        return await reader.ReadAsync(Ct) ? (reader.GetGuid(0), reader.GetGuid(1)) : null;
    }

    /// <summary>
    /// The negative proof (§18.9): every gap row has exactly <see cref="GapColumns"/>, and no row holds <paramref name="question"/>
    /// or a word of it in any column.
    /// </summary>
    private async Task ShouldHoldNoQuestionAsync(string question)
    {
        await using var connection = new NpgsqlConnection(db.Connection(RagDatabase.Api));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""SELECT to_jsonb(g)::text FROM chat."AssistantDailyGaps" g""", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = 0;
        var words = question.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim('?', ',')).Where(w => w.Length > 3).ToList();
        while (await reader.ReadAsync(Ct))
        {
            rows++;
            var row = reader.GetString(0);
            using var json = JsonDocument.Parse(row);
            json.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(GapColumns, ignoreOrder: true);
            foreach (var word in words)
            {
                row.ShouldNotContain(word, Case.Insensitive);
            }
        }

        rows.ShouldBeGreaterThan(0);
    }
}
