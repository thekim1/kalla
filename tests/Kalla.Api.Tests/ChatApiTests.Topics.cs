using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Kalla.Domain.Entities;
using Kalla.TestKit;

namespace Kalla.Api.Tests;

/// <summary>
/// <c>ume.topics</c> through the chat role (chat design §3.8, §18.9 <i>Topics per request</i>; Decisions "Topics per request") on
/// the knowledge fixture, whose altan page carries the tag <c>altan</c> and whose upload and section pages carry none.
/// </summary>
public sealed partial class ChatApiTests
{
    /// <summary>The public collection linked with topics, the section collection linked without.</summary>
    private IReadOnlyList<CollectionLink> TopicLinks(params string[] publicTopics) =>
        [new CollectionLink(db.PublicCollection, publicTopics), CollectionLink.Whole(db.SectionCollection)];

    [Fact]
    public async Task Topics_narrow_the_links_and_the_answer_lists_the_ones_searched()
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = LongHost(calls, LongFault.None, new FakeTimeProvider(DateTimeOffset.UtcNow), logs, links: TopicLinks("altan", "skrivare"));
        using var client = Client(api, db.IntegrationKey);

        // Without topics the whole link brings the section collection's pages beside the altan page.
        var (_, ume) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, """{"related_documents":10}"""));
        ume.GetProperty("related_documents").GetArrayLength().ShouldBeGreaterThan(1);

        // "altan": the topic link keeps it, the whole link is searched for it; only the altan page carries it.
        (_, ume) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, """{"topics":["altan"],"related_documents":10}"""));
        ume.GetProperty("outcome").GetString().ShouldBe("answered");
        ume.GetProperty("topics").EnumerateArray().Select(t => t.GetString()).ShouldBe(["altan"]);
        ume.GetProperty("related_documents").GetArrayLength().ShouldBe(1);

        // "konton": no topic link has it, so only the whole link is searched (for it), and nothing there carries it.
        (_, ume) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, """{"topics":["konton","altan"]}"""));
        ume.GetProperty("topics").EnumerateArray().Select(t => t.GetString()).ShouldBe(["konton", "altan"]);
        (_, ume) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, """{"topics":["konton"]}"""));
        ume.GetProperty("topics").EnumerateArray().Select(t => t.GetString()).ShouldBe(["konton"]);
        ume.GetProperty("outcome").GetString().ShouldBe("not-found");

        // [] and null change nothing: the answer carries no topics.
        foreach (var none in new[] { """{"topics":[]}""", """{"topics":null}""", "{}" })
        {
            (_, ume) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, none));
            ume.TryGetProperty("topics", out _).ShouldBeFalse();
            ume.GetProperty("outcome").GetString().ShouldBe("answered");
        }
    }

    [Fact]
    public async Task A_request_cannot_reach_a_tag_its_link_does_not_have()
    {
        // The public collection is linked for "skrivare" only; asking for "altan", which its altan page carries, leaves the link
        // out, and the whole link has nothing tagged altan.
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = LongHost(calls, LongFault.None, new FakeTimeProvider(DateTimeOffset.UtcNow), logs, links: TopicLinks("skrivare"));
        using var client = Client(api, db.IntegrationKey);

        var (_, ume) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, """{"topics":["altan"],"related_documents":10}"""));
        ume.GetProperty("outcome").GetString().ShouldBe("not-found");
        ume.GetProperty("topics").EnumerateArray().Select(t => t.GetString()).ShouldBe(["altan"]); // searched by the whole link
        ume.GetProperty("related_documents").GetArrayLength().ShouldBe(0);
        calls.ShouldNotContain(c => c.Step == "chat-answer"); // the not-found text, no answer call
    }

    [Fact]
    public async Task A_turn_with_no_link_left_ends_not_found_without_a_search()
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = LongHost(calls, LongFault.None, new FakeTimeProvider(DateTimeOffset.UtcNow), logs,
            links: [new CollectionLink(db.PublicCollection, ["altan"]), new CollectionLink(db.SectionCollection, ["plank"])]);
        using var client = Client(api, db.IntegrationKey);

        var (_, ume) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, """{"topics":["okänt"]}"""));
        ume.GetProperty("outcome").GetString().ShouldBe("not-found");
        ume.GetProperty("topics").GetArrayLength().ShouldBe(0);
        calls.ShouldBeEmpty(); // no condense, no query embedding, no answer call
    }

    [Theory]
    [InlineData("""{"topics":"altan"}""")]
    [InlineData("""{"topics":[1]}""")]
    [InlineData("""{"topics":["Altan"]}""")]
    [InlineData("""{"topics":["två ord"]}""")]
    [InlineData("""{"topics":[""]}""")]
    [InlineData("""{"topics":["a","b","c","d","e","f","g","h","i","j","k","l","m","n","o","p","q","r","s","t","u"]}""")]
    public async Task Invalid_topics_are_refused_before_any_model_call(string ume)
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = LongHost(calls, LongFault.None, new FakeTimeProvider(DateTimeOffset.UtcNow), logs);
        using var client = Client(api, db.IntegrationKey);

        using var response = await AskUme(client, AltanQuestion, ume);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error");
        (error.GetProperty("code").GetString(), error.GetProperty("param").GetString()).ShouldBe(("invalid_request", "ume.topics"));
        calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Twenty_topics_are_accepted()
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = LongHost(calls, LongFault.None, new FakeTimeProvider(DateTimeOffset.UtcNow), logs, links: TopicLinks("altan"));
        using var client = Client(api, db.IntegrationKey);

        var topics = JsonSerializer.Serialize(Enumerable.Range(0, 19).Select(i => "t" + i).Append("altan"));
        var (_, ume) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, $$"""{"topics":{{topics}}}"""));
        ume.GetProperty("outcome").GetString().ShouldBe("answered");
        ume.GetProperty("topics").GetArrayLength().ShouldBe(20); // the whole link searches every asked topic
    }
}
