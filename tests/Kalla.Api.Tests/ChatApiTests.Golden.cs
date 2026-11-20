using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Kalla.Answering.Chat;
using Kalla.Domain.Access;
using Kalla.Domain.Entities;
using Kalla.TestKit;

namespace Kalla.Api.Tests;

/// <summary>
/// The chat wire pinned byte for byte (#410 item 3): status, the answer's headers and the whole body of a JSON and a
/// streamed answer with citations (also as Open WebUI), a degraded answer (caveat before cited text), a general-knowledge answer with caveat and links, a fixed text,
/// a refusal before the first byte and an upstream failure mid-stream, against golden files in
/// <c>tests/Kalla.Api.Tests/Golden/chat</c>. Ids, <c>created</c>, request ids and the conversation id are masked. With
/// <c>UME_RAG_UPDATE_GOLDEN=1</c> the files are rewritten.
/// </summary>
public sealed partial class ChatApiTests
{
    private static readonly string ChatGoldenDirectory = Path.Combine(Path.GetDirectoryName(TestPostgres.RepositoryFile("Kalla.slnx"))!,
        "tests", "Kalla.Api.Tests", "Golden", "chat");

    /// <summary>How the gateway's chat answer is tampered with on its way to the chat role.</summary>
    public enum Upstream
    {
        Fake,
        MidStreamError,
        Refused,
    }

    [Theory]
    [InlineData("json-cited", false, false, false, "Behöver jag bygglov för en altan högre än 1,2 meter?", null, Upstream.Fake)]
    [InlineData("sse-cited", true, true, false, "Behöver jag bygglov för en altan högre än 1,2 meter?", null, Upstream.Fake)]
    [InlineData("sse-cited-owui", true, false, true, "Behöver jag bygglov för en altan högre än 1,2 meter?", null, Upstream.Fake)]
    [InlineData("json-not-found", false, false, false, "Hur långt är det till månen i kilometer?", null, Upstream.Fake)]
    [InlineData("sse-not-found", true, true, false, "Hur långt är det till månen i kilometer?", null, Upstream.Fake)]
    [InlineData("json-general", false, false, false, "Hur långt är det till månen i kilometer?", "open", Upstream.Fake)]
    [InlineData("sse-general", true, true, false, "Hur långt är det till månen i kilometer?", "open", Upstream.Fake)]
    [InlineData("json-degraded", false, false, false, "Behöver jag bygglov för en altan högre än 1,2 meter?", "degraded", Upstream.Fake)]
    [InlineData("sse-degraded", true, false, false, "Behöver jag bygglov för en altan högre än 1,2 meter?", "degraded", Upstream.Fake)]
    [InlineData("json-refused", false, false, false, "Behöver jag bygglov för en altan högre än 1,2 meter?", null, Upstream.Refused)]
    [InlineData("sse-refused", true, false, false, "Behöver jag bygglov för en altan högre än 1,2 meter?", null, Upstream.Refused)]
    [InlineData("sse-mid-stream-error", true, false, false, "Behöver jag bygglov för en altan högre än 1,2 meter?", null, Upstream.MidStreamError)]
    public Task The_chat_wire_matches_its_golden_file(string name, bool stream, bool usage, bool openWebUi, string question, string? level, Upstream upstream) =>
        WireMatchesAsync(name, stream, usage, openWebUi, question, level, upstream, null, update: true);

    /// <summary>
    /// The <c>ume</c> extension on the wire (chat design §3.8, §18.9; M-7): the JSON answer's top-level <c>ume</c> and the
    /// stream's <c>ume</c> chunk after the final chunk and before usage and <c>[DONE]</c>, for each outcome, a degraded answer,
    /// and no chunk after an error. An Open WebUI client's request with an invalid <c>ume</c> streams byte for byte as the one
    /// without (the recorded <c>sse-cited-owui</c>, never rewritten from here).
    /// </summary>
    [Theory]
    [InlineData("json-ume-cited", false, false, false, "Behöver jag bygglov för en altan högre än 1,2 meter?", null, Upstream.Fake, """{"related_documents":5}""")]
    [InlineData("sse-ume-cited", true, true, false, "Behöver jag bygglov för en altan högre än 1,2 meter?", null, Upstream.Fake, """{"related_documents":5}""")]
    [InlineData("json-ume-not-found", false, false, false, "Hur långt är det till månen i kilometer?", null, Upstream.Fake, """{"related_documents":5}""")]
    [InlineData("sse-ume-not-found", true, true, false, "Hur långt är det till månen i kilometer?", null, Upstream.Fake, """{}""")]
    [InlineData("json-ume-general", false, false, false, "Hur långt är det till månen i kilometer?", "open", Upstream.Fake, """{"related_documents":5}""")]
    [InlineData("sse-ume-degraded", true, true, false, "Behöver jag bygglov för en altan högre än 1,2 meter?", "degraded", Upstream.Fake, """{"related_documents":5}""")]
    [InlineData("sse-ume-mid-stream-error", true, true, false, "Behöver jag bygglov för en altan högre än 1,2 meter?", null, Upstream.MidStreamError,
        """{"related_documents":5}""")]
    [InlineData("sse-cited-owui", true, false, true, "Behöver jag bygglov för en altan högre än 1,2 meter?", null, Upstream.Fake, """{"foo":1,"related_documents":99}""")]
    public Task The_ume_wire_matches_its_golden_file(string name, bool stream, bool usage, bool openWebUi, string question, string? level, Upstream upstream, string ume) =>
        WireMatchesAsync(name, stream, usage, openWebUi, question, level, upstream, JsonDocument.Parse(ume).RootElement.Clone(), update: !openWebUi);

