using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Kalla.Domain.Entities;
using Kalla.TestKit;

namespace Kalla.Api.Tests;

/// <summary>
/// Topics on related documents and the nearest subject through the chat role (chat design §3.8, §9.7, §9.11, §18.9 <i>Topics and
/// the nearest subject</i>; Decisions "Progress, timings and the nearest subject in the chat extension") on the knowledge fixture,
/// whose altan page carries the tag <c>altan</c> and whose other pages carry none. The negative proof: <c>nearest</c> is the
/// collection's slug and the tags, never a title, an address or a document id.
/// </summary>
public sealed partial class ChatApiTests
{
    /// <summary>The fixture's collection slug of <paramref name="collection"/>.</summary>
    private string CollectionSlug(Guid collection) => db.Snapshot().Collections[collection].Slug;

    [Fact]
    public async Task A_related_documents_topics_are_its_best_ranked_chunks_tags_and_empty_without_any()
    {
        await using var api = Host();
        using var client = Client(api, db.IntegrationKey);

        var (_, kalla) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, """{"related_documents":10}"""));
        kalla.GetProperty("outcome").GetString().ShouldBe("answered");
        var entries = kalla.GetProperty("related_documents").EnumerateArray().ToList();
        entries.Count.ShouldBeGreaterThan(1);
        foreach (var entry in entries)
        {
            var topics = entry.GetProperty("topics").EnumerateArray().Select(t => t.GetString()).ToList();
            topics.ShouldBe(entry.GetProperty("document_id").GetGuid() == db.Altan.Id ? ["altan"] : []);
        }

        entries.ShouldContain(e => e.GetProperty("document_id").GetGuid() == db.Altan.Id);
    }

    [Theory]
    [InlineData("off", "not-found")]
    [InlineData("fallback", "answered-general")]
    public async Task Nearest_on_a_turn_without_an_answer_is_the_first_results_collection_and_tags_and_nothing_else(string level, string outcome)
    {
        await using var api = GeneralHost(level);
        using var client = Client(api, db.IntegrationKey);

        var (body, kalla) = await KallaAnswerAsync(await AskUme(client, MoonQuestion, """{"nearest":true,"related_documents":5}"""));
        kalla.GetProperty("outcome").GetString().ShouldBe(outcome);
        kalla.GetProperty("related_documents").GetArrayLength().ShouldBe(0); // §9.7: no documents for these outcomes

        // The first result is the written answer's source row of the lowest rank, the content-gap count's nearest hit (§12.1).
        var first = await FirstSourceAsync(api, body.GetProperty("id").GetString()!);
        first.ShouldNotBeNull("the fixture's search returns weak hits for the question");
        var nearest = kalla.GetProperty("nearest");
        Members(nearest).ShouldBe(["collection", "topics"]);
        nearest.GetProperty("collection").GetString().ShouldBe(CollectionSlug(first.Value.CollectionId));
        nearest.GetProperty("topics").EnumerateArray().Select(t => t.GetString()).ShouldBe(first.Value.DocumentId == db.Altan.Id ? ["altan"] : []);
        ShouldNameNoDocument(nearest, first.Value.DocumentId);
    }

    [Fact]
    public async Task Nearest_comes_in_the_first_kalla_chunk_of_a_stream()
    {
        await using var api = GeneralHost("off");
        using var client = Client(api, db.IntegrationKey);

        var events = await EventsAsync(await AskUme(client, MoonQuestion, """{"nearest":true}""", stream: true, usage: true));
        var kalla = events.Single(IsKalla).GetProperty("kalla");
        kalla.GetProperty("outcome").GetString().ShouldBe("not-found");
        Members(kalla.GetProperty("nearest")).ShouldBe(["collection", "topics"]);
    }

    [Fact]
    public async Task Nearest_is_null_on_an_answered_turn_and_absent_when_not_asked()
    {
        await using var api = Host();
        using var client = Client(api, db.IntegrationKey);

        var (_, kalla) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, """{"nearest":true}"""));
        kalla.GetProperty("outcome").GetString().ShouldBe("answered");
        kalla.GetProperty("nearest").ValueKind.ShouldBe(JsonValueKind.Null);

        // A conversational message has no search: null.
        (_, kalla) = await KallaAnswerAsync(await AskUme(client, "Hej!", """{"nearest":true}"""));
        kalla.GetProperty("nearest").ValueKind.ShouldBe(JsonValueKind.Null);

        foreach (var none in new[] { """{"nearest":false}""", "{}" })
        {
            (_, kalla) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, none));
            kalla.TryGetProperty("nearest", out _).ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Nearest_is_null_when_the_search_found_nothing()
    {
        // §3.8: no live link has the asked topic, so the turn ends not-found without a search.
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = LongHost(calls, LongFault.None, new FakeTimeProvider(DateTimeOffset.UtcNow), logs,
            links: [new CollectionLink(db.PublicCollection, ["altan"]), new CollectionLink(db.SectionCollection, ["plank"])]);
        using var client = Client(api, db.IntegrationKey);

        var (_, kalla) = await KallaAnswerAsync(await AskUme(client, AltanQuestion, """{"topics":["okänt"],"nearest":true}"""));
        kalla.GetProperty("outcome").GetString().ShouldBe("not-found");
        kalla.GetProperty("nearest").ValueKind.ShouldBe(JsonValueKind.Null);
        calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Nearest_follows_the_links_as_the_requests_topics_narrowed_them()
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();

        // The public collection linked for "altan", the section collection whole: "altan" searches only chunks tagged altan in
        // both, and only the altan page carries it, so the turn's nearest hit is that page's collection and tag.
        await using (var api = LongHost(calls, LongFault.None, new FakeTimeProvider(DateTimeOffset.UtcNow), logs, links: TopicLinks("altan")))
        {
            using var client = Client(api, db.IntegrationKey);
            var (body, kalla) = await KallaAnswerAsync(await AskUme(client, MoonQuestion, """{"topics":["altan"],"nearest":true}"""));
            kalla.GetProperty("outcome").GetString().ShouldBe("not-found");
            var first = await FirstSourceAsync(api, body.GetProperty("id").GetString()!);
            first.ShouldNotBeNull();
            first.Value.ShouldBe((db.PublicCollection, db.Altan.Id));
            var nearest = kalla.GetProperty("nearest");
            nearest.GetProperty("collection").GetString().ShouldBe(CollectionSlug(db.PublicCollection));
            nearest.GetProperty("topics").EnumerateArray().Select(t => t.GetString()).ShouldBe(["altan"]);
            ShouldNameNoDocument(nearest, db.Altan.Id);
        }

        // The public collection linked for "skrivare" only: asking for "altan" leaves it out, so the altan page, which carries the
        // tag, is not searched, and nothing in the whole link carries it. The turn never names the left-out collection.
        await using (var api = LongHost(calls, LongFault.None, new FakeTimeProvider(DateTimeOffset.UtcNow), logs, links: TopicLinks("skrivare")))
        {
            using var client = Client(api, db.IntegrationKey);
            var (_, kalla) = await KallaAnswerAsync(await AskUme(client, MoonQuestion, """{"topics":["altan"],"nearest":true}"""));
            kalla.GetProperty("outcome").GetString().ShouldBe("not-found");
            kalla.GetProperty("nearest").ValueKind.ShouldBe(JsonValueKind.Null);
            kalla.GetRawText().ShouldNotContain(CollectionSlug(db.PublicCollection));
        }
    }

    /// <summary><c>nearest</c> off gives the recorded answer of the request without it byte for byte (the file is never rewritten from here).</summary>
    [Fact]
    public Task Without_nearest_the_wire_is_the_recorded_one() =>
        WireMatchesAsync("json-kalla-cited", false, false, false, AltanQuestion, null, Upstream.Fake,
            JsonDocument.Parse("""{"related_documents":5,"nearest":false}""").RootElement.Clone(), update: false);

    [Theory]
    [InlineData("""{"nearest":"yes"}""")]
    [InlineData("""{"nearest":1}""")]
    [InlineData("""{"nearest":null}""")]
    public async Task A_nearest_value_that_is_not_a_boolean_is_refused_before_any_model_call(string kalla)
    {
        var calls = new ConcurrentQueue<GatewayCall>();
        using var logs = new CapturingLoggerProvider();
        await using var api = LongHost(calls, LongFault.None, new FakeTimeProvider(DateTimeOffset.UtcNow), logs);
        using var client = Client(api, db.IntegrationKey);

        using var response = await AskUme(client, AltanQuestion, kalla);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error");
        (error.GetProperty("code").GetString(), error.GetProperty("param").GetString()).ShouldBe(("invalid_request", "kalla.nearest"));
        calls.ShouldBeEmpty();
    }

    /// <summary>
    /// The negative proof (§9.11): <paramref name="nearest"/> holds no document id (of <paramref name="hit"/> or any fixture document),
    /// no title and no address.
    /// </summary>
    private void ShouldNameNoDocument(JsonElement nearest, Guid hit)
    {
        var text = nearest.GetRawText();
        foreach (var id in new[] { hit, db.Altan.Id, db.Upload.Id, db.Plank.Id, db.Hidden.Id, db.Sensitive.Id })
        {
            text.ShouldNotContain(id.ToString("D"), Case.Insensitive);
            text.ShouldNotContain(id.ToString("N"), Case.Insensitive);
        }

        foreach (var title in new[] { "Altan och uteplats", "Taxa 2026", "taxa-bygglov-2026", "Plank", "Utkast", "Känsligt", "Tomtkarta" })
        {
            text.ShouldNotContain(title, Case.Insensitive);
        }

        text.ShouldNotContain("http", Case.Insensitive);
        text.ShouldNotContain("www.", Case.Insensitive);
        text.ShouldNotContain("/");
    }
}