    /// <summary>
    /// Follow-up suggestions on the wire (chat design §3.8, §9.8; M-8), with a condense model and FakeGateway's default
    /// <c>follow-ups</c> answer: the JSON answer's <c>follow_ups</c> after <c>related_documents</c>, and in the stream a second
    /// <c>ume</c> chunk with <c>follow_ups</c> alone after the first, before usage and <c>[DONE]</c>; a fixed text gets
    /// <c>[]</c> in its only <c>ume</c> chunk, without a call.
    /// </summary>
    [Theory]
    [InlineData("json-ume-follow-ups", false, false, "Behöver jag bygglov för en altan högre än 1,2 meter?", """{"related_documents":2,"follow_ups":3}""")]
    [InlineData("sse-ume-follow-ups", true, true, "Behöver jag bygglov för en altan högre än 1,2 meter?", """{"related_documents":2,"follow_ups":3}""")]
    [InlineData("sse-ume-follow-ups-not-found", true, true, "Hur långt är det till månen i kilometer?", """{"follow_ups":3}""")]
    public Task The_follow_up_wire_matches_its_golden_file(string name, bool stream, bool usage, string question, string ume) =>
        WireMatchesAsync(name, stream, usage, false, question, null, Upstream.Fake, JsonDocument.Parse(ume).RootElement.Clone(), update: true, condenseModel: true);

    /// <summary>
    /// One request against <see cref="WireHost"/> compared with its golden file; the request carries <paramref name="ume"/> only
    /// when given, so the files without it pin today's bytes. <paramref name="update"/> lets <c>UME_RAG_UPDATE_GOLDEN=1</c> write the file.
    /// </summary>
    private async Task WireMatchesAsync(string name, bool stream, bool usage, bool openWebUi, string question, string? level, Upstream upstream, JsonElement? ume,
        bool update, bool condenseModel = false)
    {
        await using var api = WireHost(level, openWebUi, upstream, condenseModel);
        using var client = Client(api, db.IntegrationKey);
        if (openWebUi)
        {
            AsOpenWebUiUser(client);
        }

        var streamOptions = usage ? new { include_usage = true } : null;
        var messages = new[] { new { role = "user", content = question } };
        object request = ume is { } extension
            ? new { model = "bygg", stream, stream_options = streamOptions, messages, ume = extension }
            : new { model = "bygg", stream, stream_options = streamOptions, messages };
        using var response = await client.PostAsJsonAsync("/v1/chat/completions", request, Ct);
        var wire = new StringBuilder();
        wire.Append("HTTP ").Append((int)response.StatusCode).Append('\n');
        foreach (var header in new[] { "Content-Type", "Cache-Control", "X-Accel-Buffering", "Retry-After", "x-rag-conversation-id" })
        {
            var values = response.Headers.TryGetValues(header, out var h) ? h : response.Content.Headers.TryGetValues(header, out var c) ? c : null;
            if (values is not null)
            {
                wire.Append(header).Append(": ").Append(string.Join(", ", values)).Append('\n');
            }
        }

        wire.Append('\n').Append(await response.Content.ReadAsStringAsync(Ct));
        var actual = Mask(wire.ToString());

        var file = Path.Combine(ChatGoldenDirectory, name + ".txt");
        if (update && Environment.GetEnvironmentVariable("UME_RAG_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(ChatGoldenDirectory);
            await File.WriteAllTextAsync(file, actual, Ct);
        }

        File.Exists(file).ShouldBeTrue($"{file} is missing; run with UME_RAG_UPDATE_GOLDEN=1 to write it.");
        actual.ShouldBe((await File.ReadAllTextAsync(file, Ct)).ReplaceLineEndings("\n"), $"{name}: run with UME_RAG_UPDATE_GOLDEN=1 to regenerate.");
    }

    /// <summary>Open WebUI's user headers (a signed user JWT and a chat id) on a client of a <see cref="WireHost"/> with <c>openWebUi</c>.</summary>
    private static void AsOpenWebUiUser(HttpClient client)
    {
        var now = DateTimeOffset.UtcNow;
        client.DefaultRequestHeaders.Add("X-OpenWebUI-User-Jwt", OpenWebUiUserJwtTests.Jwt(new
        {
            sub = "u-1", role = "user", iss = "open-webui", iat = now.ToUnixTimeSeconds(), exp = now.AddMinutes(5).ToUnixTimeSeconds(),
        }));
        client.DefaultRequestHeaders.Add("X-OpenWebUI-Chat-Id", "owui-chat-" + Guid.CreateVersion7().ToString("N"));
    }

    private static string Mask(string wire)
    {
        wire = ChatIdPattern().Replace(wire, "chatcmpl-00000000000000000000000000000000");
        wire = CreatedPattern().Replace(wire, "\"created\":0");
        wire = RequestIdPattern().Replace(wire, "\"request_id\":\"MASKED\"");
        // The fixture's document ids, collection slugs and download grants (the ume related documents) change per run.
        wire = UuidPattern().Replace(wire, "00000000-0000-0000-0000-000000000000");
        wire = SlugPattern().Replace(wire, "\"c-MASKED\"");
        wire = GrantPattern().Replace(wire, "grant=g1.MASKED");
        return ConversationPattern().Replace(wire, "x-rag-conversation-id: c1.MASKED");
    }

    [GeneratedRegex("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")]
    private static partial Regex UuidPattern();

    [GeneratedRegex("\"c-[0-9a-f]{32}\"")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("grant=g1\\.[A-Za-z0-9_.-]+")]
    private static partial Regex GrantPattern();

    [GeneratedRegex("chatcmpl-[0-9a-f]{32}")]
    private static partial Regex ChatIdPattern();

    [GeneratedRegex("\"created\":[0-9]+")]
    private static partial Regex CreatedPattern();

    [GeneratedRegex("\"request_id\":\"[^\"]*\"")]
    private static partial Regex RequestIdPattern();

    [GeneratedRegex("x-rag-conversation-id: \\S+")]
    private static partial Regex ConversationPattern();

    /// <summary>The wire host; with <paramref name="condenseModel"/> <c>Settings.Chat.condenseModel</c> is set, so follow-ups are called (§9.8).</summary>
    private WebApplicationFactory<Program> WireHost(string? level, bool openWebUi, Upstream upstream, bool condenseModel = false)
    {
        var snapshot = Snapshot();

        if (level == "degraded")
        {
            // The embedding answer is discarded (residency above the profile's ceiling): keyword only, coverage unknown.
            snapshot = db.Snapshot("fake-wrong-residency-embed") with { Assistants = snapshot.Assistants };
        }
        else if (level is not null)
        {
            var bygg = snapshot.Assistants[db.AssistantId];
            snapshot = snapshot with
            {
                Assistants = new Dictionary<Guid, AssistantEntry>
                {
                    [db.AssistantId] = bygg with
                    {
                        Published = bygg.Published! with
                        {
                            QuerySteps = $$$"""{"answer":{"generalKnowledge":"{{{level}}}"}}""",
                            NotFound = """{"sv":"Inget svar i källorna.","links":[{"title":"Kontaktcenter","url":"https://www.umea.se/kontakt"}]}""",
                        },
                    },
                },
            };
        }

        if (openWebUi)
        {
            snapshot = snapshot with
            {
                Clients = snapshot.Clients.ToDictionary(c => c.Key, c => c.Key == db.IntegrationClient ? c.Value with { Kind = ClientKinds.OpenWebUi, IsManagedByDeployment = true } : c.Value),
            };
        }

        if (condenseModel)
        {
            snapshot = snapshot with { ChatSettings = """{"condenseModel":"fors/chat-eu","condenseReasoningEffort":"none"}""" };
        }

        return db.ApiHost(snapshot).WithWebHostBuilder(web =>
        {
            web.UseSetting("Rag:Roles", "knowledge,chat");
            web.UseSetting("Gateway:RetryDelay", "00:00:00");
            if (openWebUi)
            {
                web.UseSetting("OpenWebUi:JwtSecret", "open-webui-jwt-secret-for-tests-0123456789");
            }

            web.ConfigureTestServices(services => services.AddHttpClient(GatewayChatClient.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new TamperedGateway(upstream) { InnerHandler = db.Gateway.Server.CreateHandler() }));
        });
    }

    /// <summary>The FakeGateway, or a 403 refusal, or its stream cut after the first text with an error event.</summary>
    private sealed class TamperedGateway(Upstream upstream) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (upstream == Upstream.Refused)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("""{"error":{"code":"max_residency_not_met","message":"No target."}}""", Encoding.UTF8, "application/json"),
                };
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (upstream != Upstream.MidStreamError || response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            {
                return response;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var lines = body.Split('\n');
            var cut = new StringBuilder();
            var texts = 0;
            foreach (var line in lines)
            {
                cut.Append(line).Append('\n');
                if (line.Contains("\"content\":\"", StringComparison.Ordinal) && !line.Contains("\"content\":\"\"", StringComparison.Ordinal) && ++texts == 2)
                {
                    break;
                }
            }

            cut.Append("\ndata: {\"error\":{\"message\":\"upstream failed\"}}\n\n");
            var tampered = new StringContent(cut.ToString(), Encoding.UTF8);
            tampered.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            response.Content = tampered;
            return response;
        }
    }
}
